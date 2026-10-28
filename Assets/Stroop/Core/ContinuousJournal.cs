using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace AvatarStroop.Core
{
    public sealed class JournalIdentity
    {
        public string SessionId {get;private set;}
        public string Role {get;private set;}
        public string ClockId {get;private set;}
        public ExportProvenance Provenance {get;private set;}
        public string ProtocolVersion {get;private set;}
        public string SoftwareVersion {get;private set;}
        public string RunKind {get{return "Simulation";}}
        public string DataLabel {get{return "SIMULATION";}}
        public JournalIdentity(string sessionId,string role,string clockId,ExportProvenance provenance)
        {
            if(String.IsNullOrWhiteSpace(sessionId)||String.IsNullOrWhiteSpace(clockId)||provenance==null)
                throw new ArgumentException("Session, clock and export provenance are required");
            if(role!="participant"&&role!="operator")throw new ArgumentException("Journal role must be participant or operator");
            SessionId=sessionId;Role=role;ClockId=clockId;Provenance=provenance;
            ProtocolVersion=SessionConfiguration.ProtocolVersion;SoftwareVersion=SessionConfiguration.SoftwareVersion;
        }
        internal Dictionary<string,string> Fields(string journalId)
        {
            return new Dictionary<string,string>{
                {"journal_schema",ContinuousJournal.SchemaVersion},{"journal_id",journalId},
                {"session_id",SessionId},{"role",Role},{"clock_id",ClockId},
                {"run_kind",RunKind},{"data_label",DataLabel},{"protocol_version",ProtocolVersion},
                {"software_version",SoftwareVersion},{"exporter_id",Provenance.ExporterId},
                {"build_source_digest",Provenance.BuildSourceDigest},
                {"stimulus_timestamp_semantics",Provenance.StimulusTimestampSemantics},
                {"response_timestamp_semantics",Provenance.ResponseTimestampSemantics}};
        }
        internal static JournalIdentity Parse(IReadOnlyDictionary<string,string> fields,string journalId)
        {
            if(fields.Count!=13||Value(fields,"journal_schema")!=ContinuousJournal.SchemaVersion||Value(fields,"journal_id")!=journalId||
                Value(fields,"run_kind")!="Simulation"||Value(fields,"data_label")!="SIMULATION")
                throw new InvalidDataException("Invalid journal identity or unsupported schema");
            var result=new JournalIdentity(Value(fields,"session_id"),Value(fields,"role"),Value(fields,"clock_id"),
                new ExportProvenance(Value(fields,"exporter_id"),Value(fields,"stimulus_timestamp_semantics"),
                    Value(fields,"response_timestamp_semantics"),Value(fields,"build_source_digest")));
            result.ProtocolVersion=Value(fields,"protocol_version");result.SoftwareVersion=Value(fields,"software_version");
            return result;
        }
        static string Value(IReadOnlyDictionary<string,string> fields,string key)
        {string value;if(!fields.TryGetValue(key,out value)||String.IsNullOrWhiteSpace(value))throw new InvalidDataException("Missing journal identity: "+key);return value;}
    }

    public sealed class JournalRecord
    {
        public long Sequence {get;private set;}
        public string Kind {get;private set;}
        public IReadOnlyDictionary<string,string> Fields {get;private set;}
        internal JournalRecord(long sequence,string kind,Dictionary<string,string> fields)
        {Sequence=sequence;Kind=kind;Fields=new ReadOnlyDictionary<string,string>(fields);}
    }
    public enum JournalReadStatus { CleanClose,UncleanEnd,Truncated,Invalid,ReadFailure }
    public sealed class JournalReadResult
    {
        public JournalIdentity Identity {get;internal set;}
        public JournalReadStatus Status {get;internal set;}
        public string Error {get;internal set;}
        public long VerifiedBytes {get;internal set;}
        // Includes the header and, when present, the close marker. Never implies completed study data.
        public long RecordCount {get;internal set;}
        public bool CleanCloseMarkerObserved {get;internal set;}
    }

    // Single owner-thread writer. Append success is not a durable-flush acknowledgment.
    // The chain detects accidental corruption/mixing; it is not an authenticity signature.
    public sealed class ContinuousJournal : IDisposable
    {
        public const string SchemaVersion="avatar-stroop-journal-1";
        public const int MaximumFrameBytes=1024*1024;
        internal const string HeaderKind="__header",CloseKind="__close";
        internal static readonly byte[] Magic=Encoding.ASCII.GetBytes("ASJNL001");
        internal static readonly UTF8Encoding Utf8=new UTF8Encoding(false,true);
        readonly Stream stream;
        readonly Action durableFlush;
        readonly bool leaveOpen;
        readonly byte[] token;
        byte[] previousHash=new byte[32];
        long writtenOffset;
        bool closed,disposed;
        public string Path {get;private set;}
        public bool IsFaulted {get;private set;}
        public string Error {get;private set;}
        public long LastWrittenSequence {get;private set;}=-1;
        public long LastFlushedSequence {get;private set;}=-1;
        public long LastFlushedOffset {get;private set;}
        public JournalIdentity Identity {get;private set;}
        public string JournalId {get{return new Guid(token).ToString("N");}}

        ContinuousJournal(string path,JournalIdentity identity,Exception error)
        {Path=path;Identity=identity;token=Guid.NewGuid().ToByteArray();Fault(error);}
        public static ContinuousJournal CreateNew(string path,JournalIdentity identity)
        {
            if(identity==null)throw new ArgumentNullException("identity");
            if(String.IsNullOrWhiteSpace(path))throw new ArgumentException("Explicit journal path is required");
            // Reject oversized/invalid metadata before creating a file or assuming ownership.
            Payload(0,new byte[16],HeaderKind,identity.Fields(new Guid(new byte[16]).ToString("N")));
            FileStream file=null;
            try
            {
                file=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.Read);
                return new ContinuousJournal(file,()=>file.Flush(true),identity,false,path);
            }
            catch(Exception error) when(IsStorageError(error))
            {if(file!=null)file.Dispose();return new ContinuousJournal(path,identity,error);}
        }
        // The supplied stream must be new/empty. The callback must perform the desired durable flush.
        public ContinuousJournal(Stream stream,Action durableFlush,JournalIdentity identity,bool leaveOpen=false,string path=null)
        {
            if(stream==null||durableFlush==null||identity==null)throw new ArgumentNullException("stream/flush/identity");
            if(!stream.CanWrite)throw new ArgumentException("A writable stream is required");
            if(stream.CanSeek&&(stream.Position!=0||stream.Length!=0))throw new ArgumentException("A new empty journal stream is required");
            this.stream=stream;this.durableFlush=durableFlush;this.leaveOpen=leaveOpen;Path=path;Identity=identity;token=Guid.NewGuid().ToByteArray();
            byte[] header=Payload(0,token,HeaderKind,identity.Fields(JournalId));
            try{stream.Write(Magic,0,Magic.Length);writtenOffset=Magic.Length;AppendPayload(header);Flush();}
            catch(Exception error) when(IsStorageError(error)){Fault(error);}
        }
        public bool TryAppend(string kind,IReadOnlyDictionary<string,string> fields)
        {
            if(IsFaulted||closed||disposed)return false;
            if(String.IsNullOrWhiteSpace(kind)||kind.StartsWith("__",StringComparison.Ordinal))throw new ArgumentException("Explicit nonreserved record kind required");
            if(fields==null)throw new ArgumentNullException("fields");
            if(LastWrittenSequence==Int64.MaxValue)throw new InvalidOperationException("Journal sequence exhausted");
            byte[] payload=Payload(LastWrittenSequence+1,token,kind,fields);
            try{AppendPayload(payload);return true;}catch(Exception error) when(IsStorageError(error)){Fault(error);return false;}
        }
        void AppendPayload(byte[] payload)
        {
            byte[] digest=Digest(previousHash,payload),frame=new byte[8+payload.Length+digest.Length];
            PutInt(frame,payload.Length);PutInt(frame,~payload.Length,4);Buffer.BlockCopy(payload,0,frame,8,payload.Length);Buffer.BlockCopy(digest,0,frame,8+payload.Length,digest.Length);
            stream.Write(frame,0,frame.Length);
            writtenOffset+=frame.Length;LastWrittenSequence++;previousHash=digest;
        }
        public bool Flush()
        {
            if(IsFaulted||disposed)return false;
            try{durableFlush();LastFlushedSequence=LastWrittenSequence;LastFlushedOffset=writtenOffset;return true;}
            catch(Exception error) when(IsStorageError(error)){Fault(error);return false;}
        }
        public bool Close()
        {
            if(IsFaulted||disposed)return false;
            if(closed)return true;
            try
            {
                AppendPayload(Payload(LastWrittenSequence+1,token,CloseKind,new Dictionary<string,string>()));
                closed=true;return Flush();
            }
            catch(Exception error) when(IsStorageError(error)){Fault(error);return false;}
        }
        public void Dispose()
        {
            if(disposed)return;
            Close();
            if(stream!=null&&!leaveOpen)try{stream.Dispose();}catch(Exception error) when(IsStorageError(error)){Fault(error);}
            disposed=true;
        }
        void Fault(Exception error){if(!IsFaulted){IsFaulted=true;Error=error.GetType().Name+": "+error.Message;}}
        internal static bool IsStorageError(Exception error)
        {return error is IOException||error is UnauthorizedAccessException||error is System.Security.SecurityException||error is NotSupportedException||error is ObjectDisposedException;}
        internal static byte[] Payload(long sequence,byte[] token,string kind,IReadOnlyDictionary<string,string> fields)
        {
            if(fields.Count>4096)throw new ArgumentException("Too many journal fields");
            var keys=new List<string>(fields.Keys);keys.Sort(StringComparer.Ordinal);
            long size=8+16+4+Utf8.GetByteCount(kind)+4;
            foreach(string key in keys)
            {
                if(String.IsNullOrEmpty(key))throw new ArgumentException("Journal field keys cannot be empty");
                size+=4L+Utf8.GetByteCount(key)+4+(fields[key]==null?0:Utf8.GetByteCount(fields[key]));
                if(size>MaximumFrameBytes)throw new ArgumentException("Journal frame exceeds bound");
            }
            if(size>MaximumFrameBytes)throw new ArgumentException("Journal frame exceeds bound");
            using(var memory=new MemoryStream((int)size))using(var writer=new BinaryWriter(memory,Utf8,true))
            {
                writer.Write(sequence);writer.Write(token);WriteString(writer,kind);writer.Write(fields.Count);
                foreach(string key in keys){WriteString(writer,key);WriteString(writer,fields[key]);}
                return memory.ToArray();
            }
        }
        static void WriteString(BinaryWriter writer,string value)
        {if(value==null){writer.Write(-1);return;}byte[] bytes=Utf8.GetBytes(value);writer.Write(bytes.Length);writer.Write(bytes);}
        internal static string ReadString(BinaryReader reader,bool nullable)
        {
            int length=reader.ReadInt32();if(length==-1&&nullable)return null;
            if(length<0||length>MaximumFrameBytes||length>reader.BaseStream.Length-reader.BaseStream.Position)throw new InvalidDataException("Invalid field length");
            return Utf8.GetString(reader.ReadBytes(length));
        }
        internal static byte[] Digest(byte[] prior,byte[] payload)
        {using(var sha=SHA256.Create()){sha.TransformBlock(prior,0,prior.Length,prior,0);sha.TransformFinalBlock(payload,0,payload.Length);return sha.Hash;}}
        internal static bool Equal(byte[] a,byte[] b)
        {if(a.Length!=b.Length)return false;for(int i=0;i<a.Length;i++)if(a[i]!=b[i])return false;return true;}
        internal static void PutInt(byte[] bytes,int value,int offset=0)
        {for(int i=0;i<4;i++)bytes[offset+i]=(byte)(value>>(i*8));}
        internal static int Int(byte[] bytes,int offset=0)
        {return bytes[offset]|bytes[offset+1]<<8|bytes[offset+2]<<16|bytes[offset+3]<<24;}
    }

    public static class JournalPrefixReader
    {
        public static JournalReadResult Read(Stream stream,Action<JournalRecord> onRecord=null)
        {
            if(stream==null||!stream.CanRead)throw new ArgumentException("Readable stream required");
            var result=new JournalReadResult();byte[] prior=new byte[32],token=null;long offset=0,sequence=0;
            try
            {
                byte[] magic=new byte[ContinuousJournal.Magic.Length];
                if(ReadBytes(stream,magic)!=magic.Length)return End(result,JournalReadStatus.Truncated,"Incomplete journal header");
                if(!ContinuousJournal.Equal(magic,ContinuousJournal.Magic))return End(result,JournalReadStatus.Invalid,"Unknown journal signature");
                offset=magic.Length;
                while(true)
                {
                    byte[] prefix=new byte[8];int got=ReadBytes(stream,prefix);
                    if(got==0&&sequence==0)return End(result,JournalReadStatus.Truncated,"No complete journal header");
                    if(got==0)return End(result,result.CleanCloseMarkerObserved?JournalReadStatus.CleanClose:JournalReadStatus.UncleanEnd,
                        result.CleanCloseMarkerObserved?null:"No close marker; recovered prefix is incomplete");
                    if(result.CleanCloseMarkerObserved)return End(result,JournalReadStatus.Invalid,"Bytes follow the close marker");
                    if(got<8)return End(result,JournalReadStatus.Truncated,"Incomplete protected frame length");
                    int length=ContinuousJournal.Int(prefix);
                    if(ContinuousJournal.Int(prefix,4)!=~length)return End(result,JournalReadStatus.Invalid,"Frame length complement mismatch");
                    if(length<32||length>ContinuousJournal.MaximumFrameBytes)return End(result,JournalReadStatus.Invalid,"Invalid or oversized frame length");
                    byte[] payload=new byte[length],hash=new byte[32];
                    if(ReadBytes(stream,payload)!=length||ReadBytes(stream,hash)!=hash.Length)return End(result,JournalReadStatus.Truncated,"Incomplete frame");
                    if(!ContinuousJournal.Equal(hash,ContinuousJournal.Digest(prior,payload)))return End(result,JournalReadStatus.Invalid,"Frame checksum/hash chain mismatch");
                    JournalRecord record;byte[] frameToken;
                    using(var memory=new MemoryStream(payload,false))using(var reader=new BinaryReader(memory,ContinuousJournal.Utf8,true))
                    {
                        long number=reader.ReadInt64();frameToken=reader.ReadBytes(16);string kind=ContinuousJournal.ReadString(reader,false);int count=reader.ReadInt32();
                        if(number!=sequence||frameToken.Length!=16||String.IsNullOrWhiteSpace(kind)||count<0||count>4096)throw new InvalidDataException("Invalid sequence, identity or field count");
                        var fields=new Dictionary<string,string>(StringComparer.Ordinal);
                        for(int i=0;i<count;i++)
                        {string key=ContinuousJournal.ReadString(reader,false);if(String.IsNullOrEmpty(key)||fields.ContainsKey(key))throw new InvalidDataException("Empty or duplicate field key");fields.Add(key,ContinuousJournal.ReadString(reader,true));}
                        if(memory.Position!=memory.Length)throw new InvalidDataException("Unparsed frame bytes");
                        record=new JournalRecord(number,kind,fields);
                    }
                    if(sequence==0)
                    {
                        if(record.Kind!=ContinuousJournal.HeaderKind)throw new InvalidDataException("First record is not the header");
                        result.Identity=JournalIdentity.Parse(record.Fields,new Guid(frameToken).ToString("N"));token=frameToken;
                    }
                    else
                    {
                        if(!ContinuousJournal.Equal(token,frameToken))throw new InvalidDataException("Mixed journal identity");
                        if(record.Kind==ContinuousJournal.CloseKind)
                        {if(record.Fields.Count!=0)throw new InvalidDataException("Unexpected close-marker fields");result.CleanCloseMarkerObserved=true;}
                        else if(record.Kind.StartsWith("__",StringComparison.Ordinal))throw new InvalidDataException("Reserved record kind");
                    }
                    prior=hash;offset+=8+length+hash.Length;result.VerifiedBytes=offset;result.RecordCount++;sequence++;
                    // Callback exceptions belong to the consumer and are not swallowed as parsing errors.
                    if(onRecord!=null)InvokeCallback(onRecord,record);
                }
            }
            catch(ConsumerException error){System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error.InnerException).Throw();throw;}
            catch(Exception error) when(error is InvalidDataException||error is EndOfStreamException||error is ArgumentException||error is OverflowException)
            {return End(result,JournalReadStatus.Invalid,error.GetType().Name+": "+error.Message);}
            catch(Exception error) when(ContinuousJournal.IsStorageError(error))
            {return End(result,JournalReadStatus.ReadFailure,error.GetType().Name+": "+error.Message);}
        }
        sealed class ConsumerException:Exception {internal ConsumerException(Exception inner):base("Journal consumer failed",inner){}}
        static void InvokeCallback(Action<JournalRecord> callback,JournalRecord record)
        {try{callback(record);}catch(Exception error){throw new ConsumerException(error);}}
        static int ReadBytes(Stream stream,byte[] bytes)
        {int total=0;while(total<bytes.Length){int got=stream.Read(bytes,total,bytes.Length-total);if(got==0)break;total+=got;}return total;}
        static JournalReadResult End(JournalReadResult result,JournalReadStatus status,string error)
        {result.Status=status;result.Error=error;return result;}
        // Copies only verified bytes to a newly created destination. Never adds a close marker.
        // Destination existence/read/write failures are explicit exceptions; callers must report them.
        public static JournalReadResult RecoverVerifiedPrefix(string sourcePath,string destinationPath)
        {
            using(var source=new FileStream(sourcePath,FileMode.Open,FileAccess.Read,FileShare.Read))
            {
                var result=Read(source);source.Position=0;
                using(var destination=new FileStream(destinationPath,FileMode.CreateNew,FileAccess.Write,FileShare.None))
                {
                    long remaining=result.VerifiedBytes;byte[] buffer=new byte[8192];
                    while(remaining>0)
                    {
                        int got=source.Read(buffer,0,(int)Math.Min(remaining,buffer.Length));
                        if(got==0)throw new EndOfStreamException("Source changed during verified-prefix extraction");
                        destination.Write(buffer,0,got);remaining-=got;
                    }
                    destination.Flush(true);
                }
                return result;
            }
        }
    }
}
