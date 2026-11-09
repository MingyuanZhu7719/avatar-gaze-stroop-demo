using System;
using System.Collections.Generic;
using System.Globalization;

namespace AvatarStroop.Core
{
    // Engine-independent state machine. Callers acknowledge actual presentation, not merely a planned frame.
    public sealed partial class Experiment
    {
        readonly IClock clock;
        readonly object trialAuthority = new object();
        readonly List<JournalEvent> events = new List<JournalEvent>();
        readonly List<TrialRecord> trials = new List<TrialRecord>();
        readonly List<RatingRecord> ratings = new List<RatingRecord>();
        readonly HashSet<Colour> held = new HashSet<Colour>();
        readonly Stimulus[][] lists = new Stimulus[4][];
        Stimulus[] practice;
        Phase beforePause;
        bool resumeCountdown,questionnaireInstructionPending;
        // Enabled only by the participant adapter after a named raw admission
        // actually attaches. A software render witness is not an HMD/optical
        // presentation or a durable journal receipt.
        bool rawCountdownWitnessRequired,rawCountdownWitnessPending;
        double rawCountdownRemaining;
        bool namedOptionConfirmed,namedPlanBlockStarted,namedSoftwareChoicesRecorded;
        double lastTime, countdownAt, restAt, ratingAt, pausedAt, activeAccum;
        double? activeSince;
        int trialIndex, practiceCorrect, questionIndex;
        TrialRecord current;
        string sourceConfirmationOwner;
        long sourceConfirmationSequence;

        public SessionConfiguration Configuration { get; private set; }
        public Phase Phase { get; private set; }
        public int BlockIndex { get; private set; } = -1;
        public int Segment { get; private set; }
        public int PracticeAttempt { get; private set; }
        public int PracticeCorrect { get { return practiceCorrect; } }
        public int QuestionIndex { get { return questionIndex; } }
        public TrialRecord CurrentTrial { get { return current; } }
        public EndReason? EndReason { get; private set; }
        public ControlSelection? LockedControl { get; private set; }
        public bool NamedOptionConfirmed {get{return Configuration.IsNamedDesign&&namedOptionConfirmed;}}
        public ViveNamedSimulationBlock CurrentNamedPlanBlock {get{return Configuration.NamedPlan!=null&&BlockIndex>=0&&BlockIndex<Configuration.NamedPlan.Blocks.Count?Configuration.NamedPlan.Blocks[BlockIndex]:null;}}
        public ViveNamedOptionProvenance CurrentNamedSelection {get{return Configuration.NamedPlan!=null?(CurrentNamedPlanBlock==null?null:CurrentNamedPlanBlock.Selection):Configuration.NamedOption;}}
        public IReadOnlyList<JournalEvent> Events { get { return events.AsReadOnly(); } }
        public IReadOnlyList<TrialRecord> Trials { get { return trials.AsReadOnly(); } }
        public IReadOnlyList<RatingRecord> Ratings { get { return ratings.AsReadOnly(); } }
        public Condition? CurrentCondition { get { if(Configuration.IsNamedDesign)throw new InvalidOperationException("Named-option sessions do not expose legacy conditions");return BlockIndex >= 0 && BlockIndex < 4 ? (Condition?)Configuration.Allocation.Order[BlockIndex] : null; } }
        public double ActiveSeconds { get { return activeAccum + (activeSince.HasValue ? Time() - activeSince.Value : 0); } }
        public double RestSeconds { get { return InPhaseOrPaused(Phase.Rest) ? PhaseTime()-restAt : 0; } }
        public double CountdownRemainingSeconds { get { return rawCountdownWitnessPending&&InPhaseOrPaused(Phase.Countdown)?rawCountdownRemaining:Remaining(Phase.Countdown,countdownAt,Configuration.CountdownSeconds); } }
        internal bool RawCountdownAwaitingSoftwareWitness {get{return rawCountdownWitnessRequired&&rawCountdownWitnessPending&&Phase==Phase.Countdown;}}
        public double RestRemainingSeconds { get { return Remaining(Phase.Rest,restAt,Configuration.MinimumRestSeconds); } }
        public double QuestionnaireInstructionRemainingSeconds { get { return Configuration.QuestionnaireInstructionRequiresAcknowledgement?0:Remaining(Phase.Questionnaire,ratingAt,Configuration.QuestionnaireInstructionSeconds); } }
        public bool QuestionnaireInstructionPending { get { return InPhaseOrPaused(Phase.Questionnaire)&&questionnaireInstructionPending&&
            (Configuration.QuestionnaireInstructionRequiresAcknowledgement||PhaseTime()<ratingAt+Configuration.QuestionnaireInstructionSeconds); } }
        public bool QuestionnaireInputReady { get { return Phase==Phase.Questionnaire&&(Configuration.QuestionnaireInstructionRequiresAcknowledgement?
            !questionnaireInstructionPending:Time()>=ratingAt+Configuration.QuestionnaireInstructionSeconds); } }
        public bool ReadyEnabled { get { return Phase == Phase.Rest && (Configuration.ParticipantPacedRest||Time() >= restAt + Configuration.MinimumRestSeconds); } }
        public bool RetainedSourceContinuationPending { get { return BlockIndex>0&&Phase==Phase.BlockReady&&((Configuration.NamedPlan!=null&&namedOptionConfirmed)||(Configuration.RetainControlAcrossBlocks&&LockedControl.HasValue)); } }

        public Experiment(SessionConfiguration configuration, IClock clock)
        {
            if(configuration == null || clock == null || String.IsNullOrWhiteSpace(clock.ClockId)) throw new ArgumentException("Configuration and named clock required");
            if(configuration.RunKind != RunKind.Simulation)
                throw new InvalidOperationException("Apparatus sessions are unavailable: real head/eye adapters, approved fault/recovery policy, calibration and timing validation are not complete.");
            Configuration=configuration; this.clock=clock; lastTime=clock.Now;
            if(Double.IsNaN(lastTime) || Double.IsInfinity(lastTime) || lastTime < 0) throw new ArgumentException("Invalid clock");
            for(int i=0;i<4;i++) lists[i]=TrialPlans.MainList(configuration.Seed,i);
            Phase=Phase.Preparation;
            if(configuration.NamedPlan!=null)
                Log("session_created","run_kind","Simulation","protocol",configuration.NamedRecordingIntent==null?ViveNamedSimulationPlan.SchemaVersion:ViveNamedRecordingIntent.EventSchemaVersion,
                    "software",SessionConfiguration.SoftwareVersion,"seed",configuration.Seed.ToString(CultureInfo.InvariantCulture),
                    "plan_id",configuration.NamedPlan.PlanId,"planned_blocks",configuration.NamedPlan.Blocks.Count.ToString(CultureInfo.InvariantCulture),
                    "scope","explicit_plan_simulation_only","questionnaire_language",configuration.QuestionnaireLanguage);
            else if(configuration.NamedOption!=null)
                Log("session_created","run_kind",configuration.RunKind.ToString(),"protocol","vive-named-preview-v1",
                    "software",SessionConfiguration.SoftwareVersion,"seed",configuration.Seed.ToString(CultureInfo.InvariantCulture),
                    "list_index",configuration.NamedOption.ListIndex.ToString(CultureInfo.InvariantCulture),
                    "scope","single_block_simulation_only","target_validation",configuration.NamedOption.Option==ViveNamedOption.NoAvatar?
                        ViveNamedOptionProvenance.NoAvatarNotApplicable:"unvalidated",
                    "countdown_seconds",Number(configuration.CountdownSeconds),"questionnaire_language",configuration.QuestionnaireLanguage);
            else Log("session_created", "run_kind", configuration.RunKind.ToString(), "protocol", SessionConfiguration.ProtocolVersion,
                "software", SessionConfiguration.SoftwareVersion, "allocation", configuration.Allocation.AllocationId,
                "order", String.Join("", Array.ConvertAll(configuration.Allocation.Order, x=>x.ToString())),
                "b_initial_target", configuration.Allocation.BInitialTarget.ToString(), "seed", configuration.Seed.ToString(CultureInfo.InvariantCulture),
                "list_by_block", String.Join(",", configuration.Allocation.ListByBlock),
                "countdown_seconds", Number(configuration.CountdownSeconds), "questionnaire_language", configuration.QuestionnaireLanguage,
                "minimum_rest_seconds",Number(configuration.MinimumRestSeconds),"skip_final_rest",configuration.SkipFinalRest.ToString(),
                "questionnaire_instruction_seconds",Number(configuration.QuestionnaireInstructionSeconds),
                "questionnaire_instruction_requires_acknowledgement",configuration.QuestionnaireInstructionRequiresAcknowledgement.ToString(),
                "rest_policy",configuration.ParticipantPacedRest?"participant_continue_no_minimum_no_automatic_advance":"minimum_duration_then_explicit_continue",
                "source_lock_policy",configuration.RetainControlAcrossBlocks?"explicit_once_after_practice_retained_for_session":"explicit_per_block",
                "presentation_policy",configuration.QuestionnaireInstructionRequiresAcknowledgement||configuration.ParticipantPacedRest||configuration.RetainControlAcrossBlocks?
                    "participant-paced-session-preview-v2":"configured-preview-flow-v1",
                "questionnaire_duration_policy","phase_elapsed_including_instructions_excluding_explicit_pauses");
        }

