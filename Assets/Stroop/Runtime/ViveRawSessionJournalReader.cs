using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using AvatarStroop.Core;

namespace AvatarStroop.Runtime
{
    internal enum ViveRawSessionReadStatus { VerifiedPrefix,RecoveryRequired,Invalid,ReadFailure,LimitExceeded,Cancelled,ProjectionWithheld,SourceChanged }
    internal sealed class ViveRawSessionReadResult
    {
        internal ViveRawSessionReadStatus Status {get;set;}
        internal string DisplayText {get;set;}
        internal bool TrailingFrameTruncated {get;set;}
        // Counts cover only complete verified frames. They do not measure
        // sampling coverage, participant identity, or successful recording.
        internal long VerifiedBytes {get;set;}
        internal long VerifiedTaskRecords {get;set;}
        internal long VerifiedObservationRecords {get;set;}
        internal long VerifiedStatusRecords {get;set;}
        internal long VerifiedRecordCount {get;set;}
        internal string VerifiedPrefixChainDigestHex {get;set;}
        internal long PageStartOrdinal {get;set;}
        internal int PageRecordCount {get;set;}
        internal bool PageHasMoreRecords {get;set;}
        // A matching terminal is only an internal declaration made before
        // close. This is not proof of a completed session or durable storage.
        internal bool CaptureTerminalObserved {get;set;}
        internal ViveRawTrialProjection TrialProjection {get;set;}
        internal bool SourceAuthenticated {get{return false;}}
        internal bool ConsentAuthenticated {get{return false;}}
        internal bool CurrentAccessAuthorized {get{return false;}}
        internal string CurrentWithdrawalStatus {get{return "UNKNOWN";}}
        internal bool CompleteSessionProven {get{return false;}}
        internal bool DurableStorageProven {get{return false;}}
    }
    // VRAW0002 is a proposed development-only format. All sources, including
    // ones declaring SYNTHETIC_ONLY, may contain sensitive data. No terminal
    // marker is defined: even frame-aligned EOF is only a selected prefix.
    internal static class ViveRawSessionJournalReader
    {
        // A consistency marker for one completely framed prefix, not an
        // authorization, identity, consent, or complete-session assertion.
        // Previously returned strings cannot be recalled after withdrawal.
        internal sealed class PagePrefix
        {
            readonly string digest; readonly long bytes,records,tasks,eyes,statuses; readonly bool terminal;
            PagePrefix(ViveRawSessionReadResult source)
            {digest=source.VerifiedPrefixChainDigestHex;bytes=source.VerifiedBytes;records=source.VerifiedRecordCount;
                tasks=source.VerifiedTaskRecords;eyes=source.VerifiedObservationRecords;statuses=source.VerifiedStatusRecords;terminal=source.CaptureTerminalObserved;}
            internal static PagePrefix FromVerified(ViveRawSessionReadResult source)
            {
                if(source==null||source.Status!=ViveRawSessionReadStatus.VerifiedPrefix||source.TrailingFrameTruncated||
                    source.VerifiedBytes<8||source.VerifiedRecordCount<1||String.IsNullOrEmpty(source.VerifiedPrefixChainDigestHex))return null;
                return new PagePrefix(source);
            }
            internal bool Matches(ViveRawSessionReadResult value)
            {return value.Status==ViveRawSessionReadStatus.VerifiedPrefix&&!value.TrailingFrameTruncated&&
                value.VerifiedBytes==bytes&&value.VerifiedRecordCount==records&&value.VerifiedTaskRecords==tasks&&
                value.VerifiedObservationRecords==eyes&&value.VerifiedStatusRecords==statuses&&
                value.CaptureTerminalObserved==terminal&&String.Equals(value.VerifiedPrefixChainDigestHex,digest,StringComparison.Ordinal);}
        }
        const int MaxPayload=1048576, MaxText=2097152, MaxString=32768;
        static readonly byte[] Magic=Encoding.ASCII.GetBytes("VRAW0002");
        static readonly byte[] MagicV3=Encoding.ASCII.GetBytes("VRAW0003");
        static readonly byte[] MagicV4=Encoding.ASCII.GetBytes("VRAW0004");
        static readonly byte[] MagicV5=Encoding.ASCII.GetBytes("VRAW0005");
        static readonly UTF8Encoding Utf8=new UTF8Encoding(false,true);
        sealed class Limit:Exception {}
        sealed class Invalid:Exception {}
        sealed class SourceFailure:Exception {}
        sealed class Context
        {internal bool Manifest,V3,V4,V5,Terminal,TaskEnded,TaskCompleted,EndedPhase,AfterEndedPhase;internal bool WithholdObservedWithdrawal=false;internal long LastTask,Tasks,Eyes;internal long Statuses=0;
            internal string AllocationId;internal int BInitialTarget;internal double TaskChoiceObserved,EyeChoiceObserved;internal bool SoftwareChoicesSeen,LegacyPassedPreparation;
            internal int Blocks;internal string Session,Clock,Plan,Intent;internal double Choice,LatestLocal,LastStatusTime=-1;internal int[] Options,Lists;internal string[] Schedules,Geometries;
            internal ViveRawTrialAccumulator TrialAccumulator;internal readonly ViveRawPreparedPlanAudit PreparedPlans=new ViveRawPreparedPlanAudit();}
        sealed class Bounded
        {
            readonly Stream source;readonly long max;readonly CancellationToken cancellation;internal long Count;
            internal Bounded(Stream stream,long maximum,CancellationToken token){source=stream;max=maximum;cancellation=token;}
            internal int Read(byte[] buffer,int offset,int size)
            {
                cancellation.ThrowIfCancellationRequested();
                if(Count==max)
                {
                    // One read-only byte probes for data beyond the budget.
                    byte[] probe=new byte[1];int extra;
                    try{extra=source.Read(probe,0,1);}
                    catch(OperationCanceledException) when(cancellation.IsCancellationRequested){throw;}
                    catch(Exception e) when(!Fatal(e)){throw new SourceFailure();}
                    cancellation.ThrowIfCancellationRequested();if(extra!=0)throw new Limit();return 0;
                }
                int requested=(int)Math.Min(size,max-Count);int n;
                try{n=source.Read(buffer,offset,requested);}
                catch(OperationCanceledException) when(cancellation.IsCancellationRequested){throw;}
                catch(Exception e) when(!Fatal(e)){throw new SourceFailure();}
                cancellation.ThrowIfCancellationRequested();if(n<0||n>requested)throw new SourceFailure();Count+=n;return n;
            }
            internal int Fill(byte[] buffer)
            {int at=0;while(at<buffer.Length){int n=Read(buffer,at,buffer.Length-at);if(n==0)break;at+=n;}return at;}
        }
        sealed class Rows
        {
            readonly StringBuilder text=new StringBuilder();readonly long ordinal;readonly string kind;
            internal Rows(long ordinal,string kind){this.ordinal=ordinal;this.kind=kind;}
            internal string Text {get{return text.ToString();}}
            void Row(string name,string type,string value)
            {
                string row=ordinal.ToString(CultureInfo.InvariantCulture)+","+kind+","+name+","+type+","+value+"\n";
                if(text.Length>MaxText-row.Length)throw new Limit();text.Append(row);
            }
            internal void String(string name,string value){Row(name,value==null?"null":"utf8_b64",value==null?"":Convert.ToBase64String(Utf8.GetBytes(value)));}
            internal void Int(string name,long value){Row(name,"int",value.ToString(CultureInfo.InvariantCulture));}
            internal void UInt(string name,ulong value){Row(name,"uint",value.ToString(CultureInfo.InvariantCulture));}
            internal void Bool(string name,bool value){Row(name,"bool",value?"1":"0");}
            internal void DoubleBits(string name,long bits){Row(name,"f64_bits_hex",bits.ToString("X16",CultureInfo.InvariantCulture));}
            internal void Bytes(string name,byte[] value){Row(name,"bytes_b64",Convert.ToBase64String(value));}
        }
        static bool Fatal(Exception e){return e is OutOfMemoryException||e is StackOverflowException||e is AccessViolationException;}
        static string Text(BinaryReader reader,Rows rows,string name,bool required=false)
        {
            int n=reader.ReadInt32();if(n < -1||n>MaxString)throw new Invalid();
            if(n==-1){if(required)throw new Invalid();rows.String(name,null);return null;}
            byte[] data=reader.ReadBytes(n);if(data.Length!=n)throw new Invalid();
            string s=Utf8.GetString(data);if(required&&String.IsNullOrWhiteSpace(s))throw new Invalid();rows.String(name,s);return s;
        }
        static long I64(BinaryReader reader,Rows rows,string name){long x=reader.ReadInt64();rows.Int(name,x);return x;}
        static int I32(BinaryReader reader,Rows rows,string name){int x=reader.ReadInt32();rows.Int(name,x);return x;}
        static bool Flag(BinaryReader reader,Rows rows,string name)
        {byte b=reader.ReadByte();if(b>1)throw new Invalid();rows.Bool(name,b==1);return b==1;}
        static double F64(BinaryReader reader,Rows rows,string name,bool time=false)
        {long bits=reader.ReadInt64();double v=BitConverter.Int64BitsToDouble(bits);if(time&&(Double.IsNaN(v)||Double.IsInfinity(v)||v<0))throw new Invalid();rows.DoubleBits(name,bits);return v;}
        static void NullableI32(BinaryReader reader,Rows rows,string name)
        {bool present=Flag(reader,rows,name+"_present");if(present)I32(reader,rows,name);}
        static int? OptionalI32(BinaryReader reader,Rows rows,string name)
        {return Flag(reader,rows,name+"_present")?(int?)I32(reader,rows,name):null;}
        static bool? OptionalBool(BinaryReader reader,Rows rows,string name)
        {return Flag(reader,rows,name+"_present")?(bool?)Flag(reader,rows,name):null;}
        static ulong? OptionalUlong(BinaryReader reader,Rows rows,string name)
        {if(!Flag(reader,rows,name+"_present"))return null;ulong value=reader.ReadUInt64();rows.UInt(name,value);return value;}
        static void Eye(BinaryReader reader,Rows rows,string name)
        {
            if(!Flag(reader,rows,name+"_present"))return;
            rows.UInt(name+"_mask",reader.ReadUInt64());Flag(reader,rows,name+"_origin_valid");Flag(reader,rows,name+"_direction_valid");
            foreach(string axis in new[]{"origin_x","origin_y","origin_z","direction_x","direction_y","direction_z"})F64(reader,rows,name+"_"+axis);
        }
        static void Field(Dictionary<string,string> fields,string name,string expected)
        {string actual;if(!fields.TryGetValue(name,out actual)||actual!=expected)throw new Invalid();}
        static void ReadLegacyManifest(BinaryReader r,Rows rows,Context c)
        {
            if(Text(r,rows,"design_variant",true)!="LEGACY_ABCD"||Text(r,rows,"legacy_design_schema",true)!=ViveLegacyRecordingDesign.SchemaVersion)throw new Invalid();
            c.Plan=Text(r,rows,"legacy_design_id",true);
            if(Text(r,rows,"legacy_intent_schema",true)!=ViveLegacyRecordingIntent.EventSchemaVersion)throw new Invalid();c.Intent=Text(r,rows,"legacy_intent_id",true);
            if(!Flag(r,rows,"task_requested")||!Flag(r,rows,"eye_requested")||!Flag(r,rows,"pre_choice_prefix_unknown"))throw new Invalid();
            c.Choice=F64(r,rows,"owner_admission_local_observed",true);c.LatestLocal=c.Choice;
            c.LastTask=I64(r,rows,"task_baseline");if(c.LastTask< -1)throw new Invalid();
            byte[] run=r.ReadBytes(16);if(run.Length!=16||new Guid(run)==Guid.Empty)throw new Invalid();rows.Bytes("run_software_id",run);
            double task=F64(r,rows,"task_choice_poll_seconds"),eye=F64(r,rows,"eye_choice_poll_seconds");
            if(Double.IsNaN(task)||Double.IsInfinity(task)||Double.IsNaN(eye)||Double.IsInfinity(eye)||task<0||eye<0||eye<task||eye>c.Choice)throw new Invalid();
            c.TaskChoiceObserved=task;c.EyeChoiceObserved=eye;
            rows.String("choice_time_basis","controller_poll_not_hardware_event");
            uint seed=r.ReadUInt32();rows.UInt("seed",seed);int runKind=I32(r,rows,"configuration_run_kind");if(runKind!=(int)RunKind.Simulation)throw new Invalid();
            if(Text(r,rows,"questionnaire_language",true)!="en"||F64(r,rows,"countdown_seconds",true)<=0)throw new Invalid();
            F64(r,rows,"minimum_rest_seconds",true);Flag(r,rows,"skip_final_rest");F64(r,rows,"questionnaire_instruction_seconds",true);
            Flag(r,rows,"questionnaire_instruction_requires_acknowledgement");Flag(r,rows,"participant_paced_rest");Flag(r,rows,"retain_control_across_blocks");
            Text(r,rows,"protocol_version",true);Text(r,rows,"software_version",true);string geometry=Text(r,rows,"legacy_geometry_version",true),mapping=Text(r,rows,"legacy_mapping_version",true);
            c.AllocationId=Text(r,rows,"legacy_allocation_id",true);c.BInitialTarget=I32(r,rows,"legacy_b_initial_target");if(c.BInitialTarget!=(int)GazeTarget.Participant)throw new Invalid();
            c.Blocks=I32(r,rows,"block_count");if(c.Blocks!=4)throw new Invalid();c.Options=new int[4];c.Lists=new int[4];c.Schedules=new string[4];c.Geometries=new string[4];
            var order=new[]{Condition.B,Condition.C,Condition.A,Condition.D};
            for(int i=0;i<4;i++)
            {string p="block_"+i.ToString(CultureInfo.InvariantCulture)+"_";if(I32(r,rows,p+"index")!=i||I32(r,rows,p+"condition")!=(int)order[i]||I32(r,rows,p+"list_index")!=i)throw new Invalid();
                c.Options[i]=(int)order[i];c.Lists[i]=i;c.Schedules[i]=mapping;c.Geometries[i]=geometry;}
            if(c.V5&&(Text(r,rows,"legacy_control_mode",true)!="OPERATOR_WOZ_PARTICIPANT_LOCAL_RECORDING"||
                Text(r,rows,"network_clock_relation",true)!="INDEPENDENT_CLOCKS_NOT_SYNCHRONIZED"||
                Text(r,rows,"network_authentication_scope",true)!="REMOTE_KEY_POSSESSION_NOT_HARDWARE_IDENTITY"))throw new Invalid();
            if(c.TrialAccumulator!=null)c.TrialAccumulator.SetLegacyManifest(c.Session,c.Clock,c.Plan,c.Options,c.Lists,c.Schedules,c.Geometries,c.V5);
        }
        static void ValidateLegacyFields(Dictionary<string,string> f,Context c,int block)
        {
            if(c.V5)Field(f,"legacy_control_mode","OPERATOR_WOZ_PARTICIPANT_LOCAL_RECORDING");
            else if(f.ContainsKey("legacy_control_mode"))throw new Invalid();
            Field(f,"legacy_schema",ViveLegacyRecordingIntent.EventSchemaVersion);Field(f,"legacy_design_schema",ViveLegacyRecordingDesign.SchemaVersion);
            Field(f,"legacy_design_id",c.Plan);Field(f,"legacy_recording_intent_id",c.Intent);Field(f,"legacy_task_recording_intent","REQUESTED");Field(f,"legacy_eye_recording_intent","REQUESTED");
            Field(f,"legacy_eye_sample_payload","NOT_INCLUDED_IN_TASK_EVENT");Field(f,"legacy_allocation_id",c.AllocationId);Field(f,"legacy_b_initial_target",((GazeTarget)c.BInitialTarget).ToString());
            string phase;if(!f.TryGetValue("legacy_phase",out phase)||!Enum.IsDefined(typeof(Phase),phase))throw new Invalid();
            if(block>=0){Field(f,"legacy_condition",((Condition)c.Options[block]).ToString());Field(f,"legacy_list_index",c.Lists[block].ToString(CultureInfo.InvariantCulture));}
            if(block>=0)
            {
                if(f.ContainsKey("condition"))Field(f,"condition",((Condition)c.Options[block]).ToString());
                if(f.ContainsKey("list_index"))Field(f,"list_index",c.Lists[block].ToString(CultureInfo.InvariantCulture));
                if(f.ContainsKey("b_initial_target"))Field(f,"b_initial_target",((GazeTarget)c.BInitialTarget).ToString());
            }
            else if(f.ContainsKey("legacy_condition")||f.ContainsKey("legacy_list_index")||f.ContainsKey("legacy_block_started"))throw new Invalid();
        }
        static string Decode(byte[] payload,long ordinal,Context context)
        {
            using(var memory=new MemoryStream(payload,false))using(var r=new BinaryReader(memory,Utf8))
            {
                if(r.ReadInt64()!=ordinal)throw new Invalid();int kind=r.ReadByte();
                if((ordinal==0&&kind!=0)||(ordinal!=0&&kind==0)||kind>4||(!context.V3&&kind>2)||context.Terminal)throw new Invalid();
                var rows=new Rows(ordinal,kind==0?"manifest":kind==1?"task":kind==2?"observation":kind==3?"eye_status":"capture_terminal");
                if(kind==0)
                {
                    if(Text(r,rows,"schema",true)!=(context.V5?"VRAW0005":context.V4?"VRAW0004":context.V3?"VRAW0003":"VRAW0002"))throw new Invalid();
                    string source=Text(r,rows,"source",true);if(source!="SYNTHETIC_ONLY"&&source!="INSTALLED_SDK_CODE_PATH_ONLY")throw new Invalid();
                    if(Text(r,rows,"run_kind",true)!="SIMULATION_DEVELOPMENT_NOT_RESEARCH")throw new Invalid();
                    if(Text(r,rows,"native_time",true)!="NATIVE_TIME_OPAQUE"||Text(r,rows,"eye_coverage",true)!="EYE_COVERAGE_UNKNOWN"||
                       Text(r,rows,"ingest_context",true)!="INGEST_CONTEXT_NOT_SAMPLE_ALIGNMENT")throw new Invalid();
                    if(Text(r,rows,"sdk_version",true)!="SRANIPAL_SDK_VERSION_UNKNOWN"||Text(r,rows,"eye_space",true)!="RAW_EYE_SPACE_UNVERIFIED"||
                       Text(r,rows,"mapping",true)!="MAPPING_NOT_APPLIED;REHEARSAL=vive-sranipal-ray-normalized-x-reflection-tracking-to-avatar-rehearsal-v3-unvalidated"||
                       Text(r,rows,"eye_origin_unit",true)!="EYE_ORIGIN_MILLIMETERS"||Text(r,rows,"head_space",true)!="HEAD_TRACKING_LOCAL_ROTATION"||
                       Text(r,rows,"device_identity",true)!="DEVICE_IDENTITY_UNKNOWN"||Text(r,rows,"participant_identity",true)!="PARTICIPANT_IDENTITY_NOT_AUTHENTICATED")throw new Invalid();
                    context.Session=Text(r,rows,"session_id",true);context.Clock=Text(r,rows,"clock_id",true);
                    if(context.V4)ReadLegacyManifest(r,rows,context);
                    else
                    {
                    if(Text(r,rows,"plan_schema",true)!="vive-named-simulation-plan-v2")throw new Invalid();
                    context.Plan=Text(r,rows,"plan_id",true);if(Text(r,rows,"intent_schema",true)!="vive-named-recording-intent-v3")throw new Invalid();context.Intent=Text(r,rows,"intent_id",true);
                    if(!Flag(r,rows,"task_requested")||!Flag(r,rows,"eye_requested")||!Flag(r,rows,"pre_choice_prefix_unknown"))throw new Invalid();context.Choice=F64(r,rows,"second_choice_local_observed",true);context.LatestLocal=context.Choice;
                    // Keep the historical key for existing consumers, while
                    // explicitly correcting its misleading interpretation.
                    rows.String("second_choice_local_observed_semantics","recorder_admission_enabled_at_owner_start_not_participant_input");
                    context.LastTask=I64(r,rows,"task_baseline");if(context.LastTask < -1)throw new Invalid();
                    byte[] run=r.ReadBytes(16);if(run.Length!=16)throw new Invalid();bool any=false;foreach(byte b in run)any|=b!=0;if(!any)throw new Invalid();rows.Bytes("run_software_id",run);
                    context.Blocks=I32(r,rows,"block_count");if(context.Blocks<1||context.Blocks>4)throw new Invalid();
                    context.Options=new int[context.Blocks];context.Lists=new int[context.Blocks];context.Schedules=new string[context.Blocks];context.Geometries=new string[context.Blocks];
                    for(int i=0;i<context.Blocks;i++)
                    {
                        string p="block_"+i.ToString(CultureInfo.InvariantCulture)+"_";
                        if(I32(r,rows,p+"index")!=i)throw new Invalid();int option=I32(r,rows,p+"option");if(option!=0&&option!=2)throw new Invalid();context.Options[i]=option;
                        int list=I32(r,rows,p+"list_index");if(list<0||list>3)throw new Invalid();context.Lists[i]=list;context.Schedules[i]=Text(r,rows,p+"schedule_version",true);context.Geometries[i]=Text(r,rows,p+"geometry_version",true);
                        if(option==0&&context.Schedules[i]!=ViveNamedOptionProvenance.AutomaticScheduleVersion)throw new Invalid();
                        if(option==2&&(context.Schedules[i]!=ViveNamedOptionProvenance.NoAvatarNotApplicable||context.Geometries[i]!=ViveNamedOptionProvenance.NoAvatarNotApplicable))throw new Invalid();
                    }
                    if(context.TrialAccumulator!=null)context.TrialAccumulator.SetManifest(context.Session,context.Clock,context.Plan,
                        context.Options,context.Lists,context.Schedules,context.Geometries);
                    }
                    context.Manifest=true;
                }
                else if(kind==1)
                {
                    long seq=I64(r,rows,"source_sequence");if(seq<0||context.LastTask==Int64.MaxValue||seq!=context.LastTask+1)throw new Invalid();context.LastTask=seq;
                    double eventTime=F64(r,rows,"event_local_time",true);context.LatestLocal=Math.Max(context.LatestLocal,eventTime);if(Text(r,rows,"clock_id",true)!=context.Clock||Text(r,rows,"session_id",true)!=context.Session)throw new Invalid();int block=I32(r,rows,"block");int segment=I32(r,rows,"segment");
                    string trialId=Text(r,rows,"trial_id");string eventKind=Text(r,rows,"event_kind",true);int named=I32(r,rows,context.V4?"legacy_block_index":"named_block_index");if(named < -1||named>=context.Blocks||block!=named)throw new Invalid();
                    int count=I32(r,rows,"field_count");if(count<0||count>128)throw new Invalid();string previous=null;var fields=new Dictionary<string,string>(StringComparer.Ordinal);
                    for(int i=0;i<count;i++)
                    {string p="field_"+i.ToString(CultureInfo.InvariantCulture)+"_";string key=Text(r,rows,p+"key",true);if(previous!=null&&StringComparer.Ordinal.Compare(previous,key)>=0)throw new Invalid();previous=key;fields.Add(key,Text(r,rows,p+"value"));}
                    try{ViveRawSessionJournalCodec.ValidatePresentationDeclaration(eventKind,fields,context.Clock,eventTime);
                        ViveRawSessionJournalCodec.ValidateSoftwareChoices(eventKind,fields,context.Clock,eventTime);
                        ViveLegacyWozRecordingSchema.Validate(eventKind,fields,context.Clock,eventTime,context.V5);}
                    catch(InvalidOperationException){throw new Invalid();}
                    foreach(string key in fields.Keys)if(key.StartsWith(context.V4?"named_":"legacy_",StringComparison.OrdinalIgnoreCase))throw new Invalid();
                    if(context.V4)
                    {
                        ValidateLegacyFields(fields,context,block);
                        if(eventKind=="participant_recording_software_choices")
                        {
                            double taskChoice,eyeChoice;
                            if(context.SoftwareChoicesSeen||context.LegacyPassedPreparation||block!=-1||trialId!=null||fields["legacy_phase"]!="Preparation"||!Double.TryParse(fields["task_choice_poll_seconds"],NumberStyles.Float,CultureInfo.InvariantCulture,out taskChoice)||
                                !Double.TryParse(fields["eye_choice_poll_seconds"],NumberStyles.Float,CultureInfo.InvariantCulture,out eyeChoice)||
                                BitConverter.DoubleToInt64Bits(taskChoice)!=BitConverter.DoubleToInt64Bits(context.TaskChoiceObserved)||
                                BitConverter.DoubleToInt64Bits(eyeChoice)!=BitConverter.DoubleToInt64Bits(context.EyeChoiceObserved))throw new Invalid();
                            context.SoftwareChoicesSeen=true;
                        }
                        if(fields["legacy_phase"]!="Preparation")context.LegacyPassedPreparation=true;
                    }
                    else
                    {
                    Field(fields,"named_schema","vive-named-recording-intent-v3");Field(fields,"named_plan_id",context.Plan);Field(fields,"named_plan_schema","vive-named-simulation-plan-v2");
                    Field(fields,"named_recording_intent_id",context.Intent);Field(fields,"named_eye_sample_payload","NOT_INCLUDED_IN_TASK_EVENT");Field(fields,"named_task_recording_intent","REQUESTED");Field(fields,"named_eye_recording_intent","REQUESTED");
                    if(named>=0){Field(fields,"named_block_index",named.ToString(CultureInfo.InvariantCulture));Field(fields,"named_option",context.Options[named]==0?"Automatic":"NoAvatar");Field(fields,"named_list_index",context.Lists[named].ToString(CultureInfo.InvariantCulture));Field(fields,"named_schedule",context.Schedules[named]);Field(fields,"named_geometry",context.Geometries[named]);}
                    }
                    if(eventKind=="block_plan_prepared")
                    {
                        var prepared=context.PreparedPlans.Add(fields,trialId,block,segment,seq,eventTime,context.V4);
                        if(context.TrialAccumulator!=null)context.TrialAccumulator.ObservePreparedPlan(prepared);
                    }
                    else if(eventKind=="trial_planned")context.PreparedPlans.ObserveTrial(fields,block,context.Session,trialId);
                    if(context.TrialAccumulator!=null||context.WithholdObservedWithdrawal)
                    {
                        string reason;
                        if(eventKind=="session_ended"&&(!fields.TryGetValue("reason",out reason)||
                            (reason!="Completed"&&reason!="TechnicalEnd"&&reason!="PracticeFailed")))
                            throw new ViveRawTrialAccumulator.ProjectionWithheld();
                        if(context.TrialAccumulator!=null)context.TrialAccumulator.Observe(eventKind,trialId,block,segment,seq,eventTime,fields);
                    }
                    if(context.V3)
                    {
                        string value;
                        if(context.TaskEnded)
                        {
                            if(!context.EndedPhase&&eventKind=="phase_changed"&&fields.TryGetValue("to",out value)&&value=="Ended")context.EndedPhase=true;
                            else context.AfterEndedPhase=true;
                        }
                        if(eventKind=="session_ended")
                        {if(context.TaskEnded)context.AfterEndedPhase=true;context.TaskEnded=true;context.TaskCompleted=fields.TryGetValue("reason",out value)&&value=="Completed";}
                    }
                    context.Tasks++;
                }
                else if(kind==2)
                {
                    // A frame staged after the task's session_ended event cannot
                    // belong to the sealed task capture. Status records are
                    // handled separately: status may be observed or drained
                    // later, and its timestamp is not capture time.
                    if(context.V3&&context.TaskEnded)throw new Invalid();
                    I32(r,rows,"native_frame");I32(r,rows,"native_timestamp");double eye=F64(r,rows,"eye_observed",true);I64(r,rows,"head_sequence");double head=F64(r,rows,"head_observed",true);double check=F64(r,rows,"checked_at",true);if(eye<=context.Choice||head<=context.Choice||eye>check||head>check)throw new Invalid();I64(r,rows,"loss_generation");
                    context.LatestLocal=Math.Max(context.LatestLocal,check);
                    int runtime=I32(r,rows,"runtime");if(runtime<0||runtime>3)throw new Invalid();int cal=I32(r,rows,"calibration");if(cal<0||cal>2)throw new Invalid();Flag(r,rows,"eye_connected");
                    NullableI32(r,rows,"native_result");NullableI32(r,rows,"runtime_status");Flag(r,rows,"no_user");Eye(r,rows,"left");Eye(r,rows,"right");Eye(r,rows,"combined");
                    Flag(r,rows,"head_connected");Flag(r,rows,"head_valid");foreach(string axis in new[]{"x","y","z","w"})F64(r,rows,"head_rotation_"+axis);
                    int phase=I32(r,rows,"ingest_phase");if(phase<0||phase>13)throw new Invalid();I32(r,rows,"ingest_block");I32(r,rows,"ingest_segment");Text(r,rows,"ingest_trial_id");if(I64(r,rows,"task_cursor")!=context.LastTask)throw new Invalid();
                    context.Eyes++;
                }
                else if(kind==3)
                {
                    long sequence=I64(r,rows,"sequence");double time=F64(r,rows,"poll_return_local_observed",true);
                    ulong causes=r.ReadUInt64();rows.UInt("cause_flags",causes);
                    int? runtime=OptionalI32(r,rows,"runtime"),calibration=OptionalI32(r,rows,"calibration");
                    bool? connected=OptionalBool(r,rows,"eye_connected");int? native=OptionalI32(r,rows,"native_result"),status=OptionalI32(r,rows,"runtime_status");
                    bool? noUser=OptionalBool(r,rows,"no_user");ulong? mask=OptionalUlong(r,rows,"combined_mask");bool? direction=OptionalBool(r,rows,"combined_direction_valid");
                    var value=new ViveProEyeWorker.ObservedEyeStatus(sequence,time,(ViveProEyeWorker.EyeStatusCause)causes,
                        runtime.HasValue?(ViveEyeRuntimeState?)runtime.Value:null,calibration.HasValue?(ViveEyeCalibrationState?)calibration.Value:null,
                        connected,native,status,noUser,mask,direction);
                    if(sequence!=context.Statuses||time<context.Choice||time<context.LastStatusTime||!ViveRawSessionJournalCodec.ValidStatus(value))throw new Invalid();
                    if(context.TrialAccumulator!=null)context.TrialAccumulator.ObserveStatus(value);
                    context.Statuses++;context.LastStatusTime=time;context.LatestLocal=Math.Max(context.LatestLocal,time);
                }
                else
                {
                    if(Text(r,rows,"declaration",true)!="NORMAL_TASK_COMPLETION_DRAIN_DECLARED"||
                        I64(r,rows,"task_cursor")!=context.LastTask||I64(r,rows,"task_records")!=context.Tasks||
                        I64(r,rows,"eye_records")!=context.Eyes||I64(r,rows,"status_records")!=context.Statuses||
                        I64(r,rows,"sealed_watermark")!=ordinal-1||ordinal-1!=checked(context.Tasks+context.Eyes+context.Statuses)||
                        F64(r,rows,"local_observed",true)<context.LatestLocal||(context.V4&&!context.SoftwareChoicesSeen)||!context.TaskEnded||!context.TaskCompleted||!context.EndedPhase||context.AfterEndedPhase)throw new Invalid();
                    context.Terminal=true;
                }
                if(memory.Position!=memory.Length)throw new Invalid();return rows.Text;
            }
        }
        internal static ViveRawSessionReadResult Read(Stream input,bool explicitReadOnlyAction,bool explicitPrefixRecovery,long maximumBytes,int maximumRecords)
        {
            // Preserve the existing action/recovery gate before inspecting
            // budgets, so a declined read never touches the input stream.
            if(!explicitReadOnlyAction)throw new InvalidOperationException("Explicit read-only action required.");
            if(!explicitPrefixRecovery)return new ViveRawSessionReadResult{Status=ViveRawSessionReadStatus.RecoveryRequired,DisplayText=null};
            if(maximumBytes<8||maximumBytes>67108864||maximumRecords<1||maximumRecords>1000000)throw new ArgumentOutOfRangeException("Explicit bounded limits required.");
            return ReadCore(input,explicitReadOnlyAction,explicitPrefixRecovery,maximumBytes,maximumRecords,true,false,0,Int32.MaxValue,false,CancellationToken.None);
        }
        // This is a bounded read-only verification path, not an export or a
        // publication. It never retains rows across records. Cancellation is
        // cooperative between reads; a blocked OS read may not return promptly.
        // Cancellation can race the final return. Callers must run it off the
        // UI thread, own the input lifecycle and gate result publication with
        // their own generation/close state; do not start overlapping workers.
        internal static ViveRawSessionReadResult Scan(Stream input,bool explicitReadOnlyAction,bool explicitPrefixRecovery,
            long maximumBytes,int maximumRecords,CancellationToken cancellation)
        {
            if(maximumBytes<8||maximumBytes>ViveRawByteBudget.MaximumSupportedBytes||maximumRecords<1||maximumRecords>25000000)
                throw new ArgumentOutOfRangeException("Explicit bounded limits required.");
            return ReadCore(input,explicitReadOnlyAction,explicitPrefixRecovery,maximumBytes,maximumRecords,false,false,0,0,false,cancellation);
        }
        // Streams individual fully decoded, chain-checked frames into a
        // caller-owned PRIVATE staging sink. A callback is not a session
        // completion, consent, source-identity or publication receipt; an
        // incomplete/invalid scan may already have emitted a prefix. The
        // caller must keep it private and handle withdrawal independently.
        internal static ViveRawSessionReadResult ScanAnalysisRows(Stream input,long maximumBytes,int maximumRecords,
            CancellationToken cancellation,Action<string> acceptVerifiedFrame,bool syntheticFixture)
        {
#if VIVE_RAW_SYNTHETIC_TEST && !UNITY_5_3_OR_NEWER
            if(!syntheticFixture&&!ViveRawReadableAnalysisCsv.ProductionGateOpen)
#else
            if(!ViveRawReadableAnalysisCsv.ProductionGateOpen)
#endif
                throw new InvalidOperationException("Analysis streaming is not available.");
            if(acceptVerifiedFrame==null)throw new ArgumentNullException("acceptVerifiedFrame");
            if(maximumBytes<8||maximumBytes>ViveRawByteBudget.MaximumSupportedBytes||maximumRecords<1||maximumRecords>25000000)
                throw new ArgumentOutOfRangeException("Explicit bounded limits required.");
            return ReadCore(input,true,true,maximumBytes,maximumRecords,false,false,0,0,true,cancellation,null,acceptVerifiedFrame);
        }
        // Optional bounded, asynchronous private staging is not publication.
        // The caller must independently compare the scan against the writer's
        // seal and call FinishScan, or Abandon it, without waiting for staging.
        internal static ViveRawSessionReadResult ScanWithPrivateStage(Stream input,long maximumBytes,int maximumRecords,
            CancellationToken cancellation,ViveRawPrivateStage privateStage)
        {
            if(privateStage==null)throw new ArgumentNullException("privateStage");
            if(maximumBytes<8||maximumBytes>ViveRawByteBudget.MaximumSupportedBytes||maximumRecords<1||maximumRecords>25000000)
                throw new ArgumentOutOfRangeException("Explicit bounded limits required.");
            try
            {
                var result=ReadCore(input,true,true,maximumBytes,maximumRecords,false,false,0,0,false,cancellation,privateStage);
                if(result.Status!=ViveRawSessionReadStatus.VerifiedPrefix||result.TrailingFrameTruncated||!result.CaptureTerminalObserved)
                    privateStage.Abandon();
                return result;
            }
            catch {privateStage.Abandon();throw;}
        }
        // No output stream, file publication or callback is involved. Results
        // retain sensitive event text in bounded memory and must be released by
        // the caller; managed memory erasure cannot be guaranteed. The caller
        // owns lifecycle, off-UI-thread scheduling and stale-result fencing.
        internal static ViveRawSessionReadResult ProjectTrials(Stream input,bool explicitReadOnlyAction,bool explicitPrefixRecovery,
            long maximumBytes,int maximumRecords,CancellationToken cancellation)
        {
            if(maximumBytes<8||maximumBytes>ViveRawByteBudget.MaximumSupportedBytes||maximumRecords<1||maximumRecords>25000000)
                throw new ArgumentOutOfRangeException("Explicit bounded limits required.");
            return ReadCore(input,explicitReadOnlyAction,explicitPrefixRecovery,maximumBytes,maximumRecords,false,true,0,0,false,cancellation);
        }
        // Input must be positioned at its first byte for each request, which
        // scans the entire selected prefix before returning a bounded page.
        // Repeated pages re-read the source and may be slow; compare
        // verified digest, byte watermark and count, which do not authenticate
        // source or prove full coverage. No output stream or file is written.
        // Never call on the UI thread: blocking I/O may ignore cancellation.
        // Product access needs separate current authorization/withdrawal and
        // stale-result fences; observed withdrawal below is only one check.
        // Page and temporary per-frame strings may contain sensitive fields;
        // releasing managed references does not guarantee physical erasure.
        internal static ViveRawSessionReadResult ReadRawPage(Stream input,bool explicitReadOnlyAction,bool explicitPrefixRecovery,
            long startOrdinal,int pageRecords,long maximumBytes,int maximumRecords,CancellationToken cancellation)
        {
            if(startOrdinal<0||pageRecords<1||pageRecords>256||maximumBytes<8||maximumBytes>ViveRawByteBudget.MaximumSupportedBytes||
                maximumRecords<1||maximumRecords>25000000)throw new ArgumentOutOfRangeException("Explicit bounded page and scan limits required.");
            return ReadCore(input,explicitReadOnlyAction,explicitPrefixRecovery,maximumBytes,maximumRecords,true,false,startOrdinal,pageRecords,true,cancellation);
        }
        // Each page is withheld unless its entire re-scanned, fully framed
        // prefix matches the marker. This does not prevent a source change
        // after return or provide current withdrawal/authorization fencing.
        // Never publish pages to a user or output on this check alone.
        internal static ViveRawSessionReadResult ReadRawPageBoundToPrefix(Stream input,bool explicitReadOnlyAction,bool explicitPrefixRecovery,
            PagePrefix expected,long startOrdinal,int pageRecords,long maximumBytes,int maximumRecords,CancellationToken cancellation)
        {
            if(expected==null)throw new ArgumentNullException("expected");
            var result=ReadRawPage(input,explicitReadOnlyAction,explicitPrefixRecovery,startOrdinal,pageRecords,maximumBytes,maximumRecords,cancellation);
            if(result.Status!=ViveRawSessionReadStatus.VerifiedPrefix)return result;
            if(!expected.Matches(result))return new ViveRawSessionReadResult{Status=ViveRawSessionReadStatus.SourceChanged};
            return result;
        }
        static ViveRawSessionReadResult ReadCore(Stream input,bool explicitReadOnlyAction,bool explicitPrefixRecovery,long maximumBytes,int maximumRecords,
            bool includeRows,bool projectTrials,long pageStart,int pageSize,bool withholdObservedWithdrawal,CancellationToken cancellation,
            ViveRawPrivateStage privateStage=null,Action<string> acceptVerifiedFrame=null)
        {
            if(!explicitReadOnlyAction)throw new InvalidOperationException("Explicit read-only action required.");
            var result=new ViveRawSessionReadResult{Status=ViveRawSessionReadStatus.RecoveryRequired,DisplayText=null};
            if(!explicitPrefixRecovery)return result;
            try
            {
                cancellation.ThrowIfCancellationRequested();
                if(input==null||!input.CanRead||input.CanWrite){result.Status=ViveRawSessionReadStatus.ReadFailure;return result;}
                var source=new Bounded(input,maximumBytes,cancellation);byte[] magic=new byte[8];if(source.Fill(magic)!=8)throw new Invalid();
                bool version5=ContinuousJournal.Equal(magic,MagicV5);bool version4=version5||ContinuousJournal.Equal(magic,MagicV4);bool version3=version4||ContinuousJournal.Equal(magic,MagicV3);if(!version3&&!ContinuousJournal.Equal(magic,Magic))throw new Invalid();
                if(privateStage!=null)privateStage.OfferMagic(magic);
                byte[] prior=new byte[32];bool truncated=false;long ordinal=0;var context=new Context{V3=version3,V4=version4,V5=version5,TrialAccumulator=projectTrials?new ViveRawTrialAccumulator():null,WithholdObservedWithdrawal=withholdObservedWithdrawal};
                long verifiedBytes=0,taskCount=0,observationCount=0;int pageCount=0;
                var text=includeRows?new StringBuilder("ordinal,record_kind,field,type,value\n"):null;
                while(true)
                {
                    byte[] prefix=new byte[8];int got=source.Fill(prefix);if(got==0)break;if(context.Terminal)throw new Invalid();if(got<8){truncated=true;break;}
                    if(ordinal>=maximumRecords)throw new Limit();int length=ContinuousJournal.Int(prefix);
                    if(ContinuousJournal.Int(prefix,4)!=~length||length<9||length>MaxPayload)throw new Invalid();
                    byte[] payload=new byte[length],digest=new byte[32];if(source.Fill(payload)!=length||source.Fill(digest)!=32){truncated=true;break;}
                    if(!ContinuousJournal.Equal(digest,ContinuousJournal.Digest(prior,payload)))throw new Invalid();
                    string addition=Decode(payload,ordinal,context);
                    if(acceptVerifiedFrame!=null){cancellation.ThrowIfCancellationRequested();acceptVerifiedFrame(addition);}
                    if(includeRows&&ordinal>=pageStart&&ordinal-pageStart<pageSize)
                    {if(text.Length>MaxText-addition.Length)throw new Limit();text.Append(addition);pageCount++;}
                    if(payload[8]==1)taskCount++;else if(payload[8]==2)observationCount++;
                    verifiedBytes=source.Count;prior=digest;ordinal++;
                    if(privateStage!=null)privateStage.OfferFrame(prefix,payload,digest);
                }
                cancellation.ThrowIfCancellationRequested();
                if(!context.Manifest)throw new Invalid();result.Status=ViveRawSessionReadStatus.VerifiedPrefix;result.TrailingFrameTruncated=truncated;
                result.VerifiedBytes=verifiedBytes;result.VerifiedTaskRecords=taskCount;result.VerifiedObservationRecords=observationCount;
                result.VerifiedStatusRecords=context.Statuses;result.CaptureTerminalObserved=context.Terminal;
                result.VerifiedRecordCount=ordinal;result.VerifiedPrefixChainDigestHex=BitConverter.ToString(prior).Replace("-",String.Empty).ToLowerInvariant();
                if(includeRows){result.PageStartOrdinal=pageStart;result.PageRecordCount=pageCount;result.PageHasMoreRecords=ordinal>pageStart&&ordinal-pageStart>pageCount;}
                if(projectTrials)result.TrialProjection=context.TrialAccumulator.Finish();
                result.DisplayText=includeRows?text.ToString():null;return result;
            }
            catch(OperationCanceledException){result.Status=ViveRawSessionReadStatus.Cancelled;}
            catch(Limit){result.Status=ViveRawSessionReadStatus.LimitExceeded;}
            catch(ViveRawTrialAccumulator.ProjectionLimit){result.Status=ViveRawSessionReadStatus.LimitExceeded;}
            catch(ViveRawTrialAccumulator.ProjectionWithheld){result.Status=ViveRawSessionReadStatus.ProjectionWithheld;}
            catch(ViveRawPreparedPlanAudit.InvalidPlan){result.Status=ViveRawSessionReadStatus.Invalid;}
            catch(SourceFailure){result.Status=ViveRawSessionReadStatus.ReadFailure;}
            catch(Exception e) when(e is Invalid||e is EndOfStreamException||e is DecoderFallbackException||e is OverflowException||e is ArgumentException){result.Status=ViveRawSessionReadStatus.Invalid;}
            catch(Exception e) when(!Fatal(e)){result.Status=ViveRawSessionReadStatus.ReadFailure;}
            result.DisplayText=null;return result;
        }
    }
}
