#if VIVE_SYNTHETIC_BATCH_FIXTURE
using System;
using System.Linq;
using System.Threading;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    // Uses the actual worker-issued head authority, admission FIFO and writer
    // pump. The provider and rendered cue observations are synthetic; no SDK,
    // camera, real participant data or physical presentation is claimed.
    public static class VisualSearchRawFrameLifecycleTests
    {
        sealed class Provider:IViveProEyeClient
        {
            internal readonly ManualResetEvent Entered=new ManualResetEvent(false),Release=new ManualResetEvent(false);
            public string Diagnostic {get{return "search frame lifecycle fixture";}}
            public bool Start(){return true;}
            public ViveProEyePoll Poll(){Entered.Set();Release.WaitOne();return null;}
            public void Dispose(){}
        }
        sealed class GateHolder:IDisposable
        {
            readonly ManualResetEvent entered=new ManualResetEvent(false),release=new ManualResetEvent(false);
            readonly Thread thread;
            internal GateHolder(object gate)
            {
                thread=new Thread(()=>{lock(gate){entered.Set();release.WaitOne();}}){IsBackground=true};
                thread.Start();Check.True(entered.WaitOne(3000));
            }
            public void Dispose(){release.Set();Check.True(thread.Join(3000));entered.Dispose();release.Dispose();}
        }
        sealed class Fixture:IDisposable
        {
            internal readonly MonotonicClock Clock=new MonotonicClock("search-frame-lifecycle-clock");
            internal readonly Provider Source=new Provider();
            internal readonly ViveProEyeWorker Worker;
            internal readonly ViveDevelopmentParticipant Task;
            internal readonly ViveNamedSessionFrameAdmission Admission;
            internal readonly ViveNamedRawRecordingPump Pump;
            long head;int frame;
            internal Fixture()
            {
                var geometry=new AutomaticGazeGeometry(Rotation.Identity,new Vec3(0,0,1),
                    new Rotation(0,Math.Sqrt(.5),0,Math.Sqrt(.5)),new Vec3(1,0,0),"search-frame-geometry");
                var allocation=new Allocation("search-frame-allocation",new[]{Condition.B,Condition.C,Condition.A,Condition.D},GazeTarget.Participant,new[]{0,1,2,3});
                var config=SessionConfiguration.WithTaskKind(new SessionConfiguration("search-frame-session",73,allocation,RunKind.Simulation,"en",5,30,true,0,true,true,true),TaskKind.VisualSearch);
                config=SessionConfiguration.LegacyRecordingDevelopment(config,
                    ViveLegacyRecordingDesign.Select(Actor.Owner,"search-frame-design",allocation,geometry.Version,ViveFixedTrackpadMapping.Version),
                    ViveLegacyRecordingIntent.Select(Actor.Owner,"search-frame-intent",true,true));
                Task=new ViveDevelopmentParticipant(config,Clock,geometry,ViveFixedTrackpadMapping.Create(),.5,true);
                Worker=new ViveProEyeWorker(Clock,()=>new ViveHeadFrame(++head,Clock.Now,true,true,Rotation.Identity),()=>Source);
                Check.True(Worker.Start()&&Source.Entered.WaitOne(3000));
                double taskChoice=Clock.Now,eyeChoice=Clock.Now;
                Admission=ViveNamedSessionFrameAdmission.CreateLegacySyntheticForTest(Task,Worker,Clock,256,taskChoice,eyeChoice);
                Check.True(Admission.ConfirmTask()&&Admission.ConfirmEye());
                Check.True(Admission.TryBindQueueBudget(ViveRawByteBudget.SelectExplicit(33554432)));
                Pump=ViveNamedRawRecordingPump.StartLegacySyntheticWithStatusForTest(Task,Worker,Clock,Admission,
                    new ViveNamedRawRecordingPump.SyntheticWriter(Admission));
                Stage();Mutate(()=>Task.RecordLegacySoftwareChoices(Clock,taskChoice,eyeChoice));
                Mutate(()=>Task.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,false,0,0)));Drain();Begin(true);
            }
            internal bool TryStage()
            {
                // Keep synthetic owner updates near a VR frame cadence; an
                // unpaced loop can enqueue hundreds while the real consumer's
                // intentional 20 ms idle wait has not elapsed even once.
                Thread.Sleep(10);
                // Match the rig's allowed Busy path before it obtains the next
                // owner head poll. Release contention after admission so this
                // is not a fixture that also starves the writer for all 104 trials.
                using(var heldEyePublication=new GateHolder(Worker.RecordingTicketGate))
                {
                    Check.Equal(ViveNamedRawRecordingPump.Preparation.Busy,Pump.PrepareStatus());
                    ViveProEyePoll poll;Check.True(Worker.TryPollTaskForRecording(out poll));bool waiting;
                    bool staged=Pump.StageRigFrame(poll,1,false,out waiting);Check.True(!waiting);frame++;return staged;
                }
            }
            internal void Stage(){Check.True(TryStage(),"stage: "+Pump.FirstFault);Check.True(Pump.VerifyStagedEpoch());}
            internal void Mutate(Action action)
            {
                int queued=Admission.Count;Exception failed=null;bool ok=Pump.TryRunReservedOrdinaryMutation(()=>{try{action();}catch(Exception error){failed=error;throw;}});
                Check.True(ok,"mutation: "+Pump.FailureSnapshot+"; queued_before="+queued+"; phase="+Task.Phase+"; action="+(failed==null?"none":failed.ToString()));
            }
            internal void Drain(){Check.True(Pump.DrainTaskEvents(),"drain: "+Pump.FirstFault);}
            internal void Begin(bool practice)
            {
                Stage();JournalEvent planned=null;Mutate(()=>planned=Task.PrepareLegacyRecordingBlockPlan(Actor.Owner,practice));Drain();
                ViveBlockPlanRequest request;Check.True(ViveBlockPlanRequest.TryCapture(Task,Clock,planned,out request));
                ViveNamedSessionFrameAdmission.SyntheticPlanProof proof;Check.True(Admission.TryIssueSyntheticPlanProof(request,planned,out proof));
                Check.True(practice?Task.TryBeginSyntheticPracticeForFixture(Admission,request,proof):Task.TryBeginSyntheticLegacyBlockForFixture(Admission,request,proof));Drain();
            }
            internal void Answer()
            {
                var trial=Task.CurrentTrial;
                // Cue-only frames expose no response array and open no trial.
                // Each must retire its staged authority before the next poll.
                for(int i=0;i<3;i++){Stage();Mutate(()=>Task.Tick());Drain();Check.True(!trial.Onset.HasValue);}
                Stage();ViveNamedSessionFrameAdmission.PresentationReservation token;
                Check.True(Pump.TryPreparePresentation(frame,frame,out token),"reserve: "+Pump.FirstFault);
                Check.True(Pump.CommitVisualSearchPresentation(token,frame,frame,trial.TrialId,trial.VisualSearch.LayoutId,Clock.Now));
                Stage();
                Mutate(()=>Task.RecordVisualSearchCueEvidence(trial.TrialId,0,VisualSearchTrial.TargetPreviewSeconds));
                Mutate(()=>Task.RecordVisualSearchPointer(trial.TrialId,null,"hover"));
                Mutate(()=>Task.RecordVisualSearchPointer(trial.TrialId,null,"click_miss"));
                Mutate(()=>Task.RecordVisualSearchPointer(trial.TrialId,trial.VisualSearch.Items.First(x=>x.Id!=trial.VisualSearch.TargetId).Id,"hover"));
                Mutate(()=>Task.RecordVisualSearchPointer(trial.TrialId,trial.VisualSearch.TargetId,"hover"));
                Mutate(()=>Task.SelectVisualSearchItem(trial.TrialId,trial.VisualSearch.TargetId));
                // The remainder of the same owner update must still be valid
                // after the response advances to a different trial or phase.
                Mutate(()=>Task.UpdateAuxiliary(false,false,false,0,true));Mutate(()=>Task.Tick());Drain();
                Check.True(!Admission.IoFence.IsRevoked&&Pump.FirstFault==null);
            }
            internal void Confirm()
            {
                Stage();Mutate(()=>Task.UpdateAuxiliary(false,false,false,0,true));
                Mutate(()=>Task.UpdateAuxiliary(true,false,false,0,true));Mutate(()=>Task.UpdateAuxiliary(false,false,false,0,true));Drain();
            }
            internal void Complete()
            {
                for(int i=0;i<8;i++)Answer();Check.Equal(Phase.BlockPreparation,Task.Phase);
                Stage();Mutate(()=>Task.OwnerLockSource(Actor.Owner,ControlSelection.Automatic,"search-frame-owner"));Drain();
                for(int block=0;block<4;block++)
                {
                    Begin(false);Check.Equal(Phase.Countdown,Task.Phase);Stage();
                    Check.True(Task.TryStartLegacyRawCountdownFromSoftwareWitness(frame,frame,Clock.Now));Drain();
                    Thread.Sleep((int)Math.Ceiling(Task.CountdownRemainingSeconds*1000)+20);
                    Stage();Mutate(()=>Task.Tick());Drain();Check.Equal(Phase.MainTask,Task.Phase);
                    for(int i=0;i<24;i++)Answer();Check.Equal(Phase.Questionnaire,Task.Phase);Confirm();
                    for(int i=0;i<Questionnaire.Count;i++)Confirm();
                    if(block<3){Check.Equal(Phase.Rest,Task.Phase);Confirm();Check.Equal(Phase.BlockReady,Task.Phase);}
                }
                Check.True(Task.Completed);Check.Equal(20,Task.RatingCount);
                var events=Task.ReadEventsAfter(-1,4096);
                Check.Equal(104,events.Count(x=>x.Kind=="stimulus_presented"));Check.Equal(104,events.Count(x=>x.Kind=="visual_search_cue"));
                Check.Equal(416,events.Count(x=>x.Kind=="visual_search_pointer"));Check.Equal(104,events.Count(x=>x.Kind=="first_response"));
                Check.True(!Admission.IoFence.IsRevoked&&Pump.FirstFault==null&&Pump.State==ViveNamedRawRecordingPump.Outcome.Running);
            }
            public void Dispose()
            {
                Pump.Stop();SpinWait.SpinUntil(()=>Pump.HasExited,3000);
                Worker.TryRequestStop();Source.Release.Set();Check.True(Worker.Stop(1000));Worker.Dispose();
            }
        }
        public static void Register(TestSuite suite)
        {
            suite.Run("visual search cue epoch left across a new worker poll reproduces TicketIdentity failure",()=>{
                using(var f=new Fixture())
                {
                    f.Stage();Check.True(!f.Task.CurrentTrial.Onset.HasValue);
                    Check.True(!f.TryStage());Check.Equal("raw_VerifyStagedEpoch:worker_TicketIdentity",f.Pump.FirstFault);
                    Check.True(f.Admission.IoFence.IsRevoked);
                }
            });
            suite.Run("visual search drained cue frames preserve raw pump across 104 rapid presentations and response mutations",()=>{
                using(var f=new Fixture())f.Complete();
            });
        }
    }
}
#endif
