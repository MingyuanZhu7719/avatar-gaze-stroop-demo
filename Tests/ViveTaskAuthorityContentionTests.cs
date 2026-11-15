#if VIVE_SYNTHETIC_BATCH_FIXTURE
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    public static class ViveTaskAuthorityContentionTests
    {
        sealed class PausedProvider : IViveProEyeClient
        {
            internal readonly ManualResetEvent Entered=new ManualResetEvent(false), Release=new ManualResetEvent(false);
            public string Diagnostic {get{return "scripted task authority";}}
            public bool Start(){return true;}
            public ViveProEyePoll Poll(){Entered.Set();Release.WaitOne();return null;}
            public void Dispose(){}
        }
        sealed class OrderedProvider : IViveProEyeClient
        {
            internal readonly AutoResetEvent Waiting=new AutoResetEvent(false), Ready=new AutoResetEvent(false);
            ViveProEyePoll next;
            public string Diagnostic {get{return "scripted ordered status";}}
            public bool Start(){return true;}
            public ViveProEyePoll Poll(){Waiting.Set();Ready.WaitOne();return next;}
            internal void Send(ViveProEyePoll next,bool wait=true)
            {this.next=next;Ready.Set();if(wait)Check.True(Waiting.WaitOne(3000));}
            public void Dispose(){}
        }
        sealed class GateHolder : IDisposable
        {
            readonly ManualResetEvent entered=new ManualResetEvent(false), release=new ManualResetEvent(false);
            readonly Thread thread;
            internal GateHolder(object gate)
            {
                thread=new Thread(()=>{lock(gate){entered.Set();release.WaitOne();}}){IsBackground=true};
                thread.Start();Check.True(entered.WaitOne(3000),"gaze lock was not acquired by external test thread");
            }
            public void Dispose(){release.Set();Check.True(thread.Join(3000));entered.Dispose();release.Dispose();}
        }
        sealed class Fixture : IDisposable
        {
            internal readonly MonotonicClock Clock=new MonotonicClock("task-authority-contention");
            internal readonly PausedProvider Provider=new PausedProvider();
            internal readonly ViveProEyeWorker Worker;
            internal readonly ViveDevelopmentParticipant Task;
            internal readonly ViveNamedSessionFrameAdmission Admission;
            internal readonly ViveNamedRawRecordingPump Pump;
            long head;
            internal bool HeadTracked=true;
            internal bool RepeatHeadSequence;
            internal Fixture()
            {
                Worker=new ViveProEyeWorker(Clock,
                    ()=>new ViveHeadFrame(RepeatHeadSequence?head:++head,Clock.Now,true,HeadTracked,Rotation.Identity),()=>Provider);
                Check.True(Worker.Start()&&Provider.Entered.WaitOne(3000));
                var option=ViveNamedOptionProvenance.Select(Actor.Owner,ViveNamedOption.Automatic,1,"authority-geometry");
                var plan=ViveNamedSimulationPlan.Select(Actor.Owner,"authority-plan",option);
                var intent=ViveNamedRecordingIntent.Select(Actor.Owner,"authority-intent",true,true);
                var config=SessionConfiguration.NamedRecordingDevelopment("authority-session",37,plan,intent);
                var geometry=new AutomaticGazeGeometry(Rotation.Identity,new Vec3(0,0,1),
                    new Rotation(0,Math.Sqrt(.5),0,Math.Sqrt(.5)),new Vec3(1,0,0),"authority-geometry");
                var map=new Dictionary<ViveTrackpadDirection,Colour>{{ViveTrackpadDirection.Up,Colour.Red},
                    {ViveTrackpadDirection.Right,Colour.Green},{ViveTrackpadDirection.Down,Colour.Blue},{ViveTrackpadDirection.Left,Colour.Yellow}};
                Task=new ViveDevelopmentParticipant(config,Clock,geometry,map,.5,true);
                Admission=ViveNamedSessionFrameAdmission.CreateSyntheticForTest(Task,Worker,Clock,256);
                Check.True(Admission.ConfirmTask()&&Admission.ConfirmEye());
                Check.True(Admission.TryBindQueueBudget(ViveRawByteBudget.SelectExplicit(33554432)));
                Pump=ViveNamedRawRecordingPump.StartSyntheticWithStatusForTest(Task,Worker,Clock,Admission,
                    new ViveNamedRawRecordingPump.SyntheticWriter(Admission));
                Task.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,false,0,0));
            }
            internal ViveProEyePoll Poll()
            {
                ViveProEyePoll poll;Check.True(Worker.TryPollTaskForRecording(out poll));return poll;
            }
            internal void Stage()
            {
                bool waiting;Check.True(Pump.StageRigFrame(Poll(),1,false,out waiting),
                    "independent task Update could not stage: "+Pump.FirstFault);
                Check.True(!waiting&&Pump.RawTaskEpochThisFrame&&Pump.VerifyStagedEpoch());
            }
            internal void Begin()
            {
                var planned=Task.PrepareNamedRecordingBlockPlan(Actor.Owner,true);Check.True(Pump.DrainTaskEvents());
                ViveBlockPlanRequest request;ViveNamedSessionFrameAdmission.SyntheticPlanProof proof;
                Check.True(ViveBlockPlanRequest.TryCapture(Task,Clock,planned,out request)&&
                    Admission.TryValidateBlockPlanRequest(request,planned),"matching plan became unavailable under gaze gate contention");
                Check.True(Admission.TryIssueSyntheticPlanProof(request,planned,out proof));
                Check.True(Task.TryBeginSyntheticPracticeForFixture(Admission,request,proof));
                Check.True(Pump.DrainTaskEvents());Check.Equal(Phase.Practice,Task.Phase);
            }
            public void Dispose()
            {
                Pump.Stop();SpinWait.SpinUntil(()=>Pump.HasExited,3000);
                Worker.TryRequestStop();Provider.Release.Set();Worker.Stop(1000);Worker.Dispose();
            }
        }
        public static void Register(TestSuite suite)
        {
            suite.Run("held eye gate cannot interrupt plan or first Practice presentation and answer",()=>{
                using(var f=new Fixture())using(var held=new GateHolder(f.Worker.RecordingTicketGate))
                {
                    Check.Equal(ViveProEyeWorker.RecordingRunRead.Ready,f.Pump.ReadExactWorkerRun(f.Worker));
                    Check.Equal(ViveNamedRawRecordingPump.Preparation.Busy,f.Pump.PrepareStatus());
                    f.Begin();f.Stage();
                    Check.True(f.Pump.TryRunReservedOrdinaryMutation(()=>f.Task.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,false,0,0))));
                    Check.True(f.Pump.DrainTaskEvents());f.Stage();
                    var trial=f.Task.CurrentTrial;ViveNamedSessionFrameAdmission.PresentationReservation presentation;
                    Check.True(f.Pump.TryPreparePresentation(42,8,out presentation),"presentation reserve "+f.Pump.FirstFault);
                    Check.True(presentation.Ticket.HeadOnly);
                    Check.Equal((ViveRawEyeObservation.Reason?)ViveRawEyeObservation.Reason.SnapshotBusy,presentation.Ticket.EyeUnavailable);
                    Check.True(f.Pump.CommitPresentation(presentation,42,8,trial.TrialId,trial.Stimulus.Word,trial.Stimulus.Ink,f.Clock.Now));
                    Check.True(trial.Onset.HasValue&&f.Pump.VerifyTaskRunAuthorityForDisplay());
                    f.Stage();
                    var ink=trial.Stimulus.Ink;
                    double x=ink==Colour.Green?.9:ink==Colour.Yellow?-.9:0,y=ink==Colour.Red?.9:ink==Colour.Blue?-.9:0;
                    Check.True(f.Pump.TryRunReservedOrdinaryMutation(()=>f.Task.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,true,x,y))));
                    Check.True(f.Pump.DrainTaskEvents());
                    Check.Equal(1,f.Task.ReadEventsAfter(-1,4096).Count(e=>e.Kind=="first_response"&&e.TrialId==trial.TrialId));
                    Check.Equal(0,f.Task.ReadEventsAfter(-1,4096).Count(e=>e.Kind=="tracking_gap_begin"));
                    Check.True(!f.Admission.IoFence.IsRevoked&&f.Pump.State==ViveNamedRawRecordingPump.Outcome.Running);
                }
            });
            suite.Run("lock-free worker revocation denies an existing independent task epoch",()=>{
                using(var f=new Fixture())using(var held=new GateHolder(f.Worker.RecordingTicketGate))
                {
                    f.Begin();f.Stage();
                    Check.True(f.Pump.TryRunReservedOrdinaryMutation(()=>f.Task.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,false,0,0))));
                    Check.True(f.Pump.DrainTaskEvents());f.Stage();
                    Check.True(!f.Worker.TryRequestStop(),"cleanup gate unexpectedly acquired");
                    Check.Equal(ViveProEyeWorker.RecordingRunRead.Unavailable,f.Pump.ReadExactWorkerRun(f.Worker));
                    Check.True(!f.Pump.VerifyTaskRunAuthorityForDisplay());
                    Check.True(!f.Pump.VerifyStagedEpoch()&&f.Admission.IoFence.IsRevoked);
                }
            });
            suite.Run("real status FIFO overflow revokes independent task authority",()=>{
                var clock=new MonotonicClock("independent-status-fault");
                var provider=new OrderedProvider();long seq=0;
                var worker=new ViveProEyeWorker(clock,()=>new ViveHeadFrame(++seq,clock.Now,true,true,Rotation.Identity),()=>provider);
                try
                {
                    Check.True(worker.Start()&&provider.Waiting.WaitOne(3000));
                    object run;Check.True(worker.TryEnableStatusEvidence(1,out run));
                    provider.Send(null); // pre-enable in-flight return is not admitted
                    provider.Send(null); // first registered status fills capacity
                    Check.True(worker.IsAttachedTaskRunCurrent(run));
                    provider.Send(new ViveProEyePoll(null,null,ViveEyeRuntimeState.Error,ViveEyeCalibrationState.Unknown,false,-7,null));
                    Check.True(!worker.IsAttachedTaskRunCurrent(run));
                    ViveProEyePoll poll;Check.True(!worker.TryPollTaskForRecording(out poll));
                }
                finally{worker.TryRequestStop();provider.Send(null,false);worker.Stop(1000);worker.Dispose();}
            });
            suite.Run("gaze gate contention cannot conceal real same-head pose loss",()=>{
                using(var f=new Fixture())using(var held=new GateHolder(f.Worker.RecordingTicketGate))
                {
                    f.Begin();f.Stage();
                    Check.True(f.Pump.TryRunReservedOrdinaryMutation(()=>f.Task.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,false,0,0))));
                    Check.True(f.Pump.DrainTaskEvents());f.Stage();Check.True(f.Pump.DrainTaskEvents());
                    f.HeadTracked=false;Check.True(f.Pump.ObserveTrackingHeadLoss(f.Poll(),false));
                    Check.True(f.Pump.TrackingGapOpen&&!f.Admission.IoFence.IsRevoked);
                    Check.Equal(Phase.Paused,f.Task.Phase);
                }
            });
            suite.Run("independent task authority refuses a repeated head observation sequence",()=>{
                using(var f=new Fixture())using(var held=new GateHolder(f.Worker.RecordingTicketGate))
                {
                    f.Begin();f.Stage();
                    Check.True(f.Pump.TryRunReservedOrdinaryMutation(()=>f.Task.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,false,0,0))));
                    Check.True(f.Pump.DrainTaskEvents());f.Stage();Check.True(f.Pump.DrainTaskEvents());
                    f.RepeatHeadSequence=true;bool waiting;
                    Check.True(!f.Pump.StageRigFrame(f.Poll(),1,false,out waiting)&&!waiting&&f.Admission.IoFence.IsRevoked);
                }
            });
        }
    }
}
#endif