        public void RecordPreparation(Actor actor, string field, string status)
        {
            if(mutationJournal==null&&incrementalMutationJournal==null){RecordPreparationCore(actor,field,status);return;}
            BeginMutation("RecordPreparation");bool success=false;
            try{RecordPreparationCore(actor,field,status);success=true;}finally{EndMutation(success);}
        }
        // Appended only after raw admission and before any trial starts. The
        // two earlier times are software input polls on this task clock; the
        // event time is when the declaration was made, not a disk receipt.
        internal void RecordNamedSoftwareChoices(string clockId,double taskPoll,double eyePoll)
        {if(Configuration.NamedRecordingIntent==null)throw new InvalidOperationException("Named recording context required.");RecordRawSoftwareChoices(clockId,taskPoll,eyePoll);}
        internal void RecordRawSoftwareChoices(string clockId,double taskPoll,double eyePoll)
        {
            if(mutationJournal!=null||incrementalMutationJournal!=null)
                throw new InvalidOperationException("Named software choice provenance cannot use legacy observation.");
            Require(Phase.Preparation);
            if(!Configuration.HasRawRecordingIntent||namedSoftwareChoicesRecorded||
                !String.Equals(clockId,clock.ClockId,StringComparison.Ordinal)||
                Double.IsNaN(taskPoll)||Double.IsInfinity(taskPoll)||taskPoll<0||
                Double.IsNaN(eyePoll)||Double.IsInfinity(eyePoll)||eyePoll<taskPoll||eyePoll>Time())
                throw new InvalidOperationException("Software choice provenance is unavailable.");
            namedSoftwareChoicesRecorded=true;
            Log("participant_recording_software_choices",
                "task_choice_poll_seconds",Number(taskPoll),"eye_choice_poll_seconds",Number(eyePoll),
                "choice_clock_id",clockId,"choice_time_basis","controller_poll_not_hardware_event",
                "identity_or_study_consent","NOT_AUTHENTICATED");
        }
        void RecordPreparationCore(Actor actor, string field, string status)
        {
            RequireOwner(actor); Require(Phase.Preparation, Phase.Familiarization, Phase.BlockPreparation);
            if(String.IsNullOrWhiteSpace(field) || String.IsNullOrWhiteSpace(status)) throw new ArgumentException("Explicit preparation record required");
            Log("preparation_record", "field", field, "status", status);
        }
        public void BeginFamiliarization(Actor actor)
        {
            if(mutationJournal==null&&incrementalMutationJournal==null){BeginFamiliarizationCore(actor);return;}
            BeginMutation("BeginFamiliarization");bool success=false;
            try{BeginFamiliarizationCore(actor);success=true;}finally{EndMutation(success);}
        }
        void BeginFamiliarizationCore(Actor actor)
        {
            RequireOwner(actor); Require(Phase.Preparation); Move(Phase.Familiarization);
        }
        public void BeginPractice(Actor actor)
        {
            if(mutationJournal==null&&incrementalMutationJournal==null){BeginPracticeCore(actor);return;}
            BeginMutation("BeginPractice");bool success=false;
            try{BeginPracticeCore(actor);success=true;}finally{EndMutation(success);}
        }
        void BeginPracticeCore(Actor actor)
        {
            RequireOwner(actor); Require(Phase.Familiarization, Phase.PracticeRetry);
            // A named recording candidate can prepare the exact next attempt
            // before entering practice. This does not itself authorize start.
            var preparedEvent=!Configuration.HasRawRecordingIntent?null:FindNamedRecordingPlanEvent(true,PracticeAttempt+1);
            if(preparedEvent!=null&&!MatchesRawRecordingBlockPlan(preparedEvent))
                throw new InvalidOperationException("Prepared practice plan no longer matches the task.");
            bool prepared=preparedEvent!=null;
            PracticeAttempt++; TraceMutation(); practiceCorrect=0; TraceMutation(); trialIndex=0; TraceMutation();
            if(!prepared)practice=TrialPlans.Practice(Configuration.Seed,PracticeAttempt); TraceMutation();
            Log("practice_started", "attempt", PracticeAttempt.ToString());
            Move(Phase.Practice); CreateTrial(true);
        }

