using System;
using System.Collections.Generic;
using System.Globalization;

namespace AvatarStroop.Core
{
    // Engine-independent state machine. Callers acknowledge actual presentation, not merely a planned frame.
    public sealed class Experiment
    {
        readonly IClock clock;
        readonly List<JournalEvent> events = new List<JournalEvent>();
        readonly List<TrialRecord> trials = new List<TrialRecord>();
        readonly List<RatingRecord> ratings = new List<RatingRecord>();
        readonly HashSet<Colour> held = new HashSet<Colour>();
        readonly Stimulus[][] lists = new Stimulus[4][];
        Stimulus[] practice;
        Phase beforePause;
        bool resumeCountdown;
        double lastTime, countdownAt, restAt, ratingAt, pausedAt, activeAccum;
        double? activeSince;
        int trialIndex, practiceCorrect, questionIndex;
        TrialRecord current;

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
        public IReadOnlyList<JournalEvent> Events { get { return events.AsReadOnly(); } }
        public IReadOnlyList<TrialRecord> Trials { get { return trials.AsReadOnly(); } }
        public IReadOnlyList<RatingRecord> Ratings { get { return ratings.AsReadOnly(); } }
        public Condition? CurrentCondition { get { return BlockIndex >= 0 && BlockIndex < 4 ? (Condition?)Configuration.Allocation.Order[BlockIndex] : null; } }
        public double ActiveSeconds { get { return activeAccum + (activeSince.HasValue ? Time() - activeSince.Value : 0); } }
        public double RestSeconds { get { return Phase == Phase.Rest ? Time() - restAt : 0; } }
        public bool ReadyEnabled { get { return Phase == Phase.Rest && Time() >= restAt + 30; } }

        public Experiment(SessionConfiguration configuration, IClock clock)
        {
            if(configuration == null || clock == null || String.IsNullOrWhiteSpace(clock.ClockId)) throw new ArgumentException("Configuration and named clock required");
            if(configuration.RunKind != RunKind.Simulation)
                throw new InvalidOperationException("Apparatus sessions are unavailable: real head/eye adapters, approved fault/recovery policy, calibration and timing validation are not complete.");
            Configuration=configuration; this.clock=clock; lastTime=clock.Now;
            if(Double.IsNaN(lastTime) || Double.IsInfinity(lastTime) || lastTime < 0) throw new ArgumentException("Invalid clock");
            for(int i=0;i<4;i++) lists[i]=TrialPlans.MainList(configuration.Seed,i);
            Phase=Phase.Preparation;
            Log("session_created", "run_kind", configuration.RunKind.ToString(), "protocol", SessionConfiguration.ProtocolVersion,
                "software", SessionConfiguration.SoftwareVersion, "allocation", configuration.Allocation.AllocationId,
                "order", String.Join("", Array.ConvertAll(configuration.Allocation.Order, x=>x.ToString())),
                "b_initial_target", configuration.Allocation.BInitialTarget.ToString(), "seed", configuration.Seed.ToString(CultureInfo.InvariantCulture),
                "list_by_block", String.Join(",", configuration.Allocation.ListByBlock),
                "countdown_seconds", Number(configuration.CountdownSeconds), "questionnaire_language", configuration.QuestionnaireLanguage);
        }

        public void RecordPreparation(Actor actor, string field, string status)
        {
            RequireOwner(actor); Require(Phase.Preparation, Phase.Familiarization, Phase.BlockPreparation);
            if(String.IsNullOrWhiteSpace(field) || String.IsNullOrWhiteSpace(status)) throw new ArgumentException("Explicit preparation record required");
            Log("preparation_record", "field", field, "status", status);
        }
        public void BeginFamiliarization(Actor actor)
        {
            RequireOwner(actor); Require(Phase.Preparation); Move(Phase.Familiarization);
        }
        public void BeginPractice(Actor actor)
        {
            RequireOwner(actor); Require(Phase.Familiarization, Phase.PracticeRetry);
            PracticeAttempt++; practiceCorrect=0; trialIndex=0;
            practice=TrialPlans.Practice(Configuration.Seed,PracticeAttempt);
            Log("practice_started", "attempt", PracticeAttempt.ToString());
            Move(Phase.Practice); CreateTrial(true);
        }

