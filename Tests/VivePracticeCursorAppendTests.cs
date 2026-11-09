using System;
using System.Collections.Generic;
using System.Threading;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    // Genuine in-memory Simulation participant and sealed fake target only.
    // No physical device, persistent file, identity or research consent.
    public static class VivePracticeCursorAppendTests
    {
        sealed class Clock:IClock
        {
            internal double Value=10;internal Action OnNow;
            public string ClockId {get{return "cursor-append-clock";}}
            public double Now {get{var action=OnNow;OnNow=null;if(action!=null)action();return Value;}}
        }
        sealed class Fixture
        {
            internal readonly Clock Clock=new Clock();internal readonly ViveDevelopmentParticipant Task;
            internal readonly VivePracticeConsentAuthority Choices;internal readonly VivePracticeOwnedMemoryStart Host;
            internal readonly VivePracticeTaskEventCursor Cursor;internal readonly ViveExclusiveMemoryStore Store=new ViveExclusiveMemoryStore(8);
            internal Fixture(Func<int,ViveFakeWritePlan> plan=null,int maximumBytes=65536,int maximumBatches=32)
            {
                var config=new SessionConfiguration("cursor-append-session",42,
                    new Allocation("cursor-append-allocation",new[]{Condition.B,Condition.C,Condition.A,Condition.D},GazeTarget.Participant,new[]{0,1,2,3}),
                    RunKind.Simulation,"en",3,30,true,0,true,true,true);
                var geometry=DevelopmentGeometry.FromLocalDirections(new Vec3(0,0,-1),new Vec3(0,0,1),"cursor-append-geometry");
                var map=new Dictionary<ViveTrackpadDirection,Colour>{{ViveTrackpadDirection.Up,Colour.Red},{ViveTrackpadDirection.Right,Colour.Green},
                    {ViveTrackpadDirection.Down,Colour.Blue},{ViveTrackpadDirection.Left,Colour.Yellow}};
                Task=new ViveDevelopmentParticipant(config,Clock,geometry,map,.5,true,new ExperimentIncrementalOptions(4096,128,16L*1024*1024),4096);
                Choices=new VivePracticeConsentAuthority(Task,Clock,true);Check.True(Choices.ConfirmEye()&&Choices.ConfirmTask());
                var binding=new VivePracticeStartBinding(Task,Clock,Choices,true);
                Task.UpdateTrackpad(true,Neutral());Clock.Value=11;Task.OwnerBeginPractice(Actor.Owner);
                VivePracticeStartBinding.Ticket ticket;Check.True(binding.TryAcquire(1,4096,out ticket));
                var context=new VivePracticeContextV3("cursor-append-session","participant","stream","device",Clock.ClockId,"mapping",1,
                    "epoch",0,null,VivePracticeEpochReasonV3.Initial,11);
                Host=new VivePracticeOwnedMemoryStart(Task,Clock,Choices,binding,context,Store,"target",maximumBytes,maximumBatches,plan);
                Check.True(Host.TryStart(ticket));VivePracticeOwnedStartReceipt receipt;Check.True(Host.TryTakeReceipt(out receipt));
                Cursor=new VivePracticeTaskEventCursor(Host,receipt,4096,64);
            }
            internal VivePracticeMultiReadResult Read(){return VivePracticeMultiReader.Read(Store.CopyForTest("target"),true,65536,128);}
        }
        static VivePreviewTrackpadSample Neutral(){return new VivePreviewTrackpadSample(true,false,0,0);}
        static VivePreviewTrackpadSample Press(Colour colour)
        {
            switch(colour)
            {
                case Colour.Red:return new VivePreviewTrackpadSample(true,true,0,1);
                case Colour.Green:return new VivePreviewTrackpadSample(true,true,1,0);
                case Colour.Blue:return new VivePreviewTrackpadSample(true,true,0,-1);
                case Colour.Yellow:return new VivePreviewTrackpadSample(true,true,-1,0);
                default:throw new ArgumentException("Unknown fixture colour");
            }
        }
        static void Resume(Fixture f)
        {f.Task.UpdateTrackpad(true,Neutral());f.Task.UpdateAuxiliary(false,false,false,0,true);f.Task.UpdateAuxiliary(true,false,false,0,true);}
        static VivePracticeTaskEventTransaction StageOnset(Fixture f)
        {
            f.Clock.Value=12;f.Task.MarkPresented(f.Task.CurrentTrial.TrialId);VivePracticeTaskEventTransaction tx;
            Check.Equal(VivePracticeTaskCursorResult.Staged,f.Cursor.TryStage(out tx));Check.True(tx!=null);return tx;
        }
        static void Bytes(byte[] expected,byte[] actual)
        {Check.True(expected!=null&&actual!=null&&expected.Length==actual.Length);for(int i=0;i<expected.Length;i++)Check.Equal(expected[i],actual[i]);}
        public static void Register(TestSuite suite)
        {
            suite.Run("practice cursor append advances two real task transactions together with fake target",()=>{
                var f=new Fixture();var initial=f.Read();Check.Equal(3,initial.Events.Count);
                var onset=StageOnset(f);Check.Equal(1,onset.Events.Count);Check.Equal("stimulus_presented",onset.Events[0].Kind);
                Check.True(f.Host.TryAppendPending(f.Cursor,onset));VivePracticeTaskEventTransaction pending;
                Check.Equal(VivePracticeTaskCursorResult.NoEvents,f.Cursor.TryStage(out pending));
                f.Task.UpdateTrackpad(true,Neutral());f.Clock.Value=13;
                f.Task.UpdateTrackpad(true,Press(f.Task.CurrentTrial.Stimulus.Ink));
                Check.Equal(VivePracticeTaskCursorResult.Staged,f.Cursor.TryStage(out pending));Check.True(pending!=null);
                Check.Equal(3,pending.Events.Count);Check.Equal("first_response",pending.Events[0].Kind);
                Check.Equal("phase_changed",pending.Events[1].Kind);Check.Equal("trial_planned",pending.Events[2].Kind);
                Check.True(f.Host.TryAppendPending(f.Cursor,pending));var read=f.Read();Check.Equal(VivePracticeMultiStatus.Unclean,read.Status);
                Check.Equal(7,read.Events.Count);for(int i=1;i<read.Events.Count;i++)Check.Equal(read.Events[i-1].Sequence+1,read.Events[i].Sequence);
                Check.Equal("first_response",read.Events[4].Kind);Check.Equal("trial_planned",read.Events[6].Kind);
                Check.True(!read.ConsentAuthenticated&&!read.DurableStorageProven&&!read.FullPracticeCoverageProven);
            });
            suite.Run("practice cursor append partial fake write keeps only prior committed reader prefix",()=>{
                var f=new Fixture();var tx=StageOnset(f);var before=f.Store.CopyForTest("target");
                Check.True(!f.Host.TryAppendPending(f.Cursor,tx,length=>new ViveFakeWritePlan(20,true,null)));
                var after=f.Store.CopyForTest("target");Check.Equal(before.Length+20,after.Length);
                Check.Equal((ViveFakeTargetState?)ViveFakeTargetState.Uncertain,f.Store.StateForTest("target"));
                var read=f.Read();Check.Equal(VivePracticeMultiStatus.Truncated,read.Status);Check.Equal(3,read.Events.Count);
                Check.Equal(before.Length,read.CommittedBytes);Check.True(f.Cursor.Faulted);
                Check.True(!f.Host.TryAppendPending(f.Cursor,tx));Bytes(after,f.Store.CopyForTest("target"));
            });
            suite.Run("practice cursor append full fake write with in-flight withdrawal is uncertain",()=>{
                var f=new Fixture();var tx=StageOnset(f);
                Check.True(!f.Host.TryAppendPending(f.Cursor,tx,length=>new ViveFakeWritePlan(length,false,f.Choices.Withdraw)));
                var bytes=f.Store.CopyForTest("target");Check.True(bytes!=null);
                Check.Equal((ViveFakeTargetState?)ViveFakeTargetState.Uncertain,f.Store.StateForTest("target"));
                // A full structural marker cannot prove valid consent at completion.
                Check.Equal(4,f.Read().Events.Count);Check.True(f.Cursor.Faulted);
                Check.True(!f.Host.TryAppendPending(f.Cursor,tx));Bytes(bytes,f.Store.CopyForTest("target"));
            });
            suite.Run("practice cursor append plan callback withdrawal stops before fake append",()=>{
                var f=new Fixture();var tx=StageOnset(f);var before=f.Store.CopyForTest("target");
                Check.True(!f.Host.TryAppendPending(f.Cursor,tx,length=>{f.Choices.Withdraw();return new ViveFakeWritePlan(length,false,null);}));
                Bytes(before,f.Store.CopyForTest("target"));Check.True(f.Cursor.Faulted);
                Check.True(!f.Host.TryAppendPending(f.Cursor,tx));
            });
            suite.Run("practice cursor append pending withdrawal pause and ABA cannot append",()=>{
                for(int mode=0;mode<3;mode++)
                {
                    var f=new Fixture();var tx=StageOnset(f);var before=f.Store.CopyForTest("target");
                    if(mode==0)f.Choices.Withdraw();else if(mode==1)f.Task.ParticipantPause();
                    else{f.Task.ParticipantPause();Resume(f);Check.Equal(Phase.Practice,f.Task.Phase);}
                    Check.True(!f.Host.TryAppendPending(f.Cursor,tx));Bytes(before,f.Store.CopyForTest("target"));
                    Check.True(!f.Host.TryAppendPending(f.Cursor,tx));
                }
            });
            suite.Run("practice cursor append callback exception retains uncertain prefix",()=>{
                var f=new Fixture();var tx=StageOnset(f);var before=f.Store.CopyForTest("target");
                Check.True(!f.Host.TryAppendPending(f.Cursor,tx,length=>new ViveFakeWritePlan(20,false,()=>{throw new InvalidOperationException();})));
                var after=f.Store.CopyForTest("target");Check.Equal(before.Length+20,after.Length);
                Check.Equal((ViveFakeTargetState?)ViveFakeTargetState.Uncertain,f.Store.StateForTest("target"));
                Check.Equal(3,f.Read().Events.Count);Check.True(!f.Host.TryAppendPending(f.Cursor,tx));Bytes(after,f.Store.CopyForTest("target"));
            });
            suite.Run("practice cursor append rejects a copied transaction without changing target",()=>{
                var f=new Fixture();var tx=StageOnset(f);var before=f.Store.CopyForTest("target");
                var copy=new VivePracticeTaskEventTransaction(tx.Revision,tx.Events);
                Check.True(!f.Host.TryAppendPending(f.Cursor,copy));Bytes(before,f.Store.CopyForTest("target"));
                Check.True(!f.Host.TryAppendPending(f.Cursor,tx));Bytes(before,f.Store.CopyForTest("target"));
            });
            suite.Run("practice cursor append rejects a foreign cursor and preserves both targets",()=>{
                var a=new Fixture();var b=new Fixture();var tx=StageOnset(a);
                var beforeA=a.Store.CopyForTest("target");var beforeB=b.Store.CopyForTest("target");
                Check.True(!b.Host.TryAppendPending(a.Cursor,tx));Bytes(beforeA,a.Store.CopyForTest("target"));Bytes(beforeB,b.Store.CopyForTest("target"));
            });
            suite.Run("practice cursor append capacity rejects without partial transaction",()=>{
                var f=new Fixture(null,65536,1);var before=f.Store.CopyForTest("target");var tx=StageOnset(f);
                Check.True(!f.Host.TryAppendPending(f.Cursor,tx));Bytes(before,f.Store.CopyForTest("target"));
                Check.True(!f.Host.TryAppendPending(f.Cursor,tx));Check.True(f.Cursor.Faulted);
            });
            suite.Run("practice cursor append nested request cannot commit or retry",()=>{
                var f=new Fixture();var tx=StageOnset(f);bool nested=true;
                Check.True(!f.Host.TryAppendPending(f.Cursor,tx,length=>new ViveFakeWritePlan(length,false,()=>nested=f.Host.TryAppendPending(f.Cursor,tx))));
                Check.True(!nested&&f.Cursor.Faulted);var after=f.Store.CopyForTest("target");
                Check.Equal((ViveFakeTargetState?)ViveFakeTargetState.Uncertain,f.Store.StateForTest("target"));
                Check.True(!f.Host.TryAppendPending(f.Cursor,tx));Bytes(after,f.Store.CopyForTest("target"));
            });
            suite.Run("practice cursor append rejects non-owner call without target mutation",()=>{
                var f=new Fixture();var tx=StageOnset(f);var before=f.Store.CopyForTest("target");bool result=true;
                var thread=new Thread(()=>result=f.Host.TryAppendPending(f.Cursor,tx)){IsBackground=true};thread.Start();Check.True(thread.Join(5000));
                Check.True(!result);Bytes(before,f.Store.CopyForTest("target"));
            });
            suite.Run("practice cursor append foreign-thread request during fake write latches uncertain",()=>{
                var f=new Fixture();var tx=StageOnset(f);bool nested=true,joined=false;
                Check.True(!f.Host.TryAppendPending(f.Cursor,tx,length=>new ViveFakeWritePlan(length,false,()=>{
                    var thread=new Thread(()=>nested=f.Host.TryAppendPending(f.Cursor,tx)){IsBackground=true};
                    thread.Start();joined=thread.Join(5000);
                })));
                Check.True(joined&&!nested&&f.Cursor.Faulted);
                Check.Equal((ViveFakeTargetState?)ViveFakeTargetState.Uncertain,f.Store.StateForTest("target"));
                var bytes=f.Store.CopyForTest("target");Check.True(bytes!=null);Check.True(!f.Host.TryAppendPending(f.Cursor,tx));Bytes(bytes,f.Store.CopyForTest("target"));
            });
        }
    }
}