        // Named-recording plan preparation only: no file I/O, consent,
        // durability or start authority. The caller must fence this mutation,
        // verify the event against this Experiment and obtain its own receipt
        // before allowing the corresponding practice/block to begin.
        internal JournalEvent PrepareNamedRecordingBlockPlan(Actor actor,bool practiceScope)
        {
            if(Configuration.NamedRecordingIntent==null)throw new InvalidOperationException("Named recording context required.");
            return PrepareRawRecordingBlockPlan(actor,practiceScope);
        }
        internal bool MatchesNamedRecordingBlockPlan(JournalEvent prepared)
        {return Configuration.NamedRecordingIntent!=null&&MatchesRawRecordingBlockPlan(prepared);}
        internal JournalEvent PrepareRawRecordingBlockPlan(Actor actor,bool practiceScope)
        {
            if(mutationJournal==null&&incrementalMutationJournal==null)return PrepareRawRecordingBlockPlanCore(actor,practiceScope);
            BeginMutation("PrepareRawRecordingBlockPlan");bool success=false;
            try{var result=PrepareRawRecordingBlockPlanCore(actor,practiceScope);success=true;return result;}finally{EndMutation(success);}
        }
        JournalEvent PrepareRawRecordingBlockPlanCore(Actor actor,bool practiceScope)
        {
            RequireOwner(actor);
            if(!Configuration.HasRawRecordingIntent)
                throw new InvalidOperationException("Named recording plan preparation is unavailable.");
            int attempt;Stimulus[] items;string list;
            if(practiceScope)
            {
                Require(Phase.Preparation,Phase.Familiarization,Phase.PracticeRetry);
                if(BlockIndex!=-1||PracticeAttempt<0||PracticeAttempt>=2)throw new InvalidOperationException("Next practice attempt is unavailable.");
                attempt=PracticeAttempt+1;list="practice-"+Configuration.Seed+"-"+attempt;
            }
            else
            {
                Require(Phase.BlockReady);
                if(Configuration.LegacyRecordingIntent!=null?(!LockedControl.HasValue||BlockIndex<0||BlockIndex>=4||namedPlanBlockStarted):(CurrentNamedPlanBlock==null||!namedOptionConfirmed||namedPlanBlockStarted))
                    throw new InvalidOperationException("Next named block is unavailable.");
                attempt=0;list=TrialPlans.MainListId(Configuration.Seed,RecordingListIndex);
            }
            // Never silently replace a previously published plan for this
            // attempt/block, including one that no longer matches the task.
            JournalEvent prior=FindNamedRecordingPlanEvent(practiceScope,attempt);
            if(prior!=null)
            {if(!MatchesRawRecordingBlockPlan(prior))throw new InvalidOperationException("Prepared plan no longer matches the task.");return prior;}
            if(practiceScope){practice=TrialPlans.Practice(Configuration.Seed,attempt);TraceMutation();items=practice;}
            else items=lists[RecordingListIndex];
            if(items==null||items.Length!=(practiceScope?8:24))throw new InvalidOperationException("Stimulus plan is unavailable.");
            var fields=new List<string>{"recording_plan_schema","1","plan_scope",practiceScope?"PRACTICE":"MAIN",
                "attempt",attempt.ToString(CultureInfo.InvariantCulture),"list",list,"item_count",items.Length.ToString(CultureInfo.InvariantCulture)};
            for(int i=0;i<items.Length;i++)
            {
                if(items[i]==null)throw new InvalidOperationException("Stimulus plan is unavailable.");
                string prefix="item_"+i.ToString("D2",CultureInfo.InvariantCulture);
                fields.Add(prefix+"_word");fields.Add(items[i].Word.ToString());fields.Add(prefix+"_ink");fields.Add(items[i].Ink.ToString());
            }
            Log("block_plan_prepared",fields.ToArray());return events[events.Count-1];
        }
        JournalEvent FindNamedRecordingPlanEvent(bool practiceScope,int attempt)
        {
            for(int i=events.Count-1;i>=0;i--)
            {
                var e=events[i];string scope,a;
                if(e.Kind=="block_plan_prepared"&&e.Fields.TryGetValue("plan_scope",out scope)&&
                    e.Fields.TryGetValue("attempt",out a)&&scope==(practiceScope?"PRACTICE":"MAIN")&&
                    a==attempt.ToString(CultureInfo.InvariantCulture)&&(practiceScope?e.BlockIndex==-1:e.BlockIndex==BlockIndex))return e;
            }
            return null;
        }
        // Instantaneous comparison against the exact event object and the
        // Experiment's current arrays, not a durable receipt or a lease.
        internal bool MatchesRawRecordingBlockPlan(JournalEvent prepared)
        {
            if(prepared==null||!Configuration.HasRawRecordingIntent||prepared.Kind!="block_plan_prepared"||
                !Object.ReferenceEquals(prepared.LegacyRecordingDesign,Configuration.LegacyRecordingDesign)||!Object.ReferenceEquals(prepared.LegacyRecordingIntent,Configuration.LegacyRecordingIntent)||
                prepared.Sequence<0||prepared.Sequence>=events.Count||!Object.ReferenceEquals(events[(int)prepared.Sequence],prepared)||
                !Object.ReferenceEquals(prepared.NamedPlan,Configuration.NamedPlan)||!Object.ReferenceEquals(prepared.NamedRecordingIntent,Configuration.NamedRecordingIntent)||
                !String.Equals(prepared.SessionId,Configuration.SessionId,StringComparison.Ordinal)||!String.Equals(prepared.ClockId,clock.ClockId,StringComparison.Ordinal)||
                prepared.TrialId!=null)return false;
            string schema,scope,attemptText,list,count;
            if(!prepared.Fields.TryGetValue("recording_plan_schema",out schema)||schema!="1"||
                !prepared.Fields.TryGetValue("plan_scope",out scope)||!prepared.Fields.TryGetValue("attempt",out attemptText)||
                !prepared.Fields.TryGetValue("list",out list)||!prepared.Fields.TryGetValue("item_count",out count))return false;
            Stimulus[] items;int attempt;
            if(scope=="PRACTICE")
            {
                if((Phase!=Phase.Preparation&&Phase!=Phase.Familiarization&&Phase!=Phase.PracticeRetry)||BlockIndex!=-1||
                    prepared.BlockIndex!=-1||prepared.NamedBlock!=null||PracticeAttempt<0||PracticeAttempt>=2)return false;
                attempt=PracticeAttempt+1;items=practice;
                if(list!="practice-"+Configuration.Seed+"-"+attempt)return false;
            }
            else if(scope=="MAIN")
            {
                if(Phase!=Phase.BlockReady||(Configuration.LegacyRecordingIntent!=null?(!LockedControl.HasValue||BlockIndex<0||BlockIndex>=4):(CurrentNamedPlanBlock==null||!namedOptionConfirmed))||namedPlanBlockStarted||
                    prepared.BlockIndex!=BlockIndex||!Object.ReferenceEquals(prepared.NamedBlock,CurrentNamedPlanBlock))return false;
                attempt=0;items=lists[RecordingListIndex];
                if(list!=TrialPlans.MainListId(Configuration.Seed,RecordingListIndex))return false;
            }
            else return false;
            if(items==null||items.Length!=(scope=="PRACTICE"?8:24)||count!=items.Length.ToString(CultureInfo.InvariantCulture)||
                attemptText!=attempt.ToString(CultureInfo.InvariantCulture))return false;
            for(int i=0;i<items.Length;i++)
            {
                string word,ink,prefix="item_"+i.ToString("D2",CultureInfo.InvariantCulture);
                if(items[i]==null||!prepared.Fields.TryGetValue(prefix+"_word",out word)||word!=items[i].Word.ToString()||
                    !prepared.Fields.TryGetValue(prefix+"_ink",out ink)||ink!=items[i].Ink.ToString())return false;
            }
            return true;
        }

        int RecordingListIndex {get{return Configuration.LegacyRecordingIntent!=null?Configuration.Allocation.ListByBlock[BlockIndex]:CurrentNamedSelection.ListIndex;}}

        public void LockBlock(Actor actor, ControlSelection selection, string ownerId)
        {
            if(mutationJournal==null&&incrementalMutationJournal==null){LockBlockCore(actor,selection,ownerId);return;}
            BeginMutation("LockBlock");bool success=false;
            try{LockBlockCore(actor,selection,ownerId);success=true;}finally{EndMutation(success);}
        }
        void LockBlockCore(Actor actor, ControlSelection selection, string ownerId)
        {
            RequireOwner(actor); Require(Phase.BlockPreparation);
            if(Configuration.NamedPlan!=null)throw new InvalidOperationException("Confirm the complete named plan instead of locking one source");
            if(String.IsNullOrWhiteSpace(ownerId) || !Enum.IsDefined(typeof(ControlSelection),selection)) throw new ArgumentException("Owner and source required");
            if(Configuration.NamedOption!=null && (Configuration.NamedOption.Option!=ViveNamedOption.Automatic||selection!=ControlSelection.Automatic))
                throw new InvalidOperationException("Named Automatic preview requires the Automatic source; No Avatar has no control source");
            if(selection == ControlSelection.SimulatorRehearsal && Configuration.RunKind != RunKind.Simulation)
                throw new InvalidOperationException("Simulator data cannot enter apparatus sessions");
            if(Configuration.RetainControlAcrossBlocks&&(BlockIndex!=0||LockedControl.HasValue))
                throw new InvalidOperationException("The session source is confirmed once after practice and retained for all four blocks");
            LockedControl=selection;TraceMutation();
            if(Configuration.NamedOption!=null)namedOptionConfirmed=true;
            sourceConfirmationOwner=ownerId;TraceMutation();
            sourceConfirmationSequence=events.Count;TraceMutation();
            if(Configuration.RetainControlAcrossBlocks)
                Log("session_source_confirmed","owner",ownerId,"selection",selection.ToString(),"scope","all_four_blocks");
            RecordBlockSource(false);
            Move(Phase.BlockReady);
        }

        // No-avatar is a named condition without an avatar control source.
        // It must not be represented as legacy D or as Automatic control.
        public void ConfirmNamedNoAvatar(Actor actor,string ownerId)
        {
            RequireOwner(actor);Require(Phase.BlockPreparation);
            if(Configuration.NamedPlan!=null)throw new InvalidOperationException("Confirm the complete named plan instead of one block");
            if(Configuration.NamedOption==null||Configuration.NamedOption.Option!=ViveNamedOption.NoAvatar)
                throw new InvalidOperationException("This session is not the named No Avatar preview");
            if(String.IsNullOrWhiteSpace(ownerId)||namedOptionConfirmed||LockedControl.HasValue)
                throw new ArgumentException("Explicit owner and unconfirmed named option required");
            namedOptionConfirmed=true;sourceConfirmationOwner=ownerId;sourceConfirmationSequence=events.Count;
            Log("named_option_confirmed","owner",ownerId,"source",ViveNamedOptionProvenance.NoAvatarNotApplicable);
            RecordBlockSource(false);Move(Phase.BlockReady);
        }

