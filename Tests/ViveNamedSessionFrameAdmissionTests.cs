using System;
using System.Collections.Generic;
using System.Threading;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    public static class ViveNamedSessionFrameAdmissionTests
    {
        sealed class Client:IViveProEyeClient
        {
            internal readonly ManualResetEvent Ready=new ManualResetEvent(false), Release=new ManualResetEvent(false);
            internal bool BlockBeforeCapture;
            readonly MonotonicClock clock;
            internal Client(MonotonicClock clock){this.clock=clock;}
            public string Diagnostic {get{return "synthetic";}}
            public bool Start(){return true;}
            public ViveProEyePoll Poll()
            {
                if(BlockBeforeCapture){Ready.Set();Release.WaitOne(3000);}
                double at=clock.Now;
                var ray=new ViveEyeRay(2,false,true,new Vec3(0,0,0),new Vec3(.6,0,.8));
                var left=new ViveEyeRay(3,true,true,new Vec3(1,2,3),new Vec3(.1,.2,.3));
                var right=new ViveEyeRay(1,true,false,new Vec3(4,5,6),new Vec3(.4,.5,.6));
                var poll=new ViveProEyePoll(new ViveHeadFrame(1,at,true,true,Rotation.Identity),
                    new ViveEyeFrame(1,8,at,false,left,right,ray),ViveEyeRuntimeState.Working,ViveEyeCalibrationState.ReportedReady,true,0,2);
                if(!BlockBeforeCapture){Ready.Set();Release.WaitOne(3000);}return poll;
            }
            public void Dispose(){}
        }
        sealed class ControlledStatusClient:IViveProEyeClient
        {
            readonly Queue<ViveProEyePoll> values=new Queue<ViveProEyePoll>();
            internal readonly AutoResetEvent Waiting=new AutoResetEvent(false),Permit=new AutoResetEvent(false);
            public string Diagnostic {get{return "synthetic";}}
            public bool Start(){return true;}
            public ViveProEyePoll Poll()
            {Waiting.Set();if(!Permit.WaitOne(3000))throw new Exception("synthetic worker timeout");lock(values)return values.Dequeue();}
            internal void Send(ViveProEyePoll poll){lock(values)values.Enqueue(poll);Permit.Set();Check.True(Waiting.WaitOne(3000));}
            internal void ReleaseForStop(){lock(values)values.Enqueue(null);Permit.Set();}
            public void Dispose(){}
        }
        static ViveProEyePoll StatusEye(double observed,int frame,int status=2)
        {
            var ray=new ViveEyeRay(2,false,true,new Vec3(0,0,0),new Vec3(.6,0,.8));
            return new ViveProEyePoll(null,new ViveEyeFrame(frame,frame*8,observed,false,null,null,ray),
                ViveEyeRuntimeState.Working,ViveEyeCalibrationState.ReportedReady,true,0,status);
        }
        // Actual TryStage/Publish with a synthetic worker, never production I/O.
        // The result is evidence of an in-memory queue operation only.
        static void ExercisePublishedFrameAnchor(int scenario)
        {
            var clock=new MonotonicClock("synthetic-published-frame-"+scenario);var client=new ControlledStatusClient();
            var worker=new ViveProEyeWorker(clock,()=>new ViveHeadFrame(1,clock.Now,true,true,Rotation.Identity),()=>client);
            try
            {
                Check.True(worker.Start()&&client.Waiting.WaitOne(3000));var task=Participant(clock);
                var admission=ViveNamedSessionFrameAdmission.CreateSyntheticForTest(task,worker,clock,scenario==1?1:8);
                Check.True(admission.ConfirmTask()&&admission.ConfirmEye());
                ViveRawByteBudget budget=null;int headerBytes=0;
                if(scenario==6||scenario==7)
                {
                    var field=typeof(ViveNamedSessionFrameAdmission).GetField("manifest",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance);
                    var selected=(ViveNamedSessionFrameAdmission.Manifest)field.GetValue(admission);
                    headerBytes=ViveRawSessionJournalCodec.ManifestPayloadV3(selected).Length+48;
                    budget=ViveRawByteBudget.SelectExplicit(scenario==7?headerBytes+128+ViveRawByteBudget.TerminalFrameBytes:1048576);
                    Check.True(admission.TryBindQueueBudget(budget));Check.True(!admission.TryBindQueueBudget(budget));
                }
                object run;
                Check.True(admission.TryEnableWorkerStatus(4,out run));client.Send(null);
                double first=clock.Now+(scenario==5?10:0);client.Send(StatusEye(first,1));ViveProEyeWorker.StatusTicket baseline;
                Check.Equal(ViveProEyeWorker.StatusDisposition.Event,worker.TryReserveStatusEvidence(run,out baseline));
                ViveNamedSessionFrameAdmission.StatusAdmissionProof proof;
                Check.True(admission.TryStageStatus(baseline,out proof)&&worker.TryAcknowledgeStatusEvidence(baseline,proof));
                ViveProEyePoll poll;Check.True(worker.TryPollForRecording(out poll));ViveProEyeWorker.RecordingFrameTicket ticket;
                Check.Equal(ViveProEyeWorker.RecordingTicketDisposition.Issued,worker.TryIssueRecordingTicket(poll,out ticket));
                ViveDevelopmentParticipant.NamedRecordingTaskStamp stamp;Check.True(task.TryCaptureNamedRecordingTaskStamp(clock,out stamp));
                if(scenario==2)admission.AfterTailPublishedForTest=()=>admission.Withdraw();
                if(scenario==3)client.Send(StatusEye(clock.Now,1,3));
                if(scenario==4)Thread.Sleep(15);
                bool staged=admission.TryStage(stamp,ticket,scenario==4?.001:1);
                if(scenario!=0&&scenario!=6)
                {
                    Check.True(!staged&&admission.State!=0);
                    Check.Equal(ViveProEyeWorker.RecordingEpochResult.Changed,worker.TryCheckRecordingEpoch(ticket));
                    Check.Equal(0,admission.Count);return;
                }
                Check.True(staged);Check.Equal(ViveProEyeWorker.RecordingEpochResult.Current,worker.TryCheckRecordingEpoch(ticket));
                ViveNamedSessionFrameAdmission.ConsumerReceipt status,frame;
                Check.True(admission.TryReserveConsumer(out status)&&status.Record is ViveProEyeWorker.ObservedEyeStatus);
                if(scenario==6)
                {
                    var statusValue=(ViveProEyeWorker.ObservedEyeStatus)status.Record;
                    int statusBytes=ViveRawSessionJournalCodec.StatusPayloadV3(1,statusValue).Length+40;
                    Check.True(admission.VerifyQueuedFrameBudget(status,statusValue,budget,headerBytes+statusBytes,headerBytes));
                    Check.True(!admission.VerifyQueuedFrameBudget(status,statusValue,budget,headerBytes+129,headerBytes));
                    Check.True(!admission.VerifyQueuedFrameBudget(status,statusValue,ViveRawByteBudget.SelectExplicit(1048576),headerBytes+statusBytes,headerBytes));
                    Check.True(!admission.VerifyQueuedFrameBudget(status,statusValue,budget,headerBytes+statusBytes,headerBytes-1));
                }
                Check.True(admission.TryAcknowledgeSyntheticForTest(status));
                Check.True(admission.TryReserveConsumer(out frame));var actual=frame.Record as ViveNamedSessionFrameAdmission.Frame;
                Check.True(actual!=null);Check.Near(first,actual.EyeObserved);Check.Equal(1L,frame.Ordinal);
                if(scenario==6)
                {
                    ViveNamedSessionFrameAdmission.Manifest selected;Check.True(admission.TryGetManifestForReservedConsumer(frame,actual,out selected));
                    int bytes=ViveRawSessionJournalCodec.RecordPayload(2,actual,selected).Length+40;
                    Check.True(admission.VerifyQueuedFrameBudget(frame,actual,budget,bytes,0));
                    Check.True(!admission.VerifyQueuedFrameBudget(frame,actual,budget,353,0));
                }
                Check.True(admission.TryAcknowledgeSyntheticForTest(frame));Check.Equal(0,admission.Count);
                Thread.Sleep(2);double later=clock.Now;Check.True(later>first);client.Send(StatusEye(later,1));
                Check.True(worker.TryPollForRecording(out poll));ViveProEyeWorker.RecordingFrameTicket duplicate;
                Check.Equal(ViveProEyeWorker.RecordingTicketDisposition.Duplicate,worker.TryIssueRecordingTicket(poll,out duplicate));
                Check.True(duplicate==null);Check.Equal(0,admission.Count);
                // Poll invalidates the old epoch; never use it for task input.
                Check.Equal(ViveProEyeWorker.RecordingEpochResult.Changed,worker.TryCheckRecordingEpoch(ticket));
            }
            finally{worker.TryRequestStop();client.ReleaseForStop();Check.True(worker.Stop(1000));worker.Dispose();}
        }
        static ViveDevelopmentParticipant Participant(MonotonicClock clock)
        {
            var option=ViveNamedOptionProvenance.Select(Actor.Owner,ViveNamedOption.Automatic,1,"frame-geometry");
            var plan=ViveNamedSimulationPlan.Select(Actor.Owner,"frame-plan",option);
            var intent=ViveNamedRecordingIntent.Select(Actor.Owner,"frame-intent",true,true);
            var config=SessionConfiguration.NamedRecordingDevelopment("frame-session",37,plan,intent);
            var geometry=new AutomaticGazeGeometry(Rotation.Identity,new Vec3(0,0,1),new Rotation(0,Math.Sqrt(.5),0,Math.Sqrt(.5)),new Vec3(1,0,0),"frame-geometry");
            var map=new Dictionary<ViveTrackpadDirection,Colour>{{ViveTrackpadDirection.Up,Colour.Red},{ViveTrackpadDirection.Right,Colour.Green},
                {ViveTrackpadDirection.Down,Colour.Blue},{ViveTrackpadDirection.Left,Colour.Yellow}};
            return new ViveDevelopmentParticipant(config,clock,geometry,map,.5,true);
        }
        static int DrainEvents(ViveNamedSessionFrameAdmission admission)
        {
            int count=0;bool staged;
            do{Check.True(admission.TryStageNextTaskEvent(out staged));if(staged)count++;Check.True(count<15);}while(staged);
            var field=typeof(ViveNamedSessionFrameAdmission).GetField("ring",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance);
            var values=(object[])field.GetValue(admission);
            long previous=-1;
            for(int i=0;i<count;i++){var e=values[i] as JournalEvent;Check.True(e!=null);if(i>0)Check.Equal(previous+1,e.Sequence);previous=e.Sequence;}
            return count;
        }
        static void BeginPractice(ViveDevelopmentParticipant task)
        {
            task.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,false,0,0));
            // Synthetic queue mechanics only: this does not validate the
            // block-plan receipt, product start, hardware or recorded data.
            typeof(ViveDevelopmentParticipant).GetMethod("OwnerBeginPracticeCore",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)
                .Invoke(task,new object[]{Actor.Owner});
        }
        // Synthetic queue only: exercises the actual task Publish path, not
        // a real plan receipt, native file, participant or study recording.
        static void BudgetTaskPublication(bool enough)
        {
            var clock=new MonotonicClock("budget-task");var client=new Client(clock);
            var worker=new ViveProEyeWorker(clock,()=>new ViveHeadFrame(1,clock.Now,true,true,Rotation.Identity),()=>client);
            try
            {
                Check.True(worker.Start()&&client.Ready.WaitOne(3000));var task=Participant(clock);
                var admission=ViveNamedSessionFrameAdmission.CreateSyntheticForTest(task,worker,clock,16);
                Check.True(admission.ConfirmTask()&&admission.ConfirmEye());BeginPractice(task);
                var field=typeof(ViveNamedSessionFrameAdmission).GetField("manifest",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance);
                var manifest=(ViveNamedSessionFrameAdmission.Manifest)field.GetValue(admission);
                var events=task.ReadEventsAfter(manifest.TaskBaseline,1);Check.Equal(1,events.Count);int body;
                Check.True(ViveRawTaskFrameMeasurement.TryMeasure(1,events[0],manifest,1048616,out body));
                int header=ViveRawSessionJournalCodec.ManifestPayloadV3(manifest).Length+48;
                var budget=ViveRawByteBudget.SelectExplicit(header+body+ViveRawByteBudget.TerminalFrameBytes-(enough?0:1));
                Check.True(admission.TryBindQueueBudget(budget));bool staged;
                if(!enough){Check.True(!admission.TryStageNextTaskEvent(out staged));Check.Equal(3,admission.State);Check.Equal(0,admission.Count);return;}
                Check.True(admission.TryStageNextTaskEvent(out staged)&&staged);ViveNamedSessionFrameAdmission.ConsumerReceipt receipt;
                Check.True(admission.TryReserveConsumer(out receipt)&&receipt!=null&&receipt.Record is JournalEvent);
                var record=(JournalEvent)receipt.Record;int bytes=ViveRawSessionJournalCodec.RecordPayload(1,record,manifest).Length+40;
                Check.Equal(body,bytes);Check.True(admission.VerifyQueuedFrameBudget(receipt,record,budget,header+bytes,header));
                Check.True(!admission.VerifyQueuedFrameBudget(receipt,record,budget,header+bytes+1,header));
                Check.True(admission.TryAcknowledgeSyntheticForTest(receipt));
                Check.True(!admission.TryStageNextTaskEvent(out staged));Check.Equal(3,admission.State);
            }
            finally{client.Release.Set();worker.Stop(1000);worker.Dispose();}
        }
