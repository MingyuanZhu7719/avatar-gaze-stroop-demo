using System;
using System.Globalization;
using System.Threading;
using AvatarStroop.Core;

namespace AvatarStroop.Runtime
{
    // Bounded, non-persistent candidate for the actual fixed-SDK worker path.
    // It has no file writer, consumer or public UI entry. The product's
    // recording gate remains closed until the complete path is reviewed.
    internal sealed class ViveNamedSessionFrameAdmission
    {
        // Copies the fields actually exposed by the installed adapter; it does
        // not infer absent pupil/image data or a hardware capture timestamp.
        internal sealed class RawEyeRay
        {
            internal readonly ulong Mask;internal readonly bool OriginValid,DirectionValid;
            internal readonly Vec3 OriginMillimeters,Direction;
            internal RawEyeRay(ViveEyeRay ray)
            {Mask=ray.ValidityMask;OriginValid=ray.OriginReportedValid;DirectionValid=ray.DirectionReportedValid;OriginMillimeters=ray.OriginMillimeters;Direction=ray.Direction;}
        }
        internal sealed class Frame
        {
            internal readonly int NativeFrame,NativeTimestamp,Block,Segment;
            internal readonly long HeadSequence,TaskEventCursor,LossGeneration;
            internal readonly double EyeObserved,HeadObserved;double checkedAt;
            internal double CheckedAt {get{return checkedAt;}}
            internal void SealCheckedAt(double value){if(checkedAt!=0)throw new InvalidOperationException("Frame already sealed.");checkedAt=value;}
            internal readonly ulong CombinedMask;
            internal readonly Vec3 RawCombinedDirection;
            internal readonly Rotation RawHeadRotation;
            internal readonly RawEyeRay Left,Right,Combined;
            internal readonly bool NoUser,EyeDeviceConnected,HeadDeviceConnected,HeadRotationValid;
            internal readonly ViveEyeRuntimeState Runtime;
            internal readonly ViveEyeCalibrationState Calibration;
            internal readonly int? NativeResultCode,NativeRuntimeStatus;
            internal readonly Phase PhaseAtIngest;
            internal readonly string TrialAtIngest;
            internal Frame(ViveProEyePoll poll,ViveDevelopmentParticipant.NamedRecordingTaskStamp task,long loss,double checkedAt)
            {
                NativeFrame=poll.Eye.FrameSequence;NativeTimestamp=poll.Eye.Timestamp;EyeObserved=poll.Eye.ObservedLocalSeconds;
                HeadSequence=poll.Head.ObservationSequence;HeadObserved=poll.Head.ObservedLocalSeconds;
                CombinedMask=poll.Eye.Combined.ValidityMask;RawCombinedDirection=poll.Eye.Combined.Direction;RawHeadRotation=poll.Head.RawTrackingRotation;
                Left=poll.Eye.Left==null?null:new RawEyeRay(poll.Eye.Left);Right=poll.Eye.Right==null?null:new RawEyeRay(poll.Eye.Right);Combined=new RawEyeRay(poll.Eye.Combined);
                NoUser=poll.Eye.NoUser;EyeDeviceConnected=poll.EyeDeviceConnected;HeadDeviceConnected=poll.Head.DeviceConnected;HeadRotationValid=poll.Head.RotationReportedValid;
                Runtime=poll.Runtime;Calibration=poll.Calibration;NativeResultCode=poll.NativeResultCode;NativeRuntimeStatus=poll.NativeRuntimeStatus;
                Block=task.Block;Segment=task.Segment;PhaseAtIngest=task.Phase;TrialAtIngest=task.TrialId;TaskEventCursor=task.EventCursor;
                LossGeneration=loss;this.checkedAt=checkedAt;
            }
        }
        // Immutable identities come from the same participant/configuration
        // and worker-run admission; they are declarations, not authentication
        // or participant research consent. No manifest can be emitted until
        // both separate software choices have committed.
        internal sealed class Manifest
        {
            internal readonly string Session,ClockId;
            internal readonly ViveNamedSimulationPlan Plan;internal readonly ViveNamedRecordingIntent Intent;
            internal readonly ViveRawSessionContext Context;
            internal readonly double TaskChoiceObserved=-1,EyeChoiceObserved=-1;
            internal readonly bool Synthetic;internal readonly Guid SoftwareRunId;
            double choiceTime=-1;long taskBaseline=-1;
            internal double ChoiceTime {get{return choiceTime;}}
            internal long TaskBaseline {get{return taskBaseline;}}
            internal Manifest(string session,string clock,ViveNamedSimulationPlan plan,ViveNamedRecordingIntent intent,bool synthetic)
            {Session=session;ClockId=clock;Plan=plan;Intent=intent;Synthetic=synthetic;SoftwareRunId=Guid.NewGuid();}
            internal Manifest(ViveRawSessionContext context,string clock,bool synthetic,double taskObserved,double eyeObserved)
                :this(context.Configuration.SessionId,clock,context.Plan,context.Intent,synthetic)
            {
                Context=context;TaskChoiceObserved=taskObserved;EyeChoiceObserved=eyeObserved;
                if(context.IsLegacy&&(!WireCodec.Finite(taskObserved)||taskObserved<0||!WireCodec.Finite(eyeObserved)||eyeObserved<taskObserved))
                    throw new InvalidOperationException("Separate legacy software choice observations are required.");
            }
            internal void SealChoiceTime(double value,long baseline)
            {if((Context!=null&&Context.IsLegacy&&value<EyeChoiceObserved)||choiceTime>=0||Double.IsNaN(value)||Double.IsInfinity(value)||value<0||baseline< -1)throw new InvalidOperationException("Manifest choice time unavailable.");taskBaseline=baseline;choiceTime=value;}
        }
        readonly ViveDevelopmentParticipant task;readonly ViveProEyeWorker worker;readonly MonotonicClock clock;
        readonly ViveNamedSimulationPlan plan;readonly ViveNamedRecordingIntent intent;readonly int owner,capacity;readonly object runIdentity;
        readonly Manifest manifest;readonly ViveRawIoFence ioFence;
        readonly ViveRawSessionContext context;
        internal bool IsLegacy {get{return context.IsLegacy;}}
        internal ViveRawSessionContext SessionContext {get{return context;}}
        ViveRawByteBudget.QueueLedger queueLedger;
        readonly bool syntheticOnly;
        // Single owner-thread producer, one reserved consumer. Each slot is
        // published before tail and cleared before head; neither thread waits
        // for the other. These are software FIFO ordinals, not gaze time.
        object[] ring;long firstOrdinal,nextOrdinal;bool taskChoice,eyeChoice,statusEnabled,statusSealed;long choiceGeneration,lastTaskSequence=-1,lastStatusSequence=-1;
        double lastChoice=-1,enabledAt=-1,lastCommit=-1,lastStatusTime=-1;
        // P and C can coexist. R|F is an irreversible queue sentinel;
        // distinct terminal reasons are recorded in separate one-way flags.
        // No disk I/O occurs under locks.
        const int P=1,C=2,R=4,F=8,S=16;
        int state,withdrawTerminal,faultTerminal;
#if VIVE_SYNTHETIC_BATCH_FIXTURE
        internal Action<int> AfterTerminalReasonForTest;
        internal Action BeforeStatusProofPublishForTest,BeforeConsumerReceiptPublishForTest;
        internal Action AfterCountdownReservationForTest;
#endif
        long sealedWatermark=-1,sealedTaskCursor=-1;double sealedAt=-1;
        // Synthetic-only deterministic interleaving hook. Never receives data
        // and is unreachable for the installed worker path.
        internal Action AfterTailPublishedForTest {get;set;}
        internal Action BeforeSyntheticBatchTailForTest {get;set;}
        internal Action AfterSyntheticBatchAddForTest {get;set;}
        internal Action BeforeSyntheticBatchReservePublishForTest {get;set;}
        static readonly object SyntheticBatchRevoked=new object();
        object syntheticBatchState;
        // A raw presentation keeps P and one FIFO slot until the same-frame
        // callback or a safety abort. Detach irrevocably closes this slot.
        static readonly object PresentationSlotRevoked=new object();
        object presentationSlot;
        // A countdown witness is one real task event. Reserve its FIFO slot
        // and conservative encoded bound before changing the countdown state.
        // The token never retains the ring; revocation closes its shared slot.
        static readonly object CountdownSlotRevoked=new object();
        object countdownSlot;
        internal sealed class CountdownWitnessReservation
        {
            internal readonly ViveNamedSessionFrameAdmission Owner;
            internal readonly ViveDevelopmentParticipant.NamedRecordingTaskStamp Stamp;
            internal readonly long Ordinal,Revision;
            internal readonly int Frame;
            internal readonly double UnitySeconds;
            internal int Consumed;
            internal CountdownWitnessReservation(ViveNamedSessionFrameAdmission owner,ViveDevelopmentParticipant.NamedRecordingTaskStamp stamp,long ordinal,int frame,long revision,double unitySeconds)
            {Owner=owner;Stamp=stamp;Ordinal=ordinal;Frame=frame;Revision=revision;UnitySeconds=unitySeconds;}
        }
        internal sealed class PresentationReservation
        {
            internal readonly ViveNamedSessionFrameAdmission Owner;
            internal readonly ViveDevelopmentParticipant.NamedRecordingTaskStamp Stamp;
            internal readonly TrialRecord Trial;
            internal readonly ViveProEyeWorker.RecordingFrameTicket Ticket;
            internal readonly long Ordinal,Revision;
            internal readonly int Frame;
            internal readonly Colour Word,Ink;
            internal readonly double MaximumAge;
            internal int Consumed;
            internal PresentationReservation(ViveNamedSessionFrameAdmission owner,ViveDevelopmentParticipant.NamedRecordingTaskStamp stamp,
                TrialRecord trial,ViveProEyeWorker.RecordingFrameTicket ticket,long ordinal,long revision,int frame,double maximumAge)
            {Owner=owner;Stamp=stamp;Trial=trial;Ticket=ticket;Ordinal=ordinal;Revision=revision;Frame=frame;
                Word=trial.Stimulus.Word;Ink=trial.Stimulus.Ink;MaximumAge=maximumAge;}
        }
        // This fixture never accepts an installed source or authorizes task
        // mutations. It exercises only queue reservation/publication mechanics.
        internal sealed class SyntheticBatchToken
        {
            internal readonly ViveNamedSessionFrameAdmission Owner;
            internal readonly long Start;internal readonly int Limit;internal int Count,Consumed;
            internal SyntheticBatchToken(ViveNamedSessionFrameAdmission owner,long start,int limit)
            {Owner=owner;Start=start;Limit=limit;}
        }
        sealed class SyntheticBatchState
        {
            internal readonly SyntheticBatchToken Token;internal readonly object[] Target,Pending;
            internal SyntheticBatchState(SyntheticBatchToken token,object[] target,int limit)
            {Token=token;Target=target;Pending=new object[limit];}
        }
        ConsumerReceipt activeReceipt;
        StatusAdmissionProof pendingStatusProof;
        // Detach permanently closes these publication slots. A suspended
        // producer/consumer can no longer reinstall a payload after detach.
        static readonly ConsumerReceipt ConsumerSlotRevoked=new ConsumerReceipt(null);
        static readonly StatusAdmissionProof StatusSlotRevoked=new StatusAdmissionProof(null,null);
        // Only this admission creates the receipt, after publishing the exact
        // reserved worker status. It proves enqueue, not disk persistence.
        internal sealed class StatusAdmissionProof
        {
            readonly ViveNamedSessionFrameAdmission owner;readonly ViveProEyeWorker.StatusTicket ticket;
            int consumed;
            internal StatusAdmissionProof(ViveNamedSessionFrameAdmission owner,ViveProEyeWorker.StatusTicket ticket)
            {this.owner=owner;this.ticket=ticket;}
            internal bool TryConsume(ViveProEyeWorker.StatusTicket value)
            {
                if(!Object.ReferenceEquals(ticket,value)||Interlocked.Exchange(ref consumed,1)!=0)return false;
                if(!Object.ReferenceEquals(Interlocked.CompareExchange(ref owner.pendingStatusProof,null,this),this))return false;
                return !owner.ioFence.IsRevoked&&(Volatile.Read(ref owner.state)&(R|F|S))==0&&Volatile.Read(ref owner.ring)!=null;
            }
        }
        internal sealed class ConsumerReceipt
        {
            internal readonly ViveNamedSessionFrameAdmission Owner;
            internal object Record;internal long Ordinal;internal int Completed,WriteStarted;
            internal ConsumerReceipt(ViveNamedSessionFrameAdmission owner){Owner=owner;}
        }
        ViveNamedSessionFrameAdmission(ViveDevelopmentParticipant task,ViveProEyeWorker worker,MonotonicClock clock,int capacity,bool synthetic,bool legacy=false,double taskObserved=-1,double eyeObserved=-1)
        {
            if(task==null||worker==null||clock==null||capacity<1||capacity>256||!task.UsesEventClock(clock)||!worker.UsesClock(clock)||
                (!synthetic&&!worker.UsesInstalledSdkPath)||(synthetic&&worker.UsesInstalledSdkPath))
                throw new InvalidOperationException("Recording source is unavailable.");
            ViveDevelopmentParticipant.NamedRecordingTaskStamp stamp;
            if(!task.TryCaptureRawRecordingTaskStamp(clock,out stamp)||stamp.Phase!=Phase.Preparation||stamp.Context==null||
                stamp.Context.IsLegacy!=legacy||!stamp.Context.TaskRequested||!stamp.Context.EyeRequested)throw new InvalidOperationException("Explicit pre-practice intent is required.");
            object run;if(!worker.TryGetRecordingRunIdentity(out run))throw new InvalidOperationException("Recording source is unavailable.");
            this.task=task;context=stamp.Context;this.worker=worker;this.clock=clock;plan=stamp.Plan;intent=stamp.Intent;this.capacity=capacity;syntheticOnly=synthetic;runIdentity=run;
            ioFence=new ViveRawIoFence(this,task.NamedRawRevocation);
            manifest=new Manifest(context,clock.ClockId,synthetic,taskObserved,eyeObserved);
            owner=Thread.CurrentThread.ManagedThreadId;ring=new object[capacity];
            if(!task.TryAttachNamedFrameAdmission(this))
            {if(task.NamedRawRevocation.ParticipantWithdrawalRequested)Withdraw();else AbortForTechnicalFailure();
                throw new InvalidOperationException("Recording admission unavailable.");}
        }
        internal static ViveNamedSessionFrameAdmission CreateInstalledCandidate(ViveDevelopmentParticipant task,ViveProEyeWorker worker,MonotonicClock clock,int capacity)
        {return new ViveNamedSessionFrameAdmission(task,worker,clock,capacity,false);}
#if UNITY_EDITOR || (!UNITY_5_3_OR_NEWER && (STROOP_TEST_BUILD || VIVE_RAW_SYNTHETIC_TEST))
        // Unity Editor and explicitly marked standalone test builds only.
        // Unity players and unmarked standalone builds omit this factory.
        internal static ViveNamedSessionFrameAdmission CreateSyntheticForTest(ViveDevelopmentParticipant task,ViveProEyeWorker worker,MonotonicClock clock,int capacity)
        {return new ViveNamedSessionFrameAdmission(task,worker,clock,capacity,true);}
#endif
        internal static ViveNamedSessionFrameAdmission CreateLegacyInstalledCandidate(ViveDevelopmentParticipant task,ViveProEyeWorker worker,
            MonotonicClock clock,int capacity,ViveNamedRecordingSetup choices)
        {
            double taskObserved,eyeObserved;
            if(!ViveLegacyRawRecordingBuildPolicy.Available||choices==null||!choices.TryConsumeObservedTimes(clock,out taskObserved,out eyeObserved))
                throw new InvalidOperationException("Legacy raw recording is unavailable.");
            return new ViveNamedSessionFrameAdmission(task,worker,clock,capacity,false,true,taskObserved,eyeObserved);
        }
#if UNITY_EDITOR || (!UNITY_5_3_OR_NEWER && (STROOP_TEST_BUILD || VIVE_RAW_SYNTHETIC_TEST))
        internal static ViveNamedSessionFrameAdmission CreateLegacySyntheticForTest(ViveDevelopmentParticipant task,ViveProEyeWorker worker,
            MonotonicClock clock,int capacity,double taskObserved,double eyeObserved)
        {return new ViveNamedSessionFrameAdmission(task,worker,clock,capacity,true,true,taskObserved,eyeObserved);}
#endif
        internal bool MatchesLegacyObservedChoices(MonotonicClock expected,double taskObserved,double eyeObserved)
        {return context.IsLegacy&&Object.ReferenceEquals(clock,expected)&&manifest.TaskChoiceObserved==taskObserved&&manifest.EyeChoiceObserved==eyeObserved&&!ioFence.IsRevoked;}
        internal bool SyntheticOnly {get{return syntheticOnly;}}
#if VIVE_RAW_SYNTHETIC_TEST && VIVE_SYNTHETIC_BATCH_FIXTURE && !UNITY_5_3_OR_NEWER
        internal ViveRawRevocation SyntheticStageAuthorityForTest {get{return syntheticOnly?task.NamedRawRevocation:null;}}
#endif
        // The installed sink and producer must share one budget before any
        // frame is published. No rebind after a failed start is allowed.
        internal bool TryBindQueueBudget(ViveRawByteBudget budget)
        {
            if(Thread.CurrentThread.ManagedThreadId!=owner||budget==null||ioFence.IsRevoked||
                Interlocked.Read(ref nextOrdinal)!=0||Volatile.Read(ref queueLedger)!=null)return false;
            var candidate=new ViveRawByteBudget.QueueLedger(budget,manifest);
            return Interlocked.CompareExchange(ref queueLedger,candidate,null)==null&&!ioFence.IsRevoked;
        }
        internal bool UsesQueueBudget(ViveRawByteBudget budget)
        {var ledger=Volatile.Read(ref queueLedger);return ledger!=null&&ledger.Uses(budget);}
        // Existing test fixtures that do not install a ledger exercise only
        // task state. An installed source never takes that fixture-only path.
        internal bool SyntheticWithoutQueueBudget {get{return syntheticOnly&&Volatile.Read(ref queueLedger)==null;}}
        internal bool VerifyQueuedFrameBudget(ConsumerReceipt receipt,object record,ViveRawByteBudget budget,int frameBytes,int headerBytes)
        {
            var ledger=Volatile.Read(ref queueLedger);
            return ledger!=null&&ledger.Uses(budget)&&ConsumerReservationCurrent(receipt,record)&&
                ledger.VerifyEncoded(receipt.Ordinal,record,frameBytes,headerBytes);
        }
#if VIVE_SYNTHETIC_BATCH_FIXTURE
        // Test-build-only proofs never attest native storage or authorize the
        // installed/product path. Exact slot identity prevents lookalikes.
        internal sealed class SyntheticPlanProof
        {
            internal readonly ViveNamedSessionFrameAdmission Owner;internal readonly ViveBlockPlanRequest Request;
            internal int Used;internal SyntheticPlanProof(ViveNamedSessionFrameAdmission owner,ViveBlockPlanRequest request){Owner=owner;Request=request;}
        }
        internal sealed class SyntheticFrameAnchor
        {
            internal readonly ViveNamedSessionFrameAdmission Owner;internal readonly ViveProEyeWorker.RecordingFrameTicket Ticket;
            internal readonly long FifoOrdinal;internal readonly double EyeObserved,HeadObserved,MaximumAge;internal int Used;
            internal SyntheticFrameAnchor(ViveNamedSessionFrameAdmission owner,ViveProEyeWorker.RecordingFrameTicket ticket,long ordinal,double eyeObserved,double headObserved,double maximumAge)
            {Owner=owner;Ticket=ticket;FifoOrdinal=ordinal;EyeObserved=eyeObserved;HeadObserved=headObserved;MaximumAge=maximumAge;}
        }
        static readonly object SyntheticFixtureRevoked=new object();
        object syntheticPlanProof,syntheticFrameAnchor;
        internal Action BeforeSyntheticPlanProofPublishForTest {get;set;}
        internal Action BeforeSyntheticFrameAnchorPublishForTest {get;set;}
        internal Action AfterSyntheticPresentationBatchCommitForTest {get;set;}
        internal bool TryIssueSyntheticPlanProof(ViveBlockPlanRequest request,JournalEvent planEvent,out SyntheticPlanProof proof)
        {
            proof=null;if(!syntheticOnly||worker.UsesInstalledSdkPath||Thread.CurrentThread.ManagedThreadId!=owner||
                request==null||planEvent==null||State!=0||lastTaskSequence!=planEvent.Sequence||
                !TryValidateBlockPlanRequest(request,planEvent))return false;
            var candidate=new SyntheticPlanProof(this,request);
            try{var before=BeforeSyntheticPlanProofPublishForTest;if(before!=null)before();}
            catch(Exception error) when(!(error is OutOfMemoryException||error is StackOverflowException||error is AccessViolationException)){Fail();return false;}
            if(Interlocked.CompareExchange(ref syntheticPlanProof,candidate,null)!=null)return false;
            if(ioFence.IsRevoked||State!=0){Interlocked.CompareExchange(ref syntheticPlanProof,null,candidate);return false;}
            proof=candidate;return true;
        }
        internal bool ConsumeSyntheticPlanProof(SyntheticPlanProof proof,ViveBlockPlanRequest request)
        {
            if(!syntheticOnly||worker.UsesInstalledSdkPath||Thread.CurrentThread.ManagedThreadId!=owner||proof==null||
                !Object.ReferenceEquals(proof.Owner,this)||!Object.ReferenceEquals(proof.Request,request)||
                Interlocked.Exchange(ref proof.Used,1)!=0||!Object.ReferenceEquals(Interlocked.CompareExchange(ref syntheticPlanProof,null,proof),proof))return false;
            return State==0&&!ioFence.IsRevoked&&request.TryValidateCurrentForReceipt(task,clock);
        }
        internal bool TryStageSyntheticFrameWithAnchor(ViveDevelopmentParticipant.NamedRecordingTaskStamp stamp,
            ViveProEyeWorker.RecordingFrameTicket ticket,double maxAge,out SyntheticFrameAnchor anchor)
        {
            anchor=null;if(!syntheticOnly||worker.UsesInstalledSdkPath||!statusEnabled||statusSealed||lastStatusSequence<0||
                Volatile.Read(ref pendingStatusProof)!=null||ticket==null||Volatile.Read(ref syntheticFrameAnchor)!=null)return false;
            var poll=Volatile.Read(ref ticket.Frame);if(poll==null||poll.Eye==null||poll.Head==null)return false;
            double observed=poll.Eye.ObservedLocalSeconds,headObserved=poll.Head.ObservedLocalSeconds;long ordinal=Interlocked.Read(ref nextOrdinal);
            if(!TryStage(stamp,ticket,maxAge))return false;
            if(Interlocked.Read(ref nextOrdinal)!=ordinal+1||State!=0||
                worker.TryCheckRecordingEpoch(ticket)!=ViveProEyeWorker.RecordingEpochResult.Current){Fail();return false;}
            var candidate=new SyntheticFrameAnchor(this,ticket,ordinal,observed,headObserved,maxAge);
            try{var before=BeforeSyntheticFrameAnchorPublishForTest;if(before!=null)before();}
            catch(Exception error) when(!(error is OutOfMemoryException||error is StackOverflowException||error is AccessViolationException)){Fail();return false;}
            if(Interlocked.CompareExchange(ref syntheticFrameAnchor,candidate,null)!=null||ioFence.IsRevoked||State!=0){Fail();return false;}
            anchor=candidate;return true;
        }
        internal sealed class SyntheticPresentationRecord
        {
            internal const string Schema="synthetic-onset-declaration-v1";
            internal readonly JournalEvent Event;internal readonly double Onset;internal readonly string ClockId;
            internal SyntheticPresentationRecord(JournalEvent value,double onset,string clockId){Event=value;Onset=onset;ClockId=clockId;}
        }
        bool SyntheticAnchorFresh(SyntheticFrameAnchor anchor)
        {
            double now;return anchor!=null&&clock.TryReadRecordingAdmissionTime(out now)&&
                now>=anchor.EyeObserved&&now>=anchor.HeadObserved&&now-anchor.EyeObserved<=anchor.MaximumAge&&
                now-anchor.HeadObserved<=anchor.MaximumAge;
        }
        // Test-build-only experiment mutation and queue mechanics. This does
        // not prove actual camera presentation, persistence or task safety.
        internal bool TrySyntheticMarkPresented(SyntheticFrameAnchor anchor,string trialId,Action afterMutationForTest,out SyntheticPresentationRecord record)
        {
            record=null;if(!syntheticOnly||worker.UsesInstalledSdkPath||Thread.CurrentThread.ManagedThreadId!=owner||anchor==null||
                !Object.ReferenceEquals(anchor.Owner,this)||!Object.ReferenceEquals(Volatile.Read(ref syntheticFrameAnchor),anchor)||
                Interlocked.Exchange(ref anchor.Used,1)!=0||String.IsNullOrEmpty(trialId)||State!=0)return false;
            Interlocked.CompareExchange(ref syntheticFrameAnchor,null,anchor);
            SyntheticBatchToken batch=null;
            try
            {
                ViveDevelopmentParticipant.NamedRecordingTaskStamp before,after;
                if(!SyntheticAnchorFresh(anchor)||worker.TryCheckRecordingEpoch(anchor.Ticket)!=ViveProEyeWorker.RecordingEpochResult.Current||
                    !task.TryCaptureRawRecordingTaskStamp(clock,out before)||before.EventCursor!=lastTaskSequence||
                    !String.Equals(before.TrialId,trialId,StringComparison.Ordinal)){Fail();return false;}
                var trial=task.CurrentTrial;if(trial==null||!String.Equals(trial.TrialId,trialId,StringComparison.Ordinal)){Fail();return false;}
                double? previousOnset=trial.Onset;
                if(!TryReserveSyntheticBatchForTest(1,out batch))return false;
                task.MarkPresented(trialId);if(afterMutationForTest!=null)afterMutationForTest();
                if(!task.TryCaptureRawRecordingTaskStamp(clock,out after)||before.Generation>Int64.MaxValue-2||after.Generation!=before.Generation+2||
                    !Object.ReferenceEquals(task.CurrentTrial,trial)||after.EventCursor<before.EventCursor||after.EventCursor-before.EventCursor>1||
                    after.Phase!=before.Phase||after.Block!=before.Block||after.Segment!=before.Segment||after.TrialId!=before.TrialId||
                    !trial.Onset.HasValue||!SyntheticAnchorFresh(anchor)||worker.TryCheckRecordingEpoch(anchor.Ticket)!=ViveProEyeWorker.RecordingEpochResult.Current)
                {Fail();return false;}
                long delta=after.EventCursor-before.EventCursor;var events=task.ReadEventsAfter(before.EventCursor,2);
                if(events.Count!=delta||(previousOnset.HasValue?(delta!=0||trial.Onset.Value!=previousOnset.Value):delta!=1))
                {Fail();return false;}
                SyntheticPresentationRecord candidate=null;
                if(delta==1)
                {
                    var item=events[0];string word,ink;
                    if(item.Sequence!=after.EventCursor||item.Kind!="stimulus_presented"||item.TrialId!=trialId||item.ClockId!=clock.ClockId||
                        item.SessionId!=before.Session||item.BlockIndex!=before.Block||item.Segment!=before.Segment||
                        !item.Fields.TryGetValue("word",out word)||word!=trial.Stimulus.Word.ToString()||
                        !item.Fields.TryGetValue("ink",out ink)||ink!=trial.Stimulus.Ink.ToString()||item.Time<trial.Onset.Value)
                    {Fail();return false;}
                    candidate=new SyntheticPresentationRecord(item,trial.Onset.Value,clock.ClockId);
                    if(!TryAddSyntheticBatchForTest(batch,candidate))return false;
                }
                if(!Monitor.TryEnter(task.NamedRecordingGate)){Fail();return false;}
                try
                {
                    if(!task.NamedRecordingStampCurrentUnderLock(after)||!Monitor.TryEnter(worker.RecordingTicketGate)){Fail();return false;}
                    try
                    {
                        if(!SyntheticAnchorFresh(anchor)||worker.TryCheckRecordingEpoch(anchor.Ticket)!=ViveProEyeWorker.RecordingEpochResult.Current||
                            lastTaskSequence!=before.EventCursor||!TryCommitSyntheticBatchForTest(batch)){Fail();return false;}
                        var afterCommit=AfterSyntheticPresentationBatchCommitForTest;if(afterCommit!=null)afterCommit();
                        if(ioFence.IsRevoked){Fail();return false;}lastTaskSequence=after.EventCursor;
                        if(!SyntheticAnchorFresh(anchor)||worker.TryCheckRecordingEpoch(anchor.Ticket)!=ViveProEyeWorker.RecordingEpochResult.Current||ioFence.IsRevoked){Fail();return false;}
                    }
                    finally{Monitor.Exit(worker.RecordingTicketGate);}
                }
                finally{Monitor.Exit(task.NamedRecordingGate);}
                record=candidate;return true;
            }
            catch(Exception error) when(!(error is OutOfMemoryException||error is StackOverflowException||error is AccessViolationException))
            {Fail();return false;}
        }
#endif
        internal ViveRawIoFence IoFence {get{return ioFence;}}
        internal bool IsIoFenceAuthority(ViveRawIoFence candidate)
        {return Object.ReferenceEquals(ioFence,candidate)&&candidate.MatchesAuthority(this,task.NamedRawRevocation);}
        // A pump must bind to this exact task, worker and shared clock; matching
        // IDs or another worker with the same clock cannot establish ownership.
        internal bool UsesExactSources(ViveDevelopmentParticipant participant,ViveProEyeWorker source,MonotonicClock sourceClock)
        {return Object.ReferenceEquals(task,participant)&&Object.ReferenceEquals(worker,source)&&Object.ReferenceEquals(clock,sourceClock);}
        // Owner-thread instantaneous check for a flushed block-plan event.
        // It grants neither participant consent nor an optical presentation.
        internal bool TryValidateBlockPlanRequest(ViveBlockPlanRequest request,JournalEvent planned)
        {
            if(Thread.CurrentThread.ManagedThreadId!=owner||request==null||planned==null||ioFence.IsRevoked||
                !request.BelongsToContext(task,clock,context,task.NamedRawRevocation)||!request.MatchesWriterPlannedEvent(planned)||
                !request.TryValidateCurrentForReceipt(task,clock))return false;
            object run;
            return worker.TryGetRecordingRunIdentity(out run)&&Object.ReferenceEquals(runIdentity,run)&&!ioFence.IsRevoked;
        }
        internal bool PhysicalWriterAttached {get{return false;}}
        internal bool ResearchAdmission {get{return false;}}
        // The present choices occur after participant creation. Earlier task
        // events are deliberately omitted and cannot be called complete.
        internal bool PreChoiceTaskPrefixUnknown {get{return true;}}
        internal int State {get{int s=Volatile.Read(ref state);return Volatile.Read(ref withdrawTerminal)!=0?2:
            Volatile.Read(ref faultTerminal)!=0||ioFence.IsRevoked||(s&(R|F))!=0?3:(s&P)!=0?1:0;}}
        internal bool Sealed {get{return (Volatile.Read(ref state)&S)!=0;}}
        internal enum SealResult { Pending, Sealed, Faulted }
        internal int Count
        {
            get
            {
                if(Volatile.Read(ref ring)==null)return 0;
                // Diagnostic snapshot only: concurrent operations can make
                // these two independent reads inconsistent.
                long first=Interlocked.Read(ref firstOrdinal),last=Interlocked.Read(ref nextOrdinal),difference=last-first;
                return difference<0||difference>capacity?-1:(int)difference;
            }
        }
        bool AddBit(int bit)
        {
            for(int attempt=0;attempt<64;attempt++){int s=Volatile.Read(ref state);if(ioFence.IsRevoked||(s&(R|F|bit))!=0||(bit==P&&(s&S)!=0))return false;
                if(Interlocked.CompareExchange(ref state,s|bit,s)==s)return true;}
            return false;
        }
        bool ClearBitIfLive(int bit)
        {
            for(int attempt=0;attempt<64;attempt++){int s=Volatile.Read(ref state);if(ioFence.IsRevoked||(s&(R|F))!=0||(s&bit)==0)return false;
                if(Interlocked.CompareExchange(ref state,s&~bit,s)==s)return true;}
            return false;
        }
        bool Reserve(){return AddBit(P);}
        bool ProducerLive(){int s=Volatile.Read(ref state);return !ioFence.IsRevoked&&(s&(R|F))==0&&(s&P)!=0;}
        // The current named one-to-four-block protocol emits at most seven
        // task events in one ordinary participant action (confirming a four
        // item plan). Eight is an operational cap, not a claim about future
        // protocols. An unexpected ninth event faults after mutation and the
        // affected interval is UNKNOWN; it is never silently discarded.
        internal const int OrdinaryActionEventLimit=8;
        const int MaximumTaskFrameBytes=1048616;

