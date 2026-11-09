using System;
using System.Threading;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    // In-memory simulation only; no person, hardware, network or filesystem.
    public static class ViveActualExperimentTrialBindingTests
    {
        sealed class Clock:IClock
        {
            public double Time;public Action Hook;public bool Fail;
            public string ClockId{get{return "binding-test-clock";}}
            public double Now{get{var action=Hook;Hook=null;if(action!=null)action();if(Fail)throw new InvalidOperationException("synthetic clock failure");return Time;}}
        }
        static SessionConfiguration Config()
        {return new SessionConfiguration("binding-simulation",42,new Allocation("binding-allocation",new[]{Condition.A,Condition.B,Condition.D,Condition.C},GazeTarget.Participant,new[]{0,1,2,3}),RunKind.Simulation,"en",1,0);}
        static Experiment New(Clock clock,int producerCapacity=4096){return new Experiment(Config(),clock,true,producerCapacity);}
        static ViveActualExperimentTrialBinding Bind(Experiment e,long budget=16L*1024*1024)
        {return new ViveActualExperimentTrialBinding(e,128,budget);}
        static void Drain(ViveActualExperimentTrialBinding binding)
        {
            for(int i=0;i<4097;i++)
            {
                if(binding.Advance())return;
                if(binding.Terminal)throw new Exception("Synthetic trial binding ended: "+binding.LastIssue);
            }
            throw new Exception("Synthetic trial binding did not catch up");
        }
        static void SameAt(ViveActualExperimentTrialBinding binding,Experiment e,int index)
        {
            VivePersistentTrialValue actual;Check.True(binding.TryGet(index,out actual));
            Check.True(actual.Equals(new VivePersistentTrialValue(e.Trials[index])),"Trial copy differs at index "+index);
        }
        static void BeginPractice(Experiment e,ViveActualExperimentTrialBinding binding)
        {e.BeginFamiliarization(Actor.Owner);Drain(binding);e.BeginPractice(Actor.Owner);Drain(binding);}
        static void Answer(Experiment e,Clock c,ViveActualExperimentTrialBinding binding,bool correct)
        {
            var trial=e.CurrentTrial;int index=e.Trials.Count-1;e.MarkPresented(trial.TrialId);Drain(binding);SameAt(binding,e,index);
            c.Time+=.25;var answer=correct?trial.Stimulus.Ink:(Colour)(((int)trial.Stimulus.Ink+1)%4);
            Check.True(e.ButtonChanged(answer,true));Drain(binding);e.ButtonChanged(answer,false);Drain(binding);
            SameAt(binding,e,index);
            if(e.Phase==Phase.PracticeCorrection){e.ContinuePractice();Drain(binding);}
        }
        static void CompletePractice(Experiment e,Clock c,ViveActualExperimentTrialBinding binding,bool firstWrong=false)
        {BeginPractice(e,binding);for(int i=0;i<8;i++)Answer(e,c,binding,!(firstWrong&&i==0));Check.Equal(Phase.BlockPreparation,e.Phase);}
        static void BeginMain(Experiment e,Clock c,ViveActualExperimentTrialBinding binding)
        {e.LockBlock(Actor.Owner,ControlSelection.Automatic,"synthetic-owner");Drain(binding);e.BeginCountdown(Actor.Owner,true);Drain(binding);c.Time+=1;e.Tick();Drain(binding);Check.Equal(Phase.MainTask,e.Phase);}
        public static void Register(TestSuite suite)
        {
            suite.Run("actual experiment producer snapshots provisional and member trial as distinct immutable steps",()=>{
                var c=new Clock();var e=New(c);e.BeginFamiliarization(Actor.Owner);ExperimentMutationCommit first;ExperimentMutationSnapshot current;long head;
                Check.True(e.TryReadMutationAfter(0,out first,out head,out current)&&first!=null);e.BeginPractice(Actor.Owner);
                ExperimentMutationCommit practice;Check.True(e.TryReadMutationAfter(first.Revision,out practice,out head,out current)&&practice!=null);
                bool provisional=false,member=false;ExperimentMutationTrialSnapshot prior=default(ExperimentMutationTrialSnapshot);
                foreach(var step in practice.Steps)
                {
                    var s=step.After;if(!s.CurrentTrial.HasValue)continue;
                    if(s.CurrentTrialIndex==-1&&s.TrialCount==0){provisional=true;prior=s.CurrentTrial.Value;}
                    if(s.CurrentTrialIndex==0&&s.TrialCount==1){member=true;Check.True(prior.Equals(s.CurrentTrial.Value));}
                }
                Check.True(provisional&&member);Check.True(prior.Equals(new ExperimentMutationTrialSnapshot(e.CurrentTrial)));
                e.MarkPresented(e.CurrentTrial.TrialId);Check.True(!prior.Onset.HasValue);
            });
            suite.Run("actual experiment producer preserves ordered partial response snapshots",()=>{
                var c=new Clock();var e=New(c);e.BeginFamiliarization(Actor.Owner);e.BeginPractice(Actor.Owner);long revision=0,head;
                ExperimentMutationCommit next;ExperimentMutationSnapshot current;
                do{Check.True(e.TryReadMutationAfter(revision,out next,out head,out current));if(next!=null)revision=next.Revision;}while(next!=null);
                e.MarkPresented(e.CurrentTrial.TrialId);do{Check.True(e.TryReadMutationAfter(revision,out next,out head,out current));if(next!=null)revision=next.Revision;}while(next!=null);
                c.Time+=.25;Check.True(e.ButtonChanged(e.CurrentTrial.Stimulus.Ink,true));Check.True(e.TryReadMutationAfter(revision,out next,out head,out current)&&next!=null);
                bool sawAt=false,sawResponse=false,sawCorrect=false,sawComplete=false;
                foreach(var step in next.Steps)
                {
                    var s=step.After;if(s.CurrentTrialIndex!=0||!s.CurrentTrial.HasValue)continue;var t=s.CurrentTrial.Value;
                    if(t.ResponseAt.HasValue&&!t.Response.HasValue)sawAt=true;
                    if(t.Response.HasValue&&!t.Correct.HasValue)sawResponse=true;
                    if(t.Correct.HasValue&&t.Outcome==TrialOutcome.Pending)sawCorrect=true;
                    if(t.Outcome==TrialOutcome.Responded)sawComplete=true;
                }
                Check.True(sawAt&&sawResponse&&sawCorrect&&sawComplete);
            });
            suite.Run("actual experiment trial binding requires an observed source and cannot open a gate",()=>{
                var clock=new Clock();Check.Throws<ArgumentException>(()=>Bind(null));
                Check.Throws<ArgumentException>(()=>Bind(new Experiment(Config(),clock)));
                var e=New(new Clock());var binding=Bind(e);Check.True(binding.RealExperimentSourceBound&&!binding.CanEnterCountdown&&!binding.CanResume);
                VivePersistentTrialValue value;Check.True(!binding.TryGet(0,out value));
            });
            suite.Run("actual experiment trial binding hides stale and partially drained heads",()=>{
                var c=new Clock();var e=New(c);var binding=Bind(e);BeginPractice(e,binding);SameAt(binding,e,0);
                e.MarkPresented(e.CurrentTrial.TrialId);c.Time+=.25;var answer=e.CurrentTrial.Stimulus.Ink;Check.True(e.ButtonChanged(answer,true));
                VivePersistentTrialValue stale;Check.True(!binding.TryGet(0,out stale)&&stale.TrialId==null&&!binding.Terminal);
                Check.True(!binding.Advance()&&!binding.Terminal);Check.True(!binding.TryGet(0,out stale)&&stale.TrialId==null);
                Drain(binding);SameAt(binding,e,0);SameAt(binding,e,1);Check.Equal(TrialOutcome.Responded,e.Trials[0].Outcome);
            });
            suite.Run("actual experiment trial binding captures practice onset response and correction",()=>{
                var c=new Clock();var e=New(c);var binding=Bind(e);CompletePractice(e,c,binding,true);
                Check.Equal(8,e.Trials.Count);Check.Equal(8,binding.Count);Check.Equal(false,e.Trials[0].Correct.Value);
                for(int i=0;i<e.Trials.Count;i++)SameAt(binding,e,i);
                Check.True(!binding.Terminal&&!binding.CanEnterCountdown&&!binding.CanResume);
            });
            suite.Run("actual experiment trial binding retains interrupted record and next segment",()=>{
                var c=new Clock();var e=New(c);var binding=Bind(e);CompletePractice(e,c,binding);BeginMain(e,c,binding);
                var interrupted=e.CurrentTrial;e.MarkPresented(interrupted.TrialId);Drain(binding);e.Pause(Actor.Participant);Drain(binding);
                Check.Equal(TrialOutcome.Interrupted,interrupted.Outcome);SameAt(binding,e,8);e.Resume(true);Drain(binding);
                Check.Equal(Phase.Countdown,e.Phase);Check.Equal(1,e.Segment);c.Time+=1;e.Tick();Drain(binding);
                Check.Equal(Phase.MainTask,e.Phase);Check.Equal(1,e.CurrentTrial.Segment);SameAt(binding,e,8);SameAt(binding,e,9);
            });
            suite.Run("actual experiment trial binding matches all 104 completed synthetic trials",()=>{
                var c=new Clock();var e=New(c);var binding=Bind(e);CompletePractice(e,c,binding);
                for(int block=0;block<4;block++)
                {
                    BeginMain(e,c,binding);for(int i=0;i<24;i++)Answer(e,c,binding,true);
                    Check.Equal(Phase.Questionnaire,e.Phase);for(int i=1;i<=5;i++){e.Rate(i);Drain(binding);}
                    Check.True(e.Ready());Drain(binding);
                }
                Check.Equal(Phase.Ended,e.Phase);Check.Equal(104,e.Trials.Count);Check.Equal(104,binding.Count);
                for(int i=0;i<104;i++)SameAt(binding,e,i);Check.True(!binding.Terminal&&!binding.CanEnterCountdown&&!binding.CanResume);
            });
            suite.Run("actual experiment trial binding refuses a stolen producer record",()=>{
                var c=new Clock();var e=New(c);var binding=Bind(e);e.BeginFamiliarization(Actor.Owner);
                ExperimentMutationCommit stolen;ExperimentMutationSnapshot current;long head;
                Check.True(e.TryReadMutationAfter(0,out stolen,out head,out current)&&stolen!=null);
                Check.True(!binding.Advance()&&binding.Terminal);VivePersistentTrialValue value;Check.True(!binding.TryGet(0,out value));
            });
            suite.Run("actual experiment trial binding closes when producer backlog overflows",()=>{
                var c=new Clock();var e=New(c,1);var binding=Bind(e);e.BeginFamiliarization(Actor.Owner);e.BeginPractice(Actor.Owner);
                Check.True(e.MutationObservationFaulted);Check.True(!binding.Advance()&&binding.Terminal);
                VivePersistentTrialValue value;Check.True(!binding.TryGet(0,out value));Check.Equal(Phase.Practice,e.Phase);
            });
            suite.Run("actual experiment trial binding fails closed on its own budget exhaustion",()=>{
                var c=new Clock();var e=New(c);var binding=Bind(e,256);e.BeginFamiliarization(Actor.Owner);Drain(binding);e.BeginPractice(Actor.Owner);
                for(int i=0;i<30&&!binding.Terminal;i++)binding.Advance();
                Check.True(binding.Terminal);VivePersistentTrialValue value;Check.True(!binding.TryGet(0,out value));Check.Equal(Phase.Practice,e.Phase);
            });
            suite.Run("actual experiment trial binding fails closed on the original clock exception",()=>{
                var c=new Clock();var e=New(c);var binding=Bind(e);BeginPractice(e,binding);string trial=e.CurrentTrial.TrialId;c.Fail=true;
                bool raised=false;
                try{e.MarkPresented(trial);}catch(InvalidOperationException error){raised=true;Check.Equal("synthetic clock failure",error.Message);}
                Check.True(raised&&e.MutationObservationFaulted&&binding.Terminal);VivePersistentTrialValue value;
                Check.True(!binding.TryGet(0,out value)&&!binding.Advance());Check.Equal(Phase.Practice,e.Phase);Check.True(!e.CurrentTrial.Onset.HasValue);
            });
            suite.Run("actual experiment trial binding rejects foreign-thread source access",()=>{
                var c=new Clock();var e=New(c);var binding=Bind(e);BeginPractice(e,binding);bool result=true;
                var thread=new Thread(()=>{VivePersistentTrialValue value;result=binding.TryGet(0,out value);});thread.Start();thread.Join();
                Check.True(!result&&binding.Terminal);VivePersistentTrialValue ignored;Check.True(!binding.TryGet(0,out ignored));
            });
            suite.Run("actual experiment trial binding hides current data while source transaction is active",()=>{
                var c=new Clock();var e=New(c);var binding=Bind(e);BeginPractice(e,binding);bool visible=true;
                c.Hook=()=>{VivePersistentTrialValue value;visible=binding.TryGet(0,out value);};e.MarkPresented(e.CurrentTrial.TrialId);
                Check.True(!visible&&!binding.Terminal);Drain(binding);SameAt(binding,e,0);
            });
        }
    }
}
