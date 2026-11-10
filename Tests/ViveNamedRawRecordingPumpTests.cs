using System;
using System.Collections.Generic;
using System.Threading;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    public static class ViveNamedRawRecordingPumpTests
    {
        sealed class Client : IViveProEyeClient
        {
            readonly MonotonicClock clock;
            internal readonly ManualResetEvent Entered=new ManualResetEvent(false),Release=new ManualResetEvent(false);
            internal bool OneShot=false;internal int InvalidNext=0,PollCalls;
            internal Client(MonotonicClock clock){this.clock=clock;}
            public string Diagnostic {get{return "synthetic";}}
            public bool Start(){return true;}
            public ViveProEyePoll Poll()
            {
                Interlocked.Increment(ref PollCalls);Entered.Set();Release.WaitOne(3000);if(OneShot)Release.Reset();double now=clock.Now;
                var ray=new ViveEyeRay(2,false,true,new Vec3(0,0,0),new Vec3(.6,0,.8));
                return new ViveProEyePoll(new ViveHeadFrame(1,now,true,true,Rotation.Identity),
                    new ViveEyeFrame(1,8,now,Volatile.Read(ref InvalidNext)!=0,null,null,ray),ViveEyeRuntimeState.Working,ViveEyeCalibrationState.ReportedReady,true,0,2);
            }
            public void Dispose(){}
        }
        static ViveDevelopmentParticipant NewTask(MonotonicClock clock)
        {
            var option=ViveNamedOptionProvenance.Select(Actor.Owner,ViveNamedOption.Automatic,1,"pump-geometry");
            var plan=ViveNamedSimulationPlan.Select(Actor.Owner,"pump-plan",option);
            var intent=ViveNamedRecordingIntent.Select(Actor.Owner,"pump-intent",true,true);
            var config=SessionConfiguration.NamedRecordingDevelopment("pump-session",37,plan,intent);
            var geometry=new AutomaticGazeGeometry(Rotation.Identity,new Vec3(0,0,1),new Rotation(0,Math.Sqrt(.5),0,Math.Sqrt(.5)),new Vec3(1,0,0),"pump-geometry");
            var map=new Dictionary<ViveTrackpadDirection,Colour>{{ViveTrackpadDirection.Up,Colour.Red},{ViveTrackpadDirection.Right,Colour.Green},
                {ViveTrackpadDirection.Down,Colour.Blue},{ViveTrackpadDirection.Left,Colour.Yellow}};
            return new ViveDevelopmentParticipant(config,clock,geometry,map,.5,true);
        }
        public static void RegisterSceneInstallBoundaryTests(TestSuite suite)
        {
            suite.Run("scene install rejects an already withdrawn synthetic pump",()=>ExerciseInstallBoundary(true));
            suite.Run("technical install abort remains a fault, not withdrawal",()=>ExerciseInstallBoundary(false));
#if VIVE_SYNTHETIC_BATCH_FIXTURE
            suite.Run("admission construction fault after publication is not a withdrawal",()=>ExerciseAdmissionConstructionFailure(false));
            suite.Run("participant withdrawal during admission construction remains a withdrawal",()=>ExerciseAdmissionConstructionFailure(true));
#endif
        }
#if VIVE_SYNTHETIC_BATCH_FIXTURE
        static void ExerciseAdmissionConstructionFailure(bool withdraw)
        {
            var clock=new MonotonicClock("scene-construction-failure-test");var client=new Client(clock);
            var worker=new ViveProEyeWorker(clock,()=>new ViveHeadFrame(1,clock.Now,true,true,Rotation.Identity),()=>client);
            try
            {
                Check.True(worker.Start()&&client.Entered.WaitOne(3000));var task=NewTask(clock);
                task.BeforeNamedAdmissionWitnessForTest=()=>{
                    if(withdraw)task.NamedRawRevocation.RevokeForParticipant();
                    throw new InvalidOperationException("synthetic post-publication fault");};
                Check.Throws<InvalidOperationException>(()=>ViveNamedSessionFrameAdmission.CreateSyntheticForTest(task,worker,clock,32));
                var field=typeof(ViveDevelopmentParticipant).GetField("namedFrameAdmission",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic);
                var attached=(ViveNamedSessionFrameAdmission)field.GetValue(task);
                Check.True(attached!=null&&task.NamedRawRevocation.IsRevoked);
                Check.Equal(withdraw?2:3,attached.State);
                Check.Equal(withdraw,task.NamedRawRevocation.ParticipantWithdrawalRequested);
            }
            finally{client.Release.Set();worker.Stop(1000);worker.Dispose();}
        }
#endif
        static void ExerciseInstallBoundary(bool withdraw)
        {
            var clock=new MonotonicClock("scene-install-test");var client=new Client(clock);
            var worker=new ViveProEyeWorker(clock,()=>new ViveHeadFrame(1,clock.Now,true,true,Rotation.Identity),()=>client);
            ViveNamedRawRecordingPump pump=null;
            try
            {
                Check.True(worker.Start()&&client.Entered.WaitOne(3000));var task=NewTask(clock);
                var admission=ViveNamedSessionFrameAdmission.CreateSyntheticForTest(task,worker,clock,32);
                Check.True(admission.ConfirmTask()&&admission.ConfirmEye());
                var writer=new ViveNamedRawRecordingPump.SyntheticWriter(admission);
                pump=ViveNamedRawRecordingPump.StartSyntheticForTest(task,worker,clock,admission,writer);
                Check.True(ViveNamedRawRecordingPump.IsAvailableForSceneInstall(pump));
                if(withdraw)
                {
                    var thread=new Thread(()=>pump.Withdraw());thread.Start();Check.True(thread.Join(3000));
                    Check.True(!ViveNamedRawRecordingPump.IsAvailableForSceneInstall(pump)&&pump.WithdrawalRequested&&admission.State==2);
                }
                else
                {
                    admission.AbortForTechnicalFailure();
                    Check.True(!ViveNamedRawRecordingPump.IsAvailableForSceneInstall(pump)&&!pump.WithdrawalRequested&&admission.State==3);
                }
            }
            finally{if(pump!=null)pump.Stop();client.Release.Set();worker.Stop(1000);worker.Dispose();}
        }
#if VIVE_SYNTHETIC_BATCH_FIXTURE
        // This client and writer use only in-memory synthetic observations.
        sealed class SequencedFrameBoundaryClient : IViveProEyeClient
        {
            readonly Queue<ViveProEyePoll> values=new Queue<ViveProEyePoll>();
            internal readonly AutoResetEvent Waiting=new AutoResetEvent(false),Permit=new AutoResetEvent(false);
            public string Diagnostic {get{return "synthetic frame boundary";}}
            public bool Start(){return true;}
            public ViveProEyePoll Poll()
            {Waiting.Set();if(!Permit.WaitOne(10000))throw new Exception("synthetic poll timeout");lock(values)return values.Dequeue();}
            internal void Send(ViveProEyePoll value)
            {lock(values)values.Enqueue(value);Permit.Set();Check.True(Waiting.WaitOne(3000),"synthetic worker did not enter next poll");}
            internal void Release(){lock(values)values.Enqueue(null);Permit.Set();}
            public void Dispose(){}
        }
        static void ExerciseFrameBoundary(bool closeFirstFrame)
        {
            var clock=new MonotonicClock("frame-boundary-clock");var client=new SequencedFrameBoundaryClient();
            var worker=new ViveProEyeWorker(clock,()=>new ViveHeadFrame(1,clock.Now,true,true,Rotation.Identity),()=>client);
            ViveNamedRawRecordingPump pump=null;
            try
            {
                Check.True(worker.Start()&&client.Waiting.WaitOne(3000));var task=NewTask(clock);
                var admission=ViveNamedSessionFrameAdmission.CreateSyntheticForTest(task,worker,clock,256);
                Check.True(admission.ConfirmTask()&&admission.ConfirmEye());
                Check.True(admission.TryBindQueueBudget(ViveRawByteBudget.SelectExplicit(33554432)));
                var writer=new ViveNamedRawRecordingPump.SyntheticWriter(admission);
                pump=ViveNamedRawRecordingPump.StartSyntheticWithStatusForTest(task,worker,clock,admission,writer);
                Action<int> send=i=>{
                    var ray=new ViveEyeRay(2,false,true,new Vec3(0,0,0),new Vec3(.6,0,.8));
                    client.Send(new ViveProEyePoll(null,new ViveEyeFrame(i,i*8,clock.Now,false,null,null,ray),
                        ViveEyeRuntimeState.Working,ViveEyeCalibrationState.ReportedReady,true,0,2));};
                client.Send(null);send(1);
                Check.Equal(ViveNamedRawRecordingPump.Preparation.Ready,pump.PrepareStatus());
                send(2);ViveProEyePoll frame;Check.True(worker.TryPollForRecording(out frame));
                Check.True(pump.StageRigFrame(frame,1));
                Check.True(pump.TryRunReservedOrdinaryMutation(()=>task.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,false,0,0))));
                if(closeFirstFrame)Check.True(pump.DrainTaskEvents());
                send(3);Check.True(worker.TryPollForRecording(out frame));
                bool staged=pump.StageRigFrame(frame,1);
                if(!closeFirstFrame){Check.True(!staged);Check.True(admission.IoFence.IsRevoked);return;}
                Check.True(staged);bool ran=false;
                Check.True(pump.TryRunReservedOrdinaryMutation(()=>{ran=true;task.Tick();}));
                Check.True(ran&&pump.DrainTaskEvents()&&!admission.IoFence.IsRevoked);
            }
            finally{if(pump!=null)pump.Stop();worker.TryRequestStop();client.Release();worker.Stop(1000);worker.Dispose();}
        }
        public static void RegisterFrameBoundaryTests(TestSuite suite)
        {
            suite.Run("synthetic next frame rejects a retained prior epoch",()=>ExerciseFrameBoundary(false));
            suite.Run("synthetic finished frame accepts a fresh next epoch",()=>ExerciseFrameBoundary(true));
            suite.Run("participant choice poll observations enter bounded task FIFO before practice",()=>ExerciseSoftwareChoices());
        }
        static void ExerciseSoftwareChoices()
        {
            var clock=new MonotonicClock("choice-fifo-clock");double taskAt=clock.Now,eyeAt=clock.Now;
            var client=new SequencedFrameBoundaryClient();
            var worker=new ViveProEyeWorker(clock,()=>new ViveHeadFrame(1,clock.Now,true,true,Rotation.Identity),()=>client);
            ViveNamedRawRecordingPump pump=null;
            try
            {
                Check.True(worker.Start()&&client.Waiting.WaitOne(3000));var task=NewTask(clock);
                var admission=ViveNamedSessionFrameAdmission.CreateSyntheticForTest(task,worker,clock,256);
                Check.True(admission.ConfirmTask()&&admission.ConfirmEye());
                Check.True(admission.TryBindQueueBudget(ViveRawByteBudget.SelectExplicit(33554432)));
                var writer=new ViveNamedRawRecordingPump.SyntheticWriter(admission);
                pump=ViveNamedRawRecordingPump.StartSyntheticWithStatusForTest(task,worker,clock,admission,writer);
                Action<int> send=i=>{
                    var ray=new ViveEyeRay(2,false,true,new Vec3(0,0,0),new Vec3(.6,0,.8));
                    client.Send(new ViveProEyePoll(null,new ViveEyeFrame(i,i*8,clock.Now,false,null,null,ray),
                        ViveEyeRuntimeState.Working,ViveEyeCalibrationState.ReportedReady,true,0,2));};
                client.Send(null);send(1);Check.Equal(ViveNamedRawRecordingPump.Preparation.Ready,pump.PrepareStatus());
                send(2);ViveProEyePoll frame;Check.True(worker.TryPollForRecording(out frame));Check.True(pump.StageRigFrame(frame,1));
                Check.True(pump.TryRunReservedOrdinaryMutation(()=>task.RecordNamedSoftwareChoices(clock,taskAt,eyeAt)));
                Check.True(pump.DrainTaskEvents());
                var events=task.ReadEventsAfter(-1,4096);JournalEvent found=null;
                foreach(var item in events)if(item.Kind=="participant_recording_software_choices")found=item;
                Check.True(found!=null&&task.Phase==Phase.Preparation&&found.Fields["choice_clock_id"]==clock.ClockId);
                Check.Equal("controller_poll_not_hardware_event",found.Fields["choice_time_basis"]);
                Check.Equal("NOT_AUTHENTICATED",found.Fields["identity_or_study_consent"]);
                Check.Equal(taskAt.ToString("R",System.Globalization.CultureInfo.InvariantCulture),found.Fields["task_choice_poll_seconds"]);
                Check.Equal(eyeAt.ToString("R",System.Globalization.CultureInfo.InvariantCulture),found.Fields["eye_choice_poll_seconds"]);
            }
            finally{if(pump!=null)pump.Stop();worker.TryRequestStop();client.Release();worker.Stop(1000);worker.Dispose();}
        }
        // This constructs the target phase in memory. It verifies reservation
        // and callback ordering, not plan receipts or physical rendering.
        static void ExerciseCountdownWitnessBoundary(bool nested)
        {
            var clock=new MonotonicClock("countdown-boundary-clock");var client=new SequencedFrameBoundaryClient();
            var worker=new ViveProEyeWorker(clock,()=>new ViveHeadFrame(1,clock.Now,true,true,Rotation.Identity),()=>client);
            ViveNamedRawRecordingPump pump=null;
            try
            {
                Check.True(worker.Start()&&client.Waiting.WaitOne(3000));var task=NewTask(clock);
                var admission=ViveNamedSessionFrameAdmission.CreateSyntheticForTest(task,worker,clock,256);
                Check.True(admission.ConfirmTask()&&admission.ConfirmEye());
                Check.True(admission.TryBindQueueBudget(ViveRawByteBudget.SelectExplicit(33554432)));
                var writer=new ViveNamedRawRecordingPump.SyntheticWriter(admission);
                pump=ViveNamedRawRecordingPump.StartSyntheticWithStatusForTest(task,worker,clock,admission,writer);
                task.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,false,0,0));
                var engine=(Experiment)typeof(ViveDevelopmentParticipant).GetField("experiment",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance).GetValue(task);
                engine.PrepareNamedRecordingBlockPlan(Actor.Owner,true);engine.BeginFamiliarization(Actor.Owner);engine.BeginPractice(Actor.Owner);
                while(engine.Phase==Phase.Practice)
                {var trial=engine.CurrentTrial;engine.MarkPresented(trial.TrialId);Check.True(engine.ButtonChanged(trial.Stimulus.Ink,true));engine.ButtonChanged(trial.Stimulus.Ink,false);}
                engine.ConfirmNamedPlan(Actor.Owner,"synthetic-owner");engine.PrepareNamedRecordingBlockPlan(Actor.Owner,false);engine.BeginCountdown(Actor.Owner,true);
                Check.True(task.NamedRawCountdownAwaitingSoftwareWitness);
                Action<int> send=i=>{
                    var ray=new ViveEyeRay(2,false,true,new Vec3(0,0,0),new Vec3(.6,0,.8));
                    client.Send(new ViveProEyePoll(null,new ViveEyeFrame(i,i*8,clock.Now,false,null,null,ray),
                        ViveEyeRuntimeState.Working,ViveEyeCalibrationState.ReportedReady,true,0,2));};
                client.Send(null);send(1);Check.Equal(ViveNamedRawRecordingPump.Preparation.Ready,pump.PrepareStatus());
                send(2);ViveProEyePoll frame;Check.True(worker.TryPollForRecording(out frame));Check.True(pump.StageRigFrame(frame,1));
                if(nested)
                {
                    bool witnessed=true;
                    Check.True(!pump.TryRunReservedOrdinaryMutation(()=>witnessed=task.TryStartNamedRawCountdownFromSoftwareWitness(2,2,clock.Now)));
                    Check.True(!witnessed&&admission.IoFence.IsFaulted);return;
                }
                Check.True(pump.PrepareNonTrialPanelRender());
                Check.True(pump.VerifyStagedEpoch());
                Check.True(task.TryStartNamedRawCountdownFromSoftwareWitness(2,2,clock.Now));
                Check.True(pump.FinishNonTrialPanelRender());
                Check.True(!task.NamedRawCountdownAwaitingSoftwareWitness&&!admission.IoFence.IsRevoked);
            }
            finally{if(pump!=null)pump.Stop();worker.TryRequestStop();client.Release();worker.Stop(1000);worker.Dispose();}
        }
        public static void RegisterCountdownBoundaryTests(TestSuite suite)
        {
            suite.Run("countdown witness rejects a nested ordinary reservation",()=>ExerciseCountdownWitnessBoundary(true));
            suite.Run("countdown witness follows synthetic panel callback order",()=>ExerciseCountdownWitnessBoundary(false));
        }
        static void ExerciseOrdinaryEpochExpiry(bool duringMutation)
        {
            var clock=new MonotonicClock("ordinary-expiry-clock");var client=new Client(clock){OneShot=true};
            var worker=new ViveProEyeWorker(clock,()=>new ViveHeadFrame(1,clock.Now,true,true,Rotation.Identity),()=>client);
            ViveNamedRawRecordingPump pump=null;
            try
            {
                Check.True(worker.Start()&&client.Entered.WaitOne(3000));var task=NewTask(clock);
                var admission=ViveNamedSessionFrameAdmission.CreateSyntheticForTest(task,worker,clock,32);
                Check.True(admission.ConfirmTask()&&admission.ConfirmEye());
                Check.True(admission.TryBindQueueBudget(ViveRawByteBudget.SelectExplicit(33554432)));
                var writer=new ViveNamedRawRecordingPump.SyntheticWriter(admission);
                pump=ViveNamedRawRecordingPump.StartSyntheticWithStatusForTest(task,worker,clock,admission,writer);
                Check.Equal(ViveNamedRawRecordingPump.Preparation.WaitingBaseline,pump.PrepareStatus());
                client.Release.Set();Check.True(SpinWait.SpinUntil(()=>worker.CaptureDiagnostics.NativePolls>=1,3000));
                client.Release.Set();Check.True(SpinWait.SpinUntil(()=>worker.CaptureDiagnostics.NativePolls>=2,3000));
                Check.True(SpinWait.SpinUntil(()=>Volatile.Read(ref client.PollCalls)>=3,3000));
                Check.Equal(ViveNamedRawRecordingPump.Preparation.Ready,pump.PrepareStatus());
                ViveProEyePoll frame=null;Check.True(SpinWait.SpinUntil(()=>worker.TryPollForRecording(out frame),3000));
                Check.True(pump.StageRigFrame(frame,1));
                if(!duringMutation)Thread.Sleep(1100);
                bool ran=false;
                Check.True(!pump.TryRunReservedOrdinaryMutation(()=>{ran=true;task.Tick();if(duringMutation)Thread.Sleep(1100);}));
                Check.Equal(duringMutation,ran);Check.True(admission.IoFence.IsRevoked&&admission.IoFence.IsFaulted);
            }
            finally{if(pump!=null)pump.Stop();client.Release.Set();worker.Stop(1000);worker.Dispose();}
        }
        public static void RegisterOrdinaryExpiryTests(TestSuite suite)
        {
            RegisterFrameBoundaryTests(suite);
            RegisterCountdownBoundaryTests(suite);
            suite.Run("ordinary pump rejects epoch expiry before mutation",()=>ExerciseOrdinaryEpochExpiry(false));
            suite.Run("ordinary pump rejects epoch expiry during mutation",()=>ExerciseOrdinaryEpochExpiry(true));
        }