#if VIVE_SYNTHETIC_BATCH_FIXTURE
        static void CompetingTerminalReasons(bool delayWithdrawal)
        {
            var clock=new MonotonicClock("terminal-reason-race");var client=new Client(clock);
            var worker=new ViveProEyeWorker(clock,()=>new ViveHeadFrame(1,clock.Now,true,true,Rotation.Identity),()=>client);
            var entered=new ManualResetEvent(false);var release=new ManualResetEvent(false);Thread held=null;
            try
            {
                Check.True(worker.Start()&&client.Ready.WaitOne(3000));var task=Participant(clock);
                var admission=ViveNamedSessionFrameAdmission.CreateSyntheticForTest(task,worker,clock,2);
                Check.True(admission.ConfirmTask()&&admission.ConfirmEye());
                admission.AfterTerminalReasonForTest=bit=>{if(bit==(delayWithdrawal?4:8)){entered.Set();release.WaitOne(3000);}};
                held=new Thread(()=>{if(delayWithdrawal)admission.Withdraw();else admission.ConsumerFailed(null);});
                held.Start();Check.True(entered.WaitOne(3000));
                if(delayWithdrawal)admission.ConsumerFailed(null);else admission.Withdraw();
                Check.True(admission.IoFence.IsRevoked);Check.Equal(2,admission.State);
                Check.True(!admission.TryPublishSyntheticForTest(new object()));
                release.Set();Check.True(held.Join(3000));Check.Equal(2,admission.State);Check.Equal(0,admission.Count);
            }
            finally{release.Set();if(held!=null&&held.IsAlive)held.Join(3000);client.Release.Set();worker.Stop(1000);worker.Dispose();entered.Dispose();release.Dispose();}
        }
        static void ConsumerSlotCannotReinstallAfterDetach()
        {
            var clock=new MonotonicClock("consumer-slot-detach");var client=new Client(clock);
            var worker=new ViveProEyeWorker(clock,()=>new ViveHeadFrame(1,clock.Now,true,true,Rotation.Identity),()=>client);
            try
            {
                Check.True(worker.Start()&&client.Ready.WaitOne(3000));var task=Participant(clock);
                var admission=ViveNamedSessionFrameAdmission.CreateSyntheticForTest(task,worker,clock,2);
                Check.True(admission.ConfirmTask()&&admission.ConfirmEye());
                Check.True(admission.TryPublishSyntheticForTest(new object()));
                admission.BeforeConsumerReceiptPublishForTest=()=>admission.Withdraw();
                ViveNamedSessionFrameAdmission.ConsumerReceipt receipt;
                Check.True(!admission.TryReserveConsumer(out receipt)&&receipt==null);
                var type=typeof(ViveNamedSessionFrameAdmission);
                var slot=type.GetField("activeReceipt",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance).GetValue(admission);
                var revoked=type.GetField("ConsumerSlotRevoked",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Static).GetValue(null);
                Check.True(Object.ReferenceEquals(slot,revoked)&&admission.State==2&&admission.Count==0);
            }
            finally{client.Release.Set();worker.Stop(1000);worker.Dispose();}
        }
        static void StatusSlotCannotReinstallAfterDetach()
        {
            var clock=new MonotonicClock("status-slot-detach");var client=new ControlledStatusClient();
            var worker=new ViveProEyeWorker(clock,()=>new ViveHeadFrame(1,clock.Now,true,true,Rotation.Identity),()=>client);
            try
            {
                Check.True(worker.Start()&&client.Waiting.WaitOne(3000));var task=Participant(clock);
                var admission=ViveNamedSessionFrameAdmission.CreateSyntheticForTest(task,worker,clock,4);
                Check.True(admission.ConfirmTask()&&admission.ConfirmEye());object run;
                Check.True(admission.TryEnableWorkerStatus(4,out run));client.Send(null);client.Send(StatusEye(clock.Now,1));
                ViveProEyeWorker.StatusTicket ticket;
                Check.Equal(ViveProEyeWorker.StatusDisposition.Event,worker.TryReserveStatusEvidence(run,out ticket));
                admission.BeforeStatusProofPublishForTest=()=>admission.Withdraw();
                ViveNamedSessionFrameAdmission.StatusAdmissionProof proof;
                Check.True(!admission.TryStageStatus(ticket,out proof)&&proof==null);
                var type=typeof(ViveNamedSessionFrameAdmission);
                var slot=type.GetField("pendingStatusProof",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance).GetValue(admission);
                var revoked=type.GetField("StatusSlotRevoked",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Static).GetValue(null);
                Check.True(Object.ReferenceEquals(slot,revoked)&&admission.State==2&&admission.Count==0);
            }
            finally{worker.TryRequestStop();client.ReleaseForStop();worker.Stop(1000);worker.Dispose();}
        }
