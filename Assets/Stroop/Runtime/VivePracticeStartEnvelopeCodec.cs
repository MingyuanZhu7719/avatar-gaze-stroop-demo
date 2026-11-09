using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using AvatarStroop.Core;

namespace AvatarStroop.Runtime
{
    public enum VivePracticeStartEnvelopeStatus { Complete, Incomplete, Invalid, ReadFailure, LimitExceeded }

    // Serialized data only. No evidence of identity, participant consent,
    // device, stable storage, authorization to write, or experimental validity.
    public sealed class VivePracticeStartEnvelope
    {
        readonly byte[] stage,commit;
        internal VivePracticeStartEnvelope(byte[] stage,byte[] commit){this.stage=stage;this.commit=commit;}
        public byte[] StageBytes {get{return (byte[])stage.Clone();}}
        public byte[] CommitBytes {get{return (byte[])commit.Clone();}}
        public int Length {get{return stage.Length+commit.Length;}}
        public byte[] ToArray(){var bytes=new byte[Length];Buffer.BlockCopy(stage,0,bytes,0,stage.Length);Buffer.BlockCopy(commit,0,bytes,stage.Length,commit.Length);return bytes;}
        internal byte[] CopyEnvelopeBytes(){return ToArray();}
        public bool CanWrite {get{return false;}}
        public bool IdentityAuthenticated {get{return false;}}
        public bool DurableStorageProven {get{return false;}}
    }
    public sealed class VivePracticeStartEnvelopeReadResult
    {
        public VivePracticeStartEnvelopeStatus Status {get;internal set;}
        // Complete framed prefix only. This is not an authorization decision.
        public int ConsumedBytes {get;internal set;}
        // Framing/hash-verified physical prefix, not semantic proof or commit.
        public int VerifiedBytes {get;internal set;}
        public VivePracticeContextV3 Context {get;internal set;}
        public long Revision {get;internal set;}
        public IReadOnlyList<JournalEvent> Events {get;internal set;}
        public bool ConsentAuthenticated {get{return false;}}
        public bool IdentityAuthenticated {get{return false;}}
        public bool DurableStorageProven {get{return false;}}
    }

