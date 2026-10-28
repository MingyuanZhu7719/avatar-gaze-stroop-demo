using System;
using System.Collections.Generic;
using System.Globalization;

namespace AvatarStroop.Core
{
    // Owner-thread adapter. Records are immutable observations, not a serialized
    // executable Experiment and never a promise that an interrupted task can resume.
    public sealed class SimulationJournalCapture
    {
        readonly ContinuousJournal journal;
        readonly Experiment experiment;
        readonly AuditLog audit;
        readonly Dictionary<string,TrialRecord> trials=new Dictionary<string,TrialRecord>(StringComparer.Ordinal);
        readonly Dictionary<string,string> trialVersions=new Dictionary<string,string>(StringComparer.Ordinal);
        readonly Dictionary<int,string> selected=new Dictionary<int,string>();
        readonly Dictionary<string,long> acquisitionSequences=new Dictionary<string,long>(StringComparer.Ordinal);
        readonly Dictionary<string,HeadEyeSample> acquisitionStreams=new Dictionary<string,HeadEyeSample>(StringComparer.Ordinal);
        int eventCursor,trialCursor,ratingCursor,operatorCursor,presentationCursor,clockCursor,transportCursor;
        int participantCursor,windowCursor,intervalCursor;
        bool initialized;

        public ContinuousJournal Journal {get{return journal;}}
        public SimulationJournalCapture(ContinuousJournal journal,Experiment experiment=null,AuditLog audit=null)
        {
            if(journal==null)throw new ArgumentNullException("journal");
            if((experiment==null)!=(audit==null))throw new ArgumentException("Experiment and audit must be supplied together");
            if(experiment!=null && (journal.Identity.Role!="participant" || experiment.Configuration.RunKind!=RunKind.Simulation || audit.RunKind!=RunKind.Simulation ||
                journal.Identity.SessionId!=experiment.Configuration.SessionId || journal.Identity.SessionId!=audit.SessionId || journal.Identity.ClockId!=experiment.Events[0].ClockId))
                throw new ArgumentException("Recording requires the matching simulation participant session");
            if(experiment==null && journal.Identity.Role!="operator")throw new ArgumentException("Operator recorder role required");
            this.journal=journal;this.experiment=experiment;this.audit=audit;
        }

