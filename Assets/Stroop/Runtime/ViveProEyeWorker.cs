using System;
using System.Threading;
using AvatarStroop.Core;

namespace AvatarStroop.Runtime
{
    public interface IViveProEyeClient : IViveProEyeProvider, IDisposable
    {
        bool Start();
        string Diagnostic {get;}
    }

    // Optional for clients which contain cleanup failures instead of throwing.
    // The property must remain readable after Dispose. false means the native
    // lifecycle is uncertain and this worker must not permit another start.
    public interface IViveProEyeCleanupStatus
    {
        bool RestartSafe {get;}
    }

    // In-memory counts of committed worker publications for one run, not device
    // frequency, hardware freshness, valid gaze, calibration quality or accuracy.
    // NativePolls counts provider Poll returns, not individual vendor API calls.
    public sealed class ViveWorkerCaptureDiagnostics
    {
        public long NativePolls {get;}
        public long SuccessfulEyeReads {get;}
        public long SequenceChanges {get;}
        public long TimestampChanges {get;}
        internal ViveWorkerCaptureDiagnostics(long nativePolls,long successfulEyeReads,long sequenceChanges,long timestampChanges)
        {NativePolls=nativePolls;SuccessfulEyeReads=successfulEyeReads;SequenceChanges=sequenceChanges;TimestampChanges=timestampChanges;}
    }

    // The client factory, initialization, polling and disposal all run serially on
    // one background thread. The supplied clock must be thread-safe (e.g. Stopwatch)
    // and shared with that client and the source; do not use a Unity-only clock API.
    // Poll/headReader remain on the thread which constructed this worker. This class
    // never refreshes a cached frame's observation time or treats a repeated frame as new.
    public sealed class ViveProEyeWorker : IViveProEyeProvider, IDisposable
    {
        sealed class Run
        {
            internal Thread Thread;
            internal readonly ManualResetEvent StopSignal=new ManualResetEvent(false);
            internal bool Starting=true,Ready,StopRequested,Completed;
            internal long StopGeneration;internal int PollFence;
            internal ViveProEyePoll Latest;
            internal ViveProEyePoll IssuedPoll;
            internal long PollRevision;
            internal bool TicketIssued,TicketOverflow;
            internal RecordingFrameTicket CurrentTicket;
            internal RecordingFrameTicket LastConsumedTicket;
            internal long IssuedLossGeneration;
            internal long IssuedStatusRevision;internal bool IssuedStatusEnabled;
            internal bool HasTicketEye;
            internal int LastTicketFrame,LastTicketTimestamp;
            internal double LastTicketObserved,LastSeenTicketObserved;
            internal long LossGeneration;
            // Status evidence is explicitly enabled per run. Before enable the
            // worker keeps no status history and makes no coverage claim.
            internal bool StatusEnabled,StatusEverEnabled,StatusFault;
            internal ViveRawRevocation StatusAuthority;
            internal double StatusEnabledAt,LastStatusTime=-1;
            internal long StatusNextSequence,StatusRevision;
            internal long StatusLastAcknowledged=-1;
            internal bool StatusSealed;
            internal StatusSeal StatusSeal;
            internal ObservedEyeStatus[] StatusQueue;
            internal int StatusHead,StatusCount;
            internal StatusTicket StatusReserved;
            internal ObservedEyeStatus LastStatus;
            internal string Diagnostic="Starting eye client on its background thread.";
            internal long NativePolls,SuccessfulEyeReads,SequenceChanges,TimestampChanges;
            internal bool HasSuccessfulEyeRead;
            internal int LastEyeSequence,LastEyeTimestamp;
        }

