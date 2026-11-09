using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    // Simulation fixtures only: these tests create no devices, sockets, files,
    // persistent observations, research admission or live authority.
    public static class ViveIncrementalAggregatePumpTests
    {
        sealed class Clock : IClock
        {
            public double Time=0; public bool Fail; public Action Hook;
            public string ClockId { get { return "synthetic-aggregate-clock"; } }
            public double Now { get { var hook=Hook; Hook=null; if(hook!=null)hook(); if(Fail)throw new InvalidOperationException("synthetic-clock-failure"); return Time; } }
        }
        static SessionConfiguration Config(RunKind kind=RunKind.Simulation)
        {
            return new SessionConfiguration("synthetic-aggregate-session",42,
                new Allocation("aggregate-fixture",new[]{Condition.A,Condition.B,Condition.D,Condition.C},GazeTarget.Participant,new[]{0,1,2,3}),
                kind,"en",1,0,true,0,true,true,true);
        }
        static Experiment Experiment(Clock clock,int capacity=64,long budget=16L*1024*1024)
        { return new Experiment(Config(),clock,new ExperimentIncrementalOptions(capacity,128,budget)); }
        static ViveSyntheticAggregatePump Pump(Experiment experiment)
        { return new ViveSyntheticAggregatePump(experiment); }
        static ViveSyntheticAggregateHead Get(ViveSyntheticAggregatePump pump)
        { ViveSyntheticAggregateHead head; Check.True(pump.TryGet(out head)&&head!=null); return head; }
        static void Drain(ViveSyntheticAggregatePump pump,Experiment experiment)
        {
            Check.True(pump.Advance(256));var head=Get(pump);
            Check.True(experiment.IsIncrementalRevisionCurrent(head.Revision));Check.Equal(experiment.Phase,head.Snapshot.Task.Phase);
        }
        static void ReadyForSyntheticLive(Clock clock,Experiment e,ViveSyntheticAggregatePump pump)
        {
            Drain(pump,e);e.BeginFamiliarization(Actor.Owner);Drain(pump,e);e.BeginPractice(Actor.Owner);Drain(pump,e);
            for(int i=0;i<8;i++)
            {
                var trial=e.CurrentTrial;e.MarkPresented(trial.TrialId);Drain(pump,e);clock.Time+=.1;
                Check.True(e.ButtonChanged(trial.Stimulus.Ink,true));Drain(pump,e);e.ButtonChanged(trial.Stimulus.Ink,false);Drain(pump,e);
            }
            e.LockBlock(Actor.Owner,ControlSelection.LiveWoz,"synthetic-owner");Drain(pump,e);
            Check.Equal(Phase.BlockReady,e.Phase);
        }
        static void Unavailable(ViveSyntheticAggregatePump pump)
        {
            ViveSyntheticAggregateHead value; Check.True(pump.Terminal&&!pump.TryGet(out value)&&value==null);
            Check.True(!pump.CanEnterCountdown&&!pump.CanResume&&!pump.JournalDurable&&!pump.RealExperimentSourceBound);
        }
        static Queue<ExperimentIncrementalCommit> PendingForSyntheticCorruption(Experiment experiment)
        {
            var field=typeof(Experiment).GetField("incrementalMutationJournal",BindingFlags.Instance|BindingFlags.NonPublic);
            Check.True(field!=null); object journal=field.GetValue(experiment); Check.True(journal!=null);
            var queueField=journal.GetType().GetField("pending",BindingFlags.Instance|BindingFlags.NonPublic);
            Check.True(queueField!=null); return (Queue<ExperimentIncrementalCommit>)queueField.GetValue(journal);
        }
        public static void Register(TestSuite suite)
        {
            suite.Run("incremental aggregate binds only observed Simulation and keeps all gates closed",()=>{
                var clock=new Clock(); var ordinary=new Experiment(Config(),clock);
                Check.Throws<ArgumentException>(()=>Pump(ordinary));
                var e=Experiment(clock); var pump=Pump(e); Check.True(pump.Advance(1));
                var head=Get(pump); Check.Equal(0L,head.Revision); Check.Equal(Phase.Preparation,head.Snapshot.Task.Phase);
                Check.True(!head.IsRealAuthority&&!head.JournalDurable&&!pump.RealExperimentSourceBound&&!pump.JournalDurable&&!pump.CanEnterCountdown&&!pump.CanResume);
            });
            suite.Run("incremental aggregate hides backlog until every actual commit is drained",()=>{
                var clock=new Clock();var e=Experiment(clock);var pump=Pump(e);Check.True(pump.Advance(1));var original=Get(pump);
                e.BeginFamiliarization(Actor.Owner);e.BeginPractice(Actor.Owner);
                ViveSyntheticAggregateHead unavailable;Check.True(!pump.TryGet(out unavailable)&&unavailable==null&&!pump.Terminal);
                Check.True(!pump.Advance(1)&&!pump.Terminal);Check.True(!pump.TryGet(out unavailable)&&unavailable==null);
                Check.True(pump.Advance(1));var head=Get(pump);Check.True(head.Revision>original.Revision&&head.MutationStepsObserved>=2);
                Check.Equal(Phase.Practice,head.Snapshot.Task.Phase);Check.Equal(1,head.Snapshot.TrialCount);
                Check.Equal(e.CurrentTrial.TrialId,head.Snapshot.CurrentTrial.Value.TrialId);Check.True(e.IsIncrementalRevisionCurrent(head.Revision));
                Check.Equal(Phase.Preparation,original.Snapshot.Task.Phase);Check.True(!original.IsRealAuthority&&!original.JournalDurable);
            });
            suite.Run("incremental aggregate tracks a complete four block synthetic session",()=>{
                var clock=new Clock();var e=Experiment(clock);var pump=Pump(e);Drain(pump,e);
                e.BeginFamiliarization(Actor.Owner);Drain(pump,e);e.BeginPractice(Actor.Owner);Drain(pump,e);
                for(int i=0;i<8;i++)
                {
                    var trial=e.CurrentTrial;e.MarkPresented(trial.TrialId);Drain(pump,e);clock.Time+=.1;
                    Check.True(e.ButtonChanged(trial.Stimulus.Ink,true));Drain(pump,e);e.ButtonChanged(trial.Stimulus.Ink,false);Drain(pump,e);
                }
                for(int block=0;block<4;block++)
                {
                    if(block==0){e.LockBlock(Actor.Owner,ControlSelection.Automatic,"synthetic-owner");Drain(pump,e);e.BeginCountdown(Actor.Owner,true);Drain(pump,e);}
                    else{Check.True(e.TryBeginRetainedBlockCountdown(true));Drain(pump,e);}
                    clock.Time+=1;e.Tick();Drain(pump,e);Check.Equal(Phase.MainTask,e.Phase);
                    for(int i=0;i<24;i++)
                    {
                        var trial=e.CurrentTrial;e.MarkPresented(trial.TrialId);Drain(pump,e);clock.Time+=.1;
                        Check.True(e.ButtonChanged(trial.Stimulus.Ink,true));Drain(pump,e);e.ButtonChanged(trial.Stimulus.Ink,false);Drain(pump,e);
                    }
                    Check.Equal(Phase.Questionnaire,e.Phase);Check.True(e.AcknowledgeQuestionnaireInstruction());Drain(pump,e);
                    for(int value=1;value<=5;value++){clock.Time+=.1;e.Rate(value);Drain(pump,e);}
                    if(block<3){Check.True(e.Ready());Drain(pump,e);}
                }
                var final=Get(pump);Check.Equal(Phase.Ended,final.Snapshot.Task.Phase);Check.Equal(104,final.Snapshot.TrialCount);
                Check.Equal(20,e.Ratings.Count);Check.True(!pump.Terminal&&!pump.CanEnterCountdown&&!pump.JournalDurable);
            });
            suite.Run("incremental aggregate can refresh after a later legitimate mutation",()=>{
                var e=Experiment(new Clock());var pump=Pump(e);Check.True(pump.Advance(1));var old=Get(pump);
                e.BeginFamiliarization(Actor.Owner);ViveSyntheticAggregateHead head;
                Check.True(!pump.TryGet(out head)&&head==null&&!pump.Terminal);Check.True(pump.Advance(1));head=Get(pump);
                Check.True(head.Revision>old.Revision);Check.Equal(Phase.Familiarization,head.Snapshot.Task.Phase);
            });
            suite.Run("incremental aggregate reuses only current unchanged immutable heads",()=>{
                var e=Experiment(new Clock());var pump=Pump(e);Check.True(pump.Advance(1));var original=Get(pump);
                for(int i=0;i<10;i++)
                {Check.True(pump.Advance(1));var idle=Get(pump);Check.True(Object.ReferenceEquals(original,idle));Check.Equal(original.Publication,idle.Publication);}
                e.BeginFamiliarization(Actor.Owner);e.BeginPractice(Actor.Owner);ViveSyntheticAggregateHead value;
                Check.True(!pump.TryGet(out value)&&value==null);Check.True(!pump.Advance(1));Check.True(!pump.TryGet(out value)&&value==null);
                Check.True(pump.Advance(1));var updated=Get(pump);Check.True(!Object.ReferenceEquals(original,updated));
                Check.True(updated.Revision>original.Revision&&updated.Publication>original.Publication);
            });
            suite.Run("incremental aggregate hides its old head during actual Experiment clock callback",()=>{
                var clock=new Clock();var e=Experiment(clock);var pump=Pump(e);Check.True(pump.Advance(1));Get(pump);
                bool called=false,accepted=true;ViveSyntheticAggregateHead during=null;
                clock.Hook=()=>{called=true;accepted=pump.TryGet(out during);};
                e.RecordPreparation(Actor.Owner,"fixture","ready");
                Check.True(called&&!accepted&&during==null&&!pump.Terminal);
                Check.True(pump.Advance(1));var after=Get(pump);Check.True(after.Revision>0);
            });
            suite.Run("incremental aggregate detects another destructive reader",()=>{
                var e=Experiment(new Clock());var pump=Pump(e);Check.True(pump.Advance(1));e.BeginFamiliarization(Actor.Owner);
                ExperimentIncrementalCommit commit;ExperimentIncrementalSnapshot snapshot;long head;
                Check.True(e.TryReadIncrementalAfter(0,out commit,out head,out snapshot)&&commit!=null);
                Check.True(!pump.Advance(1));Unavailable(pump);Check.True(!pump.Advance(1));
            });
            suite.Run("incremental aggregate rejects queue overflow without exposing the prior head",()=>{
                var e=Experiment(new Clock(),1);var pump=Pump(e);Check.True(pump.Advance(1));e.BeginFamiliarization(Actor.Owner);e.BeginPractice(Actor.Owner);
                Check.True(e.IncrementalObservationFaulted&&pump.Terminal);string cause=pump.LastIssue;Check.True(!String.IsNullOrEmpty(cause));
                var foreign=new Thread(()=>{ViveSyntheticAggregateHead ignored;pump.TryGet(out ignored);});foreign.Start();Check.True(foreign.Join(5000));
                Check.Equal(cause,pump.LastIssue);Check.Equal("owner_thread_violation",pump.FailureContext);
                ViveSyntheticAggregateHead h;Check.True(!pump.TryGet(out h)&&h==null);Unavailable(pump);
            });
            suite.Run("incremental aggregate rejects producer budget exhaustion",()=>{
                var e=Experiment(new Clock(),64,110000);var pump=Pump(e);Check.True(pump.Advance(1));
                for(int i=0;i<64&&!e.IncrementalObservationFaulted;i++)
                {e.RecordPreparation(Actor.Owner,"fixture","ready");pump.Advance(1);}
                Check.True(e.IncrementalObservationFaulted);Check.True(!pump.Advance(1));Unavailable(pump);
            });
            suite.Run("incremental aggregate invalidates on producer clock failure and nested mutation",()=>{
                var clock=new Clock();var e=Experiment(clock);var pump=Pump(e);Check.True(pump.Advance(1));clock.Fail=true;
                Check.Throws<InvalidOperationException>(()=>e.BeginFamiliarization(Actor.Owner));Check.True(e.IncrementalObservationFaulted);
                Check.True(pump.Terminal&&pump.LastIssue!=null);
                Check.True(!pump.Advance(1));Check.Equal("source_faulted",pump.FailureContext);Unavailable(pump);
                var clock2=new Clock();var nested=Experiment(clock2);var other=Pump(nested);Check.True(other.Advance(1));
                clock2.Hook=()=>nested.Withdraw();nested.Tick();Check.True(nested.IncrementalObservationFaulted);
                Check.True(!other.Advance(1));Unavailable(other);
            });
            suite.Run("incremental aggregate fails closed on foreign thread access",()=>{
                var e=Experiment(new Clock());var pump=Pump(e);Check.True(pump.Advance(1));bool accepted=true;Exception error=null;
                var thread=new Thread(()=>{try{ViveSyntheticAggregateHead h;accepted=pump.TryGet(out h);}catch(Exception x){error=x;}});
                thread.Start();Check.True(thread.Join(5000));Check.True(error==null&&!accepted);Unavailable(pump);
            });
            suite.Run("incremental aggregate rejects corrupt later staged step and retains no visible head",()=>{
                var e=Experiment(new Clock());var pump=Pump(e);Check.True(pump.Advance(1));e.BeginFamiliarization(Actor.Owner);e.BeginPractice(Actor.Owner);
                Check.True(!pump.Advance(1)&&!pump.Terminal);
                var pending=PendingForSyntheticCorruption(e);Check.Equal(1,pending.Count);var actual=pending.Dequeue();
                Check.True(actual.Steps.Count>=2);var changed=new List<ExperimentIncrementalStep>(actual.Steps);
                var second=changed[1];changed[1]=new ExperimentIncrementalStep(second.Index+1,second.Before,second.After);
                pending.Enqueue(new ExperimentIncrementalCommit(actual.Revision,actual.Operation,changed.AsReadOnly(),actual.FinalState));
                Check.True(!pump.Advance(1));Unavailable(pump);Check.True(!pump.Advance(1));
            });
            suite.Run("incremental aggregate rejects broken snapshot reference chains and final heads",()=>{
                for(int kind=0;kind<2;kind++)
                {
                    var e=Experiment(new Clock());var pump=Pump(e);Check.True(pump.Advance(1));e.BeginFamiliarization(Actor.Owner);e.BeginPractice(Actor.Owner);
                    Check.True(!pump.Advance(1)&&!pump.Terminal);var pending=PendingForSyntheticCorruption(e);var actual=pending.Dequeue();
                    Check.True(actual.Steps.Count>=2);var changed=new List<ExperimentIncrementalStep>(actual.Steps);var second=changed[1];
                    if(kind==0)changed[1]=new ExperimentIncrementalStep(second.Index,e.InitialIncrementalSnapshot,second.After);
                    var final=kind==0?actual.FinalState:changed[0].After;
                    pending.Enqueue(new ExperimentIncrementalCommit(actual.Revision,actual.Operation,changed.AsReadOnly(),final));
                    Check.True(!pump.Advance(1));Unavailable(pump);
                }
            });
            suite.Run("incremental aggregate requires explicit current synthetic owner selection",()=>{
                var clock=new Clock();var e=Experiment(clock);var pump=Pump(e);
                Check.True(!pump.ConfirmSyntheticOwnerChoice(Actor.Owner));ReadyForSyntheticLive(clock,e,pump);
                var before=Get(pump);Check.Equal(ViveSyntheticChoiceState.AwaitingOwner,before.ChoiceState);
                Check.True(!pump.ConfirmSyntheticOwnerChoice(Actor.Participant)&&!pump.Terminal);
                Check.True(pump.ConfirmSyntheticOwnerChoice(Actor.Owner));var after=Get(pump);
                Check.Equal(ViveSyntheticChoiceState.ActiveCandidate,after.ChoiceState);Check.Equal(before.Revision,after.Revision);
                Check.True(after.Publication>before.Publication&&!Object.ReferenceEquals(before,after));
                Check.True(after.ContextEpoch>before.ContextEpoch);
                Check.True(Object.ReferenceEquals(after.Task,after.Snapshot.Task)&&!after.ContextProofAvailable&&!after.IsRealAuthority&&!after.JournalDurable);
                Check.Equal(ViveSyntheticChoiceState.AwaitingOwner,before.ChoiceState);Check.True(!pump.ConfirmSyntheticOwnerChoice(Actor.Owner));
                Check.Equal(after.MutationStepsObserved,(long)after.StepTrace.Count);
            });
            suite.Run("incremental aggregate invalidates synthetic context across pause and resumed segment",()=>{
                var clock=new Clock();var e=Experiment(clock);var pump=Pump(e);ReadyForSyntheticLive(clock,e,pump);
                Check.True(pump.ConfirmSyntheticOwnerChoice(Actor.Owner));long initial=Get(pump).ContextEpoch;
                e.BeginCountdown(Actor.Owner,true);Drain(pump,e);long countdown=Get(pump).ContextEpoch;Check.True(countdown>initial);
                clock.Time+=1;e.Tick();Drain(pump,e);long task=Get(pump).ContextEpoch;Check.True(task>countdown);
                e.Pause(Actor.Participant);Drain(pump,e);var paused=Get(pump);
                Check.Equal(Phase.Paused,paused.Task.Phase);Check.True(paused.ContextEpoch>task&&!paused.ContextProofAvailable);
                e.Resume(true);Drain(pump,e);var resumed=Get(pump);
                Check.Equal(Phase.Countdown,resumed.Task.Phase);Check.Equal(1,resumed.Task.Segment);
                Check.True(resumed.ContextEpoch>paused.ContextEpoch&&!resumed.ContextProofAvailable);
                Check.Equal(ViveSyntheticChoiceState.ActiveCandidate,resumed.ChoiceState);
            });
            suite.Run("incremental aggregate retains intermediate synthetic revocation even if final source returns",()=>{
                var clock=new Clock();var e=Experiment(clock);var pump=Pump(e);ReadyForSyntheticLive(clock,e,pump);
                Check.True(pump.ConfirmSyntheticOwnerChoice(Actor.Owner));var old=Get(pump);
                e.BeginCountdown(Actor.Owner,true);var pending=PendingForSyntheticCorruption(e);Check.Equal(1,pending.Count);var actual=pending.Dequeue();
                var originalFirst=actual.Steps[0];var altered=new List<ExperimentIncrementalStep>();var unselected=e.InitialIncrementalSnapshot;
                altered.Add(new ExperimentIncrementalStep(0,originalFirst.Before,unselected));
                altered.Add(new ExperimentIncrementalStep(1,unselected,originalFirst.After));
                for(int i=1;i<actual.Steps.Count;i++)altered.Add(new ExperimentIncrementalStep(i+1,actual.Steps[i].Before,actual.Steps[i].After));
                pending.Enqueue(new ExperimentIncrementalCommit(actual.Revision,actual.Operation,altered.AsReadOnly(),actual.FinalState));
                Check.True(pump.Advance(256));var head=Get(pump);
                Check.Equal(Phase.Countdown,head.Task.Phase);Check.Equal(ControlSelection.LiveWoz,head.Snapshot.Selection.Value);
                Check.Equal(ViveSyntheticChoiceState.Revoked,head.ChoiceState);Check.True(!pump.ConfirmSyntheticOwnerChoice(Actor.Owner));
                Check.True(head.ContextEpoch>old.ContextEpoch);Check.Equal(ViveSyntheticChoiceState.ActiveCandidate,old.ChoiceState);
                e.Withdraw();var terminalQueue=PendingForSyntheticCorruption(e);Check.Equal(1,terminalQueue.Count);var terminal=terminalQueue.Dequeue();
                var terminalSteps=new List<ExperimentIncrementalStep>(terminal.Steps);var ended=terminal.FinalState;
                terminalSteps.Add(new ExperimentIncrementalStep(terminalSteps.Count,ended,terminal.Steps[0].Before));
                terminalSteps.Add(new ExperimentIncrementalStep(terminalSteps.Count,terminal.Steps[0].Before,ended));
                terminalQueue.Enqueue(new ExperimentIncrementalCommit(terminal.Revision,terminal.Operation,terminalSteps.AsReadOnly(),ended));
                Check.True(!pump.Advance(256));Unavailable(pump);
            });
            suite.Run("incremental aggregate trace capacity closes all views without partial publication",()=>{
                var e=Experiment(new Clock());var pump=new ViveSyntheticAggregatePump(e,1);Check.True(pump.Advance(1));var old=Get(pump);
                e.BeginFamiliarization(Actor.Owner);Check.True(!pump.Advance(1));Unavailable(pump);
                Check.Equal("synthetic_trace_capacity_exceeded",pump.LastIssue);Check.Equal(0L,old.Revision);
                Check.True(!old.IsRealAuthority&&!old.JournalDurable&&!old.ContextProofAvailable);
            });
            suite.Run("incremental aggregate requires a valid bounded drain count",()=>{
                foreach(int invalid in new[]{0,-1,257}){var pump=Pump(Experiment(new Clock()));Check.True(!pump.Advance(invalid));Unavailable(pump);}
            });
        }
    }
}