        public void ConfirmNamedPlan(Actor actor,string ownerId)
        {
            RequireOwner(actor);Require(Phase.BlockPreparation);
            var plan=Configuration.NamedPlan;
            if(plan==null||BlockIndex!=0||namedOptionConfirmed||String.IsNullOrWhiteSpace(ownerId))
                throw new InvalidOperationException("Confirm the complete named plan once after practice");
            namedOptionConfirmed=true;sourceConfirmationOwner=ownerId;sourceConfirmationSequence=events.Count;
            Log("named_plan_confirmed","owner",ownerId,"plan_id",plan.PlanId,"planned_blocks",plan.Blocks.Count.ToString(CultureInfo.InvariantCulture));
            foreach(var block in plan.Blocks)
                Log("named_plan_item_confirmed","plan_index",block.Index.ToString(CultureInfo.InvariantCulture),
                    "option",block.Selection.Option.ToString(),"list_index",block.Selection.ListIndex.ToString(CultureInfo.InvariantCulture),
                    "schedule",block.Selection.ScheduleVersion,"geometry",block.Selection.GeometryVersion);
            SetNamedPlanBlockSource();RecordBlockSource(false);Move(Phase.BlockReady);
        }
        void SetNamedPlanBlockSource()
        {
            if(Configuration.NamedPlan==null||!namedOptionConfirmed||CurrentNamedPlanBlock==null)throw new InvalidOperationException("No confirmed named plan block");
            LockedControl=CurrentNamedPlanBlock.Selection.Option==ViveNamedOption.Automatic?(ControlSelection?)ControlSelection.Automatic:null;
        }

        // Source readiness is an adapter result, not an assertion of hardware validation.
        public void BeginCountdown(Actor actor, bool sourceReady)
        {
            if(mutationJournal==null&&incrementalMutationJournal==null){BeginCountdownCore(actor,sourceReady);return;}
            BeginMutation("BeginCountdown");bool success=false;
            try{BeginCountdownCore(actor,sourceReady);success=true;}finally{EndMutation(success);}
        }
        void BeginCountdownCore(Actor actor, bool sourceReady)
        {
            RequireOwner(actor); Require(Phase.BlockReady);
            if(!sourceReady) throw new InvalidOperationException("Selected source is unavailable or not revalidated");
            countdownAt=Time();TraceMutation(); resumeCountdown=false;TraceMutation();
            if(rawCountdownWitnessRequired){rawCountdownWitnessPending=true;rawCountdownRemaining=Configuration.CountdownSeconds;TraceMutation();}
            Move(Phase.Countdown);
            if(rawCountdownWitnessPending)Log("countdown_waiting_for_software_witness","basis","named_raw_software_render_not_optical");
        }

        // The adapter may enable this only after an actual named raw admission
        // has attached, before the session has left preparation.
        internal void RequireRawCountdownSoftwareWitness()
        {
            if(Phase!=Phase.Preparation||!Configuration.HasRawRecordingIntent||
                !Configuration.RawTaskRequested||!Configuration.RawEyeRequested||rawCountdownWitnessRequired)
                throw new InvalidOperationException("Named raw countdown witness unavailable.");
            rawCountdownWitnessRequired=true;
        }
        // Values are supplied by the caller and remain explicitly declared;
        // the Unity host must check its camera, phase, frame and revision.
        internal bool TryStartRawCountdownFromSoftwareWitness(int frame,long revision,double unitySeconds)
        {
            if(!RawCountdownAwaitingSoftwareWitness||frame<0||revision<0||Double.IsNaN(unitySeconds)||Double.IsInfinity(unitySeconds)||unitySeconds<0)
                return false;
            if(mutationJournal==null&&incrementalMutationJournal==null)return StartRawCountdownFromSoftwareWitnessCore(frame,revision,unitySeconds);
            BeginMutation("StartRawCountdownFromSoftwareWitness");bool success=false;
            try{bool result=StartRawCountdownFromSoftwareWitnessCore(frame,revision,unitySeconds);success=true;return result;}
            finally{EndMutation(success);}
        }
        bool StartRawCountdownFromSoftwareWitnessCore(int frame,long revision,double unitySeconds)
        {
            if(!RawCountdownAwaitingSoftwareWitness)return false;
            double now=Time();
            countdownAt=now-(Configuration.CountdownSeconds-rawCountdownRemaining);TraceMutation();
            rawCountdownWitnessPending=false;TraceMutation();
            Log("countdown_software_witness","task_start_seconds",Number(now),"task_clock_id",clock.ClockId,
                "remaining_seconds",Number(rawCountdownRemaining),"unity_proxy_seconds",Number(unitySeconds),
                "unity_proxy_frame",frame.ToString(CultureInfo.InvariantCulture),"unity_proxy_revision",revision.ToString(CultureInfo.InvariantCulture),
                "basis","caller_declared_software_render_not_optical");
            return true;
        }
        // The adapter must supply current readiness for this block and segment. A
        // retained choice never grants readiness and never clears a recovery gate.
        public bool TryBeginRetainedBlockCountdown(bool sourceReady)
        {
            if(mutationJournal==null&&incrementalMutationJournal==null)return TryBeginRetainedBlockCountdownCore(sourceReady);
            BeginMutation("TryBeginRetainedBlockCountdown");bool success=false;
            try{bool result=TryBeginRetainedBlockCountdownCore(sourceReady);success=true;return result;}finally{EndMutation(success);}
        }
        bool TryBeginRetainedBlockCountdownCore(bool sourceReady)
        {
            if(!RetainedSourceContinuationPending||!sourceReady)return false;
            Log("retained_source_countdown_started","selection",LockedControl.HasValue?LockedControl.Value.ToString():ViveNamedOptionProvenance.NoAvatarNotApplicable,
                "confirmation_event_sequence",sourceConfirmationSequence.ToString(CultureInfo.InvariantCulture));
            BeginCountdownCore(Actor.Owner,true);return true;
        }
        public void Tick()
        {
            if(mutationJournal==null&&incrementalMutationJournal==null){TickCore();return;}
            BeginMutation("Tick");bool success=false;
            try{TickCore();success=true;}finally{EndMutation(success);}
        }
        void TickCore()
        {
            double now=Time();
            CompleteQuestionnaireInstruction(now);
            if(Phase == Phase.Countdown && !rawCountdownWitnessPending && now >= countdownAt+Configuration.CountdownSeconds)
            {
                if(!resumeCountdown) { trialIndex=0;TraceMutation(); Segment=0;TraceMutation(); activeAccum=0;TraceMutation(); activeSince=null;TraceMutation(); if(Configuration.NamedPlan!=null||Configuration.LegacyRecordingIntent!=null)namedPlanBlockStarted=true; }
                Move(Phase.MainTask); CreateTrial(false);
            }
        }

