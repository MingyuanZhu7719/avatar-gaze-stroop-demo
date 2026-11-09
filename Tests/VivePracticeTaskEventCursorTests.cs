using System;
using System.Collections.Generic;
using System.Threading;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    // Actual in-memory Simulation participant and sealed fake target; no UI,
    // device, file, network, personal consent or research admission.
    public static class VivePracticeTaskEventCursorTests
    {
        sealed class Clock:IClock
        {
            internal double Value=10;internal Action OnNow;
            public string ClockId {get{return "task-cursor-clock";}}
            public double Now {get{var a=OnNow;OnNow=null;if(a!=null)a();return Value;}}
        }
        sealed class Fixture
        {
            internal readonly Clock Clock=new Clock();internal readonly ViveDevelopmentParticipant Task;
            internal readonly VivePracticeConsentAuthority Choices;internal readonly VivePracticeOwnedMemoryStart Host;
            internal readonly VivePracticeOwnedStartReceipt Receipt;internal readonly VivePracticeTaskEventCursor Cursor;
            internal readonly ViveExclusiveMemoryStore Store=new ViveExclusiveMemoryStore(4);
            internal Fixture(int traceLimit=4096,int eventLimit=256)
            {
                var config=new SessionConfiguration("task-cursor-session",42,
                    new Allocation("task-cursor-allocation",new[]{Condition.B,Condition.C,Condition.A,Condition.D},GazeTarget.Participant,new[]{0,1,2,3}),
                    RunKind.Simulation,"en",3,30,true,0,true,true,true);
                var geometry=DevelopmentGeometry.FromLocalDirections(new Vec3(0,0,-1),new Vec3(0,0,1),"task-cursor-geometry");
                var mapping=new Dictionary<ViveTrackpadDirection,Colour>{{ViveTrackpadDirection.Up,Colour.Red},{ViveTrackpadDirection.Right,Colour.Green},
                    {ViveTrackpadDirection.Down,Colour.Blue},{ViveTrackpadDirection.Left,Colour.Yellow}};
                Task=new ViveDevelopmentParticipant(config,Clock,geometry,mapping,.5,true,new ExperimentIncrementalOptions(4096,128,16L*1024*1024),4096);
                Choices=new VivePracticeConsentAuthority(Task,Clock,true);Check.True(Choices.ConfirmEye()&&Choices.ConfirmTask());
                var binding=new VivePracticeStartBinding(Task,Clock,Choices,true);
                Task.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,false,0,0));Clock.Value=11;Task.OwnerBeginPractice(Actor.Owner);
                VivePracticeStartBinding.Ticket ticket;Check.True(binding.TryAcquire(1,4096,out ticket));
                var context=new VivePracticeContextV3(config.SessionId,"participant","stream","device",Clock.ClockId,"mapping",1,"epoch",0,null,VivePracticeEpochReasonV3.Initial,11);
                Host=new VivePracticeOwnedMemoryStart(Task,Clock,Choices,binding,context,Store,"target",65536,32);
                Check.True(Host.TryStart(ticket));VivePracticeOwnedStartReceipt receipt;Check.True(Host.TryTakeReceipt(out receipt));Receipt=receipt;
                if(traceLimit==-1)
                {
                    ViveDevelopmentParticipant.PracticeObservationFence fence;
                    Check.True(Task.TryAcquirePracticeObservationFence(Clock,out fence)&&fence!=null);
                    traceLimit=fence.StepTrace.Count+1;Check.True(Task.TryConsumePracticeObservationFence(fence));
                }
                Cursor=new VivePracticeTaskEventCursor(Host,Receipt,traceLimit,eventLimit);
            }
            internal void Present(){Clock.Value=12;Task.MarkPresented(Task.CurrentTrial.TrialId);}
            internal VivePracticeTaskEventTransaction Stage()
            {VivePracticeTaskEventTransaction tx;Check.Equal(VivePracticeTaskCursorResult.Staged,Cursor.TryStage(out tx));Check.True(tx!=null);return tx;}
        }
        static void Resume(Fixture f)
        {f.Task.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,false,0,0));f.Task.UpdateAuxiliary(false,false,false,0,true);f.Task.UpdateAuxiliary(true,false,false,0,true);}
        public static void Register(TestSuite suite)
        {
            suite.Run("practice task cursor requires and consumes real owned start receipt",()=>{
                var f=new Fixture();Check.True(!f.Cursor.CanWrite&&!f.Cursor.CanClaimDurability);
                bool threw=false;try{new VivePracticeTaskEventCursor(f.Host,f.Receipt,4096,256);}catch(InvalidOperationException){threw=true;}Check.True(threw);
            });
            suite.Run("practice task cursor no events can follow legitimate no-op",()=>{
                var f=new Fixture();VivePracticeTaskEventTransaction tx;
                Check.Equal(VivePracticeTaskCursorResult.NoEvents,f.Cursor.TryStage(out tx));Check.True(tx==null);
                f.Task.Tick();Check.Equal(VivePracticeTaskCursorResult.NoEvents,f.Cursor.TryStage(out tx));Check.True(tx==null&&!f.Cursor.Faulted);
            });
            suite.Run("practice task cursor stages actual first event once without an ack",()=>{
                var f=new Fixture();f.Present();var tx=f.Stage();Check.Equal(1,tx.Events.Count);Check.Equal("stimulus_presented",tx.Events[0].Kind);
                Check.Equal(f.Task.CurrentTrial.TrialId,tx.Events[0].TrialId);Check.Equal(12.0,tx.Events[0].Time);Check.True(!tx.CanWrite);
                VivePracticeTaskEventTransaction other;Check.Equal(VivePracticeTaskCursorResult.Pending,f.Cursor.TryStage(out other));Check.True(other==null);
            });
            suite.Run("practice task cursor stages first full revision and never merges later responses",()=>{
                var f=new Fixture();f.Present();f.Clock.Value=13;f.Task.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,true,0,1));
                var tx=f.Stage();Check.Equal(1,tx.Events.Count);Check.Equal("stimulus_presented",tx.Events[0].Kind);
                VivePracticeTaskEventTransaction next;Check.Equal(VivePracticeTaskCursorResult.Pending,f.Cursor.TryStage(out next));Check.True(next==null);
            });
            suite.Run("practice task cursor rejects choice withdrawal",()=>{
                var f=new Fixture();f.Choices.Withdraw();VivePracticeTaskEventTransaction tx;
                Check.Equal(VivePracticeTaskCursorResult.Faulted,f.Cursor.TryStage(out tx));Check.True(tx==null&&f.Cursor.Faulted);
            });
            suite.Run("practice task cursor commits pause boundary and never resumes old epoch",()=>{
                var f=new Fixture();f.Task.ParticipantPause();Check.Equal(Phase.Paused,f.Task.Phase);
                VivePracticeTaskEventTransaction tx;Check.Equal(VivePracticeTaskCursorResult.Staged,f.Cursor.TryStage(out tx));
                Check.True(tx!=null);Check.Equal(3,tx.Events.Count);
                Check.Equal("trial_interrupted",tx.Events[0].Kind);Check.Equal("explicit_pause",tx.Events[1].Kind);
                Check.Equal("phase_changed",tx.Events[2].Kind);Check.Equal("Paused",tx.Events[2].Fields["to"]);
                var original=f.Task.ReadEventsAfter(-1,4096);
                for(int i=0;i<3;i++)
                {
                    var actual=original[original.Count-3+i];Check.Equal(actual.Sequence,tx.Events[i].Sequence);
                    Check.Equal(actual.TrialId,tx.Events[i].TrialId);Check.Equal(actual.Time,tx.Events[i].Time);
                }
                Check.True(f.Host.TryAppendPending(f.Cursor,tx));Check.True(f.Cursor.BoundaryCommitted&&!f.Cursor.Faulted);
                var read=VivePracticeMultiReader.Read(f.Store.CopyForTest("target"),true,65536,32);
                Check.Equal(VivePracticeMultiStatus.Unclean,read.Status);
                for(int i=0;i<3;i++)Check.Equal(tx.Events[i].Sequence,read.Events[read.Events.Count-3+i].Sequence);
                Resume(f);Check.Equal(Phase.Practice,f.Task.Phase);
                VivePracticeTaskEventTransaction after;Check.Equal(VivePracticeTaskCursorResult.NoEvents,f.Cursor.TryStage(out after));
                Check.True(after==null&&f.Cursor.BoundaryCommitted);
            });
            suite.Run("practice task cursor rejects uncommitted pause-resume ABA",()=>{
                var f=new Fixture();f.Task.ParticipantPause();Resume(f);Check.Equal(Phase.Practice,f.Task.Phase);
                VivePracticeTaskEventTransaction tx;Check.Equal(VivePracticeTaskCursorResult.Faulted,f.Cursor.TryStage(out tx));
                Check.True(tx==null&&f.Cursor.Faulted);
            });
            suite.Run("practice task cursor rejects staged pause after resume before commit",()=>{
                var f=new Fixture();f.Task.ParticipantPause();VivePracticeTaskEventTransaction tx;
                Check.Equal(VivePracticeTaskCursorResult.Staged,f.Cursor.TryStage(out tx));Check.True(tx!=null);
                Resume(f);Check.Equal(Phase.Practice,f.Task.Phase);
                Check.True(!f.Host.TryAppendPending(f.Cursor,tx));Check.True(f.Cursor.Faulted&&!f.Cursor.BoundaryCommitted);
            });
            suite.Run("practice task cursor rejects pause-resume-pause ABA",()=>{
                var f=new Fixture();f.Task.ParticipantPause();Resume(f);Check.Equal(Phase.Practice,f.Task.Phase);
                f.Task.ParticipantPause();Check.Equal(Phase.Paused,f.Task.Phase);
                VivePracticeTaskEventTransaction tx;Check.Equal(VivePracticeTaskCursorResult.Faulted,f.Cursor.TryStage(out tx));
                Check.True(tx==null&&f.Cursor.Faulted&&!f.Cursor.BoundaryCommitted);
            });
            suite.Run("practice task cursor in-flight pause resume is uncertain and never retried",()=>{
                var f=new Fixture();f.Task.ParticipantPause();VivePracticeTaskEventTransaction tx;
                Check.Equal(VivePracticeTaskCursorResult.Staged,f.Cursor.TryStage(out tx));Check.True(tx!=null);
                var before=f.Store.CopyForTest("target");
                Check.True(!f.Host.TryAppendPending(f.Cursor,tx,length=>new ViveFakeWritePlan(20,false,()=>Resume(f))));
                Check.Equal(Phase.Practice,f.Task.Phase);Check.True(f.Cursor.Faulted&&!f.Cursor.BoundaryCommitted);
                Check.Equal((ViveFakeTargetState?)ViveFakeTargetState.Uncertain,f.Store.StateForTest("target"));
                var after=f.Store.CopyForTest("target");Check.True(after.Length>=before.Length+20);Check.True(!f.Host.TryAppendPending(f.Cursor,tx));
                for(int i=0;i<before.Length;i++)Check.Equal(before[i],after[i]);
                var afterRetry=f.Store.CopyForTest("target");Check.Equal(after.Length,afterRetry.Length);
                for(int i=0;i<after.Length;i++)Check.Equal(after[i],afterRetry[i]);
            });
            suite.Run("practice task cursor pending invalidates on subsequent task action",()=>{
                var f=new Fixture();f.Present();f.Stage();f.Task.ParticipantPause();Resume(f);
                VivePracticeTaskEventTransaction tx;Check.Equal(VivePracticeTaskCursorResult.Faulted,f.Cursor.TryStage(out tx));Check.True(tx==null);
            });
            suite.Run("practice task cursor rejects withdrawal from clock callback and clock rollback",()=>{
                var f=new Fixture();f.Present();f.Clock.OnNow=f.Task.ParticipantWithdraw;VivePracticeTaskEventTransaction tx;
                Check.Equal(VivePracticeTaskCursorResult.Faulted,f.Cursor.TryStage(out tx));Check.True(tx==null&&f.Choices.Revoked);
                var other=new Fixture();other.Present();other.Clock.Value=9;
                Check.Equal(VivePracticeTaskCursorResult.Faulted,other.Cursor.TryStage(out tx));Check.True(tx==null);
            });
            suite.Run("practice task cursor wrong thread and reentrant stop fail closed",()=>{
                var f=new Fixture();VivePracticeTaskCursorResult result=VivePracticeTaskCursorResult.NoEvents;
                var thread=new Thread(()=>{VivePracticeTaskEventTransaction tx;result=f.Cursor.TryStage(out tx);});thread.Start();Check.True(thread.Join(5000));
                Check.Equal(VivePracticeTaskCursorResult.Faulted,result);Check.True(f.Cursor.Faulted);f.Task.ParticipantWithdraw();
                var other=new Fixture();other.Present();other.Clock.OnNow=other.Cursor.Stop;VivePracticeTaskEventTransaction ignored;
                Check.Equal(VivePracticeTaskCursorResult.Faulted,other.Cursor.TryStage(out ignored));Check.True(ignored==null);
            });
            suite.Run("practice task cursor rejects invalid explicit limits",()=>{
                var f=new Fixture();bool badTrace=false,badEvents=false;
                try{new VivePracticeTaskEventCursor(f.Host,f.Receipt,0,256);}catch(ArgumentOutOfRangeException){badTrace=true;}
                try{new VivePracticeTaskEventCursor(f.Host,f.Receipt,4096,257);}catch(ArgumentOutOfRangeException){badEvents=true;}
                Check.True(badTrace&&badEvents);
            });
            suite.Run("practice task cursor refuses a complete revision beyond trace budget",()=>{
                var f=new Fixture(-1);f.Present();VivePracticeTaskEventTransaction tx;
                Check.Equal(VivePracticeTaskCursorResult.Faulted,f.Cursor.TryStage(out tx));Check.True(tx==null&&f.Cursor.Faulted);
                Check.Equal("trace_missing_or_context_closed",f.Cursor.LastIssue);
            });
        }
    }
}
