using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Text;
using AvatarStroop.Core;

namespace AvatarStroop.Runtime
{
    // Development evidence only; the journal neither starts acquisition nor
    // enables apparatus sessions. No simulator journal schema is reused.
    public enum ViveDevelopmentTaskEventKind { PhaseChanged=1, BlockStarted=2, StimulusPresented=3, ResponseRecorded=4, Paused=5, Resumed=6, TrialInterrupted=7, BlockCompleted=8, TechnicalEnd=9, AuthoritativeEvent=10 }
    public enum ViveDevelopmentJournalReadStatus { CleanClose, UncleanEnd, Truncated, Invalid, ReadFailure }

    public sealed class ViveDevelopmentJournalRecord
    {
        public long Sequence {get;private set;}
        public string Kind {get;private set;}
        public IReadOnlyDictionary<string,string> Fields {get;private set;}
        internal ViveDevelopmentJournalRecord(long sequence,string kind,Dictionary<string,string> fields)
        {Sequence=sequence;Kind=kind;Fields=new ReadOnlyDictionary<string,string>(fields);}
    }
    public sealed class ViveDevelopmentJournalReadResult
    {
        public ViveDevelopmentJournalReadStatus Status {get;internal set;}
        public long VerifiedBytes {get;internal set;}
        public long RecordCount {get;internal set;}
        public bool CloseMarkerObserved {get;internal set;}
        public string Error {get;internal set;}
        // A verified prefix is a structural/integrity result, not a durable-flush
        // receipt, accuracy validation, authenticity signature or completed study.
    }

    public sealed class ViveDevelopmentJournal : IDisposable
    {
        public const string SchemaVersion="vive-development-journal-v1";
        public const string IdentitySchemaVersion="vive-development-journal-v2";
        public const int MaximumPayloadBytes=65536;
        internal static readonly byte[] Magic=Encoding.ASCII.GetBytes("VDEVJ001");
        readonly Stream stream;
        readonly Action flushCallback;
        readonly bool leaveOpen;
        readonly byte[] token=Guid.NewGuid().ToByteArray();
        readonly ViveDevelopmentStreamIdentity identity;
        readonly ViveDevelopmentDataSource source;
        readonly bool versionTwo;
        byte[] previousHash=new byte[32];
        long offset, nextOrdinal;
        bool closed,disposed,initialCoverageWritten;
        public bool IsFaulted {get;private set;}
        public string Error {get;private set;}
        public long LastWrittenSequence {get;private set;}=-1;
        public long LastFlushAcknowledgedSequence {get;private set;}=-1;
        public long LastFlushAcknowledgedOffset {get;private set;}

        // A caller-owned NEW empty seekable stream and explicit opt-in are required.
        // Flush acknowledgment means only that the supplied callback returned; it
        // is not, by itself, a hardware power-loss durability guarantee.
        public ViveDevelopmentJournal(Stream stream,Action flushCallback,ViveDevelopmentProvenance identity,
            ViveDevelopmentDataSource source,bool explicitOptIn,bool leaveOpen=false)
            :this(stream,flushCallback,LegacyIdentity(identity,explicitOptIn),source,explicitOptIn,leaveOpen,false)
        {}

        // Named factory avoids an ambiguous null overload and permits header
        // creation before the first actual eye/head observation. It never polls.
        public static ViveDevelopmentJournal CreateV2WithIdentity(Stream stream,Action flushCallback,ViveDevelopmentStreamIdentity identity,
            ViveDevelopmentDataSource source,bool explicitOptIn,bool leaveOpen=false)
        {return new ViveDevelopmentJournal(stream,flushCallback,identity,source,explicitOptIn,leaveOpen,true);}

        // Legacy bridge/sink callers preserve the existing v1 header exactly.
        internal static ViveDevelopmentJournal CreateLegacyWithIdentity(Stream stream,Action flushCallback,ViveDevelopmentStreamIdentity identity,
            ViveDevelopmentDataSource source,bool explicitOptIn,bool leaveOpen=false)
        {return new ViveDevelopmentJournal(stream,flushCallback,identity,source,explicitOptIn,leaveOpen,false);}