        const int MinimumPollDelayMilliseconds=5;
        static readonly ViveWorkerCaptureDiagnostics NoCaptureDiagnostics=new ViveWorkerCaptureDiagnostics(0,0,0,0);
        readonly object gate=new object();
        readonly Func<ViveHeadFrame> headReader;
        readonly Func<IViveProEyeClient> factory;
        readonly IClock sharedClock;
        readonly bool installedSdkPath;
        readonly Thread headThread;
        Run current;
        // Revocation publishes without waiting for the worker gate. It is
        // permanent for this worker instance, including after a normal restart;
        // new evidence needs a separately authorized new worker instance. A
        // provider call can return later, but no ticket can then be consumed.
        int statusRevoked;
        long stopGeneration;
        ViveWorkerCaptureDiagnostics completedDiagnostics=NoCaptureDiagnostics;
        bool disposed,cleanupFailed;
        string idleDiagnostic="Stopped; explicit Start is required.",headDiagnostic;
        public string ClockId {get;private set;}
        // Equal labels cannot establish a shared observation time base.
        public bool UsesClock(IClock clock){return Object.ReferenceEquals(sharedClock,clock);}
        // This means only that the worker factory is hard-wired to the
        // installed SDK client. It is not hardware identity, calibration or
        // scientific accuracy authentication.
        internal bool UsesInstalledSdkPath {get{return installedSdkPath;}}
        internal object RecordingTicketGate {get{return gate;}}
        internal bool TryGetRecordingRunIdentity(out object run)
        {
            run=null;if(!Monitor.TryEnter(gate))return false;
            try{if(disposed||current==null||!current.Ready||current.StopRequested||!GenerationLive(current))return false;run=current;return true;}
            finally{Monitor.Exit(gate);}
        }
        internal sealed class RecordingFrameTicket
        {
            internal readonly ViveProEyeWorker Owner;internal readonly object RunIdentity;
            internal ViveProEyePoll Frame;internal readonly long Revision;
            internal readonly long LossGeneration,StatusRevision;internal readonly bool StatusEnabled;internal int Consumed;
            // Observation times belong to the issued poll and survive the
            // payload being detached at admission; they do not identify the
            // vendor's hardware sampling time.
            internal readonly double EyeObserved,HeadObserved;
            internal bool InstalledPath {get{return Owner.installedSdkPath;}}
            internal RecordingFrameTicket(ViveProEyeWorker owner,object run,ViveProEyePoll frame,long revision,long loss,long statusRevision,bool statusEnabled)
            {Owner=owner;RunIdentity=run;Frame=frame;Revision=revision;LossGeneration=loss;StatusRevision=statusRevision;StatusEnabled=statusEnabled;
                EyeObserved=frame==null||frame.Eye==null?-1:frame.Eye.ObservedLocalSeconds;
                HeadObserved=frame==null||frame.Head==null?-1:frame.Head.ObservedLocalSeconds;}
            // Existing adversarial fixture constructor; cannot bypass the
            // enabled-status revision checks or the exact current-ticket check.
            internal RecordingFrameTicket(ViveProEyeWorker owner,object run,ViveProEyePoll frame,long revision,long loss)
                :this(owner,run,frame,revision,loss,0,false){}
        }
        internal enum RecordingTicketDisposition { Issued, Duplicate, OutOfOrder, Unavailable }
        [Flags]
        internal enum EyeStatusCause : ulong
        {
            None=0,PollMissing=1,RuntimeUnavailable=2,CalibrationNotReady=4,
            DeviceNotReportedConnected=8,NativeResultMissing=16,NativeResultFailed=32,
            // EyeMissing means no trustworthy eye payload was exposed by this
            // provider return, not a physical claim about an eye or headset.
            EyeMissing=64,NoEyeData=128,CombinedMissing=256,DirectionMaskInvalid=512,
            DirectionFlagInvalid=1024,DirectionMalformed=2048,InvalidEnum=4096,InvalidObservationTime=8192
        }
        // Metadata only; NoEyeData is the adapter's interpretation of the SDK
        // bit, not proof that a person removed or wore the headset. The time is
        // this process's observation immediately after the provider returned,
        // not native capture time or an estimate of dropped-frame count.
        internal sealed class ObservedEyeStatus
        {
            internal readonly long Sequence;internal readonly double PollReturnLocalSeconds;
            internal readonly EyeStatusCause CauseFlags;
            internal readonly ViveEyeRuntimeState? Runtime;internal readonly ViveEyeCalibrationState? Calibration;
            internal readonly bool? EyeDeviceConnected,NoUser,CombinedDirectionReportedValid;
            internal readonly int? NativeResultCode,NativeRuntimeStatus;internal readonly ulong? CombinedValidityMask;
            internal ObservedEyeStatus(long sequence,double time,EyeStatusCause causes,ViveEyeRuntimeState? runtime,
                ViveEyeCalibrationState? calibration,bool? connected,int? result,int? status,bool? noUser,ulong? mask,bool? direction)
            {Sequence=sequence;PollReturnLocalSeconds=time;CauseFlags=causes;Runtime=runtime;Calibration=calibration;
                EyeDeviceConnected=connected;NativeResultCode=result;NativeRuntimeStatus=status;NoUser=noUser;CombinedValidityMask=mask;CombinedDirectionReportedValid=direction;}
            internal bool SameState(ObservedEyeStatus other)
            {return other!=null&&CauseFlags==other.CauseFlags&&Runtime==other.Runtime&&Calibration==other.Calibration&&
                EyeDeviceConnected==other.EyeDeviceConnected&&NativeResultCode==other.NativeResultCode&&NativeRuntimeStatus==other.NativeRuntimeStatus&&
                NoUser==other.NoUser&&CombinedValidityMask==other.CombinedValidityMask&&CombinedDirectionReportedValid==other.CombinedDirectionReportedValid;}
        }
        internal sealed class StatusTicket
        {
            internal readonly ViveProEyeWorker Owner;internal readonly object RunIdentity;internal readonly ObservedEyeStatus Event;
            internal int Consumed;
            internal StatusTicket(ViveProEyeWorker owner,object run,ObservedEyeStatus value){Owner=owner;RunIdentity=run;Event=value;}
        }
        // A single-use receipt for the *registered software queue* only. A
        // vendor Poll may be in flight at seal, and the interval after the
        // last provider observation is unknown; it is not a capture-complete,
        // consent, storage or hardware proof.
        internal sealed class StatusSeal
        {
            readonly ViveProEyeWorker owner;internal readonly object RunIdentity;
            internal readonly long FinalSequence;internal readonly double SealLocalSeconds;internal int Consumed;
            internal StatusSeal(ViveProEyeWorker worker,object run,long sequence,double time)
            {owner=worker;RunIdentity=run;FinalSequence=sequence;SealLocalSeconds=time;}
            internal bool InFlightPollAndTailUnknown {get{return true;}}
            internal bool TryConsume(object expectedRun){return owner.TryConsumeStatusSeal(this,expectedRun);}
        }
        internal enum StatusDisposition { Event, Empty, Busy, Reserved, Faulted, Unavailable }
        internal enum StatusAckResult { Acknowledged, Busy, Invalid }
        internal enum RecordingEpochResult { Current, Busy, Changed }
        // Software status observation only. Caller must independently establish
        // participant authorization before calling this; it does not start a
        // device, authenticate consent or permit storage. At most one enable
        // per run; a revoked worker instance cannot enable again in later runs.
        internal bool TryEnableStatusEvidence(int capacity,out object runIdentity)
        {runIdentity=null;return !installedSdkPath&&TryEnableStatusEvidence(capacity,new ViveRawRevocation(),out runIdentity);}
        // The installed path must share the exact participant revocation bit
        // with admission and the I/O fence; a detached private bit is not an
        // authorization substitute. This is still only a software boundary.
        internal bool TryEnableStatusEvidence(int capacity,ViveRawRevocation authority,out object runIdentity)
        {
            runIdentity=null;
            if(!Object.ReferenceEquals(Thread.CurrentThread,headThread)||capacity<1||capacity>256||authority==null||!authority.MayReserve)return false;
            var timebase=sharedClock as MonotonicClock;if(timebase==null||!Monitor.TryEnter(gate))return false;
            try
            {
                double now;
                if(disposed||Volatile.Read(ref statusRevoked)!=0||!authority.MayReserve||current==null||!current.Ready||current.StopRequested||current.StatusEverEnabled||
                    !timebase.TryReadRecordingAdmissionTime(out now))return false;
                current.StatusEverEnabled=true;current.StatusEnabled=true;current.StatusEnabledAt=now;
                current.StatusAuthority=authority;current.StatusQueue=new ObservedEyeStatus[capacity];
                if(!authority.MayReserve){ClearStatusEvidence(current);return false;}
                runIdentity=current;return true;
            }
            finally{Monitor.Exit(gate);}
        }
        // Only the owner can reserve the current FIFO head. Busy is not empty.
        // Acknowledgment is the caller's assertion only; this API cannot prove
        // downstream admission, file writes or durability. Production use needs
        // a separately reviewed, sealed one-use admission receipt.
        internal StatusDisposition TryReserveStatusEvidence(object runIdentity,out StatusTicket ticket)
        {
            ticket=null;if(!Object.ReferenceEquals(Thread.CurrentThread,headThread)||runIdentity==null)return StatusDisposition.Unavailable;
            if(!Monitor.TryEnter(gate))return StatusDisposition.Busy;
            try
            {
                if(disposed||Volatile.Read(ref statusRevoked)!=0||current==null||!Object.ReferenceEquals(current,runIdentity)||!current.Ready||current.StopRequested||!current.StatusEnabled||StatusAuthorityRevoked(current))
                    return StatusDisposition.Unavailable;
                if(current.StatusFault)return StatusDisposition.Faulted;
                if(current.StatusReserved!=null)return StatusDisposition.Reserved;
                if(current.StatusCount==0)return StatusDisposition.Empty;
                var value=current.StatusQueue[current.StatusHead];if(value==null){current.StatusFault=true;return StatusDisposition.Faulted;}
                ticket=new StatusTicket(this,current,value);current.StatusReserved=ticket;return StatusDisposition.Event;
            }
            finally{Monitor.Exit(gate);}
        }
        internal bool StatusTicketCurrentUnderLock(StatusTicket ticket,object runIdentity)
        {
            return Monitor.IsEntered(gate)&&Object.ReferenceEquals(Thread.CurrentThread,headThread)&&Volatile.Read(ref statusRevoked)==0&&
                ticket!=null&&Object.ReferenceEquals(ticket.Owner,this)&&current!=null&&Object.ReferenceEquals(current,runIdentity)&&
                Object.ReferenceEquals(ticket.RunIdentity,current)&&current.StatusEnabled&&!StatusAuthorityRevoked(current)&&!current.StatusFault&&!current.StopRequested&&
                Object.ReferenceEquals(current.StatusReserved,ticket)&&Volatile.Read(ref ticket.Consumed)==0&&current.StatusCount>0&&
                Object.ReferenceEquals(current.StatusQueue[current.StatusHead],ticket.Event);
        }
        internal bool TryAcknowledgeStatusEvidence(StatusTicket ticket)
        {return !installedSdkPath&&AcknowledgeStatusEvidence(ticket,null)==StatusAckResult.Acknowledged;}
        internal bool TryAcknowledgeStatusEvidence(StatusTicket ticket,ViveNamedSessionFrameAdmission.StatusAdmissionProof proof)
        {return proof!=null&&AcknowledgeStatusEvidence(ticket,proof)==StatusAckResult.Acknowledged;}
        // Busy has no effect on either the ticket or proof. Once the gate is
        // entered, any failure is Invalid and must never be retried as Busy.
        internal StatusAckResult TryAcknowledgeStatusEvidenceResult(StatusTicket ticket,ViveNamedSessionFrameAdmission.StatusAdmissionProof proof)
        {return proof==null?StatusAckResult.Invalid:AcknowledgeStatusEvidence(ticket,proof);}
        StatusAckResult AcknowledgeStatusEvidence(StatusTicket ticket,ViveNamedSessionFrameAdmission.StatusAdmissionProof proof)
        {
            if(!Object.ReferenceEquals(Thread.CurrentThread,headThread)||ticket==null||!Object.ReferenceEquals(ticket.Owner,this))return StatusAckResult.Invalid;
            if(!Monitor.TryEnter(gate))return StatusAckResult.Busy;
            try
            {
                if(disposed||!StatusTicketCurrentUnderLock(ticket,ticket.RunIdentity)||
                    (proof!=null&&!proof.TryConsume(ticket))||Volatile.Read(ref statusRevoked)!=0||StatusAuthorityRevoked(current)||
                    Interlocked.Exchange(ref ticket.Consumed,1)!=0)return StatusAckResult.Invalid;
                current.StatusQueue[current.StatusHead]=null;current.StatusHead=(current.StatusHead+1)%current.StatusQueue.Length;
                current.StatusCount--;current.StatusReserved=null;current.StatusLastAcknowledged=ticket.Event.Sequence;return StatusAckResult.Acknowledged;
            }
            finally{Monitor.Exit(gate);}
        }
        // Called only after the caller has already revoked its downstream
        // recording admission. No queued evidence can be replayed in this run.
        // This is an atomic software fence, callable from any thread; it does
        // not wait, stop the native client or physically erase pending values.
        internal void RevokeStatusEvidence(){Interlocked.Exchange(ref statusRevoked,1);}
        internal bool TryStopStatusEvidence(object runIdentity)
        {
            if(!Object.ReferenceEquals(Thread.CurrentThread,headThread)||runIdentity==null)return false;
            RevokeStatusEvidence();
            if(!Monitor.TryEnter(gate))return false;
            try
            {
                if(current==null||!Object.ReferenceEquals(current,runIdentity)||!current.StatusEverEnabled)return false;
                ClearStatusEvidence(current);return true;
            }
            finally{Monitor.Exit(gate);}
        }
        // Separate graceful boundary, never used for withdrawal. Future Poll
        // returns are ignored; the already registered queue must still drain.
        internal bool TrySealStatusEvidence(object runIdentity,out StatusSeal seal)
        {
            seal=null;if(!Object.ReferenceEquals(Thread.CurrentThread,headThread)||runIdentity==null||!Monitor.TryEnter(gate))return false;
            try
            {
                double now;var timebase=sharedClock as MonotonicClock;
                if(disposed||Volatile.Read(ref statusRevoked)!=0||current==null||!Object.ReferenceEquals(current,runIdentity)||
                    !current.Ready||current.StopRequested||!current.StatusEnabled||StatusAuthorityRevoked(current)||current.StatusFault||current.StatusSealed||
                    current.StatusNextSequence==0||timebase==null||!timebase.TryReadRecordingAdmissionTime(out now)||now<current.LastStatusTime)return false;
                current.StatusSealed=true;InvalidateTicket(current);
                seal=new StatusSeal(this,current,current.StatusNextSequence-1,now);current.StatusSeal=seal;return true;
            }
            finally{Monitor.Exit(gate);}
        }
        bool TryConsumeStatusSeal(StatusSeal seal,object expectedRun)
        {
            if(!Object.ReferenceEquals(Thread.CurrentThread,headThread)||seal==null||expectedRun==null||!Monitor.TryEnter(gate))return false;
            try
            {
                if(disposed||Volatile.Read(ref statusRevoked)!=0||current==null||!Object.ReferenceEquals(current,expectedRun)||
                    !Object.ReferenceEquals(seal.RunIdentity,current)||!Object.ReferenceEquals(current.StatusSeal,seal)||!current.StatusSealed||
                    !current.StatusEnabled||StatusAuthorityRevoked(current)||current.StatusFault||current.StopRequested||current.StatusCount!=0||current.StatusReserved!=null||
                    current.StatusLastAcknowledged!=seal.FinalSequence||Interlocked.Exchange(ref seal.Consumed,1)!=0)return false;
                current.StatusSeal=null;return true;
            }
            finally{Monitor.Exit(gate);}
        }
        static void ClearStatusEvidence(Run run)
        {
            run.StatusEnabled=false;run.StatusQueue=null;run.StatusCount=0;run.LastStatus=null;run.StatusSeal=null;
            if(run.StatusReserved!=null)Interlocked.Exchange(ref run.StatusReserved.Consumed,1);
            run.StatusReserved=null;InvalidateTicket(run);
        }
        static bool StatusAuthorityRevoked(Run run)
        {return run==null||run.StatusAuthority==null||!run.StatusAuthority.MayReserve;}
        static void FaultStatusEvidence(Run run)
        {
            run.StatusFault=true;run.StatusQueue=null;run.StatusCount=0;run.LastStatus=null;
            if(run.StatusReserved!=null)Interlocked.Exchange(ref run.StatusReserved.Consumed,1);
            run.StatusReserved=null;InvalidateTicket(run);
        }
        static ObservedEyeStatus ObserveEyeStatus(long sequence,double time,ViveProEyePoll poll)
        {
            if(poll==null)return new ObservedEyeStatus(sequence,time,EyeStatusCause.PollMissing,null,null,null,null,null,null,null,null);
            EyeStatusCause causes=EyeStatusCause.None;
            ViveEyeRuntimeState? runtime=Enum.IsDefined(typeof(ViveEyeRuntimeState),poll.Runtime)?poll.Runtime:(ViveEyeRuntimeState?)null;
            ViveEyeCalibrationState? calibration=Enum.IsDefined(typeof(ViveEyeCalibrationState),poll.Calibration)?poll.Calibration:(ViveEyeCalibrationState?)null;
            if(!runtime.HasValue||!calibration.HasValue)causes|=EyeStatusCause.InvalidEnum;
            if(runtime!=ViveEyeRuntimeState.Working)causes|=EyeStatusCause.RuntimeUnavailable;
            if(calibration!=ViveEyeCalibrationState.ReportedReady)causes|=EyeStatusCause.CalibrationNotReady;
            if(!poll.EyeDeviceConnected)causes|=EyeStatusCause.DeviceNotReportedConnected;
            if(!poll.NativeResultCode.HasValue)causes|=EyeStatusCause.NativeResultMissing;
            else if(poll.NativeResultCode.Value!=0)causes|=EyeStatusCause.NativeResultFailed;
            bool canTrustEye=poll.Eye!=null&&poll.NativeResultCode.HasValue&&poll.NativeResultCode.Value==0;
            bool? noUser=null,direction=null;ulong? mask=null;
            if(!canTrustEye)causes|=EyeStatusCause.EyeMissing;
            else
            {
                var eye=poll.Eye;noUser=eye.NoUser;if(eye.NoUser)causes|=EyeStatusCause.NoEyeData;
                if(Double.IsNaN(eye.ObservedLocalSeconds)||Double.IsInfinity(eye.ObservedLocalSeconds)||eye.ObservedLocalSeconds<0)
                    causes|=EyeStatusCause.InvalidObservationTime;
                if(eye.Combined==null)causes|=EyeStatusCause.CombinedMissing;
                else
                {
                    mask=eye.Combined.ValidityMask;direction=eye.Combined.DirectionReportedValid;
                    if((eye.Combined.ValidityMask&2UL)==0)causes|=EyeStatusCause.DirectionMaskInvalid;
                    if(!eye.Combined.DirectionReportedValid)causes|=EyeStatusCause.DirectionFlagInvalid;
                    Vec3 ignored;if(!ViveProEyeSdkDirection.TryGetUnitDirection(eye.Combined.Direction,out ignored))causes|=EyeStatusCause.DirectionMalformed;
                }
            }
            return new ObservedEyeStatus(sequence,time,causes,runtime,calibration,poll.EyeDeviceConnected,
                poll.NativeResultCode,poll.NativeRuntimeStatus,noUser,mask,direction);
        }
        static void RecordStatusEvidence(Run run,ViveProEyePoll poll,bool timeValid,double time)
        {
            if(!run.StatusEnabled||StatusAuthorityRevoked(run)||run.StatusFault||run.StatusSealed)return;
            if(!timeValid||time<run.StatusEnabledAt||time<run.LastStatusTime)
            {
                // A provider return observed before enable is simply outside
                // coverage; any invalid/backward post-enable time is terminal.
                if(timeValid&&time<run.StatusEnabledAt)return;
                FaultStatusEvidence(run);return;
            }
            var candidate=ObserveEyeStatus(run.StatusNextSequence,time,poll);run.LastStatusTime=time;
            if(candidate.SameState(run.LastStatus))return;
            if(run.StatusQueue==null||run.StatusCount>=run.StatusQueue.Length||run.StatusNextSequence==Int64.MaxValue||run.StatusRevision==Int64.MaxValue)
            {FaultStatusEvidence(run);return;}
            run.StatusQueue[(run.StatusHead+run.StatusCount)%run.StatusQueue.Length]=candidate;
            run.StatusCount++;run.StatusNextSequence++;run.StatusRevision++;run.LastStatus=candidate;
        }
        public bool Starting {get{lock(gate){Reap();return current!=null&&current.Starting&&!current.StopRequested;}}}
        // Started means native client initialization succeeded, not valid eye tracking.
        public bool Started {get{lock(gate){Reap();return current!=null&&current.Ready&&!current.StopRequested&&GenerationLive(current);}}}
        public bool Stopping {get{lock(gate){Reap();return current!=null&&(current.StopRequested||(!current.Starting&&!current.Ready));}}}
        public bool CanStart {get{lock(gate){Reap();return !disposed&&!cleanupFailed&&current==null;}}}
        // Completion is independent of permission to restart after cleanup failure/disposal.
        public bool HasExited {get{lock(gate){Reap();return current==null;}}}
        public string Diagnostic
        {get{lock(gate){Reap();string state=current==null?idleDiagnostic:current.Diagnostic;return headDiagnostic==null?state:headDiagnostic+" "+state;}}}
        // All four values belong to one atomic snapshot. A new run starts at zero;
        // no completed run or gaze payload is retained by this diagnostic surface.
        public ViveWorkerCaptureDiagnostics CaptureDiagnostics
        {
            get
            {
                lock(gate)
                {
                    Reap();
                    return current==null?NoCaptureDiagnostics:new ViveWorkerCaptureDiagnostics(current.NativePolls,current.SuccessfulEyeReads,
                        current.SequenceChanges,current.TimestampChanges);
                }
            }
        }

