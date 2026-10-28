using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace AvatarStroop.Core
{
    public sealed class AuditContext
    {
        public int BlockIndex { get; private set; }
        public int Segment { get; private set; }
        public string TrialId { get; private set; }
        public AuditContext(int blockIndex,int segment,string trialId=null)
        {
            if(blockIndex < -1 || blockIndex > 3 || segment < 0) throw new ArgumentOutOfRangeException("auditContext");
            BlockIndex=blockIndex; Segment=segment; TrialId=trialId;
        }
    }

    public sealed class OperatorSampleRecord
    {
        public HeadEyeSample Sample { get; private set; }
        public string TrialId { get; private set; }
        internal OperatorSampleRecord(HeadEyeSample sample,string trialId) { Sample=sample; TrialId=trialId; }
    }

    public sealed class AvatarPresentationRecord
    {
        public AuditContext Context { get; private set; }
        public string ClockId { get; private set; }
        public double PresentationLocalSeconds { get; private set; }
        public string TimestampSemantics { get; private set; }
        public Phase Phase { get; private set; }
        public bool AvatarVisible { get; private set; }
        public ControlSource SelectedSource { get; private set; }
        public ControlSource? ActualSource { get; private set; }
        public AvatarPose? ActualPose { get; private set; }
        public AvatarPose? ReferencePose { get; private set; }
        public ReferencePhase ReferencePhase { get; private set; }
        public GazeTarget? ReferenceFrom { get; private set; }
        public GazeTarget? ReferenceTo { get; private set; }
        public double ReferenceActiveSeconds { get; private set; }
        public double ReferenceCycleSeconds { get; private set; }
        public double TransitionFraction { get; private set; }
        public string GeometryVersion { get; private set; }
        public string MappingVersion { get; private set; }
        public bool HeadValid { get; private set; }
        public bool EyeValid { get; private set; }
        public GazeOutputStatus Status { get; private set; }
        public HeadEyeSample InputSample { get; private set; }
        public string DeviationReason { get; private set; }
        public AvatarPose? RendererAppliedPose {get;private set;}
        public bool? RendererObserved {get;private set;}
        public bool? RendererHeadHeld {get;private set;}
        public bool? RendererEyesHeld {get;private set;}
        public bool? RendererVisibilityRequested {get;private set;}
        public AvatarPresentationRecord(AuditContext context,string clockId,double presentationLocalSeconds,string timestampSemantics,
            Phase phase,AvatarOutput output,string deviationReason=null,AvatarPose? rendererAppliedPose=null,
            bool? rendererObserved=null,bool? rendererHeadHeld=null,bool? rendererEyesHeld=null,bool? rendererVisibilityRequested=null)
        {
            if(context == null || output == null || output.Reference == null) throw new ArgumentNullException("context/output/reference");
            GazeNumbers.Identity(clockId,"clockId"); GazeNumbers.Identity(timestampSemantics,"timestampSemantics");
            GazeNumbers.Time(presentationLocalSeconds,"presentationLocalSeconds");
            if(!Enum.IsDefined(typeof(Phase),phase)) throw new ArgumentException("Unknown phase");
            Context=context; ClockId=clockId; PresentationLocalSeconds=presentationLocalSeconds; TimestampSemantics=timestampSemantics; Phase=phase;
            AvatarVisible=output.AvatarVisible; SelectedSource=output.SelectedSource; ActualSource=output.ActualSource;
            ActualPose=output.ActualPose; ReferencePose=output.Reference.Pose; ReferencePhase=output.Reference.Phase;
            ReferenceFrom=output.Reference.FromTarget; ReferenceTo=output.Reference.ToTarget;
            ReferenceActiveSeconds=output.Reference.ActiveSeconds; ReferenceCycleSeconds=output.Reference.CycleSeconds;
            TransitionFraction=output.Reference.TransitionFraction; GeometryVersion=output.Reference.GeometryVersion;
            MappingVersion=output.MappingVersion; HeadValid=output.HeadValid; EyeValid=output.EyeValid; Status=output.Status;
            InputSample=output.InputSample; DeviationReason=deviationReason;
            if(rendererAppliedPose.HasValue != rendererObserved.HasValue || rendererObserved.HasValue != rendererHeadHeld.HasValue || rendererObserved.HasValue != rendererEyesHeld.HasValue)
                throw new ArgumentException("Renderer provenance requires applied pose, observation and held-axis flags together");
            RendererAppliedPose=rendererAppliedPose;RendererObserved=rendererObserved;RendererHeadHeld=rendererHeadHeld;RendererEyesHeld=rendererEyesHeld;
            RendererVisibilityRequested=rendererVisibilityRequested;
        }
    }

    // Caller-provided clock observations, not a claim that synchronization was achieved.
    // Offset is source minus reference at ObservedReferenceSeconds; drift is source
    // relative to reference, in ppm. Evidence identifies the estimator and its limitations.
    public sealed class ClockAuditRecord
    {
        public AuditContext Context { get; private set; }
        public string SourceClockId { get; private set; }
        public string ReferenceClockId { get; private set; }
        public string MappingVersion { get; private set; }
        public double ObservedReferenceSeconds { get; private set; }
        public double? OffsetSeconds { get; private set; }
        public double? DriftPartsPerMillion { get; private set; }
        public double? UncertaintySeconds { get; private set; }
        public string Evidence { get; private set; }
        public ClockAuditRecord(AuditContext context,string sourceClockId,string referenceClockId,string mappingVersion,double observedReferenceSeconds,
            double? offsetSeconds,double? driftPartsPerMillion,double? uncertaintySeconds,string evidence)
        {
            if(context == null) throw new ArgumentNullException("context");
            GazeNumbers.Identity(sourceClockId,"sourceClockId"); GazeNumbers.Identity(referenceClockId,"referenceClockId");
            GazeNumbers.Identity(mappingVersion,"mappingVersion"); GazeNumbers.Identity(evidence,"evidence");
            GazeNumbers.Time(observedReferenceSeconds,"observedReferenceSeconds");
            ValidateOptional(offsetSeconds); ValidateOptional(driftPartsPerMillion); ValidateOptional(uncertaintySeconds);
            if(uncertaintySeconds < 0) throw new ArgumentException("Uncertainty cannot be negative");
            Context=context; SourceClockId=sourceClockId; ReferenceClockId=referenceClockId; MappingVersion=mappingVersion;
            ObservedReferenceSeconds=observedReferenceSeconds; OffsetSeconds=offsetSeconds; DriftPartsPerMillion=driftPartsPerMillion;
            UncertaintySeconds=uncertaintySeconds; Evidence=evidence;
        }
        internal static void ValidateOptional(double? value) { if(value.HasValue && !GazeNumbers.Finite(value.Value)) throw new ArgumentException("Nonfinite optional measurement"); }
    }

    // Adapter-neutral audit: nullable measured quantities mean unavailable, never zero.
    public sealed class TransportAuditRecord
    {
        public AuditContext Context { get; private set; }
        public ControlSource Source { get; private set; }
        public string StreamId { get; private set; }
        public long? PacketSequence { get; private set; }
        public long? SourceSampleSequence { get; private set; }
        public string ReceiveClockId { get; private set; }
        public double ReceiveLocalSeconds { get; private set; }
        public string Kind { get; private set; }
        public long? MissingSamples { get; private set; }
        public double? LatencySeconds { get; private set; }
        public double? JitterSeconds { get; private set; }
        public string ClockMappingVersion { get; private set; }
        public string Detail { get; private set; }
        public TransportAuditRecord(AuditContext context,ControlSource source,string streamId,long? packetSequence,string receiveClockId,
            double receiveLocalSeconds,string kind,long? missingSamples,double? latencySeconds,double? jitterSeconds,string clockMappingVersion,string detail,long? sourceSampleSequence=null)
        {
            if(context == null) throw new ArgumentNullException("context");
            if(!Enum.IsDefined(typeof(ControlSource),source) || source == ControlSource.Automatic) throw new ArgumentException("Transport must identify its sample source");
            GazeNumbers.Identity(streamId,"streamId"); GazeNumbers.Identity(receiveClockId,"receiveClockId"); GazeNumbers.Identity(kind,"kind");
            GazeNumbers.Time(receiveLocalSeconds,"receiveLocalSeconds"); ClockAuditRecord.ValidateOptional(latencySeconds); ClockAuditRecord.ValidateOptional(jitterSeconds);
            if(packetSequence < 0 || sourceSampleSequence < 0 || missingSamples < 0 || latencySeconds < 0 || jitterSeconds < 0) throw new ArgumentException("Negative transport measurement");
            if((latencySeconds.HasValue || jitterSeconds.HasValue) && String.IsNullOrWhiteSpace(clockMappingVersion))
                throw new ArgumentException("Latency/jitter need explicit clock-mapping provenance");
            Context=context; Source=source; StreamId=streamId; PacketSequence=packetSequence; SourceSampleSequence=sourceSampleSequence; ReceiveClockId=receiveClockId;
            ReceiveLocalSeconds=receiveLocalSeconds; Kind=kind; MissingSamples=missingSamples; LatencySeconds=latencySeconds;
            JitterSeconds=jitterSeconds; ClockMappingVersion=clockMappingVersion; Detail=detail;
        }
    }

    public sealed class AuditLog
    {
        readonly List<OperatorSampleRecord> operators=new List<OperatorSampleRecord>();
        readonly List<AvatarPresentationRecord> presentations=new List<AvatarPresentationRecord>();
        readonly List<ClockAuditRecord> clocks=new List<ClockAuditRecord>();
        readonly List<TransportAuditRecord> transport=new List<TransportAuditRecord>();
        readonly HashSet<string> operatorIdentities=new HashSet<string>(StringComparer.Ordinal);
        readonly Dictionary<string,HeadEyeSample> operatorStreams=new Dictionary<string,HeadEyeSample>(StringComparer.Ordinal);
        public string SessionId { get; private set; }
        public RunKind RunKind { get; private set; }
        public ParticipantGazeLog ParticipantGaze { get; private set; }
        public IReadOnlyList<OperatorSampleRecord> OperatorSamples { get { return operators.AsReadOnly(); } }
        public IReadOnlyList<AvatarPresentationRecord> Presentations { get { return presentations.AsReadOnly(); } }
        public IReadOnlyList<ClockAuditRecord> Clocks { get { return clocks.AsReadOnly(); } }
        public IReadOnlyList<TransportAuditRecord> Transport { get { return transport.AsReadOnly(); } }
        public AuditLog(string sessionId,RunKind runKind,ParticipantGazeLog participantGaze=null)
        {
            GazeNumbers.Identity(sessionId,"sessionId");
            if(!Enum.IsDefined(typeof(RunKind),runKind)) throw new ArgumentException("Unknown run kind");
            if(participantGaze != null && (participantGaze.SessionId != sessionId || participantGaze.RunKind != runKind)) throw new ArgumentException("Participant stream/session mismatch");
            SessionId=sessionId; RunKind=runKind; ParticipantGaze=participantGaze;
        }
        public void RecordOperatorSample(HeadEyeSample sample,string trialId=null)
        {
            ValidateOperator(sample);
            HeadEyeSample prior;
            if(operatorStreams.TryGetValue(sample.StreamId,out prior) && (prior.DeviceId != sample.DeviceId || prior.ClockId != sample.ClockId || prior.Source != sample.Source))
                throw new ArgumentException("A control stream cannot silently change device, clock or source");
            string identity=sample.StreamId.Length.ToString(CultureInfo.InvariantCulture)+":"+sample.StreamId+":"+sample.BlockIndex+":"+sample.Segment+":"+sample.Sequence.ToString(CultureInfo.InvariantCulture);
            if(!operatorIdentities.Add(identity)) throw new ArgumentException("Duplicate operator sample identity");
            operatorStreams[sample.StreamId]=sample; operators.Add(new OperatorSampleRecord(sample,trialId));
        }
        void ValidateOperator(HeadEyeSample sample)
        {
            if(sample == null) throw new ArgumentNullException("sample");
            if(sample.SessionId != SessionId || sample.Role != StreamRole.OperatorControl || sample.Source == ControlSource.Automatic)
                throw new ArgumentException("Only this session's raw operator-control samples belong here");
            ValidateSource(sample.Source);
            if(ParticipantGaze != null && (sample.StreamId == ParticipantGaze.StreamId || sample.DeviceId == ParticipantGaze.DeviceId))
                throw new ArgumentException("Operator and participant require separate stream and device identities");
        }
        void ValidateSource(ControlSource source)
        { if(source == ControlSource.Simulator && RunKind != RunKind.Simulation) throw new ArgumentException("Simulator cannot enter apparatus data"); }
        public void RecordPresentation(AvatarPresentationRecord record)
        {
            if(record == null) throw new ArgumentNullException("record");
            ValidateSource(record.SelectedSource); if(record.ActualSource.HasValue) ValidateSource(record.ActualSource.Value);
            if(record.InputSample != null)
            {
                ValidateOperator(record.InputSample);
                if(record.InputSample.BlockIndex != record.Context.BlockIndex || record.InputSample.Segment != record.Context.Segment)
                    throw new ArgumentException("Presentation/input sample context mismatch");
            }
            presentations.Add(record);
        }
        public void RecordClock(ClockAuditRecord record) { if(record == null) throw new ArgumentNullException("record"); clocks.Add(record); }
        public void RecordTransport(TransportAuditRecord record)
        { if(record == null) throw new ArgumentNullException("record"); ValidateSource(record.Source); transport.Add(record); }
    }

    public sealed class ExportProvenance
    {
        public string ExporterId { get; private set; }
        public string BuildSourceDigest { get; private set; }
        public string StimulusTimestampSemantics { get; private set; }
        public string ResponseTimestampSemantics { get; private set; }
        public ExportProvenance(string exporterId,string stimulusTimestampSemantics,string responseTimestampSemantics,string buildSourceDigest=null)
        {
            GazeNumbers.Identity(exporterId,"exporterId"); GazeNumbers.Identity(stimulusTimestampSemantics,"stimulusTimestampSemantics");
            GazeNumbers.Identity(responseTimestampSemantics,"responseTimestampSemantics");
            BuildSourceDigest=String.IsNullOrWhiteSpace(buildSourceDigest) ? "unavailable" : buildSourceDigest;
            ExporterId=exporterId; StimulusTimestampSemantics=stimulusTimestampSemantics; ResponseTimestampSemantics=responseTimestampSemantics;
        }
    }
    public sealed class ExportResult
    {
        public bool Success { get; internal set; }
        public string DirectoryPath { get; internal set; }
        public string FailedFile { get; internal set; }
        public string Error { get; internal set; }
        public bool SessionCompleted { get; internal set; }
    }

    public static class AuditExporter
    {
        public const string SchemaVersion="avatar-stroop-csv-1";
        const string Common="schema_version,protocol_version,software_version,session_id,run_kind,data_label,block_index,segment,trial_id,source";
        sealed class Table
        {
            internal string Name; internal StringBuilder Text; internal int Rows;
            internal Table(string name,string columns) { Name=name; Text=new StringBuilder(Common+","+columns+"\r\n"); }
        }
        // Call on the experiment owner thread: strings are snapshotted before disk IO.
        public static ExportResult Export(Experiment experiment,AuditLog audit,string parentDirectory,ExportProvenance provenance)
        {
            if(experiment == null || audit == null || provenance == null) throw new ArgumentNullException("experiment/audit/provenance");
            if(experiment.Configuration.SessionId != audit.SessionId || experiment.Configuration.RunKind != audit.RunKind)
                throw new ArgumentException("Audit/session provenance mismatch");
            if(String.IsNullOrWhiteSpace(parentDirectory)) throw new ArgumentException("Explicit export parent required");
            var result=new ExportResult { SessionCompleted=experiment.EndReason == EndReason.Completed };
            try
            {
                var tables=Snapshot(experiment,audit,provenance);
                Directory.CreateDirectory(parentDirectory);
                string path;
                do { path=Path.Combine(parentDirectory,"export-"+DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ",CultureInfo.InvariantCulture)+"-"+Guid.NewGuid().ToString("N")); }
                while(Directory.Exists(path) || File.Exists(path));
                Directory.CreateDirectory(path); result.DirectoryPath=path;
                var manifest=new Table("manifest.csv","file_name,row_count,sha256,complete_bundle,session_completed,exporter_id");
                foreach(var table in tables)
                {
                    result.FailedFile=table.Name;
                    byte[] bytes=new UTF8Encoding(false).GetBytes(table.Text.ToString());
                    WriteNew(path,table.Name,bytes);
                    string hash;
                    using(var sha=SHA256.Create()) hash=BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-","").ToLowerInvariant();
                    Add(manifest,experiment.Configuration,null,"export_manifest",table.Name,table.Rows,hash,true,result.SessionCompleted,provenance.ExporterId);
                }
                result.FailedFile=manifest.Name;
                WriteNew(path,manifest.Name,new UTF8Encoding(false).GetBytes(manifest.Text.ToString()));
                result.Success=true; result.FailedFile=null;
            }
            catch(Exception error) when(error is IOException || error is UnauthorizedAccessException || error is System.Security.SecurityException || error is NotSupportedException || error is ArgumentException)
            { result.Error=error.GetType().Name+": "+error.Message; }
            return result;
        }
        static void WriteNew(string directory,string name,byte[] bytes)
        {
            string staging=Path.Combine(directory,name+".partial"),final=Path.Combine(directory,name);
            using(var stream=new FileStream(staging,FileMode.CreateNew,FileAccess.Write,FileShare.None))
            { stream.Write(bytes,0,bytes.Length); stream.Flush(true); }
            File.Move(staging,final);
        }
        static List<Table> Snapshot(Experiment e,AuditLog audit,ExportProvenance provenance)
        {
            var c=e.Configuration; var tables=new List<Table>();
            var session=New(tables,"session.csv","phase,end_reason,session_completed,bundle_is_snapshot,seed,allocation_id,condition_order,b_initial_target,list_by_block,practice_attempts,countdown_seconds,questionnaire_language,exporter_id,build_source_digest,stimulus_timestamp_semantics,response_timestamp_semantics,event_count,trial_count,rating_count,task_clock_id");
            Add(session,c,null,"session_engine",e.Phase,e.EndReason,e.EndReason == EndReason.Completed,true,c.Seed,c.Allocation.AllocationId,
                String.Join("",Array.ConvertAll(c.Allocation.Order,x=>x.ToString())),c.Allocation.BInitialTarget,String.Join(";",c.Allocation.ListByBlock),e.PracticeAttempt,
                c.CountdownSeconds,c.QuestionnaireLanguage,provenance.ExporterId,provenance.BuildSourceDigest,provenance.StimulusTimestampSemantics,provenance.ResponseTimestampSemantics,e.Events.Count,e.Trials.Count,e.Ratings.Count,e.Events[0].ClockId);
            var allocations=New(tables,"allocations.csv","condition,list_index,list_id,seed,selected_control");
            var selected=new Dictionary<int,string>();
            foreach(var ev in e.Events) if(ev.Kind == "block_configuration_locked") selected[ev.BlockIndex]=ev.Fields["selection"];
            for(int i=0;i<4;i++) Add(allocations,c,new AuditContext(i,0),"allocation",c.Allocation.Order[i],c.Allocation.ListByBlock[i],TrialPlans.MainListId(c.Seed,c.Allocation.ListByBlock[i]),c.Seed,Selection(selected,i));
            var events=New(tables,"events.csv","event_sequence,time_seconds,clock_id,kind");
            var fields=New(tables,"event_fields.csv","event_sequence,field_name,field_value");
            foreach(var ev in e.Events)
            {
                var context=new AuditContext(ev.BlockIndex,ev.Segment,ev.TrialId);
                Add(events,c,context,"session_engine",ev.Sequence,ev.Time,ev.ClockId,ev.Kind);
                foreach(var field in ev.Fields) Add(fields,c,context,"session_engine",ev.Sequence,field.Key,field.Value);
            }
            var trials=New(tables,"trials.csv","is_practice,practice_attempt,condition,list_id,seed,planned_index,word,ink,congruent,outcome,stimulus_onset_recorded,onset_seconds,response_seconds,response,correct,reaction_seconds,technical_issue,selected_control,stimulus_timestamp_semantics,response_timestamp_semantics");
            foreach(var tr in e.Trials)
                Add(trials,c,new AuditContext(tr.BlockIndex,tr.Segment,tr.TrialId),"task_response",tr.IsPractice,tr.PracticeAttempt,tr.Condition,tr.ListId,c.Seed,tr.PlannedIndex,
                    tr.Stimulus.Word,tr.Stimulus.Ink,tr.Stimulus.Congruent,tr.Outcome,tr.Onset.HasValue,tr.Onset,tr.ResponseAt,tr.Response,
                    tr.Outcome == TrialOutcome.Responded ? tr.Correct : null,tr.Outcome == TrialOutcome.Responded ? tr.ReactionSeconds : null,tr.TechnicalIssue,
                    tr.IsPractice ? null : Selection(selected,tr.BlockIndex),provenance.StimulusTimestampSemantics,provenance.ResponseTimestampSemantics);
            var ratings=New(tables,"ratings.csv","condition,item_id,value,time_seconds,language,item_version");
            foreach(var rating in e.Ratings)
            {
                JournalEvent match=null;
                foreach(var ev in e.Events) if(ev.Kind == "rating" && ev.BlockIndex == rating.BlockIndex && ev.Fields["item"] == rating.ItemId) { match=ev; break; }
                Add(ratings,c,new AuditContext(rating.BlockIndex,match == null ? 0 : match.Segment),"participant_rating",c.Allocation.Order[rating.BlockIndex],rating.ItemId,rating.Value,rating.Time,rating.Language,rating.Version);
            }
            OperatorTables(tables,c,audit); ParticipantTables(tables,c,audit.ParticipantGaze); PresentationTables(tables,c,audit);
            return tables;
        }
        static string Selection(Dictionary<int,string> selected,int block) { string value; return selected.TryGetValue(block,out value) ? value : null; }
        static void OperatorTables(List<Table> tables,SessionConfiguration c,AuditLog audit)
        {
            var raw=New(tables,"operator_samples.csv","role,stream_id,device_id,clock_id,sample_sequence,capture_local_seconds,send_local_seconds,head_x,head_y,head_z,head_w,eye_x,eye_y,eye_z,head_reported_valid,eye_reported_valid,head_valid,eye_valid");
            foreach(var record in audit.OperatorSamples)
            {
                var s=record.Sample;
                Add(raw,c,new AuditContext(s.BlockIndex,s.Segment,record.TrialId),s.Source.ToString(),s.Role,s.StreamId,s.DeviceId,s.ClockId,s.Sequence,s.CaptureLocalSeconds,s.SendLocalSeconds,
                    s.RawHeadRotation.X,s.RawHeadRotation.Y,s.RawHeadRotation.Z,s.RawHeadRotation.W,s.RawEyeDirection.X,s.RawEyeDirection.Y,s.RawEyeDirection.Z,
                    s.HeadReportedValid,s.EyeReportedValid,s.HeadValid,s.EyeValid);
            }
        }
        static void ParticipantTables(List<Table> tables,SessionConfiguration c,ParticipantGazeLog gaze)
        {
            var samples=New(tables,"participant_samples.csv","role,stream_id,device_id,clock_id,sample_sequence,capture_local_seconds,eye_x,eye_y,eye_z,eye_reported_valid,eye_valid");
            var windows=New(tables,"gaze_windows.csv","stream_id,reference_clock_id,condition,phase,start_seconds,end_seconds,active_task");
            var intervals=New(tables,"gaze_intervals.csv","stream_id,sample_sequence,reference_clock_id,condition,phase,start_seconds,end_seconds,aoi,eye_valid,mapping_version,calibration_version,classification_method");
            var summaries=New(tables,"gaze_summary.csv","stream_id,reference_clock_id,condition,active_seconds,valid_seconds,invalid_seconds,unobserved_seconds,valid_coverage,coverage_denominator,task_seconds,other_seconds,eyes_seconds,remaining_face_seconds,combined_face_seconds,task_proportion,eyes_proportion,remaining_face_proportion,combined_face_proportion,proportion_denominator,avatar_aoi_applicability");
            if(gaze == null) return;
            foreach(var s in gaze.Samples)
                Add(samples,c,new AuditContext(s.BlockIndex,s.Segment),s.Source.ToString(),s.Role,s.StreamId,s.DeviceId,s.ClockId,s.Sequence,s.CaptureLocalSeconds,
                    s.RawEyeDirection.X,s.RawEyeDirection.Y,s.RawEyeDirection.Z,s.EyeReportedValid,s.EyeValid);
            foreach(var w in gaze.Windows)
                Add(windows,c,new AuditContext(w.BlockIndex,w.Segment),gaze.Source.ToString(),gaze.StreamId,gaze.ReferenceClockId,w.Condition,w.Phase,w.Start,w.End,w.Phase == Phase.MainTask);
            foreach(var i in gaze.Intervals)
                Add(intervals,c,new AuditContext(i.Sample.BlockIndex,i.Sample.Segment,i.TrialId),gaze.Source.ToString(),gaze.StreamId,i.Sample.Sequence,gaze.ReferenceClockId,
                    i.Window.Condition,i.Window.Phase,i.Start,i.End,i.Aoi,i.Sample.EyeValid,i.MappingVersion,i.CalibrationVersion,"caller_classified_no_geometry_validation_claim");
            foreach(var s in gaze.Summaries())
                Add(summaries,c,new AuditContext(s.BlockIndex,s.Segment),gaze.Source.ToString(),gaze.StreamId,gaze.ReferenceClockId,s.Condition,s.ActiveSeconds,s.ValidSeconds,s.InvalidSeconds,s.UnobservedSeconds,
                    s.ValidCoverage,"total_active_task_seconds",s.TaskSeconds,s.OtherSeconds,s.EyesSeconds,s.RemainingFaceSeconds,s.CombinedFaceSeconds,s.TaskProportion,s.EyesProportion,s.RemainingFaceProportion,
                    s.CombinedFaceProportion,"valid_active_task_seconds",s.AvatarAoisApplicable ? "Applicable" : "NotApplicable");
        }
        static void PresentationTables(List<Table> tables,SessionConfiguration c,AuditLog audit)
        {
            var presentation=New(tables,"avatar_presentations.csv","phase,presentation_clock_id,presentation_local_seconds,timestamp_semantics,avatar_visible,selected_source,actual_source,status,head_valid,eye_valid,mapping_version,geometry_version,input_stream_id,input_sequence,reference_phase,reference_from,reference_to,reference_active_seconds,reference_cycle_seconds,transition_fraction,reference_head_x,reference_head_y,reference_head_z,reference_head_w,reference_eye_x,reference_eye_y,reference_eye_z,mapped_head_x,mapped_head_y,mapped_head_z,mapped_head_w,mapped_eye_x,mapped_eye_y,mapped_eye_z,deviation_reason,renderer_observed,renderer_head_held,renderer_eyes_held,renderer_visibility_requested,applied_head_x,applied_head_y,applied_head_z,applied_head_w,applied_eye_x,applied_eye_y,applied_eye_z");
            foreach(var p in audit.Presentations)
            {
                var row=new List<object> {p.Phase,p.ClockId,p.PresentationLocalSeconds,p.TimestampSemantics,p.AvatarVisible,p.SelectedSource,p.ActualSource,p.Status,p.HeadValid,p.EyeValid,p.MappingVersion,p.GeometryVersion,
                    p.InputSample == null ? null : p.InputSample.StreamId,p.InputSample == null ? (long?)null : p.InputSample.Sequence,p.ReferencePhase,p.ReferenceFrom,p.ReferenceTo,p.ReferenceActiveSeconds,p.ReferenceCycleSeconds,p.TransitionFraction};
                Pose(row,p.ReferencePose); Pose(row,p.ActualPose); row.Add(p.DeviationReason);
                row.Add(p.RendererObserved);row.Add(p.RendererHeadHeld);row.Add(p.RendererEyesHeld);row.Add(p.RendererVisibilityRequested);Pose(row,p.RendererAppliedPose);
                Add(presentation,c,p.Context,p.ActualSource.HasValue ? p.ActualSource.ToString() : null,row.ToArray());
            }
            var clocks=New(tables,"clock_audit.csv","source_clock_id,reference_clock_id,mapping_version,observed_reference_seconds,offset_seconds,drift_parts_per_million,uncertainty_seconds,evidence");
            foreach(var r in audit.Clocks) Add(clocks,c,r.Context,"clock_observation",r.SourceClockId,r.ReferenceClockId,r.MappingVersion,r.ObservedReferenceSeconds,r.OffsetSeconds,r.DriftPartsPerMillion,r.UncertaintySeconds,r.Evidence);
            var transport=New(tables,"transport_audit.csv","stream_id,packet_sequence,source_sample_sequence,receive_clock_id,receive_local_seconds,kind,missing_samples,latency_seconds,jitter_seconds,clock_mapping_version,detail");
            foreach(var r in audit.Transport) Add(transport,c,r.Context,r.Source.ToString(),r.StreamId,r.PacketSequence,r.SourceSampleSequence,r.ReceiveClockId,r.ReceiveLocalSeconds,r.Kind,r.MissingSamples,r.LatencySeconds,r.JitterSeconds,r.ClockMappingVersion,r.Detail);
        }
        static void Pose(List<object> row,AvatarPose? pose)
        {
            if(!pose.HasValue) { for(int i=0;i<7;i++) row.Add(null); return; }
            var p=pose.Value; row.AddRange(new object[] {p.HeadRotation.X,p.HeadRotation.Y,p.HeadRotation.Z,p.HeadRotation.W,p.EyeDirection.X,p.EyeDirection.Y,p.EyeDirection.Z});
        }
        static Table New(List<Table> tables,string name,string columns) { var table=new Table(name,columns); tables.Add(table); return table; }
        static void Add(Table table,SessionConfiguration c,AuditContext context,string source,params object[] values)
        {
            var row=new List<object> {SchemaVersion,SessionConfiguration.ProtocolVersion,SessionConfiguration.SoftwareVersion,c.SessionId,c.RunKind,
                c.RunKind == RunKind.Simulation ? "SIMULATION" : "APPARATUS",context == null ? (int?)null : context.BlockIndex,context == null ? (int?)null : context.Segment,context == null ? null : context.TrialId,source};
            row.AddRange(values);
            for(int i=0;i<row.Count;i++) { if(i>0) table.Text.Append(','); table.Text.Append(Csv(row[i])); }
            table.Text.Append("\r\n"); table.Rows++;
        }
        static string Csv(object value)
        {
            if(value == null) return "";
            string text;
            if(value is double) text=((double)value).ToString("R",CultureInfo.InvariantCulture);
            else if(value is bool) text=(bool)value ? "true" : "false";
            else if(value is IFormattable) text=((IFormattable)value).ToString(null,CultureInfo.InvariantCulture);
            else text=value.ToString();
            return text.IndexOfAny(new[] {',','"','\r','\n'}) >= 0 ? "\""+text.Replace("\"","\"\"")+"\"" : text;
        }
    }
}
