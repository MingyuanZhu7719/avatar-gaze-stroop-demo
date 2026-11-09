using System;
using System.Threading;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    public static class ExperimentMutationObservationTests
    {
        sealed class Clock:IClock
        {
            public double Time;public bool Fail;public Action Hook;public int Reads;
            public string ClockId{get{return "mutation-clock";}}
            public double Now{get{Reads++;var hook=Hook;Hook=null;if(hook!=null)hook();if(Fail)throw new InvalidOperationException("synthetic clock fault");return Time;}}
        }
        static SessionConfiguration Config()
        {return new SessionConfiguration("mutation-session",42,new Allocation("mutation-allocation",new[]{Condition.A,Condition.B,Condition.D,Condition.C},GazeTarget.Participant,new[]{0,1,2,3}),RunKind.Simulation,"en",1,0);}
        static Experiment New(Clock clock,int capacity=4096){return new Experiment(Config(),clock,true,capacity);}
        static long Practice(Experiment e,Clock c,ViveExperimentMutationCursor cursor=null)
        {
            long revision=0;
            Action sync=()=>{if(cursor!=null)Drain(cursor);else DrainProducer(e,ref revision);};
            e.BeginFamiliarization(Actor.Owner);sync();e.BeginPractice(Actor.Owner);sync();
            for(int i=0;i<8;i++){var trial=e.CurrentTrial;e.MarkPresented(trial.TrialId);sync();c.Time+=.1;e.ButtonChanged(trial.Stimulus.Ink,true);sync();e.ButtonChanged(trial.Stimulus.Ink,false);sync();}
            return revision;
        }
        static void DrainProducer(Experiment e,ref long revision)
        {ExperimentMutationCommit next;ExperimentMutationSnapshot current;long head;do{Check.True(e.TryReadMutationAfter(revision,out next,out head,out current));if(next!=null)revision=next.Revision;}while(next!=null);}
        static void Drain(ViveExperimentMutationCursor cursor)
        {for(int i=0;i<4097;i++)if(cursor.Advance())return;throw new Exception("Mutation cursor did not catch up: "+cursor.LastIssue);}
        public static void Register(TestSuite suite)
        {
            suite.Run("experiment mutation observation is opt-in and source cursor requires a current read",()=>{
                var c=new Clock();var ordinary=new Experiment(Config(),c);Check.True(!ordinary.MutationObservationEnabled);
                Check.Throws<ArgumentException>(()=>new ViveExperimentMutationCursor(ordinary));
                var clock=new Clock();var e=New(clock);e.BeginFamiliarization(Actor.Owner);
                var cursor=new ViveExperimentMutationCursor(e);Check.True(cursor.RealExperimentSourceBound&&!cursor.CaughtUp&&cursor.Current==null);
                Drain(cursor);Check.Equal(Phase.Familiarization,cursor.Current.Task.Phase);Check.True(!cursor.CanEnterCountdown&&!cursor.CanResume);
                e.BeginPractice(Actor.Owner);Check.True(!cursor.CaughtUp&&cursor.Current==null);
                Drain(cursor);Check.Equal(Phase.Practice,cursor.Current.Task.Phase);Check.True(cursor.CaughtUp&&cursor.CaughtUp);
            });
            suite.Run("experiment mutation observation tracks held edges without journal events",()=>{
                var c=new Clock();var e=New(c);var cursor=new ViveExperimentMutationCursor(e);Drain(cursor);long start=cursor.Revision;int events=e.Events.Count;
                Check.True(!e.ButtonChanged(Colour.Red,true));Check.True(!cursor.CaughtUp);Drain(cursor);Check.Equal(start+1,cursor.Revision);Check.Equal(events,e.Events.Count);
                Check.True(!e.ButtonChanged(Colour.Red,true));Check.True(cursor.CaughtUp);Check.Equal(start+1,cursor.Revision);
                Check.True(!e.ButtonChanged(Colour.Red,false));Check.True(!cursor.CaughtUp);Drain(cursor);Check.Equal(start+2,cursor.Revision);
            });
            suite.Run("experiment mutation Tick preserves ordered phase and new trial steps",()=>{
                var c=new Clock();var e=New(c);long after=Practice(e,c);e.LockBlock(Actor.Owner,ControlSelection.Automatic,"owner");DrainProducer(e,ref after);e.BeginCountdown(Actor.Owner,true);
                long head;ExperimentMutationCommit next;ExperimentMutationSnapshot state;
                do{Check.True(e.TryReadMutationAfter(after,out next,out head,out state));if(next!=null)after=next.Revision;}while(next!=null);
                c.Time+=1;e.Tick();Check.True(e.TryReadMutationAfter(after,out next,out head,out state));Check.True(next!=null&&next.Operation=="Tick");
                bool sawEmptyMain=false,sawNewTrial=false;
                foreach(var step in next.Steps){if(step.After.Task.Phase==Phase.MainTask&&step.After.Task.TrialId==null)sawEmptyMain=true;if(step.After.Task.TrialId!=null)sawNewTrial=true;}
                Check.True(sawEmptyMain&&sawNewTrial&&next.FinalState.Task.TrialId!=null);Check.Equal(head,next.Revision);
            });
            suite.Run("experiment mutation backlog overflow latches without changing task behavior",()=>{
                var c=new Clock();var e=New(c,1);e.BeginFamiliarization(Actor.Owner);e.BeginPractice(Actor.Owner);
                Check.Equal(Phase.Practice,e.Phase);Check.True(e.MutationObservationFaulted);ExperimentMutationCommit next;ExperimentMutationSnapshot current;long head;
                Check.True(!e.TryReadMutationAfter(0,out next,out head,out current));
            });
            suite.Run("experiment mutation exceptions and clock reentrancy latch without replacing engine outcome",()=>{
                var clock=new Clock();var e=New(clock);clock.Fail=true;
                Check.Throws<InvalidOperationException>(()=>e.BeginFamiliarization(Actor.Owner));Check.True(e.MutationObservationFaulted);
                var c=new Clock();var other=New(c);c.Hook=()=>other.Withdraw();other.Tick();Check.True(other.MutationObservationFaulted);Check.Equal(Phase.Ended,other.Phase);
            });
            suite.Run("experiment mutation cursor rejects foreign thread read and never enables live",()=>{
                var c=new Clock();var e=New(c);var cursor=new ViveExperimentMutationCursor(e);Drain(cursor);bool result=true;
                var thread=new Thread(()=>result=cursor.CaughtUp);thread.Start();thread.Join();Check.True(!result&&cursor.Terminal&&!cursor.CanEnterCountdown&&!cursor.CanResume);
            });
            suite.Run("experiment mutation journal serializes owner head read and foreign failure",()=>{
                var c=new Clock();var e=New(c);var field=typeof(Experiment).GetField("mutationJournal",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic);
                Check.True(field!=null);var journal=field.GetValue(e);var syncField=journal.GetType().GetField("sync",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic);
                Check.True(syncField!=null);var gate=syncField.GetValue(journal);bool foreign=true;
                using(var attempted=new ManualResetEvent(false))
                {
                    var thread=new Thread(()=>{attempted.Set();foreign=e.IsMutationRevisionCurrent(0);});Monitor.Enter(gate);
                    try{thread.Start();Check.True(attempted.WaitOne(5000));Check.True(e.IsMutationRevisionCurrent(0));}
                    finally{Monitor.Exit(gate);}
                    Check.True(thread.Join(5000));Check.True(!foreign&&e.MutationObservationFaulted&&!e.IsMutationRevisionCurrent(0));
                }
            });
            suite.Run("experiment mutation records and steps cannot be rewritten by same-assembly properties",()=>{
                foreach(var type in new[]{typeof(ExperimentMutationCommit),typeof(ExperimentMutationStep)})foreach(var property in type.GetProperties())
                {var setter=property.GetSetMethod(true);Check.True(setter==null||setter.IsPrivate);}
            });
            suite.Run("experiment mutation matcher rejects missing nonnullable default values",()=>{
                var c=new Clock();var e=New(c);var valid=e.InitialMutationSnapshot;Check.True(e.MatchesMutationSnapshot(valid));
                var values=new object[valid.ValueCount];for(int i=0;i<values.Length;i++)values[i]=valid.ValueAt(i);
                int phaseIndex=13+4+4+2*4*24;values[phaseIndex]=null;
                var bad=new ExperimentMutationSnapshot(valid.Task,valid.Selection,valid.SourceOwner,valid.SourceConfirmationSequence,valid.PausedFrom,values);
                Check.True(!e.MatchesMutationSnapshot(bad));
            });
            suite.Run("experiment mutation snapshots distinguish timestamp sign bits",()=>{
                var c=new Clock();var e=New(c);var original=e.InitialMutationSnapshot;
                var values=new object[original.ValueCount];int firstZero=-1,dynamicStart=13+4+4+2*4*24;
                for(int i=0;i<values.Length;i++){values[i]=original.ValueAt(i);if(i>=dynamicStart&&firstZero<0&&values[i] is double&&(double)values[i]==0)firstZero=i;}
                Check.True(firstZero>=0);values[firstZero]=BitConverter.Int64BitsToDouble(unchecked((long)0x8000000000000000UL));
                var changed=new ExperimentMutationSnapshot(original.Task,original.Selection,original.SourceOwner,original.SourceConfirmationSequence,original.PausedFrom,values);
                Check.True(!e.MatchesMutationSnapshot(changed)&&!original.Same(changed));
            });
            suite.Run("experiment mutation cursor never asserts live choice from task source lock",()=>{
                var c=new Clock();var e=New(c);var cursor=new ViveExperimentMutationCursor(e);Practice(e,c,cursor);Drain(cursor);
                e.LockBlock(Actor.Owner,ControlSelection.LiveWoz,"owner");Check.True(!cursor.CaughtUp);Drain(cursor);
                Check.Equal(ControlSelection.LiveWoz,cursor.Current.Selection.Value);Check.True(!cursor.Current.ChoiceActive&&!cursor.CanEnterCountdown);
            });
            suite.Run("experiment mutation cursor does not expose state during an active or reentrant operation",()=>{
                var c=new Clock();var e=New(c);var cursor=new ViveExperimentMutationCursor(e);Drain(cursor);bool visible=true;
                c.Hook=()=>visible=cursor.CaughtUp;e.Tick();Check.True(!visible&&cursor.CaughtUp&&!cursor.Terminal);
            });
            suite.Run("experiment mutation read cursor mismatch is terminal and cannot be repaired",()=>{
                var c=new Clock();var e=New(c);e.BeginFamiliarization(Actor.Owner);ExperimentMutationCommit next;ExperimentMutationSnapshot current;long head;
                Check.True(!e.TryReadMutationAfter(1,out next,out head,out current)&&e.MutationObservationFaulted);
                Check.True(!e.TryReadMutationAfter(0,out next,out head,out current));
            });
            suite.Run("experiment mutation traces practice correction pause resume and technical end",()=>{
                var c=new Clock();var e=New(c);var cursor=new ViveExperimentMutationCursor(e);
                e.RecordPreparation(Actor.Owner,"check","done");e.RecordHeadsetRemoval(Actor.Owner);e.BeginFamiliarization(Actor.Owner);e.BeginPractice(Actor.Owner);
                var first=e.CurrentTrial;e.MarkPresented(first.TrialId);c.Time+=.1;var wrong=(Colour)(((int)first.Stimulus.Ink+1)%4);
                Check.True(e.ButtonChanged(wrong,true));e.ButtonChanged(wrong,false);Check.Equal(Phase.PracticeCorrection,e.Phase);e.ContinuePractice();
                e.FlagTechnicalIssue("fixture");e.Pause(Actor.Owner);Check.Equal(Phase.Paused,e.Phase);e.Resume(true);Check.Equal(Phase.Practice,e.Phase);
                e.EndTechnical(Actor.Owner,"fixture");Check.Equal(Phase.Ended,e.Phase);Drain(cursor);
                Check.True(!e.MutationObservationFaulted&&cursor.Current.Task.Phase==Phase.Ended);
            });
            suite.Run("experiment mutation traces retained-source nested countdown and explicit questionnaire acknowledgement",()=>{
                var c=new Clock();var cfg=new SessionConfiguration("mutation-session",42,new Allocation("mutation-allocation",new[]{Condition.A,Condition.B,Condition.D,Condition.C},GazeTarget.Participant,new[]{0,1,2,3}),RunKind.Simulation,"en",1,0,true,0,true,true,true);
                var e=new Experiment(cfg,c,true,4096);var cursor=new ViveExperimentMutationCursor(e);Practice(e,c,cursor);e.LockBlock(Actor.Owner,ControlSelection.Automatic,"owner");Drain(cursor);e.BeginCountdown(Actor.Owner,true);Drain(cursor);c.Time+=1;e.Tick();Drain(cursor);
                for(int i=0;i<24;i++){var trial=e.CurrentTrial;e.MarkPresented(trial.TrialId);Drain(cursor);c.Time+=.1;e.ButtonChanged(trial.Stimulus.Ink,true);Drain(cursor);e.ButtonChanged(trial.Stimulus.Ink,false);Drain(cursor);}
                Check.Equal(Phase.Questionnaire,e.Phase);Check.True(e.AcknowledgeQuestionnaireInstruction());Drain(cursor);for(int i=1;i<=5;i++){e.Rate(i);Drain(cursor);}
                Check.True(e.Ready());Check.Equal(Phase.BlockReady,e.Phase);Check.True(e.TryBeginRetainedBlockCountdown(true));Check.Equal(Phase.Countdown,e.Phase);
                Drain(cursor);Check.True(!e.MutationObservationFaulted&&cursor.Current.Task.Phase==Phase.Countdown);
            });
            suite.Run("experiment mutation traces main-task pause and next-segment resume",()=>{
                var c=new Clock();var e=New(c);var cursor=new ViveExperimentMutationCursor(e);Practice(e,c,cursor);e.LockBlock(Actor.Owner,ControlSelection.Automatic,"owner");Drain(cursor);e.BeginCountdown(Actor.Owner,true);Drain(cursor);c.Time+=1;e.Tick();Drain(cursor);
                e.Pause(Actor.Owner);Check.Equal(Phase.Paused,e.Phase);e.Resume(true);Check.Equal(1,e.Segment);Check.Equal(Phase.Countdown,e.Phase);
                Drain(cursor);Check.True(!e.MutationObservationFaulted&&cursor.Current.Task.Segment==1);
            });
            suite.Run("experiment mutation snapshot budget latches before unbounded backlog growth",()=>{
                var c=new Clock();var e=New(c,4096);int operations=0;
                while(!e.MutationObservationFaulted&&operations<2000){e.RecordHeadsetRemoval(Actor.Owner);operations++;}
                Check.True(e.MutationObservationFaulted&&e.MutationObservationIssue=="snapshot_budget_exhausted"&&operations<2000);
                e.BeginFamiliarization(Actor.Owner);Check.Equal(Phase.Familiarization,e.Phase);
            });
        }
    }
}
