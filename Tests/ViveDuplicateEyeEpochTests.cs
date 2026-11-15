#if VIVE_SYNTHETIC_BATCH_FIXTURE
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    public static class ViveDuplicateEyeEpochTests
    {
        const BindingFlags InstancePrivate=BindingFlags.Instance|BindingFlags.NonPublic;
        sealed class Client : IViveProEyeClient
        {
            readonly Queue<ViveProEyePoll> queue=new Queue<ViveProEyePoll>();
            internal readonly AutoResetEvent Waiting=new AutoResetEvent(false),Release=new AutoResetEvent(false);
            public string Diagnostic {get{return "scripted duplicate gaze test";}}
            public bool Start(){return true;}
            public ViveProEyePoll Poll()
            {
                Waiting.Set();
                if(!Release.WaitOne(10000))throw new InvalidOperationException("scripted poll timeout");
                lock(queue)return queue.Dequeue();
            }
            internal void Send(ViveProEyePoll value,bool wait=true)
            {
                lock(queue)queue.Enqueue(value);
                Release.Set();
                if(wait)Check.True(Waiting.WaitOne(3000),"scripted poll did not publish");
            }
            public void Dispose(){}
        }
        static ViveDevelopmentParticipant NewPracticeTask(MonotonicClock clock)
        {
            var option=ViveNamedOptionProvenance.Select(Actor.Owner,ViveNamedOption.Automatic,1,"duplicate-geometry");
            var plan=ViveNamedSimulationPlan.Select(Actor.Owner,"duplicate-plan",option);
            var intent=ViveNamedRecordingIntent.Select(Actor.Owner,"duplicate-intent",true,true);
            var config=SessionConfiguration.NamedRecordingDevelopment("duplicate-session",37,plan,intent);
            var geometry=new AutomaticGazeGeometry(Rotation.Identity,new Vec3(0,0,1),
                new Rotation(0,Math.Sqrt(.5),0,Math.Sqrt(.5)),new Vec3(1,0,0),"duplicate-geometry");
            var map=new Dictionary<ViveTrackpadDirection,Colour>{{ViveTrackpadDirection.Up,Colour.Red},{ViveTrackpadDirection.Right,Colour.Green},
                {ViveTrackpadDirection.Down,Colour.Blue},{ViveTrackpadDirection.Left,Colour.Yellow}};
            return new ViveDevelopmentParticipant(config,clock,geometry,map,.5,true);
        }
        static ViveProEyePoll Eye(MonotonicClock clock,int frame,int mask=2,int native=0,bool zeroDirection=false)
        {
            var ray=new ViveEyeRay((ulong)mask,(mask&1)!=0,(mask&2)!=0,new Vec3(0,0,0),
                zeroDirection?new Vec3(0,0,0):new Vec3(.6,0,.8));
            return new ViveProEyePoll(null,new ViveEyeFrame(frame,frame*8,clock.Now,false,null,null,ray),
                ViveEyeRuntimeState.Working,ViveEyeCalibrationState.ReportedReady,true,native,2);
        }
        public static void Register(TestSuite suite)
        {
            suite.Run("active Practice duplicate permits ordinary input and marks one no-new-sample observation",()=>Exercise(0));
#if !VIVE_DUPLICATE_EPOCH_BASELINE_TEST
            suite.Run("duplicate gaze does not bypass repeated head observation",()=>Exercise(1));
            suite.Run("duplicate gaze after a healthy status revision still allows current-head behavior",()=>Exercise(2));
            suite.Run("a repeated gaze frame cannot excuse an expired fresh-head witness",()=>Exercise(3));
            suite.Run("current head authority can replace an unused Unity snapshot without replaying gaze",()=>Exercise(4));
            suite.Run("out of order native gaze is marked missing rather than replayed",()=>Exercise(5));
            suite.Run("queued invalid-then-healthy gaze quality remains observable",()=>Exercise(6));
            suite.Run("mixed invalid direction and native-error status stays recorded without revocation",()=>Exercise(7));
            suite.Run("invalid direction flags preserve a zero-vector as quality metadata only",()=>Exercise(8));
            suite.Run("valid-claimed zero gaze stays visible to status audit after later bad samples",()=>Exercise(9));
            suite.Run("active frame accepts recovered gaze after preserving intervening invalidity",()=>Exercise(10));
            suite.Run("healthy pending status can span reservation and commit while preserving FIFO admission",()=>Exercise(11));
            suite.Run("invalid gaze during a reserved presentation does not suppress genuine onset",()=>Exercise(12));
#endif
        }
        static void Exercise(int mode)
        {
            var clock=new MonotonicClock("duplicate-epoch-clock");var client=new Client();
            long head=0;bool advanceHead=true;
            var worker=new ViveProEyeWorker(clock,
                ()=>new ViveHeadFrame(advanceHead?++head:head,clock.Now,true,true,Rotation.Identity),()=>client);
            ViveNamedRawRecordingPump pump=null;
            try
            {
                Check.True(worker.Start()&&client.Waiting.WaitOne(3000));
                var task=NewPracticeTask(clock);
                var admission=ViveNamedSessionFrameAdmission.CreateSyntheticForTest(task,worker,clock,256);
                Check.True(admission.ConfirmTask()&&admission.ConfirmEye());
                Check.True(admission.TryBindQueueBudget(ViveRawByteBudget.SelectExplicit(33554432)));
                var writer=new ViveNamedRawRecordingPump.SyntheticWriter(admission);
                pump=ViveNamedRawRecordingPump.StartSyntheticWithStatusForTest(task,worker,clock,admission,writer);
                task.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,false,0,0));
                // Fixture-only design entry. Native plan/Unity coverage is separate.
                if(mode>=11)
                {
                    var planEvent=task.PrepareNamedRecordingBlockPlan(Actor.Owner,true);
                    Check.True(pump.DrainTaskEvents());
                    ViveBlockPlanRequest request;ViveNamedSessionFrameAdmission.SyntheticPlanProof proof;
                    Check.True(ViveBlockPlanRequest.TryCapture(task,clock,planEvent,out request));
                    Check.True(admission.TryIssueSyntheticPlanProof(request,planEvent,out proof));
                    Check.True(task.TryBeginSyntheticPracticeForFixture(admission,request,proof));
                }
                else
                {
                    var engine=(Experiment)typeof(ViveDevelopmentParticipant).GetField("experiment",InstancePrivate).GetValue(task);
                    engine.PrepareNamedRecordingBlockPlan(Actor.Owner,true);
                    engine.BeginFamiliarization(Actor.Owner);engine.BeginPractice(Actor.Owner);
                }
                Check.Equal(Phase.Practice,task.Phase);
                client.Send(null);client.Send(Eye(clock,2));
                Check.Equal(ViveNamedRawRecordingPump.Preparation.Ready,pump.PrepareStatus());
                ViveProEyePoll frame;
                Check.True(worker.TryPollForRecording(out frame));
                Check.True(pump.StageRigFrame(frame,1));
                if(mode<11)Check.True(pump.DrainTaskEvents());
                var next=typeof(ViveNamedSessionFrameAdmission).GetField("nextOrdinal",InstancePrivate);
                long firstAfterStaging=(long)next.GetValue(admission);
#if !VIVE_DUPLICATE_EPOCH_BASELINE_TEST
                if(mode>=11)
                {
                    client.Send(Eye(clock,3,3)); // Healthy origin metadata changes after frame admission.
                    Check.True(pump.VerifyStagedEpoch(),"healthy-only metadata changed a consumed epoch");
                    ViveNamedSessionFrameAdmission.PresentationReservation token;
                    Check.True(pump.TryPreparePresentation(42,8,out token),"healthy-only metadata prevented reservation: "+pump.FirstFault);
                    var trial=task.CurrentTrial;
                    Check.True(token!=null&&!trial.Onset.HasValue);
                    Check.Equal(firstAfterStaging,(long)next.GetValue(admission)); // Status is still reserved only at worker FIFO.
                    client.Send(Eye(clock,4,mode==11?2:0));
                    bool committed=pump.CommitPresentation(token,42,8,trial.TrialId,trial.Stimulus.Word,trial.Stimulus.Ink,12.75);
                    Check.True(committed&&trial.Onset.HasValue);
                    Check.Equal(firstAfterStaging+1,(long)next.GetValue(admission)); // One software presentation row.
                    Check.Equal(ViveNamedRawRecordingPump.Preparation.Ready,pump.PrepareStatus());
                    Check.Equal(firstAfterStaging+3,(long)next.GetValue(admission)); // Two original status rows follow.
                    Check.True(!admission.IoFence.IsRevoked);
                    return;
                }
                if(mode==10)
                {
                    client.Send(Eye(clock,3,0));
                    client.Send(Eye(clock,4));
                    Check.True(worker.TryPollForRecording(out frame));
                    Check.True(pump.StageRigFrame(frame,1)&&pump.VerifyStagedEpoch()&&!admission.IoFence.IsRevoked);
                    Check.Equal(firstAfterStaging+3,(long)next.GetValue(admission)); // Invalid + healthy status and one actual recovered gaze row.
                    return;
                }
                if(mode>=6)
                {
                    client.Send(Eye(clock,3,0,0,mode>=8));
                    if(mode==7)client.Send(Eye(clock,4,2,17));
                    if(mode==9){client.Send(Eye(clock,4,2,0,true));client.Send(Eye(clock,5,0,0,true));}
                    client.Send(Eye(clock,6));
                    Check.Equal(ViveNamedRawRecordingPump.Preparation.Ready,pump.PrepareStatus());
                    Check.Equal(firstAfterStaging+(mode==7?3:mode==9?4:2),(long)next.GetValue(admission));
                    Check.True(pump.State==ViveNamedRawRecordingPump.Outcome.Running&&!admission.IoFence.IsRevoked);
                    return;
                }
#endif
                if(mode==1)advanceHead=false;
                if(mode==2){client.Send(Eye(clock,2,3));Check.Equal(ViveNamedRawRecordingPump.Preparation.Ready,pump.PrepareStatus());}
                if(mode==4)Check.True(worker.TryPollForRecording(out frame)); // drop this owner snapshot
                if(mode==5)client.Send(Eye(clock,1));
                else if(mode!=2)client.Send(Eye(clock,2));
                Check.True(worker.TryPollForRecording(out frame));
                bool waiting;
                bool ok=pump.StageRigFrame(frame,mode==3?1e-9:1,false,out waiting);
#if !VIVE_DUPLICATE_EPOCH_BASELINE_TEST
                if(mode==1||mode==3)
                {
                    Check.True(!ok&&!waiting&&admission.IoFence.IsRevoked);
                    Check.True(pump.FirstFault=="frame_ticket_Unavailable");
                    return;
                }
                if(mode==2||mode==4||mode==5)
                {
                    Check.True(ok&&!waiting&&!admission.IoFence.IsRevoked&&pump.RawTaskEpochThisFrame);
                    Check.Equal(firstAfterStaging+(mode==2?2:1),(long)next.GetValue(admission));
                    return;
                }
#endif
                Check.True(ok&&!waiting,"current synthetic Practice duplicate refused: "+pump.FirstFault);
#if !VIVE_DUPLICATE_EPOCH_BASELINE_TEST
                Check.True(pump.RawTaskEpochThisFrame);
#endif
                bool ran=false;
                Check.True(pump.TryRunReservedOrdinaryMutation(()=>{ran=true;task.Tick();})&&ran&&pump.DrainTaskEvents());
                Check.Equal(firstAfterStaging+1,(long)next.GetValue(admission));
                // A second Unity update can reuse the very same native sample,
                // but it also needs its own validated new head observation.
                Check.True(worker.TryPollForRecording(out frame)&&pump.StageRigFrame(frame,1));
#if !VIVE_DUPLICATE_EPOCH_BASELINE_TEST
                Check.True(pump.RawTaskEpochThisFrame);
#endif
                Check.True(pump.DrainTaskEvents()&&!admission.IoFence.IsRevoked);
                Check.Equal(firstAfterStaging+1,(long)next.GetValue(admission));
                client.Send(Eye(clock,3));
                Check.True(worker.TryPollForRecording(out frame)&&pump.StageRigFrame(frame,1));
#if !VIVE_DUPLICATE_EPOCH_BASELINE_TEST
                Check.True(pump.RawTaskEpochThisFrame);
#endif
                Check.True(pump.DrainTaskEvents());
                Check.Equal(firstAfterStaging+2,(long)next.GetValue(admission));
            }
            finally
            {
                if(pump!=null){pump.Stop();SpinWait.SpinUntil(()=>pump.HasExited,3000);}
                worker.TryRequestStop();client.Send(null,false);worker.Stop(1000);worker.Dispose();
            }
        }
    }
}
#endif
