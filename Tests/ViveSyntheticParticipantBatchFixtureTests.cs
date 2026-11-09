#if VIVE_SYNTHETIC_BATCH_FIXTURE
using System;
using System.Collections.Generic;
using System.Threading;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    // Fixture-only integration: actual simulation participant and SPSC queue,
    // synthetic provider/acknowledgement, no camera, real data or file I/O.
    public static class ViveSyntheticParticipantBatchFixtureTests
    {
        static void CheckRevokedFixtureSlot(ViveNamedSessionFrameAdmission admission,string name)
        {
            var type=typeof(ViveNamedSessionFrameAdmission);
            var sentinel=type.GetField("SyntheticFixtureRevoked",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Static).GetValue(null);
            var slot=type.GetField(name,System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance).GetValue(admission);
            Check.True(Object.ReferenceEquals(sentinel,slot));
        }
        sealed class Client:IViveProEyeClient
        {
            readonly Queue<ViveProEyePoll> values=new Queue<ViveProEyePoll>();
            internal readonly AutoResetEvent Waiting=new AutoResetEvent(false),Permit=new AutoResetEvent(false);
            public string Diagnostic {get{return "synthetic";}}public bool Start(){return true;}
            public ViveProEyePoll Poll(){Waiting.Set();if(!Permit.WaitOne(3000))throw new Exception("fixture timeout");lock(values)return values.Dequeue();}
            internal void Send(ViveProEyePoll value){lock(values)values.Enqueue(value);Permit.Set();Check.True(Waiting.WaitOne(3000));}
            internal void ReleaseForStop(){lock(values)values.Enqueue(null);Permit.Set();}
            public void Dispose(){}
        }
        sealed class Fixture:IDisposable
        {
            internal readonly MonotonicClock Clock=new MonotonicClock("synthetic-participant-batch-clock");
            internal readonly Client Source=new Client();internal readonly ViveProEyeWorker Worker;
            internal readonly ViveDevelopmentParticipant Task;internal readonly ViveNamedSessionFrameAdmission Admission;
            int frame;
            internal Fixture(bool withdrawBeforePlanProof=false)
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
                Check.True(Admission.ConfirmTask()&&Admission.ConfirmEye());object run;
                Check.True(Admission.TryEnableWorkerStatus(8,out run));Source.Send(null);SendEye();
                ViveProEyeWorker.StatusTicket status;Check.Equal(ViveProEyeWorker.StatusDisposition.Event,Worker.TryReserveStatusEvidence(run,out status));
                ViveNamedSessionFrameAdmission.StatusAdmissionProof statusProof;
                Check.True(Admission.TryStageStatus(status,out statusProof)&&Worker.TryAcknowledgeStatusEvidence(status,statusProof));
                Task.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,false,0,0));
                var planEvent=Task.PrepareNamedRecordingBlockPlan(Actor.Owner,true);DrainTask();
                ViveBlockPlanRequest request;Check.True(ViveBlockPlanRequest.TryCapture(Task,Clock,planEvent,out request));
                ViveNamedSessionFrameAdmission.SyntheticPlanProof planProof;
                if(withdrawBeforePlanProof)
                {
                    Admission.BeforeSyntheticPlanProofPublishForTest=()=>{var t=new Thread(Admission.Withdraw){IsBackground=true};t.Start();Check.True(t.Join(1000));};
                    Check.True(!Admission.TryIssueSyntheticPlanProof(request,planEvent,out planProof)&&planProof==null);Check.Equal(2,Admission.State);return;
                }
                Check.True(Admission.TryIssueSyntheticPlanProof(request,planEvent,out planProof));
                Check.True(Task.TryBeginSyntheticPracticeForFixture(Admission,request,planProof));
                Check.True(!Task.TryBeginSyntheticPracticeForFixture(Admission,request,planProof));DrainTask();
            }
            void SendEye()
            {
                var ray=new ViveEyeRay(2,false,true,new Vec3(0,0,0),new Vec3(.6,0,.8));int next=++frame;
                Source.Send(new ViveProEyePoll(null,new ViveEyeFrame(next,next*8,Clock.Now,false,null,null,ray),
                    ViveEyeRuntimeState.Working,ViveEyeCalibrationState.ReportedReady,true,0,2));
            }
            internal void InvalidateEye()
            {
                var ray=new ViveEyeRay(2,false,true,new Vec3(0,0,0),new Vec3(.6,0,.8));int next=++frame;
                Source.Send(new ViveProEyePoll(null,new ViveEyeFrame(next,next*8,Clock.Now,true,null,null,ray),
                    ViveEyeRuntimeState.Working,ViveEyeCalibrationState.ReportedReady,true,0,2));
            }
            internal void DrainTask(){bool staged;do{Check.True(Admission.TryStageNextTaskEvent(out staged));}while(staged);}
            internal bool TryAnchor(out ViveNamedSessionFrameAdmission.SyntheticFrameAnchor anchor,double maximumAge=1)
            {
                SendEye();ViveProEyePoll poll;Check.True(Worker.TryPollForRecording(out poll));
                ViveProEyeWorker.RecordingFrameTicket ticket;Check.Equal(ViveProEyeWorker.RecordingTicketDisposition.Issued,Worker.TryIssueRecordingTicket(poll,out ticket));
                ViveDevelopmentParticipant.NamedRecordingTaskStamp stamp;Check.True(Task.TryCaptureNamedRecordingTaskStamp(Clock,out stamp));
                return Admission.TryStageSyntheticFrameWithAnchor(stamp,ticket,maximumAge,out anchor);
            }
            internal ViveNamedSessionFrameAdmission.SyntheticFrameAnchor Anchor(double maximumAge=1)
            {ViveNamedSessionFrameAdmission.SyntheticFrameAnchor anchor;Check.True(TryAnchor(out anchor,maximumAge));return anchor;}
            internal void DrainQueue()
            {ViveNamedSessionFrameAdmission.ConsumerReceipt receipt;while(Admission.TryReserveConsumer(out receipt)&&receipt!=null)Check.True(Admission.TryAcknowledgeSyntheticForTest(receipt));}
            public void Dispose(){Admission.Withdraw();Worker.TryRequestStop();Source.ReleaseForStop();Check.True(Worker.Stop(1000));Worker.Dispose();}
        }
        public static void Register(TestSuite suite)
        {
            suite.Run("fixture binds real participant onset and one event to a published batch",()=>{
                using(var f=new Fixture())
                {
                    var trial=f.Task.CurrentTrial;var anchor=f.Anchor();ViveNamedSessionFrameAdmission.SyntheticPresentationRecord record;
                    Check.True(f.Admission.TrySyntheticMarkPresented(anchor,trial.TrialId,null,out record));
                    Check.True(record!=null&&record.Event.Kind=="stimulus_presented"&&record.ClockId==f.Clock.ClockId);
                    Check.Near(trial.Onset.Value,record.Onset);Check.True(record.Event.Time>=record.Onset);
                    bool staged;Check.True(f.Admission.TryStageNextTaskEvent(out staged)&&!staged);
                    var next=f.Anchor();ViveNamedSessionFrameAdmission.SyntheticPresentationRecord repeated;
                    Check.True(f.Admission.TrySyntheticMarkPresented(next,trial.TrialId,null,out repeated)&&repeated==null);
                    Check.True(f.Admission.TryStageNextTaskEvent(out staged)&&!staged);
                    bool found=false;ViveNamedSessionFrameAdmission.ConsumerReceipt receipt;
                    while(f.Admission.TryReserveConsumer(out receipt)&&receipt!=null)
                    {if(Object.ReferenceEquals(receipt.Record,record))found=true;Check.True(f.Admission.TryAcknowledgeSyntheticForTest(receipt));}
                    Check.True(found);
                }
            });
            suite.Run("fixture rejects old cursor before another participant mutation",()=>{
                using(var f=new Fixture())
                {var anchor=f.Anchor();var trial=f.Task.CurrentTrial;f.Task.MarkPresented(trial.TrialId);
                    ViveNamedSessionFrameAdmission.SyntheticPresentationRecord record;
                    Check.True(!f.Admission.TrySyntheticMarkPresented(anchor,trial.TrialId,null,out record)&&record==null&&f.Admission.State!=0);}
            });
            suite.Run("fixture reports unknown when action throws after onset but before event",()=>{
                using(var f=new Fixture())
                {var anchor=f.Anchor();var trial=f.Task.CurrentTrial;
                    Check.True(f.Task.SetSyntheticOnsetHookForFixture(f.Admission,()=>{throw new InvalidOperationException("fixture onset failure");}));
                    ViveNamedSessionFrameAdmission.SyntheticPresentationRecord record;
                    Check.True(!f.Admission.TrySyntheticMarkPresented(anchor,trial.TrialId,null,out record)&&record==null&&trial.Onset.HasValue&&f.Admission.State!=0);}
            });
            suite.Run("fixture gives reentrant safety priority and marks batch unknown",()=>{
                using(var f=new Fixture())
                {var anchor=f.Anchor();var trial=f.Task.CurrentTrial;
                    Check.True(f.Task.SetSyntheticOnsetHookForFixture(f.Admission,()=>f.Task.ParticipantPause()));
                    ViveNamedSessionFrameAdmission.SyntheticPresentationRecord record;
                    Check.True(!f.Admission.TrySyntheticMarkPresented(anchor,trial.TrialId,null,out record)&&record==null&&f.Admission.State!=0);
                    Check.Equal(Phase.Paused,f.Task.Phase);}
            });
            suite.Run("fixture withdrawal during batch publish makes outcome unknown",()=>{
                using(var f=new Fixture())
                {var anchor=f.Anchor();var trial=f.Task.CurrentTrial;f.Admission.AfterTailPublishedForTest=()=>f.Admission.Withdraw();
                    ViveNamedSessionFrameAdmission.SyntheticPresentationRecord record;
                    Check.True(!f.Admission.TrySyntheticMarkPresented(anchor,trial.TrialId,null,out record)&&record==null&&f.Admission.State!=0);}
            });
            suite.Run("fixture withdrawal before plan proof publish cannot reinstall proof",()=>{
                using(var f=new Fixture(true))
                {Check.Equal(Phase.Preparation,f.Task.Phase);Check.Equal(2,f.Admission.State);CheckRevokedFixtureSlot(f.Admission,"syntheticPlanProof");}
            });
            suite.Run("fixture withdrawal before frame anchor publish cannot reinstall anchor",()=>{
                using(var f=new Fixture())
                {f.Admission.BeforeSyntheticFrameAnchorPublishForTest=()=>{var t=new Thread(f.Admission.Withdraw){IsBackground=true};t.Start();Check.True(t.Join(1000));};
                    ViveNamedSessionFrameAdmission.SyntheticFrameAnchor anchor;Check.True(!f.TryAnchor(out anchor)&&anchor==null);Check.Equal(2,f.Admission.State);
                    CheckRevokedFixtureSlot(f.Admission,"syntheticFrameAnchor");}
            });
            suite.Run("fixture consumed batch before later withdraw is unknown not rolled back",()=>{
                using(var f=new Fixture())
                {
                    var anchor=f.Anchor();var trial=f.Task.CurrentTrial;f.DrainQueue();object seen=null;ViveNamedSessionFrameAdmission.ConsumerReceipt claimed=null;
                    f.Admission.AfterSyntheticPresentationBatchCommitForTest=()=>{
                        var t=new Thread(()=>{ViveNamedSessionFrameAdmission.ConsumerReceipt item;
                            if(f.Admission.TryReserveConsumer(out item)&&item!=null){claimed=item;seen=item.Record;f.Admission.TryAcknowledgeSyntheticForTest(item);}f.Admission.Withdraw();}){IsBackground=true};
                        t.Start();Check.True(t.Join(1000));
                    };
                    ViveNamedSessionFrameAdmission.SyntheticPresentationRecord record;
                    Check.True(!f.Admission.TrySyntheticMarkPresented(anchor,trial.TrialId,null,out record)&&record==null);
                    Check.True(seen is ViveNamedSessionFrameAdmission.SyntheticPresentationRecord&&claimed!=null&&!f.Admission.TryAcknowledgeSyntheticForTest(claimed));
                    Check.Equal(2,f.Admission.State);Check.Equal(Phase.Practice,f.Task.Phase);
                    // Admission failure is not proof that participant safety pause ran.
                }
            });
            suite.Run("fixture expired published observation cannot authorize participant mutation",()=>{
                using(var f=new Fixture())
                {var anchor=f.Anchor(.25);var trial=f.Task.CurrentTrial;Thread.Sleep(350);
                    ViveNamedSessionFrameAdmission.SyntheticPresentationRecord record;
                    Check.True(!f.Admission.TrySyntheticMarkPresented(anchor,trial.TrialId,null,out record)&&record==null&&!trial.Onset.HasValue&&f.Admission.State!=0);}
            });
            suite.Run("fixture status loss after frame publication blocks participant mutation",()=>{
                using(var f=new Fixture())
                {var anchor=f.Anchor();var trial=f.Task.CurrentTrial;f.InvalidateEye();
                    ViveNamedSessionFrameAdmission.SyntheticPresentationRecord record;
                    Check.True(!f.Admission.TrySyntheticMarkPresented(anchor,trial.TrialId,null,out record)&&record==null&&!trial.Onset.HasValue&&f.Admission.State!=0);}
            });
        }
    }
}
#endif