        public void LockBlock(Actor actor, ControlSelection selection, string ownerId)
        {
            RequireOwner(actor); Require(Phase.BlockPreparation);
            if(String.IsNullOrWhiteSpace(ownerId) || !Enum.IsDefined(typeof(ControlSelection),selection)) throw new ArgumentException("Owner and source required");
            if(selection == ControlSelection.SimulatorRehearsal && Configuration.RunKind != RunKind.Simulation)
                throw new InvalidOperationException("Simulator data cannot enter apparatus sessions");
            LockedControl=selection;
            Log("block_configuration_locked", "owner", ownerId, "condition", CurrentCondition.ToString(),
                "selection", selection.ToString(), "b_initial_target", Configuration.Allocation.BInitialTarget.ToString(),
                "list_index", Configuration.Allocation.ListByBlock[BlockIndex].ToString());
            Move(Phase.BlockReady);
        }

        // Source readiness is an adapter result, not an assertion of hardware validation.
        public void BeginCountdown(Actor actor, bool sourceReady)
        {
            RequireOwner(actor); Require(Phase.BlockReady);
            if(!sourceReady) throw new InvalidOperationException("Selected source is unavailable or not revalidated");
            countdownAt=Time(); resumeCountdown=false; Move(Phase.Countdown);
        }
        public void Tick()
        {
            double now=Time();
            if(Phase == Phase.Countdown && now >= countdownAt+Configuration.CountdownSeconds)
            {
                if(!resumeCountdown) { trialIndex=0; Segment=0; activeAccum=0; activeSince=null; }
                Move(Phase.MainTask); CreateTrial(false);
            }
        }

        public void MarkPresented(string trialId)
        {
            Require(Phase.Practice, Phase.MainTask);
            if(current == null || trialId != current.TrialId) throw new InvalidOperationException("Presentation acknowledges only the current trial");
            if(current.Onset.HasValue) return;
            current.Onset=Time();
            if(Phase == Phase.MainTask && !activeSince.HasValue) activeSince=current.Onset.Value;
            Log("stimulus_presented", "word", current.Stimulus.Word.ToString(), "ink", current.Stimulus.Ink.ToString(),
                "list", current.ListId, "planned_index", current.PlannedIndex.ToString());
        }

        // All releases must be forwarded in every phase, including pause and correction.
        public bool ButtonChanged(Colour colour, bool down)
        {
            if(!Enum.IsDefined(typeof(Colour),colour)) throw new ArgumentOutOfRangeException("colour");
            if(!down) { held.Remove(colour); return false; }
            if(!held.Add(colour)) return false;
            if((Phase != Phase.Practice && Phase != Phase.MainTask) || current == null || !current.Onset.HasValue || current.Outcome != TrialOutcome.Pending)
                return false;
            current.ResponseAt=Time(); current.Response=colour; current.Correct=colour == current.Stimulus.Ink;
            current.Outcome=TrialOutcome.Responded;
            Log("first_response", "response", colour.ToString(), "correct", current.Correct.Value.ToString(),
                "onset", Number(current.Onset.Value), "rt_seconds", Number(current.ReactionSeconds.Value));
            if(Phase == Phase.Practice)
            {
                if(current.Correct.Value) { practiceCorrect++; AdvancePractice(); }
                else Move(Phase.PracticeCorrection);
            }
            else AdvanceMain();
            return true;
        }

        public void ContinuePractice()
        {
            Require(Phase.PracticeCorrection);
            Log("practice_correction_continue"); AdvancePractice();
        }