        static ViveDevelopmentStreamIdentity LegacyIdentity(ViveDevelopmentProvenance identity,bool explicitOptIn)
        {if(!explicitOptIn)throw new InvalidOperationException("Explicit VIVE development recording opt-in is required");
            if(identity!=null&&identity.IsVersionTwoIdentity)throw new InvalidOperationException("A v2 observation cannot initialize a v1 journal");
            return ViveDevelopmentStreamIdentity.FromObservation(identity);}

        ViveDevelopmentJournal(Stream stream,Action flushCallback,ViveDevelopmentStreamIdentity identity,
            ViveDevelopmentDataSource source,bool explicitOptIn,bool leaveOpen,bool useVersionTwo)
        {
            if(!explicitOptIn)throw new InvalidOperationException("Explicit VIVE development recording opt-in is required");
            if(stream==null||flushCallback==null||identity==null)throw new ArgumentNullException("stream/flush/identity");
            if(!stream.CanWrite||!stream.CanSeek||stream.Position!=0||stream.Length!=0)throw new ArgumentException("A new empty writable seekable stream is required");
            if(!Enum.IsDefined(typeof(ViveDevelopmentDataSource),source))throw new ArgumentOutOfRangeException("source");
            if(!useVersionTwo&&identity.OperatorStatus!=ViveDevelopmentOperatorStatus.Present)
                throw new InvalidOperationException("Journal v1 cannot represent an absent operator");
            this.stream=stream;this.flushCallback=flushCallback;this.identity=identity;this.source=source;this.leaveOpen=leaveOpen;versionTwo=useVersionTwo;
            var fields=IdentityFields(identity,source,useVersionTwo);byte[] header=Payload(0,token,"header",fields);
            try{stream.Write(Magic,0,Magic.Length);offset=Magic.Length;WriteFrame(header);Flush();}
            catch(Exception error) when(Storage(error)){Fault(error);}
        }

        public bool TryAppendObservation(ViveDevelopmentExportRecord record)
        {
            if(IsFaulted||closed||disposed)return false;
            if(record==null)throw new ArgumentNullException("record");
            if(record.Ordinal!=nextOrdinal||record.Provenance.IsVersionTwoIdentity!=versionTwo||!Matches(identity,source,record))
                throw new ArgumentException("Expected the next record in the declared source stream and identity schema");
            var p=record.Provenance;
            var fields=new Dictionary<string,string>{
                {"ordinal",N(record.Ordinal)},{"block_index",N(record.BlockIndex)},{"segment",N(record.Segment)},
                {"poll_started_local_seconds",N(p.PollStartedLocalSeconds)},{"poll_completed_local_seconds",N(p.PollCompletedLocalSeconds)},
                {"head_observed_local_seconds",N(p.HeadObservedLocalSeconds)},
                {"eye_observed_local_seconds",record.IsGap?null:N(p.EyeObservedLocalSeconds)},
                {"native_eye_timestamp_opaque",record.IsGap?null:N(p.NativeEyeTimestampOpaque)}};
            if(record.IsGap)
            {
                fields.Add("gap_start_local_seconds",N(record.GapStartLocalSeconds));fields.Add("gap_end_local_seconds",N(record.GapEndLocalSeconds));
                fields.Add("gap_reason",record.GapReason.Value.ToString());
            }
            else
            {
                fields.Add("native_frame_sequence",N(record.NativeFrameSequence));fields.Add("native_combined_mask",N(record.NativeDirectionMask));
                fields.Add("native_combined_direction_reported_valid",B(record.NativeDirectionReportedValid));
                AddVector(fields,"raw_combined_sdk_direction",record.RawSdkDirection);
                fields.Add("derived_direction_frame",record.DerivedDirectionFrame);AddVector(fields,"derived_direction",record.DerivedDirection);
                fields.Add("eye_usable",B(record.EyeUsable));
            }
            bool ok=Append(record.IsGap?"gap":"observation",fields);
            if(ok)nextOrdinal=checked(nextOrdinal+1);return ok;
        }

