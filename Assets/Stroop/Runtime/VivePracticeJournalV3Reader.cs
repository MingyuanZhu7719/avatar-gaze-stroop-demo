using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using AvatarStroop.Core;

namespace AvatarStroop.Runtime
{
    // Strict v3 reader: never opens a path, closes the caller's stream, accepts
    // v1/v2 magic, authenticates IDs, or asserts continuous/research coverage.
    public static class VivePracticeJournalV3Reader
    {
        sealed class FrameState
        {
            internal bool Header,HasEye,HasTask,Unavailable,InitialPending,RecoveryPending,Tail,Closed,FirstAfterHeader,HasCandidateCheck,HasUsableHead,StartContiguousV1;
            internal double Opened,LastEye,LastTask,Detected,PendingEnd,LastCandidateChecked,LastUsableHeadTime;
            internal long LastTaskSequence,PracticeStartSequence,LastUsableHeadSequence;internal int LastNativeSequence,LastNativeTime,Attempt;
            internal string GapReason,Session,Clock;internal VivePracticeContextV3 Context;
            static int Int(string value){return checked((int)VivePracticeJournalV3Codec.Integer(value));}
            static string F(IReadOnlyDictionary<string,string> f,string key){return VivePracticeJournalV3Codec.Get(f,key);}
            static double T(IReadOnlyDictionary<string,string> f,string key){return VivePracticeJournalV3Codec.Time(F(f,key));}
            static TEnum EnumValue<TEnum>(string value) where TEnum:struct
            {TEnum parsed;if(!Enum.TryParse(value,false,out parsed)||!Enum.IsDefined(typeof(TEnum),parsed)||parsed.ToString()!=value)throw new InvalidDataException("Invalid enum.");return parsed;}
            internal void Observe(string kind,Dictionary<string,string> f)
            {
                if(!Header){ReadHeader(kind,f);return;}
                if(Closed||(Tail&&kind!="close"))throw new InvalidDataException("Data after tail/close.");
                if(StartContiguousV1&&FirstAfterHeader&&kind!="practice_start_boundary")throw new InvalidDataException("A declared start boundary is required.");
                if((InitialPending||RecoveryPending)&&kind!="eye_observation")throw new InvalidDataException("Coverage marker is not followed by observation.");
                if(kind=="practice_start_boundary") {if(!FirstAfterHeader)throw new InvalidDataException("Duplicate or late practice start.");FirstAfterHeader=false;StartBoundary(f);return;}
                FirstAfterHeader=false;
                switch(kind)
                {
                    case "task_event": Task(f);return;
                    case "unavailable_detected": UnavailableFrame(f);return;
                    case "initial_unknown": Initial(f);return;
                    case "recovery_unknown": Recovery(f);return;
                    case "eye_observation": Eye(f);return;
                    case "tail_unknown": TailFrame(f);return;
                    case "close": VivePracticeJournalV3Codec.Exact(f);if(!Tail)throw new InvalidDataException("Missing unknown tail.");Closed=true;return;
                    default:throw new InvalidDataException("Unknown record kind.");
                }
            }
            void ReadHeader(string kind,Dictionary<string,string> f)
            {
                if(kind!="header")throw new InvalidDataException("Header required.");
                Dictionary<string,string> baseFields=f;
                if(f.ContainsKey("task_cursor_contract"))
                {
                    if(F(f,"task_cursor_contract")!="START_CONTIGUOUS_V1")throw new InvalidDataException("Unknown task cursor contract.");
                    StartContiguousV1=true;baseFields=new Dictionary<string,string>(f);baseFields.Remove("task_cursor_contract");
                }
                VivePracticeJournalV3Codec.Exact(baseFields,"schema","data_source","session_id","participant_id","stream_id","device_id","local_clock_id","mapping_version",
                    "practice_attempt","recording_epoch_id","recovery_ordinal","previous_epoch_id","epoch_reason","practice_start_observed_local_seconds","practice_start_sequence","practice_start_binding",
                    "recording_enabled_local_seconds","block_index","condition","main_source_status","pre_consent_coverage","hardware_capture_time_verified","identity_authenticated",
                    "source_clock_binding","consent_authentication");
                if(F(f,"schema")!="vive-practice-journal-v3"||F(f,"block_index")!="-1"||F(f,"condition")!=null||F(f,"main_source_status")!="UNSELECTED"||
                    F(f,"pre_consent_coverage")!="UNKNOWN"||F(f,"hardware_capture_time_verified")!="false"||F(f,"identity_authenticated")!="false"||
                    F(f,"source_clock_binding")!="CALLER_DECLARED"||F(f,"consent_authentication")!="NOT_ESTABLISHED"||F(f,"practice_start_binding")!="CALLER_DECLARED")
                    throw new InvalidDataException("Invalid practice context.");
                EnumValue<ViveDevelopmentDataSource>(F(f,"data_source"));var reason=EnumValue<VivePracticeEpochReasonV3>(F(f,"epoch_reason"));
                string start=F(f,"practice_start_observed_local_seconds");Opened=T(f,"recording_enabled_local_seconds");
                double? practiceStart=start==null?(double?)null:VivePracticeJournalV3Codec.Time(start);
                PracticeStartSequence=VivePracticeJournalV3Codec.Integer(F(f,"practice_start_sequence"));
                if(!practiceStart.HasValue||practiceStart.Value>Opened||PracticeStartSequence<0||PracticeStartSequence==Int64.MaxValue)throw new InvalidDataException("Invalid practice start.");
                if(StartContiguousV1&&PracticeStartSequence>=Int64.MaxValue-1)throw new InvalidDataException("Start sequence leaves no contiguous task event.");
                Attempt=Int(F(f,"practice_attempt"));Session=F(f,"session_id");Clock=F(f,"local_clock_id");
                Context=new VivePracticeContextV3(Session,F(f,"participant_id"),F(f,"stream_id"),F(f,"device_id"),Clock,F(f,"mapping_version"),
                    Attempt,F(f,"recording_epoch_id"),Int(F(f,"recovery_ordinal")),F(f,"previous_epoch_id"),reason,practiceStart);
                Header=true;FirstAfterHeader=true;
            }
            void StartBoundary(Dictionary<string,string> f)
            {
                VivePracticeJournalV3Codec.Exact(f,"choices_observed_local_seconds","task_sequence","observed_local_seconds","task_segment","trial_id","detail","binding");
                double choice=T(f,"choices_observed_local_seconds"),time=T(f,"observed_local_seconds");
                long seq=VivePracticeJournalV3Codec.Integer(F(f,"task_sequence"));int segment=Int(F(f,"task_segment"));
                string trial=F(f,"trial_id"),attempt;ViveAuthoritativeTaskPayload payload;
                if(F(f,"binding")!="CALLER_DECLARED"||choice>=time||time>Opened||time!=Context.ObservedPracticeStartLocalSeconds.Value||seq!=PracticeStartSequence||segment<0||
                    (trial!=null&&trial.Length>256)||!ViveParticipantRecordingBridge.TryDecodeAuthoritativeTaskPayload(F(f,"detail"),out payload)||
                    payload.Sequence!=seq||payload.Kind!="practice_started"||!payload.Fields.TryGetValue("attempt",out attempt)||attempt!=VivePracticeJournalV3Codec.N(Attempt))
                    throw new InvalidDataException("Invalid declared practice start event.");
            }
            void Task(Dictionary<string,string> f)
            {
                VivePracticeJournalV3Codec.Exact(f,"task_sequence","observed_local_seconds","task_segment","trial_id","detail");
                long seq=VivePracticeJournalV3Codec.Integer(F(f,"task_sequence"));double time=T(f,"observed_local_seconds");
                int taskSegment=Int(F(f,"task_segment"));
                ViveAuthoritativeTaskPayload payload;
                if(seq<=PracticeStartSequence||seq==Int64.MaxValue||taskSegment<0||
                    (StartContiguousV1
                        ?time<Context.ObservedPracticeStartLocalSeconds.Value||seq!=(HasTask?LastTaskSequence:PracticeStartSequence)+1||(HasTask&&time<LastTask)
                        :time<=Opened||(HasTask&&(LastTaskSequence==Int64.MaxValue-1||seq!=LastTaskSequence+1||time<LastTask)))||
                    !ViveParticipantRecordingBridge.TryDecodeAuthoritativeTaskPayload(F(f,"detail"),out payload)||payload.Sequence!=seq)
                    throw new InvalidDataException("Invalid task event.");
                if(payload.Kind=="practice_started")throw new InvalidDataException("New practice start requires a new epoch.");
                if(payload.Kind=="practice_completed")
                {string attempt;if(!payload.Fields.TryGetValue("attempt",out attempt)||attempt!=VivePracticeJournalV3Codec.N(Attempt))throw new InvalidDataException("Practice attempt mismatch.");}
                string trial=F(f,"trial_id");if(trial!=null&&trial.Length>256)throw new InvalidDataException("Invalid trial ID.");
                HasTask=true;LastTaskSequence=seq;LastTask=time;
            }
            void UnavailableFrame(Dictionary<string,string> f)
            {
                VivePracticeJournalV3Codec.Exact(f,"detected_local_seconds","reason");double t=T(f,"detected_local_seconds");
                var reason=EnumValue<ViveDevelopmentGapReason>(F(f,"reason"));
                if(Unavailable||t<Opened||(HasEye&&t<LastEye))throw new InvalidDataException("Invalid loss detection.");
                Unavailable=true;Detected=t;GapReason=reason.ToString();
            }
            void Initial(Dictionary<string,string> f)
            {
                VivePracticeJournalV3Codec.Exact(f,"start_local_seconds","end_local_seconds");double start=T(f,"start_local_seconds"),end=T(f,"end_local_seconds");
                if(HasEye||InitialPending||start!=Opened||end<=start||(Unavailable&&end<=Detected))throw new InvalidDataException("Invalid initial unknown window.");
                InitialPending=true;PendingEnd=end;
            }
            void Recovery(Dictionary<string,string> f)
            {
                VivePracticeJournalV3Codec.Exact(f,"start_local_seconds","end_local_seconds","reason");double start=T(f,"start_local_seconds"),end=T(f,"end_local_seconds");
                if(!HasEye||!Unavailable||RecoveryPending||start!=LastEye||end<=start||end<=Detected||F(f,"reason")!=GapReason)
                    throw new InvalidDataException("Invalid recovery unknown window.");
                RecoveryPending=true;PendingEnd=end;
            }
            void Eye(Dictionary<string,string> f)
            {
                var fields=new List<string>{"eye_observed_local_seconds","native_frame_sequence","native_timestamp_opaque","runtime","calibration",
                    "device_connected","native_result_code","native_runtime_status","no_user","combined_present","combined_mask","direction_reported_valid","raw_x_bits","raw_y_bits","raw_z_bits"};
                bool extended=f.ContainsKey("eye_extension_version");
                if(extended)fields.AddRange(new[]{"eye_extension_version","head_usability","head_observed_local_seconds","head_sequence","head_connected","head_rotation_reported_valid",
                    "head_qx_bits","head_qy_bits","head_qz_bits","head_qw_bits","candidate_checked_local_seconds","candidate_max_age_seconds",
                    "derived_status","derived_frame","derived_x_bits","derived_y_bits","derived_z_bits"});
                VivePracticeJournalV3Codec.Exact(f,fields.ToArray());
                double t=T(f,"eye_observed_local_seconds");int seq=Int(F(f,"native_frame_sequence")),native=Int(F(f,"native_timestamp_opaque"));
                if(t<=Opened||(!HasEye&&(!InitialPending||t!=PendingEnd))||
                    (HasEye&&(t<=LastEye||seq<=LastNativeSequence||native<=LastNativeTime))||
                    (HasEye&&Unavailable&&(!RecoveryPending||t!=PendingEnd)))throw new InvalidDataException("Invalid eye ordering or coverage window.");
                var runtime=EnumValue<ViveEyeRuntimeState>(F(f,"runtime"));var calibration=EnumValue<ViveEyeCalibrationState>(F(f,"calibration"));
                bool connected=VivePracticeJournalV3Codec.Boolean(F(f,"device_connected"));bool noUser=VivePracticeJournalV3Codec.Boolean(F(f,"no_user"));
                if(F(f,"native_result_code")!=null)Int(F(f,"native_result_code"));if(F(f,"native_runtime_status")!=null)Int(F(f,"native_runtime_status"));
                bool present=VivePracticeJournalV3Codec.Boolean(F(f,"combined_present"));
                if(present)
                {
                    ulong mask=VivePracticeJournalV3Codec.Mask(F(f,"combined_mask"));bool reported=VivePracticeJournalV3Codec.Boolean(F(f,"direction_reported_valid"));
                    foreach(string axis in new[]{"raw_x_bits","raw_y_bits","raw_z_bits"})VivePracticeJournalV3Codec.CheckBits(F(f,axis));
                    var raw=new Vec3(FromBits(F(f,"raw_x_bits")),FromBits(F(f,"raw_y_bits")),FromBits(F(f,"raw_z_bits")));Vec3 normalized;
                    if(runtime!=ViveEyeRuntimeState.Working||calibration!=ViveEyeCalibrationState.ReportedReady||!connected||noUser||
                        F(f,"native_result_code")==null||Int(F(f,"native_result_code"))!=0||(mask&2UL)==0||!reported||
                        !ViveProEyeSdkDirection.TryGetUnitDirection(raw,out normalized))throw new InvalidDataException("Eye frame lacks usable reported state.");
                }
                else throw new InvalidDataException("Missing combined ray.");
                if(extended) EyeExtension(f,t);
                HasEye=true;LastEye=t;LastNativeSequence=seq;LastNativeTime=native;Unavailable=false;InitialPending=false;RecoveryPending=false;
            }
            void EyeExtension(Dictionary<string,string> f,double eyeTime)
            {
                if(F(f,"eye_extension_version")!="1")throw new InvalidDataException("Unknown eye extension.");
                string head=F(f,"head_usability"),derived=F(f,"derived_status"),headTime=F(f,"head_observed_local_seconds");
                string[] headDetails={"head_sequence","head_connected","head_rotation_reported_valid","head_qx_bits","head_qy_bits","head_qz_bits","head_qw_bits"};
                string[] vector={"derived_frame","derived_x_bits","derived_y_bits","derived_z_bits"};
                if(head=="NOT_EVALUATED")
                {
                    if(headTime!=null||F(f,"candidate_checked_local_seconds")!=null||F(f,"candidate_max_age_seconds")!=null||derived!="NOT_COMPUTED")throw new InvalidDataException("Unexpected head assessment.");
                    foreach(string name in headDetails)if(F(f,name)!=null)throw new InvalidDataException("Unexpected head data.");
                }
                else
                {
                    if(head!="LOCAL_ASSESSMENT_USABLE"&&head!="LOCAL_ASSESSMENT_UNAVAILABLE")throw new InvalidDataException("Invalid head assessment.");
                    double checkedAt=T(f,"candidate_checked_local_seconds"),age=T(f,"candidate_max_age_seconds");
                    if(checkedAt<eyeTime||(HasCandidateCheck&&checkedAt<LastCandidateChecked)||age<=0||age>1)throw new InvalidDataException("Invalid source assessment time.");
                    HasCandidateCheck=true;LastCandidateChecked=checkedAt;
                    if(headTime==null){foreach(string name in headDetails)if(F(f,name)!=null)throw new InvalidDataException("Partial head data.");if(head=="LOCAL_ASSESSMENT_USABLE")throw new InvalidDataException("Missing usable head.");}
                    else
                    {
                        double ht=VivePracticeJournalV3Codec.Time(headTime);long hs=VivePracticeJournalV3Codec.Integer(F(f,"head_sequence"));
                        bool connected=VivePracticeJournalV3Codec.Boolean(F(f,"head_connected")),valid=VivePracticeJournalV3Codec.Boolean(F(f,"head_rotation_reported_valid"));
                        foreach(string name in new[]{"head_qx_bits","head_qy_bits","head_qz_bits","head_qw_bits"})VivePracticeJournalV3Codec.CheckBits(F(f,name));
                        var q=new Rotation(FromBits(F(f,"head_qx_bits")),FromBits(F(f,"head_qy_bits")),FromBits(F(f,"head_qz_bits")),FromBits(F(f,"head_qw_bits")));
                        if(ht<=Opened||ht>checkedAt||hs<0)throw new InvalidDataException("Invalid head time.");
                        if(head=="LOCAL_ASSESSMENT_USABLE")
                        {
                            if(!connected||!valid||!q.IsUnit||checkedAt-ht>age||checkedAt-eyeTime>age||
                                (HasUsableHead&&(hs<=LastUsableHeadSequence||ht<LastUsableHeadTime)))throw new InvalidDataException("Inconsistent usable head.");
                            HasUsableHead=true;LastUsableHeadSequence=hs;LastUsableHeadTime=ht;
                        }
                    }
                    if(derived!="SOURCE_REPORTED_AVAILABLE"&&derived!="LOCAL_ASSESSMENT_UNAVAILABLE")throw new InvalidDataException("Invalid derived assessment.");
                    if(derived=="SOURCE_REPORTED_AVAILABLE"&&head!="LOCAL_ASSESSMENT_USABLE")throw new InvalidDataException("Derived direction requires usable head.");
                }
                if(derived=="SOURCE_REPORTED_AVAILABLE")
                {
                    if(F(f,"derived_frame")!="MAPPING_VERSION_DEFINED_UNVALIDATED")throw new InvalidDataException("Invalid derived frame.");
                    foreach(string name in new[]{"derived_x_bits","derived_y_bits","derived_z_bits"})VivePracticeJournalV3Codec.CheckBits(F(f,name));
                    if(!new Vec3(FromBits(F(f,"derived_x_bits")),FromBits(F(f,"derived_y_bits")),FromBits(F(f,"derived_z_bits"))).IsUnit)throw new InvalidDataException("Invalid derived direction.");
                }
                else foreach(string name in vector)if(F(f,name)!=null)throw new InvalidDataException("Unexpected derived vector.");
            }
            static double FromBits(string value)
            {return BitConverter.Int64BitsToDouble(unchecked((long)UInt64.Parse(value,NumberStyles.AllowHexSpecifier,CultureInfo.InvariantCulture)));}
            void TailFrame(Dictionary<string,string> f)
            {
                VivePracticeJournalV3Codec.Exact(f,"start_local_seconds","end_local_seconds","reason");double start=T(f,"start_local_seconds"),end=T(f,"end_local_seconds");
                if(Tail||start!=(HasEye?LastEye:Opened)||end<start||(HasTask&&end<LastTask)||(Unavailable&&end<Detected)||(HasCandidateCheck&&end<LastCandidateChecked)||
                    F(f,"reason")!=(!HasEye?"no_observation_yet":Unavailable?"unavailable_at_close":"after_last_observation"))
                    throw new InvalidDataException("Invalid unknown tail.");Tail=true;
            }
        }
        sealed class BoundedInput
        {
            readonly Stream input;readonly long limit;internal long Count;
            internal BoundedInput(Stream input,long limit){this.input=input;this.limit=limit;}
            internal int Read(byte[] bytes,int offset,int length)
            {
                if(length==0)return 0;
                try
                {
                    if(Count>=limit){var probe=new byte[1];if(input.Read(probe,0,1)!=0)throw new IOException("Limit exceeded.");return 0;}
                    int value=input.Read(bytes,offset,(int)Math.Min((long)length,limit-Count));Count=checked(Count+value);return value;
                }
                catch(Exception e) when(!VivePracticeJournalV3Codec.Fatal(e)){throw new IOException("Practice input unavailable.");}
            }
            internal int ReadExact(byte[] bytes)
            {int offset=0;while(offset<bytes.Length){int n=Read(bytes,offset,bytes.Length-offset);if(n==0)break;offset+=n;}return offset;}
        }
        static VivePracticeJournalV3ReadResult Result(ViveDevelopmentJournalReadStatus status,string issue,long verified,List<VivePracticeJournalV3Record> records,bool allow)
        {return new VivePracticeJournalV3ReadResult{Status=status,Issue=issue,VerifiedBytes=verified,
            Records=new ReadOnlyCollection<VivePracticeJournalV3Record>(allow?records:new List<VivePracticeJournalV3Record>())};}
        public static VivePracticeJournalV3ReadResult Read(Stream input,bool explicitReadOnlyChoice,bool explicitIncompleteRecovery,int maximumRecords,long maximumBytes)
        {
            // Never inspect input before the explicit action.
            if(!explicitReadOnlyChoice)throw new InvalidOperationException("Explicit read-only choice required.");
            if(maximumRecords<1||maximumRecords>100000||maximumBytes<8||maximumBytes>67108864)throw new ArgumentOutOfRangeException("Explicit bounded limits required.");
            var records=new List<VivePracticeJournalV3Record>();var state=new FrameState();long verified=0,sequence=0;byte[] token=null,previous=new byte[32];
            try
            {
                if(input==null||!input.CanRead||input.CanWrite)throw new IOException("Read-only stream required.");
                var source=new BoundedInput(input,maximumBytes);var magic=new byte[VivePracticeJournalV3Codec.Magic.Length];
                int n=source.ReadExact(magic);if(n!=magic.Length)return Result(ViveDevelopmentJournalReadStatus.Truncated,"incomplete_magic",verified,records,false);
                if(!ContinuousJournal.Equal(magic,VivePracticeJournalV3Codec.Magic))return Result(ViveDevelopmentJournalReadStatus.Invalid,"unknown_signature",verified,records,false);
                verified=magic.Length;
                while(true)
                {
                    var prefix=new byte[8];n=source.ReadExact(prefix);
                    if(n==0)
                    {
                        if(!state.Header)return Result(ViveDevelopmentJournalReadStatus.Truncated,"missing_header",verified,records,false);
                        var status=state.Closed?ViveDevelopmentJournalReadStatus.CleanClose:ViveDevelopmentJournalReadStatus.UncleanEnd;
                        return Result(status,state.Closed?null:"unclean_end",verified,records,state.Closed||explicitIncompleteRecovery);
                    }
                    if(state.Closed)return Result(ViveDevelopmentJournalReadStatus.Invalid,"bytes_after_close",verified,records,explicitIncompleteRecovery&&state.Header);
                    if(n<8)return Result(ViveDevelopmentJournalReadStatus.Truncated,"incomplete_prefix",verified,records,explicitIncompleteRecovery&&state.Header);
                    int length=ContinuousJournal.Int(prefix);
                    if(ContinuousJournal.Int(prefix,4)!=~length||length<32||length>VivePracticeJournalV3Codec.MaxFrame)
                        return Result(ViveDevelopmentJournalReadStatus.Invalid,"invalid_frame_length",verified,records,explicitIncompleteRecovery&&state.Header);
                    var payload=new byte[length];var digest=new byte[32];
                    if(source.ReadExact(payload)!=length||source.ReadExact(digest)!=digest.Length)
                        return Result(ViveDevelopmentJournalReadStatus.Truncated,"incomplete_frame",verified,records,explicitIncompleteRecovery&&state.Header);
                    if(!ContinuousJournal.Equal(digest,ContinuousJournal.Digest(previous,payload)))
                        return Result(ViveDevelopmentJournalReadStatus.Invalid,"hash_mismatch",verified,records,explicitIncompleteRecovery&&state.Header);
                    // A caller's budget is not evidence of malformed data; never expose
                    // a recovered prefix when this bounded read cannot finish.
                    if(sequence>=maximumRecords)throw new IOException("Record limit exceeded.");
                    long number;byte[] frameToken;string kind;var fields=new Dictionary<string,string>(StringComparer.Ordinal);
                    using(var frame=new MemoryStream(payload,false))using(var reader=new BinaryReader(frame,ContinuousJournal.Utf8,true))
                    {
                        number=reader.ReadInt64();frameToken=reader.ReadBytes(16);kind=ContinuousJournal.ReadString(reader,false);int count=reader.ReadInt32();
                        if(number!=sequence||frameToken.Length!=16||count<0||count>64)throw new InvalidDataException("Invalid frame metadata.");
                        for(int i=0;i<count;i++){string key=ContinuousJournal.ReadString(reader,false);if(String.IsNullOrEmpty(key)||fields.ContainsKey(key))throw new InvalidDataException("Invalid field key.");fields.Add(key,ContinuousJournal.ReadString(reader,true));}
                        if(frame.Position!=frame.Length)throw new InvalidDataException("Trailing frame bytes.");
                    }
                    if(token==null)token=frameToken;else if(!ContinuousJournal.Equal(token,frameToken))throw new InvalidDataException("Mixed identity.");
                    state.Observe(kind,fields);
                    previous=digest;sequence++;verified=source.Count;records.Add(new VivePracticeJournalV3Record(number,kind,fields));
                }
            }
            catch(InvalidDataException){return Result(ViveDevelopmentJournalReadStatus.Invalid,"invalid_frame_or_context",verified,records,explicitIncompleteRecovery&&state.Header);}
            catch(IOException){return Result(ViveDevelopmentJournalReadStatus.ReadFailure,"read_or_limit_failed",verified,records,false);}
            catch(Exception e) when(!VivePracticeJournalV3Codec.Fatal(e))
            {return Result(ViveDevelopmentJournalReadStatus.Invalid,"invalid_frame_or_context",verified,records,explicitIncompleteRecovery&&state.Header);}
        }
    }
}
