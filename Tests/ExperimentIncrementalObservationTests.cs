using System;
using AvatarStroop.Core;

namespace AvatarStroop.Tests
{
    // Synthetic simulation only. This compares the existing full observation
    // against the independent incremental producer; neither admits a study.
    public static class ExperimentIncrementalObservationTests
    {
        sealed class Clock:IClock
        {
            public double Time;public int Reads;public bool Fail;public Action Hook;
            public string ClockId {get{return "incremental-diff-clock";}}
            public double Now {get{Reads++;var action=Hook;Hook=null;if(action!=null)action();if(Fail)throw new InvalidOperationException("synthetic clock failure");return Time;}}
        }
        static SessionConfiguration Config()
        {
            return new SessionConfiguration("incremental-diff-session",42,
                new Allocation("incremental-diff-allocation",new[]{Condition.A,Condition.B,Condition.D,Condition.C},GazeTarget.Participant,new[]{0,1,2,3}),
                RunKind.Simulation,"en",1,0,true,0,true,true,true);
        }
        static bool EqualObject(object a,object b)
        {
            if(a==null||b==null)return a==null&&b==null;
            if(a.GetType()!=b.GetType())return false;
            if(a is double)return BitConverter.DoubleToInt64Bits((double)a)==BitConverter.DoubleToInt64Bits((double)b);
            return Object.Equals(a,b);
        }
        static void SameSnapshot(ExperimentMutationSnapshot full,ExperimentIncrementalSnapshot incremental)
        {
            Check.True(full!=null&&incremental!=null,"Both producer snapshots must be available");
            var values=incremental.ExportReferenceValuesForTest();
            Check.True(values!=null,"Incremental reference export unavailable");Check.Equal(full.ValueCount,values.Length,"Snapshot value count differs");
            for(int i=0;i<values.Length;i++)Check.True(EqualObject(full.ValueAt(i),values[i]),"Snapshot value differs at index "+i);
        }
        static void SameDouble(double? a,double? b,string field)
        {Check.True(a.HasValue==b.HasValue&&(!a.HasValue||BitConverter.DoubleToInt64Bits(a.Value)==BitConverter.DoubleToInt64Bits(b.Value)),field+" differs");}
        static void SameTrial(TrialRecord a,TrialRecord b)
        {
            Check.Equal(a.TrialId,b.TrialId);Check.Equal(a.SessionId,b.SessionId);Check.Equal(a.ListId,b.ListId);Check.Equal(a.IsPractice,b.IsPractice);
            Check.Equal(a.PracticeAttempt,b.PracticeAttempt);Check.Equal(a.BlockIndex,b.BlockIndex);Check.Equal(a.PlannedIndex,b.PlannedIndex);Check.Equal(a.Segment,b.Segment);
            Check.Equal(a.Condition,b.Condition);Check.Equal(a.Stimulus.Word,b.Stimulus.Word);Check.Equal(a.Stimulus.Ink,b.Stimulus.Ink);
            SameDouble(a.Onset,b.Onset,"onset");SameDouble(a.ResponseAt,b.ResponseAt,"response time");SameDouble(a.ReactionSeconds,b.ReactionSeconds,"reaction time");
            Check.Equal(a.Response,b.Response);Check.Equal(a.Correct,b.Correct);Check.Equal(a.Outcome,b.Outcome);Check.Equal(a.TechnicalIssue,b.TechnicalIssue);
        }
        sealed class Pair
        {
            public readonly Clock FullClock=new Clock(),IncrementalClock=new Clock();
            public readonly Experiment Full,Incremental;
            long fullRevision,incrementalRevision;
            public Pair()
            {
                Full=new Experiment(Config(),FullClock,true,4096);
                Incremental=new Experiment(Config(),IncrementalClock,new ExperimentIncrementalOptions(4096,128,16L*1024*1024));
                SameSnapshot(Full.InitialMutationSnapshot,Incremental.InitialIncrementalSnapshot);Verify();
            }
            public void AdvanceTime(double delta){FullClock.Time+=delta;IncrementalClock.Time+=delta;}
            public void Do(Action<Experiment> operation)
            {operation(Full);operation(Incremental);Verify();}
            public bool DoBool(Func<Experiment,bool> operation)
            {bool a=operation(Full),b=operation(Incremental);Check.Equal(a,b);Verify();return a;}
            public void Verify()
            {
                Check.True(!Full.MutationObservationFaulted,"Full producer faulted: "+Full.MutationObservationIssue);
                Check.True(!Incremental.IncrementalObservationFaulted,"Incremental producer faulted: "+Incremental.IncrementalObservationIssue);
                for(int guard=0;guard<4097;guard++)
                {
                    ExperimentMutationCommit full;ExperimentMutationSnapshot fullCurrent;long fullHead;
                    ExperimentIncrementalCommit incremental;ExperimentIncrementalSnapshot incrementalCurrent;long incrementalHead;
                    Check.True(Full.TryReadMutationAfter(fullRevision,out full,out fullHead,out fullCurrent));
                    Check.True(Incremental.TryReadIncrementalAfter(incrementalRevision,out incremental,out incrementalHead,out incrementalCurrent));
                    Check.Equal(fullHead,incrementalHead,"Producer head differs");SameSnapshot(fullCurrent,incrementalCurrent);
                    if(full==null||incremental==null)
                    {Check.True(full==null&&incremental==null,"Only one producer returned a commit");break;}
                    Check.Equal(full.Revision,incremental.Revision);Check.Equal(full.Operation,incremental.Operation);Check.Equal(full.Steps.Count,incremental.Steps.Count);
                    for(int i=0;i<full.Steps.Count;i++)
                    {
                        Check.Equal(full.Steps[i].Index,incremental.Steps[i].Index);SameSnapshot(full.Steps[i].Before,incremental.Steps[i].Before);
                        SameSnapshot(full.Steps[i].After,incremental.Steps[i].After);
                    }
                    SameSnapshot(full.FinalState,incremental.FinalState);fullRevision=full.Revision;incrementalRevision=incremental.Revision;
                    if(guard==4096)throw new Exception("Producer drain exceeded bound");
                }
                Check.Equal(FullClock.Reads,IncrementalClock.Reads,"Producer observation changed clock reads");
                Check.Equal(Full.Phase,Incremental.Phase);Check.Equal(Full.BlockIndex,Incremental.BlockIndex);Check.Equal(Full.Segment,Incremental.Segment);
                Check.Equal(Full.LockedControl,Incremental.LockedControl);Check.Equal(Full.EndReason,Incremental.EndReason);
                Check.Equal(Full.Events.Count,Incremental.Events.Count,"Original journal length differs");
                for(int i=0;i<Full.Events.Count;i++)
                {
                    var a=Full.Events[i];var b=Incremental.Events[i];
                    Check.Equal(a.Sequence,b.Sequence);SameDouble(a.Time,b.Time,"event time");Check.Equal(a.ClockId,b.ClockId);Check.Equal(a.SessionId,b.SessionId);
                    Check.Equal(a.BlockIndex,b.BlockIndex);Check.Equal(a.Segment,b.Segment);Check.Equal(a.TrialId,b.TrialId);Check.Equal(a.Kind,b.Kind);
                    Check.Equal(a.Fields.Count,b.Fields.Count);foreach(var field in a.Fields){string value;Check.True(b.Fields.TryGetValue(field.Key,out value));Check.Equal(field.Value,value);}
                }
                Check.Equal(Full.Trials.Count,Incremental.Trials.Count);for(int i=0;i<Full.Trials.Count;i++)SameTrial(Full.Trials[i],Incremental.Trials[i]);
                Check.Equal(Full.Ratings.Count,Incremental.Ratings.Count);
                for(int i=0;i<Full.Ratings.Count;i++)
                {var a=Full.Ratings[i];var b=Incremental.Ratings[i];Check.Equal(a.BlockIndex,b.BlockIndex);Check.Equal(a.ItemId,b.ItemId);Check.Equal(a.Value,b.Value);SameDouble(a.Time,b.Time,"rating time");Check.Equal(a.Language,b.Language);Check.Equal(a.Version,b.Version);}
            }
        }
        static void PreparePractice(Pair pair)
        {
            pair.Do(e=>e.RecordPreparation(Actor.Owner,"fixture","ready"));pair.Do(e=>e.RecordHeadsetRemoval(Actor.Owner));
            Check.True(!pair.DoBool(e=>e.AcknowledgeQuestionnaireInstruction()));Check.True(!pair.DoBool(e=>e.TryBeginRetainedBlockCountdown(false)));
            pair.Do(e=>e.BeginFamiliarization(Actor.Owner));pair.Do(e=>e.BeginPractice(Actor.Owner));
            Check.True(!pair.DoBool(e=>e.ButtonChanged(Colour.Red,true)));Check.True(!pair.DoBool(e=>e.ButtonChanged(Colour.Red,true)));
            Check.True(!pair.DoBool(e=>e.ButtonChanged(Colour.Red,false)));Check.True(!pair.DoBool(e=>e.ButtonChanged(Colour.Red,false)));
            pair.Do(e=>e.Tick());
            for(int i=0;i<8;i++)
            {
                var trial=pair.Full.CurrentTrial;pair.Do(e=>e.MarkPresented(trial.TrialId));pair.Do(e=>e.MarkPresented(trial.TrialId));
                if(i==1)pair.Do(e=>e.FlagTechnicalIssue("synthetic"));pair.AdvanceTime(.1);
                Colour colour=i==0?(Colour)(((int)trial.Stimulus.Ink+1)%4):trial.Stimulus.Ink;
                Check.True(pair.DoBool(e=>e.ButtonChanged(colour,true)));Check.True(!pair.DoBool(e=>e.ButtonChanged(colour,false)));
                if(pair.Full.Phase==Phase.PracticeCorrection)pair.Do(e=>e.ContinuePractice());
            }
            Check.Equal(Phase.BlockPreparation,pair.Full.Phase);
        }
        public static void Register(TestSuite suite)
        {
            suite.Run("incremental producer matches every full producer step through four synthetic blocks",()=>{
                var pair=new Pair();PreparePractice(pair);bool interrupted=false;
                for(int block=0;block<4;block++)
                {
                    if(block==0){pair.Do(e=>e.LockBlock(Actor.Owner,ControlSelection.Automatic,"owner"));pair.Do(e=>e.BeginCountdown(Actor.Owner,true));
                        pair.Do(e=>e.Pause(Actor.Owner));pair.AdvanceTime(3);pair.Do(e=>e.Resume(true));}
                    else{Check.True(!pair.DoBool(e=>e.TryBeginRetainedBlockCountdown(false)));Check.True(pair.DoBool(e=>e.TryBeginRetainedBlockCountdown(true)));}
                    pair.AdvanceTime(1);pair.Do(e=>e.Tick());Check.Equal(Phase.MainTask,pair.Full.Phase);
                    int guard=0;
                    while(pair.Full.Phase==Phase.MainTask)
                    {
                        Check.True(guard++<25,"Unexpected extra task trials");var trial=pair.Full.CurrentTrial;pair.Do(e=>e.MarkPresented(trial.TrialId));
                        if(block==0&&!interrupted)
                        {pair.AdvanceTime(.1);pair.Do(e=>e.Pause(Actor.Participant));pair.Do(e=>e.Resume(true));pair.AdvanceTime(1);pair.Do(e=>e.Tick());interrupted=true;continue;}
                        pair.AdvanceTime(.2);var response=trial.Stimulus.Ink;Check.True(pair.DoBool(e=>e.ButtonChanged(response,true)));Check.True(!pair.DoBool(e=>e.ButtonChanged(response,false)));
                    }
                    Check.Equal(Phase.Questionnaire,pair.Full.Phase);Check.True(pair.DoBool(e=>e.AcknowledgeQuestionnaireInstruction()));
                    for(int rating=1;rating<=5;rating++)
                    {
                        pair.AdvanceTime(.3);int selected=rating;pair.Do(e=>e.Rate(selected));
                        if(block==0&&rating==1){pair.Do(e=>e.Pause(Actor.Owner));pair.AdvanceTime(5);pair.Do(e=>e.Resume(true));}
                    }
                    if(block<3){Check.Equal(Phase.Rest,pair.Full.Phase);Check.True(pair.DoBool(e=>e.Ready()));}
                }
                Check.Equal(Phase.Ended,pair.Full.Phase);Check.Equal(104,pair.Full.Trials.Count);Check.Equal(20,pair.Full.Ratings.Count);Check.True(interrupted);
            });
            suite.Run("incremental producer preserves explicit withdrawal and technical end",()=>{
                var withdrawal=new Pair();withdrawal.Do(e=>e.Withdraw());Check.Equal(EndReason.Withdrawn,withdrawal.Full.EndReason.Value);
                var technical=new Pair();technical.Do(e=>e.EndTechnical(Actor.Owner,"synthetic"));Check.Equal(EndReason.TechnicalEnd,technical.Full.EndReason.Value);
            });
            suite.Run("incremental producer preserves original exception and latches incomplete transaction",()=>{
                var pair=new Pair();pair.FullClock.Fail=true;pair.IncrementalClock.Fail=true;Exception a=null,b=null;
                try{pair.Full.BeginFamiliarization(Actor.Owner);}catch(Exception error){a=error;}
                try{pair.Incremental.BeginFamiliarization(Actor.Owner);}catch(Exception error){b=error;}
                Check.True(a is InvalidOperationException&&b is InvalidOperationException);Check.Equal(a.Message,b.Message);Check.Equal(pair.FullClock.Reads,pair.IncrementalClock.Reads);
                Check.True(pair.Full.MutationObservationFaulted&&pair.Incremental.IncrementalObservationFaulted);
                ExperimentMutationCommit fc;ExperimentMutationSnapshot fs;ExperimentIncrementalCommit ic;ExperimentIncrementalSnapshot ins;long fh,ih;
                Check.True(!pair.Full.TryReadMutationAfter(0,out fc,out fh,out fs));Check.True(!pair.Incremental.TryReadIncrementalAfter(0,out ic,out ih,out ins));
            });
            suite.Run("incremental producer latches reentrant task changes without changing original result",()=>{
                var pair=new Pair();pair.FullClock.Hook=()=>pair.Full.Withdraw();pair.IncrementalClock.Hook=()=>pair.Incremental.Withdraw();
                pair.Full.Tick();pair.Incremental.Tick();Check.Equal(Phase.Ended,pair.Full.Phase);Check.Equal(pair.Full.Phase,pair.Incremental.Phase);
                Check.Equal(pair.Full.EndReason,pair.Incremental.EndReason);Check.Equal(pair.FullClock.Reads,pair.IncrementalClock.Reads);
                Check.True(pair.Full.MutationObservationFaulted&&pair.Incremental.IncrementalObservationFaulted);
            });
            suite.Run("incremental producer preserves partial engine state on clock failure",()=>{
                var pair=new Pair();pair.Do(e=>e.BeginFamiliarization(Actor.Owner));pair.FullClock.Fail=true;pair.IncrementalClock.Fail=true;
                Exception a=null,b=null;try{pair.Full.BeginPractice(Actor.Owner);}catch(Exception error){a=error;}try{pair.Incremental.BeginPractice(Actor.Owner);}catch(Exception error){b=error;}
                Check.True(a is InvalidOperationException&&b is InvalidOperationException);Check.Equal(a.Message,b.Message);
                Check.Equal(Phase.Familiarization,pair.Full.Phase);Check.Equal(pair.Full.Phase,pair.Incremental.Phase);
                Check.Equal(1,pair.Full.PracticeAttempt);Check.Equal(pair.Full.PracticeAttempt,pair.Incremental.PracticeAttempt);
                Check.Equal(pair.Full.Events.Count,pair.Incremental.Events.Count);Check.Equal(pair.Full.Trials.Count,pair.Incremental.Trials.Count);
                Check.True(pair.Full.MutationObservationFaulted&&pair.Incremental.IncrementalObservationFaulted);
            });
            suite.Run("incremental producer latches monotonic clock rollback",()=>{
                var pair=new Pair();pair.AdvanceTime(2);pair.Do(e=>e.BeginFamiliarization(Actor.Owner));pair.FullClock.Time=1;pair.IncrementalClock.Time=1;
                Exception a=null,b=null;try{pair.Full.BeginPractice(Actor.Owner);}catch(Exception error){a=error;}try{pair.Incremental.BeginPractice(Actor.Owner);}catch(Exception error){b=error;}
                Check.True(a is InvalidOperationException&&b is InvalidOperationException);Check.Equal(a.Message,b.Message);
                Check.Equal(pair.Full.Phase,pair.Incremental.Phase);Check.Equal(pair.Full.PracticeAttempt,pair.Incremental.PracticeAttempt);
                Check.True(pair.Full.MutationObservationFaulted&&pair.Incremental.IncrementalObservationFaulted);
            });
            suite.Run("incremental producer latches backlog overflow without changing engine outcome",()=>{
                var clock=new Clock();var e=new Experiment(Config(),clock,new ExperimentIncrementalOptions(1,128,16L*1024*1024));
                e.BeginFamiliarization(Actor.Owner);e.BeginPractice(Actor.Owner);Check.Equal(Phase.Practice,e.Phase);Check.Equal(1,e.Trials.Count);
                Check.True(e.IncrementalObservationFaulted);ExperimentIncrementalCommit next;ExperimentIncrementalSnapshot current;long head;
                Check.True(!e.TryReadIncrementalAfter(0,out next,out head,out current));
            });
            suite.Run("incremental producer retains vector allocation budget failure cause",()=>{
                var clock=new Clock();var e=new Experiment(Config(),clock,new ExperimentIncrementalOptions(4096,128,16L*1024*1024));
                e.BeginFamiliarization(Actor.Owner);
                var journalField=typeof(Experiment).GetField("incrementalMutationJournal",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic);
                Check.True(journalField!=null);object journal=journalField.GetValue(e);Check.True(journal!=null);
                var budgetField=journal.GetType().GetField("budget",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic);
                Check.True(budgetField!=null);var budget=(ExperimentPersistentMutationBudget)budgetField.GetValue(journal);Check.True(budget!=null);
                int reads=0;bool injected=false;Action hook=null;
                hook=()=>{
                    reads++;
                    if(reads==1){clock.Hook=hook;return;}
                    long remaining=budget.EstimatedBytesLimit-budget.EstimatedBytesUsed;Check.True(remaining>7000);
                    Check.True(budget.TryReserve(remaining-7000));injected=true;
                };
                clock.Hook=hook;e.BeginPractice(Actor.Owner);
                Check.True(injected&&reads==2,"The synthetic budget was not injected at the second task clock read");
                Check.True(e.IncrementalObservationFaulted);Check.Equal("estimated_budget_exhausted",e.IncrementalObservationIssue);
                Check.True(budget.Terminal);Check.Equal("estimated_budget_exhausted",budget.LastIssue);
                Check.Equal(Phase.Practice,e.Phase);Check.Equal(1,e.PracticeAttempt);Check.Equal(1,e.Trials.Count);
                Check.True(e.CurrentTrial!=null&&Object.ReferenceEquals(e.CurrentTrial,e.Trials[0]));
                Check.Equal("trial_planned",e.Events[e.Events.Count-1].Kind);
                ExperimentIncrementalCommit next;ExperimentIncrementalSnapshot current;long head;
                Check.True(!e.TryReadIncrementalAfter(0,out next,out head,out current));
            });
            suite.Run("incremental producer latches wrong reader cursor and foreign thread",()=>{
                var e=new Experiment(Config(),new Clock(),new ExperimentIncrementalOptions(4096,128,16L*1024*1024));
                e.BeginFamiliarization(Actor.Owner);ExperimentIncrementalCommit next;ExperimentIncrementalSnapshot current;long head;
                Check.True(!e.TryReadIncrementalAfter(1,out next,out head,out current));Check.True(e.IncrementalObservationFaulted);
                Check.Equal("reader_cursor_mismatch",e.IncrementalObservationIssue);
                var later=new System.Threading.Thread(()=>{ExperimentIncrementalCommit ignored;ExperimentIncrementalSnapshot state;long value;e.TryReadIncrementalAfter(0,out ignored,out value,out state);});
                later.Start();later.Join();Check.Equal("reader_cursor_mismatch",e.IncrementalObservationIssue);
                var other=new Experiment(Config(),new Clock(),new ExperimentIncrementalOptions(4096,128,16L*1024*1024));bool accepted=true;
                var thread=new System.Threading.Thread(()=>{ExperimentIncrementalCommit commit;ExperimentIncrementalSnapshot state;long value;accepted=other.TryReadIncrementalAfter(0,out commit,out value,out state);});
                thread.Start();thread.Join();Check.True(!accepted&&other.IncrementalObservationFaulted);
            });
            suite.Run("incremental producer current-head fence hides backlog and active transactions",()=>{
                var clock=new Clock();var e=new Experiment(Config(),clock,new ExperimentIncrementalOptions(4096,128,16L*1024*1024));
                Check.True(e.IsIncrementalRevisionCurrent(0));ExperimentIncrementalCommit next;ExperimentIncrementalSnapshot current;long head;
                Check.True(e.TryReadIncrementalAfter(0,out next,out head,out current));Check.True(next==null&&head==0&&current!=null);
                Check.True(e.IsIncrementalRevisionCurrent(0));e.BeginFamiliarization(Actor.Owner);
                Check.True(!e.IsIncrementalRevisionCurrent(0)&&!e.IsIncrementalRevisionCurrent(1)&&!e.IncrementalObservationFaulted);
                Check.True(e.TryReadIncrementalAfter(0,out next,out head,out current)&&next!=null&&next.Revision==1&&head==1);
                Check.True(e.IsIncrementalRevisionCurrent(1));bool during=true;
                clock.Hook=()=>during=e.IsIncrementalRevisionCurrent(1);e.Tick();
                Check.True(!during&&!e.IncrementalObservationFaulted&&e.IsIncrementalRevisionCurrent(1));
                bool foreign=true;var thread=new System.Threading.Thread(()=>foreign=e.IsIncrementalRevisionCurrent(1));thread.Start();thread.Join();
                Check.True(!foreign&&e.IncrementalObservationFaulted&&!e.IsIncrementalRevisionCurrent(1));
            });
            suite.Run("incremental producer serializes owner head check and foreign snapshot failure",()=>{
                var clock=new Clock();var e=new Experiment(Config(),clock,new ExperimentIncrementalOptions(4096,128,16L*1024*1024));
                var journalField=typeof(Experiment).GetField("incrementalMutationJournal",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic);
                Check.True(journalField!=null);object journal=journalField.GetValue(e);Check.True(journal!=null);
                var budgetField=journal.GetType().GetField("budget",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic);
                Check.True(budgetField!=null);var budget=(ExperimentPersistentMutationBudget)budgetField.GetValue(journal);Check.True(budget!=null);
                object gate=budget.Sync;var snapshot=e.InitialIncrementalSnapshot;bool accepted=true;
                using(var attempted=new System.Threading.ManualResetEvent(false))
                {
                    var thread=new System.Threading.Thread(()=>{attempted.Set();ExperimentMutationTrialSnapshot ignored;accepted=snapshot.TryGetTrial(0,out ignored);});
                    System.Threading.Monitor.Enter(gate);
                    try
                    {
                        thread.Start();Check.True(attempted.WaitOne(5000));
                        Check.True(e.IsIncrementalRevisionCurrent(0)&&!e.IncrementalObservationFaulted);
                    }
                    finally{System.Threading.Monitor.Exit(gate);}
                    Check.True(thread.Join(5000));Check.True(!accepted&&e.IncrementalObservationFaulted&&!e.IsIncrementalRevisionCurrent(0));
                    Check.Equal(budget.LastIssue,e.IncrementalObservationIssue);
                    ExperimentIncrementalCommit next;ExperimentIncrementalSnapshot current;long head;
                    Check.True(!e.TryReadIncrementalAfter(0,out next,out head,out current));
                    ExperimentMutationTrialSnapshot value;Check.True(!snapshot.TryGetTrial(0,out value));
                }
            });
        }
    }
}