        // Owner-thread ordinary task actions only. The caller must handle
        // pause, withdrawal, device loss and other safety actions separately:
        // they must never wait for or be denied by this capacity reservation.
        // No disk operation or flush is performed here. A returned success
        // proves only bounded FIFO admission, not file persistence or display.
        internal bool TryRunReservedOrdinaryMutation(Action action)
        {
            if(Thread.CurrentThread.ManagedThreadId!=owner||action==null||!Reserve()){Fail();return false;}
            var target=Volatile.Read(ref ring);int placed=0;long start=-1;bool completed=false;
            var inserted=new object[OrdinaryActionEventLimit];
            try
            {
                ViveDevelopmentParticipant.NamedRecordingTaskStamp before,after;
                var ledger=Volatile.Read(ref queueLedger);
                long first=Interlocked.Read(ref firstOrdinal),last=Interlocked.Read(ref nextOrdinal);
                int upper=checked(OrdinaryActionEventLimit*MaximumTaskFrameBytes);
                if(!ProducerLive()||!taskChoice||!eyeChoice||!statusEnabled||statusSealed||target==null||ledger==null||
                    first<0||last<first||last>Int64.MaxValue-OrdinaryActionEventLimit||last-first>capacity||
                    capacity-(last-first)<OrdinaryActionEventLimit||!ledger.CanReserveUpper(last,upper)||
                    !task.TryCaptureRawRecordingTaskStamp(clock,out before)||before==null||before.EventCursor!=lastTaskSequence||
                    !Object.ReferenceEquals(before.Context,context)||!Object.ReferenceEquals(before.Plan,plan)||!Object.ReferenceEquals(before.Intent,intent)||before.Generation>Int64.MaxValue-2)
                {Fail();return false;}
                for(int i=0;i<OrdinaryActionEventLimit;i++)
                    if(Volatile.Read(ref target[(int)((last+i)%capacity)])!=null){Fail();return false;}
                start=last;
                action();
                if(!task.TryCaptureRawRecordingTaskStamp(clock,out after)||after==null||after.Generation!=before.Generation+2||
                    after.EventCursor<before.EventCursor||after.EventCursor-before.EventCursor>OrdinaryActionEventLimit||
                    !Object.ReferenceEquals(after.Context,context)||!Object.ReferenceEquals(after.Plan,plan)||!Object.ReferenceEquals(after.Intent,intent)||
                    !String.Equals(after.Session,before.Session,StringComparison.Ordinal))
                {Fail();return false;}
                int count=checked((int)(after.EventCursor-before.EventCursor));
                var items=task.ReadEventsAfter(before.EventCursor,OrdinaryActionEventLimit+1);
                if(items==null||items.Count!=count||!Monitor.TryEnter(task.NamedRecordingGate)){Fail();return false;}
                try
                {
                    if(!task.NamedRecordingStampCurrentUnderLock(after)||!ProducerLive()||
                        !Object.ReferenceEquals(target,Volatile.Read(ref ring))||Interlocked.Read(ref nextOrdinal)!=start||
                        lastTaskSequence!=before.EventCursor){Fail();return false;}
                    for(int i=0;i<count;i++)
                    {
                        var item=items[i];long ordinal=start+i;
                        if(item==null||item.Sequence!=before.EventCursor+i+1||
                            !String.Equals(item.ClockId,clock.ClockId,StringComparison.Ordinal)||
                            !String.Equals(item.SessionId,before.Session,StringComparison.Ordinal)||
                            !context.MatchesEvent(item)||
                            !ProducerLive()||!ledger.TryCharge(ordinal,item)||
                            Interlocked.CompareExchange(ref target[(int)(ordinal%capacity)],item,null)!=null)
                        {Fail();return false;}
                        inserted[i]=item;placed++;
                    }
                    if(!ProducerLive()||!Object.ReferenceEquals(target,Volatile.Read(ref ring))){Fail();return false;}
                    Interlocked.Exchange(ref nextOrdinal,start+count);
                    lastTaskSequence=after.EventCursor;
                    if(!ClearBitIfLive(P)||ioFence.IsRevoked){Fail();return false;}
                    completed=true;return true;
                }
                finally{Monitor.Exit(task.NamedRecordingGate);}
            }
            catch(Exception error) when(!(error is OutOfMemoryException||error is StackOverflowException||error is AccessViolationException))
            {Fail();return false;}
            finally
            {
                if(!completed)
                {
                    // Slots not published as a batch are best-effort detached.
                    // An in-flight consumer or native I/O cannot be recalled.
                    if(target!=null&&start>=0)for(int i=0;i<placed;i++)
                        Interlocked.CompareExchange(ref target[(int)((start+i)%capacity)],null,inserted[i]);
                    Fail();
                }
            }
        }
        // Owner-thread only. This is a capacity reservation, not a display,
        // authorization, write, flush, or durability receipt.
        internal bool TryReserveCountdownWitness(int frame,long revision,double unitySeconds,out CountdownWitnessReservation reservation)
        {
            reservation=null;
            if(Thread.CurrentThread.ManagedThreadId!=owner||frame<0||revision<0||Double.IsNaN(unitySeconds)||
                Double.IsInfinity(unitySeconds)||unitySeconds<0||!Reserve()){Fail();return false;}
            try
            {
                ViveDevelopmentParticipant.NamedRecordingTaskStamp stamp;
                var ledger=Volatile.Read(ref queueLedger);var target=Volatile.Read(ref ring);
                long first=Interlocked.Read(ref firstOrdinal),last=Interlocked.Read(ref nextOrdinal);
                if(!ProducerLive()||!taskChoice||!eyeChoice||ledger==null||target==null||
                    first<0||last<first||last==Int64.MaxValue||last-first>=capacity||
                    Volatile.Read(ref target[(int)(last%capacity)])!=null||!ledger.CanReserveUpper(last,1048616)||
                    !task.TryCaptureRawRecordingTaskStamp(clock,out stamp)||stamp==null||stamp.Phase!=Phase.Countdown||
                    stamp.EventCursor!=lastTaskSequence||!Object.ReferenceEquals(stamp.Context,context)||!Object.ReferenceEquals(stamp.Plan,plan)||
                    !Object.ReferenceEquals(stamp.Intent,intent)||!task.InputReady||!task.RawCountdownAwaitingSoftwareWitness)
                {Fail();return false;}
                var candidate=new CountdownWitnessReservation(this,stamp,last,frame,revision,unitySeconds);
                if(Interlocked.CompareExchange(ref countdownSlot,candidate,null)!=null)
                {Fail();return false;}
#if VIVE_SYNTHETIC_BATCH_FIXTURE
                if(syntheticOnly){var hook=AfterCountdownReservationForTest;if(hook!=null)hook();}
#endif
                if(!ProducerLive()||!Object.ReferenceEquals(target,Volatile.Read(ref ring)))
                {Fail();return false;}
                reservation=candidate;return true;
            }
            catch(Exception error) when(!(error is OutOfMemoryException||error is StackOverflowException||error is AccessViolationException))
            {Fail();return false;}
        }
        // A post-mutation failure cannot be rolled back: callers must mask and
        // treat the affected interval as UNKNOWN. No writer wait occurs here.
        internal bool TryCommitCountdownWitness(CountdownWitnessReservation token)
        {
            if(Thread.CurrentThread.ManagedThreadId!=owner||token==null||!Object.ReferenceEquals(token.Owner,this)||
                Interlocked.Exchange(ref token.Consumed,1)!=0||!Object.ReferenceEquals(Volatile.Read(ref countdownSlot),token))
            {Fail();return false;}
            try
            {
                ViveDevelopmentParticipant.NamedRecordingTaskStamp after;
                if(!task.TryCaptureRawRecordingTaskStamp(clock,out after)||after==null||token.Stamp.Generation>Int64.MaxValue-2||
                    after.Generation!=token.Stamp.Generation+2||token.Stamp.EventCursor==Int64.MaxValue||
                    after.EventCursor!=token.Stamp.EventCursor+1||after.Phase!=Phase.Countdown||after.Block!=token.Stamp.Block||
                    after.Segment!=token.Stamp.Segment||!String.Equals(after.TrialId,token.Stamp.TrialId,StringComparison.Ordinal)||
                    !Object.ReferenceEquals(after.Context,context)||!Object.ReferenceEquals(after.Plan,plan)||!Object.ReferenceEquals(after.Intent,intent)||
                    task.RawCountdownAwaitingSoftwareWitness||!task.InputReady){Fail();return false;}
                var items=task.ReadEventsAfter(token.Stamp.EventCursor,2);
                if(items.Count!=1){Fail();return false;}var item=items[0];
                string taskClock,proxyFrame,proxyRevision,proxySeconds,basis;
                if(item.Sequence!=after.EventCursor||item.Kind!="countdown_software_witness"||
                    item.ClockId!=clock.ClockId||item.SessionId!=token.Stamp.Session||item.BlockIndex!=after.Block||item.Segment!=after.Segment||
                    !context.MatchesEvent(item)||
                    !item.Fields.TryGetValue("task_clock_id",out taskClock)||taskClock!=clock.ClockId||
                    !item.Fields.TryGetValue("unity_proxy_frame",out proxyFrame)||proxyFrame!=token.Frame.ToString(CultureInfo.InvariantCulture)||
                    !item.Fields.TryGetValue("unity_proxy_revision",out proxyRevision)||proxyRevision!=token.Revision.ToString(CultureInfo.InvariantCulture)||
                    !item.Fields.TryGetValue("unity_proxy_seconds",out proxySeconds)||proxySeconds!=token.UnitySeconds.ToString("R",CultureInfo.InvariantCulture)||
                    !item.Fields.TryGetValue("basis",out basis)||basis!="caller_declared_software_render_not_optical")
                {Fail();return false;}
                if(!Monitor.TryEnter(task.NamedRecordingGate)){Fail();return false;}
                try
                {
                    var target=Volatile.Read(ref ring);
                    if(!task.NamedRecordingStampCurrentUnderLock(after)||!ProducerLive()||target==null||
                        Interlocked.Read(ref nextOrdinal)!=token.Ordinal||lastTaskSequence!=token.Stamp.EventCursor)
                    {Fail();return false;}
                    if(!Publish(target,item)){Fail();return false;}
                    lastTaskSequence=after.EventCursor;
                    if(!Object.ReferenceEquals(Interlocked.CompareExchange(ref countdownSlot,null,token),token)||ioFence.IsRevoked)
                    {Fail();return false;}
                    return true;
                }
                finally{Monitor.Exit(task.NamedRecordingGate);}
            }
            catch(Exception error) when(!(error is OutOfMemoryException||error is StackOverflowException||error is AccessViolationException))
            {Fail();return false;}
        }
        internal void CancelCountdownWitness(CountdownWitnessReservation token)
        {if(token!=null&&Object.ReferenceEquals(token.Owner,this))Interlocked.Exchange(ref token.Consumed,1);Fail();}
        bool PresentationEpochFresh(ViveProEyeWorker.RecordingFrameTicket ticket,double maximumAge)
        {
            double now;
            return ticket!=null&&maximumAge>0&&maximumAge<=1&&!Double.IsNaN(maximumAge)&&!Double.IsInfinity(maximumAge)&&
                clock.TryReadRecordingAdmissionTime(out now)&&ticket.EyeObserved>=enabledAt&&ticket.HeadObserved>=enabledAt&&
                now>=ticket.EyeObserved&&now>=ticket.HeadObserved&&now-ticket.EyeObserved<=maximumAge&&now-ticket.HeadObserved<=maximumAge&&
                worker.TryCheckRecordingEpoch(ticket)==ViveProEyeWorker.RecordingEpochResult.Current&&!ioFence.IsRevoked;
        }
        // Reserves a real FIFO ordinal and a conservative encoded-size bound.
        // No file operation occurs. The owner must either commit in this same
        // Unity frame or abort; a token does not establish that pixels were seen.
        internal bool TryReservePresentation(ViveProEyeWorker.RecordingFrameTicket ticket,double maximumAge,int unityFrame,long revision,
            out PresentationReservation reservation)
        {
            reservation=null;
            if(Thread.CurrentThread.ManagedThreadId!=owner||ticket==null||unityFrame<0||revision<0||!Reserve()){Fail();return false;}
            try
            {
                ViveDevelopmentParticipant.NamedRecordingTaskStamp stamp;ViveTrialOpenRequest planned;
                var ledger=Volatile.Read(ref queueLedger);var target=Volatile.Read(ref ring);
                long first=Interlocked.Read(ref firstOrdinal),last=Interlocked.Read(ref nextOrdinal);
                if(!ProducerLive()||!taskChoice||!eyeChoice||!statusEnabled||statusSealed||ledger==null||target==null||
                    first<0||last<first||last==Int64.MaxValue||last-first>=capacity||Volatile.Read(ref target[(int)(last%capacity)])!=null||
                    !ledger.CanReserveUpper(last,1048616)||!PresentationEpochFresh(ticket,maximumAge)||
                    !task.TryCaptureRawRecordingTaskStamp(clock,out stamp)||stamp==null||stamp.EventCursor!=lastTaskSequence||
                    !Object.ReferenceEquals(stamp.Context,context)||!Object.ReferenceEquals(stamp.Plan,plan)||!Object.ReferenceEquals(stamp.Intent,intent)||
                    (stamp.Phase!=Phase.Practice&&stamp.Phase!=Phase.MainTask)||!task.InputReady||task.ObservationEnabled||
                    !ViveTrialOpenRequest.TryCapture(task,clock,out planned)||planned==null)
                {Fail();return false;}
                var trial=task.CurrentTrial;
                if(trial==null||trial.Stimulus==null||trial.Outcome!=TrialOutcome.Pending||trial.Onset.HasValue||
                    !String.Equals(stamp.TrialId,trial.TrialId,StringComparison.Ordinal)||!String.Equals(planned.Trial,trial.TrialId,StringComparison.Ordinal))
                {Fail();return false;}
                var candidate=new PresentationReservation(this,stamp,trial,ticket,last,revision,unityFrame,maximumAge);
                if(Interlocked.CompareExchange(ref presentationSlot,candidate,null)!=null||!ProducerLive()||
                    !Object.ReferenceEquals(target,Volatile.Read(ref ring))||!PresentationEpochFresh(ticket,maximumAge))
                {Fail();return false;}
                reservation=candidate;return true;
            }
            catch(Exception error) when(!(error is OutOfMemoryException||error is StackOverflowException||error is AccessViolationException))
            {Fail();return false;}
        }
        // Must be called only by the owner from the matching render callback.
        // The task event is the participant's actual immutable JournalEvent;
        // enqueue means neither file durability nor HMD visibility. Failure
        // after MarkPresented faults the interval; it is never rolled back.
        internal bool TryCommitPresentation(PresentationReservation token,int unityFrame,long revision,string trialId,Colour word,Colour ink,double unityRenderSeconds)
        {
            if(Thread.CurrentThread.ManagedThreadId!=owner||token==null||!Object.ReferenceEquals(token.Owner,this)||
                Interlocked.Exchange(ref token.Consumed,1)!=0||!Object.ReferenceEquals(Volatile.Read(ref presentationSlot),token)||
                unityFrame!=token.Frame||revision!=token.Revision||!String.Equals(trialId,token.Stamp.TrialId,StringComparison.Ordinal)||
                word!=token.Word||ink!=token.Ink||Double.IsNaN(unityRenderSeconds)||Double.IsInfinity(unityRenderSeconds)||unityRenderSeconds<0){Fail();return false;}
            try
            {
                if(!Monitor.TryEnter(task.NamedRecordingGate)){Fail();return false;}
                try
                {
                    if(!task.NamedRecordingStampCurrentUnderLock(token.Stamp)||task.ObservationEnabled){Fail();return false;}
                    {
                        var target=Volatile.Read(ref ring);
                        if(!ProducerLive()||!PresentationEpochFresh(token.Ticket,token.MaximumAge)||!task.InputReady||target==null||
                            Interlocked.Read(ref nextOrdinal)!=token.Ordinal||
                            lastTaskSequence!=token.Stamp.EventCursor||!Object.ReferenceEquals(task.CurrentTrial,token.Trial)||
                            token.Trial.Outcome!=TrialOutcome.Pending||token.Trial.Onset.HasValue||token.Trial.Stimulus==null||
                            token.Trial.Stimulus.Word!=token.Word||token.Trial.Stimulus.Ink!=token.Ink){Fail();return false;}
                        task.MarkPresentedWithSoftwareProxy(trialId,unityRenderSeconds,unityFrame,revision);
                        ViveDevelopmentParticipant.NamedRecordingTaskStamp after;
                        if(!task.TryCaptureRawRecordingTaskStamp(clock,out after)||after==null||token.Stamp.Generation>Int64.MaxValue-2||
                            after.Generation!=token.Stamp.Generation+2||token.Stamp.EventCursor==Int64.MaxValue||
                            after.EventCursor!=token.Stamp.EventCursor+1||after.Phase!=token.Stamp.Phase||after.Block!=token.Stamp.Block||
                            after.Segment!=token.Stamp.Segment||!String.Equals(after.TrialId,trialId,StringComparison.Ordinal)||
                            !Object.ReferenceEquals(after.Context,context)||!Object.ReferenceEquals(after.Plan,plan)||!Object.ReferenceEquals(after.Intent,intent)||
                            !Object.ReferenceEquals(task.CurrentTrial,token.Trial)||!token.Trial.Onset.HasValue||
                            !PresentationEpochFresh(token.Ticket,token.MaximumAge)){Fail();return false;}
                        var items=task.ReadEventsAfter(token.Stamp.EventCursor,2);string eventWord,eventInk,onset,onsetClock,onsetBasis,proxy,proxyClock,proxyFrame,proxyRevision,proxyBasis,proxySchema;
                        if(items.Count!=1){Fail();return false;}var item=items[0];
                        if(item.Sequence!=after.EventCursor||item.Kind!="stimulus_presented"||item.TrialId!=trialId||
                            item.ClockId!=clock.ClockId||item.SessionId!=token.Stamp.Session||item.BlockIndex!=after.Block||item.Segment!=after.Segment||
                            !context.MatchesEvent(item)||
                            !item.Fields.TryGetValue("word",out eventWord)||eventWord!=word.ToString()||
                            !item.Fields.TryGetValue("ink",out eventInk)||eventInk!=ink.ToString()||item.Time<token.Trial.Onset.Value||
                            !item.Fields.TryGetValue("software_proxy_schema",out proxySchema)||proxySchema!="1"||
                            !item.Fields.TryGetValue("task_onset_seconds",out onset)||onset!=token.Trial.Onset.Value.ToString("R",CultureInfo.InvariantCulture)||
                            !item.Fields.TryGetValue("task_onset_clock_id",out onsetClock)||onsetClock!=clock.ClockId||
                            !item.Fields.TryGetValue("task_onset_basis",out onsetBasis)||onsetBasis!="task_clock_after_caller_declared_unity_proxy"||
                            !item.Fields.TryGetValue("unity_render_proxy_seconds",out proxy)||proxy!=unityRenderSeconds.ToString("R",CultureInfo.InvariantCulture)||
                            !item.Fields.TryGetValue("unity_render_proxy_clock_id",out proxyClock)||proxyClock!="unity-realtime-since-startup"||
                            !item.Fields.TryGetValue("unity_render_proxy_frame",out proxyFrame)||proxyFrame!=unityFrame.ToString(CultureInfo.InvariantCulture)||
                            !item.Fields.TryGetValue("unity_render_proxy_revision",out proxyRevision)||proxyRevision!=revision.ToString(CultureInfo.InvariantCulture)||
                            !item.Fields.TryGetValue("unity_render_proxy_basis",out proxyBasis)||proxyBasis!="caller_declared_unity_render_proxy_not_photon_onset"||
                            !task.NamedRecordingStampCurrentUnderLock(after)||!PresentationEpochFresh(token.Ticket,token.MaximumAge))
                        {Fail();return false;}
                        if(!Publish(target,item)){Fail();return false;}
                        lastTaskSequence=after.EventCursor;
                        if(!Object.ReferenceEquals(Interlocked.CompareExchange(ref presentationSlot,null,token),token)||ioFence.IsRevoked)
                        {Fail();return false;}
                        return true;
                    }
                }
                finally{Monitor.Exit(task.NamedRecordingGate);}
            }
            catch(Exception error) when(!(error is OutOfMemoryException||error is StackOverflowException||error is AccessViolationException))
            {Fail();return false;}
        }
        // No retry: an absent/late callback makes the interval unknown.
        internal void CancelPresentation(PresentationReservation token)
        {if(token!=null&&Object.ReferenceEquals(token.Owner,this))Interlocked.Exchange(ref token.Consumed,1);Fail();}
        void SetTerminal(int bit)
        {
            // Technical fault and withdrawal can occur concurrently. Their
            // independent flags retain both causes; withdrawal keeps the old
            // diagnostic priority. Replacing all queue bits with a sentinel
            // invalidates any outstanding CAS without a retry loop.
            if(bit==R)Interlocked.Exchange(ref withdrawTerminal,1);
            else if(bit==F)Interlocked.Exchange(ref faultTerminal,1);
            else throw new ArgumentOutOfRangeException("bit");
#if VIVE_SYNTHETIC_BATCH_FIXTURE
            if(syntheticOnly){var hook=AfterTerminalReasonForTest;if(hook!=null)hook(bit);}
#endif
            Interlocked.Exchange(ref state,R|F);
        }
        void Fail()
        {
            ioFence.Abort();
            worker.RevokeStatusEvidence();
            SetTerminal(F);
            Detach();
        }
        void Detach()
        {
            Interlocked.Exchange(ref ring,null);
            Interlocked.Exchange(ref presentationSlot,PresentationSlotRevoked);
            Interlocked.Exchange(ref countdownSlot,CountdownSlotRevoked);
#if VIVE_SYNTHETIC_BATCH_FIXTURE
            Interlocked.Exchange(ref syntheticPlanProof,SyntheticFixtureRevoked);Interlocked.Exchange(ref syntheticFrameAnchor,SyntheticFixtureRevoked);
#endif
            // A single irreversible sentinel prevents a suspended producer
            // from reinstalling the detached ring after Withdraw returns.
            var batch=Interlocked.Exchange(ref syntheticBatchState,SyntheticBatchRevoked) as SyntheticBatchState;
            if(batch!=null)Array.Clear(batch.Pending,0,batch.Pending.Length);
            Interlocked.Exchange(ref pendingStatusProof,StatusSlotRevoked);
            var receipt=Interlocked.Exchange(ref activeReceipt,ConsumerSlotRevoked);
            if(receipt!=null)Interlocked.Exchange(ref receipt.Record,null);
        }
        internal void Withdraw(){ioFence.Revoke();worker.RevokeStatusEvidence();SetTerminal(R);Detach();}
        // A failed installation is a technical fault, never evidence that a
        // participant withdrew. Concurrent explicit withdrawal remains a
        // separate terminal reason and still takes priority in State.
        internal void AbortForTechnicalFailure(){Fail();}
        // Owner may seal only after completed task mutations are finished and
        // all authoritative task events have already been queued. Pending is
        // not a close receipt; no caller waits for the writer here.
        internal SealResult TrySealCompleted()
        {
            if(Thread.CurrentThread.ManagedThreadId!=owner){Fail();return SealResult.Faulted;}
            if(!Reserve())
            {if(!ioFence.IsRevoked&&Sealed&&(Volatile.Read(ref state)&(R|F))==0)return SealResult.Sealed;Fail();return SealResult.Faulted;}
            try
            {
                ViveDevelopmentParticipant.NamedRecordingTaskStamp stamp;
                if(!task.TryCaptureRawRecordingTaskStamp(clock,out stamp)){Fail();return SealResult.Faulted;}
                if(!Monitor.TryEnter(task.NamedRecordingGate)){Fail();return SealResult.Faulted;}
                try
                {
                    if(!task.NamedRecordingStampCurrentUnderLock(stamp)||!Object.ReferenceEquals(stamp.Context,context)||!Object.ReferenceEquals(stamp.Plan,plan)||
                        !Object.ReferenceEquals(stamp.Intent,intent)||!taskChoice||!eyeChoice||Volatile.Read(ref ring)==null)
                    {Fail();return SealResult.Faulted;}
                    if(stamp.Phase!=Phase.Ended||!task.Completed||lastTaskSequence<stamp.EventCursor||!statusSealed)
                    {if(!ClearBitIfLive(P)){Fail();return SealResult.Faulted;}return SealResult.Pending;}
                    if(lastTaskSequence!=stamp.EventCursor){Fail();return SealResult.Faulted;}
                    double now;if(!clock.TryReadRecordingAdmissionTime(out now)||now<enabledAt||now<lastCommit)
                    {Fail();return SealResult.Faulted;}
                    sealedTaskCursor=stamp.EventCursor;sealedWatermark=Interlocked.Read(ref nextOrdinal);sealedAt=now;
                    for(int attempt=0;attempt<64;attempt++)
                    {
                        int s=Volatile.Read(ref state);
                        if(ioFence.IsRevoked||(s&(R|F|S))!=0||(s&P)==0){Fail();return SealResult.Faulted;}
                        if(Interlocked.CompareExchange(ref state,(s&~P)|S,s)==s)
                        {if(ioFence.IsRevoked){Fail();return SealResult.Faulted;}return SealResult.Sealed;}
                    }
                    Fail();return SealResult.Faulted;
                }
                finally{Monitor.Exit(task.NamedRecordingGate);}
            }
            catch(Exception error) when(!(error is OutOfMemoryException||error is StackOverflowException||error is AccessViolationException))
            {Fail();return SealResult.Faulted;}
        }
        internal bool TryGetSealedBoundary(out long watermark,out long taskCursor,out double observedAt)
        {
            watermark=-1;taskCursor=-1;observedAt=-1;int s=Volatile.Read(ref state);
            if(ioFence.IsRevoked||(s&S)==0||(s&(R|F|P))!=0||Volatile.Read(ref ring)==null)return false;
            long w=Interlocked.Read(ref sealedWatermark),cursor=Interlocked.Read(ref sealedTaskCursor);double at=sealedAt;
            if(w<0||cursor< -1||Double.IsNaN(at)||Double.IsInfinity(at)||at<0||
                ioFence.IsRevoked||(Volatile.Read(ref state)&(R|F))!=0)return false;
            watermark=w;taskCursor=cursor;observedAt=at;return true;
        }
        internal bool TryGetDrainedSealBoundary(out long watermark,out long taskCursor,out double observedAt)
        {
            watermark=-1;taskCursor=-1;observedAt=-1;
            long w,c;double at;if(!TryGetSealedBoundary(out w,out c,out at))return false;
            int s=Volatile.Read(ref state);
            if((s&(P|C|R|F))!=0||Interlocked.Read(ref firstOrdinal)!=w||Interlocked.Read(ref nextOrdinal)!=w||
                Volatile.Read(ref activeReceipt)!=null||(Volatile.Read(ref state)&(P|C|R|F))!=0)return false;
            watermark=w;taskCursor=c;observedAt=at;return true;
        }
        // Called only with P reserved. A full queue is a real loss boundary:
        // fail closed instead of overwriting or silently dropping an event.
        bool Publish(object[] target,object value)
        {
            long last=Interlocked.Read(ref nextOrdinal),first=Interlocked.Read(ref firstOrdinal);
            if(!ProducerLive()||target==null||!Object.ReferenceEquals(target,Volatile.Read(ref ring))||value==null||
                first<0||last<first||last==Int64.MaxValue||last-first>=capacity){Fail();return false;}
            var ledger=Volatile.Read(ref queueLedger);
            if((!syntheticOnly&&ledger==null)||(ledger!=null&&!ledger.TryCharge(last,value))){Fail();return false;}
            int index=(int)(last%capacity);
            if(Interlocked.CompareExchange(ref target[index],value,null)!=null){Fail();return false;}
            Interlocked.Exchange(ref nextOrdinal,last+1);
            if(syntheticOnly){var hook=AfterTailPublishedForTest;if(hook!=null)hook();}
            // The consumer cannot treat the published tail as committed until
            // it has observed P clear after its slot/tail snapshot.
            if(!ClearBitIfLive(P))
            {Interlocked.CompareExchange(ref target[index],null,value);Fail();return false;}
            return true;
        }
        // Synthetic only: reserves worst-case capacity without waiting for the
        // consumer. It is not a bound on real task events or consent to mutate.
        internal bool TryReserveSyntheticBatchForTest(int limit,out SyntheticBatchToken token)
        {
            token=null;if(!syntheticOnly||Volatile.Read(ref queueLedger)!=null)return false;
            if(Thread.CurrentThread.ManagedThreadId!=owner||limit<1||limit>capacity||!Reserve()){Fail();return false;}
            var target=Volatile.Read(ref ring);long first=Interlocked.Read(ref firstOrdinal),last=Interlocked.Read(ref nextOrdinal);
            if(!ProducerLive()||!taskChoice||!eyeChoice||target==null||first<0||last<first||last-first>capacity||
                last>Int64.MaxValue-limit||capacity-(last-first)<limit||Volatile.Read(ref syntheticBatchState)!=null)
            {Fail();return false;}
            var candidate=new SyntheticBatchToken(this,last,limit);
            var batch=new SyntheticBatchState(candidate,target,limit);
            try{var before=BeforeSyntheticBatchReservePublishForTest;if(before!=null)before();}
            catch(Exception error) when(!(error is OutOfMemoryException||error is StackOverflowException||error is AccessViolationException))
            {Fail();return false;}
            if(Interlocked.CompareExchange(ref syntheticBatchState,batch,null)!=null||!ProducerLive()||
                !Object.ReferenceEquals(target,Volatile.Read(ref ring))){Fail();return false;}
            token=candidate;return true;
        }
        internal bool TryAddSyntheticBatchForTest(SyntheticBatchToken token,object value)
        {
            if(!syntheticOnly)return false;
            var batch=Volatile.Read(ref syntheticBatchState) as SyntheticBatchState;
            var target=batch==null?null:batch.Target;var pending=batch==null?null:batch.Pending;
            if(Thread.CurrentThread.ManagedThreadId!=owner||token==null||!Object.ReferenceEquals(token.Owner,this)||
                batch==null||!Object.ReferenceEquals(batch.Token,token)||Volatile.Read(ref token.Consumed)!=0||
                !ProducerLive()||target==null||pending==null||!Object.ReferenceEquals(target,Volatile.Read(ref ring))||value==null||token.Count>=token.Limit)
            {Fail();return false;}
            int index=token.Count++;Interlocked.Exchange(ref pending[index],value);
            try{var after=AfterSyntheticBatchAddForTest;if(after!=null)after();}
            catch(Exception error) when(!(error is OutOfMemoryException||error is StackOverflowException||error is AccessViolationException))
            {Interlocked.CompareExchange(ref pending[index],null,value);Fail();return false;}
            if(!ProducerLive()||!Object.ReferenceEquals(batch,Volatile.Read(ref syntheticBatchState)))
            {Interlocked.CompareExchange(ref pending[index],null,value);Fail();return false;}return true;
        }
        // Cancellation here is safe only because this fixture performs no
        // Experiment mutation. Real mutation cannot be assumed rolled back.
        internal bool TryCancelSyntheticBatchForTest(SyntheticBatchToken token)
        {
            if(!syntheticOnly)return false;
            var batch=Volatile.Read(ref syntheticBatchState) as SyntheticBatchState;
            if(Thread.CurrentThread.ManagedThreadId!=owner||token==null||!Object.ReferenceEquals(token.Owner,this)||
                batch==null||!Object.ReferenceEquals(batch.Token,token)||Interlocked.Exchange(ref token.Consumed,1)!=0||!ProducerLive())
            {Fail();return false;}
            if(!Object.ReferenceEquals(Interlocked.CompareExchange(ref syntheticBatchState,null,batch),batch)){Fail();return false;}
            Array.Clear(batch.Pending,0,batch.Pending.Length);
            if(!ClearBitIfLive(P)){Fail();return false;}return true;
        }
        internal bool TryCommitSyntheticBatchForTest(SyntheticBatchToken token)
        {
            if(!syntheticOnly)return false;
            var batch=Volatile.Read(ref syntheticBatchState) as SyntheticBatchState;
            if(Thread.CurrentThread.ManagedThreadId!=owner||token==null||!Object.ReferenceEquals(token.Owner,this)||
                batch==null||!Object.ReferenceEquals(batch.Token,token)||Interlocked.Exchange(ref token.Consumed,1)!=0||!ProducerLive())
            {Fail();return false;}
            int placed=0;bool visible=false;var target=batch.Target;
            var pending=batch.Pending;var inserted=new object[token.Count];
            try
            {
                if(target==null||pending==null||!Object.ReferenceEquals(target,Volatile.Read(ref ring))||Interlocked.Read(ref nextOrdinal)!=token.Start)
                {Fail();return false;}
                for(int i=0;i<token.Count;i++)
                {
                    object value=Volatile.Read(ref pending[i]);
                    if(!ProducerLive()||value==null||
                        Interlocked.CompareExchange(ref target[(int)((token.Start+i)%capacity)],value,null)!=null)
                    {Fail();return false;}
                    inserted[i]=value;placed++;
                }
                var before=BeforeSyntheticBatchTailForTest;if(before!=null)before();
                if(!ProducerLive()||!Object.ReferenceEquals(target,Volatile.Read(ref ring))){Fail();return false;}
                Interlocked.Exchange(ref nextOrdinal,token.Start+token.Count);
                var after=AfterTailPublishedForTest;if(after!=null)after();
                if(!ClearBitIfLive(P)){Fail();return false;}
                visible=true;Interlocked.CompareExchange(ref syntheticBatchState,null,batch);
                if(ioFence.IsRevoked||(Volatile.Read(ref state)&(R|F))!=0){Fail();return false;}
                return true;
            }
            catch(Exception error) when(!(error is OutOfMemoryException||error is StackOverflowException||error is AccessViolationException))
            {Fail();return false;}
            finally
            {
                if(!visible)for(int i=0;i<placed;i++)Interlocked.CompareExchange(ref target[(int)((token.Start+i)%capacity)],null,inserted[i]);
                if(pending!=null)Array.Clear(pending,0,pending.Length);Array.Clear(inserted,0,inserted.Length);
                Interlocked.CompareExchange(ref syntheticBatchState,null,batch);
            }
        }
        // Exercises only the in-memory SPSC mechanics with the explicitly
        // synthetic worker. Installed-source admissions cannot call this.
        internal bool TryPublishSyntheticForTest(object value)
        {
            if(!syntheticOnly||Thread.CurrentThread.ManagedThreadId!=owner||value==null||!Reserve())return false;
            if(!taskChoice||!eyeChoice){Fail();return false;}
            return Publish(Volatile.Read(ref ring),value);
        }
        // This does not grant consent or start storage: it starts collecting
        // only provider-state metadata after both existing choices committed.
        internal bool TryEnableWorkerStatus(int statusCapacity,out object statusRun)
        {
            statusRun=null;
            if(Thread.CurrentThread.ManagedThreadId!=owner||!Reserve()){Fail();return false;}
            if(!taskChoice||!eyeChoice||enabledAt<0||statusEnabled||Volatile.Read(ref ring)==null)
            {Fail();return false;}
            object run;
            if(!worker.TryEnableStatusEvidence(statusCapacity,task.NamedRawRevocation,out run)||!Object.ReferenceEquals(run,runIdentity))
            {Fail();return false;}
            statusEnabled=true;
            if(!ClearBitIfLive(P)){Fail();return false;}
            statusRun=run;return true;
        }
        // A worker seal is consumed only after all registered statuses have
        // been admitted and ACKed, and proves no future statuses are accepted.
        // A vendor Poll and the interval after the last observation remain
        // explicitly unknown even when this narrow software boundary succeeds.
        internal bool TryBindDrainedStatusSeal(ViveProEyeWorker.StatusSeal seal)
        {
            if(Thread.CurrentThread.ManagedThreadId!=owner||seal==null||!Reserve()){Fail();return false;}
            if(!statusEnabled||statusSealed||!Object.ReferenceEquals(seal.RunIdentity,runIdentity)||
                seal.FinalSequence!=lastStatusSequence||seal.FinalSequence<0||seal.SealLocalSeconds<lastStatusTime||
                Volatile.Read(ref pendingStatusProof)!=null||!seal.TryConsume(runIdentity))
            {Fail();return false;}
            statusSealed=true;
            if(!ClearBitIfLive(P)){Fail();return false;}
            return true;
        }
        // Only a currently reserved status ticket from this exact worker run
        // can enter the queue. An enqueue receipt is required before worker
        // acknowledgement can release the next gaze frame.
        internal bool TryStageStatus(ViveProEyeWorker.StatusTicket ticket,out StatusAdmissionProof proof)
        {
            proof=null;
            if(Thread.CurrentThread.ManagedThreadId!=owner||ticket==null||!Reserve()){Fail();return false;}
            try
            {
                if(!Monitor.TryEnter(worker.RecordingTicketGate)){Fail();return false;}
                try
                {
                    var status=ticket.Event;double now=0;
                    if(!taskChoice||!eyeChoice||!statusEnabled||statusSealed||enabledAt<0||!worker.StatusTicketCurrentUnderLock(ticket,runIdentity)||
                        status==null||lastStatusSequence==Int64.MaxValue||status.Sequence!=lastStatusSequence+1||
                        Double.IsNaN(status.PollReturnLocalSeconds)||Double.IsInfinity(status.PollReturnLocalSeconds)||
                        status.PollReturnLocalSeconds<enabledAt||status.PollReturnLocalSeconds<lastStatusTime||
                        !clock.TryReadRecordingAdmissionTime(out now)||status.PollReturnLocalSeconds>now||
                        Volatile.Read(ref pendingStatusProof)!=null){Fail();return false;}
                    var candidate=new StatusAdmissionProof(this,ticket);
                    lastStatusSequence=status.Sequence;lastStatusTime=status.PollReturnLocalSeconds;
                    if(!Publish(Volatile.Read(ref ring),status))return false;
                    if(ioFence.IsRevoked||(Volatile.Read(ref state)&(R|F|S))!=0||Volatile.Read(ref ring)==null)
                    {Fail();return false;}
#if VIVE_SYNTHETIC_BATCH_FIXTURE
                    if(syntheticOnly){var hook=BeforeStatusProofPublishForTest;if(hook!=null)hook();}
#endif
                    if(Interlocked.CompareExchange(ref pendingStatusProof,candidate,null)!=null)
                    {Fail();return false;}
                    if(ioFence.IsRevoked||(Volatile.Read(ref state)&(R|F|S))!=0||Volatile.Read(ref ring)==null)
                    {Interlocked.CompareExchange(ref pendingStatusProof,null,candidate);return false;}
                    proof=candidate;return true;
                }
                finally{Monitor.Exit(worker.RecordingTicketGate);}
            }
            catch(Exception error) when(!(error is OutOfMemoryException||error is StackOverflowException||error is AccessViolationException))
            {Fail();return false;}
        }
        internal bool ConfirmTask(){return Confirm(true);}
        internal bool ConfirmEye(){return Confirm(false);}
        bool Confirm(bool isTask)
        {
            if(Thread.CurrentThread.ManagedThreadId!=owner||!Reserve()){Fail();return false;}
            try
            {
                double now;ViveDevelopmentParticipant.NamedRecordingTaskStamp stamp;
                if(!clock.TryReadRecordingAdmissionTime(out now)||!task.TryCaptureRawRecordingTaskStamp(clock,out stamp)||stamp.Phase!=Phase.Preparation||
                    !Object.ReferenceEquals(stamp.Context,context)||!Object.ReferenceEquals(stamp.Plan,plan)||!Object.ReferenceEquals(stamp.Intent,intent)){Fail();return false;}
                if(!Monitor.TryEnter(task.NamedRecordingGate)){Fail();return false;}
                try
                {
                    if(!task.NamedRecordingStampCurrentUnderLock(stamp)){Fail();return false;}
                    var target=Volatile.Read(ref ring);
                    if(!ProducerLive()||target==null||(context.IsLegacy&&!isTask&&!taskChoice)||(isTask?taskChoice:eyeChoice)||now<lastChoice||choiceGeneration==Int64.MaxValue){Fail();return false;}
                    if(isTask)taskChoice=true;else eyeChoice=true;choiceGeneration++;lastChoice=now;
                    // The manifest is completely sealed while P is still set.
                    // Consumer reads that observe P defer until publication.
                    if(taskChoice&&eyeChoice)
                    {
                        if(!clock.TryReadRecordingAdmissionTime(out enabledAt)||enabledAt<now){Fail();return false;}
                        manifest.SealChoiceTime(enabledAt,stamp.EventCursor);
                        lastTaskSequence=stamp.EventCursor;
                    }
                    if(!ClearBitIfLive(P))return false;
                    return true;
                }
                finally{Monitor.Exit(task.NamedRecordingGate);}
            }
            catch(Exception error) when(!(error is OutOfMemoryException||error is StackOverflowException||error is AccessViolationException)){Fail();return false;}
        }
        internal bool TryStage(ViveDevelopmentParticipant.NamedRecordingTaskStamp stamp,ViveProEyeWorker.RecordingFrameTicket ticket,double maximumAge)
        {
            if(Thread.CurrentThread.ManagedThreadId!=owner||stamp==null||ticket==null||maximumAge<=0||maximumAge>1||
                Double.IsNaN(maximumAge)||Double.IsInfinity(maximumAge)||!Reserve()){DiscardTicket(ticket);Fail();return false;}
            object[] target=null;long expectedChoice;
            try
            {
                // Scalar/value copying happens before any publication lock;
                // the locked path rechecks this is the exact worker poll.
                if(!ProducerLive()||Volatile.Read(ref ring)==null||!taskChoice||!eyeChoice){Fail();return false;}
                expectedChoice=choiceGeneration;
                var poll=Volatile.Read(ref ticket.Frame);
                if(poll==null||poll.Eye==null||poll.Head==null||poll.Eye.Combined==null||!Object.ReferenceEquals(ticket.Owner,worker)||!Object.ReferenceEquals(ticket.RunIdentity,runIdentity)||
                    (!syntheticOnly&&!ticket.InstalledPath)||!Object.ReferenceEquals(stamp.Owner,task)||!Object.ReferenceEquals(stamp.Clock,clock)||
                    !Object.ReferenceEquals(stamp.Context,context)||!Object.ReferenceEquals(stamp.Plan,plan)||!Object.ReferenceEquals(stamp.Intent,intent)||stamp.Phase==Phase.Ended){Fail();return false;}
                var copy=new Frame(poll,stamp,ticket.LossGeneration,0);
                if(!Monitor.TryEnter(task.NamedRecordingGate)){Fail();return false;}
                try
                {
                    if(!task.NamedRecordingStampCurrentUnderLock(stamp)||!Monitor.TryEnter(worker.RecordingTicketGate)){Fail();return false;}
                    try
                    {
                        target=Volatile.Read(ref ring);double now;
                        if(!ProducerLive()||target==null||!taskChoice||!eyeChoice||choiceGeneration!=expectedChoice||enabledAt<0||lastTaskSequence<stamp.EventCursor||
                            !clock.TryReadRecordingAdmissionTime(out now)||now<enabledAt||now<lastCommit||
                            copy.EyeObserved<=enabledAt||copy.HeadObserved<=enabledAt||copy.EyeObserved>now||copy.HeadObserved>now||
                            now-copy.EyeObserved>maximumAge||now-copy.HeadObserved>maximumAge){Fail();return false;}
                        ViveProEyePoll actual;
                        if(!worker.TryConsumeRecordingTicket(ticket,out actual)||!Object.ReferenceEquals(actual,poll)){Fail();return false;}
                        // Context is explicitly at software ingest. It does
                        // not prove sample/trial or eye/head synchronization.
                        copy.SealCheckedAt(now);lastCommit=now;
                        return Publish(target,copy);
                    }
                    finally{Monitor.Exit(worker.RecordingTicketGate);}
                }
                finally{Monitor.Exit(task.NamedRecordingGate);}
            }
            catch(Exception error) when(!(error is OutOfMemoryException||error is StackOverflowException||error is AccessViolationException)){Fail();return false;}
            finally{DiscardTicket(ticket);}
        }
        // The event is obtained from this participant, never supplied by a
        // caller. A cursor gap or full queue faults the candidate; no gap is
        // represented as durably recorded until a reviewed writer exists.
        internal bool TryStageNextTaskEvent(out bool staged)
        {
            staged=false;if(Thread.CurrentThread.ManagedThreadId!=owner||!Reserve()){Fail();return false;}
            try
            {
                long cursor,choice;
                if(Volatile.Read(ref ring)==null||!taskChoice||!eyeChoice){Fail();return false;}
                cursor=lastTaskSequence;choice=choiceGeneration;
                var events=task.ReadEventsAfter(cursor,1);
                ViveDevelopmentParticipant.NamedRecordingTaskStamp stamp;
                if(!task.TryCaptureRawRecordingTaskStamp(clock,out stamp)){Fail();return false;}
                JournalEvent item=events.Count==0?null:events[0];
                if(item!=null&&(item.Sequence!=cursor+1||item.Sequence>stamp.EventCursor||
                    !context.MatchesEvent(item)||
                    !String.Equals(item.ClockId,clock.ClockId,StringComparison.Ordinal)||!String.Equals(item.SessionId,stamp.Session,StringComparison.Ordinal)))
                {Fail();return false;}
                if(!Monitor.TryEnter(task.NamedRecordingGate)){Fail();return false;}
                try
                {
                    if(!task.NamedRecordingStampCurrentUnderLock(stamp)){Fail();return false;}
                    var target=Volatile.Read(ref ring);
                    if(!ProducerLive()||target==null||choiceGeneration!=choice||lastTaskSequence!=cursor||!taskChoice||!eyeChoice)
                    {Fail();return false;}
                    if(item==null){if(!ClearBitIfLive(P)){Fail();return false;}return true;}
                    lastTaskSequence=item.Sequence;
                    if(!Publish(target,item))return false;
                    staged=true;return true;
                }
                finally{Monitor.Exit(task.NamedRecordingGate);}
            }
            catch(Exception error) when(!(error is OutOfMemoryException||error is StackOverflowException||error is AccessViolationException)){Fail();return false;}
        }
        // A consumer reserves exactly one FIFO head. The caller must perform
        // at most one append attempt outside all locks. A reservation is not
        // a durability claim, and no file writer is attached by this class.
        internal bool TryReserveConsumer(out ConsumerReceipt receipt)
        {
            receipt=null;var candidate=new ConsumerReceipt(this);
            if(!AddBit(C))
            {
                // Bounded CAS contention without a held consumer/terminal is
                // transient; report an empty/busy poll, not a lost record.
                return !ioFence.IsRevoked&&(Volatile.Read(ref state)&(R|F|C))==0;
            }
            int before=Volatile.Read(ref state);
            var target=Volatile.Read(ref ring);
            if(ioFence.IsRevoked||target==null||(before&(R|F))!=0)return false;
            // Busy is not a fault: a producer may be publishing a slot. No
            // receipt is returned and the background consumer can try later.
            if((before&P)!=0)return ClearBitIfLive(C);
            long first=Interlocked.Read(ref firstOrdinal),last=Interlocked.Read(ref nextOrdinal);
            object record=first>=0&&first<last?Volatile.Read(ref target[(int)(first%capacity)]):null;
            int after=Volatile.Read(ref state);
            if(ioFence.IsRevoked||(after&(R|F))!=0||!Object.ReferenceEquals(target,Volatile.Read(ref ring)))return false;
            if((after&P)!=0)return ClearBitIfLive(C);
            if(first<0||last<first||last-first>capacity){Fail();return false;}
            if(first==last)return ClearBitIfLive(C);
            if(record==null){Fail();return false;}
            candidate.Record=record;candidate.Ordinal=first;
#if VIVE_SYNTHETIC_BATCH_FIXTURE
            if(syntheticOnly){var hook=BeforeConsumerReceiptPublishForTest;if(hook!=null)hook();}
#endif
            if(Interlocked.CompareExchange(ref activeReceipt,candidate,null)!=null){Fail();return false;}
            if(ioFence.IsRevoked||(Volatile.Read(ref state)&(R|F))!=0||!Object.ReferenceEquals(target,Volatile.Read(ref ring)))
            {Detach();return false;}
            receipt=candidate;return true;
        }
        internal bool ConsumerReservationCurrent(ConsumerReceipt receipt,object record)
        {
            if(receipt==null||record==null||!Object.ReferenceEquals(receipt.Owner,this))return false;
            int s=Volatile.Read(ref state);var target=Volatile.Read(ref ring);
            long first=Interlocked.Read(ref firstOrdinal),last=Interlocked.Read(ref nextOrdinal);
            if(ioFence.IsRevoked||(s&(R|F))!=0||(s&C)==0||target==null||first<0||last<=first||receipt.Ordinal!=first||
                !Object.ReferenceEquals(Volatile.Read(ref activeReceipt),receipt)||!Object.ReferenceEquals(Volatile.Read(ref target[(int)(first%capacity)]),record)||
                !Object.ReferenceEquals(Volatile.Read(ref receipt.Record),record))return false;
            return !ioFence.IsRevoked&&(Volatile.Read(ref state)&(R|F))==0&&Object.ReferenceEquals(target,Volatile.Read(ref ring));
        }
        internal bool TryGetManifestForReservedConsumer(ConsumerReceipt receipt,object record,out Manifest result)
        {
            result=null;if(!ConsumerReservationCurrent(receipt,record)||manifest.ChoiceTime<0)return false;
            result=manifest;return true;
        }
        // Call only after one append reports success. If revoke/fault won the
        // CAS, the append outcome is unknown even if the OS later completes it.
        internal bool TryAcknowledgeConsumer(ConsumerReceipt receipt,ViveRawSessionJournalSink.WriteProof proof)
        {
            if(proof==null||!proof.TryConsume(this,receipt)){Fail();return false;}
            return AcknowledgeConsumerCore(receipt);
        }
        // Cannot attach to an installed source and cannot produce physical I/O.
        internal bool TryAcknowledgeSyntheticForTest(ConsumerReceipt receipt)
        {if(!syntheticOnly)return false;return AcknowledgeConsumerCore(receipt);}
        bool AcknowledgeConsumerCore(ConsumerReceipt receipt)
        {
            if(receipt==null||!Object.ReferenceEquals(receipt.Owner,this)||Interlocked.Exchange(ref receipt.Completed,1)!=0)return false;
            var target=Volatile.Read(ref ring);var record=Volatile.Read(ref receipt.Record);
            long first=Interlocked.Read(ref firstOrdinal),last=Interlocked.Read(ref nextOrdinal);
            if(target==null||record==null||first<0||last<=first||receipt.Ordinal!=first||
                !Object.ReferenceEquals(Volatile.Read(ref activeReceipt),receipt)||
                !Object.ReferenceEquals(Volatile.Read(ref target[(int)(first%capacity)]),record)||
                ioFence.IsRevoked||(Volatile.Read(ref state)&(R|F))!=0){Fail();return false;}
            // C remains reserved until slot/head/receipt updates are complete.
            // A concurrent withdrawal invalidates the acknowledgement even if
            // an earlier OS write returned: its actual outcome stays uncertain.
            if(!Object.ReferenceEquals(Interlocked.CompareExchange(ref target[(int)(first%capacity)],null,record),record)){Fail();return false;}
            Interlocked.Exchange(ref firstOrdinal,first+1);
            Interlocked.CompareExchange(ref activeReceipt,null,receipt);
            Interlocked.Exchange(ref receipt.Record,null);
            if(!ClearBitIfLive(C)){Fail();return false;}
            return true;
        }
        internal void ConsumerFailed(ConsumerReceipt receipt)
        {
            if(receipt!=null&&Object.ReferenceEquals(receipt.Owner,this))Interlocked.Exchange(ref receipt.Completed,1);
            Fail();
        }
        void DiscardTicket(ViveProEyeWorker.RecordingFrameTicket ticket)
        {
            if(ticket==null||!Object.ReferenceEquals(ticket.Owner,worker))return;
            Interlocked.Exchange(ref ticket.Consumed,1);
            Interlocked.Exchange(ref ticket.Frame,null);
        }
    }
}
