using System;
using System.Collections.Generic;
using System.Threading;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    // Real Simulation state machine and sealed in-memory fake only.
    public static class VivePracticeTerminalAdversarialTests
    {
        sealed class Clock:IClock
        {
            internal double Value=10;
            public string ClockId {get{return "terminal-adversarial-clock";}}
            public double Now {get{return Value;}}
        }
        sealed class Fixture
        {
            internal readonly Clock Clock=new Clock();
            internal readonly ViveDevelopmentParticipant Task;
            internal readonly VivePracticeConsentAuthority Choices;
            internal readonly ViveExclusiveMemoryStore Store=new ViveExclusiveMemoryStore(8);
            internal readonly VivePracticeOwnedMemoryStart Host;
            internal readonly VivePracticeTaskEventCursor Cursor;
            internal Fixture()
            {
                var config=new SessionConfiguration("terminal-adversarial-session",42,
                    new Allocation("terminal-adversarial-allocation",new[]{Condition.B,Condition.C,Condition.A,Condition.D},GazeTarget.Participant,new[]{0,1,2,3}),
                    RunKind.Simulation,"en",3,30,true,0,true,true,true);
                var geometry=DevelopmentGeometry.FromLocalDirections(new Vec3(0,0,-1),new Vec3(0,0,1),"terminal-adversarial-geometry");
                var map=new Dictionary<ViveTrackpadDirection,Colour>{{ViveTrackpadDirection.Up,Colour.Red},{ViveTrackpadDirection.Right,Colour.Green},
                    {ViveTrackpadDirection.Down,Colour.Blue},{ViveTrackpadDirection.Left,Colour.Yellow}};
                Task=new ViveDevelopmentParticipant(config,Clock,geometry,map,.5,true,new ExperimentIncrementalOptions(4096,128,16L*1024*1024),4096);
                Choices=new VivePracticeConsentAuthority(Task,Clock,true);Check.True(Choices.ConfirmEye()&&Choices.ConfirmTask());
                var binding=new VivePracticeStartBinding(Task,Clock,Choices,true);
                Task.UpdateTrackpad(true,Neutral());Clock.Value=11;Task.OwnerBeginPractice(Actor.Owner);
                VivePracticeStartBinding.Ticket ticket;Check.True(binding.TryAcquire(1,4096,out ticket));
                var context=new VivePracticeContextV3("terminal-adversarial-session","participant","stream","device",Clock.ClockId,"mapping",1,
                    "epoch",0,null,VivePracticeEpochReasonV3.Initial,11);
                Host=new VivePracticeOwnedMemoryStart(Task,Clock,Choices,binding,context,Store,"target",262144,512);
                Check.True(Host.TryStart(ticket));VivePracticeOwnedStartReceipt receipt;Check.True(Host.TryTakeReceipt(out receipt));
                Cursor=new VivePracticeTaskEventCursor(Host,receipt,4096,64);
            }
            internal byte[] Bytes(){return Store.CopyForTest("target");}
            internal VivePracticeMultiReadResult Read(){return VivePracticeMultiReader.Read(Bytes(),true,262144,512);}
            internal void Drain()
            {
                for(int i=0;i<32;i++)
                {
                    VivePracticeTaskEventTransaction tx;var result=Cursor.TryStage(out tx);
                    if(result==VivePracticeTaskCursorResult.NoEvents)return;
                    Check.Equal(VivePracticeTaskCursorResult.Staged,result);Check.True(tx!=null&&Host.TryAppendPending(Cursor,tx));
                }
                throw new InvalidOperationException("fixture drain did not terminate");
            }
            internal VivePracticeTaskEventTransaction StageTerminal()
            {
                for(int i=0;i<8;i++)
                {
                    Task.UpdateTrackpad(true,Neutral());Drain();Clock.Value+=1;Task.MarkPresented(Task.CurrentTrial.TrialId);Drain();
                    Task.UpdateTrackpad(true,Press(Task.CurrentTrial.Stimulus.Ink));
                    if(i<7)Drain();
                }
                Check.Equal(Phase.BlockPreparation,Task.Phase);VivePracticeTaskEventTransaction tx;
                Check.Equal(VivePracticeTaskCursorResult.Staged,Cursor.TryStage(out tx));Check.True(tx!=null&&tx.Events.Count==3);
                Check.Equal("practice_completed",tx.Events[1].Kind);return tx;
            }
        }
        static VivePreviewTrackpadSample Neutral(){return new VivePreviewTrackpadSample(true,false,0,0);}
        static VivePreviewTrackpadSample Press(Colour value)
        {
            switch(value)
            {
                case Colour.Red:return new VivePreviewTrackpadSample(true,true,0,1);
                case Colour.Green:return new VivePreviewTrackpadSample(true,true,1,0);
                case Colour.Blue:return new VivePreviewTrackpadSample(true,true,0,-1);
                case Colour.Yellow:return new VivePreviewTrackpadSample(true,true,-1,0);
                default:throw new ArgumentException("fixture colour");
            }
        }
        static void Same(byte[] a,byte[] b)
        {Check.True(a!=null&&b!=null&&a.Length==b.Length);for(int i=0;i<a.Length;i++)Check.Equal(a[i],b[i]);}
        public static void Register(TestSuite suite)
        {
            suite.Run("terminal pending consent withdrawal prevents fake write",()=>{
                var f=new Fixture();var tx=f.StageTerminal();var before=f.Bytes();f.Choices.Withdraw();
                Check.True(!f.Host.TryAppendPending(f.Cursor,tx));Same(before,f.Bytes());Check.True(!f.Cursor.BoundaryCommitted);
                Check.True(!f.Host.TryAppendPending(f.Cursor,tx));Same(before,f.Bytes());
            });
            suite.Run("terminal plan callback reentry cannot commit a prefix",()=>{
                var f=new Fixture();var tx=f.StageTerminal();var before=f.Bytes();bool nested=true;
                Check.True(!f.Host.TryAppendPending(f.Cursor,tx,length=>{nested=f.Host.TryAppendPending(f.Cursor,tx);return new ViveFakeWritePlan(length,false,null);}));
                Check.True(!nested&&f.Cursor.Faulted&&!f.Cursor.BoundaryCommitted);Same(before,f.Bytes());
                Check.True(!f.Host.TryAppendPending(f.Cursor,tx));Same(before,f.Bytes());
            });
            suite.Run("terminal plan callback cross-thread withdrawal prevents fake write",()=>{
                var f=new Fixture();var tx=f.StageTerminal();var before=f.Bytes();bool joined=false;
                Check.True(!f.Host.TryAppendPending(f.Cursor,tx,length=>{
                    var thread=new Thread(f.Choices.Withdraw){IsBackground=true};thread.Start();joined=thread.Join(5000);
                    return new ViveFakeWritePlan(length,false,null);
                }));
                Check.True(joined&&f.Choices.Revoked&&!f.Cursor.BoundaryCommitted);Same(before,f.Bytes());
                Check.True(!f.Host.TryAppendPending(f.Cursor,tx));Same(before,f.Bytes());
            });
            suite.Run("terminal partial fake write is uncertain and cannot retry",()=>{
                var f=new Fixture();var tx=f.StageTerminal();var before=f.Bytes();int oldEvents=f.Read().Events.Count;
                Check.True(!f.Host.TryAppendPending(f.Cursor,tx,length=>new ViveFakeWritePlan(20,true,null)));
                var after=f.Bytes();Check.Equal(before.Length+20,after.Length);
                Check.Equal((ViveFakeTargetState?)ViveFakeTargetState.Uncertain,f.Store.StateForTest("target"));
                var read=f.Read();Check.Equal(VivePracticeMultiStatus.Truncated,read.Status);Check.Equal(oldEvents,read.Events.Count);
                Check.True(f.Cursor.Faulted&&!f.Cursor.BoundaryCommitted&&!f.Host.TryAppendPending(f.Cursor,tx));Same(after,f.Bytes());
            });
            suite.Run("terminal full fake write with in-flight cross-thread withdrawal is uncertain",()=>{
                var f=new Fixture();var tx=f.StageTerminal();bool joined=false;
                Check.True(!f.Host.TryAppendPending(f.Cursor,tx,length=>new ViveFakeWritePlan(length,false,()=>{
                    var thread=new Thread(f.Choices.Withdraw){IsBackground=true};thread.Start();joined=thread.Join(5000);
                })));
                Check.True(joined&&f.Choices.Revoked&&f.Cursor.Faulted&&!f.Cursor.BoundaryCommitted);
                Check.Equal((ViveFakeTargetState?)ViveFakeTargetState.Uncertain,f.Store.StateForTest("target"));
                var after=f.Bytes();Check.True(!f.Host.TryAppendPending(f.Cursor,tx));Same(after,f.Bytes());
            });
            suite.Run("terminal committed transaction cannot replay",()=>{
                var f=new Fixture();var tx=f.StageTerminal();Check.True(f.Host.TryAppendPending(f.Cursor,tx)&&f.Cursor.BoundaryCommitted);
                var after=f.Bytes();Check.True(!f.Host.TryAppendPending(f.Cursor,tx));Same(after,f.Bytes());
            });
            suite.Run("terminal copied and foreign transaction identities cannot append",()=>{
                var f=new Fixture();var tx=f.StageTerminal();var before=f.Bytes();var copy=new VivePracticeTaskEventTransaction(tx.Revision,tx.Events);
                Check.True(!f.Host.TryAppendPending(f.Cursor,copy));Same(before,f.Bytes());Check.True(!f.Host.TryAppendPending(f.Cursor,tx));
                var a=new Fixture();var b=new Fixture();var other=a.StageTerminal();var beforeA=a.Bytes();var beforeB=b.Bytes();
                Check.True(!b.Host.TryAppendPending(a.Cursor,other));Same(beforeA,a.Bytes());Same(beforeB,b.Bytes());
            });
            suite.Run("terminal with undrained earlier task events fails closed",()=>{
                var f=new Fixture();var before=f.Bytes();f.Clock.Value+=1;f.Task.MarkPresented(f.Task.CurrentTrial.TrialId);
                f.Task.ParticipantPause();Check.Equal(Phase.Paused,f.Task.Phase);VivePracticeTaskEventTransaction tx;
                Check.True(f.Cursor.TryStage(out tx)!=VivePracticeTaskCursorResult.Staged);Check.True(tx==null&&!f.Cursor.BoundaryCommitted);
                Same(before,f.Bytes());
            });
            suite.Run("pending practice transaction invalidated by pause cannot append",()=>{
                var f=new Fixture();f.Clock.Value+=1;f.Task.MarkPresented(f.Task.CurrentTrial.TrialId);
                VivePracticeTaskEventTransaction tx;Check.Equal(VivePracticeTaskCursorResult.Staged,f.Cursor.TryStage(out tx));
                var before=f.Bytes();f.Task.ParticipantPause();Check.Equal(Phase.Paused,f.Task.Phase);
                Check.True(!f.Host.TryAppendPending(f.Cursor,tx));Same(before,f.Bytes());Check.True(!f.Host.TryAppendPending(f.Cursor,tx));
            });
            suite.Run("plan callback pause invalidates pending task before fake append",()=>{
                var f=new Fixture();f.Clock.Value+=1;f.Task.MarkPresented(f.Task.CurrentTrial.TrialId);
                VivePracticeTaskEventTransaction tx;Check.Equal(VivePracticeTaskCursorResult.Staged,f.Cursor.TryStage(out tx));var before=f.Bytes();
                Check.True(!f.Host.TryAppendPending(f.Cursor,tx,length=>{f.Task.ParticipantPause();return new ViveFakeWritePlan(length,false,null);}));
                Check.Equal(Phase.Paused,f.Task.Phase);Same(before,f.Bytes());Check.True(!f.Host.TryAppendPending(f.Cursor,tx));
            });
            suite.Run("held trackpad pause commits complete terminal transaction",()=>{
                var f=new Fixture();f.Clock.Value+=1;f.Task.MarkPresented(f.Task.CurrentTrial.TrialId);f.Drain();
                f.Task.UpdateTrackpad(true,Press(f.Task.CurrentTrial.Stimulus.Ink));f.Drain();
                Check.Equal(Phase.Practice,f.Task.Phase);f.Task.ParticipantPause();Check.Equal(Phase.Paused,f.Task.Phase);
                VivePracticeTaskEventTransaction tx;Check.Equal(VivePracticeTaskCursorResult.Staged,f.Cursor.TryStage(out tx));
                Check.True(tx!=null&&tx.Events.Count>0&&f.Host.TryAppendPending(f.Cursor,tx));Check.True(f.Cursor.BoundaryCommitted);
                var read=f.Read();Check.Equal(VivePracticeMultiStatus.Unclean,read.Status);
                Check.Equal("phase_changed",read.Events[read.Events.Count-1].Kind);
            });
        }
    }
}