        // Metadata-only snapshot of the last committed provider result. This
        // never polls hardware or exposes gaze; the local observation time is not
        // a verified device capture time. Callers must validate the actual read.
        public bool TryPeekLatestEyeMetadata(out int frameSequence,out int nativeTimestamp,out double observedLocalSeconds)
        {
            frameSequence=0;nativeTimestamp=0;observedLocalSeconds=0;
            lock(gate)
            {
                Reap();
                if(current==null||!current.Ready||current.StopRequested||!GenerationLive(current)||current.Latest==null||
                    current.Latest.Eye==null||!current.Latest.NativeResultCode.HasValue||current.Latest.NativeResultCode.Value!=0)
                    return false;
                var eye=current.Latest.Eye;
                if(Double.IsNaN(eye.ObservedLocalSeconds)||Double.IsInfinity(eye.ObservedLocalSeconds)||eye.ObservedLocalSeconds<0)return false;
                frameSequence=eye.FrameSequence;nativeTimestamp=eye.Timestamp;observedLocalSeconds=eye.ObservedLocalSeconds;
                return true;
            }
        }

        public ViveProEyeWorker(IClock clock,Func<ViveHeadFrame> mainThreadHead,Func<IViveProEyeClient> factory)
            :this(clock,mainThreadHead,factory,false){}
        ViveProEyeWorker(IClock clock,Func<ViveHeadFrame> mainThreadHead,Func<IViveProEyeClient> factory,bool installedPath)
        {
            if(clock==null)throw new ArgumentNullException("clock");
            if(String.IsNullOrWhiteSpace(clock.ClockId))throw new ArgumentException("Explicit shared clock identity required","clock");
            if(mainThreadHead==null)throw new ArgumentNullException("mainThreadHead");
            if(factory==null)throw new ArgumentNullException("factory");
            ClockId=clock.ClockId;sharedClock=clock;headReader=mainThreadHead;this.factory=factory;
            headThread=Thread.CurrentThread;installedSdkPath=installedPath;
        }
        internal static ViveProEyeWorker CreateInstalled(IClock clock,Func<ViveHeadFrame> mainThreadHead)
        {return new ViveProEyeWorker(clock,mainThreadHead,()=>new SrAnipalViveProEyeProvider(clock,()=>null),true);}