        public void MarkPresented(string trialId)
        {
            if(mutationJournal==null&&incrementalMutationJournal==null){MarkPresentedCore(trialId);return;}
            BeginMutation("MarkPresented");bool success=false;
            try{MarkPresentedCore(trialId);success=true;}finally{EndMutation(success);}
        }
        // VIVE named recording candidate only. This declaration uses two
        // independent clocks and is not proof that the HMD displayed pixels.
        // The public/Quest path continues to emit its original event fields.
        internal void MarkPresentedWithSoftwareProxy(string trialId,double unitySeconds,int frame,long revision)
        {
            if(Configuration.RunKind!=RunKind.Simulation||!Configuration.HasRawRecordingIntent||
                !Configuration.RawTaskRequested||!Configuration.RawEyeRequested||
                Double.IsNaN(unitySeconds)||Double.IsInfinity(unitySeconds)||unitySeconds<0||frame<0||revision<0||
                mutationJournal!=null||incrementalMutationJournal!=null)
                throw new InvalidOperationException("Named software presentation declaration is unavailable.");
            MarkPresentedCore(trialId,unitySeconds,frame,revision);
        }
#if VIVE_SYNTHETIC_BATCH_FIXTURE
        internal Action SyntheticAfterOnsetForFixture;
#endif
        void MarkPresentedCore(string trialId){MarkPresentedCore(trialId,null,0,0);}
        void MarkPresentedCore(string trialId,double? unityProxySeconds,int frame,long revision)
        {
            Require(Phase.Practice, Phase.MainTask);
            if(current == null || trialId != current.TrialId) throw new InvalidOperationException("Presentation acknowledges only the current trial");
            if(current.Onset.HasValue) return;
            current.SetOnset(trialAuthority,Time());TraceMutation();
#if VIVE_SYNTHETIC_BATCH_FIXTURE
            var fixtureHook=SyntheticAfterOnsetForFixture;if(fixtureHook!=null)fixtureHook();
#endif
            if(Phase == Phase.MainTask && !activeSince.HasValue) {activeSince=current.Onset.Value;TraceMutation();}
            if(unityProxySeconds.HasValue)
                Log("stimulus_presented", "word",current.Stimulus.Word.ToString(),"ink",current.Stimulus.Ink.ToString(),
                    "list",current.ListId,"planned_index",current.PlannedIndex.ToString(),
                    "software_proxy_schema","1",
                    "task_onset_seconds",Number(current.Onset.Value),"task_onset_clock_id",clock.ClockId,
                    "task_onset_basis","task_clock_after_caller_declared_unity_proxy",
                    "unity_render_proxy_seconds",Number(unityProxySeconds.Value),"unity_render_proxy_clock_id","unity-realtime-since-startup",
                    "unity_render_proxy_frame",frame.ToString(CultureInfo.InvariantCulture),
                    "unity_render_proxy_revision",revision.ToString(CultureInfo.InvariantCulture),
                    "unity_render_proxy_basis","caller_declared_unity_render_proxy_not_photon_onset");
            else Log("stimulus_presented", "word", current.Stimulus.Word.ToString(), "ink", current.Stimulus.Ink.ToString(),
                "list", current.ListId, "planned_index", current.PlannedIndex.ToString());
        }

        // All releases must be forwarded in every phase, including pause and correction.
        public bool ButtonChanged(Colour colour, bool down)
        {
            if(mutationJournal==null&&incrementalMutationJournal==null)return ButtonChangedCore(colour,down);
            BeginMutation("ButtonChanged");bool success=false;
            try{bool result=ButtonChangedCore(colour,down);success=true;return result;}finally{EndMutation(success);}
        }
        bool ButtonChangedCore(Colour colour, bool down)
        {
            if(!Enum.IsDefined(typeof(Colour),colour)) throw new ArgumentOutOfRangeException("colour");
            if(!down) { held.Remove(colour);TraceMutation(); return false; }
            if(!held.Add(colour)) return false;
            TraceMutation();
            if((Phase != Phase.Practice && Phase != Phase.MainTask) || current == null || !current.Onset.HasValue || current.Outcome != TrialOutcome.Pending)
                return false;
            current.SetResponseAt(trialAuthority,Time());TraceMutation(); current.SetResponse(trialAuthority,colour);TraceMutation(); current.SetCorrect(trialAuthority,colour == current.Stimulus.Ink);TraceMutation();
            current.SetResponded(trialAuthority);TraceMutation();
            Log("first_response", "response", colour.ToString(), "correct", current.Correct.Value.ToString(),
                "onset", Number(current.Onset.Value), "rt_seconds", Number(current.ReactionSeconds.Value));
            if(Phase == Phase.Practice)
            {
                if(current.Correct.Value) { practiceCorrect++;TraceMutation(); AdvancePractice(); }
                else Move(Phase.PracticeCorrection);
            }
            else AdvanceMain();
            return true;
        }

        public void ContinuePractice()
        {
            if(mutationJournal==null&&incrementalMutationJournal==null){ContinuePracticeCore();return;}
            BeginMutation("ContinuePractice");bool success=false;
            try{ContinuePracticeCore();success=true;}finally{EndMutation(success);}
        }
        void ContinuePracticeCore()
        {
            Require(Phase.PracticeCorrection);
            Log("practice_correction_continue"); AdvancePractice();
        }

        public void Rate(int value)
        {
            if(mutationJournal==null&&incrementalMutationJournal==null){RateCore(value);return;}
            BeginMutation("Rate");bool success=false;
            try{RateCore(value);success=true;}finally{EndMutation(success);}
        }
        void RateCore(int value)
        {
            Require(Phase.Questionnaire);
            if(value < 1 || value > 5) throw new ArgumentOutOfRangeException("value");
            double now=Time();
            if(Configuration.QuestionnaireInstructionRequiresAcknowledgement?questionnaireInstructionPending:now<ratingAt+Configuration.QuestionnaireInstructionSeconds)
                throw new InvalidOperationException(Configuration.QuestionnaireInstructionRequiresAcknowledgement?
                    "Acknowledge the questionnaire control instructions before selecting a rating":
                    "Wait for the questionnaire control instructions to finish before selecting a rating");
            CompleteQuestionnaireInstruction(now);
            var item=Questionnaire.Id(questionIndex);
            ratings.Add(new RatingRecord(BlockIndex,item,value,now,Configuration.QuestionnaireLanguage,Questionnaire.Version));
            TraceMutation();
            Log("rating", "item", item, "value", value.ToString(), "language", Configuration.QuestionnaireLanguage, "version", Questionnaire.Version);
            questionIndex++;TraceMutation();
            if(questionIndex == Questionnaire.Count)
            {
                Log("questionnaire_completed", "duration_seconds", Number(Time()-ratingAt));
                if(Configuration.NamedPlan!=null&&BlockIndex==Configuration.NamedPlan.Blocks.Count-1)
                {
                    Log("final_rest_skipped","scope","explicit_named_plan_simulation");
                    Finish(Core.EndReason.Completed,"named_plan_blocks_and_ratings_complete");
                }
                else if(Configuration.NamedPlan!=null) {restAt=Time();TraceMutation();Move(Phase.Rest);}
                else if(Configuration.NamedOption!=null)
                {
                    Log("final_rest_skipped","scope","single_block_development_preview");
                    Finish(Core.EndReason.Completed,"named_single_block_and_ratings_complete");
                }
                else if(BlockIndex==3&&Configuration.SkipFinalRest)
                {
                    Log("final_rest_skipped","minimum_rest_seconds",Number(Configuration.MinimumRestSeconds));
                    Finish(Core.EndReason.Completed,"four_blocks_and_ratings_complete_final_rest_skipped");
                }
                else {restAt=Time();TraceMutation(); Move(Phase.Rest);}
            }
        }
        public bool AcknowledgeQuestionnaireInstruction()
        {
            if(mutationJournal==null&&incrementalMutationJournal==null)return AcknowledgeQuestionnaireInstructionCore();
            BeginMutation("AcknowledgeQuestionnaireInstruction");bool success=false;
            try{bool result=AcknowledgeQuestionnaireInstructionCore();success=true;return result;}finally{EndMutation(success);}
        }
        bool AcknowledgeQuestionnaireInstructionCore()
        {
            if(Phase!=Phase.Questionnaire||!Configuration.QuestionnaireInstructionRequiresAcknowledgement||!questionnaireInstructionPending)return false;
            questionnaireInstructionPending=false;TraceMutation();
            Log("questionnaire_instruction_completed","completion_policy","explicit_participant_acknowledgement",
                "observed_elapsed_seconds",Number(Time()-ratingAt));
            return true;
        }
        public bool Ready()
        {
            if(mutationJournal==null&&incrementalMutationJournal==null)return ReadyCore();
            BeginMutation("Ready");bool success=false;
            try{bool result=ReadyCore();success=true;return result;}finally{EndMutation(success);}
        }
        bool ReadyCore()
        {
            Require(Phase.Rest);
            if(!ReadyEnabled) return false;
            if(Configuration.RetainControlAcrossBlocks&&!LockedControl.HasValue)
                throw new InvalidOperationException("The confirmed session source must remain locked before continuing");
            Log("rest_ready", "rest_seconds", Number(RestSeconds),"continuation_policy",
                Configuration.ParticipantPacedRest?"participant_continue_no_minimum":"minimum_duration_then_explicit_continue");
            if(Configuration.NamedPlan!=null)
            {
                if(!namedOptionConfirmed||BlockIndex+1>=Configuration.NamedPlan.Blocks.Count)throw new InvalidOperationException("No confirmed next named plan block");
                BlockIndex++;TraceMutation();Segment=0;TraceMutation();current=null;TraceMutation();namedPlanBlockStarted=false;activeAccum=0;activeSince=null;
                SetNamedPlanBlockSource();RecordBlockSource(true);Move(Phase.BlockReady);
            }
            else if(BlockIndex == 3) Finish(Core.EndReason.Completed, "four_blocks_ratings_and_rest_complete");
            else
            {
                BlockIndex++;TraceMutation(); Segment=0;TraceMutation(); current=null;TraceMutation();
                if(Configuration.LegacyRecordingIntent!=null){namedPlanBlockStarted=false;activeAccum=0;activeSince=null;}
                if(Configuration.RetainControlAcrossBlocks)
                {RecordBlockSource(true);Move(Phase.BlockReady);}
                else {LockedControl=null;TraceMutation();Move(Phase.BlockPreparation);}
            }
            return true;
        }

