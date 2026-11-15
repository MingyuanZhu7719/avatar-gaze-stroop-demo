using System;
using System.Threading;
using AvatarStroop.Core;

namespace AvatarStroop.Runtime
{
    // Connects the rig's *already polled* worker frame and authoritative task
    // events to the existing raw sink. No network transport is involved.
    // The installed factory is intentionally unreachable while the sink's
    // production gate is closed. This is not study or storage authorization.
    internal sealed partial class ViveNamedRawRecordingPump
    {
        // DEVELOPMENT ONLY: a close return is not proof of durability, worker
        // exit, or complete coverage. Withdrawal propagation and in-flight I/O
        // remain unverified. Production and completion gates must remain closed.
        internal enum Outcome { Running, Finishing, CloseCallsReturned, StopPending, StoppedWithoutClose, WithdrawnWithoutClose, Faulted, IoUncertain }
        // Busy includes a contended metadata lock or a bounded status backlog;
        // neither is a new task/head authority or proof that the FIFO is empty.
        internal enum Preparation { Ready, WaitingBaseline, Busy, Faulted }
        internal abstract class Writer
        {
            internal abstract bool Append(ViveNamedSessionFrameAdmission.ConsumerReceipt receipt);
            internal abstract bool Complete();
            internal abstract void Cleanup();
            internal abstract ViveRawSessionJournalSink.BlockPlanReceiptPoll PollBlockPlan(ViveBlockPlanRequest request,out ViveRawSessionJournalSink.BlockPlanWriteReceipt receipt);
            internal abstract void RevokeBlockPlanReceipt();
            // Diagnostics only; neither a resource-release nor durability proof.
            internal abstract int CompletionSnapshot {get;}
            internal abstract int CleanupSnapshot {get;}
            internal abstract bool DirectoryCleanupPending {get;}
            internal abstract bool SourceCloseReturned {get;}
            internal abstract int InstalledDestinationState {get;}
        }
        sealed class RawWriter : Writer
        {
            readonly ViveRawSessionJournalSink sink;
            internal RawWriter(ViveRawSessionJournalSink sink){this.sink=sink;}
            internal override bool Append(ViveNamedSessionFrameAdmission.ConsumerReceipt receipt){return sink.AppendOne(receipt);}
            internal override bool Complete(){return sink.CompleteSealed();}
            internal override void Cleanup(){sink.CleanupAfterTerminal();}
            internal override ViveRawSessionJournalSink.BlockPlanReceiptPoll PollBlockPlan(ViveBlockPlanRequest request,out ViveRawSessionJournalSink.BlockPlanWriteReceipt receipt)
            {
#if VIVE_RAW_SYNTHETIC_TEST && VIVE_SYNTHETIC_BATCH_FIXTURE && !UNITY_5_3_OR_NEWER
                if(sink.IsNativeFixture)return sink.TryGetSyntheticBlockPlanReceiptForTest(request,out receipt);
#endif
                return sink.TryGetBlockPlanReceipt(request,out receipt);
            }
            internal override void RevokeBlockPlanReceipt(){sink.RevokeBlockPlanWriteReceipt();}
            internal override int CompletionSnapshot {get{return sink.CompletionState;}}
            internal override int CleanupSnapshot {get{return sink.CleanupState;}}
            internal override bool DirectoryCleanupPending {get{return sink.DirectoryCleanupPending;}}
            internal override bool SourceCloseReturned {get{return sink.SourceCloseReturned;}}
            internal override int InstalledDestinationState {get{return sink.InstalledDestinationState;}}
            internal bool InstalledRelativeCompletionPending {get{return sink.InstalledRelativeCompletionPending;}}
#if VIVE_RAW_SYNTHETIC_TEST && VIVE_SYNTHETIC_BATCH_FIXTURE && !UNITY_5_3_OR_NEWER
            internal bool SyntheticRelativeCompletionPending {get{return sink.SyntheticRelativeCompletionPending;}}
#endif
        }
        // Internal fixture writer can only acknowledge synthetic admission;
        // it cannot open a file, accept a path, or wrap the installed worker.
        internal sealed class SyntheticWriter : Writer
        {
            readonly ViveNamedSessionFrameAdmission admission;
            internal readonly ManualResetEvent Entered=new ManualResetEvent(false), Release=new ManualResetEvent(false);
            internal readonly ManualResetEvent CompleteEntered=new ManualResetEvent(false),CompleteRelease=new ManualResetEvent(false);
            internal bool Block=false,Reject=false,BlockComplete=false,CompleteResult=false,ThrowComplete=false,ExitConsumerUnexpectedlyForTest=false;
            internal Action AfterRevokeForTest=null,AfterCompletionClaimForTest=null,AfterUnexpectedRevokeForTest=null;
#if STROOP_TEST_BUILD && !UNITY_5_3_OR_NEWER
            internal Action BeforeIdleWaitForTest;
#endif
            internal int Calls,CleanupCalls,CompleteCalls;
            internal SyntheticWriter(ViveNamedSessionFrameAdmission admission){this.admission=admission;}
            internal bool BelongsTo(ViveNamedSessionFrameAdmission value){return Object.ReferenceEquals(admission,value);}
            internal override bool Append(ViveNamedSessionFrameAdmission.ConsumerReceipt receipt)
            {
                Interlocked.Increment(ref Calls);Entered.Set();if(Block)Release.WaitOne();
                return !Reject&&admission.TryAcknowledgeSyntheticForTest(receipt);
            }
            internal override bool Complete()
            {Interlocked.Increment(ref CompleteCalls);CompleteEntered.Set();if(BlockComplete)CompleteRelease.WaitOne();if(ThrowComplete)throw new InvalidOperationException("Synthetic completion failure.");return CompleteResult;}
            internal override void Cleanup(){Interlocked.Increment(ref CleanupCalls);}
            internal override ViveRawSessionJournalSink.BlockPlanReceiptPoll PollBlockPlan(ViveBlockPlanRequest request,out ViveRawSessionJournalSink.BlockPlanWriteReceipt receipt)
            {receipt=null;return ViveRawSessionJournalSink.BlockPlanReceiptPoll.Faulted;}
            internal override void RevokeBlockPlanReceipt(){}
            internal override int CompletionSnapshot {get{return -1;}}
            internal override int CleanupSnapshot {get{return -1;}}
            internal override bool DirectoryCleanupPending {get{return false;}}
            internal override bool SourceCloseReturned {get{return false;}}
            internal override int InstalledDestinationState {get{return -1;}}
        }
        readonly ViveDevelopmentParticipant participant;readonly ViveProEyeWorker worker;
        readonly MonotonicClock clock;readonly ViveNamedSessionFrameAdmission admission;
        readonly Writer writer;readonly int owner;readonly object statusRun;
        int statusStopAttempted,statusStopConfirmed;
        ViveProEyeWorker.StatusTicket pendingStatusTicket;
        ViveNamedSessionFrameAdmission.StatusAdmissionProof pendingStatusProof;
        ViveProEyeWorker.StatusSeal pendingCompletionStatusSeal;
        string firstFault;
#if UNITY_EDITOR
        // The headless render fixture can substitute tracking only for an
        // in-memory synthetic writer, never an installed/native data source.
        internal bool SyntheticSceneFixtureOnly {get{return admission.SyntheticOnly&&writer is SyntheticWriter&&!worker.UsesInstalledSdkPath;}}
#endif
        internal object InitialWorkerGate {get{return worker.RecordingTicketGate;}}
        internal void DiscardPreparationPanelEpoch(){stagedEpoch=null;}
        internal string FirstFault {get{return Volatile.Read(ref firstFault)??admission.FirstFault??admission.IoFence.FirstFault;}}
        // Bounded software diagnostics only: no sample, path, native handle or secret.
        internal string FailureSnapshot {get{return "pump="+(Volatile.Read(ref firstFault)??"none")+
            "; admission="+(admission.FirstFault??"none")+"; io="+(admission.IoFence.FirstFault??"none")+
            "; terminal="+Volatile.Read(ref terminal)+"; admissionState="+admission.State+
            "; fence="+admission.IoFence.CompletionOutcome;}}
        internal void NoteFirstFault(string code){Interlocked.CompareExchange(ref firstFault,code,null);}
        internal ViveProEyeWorker.RecordingRunRead ReadExactWorkerRun(ViveProEyeWorker candidate)
        {
            if(!Object.ReferenceEquals(candidate,worker)||statusRun==null)return ViveProEyeWorker.RecordingRunRead.Unavailable;
            return worker.IsAttachedTaskRunCurrent(statusRun)?ViveProEyeWorker.RecordingRunRead.Ready:
                ViveProEyeWorker.RecordingRunRead.Unavailable;
        }
        // Render guard for an already-onset trial: the ordinary frame ticket
        // may already have been drained. This is run/writer authority only,
        // not head tracking, participant visibility, or a mutation receipt.
        internal bool VerifyTaskRunAuthorityForDisplay()
        {
            return Thread.CurrentThread.ManagedThreadId==owner&&statusRun!=null&&
                Volatile.Read(ref terminal)==0&&Volatile.Read(ref completionDrainRequested)==0&&
                !admission.Sealed&&!admission.IoFence.IsRevoked&&admission.State<=1&&
                worker.IsAttachedTaskRunCurrent(statusRun);
        }