        // true acknowledges the asynchronous request, not completion or tracking readiness.
        public bool Start()
        {
            lock(gate)
            {
                Reap();if(disposed||cleanupFailed||current!=null)return false;
                long generation=Interlocked.Read(ref stopGeneration);if(generation<0)return false;
                var run=new Run{StopGeneration=generation};completedDiagnostics=NoCaptureDiagnostics;
                run.Thread=new Thread(()=>RunClient(run)){IsBackground=true,Name="VIVE Pro Eye native polling"};
                current=run;headDiagnostic=null;
                try{run.Thread.Start();return true;}
                catch(Exception error)
                {
                    current=null;run.StopSignal.Dispose();
                    idleDiagnostic="Cannot start eye worker: "+error.GetType().Name;return false;
                }
            }
        }

        public ViveProEyePoll Poll()
        {
            // A wrongly placed Poll cannot invoke Unity APIs from a background thread.
            if(!Object.ReferenceEquals(Thread.CurrentThread,headThread))
                throw new InvalidOperationException("VIVE head observations must be polled on the worker's creating thread");
            Run observedRun;lock(gate){Reap();observedRun=current;}
            ViveHeadFrame head=null;string failure=null;
            try{head=headReader();}
            catch(Exception error){failure="Head observation failed: "+error.GetType().Name+".";}
            lock(gate)
            {
                Reap();headDiagnostic=failure;
                var latest=current==null||!Object.ReferenceEquals(current,observedRun)||!current.Ready||current.StopRequested||!GenerationLive(current)?null:current.Latest;
                if(latest==null)return new ViveProEyePoll(head,null,ViveEyeRuntimeState.Unavailable,ViveEyeCalibrationState.Unknown,false);
                var result=new ViveProEyePoll(head,latest.Eye,latest.Runtime,latest.Calibration,latest.EyeDeviceConnected,latest.NativeResultCode,latest.NativeRuntimeStatus);
                InvalidateTicket(current);
                if(current.PollRevision==Int64.MaxValue){current.TicketOverflow=true;current.IssuedPoll=null;current.CurrentTicket=null;}
                else{current.PollRevision++;current.IssuedPoll=result;current.IssuedLossGeneration=current.LossGeneration;
                    current.IssuedStatusRevision=current.StatusRevision;current.IssuedStatusEnabled=current.StatusEnabled;
                    current.TicketIssued=false;current.CurrentTicket=null;}
                return result;
            }
        }

