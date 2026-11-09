using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using AvatarStroop.Core;

namespace AvatarStroop.Runtime
{
    public enum VivePracticeEpochReasonV3 { Initial=1, PracticeRetry=2, ResumedAfterInterruption=3, AfterUncleanEnd=4 }
    public enum VivePracticeV3WriterState { Open, Faulted, Aborted, Closed, CloseOutcomeUncertain }
    public enum VivePracticeV3EyeWrite { Written, Duplicate, WaitingForFreshRecovery, Unavailable, Rejected }

    // Wire ordering only. This does not establish task-event provenance,
    // participant consent, device identity or research readiness.
    internal enum VivePracticeTaskCursorContractV3 { Legacy=0, StartContiguousV1=1 }

    // Declared context only: neither IDs nor epoch linkage authenticate hardware
    // or a person. Practice never acquires an experimental block or condition.
    public sealed class VivePracticeContextV3
    {
        public string SessionId {get;private set;} public string ParticipantId {get;private set;}
        public string StreamId {get;private set;} public string DeviceId {get;private set;}
        public string ClockId {get;private set;} public string MappingVersion {get;private set;}
        public int PracticeAttempt {get;private set;} public string RecordingEpochId {get;private set;}
        public int RecoveryOrdinal {get;private set;} public string PreviousEpochId {get;private set;}
        public VivePracticeEpochReasonV3 EpochReason {get;private set;} public double? ObservedPracticeStartLocalSeconds {get;private set;}
        public int BlockIndex {get{return -1;}} public Condition? Condition {get{return null;}}
        public string MainSourceStatus {get{return "UNSELECTED";}}
        public bool IdentityAuthenticated {get{return false;}}
        public VivePracticeContextV3(string session,string participant,string stream,string device,string clock,string mapping,
            int attempt,string epoch,int recoveryOrdinal,string previousEpoch,VivePracticeEpochReasonV3 reason,double? observedPracticeStartLocalSeconds)
        {
            foreach(string id in new[]{session,participant,stream,device,clock,mapping,epoch})WireCodec.RequireIdentity(id);
            if(attempt<1||recoveryOrdinal<0||!Enum.IsDefined(typeof(VivePracticeEpochReasonV3),reason)||
                (reason==VivePracticeEpochReasonV3.Initial&&(recoveryOrdinal!=0||previousEpoch!=null))||
                (reason==VivePracticeEpochReasonV3.PracticeRetry&&(attempt<2||recoveryOrdinal!=0||previousEpoch==null))||
                ((reason==VivePracticeEpochReasonV3.ResumedAfterInterruption||reason==VivePracticeEpochReasonV3.AfterUncleanEnd)&&(recoveryOrdinal<1||previousEpoch==null))||
                previousEpoch==epoch)throw new ArgumentException("Invalid declared practice epoch linkage.");
            if(previousEpoch!=null)WireCodec.RequireIdentity(previousEpoch);
            if(observedPracticeStartLocalSeconds.HasValue&&!VivePracticeJournalV3Codec.ValidTime(observedPracticeStartLocalSeconds.Value))
                throw new ArgumentException("Invalid observed practice start.");
            SessionId=session;ParticipantId=participant;StreamId=stream;DeviceId=device;ClockId=clock;MappingVersion=mapping;
            PracticeAttempt=attempt;RecordingEpochId=epoch;RecoveryOrdinal=recoveryOrdinal;PreviousEpochId=previousEpoch;
            EpochReason=reason;ObservedPracticeStartLocalSeconds=observedPracticeStartLocalSeconds;
        }
    }
    public sealed class VivePracticeJournalV3Record
    {
        public long Sequence {get;private set;} public string Kind {get;private set;}
        public IReadOnlyDictionary<string,string> Fields {get;private set;}
        // Old v3 eye records carry no assessment; absence must not mean false.
        public string HeadUsability {get {string value;return Kind=="eye_observation"&&Fields.TryGetValue("head_usability",out value)?value:"UNKNOWN";}}
        public string DerivedDirectionStatus {get {string value;return Kind=="eye_observation"&&Fields.TryGetValue("derived_status",out value)?value:"UNKNOWN";}}
        internal VivePracticeJournalV3Record(long sequence,string kind,Dictionary<string,string> fields)
        {Sequence=sequence;Kind=kind;Fields=new ReadOnlyDictionary<string,string>(fields);}
    }
    public sealed class VivePracticeJournalV3ReadResult
    {
        public ViveDevelopmentJournalReadStatus Status {get;internal set;} public string Issue {get;internal set;}
        public long VerifiedBytes {get;internal set;} public IReadOnlyList<VivePracticeJournalV3Record> Records {get;internal set;}
        // Describes the returned records only; this is not event provenance proof.
        public bool HasDeclaredStartBoundary {get {if(Records==null)return false;foreach(var record in Records)if(record.Kind=="practice_start_boundary")return true;return false;}}
        public bool ContainsPotentiallySensitiveData {get{return true;}} public bool IdentityAuthenticated {get{return false;}}
        public bool HardwareCaptureTimeVerified {get{return false;}} public bool ContinuousCoverageProven {get{return false;}}
        public bool CompleteSessionProven {get{return false;}} public bool DurableStorageProven {get{return false;}}
        public bool SourceClockBindingAuthenticated {get{return false;}} public bool ConsentAuthenticated {get{return false;}}
        public bool ResearchAcceptanceEstablished {get{return false;}}
    }
    static class VivePracticeJournalV3Codec
    {
        internal const int MaxFrame=65536;
        internal static readonly byte[] Magic=Encoding.ASCII.GetBytes("VPRAC003");
        internal static readonly CultureInfo Inv=CultureInfo.InvariantCulture;
        internal static bool ValidTime(double v){return !Double.IsNaN(v)&&!Double.IsInfinity(v)&&v>=0;}
        internal static string N(double v){if(!ValidTime(v))throw new ArgumentException("Invalid local time.");return v.ToString("R",Inv);}
        internal static string N(long v){return v.ToString(Inv);}
        internal static string Bits(double value){return unchecked((ulong)BitConverter.DoubleToInt64Bits(value)).ToString("x16",Inv);}
        internal static double Time(string text)
        {double v;if(!Double.TryParse(text,NumberStyles.Float,Inv,out v)||!ValidTime(v)||N(v)!=text)throw new InvalidDataException("Invalid time.");return v;}
        internal static long Integer(string text)
        {long v;if(!Int64.TryParse(text,NumberStyles.AllowLeadingSign,Inv,out v)||N(v)!=text)throw new InvalidDataException("Invalid integer.");return v;}
        internal static ulong Mask(string text)
        {ulong v;if(!UInt64.TryParse(text,NumberStyles.None,Inv,out v)||v.ToString(Inv)!=text)throw new InvalidDataException("Invalid mask.");return v;}
        internal static void CheckBits(string value)
        {ulong ignored;if(value==null||value.Length!=16||!UInt64.TryParse(value,NumberStyles.AllowHexSpecifier,Inv,out ignored)||ignored.ToString("x16",Inv)!=value)throw new InvalidDataException("Invalid raw double bits.");}
        internal static string Get(IReadOnlyDictionary<string,string> fields,string name)
        {string value;if(!fields.TryGetValue(name,out value))throw new InvalidDataException("Required field missing.");return value;}
        internal static void Exact(IReadOnlyDictionary<string,string> fields,params string[] names)
        {if(fields.Count!=names.Length)throw new InvalidDataException("Unexpected field count.");foreach(var name in names)if(!fields.ContainsKey(name))throw new InvalidDataException("Required field missing.");}
        internal static bool Boolean(string value)
        {if(value=="true")return true;if(value=="false")return false;throw new InvalidDataException("Invalid boolean.");}
        internal static bool Fatal(Exception e){return e is OutOfMemoryException||e is StackOverflowException||e is AccessViolationException;}
        internal static string Encode(string value){return Convert.ToBase64String(ContinuousJournal.Utf8.GetBytes(value));}
        internal static string TaskDetail(JournalEvent item)
        {
            if(item!=null&&(item.CarriesNamedProvenance||item.CarriesLegacyRawProvenance))throw new InvalidOperationException("Legacy practice detail cannot encode versioned raw or named-option provenance.");
            if(String.IsNullOrWhiteSpace(item.Kind)||item.Kind.Length>256||item.TrialId!=null&&item.TrialId.Length>256)throw new ArgumentException("Invalid task fields.");
            var keys=new List<string>();foreach(var pair in item.Fields)keys.Add(pair.Key);keys.Sort(StringComparer.Ordinal);
            if(keys.Count>64)throw new ArgumentException("Too many task fields.");
            var detail=new StringBuilder("AE2;SIMULATION_DEVELOPMENT;").Append(N(item.Sequence)).Append(';').Append(Encode(item.Kind)).Append(';').Append(N(keys.Count));
            foreach(var key in keys){if(String.IsNullOrWhiteSpace(key)||key.Length>128)throw new ArgumentException("Invalid task field.");string value=item.Fields[key];
                if(value!=null&&value.Length>1024)throw new ArgumentException("Task field too long.");detail.Append(';').Append(Encode(key)).Append(';').Append(value==null?"~":Encode(value));
                if(detail.Length>1024)throw new ArgumentException("Task detail too long.");}
            if(detail.Length>1024)throw new ArgumentException("Task detail too long.");return detail.ToString();
        }
    }