#endif
        public static void Register(TestSuite suite){Register(suite,true);}
        public static void Register(TestSuite suite,bool includeFileProbe)
        {
#if VIVE_SYNTHETIC_BATCH_FIXTURE
            suite.Run("admission fault cannot erase a concurrent withdrawal",()=>CompetingTerminalReasons(true));
            suite.Run("admission delayed fault cannot erase withdrawal",()=>CompetingTerminalReasons(false));
            suite.Run("detached consumer slot cannot be reinstalled",ConsumerSlotCannotReinstallAfterDetach);
            suite.Run("detached status slot cannot be reinstalled",StatusSlotCannotReinstallAfterDetach);
#endif
            suite.Run("synthetic frame FIFO publication differs from worker epoch",()=>ExercisePublishedFrameAnchor(0));
            suite.Run("full queue invalidates consumed frame instead of proving publish",()=>ExercisePublishedFrameAnchor(1));
            suite.Run("withdraw during frame publish invalidates the candidate anchor",()=>ExercisePublishedFrameAnchor(2));
            suite.Run("status revision before frame publish invalidates the candidate anchor",()=>ExercisePublishedFrameAnchor(3));
            suite.Run("expired local observation cannot be admitted as a frame",()=>ExercisePublishedFrameAnchor(4));
            suite.Run("future local observation cannot be admitted as a frame",()=>ExercisePublishedFrameAnchor(5));
            suite.Run("budget follows real synthetic status and eye FIFO and binds exact sink budget",()=>ExercisePublishedFrameAnchor(6));
            suite.Run("queued status consumes budget before writer and rejects over-budget eye",()=>ExercisePublishedFrameAnchor(7));
            suite.Run("budget checks actual task FIFO publication and does not refund on dequeue",()=>BudgetTaskPublication(true));
            suite.Run("budget refuses actual task FIFO publication when one byte short",()=>BudgetTaskPublication(false));
            suite.Run("named task event reads long prefix directly and rejects mismatched sequence",()=>{
                var clock=new MonotonicClock("long-event-clock");var task=Participant(clock);
                var experimentField=typeof(ViveDevelopmentParticipant).GetField("experiment",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance);
                var experiment=(Experiment)experimentField.GetValue(task);
                var eventsField=typeof(Experiment).GetField("events",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance);
                var events=(List<JournalEvent>)eventsField.GetValue(experiment);
                while(events.Count<10000){int i=events.Count;events.Add(new JournalEvent(i,0,clock.ClockId,"frame-session",-1,0,null,"synthetic",new Dictionary<string,string>()));}
                var page=task.ReadEventsAfter(9998,1);Check.Equal(1,page.Count);Check.Equal(9999L,page[0].Sequence);
                events[9999]=new JournalEvent(7,0,clock.ClockId,"frame-session",-1,0,null,"synthetic",new Dictionary<string,string>());
                Check.Throws<InvalidOperationException>(()=>task.ReadEventsAfter(9998,1));
            });
            suite.Run("named recording task stamp refuses a held authority gate",()=>{
                var clock=new MonotonicClock("held-gate-clock");var task=Participant(clock);
                var entered=new ManualResetEvent(false);var release=new ManualResetEvent(false);
                var thread=new Thread(()=>{lock(task.NamedRecordingGate){entered.Set();release.WaitOne(3000);}});
                try
                {
                    thread.Start();Check.True(entered.WaitOne(3000));
                    ViveDevelopmentParticipant.NamedRecordingTaskStamp stamp;
                    Check.True(!task.TryCaptureNamedRecordingTaskStamp(clock,out stamp));
                    Check.Throws<InvalidOperationException>(()=>task.ReadEventsAfter(-1,1));
                }
                finally{release.Set();Check.True(thread.Join(3000));entered.Dispose();release.Dispose();}
            });
            suite.Run("wrong worker does not consume another worker ticket",()=>{
                var clock=new MonotonicClock("other-frame-clock");
                var first=new ViveProEyeWorker(clock,()=>null,()=>new Client(clock));
                var second=new ViveProEyeWorker(clock,()=>null,()=>new Client(clock));
                try
                {
                    var ticket=new ViveProEyeWorker.RecordingFrameTicket(first,new object(),null,0,0);
                    ViveProEyePoll ignored;Check.True(!second.TryConsumeRecordingTicket(ticket,out ignored));Check.Equal(0,ticket.Consumed);
                }
                finally{first.Dispose();second.Dispose();}
            });
            suite.Run("frame admission accepts fresh synthetic frame only after both choices",()=>{
                var clock=new MonotonicClock("fresh-frame-clock");var client=new Client(clock){BlockBeforeCapture=true};
                var worker=new ViveProEyeWorker(clock,()=>new ViveHeadFrame(1,clock.Now,true,true,Rotation.Identity),()=>client);
                try
                {
                    Check.True(worker.Start());Check.True(client.Ready.WaitOne(3000));var task=Participant(clock);
                    var admission=ViveNamedSessionFrameAdmission.CreateSyntheticForTest(task,worker,clock,16);
                    Check.True(admission.ConfirmTask()&&admission.ConfirmEye());BeginPractice(task);client.Release.Set();
                    int events=DrainEvents(admission);Check.True(events>0&&admission.PreChoiceTaskPrefixUnknown);
                    var ringField=typeof(ViveNamedSessionFrameAdmission).GetField("ring",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance);
                    Check.True(((JournalEvent)((object[])ringField.GetValue(admission))[0]).Sequence>0);
                    Check.True(SpinWait.SpinUntil(()=>worker.CaptureDiagnostics.SuccessfulEyeReads>0,3000));
                    var poll=worker.Poll();ViveProEyeWorker.RecordingFrameTicket ticket;
                    Check.Equal(ViveProEyeWorker.RecordingTicketDisposition.Issued,worker.TryIssueRecordingTicket(poll,out ticket));
                    ViveDevelopmentParticipant.NamedRecordingTaskStamp stamp;Check.True(task.TryCaptureNamedRecordingTaskStamp(clock,out stamp));
                    Check.True(admission.TryStage(stamp,ticket,1));Check.Equal(events+1,admission.Count);Check.Equal(0,admission.State);
                    var captured=(ViveNamedSessionFrameAdmission.Frame)((object[])ringField.GetValue(admission))[events];
                    Check.Equal(3UL,captured.Left.Mask);Check.Equal(2.0,captured.Left.OriginMillimeters.Y);Check.True(captured.Left.DirectionValid);
                    Check.Equal(1UL,captured.Right.Mask);Check.True(!captured.Right.DirectionValid);Check.Equal((int?)0,captured.NativeResultCode);
                    Check.Equal((int?)2,captured.NativeRuntimeStatus);Check.True(captured.HeadDeviceConnected&&captured.HeadRotationValid&&!captured.NoUser);
                    ViveNamedSessionFrameAdmission.ConsumerReceipt receipt;Check.True(admission.TryReserveConsumer(out receipt)&&receipt!=null);
                    ViveNamedSessionFrameAdmission.Manifest manifest;Check.True(admission.TryGetManifestForReservedConsumer(receipt,receipt.Record,out manifest));
                    var payload=ViveRawSessionJournalCodec.ManifestPayload(manifest);byte[] digest;
                    var encoded=ViveRawSessionJournalCodec.Frame(payload,new byte[32],true,out digest);
                    Check.Equal("VRAW0002",System.Text.Encoding.ASCII.GetString(encoded,0,8));Check.Equal(32,digest.Length);
                    byte[] samplePayload=ViveRawSessionJournalCodec.RecordPayload(1,captured,manifest);
                    Check.True(samplePayload.Length>0);
                    Console.WriteLine("SYNTHETIC_VRAW2_EYE_RECORD_BYTES="+(samplePayload.Length+40));
                    var observed=(JournalEvent)receipt.Record;Check.True(ViveRawSessionJournalCodec.RecordPayload(1,observed,manifest).Length>0);
                    var altered=new Dictionary<string,string>();foreach(var pair in observed.Fields)altered.Add(pair.Key,pair.Value);
                    altered["named_plan_id"]="not-the-plan";
                    var forged=new JournalEvent(observed.Sequence,observed.Time,observed.ClockId,observed.SessionId,observed.BlockIndex,observed.Segment,
                        observed.TrialId,observed.Kind,altered,observed.NamedOption,observed.NamedPlan,observed.NamedBlock,observed.NamedRecordingIntent);
                    Check.Throws<InvalidOperationException>(()=>ViveRawSessionJournalCodec.RecordPayload(1,forged,manifest));
                    var originalFields=new Dictionary<string,string>();foreach(var pair in observed.Fields)originalFields.Add(pair.Key,pair.Value);
                    var emptyKind=new JournalEvent(observed.Sequence,observed.Time,observed.ClockId,observed.SessionId,observed.BlockIndex,observed.Segment,
                        observed.TrialId," ",originalFields,observed.NamedOption,observed.NamedPlan,observed.NamedBlock,observed.NamedRecordingIntent);
                    Check.Throws<InvalidOperationException>(()=>ViveRawSessionJournalCodec.RecordPayload(1,emptyKind,manifest));
                    var emptyKey=new Dictionary<string,string>(originalFields);emptyKey.Add(" ","synthetic");
                    var badKey=new JournalEvent(observed.Sequence,observed.Time,observed.ClockId,observed.SessionId,observed.BlockIndex,observed.Segment,
                        observed.TrialId,observed.Kind,emptyKey,observed.NamedOption,observed.NamedPlan,observed.NamedBlock,observed.NamedRecordingIntent);
                    Check.Throws<InvalidOperationException>(()=>ViveRawSessionJournalCodec.RecordPayload(1,badKey,manifest));
                    var syntheticRay=new ViveEyeRay(2,false,true,new Vec3(0,0,0),new Vec3(.6,0,.8));
                    var invalidPoll=new ViveProEyePoll(new ViveHeadFrame(1,captured.HeadObserved,true,true,Rotation.Identity),
                        new ViveEyeFrame(1,8,captured.EyeObserved,false,null,null,syntheticRay),(ViveEyeRuntimeState)99,ViveEyeCalibrationState.ReportedReady,true,0,2);
                    var invalidFrame=new ViveNamedSessionFrameAdmission.Frame(invalidPoll,stamp,0,captured.CheckedAt);
                    Check.Throws<InvalidOperationException>(()=>ViveRawSessionJournalCodec.RecordPayload(1,invalidFrame,manifest));
                    admission.Withdraw();Check.Equal(0,admission.Count);Check.Equal(2,admission.State);
                    Check.True(admission.SyntheticOnly&&!admission.PhysicalWriterAttached&&!admission.ResearchAdmission);
                }
                finally{client.Release.Set();worker.Stop(1000);worker.Dispose();}
            });
            suite.Run("frame admission rejects pre-choice cached eye and consumes ticket",()=>{
                var clock=new MonotonicClock("frame-clock");var client=new Client(clock);
                var worker=new ViveProEyeWorker(clock,()=>new ViveHeadFrame(1,clock.Now,true,true,Rotation.Identity),()=>client);
                try
                {
                    Check.True(worker.Start());Check.True(client.Ready.WaitOne(3000));
                    var task=Participant(clock);var admission=ViveNamedSessionFrameAdmission.CreateSyntheticForTest(task,worker,clock,16);
                    client.Release.Set();Check.True(SpinWait.SpinUntil(()=>worker.CaptureDiagnostics.SuccessfulEyeReads>0,3000));
                    var poll=worker.Poll();ViveProEyeWorker.RecordingFrameTicket ticket;
                    Check.Equal(ViveProEyeWorker.RecordingTicketDisposition.Issued,worker.TryIssueRecordingTicket(poll,out ticket));
                    Check.True(admission.ConfirmTask()&&admission.ConfirmEye());BeginPractice(task);
                    Check.True(DrainEvents(admission)>0);
                    ViveDevelopmentParticipant.NamedRecordingTaskStamp stamp;Check.True(task.TryCaptureNamedRecordingTaskStamp(clock,out stamp));
                    Check.True(!admission.TryStage(stamp,ticket,1));Check.Equal(3,admission.State);Check.Equal(0,admission.Count);
                    ViveProEyePoll ignored;Check.True(!worker.TryConsumeRecordingTicket(ticket,out ignored));
                }
                finally{client.Release.Set();worker.Stop(1000);worker.Dispose();}
            });
            suite.Run("frame admission withdrawal remains terminal and detaches",()=>{
                var clock=new MonotonicClock("withdraw-clock");var client=new Client(clock);
                var worker=new ViveProEyeWorker(clock,()=>new ViveHeadFrame(1,clock.Now,true,true,Rotation.Identity),()=>client);
                try
                {
                    Check.True(worker.Start());Check.True(client.Ready.WaitOne(3000));var task=Participant(clock);
                    var admission=ViveNamedSessionFrameAdmission.CreateSyntheticForTest(task,worker,clock,1);
                    Check.True(admission.ConfirmTask());admission.Withdraw();Check.True(!admission.ConfirmEye());
                    Check.Equal(2,admission.State);Check.Equal(0,admission.Count);Check.True(admission.SyntheticOnly&&!admission.PhysicalWriterAttached);
                }
                finally{client.Release.Set();worker.Stop(1000);worker.Dispose();}
            });
            suite.Run("participant withdrawal detaches admission before waiting on authority lock",()=>{
                var clock=new MonotonicClock("participant-withdraw-clock");var client=new Client(clock);
                var worker=new ViveProEyeWorker(clock,()=>new ViveHeadFrame(1,clock.Now,true,true,Rotation.Identity),()=>client);
                try
                {
                    Check.True(worker.Start());Check.True(client.Ready.WaitOne(3000));var task=Participant(clock);
                    var admission=ViveNamedSessionFrameAdmission.CreateSyntheticForTest(task,worker,clock,1);
                    Check.True(admission.ConfirmTask()&&admission.ConfirmEye());Thread thread;
                    lock(task.NamedRecordingGate)
                    {
                        thread=new Thread(task.ParticipantWithdraw);thread.Start();
                        Check.True(SpinWait.SpinUntil(()=>admission.State==2,3000));Check.Equal(0,admission.Count);
                        Check.True(task.NamedRawRevocation.IsRevoked&&admission.IoFence.IsRevoked);
                    }
                    Check.True(thread.Join(3000));Check.Equal(2,admission.State);
                }
                finally{client.Release.Set();worker.Stop(1000);worker.Dispose();}
            });
            suite.Run("frame admission refuses new worker run in same queue",()=>{
                var clock=new MonotonicClock("restart-clock");var client=new Client(clock);
                var worker=new ViveProEyeWorker(clock,()=>new ViveHeadFrame(1,clock.Now,true,true,Rotation.Identity),()=>client);
                try
                {
                    Check.True(worker.Start());Check.True(client.Ready.WaitOne(3000));var task=Participant(clock);
                    var admission=ViveNamedSessionFrameAdmission.CreateSyntheticForTest(task,worker,clock,1);
                    Check.True(admission.ConfirmTask()&&admission.ConfirmEye());client.Release.Set();Check.True(worker.Stop(1000));
                    Check.True(worker.Start());Check.True(SpinWait.SpinUntil(()=>worker.CaptureDiagnostics.SuccessfulEyeReads>0,3000));
                    var poll=worker.Poll();ViveProEyeWorker.RecordingFrameTicket ticket;
                    Check.Equal(ViveProEyeWorker.RecordingTicketDisposition.Issued,worker.TryIssueRecordingTicket(poll,out ticket));
                    ViveDevelopmentParticipant.NamedRecordingTaskStamp stamp;Check.True(task.TryCaptureNamedRecordingTaskStamp(clock,out stamp));
                    Check.True(!admission.TryStage(stamp,ticket,1));Check.Equal(3,admission.State);Check.Equal(0,admission.Count);
                }
                finally{client.Release.Set();worker.Stop(1000);worker.Dispose();}
            });
            suite.Run("full frame admission queue fails closed with unknown tail",()=>{
                var clock=new MonotonicClock("full-frame-clock");var client=new Client(clock){BlockBeforeCapture=true};
                var worker=new ViveProEyeWorker(clock,()=>new ViveHeadFrame(1,clock.Now,true,true,Rotation.Identity),()=>client);
                try
                {
                    Check.True(worker.Start());Check.True(client.Ready.WaitOne(3000));var task=Participant(clock);
                    var admission=ViveNamedSessionFrameAdmission.CreateSyntheticForTest(task,worker,clock,1);
                    Check.True(admission.ConfirmTask()&&admission.ConfirmEye());BeginPractice(task);bool staged;
                    Check.True(admission.TryStageNextTaskEvent(out staged)&&staged);client.Release.Set();
                    Check.True(SpinWait.SpinUntil(()=>worker.CaptureDiagnostics.SuccessfulEyeReads>0,3000));
                    var poll=worker.Poll();ViveProEyeWorker.RecordingFrameTicket ticket;
                    Check.Equal(ViveProEyeWorker.RecordingTicketDisposition.Issued,worker.TryIssueRecordingTicket(poll,out ticket));
                    ViveDevelopmentParticipant.NamedRecordingTaskStamp stamp;Check.True(task.TryCaptureNamedRecordingTaskStamp(clock,out stamp));
                    Check.True(!admission.TryStage(stamp,ticket,1));Check.Equal(3,admission.State);Check.Equal(0,admission.Count);
                    ViveProEyePoll ignored;Check.True(!worker.TryConsumeRecordingTicket(ticket,out ignored));
                }
                finally{client.Release.Set();worker.Stop(1000);worker.Dispose();}
            });
            suite.Run("consumer acknowledges FIFO once and withdraw invalidates reserved head",()=>{
                var clock=new MonotonicClock("fifo-clock");var client=new Client(clock){BlockBeforeCapture=true};
                var worker=new ViveProEyeWorker(clock,()=>new ViveHeadFrame(1,clock.Now,true,true,Rotation.Identity),()=>client);
                try
                {
                    Check.True(worker.Start());Check.True(client.Ready.WaitOne(3000));var task=Participant(clock);
                    var admission=ViveNamedSessionFrameAdmission.CreateSyntheticForTest(task,worker,clock,16);
                    Check.True(admission.ConfirmTask()&&admission.ConfirmEye());BeginPractice(task);int events=DrainEvents(admission);Check.True(events>0);
                    ViveNamedSessionFrameAdmission.ConsumerReceipt first;Check.True(admission.TryReserveConsumer(out first)&&first!=null);
                    Check.Equal(0L,first.Ordinal);Check.True(first.Record is JournalEvent);client.Release.Set();
                    Check.True(SpinWait.SpinUntil(()=>worker.CaptureDiagnostics.SuccessfulEyeReads>0,3000));
                    var poll=worker.Poll();ViveProEyeWorker.RecordingFrameTicket ticket;
                    Check.Equal(ViveProEyeWorker.RecordingTicketDisposition.Issued,worker.TryIssueRecordingTicket(poll,out ticket));
                    ViveDevelopmentParticipant.NamedRecordingTaskStamp stamp;Check.True(task.TryCaptureNamedRecordingTaskStamp(clock,out stamp));
                    Check.True(admission.TryStage(stamp,ticket,1));Check.Equal(events+1,admission.Count);
                    Check.True(admission.TryAcknowledgeSyntheticForTest(first));Check.True(!admission.TryAcknowledgeSyntheticForTest(first));
                    ViveNamedSessionFrameAdmission.ConsumerReceipt next;Check.True(admission.TryReserveConsumer(out next)&&next!=null);
                    Check.Equal(1L,next.Ordinal);admission.Withdraw();Check.True(next.Record==null);Check.True(!admission.TryAcknowledgeSyntheticForTest(next));
                    Check.Equal(2,admission.State);Check.Equal(0,admission.Count);
                }
                finally{client.Release.Set();worker.Stop(1000);worker.Dispose();}
            });
            suite.Run("consumer ring wraps without reordering event ordinals",()=>{
                var clock=new MonotonicClock("ring-wrap-clock");var client=new Client(clock);
                var worker=new ViveProEyeWorker(clock,()=>new ViveHeadFrame(1,clock.Now,true,true,Rotation.Identity),()=>client);
                try
                {
                    Check.True(worker.Start());Check.True(client.Ready.WaitOne(3000));var task=Participant(clock);
                    var admission=ViveNamedSessionFrameAdmission.CreateSyntheticForTest(task,worker,clock,2);
                    Check.True(admission.ConfirmTask()&&admission.ConfirmEye());BeginPractice(task);task.MarkPresented(task.CurrentTrial.TrialId);
                    long sequence=-1;
                    for(int i=0;i<3;i++)
                    {
                        bool staged;Check.True(admission.TryStageNextTaskEvent(out staged)&&staged);
                        ViveNamedSessionFrameAdmission.ConsumerReceipt receipt;Check.True(admission.TryReserveConsumer(out receipt)&&receipt!=null);
                        Check.Equal((long)i,receipt.Ordinal);var item=receipt.Record as JournalEvent;Check.True(item!=null);
                        if(i>0)Check.Equal(sequence+1,item.Sequence);sequence=item.Sequence;
                        Check.True(admission.TryAcknowledgeSyntheticForTest(receipt));Check.Equal(0,admission.Count);
                    }
                }
                finally{client.Release.Set();worker.Stop(1000);worker.Dispose();}
            });
            suite.Run("unissued raw sink proof cannot acknowledge a queued event",()=>{
                var clock=new MonotonicClock("proof-clock");var client=new Client(clock);
                var worker=new ViveProEyeWorker(clock,()=>new ViveHeadFrame(1,clock.Now,true,true,Rotation.Identity),()=>client);
                try
                {
                    Check.True(worker.Start());Check.True(client.Ready.WaitOne(3000));var task=Participant(clock);
                    var admission=ViveNamedSessionFrameAdmission.CreateSyntheticForTest(task,worker,clock,4);
                    Check.True(admission.ConfirmTask()&&admission.ConfirmEye());BeginPractice(task);bool staged;
                    Check.True(admission.TryStageNextTaskEvent(out staged)&&staged);
                    ViveNamedSessionFrameAdmission.ConsumerReceipt receipt;Check.True(admission.TryReserveConsumer(out receipt)&&receipt!=null);
                    string unused=System.IO.Path.GetFullPath(System.IO.Path.Combine("build","synthetic-never-opened.vraw"));
                    var sink=new ViveRawSessionJournalSink(admission,unused);
                    var forged=new ViveRawSessionJournalSink.WriteProof(sink,receipt,receipt.Record,receipt.Ordinal);
                    Check.True(!admission.TryAcknowledgeConsumer(receipt,forged));Check.Equal(3,admission.State);
                }
                finally{client.Release.Set();worker.Stop(1000);worker.Dispose();}
            });
            suite.Run("closed raw sink rejects a synthetic reservation before I/O",()=>{
                var clock=new MonotonicClock("closed-sink-memory-clock");var client=new Client(clock);
                var worker=new ViveProEyeWorker(clock,()=>new ViveHeadFrame(1,clock.Now,true,true,Rotation.Identity),()=>client);
                try
                {
                    Check.True(worker.Start()&&client.Ready.WaitOne(3000));var task=Participant(clock);
                    var admission=ViveNamedSessionFrameAdmission.CreateSyntheticForTest(task,worker,clock,4);
                    Check.True(admission.ConfirmTask()&&admission.ConfirmEye());BeginPractice(task);bool staged;
                    Check.True(admission.TryStageNextTaskEvent(out staged)&&staged);
                    ViveNamedSessionFrameAdmission.ConsumerReceipt receipt;Check.True(admission.TryReserveConsumer(out receipt)&&receipt!=null);
                    var sink=new ViveRawSessionJournalSink(admission,@"C:\synthetic-not-opened\raw.vraw");bool result=true;
                    var thread=new Thread(()=>result=sink.AppendOne(receipt));thread.Start();Check.True(thread.Join(3000));
                    Check.True(!result&&!sink.ProductionGateOpen);Check.Equal(3,admission.State);
                }
                finally{client.Release.Set();worker.Stop(1000);worker.Dispose();}
            });
            if(includeFileProbe)suite.Run("closed raw sink cannot create a file",()=>{
                var clock=new MonotonicClock("closed-sink-clock");var client=new Client(clock);
                var worker=new ViveProEyeWorker(clock,()=>new ViveHeadFrame(1,clock.Now,true,true,Rotation.Identity),()=>client);
                try
                {
                    Check.True(worker.Start());Check.True(client.Ready.WaitOne(3000));var task=Participant(clock);
                    var admission=ViveNamedSessionFrameAdmission.CreateSyntheticForTest(task,worker,clock,4);
                    Check.True(admission.ConfirmTask()&&admission.ConfirmEye());BeginPractice(task);bool staged;
                    Check.True(admission.TryStageNextTaskEvent(out staged)&&staged);
                    ViveNamedSessionFrameAdmission.ConsumerReceipt receipt;Check.True(admission.TryReserveConsumer(out receipt)&&receipt!=null);
                    string unused=System.IO.Path.GetFullPath(System.IO.Path.Combine("build","closed-"+Guid.NewGuid().ToString("N")+".vraw"));
                    Check.True(!System.IO.File.Exists(unused));var sink=new ViveRawSessionJournalSink(admission,unused);bool result=true;
                    var thread=new Thread(()=>result=sink.AppendOne(receipt));thread.Start();Check.True(thread.Join(3000));
                    Check.True(!result&&!sink.ProductionGateOpen&&!System.IO.File.Exists(unused));Check.Equal(3,admission.State);
                }
                finally{client.Release.Set();worker.Stop(1000);worker.Dispose();}
            });
        }
    }
}