        // Recording-only owner-thread snapshot. Neither attempt to acquire the
        // worker gate waits; a busy/replaced/stopping run returns no frame and
        // the caller must block presentation. The Unity head callback is outside
        // the gate, but its own platform calls are not hard-real-time bounded.
        internal bool TryPollForRecording(out ViveProEyePoll frame)
        {
            frame=null;if(!Object.ReferenceEquals(Thread.CurrentThread,headThread)||Volatile.Read(ref statusRevoked)!=0||!Monitor.TryEnter(gate))return false;
            Run observed;
            try
            {
                observed=current;
                if(disposed||observed==null||!observed.Ready||observed.StopRequested||!GenerationLive(observed)||observed.Latest==null||
                    !observed.StatusEnabled||StatusAuthorityRevoked(observed)||observed.StatusFault||observed.StatusSealed)return false;
            }
            finally{Monitor.Exit(gate);}
            ViveHeadFrame head;
            try{head=headReader();}catch(Exception){return false;}
            if(head==null||Volatile.Read(ref statusRevoked)!=0||!Monitor.TryEnter(gate))return false;
            try
            {
                if(disposed||!Object.ReferenceEquals(current,observed)||!observed.Ready||observed.StopRequested||!GenerationLive(observed)||observed.Latest==null||
                    !observed.StatusEnabled||StatusAuthorityRevoked(observed)||observed.StatusFault||observed.StatusSealed||Volatile.Read(ref statusRevoked)!=0)return false;
                var latest=observed.Latest;InvalidateTicket(observed);
                if(observed.PollRevision==Int64.MaxValue){observed.TicketOverflow=true;return false;}
                var result=new ViveProEyePoll(head,latest.Eye,latest.Runtime,latest.Calibration,latest.EyeDeviceConnected,latest.NativeResultCode,latest.NativeRuntimeStatus);
                observed.PollRevision++;observed.IssuedPoll=result;observed.IssuedLossGeneration=observed.LossGeneration;
                observed.IssuedStatusRevision=observed.StatusRevision;observed.IssuedStatusEnabled=observed.StatusEnabled;
                observed.TicketIssued=false;observed.CurrentTicket=null;frame=result;return true;
            }
            finally{Monitor.Exit(gate);}
        }

