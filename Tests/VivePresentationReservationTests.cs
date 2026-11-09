#if VIVE_SYNTHETIC_BATCH_FIXTURE
using System;
using System.Collections.Generic;
using System.Threading;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    // These checks use a real simulation participant and actual admission
    // FIFO, with a fake eye client. They never start a Unity camera or write a
    // file and do not establish hardware visibility or durable output.
    public static class VivePresentationReservationTests
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
            internal readonly MonotonicClock Clock=new MonotonicClock("presentation-reservation-clock");
            internal readonly Client Source=new Client();
            internal readonly ViveProEyeWorker Worker;
            internal readonly ViveDevelopmentParticipant Task;
            internal readonly ViveNamedSessionFrameAdmission Admission;
            internal readonly ViveRawByteBudget Budget;
            readonly int capacity;
            int frame;
            internal Fixture(long maxBytes=8388608,int capacity=32)
            {
                this.capacity=capacity;
                Worker=new ViveProEyeWorker(Clock,()=>new ViveHeadFrame(1,Clock.Now,true,true,Rotation.Identity),()=>Source);
                Check.True(Worker.Start()&&Source.Waiting.WaitOne(3000));
                var option=ViveNamedOptionProvenance.Select(Actor.Owner,ViveNamedOption.Automatic,1,"reservation-geometry");
                var plan=ViveNamedSimulationPlan.Select(Actor.Owner,"reservation-plan",option);
                var intent=ViveNamedRecordingIntent.Select(Actor.Owner,"reservation-intent",true,true);
                var config=SessionConfiguration.NamedRecordingDevelopment("reservation-session",37,plan,intent);
                var geometry=new AutomaticGazeGeometry(Rotation.Identity,new Vec3(0,0,1),new Rotation(0,Math.Sqrt(.5),0,Math.Sqrt(.5)),new Vec3(1,0,0),"reservation-geometry");
                var map=new Dictionary<ViveTrackpadDirection,Colour>{{ViveTrackpadDirection.Up,Colour.Red},{ViveTrackpadDirection.Right,Colour.Green},{ViveTrackpadDirection.Down,Colour.Blue},{ViveTrackpadDirection.Left,Colour.Yellow}};
                Task=new ViveDevelopmentParticipant(config,Clock,geometry,map,.5,true);
                Admission=ViveNamedSessionFrameAdmission.CreateSyntheticForTest(Task,Worker,Clock,capacity);
                Check.True(Admission.ConfirmTask()&&Admission.ConfirmEye());
                Budget=ViveRawByteBudget.SelectExplicit(maxBytes);
                Check.True(Admission.TryBindQueueBudget(Budget));
                object run;Check.True(Admission.TryEnableWorkerStatus(8,out run));
                Source.Send(null);SendEye();
                ViveProEyeWorker.StatusTicket status;
                Check.Equal(ViveProEyeWorker.StatusDisposition.Event,Worker.TryReserveStatusEvidence(run,out status));
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
            }
            internal void SendEye(bool noUser=false)
            {
                var ray=new ViveEyeRay(2,false,true,new Vec3(0,0,0),new Vec3(.6,0,.8));int next=++frame;
                Source.Send(new ViveProEyePoll(null,new ViveEyeFrame(next,next*8,Clock.Now,noUser,null,null,ray),
                    ViveEyeRuntimeState.Working,ViveEyeCalibrationState.ReportedReady,true,0,2));
            }
            internal ViveProEyeWorker.RecordingFrameTicket StageFreshEye()
            {
                SendEye();ViveProEyePoll poll;Check.True(Worker.TryPollForRecording(out poll));
                ViveProEyeWorker.RecordingFrameTicket ticket;
                Check.Equal(ViveProEyeWorker.RecordingTicketDisposition.Issued,Worker.TryIssueRecordingTicket(poll,out ticket));
                ViveDevelopmentParticipant.NamedRecordingTaskStamp stamp;
                Check.True(Task.TryCaptureNamedRecordingTaskStamp(Clock,out stamp)&&Admission.TryStage(stamp,ticket,1));
                return ticket;
            }
            internal void DrainTask(){bool staged;do{Check.True(Admission.TryStageNextTaskEvent(out staged));if(staged&&capacity==1)DrainQueue();}while(staged);}
            internal void DrainQueue()
            {ViveNamedSessionFrameAdmission.ConsumerReceipt receipt;while(Admission.TryReserveConsumer(out receipt)&&receipt!=null)Check.True(Admission.TryAcknowledgeSyntheticForTest(receipt));}
            public void Dispose()
            {Admission.Withdraw();Worker.TryRequestStop();Source.ReleaseForStop();Check.True(Worker.Stop(1000));Worker.Dispose();}
        }
        static bool Reserve(Fixture f,ViveProEyeWorker.RecordingFrameTicket ticket,out ViveNamedSessionFrameAdmission.PresentationReservation token)
        {return f.Admission.TryReservePresentation(ticket,1,42,8,out token);}
        static bool Commit(Fixture f,ViveNamedSessionFrameAdmission.PresentationReservation token,int frame=42,long revision=8,string trial=null)
        {
            var current=f.Task.CurrentTrial;
            return f.Admission.TryCommitPresentation(token,frame,revision,trial??current.TrialId,current.Stimulus.Word,current.Stimulus.Ink,12.75);
        }
        public static void Register(TestSuite suite)
        {
            suite.Run("presentation reservation commits the real task event to the shared FIFO",()=>{
                using(var f=new Fixture())
                {
                    var ticket=f.StageFreshEye();f.DrainQueue();var trial=f.Task.CurrentTrial;
                    ViveNamedSessionFrameAdmission.PresentationReservation token;
                    Check.True(Reserve(f,ticket,out token)&&token!=null&&!trial.Onset.HasValue);
                    Check.True(Commit(f,token)&&trial.Onset.HasValue);
                    ViveNamedSessionFrameAdmission.ConsumerReceipt receipt;
                    Check.True(f.Admission.TryReserveConsumer(out receipt)&&receipt!=null);
                    var item=receipt.Record as JournalEvent;
                    Check.True(item!=null&&item.Kind=="stimulus_presented"&&item.TrialId==trial.TrialId&&item.ClockId==f.Clock.ClockId);
                    Check.Equal(trial.Stimulus.Word.ToString(),item.Fields["word"]);
                    Check.Equal(trial.Stimulus.Ink.ToString(),item.Fields["ink"]);
                    Check.Equal("1",item.Fields["software_proxy_schema"]);
                    Check.Equal(trial.Onset.Value.ToString("R",System.Globalization.CultureInfo.InvariantCulture),item.Fields["task_onset_seconds"]);
                    Check.Equal(f.Clock.ClockId,item.Fields["task_onset_clock_id"]);
                    Check.Equal("task_clock_after_caller_declared_unity_proxy",item.Fields["task_onset_basis"]);
                    Check.Equal("12.75",item.Fields["unity_render_proxy_seconds"]);
                    Check.Equal("unity-realtime-since-startup",item.Fields["unity_render_proxy_clock_id"]);
                    Check.Equal("42",item.Fields["unity_render_proxy_frame"]);
                    Check.Equal("8",item.Fields["unity_render_proxy_revision"]);
                    Check.Equal("caller_declared_unity_render_proxy_not_photon_onset",item.Fields["unity_render_proxy_basis"]);
                    ViveNamedSessionFrameAdmission.Manifest manifest;
                    Check.True(f.Admission.TryGetManifestForReservedConsumer(receipt,item,out manifest));
                    int encoded=ViveRawSessionJournalCodec.RecordPayload(receipt.Ordinal+1,item,manifest).Length+40;
                    Check.True(f.Admission.VerifyQueuedFrameBudget(receipt,item,f.Budget,encoded,0));
                    Check.True(f.Admission.TryAcknowledgeSyntheticForTest(receipt));
                    bool staged;Check.True(f.Admission.TryStageNextTaskEvent(out staged)&&!staged);
                }
            });
            suite.Run("presentation cannot reserve a full shared FIFO",()=>{
                using(var f=new Fixture(capacity:1))
                {var ticket=f.StageFreshEye();var trial=f.Task.CurrentTrial;ViveNamedSessionFrameAdmission.PresentationReservation token;
                    Check.True(!Reserve(f,ticket,out token)&&token==null&&!trial.Onset.HasValue&&f.Admission.State!=0);}
            });
            suite.Run("presentation cannot reserve beyond the remaining byte budget",()=>{
                using(var f=new Fixture(maxBytes:1048576))
                {var ticket=f.StageFreshEye();f.DrainQueue();var trial=f.Task.CurrentTrial;ViveNamedSessionFrameAdmission.PresentationReservation token;
                    Check.True(!Reserve(f,ticket,out token)&&token==null&&!trial.Onset.HasValue&&f.Admission.State!=0);}
            });
            suite.Run("late frame or changed revision cannot mutate a trial",()=>{
                foreach(bool badFrame in new[]{true,false})
                using(var f=new Fixture())
                {var ticket=f.StageFreshEye();f.DrainQueue();var trial=f.Task.CurrentTrial;ViveNamedSessionFrameAdmission.PresentationReservation token;
                    Check.True(Reserve(f,ticket,out token));
                    Check.True(!Commit(f,token,badFrame?43:42,badFrame?8:9)&&!trial.Onset.HasValue&&f.Admission.State!=0);}
            });
            suite.Run("other trial identity cannot mutate the reserved trial",()=>{
                using(var f=new Fixture())
                {var ticket=f.StageFreshEye();f.DrainQueue();var trial=f.Task.CurrentTrial;ViveNamedSessionFrameAdmission.PresentationReservation token;
                    Check.True(Reserve(f,ticket,out token));
                    Check.True(!Commit(f,token,trial:"another-trial")&&!trial.Onset.HasValue&&f.Admission.State!=0);}
            });
            suite.Run("invalid Unity proxy time cannot mutate the reserved trial",()=>{
                foreach(double time in new[]{-1.0,Double.NaN,Double.PositiveInfinity})
                using(var f=new Fixture())
                {var ticket=f.StageFreshEye();f.DrainQueue();var trial=f.Task.CurrentTrial;ViveNamedSessionFrameAdmission.PresentationReservation token;
                    Check.True(Reserve(f,ticket,out token));
                    Check.True(!f.Admission.TryCommitPresentation(token,42,8,trial.TrialId,trial.Stimulus.Word,trial.Stimulus.Ink,time)&&!trial.Onset.HasValue&&f.Admission.State!=0);}
            });
            suite.Run("withdrawal before callback prevents trial onset",()=>{
                using(var f=new Fixture())
                {var ticket=f.StageFreshEye();f.DrainQueue();var trial=f.Task.CurrentTrial;ViveNamedSessionFrameAdmission.PresentationReservation token;
                    Check.True(Reserve(f,ticket,out token));f.Task.ParticipantWithdraw();
                    Check.True(!f.Admission.TryCommitPresentation(token,42,8,trial.TrialId,trial.Stimulus.Word,trial.Stimulus.Ink,12.75)&&!trial.Onset.HasValue&&f.Admission.State!=0);}
            });
            suite.Run("loss of eye epoch before callback prevents trial onset",()=>{
                using(var f=new Fixture())
                {var ticket=f.StageFreshEye();f.DrainQueue();var trial=f.Task.CurrentTrial;ViveNamedSessionFrameAdmission.PresentationReservation token;
                    Check.True(Reserve(f,ticket,out token));f.SendEye(true);
                    Check.True(!Commit(f,token)&&!trial.Onset.HasValue&&f.Admission.State!=0);}
            });
            suite.Run("reservation is single-use and cannot publish a second onset",()=>{
                using(var f=new Fixture())
                {var ticket=f.StageFreshEye();f.DrainQueue();var trial=f.Task.CurrentTrial;ViveNamedSessionFrameAdmission.PresentationReservation token;
                    Check.True(Reserve(f,ticket,out token)&&Commit(f,token));double onset=trial.Onset.Value;
                    Check.True(!Commit(f,token)&&trial.Onset.Value==onset);}
            });
            suite.Run("missing render callback cancels without onset and stops admission",()=>{
                using(var f=new Fixture())
                {var ticket=f.StageFreshEye();f.DrainQueue();var trial=f.Task.CurrentTrial;ViveNamedSessionFrameAdmission.PresentationReservation token;
                    Check.True(Reserve(f,ticket,out token));f.Admission.CancelPresentation(token);
                    Check.True(!trial.Onset.HasValue&&f.Admission.State!=0);}
            });
            suite.Run("failure after onset stays unknown and cannot publish a valid event",()=>{
                using(var f=new Fixture())
                {var ticket=f.StageFreshEye();f.DrainQueue();var trial=f.Task.CurrentTrial;ViveNamedSessionFrameAdmission.PresentationReservation token;
                    Check.True(Reserve(f,ticket,out token));
                    Check.True(f.Task.SetSyntheticOnsetHookForFixture(f.Admission,()=>{throw new InvalidOperationException("fixture onset failed");}));
                    Check.True(!Commit(f,token)&&trial.Onset.HasValue&&f.Admission.State!=0&&f.Admission.Count==0);}
            });
            suite.Run("withdrawal during event publication does not report a successful commit",()=>{
                using(var f=new Fixture())
                {var ticket=f.StageFreshEye();f.DrainQueue();var trial=f.Task.CurrentTrial;ViveNamedSessionFrameAdmission.PresentationReservation token;
                    Check.True(Reserve(f,ticket,out token));f.Admission.AfterTailPublishedForTest=()=>f.Admission.Withdraw();
                    Check.True(!Commit(f,token)&&trial.Onset.HasValue&&f.Admission.State!=0);}
            });
            suite.Run("reservation does not block a consumer or explicit withdrawal",()=>{
                using(var f=new Fixture())
                {var ticket=f.StageFreshEye();f.DrainQueue();ViveNamedSessionFrameAdmission.PresentationReservation token;
                    Check.True(Reserve(f,ticket,out token));ViveNamedSessionFrameAdmission.ConsumerReceipt receipt;
                    Check.True(f.Admission.TryReserveConsumer(out receipt)&&receipt==null);
                    var stopped=new Thread(f.Admission.Withdraw){IsBackground=true};stopped.Start();
                    Check.True(stopped.Join(1000)&&f.Admission.State!=0);}
            });
        }
    }
}
#endif