        ViveProEyeWorker.RecordingFrameTicket stagedEpoch;
        double stagedMaximumAge,lastEpochVerification=-1;
        internal bool RawTaskEpochThisFrame {get{return stagedEpoch!=null&&stagedEpoch.TaskAuthority;}}
        const int MaxStatusPerPreparation=64;
        readonly Thread thread;int terminal,ioReserved,uncertain,appendAttempted,withdrawalRequested,finishRequested,completionDrainRequested;
#if VIVE_RAW_SYNTHETIC_TEST && VIVE_SYNTHETIC_BATCH_FIXTURE && !UNITY_5_3_OR_NEWER
        string fixtureFirstFailure;
        void MarkFixtureFailure(string stage)
        {
            Interlocked.CompareExchange(ref fixtureFirstFailure,stage,null);
            if(stage.StartsWith("writer_",StringComparison.Ordinal))return;
            var s=stage.StartsWith("status_",StringComparison.Ordinal)?ViveSyntheticNativeTrace.Stage.ProducerStatus:
                stage.StartsWith("frame_ticket",StringComparison.Ordinal)?ViveSyntheticNativeTrace.Stage.ProducerTicket:
                stage.StartsWith("frame_stamp",StringComparison.Ordinal)?ViveSyntheticNativeTrace.Stage.ProducerStamp:
                stage.StartsWith("frame_epoch",StringComparison.Ordinal)?ViveSyntheticNativeTrace.Stage.ProducerEpoch:
                ViveSyntheticNativeTrace.Stage.ProducerState;
            ViveSyntheticNativeTrace.Mark(s);
        }
        internal string FixtureDiagnosticsForTest()
        {
            return "first_observed="+(Volatile.Read(ref fixtureFirstFailure)??"none")+
                " terminal="+Volatile.Read(ref terminal)+" uncertain="+Volatile.Read(ref uncertain)+
                " io_reserved="+Volatile.Read(ref ioReserved)+" append_attempted="+Volatile.Read(ref appendAttempted)+
                " admission_state="+admission.State+" fence="+admission.IoFence.CompletionOutcome+
                " writer_exited="+HasExited+" completion="+writer.CompletionSnapshot+" cleanup="+writer.CleanupSnapshot;
        }
#endif
        // terminal: 0 running, 1 stop, 2 withdraw, 3 fault, 4 normal calls
        // returned. Uncertainty is
        // permanent once a stop races a reserved operation or cleanup.
        ViveNamedRawRecordingPump(ViveDevelopmentParticipant participant,ViveProEyeWorker worker,MonotonicClock clock,
            ViveNamedSessionFrameAdmission admission,Writer writer,object statusRun)
        {
            this.participant=participant;this.worker=worker;this.clock=clock;this.admission=admission;this.writer=writer;
            this.statusRun=statusRun;
            owner=Thread.CurrentThread.ManagedThreadId;
            thread=new Thread(Consume){IsBackground=true,Name="VIVE named raw journal writer"};
            if(!admission.IoFence.BindWriter(thread))throw new InvalidOperationException("Writer authority unavailable.");
            thread.Start();
        }
        internal static bool TryStartInstalled(ViveDevelopmentParticipant participant,ViveProEyeWorker worker,MonotonicClock clock,
            ViveNamedSessionFrameAdmission admission,ViveRawSessionJournalSink sink,out ViveNamedRawRecordingPump result)
        {result=null;return admission!=null&&!admission.IsLegacy&&TryStartRawInstalled(participant,worker,clock,admission,sink,out result);}
        internal static bool TryStartLegacyInstalled(ViveDevelopmentParticipant participant,ViveProEyeWorker worker,MonotonicClock clock,
            ViveNamedSessionFrameAdmission admission,ViveRawSessionJournalSink sink,out ViveNamedRawRecordingPump result)
        {result=null;return ViveLegacyRawRecordingBuildPolicy.Available&&admission!=null&&admission.IsLegacy&&TryStartRawInstalled(participant,worker,clock,admission,sink,out result);}
        static bool TryStartRawInstalled(ViveDevelopmentParticipant participant,ViveProEyeWorker worker,MonotonicClock clock,
            ViveNamedSessionFrameAdmission admission,ViveRawSessionJournalSink sink,out ViveNamedRawRecordingPump result)
        {
            result=null;
            if(participant==null||worker==null||clock==null||admission==null||sink==null||
                admission.SyntheticOnly||!admission.UsesExactSources(participant,worker,clock)||!worker.UsesInstalledSdkPath||!sink.ProductionGateOpen||
                !sink.BelongsTo(admission)||!participant.UsesEventClock(clock)||!worker.UsesClock(clock))return false;
            if(!admission.TryBindQueueBudget(sink.SelectedBudget))return false;
            object run;if(!admission.TryEnableWorkerStatus(128,out run))return false;
            try{result=new ViveNamedRawRecordingPump(participant,worker,clock,admission,new RawWriter(sink),run);return true;}
            catch(Exception error) when(!(error is OutOfMemoryException||error is StackOverflowException||error is AccessViolationException))
            {admission.AbortForTechnicalFailure();worker.TryStopStatusEvidence(run);return false;}
        }
        internal static ViveNamedRawRecordingPump StartSyntheticForTest(ViveDevelopmentParticipant participant,ViveProEyeWorker worker,
            MonotonicClock clock,ViveNamedSessionFrameAdmission admission,SyntheticWriter writer)
        {if(admission==null||admission.IsLegacy)throw new InvalidOperationException("Named synthetic context required.");return StartRawSyntheticForTest(participant,worker,clock,admission,writer);}
#if UNITY_EDITOR || (!UNITY_5_3_OR_NEWER && (STROOP_TEST_BUILD || VIVE_RAW_SYNTHETIC_TEST))
        internal static ViveNamedRawRecordingPump StartLegacySyntheticForTest(ViveDevelopmentParticipant participant,ViveProEyeWorker worker,
            MonotonicClock clock,ViveNamedSessionFrameAdmission admission,SyntheticWriter writer)
        {if(admission==null||!admission.IsLegacy)throw new InvalidOperationException("Legacy synthetic context required.");return StartRawSyntheticForTest(participant,worker,clock,admission,writer);}
#endif
        static ViveNamedRawRecordingPump StartRawSyntheticForTest(ViveDevelopmentParticipant participant,ViveProEyeWorker worker,
            MonotonicClock clock,ViveNamedSessionFrameAdmission admission,SyntheticWriter writer)
        {
            if(participant==null||worker==null||clock==null||admission==null||writer==null||!admission.SyntheticOnly||
                !admission.UsesExactSources(participant,worker,clock)||!writer.BelongsTo(admission)||worker.UsesInstalledSdkPath||
                !participant.UsesEventClock(clock)||!worker.UsesClock(clock))throw new InvalidOperationException("Synthetic source required.");
            return new ViveNamedRawRecordingPump(participant,worker,clock,admission,writer,null);
        }
        // Separate synthetic-only route tests the installed status ordering;
        // the existing synthetic safety fixture never opens a status source.
        internal static ViveNamedRawRecordingPump StartSyntheticWithStatusForTest(ViveDevelopmentParticipant participant,ViveProEyeWorker worker,
            MonotonicClock clock,ViveNamedSessionFrameAdmission admission,SyntheticWriter writer,bool captureAcquisitionForTest=false)
        {if(admission==null||admission.IsLegacy)throw new InvalidOperationException("Named synthetic context required.");return StartRawSyntheticWithStatusForTest(participant,worker,clock,admission,writer,captureAcquisitionForTest);}
#if UNITY_EDITOR || (!UNITY_5_3_OR_NEWER && (STROOP_TEST_BUILD || VIVE_RAW_SYNTHETIC_TEST))
        internal static ViveNamedRawRecordingPump StartLegacySyntheticWithStatusForTest(ViveDevelopmentParticipant participant,ViveProEyeWorker worker,
            MonotonicClock clock,ViveNamedSessionFrameAdmission admission,SyntheticWriter writer)
        {if(admission==null||!admission.IsLegacy)throw new InvalidOperationException("Legacy synthetic context required.");return StartRawSyntheticWithStatusForTest(participant,worker,clock,admission,writer);}
#endif
        static ViveNamedRawRecordingPump StartRawSyntheticWithStatusForTest(ViveDevelopmentParticipant participant,ViveProEyeWorker worker,
            MonotonicClock clock,ViveNamedSessionFrameAdmission admission,SyntheticWriter writer,bool captureAcquisitionForTest=false)
        {
            if(participant==null||worker==null||clock==null||admission==null||writer==null||!admission.SyntheticOnly||
                !admission.UsesExactSources(participant,worker,clock)||!writer.BelongsTo(admission)||worker.UsesInstalledSdkPath)
                throw new InvalidOperationException("Synthetic source required.");
            object run;if(!admission.TryEnableWorkerStatus(128,out run,captureAcquisitionForTest))throw new InvalidOperationException("Synthetic status unavailable.");
            try{return new ViveNamedRawRecordingPump(participant,worker,clock,admission,writer,run);}
            catch{admission.Withdraw();worker.TryStopStatusEvidence(run);throw;}
        }
#if VIVE_RAW_SYNTHETIC_TEST && VIVE_SYNTHETIC_BATCH_FIXTURE && !UNITY_5_3_OR_NEWER
        // Test assembly only: actual native sink, but strictly synthetic
        // participant/worker. This route cannot open the installed SDK path.
        internal static ViveNamedRawRecordingPump StartNativeFixtureForTest(ViveDevelopmentParticipant participant,ViveProEyeWorker worker,
            MonotonicClock clock,ViveNamedSessionFrameAdmission admission,ViveRawSessionJournalSink sink)
        {
            if(participant==null||worker==null||clock==null||admission==null||admission.IsLegacy||sink==null||!admission.SyntheticOnly||
                !admission.UsesExactSources(participant,worker,clock)||!sink.IsNativeFixture||!sink.BelongsTo(admission)||
                worker.UsesInstalledSdkPath||!participant.UsesEventClock(clock)||!worker.UsesClock(clock)||sink.ProductionGateOpen)
                throw new InvalidOperationException("Native fixture requires synthetic sources and a closed production gate.");
            if(!admission.TryBindQueueBudget(sink.SelectedBudget))throw new InvalidOperationException("Fixture budget unavailable.");
            object run;if(!admission.TryEnableWorkerStatus(128,out run))throw new InvalidOperationException("Synthetic status unavailable.");
            try{return new ViveNamedRawRecordingPump(participant,worker,clock,admission,new RawWriter(sink),run);}
            catch{admission.Withdraw();worker.TryStopStatusEvidence(run);throw;}
        }
        internal static ViveNamedRawRecordingPump StartLegacyNativeFixtureForTest(ViveDevelopmentParticipant participant,ViveProEyeWorker worker,
            MonotonicClock clock,ViveNamedSessionFrameAdmission admission,ViveRawSessionJournalSink sink)
        {
            if(participant==null||worker==null||clock==null||admission==null||!admission.SyntheticOnly||!admission.IsLegacy||
                admission.SessionContext==null||!admission.SessionContext.IsLegacy||sink==null||
                !admission.UsesExactSources(participant,worker,clock)||!sink.IsLegacyIntegrationFixture||!sink.BelongsTo(admission)||
                worker.UsesInstalledSdkPath||!participant.UsesEventClock(clock)||!worker.UsesClock(clock)||
                sink.ProductionGateOpen||sink.CompletionGateOpen||ViveLegacyRawRecordingBuildPolicy.Available)
                throw new InvalidOperationException("Legacy native fixture requires exact synthetic legacy sources and closed production gates.");
            if(!admission.TryBindQueueBudget(sink.SelectedBudget))throw new InvalidOperationException("Fixture budget unavailable.");
            object run;if(!admission.TryEnableWorkerStatus(128,out run))throw new InvalidOperationException("Synthetic status unavailable.");
            try{return new ViveNamedRawRecordingPump(participant,worker,clock,admission,new RawWriter(sink),run);}
            catch{admission.Withdraw();worker.TryStopStatusEvidence(run);throw;}
        }
        internal static ViveNamedRawRecordingPump StartWozNativeFixtureForTest(ViveDevelopmentParticipant participant,ViveProEyeWorker worker,
            MonotonicClock clock,ViveNamedSessionFrameAdmission admission,ViveRawSessionJournalSink sink)
        {
            if(participant==null||worker==null||clock==null||admission==null||!admission.SyntheticOnly||!admission.IsLegacy||
                admission.SessionContext==null||!admission.SessionContext.IsLegacyWoz||sink==null||
                !admission.UsesExactSources(participant,worker,clock)||!sink.IsWozIntegrationFixture||!sink.BelongsTo(admission)||
                worker.UsesInstalledSdkPath||!participant.UsesEventClock(clock)||!worker.UsesClock(clock)||
                sink.ProductionGateOpen||sink.CompletionGateOpen||ViveLegacyWozRecordingBuildPolicy.Available)
                throw new InvalidOperationException("WoZ native fixture requires exact synthetic sources and closed production gates.");
            if(!admission.TryBindQueueBudget(sink.SelectedBudget))throw new InvalidOperationException("Fixture budget unavailable.");
            object run;if(!admission.TryEnableWorkerStatus(128,out run))throw new InvalidOperationException("Synthetic status unavailable.");
            try{return new ViveNamedRawRecordingPump(participant,worker,clock,admission,new RawWriter(sink),run);}
            catch{admission.Withdraw();worker.TryStopStatusEvidence(run);throw;}
        }
#endif
        internal bool HasExited {get{return !thread.IsAlive;}}
        // A point-in-time check only. Withdrawal can occur immediately after
        // it returns; the scene must continue checking State on every frame.
        internal static bool IsAvailableForSceneInstall(ViveNamedRawRecordingPump pump)
        {return pump!=null&&!pump.WithdrawalRequested&&pump.State==Outcome.Running;}
        internal bool UsesRecordingBinding(ViveDevelopmentParticipant task,ViveProEyeWorker source,MonotonicClock shared,ViveNamedSessionFrameAdmission candidate)
        {return Object.ReferenceEquals(participant,task)&&Object.ReferenceEquals(worker,source)&&Object.ReferenceEquals(clock,shared)&&
            Object.ReferenceEquals(admission,candidate)&&IsAvailableForSceneInstall(this);}
        internal bool HasReservedIo {get{return Volatile.Read(ref ioReserved)!=0;}}
        internal bool CompletionDrainSealed {get{return Volatile.Read(ref finishRequested)!=0;}}
        // 4 = file close API returned; 6 = directory/ancestor cleanup
        // pending; 7 = reviewed close APIs returned. None proves durability.
        // -1 is synthetic fixture only.
        internal int NativeCompletionState {get{return writer.CompletionSnapshot;}}
        internal int NativeCleanupState {get{return writer.CleanupSnapshot;}}
        internal bool NativeDirectoryCleanupPending {get{return writer.DirectoryCleanupPending;}}
        internal bool NativeSourceCloseReturned {get{return writer.SourceCloseReturned;}}
        internal int NativeInstalledDestinationState {get{return writer.InstalledDestinationState;}}
        internal bool WithdrawalRequested {get{return Volatile.Read(ref withdrawalRequested)!=0;}}
        internal bool StatusCleanupPending {get{return statusRun!=null&&Volatile.Read(ref terminal)!=0&&Volatile.Read(ref statusStopConfirmed)==0;}}
        internal ViveRawSessionJournalSink.BlockPlanReceiptPoll TryGetBlockPlanReceipt(ViveBlockPlanRequest request,out ViveRawSessionJournalSink.BlockPlanWriteReceipt receipt)
        {
            receipt=null;
            if(Thread.CurrentThread.ManagedThreadId!=owner||Volatile.Read(ref terminal)!=0||Volatile.Read(ref finishRequested)!=0)
                return ViveRawSessionJournalSink.BlockPlanReceiptPoll.Faulted;
            var result=writer.PollBlockPlan(request,out receipt);
            if(result==ViveRawSessionJournalSink.BlockPlanReceiptPoll.Faulted)Fail();
            return result;
        }
        internal Outcome State
        {
            get
            {
                int kind=Volatile.Read(ref terminal);
                if(Volatile.Read(ref uncertain)!=0)return Outcome.IoUncertain;
                if(kind==0)
                {
                    if(admission.IoFence.CompletionOutcome==ViveRawRevocation.Outcome.Revoked)return Outcome.IoUncertain;
                    if(Volatile.Read(ref finishRequested)==0&&Volatile.Read(ref completionDrainRequested)==0)return Outcome.Running;
                    return Outcome.Finishing;
                }
                if(thread.IsAlive)return Outcome.StopPending;
                if(kind==4)return admission.IoFence.CompletionOutcome==ViveRawRevocation.Outcome.CallsReturned?Outcome.CloseCallsReturned:Outcome.IoUncertain;
                return kind==2?Outcome.WithdrawnWithoutClose:kind==3?Outcome.Faulted:Outcome.StoppedWithoutClose;
            }
        }
        // Called before obtaining the frame used for ordinary task input. An
        // Initial Empty is not a baseline. A precisely typed lock Busy can
        // defer on the owner frame; invalid authority remains terminal.
        internal Preparation PrepareStatus()
        {
            if(Volatile.Read(ref terminal)!=0||Volatile.Read(ref finishRequested)!=0){
                NoteFirstFault("status_already_terminal");
#if VIVE_RAW_SYNTHETIC_TEST && VIVE_SYNTHETIC_BATCH_FIXTURE && !UNITY_5_3_OR_NEWER
                MarkFixtureFailure("status_already_terminal");
#endif
                return Preparation.Faulted;}
            if(Thread.CurrentThread.ManagedThreadId!=owner||admission.State!=0){
                NoteFirstFault("eye_status_authority_invalid");
                NoteFirstFault("status_owner_or_admission");
#if VIVE_RAW_SYNTHETIC_TEST && VIVE_SYNTHETIC_BATCH_FIXTURE && !UNITY_5_3_OR_NEWER
                MarkFixtureFailure("status_owner_or_admission");
#endif
                Fail();return Preparation.Faulted;}
            if(statusRun==null)return admission.SyntheticOnly?Preparation.Ready:Preparation.Faulted;

            var acquisitionPreparation=DrainAcquisitions();
            if(acquisitionPreparation!=Preparation.Ready)return acquisitionPreparation;
            if(pendingStatusTicket==null&&!DrainTaskEvents()){
                NoteFirstFault("status_task_drain");
#if VIVE_RAW_SYNTHETIC_TEST && VIVE_SYNTHETIC_BATCH_FIXTURE && !UNITY_5_3_OR_NEWER
                MarkFixtureFailure("status_task_drain");
#endif
                return Preparation.Faulted;}
            try
            {
                for(int i=0;i<MaxStatusPerPreparation;i++)
                {
                    var ticket=pendingStatusTicket;
                    if(ticket==null)
                    {
                        var next=worker.TryReserveStatusEvidence(statusRun,out ticket);
                        if(next==ViveProEyeWorker.StatusDisposition.Busy)return Preparation.Busy;
                        // No admitted SDK status yet is a measurement absence,
                        // not a missing task authority after explicit start.
                        if(next==ViveProEyeWorker.StatusDisposition.Empty)return Preparation.Ready;
                        if(next!=ViveProEyeWorker.StatusDisposition.Event||ticket==null)
                        {NoteFirstFault("eye_status_ticket_invalid");Fail();return Preparation.Faulted;}
                        pendingStatusTicket=ticket;
                    }
                    if(pendingStatusProof==null)
                    {
                        // An unavailable lock is not an invalid ticket. Keep
                        // the one reserved FIFO ticket, without enqueueing it.
                        if(!Monitor.TryEnter(worker.RecordingTicketGate))return Preparation.Busy;
                        try
                        {
                            if(!admission.TryStageStatus(ticket,out pendingStatusProof)||pendingStatusProof==null)
                            {NoteFirstFault("eye_status_admission_invalid");Fail();return Preparation.Faulted;}
                        }
                        finally{Monitor.Exit(worker.RecordingTicketGate);}
                    }
                    var ack=worker.TryAcknowledgeStatusEvidenceResult(ticket,pendingStatusProof);
                    if(ack==ViveProEyeWorker.StatusAckResult.Busy)return Preparation.Busy;
                    if(ack!=ViveProEyeWorker.StatusAckResult.Acknowledged)
                    {NoteFirstFault("eye_status_ack_invalid");Fail();return Preparation.Faulted;}
                    pendingStatusTicket=null;pendingStatusProof=null;

                    // Device status describes gaze, not behavioral authority.
                }
                // The owner budget is not queue overflow. One nonblocking
                // probe distinguishes a truly drained batch from more work.
                // Do not reserve a 65th record: the next call must perform its
                // ordinary task-event drain before reserving more metadata.
                // Independent task authority continues while metadata waits.
                var afterBatch=worker.ReadStatusBacklog(statusRun);
                if(afterBatch==ViveProEyeWorker.StatusDisposition.Empty)return Preparation.Ready;
                if(afterBatch==ViveProEyeWorker.StatusDisposition.Busy)return Preparation.Busy;
                if(afterBatch==ViveProEyeWorker.StatusDisposition.Event)return Preparation.Busy;
                NoteFirstFault("eye_status_ticket_invalid");Fail();return Preparation.Faulted;
            }
            catch(Exception error) when(!(error is OutOfMemoryException||error is StackOverflowException||error is AccessViolationException))
            {
                NoteFirstFault("status_exception_");
#if VIVE_RAW_SYNTHETIC_TEST && VIVE_SYNTHETIC_BATCH_FIXTURE && !UNITY_5_3_OR_NEWER
                MarkFixtureFailure("status_exception_"+error.GetType().Name);
#endif
                Fail();return Preparation.Faulted;}
        }
        Preparation DrainAcquisitions()
        {
            // Bounded archival work; a backlog is not a fresh task authority.
            // On completion the worker is sealed first, then this same FIFO
            // must become empty before the terminal waterline is published.
            for(int i=0;i<32;i++)
            {
                var next=admission.TryStageNextAcquisition();
                if(next==ViveProEyeWorker.StatusDisposition.Empty)return Preparation.Ready;
                if(next==ViveProEyeWorker.StatusDisposition.Busy)return Preparation.Busy;
                if(next!=ViveProEyeWorker.StatusDisposition.Event)
                {NoteFirstFault("acquisition_admission_invalid");Fail();return Preparation.Faulted;}
            }
            var remaining=worker.ReadAcquisitionBacklog(statusRun);
            if(remaining==ViveProEyeWorker.StatusDisposition.Empty)return Preparation.Ready;
            if(remaining==ViveProEyeWorker.StatusDisposition.Event||remaining==ViveProEyeWorker.StatusDisposition.Busy)
                return Preparation.Busy;
            NoteFirstFault("acquisition_backlog_invalid");Fail();return Preparation.Faulted;
        }
        // Owner thread only; called after task processing, using the exact
        // cached Poll object from the rig's BeginFrame/EndFrame scope. Task
        // events are queued first, so frame context never precedes its cursor.
        // A false result requires the caller to block task presentation and
        // request participant safety handling; it is not a successful pause.
        internal bool StageRigFrame(ViveProEyePoll frame,double maximumAge)
        {return StageRigFrame(frame,maximumAge,false,out _);}
        internal bool StageRigFrame(ViveProEyePoll frame,double maximumAge,bool allowInitialWait,out bool waiting,ViveEyeAoiObservation aoi=null)
        {
            waiting=false;
            allowInitialWait=allowInitialWait&&participant.Phase==Phase.Preparation&&participant.CurrentTrial==null;
            // Admission detachment is also used by Stop and fault, so it is
            // never itself evidence of an explicit participant withdrawal.
            if(Volatile.Read(ref terminal)!=0||Volatile.Read(ref finishRequested)!=0){
                NoteFirstFault("frame_already_terminal");
#if VIVE_RAW_SYNTHETIC_TEST && VIVE_SYNTHETIC_BATCH_FIXTURE && !UNITY_5_3_OR_NEWER
                MarkFixtureFailure("frame_already_terminal");
#endif
                return false;}
            if(Thread.CurrentThread.ManagedThreadId!=owner||admission.State!=0)
            {
                NoteFirstFault("frame_owner_or_admission");
#if VIVE_RAW_SYNTHETIC_TEST && VIVE_SYNTHETIC_BATCH_FIXTURE && !UNITY_5_3_OR_NEWER
                MarkFixtureFailure("frame_owner_or_admission");
#endif
                Fail();return false;}
            try
            {
                bool independent=statusRun!=null&&worker.IsIndependentTaskPoll(frame);
                if(statusRun!=null&&worker.UsesInstalledSdkPath&&!independent)
                {NoteFirstFault("installed_task_snapshot_not_bound");Fail();return false;}
                bool statusDeferred=false;
                if(statusRun!=null)
                {
                    var preparation=PrepareStatus();
                    if(preparation==Preparation.Busy&&independent)
                    {
                        // Status remains in its exact reservable/ACK state;
                        // its lock may not pause behavioral input or render.
                        statusDeferred=true;
                        if(!DrainTaskEvents())return false;
                    }
                    else if(preparation!=Preparation.Ready)
                    {
                        if(allowInitialWait&&preparation==Preparation.Busy){waiting=true;return false;}
                        NoteFirstFault("frame_status_not_ready");
#if VIVE_RAW_SYNTHETIC_TEST && VIVE_SYNTHETIC_BATCH_FIXTURE && !UNITY_5_3_OR_NEWER
                        MarkFixtureFailure("frame_status_not_ready");
#endif
                        return false;
                    }
                }
                else if(!DrainTaskEvents())return false;
                ViveProEyeWorker.RecordingFrameTicket ticket;
                double now;
                if(!clock.TryReadRecordingAdmissionTime(out now)||now<lastEpochVerification)
                {NoteFirstFault("raw_frame_clock_invalid");Fail();return false;}
                var disposition=independent?worker.TryIssueIndependentTaskTicket(frame,now,maximumAge,statusDeferred,out ticket):
                    statusRun!=null?worker.TryIssueRecordingTaskTicket(frame,now,maximumAge,out ticket):
                    worker.TryIssueRecordingTicket(frame,out ticket);
                if(statusRun==null&&disposition==ViveProEyeWorker.RecordingTicketDisposition.Duplicate)
                    return Volatile.Read(ref terminal)==0&&admission.State==0;
                if(disposition!=ViveProEyeWorker.RecordingTicketDisposition.Issued){
                    NoteFirstFault("frame_ticket_"+disposition);
#if VIVE_RAW_SYNTHETIC_TEST && VIVE_SYNTHETIC_BATCH_FIXTURE && !UNITY_5_3_OR_NEWER
                    MarkFixtureFailure("frame_ticket_"+disposition);
#endif
                    Fail();return false;}
                ViveDevelopmentParticipant.NamedRecordingTaskStamp stamp;
                if(!participant.TryCaptureRawRecordingTaskStamp(clock,out stamp)||!admission.TryStage(stamp,ticket,maximumAge,aoi))
                {
                    NoteFirstFault("frame_stamp_or_admit");
#if VIVE_RAW_SYNTHETIC_TEST && VIVE_SYNTHETIC_BATCH_FIXTURE && !UNITY_5_3_OR_NEWER
                    MarkFixtureFailure("frame_stamp_or_admit");
#endif
                    if(ticket!=null){Interlocked.Exchange(ref ticket.Consumed,1);Interlocked.Exchange(ref ticket.Frame,null);}Fail();return false;}
                if(statusRun!=null){stagedEpoch=ticket;stagedMaximumAge=maximumAge;
                    if(!VerifyStagedEpoch()){
                    NoteFirstFault("frame_epoch_verify");
#if VIVE_RAW_SYNTHETIC_TEST && VIVE_SYNTHETIC_BATCH_FIXTURE && !UNITY_5_3_OR_NEWER
                    MarkFixtureFailure("frame_epoch_verify");
#endif
                    return false;}}
                return true;
            }
            catch(Exception error) when(!(error is OutOfMemoryException||error is StackOverflowException||error is AccessViolationException))
            {
                NoteFirstFault("frame_exception_");
#if VIVE_RAW_SYNTHETIC_TEST && VIVE_SYNTHETIC_BATCH_FIXTURE && !UNITY_5_3_OR_NEWER
                MarkFixtureFailure("frame_exception_"+error.GetType().Name);
#endif
                Fail();return false;}
        }
        // Called after an owner task mutation. It never polls the worker or
        // reuses the pre-mutation frame ticket and never waits for the writer.
        internal bool DrainTaskEvents()
        {return DrainTaskEvents(false);}
        // Keep this frame's already-consumed eye epoch only until a non-trial
        // participant-camera panel callback. No new SDK read or disk wait is
        // performed, and a missing callback must be cancelled by the owner.
        internal bool PrepareNonTrialPanelRender()
        {
            if(Thread.CurrentThread.ManagedThreadId!=owner||statusRun==null||stagedEpoch==null||
                Volatile.Read(ref terminal)!=0||Volatile.Read(ref finishRequested)!=0){Fail();return false;}
            return DrainTaskEvents(true)&&stagedEpoch!=null&&VerifyStagedEpoch();
        }
        internal bool FinishNonTrialPanelRender()
        {
            if(Thread.CurrentThread.ManagedThreadId!=owner||statusRun==null||stagedEpoch==null||!VerifyStagedEpoch())
            {Fail();return false;}
            return DrainTaskEvents(false);
        }
        // Only ordinary task actions belong here. Safety actions (including
        // loss, pause and withdrawal) must instead revoke/stop immediately and
        // never wait for capacity. The host must call this during the owner
        // frame that staged the current eye epoch; no cached readiness is a
        // substitute. Failure does not prove that a partially run action was
        // rolled back or that a queued event reached storage.
        internal bool TryRunReservedOrdinaryMutation(Action action)
        {
            if(Thread.CurrentThread.ManagedThreadId!=owner||action==null||statusRun==null||stagedEpoch==null||
                Volatile.Read(ref terminal)!=0||Volatile.Read(ref finishRequested)!=0)
            {Fail();return false;}
            try
            {
                if(!DrainTaskEvents(true)||!VerifyStagedEpoch()||!admission.TryRunReservedOrdinaryMutation(action)||
                    !VerifyStagedEpoch()||Volatile.Read(ref terminal)!=0||admission.State!=0)
                {Fail();return false;}
                return true;
            }
            catch(Exception error) when(!(error is OutOfMemoryException||error is StackOverflowException||error is AccessViolationException))
            {Fail();return false;}
        }
        bool DrainTaskEvents(bool retainEpoch)
        {
            if(Volatile.Read(ref terminal)!=0||Volatile.Read(ref finishRequested)!=0)return false;
            if(Thread.CurrentThread.ManagedThreadId!=owner||admission.State!=0){Fail();return false;}
            try
            {
                if(stagedEpoch!=null&&!VerifyStagedEpoch())return false;
                bool staged;int count=0;
                do
                {
                    if(!admission.TryStageNextTaskEvent(out staged)){
                        NoteFirstFault("drain_task_event");
#if VIVE_RAW_SYNTHETIC_TEST && VIVE_SYNTHETIC_BATCH_FIXTURE && !UNITY_5_3_OR_NEWER
                        MarkFixtureFailure("drain_task_event");
#endif
                        Fail();return false;}
                    if(staged&&++count>64){
                        NoteFirstFault("drain_task_batch_limit");
#if VIVE_RAW_SYNTHETIC_TEST && VIVE_SYNTHETIC_BATCH_FIXTURE && !UNITY_5_3_OR_NEWER
                        MarkFixtureFailure("drain_task_batch_limit");
#endif
                        Fail();return false;}
                }while(staged);
                if(stagedEpoch!=null&&!VerifyStagedEpoch())return false;
                if(!retainEpoch)stagedEpoch=null;
                return Volatile.Read(ref terminal)==0&&admission.State==0;
            }
            catch(Exception error) when(!(error is OutOfMemoryException||error is StackOverflowException||error is AccessViolationException))
            {Fail();return false;}
        }
        // Owner-thread bridge for a future raw scene presentation. This
        // reserves queue space and budget only, not a disk write or permission
        // to show a trial. The caller must commit from the same render frame
        // or cancel before processing the next frame. Raw display remains gated.
        internal bool TryPreparePresentation(int unityFrame,long revision,out ViveNamedSessionFrameAdmission.PresentationReservation token)
        {
            token=null;
            if(Thread.CurrentThread.ManagedThreadId!=owner||statusRun==null||stagedEpoch==null||
                Volatile.Read(ref terminal)!=0||Volatile.Read(ref finishRequested)!=0){Fail();return false;}
            if(!DrainTaskEvents(true)||stagedEpoch==null||!VerifyStagedEpoch()||
                !admission.TryReservePresentation(stagedEpoch,1.0,unityFrame,revision,out token))
            {Fail();return false;}
            return true;
        }
        internal bool CommitPresentation(ViveNamedSessionFrameAdmission.PresentationReservation token,
            int unityFrame,long revision,string trialId,Colour word,Colour ink,double unityRenderSeconds)
        {
            if(Thread.CurrentThread.ManagedThreadId!=owner||Volatile.Read(ref terminal)!=0||
                !admission.TryCommitPresentation(token,unityFrame,revision,trialId,word,ink,unityRenderSeconds))
            {stagedEpoch=null;Fail();return false;}
            stagedEpoch=null;return true;
        }
        internal void CancelPresentation(ViveNamedSessionFrameAdmission.PresentationReservation token)
        {stagedEpoch=null;admission.CancelPresentation(token);Fail();}
        // A software revision check only. It cannot prove physical eye/head
        // continuity or the exact interval in which the vendor sampled gaze.
        internal bool VerifyStagedEpoch()
        {
            if(statusRun==null)return admission.SyntheticOnly&&Volatile.Read(ref terminal)==0;
            double now;string reason;
            if(ReadEpoch(false,out now,out reason)!=PreparationPanelEpochResult.Current)
            {NoteFirstFault("raw_VerifyStagedEpoch:"+reason);Fail();return false;}
            lastEpochVerification=now;
            return true;
        }
        internal enum PreparationPanelEpochResult { Current, Retry, Invalid }
        PreparationPanelEpochResult ReadEpoch(bool preparation,out double now,out string reason)
        {
            now=0;
            if(Thread.CurrentThread.ManagedThreadId!=owner){reason="wrong_owner";return PreparationPanelEpochResult.Invalid;}
            if(Volatile.Read(ref terminal)!=0){reason="terminal";return PreparationPanelEpochResult.Invalid;}
            var ticket=stagedEpoch;
            if(ticket==null){reason="missing_ticket";return PreparationPanelEpochResult.Invalid;}
            if(!clock.TryReadRecordingAdmissionTime(out now)||!WireCodec.Finite(now)||!WireCodec.Finite(lastEpochVerification)||
                !WireCodec.Finite(ticket.HeadObserved)||!WireCodec.Finite(stagedMaximumAge)||stagedMaximumAge<=0||
                stagedMaximumAge>1||now<lastEpochVerification||now<ticket.HeadObserved||
                !ticket.TaskAuthority&&(!WireCodec.Finite(ticket.EyeObserved)||now<ticket.EyeObserved))
            {reason="clock_invalid";return PreparationPanelEpochResult.Invalid;}
            double age=ticket.TaskAuthority?now-ticket.HeadObserved:Math.Max(now-ticket.EyeObserved,now-ticket.HeadObserved);
            // Keep the strict caller's prior short circuit on a stale sample.
            if(!preparation&&age>stagedMaximumAge){reason="age_expired";return PreparationPanelEpochResult.Invalid;}
            var workerEpoch=worker.ClassifyRecordingEpoch(ticket,true);
            if(workerEpoch!=ViveProEyeWorker.RecordingEpochDetail.Current&&workerEpoch!=ViveProEyeWorker.RecordingEpochDetail.HealthyStatusPending&&
                workerEpoch!=ViveProEyeWorker.RecordingEpochDetail.RecoverableStatusPending)
            {reason="worker_"+workerEpoch;return PreparationPanelEpochResult.Invalid;}
            if(age>stagedMaximumAge)
            {
                reason="age_expired";
                return age<=stagedMaximumAge+1?PreparationPanelEpochResult.Retry:PreparationPanelEpochResult.Invalid;
            }
            if(preparation&&!ticket.TaskAuthority&&workerEpoch==ViveProEyeWorker.RecordingEpochDetail.HealthyStatusPending)
            {reason="healthy_status_pending";return PreparationPanelEpochResult.Retry;}
            reason="current";return PreparationPanelEpochResult.Current;
        }
        // Initial, unaccepted non-trial panel only. The owner holds both
        // gates across the render decision. No producer, trial witness,
        // countdown reservation, seal or revocation can be present.
        internal PreparationPanelEpochResult CheckPreparationPanelEpoch(out string reason)
        {
            if(Thread.CurrentThread.ManagedThreadId!=owner||!Monitor.IsEntered(participant.NamedRecordingGate)||
                (stagedEpoch!=null&&!stagedEpoch.IndependentTaskAuthority&&!Monitor.IsEntered(worker.RecordingTicketGate))||
                participant.Phase!=Phase.Preparation||participant.CurrentTrial!=null||
                statusRun==null||Volatile.Read(ref finishRequested)!=0||!admission.AllowsUnacceptedPreparationPanelDiscard)
            {reason="preparation_scope_invalid";return PreparationPanelEpochResult.Invalid;}
            double now;return ReadEpoch(true,out now,out reason);
        }
        internal bool TryDiscardPreparationPanelEpoch()
        {
            string reason;
            if(CheckPreparationPanelEpoch(out reason)!=PreparationPanelEpochResult.Retry)return false;
            stagedEpoch=null;return true;
        }
        internal static bool EpochTimeFresh(double now,double previous,double eye,double head,double maximumAge)
        {
            return WireCodec.Finite(now)&&WireCodec.Finite(previous)&&WireCodec.Finite(eye)&&WireCodec.Finite(head)&&
                WireCodec.Finite(maximumAge)&&maximumAge>0&&maximumAge<=1&&now>=previous&&now>=eye&&now>=head&&
                now-eye<=maximumAge&&now-head<=maximumAge;
        }
        void AfterSyntheticRevokeForTest()
        {var synthetic=writer as SyntheticWriter;if(admission.SyntheticOnly&&synthetic!=null){var hook=synthetic.AfterRevokeForTest;if(hook!=null)hook();}}
        internal void Stop(){admission.IoFence.Revoke();AfterSyntheticRevokeForTest();Terminate(1);}
        internal void Withdraw(){admission.IoFence.Revoke();AfterSyntheticRevokeForTest();Interlocked.Exchange(ref withdrawalRequested,1);Terminate(2);}
        void Fail([System.Runtime.CompilerServices.CallerMemberName]string member=null,
            [System.Runtime.CompilerServices.CallerLineNumber]int line=0)
        {NoteFirstFault("raw_"+member+":"+line);admission.IoFence.Abort();Terminate(3);}
        // Normal end is distinct from stop/withdraw/fault. Only the owner of
        // an already Completed task can request native callback quiescence,
        // drain its provider FIFO and then freeze the same worker's status FIFO,
        // drain its previously registered records and seal the task cursor.
        // Nothing here waits for disk or certifies physical sample coverage.
        internal bool RequestCompletedDrain()
        {
            if(Thread.CurrentThread.ManagedThreadId!=owner||statusRun==null||Volatile.Read(ref terminal)!=0||
                Volatile.Read(ref finishRequested)!=0||!participant.Completed||
                Interlocked.CompareExchange(ref completionDrainRequested,1,0)!=0){Fail();return false;}
            return PollRequestedCompletionDrain();
        }