        // A ticket proves only that this exact owner-thread Poll object was
        // issued by this worker run. It grants no recording or consent right.
        internal RecordingTicketDisposition TryIssueRecordingTicket(ViveProEyePoll frame,out RecordingFrameTicket ticket)
        {
            ticket=null;if(!Object.ReferenceEquals(Thread.CurrentThread,headThread)||frame==null)return RecordingTicketDisposition.Unavailable;
            if(!Monitor.TryEnter(gate))return RecordingTicketDisposition.Unavailable;
            try
            {
                if(disposed||current==null||!current.Ready||current.StopRequested||!GenerationLive(current)||current.TicketOverflow||
                    current.TicketIssued||current.IssuedLossGeneration!=current.LossGeneration||
                    current.IssuedStatusEnabled!=current.StatusEnabled||current.IssuedStatusRevision!=current.StatusRevision||
                    (current.StatusEverEnabled&&(Volatile.Read(ref statusRevoked)!=0||!current.StatusEnabled||StatusAuthorityRevoked(current)||current.StatusSealed||current.StatusFault||current.StatusNextSequence==0||current.StatusCount!=0||current.StatusReserved!=null))||
                    !Object.ReferenceEquals(current.IssuedPoll,frame)||!EyeUsableForTicket(frame)||!HeadUsableForTicket(frame))return RecordingTicketDisposition.Unavailable;
                var eye=frame.Eye;
                if(current.HasTicketEye)
                {
                    if(eye.FrameSequence==current.LastTicketFrame&&eye.Timestamp==current.LastTicketTimestamp)
                    {
                        if(eye.ObservedLocalSeconds<current.LastSeenTicketObserved)return RecordingTicketDisposition.OutOfOrder;
                        // A later API read of the same device frame is not a
                        // fresh sample and must not extend its first-observed
                        // timestamp. Track it separately only to detect clock rollback.
                        current.LastSeenTicketObserved=eye.ObservedLocalSeconds;
                        return RecordingTicketDisposition.Duplicate;
                    }
                    if(eye.FrameSequence<=current.LastTicketFrame||eye.Timestamp<=current.LastTicketTimestamp||eye.ObservedLocalSeconds<=current.LastSeenTicketObserved)
                        return RecordingTicketDisposition.OutOfOrder;
                }
                current.TicketIssued=true;current.HasTicketEye=true;current.LastTicketFrame=eye.FrameSequence;
                current.LastTicketTimestamp=eye.Timestamp;current.LastTicketObserved=eye.ObservedLocalSeconds;current.LastSeenTicketObserved=eye.ObservedLocalSeconds;
                ticket=new RecordingFrameTicket(this,current,frame,current.PollRevision,current.LossGeneration,current.StatusRevision,current.StatusEnabled);current.CurrentTicket=ticket;
                return RecordingTicketDisposition.Issued;
            }
            finally{Monitor.Exit(gate);}
        }
        static bool HeadUsableForTicket(ViveProEyePoll poll)
        {return poll!=null&&poll.Head!=null&&poll.Head.DeviceConnected&&poll.Head.RotationReportedValid&&poll.Head.RawTrackingRotation.IsUnit&&
            !Double.IsNaN(poll.Head.ObservedLocalSeconds)&&!Double.IsInfinity(poll.Head.ObservedLocalSeconds)&&poll.Head.ObservedLocalSeconds>=0;}
        static bool EyeUsableForTicket(ViveProEyePoll poll)
        {
            if(poll==null||poll.Eye==null||poll.Runtime!=ViveEyeRuntimeState.Working||poll.Calibration!=ViveEyeCalibrationState.ReportedReady||
                !poll.EyeDeviceConnected||!poll.NativeResultCode.HasValue||poll.NativeResultCode.Value!=0||poll.Eye.NoUser||
                Double.IsNaN(poll.Eye.ObservedLocalSeconds)||Double.IsInfinity(poll.Eye.ObservedLocalSeconds)||poll.Eye.ObservedLocalSeconds<0||
                poll.Eye.Combined==null||(poll.Eye.Combined.ValidityMask&2UL)==0||!poll.Eye.Combined.DirectionReportedValid)return false;
            Vec3 unit;return ViveProEyeSdkDirection.TryGetUnitDirection(poll.Eye.Combined.Direction,out unit);
        }
        internal bool TryConsumeRecordingTicket(RecordingFrameTicket ticket,out ViveProEyePoll frame)
        {
            frame=null;if(ticket==null||!Object.ReferenceEquals(ticket.Owner,this)||Interlocked.Exchange(ref ticket.Consumed,1)!=0)return false;
            var ticketFrame=Interlocked.Exchange(ref ticket.Frame,null);
            if(!Object.ReferenceEquals(Thread.CurrentThread,headThread))return false;
            if(!Monitor.TryEnter(gate))return false;
            try
            {
                if(disposed||current==null||!current.Ready||current.StopRequested||!GenerationLive(current)||current.TicketOverflow||
                    !Object.ReferenceEquals(ticket.Owner,this)||!Object.ReferenceEquals(ticket.RunIdentity,current)||
                    !Object.ReferenceEquals(ticket,current.CurrentTicket))return false;
                current.CurrentTicket=null;
                if(ticket.Revision!=current.PollRevision||ticket.LossGeneration!=current.LossGeneration||
                    ticket.StatusEnabled!=current.StatusEnabled||(current.StatusEverEnabled&&(Volatile.Read(ref statusRevoked)!=0||!current.StatusEnabled||StatusAuthorityRevoked(current)||current.StatusSealed||current.StatusFault||current.StatusNextSequence==0||current.StatusCount!=0||
                        current.StatusReserved!=null||ticket.StatusRevision!=current.StatusRevision))||
                    !Object.ReferenceEquals(ticketFrame,current.IssuedPoll)){current.IssuedPoll=null;return false;}
                current.IssuedPoll=null;current.LastConsumedTicket=ticket;frame=ticketFrame;return true;
            }
            finally{Monitor.Exit(gate);}
        }