        public void Pause(Actor actor)
        {
            if(mutationJournal==null&&incrementalMutationJournal==null){PauseCore(actor);return;}
            BeginMutation("Pause");bool success=false;
            try{PauseCore(actor);success=true;}finally{EndMutation(success);}
        }
        void PauseCore(Actor actor)
        {
            if(Phase == Phase.Ended || Phase == Phase.Paused || Phase == Phase.NeedsProtocolDecision) return;
            if(!Enum.IsDefined(typeof(Actor),actor)) throw new ArgumentException("Unknown actor");
            CompleteQuestionnaireInstruction(Time());
            beforePause=Phase;TraceMutation(); pausedAt=Time();TraceMutation();
            if(rawCountdownWitnessRequired&&Phase==Phase.Countdown)
            {
                if(!rawCountdownWitnessPending)rawCountdownRemaining=Math.Max(0,countdownAt+Configuration.CountdownSeconds-pausedAt);
                rawCountdownWitnessPending=true;TraceMutation();
            }
            FreezeActive();
            if((Phase == Phase.MainTask || Phase == Phase.Practice) && current != null && current.Outcome == TrialOutcome.Pending)
            {
                current.SetInterrupted(trialAuthority);TraceMutation();
                Log("trial_interrupted", "actor", actor.ToString(), "onset_available", current.Onset.HasValue.ToString());
            }
            Log("explicit_pause", "actor", actor.ToString(), "active_seconds", Number(activeAccum));
            Move(Phase.Paused);
        }
        public void Resume(bool sourceReady)
        {
            if(mutationJournal==null&&incrementalMutationJournal==null){ResumeCore(sourceReady);return;}
            BeginMutation("Resume");bool success=false;
            try{ResumeCore(sourceReady);success=true;}finally{EndMutation(success);}
        }
        void ResumeCore(bool sourceReady)
        {
            Require(Phase.Paused);
            if((beforePause == Phase.MainTask || beforePause == Phase.Countdown) && !sourceReady)
                throw new InvalidOperationException("Reacquire the selected source before resumption");
            double duration=Time()-pausedAt;
            Log("explicit_resume", "paused_seconds", Number(duration));
            if(beforePause == Phase.MainTask)
            {
                Segment++;TraceMutation(); trialIndex++;TraceMutation();
                if(trialIndex >= 24) BeginQuestionnaire();
                else { resumeCountdown=true;TraceMutation(); countdownAt=Time();TraceMutation();
                    if(rawCountdownWitnessRequired){rawCountdownWitnessPending=true;rawCountdownRemaining=Configuration.CountdownSeconds;TraceMutation();}
                    Move(Phase.Countdown);
                    if(rawCountdownWitnessPending)Log("countdown_waiting_for_software_witness","basis","named_raw_software_render_not_optical"); }
            }
            else if(beforePause == Phase.Practice && current != null && current.Outcome == TrialOutcome.Interrupted)
            {
                // PRD section 3 applies one explicit-pause rule to both task types: an
                // explicit resume follows the next planned trial, never silently
                // repeats the interrupted item, and never records it as an incorrect
                // answer. Practice therefore continues with the next planned trial of
                // the same eight-trial attempt, exactly as an interrupted main trial
                // advances. The interrupted trial stays registered as Interrupted and
                // still occupies one of the eight planned trials, so the 6/8 rule is
                // unchanged after a pause and no trial is invented.
                Log("practice_resume_next_planned_trial", "interrupted_trial", current.TrialId);
                AdvancePractice();
            }
            else
            {
                if(beforePause == Phase.Rest) {restAt+=duration;TraceMutation();}
                if(beforePause == Phase.Countdown&&!rawCountdownWitnessRequired) {countdownAt+=duration;TraceMutation();}
                // Rating duration must exclude an explicit pause, as the rest and
                // countdown clocks already do. Without this the exported
                // questionnaire_completed duration reports pause time as rating time.
                if(beforePause == Phase.Questionnaire) {ratingAt+=duration;TraceMutation();}
                Move(beforePause);
                if(beforePause==Phase.Countdown&&rawCountdownWitnessPending)
                    Log("countdown_waiting_for_software_witness","basis","named_raw_software_render_not_optical");
            }
        }
        public void RecordHeadsetRemoval(Actor actor)
        {
            if(mutationJournal==null&&incrementalMutationJournal==null){RecordHeadsetRemovalCore(actor);return;}
            BeginMutation("RecordHeadsetRemoval");bool success=false;
            try{RecordHeadsetRemovalCore(actor);success=true;}finally{EndMutation(success);}
        }
        void RecordHeadsetRemovalCore(Actor actor)
        {
            if(!Enum.IsDefined(typeof(Actor),actor)) throw new ArgumentException("Unknown actor");
            Log("headset_removed", "actor", actor.ToString());
        }
        public void Withdraw()
        {
            if(mutationJournal==null&&incrementalMutationJournal==null){WithdrawCore();return;}
            BeginMutation("Withdraw");bool success=false;
            try{WithdrawCore();success=true;}finally{EndMutation(success);}
        }
        void WithdrawCore()
        { if(Phase != Phase.Ended) Finish(Core.EndReason.Withdrawn, "participant_withdrawal"); }
        public void EndTechnical(Actor actor, string reason)
        {
            if(mutationJournal==null&&incrementalMutationJournal==null){EndTechnicalCore(actor,reason);return;}
            BeginMutation("EndTechnical");bool success=false;
            try{EndTechnicalCore(actor,reason);success=true;}finally{EndMutation(success);}
        }
        void EndTechnicalCore(Actor actor, string reason)
        {
            if(actor != Actor.Owner && actor != Actor.Operator) throw new UnauthorizedAccessException("Owner or safety operator required");
            if(String.IsNullOrWhiteSpace(reason)) throw new ArgumentException("Technical reason required");
            if(Phase != Phase.Ended) Finish(Core.EndReason.TechnicalEnd, reason);
        }
        public void FlagTechnicalIssue(string code)
        {
            if(mutationJournal==null&&incrementalMutationJournal==null){FlagTechnicalIssueCore(code);return;}
            BeginMutation("FlagTechnicalIssue");bool success=false;
            try{FlagTechnicalIssueCore(code);success=true;}finally{EndMutation(success);}
        }
        void FlagTechnicalIssueCore(string code)
        {
            if(String.IsNullOrWhiteSpace(code)) throw new ArgumentException("Fault code required");
            if(current != null && current.Outcome == TrialOutcome.Pending) {current.SetTechnicalIssue(trialAuthority);TraceMutation();}
            Log("technical_issue", "code", code);
            // No automatic pause/recovery default: that experimental policy remains unresolved.
        }