        public bool Capture()
        {
            if(journal.IsFaulted)return false;
            if(experiment==null)return !journal.IsFaulted;
            if(!initialized)
            {
                var c=experiment.Configuration;
                if(!Append("session_configuration",Fields("seed",c.Seed,"allocation_id",c.Allocation.AllocationId,
                    "condition_order",String.Join("",Array.ConvertAll(c.Allocation.Order,x=>x.ToString())),"b_initial_target",c.Allocation.BInitialTarget,
                    "list_by_block",String.Join(";",c.Allocation.ListByBlock),"countdown_seconds",c.CountdownSeconds,"questionnaire_language",c.QuestionnaireLanguage)))return false;
                for(int block=0;block<4;block++)if(!Append("allocation",Fields("block_index",block,"condition",c.Allocation.Order[block],"list_index",c.Allocation.ListByBlock[block],
                    "list_id",TrialPlans.MainListId(c.Seed,c.Allocation.ListByBlock[block]),"seed",c.Seed)))return false;
                var gaze=audit.ParticipantGaze;
                if(!Append("participant_measurement_configuration",Fields("available",gaze!=null,"reason",gaze==null?"no_independent_participant_measurement_stream_attached":"independent_caller_supplied_stream",
                    "stream_id",gaze==null?null:gaze.StreamId,"device_id",gaze==null?null:gaze.DeviceId,"reference_clock_id",gaze==null?null:gaze.ReferenceClockId,
                    "source",gaze==null?null:gaze.Source.ToString(),"aoi_method","caller_classified_no_geometry_validation_claim")))return false;
                initialized=true;
            }
            for(;trialCursor<experiment.Trials.Count;trialCursor++)trials.Add(experiment.Trials[trialCursor].TrialId,experiment.Trials[trialCursor]);
            bool changed=eventCursor<experiment.Events.Count;
            var dirty=new HashSet<string>(StringComparer.Ordinal);
            for(;eventCursor<experiment.Events.Count;eventCursor++)
            {
                var e=experiment.Events[eventCursor];
                var f=Context(e.BlockIndex,e.Segment,e.TrialId);
                Put(f,"engine_sequence",e.Sequence,"time_seconds",e.Time,"clock_id",e.ClockId,"event_kind",e.Kind);
                foreach(var field in e.Fields)f.Add("field:"+field.Key,field.Value);
                if(!Append("engine_event",f))return false;
                if(e.Kind=="block_configuration_locked")selected[e.BlockIndex]=e.Fields["selection"];
                if(e.TrialId!=null)dirty.Add(e.TrialId);
            }
            // Trial objects mutate after insertion. Only trials named by newly
            // observed engine events need a new immutable state observation.
            foreach(var id in dirty)
            {
                TrialRecord tr;if(!trials.TryGetValue(id,out tr))continue;
                var f=Context(tr.BlockIndex,tr.Segment,tr.TrialId);string selection;
                Put(f,"is_practice",tr.IsPractice,"practice_attempt",tr.PracticeAttempt,"condition",tr.Condition,"list_id",tr.ListId,"seed",experiment.Configuration.Seed,
                    "planned_index",tr.PlannedIndex,"word",tr.Stimulus.Word,"ink",tr.Stimulus.Ink,"congruent",tr.Stimulus.Congruent,"outcome",tr.Outcome,
                    "stimulus_onset_recorded",tr.Onset.HasValue,"onset_seconds",tr.Onset,"response_seconds",tr.ResponseAt,"response",tr.Response,
                    "correct",tr.Outcome==TrialOutcome.Responded?tr.Correct:null,"reaction_seconds",tr.Outcome==TrialOutcome.Responded?tr.ReactionSeconds:null,
                    "technical_issue",tr.TechnicalIssue,"selected_control",!tr.IsPractice&&selected.TryGetValue(tr.BlockIndex,out selection)?selection:null,
                    "observed_through_engine_sequence",eventCursor-1);
                // Compare the mutable values only; unrelated later events must not
                // produce duplicate observations of an unchanged trial.
                string version=Text(tr.Onset)+"|"+Text(tr.ResponseAt)+"|"+Text(tr.Response)+"|"+Text(tr.Outcome)+"|"+Text(tr.Correct)+"|"+Text(tr.TechnicalIssue);
                string prior;if(!trialVersions.TryGetValue(id,out prior)||prior!=version)
                {if(!Append("trial_observation",f))return false;trialVersions[id]=version;}
            }
            for(;ratingCursor<experiment.Ratings.Count;ratingCursor++)
            {
                var r=experiment.Ratings[ratingCursor];int segment=0;
                // There are at most twenty ratings. Its original engine event is
                // the context authority, just as in the snapshot exporter.
                foreach(var e in experiment.Events)if(e.Kind=="rating"&&e.BlockIndex==r.BlockIndex&&e.Fields["item"]==r.ItemId){segment=e.Segment;break;}
                var f=Context(r.BlockIndex,segment,null);Put(f,"rating_index",ratingCursor,"condition",experiment.Configuration.Allocation.Order[r.BlockIndex],
                    "item_id",r.ItemId,"value",r.Value,"time_seconds",r.Time,"language",r.Language,"item_version",r.Version);
                if(!Append("rating",f))return false;
            }
            if(changed)
            {
                var f=Context(experiment.BlockIndex,experiment.Segment,experiment.CurrentTrial==null?null:experiment.CurrentTrial.TrialId);
                Put(f,"phase",experiment.Phase,"end_reason",experiment.EndReason,"session_completed",experiment.EndReason==EndReason.Completed,
                    "practice_attempts",experiment.PracticeAttempt,"locked_control",experiment.LockedControl,"event_count",eventCursor,"trial_count",trialCursor,"rating_count",ratingCursor);
                if(!Append("session_observation",f))return false;
            }
            for(;operatorCursor<audit.OperatorSamples.Count;operatorCursor++)
            {var r=audit.OperatorSamples[operatorCursor];var f=OperatorFields(r.Sample,r.TrialId);Put(f,"audit_index",operatorCursor);if(!Append("operator_received_sample",f))return false;}
            for(;presentationCursor<audit.Presentations.Count;presentationCursor++)
            {
                var p=audit.Presentations[presentationCursor];var f=Context(p.Context);
                Put(f,"audit_index",presentationCursor,"phase",p.Phase,"presentation_clock_id",p.ClockId,"presentation_local_seconds",p.PresentationLocalSeconds,
                    "timestamp_semantics",p.TimestampSemantics,"avatar_visible",p.AvatarVisible,"selected_source",p.SelectedSource,"actual_source",p.ActualSource,
                    "status",p.Status,"head_valid",p.HeadValid,"eye_valid",p.EyeValid,"mapping_version",p.MappingVersion,"geometry_version",p.GeometryVersion,
                    "input_stream_id",p.InputSample==null?null:p.InputSample.StreamId,"input_sequence",p.InputSample==null?(long?)null:p.InputSample.Sequence,
                    "reference_phase",p.ReferencePhase,"reference_from",p.ReferenceFrom,"reference_to",p.ReferenceTo,"reference_active_seconds",p.ReferenceActiveSeconds,
                    "reference_cycle_seconds",p.ReferenceCycleSeconds,"transition_fraction",p.TransitionFraction,"deviation_reason",p.DeviationReason,
                    "renderer_observed",p.RendererObserved,"renderer_head_held",p.RendererHeadHeld,"renderer_eyes_held",p.RendererEyesHeld,"renderer_visibility_requested",p.RendererVisibilityRequested);
                Pose(f,"reference_",p.ReferencePose);Pose(f,"mapped_",p.ActualPose);Pose(f,"applied_",p.RendererAppliedPose);
                if(!Append("avatar_presentation",f))return false;
            }
            for(;clockCursor<audit.Clocks.Count;clockCursor++)
            {
                var r=audit.Clocks[clockCursor];var f=Context(r.Context);Put(f,"audit_index",clockCursor,"source_clock_id",r.SourceClockId,"reference_clock_id",r.ReferenceClockId,
                    "mapping_version",r.MappingVersion,"observed_reference_seconds",r.ObservedReferenceSeconds,"offset_seconds",r.OffsetSeconds,
                    "drift_parts_per_million",r.DriftPartsPerMillion,"uncertainty_seconds",r.UncertaintySeconds,"evidence",r.Evidence);
                if(!Append("clock_observation",f))return false;
            }
            for(;transportCursor<audit.Transport.Count;transportCursor++)
            {
                var r=audit.Transport[transportCursor];var f=Context(r.Context);Put(f,"audit_index",transportCursor,"source",r.Source,"stream_id",r.StreamId,
                    "packet_sequence",r.PacketSequence,"source_sample_sequence",r.SourceSampleSequence,"receive_clock_id",r.ReceiveClockId,"receive_local_seconds",r.ReceiveLocalSeconds,
                    "transport_kind",r.Kind,"missing_samples",r.MissingSamples,"latency_seconds",r.LatencySeconds,"jitter_seconds",r.JitterSeconds,"clock_mapping_version",r.ClockMappingVersion,"detail",r.Detail);
                if(!Append("transport_observation",f))return false;
            }
            return CaptureParticipant();
        }

