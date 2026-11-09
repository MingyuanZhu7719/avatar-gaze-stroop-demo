using System;
using System.Threading;
using AvatarStroop.Core;

namespace AvatarStroop.Runtime
{
    // Connects the rig's *already polled* worker frame and authoritative task
    // events to the existing raw sink. No network transport is involved.
    // The installed factory is intentionally unreachable while the sink's
    // production gate is closed. This is not study or storage authorization.
    internal sealed class ViveNamedRawRecordingPump
    {
        // DEVELOPMENT ONLY: a close return is not proof of durability, worker
        // exit, or complete coverage. Withdrawal propagation and in-flight I/O
        // remain unverified. Production and completion gates must remain closed.
        internal enum Outcome { Running, Finishing, CloseCallsReturned, StopPending, StoppedWithoutClose, WithdrawnWithoutClose, Faulted, IoUncertain }
        internal enum Preparation { Ready, WaitingBaseline, Faulted }
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
        bool statusBaseline;int waitingBaselinePolls,statusStopAttempted,statusStopConfirmed;
        ViveProEyeWorker.RecordingFrameTicket stagedEpoch;
        double stagedMaximumAge,lastEpochVerification=-1;
        const int MaxBaselinePolls=120,MaxStatusPerPreparation=64;
        readonly Thread thread;int terminal,ioReserved,uncertain,appendAttempted,withdrawalRequested,finishRequested;
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
            MonotonicClock clock,ViveNamedSessionFrameAdmission admission,SyntheticWriter writer)
        {if(admission==null||admission.IsLegacy)throw new InvalidOperationException("Named synthetic context required.");return StartRawSyntheticWithStatusForTest(participant,worker,clock,admission,writer);}
#if UNITY_EDITOR || (!UNITY_5_3_OR_NEWER && (STROOP_TEST_BUILD || VIVE_RAW_SYNTHETIC_TEST))
        internal static ViveNamedRawRecordingPump StartLegacySyntheticWithStatusForTest(ViveDevelopmentParticipant participant,ViveProEyeWorker worker,
            MonotonicClock clock,ViveNamedSessionFrameAdmission admission,SyntheticWriter writer)
        {if(admission==null||!admission.IsLegacy)throw new InvalidOperationException("Legacy synthetic context required.");return StartRawSyntheticWithStatusForTest(participant,worker,clock,admission,writer);}
#endif
        static ViveNamedRawRecordingPump StartRawSyntheticWithStatusForTest(ViveDevelopmentParticipant participant,ViveProEyeWorker worker,
            MonotonicClock clock,ViveNamedSessionFrameAdmission admission,SyntheticWriter writer)
        {
            if(participant==null||worker==null||clock==null||admission==null||writer==null||!admission.SyntheticOnly||
                !admission.UsesExactSources(participant,worker,clock)||!writer.BelongsTo(admission)||worker.UsesInstalledSdkPath)
                throw new InvalidOperationException("Synthetic source required.");
            object run;if(!admission.TryEnableWorkerStatus(128,out run))throw new InvalidOperationException("Synthetic status unavailable.");
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
#endif
        internal bool HasExited {get{return !thread.IsAlive;}}
        // A point-in-time check only. Withdrawal can occur immediately after
        // it returns; the scene must continue checking State on every frame.
        internal static bool IsAvailableForSceneInstall(ViveNamedRawRecordingPump pump)
        {return pump!=null&&!pump.WithdrawalRequested&&pump.State==Outcome.Running;}
        internal bool HasReservedIo {get{return Volatile.Read(ref ioReserved)!=0;}}
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
                    if(Volatile.Read(ref finishRequested)==0)return Outcome.Running;
                    return Outcome.Finishing;
                }
                if(thread.IsAlive)return Outcome.StopPending;
                if(kind==4)return admission.IoFence.CompletionOutcome==ViveRawRevocation.Outcome.CallsReturned?Outcome.CloseCallsReturned:Outcome.IoUncertain;
                return kind==2?Outcome.WithdrawnWithoutClose:kind==3?Outcome.Faulted:Outcome.StoppedWithoutClose;
            }
        }
        // Called before obtaining the frame used for ordinary task input. An
        // initial Empty is not a baseline. Busy/ACK failure is not safely
        // distinguishable from invalidity with the current worker API and is
        // conservatively terminal; no owner-thread wait or unbounded loop.
        internal Preparation PrepareStatus()
        {
            if(Volatile.Read(ref terminal)!=0||Volatile.Read(ref finishRequested)!=0){
#if VIVE_RAW_SYNTHETIC_TEST && VIVE_SYNTHETIC_BATCH_FIXTURE && !UNITY_5_3_OR_NEWER
                MarkFixtureFailure("status_already_terminal");
#endif
                return Preparation.Faulted;}
            if(Thread.CurrentThread.ManagedThreadId!=owner||admission.State!=0){
#if VIVE_RAW_SYNTHETIC_TEST && VIVE_SYNTHETIC_BATCH_FIXTURE && !UNITY_5_3_OR_NEWER
                MarkFixtureFailure("status_owner_or_admission");
#endif
                Fail();return Preparation.Faulted;}
            if(statusRun==null)return admission.SyntheticOnly?Preparation.Ready:Preparation.Faulted;
            if(!DrainTaskEvents()){
#if VIVE_RAW_SYNTHETIC_TEST && VIVE_SYNTHETIC_BATCH_FIXTURE && !UNITY_5_3_OR_NEWER
                MarkFixtureFailure("status_task_drain");
#endif
                return Preparation.Faulted;}
            try
            {
                for(int i=0;i<MaxStatusPerPreparation;i++)
                {
                    ViveProEyeWorker.StatusTicket ticket;
                    var next=worker.TryReserveStatusEvidence(statusRun,out ticket);
                    if(next==ViveProEyeWorker.StatusDisposition.Empty)
                    {
                        if(statusBaseline){waitingBaselinePolls=0;return Preparation.Ready;}
                        if(++waitingBaselinePolls<=MaxBaselinePolls)return Preparation.WaitingBaseline;
                        
#if VIVE_RAW_SYNTHETIC_TEST && VIVE_SYNTHETIC_BATCH_FIXTURE && !UNITY_5_3_OR_NEWER
                        MarkFixtureFailure("status_baseline_timeout");
#endif
                        Fail();return Preparation.Faulted;
                    }
                    if(next!=ViveProEyeWorker.StatusDisposition.Event||ticket==null){
#if VIVE_RAW_SYNTHETIC_TEST && VIVE_SYNTHETIC_BATCH_FIXTURE && !UNITY_5_3_OR_NEWER
                        MarkFixtureFailure("status_ticket_"+next);
#endif
                        Fail();return Preparation.Faulted;}
                    ViveNamedSessionFrameAdmission.StatusAdmissionProof proof;
                    if(!admission.TryStageStatus(ticket,out proof)||proof==null||!worker.TryAcknowledgeStatusEvidence(ticket,proof))
                    {
#if VIVE_RAW_SYNTHETIC_TEST && VIVE_SYNTHETIC_BATCH_FIXTURE && !UNITY_5_3_OR_NEWER
                        MarkFixtureFailure("status_admit_or_ack");
#endif
                        Fail();return Preparation.Faulted;}
                    statusBaseline=true;
                    // ACK means admitted to memory, not persisted. Invalid
                    // state is fail-stop even if a later recovery is queued.
                    if(ticket.Event.CauseFlags!=ViveProEyeWorker.EyeStatusCause.None){
#if VIVE_RAW_SYNTHETIC_TEST && VIVE_SYNTHETIC_BATCH_FIXTURE && !UNITY_5_3_OR_NEWER
                        MarkFixtureFailure("status_invalid_"+ticket.Event.CauseFlags);
#endif
                        Fail();return Preparation.Faulted;}
                }
                // Hitting the bound cannot be treated as an empty queue.
                
#if VIVE_RAW_SYNTHETIC_TEST && VIVE_SYNTHETIC_BATCH_FIXTURE && !UNITY_5_3_OR_NEWER
                MarkFixtureFailure("status_batch_limit");
#endif
                Fail();return Preparation.Faulted;
            }
            catch(Exception error) when(!(error is OutOfMemoryException||error is StackOverflowException||error is AccessViolationException))
            {
#if VIVE_RAW_SYNTHETIC_TEST && VIVE_SYNTHETIC_BATCH_FIXTURE && !UNITY_5_3_OR_NEWER
                MarkFixtureFailure("status_exception_"+error.GetType().Name);
#endif
                Fail();return Preparation.Faulted;}
        }
        // Owner thread only; called after task processing, using the exact
        // cached Poll object from the rig's BeginFrame/EndFrame scope. Task
        // events are queued first, so frame context never precedes its cursor.
        // A false result requires the caller to block task presentation and
        // request participant safety handling; it is not a successful pause.
        internal bool StageRigFrame(ViveProEyePoll frame,double maximumAge)
        {
            // Admission detachment is also used by Stop and fault, so it is
            // never itself evidence of an explicit participant withdrawal.
            if(Volatile.Read(ref terminal)!=0||Volatile.Read(ref finishRequested)!=0){
#if VIVE_RAW_SYNTHETIC_TEST && VIVE_SYNTHETIC_BATCH_FIXTURE && !UNITY_5_3_OR_NEWER
                MarkFixtureFailure("frame_already_terminal");
#endif
                return false;}
            if(Thread.CurrentThread.ManagedThreadId!=owner||admission.State!=0)
            {
#if VIVE_RAW_SYNTHETIC_TEST && VIVE_SYNTHETIC_BATCH_FIXTURE && !UNITY_5_3_OR_NEWER
                MarkFixtureFailure("frame_owner_or_admission");
#endif
                Fail();return false;}
            try
            {
                if(statusRun!=null)
                {if(PrepareStatus()!=Preparation.Ready){
#if VIVE_RAW_SYNTHETIC_TEST && VIVE_SYNTHETIC_BATCH_FIXTURE && !UNITY_5_3_OR_NEWER
                    MarkFixtureFailure("frame_status_not_ready");
#endif
                    return false;}}
                else if(!DrainTaskEvents())return false;
                ViveProEyeWorker.RecordingFrameTicket ticket;
                var disposition=worker.TryIssueRecordingTicket(frame,out ticket);
                if(disposition==ViveProEyeWorker.RecordingTicketDisposition.Duplicate)
                {
                    // A duplicate has no new consumed ticket with which to
                    // validate the action interval in status mode.
                    if(statusRun!=null){
#if VIVE_RAW_SYNTHETIC_TEST && VIVE_SYNTHETIC_BATCH_FIXTURE && !UNITY_5_3_OR_NEWER
                        MarkFixtureFailure("frame_ticket_duplicate");
#endif
                        Fail();return false;}
                    if(Volatile.Read(ref terminal)!=0||admission.State!=0){Fail();return false;}
                    return true; // Caller must recheck safety before presentation.
                }
                if(disposition!=ViveProEyeWorker.RecordingTicketDisposition.Issued){
#if VIVE_RAW_SYNTHETIC_TEST && VIVE_SYNTHETIC_BATCH_FIXTURE && !UNITY_5_3_OR_NEWER
                    MarkFixtureFailure("frame_ticket_"+disposition);
#endif
                    Fail();return false;}
                ViveDevelopmentParticipant.NamedRecordingTaskStamp stamp;
                if(!participant.TryCaptureRawRecordingTaskStamp(clock,out stamp)||!admission.TryStage(stamp,ticket,maximumAge))
                {
#if VIVE_RAW_SYNTHETIC_TEST && VIVE_SYNTHETIC_BATCH_FIXTURE && !UNITY_5_3_OR_NEWER
                    MarkFixtureFailure("frame_stamp_or_admit");
#endif
                    if(ticket!=null){Interlocked.Exchange(ref ticket.Consumed,1);Interlocked.Exchange(ref ticket.Frame,null);}Fail();return false;}
                if(statusRun!=null){stagedEpoch=ticket;stagedMaximumAge=maximumAge;if(!VerifyStagedEpoch()){
#if VIVE_RAW_SYNTHETIC_TEST && VIVE_SYNTHETIC_BATCH_FIXTURE && !UNITY_5_3_OR_NEWER
                    MarkFixtureFailure("frame_epoch_verify");
#endif
                    return false;}}
                return true;
            }
            catch(Exception error) when(!(error is OutOfMemoryException||error is StackOverflowException||error is AccessViolationException))
            {
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
#if VIVE_RAW_SYNTHETIC_TEST && VIVE_SYNTHETIC_BATCH_FIXTURE && !UNITY_5_3_OR_NEWER
                        MarkFixtureFailure("drain_task_event");
#endif
                        Fail();return false;}
                    if(staged&&++count>64){
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
            double now;
            if(Thread.CurrentThread.ManagedThreadId!=owner||Volatile.Read(ref terminal)!=0||stagedEpoch==null||
                !clock.TryReadRecordingAdmissionTime(out now)||
                !EpochTimeFresh(now,lastEpochVerification,stagedEpoch.EyeObserved,stagedEpoch.HeadObserved,stagedMaximumAge)||
                worker.TryCheckRecordingEpoch(stagedEpoch)!=ViveProEyeWorker.RecordingEpochResult.Current)
            {Fail();return false;}
            lastEpochVerification=now;
            return true;
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
        void Fail(){admission.IoFence.Abort();Terminate(3);}
        // Normal end is distinct from stop/withdraw/fault. Only the owner of
        // an already Completed task can freeze the same worker's status FIFO,
        // drain its previously registered records and seal the task cursor.
        // Nothing here waits for disk or certifies physical sample coverage.
        internal bool RequestCompletedDrain()
        {
            if(Thread.CurrentThread.ManagedThreadId!=owner||statusRun==null||Volatile.Read(ref terminal)!=0||
                Volatile.Read(ref finishRequested)!=0||!participant.Completed){Fail();return false;}
            try
            {
                if(!DrainTaskEvents()){Fail();return false;}
                ViveProEyeWorker.StatusSeal seal;
                if(!worker.TrySealStatusEvidence(statusRun,out seal)||seal==null){Fail();return false;}
                if(PrepareStatus()!=Preparation.Ready||!admission.TryBindDrainedStatusSeal(seal)||
                    admission.TrySealCompleted()!=ViveNamedSessionFrameAdmission.SealResult.Sealed)
                {Fail();return false;}
                if(Volatile.Read(ref terminal)!=0||Interlocked.CompareExchange(ref finishRequested,1,0)!=0)
                {Fail();return false;}
                return true;
            }
            catch(Exception error) when(!(error is OutOfMemoryException||error is StackOverflowException||error is AccessViolationException))
            {Fail();return false;}
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
                        if(Volatile.Read(ref finishRequested)==0){Thread.Sleep(20);continue;}
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
#if VIVE_RAW_SYNTHETIC_TEST && VIVE_SYNTHETIC_BATCH_FIXTURE && !UNITY_5_3_OR_NEWER
                        MarkFixtureFailure("writer_append_false");
#endif
                        Fail();break;}
                }
            }
            catch(Exception error) when(!(error is OutOfMemoryException||error is StackOverflowException||error is AccessViolationException))
            {
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