    // A SINGLE development start envelope; this is not a v3 journal or a
    // multi-batch file. Encode never receives a stream or performs I/O.
    // DecodePrefix is for a future bounded outer framer, not a journal reader.
    public static class VivePracticeStartEnvelopeCodec
    {
        public const int MaximumEnvelopeBytes=262144;
        const int Overhead=88,MaxStringBytes=4096,MaxFields=64;
        static readonly byte[] StageMagic=Encoding.ASCII.GetBytes("VPSTG001"),CommitMagic=Encoding.ASCII.GetBytes("VPCMT001");
        static readonly byte[] StageDomain=Encoding.ASCII.GetBytes("VIVE-PRACTICE-START-STAGE-1\0"),CommitDomain=Encoding.ASCII.GetBytes("VIVE-PRACTICE-START-COMMIT-1\0");
        static readonly UTF8Encoding Utf8=new UTF8Encoding(false,true);
        static readonly IReadOnlyList<JournalEvent> EmptyEvents=new ReadOnlyCollection<JournalEvent>(new List<JournalEvent>());
        static bool Finite(double value){return !Double.IsNaN(value)&&!Double.IsInfinity(value)&&value>=0;}
        static void Limit(int max){if(max<Overhead||max>MaximumEnvelopeBytes)throw new ArgumentOutOfRangeException("maximumBytes");}
        sealed class BoundedWriter:IDisposable
        {
            readonly MemoryStream memory=new MemoryStream();readonly BinaryWriter writer;readonly int max;
            internal BoundedWriter(int limit){max=limit;writer=new BinaryWriter(memory,Utf8,true);}
            void Room(int n){if(n<0||memory.Length>max-(long)n)throw new ArgumentException("Encoded payload exceeds its explicit bound.");}
            internal void Int(int n){Room(4);writer.Write(n);}internal void Long(long n){Room(8);writer.Write(n);}
            internal void String(string value)
            {
                if(value==null){Int(-1);return;}
                if(value.Length>MaxStringBytes)throw new ArgumentException("Encoded string exceeds its bound.");
                int count=Utf8.GetByteCount(value);if(count>MaxStringBytes)throw new ArgumentException("Encoded string exceeds its bound.");
                Room(4+count);writer.Write(count);if(count!=0){var bytes=Utf8.GetBytes(value);writer.Write(bytes);}
            }
            internal byte[] Done(){writer.Flush();return memory.ToArray();}
            public void Dispose(){writer.Dispose();memory.Dispose();}
        }
        sealed class PayloadReader:IDisposable
        {
            readonly MemoryStream memory;readonly BinaryReader reader;
            internal PayloadReader(byte[] bytes){memory=new MemoryStream(bytes,false);reader=new BinaryReader(memory,Utf8,true);}
            internal int Int(){return reader.ReadInt32();}internal long Long(){return reader.ReadInt64();}
            internal string String()
            {
                int count=Int();if(count==-1)return null;
                if(count<0||count>MaxStringBytes||count>memory.Length-memory.Position)throw new InvalidDataException("Invalid string length.");
                return Utf8.GetString(reader.ReadBytes(count));
            }
            internal bool End {get{return memory.Position==memory.Length;}}
            public void Dispose(){reader.Dispose();memory.Dispose();}
        }
        static void WriteContext(BoundedWriter w,VivePracticeContextV3 c)
        {
            w.String(c.SessionId);w.String(c.ParticipantId);w.String(c.StreamId);w.String(c.DeviceId);w.String(c.ClockId);w.String(c.MappingVersion);
            w.Int(c.PracticeAttempt);w.String(c.RecordingEpochId);w.Int(c.RecoveryOrdinal);w.String(c.PreviousEpochId);w.Int((int)c.EpochReason);
            if(!c.ObservedPracticeStartLocalSeconds.HasValue)throw new ArgumentException("A declared start time is required.");
            w.Long(BitConverter.DoubleToInt64Bits(c.ObservedPracticeStartLocalSeconds.Value));
        }
        static VivePracticeContextV3 ReadContext(PayloadReader r)
        {
            string session=r.String(),participant=r.String(),stream=r.String(),device=r.String(),clock=r.String(),mapping=r.String();
            int attempt=r.Int();string epoch=r.String();int ordinal=r.Int();string previous=r.String();var reason=(VivePracticeEpochReasonV3)r.Int();
            double start=BitConverter.Int64BitsToDouble(r.Long());
            return new VivePracticeContextV3(session,participant,stream,device,clock,mapping,attempt,epoch,ordinal,previous,reason,start);
        }
        static void WriteEvent(BoundedWriter w,JournalEvent item)
        {
            if(item!=null&&(item.CarriesNamedProvenance||item.CarriesLegacyRawProvenance))throw new InvalidOperationException("Legacy practice envelope cannot encode versioned raw or named-option provenance.");
            w.Long(item.Sequence);w.Long(BitConverter.DoubleToInt64Bits(item.Time));w.String(item.ClockId);w.String(item.SessionId);
            w.Int(item.BlockIndex);w.Int(item.Segment);w.String(item.TrialId);w.String(item.Kind);
            var keys=new List<string>();foreach(var pair in item.Fields)keys.Add(pair.Key);keys.Sort(StringComparer.Ordinal);
            if(keys.Count>MaxFields)throw new ArgumentException("Too many event fields.");w.Int(keys.Count);
            foreach(var key in keys){if(String.IsNullOrWhiteSpace(key))throw new ArgumentException("Invalid field key.");w.String(key);w.String(item.Fields[key]);}
        }
        static JournalEvent ReadEvent(PayloadReader r)
        {
            long seq=r.Long();double time=BitConverter.Int64BitsToDouble(r.Long());string clock=r.String(),session=r.String();
            int block=r.Int(),segment=r.Int();string trial=r.String(),kind=r.String();int count=r.Int();
            if(count<0||count>MaxFields)throw new InvalidDataException("Invalid field count.");
            var fields=new Dictionary<string,string>(StringComparer.Ordinal);string previous=null;
            for(int i=0;i<count;i++)
            {
                string key=r.String(),value=r.String();
                if(String.IsNullOrWhiteSpace(key)||(previous!=null&&StringComparer.Ordinal.Compare(previous,key)>=0))throw new InvalidDataException("Noncanonical event fields.");
                fields.Add(key,value);previous=key;
            }
            return new JournalEvent(seq,time,clock,session,block,segment,trial,kind,fields);
        }
        static void Validate(VivePracticeContextV3 c,long revision,IReadOnlyList<JournalEvent> events)
        {
            if(c==null||revision<1||!c.ObservedPracticeStartLocalSeconds.HasValue||events==null||events.Count!=3)throw new ArgumentException("Explicit three-event start context required.");
            string[] kinds={"practice_started","phase_changed","trial_planned"};
            long startSequence=events[0]==null?-1:events[0].Sequence;double previous=-1;int segment=events[0]==null?-1:events[0].Segment;
            if(startSequence<0||startSequence>Int64.MaxValue-3||segment<0)throw new ArgumentException("Invalid event order.");
            for(int i=0;i<3;i++)
            {
                var e=events[i];if(e!=null&&(e.CarriesNamedProvenance||e.CarriesLegacyRawProvenance))throw new InvalidOperationException("Legacy practice envelope cannot encode versioned raw or named-option provenance.");
                if(e==null||e.Sequence!=startSequence+i||!Finite(e.Time)||e.Time<previous||e.ClockId!=c.ClockId||e.SessionId!=c.SessionId||
                    e.BlockIndex!=-1||e.Segment!=segment||e.Kind!=kinds[i]||e.Fields==null||e.Fields.Count>MaxFields)throw new ArgumentException("Mismatched start event.");
                previous=e.Time;
            }
            if(BitConverter.DoubleToInt64Bits(events[0].Time)!=BitConverter.DoubleToInt64Bits(c.ObservedPracticeStartLocalSeconds.Value))throw new ArgumentException("Start time differs.");
            string attempt,from,to,list,index;
            if(!events[0].Fields.TryGetValue("attempt",out attempt)||attempt!=c.PracticeAttempt.ToString(CultureInfo.InvariantCulture)||
                !events[1].Fields.TryGetValue("from",out from)||(from!=Phase.Familiarization.ToString()&&from!=Phase.PracticeRetry.ToString())||
                !events[1].Fields.TryGetValue("to",out to)||to!=Phase.Practice.ToString()||
                !events[2].Fields.TryGetValue("list",out list)||String.IsNullOrWhiteSpace(list)||
                !events[2].Fields.TryGetValue("planned_index",out index)||index!="0"||String.IsNullOrWhiteSpace(events[2].TrialId))throw new ArgumentException("Invalid start semantics.");
        }
        static byte[] Digest(byte[] domain,byte[] bytes)
        {using(var hash=SHA256.Create()){hash.TransformBlock(domain,0,domain.Length,domain,0);hash.TransformFinalBlock(bytes,0,bytes.Length);return hash.Hash;}}
        static bool Equal(byte[] a,byte[] b)
        {if(a==null||b==null||a.Length!=b.Length)return false;int difference=0;for(int i=0;i<a.Length;i++)difference|=a[i]^b[i];return difference==0;}
        public static VivePracticeStartEnvelope Encode(VivePracticeContextV3 context,VivePracticeStartBatch batch,int maximumBytes)
        {
            Limit(maximumBytes);if(context==null||batch==null)throw new ArgumentNullException("context/batch");
            if(batch.SessionId!=context.SessionId||batch.ClockId!=context.ClockId||batch.Attempt!=context.PracticeAttempt)throw new ArgumentException("Batch context mismatch.");
            Validate(context,batch.Revision,batch.Events);byte[] payload;
            using(var w=new BoundedWriter(maximumBytes-Overhead))
            {WriteContext(w,context);w.Long(batch.Revision);w.Int(3);foreach(var item in batch.Events)WriteEvent(w,item);payload=w.Done();}
            byte[] stage;
            using(var memory=new MemoryStream())using(var writer=new BinaryWriter(memory,Utf8,true))
            {writer.Write(StageMagic);writer.Write(payload.Length);writer.Write(~payload.Length);writer.Write(payload);writer.Write(Digest(StageDomain,payload));writer.Flush();stage=memory.ToArray();}
            byte[] commit=new byte[40];Buffer.BlockCopy(CommitMagic,0,commit,0,8);Buffer.BlockCopy(Digest(CommitDomain,stage),0,commit,8,32);
            return new VivePracticeStartEnvelope(stage,commit);
        }
        static VivePracticeStartEnvelopeReadResult Result(VivePracticeStartEnvelopeStatus status,int verified=0)
        {return new VivePracticeStartEnvelopeReadResult{Status=status,VerifiedBytes=verified,ConsumedBytes=0,Context=null,Revision=0,Events=EmptyEvents};}
        static int ReadExact(Stream stream,byte[] bytes,int offset,int count)
        {
            int total=0;while(total<count)
            {
                int n;try{n=stream.Read(bytes,offset+total,count-total);}
                catch(Exception e) when(!VivePracticeJournalV3Codec.Fatal(e)){throw new IOException("Input read failed.",e);}
                if(n==0)break;if(n<0||n>count-total)throw new IOException("Invalid stream read.");total+=n;
            }
            return total;
        }
        static bool Magic(byte[] source,int offset,byte[] expected)
        {if(source.Length-offset<expected.Length)return false;for(int i=0;i<expected.Length;i++)if(source[offset+i]!=expected[i])return false;return true;}
        static VivePracticeStartEnvelopeReadResult ReadCore(Stream input,int maximumBytes,bool strictEof)
        {
            int verified=0;
            try
            {
                bool readable;try{readable=input!=null&&input.CanRead;}
                catch(Exception e) when(!VivePracticeJournalV3Codec.Fatal(e)){return Result(VivePracticeStartEnvelopeStatus.ReadFailure);}
                if(!readable)return Result(VivePracticeStartEnvelopeStatus.ReadFailure);
                var prefix=new byte[16];if(ReadExact(input,prefix,0,prefix.Length)!=prefix.Length)return Result(VivePracticeStartEnvelopeStatus.Incomplete);
                if(!Magic(prefix,0,StageMagic))return Result(VivePracticeStartEnvelopeStatus.Invalid);
                int length=prefix[8]|prefix[9]<<8|prefix[10]<<16|prefix[11]<<24;
                int inverse=prefix[12]|prefix[13]<<8|prefix[14]<<16|prefix[15]<<24;
                if(length<0||inverse!=~length)return Result(VivePracticeStartEnvelopeStatus.Invalid);
                if(length>maximumBytes-Overhead)return Result(VivePracticeStartEnvelopeStatus.LimitExceeded);
                var payload=new byte[length];var digest=new byte[32];
                if(ReadExact(input,payload,0,length)!=length||ReadExact(input,digest,0,32)!=32)return Result(VivePracticeStartEnvelopeStatus.Incomplete);
                if(!Equal(digest,Digest(StageDomain,payload)))return Result(VivePracticeStartEnvelopeStatus.Invalid);
                int stageLength=checked(16+length+32);verified=stageLength;
                var stage=new byte[stageLength];Buffer.BlockCopy(prefix,0,stage,0,16);Buffer.BlockCopy(payload,0,stage,16,length);Buffer.BlockCopy(digest,0,stage,16+length,32);
                var marker=new byte[40];if(ReadExact(input,marker,0,40)!=40)return Result(VivePracticeStartEnvelopeStatus.Incomplete,verified);
                if(!Magic(marker,0,CommitMagic))return Result(VivePracticeStartEnvelopeStatus.Invalid,verified);
                var claimed=new byte[32];Buffer.BlockCopy(marker,8,claimed,0,32);
                if(!Equal(claimed,Digest(CommitDomain,stage)))return Result(VivePracticeStartEnvelopeStatus.Invalid,verified);
                int consumed=checked(stageLength+40);
                if(strictEof){var extra=new byte[1];if(ReadExact(input,extra,0,1)!=0)return Result(VivePracticeStartEnvelopeStatus.Invalid,verified);}
                VivePracticeContextV3 context;long revision;var events=new List<JournalEvent>(3);
                using(var r=new PayloadReader(payload))
                {
                    context=ReadContext(r);revision=r.Long();if(r.Int()!=3)throw new InvalidDataException("A start envelope has exactly three events.");
                    for(int i=0;i<3;i++)events.Add(ReadEvent(r));if(!r.End)throw new InvalidDataException("Trailing payload bytes.");
                }
                Validate(context,revision,events);
                return new VivePracticeStartEnvelopeReadResult{Status=VivePracticeStartEnvelopeStatus.Complete,VerifiedBytes=consumed,ConsumedBytes=consumed,
                    Context=context,Revision=revision,Events=new ReadOnlyCollection<JournalEvent>(events)};
            }
            catch(EndOfStreamException){return Result(VivePracticeStartEnvelopeStatus.Invalid,verified);}
            catch(IOException){return Result(VivePracticeStartEnvelopeStatus.ReadFailure,verified);}
            catch(ArgumentException){return Result(VivePracticeStartEnvelopeStatus.Invalid,verified);}
            catch(OverflowException){return Result(VivePracticeStartEnvelopeStatus.Invalid,verified);}
            catch(Exception e) when(!VivePracticeJournalV3Codec.Fatal(e)){return Result(VivePracticeStartEnvelopeStatus.Invalid,verified);}
        }
        // This method never closes input. The caller must provide the complete
        // single-envelope stream; even one trailing byte is rejected.
        public static VivePracticeStartEnvelopeReadResult Decode(Stream input,int maximumBytes)
        {Limit(maximumBytes);return ReadCore(input,maximumBytes,true);}
        // Parses exactly one bounded prefix, without treating later bytes as
        // part of this envelope. A future outer framer owns continuity/epochs.
        public static VivePracticeStartEnvelopeReadResult DecodePrefix(byte[] bytes,int offset,int availableLength,int maximumBytes)
        {
            Limit(maximumBytes);if(bytes==null)throw new ArgumentNullException("bytes");
            if(offset<0||availableLength<0||offset>bytes.Length-availableLength)throw new ArgumentOutOfRangeException("offset/availableLength");
            using(var stream=new MemoryStream(bytes,offset,availableLength,false,true))return ReadCore(stream,maximumBytes,false);
        }
    }
}