#endif
        static void ForceSyntheticCompleted(ViveDevelopmentParticipant task)
        {
            // Fixture-only mutation, not a completed study or recorded result.
            var field=typeof(ViveDevelopmentParticipant).GetField("experiment",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance);
            var exp=(Experiment)field.GetValue(task);
            typeof(Experiment).GetField("<Phase>k__BackingField",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance).SetValue(exp,Phase.Ended);
            typeof(Experiment).GetField("<EndReason>k__BackingField",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance).SetValue(exp,(EndReason?)EndReason.Completed);
        }
        static void ForceSyntheticPracticeWithoutReceipt(ViveDevelopmentParticipant task)
        {
            // Queue/seal mechanics only: this fixture does not validate the
            // product plan receipt, presentation or actual recording path.
            typeof(ViveDevelopmentParticipant).GetMethod("OwnerBeginPracticeCore",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)
                .Invoke(task,new object[]{Actor.Owner});
        }
        static void ExerciseSyntheticDrain(int mode)
        {
            var clock=new MonotonicClock("pump-normal-drain-"+mode);var client=new Client(clock){OneShot=true};
            var worker=new ViveProEyeWorker(clock,()=>new ViveHeadFrame(1,clock.Now,true,true,Rotation.Identity),()=>client);
            ViveNamedRawRecordingPump pump=null;ViveNamedRawRecordingPump.SyntheticWriter writer=null;
            try
            {
                Check.True(worker.Start()&&client.Entered.WaitOne(3000));var task=NewTask(clock);
                var admission=ViveNamedSessionFrameAdmission.CreateSyntheticForTest(task,worker,clock,32);
                Check.True(admission.ConfirmTask()&&admission.ConfirmEye());
                writer=new ViveNamedRawRecordingPump.SyntheticWriter(admission){CompleteResult=mode!=1,BlockComplete=mode==2,ThrowComplete=mode==4};
                pump=ViveNamedRawRecordingPump.StartSyntheticWithStatusForTest(task,worker,clock,admission,writer);
                client.Release.Set();Check.True(SpinWait.SpinUntil(()=>worker.CaptureDiagnostics.NativePolls>=1,3000));
                client.Release.Set();Check.True(SpinWait.SpinUntil(()=>worker.CaptureDiagnostics.NativePolls>=2,3000));
                // NativePolls counts published returns; it does not mean the
                // worker has reached its next Poll outside RecordingTicketGate.
                Check.True(SpinWait.SpinUntil(()=>Volatile.Read(ref client.PollCalls)>=3,3000));
                Check.Equal(ViveNamedRawRecordingPump.Preparation.Ready,pump.PrepareStatus());
                task.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,false,0,0));ForceSyntheticPracticeWithoutReceipt(task);
                ForceSyntheticCompleted(task);
                Check.True(pump.RequestCompletedDrain());
                // A sealed synthetic capture may not admit another frame or
                // drain new ordinary task input, even while completion waits.
                Check.True(!pump.StageRigFrame(null,1.0));
                Check.True(!pump.DrainTaskEvents());
                Check.True(writer.CompleteEntered.WaitOne(3000));
                if(mode==2)
                {
                    Check.True(pump.HasReservedIo);pump.Withdraw();
                    Check.Equal(ViveNamedRawRecordingPump.Outcome.IoUncertain,pump.State);
                    writer.CompleteRelease.Set();
                }
                Check.True(SpinWait.SpinUntil(()=>pump.HasExited,3000));
                Check.Equal(1,writer.CompleteCalls);
                if(mode==0||mode==3)
                {
                    // Synthetic callback only; not an OS close or durable file.
                    Check.Equal(ViveNamedRawRecordingPump.Outcome.CloseCallsReturned,pump.State);
                    Check.Equal(0,writer.CleanupCalls);
                    if(mode==3)
                    {
                        // The explicit flag takes precedence even when the
                        // completion snapshot cannot be retroactively changed.
                        pump.Withdraw();Check.True(pump.WithdrawalRequested);
                        Check.Equal(1,writer.CompleteCalls);
                    }
                }
                else
                {
                    Check.Equal(ViveNamedRawRecordingPump.Outcome.IoUncertain,pump.State);
                    Check.Equal(1,writer.CleanupCalls);
                }
            }
            finally{if(writer!=null)writer.CompleteRelease.Set();if(pump!=null)pump.Stop();client.Release.Set();worker.Stop(1000);worker.Dispose();}
        }
        static void ExerciseCompletionLinearization(int mode)
        {
            var clock=new MonotonicClock("pump-completion-race-"+mode);var client=new Client(clock){OneShot=true};
            var worker=new ViveProEyeWorker(clock,()=>new ViveHeadFrame(1,clock.Now,true,true,Rotation.Identity),()=>client);
            var entered=new ManualResetEvent(false);var release=new ManualResetEvent(false);
            ViveNamedRawRecordingPump pump=null;ViveNamedRawRecordingPump.SyntheticWriter writer=null;Thread stop=null;
            try
            {
                Check.True(worker.Start()&&client.Entered.WaitOne(3000));var task=NewTask(clock);
                var admission=ViveNamedSessionFrameAdmission.CreateSyntheticForTest(task,worker,clock,32);
                Check.True(admission.ConfirmTask()&&admission.ConfirmEye());
                writer=new ViveNamedRawRecordingPump.SyntheticWriter(admission){CompleteResult=true,BlockComplete=mode<2};
                pump=ViveNamedRawRecordingPump.StartSyntheticWithStatusForTest(task,worker,clock,admission,writer);
                client.Release.Set();Check.True(SpinWait.SpinUntil(()=>worker.CaptureDiagnostics.NativePolls>=1,3000));
                client.Release.Set();Check.True(SpinWait.SpinUntil(()=>worker.CaptureDiagnostics.NativePolls>=2,3000));
                Check.True(SpinWait.SpinUntil(()=>Volatile.Read(ref client.PollCalls)>=3,3000));
                Check.Equal(ViveNamedRawRecordingPump.Preparation.Ready,pump.PrepareStatus());
                task.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,false,0,0));ForceSyntheticPracticeWithoutReceipt(task);ForceSyntheticCompleted(task);
                Action barrier=()=>{entered.Set();if(!release.WaitOne(3000))throw new InvalidOperationException("Synthetic barrier timeout.");};
                if(mode<2)writer.AfterRevokeForTest=barrier;else writer.AfterCompletionClaimForTest=barrier;
                Check.True(pump.RequestCompletedDrain());Check.True(writer.CompleteEntered.WaitOne(3000));
                if(mode<2)
                {
                    stop=new Thread(()=>{if(mode==0)pump.Stop();else pump.Withdraw();});stop.Start();Check.True(entered.WaitOne(3000));
                    Check.Equal(ViveRawRevocation.Outcome.Revoked,admission.IoFence.CompletionOutcome);
                    Check.True(!pump.WithdrawalRequested); // Explicit flag follows fence revocation.
                    Check.True(!admission.TryPublishSyntheticForTest(123));
                    writer.CompleteRelease.Set();Check.True(SpinWait.SpinUntil(()=>pump.HasExited,3000));
                    Check.Equal(ViveNamedRawRecordingPump.Outcome.IoUncertain,pump.State);
                    release.Set();Check.True(stop.Join(3000));Check.Equal(mode==1,pump.WithdrawalRequested);
                }
                else
                {
                    Check.True(entered.WaitOne(3000));
                    Check.Equal(ViveRawRevocation.Outcome.CallsReturned,admission.IoFence.CompletionOutcome);
                    if(mode==2)pump.Withdraw();else
                    {task.ParticipantWithdraw();Check.True(task.NamedRecordingWithdrawalRequested);}
                    release.Set();Check.True(SpinWait.SpinUntil(()=>pump.HasExited,3000));
                    Check.Equal(ViveNamedRawRecordingPump.Outcome.IoUncertain,pump.State);
                    Check.Equal(mode==2,pump.WithdrawalRequested);
                }
                Check.Equal(1,writer.CompleteCalls);
            }
            finally
            {
                release.Set();if(writer!=null){writer.AfterRevokeForTest=null;writer.AfterCompletionClaimForTest=null;writer.CompleteRelease.Set();}
                if(stop!=null)stop.Join(3000);if(pump!=null)pump.Stop();client.Release.Set();worker.Stop(1000);worker.Dispose();
                entered.Dispose();release.Dispose();
            }
        }
        static void ExerciseUnexpectedExitRace(int mode)
        {
            var clock=new MonotonicClock("pump-unexpected-exit-"+mode);var client=new Client(clock);
            var worker=new ViveProEyeWorker(clock,()=>new ViveHeadFrame(1,clock.Now,true,true,Rotation.Identity),()=>client);
            var entered=new ManualResetEvent(false);var release=new ManualResetEvent(false);
            ViveNamedRawRecordingPump pump=null;ViveNamedRawRecordingPump.SyntheticWriter writer=null;
            try
            {
                Check.True(worker.Start()&&client.Entered.WaitOne(3000));var task=NewTask(clock);
                var admission=ViveNamedSessionFrameAdmission.CreateSyntheticForTest(task,worker,clock,16);
                Check.True(admission.ConfirmTask()&&admission.ConfirmEye());
                writer=new ViveNamedRawRecordingPump.SyntheticWriter(admission){ExitConsumerUnexpectedlyForTest=true};
                writer.AfterUnexpectedRevokeForTest=()=>{entered.Set();release.WaitOne(3000);};
                pump=ViveNamedRawRecordingPump.StartSyntheticForTest(task,worker,clock,admission,writer);
                Check.True(entered.WaitOne(3000));
                Check.True(admission.IoFence.IsRevoked&&!pump.WithdrawalRequested);
                Check.Equal(ViveNamedRawRecordingPump.Outcome.IoUncertain,pump.State);
                Check.True(!admission.TryPublishSyntheticForTest(123));
                if(mode==1)pump.Stop();else if(mode==2)pump.Withdraw();
                release.Set();Check.True(SpinWait.SpinUntil(()=>pump.HasExited,3000));
                Check.Equal(ViveNamedRawRecordingPump.Outcome.IoUncertain,pump.State);
                Check.Equal(mode==2,pump.WithdrawalRequested);
                Check.Equal(mode==0,admission.IoFence.IsFaulted);
                Check.Equal(0,writer.CompleteCalls);Check.Equal(1,writer.CleanupCalls);
            }
            finally
            {
                release.Set();if(writer!=null)writer.AfterUnexpectedRevokeForTest=null;
                if(pump!=null)pump.Stop();client.Release.Set();worker.Stop(1000);worker.Dispose();
                entered.Dispose();release.Dispose();
            }
        }
        static void ExerciseTrialReceipt(bool withdrawAfterClaim)
        {
            var clock=new MonotonicClock("synthetic-planned-receipt");var client=new Client(clock);
            var worker=new ViveProEyeWorker(clock,()=>new ViveHeadFrame(1,clock.Now,true,true,Rotation.Identity),()=>client);
            try
            {
                Check.True(worker.Start()&&client.Entered.WaitOne(3000));var task=NewTask(clock);
                var admission=ViveNamedSessionFrameAdmission.CreateSyntheticForTest(task,worker,clock,16);
                Check.True(admission.ConfirmTask()&&admission.ConfirmEye());
                task.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,false,0,0));
                Check.Throws<InvalidOperationException>(()=>task.OwnerBeginPractice(Actor.Owner));
                Check.Equal(Phase.Preparation,task.Phase);Check.Equal(0,task.TrialCount);
                var planned=task.PrepareNamedRecordingBlockPlan(Actor.Owner,true);
                ViveBlockPlanRequest request;Check.True(ViveBlockPlanRequest.TryCapture(task,clock,planned,out request));
                string unused=Fixtures.ClosedRawSinkPath();Fixtures.AssertClosedRawSinkPathAbsent(unused);
                var sink=new ViveRawSessionJournalSink(admission,unused);
                ViveRawSessionJournalSink.BlockPlanWriteReceipt receipt;
                Check.Equal(ViveRawSessionJournalSink.BlockPlanReceiptPoll.Pending,sink.TryGetSyntheticBlockPlanReceiptForTest(request,out receipt));
                Check.True(receipt==null&&!task.TryBeginNamedRecordingWithPlan(Actor.Owner,request,null,true));
                Check.Equal(Phase.Preparation,task.Phase);Check.Equal(0,task.TrialCount);
                Check.True(!sink.TryIssueSyntheticBlockPlanReceiptForTest(planned,0,2,2));
                Check.True(sink.TryIssueSyntheticBlockPlanReceiptForTest(planned,0,2,3));
                Check.True(!sink.TryIssueSyntheticBlockPlanReceiptForTest(planned,0,4,5));
                Check.Equal(ViveRawSessionJournalSink.BlockPlanReceiptPoll.Ready,sink.TryGetSyntheticBlockPlanReceiptForTest(request,out receipt));
                Check.Equal(Phase.Preparation,task.Phase);
                Check.True(!task.TryBeginNamedRecordingWithPlan(Actor.Owner,request,receipt,true));
                Check.Equal(Phase.Preparation,task.Phase);
                Check.True(!receipt.TryConsume(request));
                if(withdrawAfterClaim)receipt.AfterSyntheticClaimForTest=()=>task.NamedRawRevocation.Revoke();
                Check.Equal(!withdrawAfterClaim,receipt.TryConsumeSyntheticForTest(request));
                Check.True(!receipt.TryConsumeSyntheticForTest(request));
                Fixtures.AssertClosedRawSinkPathAbsent(unused);
            }
            finally{client.Release.Set();worker.Stop(1000);worker.Dispose();}
        }
        static void ExerciseTrialReceiptPublicationRace(bool after,bool fault)
        {
            var clock=new MonotonicClock("synthetic-planned-publication");var client=new Client(clock);
            var worker=new ViveProEyeWorker(clock,()=>new ViveHeadFrame(1,clock.Now,true,true,Rotation.Identity),()=>client);
            try
            {
                Check.True(worker.Start()&&client.Entered.WaitOne(3000));var task=NewTask(clock);
                var admission=ViveNamedSessionFrameAdmission.CreateSyntheticForTest(task,worker,clock,16);
                Check.True(admission.ConfirmTask()&&admission.ConfirmEye());
                var planned=task.PrepareNamedRecordingBlockPlan(Actor.Owner,true);
                ViveBlockPlanRequest request;Check.True(ViveBlockPlanRequest.TryCapture(task,clock,planned,out request));
                string unused=Fixtures.ClosedRawSinkPath();Fixtures.AssertClosedRawSinkPathAbsent(unused);
                var sink=new ViveRawSessionJournalSink(admission,unused);
                Action interleave=()=>{if(fault)throw new InvalidOperationException("synthetic publication fault");task.NamedRawRevocation.Revoke();};
                if(after)sink.AfterSyntheticReceiptPublishForTest=interleave;else sink.BeforeSyntheticReceiptPublishForTest=interleave;
                Check.True(!sink.TryIssueSyntheticBlockPlanReceiptForTest(planned,0,2,3));
                ViveRawSessionJournalSink.BlockPlanWriteReceipt receipt;
                Check.Equal(ViveRawSessionJournalSink.BlockPlanReceiptPoll.Faulted,sink.TryGetSyntheticBlockPlanReceiptForTest(request,out receipt));
                Check.True(receipt==null&&!sink.TryIssueSyntheticBlockPlanReceiptForTest(planned,0,4,5));
                Fixtures.AssertClosedRawSinkPathAbsent(unused);
            }
            finally{client.Release.Set();worker.Stop(1000);worker.Dispose();}
        }
        public static void Register(TestSuite suite)
        {
            suite.Run("rest continuation cannot auto-start a recorded block without plan receipt",()=>{
                foreach(bool attach in new[]{false,true})
                {
                    var clock=new MonotonicClock("synthetic-rest-barrier");var client=new Client(clock);
                    var worker=new ViveProEyeWorker(clock,()=>new ViveHeadFrame(1,clock.Now,true,true,Rotation.Identity),()=>client);
                    try
                    {
                        Check.True(worker.Start()&&client.Entered.WaitOne(3000));
                        var first=ViveNamedOptionProvenance.Select(Actor.Owner,ViveNamedOption.Automatic,0,"rest-geometry");
                        var second=ViveNamedOptionProvenance.Select(Actor.Owner,ViveNamedOption.Automatic,1,"rest-geometry");
                        var plan=ViveNamedSimulationPlan.Select(Actor.Owner,"rest-plan",first,second);
                        var intent=ViveNamedRecordingIntent.Select(Actor.Owner,"rest-intent",true,true);
                        var config=SessionConfiguration.NamedRecordingDevelopment("rest-session",37,plan,intent);
                        var geometry=new AutomaticGazeGeometry(Rotation.Identity,new Vec3(0,0,1),new Rotation(0,Math.Sqrt(.5),0,Math.Sqrt(.5)),new Vec3(1,0,0),"rest-geometry");
                        var map=new Dictionary<ViveTrackpadDirection,Colour>{{ViveTrackpadDirection.Up,Colour.Red},{ViveTrackpadDirection.Right,Colour.Green},{ViveTrackpadDirection.Down,Colour.Blue},{ViveTrackpadDirection.Left,Colour.Yellow}};
                        var task=new ViveDevelopmentParticipant(config,clock,geometry,map,.5,true);
                        if(attach){var admission=ViveNamedSessionFrameAdmission.CreateSyntheticForTest(task,worker,clock,16);Check.True(admission.ConfirmTask()&&admission.ConfirmEye());}
                        // Synthetic phase setup only, not a completed block or receipt.
                        var exp=(Experiment)typeof(ViveDevelopmentParticipant).GetField("experiment",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance).GetValue(task);
                        var flags=System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance;
                        typeof(Experiment).GetField("<Phase>k__BackingField",flags).SetValue(exp,Phase.Rest);
                        typeof(Experiment).GetField("<BlockIndex>k__BackingField",flags).SetValue(exp,0);
                        typeof(Experiment).GetField("namedOptionConfirmed",flags).SetValue(exp,true);
                        typeof(Experiment).GetField("<LockedControl>k__BackingField",flags).SetValue(exp,(ControlSelection?)ControlSelection.Automatic);
                        task.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,false,0,0));
                        task.UpdateAuxiliary(false,false,false,0,true);task.UpdateAuxiliary(true,false,false,0,true);
                        Check.Equal(Phase.BlockReady,task.Phase);task.Tick();
                        Check.Equal(attach?Phase.BlockReady:Phase.Countdown,task.Phase);
                    }
                    finally{client.Release.Set();worker.Stop(1000);worker.Dispose();}
                }
            });
            suite.Run("only prestart block plans require the writer flush barrier",()=>{
                var clock=new MonotonicClock("synthetic-plan-flush-policy");var task=NewTask(clock);
                var plan=task.PrepareNamedRecordingBlockPlan(Actor.Owner,true);
                Check.True(ViveRawSessionJournalSink.IsPrestartPlan(plan));
                task.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,false,0,0));ForceSyntheticPracticeWithoutReceipt(task);
                bool sawTrial=false;foreach(var item in task.ReadEventsAfter(plan.Sequence,64))
                {if(item.Kind=="trial_planned")sawTrial=true;Check.True(!ViveRawSessionJournalSink.IsPrestartPlan(item));}
                Check.True(sawTrial&&!ViveRawSessionJournalSink.IsPrestartPlan(null));
            });
            suite.Run("synthetic planned-event receipt is single-use and never product authority",()=>ExerciseTrialReceipt(false));
            suite.Run("synthetic planned-event receipt rejects concurrent withdrawal after claim",()=>ExerciseTrialReceipt(true));
            suite.Run("synthetic receipt publication rejects withdrawal before and after slot CAS",()=>{ExerciseTrialReceiptPublicationRace(false,false);ExerciseTrialReceiptPublicationRace(true,false);});
            suite.Run("synthetic receipt publication aborts on faults before and after slot CAS",()=>{ExerciseTrialReceiptPublicationRace(false,true);ExerciseTrialReceiptPublicationRace(true,true);});
            suite.Run("synthetic completed drain seals and invokes completion once",()=>ExerciseSyntheticDrain(0));
            suite.Run("synthetic completed drain failed completion is unknown and never retried",()=>ExerciseSyntheticDrain(1));
            suite.Run("synthetic withdrawal during completion is unknown and never retried",()=>ExerciseSyntheticDrain(2));
            suite.Run("synthetic withdrawal after completed drain stays explicit without a second close",()=>ExerciseSyntheticDrain(3));
            suite.Run("synthetic completion exception stays unknown and is not retried",()=>ExerciseSyntheticDrain(4));
            suite.Run("synthetic stop fence wins before writer completion claim",()=>ExerciseCompletionLinearization(0));
            suite.Run("synthetic withdrawal fence wins before flag and completion claim",()=>ExerciseCompletionLinearization(1));
            suite.Run("synthetic withdrawal after completion claim remains unknown",()=>ExerciseCompletionLinearization(2));
            suite.Run("synthetic participant withdrawal after completion claim remains unknown",()=>ExerciseCompletionLinearization(3));
            suite.Run("unexpected synthetic writer exit revokes before fault state",()=>ExerciseUnexpectedExitRace(0));
            suite.Run("concurrent synthetic stop preserves non-withdrawal on unexpected exit",()=>ExerciseUnexpectedExitRace(1));
            suite.Run("concurrent synthetic withdrawal stays explicit on unexpected exit",()=>ExerciseUnexpectedExitRace(2));
