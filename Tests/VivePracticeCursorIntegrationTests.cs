using System;
using System.Collections.Generic;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    // Actual in-memory Simulation participant and sealed fake target only.
    // No eye device, real file, network, user-consent proof or study gate.
    public static class VivePracticeCursorIntegrationTests
    {
        sealed class Clock:IClock
        {
            internal double Value=10;
            public string ClockId {get{return "cursor-integration-clock";}}
            public double Now {get{return Value;}}
        }
        sealed class Fixture
        {
            internal readonly Clock Clock=new Clock();internal readonly ViveDevelopmentParticipant Task;
            internal readonly VivePracticeConsentAuthority Choices;internal readonly VivePracticeOwnedMemoryStart Host;
            internal readonly VivePracticeOwnedStartReceipt Receipt;internal readonly ViveExclusiveMemoryStore Store=new ViveExclusiveMemoryStore(8);
            internal readonly VivePracticeContextV3 Context;
            internal Fixture()
            {
                var config=new SessionConfiguration("cursor-integration-session",42,
                    new Allocation("cursor-integration-allocation",new[]{Condition.B,Condition.C,Condition.A,Condition.D},GazeTarget.Participant,new[]{0,1,2,3}),
                    RunKind.Simulation,"en",3,30,true,0,true,true,true);
                var geometry=DevelopmentGeometry.FromLocalDirections(new Vec3(0,0,-1),new Vec3(0,0,1),"cursor-integration-geometry");
                var map=new Dictionary<ViveTrackpadDirection,Colour>{{ViveTrackpadDirection.Up,Colour.Red},{ViveTrackpadDirection.Right,Colour.Green},
                    {ViveTrackpadDirection.Down,Colour.Blue},{ViveTrackpadDirection.Left,Colour.Yellow}};
                Task=new ViveDevelopmentParticipant(config,Clock,geometry,map,.5,true,new ExperimentIncrementalOptions(4096,128,16L*1024*1024),4096);
                Choices=new VivePracticeConsentAuthority(Task,Clock,true);Check.True(Choices.ConfirmEye()&&Choices.ConfirmTask());
                var binding=new VivePracticeStartBinding(Task,Clock,Choices,true);
                Task.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,false,0,0));Clock.Value=11;Task.OwnerBeginPractice(Actor.Owner);
                VivePracticeStartBinding.Ticket ticket;Check.True(binding.TryAcquire(1,4096,out ticket));
                Context=new VivePracticeContextV3("cursor-integration-session","participant","stream","device",Clock.ClockId,"mapping",1,
                    "epoch",0,null,VivePracticeEpochReasonV3.Initial,11);
                Host=new VivePracticeOwnedMemoryStart(Task,Clock,Choices,binding,Context,Store,"target",65536,32);
                Check.True(Host.TryStart(ticket));VivePracticeOwnedStartReceipt receipt;Check.True(Host.TryTakeReceipt(out receipt));Receipt=receipt;
            }
        }
        static void Resume(Fixture f)
        {f.Task.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,false,0,0));f.Task.UpdateAuxiliary(false,false,false,0,true);f.Task.UpdateAuxiliary(true,false,false,0,true);}
        public static void Register(TestSuite suite)
        {
            suite.Run("synthetic eye checkpoint requires current committed task head and exact identities",()=>{
                var f=new Fixture();var cursor=new VivePracticeTaskEventCursor(f.Host,f.Receipt,4096,64);
                ViveDevelopmentParticipant.PracticeObservationFence fence;Check.True(f.Task.TryAcquirePracticeObservationFence(f.Clock,out fence));
                var snapshot=fence.Snapshot;long revision=fence.Revision;int count=fence.StepTrace.Count;
                Check.True(cursor.MatchesSyntheticEyeCheckpoint(f.Host,f.Task,f.Clock,f.Choices,f.Context,fence,snapshot,revision,count));
                var other=new Fixture();
                Check.True(!cursor.MatchesSyntheticEyeCheckpoint(other.Host,f.Task,f.Clock,f.Choices,f.Context,fence,snapshot,revision,count));
                Check.True(!cursor.MatchesSyntheticEyeCheckpoint(f.Host,other.Task,f.Clock,f.Choices,f.Context,fence,snapshot,revision,count));
                Check.True(!cursor.MatchesSyntheticEyeCheckpoint(f.Host,f.Task,other.Clock,f.Choices,f.Context,fence,snapshot,revision,count));
                Check.True(!cursor.MatchesSyntheticEyeCheckpoint(f.Host,f.Task,f.Clock,other.Choices,f.Context,fence,snapshot,revision,count));
                Check.True(!cursor.MatchesSyntheticEyeCheckpoint(f.Host,f.Task,f.Clock,f.Choices,other.Context,fence,snapshot,revision,count));
                Check.True(!cursor.MatchesSyntheticEyeCheckpoint(f.Host,f.Task,f.Clock,f.Choices,f.Context,fence,snapshot,revision+1,count));
                Check.True(!cursor.MatchesSyntheticEyeCheckpoint(f.Host,f.Task,f.Clock,f.Choices,f.Context,fence,snapshot,revision,count+1));
                Check.True(f.Task.TryConsumePracticeObservationFence(fence));
                f.Clock.Value=12;f.Task.MarkPresented(f.Task.CurrentTrial.TrialId);
                Check.True(f.Task.TryAcquirePracticeObservationFence(f.Clock,out fence));
                Check.True(!cursor.MatchesSyntheticEyeCheckpoint(f.Host,f.Task,f.Clock,f.Choices,f.Context,fence,snapshot,revision,count));
                f.Task.AbandonPracticeObservationFence(fence);
                VivePracticeTaskEventTransaction staged;Check.Equal(VivePracticeTaskCursorResult.Staged,cursor.TryStage(out staged));
                Check.True(f.Host.TryAppendPending(cursor,staged));Check.Equal(VivePracticeTaskCursorResult.NoEvents,cursor.TryStage(out staged));
                Check.True(f.Task.TryAcquirePracticeObservationFence(f.Clock,out fence));
                Check.True(cursor.MatchesSyntheticEyeCheckpoint(f.Host,f.Task,f.Clock,f.Choices,f.Context,fence,fence.Snapshot,fence.Revision,fence.StepTrace.Count));
                f.Choices.Withdraw();Check.True(!cursor.MatchesSyntheticEyeCheckpoint(f.Host,f.Task,f.Clock,f.Choices,f.Context,fence,fence.Snapshot,fence.Revision,fence.StepTrace.Count));
                f.Task.AbandonPracticeObservationFence(fence);
            });
            suite.Run("practice cursor receives anchor only from genuine owned participant start",()=>{
                var f=new Fixture();var initial=f.Store.CopyForTest("target");Check.True(initial!=null);
                var decoded=VivePracticeMultiReader.Read(initial,true,65536,32);Check.Equal(VivePracticeMultiStatus.Unclean,decoded.Status);
                Check.Equal(3,decoded.Events.Count);var cursor=new VivePracticeTaskEventCursor(f.Host,f.Receipt,4096,64);
                Check.True(!cursor.CanWrite&&!cursor.CanClaimDurability);VivePracticeTaskEventTransaction transaction;
                Check.Equal(VivePracticeTaskCursorResult.NoEvents,cursor.TryStage(out transaction));Check.True(transaction==null);
                var after=f.Store.CopyForTest("target");Check.Equal(initial.Length,after.Length);
            });
            suite.Run("practice cursor stages real event without implicitly appending or advancing",()=>{
                var f=new Fixture();var cursor=new VivePracticeTaskEventCursor(f.Host,f.Receipt,4096,64);
                var before=f.Store.CopyForTest("target");f.Clock.Value=12;f.Task.MarkPresented(f.Task.CurrentTrial.TrialId);
                VivePracticeTaskEventTransaction first;Check.Equal(VivePracticeTaskCursorResult.Staged,cursor.TryStage(out first));
                Check.True(first!=null&&!first.CanWrite&&first.Events.Count==1);Check.Equal("stimulus_presented",first.Events[0].Kind);
                var after=f.Store.CopyForTest("target");Check.Equal(before.Length,after.Length);
                for(int i=0;i<before.Length;i++)Check.Equal(before[i],after[i]);
                VivePracticeTaskEventTransaction again;Check.Equal(VivePracticeTaskCursorResult.Pending,cursor.TryStage(out again));Check.True(again==null);
            });
            suite.Run("practice cursor refuses a cloned receipt without consuming real receipt",()=>{
                var f=new Fixture();var r=f.Receipt;
                var clone=new VivePracticeOwnedStartReceipt(r.Issuer,r.Participant,r.Choices,r.Clock,r.TargetReceipt,r.Batch,r.ChoiceGeneration);
                Check.Throws<InvalidOperationException>(()=>new VivePracticeTaskEventCursor(f.Host,clone,4096,64));
                var cursor=new VivePracticeTaskEventCursor(f.Host,r,4096,64);VivePracticeTaskEventTransaction tx;
                Check.Equal(VivePracticeTaskCursorResult.NoEvents,cursor.TryStage(out tx));
            });
            suite.Run("practice cursor rejects another participant issuer even with same session label",()=>{
                var a=new Fixture();var b=new Fixture();Check.Equal(a.Task.SessionId,b.Task.SessionId);
                Check.Throws<InvalidOperationException>(()=>new VivePracticeTaskEventCursor(b.Host,a.Receipt,4096,64));
                var cursor=new VivePracticeTaskEventCursor(a.Host,a.Receipt,4096,64);VivePracticeTaskEventTransaction tx;
                Check.Equal(VivePracticeTaskCursorResult.NoEvents,cursor.TryStage(out tx));
            });
            suite.Run("practice cursor consumes one receipt exactly once",()=>{
                var f=new Fixture();var cursor=new VivePracticeTaskEventCursor(f.Host,f.Receipt,4096,64);
                Check.Throws<InvalidOperationException>(()=>new VivePracticeTaskEventCursor(f.Host,f.Receipt,4096,64));
                VivePracticeTaskEventTransaction tx;Check.Equal(VivePracticeTaskCursorResult.NoEvents,cursor.TryStage(out tx));
            });
            suite.Run("practice cursor cannot be constructed after a separately consumed anchor",()=>{
                var f=new Fixture();VivePracticeCursorAnchor anchor;
                Check.True(f.Host.TryConsumeForCursor(f.Receipt,out anchor)&&anchor!=null);
                Check.Throws<InvalidOperationException>(()=>new VivePracticeTaskEventCursor(f.Host,f.Receipt,4096,64));
            });
            suite.Run("practice cursor refuses receipt after withdrawal or pause-resume ABA",()=>{
                for(int mode=0;mode<3;mode++)
                {
                    var f=new Fixture();if(mode==0)f.Choices.Withdraw();else if(mode==1)f.Task.ParticipantPause();
                    else{f.Task.ParticipantPause();Resume(f);Check.Equal(Phase.Practice,f.Task.Phase);}
                    Check.Throws<InvalidOperationException>(()=>new VivePracticeTaskEventCursor(f.Host,f.Receipt,4096,64));
                }
            });
            suite.Run("practice cursor refuses invalid limits without consuming receipt",()=>{
                var f=new Fixture();Check.Throws<ArgumentOutOfRangeException>(()=>new VivePracticeTaskEventCursor(f.Host,f.Receipt,0,64));
                var cursor=new VivePracticeTaskEventCursor(f.Host,f.Receipt,4096,64);VivePracticeTaskEventTransaction tx;
                Check.Equal(VivePracticeTaskCursorResult.NoEvents,cursor.TryStage(out tx));
            });
            suite.Run("practice cursor pending batch faults on later withdrawal or pause ABA",()=>{
                for(int mode=0;mode<3;mode++)
                {
                    var f=new Fixture();var cursor=new VivePracticeTaskEventCursor(f.Host,f.Receipt,4096,64);
                    f.Clock.Value=12;f.Task.MarkPresented(f.Task.CurrentTrial.TrialId);VivePracticeTaskEventTransaction tx;
                    Check.Equal(VivePracticeTaskCursorResult.Staged,cursor.TryStage(out tx));Check.True(tx!=null);
                    if(mode==0)f.Choices.Withdraw();else if(mode==1)f.Task.ParticipantPause();
                    else{f.Task.ParticipantPause();Resume(f);Check.Equal(Phase.Practice,f.Task.Phase);}
                    Check.Equal(VivePracticeTaskCursorResult.Faulted,cursor.TryStage(out tx));Check.True(tx==null&&cursor.Faulted);
                    Check.Equal(VivePracticeTaskCursorResult.Faulted,cursor.TryStage(out tx));
                }
            });
        }
    }
}
