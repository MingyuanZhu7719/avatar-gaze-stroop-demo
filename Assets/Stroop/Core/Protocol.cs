using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace AvatarStroop.Core
{
    public enum Colour { Red, Green, Blue, Yellow }
    public enum Condition { A, B, C, D }
    public enum GazeTarget { Participant, Task }
    public enum Actor { Participant, Operator, Owner }
    public enum ControlSelection { Automatic, LiveWoz, SimulatorRehearsal }
    public enum RunKind { Simulation, Apparatus }
    public enum Phase { Preparation, Familiarization, Practice, PracticeCorrection, PracticeRetry,
        BlockPreparation, BlockReady, Countdown, MainTask, Questionnaire, Rest, Paused,
        // Retained for a future fault-recovery decision. It is not currently entered:
        // explicit pause/resume now follows PRD section 3 for practice as well as the
        // main task, so no state waits for an owner decision that does not exist.
        NeedsProtocolDecision, Ended }
    public enum EndReason { Completed, PracticeFailed, Withdrawn, TechnicalEnd }
    public enum TrialOutcome { Pending, Responded, Interrupted }

    // A new named design is deliberately not an alias for legacy A/B/C/D.
    public enum ViveNamedOption { Automatic, LiveWoz, NoAvatar, DirectParticipantEyes }
    // A pre-session development recording choice, not participant consent,
    // evidence of a running recorder, valid tracking, or complete coverage.
    public sealed class ViveNamedRecordingIntent
    {
        public const string EventSchemaVersion="vive-named-recording-intent-v3";
        public string IntentId {get;private set;}
        public bool TaskRequested {get;private set;}
        public bool EyeRequested {get;private set;}
        ViveNamedRecordingIntent(string id,bool task,bool eye){IntentId=id;TaskRequested=task;EyeRequested=eye;}
        public static ViveNamedRecordingIntent Disabled(Actor actor,string intentId)
        {return Select(actor,intentId,false,false);}
        public static ViveNamedRecordingIntent Select(Actor actor,string intentId,bool taskRequested,bool eyeRequested)
        {
            if(actor!=Actor.Owner)throw new UnauthorizedAccessException("Owner recording selection is required");
            if(String.IsNullOrWhiteSpace(intentId))throw new ArgumentException("Explicit recording intent identity required", "intentId");
            return new ViveNamedRecordingIntent(intentId,taskRequested,eyeRequested);
        }
    }
    public sealed class ViveNamedOptionProvenance
    {
        public const string SchemaVersion="vive-named-option-simulation-v1";
        public const string AutomaticScheduleVersion="vive-automatic-task-first-3-0.5-v1";
        public const string NoAvatarNotApplicable="NOT_APPLICABLE_NO_AVATAR";
        public ViveNamedOption Option {get;private set;}
        public string ScheduleVersion {get;private set;}
        public string GeometryVersion {get;private set;}
        public int ListIndex {get;private set;}
        ViveNamedOptionProvenance(ViveNamedOption option,int listIndex,string geometryVersion)
        {Option=option;ListIndex=listIndex;GeometryVersion=option==ViveNamedOption.NoAvatar?NoAvatarNotApplicable:geometryVersion;
            ScheduleVersion=option==ViveNamedOption.NoAvatar?NoAvatarNotApplicable:AutomaticScheduleVersion;}
        public static ViveNamedOptionProvenance Select(Actor actor,ViveNamedOption option,int listIndex,string geometryVersion)
        {
            if(actor!=Actor.Owner)throw new UnauthorizedAccessException("Owner selection is required");
            if(!Enum.IsDefined(typeof(ViveNamedOption),option))throw new ArgumentOutOfRangeException("option");
            if(option!=ViveNamedOption.Automatic&&option!=ViveNamedOption.NoAvatar)throw new InvalidOperationException("This named option is not implemented or available");
            if(listIndex<0||listIndex>3)throw new ArgumentOutOfRangeException("listIndex");
            if(option==ViveNamedOption.Automatic&&String.IsNullOrWhiteSpace(geometryVersion))throw new ArgumentException("Explicit target geometry version required", "geometryVersion");
            if(option==ViveNamedOption.NoAvatar&&geometryVersion!=null)throw new ArgumentException("No-avatar target geometry is structurally not applicable", "geometryVersion");
            return new ViveNamedOptionProvenance(option,listIndex,geometryVersion);
        }
    }

    public sealed class Stimulus
    {
        public Colour Word { get; private set; }
        public Colour Ink { get; private set; }
        public bool Congruent { get { return Word == Ink; } }
        public string Key { get { return Word + "/" + Ink; } }
        public Stimulus(Colour word, Colour ink)
        {
            if (!Enum.IsDefined(typeof(Colour), word) || !Enum.IsDefined(typeof(Colour), ink))
                throw new ArgumentOutOfRangeException("colour");
            Word = word; Ink = ink;
        }
    }

    public sealed class Allocation
    {
        readonly Condition[] order;
        readonly int[] lists;
        public string AllocationId { get; private set; }
        public Condition[] Order { get { return (Condition[])order.Clone(); } }
        public GazeTarget BInitialTarget { get; private set; }
        public int[] ListByBlock { get { return (int[])lists.Clone(); } }
        public Allocation(string allocationId, Condition[] order, GazeTarget bInitialTarget, int[] listByBlock)
        {
            if (String.IsNullOrWhiteSpace(allocationId) || order == null || listByBlock == null ||
                order.Length != 4 || listByBlock.Length != 4) throw new ArgumentException("Invalid allocation");
            var names = String.Join("", Array.ConvertAll(order, x => x.ToString()));
            if (names != "ABDC" && names != "BCAD" && names != "CDBA" && names != "DACB")
                throw new ArgumentException("Use an assigned Williams order");
            var seen = new HashSet<int>(listByBlock);
            if (seen.Count != 4 || !seen.SetEquals(new[] {0,1,2,3}) ||
                !Enum.IsDefined(typeof(GazeTarget), bInitialTarget)) throw new ArgumentException("Invalid factors");
            AllocationId = allocationId; this.order = (Condition[])order.Clone();
            BInitialTarget = bInitialTarget; lists = (int[])listByBlock.Clone();
        }
    }

    public interface IClock { double Now { get; } string ClockId { get; } }

    public sealed class SessionConfiguration
    {
        public string SessionId { get; private set; }
        public uint Seed { get; private set; }
        Allocation allocation;
        public Allocation Allocation { get { if(IsNamedDesign)throw new InvalidOperationException("Named-option sessions do not have a legacy A/B/C/D allocation");return allocation; } private set {allocation=value;} }
        ViveNamedOptionProvenance namedOption;
        public ViveNamedOptionProvenance NamedOption {get{if(NamedPlan!=null)throw new InvalidOperationException("A multi-block plan has no single named option");return namedOption;}private set{namedOption=value;}}
        public ViveNamedSimulationPlan NamedPlan {get;private set;}
        public ViveNamedRecordingIntent NamedRecordingIntent {get;private set;}
        public ViveLegacyRecordingDesign LegacyRecordingDesign {get;private set;}
        public ViveLegacyRecordingIntent LegacyRecordingIntent {get;private set;}
        internal bool HasRawRecordingIntent {get{return NamedRecordingIntent!=null||LegacyRecordingIntent!=null;}}
        internal bool RawTaskRequested {get{return NamedRecordingIntent!=null?NamedRecordingIntent.TaskRequested:LegacyRecordingIntent!=null&&LegacyRecordingIntent.TaskRequested;}}
        internal bool RawEyeRequested {get{return NamedRecordingIntent!=null?NamedRecordingIntent.EyeRequested:LegacyRecordingIntent!=null&&LegacyRecordingIntent.EyeRequested;}}
        public bool IsNamedDesign {get{return namedOption!=null||NamedPlan!=null;}}
        public RunKind RunKind { get; private set; }
        public string QuestionnaireLanguage { get; private set; }
        public double CountdownSeconds { get; private set; }
        public double MinimumRestSeconds { get; private set; }
        public bool SkipFinalRest { get; private set; }
        public double QuestionnaireInstructionSeconds { get; private set; }
        public bool QuestionnaireInstructionRequiresAcknowledgement { get; private set; }
        public bool ParticipantPacedRest { get; private set; }
        public bool RetainControlAcrossBlocks { get; private set; }
        public const string ProtocolVersion = "2026-10-28-prd-66dbdb5";
        public const string SoftwareVersion = "0.5.1-dev";
        // New sessions explicitly use the approved English wording. Historical
        // journal language fields are preserved by the separate evidence reader.
        public SessionConfiguration(string sessionId, uint seed, Allocation allocation, RunKind runKind,
            string questionnaireLanguage, double countdownSeconds, double minimumRestSeconds=30,
            bool skipFinalRest=false, double questionnaireInstructionSeconds=0,
            bool questionnaireInstructionRequiresAcknowledgement=false, bool participantPacedRest=false,
            bool retainControlAcrossBlocks=false)
        {
            if (String.IsNullOrWhiteSpace(sessionId) || allocation == null) throw new ArgumentException("Session identity required");
            if (questionnaireLanguage != "en") throw new ArgumentException("New sessions require the approved English questionnaire (en); historical language identifiers are not converted");
            if (Double.IsNaN(countdownSeconds) || Double.IsInfinity(countdownSeconds) || countdownSeconds <= 0)
                throw new ArgumentOutOfRangeException("countdownSeconds", "Explicit provisional countdown must be positive");
            if (Double.IsNaN(minimumRestSeconds) || Double.IsInfinity(minimumRestSeconds) || minimumRestSeconds < 0)
                throw new ArgumentOutOfRangeException("minimumRestSeconds", "Minimum rest duration must be finite and nonnegative");
            if (Double.IsNaN(questionnaireInstructionSeconds) || Double.IsInfinity(questionnaireInstructionSeconds) || questionnaireInstructionSeconds < 0)
                throw new ArgumentOutOfRangeException("questionnaireInstructionSeconds", "Questionnaire instruction duration must be finite and nonnegative");
            if (!Enum.IsDefined(typeof(RunKind), runKind)) throw new ArgumentException("Invalid run kind");
            SessionId = sessionId; Seed = seed; Allocation = allocation; RunKind = runKind;
            QuestionnaireLanguage = questionnaireLanguage; CountdownSeconds = countdownSeconds;
            MinimumRestSeconds = minimumRestSeconds; SkipFinalRest = skipFinalRest;
            QuestionnaireInstructionSeconds = questionnaireInstructionSeconds;
            QuestionnaireInstructionRequiresAcknowledgement = questionnaireInstructionRequiresAcknowledgement;
            ParticipantPacedRest = participantPacedRest;
            RetainControlAcrossBlocks = retainControlAcrossBlocks;
        }
        // A copy is configured before Experiment starts. Existing legacy preview remains unchanged.
        public static SessionConfiguration LegacyRecordingDevelopment(SessionConfiguration legacyConfiguration,
            ViveLegacyRecordingDesign design,ViveLegacyRecordingIntent intent)
        {
            if(legacyConfiguration==null||design==null||intent==null||legacyConfiguration.IsNamedDesign||
                legacyConfiguration.HasRawRecordingIntent||legacyConfiguration.RunKind!=RunKind.Simulation||
                !Object.ReferenceEquals(legacyConfiguration.Allocation,design.Allocation))
                throw new ArgumentException("Exact legacy configuration, design and intent are required.");
            var c=legacyConfiguration;
            var result=new SessionConfiguration(c.SessionId,c.Seed,c.Allocation,c.RunKind,c.QuestionnaireLanguage,c.CountdownSeconds,
                c.MinimumRestSeconds,c.SkipFinalRest,c.QuestionnaireInstructionSeconds,c.QuestionnaireInstructionRequiresAcknowledgement,
                c.ParticipantPacedRest,c.RetainControlAcrossBlocks);
            result.LegacyRecordingDesign=design;result.LegacyRecordingIntent=intent;return result;
        }
        // Single-block development preview only: it does not define a four-option
        // roster, establish research admission or create a legacy allocation.
        public static SessionConfiguration NamedAutomaticDevelopment(string sessionId,uint seed,ViveNamedOptionProvenance selection)
        {if(selection==null||selection.Option!=ViveNamedOption.Automatic)throw new ArgumentException("Named Automatic selection required");return new SessionConfiguration(sessionId,seed,selection);}
        public static SessionConfiguration NamedNoAvatarDevelopment(string sessionId,uint seed,ViveNamedOptionProvenance selection)
        {if(selection==null||selection.Option!=ViveNamedOption.NoAvatar)throw new ArgumentException("Named No Avatar selection required");return new SessionConfiguration(sessionId,seed,selection);}
        public static SessionConfiguration NamedPlanDevelopment(string sessionId,uint seed,ViveNamedSimulationPlan plan)
        {return new SessionConfiguration(sessionId,seed,plan);}
        // A different event schema, selected before Experiment emits session_created.
        // This factory itself does not start or authorize any recording.
        public static SessionConfiguration NamedRecordingDevelopment(string sessionId,uint seed,ViveNamedSimulationPlan plan,ViveNamedRecordingIntent intent)
        {if(intent==null)throw new ArgumentNullException("intent");return new SessionConfiguration(sessionId,seed,plan,intent);}
        SessionConfiguration(string sessionId,uint seed,ViveNamedSimulationPlan plan)
            :this(sessionId,seed,plan,null)
        {}
        SessionConfiguration(string sessionId,uint seed,ViveNamedSimulationPlan plan,ViveNamedRecordingIntent intent)
        {
            if(String.IsNullOrWhiteSpace(sessionId)||plan==null)throw new ArgumentException("Session and explicit named plan required");
            SessionId=sessionId;Seed=seed;NamedPlan=plan;NamedRecordingIntent=intent;RunKind=RunKind.Simulation;QuestionnaireLanguage="en";
            CountdownSeconds=3;MinimumRestSeconds=30;SkipFinalRest=true;QuestionnaireInstructionSeconds=0;
            QuestionnaireInstructionRequiresAcknowledgement=true;ParticipantPacedRest=true;RetainControlAcrossBlocks=false;
        }
        SessionConfiguration(string sessionId,uint seed,ViveNamedOptionProvenance selection)
        {
            if(String.IsNullOrWhiteSpace(sessionId)||selection==null)throw new ArgumentException("Session and explicit named-option selection required");
            if(selection.Option!=ViveNamedOption.Automatic&&selection.Option!=ViveNamedOption.NoAvatar)throw new InvalidOperationException("Named option unavailable");
            SessionId=sessionId;Seed=seed;NamedOption=selection;RunKind=RunKind.Simulation;QuestionnaireLanguage="en";
            CountdownSeconds=3;MinimumRestSeconds=30;SkipFinalRest=true;QuestionnaireInstructionSeconds=0;
            QuestionnaireInstructionRequiresAcknowledgement=true;ParticipantPacedRest=true;RetainControlAcrossBlocks=false;
        }
    }

    public sealed class TrialRecord
    {
        readonly object authority;
        public string TrialId { get; private set; }
        public string SessionId { get; private set; }
        public string ListId { get; private set; }
        public bool IsPractice { get; private set; }
        public int PracticeAttempt { get; private set; }
        public int BlockIndex { get; private set; }
        public int PlannedIndex { get; private set; }
        public int Segment { get; private set; }
        Condition? condition;
        public Condition? Condition { get {if(NamedOption!=null||NamedPlan!=null)throw new InvalidOperationException("Named-option trials do not have legacy conditions");return condition;} private set {condition=value;} }
        public ViveNamedOptionProvenance NamedOption {get;private set;}
        public ViveNamedSimulationPlan NamedPlan {get;private set;}
        public ViveNamedSimulationBlock NamedBlock {get;private set;}
        public Stimulus Stimulus { get; private set; }
        public double? Onset { get; private set; }
        public double? ResponseAt { get; private set; }
        public Colour? Response { get; private set; }
        public bool? Correct { get; private set; }
        public double? ReactionSeconds { get { return Outcome == TrialOutcome.Responded && Onset.HasValue ? ResponseAt - Onset : null; } }
        public TrialOutcome Outcome { get; private set; }
        public bool TechnicalIssue { get; private set; }
        // The instance is still shared with read-only consumers such as the
        // journal exporter. Only its originating Experiment holds the private
        // capability required to change this instance. This is in-process
        // ownership, not protection from reflection or hostile native code.
        // Preserve the previously implicit public parameterless constructor;
        // a detached record cannot be inserted into an Experiment's trial list.
        public TrialRecord(){authority=new object();}
        internal TrialRecord(object authority,string trialId,string sessionId,string listId,bool isPractice,int practiceAttempt,
            int blockIndex,int plannedIndex,int segment,Condition? condition,Stimulus stimulus,ViveNamedOptionProvenance namedOption=null,
            ViveNamedSimulationPlan namedPlan=null,ViveNamedSimulationBlock namedBlock=null)
        {
            if(authority==null)throw new ArgumentNullException("authority");
            if((namedOption!=null&&condition.HasValue)||(namedPlan==null&&namedBlock!=null))throw new ArgumentException("Invalid named-option trial provenance");
            if(namedPlan!=null&&(condition.HasValue||namedOption!=null||(!isPractice&&(namedBlock==null||!Object.ReferenceEquals(namedBlock.Plan,namedPlan)||namedBlock.Index!=blockIndex))||(isPractice&&namedBlock!=null)))
                throw new ArgumentException("Named plan trial requires its actual block or an unassigned practice");
            this.authority=authority;TrialId=trialId;SessionId=sessionId;ListId=listId;IsPractice=isPractice;
            PracticeAttempt=practiceAttempt;BlockIndex=blockIndex;PlannedIndex=plannedIndex;Segment=segment;Condition=condition;Stimulus=stimulus;NamedOption=namedOption;NamedPlan=namedPlan;NamedBlock=namedBlock;
            Outcome=TrialOutcome.Pending;
        }
        void RequireAuthority(object key)
        {if(!Object.ReferenceEquals(authority,key))throw new UnauthorizedAccessException("Trial belongs to another experiment");}
        internal void SetOnset(object key,double value){RequireAuthority(key);Onset=value;}
        internal void SetResponseAt(object key,double at){RequireAuthority(key);ResponseAt=at;}
        internal void SetResponse(object key,Colour response){RequireAuthority(key);Response=response;}
        internal void SetCorrect(object key,bool correct){RequireAuthority(key);Correct=correct;}
        internal void SetResponded(object key){RequireAuthority(key);Outcome=TrialOutcome.Responded;}
        internal void SetInterrupted(object key){RequireAuthority(key);Outcome=TrialOutcome.Interrupted;}
        internal void SetTechnicalIssue(object key){RequireAuthority(key);TechnicalIssue=true;}
    }

    public sealed class JournalEvent
    {
        public long Sequence { get; private set; }
        public double Time { get; private set; }
        public string ClockId { get; private set; }
        public string SessionId { get; private set; }
        public int BlockIndex { get; private set; }
        public int Segment { get; private set; }
        public string TrialId { get; private set; }
        public string Kind { get; private set; }
        public ViveNamedOptionProvenance NamedOption {get;private set;}
        public ViveNamedSimulationPlan NamedPlan {get;private set;}
        public ViveNamedSimulationBlock NamedBlock {get;private set;}
        public ViveNamedRecordingIntent NamedRecordingIntent {get;private set;}
        public ViveLegacyRecordingDesign LegacyRecordingDesign {get;private set;}
        public ViveLegacyRecordingIntent LegacyRecordingIntent {get;private set;}
        public IReadOnlyDictionary<string, string> Fields { get; private set; }
        // Old serializers cannot represent the typed value. Reserved keys also
        // catch a plain legacy copy of a named event whose typed value was lost.
        // This is not authentication against malicious same-process callers.
        public bool CarriesLegacyRawProvenance
        {get{if(LegacyRecordingDesign!=null||LegacyRecordingIntent!=null)return true;foreach(var key in Fields.Keys)if(key.StartsWith("legacy_",StringComparison.OrdinalIgnoreCase))return true;return false;}}
        public bool CarriesNamedProvenance
        {get{if(NamedOption!=null||NamedPlan!=null)return true;foreach(var key in Fields.Keys)if(key.StartsWith("named_",StringComparison.OrdinalIgnoreCase))return true;return false;}}
        public JournalEvent(long sequence, double time, string clockId, string sessionId, int blockIndex,
            int segment, string trialId, string kind, IDictionary<string,string> fields)
            :this(sequence,time,clockId,sessionId,blockIndex,segment,trialId,kind,fields,null)
        {}
        internal JournalEvent(long sequence,double time,string clockId,string sessionId,int blockIndex,
            int segment,string trialId,string kind,IDictionary<string,string> fields,ViveNamedOptionProvenance namedOption,
            ViveNamedSimulationPlan namedPlan=null,ViveNamedSimulationBlock namedBlock=null,ViveNamedRecordingIntent namedRecordingIntent=null,
            ViveLegacyRecordingDesign legacyRecordingDesign=null,ViveLegacyRecordingIntent legacyRecordingIntent=null)
        {
            if(namedRecordingIntent!=null&&namedPlan==null)throw new ArgumentException("Recording intent requires a named simulation plan");
            if((legacyRecordingDesign==null)!=(legacyRecordingIntent==null)||
                (legacyRecordingDesign!=null&&(namedOption!=null||namedPlan!=null||namedBlock!=null||namedRecordingIntent!=null)))
                throw new ArgumentException("Recording designs cannot be mixed.");
            LegacyRecordingDesign=legacyRecordingDesign;LegacyRecordingIntent=legacyRecordingIntent;
            Sequence=sequence; Time=time; ClockId=clockId; SessionId=sessionId; BlockIndex=blockIndex;
            Segment=segment; TrialId=trialId; Kind=kind;
            Fields=new ReadOnlyDictionary<string,string>(new Dictionary<string,string>(fields));NamedOption=namedOption;NamedPlan=namedPlan;NamedBlock=namedBlock;NamedRecordingIntent=namedRecordingIntent;
        }
    }

    public sealed class RatingRecord
    {
        public int BlockIndex { get; private set; }
        public string ItemId { get; private set; }
        public int Value { get; private set; }
        public double Time { get; private set; }
        public string Language { get; private set; }
        public string Version { get; private set; }
        // Preserve the previously implicit public parameterless constructor.
        // Experiment-owned ratings are constructed once and never mutated.
        public RatingRecord(){}
        internal RatingRecord(int blockIndex,string itemId,int value,double time,string language,string version)
        {BlockIndex=blockIndex;ItemId=itemId;Value=value;Time=time;Language=language;Version=version;}
    }

    public sealed class TaskView
    {
        public Phase Phase { get; internal set; }
        public int BlockIndex {get;internal set;}
        public int Segment {get;internal set;}
        public string TrialId { get; internal set; }
        public Stimulus Stimulus { get; internal set; }
        public bool OperatorAvatarVisible { get; internal set; }
        public bool ParticipantAvatarVisible { get; internal set; }
        public bool OperatorViewProvisional { get; internal set; }
        public string PracticeCorrection { get; internal set; }
        public string QuestionnaireItem { get; internal set; }
        public bool ReadyEnabled { get; internal set; }
        public bool SourceContinuationPending { get; internal set; }
        public string SourceLabel { get; internal set; }
    }
}
