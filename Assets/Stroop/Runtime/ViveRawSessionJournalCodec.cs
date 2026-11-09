using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using AvatarStroop.Core;

namespace AvatarStroop.Runtime
{
    // VRAW0002 candidate: schema-only encoding; not a writer, consent,
    // authentication, eye-coverage proof or research admission.
    internal static class ViveRawSessionJournalCodec
    {
        static readonly UTF8Encoding Utf8=new UTF8Encoding(false,true);
        internal static byte[] ManifestPayload(ViveNamedSessionFrameAdmission.Manifest manifest)
        {return ManifestPayloadFor(manifest,false);}
        internal static byte[] ManifestPayloadV3(ViveNamedSessionFrameAdmission.Manifest manifest)
        {return ManifestPayloadFor(manifest,true);}
        internal static byte[] ManifestPayloadV4(ViveNamedSessionFrameAdmission.Manifest manifest)
        {return ManifestPayloadFor(manifest,true,true);}
        static byte[] ManifestPayloadFor(ViveNamedSessionFrameAdmission.Manifest manifest,bool version3,bool legacy=false)
        {
            if(manifest==null||manifest.ChoiceTime<0||!Finite(manifest.ChoiceTime)||manifest.SoftwareRunId==Guid.Empty)throw new InvalidOperationException("Manifest unavailable.");
            if(legacy!=(manifest.Context!=null&&manifest.Context.IsLegacy))throw new InvalidOperationException("Manifest design does not match format.");
            return Build(w=>{
                w.Write(0L);w.Write((byte)0);Text(w,legacy?"VRAW0004":version3?"VRAW0003":"VRAW0002");Text(w,manifest.Synthetic?"SYNTHETIC_ONLY":"INSTALLED_SDK_CODE_PATH_ONLY");
                Text(w,"SIMULATION_DEVELOPMENT_NOT_RESEARCH");Text(w,"NATIVE_TIME_OPAQUE");Text(w,"EYE_COVERAGE_UNKNOWN");
                Text(w,"INGEST_CONTEXT_NOT_SAMPLE_ALIGNMENT");Text(w,"SRANIPAL_SDK_VERSION_UNKNOWN");Text(w,"RAW_EYE_SPACE_UNVERIFIED");
                Text(w,"MAPPING_NOT_APPLIED;REHEARSAL="+ViveProEyeRehearsalCoordinates.MappingVersion);
                Text(w,"EYE_ORIGIN_MILLIMETERS");Text(w,"HEAD_TRACKING_LOCAL_ROTATION");Text(w,"DEVICE_IDENTITY_UNKNOWN");
                Text(w,"PARTICIPANT_IDENTITY_NOT_AUTHENTICATED");RequiredText(w,manifest.Session);RequiredText(w,manifest.ClockId);
                if(legacy){WriteLegacyManifest(w,manifest);return;}
                Text(w,ViveNamedSimulationPlan.SchemaVersion);RequiredText(w,manifest.Plan.PlanId);Text(w,ViveNamedRecordingIntent.EventSchemaVersion);RequiredText(w,manifest.Intent.IntentId);
                Bool(w,manifest.Intent.TaskRequested);Bool(w,manifest.Intent.EyeRequested);Bool(w,true);w.Write(manifest.ChoiceTime);w.Write(manifest.TaskBaseline);
                byte[] id=manifest.SoftwareRunId.ToByteArray();w.Write(id);w.Write(manifest.Plan.Blocks.Count);
                foreach(var block in manifest.Plan.Blocks)
                {w.Write(block.Index);w.Write((int)block.Selection.Option);w.Write(block.Selection.ListIndex);RequiredText(w,block.Selection.ScheduleVersion);RequiredText(w,block.Selection.GeometryVersion);}
            });
        }
        static void WriteLegacyManifest(BinaryWriter w,ViveNamedSessionFrameAdmission.Manifest m)
        {
            var c=m.Context.Configuration;var d=m.Context.LegacyDesign;var intent=m.Context.LegacyIntent;
            if(c==null||d==null||intent==null||c.IsNamedDesign||c.SessionId!=m.Session||!Object.ReferenceEquals(c.Allocation,d.Allocation))throw new InvalidOperationException("Legacy context unavailable.");
            Text(w,"LEGACY_ABCD");Text(w,ViveLegacyRecordingDesign.SchemaVersion);RequiredText(w,d.DesignId);
            Text(w,ViveLegacyRecordingIntent.EventSchemaVersion);RequiredText(w,intent.IntentId);
            Bool(w,intent.TaskRequested);Bool(w,intent.EyeRequested);Bool(w,true);w.Write(m.ChoiceTime);w.Write(m.TaskBaseline);w.Write(m.SoftwareRunId.ToByteArray());
            if(!Finite(m.TaskChoiceObserved)||!Finite(m.EyeChoiceObserved)||m.TaskChoiceObserved<0||m.EyeChoiceObserved<0||m.EyeChoiceObserved<m.TaskChoiceObserved||m.EyeChoiceObserved>m.ChoiceTime)
                throw new InvalidOperationException("Legacy choice declaration unavailable.");
            w.Write(m.TaskChoiceObserved);w.Write(m.EyeChoiceObserved);w.Write(c.Seed);w.Write((int)c.RunKind);
            RequiredText(w,c.QuestionnaireLanguage);w.Write(c.CountdownSeconds);w.Write(c.MinimumRestSeconds);Bool(w,c.SkipFinalRest);
            w.Write(c.QuestionnaireInstructionSeconds);Bool(w,c.QuestionnaireInstructionRequiresAcknowledgement);Bool(w,c.ParticipantPacedRest);Bool(w,c.RetainControlAcrossBlocks);
            Text(w,SessionConfiguration.ProtocolVersion);Text(w,SessionConfiguration.SoftwareVersion);RequiredText(w,d.GeometryVersion);RequiredText(w,d.MappingVersion);
            RequiredText(w,d.Allocation.AllocationId);w.Write((int)d.Allocation.BInitialTarget);var order=d.Allocation.Order;var lists=d.Allocation.ListByBlock;
            if(String.Join("",Array.ConvertAll(order,x=>x.ToString()))!="BCAD"||d.Allocation.BInitialTarget!=GazeTarget.Participant)throw new InvalidOperationException("Legacy allocation unavailable.");
            w.Write(order.Length);for(int i=0;i<order.Length;i++){if(lists[i]!=i)throw new InvalidOperationException("Legacy list unavailable.");w.Write(i);w.Write((int)order[i]);w.Write(lists[i]);}
        }
        internal static byte[] RecordPayload(long ordinal,object value,ViveNamedSessionFrameAdmission.Manifest manifest)
        {
            if(ordinal<1||manifest==null)throw new InvalidOperationException("Raw record unavailable.");
            return Build(w=>{
                w.Write(ordinal);var item=value as JournalEvent;
                if(item!=null)
                {
                    ValidateTaskRecord(item,manifest);
                    w.Write((byte)1);w.Write(item.Sequence);w.Write(item.Time);Text(w,item.ClockId);Text(w,item.SessionId);w.Write(item.BlockIndex);w.Write(item.Segment);
                    Text(w,item.TrialId);RequiredText(w,item.Kind);w.Write(manifest.Context!=null&&manifest.Context.IsLegacy?item.BlockIndex:item.NamedBlock==null?-1:item.NamedBlock.Index);
                    if(item.Fields.Count>128)throw new InvalidOperationException("Field limit.");
                    var keys=new List<string>(item.Fields.Keys);keys.Sort(StringComparer.Ordinal);w.Write(keys.Count);
                    foreach(string key in keys){RequiredText(w,key);Text(w,item.Fields[key]);}return;
                }
                var frame=value as ViveNamedSessionFrameAdmission.Frame;if(frame==null||!Finite(frame.EyeObserved)||frame.EyeObserved<0||
                    !Finite(frame.HeadObserved)||frame.HeadObserved<0||!Finite(frame.CheckedAt)||frame.CheckedAt<0||
                    frame.EyeObserved<=manifest.ChoiceTime||frame.HeadObserved<=manifest.ChoiceTime||
                    frame.EyeObserved>frame.CheckedAt||frame.HeadObserved>frame.CheckedAt||!Enum.IsDefined(typeof(ViveEyeRuntimeState),frame.Runtime)||
                    !Enum.IsDefined(typeof(ViveEyeCalibrationState),frame.Calibration)||!Enum.IsDefined(typeof(Phase),frame.PhaseAtIngest))throw new InvalidOperationException("Eye record unavailable.");
                w.Write((byte)2);w.Write(frame.NativeFrame);w.Write(frame.NativeTimestamp);w.Write(frame.EyeObserved);w.Write(frame.HeadSequence);w.Write(frame.HeadObserved);
                w.Write(frame.CheckedAt);w.Write(frame.LossGeneration);w.Write((int)frame.Runtime);w.Write((int)frame.Calibration);Bool(w,frame.EyeDeviceConnected);
                OptionalInt(w,frame.NativeResultCode);OptionalInt(w,frame.NativeRuntimeStatus);Bool(w,frame.NoUser);
                Ray(w,frame.Left);Ray(w,frame.Right);Ray(w,frame.Combined);Bool(w,frame.HeadDeviceConnected);Bool(w,frame.HeadRotationValid);
                w.Write(frame.RawHeadRotation.X);w.Write(frame.RawHeadRotation.Y);w.Write(frame.RawHeadRotation.Z);w.Write(frame.RawHeadRotation.W);
                w.Write((int)frame.PhaseAtIngest);w.Write(frame.Block);w.Write(frame.Segment);Text(w,frame.TrialAtIngest);w.Write(frame.TaskEventCursor);
            });
        }
        internal static byte[] Frame(byte[] payload,byte[] previous,bool magic,out byte[] digest)
        {return FrameFor(payload,previous,magic,false,out digest);}
        internal static byte[] FrameV3(byte[] payload,byte[] previous,bool magic,out byte[] digest)
        {return FrameFor(payload,previous,magic,true,out digest);}
        internal static byte[] FrameV4(byte[] payload,byte[] previous,bool magic,out byte[] digest)
        {var result=FrameFor(payload,previous,magic,true,out digest);if(magic)Buffer.BlockCopy(Encoding.ASCII.GetBytes("VRAW0004"),0,result,0,8);return result;}
        static byte[] FrameFor(byte[] payload,byte[] previous,bool magic,bool version3,out byte[] digest)
        {
            if(payload==null||payload.Length==0||payload.Length>1048576||previous==null||previous.Length!=32)throw new InvalidOperationException("Frame limit.");
            digest=ContinuousJournal.Digest(previous,payload);int offset=magic?8:0;byte[] frame=new byte[offset+8+payload.Length+digest.Length];
            if(magic)Buffer.BlockCopy(Encoding.ASCII.GetBytes(version3?"VRAW0003":"VRAW0002"),0,frame,0,8);
            ContinuousJournal.PutInt(frame,payload.Length,offset);ContinuousJournal.PutInt(frame,~payload.Length,offset+4);
            Buffer.BlockCopy(payload,0,frame,offset+8,payload.Length);Buffer.BlockCopy(digest,0,frame,offset+8+payload.Length,digest.Length);return frame;
        }
        // These values must ultimately come from the writer's own confirmed
        // cursor and the admission's sealed waterline, never from owner/UI
        // input. A marker declares a drained capture only: it cannot certify
        // successful flush/OS close, durable storage, identity, or coverage.
        internal static byte[] TerminalPayloadV3(long ordinal,long taskCursor,long tasks,long eyes,long statuses,long sealedWatermark,double localObserved)
        {
            if(ordinal<1||taskCursor< -1||tasks<0||eyes<0||statuses<0||sealedWatermark<0||!Finite(localObserved)||localObserved<0)
                throw new InvalidOperationException("Invalid capture terminal.");
            long sum=checked(tasks+eyes+statuses);
            if(sum!=sealedWatermark||sum!=ordinal-1)throw new InvalidOperationException("Capture waterline differs from encoded records.");
            return Build(w=>{w.Write(ordinal);w.Write((byte)4);Text(w,"NORMAL_TASK_COMPLETION_DRAIN_DECLARED");w.Write(taskCursor);
                w.Write(tasks);w.Write(eyes);w.Write(statuses);w.Write(sealedWatermark);w.Write(localObserved);});
        }
        // A status describes one provider return on the local clock, not a
        // hardware timestamp, lost-frame count, or interval duration.
        internal static byte[] StatusPayloadV3(long ordinal,ViveProEyeWorker.ObservedEyeStatus value)
        {
            if(ordinal<1||!ValidStatus(value))throw new InvalidOperationException("Invalid eye status.");
            return Build(w=>{w.Write(ordinal);w.Write((byte)3);w.Write(value.Sequence);w.Write(value.PollReturnLocalSeconds);w.Write((ulong)value.CauseFlags);
                OptionalInt(w,value.Runtime.HasValue?(int?)value.Runtime.Value:null);OptionalInt(w,value.Calibration.HasValue?(int?)value.Calibration.Value:null);
                OptionalBool(w,value.EyeDeviceConnected);OptionalInt(w,value.NativeResultCode);OptionalInt(w,value.NativeRuntimeStatus);
                OptionalBool(w,value.NoUser);OptionalUlong(w,value.CombinedValidityMask);OptionalBool(w,value.CombinedDirectionReportedValid);});
        }
        static bool Has(ulong flags,ulong bit){return (flags&bit)!=0;}
        internal static bool ValidStatus(ViveProEyeWorker.ObservedEyeStatus v)
        {
            if(v==null||v.Sequence<0||!Finite(v.PollReturnLocalSeconds)||v.PollReturnLocalSeconds<0)return false;
            ulong flags=(ulong)v.CauseFlags;if((flags&~16383UL)!=0)return false;
            if(Has(flags,1))return flags==1&&!v.Runtime.HasValue&&!v.Calibration.HasValue&&!v.EyeDeviceConnected.HasValue&&
                !v.NativeResultCode.HasValue&&!v.NativeRuntimeStatus.HasValue&&!v.NoUser.HasValue&&!v.CombinedValidityMask.HasValue&&!v.CombinedDirectionReportedValid.HasValue;
            if(!v.EyeDeviceConnected.HasValue||
                (v.Runtime.HasValue&&!Enum.IsDefined(typeof(ViveEyeRuntimeState),v.Runtime.Value))||
                (v.Calibration.HasValue&&!Enum.IsDefined(typeof(ViveEyeCalibrationState),v.Calibration.Value))||
                Has(flags,4096)!=( !v.Runtime.HasValue||!v.Calibration.HasValue)||
                Has(flags,2)!=(v.Runtime!=ViveEyeRuntimeState.Working)||Has(flags,4)!=(v.Calibration!=ViveEyeCalibrationState.ReportedReady)||
                Has(flags,8)!=(!v.EyeDeviceConnected.Value)||Has(flags,16)!=(!v.NativeResultCode.HasValue)||
                Has(flags,32)!=(v.NativeResultCode.HasValue&&v.NativeResultCode.Value!=0))return false;
            bool eyeMissing=Has(flags,64);
            if(eyeMissing)
                return !v.NoUser.HasValue&&!v.CombinedValidityMask.HasValue&&!v.CombinedDirectionReportedValid.HasValue&&
                    (flags&(128UL|256UL|512UL|1024UL|2048UL|8192UL))==0;
            if(!v.NativeResultCode.HasValue||v.NativeResultCode.Value!=0||!v.NoUser.HasValue||Has(flags,128)!=v.NoUser.Value)return false;
            bool combinedMissing=Has(flags,256);
            if(combinedMissing)return !v.CombinedValidityMask.HasValue&&!v.CombinedDirectionReportedValid.HasValue&&
                (flags&(512UL|1024UL|2048UL))==0;
            return v.CombinedValidityMask.HasValue&&v.CombinedDirectionReportedValid.HasValue&&
                Has(flags,512)!=((v.CombinedValidityMask.Value&2UL)!=0)&&Has(flags,1024)!=v.CombinedDirectionReportedValid.Value;
        }
        static byte[] Build(Action<BinaryWriter> write)
        {
            using(var memory=new MemoryStream())using(var writer=new BinaryWriter(memory,Utf8))
            {write(writer);writer.Flush();if(memory.Length>1048576)throw new InvalidOperationException("Payload limit.");return memory.ToArray();}
        }
        static void Text(BinaryWriter writer,string value)
        {if(value==null){writer.Write(-1);return;}int size=Utf8.GetByteCount(value);if(size>32768)throw new InvalidOperationException("Text limit.");writer.Write(size);writer.Write(Utf8.GetBytes(value));}
        static void RequiredText(BinaryWriter writer,string value)
        {if(String.IsNullOrWhiteSpace(value))throw new InvalidOperationException("Required text unavailable.");Text(writer,value);}
        static void Field(JournalEvent item,string key,string expected)
        {string actual;if(!item.Fields.TryGetValue(key,out actual)||!String.Equals(actual,expected,StringComparison.Ordinal))throw new InvalidOperationException("Task provenance unavailable.");}
        // Shared with the independent size candidate. This
        // is exactly the task identity/provenance check used by the encoder;
        // string, field-count and payload limits remain at their old positions.
        internal static void ValidateTaskRecord(JournalEvent item,ViveNamedSessionFrameAdmission.Manifest manifest)
        {
            if(manifest!=null&&manifest.Context!=null&&manifest.Context.IsLegacy)
            {ValidateLegacyTask(item,manifest);ValidatePresentationDeclaration(item.Kind,item.Fields,item.ClockId,item.Time);ValidateSoftwareChoices(item.Kind,item.Fields,item.ClockId,item.Time);return;}
            if(item==null||manifest==null||!Finite(item.Time)||item.Time<0||!Object.ReferenceEquals(item.NamedPlan,manifest.Plan)||
                !Object.ReferenceEquals(item.NamedRecordingIntent,manifest.Intent)||!String.Equals(item.SessionId,manifest.Session,StringComparison.Ordinal)||
                !String.Equals(item.ClockId,manifest.ClockId,StringComparison.Ordinal)||item.Sequence<0||
                (item.NamedBlock!=null&&(!Object.ReferenceEquals(item.NamedBlock.Plan,manifest.Plan)||item.NamedBlock.Index<0||item.NamedBlock.Index>=manifest.Plan.Blocks.Count||
                    !Object.ReferenceEquals(item.NamedBlock,manifest.Plan.Blocks[item.NamedBlock.Index]))))throw new InvalidOperationException("Task identity unavailable.");
            ValidateTaskFields(item,manifest);
            ValidatePresentationDeclaration(item.Kind,item.Fields,item.ClockId,item.Time);
            ValidateSoftwareChoices(item.Kind,item.Fields,item.ClockId,item.Time);
        }
        // Optional declared event; old files without it remain valid. It is
        // not proof of identity, study consent, hardware event time or disk.
        internal static void ValidateSoftwareChoices(string kind,IReadOnlyDictionary<string,string> fields,string clockId,double eventTime)
        {
            if(kind!="participant_recording_software_choices")return;
            string task,eye,id,basis,identity;
            double t,e;
            if(fields==null||!fields.TryGetValue("task_choice_poll_seconds",out task)||
                !fields.TryGetValue("eye_choice_poll_seconds",out eye)||!fields.TryGetValue("choice_clock_id",out id)||
                !fields.TryGetValue("choice_time_basis",out basis)||!fields.TryGetValue("identity_or_study_consent",out identity)||
                id!=clockId||basis!="controller_poll_not_hardware_event"||identity!="NOT_AUTHENTICATED"||
                !Double.TryParse(task,NumberStyles.Float,CultureInfo.InvariantCulture,out t)||!Finite(t)||t<0||
                !Double.TryParse(eye,NumberStyles.Float,CultureInfo.InvariantCulture,out e)||!Finite(e)||e<t||e>eventTime)
                throw new InvalidOperationException("Software choice declaration unavailable.");
        }
        // Optional v1 declaration: legacy events have none of these fields.
        // Values retain their independent clock domains and describe software
        // observations only, not photons, participant vision or persistence.
        internal static void ValidatePresentationDeclaration(string kind,IReadOnlyDictionary<string,string> fields,string clockId,double eventTime)
        {
            string[] keys={"software_proxy_schema","task_onset_seconds","task_onset_clock_id","task_onset_basis",
                "unity_render_proxy_seconds","unity_render_proxy_clock_id","unity_render_proxy_frame","unity_render_proxy_revision","unity_render_proxy_basis"};
            int found=0;foreach(string key in keys)if(fields.ContainsKey(key))found++;
            if(found==0)return;
            if(found!=keys.Length||kind!="stimulus_presented"||fields["software_proxy_schema"]!="1"||
                fields["task_onset_clock_id"]!=clockId||fields["task_onset_basis"]!="task_clock_after_caller_declared_unity_proxy"||
                fields["unity_render_proxy_clock_id"]!="unity-realtime-since-startup"||
                fields["unity_render_proxy_basis"]!="caller_declared_unity_render_proxy_not_photon_onset")
                throw new InvalidOperationException("Software presentation declaration unavailable.");
            double onset,proxy;int frame;long revision;
            if(!Double.TryParse(fields["task_onset_seconds"],NumberStyles.Float,CultureInfo.InvariantCulture,out onset)||!Finite(onset)||onset<0||onset>eventTime||
                !Double.TryParse(fields["unity_render_proxy_seconds"],NumberStyles.Float,CultureInfo.InvariantCulture,out proxy)||!Finite(proxy)||proxy<0||
                !Int32.TryParse(fields["unity_render_proxy_frame"],NumberStyles.None,CultureInfo.InvariantCulture,out frame)||frame<0||
                !Int64.TryParse(fields["unity_render_proxy_revision"],NumberStyles.None,CultureInfo.InvariantCulture,out revision)||revision<0)
                throw new InvalidOperationException("Software presentation declaration invalid.");
        }
        static void ValidateTaskFields(JournalEvent item,ViveNamedSessionFrameAdmission.Manifest manifest)
        {
            foreach(string key in item.Fields.Keys)if(key.StartsWith("legacy_",StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Mixed design fields.");
            Field(item,"named_schema",ViveNamedRecordingIntent.EventSchemaVersion);Field(item,"named_plan_id",manifest.Plan.PlanId);
            Field(item,"named_plan_schema",ViveNamedSimulationPlan.SchemaVersion);Field(item,"named_recording_intent_id",manifest.Intent.IntentId);
            Field(item,"named_eye_sample_payload","NOT_INCLUDED_IN_TASK_EVENT");
            Field(item,"named_task_recording_intent",manifest.Intent.TaskRequested?"REQUESTED":"OFF");
            Field(item,"named_eye_recording_intent",manifest.Intent.EyeRequested?"REQUESTED":"OFF");
            if(item.NamedBlock==null){if(item.BlockIndex!=-1)throw new InvalidOperationException("Task block unavailable.");return;}
            var block=item.NamedBlock;var selection=block.Selection;
            if(item.BlockIndex!=block.Index)throw new InvalidOperationException("Task block unavailable.");
            Field(item,"named_block_index",block.Index.ToString(CultureInfo.InvariantCulture));Field(item,"named_option",selection.Option.ToString());
            Field(item,"named_list_index",selection.ListIndex.ToString(CultureInfo.InvariantCulture));
            Field(item,"named_schedule",selection.ScheduleVersion);Field(item,"named_geometry",selection.GeometryVersion);
        }
        static void ValidateLegacyTask(JournalEvent item,ViveNamedSessionFrameAdmission.Manifest m)
        {
            var d=m.Context.LegacyDesign;var intent=m.Context.LegacyIntent;
            if(item==null||!Finite(item.Time)||item.Time<0||item.Sequence<0||item.SessionId!=m.Session||item.ClockId!=m.ClockId||
                item.NamedPlan!=null||item.NamedOption!=null||item.NamedBlock!=null||item.NamedRecordingIntent!=null||
                !Object.ReferenceEquals(item.LegacyRecordingDesign,d)||!Object.ReferenceEquals(item.LegacyRecordingIntent,intent)||item.BlockIndex< -1||item.BlockIndex>=4)
                throw new InvalidOperationException("Legacy task identity unavailable.");
            foreach(string key in item.Fields.Keys)if(key.StartsWith("named_",StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Mixed design fields.");
            Field(item,"legacy_schema",ViveLegacyRecordingIntent.EventSchemaVersion);Field(item,"legacy_design_schema",ViveLegacyRecordingDesign.SchemaVersion);
            Field(item,"legacy_design_id",d.DesignId);Field(item,"legacy_recording_intent_id",intent.IntentId);
            Field(item,"legacy_task_recording_intent",intent.TaskRequested?"REQUESTED":"OFF");Field(item,"legacy_eye_recording_intent",intent.EyeRequested?"REQUESTED":"OFF");
            Field(item,"legacy_eye_sample_payload","NOT_INCLUDED_IN_TASK_EVENT");Field(item,"legacy_allocation_id",d.Allocation.AllocationId);Field(item,"legacy_b_initial_target",d.Allocation.BInitialTarget.ToString());
            if(item.Kind=="participant_recording_software_choices")
            {
                string taskText,eyeText;double taskChoice,eyeChoice;
                if(!item.Fields.TryGetValue("task_choice_poll_seconds",out taskText)||!item.Fields.TryGetValue("eye_choice_poll_seconds",out eyeText)||
                    !Double.TryParse(taskText,NumberStyles.Float,CultureInfo.InvariantCulture,out taskChoice)||
                    !Double.TryParse(eyeText,NumberStyles.Float,CultureInfo.InvariantCulture,out eyeChoice)||
                    BitConverter.DoubleToInt64Bits(taskChoice)!=BitConverter.DoubleToInt64Bits(m.TaskChoiceObserved)||
                    BitConverter.DoubleToInt64Bits(eyeChoice)!=BitConverter.DoubleToInt64Bits(m.EyeChoiceObserved))
                    throw new InvalidOperationException("Legacy software choice declaration differs from admission.");
            }
            string phase;if(!item.Fields.TryGetValue("legacy_phase",out phase)||!Enum.IsDefined(typeof(Phase),phase))throw new InvalidOperationException("Legacy phase unavailable.");
            if(item.BlockIndex>=0){Field(item,"legacy_condition",d.Allocation.Order[item.BlockIndex].ToString());Field(item,"legacy_list_index",d.Allocation.ListByBlock[item.BlockIndex].ToString(CultureInfo.InvariantCulture));}
            if(item.BlockIndex>=0)
            {
                if(item.Fields.ContainsKey("condition"))Field(item,"condition",d.Allocation.Order[item.BlockIndex].ToString());
                if(item.Fields.ContainsKey("list_index"))Field(item,"list_index",d.Allocation.ListByBlock[item.BlockIndex].ToString(CultureInfo.InvariantCulture));
                if(item.Fields.ContainsKey("b_initial_target"))Field(item,"b_initial_target",d.Allocation.BInitialTarget.ToString());
            }
            else if(item.Fields.ContainsKey("legacy_condition")||item.Fields.ContainsKey("legacy_list_index")||item.Fields.ContainsKey("legacy_block_started"))throw new InvalidOperationException("Legacy block unavailable.");
        }
        static void Bool(BinaryWriter writer,bool value){writer.Write((byte)(value?1:0));}
        static void OptionalInt(BinaryWriter writer,int? value){Bool(writer,value.HasValue);if(value.HasValue)writer.Write(value.Value);}
        static void OptionalBool(BinaryWriter writer,bool? value){Bool(writer,value.HasValue);if(value.HasValue)Bool(writer,value.Value);}
        static void OptionalUlong(BinaryWriter writer,ulong? value){Bool(writer,value.HasValue);if(value.HasValue)writer.Write(value.Value);}
        static void Ray(BinaryWriter writer,ViveNamedSessionFrameAdmission.RawEyeRay value)
        {Bool(writer,value!=null);if(value==null)return;writer.Write(value.Mask);Bool(writer,value.OriginValid);Bool(writer,value.DirectionValid);
            writer.Write(value.OriginMillimeters.X);writer.Write(value.OriginMillimeters.Y);writer.Write(value.OriginMillimeters.Z);
            writer.Write(value.Direction.X);writer.Write(value.Direction.Y);writer.Write(value.Direction.Z);}
        static bool Finite(double value){return !Double.IsNaN(value)&&!Double.IsInfinity(value);}
    }
}