        public void Rate(int value)
        {
            Require(Phase.Questionnaire);
            if(value < 1 || value > 5) throw new ArgumentOutOfRangeException("value");
            var item=Questionnaire.Id(questionIndex);
            ratings.Add(new RatingRecord {BlockIndex=BlockIndex,ItemId=item,Value=value,Time=Time(),Language=Configuration.QuestionnaireLanguage,Version=Questionnaire.Version});
            Log("rating", "item", item, "value", value.ToString(), "language", Configuration.QuestionnaireLanguage, "version", Questionnaire.Version);
            questionIndex++;
            if(questionIndex == Questionnaire.Count)
            {
                Log("questionnaire_completed", "duration_seconds", Number(Time()-ratingAt));
                restAt=Time(); Move(Phase.Rest);
            }
        }
        public bool Ready()
        {
            Require(Phase.Rest);
            if(!ReadyEnabled) return false;
            Log("rest_ready", "rest_seconds", Number(RestSeconds));
            if(BlockIndex == 3) Finish(Core.EndReason.Completed, "four_blocks_ratings_and_rest_complete");
            else { BlockIndex++; Segment=0; LockedControl=null; current=null; Move(Phase.BlockPreparation); }
            return true;
        }

        public void Pause(Actor actor)
        {
            if(Phase == Phase.Ended || Phase == Phase.Paused || Phase == Phase.NeedsProtocolDecision) return;
            if(!Enum.IsDefined(typeof(Actor),actor)) throw new ArgumentException("Unknown actor");
            beforePause=Phase; pausedAt=Time(); FreezeActive();
            if((Phase == Phase.MainTask || Phase == Phase.Practice) && current != null && current.Outcome == TrialOutcome.Pending)
            {
                current.Outcome=TrialOutcome.Interrupted;
                Log("trial_interrupted", "actor", actor.ToString(), "onset_available", current.Onset.HasValue.ToString());
            }
            Log("explicit_pause", "actor", actor.ToString(), "active_seconds", Number(activeAccum));
            Move(Phase.Paused);
        }
        public void Resume(bool sourceReady)
        {
            Require(Phase.Paused);
            if((beforePause == Phase.MainTask || beforePause == Phase.Countdown) && !sourceReady)
                throw new InvalidOperationException("Reacquire the selected source before resumption");
            double duration=Time()-pausedAt;
            Log("explicit_resume", "paused_seconds", Number(duration));
            if(beforePause == Phase.MainTask)
            {
                Segment++; trialIndex++;
                if(trialIndex >= 24) BeginQuestionnaire();
                else { resumeCountdown=true; countdownAt=Time(); Move(Phase.Countdown); }
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
                if(beforePause == Phase.Rest) restAt+=duration;
                if(beforePause == Phase.Countdown) countdownAt+=duration;
                // Rating duration must exclude an explicit pause, as the rest and
                // countdown clocks already do. Without this the exported
                // questionnaire_completed duration reports pause time as rating time.
                if(beforePause == Phase.Questionnaire) ratingAt+=duration;
                Move(beforePause);
            }
        }
        public void RecordHeadsetRemoval(Actor actor)
        {
            if(!Enum.IsDefined(typeof(Actor),actor)) throw new ArgumentException("Unknown actor");
            Log("headset_removed", "actor", actor.ToString());
        }
        public void Withdraw() { if(Phase != Phase.Ended) Finish(Core.EndReason.Withdrawn, "participant_withdrawal"); }
        public void EndTechnical(Actor actor, string reason)
        {
            if(actor != Actor.Owner && actor != Actor.Operator) throw new UnauthorizedAccessException("Owner or safety operator required");
            if(String.IsNullOrWhiteSpace(reason)) throw new ArgumentException("Technical reason required");
            if(Phase != Phase.Ended) Finish(Core.EndReason.TechnicalEnd, reason);
        }
        public void FlagTechnicalIssue(string code)
        {
            if(String.IsNullOrWhiteSpace(code)) throw new ArgumentException("Fault code required");
            if(current != null && current.Outcome == TrialOutcome.Pending) current.TechnicalIssue=true;
            Log("technical_issue", "code", code);
            // No automatic pause/recovery default: that experimental policy remains unresolved.
        }