#if VIVE_SYNTHETIC_BATCH_FIXTURE
            RegisterOrdinaryExpiryTests(suite);
#endif
            suite.Run("pump status Busy fails closed without treating queue as empty",()=>{
                var clock=new MonotonicClock("pump-status-busy-clock");var client=new Client(clock){OneShot=true};
                var worker=new ViveProEyeWorker(clock,()=>new ViveHeadFrame(1,clock.Now,true,true,Rotation.Identity),()=>client);
                var held=new ManualResetEvent(false);var releaseGate=new ManualResetEvent(false);ViveNamedRawRecordingPump pump=null;
                try
                {
                    Check.True(worker.Start()&&client.Entered.WaitOne(3000));var task=NewTask(clock);
                    var admission=ViveNamedSessionFrameAdmission.CreateSyntheticForTest(task,worker,clock,16);
                    Check.True(admission.ConfirmTask()&&admission.ConfirmEye());
                    var writer=new ViveNamedRawRecordingPump.SyntheticWriter(admission);
                    pump=ViveNamedRawRecordingPump.StartSyntheticWithStatusForTest(task,worker,clock,admission,writer);
                    var holder=new Thread(()=>{lock(worker.RecordingTicketGate){held.Set();releaseGate.WaitOne(3000);}});
                    holder.Start();Check.True(held.WaitOne(3000));
                    Check.Equal(ViveNamedRawRecordingPump.Preparation.Faulted,pump.PrepareStatus());
                    Check.True(pump.State!=ViveNamedRawRecordingPump.Outcome.Running&&!pump.WithdrawalRequested);
                    releaseGate.Set();Check.True(holder.Join(3000));
                }
                finally{releaseGate.Set();if(pump!=null)pump.Stop();client.Release.Set();worker.Stop(1000);worker.Dispose();}
            });
            suite.Run("pump records invalid baseline only as admission then fail-stops",()=>{
                var clock=new MonotonicClock("pump-invalid-baseline-clock");var client=new Client(clock){OneShot=true,InvalidNext=1};
                var worker=new ViveProEyeWorker(clock,()=>new ViveHeadFrame(1,clock.Now,true,true,Rotation.Identity),()=>client);
                ViveNamedRawRecordingPump pump=null;
                try
                {
                    Check.True(worker.Start()&&client.Entered.WaitOne(3000));var task=NewTask(clock);
                    var admission=ViveNamedSessionFrameAdmission.CreateSyntheticForTest(task,worker,clock,16);
                    Check.True(admission.ConfirmTask()&&admission.ConfirmEye());
                    var writer=new ViveNamedRawRecordingPump.SyntheticWriter(admission);
                    pump=ViveNamedRawRecordingPump.StartSyntheticWithStatusForTest(task,worker,clock,admission,writer);
                    client.Release.Set();Check.True(SpinWait.SpinUntil(()=>worker.CaptureDiagnostics.NativePolls>=1,3000));
                    client.Release.Set();Check.True(SpinWait.SpinUntil(()=>worker.CaptureDiagnostics.NativePolls>=2,3000));
                    Check.True(SpinWait.SpinUntil(()=>Volatile.Read(ref client.PollCalls)>=3,3000));
                    Check.Equal(ViveNamedRawRecordingPump.Preparation.Faulted,pump.PrepareStatus());
                    Check.True(pump.State!=ViveNamedRawRecordingPump.Outcome.Running);
                    Check.True(!pump.WithdrawalRequested);
                    // FIFO admission/ACK does not prove the invalid status was
                    // written before the fail-stop detached its queue.
                }
                finally{if(pump!=null)pump.Stop();client.Release.Set();worker.Stop(1000);worker.Dispose();}
            });
            suite.Run("pump admits status baseline before frame and detects later invalidation",()=>{
                var clock=new MonotonicClock("pump-status-epoch-clock");var client=new Client(clock){OneShot=true};
                var worker=new ViveProEyeWorker(clock,()=>new ViveHeadFrame(1,clock.Now,true,true,Rotation.Identity),()=>client);
                ViveNamedRawRecordingPump pump=null;
                try
                {
                    Check.True(worker.Start()&&client.Entered.WaitOne(3000));var task=NewTask(clock);
                    var admission=ViveNamedSessionFrameAdmission.CreateSyntheticForTest(task,worker,clock,32);
                    Check.True(admission.ConfirmTask()&&admission.ConfirmEye());
                    var writer=new ViveNamedRawRecordingPump.SyntheticWriter(admission);
                    pump=ViveNamedRawRecordingPump.StartSyntheticWithStatusForTest(task,worker,clock,admission,writer);
                    Check.Equal(ViveNamedRawRecordingPump.Preparation.WaitingBaseline,pump.PrepareStatus());
                    // The first native call began before enable and must not
                    // count as an authorized status baseline.
                    client.Release.Set();Check.True(SpinWait.SpinUntil(()=>worker.CaptureDiagnostics.NativePolls>=1,3000));
                    client.Release.Set();Check.True(SpinWait.SpinUntil(()=>worker.CaptureDiagnostics.NativePolls>=2,3000));
                    Check.True(SpinWait.SpinUntil(()=>Volatile.Read(ref client.PollCalls)>=3,3000));
                    Check.Equal(ViveNamedRawRecordingPump.Preparation.Ready,pump.PrepareStatus());
                    ViveProEyePoll frame=null;Check.True(SpinWait.SpinUntil(()=>worker.TryPollForRecording(out frame),3000));
                    Check.True(pump.StageRigFrame(frame,1)&&pump.VerifyStagedEpoch());
                    Volatile.Write(ref client.InvalidNext,1);client.Release.Set();
                    Check.True(SpinWait.SpinUntil(()=>worker.CaptureDiagnostics.NativePolls>=3,3000));
                    // A status change after the recorded frame makes the task
                    // interval unknown, not continuously valid.
                    Check.True(!pump.DrainTaskEvents());
                    Check.True(pump.State!=ViveNamedRawRecordingPump.Outcome.Running);
                    Check.True(!pump.WithdrawalRequested);
                    Check.True(pump.TryStopStatusOnOwner());
                }
                finally{if(pump!=null)pump.Stop();client.Release.Set();worker.Stop(1000);worker.Dispose();}
            });
            suite.Run("status FIFO requires bound enqueue proof before drain seal",()=>{
                var clock=new MonotonicClock("pump-status-proof-clock");var client=new Client(clock){OneShot=true};
                var worker=new ViveProEyeWorker(clock,()=>new ViveHeadFrame(1,clock.Now,true,true,Rotation.Identity),()=>client);
                try
                {
                    Check.True(worker.Start()&&client.Entered.WaitOne(3000));var task=NewTask(clock);
                    var admission=ViveNamedSessionFrameAdmission.CreateSyntheticForTest(task,worker,clock,16);
                    Check.True(admission.ConfirmTask()&&admission.ConfirmEye());object run;
                    Check.True(admission.TryEnableWorkerStatus(8,out run));client.Release.Set();
                    Check.True(SpinWait.SpinUntil(()=>worker.CaptureDiagnostics.NativePolls>=1,3000));client.Release.Set();
                    Check.True(SpinWait.SpinUntil(()=>worker.CaptureDiagnostics.NativePolls>=2,3000));
                    ViveProEyeWorker.StatusTicket ticket=null;
                    Check.True(SpinWait.SpinUntil(()=>worker.TryReserveStatusEvidence(run,out ticket)==ViveProEyeWorker.StatusDisposition.Event,3000));
                    ViveNamedSessionFrameAdmission.StatusAdmissionProof proof;
                    Check.True(admission.TryStageStatus(ticket,out proof)&&proof!=null);
                    var held=new ManualResetEvent(false);var releaseGate=new ManualResetEvent(false);
                    var holder=new Thread(()=>{lock(worker.RecordingTicketGate){held.Set();releaseGate.WaitOne(3000);}});
                    holder.Start();Check.True(held.WaitOne(3000));
                    Check.Equal(ViveProEyeWorker.StatusAckResult.Busy,worker.TryAcknowledgeStatusEvidenceResult(ticket,proof));
                    releaseGate.Set();Check.True(holder.Join(3000));
                    Check.Equal(ViveProEyeWorker.StatusAckResult.Acknowledged,worker.TryAcknowledgeStatusEvidenceResult(ticket,proof));
                    Check.Equal(ViveProEyeWorker.StatusAckResult.Invalid,worker.TryAcknowledgeStatusEvidenceResult(ticket,proof));
                    ViveProEyeWorker.StatusSeal seal=null;
                    Check.True(SpinWait.SpinUntil(()=>worker.TrySealStatusEvidence(run,out seal),3000));
                    Check.True(admission.TryBindDrainedStatusSeal(seal));
                    Check.Equal(ViveNamedSessionFrameAdmission.SealResult.Pending,admission.TrySealCompleted());
                    Check.True(!admission.Sealed);
                    task.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,false,0,0));ForceSyntheticPracticeWithoutReceipt(task);
                    // Synthetic reflection tests only the seal mechanics; it
                    // does not represent an actual completed experiment.
                    var expField=typeof(ViveDevelopmentParticipant).GetField("experiment",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance);
                    var exp=(Experiment)expField.GetValue(task);
                    typeof(Experiment).GetField("<Phase>k__BackingField",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance).SetValue(exp,Phase.Ended);
                    typeof(Experiment).GetField("<EndReason>k__BackingField",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance).SetValue(exp,(EndReason?)EndReason.Completed);
                    Check.Equal(ViveNamedSessionFrameAdmission.SealResult.Pending,admission.TrySealCompleted());
                    bool staged;do{Check.True(admission.TryStageNextTaskEvent(out staged));}while(staged);
                    Check.Equal(ViveNamedSessionFrameAdmission.SealResult.Sealed,admission.TrySealCompleted());
                    long water,cursor;double sealedAt;
                    Check.True(!admission.TryGetDrainedSealBoundary(out water,out cursor,out sealedAt));
                    ViveNamedSessionFrameAdmission.ConsumerReceipt receipt;
                    Check.True(admission.TryReserveConsumer(out receipt)&&receipt!=null&&receipt.Record is ViveProEyeWorker.ObservedEyeStatus);
                    Check.True(admission.TryAcknowledgeSyntheticForTest(receipt));
                    do
                    {
                        Check.True(admission.TryReserveConsumer(out receipt));
                        if(receipt!=null)Check.True(admission.TryAcknowledgeSyntheticForTest(receipt));
                    }while(receipt!=null);
                    Check.True(admission.TryGetDrainedSealBoundary(out water,out cursor,out sealedAt));
                    Check.True(water>0&&cursor>=0&&sealedAt>=0&&!admission.TryPublishSyntheticForTest(99));
                }
                finally{client.Release.Set();worker.Stop(1000);worker.Dispose();}
            });
            suite.Run("SPSC consumer defers an unpublished producer tail without fault",()=>{
                var clock=new MonotonicClock("pump-interleave-clock");var client=new Client(clock);
                var worker=new ViveProEyeWorker(clock,()=>new ViveHeadFrame(1,clock.Now,true,true,Rotation.Identity),()=>client);
                var tailReady=new ManualResetEvent(false);var busy=new ManualResetEvent(false);var committed=new ManualResetEvent(false);Exception failure=null;
                try
                {
                    Check.True(worker.Start()&&client.Entered.WaitOne(3000));var task=NewTask(clock);
                    var admission=ViveNamedSessionFrameAdmission.CreateSyntheticForTest(task,worker,clock,2);
                    Check.True(admission.ConfirmTask()&&admission.ConfirmEye());
                    admission.AfterTailPublishedForTest=()=>{tailReady.Set();Check.True(busy.WaitOne(3000));};
                    var consumer=new Thread(()=>{
                        try
                        {
                            Check.True(tailReady.WaitOne(3000));ViveNamedSessionFrameAdmission.ConsumerReceipt receipt;
                            Check.True(admission.TryReserveConsumer(out receipt)&&receipt==null);
                            busy.Set();Check.True(committed.WaitOne(3000));
                            Check.True(admission.TryReserveConsumer(out receipt)&&receipt!=null);
                            Check.Equal(0L,receipt.Ordinal);Check.Equal(42,(int)receipt.Record);
                            Check.True(admission.TryAcknowledgeSyntheticForTest(receipt));
                        }
                        catch(Exception error){failure=error;busy.Set();}
                    });
                    consumer.Start();Check.True(admission.TryPublishSyntheticForTest(42));committed.Set();
                    Check.True(consumer.Join(3000));if(failure!=null)throw failure;
                    Check.Equal(0,admission.Count);Check.Equal(0,admission.State);
                }
                finally{tailReady.Set();busy.Set();committed.Set();client.Release.Set();worker.Stop(1000);worker.Dispose();}
            });
            suite.Run("SPSC FIFO survives concurrent producer consumer wraparound",()=>{
                var clock=new MonotonicClock("pump-stress-clock");var client=new Client(clock);
                var worker=new ViveProEyeWorker(clock,()=>new ViveHeadFrame(1,clock.Now,true,true,Rotation.Identity),()=>client);
                Exception failure=null;const int total=12000;
                try
                {
                    Check.True(worker.Start()&&client.Entered.WaitOne(3000));var task=NewTask(clock);
                    var admission=ViveNamedSessionFrameAdmission.CreateSyntheticForTest(task,worker,clock,32);
                    Check.True(admission.ConfirmTask()&&admission.ConfirmEye());
                    var consumer=new Thread(()=>{
                        try
                        {
                            int expected=0;var deadline=DateTime.UtcNow.AddSeconds(20);
                            while(expected<total&&DateTime.UtcNow<deadline)
                            {
                                ViveNamedSessionFrameAdmission.ConsumerReceipt receipt;
                                Check.True(admission.TryReserveConsumer(out receipt));
                                if(receipt==null){Thread.Yield();continue;}
                                Check.Equal((long)expected,receipt.Ordinal);Check.Equal(expected,(int)receipt.Record);
                                Check.True(admission.TryAcknowledgeSyntheticForTest(receipt));expected++;
                            }
                            Check.Equal(total,expected);
                        }
                        catch(Exception error){failure=error;admission.Withdraw();}
                    });
                    consumer.IsBackground=true;consumer.Start();var limit=DateTime.UtcNow.AddSeconds(20);
                    for(int value=0;value<total;value++)
                    {
                        // Test harness pacing avoids intentionally filling the
                        // bounded queue; production overflow still fail-stops.
                        while((admission.Count<0||admission.Count>=32)&&DateTime.UtcNow<limit)Thread.Yield();
                        Check.True(DateTime.UtcNow<limit);Check.True(admission.TryPublishSyntheticForTest(value));
                    }
                    Check.True(consumer.Join(21000));if(failure!=null)throw failure;
                    Check.Equal(0,admission.Count);Check.Equal(0,admission.State);
                }
                finally{client.Release.Set();worker.Stop(1000);worker.Dispose();}
            });
            suite.Run("SPSC withdrawal before producer commit detaches unpublished payload",()=>{
                var clock=new MonotonicClock("pump-withdraw-publish-clock");var client=new Client(clock);
                var worker=new ViveProEyeWorker(clock,()=>new ViveHeadFrame(1,clock.Now,true,true,Rotation.Identity),()=>client);
                var withdrawn=new ManualResetEvent(false);
                try
                {
                    Check.True(worker.Start()&&client.Entered.WaitOne(3000));var task=NewTask(clock);
                    var admission=ViveNamedSessionFrameAdmission.CreateSyntheticForTest(task,worker,clock,2);
                    Check.True(admission.ConfirmTask()&&admission.ConfirmEye());
                    var ringField=typeof(ViveNamedSessionFrameAdmission).GetField("ring",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance);
                    var oldRing=(object[])ringField.GetValue(admission);
                    var helper=new Thread(()=>{admission.Withdraw();withdrawn.Set();});
                    // Fire withdrawal only after the producer has published tail.
                    admission.AfterTailPublishedForTest=()=>{helper.Start();Check.True(withdrawn.WaitOne(3000));};
                    Check.True(!admission.TryPublishSyntheticForTest(123));Check.True(helper.Join(3000));
                    Check.Equal(2,admission.State);Check.Equal(0,admission.Count);Check.True(oldRing[0]==null);
                    ViveNamedSessionFrameAdmission.ConsumerReceipt receipt;Check.True(!admission.TryReserveConsumer(out receipt)&&receipt==null);
                }
                finally{withdrawn.Set();client.Release.Set();worker.Stop(1000);worker.Dispose();}
            });
            suite.Run("named raw pump stays closed before installed target authorization",()=>{
                var clock=new MonotonicClock("pump-gate-clock");var client=new Client(clock);
                var worker=new ViveProEyeWorker(clock,()=>new ViveHeadFrame(1,clock.Now,true,true,Rotation.Identity),()=>client);
                try
                {
                    Check.True(worker.Start()&&client.Entered.WaitOne(3000));var task=NewTask(clock);
                    var admission=ViveNamedSessionFrameAdmission.CreateSyntheticForTest(task,worker,clock,16);
                    string unused=Fixtures.ClosedRawSinkPath();Fixtures.AssertClosedRawSinkPathAbsent(unused);
                    var sink=new ViveRawSessionJournalSink(admission,unused);
                    ViveNamedRawRecordingPump pump;
                    Check.True(sink.BelongsTo(admission)&&!sink.HasNativeConfiguration&&!sink.ProductionGateOpen&&!sink.CompletionGateOpen);
                    Check.True(!sink.CompleteSealed()&&sink.CompletionState==0&&!sink.CleanupAfterTerminal());
                    Check.Throws<ArgumentNullException>(()=>new ViveRawSessionJournalSink(admission,null,@"C:\synthetic-directory",ViveRawByteBudget.SelectExplicit(4096)));
                    var bytes=new byte[]{1,2,3};
                    var unissued=new ViveRawSessionJournalSink.NativeAuthority(sink,ViveRawSessionJournalSink.NativeStep.Data,null,new object(),null,null,bytes,-1,-1,-1);
                    Check.True(!unissued.TryConsume(ViveRawSessionJournalSink.NativeStep.Data,null,null,bytes));
                    Check.True(!unissued.TryConsume(ViveRawSessionJournalSink.NativeStep.Data,null,null,bytes));
                    Check.True(!ViveNamedRawRecordingPump.TryStartInstalled(task,worker,clock,admission,sink,out pump)&&pump==null);
                    var otherTask=NewTask(clock);
                    var otherAdmission=ViveNamedSessionFrameAdmission.CreateSyntheticForTest(otherTask,worker,clock,16);
                    var otherWriter=new ViveNamedRawRecordingPump.SyntheticWriter(otherAdmission);
                    Check.Throws<InvalidOperationException>(()=>ViveNamedRawRecordingPump.StartSyntheticForTest(task,worker,clock,admission,otherWriter));
                    var rightWriter=new ViveNamedRawRecordingPump.SyntheticWriter(admission);
                    Check.Throws<InvalidOperationException>(()=>ViveNamedRawRecordingPump.StartSyntheticForTest(otherTask,worker,clock,admission,rightWriter));
                    Check.True(admission.State==0);
                    Fixtures.AssertClosedRawSinkPathAbsent(unused);
                }
                finally{client.Release.Set();worker.Stop(1000);worker.Dispose();}
            });
            suite.Run("named raw pump withdrawal during reserved append is prompt and uncertain",()=>{
                var clock=new MonotonicClock("pump-withdraw-clock");var client=new Client(clock);
                var worker=new ViveProEyeWorker(clock,()=>new ViveHeadFrame(1,clock.Now,true,true,Rotation.Identity),()=>client);
                ViveNamedRawRecordingPump.SyntheticWriter writer=null;
                try
                {
                    Check.True(worker.Start()&&client.Entered.WaitOne(3000));var task=NewTask(clock);
                    var admission=ViveNamedSessionFrameAdmission.CreateSyntheticForTest(task,worker,clock,32);
                    Check.True(admission.ConfirmTask()&&admission.ConfirmEye());
                    task.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,false,0,0));ForceSyntheticPracticeWithoutReceipt(task);
                    bool staged;Check.True(admission.TryStageNextTaskEvent(out staged)&&staged);
                    writer=new ViveNamedRawRecordingPump.SyntheticWriter(admission){Block=true};
                    var pump=ViveNamedRawRecordingPump.StartSyntheticForTest(task,worker,clock,admission,writer);
                    Check.True(writer.Entered.WaitOne(3000)&&pump.HasReservedIo);
                    client.Release.Set();Check.True(SpinWait.SpinUntil(()=>worker.CaptureDiagnostics.SuccessfulEyeReads>0,3000));
                    Check.True(pump.StageRigFrame(worker.Poll(),1));
                    // Post-task drain must not reissue the consumed frame.
                    Check.True(pump.DrainTaskEvents());
                    var before=DateTime.UtcNow;pump.Withdraw();
                    Check.True((DateTime.UtcNow-before).TotalMilliseconds<500&&pump.WithdrawalRequested);
                    Check.Equal(ViveNamedRawRecordingPump.Outcome.IoUncertain,pump.State);
                    Check.True(!pump.HasExited&&admission.State==2&&admission.Count==0);
                    writer.Release.Set();Check.True(SpinWait.SpinUntil(()=>pump.HasExited,3000));
                    Check.Equal(ViveNamedRawRecordingPump.Outcome.IoUncertain,pump.State);
                    Check.Equal(1,writer.Calls);Check.Equal(1,writer.CleanupCalls);
                    Check.True(!pump.StageRigFrame(null,1));
                }
                finally{if(writer!=null)writer.Release.Set();client.Release.Set();worker.Stop(1000);worker.Dispose();}
            });
            suite.Run("named raw stop remains revoked if subsequent presentation fails",()=>{
                var clock=new MonotonicClock("pump-stop-before-view-clock");var client=new Client(clock);
                var worker=new ViveProEyeWorker(clock,()=>new ViveHeadFrame(1,clock.Now,true,true,Rotation.Identity),()=>client);
                ViveNamedRawRecordingPump.SyntheticWriter writer=null;
                try
                {
                    Check.True(worker.Start()&&client.Entered.WaitOne(3000));var task=NewTask(clock);
                    var admission=ViveNamedSessionFrameAdmission.CreateSyntheticForTest(task,worker,clock,16);
                    Check.True(admission.ConfirmTask()&&admission.ConfirmEye());
                    task.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,false,0,0));ForceSyntheticPracticeWithoutReceipt(task);
                    bool staged;Check.True(admission.TryStageNextTaskEvent(out staged)&&staged);
                    writer=new ViveNamedRawRecordingPump.SyntheticWriter(admission){Block=true};
                    var pump=ViveNamedRawRecordingPump.StartSyntheticForTest(task,worker,clock,admission,writer);
                    Check.True(writer.Entered.WaitOne(3000)&&pump.HasReservedIo);
                    try{pump.Stop();throw new InvalidOperationException("Synthetic view failure after stop.");}
                    catch(InvalidOperationException){}
                    Check.True(admission.IoFence.IsRevoked&&!pump.WithdrawalRequested);
                    Check.Equal(ViveNamedRawRecordingPump.Outcome.IoUncertain,pump.State);
                    Check.True(!pump.StageRigFrame(null,1));
                    pump.Withdraw();Check.True(pump.WithdrawalRequested);
                    writer.Release.Set();Check.True(SpinWait.SpinUntil(()=>pump.HasExited,3000));
                    Check.Equal(ViveNamedRawRecordingPump.Outcome.IoUncertain,pump.State);
                    Check.Equal(1,writer.Calls);Check.Equal(1,writer.CleanupCalls);
                }
                finally{if(writer!=null)writer.Release.Set();client.Release.Set();worker.Stop(1000);worker.Dispose();}
            });
            suite.Run("named raw pump idle stop is asynchronous and not a clean close",()=>{
                var clock=new MonotonicClock("pump-stop-clock");var client=new Client(clock);
                var worker=new ViveProEyeWorker(clock,()=>new ViveHeadFrame(1,clock.Now,true,true,Rotation.Identity),()=>client);
                try
                {
                    Check.True(worker.Start()&&client.Entered.WaitOne(3000));var task=NewTask(clock);
                    var admission=ViveNamedSessionFrameAdmission.CreateSyntheticForTest(task,worker,clock,16);
                    Check.True(admission.ConfirmTask()&&admission.ConfirmEye());
                    var writer=new ViveNamedRawRecordingPump.SyntheticWriter(admission);
                    var pump=ViveNamedRawRecordingPump.StartSyntheticForTest(task,worker,clock,admission,writer);
                    pump.Stop();Check.True(SpinWait.SpinUntil(()=>pump.HasExited,3000));
                    Check.Equal(ViveNamedRawRecordingPump.Outcome.StoppedWithoutClose,pump.State);
                    Check.Equal(0,writer.Calls);Check.Equal(1,writer.CleanupCalls);
                    Check.True(!pump.StageRigFrame(null,1)&&!pump.WithdrawalRequested);
                    pump.Withdraw();Check.True(pump.WithdrawalRequested);
                    Check.Equal(ViveNamedRawRecordingPump.Outcome.WithdrawnWithoutClose,pump.State);
                }
                finally{client.Release.Set();worker.Stop(1000);worker.Dispose();}
            });
            suite.Run("named raw pump fault does not invent withdrawal and later withdrawal is retained",()=>{
                var clock=new MonotonicClock("pump-fault-clock");var client=new Client(clock);
                var worker=new ViveProEyeWorker(clock,()=>new ViveHeadFrame(1,clock.Now,true,true,Rotation.Identity),()=>client);
                try
                {
                    Check.True(worker.Start()&&client.Entered.WaitOne(3000));var task=NewTask(clock);
                    var admission=ViveNamedSessionFrameAdmission.CreateSyntheticForTest(task,worker,clock,16);
                    Check.True(admission.ConfirmTask()&&admission.ConfirmEye());
                    var writer=new ViveNamedRawRecordingPump.SyntheticWriter(admission);
                    var pump=ViveNamedRawRecordingPump.StartSyntheticForTest(task,worker,clock,admission,writer);
                    Check.True(!pump.StageRigFrame(null,1));Check.True(!pump.WithdrawalRequested);
                    Check.True(admission.IoFence.IsRevoked&&admission.IoFence.IsFaulted);
                    pump.Withdraw();Check.True(pump.WithdrawalRequested);
                    Check.True(SpinWait.SpinUntil(()=>pump.HasExited,3000));
                    Check.True(pump.State==ViveNamedRawRecordingPump.Outcome.Faulted||pump.State==ViveNamedRawRecordingPump.Outcome.IoUncertain);
                }
                finally{client.Release.Set();worker.Stop(1000);worker.Dispose();}
            });
        }
    }
}