        // Recording stays Finishing if the optional eye gate is busy or its
        // bounded FIFO needs another owner poll. Freeze exactly once; no
        // success/terminal frame is asserted before that same seal is drained.
        internal bool PollRequestedCompletionDrain()
        {
            if(Thread.CurrentThread.ManagedThreadId!=owner||Volatile.Read(ref completionDrainRequested)==0||
                !participant.Completed)return false;
            int outcome=Volatile.Read(ref terminal);
            if(Volatile.Read(ref finishRequested)!=0)return outcome==0||outcome==4;
            if(outcome!=0)return false;
            var gate=worker.RecordingTicketGate;
            if(!Monitor.TryEnter(gate))return true;
            try
            {
                if(!DrainTaskEvents()){Fail();return false;}
                var seal=pendingCompletionStatusSeal;
                if(seal==null)
                {
                    // The owner never waits for a native unregister call. The
                    // worker stops callbacks and publishes all already received
                    // packets before acknowledging this asynchronous boundary.
                    var acquisitionSeal=worker.PrepareAcquisitionSeal(statusRun);
                    if(acquisitionSeal==ViveProEyeWorker.AcquisitionSealDisposition.Faulted)
                    {Fail();return false;}
                    if(acquisitionSeal==ViveProEyeWorker.AcquisitionSealDisposition.Busy)
                    {
                        // Continue draining bounded batches while the provider
                        // finishes, so its tail cannot fill the worker queue.
                        return PrepareStatus()!=Preparation.Faulted;
                    }
                    if(!worker.TrySealStatusEvidence(statusRun,out seal,true)||seal==null)
                    {Fail();return false;}
                    pendingCompletionStatusSeal=seal;
                }
                var preparation=PrepareStatus();
                if(preparation==Preparation.Busy)return true;
                if(preparation!=Preparation.Ready||!admission.TryBindDrainedStatusSeal(seal)||
                    admission.TrySealCompleted()!=ViveNamedSessionFrameAdmission.SealResult.Sealed)
                {Fail();return false;}
                pendingCompletionStatusSeal=null;
                if(Volatile.Read(ref terminal)!=0||Interlocked.CompareExchange(ref finishRequested,1,0)!=0)
                {Fail();return false;}
                return true;
            }
            catch(Exception error) when(!(error is OutOfMemoryException||error is StackOverflowException||error is AccessViolationException))
            {Fail();return false;}
            finally{Monitor.Exit(gate);}
        }
        void Terminate(int reason)
        {
            // Withdrawal always detaches queued payloads immediately. A prior
            // reserved OS operation can still begin or finish; no wait occurs.
            // Ordinary non-fallback writes move terminal upwards (0 ->
            // 1/2/3 or 0 -> 4). For reason 1..3 at most three such competing
            // transitions can defeat a CAS. If that invariant is violated,
            // the bounded fallback conservatively replaces even a 4 with a
            // fault: the fence was already revoked and its outcome is unknown.
            bool published=false;
            for(int attempt=0;attempt<3;attempt++)
            {int old=Volatile.Read(ref terminal);if(old>=reason){published=true;break;}
                if(Interlocked.CompareExchange(ref terminal,reason,old)==old){published=true;break;}}
            if(!published&&Volatile.Read(ref terminal)<reason)
            {Interlocked.Exchange(ref uncertain,1);Interlocked.Exchange(ref terminal,3);}
            admission.Withdraw();
            writer.RevokeBlockPlanReceipt();
            if(statusRun!=null)worker.RevokeStatusEvidence();
            // Revocation above makes even a raced/retained seal unusable.
            Interlocked.Exchange(ref pendingCompletionStatusSeal,null);
            if(Volatile.Read(ref ioReserved)!=0)Interlocked.Exchange(ref uncertain,1);
        }
        // The atomic revoke is immediate. This optional owner-thread attempt
        // only clears the worker's queued metadata; Busy remains pending and
        // is not retried blindly or reported as a completed cleanup.
        internal bool TryStopStatusOnOwner()
        {
            if(statusRun==null)return true;
            if(Thread.CurrentThread.ManagedThreadId!=owner||Volatile.Read(ref terminal)==0)return false;
            if(Interlocked.CompareExchange(ref statusStopAttempted,1,0)!=0)return Volatile.Read(ref statusStopConfirmed)!=0;
            bool stopped=worker.TryStopStatusEvidence(statusRun);
            if(stopped)Volatile.Write(ref statusStopConfirmed,1);
            return stopped;
        }
        void Consume()
        {
            try
            {
                while(Volatile.Read(ref terminal)==0)
                {
                    var synthetic=writer as SyntheticWriter;
                    if(admission.SyntheticOnly&&synthetic!=null&&synthetic.ExitConsumerUnexpectedlyForTest)break;
                    ViveNamedSessionFrameAdmission.ConsumerReceipt receipt;
                    if(!admission.TryReserveConsumer(out receipt)){if(Volatile.Read(ref terminal)==0)Fail();break;}
                    if(receipt==null)
                    {
                        if(Volatile.Read(ref finishRequested)==0)
                        {
#if STROOP_TEST_BUILD && !UNITY_5_3_OR_NEWER
                            if(admission.SyntheticOnly&&synthetic!=null)
                            {var idle=synthetic.BeforeIdleWaitForTest;if(idle!=null)idle();}
#endif
                            Thread.Sleep(20);continue;
                        }
                        long water,cursor;double at;
                        if(!admission.TryGetDrainedSealBoundary(out water,out cursor,out at)){Fail();break;}
                        Interlocked.Exchange(ref ioReserved,1);
                        if(Volatile.Read(ref terminal)!=0){Interlocked.Exchange(ref uncertain,1);break;}
                        bool closed=writer.Complete();
                        Interlocked.Exchange(ref ioReserved,0);
                        if(Volatile.Read(ref terminal)!=0){Interlocked.Exchange(ref uncertain,1);break;}
                        var installedRawWriter=writer as RawWriter;
                        if(!closed&&installedRawWriter!=null&&installedRawWriter.InstalledRelativeCompletionPending)
                        {Thread.Sleep(20);continue;}
#if VIVE_RAW_SYNTHETIC_TEST && VIVE_SYNTHETIC_BATCH_FIXTURE && !UNITY_5_3_OR_NEWER
                        var rawWriter=writer as RawWriter;
                        if(!closed&&rawWriter!=null&&rawWriter.SyntheticRelativeCompletionPending)
                        {Thread.Sleep(20);continue;}
#endif
                        if(!closed){Fail();break;}
                        // Completion and participant revocation compete in the
                        // shared fence, not in a separate terminal snapshot.
                        // It states only that the writer's callbacks returned.
                        if(!admission.IoFence.TryPublishCallsReturned())
                        {Interlocked.Exchange(ref uncertain,1);Fail();break;}
                        if(admission.SyntheticOnly&&synthetic!=null){var hook=synthetic.AfterCompletionClaimForTest;if(hook!=null)hook();}
                        if(Interlocked.CompareExchange(ref terminal,4,0)!=0)
                        {Interlocked.Exchange(ref uncertain,1);admission.IoFence.Abort();}
                        break;
                    }
                    Interlocked.Exchange(ref ioReserved,1);
                    if(Volatile.Read(ref terminal)!=0){Interlocked.Exchange(ref uncertain,1);break;}
                    Volatile.Write(ref appendAttempted,1);bool written=writer.Append(receipt);
                    Interlocked.Exchange(ref ioReserved,0);
                    if(Volatile.Read(ref terminal)!=0){Interlocked.Exchange(ref uncertain,1);break;}
                    if(!written){
                        NoteFirstFault("writer_append_false");
#if VIVE_RAW_SYNTHETIC_TEST && VIVE_SYNTHETIC_BATCH_FIXTURE && !UNITY_5_3_OR_NEWER
                        MarkFixtureFailure("writer_append_false");
#endif
                        Fail();break;}
                }
            }
            catch(Exception error) when(!(error is OutOfMemoryException||error is StackOverflowException||error is AccessViolationException))
            {
                NoteFirstFault("writer_exception_");
#if VIVE_RAW_SYNTHETIC_TEST && VIVE_SYNTHETIC_BATCH_FIXTURE && !UNITY_5_3_OR_NEWER
                MarkFixtureFailure("writer_exception_"+error.GetType().Name);
#endif
                Interlocked.Exchange(ref uncertain,1);Fail();}
            finally
            {
                // Fence an unexpected exit before publishing terminal state.
                // A concurrent explicit stop/withdraw may win the terminal CAS;
                // its cause is preserved and the outcome remains conservative.
                if(Volatile.Read(ref terminal)==0)
                {
                    admission.IoFence.Revoke();Interlocked.Exchange(ref uncertain,1);
                    var synthetic=writer as SyntheticWriter;
                    if(admission.SyntheticOnly&&synthetic!=null)
                    {try{var hook=synthetic.AfterUnexpectedRevokeForTest;if(hook!=null)hook();}catch(Exception){}}
                    if(Interlocked.CompareExchange(ref terminal,3,0)==0)admission.ConsumerFailed(null);
                }
                if(Volatile.Read(ref terminal)!=4)
                {
                    // A caller never waits for cleanup or mistakes it for a
                    // closed handle. After any attempted write the tail is unknown.
                    if(Volatile.Read(ref appendAttempted)!=0)Interlocked.Exchange(ref uncertain,1);
                    Interlocked.Exchange(ref ioReserved,1);
                    try{writer.Cleanup();}catch(Exception){Interlocked.Exchange(ref uncertain,1);}
                    Interlocked.Exchange(ref ioReserved,0);
                }
            }
        }
    }
}
