using System;
using System.Collections.Generic;
using System.Reflection;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    // Simulation only; this suite does not access a device, network or file.
    public static class ViveAutomaticObservationIntegrationTests
    {
        sealed class Clock:IClock
        {
            public double Time;public int Reads;public bool Fail;public Action Hook;
            public string ClockId {get{return "paired-automatic-clock";}}
            public double Now {get{Reads++;var hook=Hook;Hook=null;if(hook!=null)hook();if(Fail)throw new InvalidOperationException("synthetic clock failure");return Time;}}
        }
        static SessionConfiguration Config()
        {return new SessionConfiguration("automatic-observation-fixture",42,new Allocation("fixture-BCAD",new[]{Condition.B,Condition.C,Condition.A,Condition.D},GazeTarget.Participant,new[]{0,1,2,3}),RunKind.Simulation,"en",3,30,true,0,true,true,true);}
        static Dictionary<ViveTrackpadDirection,Colour> Map()
        {return new Dictionary<ViveTrackpadDirection,Colour>{{ViveTrackpadDirection.Up,Colour.Red},{ViveTrackpadDirection.Right,Colour.Green},{ViveTrackpadDirection.Down,Colour.Blue},{ViveTrackpadDirection.Left,Colour.Yellow}};}
        static ViveDevelopmentParticipant New(Clock clock,bool observe,int traceLimit=4096,long bytes=16L*1024*1024,int capacity=4096)
        {
            if(!observe)return new ViveDevelopmentParticipant(Config(),clock,DevelopmentFixtures.Geometry(),Map(),.5,true);
            return new ViveDevelopmentParticipant(Config(),clock,DevelopmentFixtures.Geometry(),Map(),.5,true,new ExperimentIncrementalOptions(capacity,128,bytes),traceLimit);
        }
        static Experiment ExperimentOf(ViveDevelopmentParticipant participant)
        {
            var f=typeof(ViveDevelopmentParticipant).GetField("experiment",BindingFlags.Instance|BindingFlags.NonPublic);
            Check.True(f!=null);return (Experiment)f.GetValue(participant);
        }
        static VivePreviewTrackpadSample Sample(bool pressed=false,double x=0,double y=0,bool ready=true)
        {return new VivePreviewTrackpadSample(ready,pressed,x,y);}
        static VivePreviewTrackpadSample Down(Colour colour)
        {
            switch(colour){case Colour.Red:return Sample(true,0,1);case Colour.Green:return Sample(true,1,0);case Colour.Blue:return Sample(true,0,-1);case Colour.Yellow:return Sample(true,-1,0);default:throw new ArgumentOutOfRangeException();}
        }
        static void SameDouble(double? a,double? b,string name)
        {Check.True(a.HasValue==b.HasValue&&(!a.HasValue||BitConverter.DoubleToInt64Bits(a.Value)==BitConverter.DoubleToInt64Bits(b.Value)),name);}
        static void SameTrial(TrialRecord a,TrialRecord b)
        {
            Check.Equal(a.TrialId,b.TrialId);Check.Equal(a.SessionId,b.SessionId);Check.Equal(a.ListId,b.ListId);Check.Equal(a.IsPractice,b.IsPractice);
            Check.Equal(a.PracticeAttempt,b.PracticeAttempt);Check.Equal(a.BlockIndex,b.BlockIndex);Check.Equal(a.PlannedIndex,b.PlannedIndex);Check.Equal(a.Segment,b.Segment);
            Check.Equal(a.Condition,b.Condition);Check.Equal(a.Stimulus.Word,b.Stimulus.Word);Check.Equal(a.Stimulus.Ink,b.Stimulus.Ink);
            SameDouble(a.Onset,b.Onset,"onset");SameDouble(a.ResponseAt,b.ResponseAt,"response");SameDouble(a.ReactionSeconds,b.ReactionSeconds,"reaction");
            Check.Equal(a.Response,b.Response);Check.Equal(a.Correct,b.Correct);Check.Equal(a.Outcome,b.Outcome);Check.Equal(a.TechnicalIssue,b.TechnicalIssue);
        }
        static void SameView(TaskView a,TaskView b)
        {
            Check.Equal(a.Phase,b.Phase);Check.Equal(a.BlockIndex,b.BlockIndex);Check.Equal(a.Segment,b.Segment);Check.Equal(a.TrialId,b.TrialId);
            Check.True((a.Stimulus==null)==(b.Stimulus==null));if(a.Stimulus!=null){Check.Equal(a.Stimulus.Word,b.Stimulus.Word);Check.Equal(a.Stimulus.Ink,b.Stimulus.Ink);}
            Check.Equal(a.PracticeCorrection,b.PracticeCorrection);Check.Equal(a.QuestionnaireItem,b.QuestionnaireItem);
            Check.Equal(a.ParticipantAvatarVisible,b.ParticipantAvatarVisible);Check.Equal(a.OperatorAvatarVisible,b.OperatorAvatarVisible);
            Check.Equal(a.SourceLabel,b.SourceLabel);Check.Equal(a.ReadyEnabled,b.ReadyEnabled);Check.Equal(a.SourceContinuationPending,b.SourceContinuationPending);
        }
        sealed class Pair
        {
            public readonly Clock AClock=new Clock(),BClock=new Clock();
            public readonly ViveDevelopmentParticipant A,B;readonly Experiment a,b;
            public Pair(){A=New(AClock,false);B=New(BClock,true);a=ExperimentOf(A);b=ExperimentOf(B);Check.True(!A.ObservationEnabled&&B.ObservationEnabled);Verify();}
            public void Advance(double seconds){AClock.Time+=seconds;BClock.Time+=seconds;}
            public void Do(Action<ViveDevelopmentParticipant> action){action(A);action(B);Verify();}
            public void Verify()
            {
                Check.True(!B.ObservationFaulted&&!B.ObservationPending&&!B.ObservationBlocked,"Observation was unavailable: "+B.ObservationIssue);
                Check.Equal(A.Phase,B.Phase);Check.Equal(A.BlockIndex,B.BlockIndex);Check.Equal(A.Segment,B.Segment);Check.Equal(A.InputReady,B.InputReady);
                Check.Equal(A.LockedControl,B.LockedControl);Check.Equal(A.EndReason,B.EndReason);Check.Equal(A.CandidateRating,B.CandidateRating);
                SameView(A.ParticipantView,B.ParticipantView);SameView(A.OperatorView,B.OperatorView);
                Check.Equal(a.Events.Count,b.Events.Count,"event count");
                for(int i=0;i<a.Events.Count;i++)
                {
                    var x=a.Events[i];var y=b.Events[i];Check.Equal(x.Sequence,y.Sequence);SameDouble(x.Time,y.Time,"event time");
                    Check.Equal(x.ClockId,y.ClockId);Check.Equal(x.SessionId,y.SessionId);Check.Equal(x.BlockIndex,y.BlockIndex);Check.Equal(x.Segment,y.Segment);
                    Check.Equal(x.TrialId,y.TrialId);Check.Equal(x.Kind,y.Kind);Check.Equal(x.Fields.Count,y.Fields.Count);
                    foreach(var kv in x.Fields){string v;Check.True(y.Fields.TryGetValue(kv.Key,out v));Check.Equal(kv.Value,v);}
                }
                Check.Equal(a.Trials.Count,b.Trials.Count);for(int i=0;i<a.Trials.Count;i++)SameTrial(a.Trials[i],b.Trials[i]);
                Check.Equal(a.Ratings.Count,b.Ratings.Count);for(int i=0;i<a.Ratings.Count;i++)
                {var x=a.Ratings[i];var y=b.Ratings[i];Check.Equal(x.BlockIndex,y.BlockIndex);Check.Equal(x.ItemId,y.ItemId);Check.Equal(x.Value,y.Value);SameDouble(x.Time,y.Time,"rating time");Check.Equal(x.Language,y.Language);Check.Equal(x.Version,y.Version);}
                Check.Equal(AClock.Reads,BClock.Reads,"observation added clock reads");
            }
        }
        static void Aux(Pair pair,bool confirm=false,int rating=0,bool display=true)
        {pair.Do(p=>p.UpdateAuxiliary(confirm,false,false,rating,display));}
        static void Respond(Pair pair)
        {
            Colour ink=pair.A.CurrentTrial.Stimulus.Ink;string id=pair.A.CurrentTrial.TrialId;
            pair.Do(p=>p.UpdateTrackpad(true,Sample()));pair.Do(p=>p.MarkPresented(id));pair.Advance(.1);
            pair.Do(p=>p.UpdateTrackpad(true,Down(ink)));pair.Do(p=>p.UpdateTrackpad(true,Sample()));
        }
        static void Practice(Pair pair)
        {pair.Do(p=>p.UpdateTrackpad(true,Sample()));pair.Do(p=>p.OwnerBeginPractice(Actor.Owner));for(int i=0;i<8;i++)Respond(pair);Check.Equal(Phase.BlockPreparation,pair.A.Phase);}
        static ExperimentPersistentMutationBudget BudgetOf(Experiment experiment)
        {
            // Synthetic fault injection only; do not modify a real session.
            var journalField=typeof(Experiment).GetField("incrementalMutationJournal",BindingFlags.Instance|BindingFlags.NonPublic);
            Check.True(journalField!=null);var journal=journalField.GetValue(experiment);Check.True(journal!=null);
            var budgetField=journal.GetType().GetField("budget",BindingFlags.Instance|BindingFlags.NonPublic);Check.True(budgetField!=null);
            var budget=(ExperimentPersistentMutationBudget)budgetField.GetValue(journal);Check.True(budget!=null);
            return budget;
        }
        static void ExhaustSourceBudget(Experiment experiment)
        {
            var budget=BudgetOf(experiment);
            Check.True(!budget.TryReserve(budget.EstimatedBytesLimit));Check.True(budget.Terminal);
        }
        static bool IsHeld(Experiment experiment,Colour colour)
        {
            var field=typeof(Experiment).GetField("held",BindingFlags.Instance|BindingFlags.NonPublic);Check.True(field!=null);
            return ((HashSet<Colour>)field.GetValue(experiment)).Contains(colour);
        }
        static void SingleRespond(ViveDevelopmentParticipant p)
        {
            var trial=p.CurrentTrial;p.UpdateTrackpad(true,Sample());p.MarkPresented(trial.TrialId);
            p.UpdateTrackpad(true,Down(trial.Stimulus.Ink));p.UpdateTrackpad(true,Sample());
        }
        static void SingleAux(ViveDevelopmentParticipant p,bool confirm)
        {p.UpdateAuxiliary(confirm,false,false,0,true);}
        static void PrepareRetainedNextBlock(ViveDevelopmentParticipant p,Clock clock)
        {
            p.UpdateTrackpad(true,Sample());p.OwnerBeginPractice(Actor.Owner);for(int i=0;i<8;i++)SingleRespond(p);
            p.OwnerLockSource(Actor.Owner,ControlSelection.Automatic,"explicit-owner");p.OwnerBeginBlock(Actor.Owner);clock.Time+=3;p.Tick();
            for(int i=0;i<24;i++)SingleRespond(p);Check.Equal(Phase.Questionnaire,p.Phase);
            SingleAux(p,false);SingleAux(p,true);SingleAux(p,false);
            for(int i=0;i<5;i++){SingleAux(p,true);SingleAux(p,false);}
            Check.Equal(Phase.Rest,p.Phase);SingleAux(p,false);SingleAux(p,true);
            Check.Equal(Phase.BlockReady,p.Phase);Check.Equal(1,p.BlockIndex);Check.True(!p.ObservationBlocked);
        }

        public static void Register(TestSuite suite)
        {
            suite.Run("VIVE Automatic opt-in observation preserves complete four-block task and journal",()=>{
                var pair=new Pair();Practice(pair);pair.Do(p=>p.OwnerLockSource(Actor.Owner,ControlSelection.Automatic,"explicit-owner"));
                pair.Do(p=>p.OwnerBeginBlock(Actor.Owner));pair.Advance(3);pair.Do(p=>p.Tick());
                for(int block=0;block<4;block++)
                {
                    Check.Equal(Phase.MainTask,pair.A.Phase);Check.Equal(block,pair.A.BlockIndex);
                    Check.Equal(block!=3,pair.A.CurrentAvatar.AvatarVisible);Check.Equal(block!=3,pair.B.CurrentAvatar.AvatarVisible);
                    for(int i=0;i<24;i++)Respond(pair);
                    Check.Equal(Phase.Questionnaire,pair.A.Phase);Aux(pair);Aux(pair,true);Aux(pair,true);Aux(pair,false);
                    Aux(pair,false,-1);Aux(pair,false,0);Aux(pair,true);Check.Equal(block*5+1,pair.A.RatingCount);
                    for(int i=1;i<5;i++){Aux(pair,false);Aux(pair,true);}
                    if(block<3){Check.Equal(Phase.Rest,pair.A.Phase);Aux(pair,false);Aux(pair,true);pair.Do(p=>p.Tick());pair.Advance(3);pair.Do(p=>p.Tick());}
                }
                Check.Equal(Phase.Ended,pair.A.Phase);Check.Equal(EndReason.Completed,pair.A.EndReason.Value);Check.Equal(104,pair.A.TrialCount);Check.Equal(20,pair.A.RatingCount);pair.Verify();
            });
            suite.Run("VIVE Automatic observation requires explicit opt-in and keeps identical clock",()=>{
                var clock=new Clock();var normal=New(clock,false);Check.True(!normal.ObservationEnabled&&!normal.ObservationBlocked);
                Check.True(normal.SynchronizeObservation()&&normal.UsesEventClock(clock));
                var enabledClock=new Clock();var enabled=New(enabledClock,true);Check.True(enabled.ObservationEnabled&&!enabled.ObservationBlocked);
                Check.True(enabled.UsesEventClock(enabledClock)&&!enabled.UsesEventClock(clock));
                Check.True(!enabled.IsResearchSession&&!enabled.UsesNetwork&&!enabled.RecordsTaskFiles&&!enabled.RecordsEyeSamples);
                Check.Throws<InvalidOperationException>(()=>enabled.OwnerLockSource(Actor.Owner,ControlSelection.LiveWoz,"owner"));
            });
            suite.Run("VIVE Automatic observation blocks unapplied actions while backlog catches up",()=>{
                var clock=new Clock();var p=New(clock,true);p.UpdateTrackpad(true,Sample());var e=ExperimentOf(p);
                // Deliberately bypass the facade only on this isolated fixture to
                // reproduce a backlog greater than one bounded drain window.
                for(int i=0;i<20;i++)e.RecordPreparation(Actor.Owner,"fixture",i.ToString());
                int eventCount=e.Events.Count;Check.Throws<InvalidOperationException>(()=>p.OwnerBeginPractice(Actor.Owner));
                Check.Equal(Phase.Preparation,p.Phase);Check.Equal(eventCount,e.Events.Count);Check.True(p.ObservationPending&&!p.ObservationFaulted);
                Check.True(p.SynchronizeObservation());Check.True(!p.ObservationBlocked);
                p.OwnerBeginPractice(Actor.Owner);Check.Equal(Phase.Practice,p.Phase);Check.Equal(1,p.TrialCount);Check.True(!p.ObservationBlocked);
            });
            suite.Run("VIVE Automatic post-action observation fault does not replay and safety remains available",()=>{
                var clock=new Clock();var p=New(clock,true);p.UpdateTrackpad(true,Sample());var e=ExperimentOf(p);
                p.OwnerBeginPractice(Actor.Owner);Check.Equal(Phase.Practice,p.Phase);Check.Equal(1,p.TrialCount);var first=p.CurrentTrial;
                bool injected=false;clock.Hook=()=>{injected=true;ExhaustSourceBudget(e);};p.MarkPresented(first.TrialId);
                Check.True(injected&&first.Onset.HasValue&&p.ObservationFaulted&&p.ObservationBlocked);int count=e.Events.Count;
                Check.Throws<InvalidOperationException>(()=>p.MarkPresented(first.TrialId));Check.Equal(count,e.Events.Count);Check.Equal(1,p.TrialCount);
                p.OwnerPause(Actor.Owner);Check.Equal(Phase.Paused,p.Phase);Check.Equal(TrialOutcome.Interrupted,first.Outcome);
                p.ParticipantWithdraw();Check.Equal(Phase.Ended,p.Phase);Check.Equal(EndReason.Withdrawn,p.EndReason.Value);Check.True(p.ObservationFaulted);
            });
            suite.Run("VIVE Automatic clock exception preserves original result and permits later withdrawal",()=>{
                var baselineClock=new Clock();var observedClock=new Clock();var baseline=New(baselineClock,false);var observed=New(observedClock,true);
                baseline.UpdateTrackpad(true,Sample());observed.UpdateTrackpad(true,Sample());baselineClock.Fail=true;observedClock.Fail=true;
                Exception a=null,b=null;try{baseline.OwnerBeginPractice(Actor.Owner);}catch(Exception e){a=e;}try{observed.OwnerBeginPractice(Actor.Owner);}catch(Exception e){b=e;}
                Check.True(a is InvalidOperationException&&b is InvalidOperationException);Check.Equal(a.Message,b.Message);Check.Equal(baseline.Phase,observed.Phase);
                Check.Equal(ExperimentOf(baseline).Events.Count,ExperimentOf(observed).Events.Count);Check.True(observed.ObservationFaulted);
                baselineClock.Fail=false;observedClock.Fail=false;observed.ParticipantWithdraw();Check.Equal(Phase.Ended,observed.Phase);Check.Equal(EndReason.Withdrawn,observed.EndReason.Value);
            });
            suite.Run("VIVE Automatic source loss remains a safety action after observation failure",()=>{
                var p=New(new Clock(),true);p.UpdateTrackpad(true,Sample());p.OwnerBeginPractice(Actor.Owner);ExhaustSourceBudget(ExperimentOf(p));
                Check.True(p.ObservationFaulted);var trial=p.CurrentTrial;
                p.UpdateTrackpad(false,Sample());Check.True(!p.InputReady);Check.Equal(Phase.Paused,p.Phase);Check.Equal(TrialOutcome.Interrupted,trial.Outcome);
                p.ParticipantWithdraw();Check.Equal(Phase.Ended,p.Phase);
                var q=New(new Clock(),true);q.UpdateTrackpad(true,Sample());q.OwnerBeginPractice(Actor.Owner);ExhaustSourceBudget(ExperimentOf(q));
                Check.True(q.ObservationFaulted);q.UpdateNeutralPending(false);Check.Equal(Phase.Paused,q.Phase);Check.True(!q.InputReady);
                q.ParticipantWithdraw();Check.Equal(Phase.Ended,q.Phase);
            });
            suite.Run("VIVE Automatic observation fault permits safety edges but suppresses simultaneous confirm",()=>{
                var p=New(new Clock(),true);p.UpdateTrackpad(true,Sample());p.OwnerBeginPractice(Actor.Owner);ExhaustSourceBudget(ExperimentOf(p));
                Check.True(p.ObservationFaulted);var trial=p.CurrentTrial;
                p.UpdateAuxiliary(false,false,false,0,true);p.UpdateAuxiliary(true,true,false,0,true);
                Check.Equal(Phase.Paused,p.Phase);Check.Equal(TrialOutcome.Interrupted,trial.Outcome);
                p.UpdateAuxiliary(false,false,false,0,true);p.UpdateAuxiliary(true,false,true,0,true);
                Check.Equal(Phase.Ended,p.Phase);Check.Equal(EndReason.Withdrawn,p.EndReason.Value);Check.Equal(1,p.TrialCount);
            });
            suite.Run("VIVE Automatic composite practice stops after first mutation loses source",()=>{
                var clock=new Clock();var p=New(clock,true);p.UpdateTrackpad(true,Sample());var e=ExperimentOf(p);
                bool injected=false;clock.Hook=()=>{injected=true;ExhaustSourceBudget(e);};
                try{p.OwnerBeginPractice(Actor.Owner);}catch(InvalidOperationException){}
                Check.True(injected&&p.ObservationFaulted);Check.Equal(Phase.Familiarization,p.Phase);Check.Equal(0,p.TrialCount);
                foreach(var item in e.Events)Check.True(item.Kind!="practice_started","Unobserved second mutation was executed");
                p.ParticipantWithdraw();Check.Equal(Phase.Ended,p.Phase);Check.Equal(EndReason.Withdrawn,p.EndReason.Value);
            });
            suite.Run("VIVE Automatic retained countdown stops before tick after source failure",()=>{
                var expectedClock=new Clock();var actualClock=new Clock();var expected=New(expectedClock,true);var actual=New(actualClock,true);
                PrepareRetainedNextBlock(expected,expectedClock);PrepareRetainedNextBlock(actual,actualClock);
                var expectedExperiment=ExperimentOf(expected);var actualExperiment=ExperimentOf(actual);
                int expectedBefore=expectedClock.Reads,actualBefore=actualClock.Reads;
                Check.True(expectedExperiment.TryBeginRetainedBlockCountdown(true));int firstMutationReads=expectedClock.Reads-expectedBefore;
                bool injected=false;actualClock.Hook=()=>{injected=true;ExhaustSourceBudget(actualExperiment);};
                try{actual.Tick();}catch(InvalidOperationException){}
                Check.True(injected&&actual.ObservationFaulted);Check.Equal(Phase.Countdown,actual.Phase);
                Check.Equal(firstMutationReads,actualClock.Reads-actualBefore,"Tick executed after source became unavailable");
                actual.OwnerPause(Actor.Owner);Check.Equal(Phase.Paused,actual.Phase);actual.ParticipantWithdraw();Check.Equal(Phase.Ended,actual.Phase);
            });
            suite.Run("VIVE Automatic held release survives source failure but later press is blocked",()=>{
                var p=New(new Clock(),true);p.UpdateTrackpad(true,Sample());p.OwnerBeginPractice(Actor.Owner);
                p.UpdateTrackpad(true,Sample());p.UpdateTrackpad(true,Down(Colour.Red));var e=ExperimentOf(p);Check.True(IsHeld(e,Colour.Red));
                var budget=BudgetOf(e);long remaining=budget.EstimatedBytesLimit-budget.EstimatedBytesUsed;
                Check.True(remaining>1024&&budget.TryReserve(remaining-1024));
                p.UpdateTrackpad(true,Sample());Check.True(p.ObservationFaulted);Check.True(!IsHeld(e,Colour.Red));
                int eventCount=e.Events.Count;Check.Throws<InvalidOperationException>(()=>p.UpdateTrackpad(true,Down(Colour.Blue)));
                Check.True(!IsHeld(e,Colour.Blue));Check.Equal(eventCount,e.Events.Count);p.ParticipantWithdraw();Check.Equal(Phase.Ended,p.Phase);
            });
            suite.Run("VIVE Automatic prior foreign-thread violation prevents ordinary action",()=>{
                var p=New(new Clock(),true);p.UpdateTrackpad(true,Sample());var e=ExperimentOf(p);bool accepted=true;
                var thread=new System.Threading.Thread(()=>accepted=p.SynchronizeObservation());thread.Start();Check.True(thread.Join(5000));
                Check.True(!accepted&&p.ObservationFaulted);int count=e.Events.Count;
                Check.Throws<InvalidOperationException>(()=>p.OwnerBeginPractice(Actor.Owner));
                Check.Equal(Phase.Preparation,p.Phase);Check.Equal(count,e.Events.Count);Check.Equal(0,p.TrialCount);
                p.ParticipantWithdraw();Check.Equal(Phase.Ended,p.Phase);Check.Equal(EndReason.Withdrawn,p.EndReason.Value);
            });
            suite.Run("VIVE Automatic foreign-thread violation during action preserves prefix without retry",()=>{
                var clock=new Clock();var p=New(clock,true);p.UpdateTrackpad(true,Sample());var e=ExperimentOf(p);bool accepted=true,injected=false;
                clock.Hook=()=>{
                    var thread=new System.Threading.Thread(()=>accepted=p.SynchronizeObservation());thread.Start();
                    Check.True(thread.Join(5000));injected=true;
                };
                try{p.OwnerBeginPractice(Actor.Owner);}catch(InvalidOperationException){}
                Check.True(injected&&!accepted&&p.ObservationFaulted);Check.Equal(Phase.Familiarization,p.Phase);Check.Equal(0,p.TrialCount);
                int count=e.Events.Count;Check.Throws<InvalidOperationException>(()=>p.OwnerBeginPractice(Actor.Owner));Check.Equal(count,e.Events.Count);
                foreach(var item in e.Events)Check.True(item.Kind!="practice_started");
                p.ParticipantWithdraw();Check.Equal(Phase.Ended,p.Phase);Check.Equal(EndReason.Withdrawn,p.EndReason.Value);
            });
            suite.Run("VIVE Automatic action CAS rejects fault after synchronization but before start",()=>{
                var p=New(new Clock(),true);p.UpdateTrackpad(true,Sample());Check.True(p.SynchronizeObservation());
                var begin=typeof(ViveDevelopmentParticipant).GetMethod("BeginOrdinaryObservedAction",BindingFlags.Instance|BindingFlags.NonPublic);
                Check.True(begin!=null);bool accepted=true;var thread=new System.Threading.Thread(()=>accepted=p.SynchronizeObservation());
                thread.Start();Check.True(thread.Join(5000));Check.True(!accepted&&p.ObservationFaulted);
                Check.True(!(bool)begin.Invoke(p,null));Check.Equal(Phase.Preparation,p.Phase);Check.Equal(0,p.TrialCount);
                p.ParticipantWithdraw();Check.Equal(Phase.Ended,p.Phase);
            });
            suite.Run("VIVE Automatic observation is blocked during task action and clears on completion",()=>{
                var clock=new Clock();var p=New(clock,true);p.UpdateTrackpad(true,Sample());bool blockedInside=false,called=false;
                clock.Hook=()=>{called=true;blockedInside=p.ObservationBlocked;};p.OwnerBeginPractice(Actor.Owner);
                Check.True(called&&blockedInside);Check.Equal(Phase.Practice,p.Phase);
                Check.True(!p.ObservationFaulted&&!p.ObservationBlocked&&!p.ObservationPending);
            });
            suite.Run("VIVE Automatic safety pauses preserve actor after observer failure",()=>{
                foreach(bool participant in new[]{false,true})
                {
                    var p=New(new Clock(),true);p.UpdateTrackpad(true,Sample());p.OwnerBeginPractice(Actor.Owner);var e=ExperimentOf(p);
                    ExhaustSourceBudget(e);Check.True(p.ObservationFaulted);
                    if(participant)p.ParticipantPause();else p.OwnerPause(Actor.Owner);
                    Check.Equal(Phase.Paused,p.Phase);string actor=null;int pauses=0;
                    foreach(var item in e.Events)if(item.Kind=="explicit_pause"){pauses++;Check.True(item.Fields.TryGetValue("actor",out actor));}
                    Check.Equal(1,pauses);Check.Equal(participant?"Participant":"Owner",actor);
                    p.ParticipantWithdraw();Check.Equal(Phase.Ended,p.Phase);Check.Equal(EndReason.Withdrawn,p.EndReason.Value);
                }
            });
        }
    }
}
