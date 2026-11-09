#if VIVE_RAW_SYNTHETIC_TEST && VIVE_SYNTHETIC_BATCH_FIXTURE && !UNITY_5_3_OR_NEWER
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    // Managed synthetic observations only. No native factories, files, SDK,
    // Unity, participant data, supplied write-success or shortened protocol.
    public static class ViveLegacyRawLifecycleTests
    {
        sealed class Client:IViveProEyeClient
        {
            readonly Queue<ViveProEyePoll> values=new Queue<ViveProEyePoll>();
            internal readonly AutoResetEvent Waiting=new AutoResetEvent(false),Permit=new AutoResetEvent(false);
            public string Diagnostic {get{return "legacy-memory-synthetic";}}
            public bool Start(){return true;}
            public ViveProEyePoll Poll(){Waiting.Set();if(!Permit.WaitOne(180000))throw new Exception("synthetic poll timeout");lock(values)return values.Dequeue();}
            internal void Send(ViveProEyePoll value){lock(values)values.Enqueue(value);Permit.Set();Check.True(Waiting.WaitOne(3000));}
            internal void Release(){lock(values)values.Enqueue(null);Permit.Set();}
            public void Dispose(){}
        }
        static ViveDevelopmentParticipant NewTask(MonotonicClock clock)
        {
            var geometry=new AutomaticGazeGeometry(Rotation.Identity,new Vec3(0,0,1),new Rotation(0,Math.Sqrt(.5),0,Math.Sqrt(.5)),new Vec3(1,0,0),"legacy-memory-geometry");
            var allocation=new Allocation("legacy-memory-allocation",new[]{Condition.B,Condition.C,Condition.A,Condition.D},GazeTarget.Participant,new[]{0,1,2,3});
            var design=ViveLegacyRecordingDesign.Select(Actor.Owner,"legacy-memory-design",allocation,geometry.Version,ViveFixedTrackpadMapping.Version);
            var config=SessionConfiguration.LegacyRecordingDevelopment(new SessionConfiguration("legacy-memory-session",73,allocation,RunKind.Simulation,"en",3,30,true,0,true,true,true),
                design,ViveLegacyRecordingIntent.Select(Actor.Owner,"legacy-memory-intent",true,true));
            return new ViveDevelopmentParticipant(config,clock,geometry,ViveFixedTrackpadMapping.Create(),.5,true);
        }
        sealed class Fixture:IDisposable
        {
            internal readonly MonotonicClock Clock=new MonotonicClock("legacy-memory-clock");
            internal readonly Client Source=new Client();internal readonly ViveProEyeWorker Worker;
            internal readonly ViveDevelopmentParticipant Task;internal readonly ViveNamedSessionFrameAdmission Admission;
            internal readonly MemoryStream Bytes=new MemoryStream();internal readonly List<JournalEvent> Events=new List<JournalEvent>();
            internal readonly List<ViveNamedSessionFrameAdmission.Frame> Eyes=new List<ViveNamedSessionFrameAdmission.Frame>();
            internal readonly double TaskChoice,EyeChoice;readonly object run;byte[] digest=new byte[32];long ordinal,statuses;int frame;
            internal Fixture(int capacity=256,long budget=33554432,bool pumpMode=false)
            {
                Task=NewTask(Clock);TaskChoice=Clock.Now;EyeChoice=Clock.Now;
                Worker=new ViveProEyeWorker(Clock,()=>new ViveHeadFrame(frame,Clock.Now,true,true,Rotation.Identity),()=>Source);
                Check.True(Worker.Start()&&Source.Waiting.WaitOne(3000));
                Admission=ViveNamedSessionFrameAdmission.CreateLegacySyntheticForTest(Task,Worker,Clock,capacity,TaskChoice,EyeChoice);
                Check.True(Admission.ConfirmTask()&&Admission.ConfirmEye());
                Check.True(Admission.TryBindQueueBudget(ViveRawByteBudget.SelectExplicit(budget)));
                if(!pumpMode){Check.True(Admission.TryEnableWorkerStatus(128,out run));Source.Send(null);SendEye();DrainStatus();DrainQueue();}
            }
            internal void SendEye(bool invalid=false)
            {
                var ray=new ViveEyeRay(2,false,true,new Vec3(0,0,0),new Vec3(.6,0,.8));int next=++frame;
                var left=new ViveEyeRay(3,true,true,new Vec3(-31.25,2.5,3.75),new Vec3(.3,.4,.5));
                var right=new ViveEyeRay(3,true,true,new Vec3(32.125,-2.25,4.5),new Vec3(-.25,.125,.875));
                Source.Send(new ViveProEyePoll(null,new ViveEyeFrame(next,next*8,Clock.Now,invalid,left,right,ray),ViveEyeRuntimeState.Working,ViveEyeCalibrationState.ReportedReady,true,0,2));
            }
            internal ViveProEyeWorker.RecordingFrameTicket Ticket()
            {SendEye();ViveProEyePoll poll;Check.True(Worker.TryPollForRecording(out poll));ViveProEyeWorker.RecordingFrameTicket ticket;
                Check.Equal(ViveProEyeWorker.RecordingTicketDisposition.Issued,Worker.TryIssueRecordingTicket(poll,out ticket));return ticket;}
            internal ViveProEyeWorker.RecordingFrameTicket QueueEye()
            {var ticket=Ticket();ViveDevelopmentParticipant.NamedRecordingTaskStamp stamp;Check.True(Task.TryCaptureLegacyRecordingTaskStamp(Clock,out stamp));Check.True(Admission.TryStage(stamp,ticket,1));return ticket;}
            void Append(byte[] payload,bool first=false)
            {byte[] next;var encoded=ViveRawSessionJournalCodec.FrameV4(payload,digest,first,out next);Bytes.Write(encoded,0,encoded.Length);digest=next;}
            internal void DrainQueue()
            {
                ViveNamedSessionFrameAdmission.ConsumerReceipt receipt;
                while(Admission.TryReserveConsumer(out receipt)&&receipt!=null)
                {
                    object record=receipt.Record;ViveNamedSessionFrameAdmission.Manifest manifest;
                    Check.True(Admission.TryGetManifestForReservedConsumer(receipt,record,out manifest));
                    if(Bytes.Length==0)Append(ViveRawSessionJournalCodec.ManifestPayloadV4(manifest),true);
                    Check.Equal(ordinal,receipt.Ordinal);var status=record as ViveProEyeWorker.ObservedEyeStatus;
                    Append(status==null?ViveRawSessionJournalCodec.RecordPayload(++ordinal,record,manifest):ViveRawSessionJournalCodec.StatusPayloadV3(++ordinal,status));
                    if(status!=null)statuses++;var task=record as JournalEvent;if(task!=null)Events.Add(task);
                    var eye=record as ViveNamedSessionFrameAdmission.Frame;if(eye!=null)Eyes.Add(eye);
                    Check.True(Admission.TryAcknowledgeSyntheticForTest(receipt));
                }
            }
            internal void DrainStatus()
            {
                for(int i=0;i<128;i++)
                {ViveProEyeWorker.StatusTicket ticket;var result=Worker.TryReserveStatusEvidence(run,out ticket);
                    if(result==ViveProEyeWorker.StatusDisposition.Empty)return;
                    Check.Equal(ViveProEyeWorker.StatusDisposition.Event,result);ViveNamedSessionFrameAdmission.StatusAdmissionProof proof;
                    Check.True(Admission.TryStageStatus(ticket,out proof)&&Worker.TryAcknowledgeStatusEvidence(ticket,proof));}
                throw new Exception("status bound");
            }
            internal void Mutate(Action action){Check.True(Admission.TryRunReservedOrdinaryMutation(action));DrainQueue();}
            internal void Begin(bool practice)
            {
                JournalEvent prepared=null;Mutate(()=>prepared=Task.PrepareLegacyRecordingBlockPlan(Actor.Owner,practice));
                ViveBlockPlanRequest request;Check.True(ViveBlockPlanRequest.TryCapture(Task,Clock,prepared,out request));
                ViveNamedSessionFrameAdmission.SyntheticPlanProof proof;Check.True(Admission.TryIssueSyntheticPlanProof(request,prepared,out proof));
                Check.True(practice?Task.TryBeginSyntheticPracticeForFixture(Admission,request,proof):Task.TryBeginSyntheticLegacyBlockForFixture(Admission,request,proof));
                bool staged;do{Check.True(Admission.TryStageNextTaskEvent(out staged));}while(staged);DrainQueue();
                Check.True(!(practice?Task.TryBeginSyntheticPracticeForFixture(Admission,request,proof):Task.TryBeginSyntheticLegacyBlockForFixture(Admission,request,proof)));
            }
            internal void Neutral(){Mutate(()=>Task.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,false,0,0)));}
            internal void Answer(bool correct=true)
            {
                var ticket=QueueEye();DrainQueue();var trial=Task.CurrentTrial;ViveNamedSessionFrameAdmission.PresentationReservation presentation;
                Check.True(Admission.TryReservePresentation(ticket,1,frame,frame,out presentation),"presentation reserve "+trial.TrialId);
                Check.True(Admission.TryCommitPresentation(presentation,frame,frame,trial.TrialId,trial.Stimulus.Word,trial.Stimulus.Ink,Clock.Now),"presentation commit "+trial.TrialId);DrainQueue();
                Colour ink=Task.CurrentTrial.Stimulus.Ink;if(!correct)ink=(Colour)(((int)ink+1)%4);double x=ink==Colour.Green?.9:ink==Colour.Yellow?-.9:0,y=ink==Colour.Red?.9:ink==Colour.Blue?-.9:0;
                Mutate(()=>Task.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,true,x,y)));Neutral();
            }
            internal void Confirm()
            {Mutate(()=>Task.UpdateAuxiliary(false,false,false,0,true));Mutate(()=>Task.UpdateAuxiliary(true,false,false,0,true));Mutate(()=>Task.UpdateAuxiliary(false,false,false,0,true));}
            internal void Complete()
            {
                Mutate(()=>Task.RecordLegacySoftwareChoices(Clock,TaskChoice,EyeChoice));Neutral();Begin(true);Neutral();
                for(int i=0;i<8;i++)Answer();Check.Equal(Phase.BlockPreparation,Task.Phase);
                Mutate(()=>Task.OwnerLockSource(Actor.Owner,ControlSelection.Automatic,"synthetic-owner"));
                for(int block=0;block<4;block++)
                {
                    Begin(false);Check.Equal(Phase.Countdown,Task.Phase);
                    Check.True(Task.TryStartLegacyRawCountdownFromSoftwareWitness(block+1,block+1,Clock.Now));DrainQueue();
                    // Real sealed stopwatch: preserve full 3-second countdown.
                    Thread.Sleep((int)Math.Ceiling(Task.CountdownRemainingSeconds*1000)+20);Mutate(()=>Task.Tick());Neutral();
                    for(int i=0;i<24;i++)Answer();Check.Equal(Phase.Questionnaire,Task.Phase);Confirm();
                    for(int q=0;q<Questionnaire.Count;q++)Confirm();
                    if(block<3){Check.Equal(Phase.Rest,Task.Phase);Check.True(Task.ReadyEnabled);Confirm();Check.Equal(Phase.BlockReady,Task.Phase);}
                }
                Check.True(Task.Completed);ViveProEyeWorker.StatusSeal seal;Check.True(Worker.TrySealStatusEvidence(run,out seal));DrainStatus();
                Check.True(Admission.TryBindDrainedStatusSeal(seal));Check.Equal(ViveNamedSessionFrameAdmission.SealResult.Sealed,Admission.TrySealCompleted());DrainQueue();
                long water,cursor;double at;Check.True(Admission.TryGetDrainedSealBoundary(out water,out cursor,out at));Check.Equal(ordinal,water);
                Append(ViveRawSessionJournalCodec.TerminalPayloadV3(ordinal+1,cursor,Events.Count,Eyes.Count,statuses,water,at));
            }
            public void Dispose(){Admission.Withdraw();Worker.TryRequestStop();Source.Release();Check.True(Worker.Stop(1000));Worker.Dispose();Bytes.Dispose();}
        }
        static ViveRawSessionReadResult Read(byte[] bytes)
        {using(var input=new MemoryStream(bytes,false))return ViveRawSessionJournalReader.ProjectTrials(input,true,true,16777216,20000,CancellationToken.None);}
        static List<string[]> ParseCsv(string text)
        {
            var rows=new List<string[]>();var fields=new List<string>();var cell=new StringBuilder();bool quoted=false;
            for(int i=0;i<text.Length;i++)
            {char c=text[i];if(c=='"'){if(quoted&&i+1<text.Length&&text[i+1]=='"'){cell.Append('"');i++;}else quoted=!quoted;}
                else if(!quoted&&(c==','||c=='\n')){fields.Add(cell.ToString());cell.Clear();if(c=='\n'){rows.Add(fields.ToArray());fields.Clear();}}
                else cell.Append(c);}
            Check.True(!quoted&&fields.Count==0&&cell.Length==0);return rows;
        }
        static string Raw(double value){return "raw:f64_bits_hex:"+BitConverter.DoubleToInt64Bits(value).ToString("X16",CultureInfo.InvariantCulture);}
        static void CheckCsv(string csv,List<ViveNamedSessionFrameAdmission.Frame> expected)
        {
            var parsed=ParseCsv(csv);var header=parsed[0];int kind=Array.IndexOf(header,"record_kind");
            var rows=parsed.Skip(1).Where(r=>r[kind]=="observation").ToList();Check.Equal(expected.Count,rows.Count);
            for(int i=0;i<rows.Count;i++)
            {
                var row=rows[i];var eye=expected[i];Check.Equal(header.Length,row.Length);
                Func<string,string> value=name=>row[Array.IndexOf(header,"eye_"+name)];
                Check.Equal(eye.NativeFrame.ToString(CultureInfo.InvariantCulture),value("native_frame"));
                Check.Equal(eye.NativeTimestamp.ToString(CultureInfo.InvariantCulture),value("native_timestamp"));
                Check.Equal(Raw(eye.EyeObserved),value("eye_observed_raw"));Check.Equal(Raw(eye.HeadObserved),value("head_observed_raw"));Check.Equal(Raw(eye.CheckedAt),value("checked_at_raw"));
                Check.Equal(eye.TrialAtIngest,value("ingest_trial_id"));Check.Equal(eye.Block.ToString(CultureInfo.InvariantCulture),value("ingest_block"));
                foreach(var pair in new[]{Tuple.Create("left",eye.Left),Tuple.Create("right",eye.Right),Tuple.Create("combined",eye.Combined)})
                {
                    var ray=pair.Item2;Check.Equal("true",value(pair.Item1+"_present"));Check.Equal(ray.Mask.ToString(CultureInfo.InvariantCulture),value(pair.Item1+"_mask"));
                    Check.Equal(ray.OriginValid?"true":"false",value(pair.Item1+"_origin_valid"));Check.Equal(ray.DirectionValid?"true":"false",value(pair.Item1+"_direction_valid"));
                    Check.Equal(Raw(ray.OriginMillimeters.X),value(pair.Item1+"_origin_x_raw"));Check.Equal(Raw(ray.OriginMillimeters.Y),value(pair.Item1+"_origin_y_raw"));Check.Equal(Raw(ray.OriginMillimeters.Z),value(pair.Item1+"_origin_z_raw"));
                    Check.Equal(Raw(ray.Direction.X),value(pair.Item1+"_direction_x_raw"));Check.Equal(Raw(ray.Direction.Y),value(pair.Item1+"_direction_y_raw"));Check.Equal(Raw(ray.Direction.Z),value(pair.Item1+"_direction_z_raw"));
                }
            }
        }
        public static void Register(TestSuite suite)
        {
            suite.Run("legacy full actual 8 practice 96 main 20 ratings reconcile FIFO VRAW4 memory",()=>{
                using(var f=new Fixture())
                {
                    f.Complete();var bytes=f.Bytes.ToArray();var result=Read(bytes);Check.Equal(ViveRawSessionReadStatus.VerifiedPrefix,result.Status);
                    Check.True(result.CaptureTerminalObserved);Check.Equal(104,f.Task.TrialCount);Check.Equal(20,f.Task.RatingCount);
                    var p=result.TrialProjection;Check.Equal(104,p.Trials.Count);Check.Equal(20,p.Ratings.Count);Check.Equal(5,p.PreparedPlans.Count);
                    Check.Equal("LEGACY_ABCD",p.DesignVariant);Check.Equal(f.Clock.ClockId,p.DeclaredClockId);
                    string[] order={"B","C","A","D"};for(int b=0;b<4;b++){Check.Equal(order[b],p.DeclaredPlanBlocks[b].Option);Check.Equal(b,p.DeclaredPlanBlocks[b].ListIndex);Check.Equal(24,p.Trials.Count(t=>t.Block==b));}
                    Check.Equal((long)f.Events.Count,result.VerifiedTaskRecords);Check.Equal((long)f.Eyes.Count,result.VerifiedObservationRecords);
                    foreach(var row in p.Trials)
                    {var presented=f.Events.Single(e=>e.Kind=="stimulus_presented"&&e.TrialId==row.TrialId);Check.Equal((double?)presented.Time,row.PresentationEventLocalSeconds);Check.True(!row.EventConflict&&!row.IncompleteEvidence);Check.Equal(1,row.ResponseEvents);
                        var planned=f.Events.Single(e=>e.Kind=="trial_planned"&&e.TrialId==row.TrialId);var response=f.Events.Single(e=>e.Kind=="first_response"&&e.TrialId==row.TrialId);
                        Check.Equal(f.Clock.ClockId,row.DeclaredPresentationTaskClock);Check.Equal("caller_declared_unity_render_proxy_not_photon_onset",row.DeclaredUnityRenderBasis);
                        Check.Equal(planned.Fields["list"],row.PlannedList);Check.Equal((double?)response.Time,row.ResponseEventLocalSeconds);Check.Equal(response.Fields["response"],row.Response);Check.Equal(response.Fields["rt_seconds"],row.DeclaredReactionSeconds);
                        var plan=p.PreparedPlans.Single(x=>x.List==row.PlannedList);var stimulus=plan.Items[Int32.Parse(row.PlannedIndex,CultureInfo.InvariantCulture)];Check.Equal(stimulus.Word,row.PlannedWord);Check.Equal(stimulus.Ink,row.PlannedInk);Check.Equal(stimulus.Word,row.Word);Check.Equal(stimulus.Ink,row.Ink);}
                    var ratings=f.Events.Where(e=>e.Kind=="rating").ToList();for(int i=0;i<ratings.Count;i++){Check.Equal(ratings[i].Time,p.Ratings[i].EventLocalSeconds);Check.Equal(ratings[i].Fields["item"],p.Ratings[i].Item);Check.Equal(ratings[i].Fields["value"],p.Ratings[i].DeclaredValue);}
                    Check.True(!result.SourceAuthenticated&&!result.ConsentAuthenticated&&!result.CompleteSessionProven&&!result.DurableStorageProven);
                    using(var input=new MemoryStream(bytes,false))using(var output=new MemoryStream())
                    {Check.Equal(ViveRawReadableAnalysisCsv.Outcome.WrittenAndFlushed,ViveRawReadableAnalysisCsv.WriteSyntheticForTest(input,output,16777216,20000,33554432,CancellationToken.None));var csv=Encoding.UTF8.GetString(output.ToArray());Check.True(csv.Contains("SYNTHETIC_ONLY")&&csv.Contains("INGEST_NOT_SAMPLE_ALIGNMENT"));CheckCsv(csv,f.Eyes);}
                    var corrupt=(byte[])bytes.Clone();corrupt[corrupt.Length-1]^=1;Check.Equal(ViveRawSessionReadStatus.Invalid,Read(corrupt).Status);
                    Array.Resize(ref bytes,bytes.Length-1);var prefix=Read(bytes);Check.True(prefix.TrailingFrameTruncated&&!prefix.CaptureTerminalObserved);
                }
            });
            suite.Run("legacy exact context rejects named factories and equal-label foreign clock",()=>{
                using(var f=new Fixture())
                {Check.Throws<InvalidOperationException>(()=>ViveNamedSessionFrameAdmission.CreateSyntheticForTest(f.Task,f.Worker,f.Clock,256));
                    ViveDevelopmentParticipant.NamedRecordingTaskStamp stamp;Check.True(!f.Task.TryCaptureNamedRecordingTaskStamp(f.Clock,out stamp));
                    Check.True(!f.Task.TryCaptureLegacyRecordingTaskStamp(new MonotonicClock(f.Clock.ClockId),out stamp));
                    Check.Throws<InvalidOperationException>(()=>f.Task.PrepareNamedRecordingBlockPlan(Actor.Owner,true));
                    Check.True(!f.Task.IsResearchSession&&!f.Task.RecordsEyeSamples&&!f.Task.RecordsTaskFiles&&!ViveNamedRecordingUiAdmission.RecordingAvailable&&!ViveRawRelativeFileCapability.ProductionGateOpen);}
            });
            suite.Run("legacy reservation capacity fails before participant mutation",()=>{
                using(var f=new Fixture(4))
                {int before=f.Task.AuthoritativeEventCount;bool ran=false;Check.True(!f.Admission.TryRunReservedOrdinaryMutation(()=>{ran=true;f.Task.Tick();}));Check.True(!ran);Check.Equal(before,f.Task.AuthoritativeEventCount);Check.True(f.Admission.IoFence.IsFaulted);}
            });
            suite.Run("legacy stale task stamp cannot admit a later eye",()=>{
                using(var f=new Fixture())
                {ViveDevelopmentParticipant.NamedRecordingTaskStamp stamp;Check.True(f.Task.TryCaptureLegacyRecordingTaskStamp(f.Clock,out stamp));f.Neutral();var ticket=f.Ticket();Check.True(!f.Admission.TryStage(stamp,ticket,1));}
            });
            suite.Run("legacy consumed FIFO receipt cannot replay or survive withdrawal",()=>{
                using(var f=new Fixture())
                {f.QueueEye();ViveNamedSessionFrameAdmission.ConsumerReceipt receipt;Check.True(f.Admission.TryReserveConsumer(out receipt)&&receipt!=null);Check.True(f.Admission.TryAcknowledgeSyntheticForTest(receipt));Check.True(!f.Admission.TryAcknowledgeSyntheticForTest(receipt));f.Admission.Withdraw();bool ran=false;Check.True(!f.Admission.TryRunReservedOrdinaryMutation(()=>{ran=true;f.Task.Tick();}));Check.True(!ran&&f.Admission.Count==0);}
            });
            suite.Run("legacy byte reservation rejects before invoking participant",()=>{
                using(var f=new Fixture(budget:8388608)){bool ran=false;int count=f.Task.AuthoritativeEventCount;Check.True(!f.Admission.TryRunReservedOrdinaryMutation(()=>{ran=true;f.Task.Tick();}));Check.True(!ran);Check.Equal(count,f.Task.AuthoritativeEventCount);}
            });
            suite.Run("legacy retry keeps actual first and second eight-item practice plans",()=>{
                using(var f=new Fixture())
                {
                    f.Mutate(()=>f.Task.RecordLegacySoftwareChoices(f.Clock,f.TaskChoice,f.EyeChoice));f.Neutral();f.Begin(true);f.Neutral();
                    for(int i=0;i<8;i++){f.Answer(i>=3);if(f.Task.Phase==Phase.PracticeCorrection){f.Confirm();f.Neutral();}}
                    Check.Equal(Phase.PracticeRetry,f.Task.Phase);f.Neutral();f.Begin(true);f.Neutral();for(int i=0;i<8;i++)f.Answer();
                    Check.Equal(Phase.BlockPreparation,f.Task.Phase);Check.Equal(16,f.Task.TrialCount);
                    var projection=Read(f.Bytes.ToArray());Check.Equal(ViveRawSessionReadStatus.VerifiedPrefix,projection.Status);Check.Equal(16,projection.TrialProjection.Trials.Count);Check.Equal(2,projection.TrialProjection.PreparedPlans.Count);Check.True(!projection.CaptureTerminalObserved);
                    var attempts=f.Events.Where(e=>e.Kind=="practice_completed").ToList();Check.Equal(2,attempts.Count);Check.Equal("5",attempts[0].Fields["correct_first"]);Check.Equal("8",attempts[1].Fields["correct_first"]);
                }
            });
            suite.Run("legacy eye ticket from foreign worker run cannot enter FIFO",()=>{
                using(var a=new Fixture())using(var b=new Fixture()){var ticket=b.Ticket();ViveDevelopmentParticipant.NamedRecordingTaskStamp stamp;Check.True(a.Task.TryCaptureLegacyRecordingTaskStamp(a.Clock,out stamp));Check.True(!a.Admission.TryStage(stamp,ticket,1));}
            });
            suite.Run("legacy duplicate eye ticket and full queue fail closed",()=>{
                using(var f=new Fixture()){var ticket=f.Ticket();ViveDevelopmentParticipant.NamedRecordingTaskStamp stamp;Check.True(f.Task.TryCaptureLegacyRecordingTaskStamp(f.Clock,out stamp));Check.True(f.Admission.TryStage(stamp,ticket,1));Check.True(!f.Admission.TryStage(stamp,ticket,1));Check.True(f.Admission.IoFence.IsFaulted);}
                using(var f=new Fixture(8)){for(int i=0;i<8;i++)f.QueueEye();Check.Equal(8,f.Admission.Count);var ticket=f.Ticket();ViveDevelopmentParticipant.NamedRecordingTaskStamp stamp;Check.True(f.Task.TryCaptureLegacyRecordingTaskStamp(f.Clock,out stamp));Check.True(!f.Admission.TryStage(stamp,ticket,1));Check.True(f.Admission.IoFence.IsFaulted);}
            });
            suite.Run("legacy equal-label foreign participant context is not interchangeable",()=>{
                using(var f=new Fixture()){var other=NewTask(f.Clock);ViveDevelopmentParticipant.NamedRecordingTaskStamp stamp;Check.True(other.TryCaptureLegacyRecordingTaskStamp(f.Clock,out stamp));var ticket=f.Ticket();Check.True(!f.Admission.TryStage(stamp,ticket,1));}
            });
            suite.Run("legacy aged ticket and later invalid provider reject admission",()=>{
                using(var f=new Fixture()){var ticket=f.Ticket();ViveDevelopmentParticipant.NamedRecordingTaskStamp stamp;Check.True(f.Task.TryCaptureLegacyRecordingTaskStamp(f.Clock,out stamp));Thread.Sleep(40);Check.True(!f.Admission.TryStage(stamp,ticket,.01));}
                using(var f=new Fixture()){var ticket=f.Ticket();ViveDevelopmentParticipant.NamedRecordingTaskStamp stamp;Check.True(f.Task.TryCaptureLegacyRecordingTaskStamp(f.Clock,out stamp));f.SendEye(true);Check.True(!f.Admission.TryStage(stamp,ticket,1));}
            });
            suite.Run("legacy admitted frame snapshot survives newer participant and source state",()=>{
                using(var f=new Fixture()){f.QueueEye();f.DrainQueue();var old=f.Eyes[0];var raw=Raw(old.EyeObserved);var trial=old.TrialAtIngest;f.Neutral();f.SendEye();Check.Equal(raw,Raw(old.EyeObserved));Check.Equal(trial,old.TrialAtIngest);Check.Equal(-31.25,old.Left.OriginMillimeters.X);Check.Equal(32.125,old.Right.OriginMillimeters.X);}
            });
            suite.Run("legacy synthetic pump uses exact identity status and withdrawal fence",()=>{
                using(var f=new Fixture(pumpMode:true))
                {
                    var writer=new ViveNamedRawRecordingPump.SyntheticWriter(f.Admission);
                    Check.Throws<InvalidOperationException>(()=>ViveNamedRawRecordingPump.StartSyntheticWithStatusForTest(f.Task,f.Worker,f.Clock,f.Admission,writer));
                    Check.Throws<InvalidOperationException>(()=>ViveNamedRawRecordingPump.StartLegacySyntheticWithStatusForTest(NewTask(f.Clock),f.Worker,f.Clock,f.Admission,writer));
                    var pump=ViveNamedRawRecordingPump.StartLegacySyntheticWithStatusForTest(f.Task,f.Worker,f.Clock,f.Admission,writer);
                    try{f.Source.Send(null);f.SendEye();Check.Equal(ViveNamedRawRecordingPump.Preparation.Ready,pump.PrepareStatus());pump.Withdraw();Check.True(SpinWait.SpinUntil(()=>pump.HasExited,3000));Check.True(pump.WithdrawalRequested&&f.Admission.Count==0&&!pump.StageRigFrame(null,1));Check.Equal(0,writer.CompleteCalls);}
                    finally{pump.Stop();}
                }
            });
        }
    }
}
#endif