        public TaskView View(Actor actor)
        {
            if(!Enum.IsDefined(typeof(Actor),actor)) throw new ArgumentException("Unknown actor");
            bool task=Phase == Phase.MainTask || (actor == Actor.Participant && Phase == Phase.Practice);
            return new TaskView {
                Phase=Phase,BlockIndex=BlockIndex,Segment=Segment, TrialId=task && current != null ? current.TrialId : null,
                Stimulus=task && current != null ? current.Stimulus : null,
                OperatorAvatarVisible=actor == Actor.Participant && Phase == Phase.MainTask && CurrentCondition != Condition.D,
                ParticipantAvatarVisible=actor == Actor.Operator && Phase == Phase.MainTask,
                OperatorViewProvisional=actor == Actor.Operator && (Phase != Phase.MainTask || CurrentCondition == Condition.D),
                PracticeCorrection=actor == Actor.Participant && Phase == Phase.PracticeCorrection ? "Select the font colour: " + current.Stimulus.Ink : null,
                QuestionnaireItem=actor == Actor.Participant && Phase == Phase.Questionnaire ? Questionnaire.Item(questionIndex,Configuration.QuestionnaireLanguage) : null,
                ReadyEnabled=ReadyEnabled,
                SourceLabel=LockedControl == ControlSelection.SimulatorRehearsal ? "SIMULATOR REHEARSAL — NOT LIVE WOZ" : LockedControl.ToString()
            };
        }

        void CreateTrial(bool isPractice)
        {
            int listIndex=isPractice ? -1 : Configuration.Allocation.ListByBlock[BlockIndex];
            current=new TrialRecord {SessionId=Configuration.SessionId, IsPractice=isPractice, PracticeAttempt=isPractice?PracticeAttempt:0,
                BlockIndex=isPractice?-1:BlockIndex, PlannedIndex=trialIndex, Segment=Segment,
                Condition=isPractice?null:CurrentCondition, Stimulus=isPractice?practice[trialIndex]:lists[listIndex][trialIndex],
                TrialId=Configuration.SessionId+(isPractice?"/p"+PracticeAttempt:"/b"+BlockIndex)+"/t"+trialIndex,
                ListId=isPractice?"practice-"+Configuration.Seed+"-"+PracticeAttempt:TrialPlans.MainListId(Configuration.Seed,listIndex),
                Outcome=TrialOutcome.Pending};
            trials.Add(current); Log("trial_planned", "list", current.ListId, "planned_index", trialIndex.ToString());
        }
        void AdvancePractice()
        {
            trialIndex++;
            if(trialIndex < 8) { Move(Phase.Practice); CreateTrial(true); return; }
            Log("practice_completed", "attempt", PracticeAttempt.ToString(), "correct_first", practiceCorrect.ToString(), "planned", "8");
            current=null;
            if(practiceCorrect >= 6) { BlockIndex=0; Move(Phase.BlockPreparation); }
            else if(PracticeAttempt < 2) Move(Phase.PracticeRetry);
            else Finish(Core.EndReason.PracticeFailed, "two_practice_attempts_below_6_of_8");
        }
        void AdvanceMain()
        {
            trialIndex++;
            if(trialIndex < 24) CreateTrial(false); else BeginQuestionnaire();
        }
        void BeginQuestionnaire()
        {
            FreezeActive(); Log("block_task_ended", "active_seconds", Number(activeAccum));
            current=null; questionIndex=0; ratingAt=Time(); Move(Phase.Questionnaire);
        }
        void Finish(EndReason reason, string detail)
        {
            FreezeActive();
            if(current != null && current.Outcome == TrialOutcome.Pending)
            { current.Outcome=TrialOutcome.Interrupted; Log("trial_interrupted", "reason", detail); }
            EndReason=reason; Log("session_ended", "reason", reason.ToString(), "detail", detail); Move(Phase.Ended);
        }
        void FreezeActive() { if(activeSince.HasValue) { activeAccum+=Time()-activeSince.Value; activeSince=null; } }
        void Move(Phase phase) { var prior=Phase; Phase=phase; Log("phase_changed", "from", prior.ToString(), "to", phase.ToString()); }
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
            events.Add(new JournalEvent(events.Count,Time(),clock.ClockId,Configuration.SessionId,BlockIndex,Segment,current==null?null:current.TrialId,kind,data));
        }
        static string Number(double value) { return value.ToString("R",CultureInfo.InvariantCulture); }
    }
}