    // A caller-declared ordering checkpoint, not proof of consent, identity, or
    // provenance. The host must separately bind this to its real Experiment.
    public sealed class VivePracticeRecordingPreparationV3
    {
        [ThreadStatic] static bool creating;
        readonly IClock clock;readonly int owner,attempt;readonly string session,epoch;int consumed;
        public double ChoicesObservedLocalSeconds {get;private set;}
        VivePracticeRecordingPreparationV3(IClock clock,string session,int attempt,string epoch,double at)
        {this.clock=clock;this.session=session;this.attempt=attempt;this.epoch=epoch;owner=Thread.CurrentThread.ManagedThreadId;ChoicesObservedLocalSeconds=at;}
        public static VivePracticeRecordingPreparationV3 Create(IClock clock,string session,int attempt,string epoch,bool explicitEyeChoice,bool explicitTaskChoice)
        {
            if(!explicitEyeChoice||!explicitTaskChoice)throw new InvalidOperationException("Both explicit recording choices are required.");
            if(clock==null)throw new ArgumentNullException("clock");
            WireCodec.RequireIdentity(session);WireCodec.RequireIdentity(epoch);if(attempt<1)throw new ArgumentException("Invalid attempt.");
            if(creating)throw new InvalidOperationException("Preparation already in progress.");
            creating=true;
            try {double at=clock.Now;if(!VivePracticeJournalV3Codec.ValidTime(at))throw new InvalidOperationException("Invalid local clock.");return new VivePracticeRecordingPreparationV3(clock,session,attempt,epoch,at);}
            catch(Exception e) when(!VivePracticeJournalV3Codec.Fatal(e)){throw new InvalidOperationException("Practice preparation failed.");}
            finally {creating=false;}
        }
        // Preparation can be revoked only before it is claimed. Afterwards the
        // host must stop the writer; this method does not stop in-flight I/O.
        public void Revoke()
        {if(Thread.CurrentThread.ManagedThreadId!=owner)throw new InvalidOperationException("Preparation owner required.");
            if(Interlocked.CompareExchange(ref consumed,2,0)!=0)throw new InvalidOperationException("Preparation already claimed or revoked; stop the writer separately.");}
        internal double Claim(IClock candidate,VivePracticeContextV3 context)
        {
            if(Interlocked.CompareExchange(ref consumed,1,0)!=0)throw new InvalidOperationException("Preparation already used.");
            if(Thread.CurrentThread.ManagedThreadId!=owner||!Object.ReferenceEquals(clock,candidate)||context==null||session!=context.SessionId||attempt!=context.PracticeAttempt||epoch!=context.RecordingEpochId)
                throw new InvalidOperationException("Preparation owner, clock, and declared context must match.");
            return ChoicesObservedLocalSeconds;
        }
    }

