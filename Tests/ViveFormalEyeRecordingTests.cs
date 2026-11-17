using System;
using System.Collections.Generic;
using System.Threading;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    public static class ViveFormalEyeRecordingTests
    {
        sealed class Client : IViveProEyeClient
        {
            readonly Queue<ViveProEyePoll> pending=new Queue<ViveProEyePoll>();
            internal readonly AutoResetEvent Waiting=new AutoResetEvent(false),Permit=new AutoResetEvent(false);
            public string Diagnostic {get{return "synthetic formal eye boundary";}}
            public bool Start(){return true;}
            public ViveProEyePoll Poll(){Waiting.Set();Check.True(Permit.WaitOne(3000));lock(pending)return pending.Dequeue();}
            internal void Send(ViveProEyePoll value,bool wait=true)
            {lock(pending)pending.Enqueue(value);Permit.Set();if(wait)Check.True(Waiting.WaitOne(3000));}
            public void Dispose(){}
        }
        static ViveProEyePoll Eye(double now,int frame,bool healthy=true)
        {
            var ray=new ViveEyeRay(healthy?3UL:0UL,healthy,healthy,new Vec3(0,0,0),new Vec3(0,0,1));
            return new ViveProEyePoll(null,new ViveEyeFrame(frame,frame*8,now,false,ray,ray,ray),ViveEyeRuntimeState.Working,
                healthy?ViveEyeCalibrationState.ReportedReady:ViveEyeCalibrationState.Required,true,0,2);
        }
        static ViveProEyeWorker.RecordingFrameTicket Ticket(ViveProEyeWorker worker,MonotonicClock clock)
        {
            ViveProEyePoll poll;Check.True(worker.TryPollTaskForRecording(out poll));
            ViveProEyeWorker.RecordingFrameTicket ticket;
            Check.Equal(ViveProEyeWorker.RecordingTicketDisposition.Issued,worker.TryIssueIndependentTaskTicket(poll,clock.Now,1,false,out ticket));
            return ticket;
        }
        static void Ack(ViveProEyeWorker worker,object run)
        {
            ViveProEyeWorker.StatusTicket ticket;
            while(worker.TryReserveStatusEvidence(run,out ticket)==ViveProEyeWorker.StatusDisposition.Event)
                Check.True(worker.TryAcknowledgeStatusEvidence(ticket));
        }
#if VIVE_SYNTHETIC_BATCH_FIXTURE
        sealed class PumpFixture : IDisposable
        {
            internal readonly MonotonicClock Clock=new MonotonicClock("formal-pump-training");
            internal readonly Client Native=new Client();
            internal readonly ViveProEyeWorker Worker;
            internal readonly ViveDevelopmentParticipant Task;
            internal readonly ViveNamedSessionFrameAdmission Admission;
            internal readonly ViveNamedRawRecordingPump Pump;
            internal readonly ViveNamedRawRecordingPump.SyntheticWriter Writer;
            internal int Factories;long head;int renderFrame;bool nativeStarted;
            internal PumpFixture()
            {
                var geometry=DevelopmentFixtures.Geometry();
                var allocation=new Allocation("formal-allocation",new[]{Condition.B,Condition.C,Condition.A,Condition.D},GazeTarget.Participant,new[]{0,1,2,3});
                var design=ViveLegacyRecordingDesign.Select(Actor.Owner,"formal-design",allocation,geometry.Version,ViveFixedTrackpadMapping.Version);
                var config=SessionConfiguration.LegacyRecordingDevelopment(new SessionConfiguration("formal-pump",73,allocation,RunKind.Simulation,"en",5,30,true,0,true,true,true),
                    design,ViveLegacyRecordingIntent.Select(Actor.Owner,"formal-intent",true,true));
                config=SessionConfiguration.WithFormalEyeRecordingOnly(SessionConfiguration.WithTrainingProtocol(config,TrainingProtocol.TutorialExamV1));
                Worker=new ViveProEyeWorker(Clock,()=>new ViveHeadFrame(++head,Clock.Now,true,true,Rotation.Identity),()=>{Factories++;return Native;});
                Check.True(Worker.StartTaskOnly());
                Task=new ViveDevelopmentParticipant(config,Clock,geometry,ViveFixedTrackpadMapping.Create(),.5,true);
                Admission=ViveNamedSessionFrameAdmission.CreateLegacySyntheticForTest(Task,Worker,Clock,256,0,0);
                Check.True(Admission.ConfirmTask()&&Admission.ConfirmEye());Check.True(Admission.TryBindQueueBudget(ViveRawByteBudget.SelectExplicit(64*1024*1024)));
                Writer=new ViveNamedRawRecordingPump.SyntheticWriter(Admission);
                Pump=ViveNamedRawRecordingPump.StartLegacySyntheticWithStatusForTest(Task,Worker,Clock,Admission,Writer);
                Mutate(()=>Task.RecordLegacySoftwareChoices(Clock,0,0));
                Mutate(()=>Task.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,false,0,0)));
                Mutate(()=>Task.OwnerBeginTutorial(Actor.Owner));
            }
            internal void Stage()
            {
                Check.Equal(ViveNamedRawRecordingPump.Preparation.Ready,Pump.PrepareStatus());
                ViveProEyePoll frame;Check.True(Worker.TryPollTaskForRecording(out frame));bool waiting;
                Check.True(Pump.StageRigFrame(frame,1,false,out waiting),"pump stage: "+Pump.FirstFault);Check.True(!waiting&&Pump.RawTaskEpochThisFrame);
            }
            internal void Mutate(Action action)
            {Stage();Check.True(Pump.TryRunReservedOrdinaryMutation(action),"pump mutation: "+Pump.FirstFault);}
            internal void Answer()
            {
                Mutate(()=>Task.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,false,0,0)));Stage();
                var trial=Task.CurrentTrial;ViveNamedSessionFrameAdmission.PresentationReservation token;int frame=++renderFrame;
                Check.True(Pump.TryPreparePresentation(frame,frame,out token));Check.True(token.Ticket.EyeNotRequested);
                Check.True(Pump.CommitPresentation(token,frame,frame,trial.TrialId,trial.Stimulus.Word,trial.Stimulus.Ink,Clock.Now));
                var colour=trial.ExpectedResponse;
                double x=colour==Colour.Green?.9:colour==Colour.Yellow?-.9:0,y=colour==Colour.Red?.9:colour==Colour.Blue?-.9:0;
                Mutate(()=>Task.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,true,x,y)));
                Mutate(()=>Task.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,false,0,0)));
            }
            internal void BeginPlan(bool practice)
            {
                var plan=Task.PrepareRawRecordingBlockPlan(Actor.Owner,practice);Check.True(Pump.DrainTaskEvents());
                ViveBlockPlanRequest request;ViveNamedSessionFrameAdmission.SyntheticPlanProof proof;
                Check.True(ViveBlockPlanRequest.TryCapture(Task,Clock,plan,out request));
                Check.True(Admission.TryIssueSyntheticPlanProof(request,plan,out proof));
                Check.True(practice?Task.TryBeginSyntheticPracticeForFixture(Admission,request,proof):
                    Task.TryBeginSyntheticLegacyBlockForFixture(Admission,request,proof));Check.True(Pump.DrainTaskEvents());
            }
            internal void StartNative()
            {Check.True(Worker.RequestFormalEyeStart());nativeStarted=true;Check.True(Native.Waiting.WaitOne(3000));}
            internal void Healthy(ref int frame)
            {
                double deadline=Clock.Now+3;
                while(!Worker.ReadRecordingStartReadiness().Ready&&Clock.Now<deadline)
                {Thread.Sleep(20);Native.Send(Eye(Clock.Now,frame++));}
                Check.True(Worker.ReadRecordingStartReadiness().Ready);
            }
            public void Dispose()
            {
                Pump.Stop();SpinWait.SpinUntil(()=>Pump.HasExited,3000);
                Worker.TryRequestStop();if(nativeStarted)Native.Send(null,false);Worker.Stop(1000);Worker.Dispose();
                Native.Waiting.Dispose();Native.Permit.Dispose();
            }
        }