        public bool TryAppendTaskEvent(ViveDevelopmentTaskEventKind kind,double observedLocalSeconds,int blockIndex,int segment,
            string phase,string trialId,string detail)
        {
            if(IsFaulted||closed||disposed)return false;
            if(!Enum.IsDefined(typeof(ViveDevelopmentTaskEventKind),kind))throw new ArgumentOutOfRangeException("kind");
            if(Double.IsNaN(observedLocalSeconds)||Double.IsInfinity(observedLocalSeconds)||observedLocalSeconds<0)throw new ArgumentOutOfRangeException("observedLocalSeconds");
            if(blockIndex< -1||blockIndex>3||segment<0)throw new ArgumentOutOfRangeException("blockIndex");
            ValidateText(phase,"phase",false,128);ValidateText(trialId,"trialId",true,256);ValidateText(detail,"detail",true,1024);
            return Append("task_event",new Dictionary<string,string>{
                {"event_kind",kind.ToString()},{"observed_local_seconds",N(observedLocalSeconds)},
                {"block_index",N(blockIndex)},{"segment",N(segment)},{"phase",phase},{"trial_id",trialId},{"detail",detail}});
        }
        // This is an explicit unknown local-coverage window, not a gap sample
        // or an assertion of an actual tracking-loss onset or hardware timestamp.
        public bool TryAppendInitialUnknownCoverage(int blockIndex,int segment,double start,double end)
        {
            if(IsFaulted||closed||disposed)return false;
            if(!versionTwo||initialCoverageWritten||nextOrdinal!=0)throw new InvalidOperationException("Unknown initial coverage requires the start of a v2 stream");
            if(blockIndex<0||blockIndex>3||segment<0||Double.IsNaN(start)||Double.IsInfinity(start)||start<0||
                Double.IsNaN(end)||Double.IsInfinity(end)||end<start)throw new ArgumentException("Invalid local coverage bounds");
            bool ok=Append("initial_coverage_unknown",new Dictionary<string,string>{{"block_index",N(blockIndex)},{"segment",N(segment)},
                {"coverage_start_local_seconds",N(start)},{"coverage_end_local_seconds",N(end)}});
            if(ok)initialCoverageWritten=true;return ok;
        }
        public bool TryAppendRecoveryUnknownCoverage(int blockIndex,int segment,double start,double end,ViveDevelopmentGapReason reason)
        {
            if(IsFaulted||closed||disposed)return false;
            if(!versionTwo||nextOrdinal==0)throw new InvalidOperationException("Recovery window requires prior v2 observation");
            if(blockIndex<0||blockIndex>3||segment<0||Double.IsNaN(start)||Double.IsInfinity(start)||start<0||
                Double.IsNaN(end)||Double.IsInfinity(end)||end<=start||!Enum.IsDefined(typeof(ViveDevelopmentGapReason),reason))
                throw new ArgumentException("Invalid recovery coverage bounds");
            return Append("recovery_coverage_unknown",new Dictionary<string,string>{{"block_index",N(blockIndex)},{"segment",N(segment)},
                {"coverage_start_local_seconds",N(start)},{"coverage_end_local_seconds",N(end)},{"coverage_reason_detected",reason.ToString()}});
        }
        bool Append(string kind,Dictionary<string,string> fields)
        {
            if(LastWrittenSequence==Int64.MaxValue)throw new InvalidOperationException("Journal sequence exhausted");
            byte[] payload=Payload(LastWrittenSequence+1,token,kind,fields);
            try{WriteFrame(payload);return true;}catch(Exception error) when(Storage(error)){Fault(error);return false;}
        }
        void WriteFrame(byte[] payload)
        {
            byte[] digest=ContinuousJournal.Digest(previousHash,payload),frame=new byte[8+payload.Length+digest.Length];
            ContinuousJournal.PutInt(frame,payload.Length);ContinuousJournal.PutInt(frame,~payload.Length,4);
            Buffer.BlockCopy(payload,0,frame,8,payload.Length);Buffer.BlockCopy(digest,0,frame,8+payload.Length,digest.Length);
            stream.Write(frame,0,frame.Length);offset+=frame.Length;LastWrittenSequence++;previousHash=digest;
        }
        public bool Flush()
        {
            if(IsFaulted||disposed)return false;
            try{flushCallback();LastFlushAcknowledgedSequence=LastWrittenSequence;LastFlushAcknowledgedOffset=offset;return true;}
            catch(Exception error) when(Storage(error)){Fault(error);return false;}
        }
        public bool Close()
        {
            if(IsFaulted||disposed)return false;if(closed)return true;
            if(!Append("close",new Dictionary<string,string>()))return false;
            closed=true;return Flush();
        }
        public void Dispose()
        {
            if(disposed)return;Close();
            if(!leaveOpen)try{stream.Dispose();}catch(Exception error) when(Storage(error)){Fault(error);}
            disposed=true;
        }
        void Fault(Exception error){if(!IsFaulted){IsFaulted=true;Error=error.GetType().Name;}}
        static bool Storage(Exception e){return e is IOException||e is UnauthorizedAccessException||e is System.Security.SecurityException||e is NotSupportedException||e is ObjectDisposedException;}
        static byte[] Payload(long sequence,byte[] token,string kind,IReadOnlyDictionary<string,string> fields)
        {var bytes=ContinuousJournal.Payload(sequence,token,kind,fields);if(bytes.Length>MaximumPayloadBytes)throw new ArgumentException("VIVE journal frame exceeds bound");return bytes;}
        static Dictionary<string,string> IdentityFields(ViveDevelopmentStreamIdentity p,ViveDevelopmentDataSource source,bool useVersionTwo)
        {var fields=new Dictionary<string,string>{{"schema",useVersionTwo?IdentitySchemaVersion:SchemaVersion},{"data_source",source.ToString()},{"session_id",p.SessionId},{"participant_id",p.ParticipantId},
            {"operator_id",p.OperatorId},{"role",p.MeasurementRole.ToString()},{"stream_id",p.StreamId},{"device_id",p.DeviceId},
            {"local_clock_id",p.LocalObservationClockId},{"mapping_version",p.MappingVersion},{"hardware_capture_time_verified","false"},{"apparatus_mapping_validated","false"}};
            if(useVersionTwo)fields.Add("operator_status",p.OperatorStatus.ToString());return fields;}
        static bool Matches(ViveDevelopmentStreamIdentity x,ViveDevelopmentDataSource source,ViveDevelopmentExportRecord r)
        {var y=r.Provenance;return source==r.Source&&x.SessionId==y.SessionId&&x.ParticipantId==y.ParticipantId&&x.OperatorId==y.OperatorId&&x.OperatorStatus==y.OperatorStatus&&
            x.MeasurementRole==y.MeasurementRole&&x.StreamId==y.StreamId&&x.DeviceId==y.DeviceId&&x.LocalObservationClockId==y.LocalObservationClockId&&x.MappingVersion==y.MappingVersion;}
        static void ValidateText(string value,string name,bool nullable,int max)
        {if(value==null&&nullable)return;if(String.IsNullOrWhiteSpace(value)||value.Length>max)throw new ArgumentException("Bounded nonempty text required",name);}
        static void AddVector(Dictionary<string,string> fields,string name,Vec3? v)
        {fields.Add(name+"_x",v.HasValue?N(v.Value.X):null);fields.Add(name+"_y",v.HasValue?N(v.Value.Y):null);fields.Add(name+"_z",v.HasValue?N(v.Value.Z):null);}
        static string N<T>(T? value) where T:struct,IFormattable{return value.HasValue?N(value.Value):null;}
        static string N<T>(T value) where T:struct,IFormattable{return value.ToString(value is double?"R":null,CultureInfo.InvariantCulture);}
        static string B(bool? value){return value.HasValue?(value.Value?"true":"false"):null;}
    }