        public bool RecordAcquisition(HeadEyeSample sample)
        {
            if(journal.Identity.Role!="operator" || sample==null || sample.SessionId!=journal.Identity.SessionId || sample.ClockId!=journal.Identity.ClockId || sample.Role!=StreamRole.OperatorControl || sample.Source!=ControlSource.Simulator)
                throw new ArgumentException("This operator simulation's acquired sample is required");
            if(journal.IsFaulted)return false;
            HeadEyeSample prior;
            if(acquisitionStreams.TryGetValue(sample.StreamId,out prior)&&(prior.DeviceId!=sample.DeviceId||prior.ClockId!=sample.ClockId))throw new ArgumentException("Acquisition stream identity changed");
            string key=sample.StreamId.Length.ToString(CultureInfo.InvariantCulture)+":"+sample.StreamId+":"+sample.BlockIndex+":"+sample.Segment;long last;
            if(acquisitionSequences.TryGetValue(key,out last)&&sample.Sequence<=last)throw new ArgumentException("Duplicate or reordered acquisition sample");
            if(!Append("operator_acquired_sample",OperatorFields(sample,null,true)))return false;
            acquisitionSequences[key]=sample.Sequence;acquisitionStreams[sample.StreamId]=sample;return true;
        }

        bool CaptureParticipant()
        {
            var gaze=audit.ParticipantGaze;if(gaze==null)return !journal.IsFaulted;
            for(;participantCursor<gaze.Samples.Count;participantCursor++)
            {
                var s=gaze.Samples[participantCursor];var f=Context(s.BlockIndex,s.Segment,null);Put(f,"audit_index",participantCursor,"source",s.Source,"measurement_role",s.Role,
                    "stream_id",s.StreamId,"device_id",s.DeviceId,"clock_id",s.ClockId,"sample_sequence",s.Sequence,"capture_local_seconds",s.CaptureLocalSeconds,
                    "eye_x",s.RawEyeDirection.X,"eye_y",s.RawEyeDirection.Y,"eye_z",s.RawEyeDirection.Z,"eye_reported_valid",s.EyeReportedValid,"eye_valid",s.EyeValid);
                if(!Append("participant_sample",f))return false;
            }
            for(;windowCursor<gaze.Windows.Count;windowCursor++)
            {
                var w=gaze.Windows[windowCursor];var f=Context(w.BlockIndex,w.Segment,null);Put(f,"window_index",windowCursor,"stream_id",gaze.StreamId,"reference_clock_id",gaze.ReferenceClockId,
                    "condition",w.Condition,"phase",w.Phase,"start_seconds",w.Start,"end_seconds",w.End,"active_task",w.Phase==Phase.MainTask);
                if(!Append("gaze_window",f))return false;
            }
            for(;intervalCursor<gaze.Intervals.Count;intervalCursor++)
            {
                var i=gaze.Intervals[intervalCursor];var f=Context(i.Sample.BlockIndex,i.Sample.Segment,i.TrialId);Put(f,"interval_index",intervalCursor,"stream_id",gaze.StreamId,
                    "sample_sequence",i.Sample.Sequence,"reference_clock_id",gaze.ReferenceClockId,"condition",i.Window.Condition,"phase",i.Window.Phase,
                    "window_start_seconds",i.Window.Start,"window_end_seconds",i.Window.End,"start_seconds",i.Start,"end_seconds",i.End,"aoi",i.Aoi,"eye_valid",i.Sample.EyeValid,
                    "mapping_version",i.MappingVersion,"calibration_version",i.CalibrationVersion,"classification_method","caller_classified_no_geometry_validation_claim");
                if(!Append("gaze_interval",f))return false;
            }
            return !journal.IsFaulted;
        }