#endif
        public static void Register(TestSuite suite)
        {
            suite.Run("Initial task-only action reserves exact manifest plus full action and terminal budget",()=>{
                var option=ViveNamedOptionProvenance.Select(Actor.Owner,ViveNamedOption.Automatic,0,"budget-geometry");
                var config=SessionConfiguration.WithFormalEyeRecordingOnly(SessionConfiguration.NamedRecordingDevelopment("budget-session",73,
                    ViveNamedSimulationPlan.Select(Actor.Owner,"budget-plan",option),ViveNamedRecordingIntent.Select(Actor.Owner,"budget-intent",true,true)));
                var manifest=new ViveNamedSessionFrameAdmission.Manifest(ViveRawSessionContext.FromConfiguration(config),"budget-clock",true,0,0);manifest.SealChoiceTime(0,-1);
                int header=ViveRawSessionJournalCodec.CurrentManifestPayload(manifest).Length+48,upper=ViveNamedSessionFrameAdmission.OrdinaryActionEventLimit*1048616;
                long exact=(long)header+upper+ViveRawByteBudget.TerminalFrameBytes;
                var full=new ViveRawByteBudget.QueueLedger(ViveRawByteBudget.SelectExplicit(exact),manifest);
                var shortOne=new ViveRawByteBudget.QueueLedger(ViveRawByteBudget.SelectExplicit(exact-1),manifest);
                Check.True(!full.CanReserveUpper(0,upper),"legacy reservation must remain unchanged");
                Check.True(full.CanReserveUpper(0,upper,true));Check.True(!full.CanReserveUpper(1,upper,true));
                Check.True(!shortOne.CanReserveUpper(0,upper,true),"manifest or terminal reserve was omitted");
            });
#if VIVE_SYNTHETIC_BATCH_FIXTURE
            suite.Run("Task-only raw pump completes tutorial and ten-question exam then holds first formal countdown until eye readiness recovers",()=>{
                using(var f=new PumpFixture())
                {
                    Check.Equal(Phase.Tutorial,f.Task.Phase);f.Answer();f.Mutate(f.Task.RequestPracticeExam);f.BeginPlan(true);
                    for(int i=0;i<10;i++)f.Answer();Check.Equal(Phase.PracticeExamResult,f.Task.Phase);Check.Equal(10,f.Task.PracticeCorrect);
                    Check.Equal(0,f.Factories);Check.True(!f.Worker.FormalEyePersistenceEnabled);
                    f.Mutate(f.Task.ContinuePracticeExamResult);f.Mutate(()=>f.Task.OwnerLockSource(Actor.Owner,ControlSelection.Automatic,"test-owner"));
                    f.StartNative();int frame=1;f.Healthy(ref frame);
                    int taskCount=f.Task.ReadEventsAfter(0,4096).Count;
                    Check.True(SpinWait.SpinUntil(()=>Volatile.Read(ref f.Writer.Calls)>=taskCount,3000));
                    Check.Equal(taskCount,Volatile.Read(ref f.Writer.Calls),"preformal pump persisted a non-task row");
                    f.BeginPlan(false);Check.Equal(Phase.Countdown,f.Task.Phase);
                    ViveNamedSessionFrameAdmission.CountdownWitnessReservation earlyCountdown;
                    Check.True(!f.Admission.TryReserveCountdownWitness(42,42,f.Clock.Now,out earlyCountdown)&&earlyCountdown==null,
                        "same-frame training authority admitted the first formal countdown before eye persistence activation");
                    Check.True(f.Task.RawCountdownAwaitingSoftwareWitness&&!f.Admission.IoFence.IsRevoked);Check.Equal(0,f.Admission.State);
                    f.Native.Send(Eye(f.Clock.Now,frame++,false));
                    Check.Equal(ViveNamedRawRecordingPump.Preparation.WaitingBaseline,f.Pump.PrepareStatus());
                    Check.True(f.Pump.FormalEyeActivationPending&&!f.Pump.RawTaskEpochThisFrame);
                    ViveProEyePoll poll;Check.True(f.Worker.TryPollTaskForRecording(out poll));bool waiting;
                    Check.True(!f.Pump.StageRigFrame(poll,1,false,out waiting)&&waiting);
                    Check.True(!f.Pump.RawTaskEpochThisFrame&&!f.Worker.FormalEyePersistenceEnabled);
                    Check.Equal(Phase.Countdown,f.Task.Phase);Check.Equal(ViveNamedRawRecordingPump.Outcome.Running,f.Pump.State);
                    f.Healthy(ref frame);Check.Equal(ViveNamedRawRecordingPump.Preparation.Ready,f.Pump.PrepareStatus());
                    Check.True(f.Worker.FormalEyePersistenceEnabled&&!f.Pump.FormalEyeActivationPending);f.Stage();
                    Check.True(!f.Admission.IoFence.IsRevoked);
                }
            });
#endif
            suite.Run("Task-only training never constructs an eye client and retains fresh single-use head authority",()=>{
                var clock=new MonotonicClock("formal-task-carrier");int factories=0;long head=0;
                var worker=new ViveProEyeWorker(clock,()=>new ViveHeadFrame(++head,clock.Now,true,true,Rotation.Identity),()=>{factories++;throw new InvalidOperationException("must not start during training");});
                try
                {
                    Check.True(worker.StartTaskOnly());object run;Check.True(worker.TryGetRecordingRunIdentity(out run));
                    Check.True(worker.IsTaskOnlyRecordingRun&&!worker.Started&&!worker.Starting&&!worker.Stopping);
                    int starts=0;var claim=new ViveRecordingStartClaim(worker,true);
                    Check.Equal(ViveRecordingStartClaim.Result.Waiting,claim.TryExecute(()=>true,()=>false,()=>starts++));
                    Check.Equal(ViveRecordingStartClaim.Result.Attempted,claim.TryExecute(()=>true,()=>true,()=>starts++));
                    Check.Equal(ViveRecordingStartClaim.Result.Invalid,claim.TryExecute(()=>true,()=>true,()=>starts++));Check.Equal(1,starts);
                    object bound;Check.True(worker.TryEnableStatusEvidence(8,new ViveRawRevocation(),out bound,true));Check.True(Object.ReferenceEquals(run,bound));
                    for(int i=0;i<12;i++)
                    {
                        var ticket=Ticket(worker,clock);Check.True(ticket.EyeNotRequested&&ticket.HeadOnly&&ticket.TaskAuthority&&ticket.Frame.Eye==null);
                        ViveProEyePoll consumed;Check.True(worker.TryConsumeRecordingTicket(ticket,out consumed));
                        Check.True(!worker.TryConsumeRecordingTicket(ticket,out consumed));
                        Check.Equal(ViveProEyeWorker.RecordingEpochResult.Current,worker.TryCheckRecordingEpoch(ticket));
                    }
                    Check.Equal(ViveProEyeWorker.StatusDisposition.Empty,worker.ReadStatusBacklog(run));
                    Check.Equal(ViveProEyeWorker.StatusDisposition.Empty,worker.ReadAcquisitionBacklog(run));
                    Check.True(!worker.ReadRecordingStartReadiness().Ready);Check.Equal(0,factories);
                    Check.Equal(ViveProEyeWorker.AcquisitionSealDisposition.Ready,worker.PrepareAcquisitionSeal(run));
                    ViveProEyeWorker.StatusSeal seal;Check.True(worker.TrySealStatusEvidence(run,out seal,true));Check.Equal(-1L,seal.FinalSequence);
                    Check.True(worker.Stop(1000));Check.Equal(0,factories);
                }
                finally{worker.Dispose();}
            });
            suite.Run("Formal eye boundary requires healthy readiness and excludes all warmup status gaze and buffered old frames",()=>{
                var clock=new MonotonicClock("formal-eye-window");var client=new Client();long head=0;int factories=0;
                var worker=new ViveProEyeWorker(clock,()=>new ViveHeadFrame(++head,clock.Now,true,true,Rotation.Identity),()=>{factories++;return client;});
                bool native=false;
                try
                {
                    Check.True(worker.StartTaskOnly());object run;Check.True(worker.TryEnableStatusEvidence(64,new ViveRawRevocation(),out run,true));
                    Check.True(worker.RequestFormalEyeStart());native=true;Check.True(client.Waiting.WaitOne(3000));Check.Equal(1,factories);
                    client.Send(Eye(clock.Now,1,false));
                    Check.True(!worker.ReadRecordingStartReadiness().Ready);
                    Check.Equal(ViveProEyeWorker.RecordingStartReadinessState.Waiting,worker.TryEnableFormalEyePersistence(run,clock.Now));
                    int frame=2;double deadline=clock.Now+3;
                    while(!worker.ReadRecordingStartReadiness().Ready&&clock.Now<deadline)
                    {Thread.Sleep(20);client.Send(Eye(clock.Now,frame++));}
                    Check.True(worker.ReadRecordingStartReadiness().Ready);
                    Check.Equal(ViveProEyeWorker.StatusDisposition.Empty,worker.ReadStatusBacklog(run));
                    Check.Equal(ViveProEyeWorker.StatusDisposition.Empty,worker.ReadAcquisitionBacklog(run));
                    var training=Ticket(worker,clock);Check.True(training.EyeNotRequested&&training.Frame.Eye==null);
                    ViveProEyePoll consumed;Check.True(worker.TryConsumeRecordingTicket(training,out consumed));
                    Check.Equal(ViveProEyeWorker.RecordingStartReadinessState.Unavailable,worker.TryEnableFormalEyePersistence(new object(),clock.Now));
                    double boundary=clock.Now;
                    Check.Equal(ViveProEyeWorker.RecordingStartReadinessState.Ready,worker.TryEnableFormalEyePersistence(run,boundary));
                    // Poll already reserved before boundary; then a callback whose observed time predates it.
                    client.Send(Eye(boundary,frame++));client.Send(Eye(boundary-.001,frame++));
                    Check.Equal(ViveProEyeWorker.StatusDisposition.Empty,worker.ReadAcquisitionBacklog(run));
                    Check.Equal(ViveProEyeWorker.StatusDisposition.Empty,worker.ReadStatusBacklog(run));
                    client.Send(Eye(clock.Now,frame++));Ack(worker,run);
                    ViveRawEyeAcquisition sample;Check.Equal(ViveProEyeWorker.StatusDisposition.Event,worker.TryDequeueAcquisition(run,out sample));
                    Check.Equal(0L,sample.Sequence);Check.True(sample.Poll.Eye.ObservedLocalSeconds>boundary);
                    var formal=Ticket(worker,clock);Check.True(!formal.EyeNotRequested&&!formal.HeadOnly);
                    Check.True(worker.TryConsumeRecordingTicket(formal,out consumed));
                    object same;Check.True(worker.TryGetRecordingRunIdentity(out same)&&Object.ReferenceEquals(run,same));
                    Check.Equal(ViveProEyeWorker.RecordingStartReadinessState.Ready,worker.TryEnableFormalEyePersistence(run,boundary));
                }
                finally{worker.TryRequestStop();if(native)client.Send(null,false);worker.Stop(1000);worker.Dispose();client.Waiting.Dispose();client.Permit.Dispose();}
            });
            suite.Run("Task-only raw admission emits task records without fabricated missing eye rows",()=>{
                var clock=new MonotonicClock("training-no-eye-payload");long head=0;int factories=0;
                var worker=new ViveProEyeWorker(clock,()=>new ViveHeadFrame(++head,clock.Now,true,true,Rotation.Identity),()=>{factories++;return new Client();});
                ViveNamedSessionFrameAdmission admission=null;
                try
                {
                    Check.True(worker.StartTaskOnly());
                    var option=ViveNamedOptionProvenance.Select(Actor.Owner,ViveNamedOption.Automatic,0,DevelopmentFixtures.Geometry().Version);
                    var config=SessionConfiguration.NamedRecordingDevelopment("training-no-eyes",73,ViveNamedSimulationPlan.Select(Actor.Owner,"training-plan",option),
                        ViveNamedRecordingIntent.Select(Actor.Owner,"training-intent",true,true));
                    config=SessionConfiguration.WithFormalEyeRecordingOnly(SessionConfiguration.WithTrainingProtocol(config,TrainingProtocol.TutorialExamV1));
                    var task=new ViveDevelopmentParticipant(config,clock,DevelopmentFixtures.Geometry(),ViveFixedTrackpadMapping.Create(),.5,true);
                    admission=ViveNamedSessionFrameAdmission.CreateSyntheticForTest(task,worker,clock,256);
                    Check.True(admission.ConfirmTask()&&admission.ConfirmEye(),"choices");object run;Check.True(admission.TryEnableWorkerStatus(16,out run,true),"status attach");
                    Check.True(admission.TryBindQueueBudget(ViveRawByteBudget.SelectExplicit(32*1024*1024)),"queue budget");
                    Check.True(admission.TryRunReservedOrdinaryMutation(()=>task.RecordFocusState(true,true,true)),"initial task metadata mutation");
                    bool staged;do{Check.True(admission.TryStageNextTaskEvent(out staged),"task event drain");}while(staged);
                    for(int i=0;i<8;i++)
                    {
                        ViveDevelopmentParticipant.NamedRecordingTaskStamp stamp;Check.True(task.TryCaptureRawRecordingTaskStamp(clock,out stamp),"task stamp");
                        Check.True(admission.TryStage(stamp,Ticket(worker,clock),1),"task-only head stage");
                    }
                    int taskRecords=0;ViveNamedSessionFrameAdmission.ConsumerReceipt receipt;
                    do
                    {
                        Check.True(admission.TryReserveConsumer(out receipt),"consumer reserve");if(receipt==null)break;
                        Check.True(receipt.Record is JournalEvent,"training emitted an eye/quality/acquisition record");taskRecords++;
                        Check.True(admission.TryAcknowledgeSyntheticForTest(receipt),"consumer ack");
                    }while(true);
                    Check.True(taskRecords>0,"task records absent");Check.Equal(0,factories);Check.Equal(0,admission.State);
                    Check.Equal(ViveProEyeWorker.StatusDisposition.Empty,worker.ReadStatusBacklog(run));
                    Check.Equal(ViveProEyeWorker.StatusDisposition.Empty,worker.ReadAcquisitionBacklog(run));
                }
                finally{if(admission!=null)admission.Withdraw();worker.Stop(1000);worker.Dispose();}
            });
        }
    }
}