        // A successful status check is a software epoch witness only. The
        // caller must already have staged this ticket's frame successfully; it
        // cannot prove device capture time, continuous sampling, or head pose.
        internal RecordingEpochResult TryCheckRecordingEpoch(RecordingFrameTicket ticket)
        {
            if(!Object.ReferenceEquals(Thread.CurrentThread,headThread)||ticket==null||!Object.ReferenceEquals(ticket.Owner,this))return RecordingEpochResult.Changed;
            if(!Monitor.TryEnter(gate))return RecordingEpochResult.Busy;
            try
            {
                if(disposed||current==null||!Object.ReferenceEquals(ticket.RunIdentity,current)||!Object.ReferenceEquals(current.LastConsumedTicket,ticket)||
                    Volatile.Read(ref ticket.Consumed)!=1||!current.Ready||current.StopRequested||!GenerationLive(current)||
                    ticket.LossGeneration!=current.LossGeneration||ticket.StatusEnabled!=current.StatusEnabled||
                    (current.StatusEverEnabled&&(Volatile.Read(ref statusRevoked)!=0||!current.StatusEnabled||StatusAuthorityRevoked(current)||current.StatusSealed||
                        current.StatusFault||current.StatusCount!=0||current.StatusReserved!=null||ticket.StatusRevision!=current.StatusRevision)))
                    return RecordingEpochResult.Changed;
                return RecordingEpochResult.Current;
            }
            finally{Monitor.Exit(gate);}
        }

        // Default is nonblocking. A bounded join is available for controlled shutdown
        // and tests; an in-flight native call is never aborted or unloaded underneath.
        // false means cleanup/thread exit is still pending, so Start remains refused.
        public bool Stop(int maximumWaitMilliseconds=0)
        {
            if(maximumWaitMilliseconds<0||maximumWaitMilliseconds>1000)throw new ArgumentOutOfRangeException("maximumWaitMilliseconds");
            Thread thread;
            lock(gate)
            {
                Reap();if(current==null)return true;
                RequestStop(current);thread=current.Thread;
            }
            if(thread==Thread.CurrentThread)return false;
            bool ended=thread.Join(maximumWaitMilliseconds);
            if(ended)lock(gate){Reap();}
            return ended;
        }

        // Nonblocking request only: false means the state lock was busy and
        // the caller must keep the task blocked and try the request later.
        // True does not mean an in-flight native call or cleanup has exited.
        internal bool TryRequestStop()
        {
            Interlocked.Exchange(ref statusRevoked,1);
            Interlocked.Increment(ref stopGeneration);
            var observed=Volatile.Read(ref current);if(observed!=null)Interlocked.Exchange(ref observed.PollFence,2);
            if(!Monitor.TryEnter(gate))return false;
            try{if(current!=null)RequestStop(current);return true;}
            finally{Monitor.Exit(gate);}
        }
        // A successful query reports actual worker thread exit only. It is not
        // evidence that any task, file or recording has closed successfully.
        internal bool TryCheckExited(out bool exited)
        {
            exited=false;if(!Monitor.TryEnter(gate))return false;
            try{Reap();exited=current==null;return true;}
            finally{Monitor.Exit(gate);}
        }

        // Called only after a stop request. Never join or wait for the native
        // provider; a busy lock or still-live thread leaves the result pending.
        // A returned cleanup-safe bit concerns this client only, not recording
        // I/O, sample coverage, persistence, or the health of the hardware.
        internal bool TryFinishStopped(out bool cleanupSafe)
        {
            cleanupSafe=false;if(!Monitor.TryEnter(gate))return false;
            try
            {
                Reap();
                if(Interlocked.Read(ref stopGeneration)==0||current!=null)return false;
                disposed=true;cleanupSafe=!cleanupFailed;return true;
            }
            finally{Monitor.Exit(gate);}
        }

        // Atomically prevent further publications and return the last committed
        // aggregate. No native call or thread join occurs while holding the lock.
        public ViveWorkerCaptureDiagnostics StopAndCaptureDiagnostics()
        {
            lock(gate)
            {
                Reap();
                if(current==null)return completedDiagnostics;
                RequestStop(current);
                return Snapshot(current);
            }
        }

        public void Dispose()
        {
            // Do not join: Unity teardown must not wait on an unresponsive native DLL.
            // The background thread retains the client until its own finally completes.
            lock(gate)
            {
                if(disposed)return;disposed=true;Reap();
                if(current!=null)RequestStop(current);
            }
        }

        void RequestStop(Run run)
        {
            Interlocked.Exchange(ref statusRevoked,1);
            Interlocked.Increment(ref stopGeneration);Interlocked.Exchange(ref run.PollFence,2);
            run.StopRequested=true;run.Ready=false;run.Starting=false;run.Latest=null;ClearStatusEvidence(run);
            if(!run.Completed)
            {
                run.Diagnostic="Stopping; waiting for any native call and client cleanup to finish.";
                run.StopSignal.Set();
            }
        }
        bool GenerationLive(Run run)
        {return run!=null&&Interlocked.Read(ref stopGeneration)==run.StopGeneration&&Volatile.Read(ref run.PollFence)!=2;}
        static void InvalidateTicket(Run run)
        {
            if(run.CurrentTicket!=null)
            {Interlocked.Exchange(ref run.CurrentTicket.Consumed,1);Interlocked.Exchange(ref run.CurrentTicket.Frame,null);run.CurrentTicket=null;}
            run.IssuedPoll=null;
            run.LastConsumedTicket=null;
        }