    public static class ViveDevelopmentJournalReader
    {
        sealed class ConsumerFailure:Exception {public ConsumerFailure(Exception inner):base("Consumer callback failed",inner){}}
        public static ViveDevelopmentJournalReadResult Read(Stream stream,Action<ViveDevelopmentJournalRecord> onRecord=null)
        {
            if(stream==null||!stream.CanRead)throw new ArgumentException("Readable stream required");
            var result=new ViveDevelopmentJournalReadResult();byte[] prior=new byte[32],token=null;long offset=0,sequence=0,nextOrdinal=0;bool versionTwo=false,initialCoverageSeen=false;
            try
            {
                var magic=new byte[ViveDevelopmentJournal.Magic.Length];int count=ReadBytes(stream,magic);
                if(count!=magic.Length)return End(result,ViveDevelopmentJournalReadStatus.Truncated,"Incomplete magic");
                if(!ContinuousJournal.Equal(magic,ViveDevelopmentJournal.Magic))return End(result,ViveDevelopmentJournalReadStatus.Invalid,"Unknown signature");
                offset=magic.Length;
                while(true)
                {
                    var prefix=new byte[8];int got=ReadBytes(stream,prefix);
                    if(got==0&&sequence==0)return End(result,ViveDevelopmentJournalReadStatus.Truncated,"Missing header");
                    if(got==0)return End(result,result.CloseMarkerObserved?ViveDevelopmentJournalReadStatus.CleanClose:ViveDevelopmentJournalReadStatus.UncleanEnd,result.CloseMarkerObserved?null:"No close marker");
                    if(result.CloseMarkerObserved)return End(result,ViveDevelopmentJournalReadStatus.Invalid,"Bytes follow close marker");
                    if(got<8)return End(result,ViveDevelopmentJournalReadStatus.Truncated,"Incomplete frame prefix");
                    int length=ContinuousJournal.Int(prefix);
                    if(ContinuousJournal.Int(prefix,4)!=~length||length<32||length>ViveDevelopmentJournal.MaximumPayloadBytes)
                        return End(result,ViveDevelopmentJournalReadStatus.Invalid,"Invalid protected frame length");
                    var payload=new byte[length];var hash=new byte[32];
                    if(ReadBytes(stream,payload)!=length||ReadBytes(stream,hash)!=hash.Length)return End(result,ViveDevelopmentJournalReadStatus.Truncated,"Incomplete frame");
                    if(!ContinuousJournal.Equal(hash,ContinuousJournal.Digest(prior,payload)))return End(result,ViveDevelopmentJournalReadStatus.Invalid,"Frame checksum mismatch");
                    ViveDevelopmentJournalRecord record;byte[] frameToken;
                    using(var memory=new MemoryStream(payload,false))using(var reader=new BinaryReader(memory,ContinuousJournal.Utf8,true))
                    {
                        long number=reader.ReadInt64();frameToken=reader.ReadBytes(16);string kind=ContinuousJournal.ReadString(reader,false);int fieldsCount=reader.ReadInt32();
                        if(number!=sequence||frameToken.Length!=16||fieldsCount<0||fieldsCount>64)throw new InvalidDataException("Invalid frame identity or field count");
                        var fields=new Dictionary<string,string>(StringComparer.Ordinal);
                        for(int i=0;i<fieldsCount;i++){string key=ContinuousJournal.ReadString(reader,false);if(String.IsNullOrEmpty(key)||fields.ContainsKey(key))throw new InvalidDataException("Invalid field key");fields.Add(key,ContinuousJournal.ReadString(reader,true));}
                        if(memory.Position!=memory.Length)throw new InvalidDataException("Trailing frame content");
                        record=new ViveDevelopmentJournalRecord(number,kind,fields);
                    }
                    if(sequence==0)
                    {
                        string schema,dataSource,hardware,mapping;
                        if(record.Kind!="header"||!record.Fields.TryGetValue("schema",out schema)||
                            (schema!=ViveDevelopmentJournal.SchemaVersion&&schema!=ViveDevelopmentJournal.IdentitySchemaVersion)||
                            record.Fields.Count!=(schema==ViveDevelopmentJournal.SchemaVersion?12:13)||
                            !record.Fields.TryGetValue("data_source",out dataSource)||(dataSource!="RealVive"&&dataSource!="SyntheticFixture")||
                            !record.Fields.TryGetValue("hardware_capture_time_verified",out hardware)||hardware!="false"||
                            !record.Fields.TryGetValue("apparatus_mapping_validated",out mapping)||mapping!="false")throw new InvalidDataException("Invalid VIVE journal header");
                        foreach(var key in new[]{"session_id","participant_id","role","stream_id","device_id","local_clock_id","mapping_version"})
                        {string value;if(!record.Fields.TryGetValue(key,out value)||String.IsNullOrWhiteSpace(value))throw new InvalidDataException("Missing VIVE journal identity");}
                        string operatorId;
                        if(!record.Fields.TryGetValue("operator_id",out operatorId))throw new InvalidDataException("Missing operator identity field");
                        if(schema==ViveDevelopmentJournal.SchemaVersion)
                        {
                            if(String.IsNullOrWhiteSpace(operatorId)||String.Equals(record.Fields["participant_id"],operatorId,StringComparison.OrdinalIgnoreCase)||
                                (record.Fields["role"]!="Participant"&&record.Fields["role"]!="Operator"))throw new InvalidDataException("Invalid VIVE journal role or identity");
                        }
                        else
                        {
                            string status;
                            if(!record.Fields.TryGetValue("operator_status",out status)||
                                (status!="Present"&&status!="NotApplicable")||
                                (record.Fields["role"]!="Participant"&&record.Fields["role"]!="Operator"))throw new InvalidDataException("Invalid v2 role or operator status");
                            var role=record.Fields["role"]=="Participant"?ViveDevelopmentMeasurementRole.Participant:ViveDevelopmentMeasurementRole.Operator;
                            var presence=status=="Present"?ViveDevelopmentOperatorStatus.Present:ViveDevelopmentOperatorStatus.NotApplicable;
                            // Reuse constructor validation for canonical IDs and exact null/status/role combinations.
                            new ViveDevelopmentStreamIdentity(record.Fields["session_id"],record.Fields["participant_id"],operatorId,role,
                                record.Fields["stream_id"],record.Fields["device_id"],record.Fields["local_clock_id"],record.Fields["mapping_version"],presence);
                        }
                        token=frameToken;versionTwo=schema==ViveDevelopmentJournal.IdentitySchemaVersion;
                    }
                    else
                    {
                        if(!ContinuousJournal.Equal(token,frameToken))throw new InvalidDataException("Mixed stream identity");
                        if(record.Kind=="close"){if(record.Fields.Count!=0)throw new InvalidDataException("Invalid close marker");result.CloseMarkerObserved=true;}
                        else ValidateRecord(record,ref nextOrdinal,versionTwo,ref initialCoverageSeen);
                    }
                    prior=hash;offset+=8+length+hash.Length;result.VerifiedBytes=offset;result.RecordCount++;sequence++;
                    if(onRecord!=null)try{onRecord(record);}catch(Exception e){throw new ConsumerFailure(e);}
                }
            }
            catch(ConsumerFailure error){ExceptionDispatchInfo.Capture(error.InnerException).Throw();throw;}
            catch(Exception error) when(error is InvalidDataException||error is EndOfStreamException||error is DecoderFallbackException||error is ArgumentException||error is OverflowException)
            {return End(result,ViveDevelopmentJournalReadStatus.Invalid,error.GetType().Name);}
            catch(Exception error) when(error is IOException||error is UnauthorizedAccessException||error is System.Security.SecurityException||error is NotSupportedException||error is ObjectDisposedException)
            {return End(result,ViveDevelopmentJournalReadStatus.ReadFailure,error.GetType().Name);}
        }
        static int ReadBytes(Stream stream,byte[] bytes)
        {int offset=0;while(offset<bytes.Length){int read=stream.Read(bytes,offset,bytes.Length-offset);if(read==0)break;offset+=read;}return offset;}
        static void ValidateRecord(ViveDevelopmentJournalRecord record,ref long nextOrdinal,bool versionTwo,ref bool initialCoverageSeen)
        {
            var f=record.Fields;
            if(record.Kind=="initial_coverage_unknown")
            {
                if(!versionTwo||initialCoverageSeen||nextOrdinal!=0||f.Count!=4)throw new InvalidDataException("Invalid unknown-coverage record");
                Block(Field(f,"block_index"),false);Nonnegative(Field(f,"segment"));
                if(Time(Field(f,"coverage_end_local_seconds"))<Time(Field(f,"coverage_start_local_seconds")))throw new InvalidDataException("Invalid unknown-coverage bounds");
                initialCoverageSeen=true;return;
            }
            if(record.Kind=="recovery_coverage_unknown")
            {
                if(!versionTwo||nextOrdinal==0||f.Count!=5)throw new InvalidDataException("Invalid recovery-coverage record");
                Block(Field(f,"block_index"),false);Nonnegative(Field(f,"segment"));
                if(Time(Field(f,"coverage_end_local_seconds"))<=Time(Field(f,"coverage_start_local_seconds")))throw new InvalidDataException("Invalid recovery-coverage bounds");
                ViveDevelopmentGapReason reason;
                if(!Enum.TryParse(Field(f,"coverage_reason_detected"),false,out reason)||!Enum.IsDefined(typeof(ViveDevelopmentGapReason),reason))throw new InvalidDataException("Invalid detected reason");
                return;
            }
            if(record.Kind=="task_event")
            {
                if(f.Count!=7)throw new InvalidDataException("Invalid task event fields");
                ViveDevelopmentTaskEventKind kind;
                if(!Enum.TryParse(Field(f,"event_kind"),false,out kind)||!Enum.IsDefined(typeof(ViveDevelopmentTaskEventKind),kind))throw new InvalidDataException("Invalid task event kind");
                Time(Field(f,"observed_local_seconds"));Block(Field(f,"block_index"),true);Nonnegative(Field(f,"segment"));
                if(String.IsNullOrWhiteSpace(Field(f,"phase")))throw new InvalidDataException("Missing phase");Field(f,"trial_id");Field(f,"detail");return;
            }
            if(record.Kind!="gap"&&record.Kind!="observation")throw new InvalidDataException("Unknown VIVE record kind");
            if(f.Count!=(record.Kind=="gap"?11:19))throw new InvalidDataException("Invalid VIVE observation fields");
            if(Nonnegative(Field(f,"ordinal"))!=nextOrdinal)throw new InvalidDataException("Unexpected observation ordinal");
            nextOrdinal=checked(nextOrdinal+1);Block(Field(f,"block_index"),false);Nonnegative(Field(f,"segment"));
            double start=Time(Field(f,"poll_started_local_seconds")),end=Time(Field(f,"poll_completed_local_seconds"));
            if(end<start)throw new InvalidDataException("Invalid poll times");
            var head=Field(f,"head_observed_local_seconds");if(head!=null&&Time(head)>end)throw new InvalidDataException("Invalid head observation time");
            var eye=Field(f,"eye_observed_local_seconds");var native=Field(f,"native_eye_timestamp_opaque");
            if(record.Kind=="gap")
            {
                if(eye!=null||native!=null)throw new InvalidDataException("Gap must not invent an eye sample");
                if(Time(Field(f,"gap_end_local_seconds"))<=Time(Field(f,"gap_start_local_seconds")))throw new InvalidDataException("Invalid gap duration");
                ViveDevelopmentGapReason reason;
                if(!Enum.TryParse(Field(f,"gap_reason"),false,out reason)||!Enum.IsDefined(typeof(ViveDevelopmentGapReason),reason))throw new InvalidDataException("Invalid gap reason");return;
            }
            if(eye==null||native==null||Time(eye)>end)throw new InvalidDataException("Missing eye observation time");
            int nativeTime,nativeSequence;ulong mask;bool reported,usable;
            if(!Int32.TryParse(native,NumberStyles.Integer,CultureInfo.InvariantCulture,out nativeTime)||
                !Int32.TryParse(Field(f,"native_frame_sequence"),NumberStyles.Integer,CultureInfo.InvariantCulture,out nativeSequence)||
                !UInt64.TryParse(Field(f,"native_combined_mask"),NumberStyles.None,CultureInfo.InvariantCulture,out mask)||
                !Boolean.TryParse(Field(f,"native_combined_direction_reported_valid"),out reported)||
                !Boolean.TryParse(Field(f,"eye_usable"),out usable))throw new InvalidDataException("Invalid eye metadata");
            var raw=Vector(f,"raw_combined_sdk_direction",false);var derived=Vector(f,"derived_direction",true);var frame=Field(f,"derived_direction_frame");
            if(derived.HasValue!=(frame!=null&&!String.IsNullOrWhiteSpace(frame))||(derived.HasValue&&!derived.Value.IsUnit))
                throw new InvalidDataException("Invalid derived frame or vector");
            Vec3 converted;
            if(usable&&(!reported||(mask&2UL)==0||!derived.HasValue||!ViveProEyeSdkDirection.TryGetUnitDirection(raw.Value,out converted)))
                throw new InvalidDataException("Usable output lacks valid native direction");
        }
        static string Field(IReadOnlyDictionary<string,string> fields,string key)
        {string value;if(!fields.TryGetValue(key,out value))throw new InvalidDataException("Missing VIVE field");return value;}
        static long Nonnegative(string value)
        {long parsed;if(!Int64.TryParse(value,NumberStyles.None,CultureInfo.InvariantCulture,out parsed)||parsed<0)throw new InvalidDataException("Invalid nonnegative integer");return parsed;}
        static void Block(string value,bool allowPreparation)
        {int parsed;if(!Int32.TryParse(value,NumberStyles.Integer,CultureInfo.InvariantCulture,out parsed)||parsed<(allowPreparation?-1:0)||parsed>3)throw new InvalidDataException("Invalid block");}
        static double Time(string value)
        {double parsed;if(!Double.TryParse(value,NumberStyles.Float,CultureInfo.InvariantCulture,out parsed)||Double.IsNaN(parsed)||Double.IsInfinity(parsed)||parsed<0)throw new InvalidDataException("Invalid local time");return parsed;}
        static Vec3? Vector(IReadOnlyDictionary<string,string> fields,string key,bool nullable)
        {
            var x=Field(fields,key+"_x");var y=Field(fields,key+"_y");var z=Field(fields,key+"_z");
            if(x==null&&y==null&&z==null&&nullable)return null;
            double a,b,c;
            if(x==null||y==null||z==null||!Double.TryParse(x,NumberStyles.Float,CultureInfo.InvariantCulture,out a)||
                !Double.TryParse(y,NumberStyles.Float,CultureInfo.InvariantCulture,out b)||!Double.TryParse(z,NumberStyles.Float,CultureInfo.InvariantCulture,out c))throw new InvalidDataException("Invalid vector");
            return new Vec3(a,b,c);
        }
        static ViveDevelopmentJournalReadResult End(ViveDevelopmentJournalReadResult result,ViveDevelopmentJournalReadStatus status,string error)
        {result.Status=status;result.Error=error;return result;}
    }
}
