using System;
using System.Globalization;
using System.Threading;
using AvatarStroop.Core;

namespace AvatarStroop.Runtime
{
    // Development-only, owning-thread adapter around the actual Experiment.
    // It never starts, ticks, resumes or authorizes an experiment, and is not
    // wired to an app or network. Explicit failure can only pause a live active
    // phase and clear the live presentation layer. A source event and snapshot
    // are derived from the Experiment, not copied from an operator packet. The
    // constructor's caller must handle failure; use TryCreate for fail-closed
    // construction and continue calling its maintenance methods on the owner loop.
    public sealed class ViveAuthoritativeIntentCandidate
    {
        readonly Experiment experiment;
        readonly IClock clock;
        readonly Action clearLivePose;
        readonly int ownerThread=Thread.CurrentThread.ManagedThreadId;
        readonly int revision;
        readonly ViveCountdownIntentContext context;
        readonly ViveCountdownIntentCandidate candidate;
        readonly string interruptedTrialId;
        readonly int interruptedPlannedIndex;
        bool failed;
        int foreignThreadAccess;
        public bool RecoveryDecisionRequired {get{return failed||Interlocked.CompareExchange(ref foreignThreadAccess,0,0)!=0||candidate.RecoveryDecisionRequired;}}
        public bool CanEnterCountdown {get{return false;}}
        public bool CanResume {get{return false;}}
        public bool SharedClockObjectVerified {get{return false;}}
        public bool PreviousEpochVerified {get{return false;}}
        public bool PeerAuthenticated {get{return false;}}
        public bool PauseVerified {get;private set;}
        public string LastIssue {get;private set;}
        public string Challenge {get{return candidate.Challenge;}}
        // Development data only. This is not a proof that the caller's epoch,
        // mapping or device identity has been validated.
        public ViveCountdownIntentContext IntentContext {get{return context;}}