        void RunClient(Run run)
        {
            IViveProEyeClient client=null;string failure=null,finalDiagnostic=null;
            try
            {
                if(run.StopSignal.WaitOne(0))return;
                client=factory();if(client==null)throw new InvalidOperationException("Client factory returned null");
                if(run.StopSignal.WaitOne(0))return;
                bool initialized=client.Start();string diagnostic=ReadDiagnostic(client);
                lock(gate)
                {
                    run.Starting=false;
                    if(!run.StopRequested){run.Ready=initialized;run.Diagnostic=diagnostic;}
                }
                if(!initialized){failure="Eye client initialization failed. "+diagnostic;return;}
                while(!run.StopSignal.WaitOne(0))
                {
                    // The provider call itself must begin after software
                    // status admission; a pre-admission call returning late
                    // is not evidence within the authorized observation span.
                    bool statusPollEligible;
                    lock(gate){statusPollEligible=run.StatusEnabled&&!StatusAuthorityRevoked(run)&&!run.StatusFault&&Volatile.Read(ref statusRevoked)==0&&!run.StopRequested;}
                    // The poll reservation and stop request race atomically.
                    // A call reserved before stop may complete, but its result
                    // is never published once stop wins the fence.
                    if(Interlocked.Read(ref stopGeneration)!=run.StopGeneration||Interlocked.CompareExchange(ref run.PollFence,1,0)!=0)break;
                    if(Interlocked.Read(ref stopGeneration)!=run.StopGeneration){Interlocked.Exchange(ref run.PollFence,2);break;}
                    ViveProEyePoll poll=client.Poll();
                    if(Interlocked.CompareExchange(ref run.PollFence,0,1)!=1||Interlocked.Read(ref stopGeneration)!=run.StopGeneration)break;
                    double statusObserved=0;var statusClock=sharedClock as MonotonicClock;
                    bool statusTimeValid=statusClock!=null&&statusClock.TryReadRecordingAdmissionTime(out statusObserved);
                    diagnostic=ReadDiagnostic(client);
                    lock(gate)
                    {
                        // Stop clears publication immediately. A late native return
                        // cannot restore a frame after stop/dispose was requested.
                        if(run.StopRequested||!GenerationLive(run))break;
                        if(Volatile.Read(ref statusRevoked)!=0||(run.StatusEverEnabled&&StatusAuthorityRevoked(run)))ClearStatusEvidence(run);
                        else {if(statusPollEligible)RecordStatusEvidence(run,poll,statusTimeValid,statusObserved);
                            if(Volatile.Read(ref statusRevoked)!=0||(run.StatusEverEnabled&&StatusAuthorityRevoked(run)))ClearStatusEvidence(run);}
                        if(!EyeUsableForTicket(poll))
                        {InvalidateTicket(run);if(run.LossGeneration==Int64.MaxValue)run.TicketOverflow=true;else run.LossGeneration++;}
                        run.Latest=poll;run.Diagnostic=diagnostic;
                        run.NativePolls++;
                        if(poll!=null&&poll.Eye!=null&&poll.NativeResultCode.HasValue&&poll.NativeResultCode.Value==0)
                        {
                            run.SuccessfulEyeReads++;
                            if(run.HasSuccessfulEyeRead)
                            {
                                if(poll.Eye.FrameSequence!=run.LastEyeSequence)run.SequenceChanges++;
                                if(poll.Eye.Timestamp!=run.LastEyeTimestamp)run.TimestampChanges++;
                            }
                            run.HasSuccessfulEyeRead=true;run.LastEyeSequence=poll.Eye.FrameSequence;run.LastEyeTimestamp=poll.Eye.Timestamp;
                        }
                    }
                    if(run.StopSignal.WaitOne(MinimumPollDelayMilliseconds))break;
                }
            }
            catch(Exception error)
            {
                failure="Eye worker failed: "+error.GetType().Name+".";
                lock(gate){run.Latest=null;ClearStatusEvidence(run);run.Ready=false;run.Starting=false;run.Diagnostic=failure;}
            }
            finally
            {
                // No client call, including Diagnostic and Dispose, occurs under gate.
                bool clientDisposed=false;
                try
                {
                    if(client!=null){client.Dispose();clientDisposed=true;}
                    else finalDiagnostic="Stopped; no native client was created.";
                }
                catch(Exception error)
                {
                    finalDiagnostic="Client cleanup failed: "+error.GetType().Name+". Restart is blocked for this worker.";
                    lock(gate){cleanupFailed=true;}
                }
                if(clientDisposed)
                {
                    bool restartUnsafe=false;
                    var cleanup=client as IViveProEyeCleanupStatus;
                    if(cleanup!=null)
                    {
                        try
                        {
                            if(!cleanup.RestartSafe){restartUnsafe=true;lock(gate){cleanupFailed=true;}}
                        }
                        catch(Exception error)
                        {
                            restartUnsafe=true;lock(gate){cleanupFailed=true;}
                            failure="Client cleanup status unavailable: "+error.GetType().Name+". Restart is blocked for this worker.";
                        }
                    }
                    try{finalDiagnostic=ReadDiagnostic(client);}
                    catch(Exception error)
                    {finalDiagnostic=(restartUnsafe?"Client cleanup is unsafe; restart is blocked. ":"Stopped; client cleanup completed. ")+"Diagnostic unavailable: "+error.GetType().Name+".";}
                }
                lock(gate)
                {
                    run.Latest=null;ClearStatusEvidence(run);run.Ready=false;run.Starting=false;run.Completed=true;
                    run.Diagnostic=failure==null?finalDiagnostic:failure+" "+finalDiagnostic;
                    // Stop only signals an incomplete run under this same lock.
                    run.StopSignal.Dispose();
                }
            }
        }

        // Keep the run reserved until the actual thread has exited, including its
        // disposal finally. This closes the restart window between cleanup and return.
        void Reap()
        {
            if(current!=null&&current.Completed&&!current.Thread.IsAlive)
            {completedDiagnostics=Snapshot(current);idleDiagnostic=current.Diagnostic;current=null;}
        }
        static ViveWorkerCaptureDiagnostics Snapshot(Run run)
        {return new ViveWorkerCaptureDiagnostics(run.NativePolls,run.SuccessfulEyeReads,run.SequenceChanges,run.TimestampChanges);}
        static string ReadDiagnostic(IViveProEyeClient client)
        {
            string diagnostic=client.Diagnostic;
            return String.IsNullOrWhiteSpace(diagnostic)?"Eye client supplied no diagnostic.":diagnostic;
        }
    }
}