        bool Append(string kind,Dictionary<string,string> fields){return journal.TryAppend(kind,fields);}
        static Dictionary<string,string> OperatorFields(HeadEyeSample s,string trial,bool acquisition=false)
        {
            var f=Context(s.BlockIndex,s.Segment,trial);Put(f,"source",s.Source,"measurement_role",s.Role,"stream_id",s.StreamId,"device_id",s.DeviceId,"clock_id",s.ClockId,
                "sample_sequence",s.Sequence,"capture_local_seconds",s.CaptureLocalSeconds,"send_local_seconds",s.SendLocalSeconds,
                "send_timestamp_semantics",acquisition?"sample_prepared_before_journal_append_and_socket_attempt;not_network_emission":"source_supplied_send_timestamp;consult_source_acquisition_endpoint_semantics",
                "head_x",s.RawHeadRotation.X,"head_y",s.RawHeadRotation.Y,"head_z",s.RawHeadRotation.Z,"head_w",s.RawHeadRotation.W,
                "eye_x",s.RawEyeDirection.X,"eye_y",s.RawEyeDirection.Y,"eye_z",s.RawEyeDirection.Z,"head_reported_valid",s.HeadReportedValid,"eye_reported_valid",s.EyeReportedValid,"head_valid",s.HeadValid,"eye_valid",s.EyeValid);
            return f;
        }
        static void Pose(Dictionary<string,string> fields,string prefix,AvatarPose? pose)
        {
            Put(fields,prefix+"head_x",pose.HasValue?(double?)pose.Value.HeadRotation.X:null,prefix+"head_y",pose.HasValue?(double?)pose.Value.HeadRotation.Y:null,
                prefix+"head_z",pose.HasValue?(double?)pose.Value.HeadRotation.Z:null,prefix+"head_w",pose.HasValue?(double?)pose.Value.HeadRotation.W:null,
                prefix+"eye_x",pose.HasValue?(double?)pose.Value.EyeDirection.X:null,prefix+"eye_y",pose.HasValue?(double?)pose.Value.EyeDirection.Y:null,prefix+"eye_z",pose.HasValue?(double?)pose.Value.EyeDirection.Z:null);
        }
        static Dictionary<string,string> Context(AuditContext context){return Context(context.BlockIndex,context.Segment,context.TrialId);}
        static Dictionary<string,string> Context(int block,int segment,string trial){return Fields("block_index",block,"segment",segment,"trial_id",trial);}
        public static Dictionary<string,string> Fields(params object[] pairs){var f=new Dictionary<string,string>(StringComparer.Ordinal);Put(f,pairs);return f;}
        static void Put(Dictionary<string,string> fields,params object[] pairs)
        {for(int i=0;i<pairs.Length;i+=2)fields.Add((string)pairs[i],Text(pairs[i+1]));}
        static string Text(object value)
        {if(value==null)return null;if(value is double)return ((double)value).ToString("R",CultureInfo.InvariantCulture);if(value is bool)return (bool)value?"true":"false";return value is IFormattable?((IFormattable)value).ToString(null,CultureInfo.InvariantCulture):value.ToString();}
    }
}
