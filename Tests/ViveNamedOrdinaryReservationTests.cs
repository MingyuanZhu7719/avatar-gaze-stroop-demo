#if VIVE_SYNTHETIC_BATCH_FIXTURE
using System;
using System.Collections.Generic;
using System.Threading;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    // Real participant/admission FIFO with a fake eye client. No file, native
    // writer, Unity camera, consent or hardware validity is tested here.
    public static class ViveNamedOrdinaryReservationTests
    {
        sealed class Client : IViveProEyeClient
        {
            readonly Queue<ViveProEyePoll> values=new Queue<ViveProEyePoll>();
            internal readonly AutoResetEvent Waiting=new AutoResetEvent(false),Permit=new AutoResetEvent(false);
            public string Diagnostic {get{return "synthetic";}}
            public bool Start(){return true;}
            public ViveProEyePoll Poll()
            {Waiting.Set();if(!Permit.WaitOne(3000))throw new Exception("fixture timeout");lock(values)return values.Dequeue();}
            internal void Send(ViveProEyePoll value)
            {lock(values)values.Enqueue(value);Permit.Set();Check.True(Waiting.WaitOne(3000));}
            internal void ReleaseForStop(){lock(values)values.Enqueue(null);Permit.Set();}
            public void Dispose(){}
        }
        sealed class Fixture : IDisposable
        {
            internal readonly MonotonicClock Clock=new MonotonicClock("ordinary-reservation-clock");
            internal readonly Client Source=new Client();
            internal readonly ViveProEyeWorker Worker;
            internal readonly ViveDevelopmentParticipant Task;
            internal readonly ViveNamedSessionFrameAdmission Admission;
            readonly int capacity;int frame;
            internal Fixture(long maxBytes=33554432,int slots=32)
            {
                capacity=slots;
                Worker=new ViveProEyeWorker(Clock,()=>new ViveHeadFrame(1,Clock.Now,true,true,Rotation.Identity),()=>Source);
                Check.True(Worker.Start()&&Source.Waiting.WaitOne(3000));
                var option=ViveNamedOptionProvenance.Select(Actor.Owner,ViveNamedOption.Automatic,1,"ordinary-geometry");
                var noAvatar=ViveNamedOptionProvenance.Select(Actor.Owner,ViveNamedOption.NoAvatar,2,null);
                var plan=ViveNamedSimulationPlan.Select(Actor.Owner,"ordinary-plan",option,noAvatar,option,noAvatar);
                var intent=ViveNamedRecordingIntent.Select(Actor.Owner,"ordinary-intent",true,true);
                var config=SessionConfiguration.NamedRecordingDevelopment("ordinary-session",37,plan,intent);
                var geometry=new AutomaticGazeGeometry(Rotation.Identity,new Vec3(0,0,1),new Rotation(0,Math.Sqrt(.5),0,Math.Sqrt(.5)),new Vec3(1,0,0),"ordinary-geometry");
                var map=new Dictionary<ViveTrackpadDirection,Colour>{{ViveTrackpadDirection.Up,Colour.Red},{ViveTrackpadDirection.Right,Colour.Green},{ViveTrackpadDirection.Down,Colour.Blue},{ViveTrackpadDirection.Left,Colour.Yellow}};
                Task=new ViveDevelopmentParticipant(config,Clock,geometry,map,.5,true);
                Admission=ViveNamedSessionFrameAdmission.CreateSyntheticForTest(Task,Worker,Clock,capacity);
                Check.True(Admission.ConfirmTask()&&Admission.ConfirmEye());
                Check.True(Admission.TryBindQueueBudget(ViveRawByteBudget.SelectExplicit(maxBytes)));
                object run;Check.True(Admission.TryEnableWorkerStatus(8,out run));
                Source.Send(null);SendEye();
                ViveProEyeWorker.StatusTicket status;Check.Equal(ViveProEyeWorker.StatusDisposition.Event,Worker.TryReserveStatusEvidence(run,out status));
                ViveNamedSessionFrameAdmission.StatusAdmissionProof proof;
                Check.True(Admission.TryStageStatus(status,out proof)&&Worker.TryAcknowledgeStatusEvidence(status,proof));
                DrainQueue();
                Task.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,false,0,0));
                var planEvent=Task.PrepareNamedRecordingBlockPlan(Actor.Owner,true);DrainTask();
                ViveBlockPlanRequest request;Check.True(ViveBlockPlanRequest.TryCapture(Task,Clock,planEvent,out request));
                ViveNamedSessionFrameAdmission.SyntheticPlanProof planProof;
                Check.True(Admission.TryIssueSyntheticPlanProof(request,planEvent,out planProof));
                Check.True(Task.TryBeginSyntheticPracticeForFixture(Admission,request,planProof));
                DrainTask();DrainQueue();
                // Seed the new phase with a released sample: the real input
                // adapter intentionally consumes its first sample per phase.
                Task.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,false,0,0));
                DrainTask();DrainQueue();
            }
            internal void SendEye()
            {
                var ray=new ViveEyeRay(2,false,true,new Vec3(0,0,0),new Vec3(.6,0,.8));int next=++frame;
                Source.Send(new ViveProEyePoll(null,new ViveEyeFrame(next,next*8,Clock.Now,false,null,null,ray),
                    ViveEyeRuntimeState.Working,ViveEyeCalibrationState.ReportedReady,true,0,2));
            }
            internal void QueueEye()
            {
                SendEye();ViveProEyePoll poll;Check.True(Worker.TryPollForRecording(out poll));
                ViveProEyeWorker.RecordingFrameTicket ticket;
                Check.Equal(ViveProEyeWorker.RecordingTicketDisposition.Issued,Worker.TryIssueRecordingTicket(poll,out ticket));
                ViveDevelopmentParticipant.NamedRecordingTaskStamp stamp;
                Check.True(Task.TryCaptureNamedRecordingTaskStamp(Clock,out stamp)&&Admission.TryStage(stamp,ticket,1));
            }
            internal void DrainTask(){bool staged;do{Check.True(Admission.TryStageNextTaskEvent(out staged));if(staged&&capacity==1)DrainQueue();}while(staged);}
            internal void DrainQueue()
            {ViveNamedSessionFrameAdmission.ConsumerReceipt receipt;while(Admission.TryReserveConsumer(out receipt)&&receipt!=null)Check.True(Admission.TryAcknowledgeSyntheticForTest(receipt));}
            internal void Present(){Task.MarkPresented(Task.CurrentTrial.TrialId);DrainTask();DrainQueue();}
            internal VivePreviewTrackpadSample CorrectPress()
            {
                switch(Task.CurrentTrial.Stimulus.Ink)
                {
                    case Colour.Red:return new VivePreviewTrackpadSample(true,true,0,.9);
                    case Colour.Green:return new VivePreviewTrackpadSample(true,true,.9,0);
                    case Colour.Blue:return new VivePreviewTrackpadSample(true,true,0,-.9);
                    default:return new VivePreviewTrackpadSample(true,true,-.9,0);
                }
            }
            Experiment Core()
            {return (Experiment)typeof(ViveDevelopmentParticipant).GetField("experiment",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance).GetValue(Task);}
            internal void CompletePracticeAndConfirmPlan()
            {
                for(int i=0;i<8;i++)
                {Present();Task.UpdateTrackpad(true,CorrectPress());DrainTask();DrainQueue();Task.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,false,0,0));DrainTask();DrainQueue();}
                Check.Equal(Phase.BlockPreparation,Task.Phase);
                Check.True(Admission.TryRunReservedOrdinaryMutation(()=>Task.OwnerConfirmNamedPlan(Actor.Owner,"synthetic-owner")));DrainQueue();
            }
            internal void EnterMainForBoundaryFixture()
            {
                // Synthetic setup only: no file receipt or real elapsed countdown
                // is claimed. The tested response/rating/rest is still the real
                // participant action inside the actual admission reservation.
                var exp=Core();exp.BeginCountdown(Actor.Owner,true);DrainTask();DrainQueue();
                Check.True(Task.TryStartNamedRawCountdownFromSoftwareWitness(1,1,1));DrainQueue();
                typeof(Experiment).GetField("countdownAt",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance).SetValue(exp,-100.0);
                Task.Tick();DrainTask();DrainQueue();Check.Equal(Phase.MainTask,Task.Phase);
                Task.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,false,0,0));DrainTask();DrainQueue();
            }
            internal void RespondAndDrain()
            {Present();Task.UpdateTrackpad(true,CorrectPress());DrainTask();DrainQueue();Task.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,false,0,0));DrainTask();DrainQueue();}
            internal void PrimeAux()
            {Task.UpdateAuxiliary(false,false,false,0,true);DrainTask();DrainQueue();}
            internal void ConfirmAndDrain()
            {PrimeAux();Task.UpdateAuxiliary(true,false,false,0,true);DrainTask();DrainQueue();PrimeAux();}
            internal List<string> ReservedEvents(Action action)
            {
                Check.True(Admission.TryRunReservedOrdinaryMutation(action));var kinds=new List<string>();
                ViveNamedSessionFrameAdmission.ConsumerReceipt receipt;
                while(Admission.TryReserveConsumer(out receipt)&&receipt!=null)
                {var item=receipt.Record as JournalEvent;Check.True(item!=null);kinds.Add(item.Kind);Check.True(Admission.TryAcknowledgeSyntheticForTest(receipt));}
                return kinds;
            }
            internal void ReachBlockEnd(int block)
            {
                CompletePracticeAndConfirmPlan();
                for(int b=0;b<=block;b++)
                {
                    EnterMainForBoundaryFixture();
                    for(int i=0;i<23;i++)RespondAndDrain();
                    if(b==block)return;
                    RespondAndDrain();Check.Equal(Phase.Questionnaire,Task.Phase);
                    ConfirmAndDrain();
                    for(int i=0;i<5;i++)ConfirmAndDrain();
                    Check.Equal(Phase.Rest,Task.Phase);ConfirmAndDrain();Check.Equal(Phase.BlockReady,Task.Phase);
                }
            }
            public void Dispose()
            {Admission.Withdraw();Worker.TryRequestStop();Source.ReleaseForStop();Check.True(Worker.Stop(1000));Worker.Dispose();}
        }
        public static void RegisterFinalFrameBoundaryTests(TestSuite suite)
        {
            suite.Run("completed final questionnaire rejects another synthetic eye frame",()=>{
                using(var f=new Fixture())
                {
                    f.ReachBlockEnd(3);f.RespondAndDrain();f.ConfirmAndDrain();
                    for(int i=0;i<4;i++)f.ConfirmAndDrain();
                    Check.Equal(Phase.Questionnaire,f.Task.Phase);f.PrimeAux();
                    var events=f.ReservedEvents(()=>f.Task.UpdateAuxiliary(true,false,false,0,true));
                    Check.Equal(Phase.Ended,f.Task.Phase);Check.True(f.Task.Completed);
                    Check.Equal("session_ended",events[3]);Check.Equal("phase_changed",events[4]);
                    f.SendEye();ViveProEyePoll poll;Check.True(f.Worker.TryPollForRecording(out poll));
                    ViveProEyeWorker.RecordingFrameTicket ticket;
                    Check.Equal(ViveProEyeWorker.RecordingTicketDisposition.Issued,f.Worker.TryIssueRecordingTicket(poll,out ticket));
                    ViveDevelopmentParticipant.NamedRecordingTaskStamp stamp;
                    Check.True(f.Task.TryCaptureNamedRecordingTaskStamp(f.Clock,out stamp));Check.Equal(Phase.Ended,stamp.Phase);
                    Check.True(!f.Admission.TryStage(stamp,ticket,1));Check.True(f.Admission.IoFence.IsFaulted);
                }
            });
        }
        public static void Register(TestSuite suite)
        {
            RegisterFinalFrameBoundaryTests(suite);
            suite.Run("ordinary epoch time rejects stale eye or head and clock rollback",()=>{
                Check.True(ViveNamedRawRecordingPump.EpochTimeFresh(10.2,10.1,10.0,10.05,.5));
                Check.True(!ViveNamedRawRecordingPump.EpochTimeFresh(10.2,10.3,10.0,10.05,.5));
                Check.True(!ViveNamedRawRecordingPump.EpochTimeFresh(10.6,10.1,10.0,10.5,.5));
                Check.True(!ViveNamedRawRecordingPump.EpochTimeFresh(10.6,10.1,10.5,10.0,.5));
                Check.True(!ViveNamedRawRecordingPump.EpochTimeFresh(9.9,9.8,10.0,9.8,.5));
                Check.True(!ViveNamedRawRecordingPump.EpochTimeFresh(Double.NaN,10,10,10,.5));
            });
            suite.Run("ordinary reservation admits all seven real four-block confirmation events in order",()=>{
                using(var f=new Fixture())
                {
                    for(int i=0;i<8;i++)
                    {
                        f.Present();f.Task.UpdateTrackpad(true,f.CorrectPress());f.DrainTask();f.DrainQueue();
                        f.Task.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,false,0,0));f.DrainTask();f.DrainQueue();
                    }
                    Check.Equal(Phase.BlockPreparation,f.Task.Phase);
                    Check.True(f.Admission.TryRunReservedOrdinaryMutation(()=>f.Task.OwnerConfirmNamedPlan(Actor.Owner,"synthetic-owner")));
                    var kinds=new List<string>();ViveNamedSessionFrameAdmission.ConsumerReceipt receipt;
                    while(f.Admission.TryReserveConsumer(out receipt)&&receipt!=null)
                    {var e=receipt.Record as JournalEvent;Check.True(e!=null);kinds.Add(e.Kind);Check.True(f.Admission.TryAcknowledgeSyntheticForTest(receipt));}
                    Check.Equal(7,kinds.Count);Check.Equal("named_plan_confirmed",kinds[0]);
                    for(int i=1;i<=4;i++)Check.Equal("named_plan_item_confirmed",kinds[i]);
                    Check.Equal("block_configuration_locked",kinds[5]);Check.Equal("phase_changed",kinds[6]);
                }
            });
            suite.Run("ordinary reserved response publishes its actual ordered task events",()=>{
                using(var f=new Fixture())
                {
                    f.Present();var id=f.Task.CurrentTrial.TrialId;
                    Check.True(f.Admission.TryRunReservedOrdinaryMutation(()=>f.Task.UpdateTrackpad(true,f.CorrectPress())));
                    var kinds=new List<string>();ViveNamedSessionFrameAdmission.ConsumerReceipt receipt;
                    while(f.Admission.TryReserveConsumer(out receipt)&&receipt!=null)
                    {
                        var e=receipt.Record as JournalEvent;Check.True(e!=null);kinds.Add(e.Kind);
                        if(kinds.Count==1){Check.Equal("first_response",e.Kind);Check.Equal(id,e.TrialId);}
                        Check.True(f.Admission.TryAcknowledgeSyntheticForTest(receipt));
                    }
                    Check.Equal(3,kinds.Count);Check.Equal("phase_changed",kinds[1]);Check.Equal("trial_planned",kinds[2]);
                    bool staged;Check.True(f.Admission.TryStageNextTaskEvent(out staged)&&!staged);
                }
            });
            suite.Run("reserved last main response reaches questionnaire in FIFO order",()=>{
                using(var f=new Fixture())
                {
                    f.ReachBlockEnd(0);f.Present();var events=f.ReservedEvents(()=>f.Task.UpdateTrackpad(true,f.CorrectPress()));
                    Check.Equal(4,events.Count);Check.Equal("first_response",events[0]);Check.Equal("block_task_ended",events[1]);
                    Check.Equal("phase_changed",events[2]);Check.Equal("questionnaire_instruction_started",events[3]);Check.Equal(Phase.Questionnaire,f.Task.Phase);
                }
            });
            suite.Run("reserved rest confirmation advances named block in FIFO order",()=>{
                using(var f=new Fixture())
                {
                    f.ReachBlockEnd(0);f.RespondAndDrain();f.ConfirmAndDrain();
                    for(int i=0;i<5;i++)f.ConfirmAndDrain();Check.Equal(Phase.Rest,f.Task.Phase);f.PrimeAux();
                    var events=f.ReservedEvents(()=>f.Task.UpdateAuxiliary(true,false,false,0,true));
                    Check.Equal(3,events.Count);Check.Equal("rest_ready",events[0]);Check.Equal("block_configuration_locked",events[1]);
                    Check.Equal("phase_changed",events[2]);Check.Equal(Phase.BlockReady,f.Task.Phase);
                }
            });
            suite.Run("reserved final rating finishes named plan in FIFO order",()=>{
                using(var f=new Fixture())
                {
                    f.ReachBlockEnd(3);f.RespondAndDrain();f.ConfirmAndDrain();
                    for(int i=0;i<4;i++)f.ConfirmAndDrain();Check.Equal(Phase.Questionnaire,f.Task.Phase);f.PrimeAux();
                    var events=f.ReservedEvents(()=>f.Task.UpdateAuxiliary(true,false,false,0,true));
                    Check.Equal(5,events.Count);Check.Equal("rating",events[0]);Check.Equal("questionnaire_completed",events[1]);
                    Check.Equal("final_rest_skipped",events[2]);Check.Equal("session_ended",events[3]);Check.Equal("phase_changed",events[4]);
                    Check.Equal(Phase.Ended,f.Task.Phase);
                }
            });
            suite.Run("ordinary mutation cannot run without its full reserved FIFO capacity",()=>{
                using(var f=new Fixture(slots:8))
                {
                    f.QueueEye();bool ran=false;var before=f.Task.CurrentTrial;
                    Check.True(!f.Admission.TryRunReservedOrdinaryMutation(()=>{ran=true;f.Task.Tick();}));
                    Check.True(!ran&&Object.ReferenceEquals(before,f.Task.CurrentTrial)&&f.Admission.State!=0);
                }
            });
            suite.Run("ordinary mutation cannot run without conservative byte budget",()=>{
                using(var f=new Fixture(maxBytes:8388608))
                {bool ran=false;var before=f.Task.CurrentTrial;
                    Check.True(!f.Admission.TryRunReservedOrdinaryMutation(()=>{ran=true;f.Task.Tick();}));
                    Check.True(!ran&&Object.ReferenceEquals(before,f.Task.CurrentTrial)&&f.Admission.State!=0);}
            });
            suite.Run("explicit withdrawal revokes during a reserved ordinary mutation",()=>{
                using(var f=new Fixture())
                {
                    var done=new ManualResetEvent(false);Exception error=null;
                    Check.True(!f.Admission.TryRunReservedOrdinaryMutation(()=>{
                        f.Task.Tick();var thread=new Thread(()=>{try{f.Task.ParticipantWithdraw();}catch(Exception e){error=e;}finally{done.Set();}});
                        thread.Start();Check.True(done.WaitOne(1000));}));
                    Check.True(error==null&&f.Task.NamedRecordingWithdrawalRequested&&f.Admission.State!=0);
                }
            });
            suite.Run("exception after ordinary mutation leaves admission faulted and unconfirmed",()=>{
                using(var f=new Fixture())
                {f.Present();var before=f.Task.CurrentTrial;
                    Check.True(!f.Admission.TryRunReservedOrdinaryMutation(()=>{f.Task.UpdateTrackpad(true,f.CorrectPress());throw new InvalidOperationException("synthetic after mutation");}));
                    Check.True(before.Outcome==TrialOutcome.Responded&&f.Admission.State!=0);
                }
            });
        }
    }
}
#endif
