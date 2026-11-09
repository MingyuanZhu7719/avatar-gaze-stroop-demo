using System;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    public static class ViveAuthoritativeIntentCandidateTests
    {
        static Experiment Ready(ManualClock clock,ControlSelection selection=ControlSelection.LiveWoz)
        {var e=Fixtures.New(clock);Fixtures.Practice(e,clock,8);e.LockBlock(Actor.Owner,selection,"actual-owner");return e;}
        static ViveAuthoritativeIntentCandidate New(Experiment e,IClock c,Action clear=null,bool resume=false,string previous=null)
        {return new ViveAuthoritativeIntentCandidate(e,c,clear??(()=>{}),"new-epoch",previous,"mapping-v1","operator-clock",true,resume,.5);}
        static ViveCountdownIntentEvidence Evidence(ViveAuthoritativeIntentCandidate host,ManualClock clock,long sequence=1,bool valid=true)
        {double t=clock.Now;return new ViveCountdownIntentEvidence(host.IntentContext,host.Challenge,sequence,sequence,sequence,
            t+.05,t+.06,t+.07,t+.08,valid,valid);}
        sealed class StepClock : IClock
        {
            public double Seconds;public int Allowed=-1;public string ClockId {get{return "step-clock";}}
            public double Now {get{if(Allowed==0)throw new InvalidOperationException("synthetic clock failure");if(Allowed>0)Allowed--;return Seconds;}}
        }
        static Experiment StepReady(StepClock c)
        {
            var a=new Allocation("step",new[]{Condition.A,Condition.B,Condition.D,Condition.C},GazeTarget.Participant,new[]{0,1,2,3});
            var e=new Experiment(new SessionConfiguration("step-session",42,a,RunKind.Simulation,"en",1),c);
            e.BeginFamiliarization(Actor.Owner);e.BeginPractice(Actor.Owner);
            for(int i=0;i<8;i++){e.MarkPresented(e.CurrentTrial.TrialId);c.Seconds+=.1;var ink=e.CurrentTrial.Stimulus.Ink;e.ButtonChanged(ink,true);e.ButtonChanged(ink,false);}
            e.LockBlock(Actor.Owner,ControlSelection.LiveWoz,"step-owner");return e;
        }

        public static void Register(TestSuite suite)
        {
            suite.Run("VIVE authoritative intent binds actual Live source and never advances real experiment",()=>{
                var c=new ManualClock();var e=Ready(c);int revision=e.Events.Count;var h=New(e,c);
                Check.Equal("actual-owner",h.IntentContext.SourceOwner);Check.Equal(Phase.BlockReady,h.IntentContext.Task.Phase);
                Check.Equal(ControlSelection.LiveWoz,e.LockedControl.Value);Check.True(!h.CanEnterCountdown&&!h.CanResume&&!h.SharedClockObjectVerified&&!h.PreviousEpochVerified&&!h.PeerAuthenticated);
                var proof=Evidence(h,c);c.Advance(.1);Check.True(h.Observe(proof)&&h.CandidateObserved());
                Check.Equal(Phase.BlockReady,e.Phase);Check.Equal(revision,e.Events.Count);
            });
            suite.Run("VIVE authoritative intent rejects actual Automatic source and unconfirmed recovery",()=>{
                var c=new ManualClock();var automatic=Ready(c,ControlSelection.Automatic);Check.Throws<InvalidOperationException>(()=>New(automatic,c));
                var c2=new ManualClock();var e=Ready(c2);e.BeginCountdown(Actor.Owner,true);e.Pause(Actor.Owner);
                Check.Throws<InvalidOperationException>(()=>New(e,c2));Check.Throws<ArgumentException>(()=>New(e,c2,resume:true));
            });
            suite.Run("VIVE candidate change in live countdown pauses real experiment and freezes its timer",()=>{
                var c=new ManualClock();var e=Ready(c);int clears=0;var h=New(e,c,()=>clears++);
                e.BeginCountdown(Actor.Owner,true);c.Advance(.2);Check.True(!h.CandidateObserved());
                Check.Equal(Phase.Paused,e.Phase);Check.True(h.PauseVerified&&h.RecoveryDecisionRequired);Check.Equal(1,clears);
                double remaining=e.CountdownRemainingSeconds;c.Advance(4);Check.Near(remaining,e.CountdownRemainingSeconds);
                Check.True(!h.CandidateObserved()&&e.Phase==Phase.Paused);
            });
            suite.Run("VIVE recovery derives paused countdown origin from real events and never resumes",()=>{
                var c=new ManualClock();var e=Ready(c);e.BeginCountdown(Actor.Owner,true);c.Advance(.1);e.Pause(Actor.Owner);
                var h=New(e,c,resume:true,previous:"prior-epoch");Check.Equal(Phase.Countdown,h.IntentContext.PausedFrom.Value);
                Check.Equal(e.Segment,h.IntentContext.ExpectedCountdownSegment);int revision=e.Events.Count;
                var proof=Evidence(h,c);c.Advance(.1);Check.True(h.Observe(proof)&&h.CandidateObserved());
                Check.Equal(Phase.Paused,e.Phase);Check.Equal(revision,e.Events.Count);
            });
            foreach(bool presented in new[]{false,true})
            {
                bool wasPresented=presented;
                suite.Run("VIVE interrupted real main trial retains evidence and recovery predicts next segment presented="+wasPresented,()=>{
                    var c=new ManualClock();var e=Ready(c);e.BeginCountdown(Actor.Owner,true);c.Advance(1);e.Tick();
                    var trial=e.CurrentTrial;if(wasPresented)e.MarkPresented(trial.TrialId);e.Pause(Actor.Owner);
                    Check.Equal(TrialOutcome.Interrupted,trial.Outcome);var h=New(e,c,resume:true,previous:"prior-epoch");
                    Check.Equal(Phase.MainTask,h.IntentContext.PausedFrom.Value);Check.Equal(1,h.IntentContext.ExpectedCountdownSegment);
                    Check.Equal(wasPresented,trial.Onset.HasValue);Check.Equal(Phase.Paused,e.Phase);
                    var proof=Evidence(h,c);c.Advance(.1);Check.True(h.Observe(proof)&&h.CandidateObserved());
                    Check.Equal(TrialOutcome.Interrupted,trial.Outcome);Check.Equal(Phase.Paused,e.Phase);
                });
            }
            suite.Run("VIVE interrupted final main trial cannot claim a following countdown",()=>{
                var c=new ManualClock();var e=Ready(c);e.BeginCountdown(Actor.Owner,true);c.Advance(1);e.Tick();
                for(int i=0;i<23;i++)Fixtures.Answer(e,c);Check.Equal(23,e.CurrentTrial.PlannedIndex);e.Pause(Actor.Owner);
                Check.Throws<InvalidOperationException>(()=>New(e,c,resume:true,previous:"prior-epoch"));Check.Equal(Phase.Paused,e.Phase);
            });
            suite.Run("VIVE loss pauses actual main trial even if clearing pose fails",()=>{
                var c=new ManualClock();var e=Ready(c);int clears=0;var h=New(e,c,()=>{clears++;throw new InvalidOperationException();});
                e.BeginCountdown(Actor.Owner,true);c.Advance(1);e.Tick();e.MarkPresented(e.CurrentTrial.TrialId);var trial=e.CurrentTrial;
                h.SignalLoss("connection");Check.Equal(Phase.Paused,e.Phase);Check.Equal(TrialOutcome.Interrupted,trial.Outcome);
                Check.True(h.PauseVerified&&h.RecoveryDecisionRequired&&h.LastIssue.Contains("clear_failed"));Check.Equal(1,clears);
            });
            suite.Run("VIVE same clock name is not claimed as proof of a shared clock instance",()=>{
                var c=new ManualClock();var e=Ready(c);var different=new ManualClock{Now=c.Now};var h=New(e,different);
                Check.True(!h.SharedClockObjectVerified&&!h.CanEnterCountdown&&!h.CanResume);Check.Equal(Phase.BlockReady,e.Phase);
            });
            suite.Run("VIVE task event changes invalidate candidate without changing paused data",()=>{
                var c=new ManualClock();var e=Ready(c);e.BeginCountdown(Actor.Owner,true);e.Pause(Actor.Owner);
                var h=New(e,c,resume:true,previous:"prior-epoch");e.RecordHeadsetRemoval(Actor.Participant);
                Check.True(!h.CandidateObserved()&&h.RecoveryDecisionRequired&&h.PauseVerified);Check.Equal(Phase.Paused,e.Phase);
            });
            suite.Run("VIVE retained actual source record remains valid across blocks without another selection",()=>{
                var c=new ManualClock();var a=new Allocation("retained",new[]{Condition.A,Condition.B,Condition.D,Condition.C},GazeTarget.Participant,new[]{0,1,2,3});
                var cfg=new SessionConfiguration("retained-session",42,a,RunKind.Simulation,"en",1,0,false,0,false,true,true);
                var e=new Experiment(cfg,c);Fixtures.Practice(e,c,8);e.LockBlock(Actor.Owner,ControlSelection.LiveWoz,"retained-owner");
                var first=New(e,c);long sequence=first.IntentContext.SourceConfirmationSequence;
                e.BeginCountdown(Actor.Owner,true);c.Advance(1);e.Tick();for(int i=0;i<24;i++)Fixtures.Answer(e,c);
                Fixtures.Ratings(e,c);Check.Equal(Phase.Rest,e.Phase);Check.True(e.Ready());Check.Equal(1,e.BlockIndex);
                var second=New(e,c);Check.Equal(sequence,second.IntentContext.SourceConfirmationSequence);Check.Equal("retained-owner",second.IntentContext.SourceOwner);
                Check.Equal(Condition.B,second.IntentContext.Task.Condition.Value);Check.Equal(Phase.BlockReady,e.Phase);
            });
            suite.Run("VIVE failed pause is not reported as verified and still clears live pose",()=>{
                var c=new ManualClock();var e=Ready(c);int clears=0;var h=New(e,c,()=>clears++);
                e.BeginCountdown(Actor.Owner,true);c.Advance(1);e.Tick();c.Now=0;
                h.SignalLoss("clock_failure");Check.True(!h.PauseVerified&&h.RecoveryDecisionRequired&&h.LastIssue.Contains("pause_failed"));
                Check.Equal(Phase.MainTask,e.Phase);Check.Equal(1,clears);
            });
            suite.Run("VIVE foreign-thread host access is refused and latches",()=>{
                var c=new ManualClock();var e=Ready(c);var h=New(e,c);bool threw=false;
                var thread=new System.Threading.Thread(()=>{try{h.CandidateObserved();}catch(InvalidOperationException){threw=true;}});
                thread.Start();thread.Join();Check.True(threw&&h.RecoveryDecisionRequired&&!h.CandidateObserved());Check.Equal(Phase.BlockReady,e.Phase);
            });
            suite.Run("VIVE failed candidate keeps enforcing pause if another caller later starts a live countdown",()=>{
                var c=new ManualClock();var e=Ready(c);int clears=0;var h=New(e,c,()=>clears++);
                h.SignalLoss("disconnected");Check.True(h.RecoveryDecisionRequired&&!h.PauseVerified);Check.Equal(Phase.BlockReady,e.Phase);
                e.BeginCountdown(Actor.Owner,true);Check.True(!h.CandidateObserved());Check.Equal(Phase.Paused,e.Phase);
                Check.True(h.PauseVerified&&clears>=2);
            });
            suite.Run("VIVE fail-closed factory pauses active live engine even when construction fails",()=>{
                var c=new ManualClock();var e=Ready(c);e.BeginCountdown(Actor.Owner,true);int clears=0;bool paused;string issue;ViveAuthoritativeIntentCandidate result;
                Check.True(!ViveAuthoritativeIntentCandidate.TryCreate(e,c,()=>clears++,"new-epoch",null,"mapping-v1","operator-clock",true,false,.5,
                    out result,out paused,out issue));
                Check.True(result==null&&paused&&issue.Contains("construction_failed"));Check.Equal(Phase.Paused,e.Phase);Check.Equal(1,clears);
            });
            suite.Run("VIVE reads back paused state after failed phase log and refuses stale pause history",()=>{
                var c=new StepClock();var e=StepReady(c);var h=New(e,c);e.BeginCountdown(Actor.Owner,true);
                c.Allowed=3;h.SignalLoss("clock_log_failure");Check.Equal(Phase.Paused,e.Phase);
                Check.True(h.PauseVerified&&h.LastIssue.Contains("pause_failed"));
                c.Allowed=-1;Check.Throws<InvalidOperationException>(()=>New(e,c,resume:true,previous:"prior-epoch"));
            });
        }
    }
}
