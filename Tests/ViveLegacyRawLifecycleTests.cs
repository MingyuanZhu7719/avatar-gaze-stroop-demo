#if VIVE_RAW_SYNTHETIC_TEST && VIVE_SYNTHETIC_BATCH_FIXTURE && !UNITY_5_3_OR_NEWER
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    // Managed synthetic observations only. No native factories, SDK,
    // Unity, participant data, supplied write-success or shortened protocol.
    // Only the explicit fixture output below can copy a synthetic prefix.
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
            internal readonly double TaskChoice,EyeChoice;readonly object run;byte[] digest=new byte[32];long ordinal,statuses,headSequence;int frame;
            internal Fixture(int capacity=256,long budget=33554432,bool pumpMode=false,bool startupPreflight=false)
            {
                Task=NewTask(Clock);TaskChoice=Clock.Now;EyeChoice=Clock.Now;
                Worker=new ViveProEyeWorker(Clock,()=>new ViveHeadFrame(++headSequence,Clock.Now,true,true,Rotation.Identity),()=>Source);
                Check.True(Worker.Start()&&Source.Waiting.WaitOne(3000));
                if(startupPreflight)
                {
                    try
                    {
                        var claim=new ViveRecordingStartClaim(Worker);
                        int attempts=0;
                        ViveNamedSessionFrameAdmission attached=null;
                        Func<bool> context=()=>Task.Phase==Phase.Preparation&&Task.CurrentTrial==null;
                        Action attempt=()=>{
                            attempts++;
                            var current=Worker.ReadRecordingStartReadiness();object exactRun;
                            Check.True(Monitor.IsEntered(Worker.RecordingTicketGate)&&current.Ready&&
                                Worker.TryGetRecordingRunIdentity(out exactRun)&&
                                Object.ReferenceEquals(current.RunIdentity,exactRun),"exact healthy preflight at synthetic start");
                            CheckNoPreflightOutput();
                            attached=ViveNamedSessionFrameAdmission.CreateLegacySyntheticForTest(Task,Worker,Clock,capacity,TaskChoice,EyeChoice);
                        };
                        VerifyStartupWaiting(claim,context,attempt);
                        Check.True(attempts==0&&attached==null);
                        Check.Equal(ViveRecordingStartClaim.Result.Waiting,claim.TryExecute(context,()=>false,attempt));
                        Check.True(attempts==0&&attached==null);

                        var stale=new ViveRecordingStartClaim(Worker);
                        Check.Equal(ViveRecordingStartClaim.Result.Invalid,stale.TryExecute(()=>false,()=>true,attempt));
                        Check.Equal(ViveRecordingStartClaim.Result.Invalid,stale.TryExecute(context,()=>true,attempt));
                        Check.True(attempts==0&&attached==null);

                        var rejectedInstall=new ViveRecordingStartClaim(Worker);int rejectedAttempts=0;
                        Check.Throws<InvalidOperationException>(()=>rejectedInstall.TryExecute(context,()=>true,
                            ()=>{rejectedAttempts++;throw new InvalidOperationException("Synthetic installation rejection");}));
                        Check.Equal(ViveRecordingStartClaim.Result.Invalid,rejectedInstall.TryExecute(context,()=>true,attempt));
                        Check.True(rejectedAttempts==1&&attempts==0&&attached==null);
                        CheckNoPreflightOutput();

                        Check.Equal(ViveRecordingStartClaim.Result.Attempted,claim.TryExecute(context,()=>true,attempt));
                        Check.Equal(ViveRecordingStartClaim.Result.Invalid,claim.TryExecute(context,()=>true,attempt));
                        Check.True(attempts==1&&attached!=null,"synthetic admission must be attempted exactly once");
                        Admission=attached;
                    }
                    catch{Worker.TryRequestStop();Source.Release();Worker.Stop(1000);Worker.Dispose();Bytes.Dispose();throw;}
                }
                else Admission=ViveNamedSessionFrameAdmission.CreateLegacySyntheticForTest(Task,Worker,Clock,capacity,TaskChoice,EyeChoice);
                Check.True(Admission.ConfirmTask()&&Admission.ConfirmEye());
                Check.True(Admission.TryBindQueueBudget(ViveRawByteBudget.SelectExplicit(budget)));
                if(!pumpMode){Check.True(Admission.TryEnableWorkerStatus(128,out run));Source.Send(null);SendEye();DrainStatus();DrainQueue();}
            }
            void CheckNoPreflightOutput()
            {
                var field=typeof(ViveDevelopmentParticipant).GetField("namedFrameAdmission",BindingFlags.Instance|BindingFlags.NonPublic);
                Check.True(field!=null&&field.GetValue(Task)==null&&Bytes.Length==0&&Events.Count==0&&Eyes.Count==0&&
                    Task.Phase==Phase.Preparation&&Task.CurrentTrial==null,"preflight created an admission, task, or consumer output");
                Check.True(!Worker.UsesInstalledSdkPath&&!ViveRawRelativeFileCapability.ProductionGateOpen,
                    "managed preflight fixture must not have native recording authority");
            }
            void VerifyStartupWaiting(ViveRecordingStartClaim claim,Func<bool> context,Action attempt)
            {
                CheckNoPreflightOutput();
                SendEye(calibrationRequired:true);
                var invalid=Worker.ReadRecordingStartReadiness();
                Check.Equal(ViveProEyeWorker.RecordingStartReadinessReason.CalibrationRequired,invalid.Reason);
                Check.True(!invalid.Ready&&invalid.RunIdentity==null);
                Check.Equal(ViveRecordingStartClaim.Result.Waiting,claim.TryExecute(context,()=>true,attempt));
                CheckNoPreflightOutput();
                SendEye();
                Check.True(!Worker.ReadRecordingStartReadiness().Ready,"one fresh healthy return admitted startup");
                Check.Equal(ViveRecordingStartClaim.Result.Waiting,claim.TryExecute(context,()=>true,attempt));
                double until=Clock.Now+4;
                while(!Worker.ReadRecordingStartReadiness().Ready&&Clock.Now<until)
                {CheckNoPreflightOutput();Thread.Sleep(20);SendEye();}
                Check.True(Worker.ReadRecordingStartReadiness().Ready,"synthetic preflight never became healthy");
            }
            internal void SendEye(bool invalid=false,bool calibrationRequired=false,int nativeResult=0,int nativeRuntimeStatus=2)
            {
                var ray=new ViveEyeRay(calibrationRequired?0UL:2UL,false,!calibrationRequired,new Vec3(0,0,0),new Vec3(.6,0,.8));int next=++frame;
                var left=new ViveEyeRay(3,true,true,new Vec3(-31.25,2.5,3.75),new Vec3(.3,.4,.5));
                var right=new ViveEyeRay(3,true,true,new Vec3(32.125,-2.25,4.5),new Vec3(-.25,.125,.875));
                Source.Send(new ViveProEyePoll(null,new ViveEyeFrame(next,next*8,Clock.Now,invalid,left,right,ray),ViveEyeRuntimeState.Working,calibrationRequired?ViveEyeCalibrationState.Required:ViveEyeCalibrationState.ReportedReady,true,nativeResult,nativeRuntimeStatus));
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
        const string IncompleteFixtureScope="SYNTHETIC_ONLY_INCOMPLETE_VERIFIED_PREFIX_NOT_COMPLETED_ANALYSIS_EXPORT";
        static void CopyExplicitSyntheticPrefix(byte[] bytes,string trialsCsv)
        {
            string configured=Environment.GetEnvironmentVariable("STROOP_READINESS_SYNTHETIC_OUTPUT");
            if(configured==null)return;
            Check.True(configured.Length>=3&&Char.IsLetter(configured[0])&&configured[1]==':'&&
                (configured[2]=='\\'||configured[2]=='/'),"synthetic output requires an explicit local Windows path");
            var directory=new DirectoryInfo(Path.GetFullPath(configured));
            Check.True(directory.Exists&&directory.Parent!=null,"synthetic output folder must already exist");
            for(var d=directory;d!=null;d=d.Parent)
                Check.True((d.Attributes&FileAttributes.ReparsePoint)==0,"synthetic output reparse path refused");
            Check.True(!directory.EnumerateFileSystemInfos().Any(),"synthetic output folder must be empty");
            var tagged=new StringBuilder();
            tagged.Append("fixture_scope,").Append(trialsCsv.TrimEnd('\n').Replace("\n","\n"+IncompleteFixtureScope+","));
            // The first line is the header; label every following data row.
            tagged.Append('\n');
            using(var trace=new FileStream(Path.Combine(directory.FullName,"SYNTHETIC_ONLY_INCOMPLETE_2_RESPONSES.vraw"),FileMode.CreateNew,FileAccess.Write,FileShare.None))
                trace.Write(bytes,0,bytes.Length);
            using(var csv=new FileStream(Path.Combine(directory.FullName,"SYNTHETIC_ONLY_INCOMPLETE_PREFIX_TRIAL_PROJECTION.csv"),FileMode.CreateNew,FileAccess.Write,FileShare.None))
            {
                var data=new UTF8Encoding(false,true).GetBytes(tagged.ToString());
                csv.Write(data,0,data.Length);
            }
        }
        // Real worker/provider publication and admission; reflection below only
        // reads private diagnostic state, except the explicitly synthetic task
        // completion seam. No status queue, ticket or ACK is fabricated.
        sealed class BacklogFixture:IDisposable
        {
            internal readonly Fixture F=new Fixture(pumpMode:true);
            internal readonly ViveNamedRawRecordingPump Pump;
            internal readonly ViveNamedRawRecordingPump.SyntheticWriter Writer;
            internal readonly List<long> Admitted=new List<long>();
            internal readonly long Baseline;
            int runtime=100;
            static readonly BindingFlags Fields=BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public;
            internal static object Field(object value,string name){return value.GetType().GetField(name,Fields).GetValue(value);}
            internal BacklogFixture()
            {
                Writer=new ViveNamedRawRecordingPump.SyntheticWriter(F.Admission){CompleteResult=true};
                Pump=ViveNamedRawRecordingPump.StartLegacySyntheticWithStatusForTest(F.Task,F.Worker,F.Clock,F.Admission,Writer);
                F.Admission.BeforeStatusProofPublishForTest=()=>Admitted.Add((long)Field(F.Admission,"lastStatusSequence"));
                F.Source.Send(null);F.SendEye();
                Check.Equal(ViveNamedRawRecordingPump.Preparation.Ready,Pump.PrepareStatus());
                Baseline=LastAck;Admitted.Clear();WaitWriter();
            }
            internal object Run {get{return Field(F.Worker,"current");}}
            internal int Count {get{lock(F.Worker.RecordingTicketGate)return (int)Field(Run,"StatusCount");}}
            internal long LastAck {get{lock(F.Worker.RecordingTicketGate)return (long)Field(Run,"StatusLastAcknowledged");}}
            internal bool Fault {get{lock(F.Worker.RecordingTicketGate)return (bool)Field(Run,"StatusFault");}}
            internal object PendingTicket {get{return Field(Pump,"pendingStatusTicket");}}
            internal ViveProEyeWorker.StatusSeal PendingSeal {get{return (ViveProEyeWorker.StatusSeal)Field(Pump,"pendingCompletionStatusSeal");}}
            internal void Publish(int count){for(int i=0;i<count;i++)F.SendEye(nativeRuntimeStatus:++runtime);}
            internal void WaitWriter(){Check.True(SpinWait.SpinUntil(()=>F.Admission.Count==0&&!Pump.HasReservedIo,3000),"synthetic writer did not drain");}
            internal void CheckOrder(int count)
            {Check.Equal(count,Admitted.Count);for(int i=0;i<count;i++)Check.Equal(Baseline+i+1,Admitted[i]);Check.Equal(Baseline+count,LastAck);}
            internal void CompleteTaskOnlyForSealTest()
            {
                // This tests queue/seal mechanics, not real protocol completion.
                // Actual final-rating/writer/CSV coverage lives in the native E2E.
                F.Task.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,false,0,0));
                typeof(ViveDevelopmentParticipant).GetMethod("OwnerBeginPracticeCore",Fields).Invoke(F.Task,new object[]{Actor.Owner});
                var experiment=(Experiment)Field(F.Task,"experiment");
                typeof(Experiment).GetField("<Phase>k__BackingField",Fields).SetValue(experiment,Phase.Ended);
                typeof(Experiment).GetField("<EndReason>k__BackingField",Fields).SetValue(experiment,(EndReason?)EndReason.Completed);
            }
            public void Dispose()
            {Writer.Release.Set();Writer.CompleteRelease.Set();Pump.Stop();Check.True(SpinWait.SpinUntil(()=>Pump.HasExited,3000));F.Dispose();}
        }
        static void RegisterStatusBacklogTests(TestSuite suite)
        {
            foreach(int count in new[]{63,64,65,128})
            {
                int n=count;
                suite.Run("status backlog "+n+" drains bounded FIFO without false stop",()=>{
                    using(var f=new BacklogFixture())
                    {
                        int writes=f.Writer.Calls;f.Publish(n);Check.Equal(n,f.Count);Check.True(!f.Fault);
                        var first=f.Pump.PrepareStatus();
                        Check.Equal(n<=64?ViveNamedRawRecordingPump.Preparation.Ready:ViveNamedRawRecordingPump.Preparation.Busy,first);
                        Check.Equal(Math.Max(0,n-64),f.Count);Check.Equal(Math.Min(n,64),f.Admitted.Count);
                        Check.True(f.PendingTicket==null);Check.True(f.Pump.FirstFault==null&&!f.F.Admission.IoFence.IsRevoked);
                        if(n>64)Check.Equal(ViveNamedRawRecordingPump.Preparation.Ready,f.Pump.PrepareStatus());
                        f.CheckOrder(n);Check.Equal(0,f.Count);Check.True(f.PendingTicket==null);
                        f.WaitWriter();Check.Equal(writes+n,f.Writer.Calls);Check.Equal(0,f.Writer.CompleteCalls);
                        Check.Equal(ViveNamedRawRecordingPump.Outcome.Running,f.Pump.State);
                    }
                });
                suite.Run("status completion backlog "+n+" retains exact seal until drained",()=>{
                    using(var f=new BacklogFixture())
                    {
                        f.CompleteTaskOnlyForSealTest();f.Publish(n);Check.True(f.Pump.RequestCompletedDrain());
                        if(n>64)
                        {
                            var seal=f.PendingSeal;Check.True(seal!=null);Check.Equal(f.Baseline+n,seal.FinalSequence);
                            Check.Equal(ViveNamedRawRecordingPump.Outcome.Finishing,f.Pump.State);
                            Check.True(!f.F.Admission.Sealed&&f.Writer.CompleteCalls==0&&seal.Consumed==0);
                            // A provider return after the frozen boundary cannot
                            // add status records or extend this exact seal.
                            f.Publish(1);Check.Equal(n-64,f.Count);Check.True(Object.ReferenceEquals(seal,f.PendingSeal));
                            Check.True(f.Pump.PollRequestedCompletionDrain());Check.Equal(1,seal.Consumed);
                        }
                        Check.True(f.PendingSeal==null);f.CheckOrder(n);Check.Equal(0,f.Count);
                        Check.True(f.Writer.CompleteEntered.WaitOne(3000)&&SpinWait.SpinUntil(()=>f.Pump.HasExited,3000));
                        Check.Equal(1,f.Writer.CompleteCalls);Check.Equal(ViveNamedRawRecordingPump.Outcome.CloseCallsReturned,f.Pump.State);
                        Check.True(f.Pump.PollRequestedCompletionDrain());Check.Equal(1,f.Writer.CompleteCalls);
                    }
                });
            }
            suite.Run("sustained status backlog progresses in exact order without unbounded owner work",()=>{
                using(var f=new BacklogFixture())
                {
                    f.Publish(65);int total=65;
                    for(int round=0;round<5;round++)
                    {
                        int before=f.Admitted.Count;
                        Check.Equal(ViveNamedRawRecordingPump.Preparation.Busy,f.Pump.PrepareStatus());
                        Check.Equal(64,f.Admitted.Count-before);Check.Equal(1,f.Count);Check.True(f.PendingTicket==null);
                        f.WaitWriter();f.Publish(64);total+=64;
                    }
                    Check.Equal(ViveNamedRawRecordingPump.Preparation.Busy,f.Pump.PrepareStatus());
                    Check.Equal(ViveNamedRawRecordingPump.Preparation.Ready,f.Pump.PrepareStatus());
                    f.CheckOrder(total);Check.True(f.Pump.FirstFault==null&&!f.F.Admission.IoFence.IsRevoked);
                }
            });
            suite.Run("bounded status backlog does not gate independent task mutation",()=>{
                using(var f=new BacklogFixture())
                {
                    f.Publish(65);ViveProEyePoll poll;Check.True(f.F.Worker.TryPollTaskForRecording(out poll));
                    bool waiting;Check.True(f.Pump.StageRigFrame(poll,1,false,out waiting));
                    Check.True(!waiting&&f.Pump.RawTaskEpochThisFrame&&f.Pump.VerifyStagedEpoch());
                    var ticket=(ViveProEyeWorker.RecordingFrameTicket)BacklogFixture.Field(f.Pump,"stagedEpoch");
                    Check.True(ticket.HeadOnly);Check.Equal(-1.0,ticket.EyeObserved);
                    Check.Equal((ViveRawEyeObservation.Reason?)ViveRawEyeObservation.Reason.SnapshotBusy,ticket.EyeUnavailable);
                    Check.Equal(1,f.Count);Check.True(f.PendingTicket==null);
                    bool ran=false;Check.True(f.Pump.TryRunReservedOrdinaryMutation(()=>{ran=true;f.F.Task.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,false,0,0));}));
                    Check.True(ran&&f.Pump.VerifyTaskRunAuthorityForDisplay());
                    Check.Equal(ViveNamedRawRecordingPump.Preparation.Ready,f.Pump.PrepareStatus());f.CheckOrder(65);
                    Check.True(f.Pump.FirstFault==null&&!f.F.Admission.IoFence.IsRevoked);
                }
            });
            suite.Run("task event between bounded status batches drains before the next frame cursor",()=>{
                using(var f=new BacklogFixture())
                {
                    f.Publish(65);Check.Equal(ViveNamedRawRecordingPump.Preparation.Busy,f.Pump.PrepareStatus());
                    Check.True(f.PendingTicket==null);int before=f.F.Task.AuthoritativeEventCount;
                    Check.True(f.F.Admission.TryRunReservedOrdinaryMutation(()=>
                        f.F.Task.RecordLegacySoftwareChoices(f.F.Clock,f.F.TaskChoice,f.F.EyeChoice)));
                    Check.True(f.F.Task.AuthoritativeEventCount>before,"fixture must produce a real intervening task event");
                    ViveProEyePoll poll;Check.True(f.F.Worker.TryPollTaskForRecording(out poll));bool waiting;
                    Check.True(f.Pump.StageRigFrame(poll,1,false,out waiting),"next frame rejected intervening task cursor: "+f.Pump.FirstFault);
                    Check.True(!waiting&&f.Pump.VerifyStagedEpoch());f.CheckOrder(65);
                    Check.True((long)BacklogFixture.Field(f.F.Admission,"lastTaskSequence")>=f.F.Task.AuthoritativeEventCount-1);
                    Check.True(f.Pump.FirstFault==null&&!f.F.Admission.IoFence.IsRevoked);
                }
            });
            suite.Run("read-only status backlog probe preserves queue and exact run binding",()=>{
                using(var f=new BacklogFixture())
                {
                    Check.Equal(ViveProEyeWorker.StatusDisposition.Empty,f.F.Worker.ReadStatusBacklog(f.Run));
                    f.Publish(65);long acknowledged=f.LastAck;
                    for(int i=0;i<3;i++)Check.Equal(ViveProEyeWorker.StatusDisposition.Event,f.F.Worker.ReadStatusBacklog(f.Run));
                    Check.Equal(ViveProEyeWorker.StatusDisposition.Unavailable,f.F.Worker.ReadStatusBacklog(new object()));
                    Check.Equal(65,f.Count);Check.Equal(acknowledged,f.LastAck);Check.True(f.PendingTicket==null);
                    using(var entered=new ManualResetEvent(false))using(var release=new ManualResetEvent(false))
                    {
                        var holder=new Thread(()=>{lock(f.F.Worker.RecordingTicketGate){entered.Set();release.WaitOne();}}){IsBackground=true};
                        holder.Start();Check.True(entered.WaitOne(3000));
                        try{Check.Equal(ViveProEyeWorker.StatusDisposition.Busy,f.F.Worker.ReadStatusBacklog(f.Run));}
                        finally{release.Set();Check.True(holder.Join(3000));}
                    }
                    Check.Equal(65,f.Count);Check.Equal(acknowledged,f.LastAck);
                    Check.Equal(ViveNamedRawRecordingPump.Preparation.Busy,f.Pump.PrepareStatus());
                    Check.Equal(ViveNamedRawRecordingPump.Preparation.Ready,f.Pump.PrepareStatus());f.CheckOrder(65);
                }
            });
            suite.Run("strict non-independent initial capture waits for status backlog proof",()=>{
                using(var f=new BacklogFixture())
                {
                    f.Publish(65);ViveProEyePoll poll;Check.True(f.F.Worker.TryPollForRecording(out poll));
                    bool waiting;Check.True(!f.Pump.StageRigFrame(poll,1,true,out waiting));
                    Check.True(waiting&&!f.Pump.RawTaskEpochThisFrame&&f.Pump.FirstFault==null);
                    Check.Equal(1,f.Count);Check.Equal(ViveNamedRawRecordingPump.Preparation.Ready,f.Pump.PrepareStatus());
                    Check.True(f.Pump.StageRigFrame(poll,1,true,out waiting)&&!waiting);f.CheckOrder(65);
                }
            });
            foreach(bool completion in new[]{false,true})
            {
                bool finish=completion;
                suite.Run("real status overflow remains fail closed "+(finish?"at completion":"during capture"),()=>{
                    using(var f=new BacklogFixture())
                    {
                        if(finish)f.CompleteTaskOnlyForSealTest();f.Publish(129);Check.True(f.Fault);
                        if(finish)Check.True(!f.Pump.RequestCompletedDrain());
                        else Check.Equal(ViveNamedRawRecordingPump.Preparation.Faulted,f.Pump.PrepareStatus());
                        Check.True(f.Pump.FirstFault!=null&&f.F.Admission.IoFence.IsRevoked);Check.Equal(0,f.Writer.CompleteCalls);
                    }
                });
            }
            foreach(bool withdrawal in new[]{false,true})
            {
                bool withdraw=withdrawal;
                suite.Run("pending completion backlog seal is revoked by "+(withdraw?"withdrawal":"stop"),()=>{
                    using(var f=new BacklogFixture())
                    {
                        f.CompleteTaskOnlyForSealTest();f.Publish(65);Check.True(f.Pump.RequestCompletedDrain());
                        var seal=f.PendingSeal;Check.True(seal!=null&&seal.Consumed==0);
                        if(withdraw)f.Pump.Withdraw();else f.Pump.Stop();
                        Check.True(f.PendingSeal==null&&!f.Pump.PollRequestedCompletionDrain());
                        Check.True(!seal.TryConsume(seal.RunIdentity)&&f.F.Admission.IoFence.IsRevoked);
                        Check.Equal(0,f.Writer.CompleteCalls);
                    }
                });
            }
            suite.Run("bounded backlog cannot survive worker authority revocation",()=>{
                using(var f=new BacklogFixture())
                {
                    f.Publish(65);Check.Equal(ViveNamedRawRecordingPump.Preparation.Busy,f.Pump.PrepareStatus());
                    Check.True(f.PendingTicket==null);f.F.Worker.RevokeStatusEvidence();
                    Check.Equal(ViveNamedRawRecordingPump.Preparation.Faulted,f.Pump.PrepareStatus());
                    Check.True(f.F.Admission.IoFence.IsRevoked);Check.Equal(64,f.Admitted.Count);Check.Equal(0,f.Writer.CompleteCalls);
                }
            });
        }
        public static void Register(TestSuite suite)
        {
            RegisterStatusBacklogTests(suite);
            suite.Run("legacy worker startup preflight then two witnessed responses through verified FIFO prefix and CSV projection",()=>{
                using(var f=new Fixture(startupPreflight:true))
                {
                    f.Mutate(()=>f.Task.RecordLegacySoftwareChoices(f.Clock,f.TaskChoice,f.EyeChoice));
                    f.Neutral();f.Begin(true);f.Neutral();
                    for(int i=0;i<2;i++)f.Answer();
                    var bytes=f.Bytes.ToArray();var result=Read(bytes);
                    Check.Equal(ViveRawSessionReadStatus.VerifiedPrefix,result.Status);
                    Check.True(!result.CaptureTerminalObserved&&!result.CompleteSessionProven&&!result.DurableStorageProven);
                    var answered=result.TrialProjection.Trials.Where(t=>t.ResponseEvents>0).ToArray();
                    Check.Equal(2,answered.Length);
                    Check.True(answered[0].TrialId!=answered[1].TrialId&&
                        answered[0].ResponseEventLocalSeconds<answered[1].ResponseEventLocalSeconds);
                    foreach(var t in answered)
                    {
                        Check.True(t.ResponseEvents==1&&t.PresentationEvents==1&&!t.EventConflict&&!t.IncompleteEvidence&&
                            t.PresentationEventLocalSeconds.HasValue&&t.ResponseEventLocalSeconds.HasValue&&
                            t.ResponseEventLocalSeconds>=t.PresentationEventLocalSeconds);
                        var response=f.Events.Single(e=>e.Kind=="first_response"&&e.TrialId==t.TrialId);
                        Check.Equal((double?)response.Time,t.ResponseEventLocalSeconds);
                        Check.Equal(response.Fields["rt_seconds"],t.DeclaredReactionSeconds);
                    }
                    ViveRawTrialReport report;Check.True(ViveRawTrialReport.TryBuild(result,true,out report)&&!report.Saved&&
                        report.ManifestCsv.Contains("capture_terminal_declared,bool:false"));
                    string[] rows=report.TrialsCsv.TrimEnd('\n').Split('\n');
                    foreach(var t in answered)
                    {
                        string id="b64:"+Convert.ToBase64String(Encoding.UTF8.GetBytes(t.TrialId));
                        var row=rows.Single(r=>r.StartsWith(id+",",StringComparison.Ordinal)).Split(',');
                        var columns=rows[0].Split(',');
                        Check.Equal("i32:1",row[Array.IndexOf(columns,"response_event_count")]);
                        Check.Equal("f64bits:"+BitConverter.DoubleToInt64Bits(t.ResponseEventLocalSeconds.Value).ToString("X16",CultureInfo.InvariantCulture),
                            row[Array.IndexOf(columns,"response_event_local_f64bits")]);
                    }
                    // The full analysis writer may emit private partial rows
                    // before the terminal check; never retain that output.
                    using(var input=new MemoryStream(bytes,false))using(var output=new MemoryStream())
                        Check.True(ViveRawReadableAnalysisCsv.WriteSyntheticForTest(input,output,16777216,20000,33554432,CancellationToken.None)!=
                            ViveRawReadableAnalysisCsv.Outcome.WrittenAndFlushed);
                    CopyExplicitSyntheticPrefix(bytes,report.TrialsCsv);
                }
            });
            suite.Run("legacy preflight success does not mistake first post-attach calibration or direction loss for good gaze",()=>{
                foreach(bool nativeFault in new[]{false,true})
                using(var f=new Fixture(pumpMode:true,startupPreflight:true))
                {
                    var writer=new ViveNamedRawRecordingPump.SyntheticWriter(f.Admission);
                    var pump=ViveNamedRawRecordingPump.StartLegacySyntheticWithStatusForTest(f.Task,f.Worker,f.Clock,f.Admission,writer);
                    try
                    {
                        f.Source.Send(null); // poll begun before status-enable; not evidence
                        f.SendEye(calibrationRequired:true,nativeResult:nativeFault?-1:0);
                        Check.Equal(ViveNamedRawRecordingPump.Preparation.Ready,pump.PrepareStatus());
                        ViveProEyePoll frame;
                        Check.True(f.Worker.TryPollForRecording(out frame)&&pump.StageRigFrame(frame,1)&&pump.DrainTaskEvents());
                        Check.True(!pump.TrackingGapOpen&&!f.Admission.IoFence.IsRevoked&&f.Task.Phase==Phase.Preparation&&f.Task.CurrentTrial==null);
                        Check.Equal(ViveNamedRawRecordingPump.Outcome.Running,pump.State);
                        f.SendEye();
                        Check.True(f.Worker.TryPollForRecording(out frame)&&pump.StageRigFrame(frame,1)&&pump.DrainTaskEvents());
                        Check.True(!f.Admission.IoFence.IsRevoked&&f.Task.CurrentTrial==null);
                        Check.Equal(0,writer.CompleteCalls);
                    }
                    finally{pump.Stop();}
                }
            });
            suite.Run("legacy selections and real FIFO evidence reach first witnessed practice response",()=>{
                using(var f=new Fixture())
                {
                    string reason;
                    var setup=ViveNamedRecordingSetup.TryPrepare(true,@"C:\SyntheticNeverOpened","32",_=>true,out reason);
                    Check.True(setup!=null&&!setup.BothSelected);
                    setup.Observe(true,true,false,false,false,false,f.Clock);
                    setup.Observe(true,true,true,false,false,false,f.Clock);
                    setup.Observe(true,true,true,false,false,false,f.Clock);
                    Check.Equal(ViveNamedRecordingSetup.Step.Eye,setup.Current);
                    setup.Observe(true,true,false,false,false,false,f.Clock);
                    setup.Observe(true,true,true,false,false,false,f.Clock);
                    Check.True(setup.BothSelected);
                    setup.SuspendInputForTracking();
                    Check.True(!setup.CanStart(true,true,true,true,1,1));
                    setup.Observe(true,true,false,false,false,false,f.Clock);
                    Check.True(setup.CanStart(true,true,true,true,2,2));
                    f.Mutate(()=>f.Task.RecordLegacySoftwareChoices(f.Clock,f.TaskChoice,f.EyeChoice));
                    f.Neutral();f.Begin(true);f.Neutral();
                    var first=f.Task.CurrentTrial;Check.True(first!=null&&!first.Onset.HasValue);
                    f.Answer();
                    Check.True(first.Onset.HasValue&&first.ReactionSeconds.HasValue&&first.ReactionSeconds.Value>=0);
                    var r=Read(f.Bytes.ToArray());Check.Equal(ViveRawSessionReadStatus.VerifiedPrefix,r.Status);
                    Check.Equal(1,r.TrialProjection.Trials[0].ResponseEvents);
                }
            });
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