        public TaskView View(Actor actor)
        {
            if(!Enum.IsDefined(typeof(Actor),actor)) throw new ArgumentException("Unknown actor");
            if(Configuration.IsNamedDesign&&actor==Actor.Operator)throw new InvalidOperationException("Legacy operator view does not represent named-option provenance");
            bool task=Phase == Phase.MainTask || (actor == Actor.Participant && Phase == Phase.Practice);
            return new TaskView {
                Phase=Phase,BlockIndex=BlockIndex,Segment=Segment, TrialId=task && current != null ? current.TrialId : null,
                Stimulus=task && current != null ? current.Stimulus : null,
                OperatorAvatarVisible=actor == Actor.Participant && Phase == Phase.MainTask &&
                    (Configuration.IsNamedDesign ? CurrentNamedSelection!=null&&CurrentNamedSelection.Option!=ViveNamedOption.NoAvatar : CurrentCondition != Condition.D),
                ParticipantAvatarVisible=actor == Actor.Operator && Phase == Phase.MainTask,
                OperatorViewProvisional=actor == Actor.Operator && (Configuration.IsNamedDesign || Phase != Phase.MainTask || CurrentCondition == Condition.D),
                PracticeCorrection=actor == Actor.Participant && Phase == Phase.PracticeCorrection ? "Select the font colour: " + current.Stimulus.Ink : null,
                QuestionnaireItem=actor == Actor.Participant && Phase == Phase.Questionnaire ? Questionnaire.Item(questionIndex,Configuration.QuestionnaireLanguage) : null,
                ReadyEnabled=ReadyEnabled,
                SourceContinuationPending=RetainedSourceContinuationPending,
                SourceLabel=Configuration.IsNamedDesign&&CurrentNamedSelection!=null&&CurrentNamedSelection.Option==ViveNamedOption.NoAvatar ? "NO AVATAR - SOURCE NOT APPLICABLE" :
                    LockedControl == ControlSelection.SimulatorRehearsal ? "SIMULATOR REHEARSAL - NOT LIVE WOZ" : LockedControl.ToString()
            };
        }

