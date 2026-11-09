#if VIVE_SYNTHETIC_BATCH_FIXTURE
using System;
using System.Collections.Generic;
using System.Threading;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    public static class ViveSyntheticSafetyLatchTests
    {
        sealed class Client:IViveProEyeClient
        {
            internal readonly ManualResetEvent Waiting=new ManualResetEvent(false),Release=new ManualResetEvent(false);
            public string Diagnostic {get{return "synthetic";}}
            public bool Start(){return true;}
            public ViveProEyePoll Poll(){Waiting.Set();Release.WaitOne();return null;}
            public void Dispose(){}
        }
        sealed class Fixture:IDisposable
        {
            internal readonly MonotonicClock Clock=new MonotonicClock("synthetic-safety-clock");
            internal readonly Client Source=new Client();internal readonly ViveProEyeWorker Worker;
            internal readonly ViveDevelopmentParticipant Task;internal readonly ViveNamedSessionFrameAdmission Admission;
            internal readonly ViveSyntheticSafetyLatch Latch;
            internal Fixture(bool practice)
            {
                Worker=new ViveProEyeWorker(Clock,()=>new ViveHeadFrame(1,Clock.Now,true,true,Rotation.Identity),()=>Source);
                Check.True(Worker.Start()&&Source.Waiting.WaitOne(3000));
                var option=ViveNamedOptionProvenance.Select(Actor.Owner,ViveNamedOption.Automatic,1,"fixture-geometry");
                var plan=ViveNamedSimulationPlan.Select(Actor.Owner,"fixture-plan",option);
                var intent=ViveNamedRecordingIntent.Select(Actor.Owner,"fixture-intent",true,true);
                var config=SessionConfiguration.NamedRecordingDevelopment("fixture-session",37,plan,intent);
                var geometry=new AutomaticGazeGeometry(Rotation.Identity,new Vec3(0,0,1),new Rotation(0,Math.Sqrt(.5),0,Math.Sqrt(.5)),new Vec3(1,0,0),"fixture-geometry");
                var map=new Dictionary<ViveTrackpadDirection,Colour>{{ViveTrackpadDirection.Up,Colour.Red},{ViveTrackpadDirection.Right,Colour.Green},{ViveTrackpadDirection.Down,Colour.Blue},{ViveTrackpadDirection.Left,Colour.Yellow}};
                Task=new ViveDevelopmentParticipant(config,Clock,geometry,map,.5,true);
                Admission=ViveNamedSessionFrameAdmission.CreateSyntheticForTest(Task,Worker,Clock,32);
                Check.True(Admission.ConfirmTask()&&Admission.ConfirmEye());
                if(practice)
                {
                    Task.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,false,0,0));
                    var planned=Task.PrepareNamedRecordingBlockPlan(Actor.Owner,true);bool staged;
                    do{Check.True(Admission.TryStageNextTaskEvent(out staged));}while(staged);
                    ViveBlockPlanRequest request;Check.True(ViveBlockPlanRequest.TryCapture(Task,Clock,planned,out request));
                    ViveNamedSessionFrameAdmission.SyntheticPlanProof proof;
                    Check.True(Admission.TryIssueSyntheticPlanProof(request,planned,out proof)&&Task.TryBeginSyntheticPracticeForFixture(Admission,request,proof));
                }
                Latch=new ViveSyntheticSafetyLatch(Task,Admission,Worker,Clock);
            }
            public void Dispose(){Latch.Close();Admission.Withdraw();Source.Release.Set();Check.True(Worker.Stop(1000));Worker.Dispose();}
        }
        public static void Register(TestSuite suite)
        {
            suite.Run("synthetic fault closes admission and pauses actual practice without withdrawal",()=>{
                using(var f=new Fixture(true))
                {Check.True(f.Latch.SignalFault(f.Latch.RunForTest));Check.True(f.Admission.State!=0);
                    Check.Equal(ViveSyntheticSafetyLatch.Result.Paused,f.Latch.PollOnOwner());Check.Equal(Phase.Paused,f.Task.Phase);
                    Check.True(f.Task.EndReason!=EndReason.Withdrawn);Check.Equal(ViveSyntheticSafetyLatch.Result.Paused,f.Latch.PollOnOwner());}
            });
            suite.Run("synthetic withdrawal outranks fault and flips shared fence",()=>{
                using(var f=new Fixture(true))
                {Check.True(f.Latch.SignalFault(f.Latch.RunForTest)&&f.Latch.SignalWithdrawal(f.Latch.RunForTest));
                    Check.True(f.Task.NamedRawRevocation.IsRevoked);Check.Equal(ViveSyntheticSafetyLatch.Result.Withdrawn,f.Latch.PollOnOwner());
                    Check.Equal(EndReason.Withdrawn,f.Task.EndReason.Value);}
            });
            suite.Run("synthetic safety defers busy gate without claiming action",()=>{
                using(var f=new Fixture(true))
                {var held=new ManualResetEvent(false);var release=new ManualResetEvent(false);
                    var thread=new Thread(()=>{lock(f.Task.NamedRecordingGate){held.Set();release.WaitOne();}}){IsBackground=true};thread.Start();
                    try{Check.True(held.WaitOne(3000));Check.True(f.Latch.SignalFault(f.Latch.RunForTest));
                        Check.Equal(ViveSyntheticSafetyLatch.Result.Pending,f.Latch.PollOnOwner());Check.Equal(Phase.Practice,f.Task.Phase);}
                    finally{release.Set();Check.True(thread.Join(3000));held.Dispose();release.Dispose();}
                    Check.Equal(ViveSyntheticSafetyLatch.Result.Paused,f.Latch.PollOnOwner());}
            });
            suite.Run("synthetic safety rejects stale capability and closed latch",()=>{
                using(var f=new Fixture(false))
                {Check.True(!f.Latch.SignalFault(new object()));Check.Equal(0,f.Admission.State);f.Latch.Close();
                    Check.True(!f.Latch.SignalWithdrawal(f.Latch.RunForTest));Check.True(!f.Task.NamedRawRevocation.IsRevoked);}
            });
            suite.Run("closing synthetic latch preserves an accepted withdrawal",()=>{
                using(var f=new Fixture(true))
                {Check.True(f.Latch.SignalWithdrawal(f.Latch.RunForTest));f.Latch.Close();
                    Check.Equal(ViveSyntheticSafetyLatch.Result.Withdrawn,f.Latch.PollOnOwner());Check.Equal(EndReason.Withdrawn,f.Task.EndReason.Value);}
            });
            suite.Run("synthetic full queue still allows the independent safety request",()=>{
                using(var f=new Fixture(false))
                {for(int i=0;i<32;i++)Check.True(f.Admission.TryPublishSyntheticForTest(new object()));
                    Check.True(!f.Admission.TryPublishSyntheticForTest(new object()));Check.True(f.Latch.SignalFault(f.Latch.RunForTest));
                    Check.Equal(ViveSyntheticSafetyLatch.Result.NotApplied,f.Latch.PollOnOwner());Check.Equal(Phase.Preparation,f.Task.Phase);}
            });
            suite.Run("synthetic safety may only act on its owner thread",()=>{
                using(var f=new Fixture(true))
                {Check.True(f.Latch.SignalFault(f.Latch.RunForTest));var result=ViveSyntheticSafetyLatch.Result.Idle;
                    var thread=new Thread(()=>result=f.Latch.PollOnOwner()){IsBackground=true};thread.Start();Check.True(thread.Join(3000));
                    Check.Equal(ViveSyntheticSafetyLatch.Result.WrongOwner,result);Check.Equal(Phase.Practice,f.Task.Phase);
                    Check.Equal(ViveSyntheticSafetyLatch.Result.Paused,f.Latch.PollOnOwner());}
            });
            suite.Run("synthetic fault in preparation does not assert pause",()=>{
                using(var f=new Fixture(false))
                {Check.True(f.Latch.SignalFault(f.Latch.RunForTest));Check.Equal(ViveSyntheticSafetyLatch.Result.NotApplied,f.Latch.PollOnOwner());
                    Check.Equal(Phase.Preparation,f.Task.Phase);Check.True(f.Task.EndReason!=EndReason.Withdrawn);}
            });
        }
    }
}
#endif