    // Independent v3 writer. The caller supplies a NEW empty seekable writable
    // stream. It opens no path, starts no devices and never closes the stream.
    // A failed/uncertain write is terminal and is never retried automatically.
    public sealed class VivePracticeJournalV3Writer
    {
        readonly Stream stream;readonly IClock clock;readonly VivePracticeContextV3 context;readonly int ownerThread,maxRecords;readonly long maxBytes;
        readonly VivePracticeTaskCursorContractV3 taskCursorContract;
        readonly byte[] token=Guid.NewGuid().ToByteArray();byte[] previous=new byte[32];long bytes,sequence;double lastNow,opened,lastEye=-1,lastTask=-1;
        long lastTaskSequence=-1,practiceStartSequence;int lastNativeEye,lastNativeTime;bool hasEye,hasTask,unavailable;
        ViveDevelopmentGapReason gapReason;double gapDetected=-1;int externalFault;bool active;readonly object stateGate=new object();
        VivePracticeV3WriterState state;
        public VivePracticeV3WriterState State {get{return Interlocked.CompareExchange(ref externalFault,0,0)!=0?VivePracticeV3WriterState.Faulted:state;}private set{state=value;}}
        public string LastIssue {get;private set;}
        public bool IdentityAuthenticated {get{return false;}} public bool ContinuousCoverageProven {get{return false;}}
        public bool HardwareCaptureTimeVerified {get{return false;}} public bool ResearchAcceptanceEstablished {get{return false;}}
        public bool SourceClockBindingAuthenticated {get{return false;}} public bool ConsentAuthenticated {get{return false;}}
        // This independent development writer is never an experiment gate.
        public bool ProductionGateOpen {get{return false;}}
        public VivePracticeJournalV3Writer(Stream destination,IClock clock,VivePracticeContextV3 context,JournalEvent declaredPracticeStart,ViveDevelopmentDataSource declaredEyeSource,
            bool explicitEyeRecordingChoice,bool explicitTaskRecordingChoice,int maximumRecords,long maximumBytes)
            :this(destination,clock,context,declaredPracticeStart,declaredEyeSource,explicitEyeRecordingChoice,explicitTaskRecordingChoice,maximumRecords,maximumBytes,null){}
        public VivePracticeJournalV3Writer(Stream destination,IClock clock,VivePracticeContextV3 context,JournalEvent declaredPracticeStart,ViveDevelopmentDataSource declaredEyeSource,
            bool explicitEyeRecordingChoice,bool explicitTaskRecordingChoice,int maximumRecords,long maximumBytes,VivePracticeRecordingPreparationV3 preparation)
            :this(destination,clock,context,declaredPracticeStart,declaredEyeSource,explicitEyeRecordingChoice,explicitTaskRecordingChoice,
                maximumRecords,maximumBytes,preparation,VivePracticeTaskCursorContractV3.Legacy){}
        // Internal structural contract only. This overload is not connected to
        // a trusted event producer and does not authorize recording or gate entry.
        internal VivePracticeJournalV3Writer(Stream destination,IClock clock,VivePracticeContextV3 context,JournalEvent declaredPracticeStart,ViveDevelopmentDataSource declaredEyeSource,
            bool explicitEyeRecordingChoice,bool explicitTaskRecordingChoice,int maximumRecords,long maximumBytes,VivePracticeRecordingPreparationV3 preparation,
            VivePracticeTaskCursorContractV3 taskCursorContract)
        {
            // Refusal occurs before inspecting even the caller's stream properties.
            if(declaredPracticeStart!=null&&(declaredPracticeStart.CarriesNamedProvenance||declaredPracticeStart.CarriesLegacyRawProvenance))
                throw new InvalidOperationException("Legacy practice journal cannot encode versioned raw or named-option provenance.");
            if(preparation==null)throw new InvalidOperationException("Earlier preparation is required.");
            double choicesObserved=preparation.Claim(clock,context);
            if(!explicitEyeRecordingChoice||!explicitTaskRecordingChoice)throw new InvalidOperationException("Both explicit recording choices are required.");
            if(destination==null||clock==null||context==null)throw new ArgumentNullException("destination/clock/context");
            if(!Enum.IsDefined(typeof(VivePracticeTaskCursorContractV3),taskCursorContract))throw new ArgumentException("Unknown task cursor contract.");
            if(!Enum.IsDefined(typeof(ViveDevelopmentDataSource),declaredEyeSource)||maximumRecords<4||maximumRecords>100000||maximumBytes<256||maximumBytes>67108864)
                throw new ArgumentException("Explicit source and bounded limits required.");
            if(!destination.CanWrite||!destination.CanSeek||destination.Position!=0||destination.Length!=0)throw new ArgumentException("A new empty writable seekable stream is required.");
            double now=clock.Now;if(clock.ClockId!=context.ClockId||!VivePracticeJournalV3Codec.ValidTime(now)||
                (context.ObservedPracticeStartLocalSeconds.HasValue&&context.ObservedPracticeStartLocalSeconds.Value>now))throw new ArgumentException("A matching valid local clock is required.");
            string attempt;
            if(declaredPracticeStart==null||declaredPracticeStart.Kind!="practice_started"||declaredPracticeStart.SessionId!=context.SessionId||
                declaredPracticeStart.ClockId!=context.ClockId||declaredPracticeStart.BlockIndex!=-1||declaredPracticeStart.Segment<0||
                declaredPracticeStart.Sequence<0||declaredPracticeStart.Sequence==Int64.MaxValue||!context.ObservedPracticeStartLocalSeconds.HasValue||
                declaredPracticeStart.Time!=context.ObservedPracticeStartLocalSeconds.Value||declaredPracticeStart.Fields==null||!declaredPracticeStart.Fields.TryGetValue("attempt",out attempt)||
                attempt!=VivePracticeJournalV3Codec.N(context.PracticeAttempt))throw new ArgumentException("Matching practice_started boundary event is required.");
            if(declaredPracticeStart.Time<=choicesObserved)throw new ArgumentException("Practice start must follow the declared choices.");
            if(taskCursorContract==VivePracticeTaskCursorContractV3.StartContiguousV1&&declaredPracticeStart.Sequence>=Int64.MaxValue-1)
                throw new ArgumentException("Start sequence leaves no representable contiguous task event.");
            string startDetail=VivePracticeJournalV3Codec.TaskDetail(declaredPracticeStart);
            stream=destination;this.clock=clock;this.context=context;this.taskCursorContract=taskCursorContract;maxRecords=maximumRecords;maxBytes=maximumBytes;ownerThread=Thread.CurrentThread.ManagedThreadId;opened=lastNow=now;
            practiceStartSequence=declaredPracticeStart.Sequence;
            State=VivePracticeV3WriterState.Open;
            try
            {
                stream.Write(VivePracticeJournalV3Codec.Magic,0,VivePracticeJournalV3Codec.Magic.Length);bytes=VivePracticeJournalV3Codec.Magic.Length;
                var f=new Dictionary<string,string>{{"schema","vive-practice-journal-v3"},{"data_source",declaredEyeSource.ToString()},
                    {"session_id",context.SessionId},{"participant_id",context.ParticipantId},{"stream_id",context.StreamId},{"device_id",context.DeviceId},
                    {"local_clock_id",context.ClockId},{"mapping_version",context.MappingVersion},{"practice_attempt",VivePracticeJournalV3Codec.N(context.PracticeAttempt)},
                    {"recording_epoch_id",context.RecordingEpochId},{"recovery_ordinal",VivePracticeJournalV3Codec.N(context.RecoveryOrdinal)},
                    {"previous_epoch_id",context.PreviousEpochId},{"epoch_reason",context.EpochReason.ToString()},
                    {"practice_start_observed_local_seconds",context.ObservedPracticeStartLocalSeconds.HasValue?VivePracticeJournalV3Codec.N(context.ObservedPracticeStartLocalSeconds.Value):null},
                    {"practice_start_sequence",VivePracticeJournalV3Codec.N(practiceStartSequence)},{"practice_start_binding","CALLER_DECLARED"},
                    {"recording_enabled_local_seconds",VivePracticeJournalV3Codec.N(opened)},{"block_index","-1"},{"condition",null},{"main_source_status","UNSELECTED"},
                    {"pre_consent_coverage","UNKNOWN"},{"hardware_capture_time_verified","false"},{"identity_authenticated","false"},
                    {"source_clock_binding","CALLER_DECLARED"},{"consent_authentication","NOT_ESTABLISHED"}};
                if(taskCursorContract==VivePracticeTaskCursorContractV3.StartContiguousV1)
                    f.Add("task_cursor_contract","START_CONTIGUOUS_V1");
                Append("header",f);
                Append("practice_start_boundary",new Dictionary<string,string>{
                    {"choices_observed_local_seconds",VivePracticeJournalV3Codec.N(choicesObserved)},
                    {"task_sequence",VivePracticeJournalV3Codec.N(declaredPracticeStart.Sequence)},
                    {"observed_local_seconds",VivePracticeJournalV3Codec.N(declaredPracticeStart.Time)},
                    {"task_segment",VivePracticeJournalV3Codec.N(declaredPracticeStart.Segment)},
                    {"trial_id",declaredPracticeStart.TrialId},{"detail",startDetail},{"binding","CALLER_DECLARED"}});
            }
            catch(Exception e) when(!VivePracticeJournalV3Codec.Fatal(e)){Fault("header_write_failed");throw new IOException("Practice journal initialization failed.");}
        }
        void Owner(){if(Thread.CurrentThread.ManagedThreadId!=ownerThread){lock(stateGate){Interlocked.Exchange(ref externalFault,1);}throw new InvalidOperationException("Writer owner thread required.");}}
        void RequireOpen(){Owner();if(State!=VivePracticeV3WriterState.Open)throw new InvalidOperationException("Practice writer is not open.");}
        void Enter(){RequireOpen();if(active){Fault("reentrant_write");throw new InvalidOperationException("Writer operation already active.");}active=true;}
        void Fault(string issue){if(State==VivePracticeV3WriterState.Open){State=VivePracticeV3WriterState.Faulted;LastIssue=issue;}}
        double Now()
        {
            try{double now=clock.Now;if(State!=VivePracticeV3WriterState.Open||clock.ClockId!=context.ClockId||State!=VivePracticeV3WriterState.Open||!VivePracticeJournalV3Codec.ValidTime(now)||now<lastNow)throw new InvalidOperationException();lastNow=now;return now;}
            catch(Exception e) when(!VivePracticeJournalV3Codec.Fatal(e)){Fault("clock_failed");throw new InvalidOperationException("Practice local clock failed.");}
        }
        void Append(string kind,Dictionary<string,string> fields)
        {
            if(State!=VivePracticeV3WriterState.Open)throw new InvalidOperationException("Practice writer is not open.");
            if(sequence>=maxRecords)throw new InvalidOperationException("Record budget exceeded.");
            byte[] payload=ContinuousJournal.Payload(sequence,token,kind,fields);
            if(payload.Length>VivePracticeJournalV3Codec.MaxFrame||bytes>maxBytes-(8L+payload.Length+32))throw new InvalidOperationException("Byte budget exceeded.");
            byte[] hash=ContinuousJournal.Digest(previous,payload),frame=new byte[8+payload.Length+hash.Length];
            ContinuousJournal.PutInt(frame,payload.Length);ContinuousJournal.PutInt(frame,~payload.Length,4);
            Buffer.BlockCopy(payload,0,frame,8,payload.Length);Buffer.BlockCopy(hash,0,frame,8+payload.Length,hash.Length);
            if(State!=VivePracticeV3WriterState.Open)throw new InvalidOperationException("Practice writer fault before write.");
            stream.Write(frame,0,frame.Length);
            lock(stateGate){if(State!=VivePracticeV3WriterState.Open)throw new IOException("Writer fault during write.");bytes+=frame.Length;sequence++;previous=hash;}
        }
        void SafeAppend(string kind,Dictionary<string,string> fields)
        {try{Append(kind,fields);}catch(Exception e) when(!VivePracticeJournalV3Codec.Fatal(e)){Fault("write_failed_or_uncertain");throw new IOException("Practice journal write failed or is uncertain.");}}
        public void WriteTaskEvent(JournalEvent item)
        {if(item!=null&&(item.CarriesNamedProvenance||item.CarriesLegacyRawProvenance))throw new InvalidOperationException("Legacy practice journal cannot encode versioned raw or named-option provenance.");Enter();try{WriteTaskEventCore(item);}finally{active=false;}}
        void WriteTaskEventCore(JournalEvent item)
        {
            double now=Now();
            if(item==null||item.SessionId!=context.SessionId||item.ClockId!=context.ClockId||item.BlockIndex!=-1||item.Segment<0||
                item.Sequence<=practiceStartSequence||item.Sequence==Int64.MaxValue||!VivePracticeJournalV3Codec.ValidTime(item.Time)||item.Time>now||
                (taskCursorContract==VivePracticeTaskCursorContractV3.Legacy
                    ?item.Time<=opened||(hasTask&&(item.Sequence!=lastTaskSequence+1||item.Time<lastTask))
                    :item.Time<context.ObservedPracticeStartLocalSeconds.Value||item.Sequence!=(hasTask?lastTaskSequence:practiceStartSequence)+1||
                        (hasTask&&item.Time<lastTask)))
            {Fault("task_event_invalid");throw new ArgumentException("Practice task event is not current or contiguous.");}
            if(item.Kind=="practice_started"){Fault("new_practice_start_requires_new_epoch");throw new ArgumentException("A new practice start requires a new recording epoch.");}
            if(item.Kind=="practice_completed")
            {string attempt;if(!item.Fields.TryGetValue("attempt",out attempt)||attempt!=VivePracticeJournalV3Codec.N(context.PracticeAttempt))
                {Fault("practice_attempt_mismatch");throw new ArgumentException("Practice attempt does not match the declared context.");}}
            string detail;try{detail=VivePracticeJournalV3Codec.TaskDetail(item);}catch(Exception e) when(!VivePracticeJournalV3Codec.Fatal(e)){Fault("task_event_invalid");throw new ArgumentException("Practice task event is invalid.");}
            SafeAppend("task_event",new Dictionary<string,string>{{"task_sequence",VivePracticeJournalV3Codec.N(item.Sequence)},
                {"observed_local_seconds",VivePracticeJournalV3Codec.N(item.Time)},{"task_segment",VivePracticeJournalV3Codec.N(item.Segment)},
                {"trial_id",item.TrialId},{"detail",detail}});
            hasTask=true;lastTaskSequence=item.Sequence;lastTask=item.Time;
        }
        // Detection time is a local observation, not a device failure onset.
        public void MarkUnavailable(ViveDevelopmentGapReason reason)
        {Enter();try{MarkUnavailableCore(reason,Now());}finally{active=false;}}
        void MarkUnavailableCore(ViveDevelopmentGapReason reason,double now)
        {
            if(!Enum.IsDefined(typeof(ViveDevelopmentGapReason),reason)){Fault("gap_reason_invalid");throw new ArgumentException("Invalid detected reason.");}
            if(unavailable){if(reason!=gapReason){Fault("gap_reason_changed");throw new InvalidOperationException("A different reason requires review.");}return;}
            SafeAppend("unavailable_detected",new Dictionary<string,string>{{"detected_local_seconds",VivePracticeJournalV3Codec.N(now)},{"reason",reason.ToString()}});
            unavailable=true;gapReason=reason;gapDetected=now;
        }
        static bool Usable(ViveProEyePoll poll)
        {
            if(poll==null||poll.Eye==null||poll.Runtime!=ViveEyeRuntimeState.Working||poll.Calibration!=ViveEyeCalibrationState.ReportedReady||
                !poll.EyeDeviceConnected||!poll.NativeResultCode.HasValue||poll.NativeResultCode.Value!=0||poll.Eye.NoUser||poll.Eye.Combined==null||
                (poll.Eye.Combined.ValidityMask&2UL)==0||!poll.Eye.Combined.DirectionReportedValid)return false;
            Vec3 normalized;return ViveProEyeSdkDirection.TryGetUnitDirection(poll.Eye.Combined.Direction,out normalized);
        }
        static ViveDevelopmentGapReason Reason(ViveProEyePoll poll)
        {
            if(!poll.EyeDeviceConnected)return ViveDevelopmentGapReason.DeviceUnavailable;
            if(poll.Calibration!=ViveEyeCalibrationState.ReportedReady)return ViveDevelopmentGapReason.CalibrationUnavailable;
            if(poll.Runtime!=ViveEyeRuntimeState.Working||!poll.NativeResultCode.HasValue||poll.NativeResultCode.Value!=0)return ViveDevelopmentGapReason.RuntimeFailure;
            return ViveDevelopmentGapReason.Other;
        }
        // Independent caller-declared, unauthenticated raw-poll development seam.
        // It does not bind the poll to a participant, current task, device,
        // consent, or a live ticket and MUST NOT be wired into a production UI or
        // used to open any experiment gate. Candidate.RawPoll can be passed here:
        // closing WriteCandidate does not secure this separate path. Unusable eye
        // observations produce an unavailable marker, not invented gaze.
        public VivePracticeV3EyeWrite WriteEye(ViveProEyePoll poll)
        {Enter();try{return WriteEyeCore(poll);}finally{active=false;}}
        // Candidate context and live consent binding are not yet established.
        // Do not inspect or write a candidate, even if it looks fresh.
        public VivePracticeV3EyeWrite WriteCandidate(VivePracticeEyeCandidate candidate)
        {Enter();try{Fault("candidate_identity_binding_unavailable");return VivePracticeV3EyeWrite.Rejected;}finally{active=false;}}
        VivePracticeV3EyeWrite WriteEyeCore(ViveProEyePoll poll)
        {
            double now=Now();
            if(poll==null||poll.Eye==null||!Enum.IsDefined(typeof(ViveEyeRuntimeState),poll.Runtime)||!Enum.IsDefined(typeof(ViveEyeCalibrationState),poll.Calibration))
            {Fault("eye_observation_missing");return VivePracticeV3EyeWrite.Rejected;}
            var eye=poll.Eye;
            if(!VivePracticeJournalV3Codec.ValidTime(eye.ObservedLocalSeconds)||eye.ObservedLocalSeconds<=opened||eye.ObservedLocalSeconds>now)
            {Fault("eye_time_invalid");return VivePracticeV3EyeWrite.Rejected;}
            if(!Usable(poll)){MarkUnavailableCore(Reason(poll),now);return VivePracticeV3EyeWrite.Unavailable;}
            if(hasEye&&eye.FrameSequence==lastNativeEye&&eye.Timestamp==lastNativeTime&&eye.ObservedLocalSeconds==lastEye)return VivePracticeV3EyeWrite.Duplicate;
            if(unavailable&&eye.ObservedLocalSeconds<=gapDetected)return VivePracticeV3EyeWrite.WaitingForFreshRecovery;
            if(hasEye&&(eye.FrameSequence<=lastNativeEye||eye.Timestamp<=lastNativeTime||eye.ObservedLocalSeconds<=lastEye))
            {Fault("eye_order_invalid");return VivePracticeV3EyeWrite.Rejected;}
            if(!hasEye)SafeAppend("initial_unknown",new Dictionary<string,string>{{"start_local_seconds",VivePracticeJournalV3Codec.N(opened)},{"end_local_seconds",VivePracticeJournalV3Codec.N(eye.ObservedLocalSeconds)}});
            else if(unavailable)SafeAppend("recovery_unknown",new Dictionary<string,string>{{"start_local_seconds",VivePracticeJournalV3Codec.N(lastEye)},
                {"end_local_seconds",VivePracticeJournalV3Codec.N(eye.ObservedLocalSeconds)},{"reason",gapReason.ToString()}});
            var ray=eye.Combined;
            var f=new Dictionary<string,string>{{"eye_observed_local_seconds",VivePracticeJournalV3Codec.N(eye.ObservedLocalSeconds)},
                {"native_frame_sequence",VivePracticeJournalV3Codec.N(eye.FrameSequence)},{"native_timestamp_opaque",VivePracticeJournalV3Codec.N(eye.Timestamp)},
                {"runtime",poll.Runtime.ToString()},{"calibration",poll.Calibration.ToString()},{"device_connected",poll.EyeDeviceConnected?"true":"false"},
                {"native_result_code",poll.NativeResultCode.HasValue?VivePracticeJournalV3Codec.N(poll.NativeResultCode.Value):null},
                {"native_runtime_status",poll.NativeRuntimeStatus.HasValue?VivePracticeJournalV3Codec.N(poll.NativeRuntimeStatus.Value):null},
                {"no_user",eye.NoUser?"true":"false"},{"combined_present",ray==null?"false":"true"},
                {"combined_mask",ray==null?null:ray.ValidityMask.ToString(CultureInfo.InvariantCulture)},
                {"direction_reported_valid",ray==null?null:(ray.DirectionReportedValid?"true":"false")},
                {"raw_x_bits",ray==null?null:VivePracticeJournalV3Codec.Bits(ray.Direction.X)},
                {"raw_y_bits",ray==null?null:VivePracticeJournalV3Codec.Bits(ray.Direction.Y)},
                {"raw_z_bits",ray==null?null:VivePracticeJournalV3Codec.Bits(ray.Direction.Z)}};
            // A bare SDK poll does not carry a freshness-checked head/derived
            // assessment. Do not infer one or invent a direction.
            f.Add("eye_extension_version","1");f.Add("head_usability","NOT_EVALUATED");
            foreach(string key in new[]{"head_observed_local_seconds","head_sequence","head_connected","head_rotation_reported_valid",
                "head_qx_bits","head_qy_bits","head_qz_bits","head_qw_bits","candidate_checked_local_seconds","candidate_max_age_seconds",
                "derived_frame","derived_x_bits","derived_y_bits","derived_z_bits"})f.Add(key,null);
            f.Add("derived_status","NOT_COMPUTED");
            SafeAppend("eye_observation",f);hasEye=true;lastEye=eye.ObservedLocalSeconds;lastNativeEye=eye.FrameSequence;lastNativeTime=eye.Timestamp;unavailable=false;
            return VivePracticeV3EyeWrite.Written;
        }
        // An explicit close records unknown tail, including a still-open gap.
        // Flush success is not stable-storage or whole-session proof.
        public bool Close()
        {Enter();try{return CloseCore();}finally{active=false;}}
        bool CloseCore()
        {
            double now=Now();
            try
            {
                Append("tail_unknown",new Dictionary<string,string>{{"start_local_seconds",VivePracticeJournalV3Codec.N(hasEye?lastEye:opened)},
                    {"end_local_seconds",VivePracticeJournalV3Codec.N(now)},{"reason",!hasEye?"no_observation_yet":unavailable?"unavailable_at_close":"after_last_observation"}});
                Append("close",new Dictionary<string,string>());stream.Flush();
                lock(stateGate){if(State!=VivePracticeV3WriterState.Open)throw new IOException("Writer fault during flush.");State=VivePracticeV3WriterState.Closed;return true;}
            }
            catch(Exception e) when(!VivePracticeJournalV3Codec.Fatal(e)){State=VivePracticeV3WriterState.CloseOutcomeUncertain;LastIssue="close_or_flush_uncertain";return false;}
        }
        // Never manufactures a close; the caller retains stream ownership.
        public void Abort(){Enter();try{State=VivePracticeV3WriterState.Aborted;LastIssue="aborted_unclean";}finally{active=false;}}
    }
}