        void CreateTrial(bool isPractice)
        {
            int listIndex=isPractice ? -1 : Configuration.IsNamedDesign ? CurrentNamedSelection.ListIndex : Configuration.Allocation.ListByBlock[BlockIndex];
            current=new TrialRecord(trialAuthority,Configuration.SessionId+(isPractice?"/p"+PracticeAttempt:"/b"+BlockIndex)+"/t"+trialIndex,
                Configuration.SessionId,isPractice?"practice-"+Configuration.Seed+"-"+PracticeAttempt:TrialPlans.MainListId(Configuration.Seed,listIndex),
                isPractice,isPractice?PracticeAttempt:0,isPractice?-1:BlockIndex,trialIndex,Segment,isPractice||Configuration.IsNamedDesign?null:CurrentCondition,
                isPractice?practice[trialIndex]:lists[listIndex][trialIndex],Configuration.NamedPlan==null?Configuration.NamedOption:null,
                Configuration.NamedPlan,isPractice?null:CurrentNamedPlanBlock);TraceMutation();
            trials.Add(current);TraceMutation();
            // The named recording event captures the planned stimulus before
            // presentation. It is not evidence that the headset displayed it.
            // Preserve the existing Quest and earlier named event encodings.
            if(Configuration.HasRawRecordingIntent)
                Log("trial_planned", "list", current.ListId, "planned_index", trialIndex.ToString(),
                    "planned_stimulus_schema", "1", "planned_word", current.Stimulus.Word.ToString(),
                    "planned_ink", current.Stimulus.Ink.ToString());
            else Log("trial_planned", "list", current.ListId, "planned_index", trialIndex.ToString());
        }
        void AdvancePractice()
        {
            trialIndex++;TraceMutation();
            if(trialIndex < 8) { Move(Phase.Practice); CreateTrial(true); return; }
            Log("practice_completed", "attempt", PracticeAttempt.ToString(), "correct_first", practiceCorrect.ToString(), "planned", "8");
            current=null;TraceMutation();
            if(practiceCorrect >= 6) { BlockIndex=0;TraceMutation(); Move(Phase.BlockPreparation); }
            else if(PracticeAttempt < 2) Move(Phase.PracticeRetry);
            else Finish(Core.EndReason.PracticeFailed, "two_practice_attempts_below_6_of_8");
        }
        void AdvanceMain()
        {
            trialIndex++;TraceMutation();
            if(trialIndex < 24) CreateTrial(false); else BeginQuestionnaire();
        }
        void BeginQuestionnaire()
        {
            FreezeActive(); Log("block_task_ended", "active_seconds", Number(activeAccum));
            current=null;TraceMutation(); questionIndex=0;TraceMutation(); ratingAt=Time();TraceMutation(); Move(Phase.Questionnaire);
            questionnaireInstructionPending=Configuration.QuestionnaireInstructionRequiresAcknowledgement||Configuration.QuestionnaireInstructionSeconds>0;TraceMutation();
            if(questionnaireInstructionPending)
                Log("questionnaire_instruction_started","duration_seconds",Number(Configuration.QuestionnaireInstructionSeconds),
                    "input_policy",Configuration.QuestionnaireInstructionRequiresAcknowledgement?
                        "ratings_disabled_until_explicit_participant_acknowledgement":"ratings_disabled_until_instruction_duration_elapsed");
        }
        void CompleteQuestionnaireInstruction(double now)
        {
            if(Configuration.QuestionnaireInstructionRequiresAcknowledgement||Phase!=Phase.Questionnaire||!questionnaireInstructionPending||now<ratingAt+Configuration.QuestionnaireInstructionSeconds)return;
            questionnaireInstructionPending=false;TraceMutation();
            Log("questionnaire_instruction_completed","duration_seconds",Number(Configuration.QuestionnaireInstructionSeconds),
                "observed_elapsed_seconds",Number(now-ratingAt));
        }
        void RecordBlockSource(bool retained)
        {
            if(Configuration.NamedPlan!=null)
            {
                var selection=CurrentNamedSelection;bool noAvatar=selection.Option==ViveNamedOption.NoAvatar;
                if(!namedOptionConfirmed||(noAvatar?LockedControl.HasValue:LockedControl!=ControlSelection.Automatic))throw new InvalidOperationException("Invalid named plan block source");
                Log("block_configuration_locked","owner",sourceConfirmationOwner,"selection",noAvatar?ViveNamedOptionProvenance.NoAvatarNotApplicable:"Automatic",
                    "list_index",selection.ListIndex.ToString(CultureInfo.InvariantCulture),"selection_origin",retained?"confirmed_plan":"explicit_owner_plan_confirmation",
                    "confirmation_event_sequence",sourceConfirmationSequence.ToString(CultureInfo.InvariantCulture));return;
            }
            if(Configuration.NamedOption!=null)
            {
                bool noAvatar=Configuration.NamedOption.Option==ViveNamedOption.NoAvatar;
                if(retained||BlockIndex!=0||!namedOptionConfirmed||(noAvatar?LockedControl.HasValue:LockedControl!=ControlSelection.Automatic))
                    throw new InvalidOperationException("Unsupported named-option block/source");
                Log("block_configuration_locked","owner",sourceConfirmationOwner,"selection",noAvatar?ViveNamedOptionProvenance.NoAvatarNotApplicable:LockedControl.Value.ToString(),
                    "list_index",Configuration.NamedOption.ListIndex.ToString(CultureInfo.InvariantCulture),
                    "selection_origin","explicit_owner_confirmation","confirmation_block_index","0",
                    "confirmation_event_sequence",sourceConfirmationSequence.ToString(CultureInfo.InvariantCulture));
                return;
            }
            Log("block_configuration_locked","owner",sourceConfirmationOwner,"condition",CurrentCondition.ToString(),
                "selection",LockedControl.Value.ToString(),"b_initial_target",Configuration.Allocation.BInitialTarget.ToString(),
                "list_index",Configuration.Allocation.ListByBlock[BlockIndex].ToString(),
                "selection_origin",retained?"retained_session":"explicit_owner_confirmation",
                "confirmation_block_index",(Configuration.RetainControlAcrossBlocks?0:BlockIndex).ToString(CultureInfo.InvariantCulture),
                "confirmation_event_sequence",sourceConfirmationSequence.ToString(CultureInfo.InvariantCulture));
        }
        bool InPhaseOrPaused(Phase phase){return Phase==phase||(Phase==Phase.Paused&&beforePause==phase);}
        double PhaseTime(){double now=Time();return Phase==Phase.Paused?pausedAt:now;}
        double Remaining(Phase phase,double started,double duration)
        {return InPhaseOrPaused(phase)?Math.Max(0,started+duration-PhaseTime()):0;}
        void Finish(EndReason reason, string detail)
        {
            FreezeActive();
            if(current != null && current.Outcome == TrialOutcome.Pending)
            { current.SetInterrupted(trialAuthority);TraceMutation(); Log("trial_interrupted", "reason", detail); }
            EndReason=reason;TraceMutation(); Log("session_ended", "reason", reason.ToString(), "detail", detail); Move(Phase.Ended);
        }
        void FreezeActive() { if(activeSince.HasValue) { activeAccum+=Time()-activeSince.Value;TraceMutation(); activeSince=null;TraceMutation(); } }
        void Move(Phase phase) { var prior=Phase; Phase=phase;TraceMutation(); Log("phase_changed", "from", prior.ToString(), "to", phase.ToString()); }
        void Require(params Phase[] allowed) { if(Array.IndexOf(allowed,Phase)<0) throw new InvalidOperationException("Action unavailable in "+Phase); }
        static void RequireOwner(Actor actor) { if(actor != Actor.Owner) throw new UnauthorizedAccessException("Experiment owner role required"); }
        double Time()
        {
            double now=clock.Now;
            if(Double.IsNaN(now) || Double.IsInfinity(now) || now < lastTime) throw new InvalidOperationException("Named monotonic clock moved backward or became invalid");
            lastTime=now; return now;
        }
        void Log(string kind, params string[] fields)
        {
            var data=new Dictionary<string,string>(); for(int i=0;i<fields.Length;i+=2) data.Add(fields[i],fields[i+1]);
            double now=Time();
            if(Configuration.NamedPlan!=null)
            {
                var plan=Configuration.NamedPlan;var block=kind.StartsWith("named_plan_",StringComparison.Ordinal)?null:CurrentNamedPlanBlock;
                var intent=Configuration.NamedRecordingIntent;
                data.Add("named_schema",intent==null?ViveNamedSimulationPlan.SchemaVersion:ViveNamedRecordingIntent.EventSchemaVersion);data.Add("named_plan_id",plan.PlanId);
                data.Add("named_scope","explicit_plan_simulation_only");data.Add("named_phase",Phase.ToString());
                data.Add("named_active_seconds",Number(activeAccum+(activeSince.HasValue?now-activeSince.Value:0)));
                if(intent==null)data.Add("named_participant_eye","NOT_RECORDED");
                else
                {
                    data.Add("named_plan_schema",ViveNamedSimulationPlan.SchemaVersion);
                    data.Add("named_recording_intent_id",intent.IntentId);
                    data.Add("named_eye_sample_payload","NOT_INCLUDED_IN_TASK_EVENT");
                    data.Add("named_task_recording_intent",intent.TaskRequested?"REQUESTED":"OFF");
                    data.Add("named_eye_recording_intent",intent.EyeRequested?"REQUESTED":"OFF");
                }
                if(block!=null)
                {
                    var option=block.Selection;bool absent=option.Option==ViveNamedOption.NoAvatar;
                    data.Add("named_block_index",block.Index.ToString(CultureInfo.InvariantCulture));data.Add("named_block_started",namedPlanBlockStarted.ToString());
                    data.Add("named_option",option.Option.ToString());data.Add("named_list_index",option.ListIndex.ToString(CultureInfo.InvariantCulture));
                    data.Add("named_schedule",option.ScheduleVersion);data.Add("named_geometry",option.GeometryVersion);
                    data.Add("named_source",absent?ViveNamedOptionProvenance.NoAvatarNotApplicable:"AUTOMATIC");
                    data.Add("named_avatar_aoi",absent?ViveNamedOptionProvenance.NoAvatarNotApplicable:"NOT_ASSESSED");
                }
            }
            else if(Configuration.NamedOption!=null)
            {
                var named=Configuration.NamedOption;
                data.Add("named_schema",ViveNamedOptionProvenance.SchemaVersion);data.Add("named_option",named.Option.ToString());
                data.Add("named_schedule",named.ScheduleVersion);data.Add("named_geometry",named.GeometryVersion);
                data.Add("named_source",named.Option==ViveNamedOption.NoAvatar?ViveNamedOptionProvenance.NoAvatarNotApplicable:"AUTOMATIC");
                data.Add("named_avatar_aoi",named.Option==ViveNamedOption.NoAvatar?ViveNamedOptionProvenance.NoAvatarNotApplicable:"NOT_ASSESSED");
                data.Add("named_participant_eye","NOT_RECORDED");
                data.Add("named_phase",Phase.ToString());data.Add("named_active_seconds",Number(activeAccum+(activeSince.HasValue?now-activeSince.Value:0)));
                data.Add("named_scope","single_block_simulation_only");
            }
            if(Configuration.LegacyRecordingIntent!=null)
            {
                var design=Configuration.LegacyRecordingDesign;var intent=Configuration.LegacyRecordingIntent;
                data.Add("legacy_schema",ViveLegacyRecordingIntent.EventSchemaVersion);
                data.Add("legacy_design_id",design.DesignId);data.Add("legacy_design_schema",ViveLegacyRecordingDesign.SchemaVersion);
                data.Add("legacy_recording_intent_id",intent.IntentId);
                data.Add("legacy_task_recording_intent",intent.TaskRequested?"REQUESTED":"OFF");
                data.Add("legacy_eye_recording_intent",intent.EyeRequested?"REQUESTED":"OFF");
                data.Add("legacy_eye_sample_payload","NOT_INCLUDED_IN_TASK_EVENT");
                data.Add("legacy_phase",Phase.ToString());data.Add("legacy_allocation_id",design.Allocation.AllocationId);
                data.Add("legacy_b_initial_target",design.Allocation.BInitialTarget.ToString());
                data.Add("legacy_active_seconds",Number(activeAccum+(activeSince.HasValue?now-activeSince.Value:0)));
                if(BlockIndex>=0&&BlockIndex<4)
                {
                    data.Add("legacy_condition",design.Allocation.Order[BlockIndex].ToString());
                    data.Add("legacy_list_index",design.Allocation.ListByBlock[BlockIndex].ToString(CultureInfo.InvariantCulture));
                    data.Add("legacy_block_started",namedPlanBlockStarted.ToString());
                }
            }
            var eventBlock=Configuration.NamedPlan!=null&&kind.StartsWith("named_plan_",StringComparison.Ordinal)?null:CurrentNamedPlanBlock;
            events.Add(new JournalEvent(events.Count,now,clock.ClockId,Configuration.SessionId,
                Configuration.NamedPlan!=null&&eventBlock==null?-1:BlockIndex,Segment,kind=="block_plan_prepared"||current==null?null:current.TrialId,kind,data,
                Configuration.NamedPlan==null?Configuration.NamedOption:null,Configuration.NamedPlan,eventBlock,Configuration.NamedRecordingIntent,Configuration.LegacyRecordingDesign,Configuration.LegacyRecordingIntent));
            if(mutationJournal!=null||incrementalMutationJournal!=null)TraceMutation();
        }
        static string Number(double value) { return value.ToString("R",CultureInfo.InvariantCulture); }
    }
}
