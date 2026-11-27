#if VIVE_RAW_SYNTHETIC_TEST && VIVE_SYNTHETIC_BATCH_FIXTURE && !UNITY_5_3_OR_NEWER
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    public static class ViveLegacyWozRecordingPumpTests
    {
        internal sealed class Client:IViveProEyeClient
        {
            readonly Queue<ViveProEyePoll> values=new Queue<ViveProEyePoll>();
            internal readonly AutoResetEvent Waiting=new AutoResetEvent(false),Permit=new AutoResetEvent(false);
            public string Diagnostic {get{return "woz-raw-memory-synthetic";}}
            public bool Start(){return true;}
            public ViveProEyePoll Poll(){Waiting.Set();if(!Permit.WaitOne(30000))throw new Exception("fixture poll deadline");lock(values)return values.Dequeue();}
            internal void Send(ViveProEyePoll value){lock(values)values.Enqueue(value);Permit.Set();Check.True(Waiting.WaitOne(3000),"worker returned to fixture wait");}
            internal void Release(){lock(values)values.Enqueue(null);Permit.Set();}
            public void Dispose(){}
        }
        internal sealed class Fixture:IDisposable
        {
            internal static int InitializationStage;
            internal readonly MonotonicClock Clock=new MonotonicClock("woz-recording-local-"+Guid.NewGuid().ToString("N"));
            internal readonly MonotonicClock RemoteClock=new MonotonicClock("woz-recording-remote-"+Guid.NewGuid().ToString("N"));
            internal readonly Client Source=new Client();internal readonly ViveProEyeWorker Worker;
            internal readonly ViveDevelopmentParticipant Task;internal readonly ViveNamedSessionFrameAdmission Admission;
            internal readonly ViveNamedRawRecordingPump Pump;internal readonly ViveNamedRawRecordingPump.MemoryJournalFixture Writer;
            internal readonly ViveRawSessionJournalSink NativeSink;
            internal readonly ViveRawExclusiveDestination NativeDestination;
            internal ViveLegacyWozTaskOwner Owner;internal ViveLegacyWozRecordingBinding Recording;
            internal ViveLegacyWozParticipantProcess P;internal ViveLegacyWozOperatorProcess O;
            internal ViveLegacyWozControlledScene Scene;internal readonly ViveLegacyWozControlledScene.MemorySink Sink=new ViveLegacyWozControlledScene.MemorySink();
            internal ViveLegacyWozObservationOwner.SyntheticObservations Remote;
            internal readonly ViveLegacyWozProcessTransport.MemorySocket PS=new ViveLegacyWozProcessTransport.MemorySocket(),OS=new ViveLegacyWozProcessTransport.MemorySocket();
            long headSequence;int frame;long remoteSequence;
            internal Fixture(bool downgrade=false,ViveRawSessionJournalSink.IntegrationFixtureScenario? nativeScenario=null,bool shareParticipantAvatar=false,int? participantTask=null,bool photonTiming=false)
            {
                InitializationStage=1;
                var geometry=new AutomaticGazeGeometry(Rotation.Identity,new Vec3(0,0,1),new Rotation(0,Math.Sqrt(.5),0,Math.Sqrt(.5)),new Vec3(1,0,0),"woz-raw-geometry");
                var allocation=new Allocation("woz-raw-BCAD",new[]{Condition.B,Condition.C,Condition.A,Condition.D},GazeTarget.Participant,new[]{0,1,2,3});
                var design=ViveLegacyRecordingDesign.SelectOperatorWoz(Actor.Owner,"woz-raw-design",allocation,geometry.Version,ViveFixedTrackpadMapping.Version);
                var config=SessionConfiguration.LegacyRecordingDevelopment(new SessionConfiguration("woz-raw-session",73,allocation,RunKind.Simulation,"en",3,30,true,0,true,true,true),
                    design,ViveLegacyRecordingIntent.Select(Actor.Owner,"woz-raw-intent",true,true));
                if(participantTask.HasValue)
                {
                    ParticipantAssignment assignment;
                    Check.True(ParticipantAssignment.TryCreate("P017",participantTask==2?TaskKind.VisualSearch:TaskKind.Stroop,
                        participantTask==1?ResponseRule.WordMeaning:ResponseRule.InkColor,out assignment));
                    design=ViveLegacyRecordingDesign.SelectParticipant(Actor.Owner,"woz-study-design",assignment,geometry.Version,ViveFixedTrackpadMapping.Version,true);
                    var basis=new SessionConfiguration("woz-study-"+participantTask,ParticipantAssignment.StimulusSeed,assignment.Allocation,RunKind.Apparatus,"en",3,30,true,0,true,true,true,assignment.ResponseRule);
                    basis=SessionConfiguration.WithTaskKind(basis,assignment.TaskKind);
                    config=SessionConfiguration.ParticipantRecording(basis,design,ViveLegacyRecordingIntent.Select(Actor.Owner,"woz-study-intent",true,true));
                }
                Task=new ViveDevelopmentParticipant(config,Clock,geometry,ViveFixedTrackpadMapping.Create(),.5,true);
                double taskChoice=Clock.Now,eyeChoice=Clock.Now;
                Worker=new ViveProEyeWorker(Clock,()=>new ViveHeadFrame(++headSequence,Clock.Now,true,true,Rotation.Identity),()=>Source);
                Check.True(Worker.Start()&&Source.Waiting.WaitOne(3000));
                Admission=ViveNamedSessionFrameAdmission.CreateLegacySyntheticForTest(Task,Worker,Clock,256,taskChoice,eyeChoice);
                Check.True(Admission.ConfirmTask()&&Admission.ConfirmEye());InitializationStage=2;
                if(nativeScenario.HasValue)
                {
                    InitializationStage=3;
                    NativeSink=ViveRawSessionJournalSink.CreateWozIntegrationFixtureForTest(Admission,ViveRawByteBudget.SelectExplicit(67108864),nativeScenario.Value);
                    InitializationStage=4;
                    NativeDestination=ViveRawExclusiveDestination.CreateIntegrationFixtureForTest(Admission,NativeSink,Task.NamedRawRevocation);
                    InitializationStage=5;
                    Pump=ViveNamedRawRecordingPump.StartWozNativeFixtureForTest(Task,Worker,Clock,Admission,NativeSink);
                }
                else
                {
                    Check.True(Admission.TryBindQueueBudget(ViveRawByteBudget.SelectExplicit(67108864)));
                    Pump=ViveNamedRawRecordingPump.StartWozMemoryJournalFixture(Task,Worker,Clock,Admission,out Writer);
                }
                InitializationStage=6;
                Source.Send(null);Stage();Mutate(()=>Task.RecordLegacySoftwareChoices(Clock,taskChoice,eyeChoice));Neutral();BeginPractice();Neutral();
                InitializationStage=7;
                for(int i=0;i<8;i++)Answer();Check.Equal(Phase.BlockPreparation,Task.Phase);
                InitializationStage=8;
                Owner=Task.CreateLegacyWozTaskOwner(Clock);Recording=ViveLegacyWozRecordingBinding.CreateMemoryFixture(Owner,Admission,Worker,Pump);
                Remote=new ViveLegacyWozObservationOwner.SyntheticObservations(RemoteClock);PS.Other=OS;OS.Other=PS;
                Stage();using(var offer=ViveLegacyWozProcessPairing.CreateSyntheticOffer(Owner,"woz-local-owner",RemoteClock.ClockId,shareParticipantAvatar))
                {
                    char[] token=offer.RevealToken();
                    if(downgrade)
                    {
                        byte[] payload=Convert.FromBase64CharArray(token,9,token.Length-9);Array.Clear(token,0,token.Length);
                        try{payload[0]=1;token=("WOZPROC1."+Convert.ToBase64String(payload)).ToCharArray();}finally{Array.Clear(payload,0,payload.Length);}
                    }
                    try{using(var imported=ViveLegacyWozProcessPairing.Parse(token))
                    {P=ViveLegacyWozParticipantProcess.Memory(offer,Owner,new IPEndPoint(IPAddress.Loopback,45311),new IPEndPoint(IPAddress.Loopback,45312),5,PS,photonTiming);
                     O=ViveLegacyWozOperatorProcess.Synthetic(imported,RemoteClock,Remote,new IPEndPoint(IPAddress.Loopback,45312),new IPEndPoint(IPAddress.Loopback,45311),5,OS,photonTiming);
                     var creatorKey=offer.CopyKey();var importedKey=imported.CopyKey();try{Check.Equal(!downgrade,creatorKey.SequenceEqual(importedKey));}finally{Array.Clear(creatorKey,0,creatorKey.Length);Array.Clear(importedKey,0,importedKey.Length);}}}
                    finally{Array.Clear(token,0,token.Length);}
                }
                Check.True(P.Start()&&O.Start());Check.Equal(!downgrade,O.ParticipantLocalRecordingDeclared);
                if(downgrade){Check.True(O.ConfirmLocalOperatorSource());Check.True(!O.ConfirmForwardingAndParticipantLocalStorage());}
                else{Check.True(!O.ConfirmLocalOperatorSource());Check.True(O.ConfirmForwardingAndParticipantLocalStorage());}Drain();
                Scene=ViveLegacyWozControlledScene.CreateMemoryFixture(P,Sink);InitializationStage=9;
            }
            internal void Stage()
            {
                // End the previous owner-frame epoch before the next provider
                // observation replaces it, as the real scene's EndFrame does.
                Check.True(Pump.DrainTaskEvents(),"previous frame drain: "+Pump.FixtureDiagnosticsForTest());
                int next=++frame;var ray=new ViveEyeRay(3,true,true,new Vec3(-31.25,2.5,3.75),new Vec3(.3,.4,.5));
                Source.Send(new ViveProEyePoll(null,new ViveEyeFrame(next,next*8,Clock.Now,false,ray,ray,ray),ViveEyeRuntimeState.Working,ViveEyeCalibrationState.ReportedReady,true,0,2));
                ViveProEyePoll poll;Check.True(Worker.TryPollForRecording(out poll));Check.True(Pump.StageRigFrame(poll,1),"stage "+Task.Phase+": "+Pump.FixtureDiagnosticsForTest());
            }
            internal void Drain(){Check.True(SpinWait.SpinUntil(()=>Admission.Count==0&&!Pump.HasReservedIo,3000),"consumer drain: "+Pump.FixtureDiagnosticsForTest());}
            internal void Mutate(Action action){Stage();Check.True(Pump.TryRunReservedOrdinaryMutation(action),"mutation: "+Pump.FixtureDiagnosticsForTest());Drain();}
            internal void Neutral(){Mutate(()=>Task.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,false,0,0)));}
            void BeginPractice()
            {
                JournalEvent prepared=null;Mutate(()=>prepared=Task.PrepareLegacyRecordingBlockPlan(Actor.Owner,true));
                ViveBlockPlanRequest request;Check.True(ViveBlockPlanRequest.TryCapture(Task,Clock,prepared,out request));
                if(NativeSink!=null)BeginNative(request,true);
                else{ViveNamedSessionFrameAdmission.SyntheticPlanProof proof;Check.True(Admission.TryIssueSyntheticPlanProof(request,prepared,out proof));
                    Check.True(Task.TryBeginSyntheticPracticeForFixture(Admission,request,proof));}
                Check.True(Pump.DrainTaskEvents());Drain();
            }
            ViveRawSessionJournalSink.BlockPlanWriteReceipt NativePlan(ViveBlockPlanRequest request)
            {
                Check.True(Pump.DrainTaskEvents());ViveRawSessionJournalSink.BlockPlanWriteReceipt receipt=null;
                var deadline=DateTime.UtcNow.AddSeconds(10);
                while(DateTime.UtcNow<deadline)
                {var state=Pump.TryGetBlockPlanReceipt(request,out receipt);if(state==ViveRawSessionJournalSink.BlockPlanReceiptPoll.Ready)break;
                    Check.Equal(ViveRawSessionJournalSink.BlockPlanReceiptPoll.Pending,state);Thread.Sleep(5);}
                Check.True(receipt!=null&&receipt.BelongsToNativeFixtureForTest(NativeSink),"actual fixed native plan receipt");return receipt;
            }
            void BeginNative(ViveBlockPlanRequest request,bool practice)
            {var receipt=NativePlan(request);bool started=false;Mutate(()=>started=Task.TryBeginNativeFixtureWithPlan(Admission,NativeSink,request,receipt,practice));Check.True(started,"native exact plan begin");}
            internal void Answer()
            {
                Stage();var trial=Task.CurrentTrial;ViveNamedSessionFrameAdmission.PresentationReservation token;
                bool reserved=Pump.TryPreparePresentation(frame,frame,out token);
                Check.True(reserved,"reserve presentation "+trial.TrialId+" "+(Recording==null?"practice":Recording.LastPresentationIssue)+" "+Pump.FixtureDiagnosticsForTest());
                if(Task.Phase==Phase.MainTask){Scene.Present();Check.True(token.WozEvidence!=null&&token.WozEvidence.Applied,"exact owned scene applied");}
                Check.True(token.Search==null?Pump.CommitPresentation(token,frame,frame,trial.TrialId,token.Word,token.Ink,Clock.Now):
                    Pump.CommitVisualSearchPresentation(token,frame,frame,trial.TrialId,token.Search.LayoutId,Clock.Now),"commit presentation");
                if(Task.Phase==Phase.MainTask)
                {
                    // The real scene rechecks immediately after onset, before
                    // an answer or the next trial can obtain fresh authority.
                    double checkedAt=Clock.Now;var used=token.WozEvidence==null?null:token.WozEvidence.Pose;
                    string timing=used==null?" no-avatar":" receipt_age="+(checkedAt-used.OriginalReceiptParticipantSeconds).ToString("R")+
                        " task_age="+(checkedAt-used.OriginalTaskIssuedParticipantSeconds).ToString("R")+" cap="+P.LocalReceiptBudgetSeconds.ToString("R");
                    Scene.RecheckPresentation();Check.True(!Scene.Closed&&Scene.MayShowTask,
                        "own onset preserves current presentation authority;"+timing+" trial="+trial.TrialId+" process="+P.LastIssue+" raw="+Pump.FixtureDiagnosticsForTest());
                }
                // Unity does not synchronously wait for the writer before its
                // presentation check. Drain only after checking that boundary;
                // expiry during a slow flush is tested separately as a stop.
                Drain();
                if(trial.VisualSearch!=null)Mutate(()=>Task.SelectVisualSearchItem(trial.TrialId,trial.VisualSearch.TargetId));
                else
                {var c=trial.ExpectedResponse;Mutate(()=>Task.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,true,c==Colour.Green?1:c==Colour.Yellow?-1:0,c==Colour.Red?1:c==Colour.Blue?-1:0)));}
                Neutral();
            }
            internal void Grant()
            {Stage();Check.True(P.PublishCurrentTask(),P.LastIssue);Check.True(P.ConfirmCurrentSource(),P.LastIssue);O.Tick();P.Tick();Check.True(P.CurrentTaskAcknowledged,P.LastIssue);}
            internal void Preflight()
            {Stage();Check.True(P.BeginPhasePreflight(),P.LastIssue);O.Tick();Remote.Fresh(++remoteSequence,(int)remoteSequence,(int)remoteSequence);Check.True(O.PollPhasePreflight(),O.LastIssue);P.Tick();Check.True(P.CurrentPreflight!=null,P.LastIssue);}
            internal void Pose()
            {Stage();Remote.Fresh(++remoteSequence,(int)remoteSequence,(int)remoteSequence);Check.True(O.PollObservation(),O.LastIssue);P.Tick();Check.True(P.CurrentPose!=null,P.LastIssue);}
            internal void Confirm()
            {Mutate(()=>Task.UpdateAuxiliary(false,false,false,0,true));Mutate(()=>Task.UpdateAuxiliary(true,false,false,0,true));if(!Task.Completed)Mutate(()=>Task.UpdateAuxiliary(false,false,false,0,true));}
            internal ViveBlockPlanRequest PreparePlan(out ViveNamedSessionFrameAdmission.SyntheticPlanProof plan)
            {JournalEvent prepared=null;Mutate(()=>prepared=Task.PrepareLegacyRecordingBlockPlan(Actor.Owner,false));
                ViveBlockPlanRequest request;Check.True(ViveBlockPlanRequest.TryCapture(Task,Clock,prepared,out request));
                plan=null;if(NativeSink==null)Check.True(Admission.TryIssueSyntheticPlanProof(request,prepared,out plan));return request;}
            internal ViveBlockPlanRequest BeginMain()
            {
                ViveNamedSessionFrameAdmission.SyntheticPlanProof plan;var request=PreparePlan(out plan);
                var nativeReceipt=NativeSink==null?null:NativePlan(request);
                Grant();if(Owner.SceneTask.Condition!=Condition.D)Preflight();
                Stage();Check.True(request.TryValidateCurrentForReceipt(Task,Clock),"plan remains exact across typed evidence only");
                if(NativeSink==null)Check.True(Task.TryBeginSyntheticLegacyBlockForFixture(Admission,request,plan),"recorded block start");
                else{bool started=false;Check.True(Pump.TryRunReservedOrdinaryMutation(()=>started=Task.TryBeginNativeFixtureWithPlan(Admission,NativeSink,request,nativeReceipt,false)));Check.True(started,"native recorded block start");}
                Drain();
                Check.True(!request.TryValidateCurrentForReceipt(Task,Clock),"phase change rejects prior plan receipt");
                Check.Equal(Phase.Countdown,Task.Phase);Check.True(Task.TryStartLegacyRawCountdownFromSoftwareWitness(frame,frame,Clock.Now));Check.True(Pump.DrainTaskEvents());Drain();
                Grant();if(Owner.SceneTask.Condition!=Condition.D)Preflight();Check.True(Pump.DrainTaskEvents());
                Thread.Sleep((int)Math.Ceiling(Task.CountdownRemainingSeconds*1000)+20);Mutate(()=>Task.Tick());Neutral();Check.Equal(Phase.MainTask,Task.Phase);
                return request;
            }
            internal ViveNamedSessionFrameAdmission.PresentationReservation ReserveFirstMain()
            {BeginMain();Grant();Preflight();Pose();Stage();ViveNamedSessionFrameAdmission.PresentationReservation token;
                Check.True(Pump.TryPreparePresentation(frame,frame,out token),"first main reserve "+Recording.LastPresentationIssue);return token;}
            internal bool Commit(ViveNamedSessionFrameAdmission.PresentationReservation token)
            {return Pump.CommitPresentation(token,token.Frame,token.Revision,token.Trial.TrialId,token.Word,token.Ink,Clock.Now);}
            internal void Complete()
            {
                for(int block=0;block<4;block++)
                {
                    var request=BeginMain();
                    for(int i=0;i<24;i++){Grant();if(Owner.SceneTask.Condition!=Condition.D){Preflight();Pose();}Answer();}
                    Check.True(!request.TryValidateCurrentForReceipt(Task,Clock),"later trial cannot reuse plan receipt");
                    Check.Equal(Phase.Questionnaire,Task.Phase);Grant();Confirm();for(int q=0;q<Questionnaire.Count;q++)Confirm();
                    if(block<3){Check.Equal(Phase.Rest,Task.Phase);Grant();Confirm();Check.Equal(Phase.BlockReady,Task.Phase);}
                }
                Check.True(Task.Completed&&Recording.CompletedTaskObserved);Check.True(P.CurrentPose==null&&!P.Terminal&&Admission.State==0);
                Check.True(Recording.RequestCompletedDrain(),"actual completed drain: "+Pump.FixtureDiagnosticsForTest());
                Check.True(SpinWait.SpinUntil(()=>Pump.HasExited,NativeSink==null?3000:90000),"completed writer bounded wait");
                if(NativeSink==null){Check.Equal(ViveNamedRawRecordingPump.Outcome.CloseCallsReturned,Pump.State);Check.Equal(1,Writer.CompleteCalls);Check.Equal(0,Writer.CleanupCalls);}
            }
            public void Dispose()
            {if(P!=null)P.Dispose();if(O!=null)O.Dispose();if(!Pump.HasExited)Pump.Stop();Check.True(SpinWait.SpinUntil(()=>Pump.HasExited,3000));Admission.Withdraw();Worker.TryRequestStop();Source.Release();Check.True(Worker.Stop(1000));Worker.Dispose();}
        }
        static ViveRawSessionReadResult Read(byte[] bytes)
        {using(var input=new MemoryStream(bytes,false))return ViveRawSessionJournalReader.ProjectTrials(input,true,true,16777216,20000,CancellationToken.None);}
        internal static void RegisterFocusHoldTests(TestSuite suite)
        {
            suite.Run("WoZ pending plan XR focus hold revokes positives without consuming plan or stopping local raw",()=>{
                using(var f=new Fixture())
                {
                    ViveNamedSessionFrameAdmission.SyntheticPlanProof proof;var request=f.PreparePlan(out proof);
                    f.Stage();Check.True(f.Pump.ProcessFocusState(true,false));
                    Check.True(f.Task.Phase==Phase.BlockReady&&f.Task.IsXrFocusInteractionHeld&&!f.Task.IsFocusHoldPaused);
                    Check.True(f.P.TickFocusHoldSuspension()&&f.P.FocusSuspendStarted&&f.P.FocusForwardingSuspended);
                    Check.True(f.P.FocusForwardingRevokeNotApplicable&&!f.P.SourceRevokeAcknowledged&&f.P.FocusForwardingCleanupUnknown);
                    Check.True(f.P.RecordFocusSuspensionMetadataForCurrentFrame());
                    Check.True(!request.TryValidateCurrentForReceipt(f.Task,f.Clock)&&!f.Admission.ConsumeSyntheticPlanProof(proof,request));
                    Check.True(f.Recording.Current&&f.Admission.State==0&&f.Pump.State==ViveNamedRawRecordingPump.Outcome.Running);
                    f.Stage();Check.True(f.Pump.ProcessFocusState(true,true)&&f.P.TickFocusHoldSuspension());
                    Check.True(f.Task.Phase==Phase.BlockReady&&!f.P.LocalChoiceCurrent&&!f.Scene.CanBeginCountdown&&f.P.CurrentPose==null);
                    Check.True(f.Pump.DrainTaskEvents());f.Drain();var result=Read(f.Writer.Snapshot());
                    Check.Equal(ViveRawSessionReadStatus.VerifiedPrefix,result.Status);
                    Check.True(result.TrialProjection.ObservedSafetyEvents.Any(item=>item.Kind=="focus_forwarding_suspended"&&
                        item.Detail.Contains("NOT_APPLICABLE_NO_POSITIVE_GRANT")&&item.Detail.Contains("remote_physical_stop=NOT_ESTABLISHED")));
                    Check.Equal(0,f.Writer.CompleteCalls);
                }
            });
            suite.Run("WoZ pending plan retains strict source across desktop focus and typed evidence only",()=>{
                using(var f=new Fixture())
                {
                    ViveNamedSessionFrameAdmission.SyntheticPlanProof proof;var request=f.PreparePlan(out proof);
                    f.Grant();f.Preflight();
                    f.Stage();Check.True(f.Pump.ProcessFocusState(false,true));
                    f.Grant();f.Preflight();
                    f.Stage();Check.True(f.Pump.ProcessFocusState(true,true));
                    Check.True(request.TryValidateCurrentForReceipt(f.Task,f.Clock));
                    f.P.RevokeSource();Check.True(!request.TryValidateCurrentForReceipt(f.Task,f.Clock));
                }
            });
            suite.Run("WoZ focus hold revokes forwarding with real ack while local raw capture continues",()=>{
                using(var f=new Fixture())
                {
                    f.BeginMain();f.Grant();f.Preflight();f.Pose();f.Stage();
                    Check.True(f.Pump.ProcessFocusState(true,false));
                    Check.True(f.Task.IsFocusHoldPaused&&f.Task.Phase==Phase.Paused&&!f.P.LocalChoiceCurrent);
                    Check.True(f.P.TickFocusHoldSuspension()&&f.P.FocusForwardingSuspending&&!f.P.Terminal);
                    Check.True(f.Recording.Current&&f.Pump.State==ViveNamedRawRecordingPump.Outcome.Running);
                    f.O.Tick();Check.True(f.O.Terminal&&f.O.SourceRevokeAcknowledged);
                    Check.True(f.P.TickFocusHoldSuspension()&&f.P.FocusForwardingSuspended&&f.P.Terminal);
                    Check.True(f.P.SourceRevokeAcknowledged&&!f.P.FocusForwardingCleanupUnknown);
                    Check.True(f.P.RecordFocusSuspensionMetadataForCurrentFrame());
                    Check.True(f.P.CurrentPose==null&&f.P.CurrentPreflight==null&&!f.P.CurrentTaskAcknowledged&&!f.P.LocalChoiceCurrent);
                    Check.True(f.Scene.ClearForSafety());Check.True(f.Sink.Output==null);
                    f.Stage();Check.True(f.Pump.ProcessFocusState(true,true));
                    Check.True(f.P.TickFocusHoldSuspension()&&f.Task.Phase==Phase.Paused&&f.Task.IsFocusHoldPaused);
                    Check.True(f.Recording.Current&&f.Admission.State==0&&f.Pump.State==ViveNamedRawRecordingPump.Outcome.Running);
                    Check.True(f.Pump.DrainTaskEvents());f.Drain();
                    var result=Read(f.Writer.Snapshot());
                    Check.Equal(ViveRawSessionReadStatus.VerifiedPrefix,result.Status);
                    Check.True(result.TrialProjection.ObservedSafetyEvents.Any(item=>item.Kind=="focus_changed")&&!result.CaptureTerminalObserved);
                    Check.True(result.TrialProjection.ObservedSafetyEvents.Any(item=>item.Kind=="focus_forwarding_suspended"&&
                        item.Detail.Contains("remote_software_ack=RECEIVED")&&item.Detail.Contains("remote_physical_stop=NOT_ESTABLISHED")));
                    Check.Equal(0,f.Writer.CompleteCalls);
                }
            });
            suite.Run("WoZ focus revoke timeout marks remote cleanup unknown without stopping healthy local raw",()=>{
                using(var f=new Fixture())
                {
                    f.BeginMain();f.Grant();f.Preflight();f.Pose();f.Stage();
                    Check.True(f.Pump.ProcessFocusState(true,false));
                    Check.True(f.P.TickFocusHoldSuspension()&&f.P.FocusForwardingSuspending);
                    // Deliberately do not pump the remote peer; no ack exists.
                    var waitUntil=DateTime.UtcNow.AddMilliseconds(5100);
                    while(!f.P.FocusForwardingSuspended&&DateTime.UtcNow<waitUntil)
                    {Thread.Sleep(20);f.Stage();Check.True(f.P.TickFocusHoldSuspension());}
                    Check.True(f.P.FocusForwardingSuspended&&f.P.FocusForwardingCleanupUnknown&&!f.P.SourceRevokeAcknowledged);
                    Check.True(f.P.RecordFocusSuspensionMetadataForCurrentFrame());
                    Check.True(f.P.Terminal&&!f.P.LocalChoiceCurrent&&f.P.CurrentPose==null&&f.Sink.Output==null);
                    Check.True(f.Recording.Current&&f.Admission.State==0&&f.Pump.State==ViveNamedRawRecordingPump.Outcome.Running);
                    f.Stage();Check.True(f.Pump.ProcessFocusState(true,true));
                    Check.True(f.Task.Phase==Phase.Paused&&f.P.TickFocusHoldSuspension());
                    f.P.Dispose();Check.True(!f.Recording.Current&&f.Admission.State!=0);
                }
            });
            suite.Run("WoZ ordinary pause cannot claim the negative focus suspension exception",()=>{
                using(var f=new Fixture())
                {
                    f.BeginMain();f.Grant();f.Preflight();f.Mutate(()=>f.Task.OwnerPause(Actor.Owner));
                    Check.True(f.Task.Phase==Phase.Paused&&!f.Task.IsFocusHoldPaused);
                    Check.True(!f.P.TickFocusHoldSuspension()&&f.P.Terminal);
                    Check.True(!f.Recording.Current&&f.Admission.State!=0);
                }
            });
            suite.Run("WoZ authenticated operator stop during focus cleanup still stops local raw",()=>{
                using(var f=new Fixture())
                {
                    f.BeginMain();f.Grant();f.Preflight();f.Pose();f.Stage();
                    Check.True(f.Pump.ProcessFocusState(true,false)&&f.P.TickFocusHoldSuspension(),"start negative focus cleanup");
                    Check.True(f.P.FocusForwardingSuspending&&f.Recording.Current,"pending cleanup retains local raw");
                    Check.True(f.O.RequestSafetyStop(),"operator authenticated stop sent: "+f.O.LastIssue);
                    bool held=f.P.TickFocusHoldSuspension();
                    Check.True(!held&&f.P.Terminal&&f.P.SafetyStopAcknowledged,"actual stop receipt: held="+held+", terminal="+f.P.Terminal+", ack="+f.P.SafetyStopAcknowledged+", issue="+f.P.LastIssue);
                    Check.True(!f.Recording.Current&&f.Admission.State!=0&&!f.P.FocusForwardingSuspended,"actual stop revokes raw binding");
                }
            });
        }
        internal static void RegisterParticipant(TestSuite suite,int? onlyTask=null)
        {
            if(!onlyTask.HasValue)RegisterDisplayTiming(suite);
            for(int mode=0;mode<3;mode++)
            {
                if(onlyTask.HasValue&&onlyTask.Value!=mode)continue;
                int captured=mode;
                suite.Run("Assigned WoZ actual paired recording pump completes four blocks for task "+mode,()=>{
                    using(var f=new Fixture(participantTask:captured))
                    {
                        f.Complete();byte[] bytes=f.Writer.Snapshot();
                        Check.Equal("VRAW0007",Encoding.ASCII.GetString(bytes,0,8));
                        AssertPoseUsedRows(bytes);
                        var analysis=new StudyAnalysis();
                        using(var input=new MemoryStream(bytes,false))
                        {var read=StudyDataReader.Read(input,"vraw",analysis.Accept,CancellationToken.None);Check.Equal("Complete",read.Status,read.Message);}
                        analysis.Finish();Check.Equal("P017",analysis.ParticipantId);Check.Equal("OperatorWoZ",analysis.ControlMode);
                        Check.Equal(new[]{"InkColor","WordMeaning","VisualSearch"}[captured],analysis.ParticipantTask);
                        Check.Equal(96,analysis.Trials.Count(x=>x.Scope=="Formal"));Check.Equal(4,analysis.EndedBlocks);Check.Equal(20,analysis.Ratings.Count);
                        Check.True(analysis.Trials.Where(x=>x.Scope=="Formal").All(x=>x.Correct==true));
                        Check.True(analysis.EventCounts[ViveLegacyWozRecordingSchema.EventKind]>0);
                        if(captured==2)
                        {
                            Check.Equal(48,analysis.Trials.Count(x=>x.Scope=="Formal"&&x.SetSize==12));
                            Check.Equal(48,analysis.Trials.Count(x=>x.Scope=="Formal"&&x.SetSize==24));
                        }
                        using(var input=new MemoryStream(bytes,false))using(var output=new MemoryStream())
                        {
                            Check.Equal(ViveRawReadableAnalysisCsv.Outcome.WrittenAndFlushed,
                                ViveRawReadableAnalysisCsv.WriteSyntheticForTest(input,output,16777216,20000,268435456,CancellationToken.None));
                            var csv=new StudyAnalysis();using(var readOnly=new MemoryStream(output.ToArray(),false))
                            {var read=StudyDataReader.Read(readOnly,"csv",csv.Accept,CancellationToken.None);Check.Equal("Complete",read.Status,read.Message);}
                            csv.Finish();Check.Equal(analysis.ParticipantId,csv.ParticipantId);Check.Equal(analysis.ParticipantTask,csv.ParticipantTask);
                            Check.Equal(analysis.ConditionOrder,csv.ConditionOrder);Check.Equal(analysis.ListByBlock,csv.ListByBlock);
                            Check.Equal(analysis.ControlMode,csv.ControlMode);Check.Equal(analysis.Trials.Count,csv.Trials.Count);
                        }
                    }
                });
            }
        }
        internal static void RegisterDisplayTiming(TestSuite suite)
        {
            foreach(bool failClear in new[]{true,false})
            {
                bool selected=failClear;
                suite.Run("WoZ held display leaves an open endpoint after "+(selected?"failed sink clear":"recording source failure"),()=>{
                    using(var f=new Fixture(participantTask:0,photonTiming:true))
                    {
                        f.BeginMain();f.Grant();f.Preflight();f.Pose();f.Stage();f.Scene.Present();
                        Thread.Sleep(180);f.Stage();f.Scene.Present();f.Drain();
                        if(selected)
                        {f.Sink.FailClear=true;Check.True(!f.Scene.ClearForSafety());f.P.Dispose();}
                        else
                        {
                            f.Pose();f.Stage();
                            f.Sink.DuringApply=()=>{f.Worker.TryRequestStop();f.Source.Release();};
                            f.Scene.Present();f.Scene.RecheckPresentation();
                        }
                        Check.True(!f.Recording.Current);Check.True(SpinWait.SpinUntil(()=>f.Pump.HasExited,3000));
                        var a=new StudyAnalysis();using(var input=new MemoryStream(f.Writer.Snapshot(),false))
                            Check.Equal("Incomplete",StudyDataReader.Read(input,"vraw",a.Accept,CancellationToken.None).Status);
                        a.Finish();Check.Equal(1,a.WozPoseHolds.Count);Check.True(!a.WozPoseHolds[0].FollowingObservedTime.HasValue);
                        Check.True(a.WozPoseHolds[0].EndObservation.StartsWith("OPEN_",StringComparison.Ordinal));
                        Check.Equal(0,f.Writer.CompleteCalls);
                    }
                });
            }
            suite.Run("Owned Photon-timed scene records held display recovery and expiry through the actual raw admission",()=>{
                using(var f=new Fixture(participantTask:0,photonTiming:true))
                {
                    f.BeginMain();f.Grant();f.Preflight();f.Pose();f.Stage();f.Scene.Present();
                    Check.True(!f.Scene.Closed&&f.Scene.MayShowTask);
                    Thread.Sleep(180);f.Stage();f.Scene.Present();f.Drain();
                    f.Pose();f.Scene.Present();f.Drain();
                    Thread.Sleep(180);f.Stage();f.Scene.Present();f.Drain();
                    Thread.Sleep(160);f.Stage();f.Scene.RecheckPresentation();
                    Check.True(f.P.Terminal&&f.Scene.Closed);Check.True(SpinWait.SpinUntil(()=>f.Pump.HasExited,3000));
                    var a=new StudyAnalysis();using(var input=new MemoryStream(f.Writer.Snapshot(),false))
                        Check.Equal("Incomplete",StudyDataReader.Read(input,"vraw",a.Accept,CancellationToken.None).Status);
                    a.Finish();Check.Equal(2,a.WozPoseHolds.Count);
                    Check.Equal("FRESH_POSE_OBSERVED",a.WozPoseHolds[0].EndObservation);
                    // Immediate safety stop may discard queued clear evidence.
                    // Never delay stopping or manufacture an endpoint to fill it.
                    Check.True(a.WozPoseHolds[1].EndObservation=="DISPLAY_CLEARED_OBSERVED"||
                        a.WozPoseHolds[1].EndObservation=="OPEN_NO_FOLLOWING_OBSERVATION");
                    Check.True(a.WozPoseHolds.All(x=>x.ReceiptOrigin=="MEMORY"&&x.FirstReceiptAge>.15&&x.LastReceiptAge<=.3&&x.Cap==.3));
                    Check.Equal(0,f.Writer.CompleteCalls); // Expiry never invents a completed capture.
                }
            });
        }
        public static void Register(TestSuite suite)
        {
            RegisterParticipant(suite);
            RegisterFocusHoldTests(suite);
            suite.Run("WoZ recorded duplex renders participant gaze without a reverse journal event",()=>{
                using(var f=new Fixture(shareParticipantAvatar:true))
                {
                    Check.True(f.O.ParticipantLocalRecordingDeclared&&f.O.ParticipantAvatarSharingDeclared);
                    f.BeginMain();f.Grant();f.Preflight();f.Pose();
                    var forward=f.P.CurrentPose;
                    Check.True(forward!=null&&f.Pump.DrainTaskEvents());
                    f.Drain();int eventCount=f.Task.LegacyWozEventCursor;
                    byte[] journal=f.Writer.Snapshot();
                    var head=new Rotation(0,.6,0,.8);var eye=new Vec3(0,-.6,.8);
                    Check.True(f.P.TrySendParticipantAvatarPose(head,eye,f.Clock.Now),"recorded participant pose sent");
                    f.O.Tick();Rotation receivedHead;Vec3 receivedEye;
                    Check.True(f.O.TryGetParticipantAvatarPose(out receivedHead,out receivedEye));
                    Check.Equal(head,receivedHead);Check.Equal(eye,receivedEye);
                    f.P.ClearParticipantAvatarPose();f.O.Tick();
                    Check.True(!f.O.TryGetParticipantAvatarPose(out receivedHead,out receivedEye));
                    Check.True(Object.ReferenceEquals(forward,f.P.CurrentPose)&&f.Recording.Current);
                    Check.True(f.Pump.DrainTaskEvents());f.Drain();
                    Check.Equal(eventCount,f.Task.LegacyWozEventCursor);
                    Check.True(journal.SequenceEqual(f.Writer.Snapshot()),"display and clear leave journal bytes unchanged");
                }
            });
            suite.Run("WoZ raw callback without owned apply cannot create onset or PoseUsed",()=>{
                using(var f=new Fixture()){var token=f.ReserveFirstMain();Check.True(!token.WozEvidence.Applied&&!f.Commit(token));
                    Check.True(!token.Trial.Onset.HasValue);f.Scene.RecheckPresentation();Check.Equal(Phase.Paused,f.Task.Phase);Check.Equal(0,f.Writer.CompleteCalls);}
            });
            suite.Run("WoZ apply then clear invalidates the exact pending software witness",()=>{
                using(var f=new Fixture()){var token=f.ReserveFirstMain();f.Scene.Present();Check.True(token.WozEvidence.Applied);
                    Check.True(f.Scene.ClearForSafety());Check.True(!token.WozEvidence.Applied&&!f.Commit(token));Check.True(!token.Trial.Onset.HasValue);}
            });
            suite.Run("WoZ original pose age expires between apply and raw callback",()=>{
                using(var f=new Fixture()){var token=f.ReserveFirstMain();f.Scene.Present();Check.True(token.WozEvidence.Applied);Thread.Sleep(180);
                    Check.True(!f.Commit(token)&&!token.Trial.Onset.HasValue);f.Scene.RecheckPresentation();Check.Equal(Phase.Paused,f.Task.Phase);}
            });
            suite.Run("WoZ apply exception clears sink and revokes raw admission before any callback",()=>{
                using(var f=new Fixture()){var token=f.ReserveFirstMain();f.Sink.DuringApply=()=>{throw new InvalidOperationException("sealed apply failure");};f.Scene.Present();
                    Check.True(f.Sink.Output==null&&f.P.Terminal&&f.Admission.State!=0&&!token.Trial.Onset.HasValue);Check.Equal(Phase.Paused,f.Task.Phase);Check.True(!f.P.SafetyStopAcknowledged);}
            });
            suite.Run("WoZ plan evidence-only extension rejects revoked source and participant withdrawal before consume",()=>{
                foreach(bool withdrawal in new[]{false,true})using(var f=new Fixture())
                {ViveNamedSessionFrameAdmission.SyntheticPlanProof proof;var request=f.PreparePlan(out proof);f.Grant();f.Preflight();Check.True(request.TryValidateCurrentForReceipt(f.Task,f.Clock));
                    if(withdrawal)f.Task.ParticipantWithdraw();else f.P.RevokeSource();
                    Check.True(f.Task.LegacyWozRecordingDeclared&&!f.Recording.Current&&!f.Recording.CompletedTaskObserved);
                    Check.True(!request.TryValidateCurrentForReceipt(f.Task,f.Clock)&&!f.Admission.ConsumeSyntheticPlanProof(proof,request));
                    Check.True(!f.Recording.RequestCompletedDrain());Check.Equal(0,f.Writer.CompleteCalls);}
            });
            suite.Run("WoZ plan refresh tail has an exact 256 event bound",()=>{
                using(var f=new Fixture()){ViveNamedSessionFrameAdmission.SyntheticPlanProof proof;var request=f.PreparePlan(out proof);
                    for(int i=0;i<256;i++){f.Grant();f.Drain();}Check.True(request.TryValidateCurrentForReceipt(f.Task,f.Clock));
                    f.Grant();f.Drain();Check.True(!request.TryValidateCurrentForReceipt(f.Task,f.Clock));}
            });
            suite.Run("WoZ uncertain local transport close cannot claim completed capture or remote stop",()=>{
                using(var f=new Fixture()){f.Grant();f.Preflight();f.PS.FailClose=true;f.P.Dispose();Check.True(f.Owner.Terminal&&!f.Recording.Current);
                    Check.True(!f.P.SourceRevokeAcknowledged&&!f.P.SafetyStopAcknowledged&&!f.Recording.RequestCompletedDrain());Check.Equal(0,f.Writer.CompleteCalls);}
            });
            suite.Run("WoZ V2 token rewritten as V1 cannot authenticate recorded owner traffic",()=>{
                using(var f=new Fixture(true))
                {f.Stage();Check.True(f.P.PublishCurrentTask()&&f.P.ConfirmCurrentSource());f.O.Tick();Check.True(f.O.Terminal&&!f.O.SourceConfirmed);f.P.Tick();Check.True(!f.P.CurrentTaskAcknowledged&&f.P.CurrentPreflight==null);}
            });
            suite.Run("WoZ memory recording join rejects foreign sources missing pump and installed admission route",()=>{
                using(var f=new Fixture())using(var foreign=new ViveProEyeWorker(f.Clock,()=>null,()=>new Client()))
                {
                    Check.Throws<InvalidOperationException>(()=>ViveLegacyWozRecordingBinding.CreateMemoryFixture(f.Owner,f.Admission,foreign,f.Pump));
                    Check.Throws<InvalidOperationException>(()=>ViveLegacyWozRecordingBinding.CreateMemoryFixture(f.Owner,f.Admission,f.Worker,null));
                    Check.Throws<InvalidOperationException>(()=>ViveLegacyWozRecordingBinding.CreateInstalled(f.Owner,f.Admission,f.Worker,f.Pump));
                    Check.True(f.Recording.Current&&!f.Owner.Terminal);
                }
            });
            suite.Run("WoZ real status pump journals actual owner practice and accepted memory source evidence",()=>{
                using(var f=new Fixture())
                {
                    f.Grant();f.Preflight();f.Drain();var proof=f.P.CurrentPreflight;var original=proof.OriginalReceiptParticipantSeconds;
                    var before=Read(f.Writer.Snapshot());Check.Equal(ViveRawSessionReadStatus.VerifiedPrefix,before.Status);
                    Check.True(before.TrialProjection.OperatorWozRecordingDeclared);Check.Equal(8,before.TrialProjection.Trials.Count);
                    Check.True(before.VerifiedObservationRecords>8&&before.VerifiedStatusRecords>0&&!before.CaptureTerminalObserved);
                    f.Mutate(()=>f.Task.Tick());Check.Equal(original,f.P.CurrentPreflight.OriginalReceiptParticipantSeconds);
                    Check.True(Object.ReferenceEquals(proof,f.P.CurrentPreflight));
                    Check.True(f.P.RevokeSource());Check.Equal(Phase.Paused,f.Task.Phase);Check.True(!f.Recording.Current&&f.Admission.State!=0);
                    f.O.Tick();f.P.Tick();Check.True(f.P.SourceRevokeAcknowledged);Check.True(SpinWait.SpinUntil(()=>f.Pump.HasExited,3000));
                    Check.Equal(0,f.Writer.CompleteCalls);var stopped=Read(f.Writer.Snapshot());Check.True(!stopped.CaptureTerminalObserved&&!stopped.CompleteSessionProven);
                    using(var input=new MemoryStream(f.Writer.Snapshot(),false))using(var output=new MemoryStream())
                        Check.True(ViveRawReadableAnalysisCsv.WriteSyntheticForTest(input,output,16777216,20000,33554432,CancellationToken.None)!=ViveRawReadableAnalysisCsv.Outcome.WrittenAndFlushed);
                }
            });
            suite.Run("WoZ full actual BCAD pump completion preserves local raw trials ratings and separate operator CSV provenance",()=>{
                using(var f=new Fixture())
                {
                    f.Complete();var bytes=f.Writer.Snapshot();AssertPoseUsedRows(bytes);var result=Read(bytes);Check.Equal(ViveRawSessionReadStatus.VerifiedPrefix,result.Status);
                    Check.True(result.CaptureTerminalObserved&&!result.CompleteSessionProven&&!result.SourceAuthenticated);
                    Check.Equal(104,result.TrialProjection.Trials.Count);Check.Equal(20,result.TrialProjection.Ratings.Count);Check.Equal(5,result.TrialProjection.PreparedPlans.Count);
                    Check.Equal("LEGACY_ABCD",result.TrialProjection.DesignVariant);Check.True(result.TrialProjection.OperatorWozRecordingDeclared);
                    using(var input=new MemoryStream(bytes,false))using(var output=new MemoryStream())
                    {
                        // Match the current CSV budget: expanded eye/AOI columns also widen long-form WoZ rows.
                        const long maximumCsvBytes=268435456;
                        Check.Equal(ViveRawReadableAnalysisCsv.Outcome.WrittenAndFlushed,ViveRawReadableAnalysisCsv.WriteSyntheticForTest(input,output,16777216,20000,maximumCsvBytes,CancellationToken.None));
                        Check.True(output.Length>0&&output.Length<=maximumCsvBytes,"CSV remains within its explicit output budget");
                        var csv=Encoding.UTF8.GetString(output.ToArray());Check.True(csv.Contains("woz_network_evidence")&&csv.Contains("woz_receipt_participant_time")&&csv.Contains("woz_head_operator_time"));
                        Check.True(csv.Contains("PoseUsed")&&csv.Contains("INDEPENDENT_NOT_SYNCHRONIZED")&&csv.Contains("HMAC_SHARED_KEY_POSSESSION")&&csv.Contains("NATIVE_TIME_OPAQUE")&&csv.Contains("INGEST_NOT_SAMPLE_ALIGNMENT"));
                        Check.True(csv.Contains(f.Clock.ClockId)&&csv.Contains(f.RemoteClock.ClockId)&&!csv.Contains("WOZPROC2."));
                        Check.True(csv.Contains("NOT_PROVEN_BY_PARTICIPANT_CAPTURE_TERMINAL")&&csv.Contains("NO_SYNCHRONIZATION_OR_LATENCY_ESTIMATE"));
                    }
                }
            });
        }
        internal static void AssertPoseUsedRows(byte[] bytes)
        {
            var presented=new Dictionary<string,string>();var used=new HashSet<string>();Exception callbackFailure=null;
            using(var input=new MemoryStream(bytes,false))
            {
                var read=ViveRawSessionJournalReader.ScanAnalysisRows(input,67108864,60000,CancellationToken.None,frameRows=>{
                    try{var rows=frameRows.Split(new[]{'\n'},StringSplitOptions.RemoveEmptyEntries).Select(line=>line.Split(',')).ToArray();
                    if(rows.Length==0||rows[0][1]!="task")return;
                    var fields=rows.ToDictionary(row=>row[2],row=>row[4]);
                    Func<string,string> text=key=>Encoding.UTF8.GetString(Convert.FromBase64String(fields[key]));
                    string kind=text("event_kind"),trial=text("trial_id");var values=new Dictionary<string,string>();
                    int count=Int32.Parse(fields["field_count"]);for(int i=0;i<count;i++)values.Add(text("field_"+i+"_key"),text("field_"+i+"_value"));
                    if(kind=="stimulus_presented"){string condition;values.TryGetValue("legacy_condition",out condition);presented.Add(trial,values["legacy_phase"]+":"+condition);}
                    string subtype;if(kind==ViveLegacyWozRecordingSchema.EventKind&&values.TryGetValue("woz_event",out subtype)&&subtype=="PoseUsed")
                    {Check.True(presented.ContainsKey(trial)&&presented[trial].StartsWith("MainTask:")&&!presented[trial].EndsWith(":D"));Check.True(used.Add(trial));}}
                    catch(Exception error){callbackFailure=error;throw;}
                },true);
                if(callbackFailure!=null)throw new Exception("Raw PoseUsed lineage assertion failed",callbackFailure);
                Check.Equal(ViveRawSessionReadStatus.VerifiedPrefix,read.Status);
            }
            Check.Equal(104,presented.Count);Check.Equal(72,used.Count);
            Check.Equal(72,presented.Count(pair=>pair.Value.StartsWith("MainTask:")&&!pair.Value.EndsWith(":D")));
        }
        public static int Main()
        {var suite=new TestSuite();ViveLegacyWozRawSchemaTests.Register(suite);Register(suite);Console.WriteLine("RESULT WoZ recording memory "+suite.Passed+" passed; "+suite.Failed+" failed.");return suite.Failed==0?0:1;}
    }
}
#endif