        public ViveAuthoritativeIntentCandidate(Experiment experiment,IClock clock,Action clearLivePose,
            string epoch,string previousEpoch,string mappingVersion,string operatorClock,
            bool explicitDevelopmentScope,bool explicitResumeIntent,double maximumAgeSeconds)
        {
            if(!explicitDevelopmentScope||experiment==null||clock==null||clearLivePose==null)
                throw new ArgumentException("Explicit development experiment, named clock and clear action required");
            if(experiment.Configuration.RunKind!=RunKind.Simulation)throw new InvalidOperationException("Apparatus is unavailable");
            this.experiment=experiment;this.clock=clock;this.clearLivePose=clearLivePose;
            revision=experiment.Events.Count;
            context=ReadContext(epoch,previousEpoch,mappingVersion,operatorClock,explicitResumeIntent);
            if(context.PausedFrom==Phase.MainTask){interruptedTrialId=experiment.CurrentTrial.TrialId;interruptedPlannedIndex=experiment.CurrentTrial.PlannedIndex;}
            candidate=new ViveCountdownIntentCandidate(context,clock.Now,maximumAgeSeconds);
        }
        // The caller must invoke this on the Experiment owner thread and keep
        // enforcing failure in its loop even when no candidate was produced.
        public static bool TryCreate(Experiment experiment,IClock clock,Action clearLivePose,string epoch,string previousEpoch,
            string mappingVersion,string operatorClock,bool explicitDevelopmentScope,bool explicitResumeIntent,double maximumAgeSeconds,
            out ViveAuthoritativeIntentCandidate result,out bool pauseVerified,out string issue)
        {
            result=null;pauseVerified=false;issue=null;
            if(!explicitDevelopmentScope){issue="development_scope_required";return false;}
            try
            {
                result=new ViveAuthoritativeIntentCandidate(experiment,clock,clearLivePose,epoch,previousEpoch,mappingVersion,operatorClock,
                    true,explicitResumeIntent,maximumAgeSeconds);return true;
            }
            catch(Exception e) when(!(e is OutOfMemoryException)&&!(e is StackOverflowException)&&!(e is AccessViolationException))
            {
                issue="construction_failed:"+e.GetType().Name;
                if(experiment!=null)
                {
                    try{if(experiment.LockedControl==ControlSelection.LiveWoz&&(experiment.Phase==Phase.Countdown||experiment.Phase==Phase.MainTask))experiment.Pause(Actor.Owner);}
                    catch(Exception error){issue+=";pause_failed:"+error.GetType().Name;}
                    try{pauseVerified=experiment.Phase==Phase.Paused||experiment.Phase==Phase.Ended;}
                    catch(Exception error){issue+=";pause_readback_failed:"+error.GetType().Name;}
                }
                if(clearLivePose!=null){try{clearLivePose();}catch(Exception error){issue+=";clear_failed:"+error.GetType().Name;}}
                return false;
            }
        }
        void Owner()
        {if(Thread.CurrentThread.ManagedThreadId!=ownerThread){Interlocked.Exchange(ref foreignThreadAccess,1);throw new InvalidOperationException("Authoritative task belongs to its owner thread");}}
        static string Field(JournalEvent e,string name)
        {string result;if(e==null||!e.Fields.TryGetValue(name,out result)||String.IsNullOrWhiteSpace(result))throw new InvalidOperationException("Required actual task event field is unavailable");return result;}
        ViveCountdownIntentContext ReadContext(string epoch,string previousEpoch,string mappingVersion,string operatorClock,bool explicitResumeIntent)
        {
            Owner();
            if(experiment.LockedControl!=ControlSelection.LiveWoz||experiment.BlockIndex<0||!experiment.CurrentCondition.HasValue)
                throw new InvalidOperationException("A currently locked LiveWoz source and actual block are required");
            var events=experiment.Events;
            if(events.Count==0||events[events.Count-1].ClockId!=clock.ClockId)
                throw new InvalidOperationException("The declared local clock ID must match actual experiment event metadata");
            JournalEvent source=null;
            for(int i=events.Count-1;i>=0;i--)
                if(events[i].Kind=="block_configuration_locked"&&events[i].BlockIndex==experiment.BlockIndex){source=events[i];break;}
            if(source==null||source.SessionId!=experiment.Configuration.SessionId||Field(source,"selection")!=ControlSelection.LiveWoz.ToString()||
                Field(source,"condition")!=experiment.CurrentCondition.Value.ToString())
                throw new InvalidOperationException("Actual block/source record does not match the experiment");
            string owner=Field(source,"owner");long confirmation;
            if(!Int64.TryParse(Field(source,"confirmation_event_sequence"),NumberStyles.None,CultureInfo.InvariantCulture,out confirmation)||
                confirmation<0||confirmation>=events.Count)throw new InvalidOperationException("Actual source confirmation record is unavailable");
            var recorded=events[(int)confirmation];
            int originalBlock=experiment.Configuration.RetainControlAcrossBlocks?0:experiment.BlockIndex;
            string expectedOrigin=experiment.Configuration.RetainControlAcrossBlocks&&experiment.BlockIndex>0?"retained_session":"explicit_owner_confirmation";
            if(recorded.Sequence!=confirmation||recorded.SessionId!=experiment.Configuration.SessionId||
                recorded.BlockIndex!=originalBlock||
                recorded.Kind!=(experiment.Configuration.RetainControlAcrossBlocks?"session_source_confirmed":"block_configuration_locked")||
                Field(source,"selection_origin")!=expectedOrigin||Field(source,"confirmation_block_index")!=originalBlock.ToString(CultureInfo.InvariantCulture)||
                Field(recorded,"owner")!=owner||Field(recorded,"selection")!=ControlSelection.LiveWoz.ToString())
                throw new InvalidOperationException("Source confirmation cannot be verified from actual task events");
            var view=experiment.View(Actor.Operator);var stimulus=view.Stimulus;
            var task=new TaskSnapshot(experiment.Configuration.SessionId,experiment.BlockIndex,experiment.Segment,
                view.TrialId,stimulus==null?(Colour?)null:stimulus.Word,stimulus==null?(Colour?)null:stimulus.Ink,
                experiment.Phase,experiment.CurrentCondition);
            Phase? pausedFrom=null;bool next=false;int segment=task.Segment;
            ViveCountdownIntent intent;
            if(task.Phase==Phase.BlockReady)
            {if(explicitResumeIntent)throw new InvalidOperationException("Cannot assert a resume while BlockReady");intent=ViveCountdownIntent.BeginBlock;}
            else if(task.Phase==Phase.Paused)
            {
                if(!explicitResumeIntent)throw new InvalidOperationException("Explicit recovery intent is required");
                JournalEvent transition=null;int transitionIndex=-1;
                for(int i=events.Count-1;i>=0;i--)
                    if(events[i].Kind=="phase_changed") {transition=events[i];transitionIndex=i;break;}
                Phase origin;
                if(transition==null||transitionIndex<1||Field(transition,"to")!=Phase.Paused.ToString()||
                    transition.SessionId!=task.SessionId||transition.BlockIndex!=task.BlockIndex||transition.Segment!=task.Segment||
                    events[transitionIndex-1].Kind!="explicit_pause"||events[transitionIndex-1].SessionId!=task.SessionId||
                    events[transitionIndex-1].BlockIndex!=task.BlockIndex||events[transitionIndex-1].Segment!=task.Segment||
                    !Enum.TryParse<Phase>(Field(transition,"from"),false,out origin)||(origin!=Phase.Countdown&&origin!=Phase.MainTask))
                    throw new InvalidOperationException("Actual supported pause origin cannot be verified");
                for(int i=transitionIndex+1;i<events.Count;i++)
                    if(events[i].Kind=="explicit_pause"||events[i].Kind=="explicit_resume")
                        throw new InvalidOperationException("An unmatched pause or resume event prevents recovery inference");
                pausedFrom=origin;intent=ViveCountdownIntent.ResumeCountdown;
                if(origin==Phase.MainTask)
                {
                    var trial=experiment.CurrentTrial;
                    if(trial==null||trial.IsPractice||trial.Outcome!=TrialOutcome.Interrupted||trial.SessionId!=task.SessionId||
                        trial.BlockIndex!=task.BlockIndex||trial.Segment!=task.Segment||trial.Condition!=task.Condition||trial.PlannedIndex<0||trial.PlannedIndex>=23)
                        throw new InvalidOperationException("Resume would not demonstrably enter the next countdown");
                    segment=checked(segment+1);next=true;
                }
            }
            else throw new InvalidOperationException("The actual experiment is neither BlockReady nor Paused");
            return new ViveCountdownIntentContext(task,intent,pausedFrom,segment,confirmation,owner,epoch,previousEpoch,
                ViveDevelopmentTransport.OperatorStream(mappingVersion,epoch),mappingVersion,clock.ClockId,operatorClock,true,true,
                explicitResumeIntent,next);
        }
        bool Current()
        {
            Owner();if(failed){MaintainFailure();return false;}
            if(Interlocked.CompareExchange(ref foreignThreadAccess,0,0)!=0){Fail("foreign_thread_access");return false;}
            try
            {
                if(experiment.Events.Count!=revision)throw new InvalidOperationException("Task event revision changed");
                var current=ReadContext(context.Epoch,context.PreviousEpoch,context.MappingVersion,context.OperatorClock,
                    context.Intent==ViveCountdownIntent.ResumeCountdown);
                if(!context.Same(current))throw new InvalidOperationException("Task context changed");
                if(context.PausedFrom==Phase.MainTask&&(experiment.CurrentTrial==null||experiment.CurrentTrial.TrialId!=interruptedTrialId||
                    experiment.CurrentTrial.PlannedIndex!=interruptedPlannedIndex))throw new InvalidOperationException("Interrupted trial identity changed");
                return true;
            }
            catch(Exception e){Fail("authoritative_context_changed:"+e.GetType().Name);return false;}
        }
        void Fail(string issue)
        {
            if(!failed)
            {failed=true;LastIssue=issue;try{candidate.SignalLoss(issue);}catch(Exception e){Append("candidate_loss_failed:"+e.GetType().Name);}}
            MaintainFailure();
        }
        void Append(string issue)
        {string suffix=";"+issue;if(LastIssue.IndexOf(suffix,StringComparison.Ordinal)<0)LastIssue+=suffix;}
        void MaintainFailure()
        {
            try
            {
                if(experiment.LockedControl==ControlSelection.LiveWoz&&(experiment.Phase==Phase.Countdown||experiment.Phase==Phase.MainTask))
                    experiment.Pause(Actor.Owner);
            }
            catch(Exception e){Append("pause_failed:"+e.GetType().Name);}
            try{PauseVerified=experiment.Phase==Phase.Paused||experiment.Phase==Phase.Ended;}
            catch(Exception e){PauseVerified=false;Append("pause_readback_failed:"+e.GetType().Name);}
            try{clearLivePose();}catch(Exception e){Append("clear_failed:"+e.GetType().Name);}
        }
        public bool Observe(ViveCountdownIntentEvidence evidence)
        {
            if(!Current())return false;
            try
            {
                bool accepted=candidate.Observe(context,evidence,clock.Now);
                if(candidate.RecoveryDecisionRequired)Fail("candidate_lost:"+candidate.LastIssue);
                return accepted&&!failed;
            }
            catch(Exception e){Fail("candidate_fault:"+e.GetType().Name);return false;}
        }
        public bool CandidateObserved()
        {
            if(!Current())return false;
            try
            {
                bool ready=candidate.CandidateReady(context,clock.Now);
                if(candidate.RecoveryDecisionRequired)Fail("candidate_lost:"+candidate.LastIssue);
                return ready&&!failed;
            }
            catch(Exception e){Fail("candidate_fault:"+e.GetType().Name);return false;}
        }
        public void SignalLoss(string reason)
        {Owner();if(String.IsNullOrWhiteSpace(reason))throw new ArgumentException("Explicit loss reason required");Fail("loss:"+reason);}
    }
}
