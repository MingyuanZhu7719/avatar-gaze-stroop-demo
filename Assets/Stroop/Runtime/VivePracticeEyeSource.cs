using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using AvatarStroop.Core;

namespace AvatarStroop.Runtime
{
    public enum VivePracticeEyeStatus
    { NotRead, ReadyCandidate, AwaitingPractice, WaitingForFreshFrame, AcceptedCandidate, AcceptedRawOnlyCandidate, Duplicate, HeadUnavailable, Missing, Unavailable, RecoveryRequired, Faulted, Closed }

    // A short-lived in-memory candidate; this is neither authorization to write
    // gaze nor evidence of durable recording, calibration or hardware accuracy.
    public sealed class VivePracticeEyeCandidate
    {
        readonly IClock clock;
        readonly VivePracticeContextV3 context;
        public ViveProEyePoll RawPoll {get;private set;}
        public Vec3? MappedDirection {get;private set;}
        public bool DerivedAvailable {get{return MappedDirection.HasValue;}}
        public bool HeadUsable {get;private set;}
        public string MappingVersion {get;private set;}
        public double PollStartedLocalSeconds {get;private set;}
        public double PollCompletedLocalSeconds {get;private set;}
        public int PracticeAttempt {get;private set;}
        public long PracticeStartSequence {get;private set;}
        public double EpochStartedLocalSeconds {get;private set;}
        public int BlockIndex {get{return -1;}}
        public Condition? Condition {get{return null;}}
        public bool MainSourceSelected {get{return false;}}
        public bool HardwareCaptureTimeVerified {get{return false;}}
        public bool MappingValidatedForApparatus {get{return false;}}
        public bool CanWrite {get{return false;}}
        internal bool UsesClock(IClock value){return Object.ReferenceEquals(clock,value);}
        internal double MaximumAgeSeconds {get;private set;}
        // Declared provenance only. A null context denotes the reference source;
        // neither a context nor matching IDs prove consent or device identity.
        internal VivePracticeContextV3 Context {get{return context;}}
        internal VivePracticeEyeCandidate(ViveProEyePoll poll,Vec3? direction,bool headUsable,string version,double started,double ended,int attempt,long sequence,double epoch,IClock sourceClock,double maximumAge)
            :this(poll,direction,headUsable,version,started,ended,attempt,sequence,epoch,sourceClock,maximumAge,null){}
        internal VivePracticeEyeCandidate(ViveProEyePoll poll,Vec3? direction,bool headUsable,string version,double started,double ended,int attempt,long sequence,double epoch,IClock sourceClock,double maximumAge,VivePracticeContextV3 declaredContext)
        {RawPoll=poll;MappedDirection=direction;HeadUsable=headUsable;MappingVersion=version;PollStartedLocalSeconds=started;PollCompletedLocalSeconds=ended;PracticeAttempt=attempt;PracticeStartSequence=sequence;EpochStartedLocalSeconds=epoch;clock=sourceClock;MaximumAgeSeconds=maximumAge;context=declaredContext;}
    }

    // Development-only cooperative consumer. Its broker prevents another
    // cooperative lease, but cannot stop an owner holding the worker directly.
    // The worker/client factory clock and real consent are not authenticated.
    // No UI, worker start, file, network, experimental gate or writer is invoked.
    public sealed class VivePracticeEyeSource : IDisposable
    {
        readonly ViveDevelopmentParticipant task;readonly IClock clock;readonly string clockId,session,version;
        readonly ViveExclusiveObservationProvider.Lease lease;readonly IViveProEyeProvider provider;
        readonly IViveProEyeCoordinateMapper mapper;readonly bool controlledMapper;readonly object token=new object(),stateGate=new object();readonly int owner;
        readonly double maximumAge,enabledAt;double lastNow,epoch,startTime,lastEye=-1,lastHead=-1;
        int attempt,lastSequence,lastNative;long startSequence=-1,lastHeadSequence=-1;bool hasEye,hasHead,bound,busy,closed;
        int terminal;string issue;
        VivePracticeEyeStatus status;
        public VivePracticeEyeStatus Status {get{lock(stateGate)return status;}}
        public string LastIssue {get{lock(stateGate)return issue;}}
        public bool RecoveryDecisionRequired {get{return Interlocked.CompareExchange(ref terminal,0,0)!=0;}}
        public bool CanWrite {get{return false;}}
        public bool CanEnterCountdown {get{return false;}}
        public bool GlobalExclusiveOwnershipProven {get{return false;}}
        public bool ClientClockVerified {get{return false;}}
        public bool ConsentAuthenticated {get{return false;}}
        public bool HardwareCaptureTimeVerified {get{return false;}}
        // This identifies only the local sealed rehearsal implementation. It
        // does not establish scientific validity or HTC certification.
        public bool ControlledRehearsalMapper {get{return controlledMapper;}}
        // Historical diagnostic only; it is not a live task-context check.
        public bool PracticeBoundaryWasBound {get{lock(stateGate)return bound;}}
        // A before/after view cannot rule out unlogged change-and-revert. The
        // participant does not expose an authoritative mutation-head token.
        public bool CompleteTaskMutationContinuityProven {get{return false;}}
        // Independent, non-payload diagnostics from the latest poll. A valid
        // SDK direction bit is not the same as a usable or mapped gaze.
        public bool LastNativeDirectionReportedValid {get;private set;}
        public bool LastRawDirectionNormalizable {get;private set;}
        public bool LastEyeUsable {get;private set;}
        public bool LastHeadUsable {get;private set;}
        public bool LastDerivedDirectionUsable {get;private set;}
        public int? LastNativeResultCode {get;private set;}
        public ViveFrameDisposition LastHeadDisposition {get;private set;}

        public VivePracticeEyeSource(ViveDevelopmentParticipant task,IClock clock,ViveProEyeWorker worker,
            IViveProEyeCoordinateMapper mapper,double maximumAgeSeconds,bool explicitEyeOptIn,bool explicitTaskOptIn)
            :this(task,clock,Acquire(worker,clock),mapper,maximumAgeSeconds,explicitEyeOptIn,explicitTaskOptIn){}

        static ViveExclusiveObservationProvider.Lease Acquire(ViveProEyeWorker worker,IClock clock)
        {
            if(worker==null||clock==null||!worker.UsesClock(clock))throw new ArgumentException("Worker must share the actual task clock object.");
            return new ViveExclusiveObservationProvider(worker,clock).Acquire();
        }
        // The fake-only entry point enables deterministic tests. Caller claims
        // are not proof of hardware origin or of any global exclusion.
        internal static VivePracticeEyeSource CreateSynthetic(ViveDevelopmentParticipant task,IClock clock,
            ViveExclusiveObservationProvider.Lease lease,IViveProEyeCoordinateMapper mapper,double maximumAgeSeconds,bool eyeOptIn,bool taskOptIn)
        {return new VivePracticeEyeSource(task,clock,lease,mapper,maximumAgeSeconds,eyeOptIn,taskOptIn);}
        VivePracticeEyeSource(ViveDevelopmentParticipant participant,IClock sharedClock,ViveExclusiveObservationProvider.Lease claimedLease,
            IViveProEyeCoordinateMapper coordinateMapper,double age,bool eyeOptIn,bool taskOptIn)
        {
            if(claimedLease==null)throw new ArgumentNullException("claimedLease");
            try
            {
                if(!eyeOptIn||!taskOptIn)throw new InvalidOperationException("Both explicit recording choices are required.");
                if(participant==null||sharedClock==null||coordinateMapper==null||!participant.UsesEventClock(sharedClock)||!claimedLease.UsesClock(sharedClock))
                    throw new ArgumentException("Task and cooperative lease must use the same clock object.");
                if(String.IsNullOrWhiteSpace(sharedClock.ClockId)||String.IsNullOrWhiteSpace(coordinateMapper.Version)||
                    Double.IsNaN(age)||Double.IsInfinity(age)||age<=0||age>1)throw new ArgumentException("Explicit clock, mapping and bounded freshness required.");
                if(participant.IsResearchSession||participant.BlockIndex!=-1||participant.LockedControl.HasValue||
                    (participant.Phase!=Phase.Preparation&&participant.Phase!=Phase.Familiarization&&participant.Phase!=Phase.PracticeRetry))
                    throw new InvalidOperationException("Arm only before a simulation practice attempt and before source selection.");
                double now=sharedClock.Now;if(!Finite(now)||now<0)throw new ArgumentException("Valid local clock required.");
                task=participant;clock=sharedClock;clockId=clock.ClockId;session=task.SessionId;mapper=coordinateMapper;version=mapper.Version;
                controlledMapper=coordinateMapper.GetType()==typeof(ViveProEyeRehearsalCoordinates);
                maximumAge=age;enabledAt=lastNow=now;owner=Thread.CurrentThread.ManagedThreadId;lease=claimedLease;
                provider=lease.ClaimForPump(token);status=VivePracticeEyeStatus.NotRead;
            }
            catch {claimedLease.Dispose();throw;}
        }
        static bool Finite(double value){return !Double.IsNaN(value)&&!Double.IsInfinity(value);}
        bool Fail(string reason)
        {lock(stateGate){Interlocked.Exchange(ref terminal,1);if(issue==null)issue=reason;status=VivePracticeEyeStatus.Faulted;return false;}}
        void SetStatus(VivePracticeEyeStatus value)
        {lock(stateGate){if(Interlocked.CompareExchange(ref terminal,0,0)==0&&!closed)status=value;}}
        bool Enter()
        {
            lock(stateGate)
            {
                if(Thread.CurrentThread.ManagedThreadId!=owner)return Fail("owner_thread_violation");
                if(closed||RecoveryDecisionRequired)return false;if(busy)return Fail("reentrant_read");busy=true;return true;
            }
        }
        void Exit(){lock(stateGate)busy=false;}
        bool Now(out double now)
        {
            now=0;try{string observedClockId=clock.ClockId;now=clock.Now;if(observedClockId!=clockId||!Finite(now)||now<lastNow)return Fail("clock_invalid_or_rollback");lastNow=now;return true;}
            catch(Exception){return Fail("clock_failed");}
        }
        bool BeforePractice()
        {return task.BlockIndex==-1&&!task.LockedControl.HasValue&&(task.Phase==Phase.Preparation||task.Phase==Phase.Familiarization||task.Phase==Phase.PracticeRetry);}
        sealed class TaskFence
        {
            internal Phase Phase;internal int Block,Segment,Trials,Ratings;internal bool Input;
            internal ControlSelection? Selection;internal TrialRecord Trial;
            internal ExperimentMutationTrialSnapshot? TrialValue;internal IReadOnlyList<JournalEvent> Events;
        }
        TaskFence CaptureFence()
        {
            var trial=task.CurrentTrial;var events=task.ReadEventsAfter(-1,4096);
            if(events.Count>=4096)throw new InvalidOperationException("Task fence exceeds bounded history.");
            return new TaskFence{Phase=task.Phase,Block=task.BlockIndex,Segment=task.Segment,Trials=task.TrialCount,Ratings=task.RatingCount,
                Input=task.InputReady,Selection=task.LockedControl,Trial=trial,
                TrialValue=trial==null?(ExperimentMutationTrialSnapshot?)null:new ExperimentMutationTrialSnapshot(trial),Events=events};
        }
        static bool SameEvents(IReadOnlyList<JournalEvent> a,IReadOnlyList<JournalEvent> b)
        {
            if(a.Count!=b.Count)return false;
            for(int i=0;i<a.Count;i++)
            {
                var x=a[i];var y=b[i];
                if(x.Sequence!=y.Sequence||BitConverter.DoubleToInt64Bits(x.Time)!=BitConverter.DoubleToInt64Bits(y.Time)||x.ClockId!=y.ClockId||
                    x.SessionId!=y.SessionId||x.BlockIndex!=y.BlockIndex||x.Segment!=y.Segment||x.TrialId!=y.TrialId||x.Kind!=y.Kind||x.Fields.Count!=y.Fields.Count)return false;
                foreach(var pair in x.Fields){string value;if(!y.Fields.TryGetValue(pair.Key,out value)||value!=pair.Value)return false;}
            }
            return true;
        }
        bool SameFence(TaskFence before)
        {
            var after=CaptureFence();
            return before.Phase==after.Phase&&before.Block==after.Block&&before.Segment==after.Segment&&before.Trials==after.Trials&&
                before.Ratings==after.Ratings&&before.Input==after.Input&&before.Selection==after.Selection&&Object.ReferenceEquals(before.Trial,after.Trial)&&
                before.TrialValue.HasValue==after.TrialValue.HasValue&&(!before.TrialValue.HasValue||before.TrialValue.Value.Equals(after.TrialValue.Value))&&
                SameEvents(before.Events,after.Events);
        }
        // Readiness never returns a sample. Its high-water marks prevent this
        // pre-bound observation from becoming the first post-bound candidate.
        public bool PollReadiness()
        {
            if(!Enter())return false;
            try
            {
                if(bound||!BeforePractice())return Fail("readiness_context_changed");var fence=CaptureFence();
                ViveProEyePoll poll;Vec3 mapped;double begin,end;
                bool usable=Read(out poll,out mapped,out begin,out end,true);
                if(RecoveryDecisionRequired)return false;
                double final;if(!Now(out final))return false;
                bool mappingCurrent=usable&&controlledMapper&&LastDerivedDirectionUsable;
                if(RecoveryDecisionRequired||!lease.IsClaimedBy(token)||!BeforePractice()||!SameFence(fence))return Fail("readiness_context_changed");
                if(!usable)return false;
                if(!mappingCurrent){LastDerivedDirectionUsable=false;SetStatus(VivePracticeEyeStatus.Unavailable);return false;}
                if(final-poll.Eye.ObservedLocalSeconds>maximumAge||final-poll.Head.ObservedLocalSeconds>maximumAge){SetStatus(VivePracticeEyeStatus.Unavailable);return false;}
                lock(stateGate){if(closed||RecoveryDecisionRequired)return false;status=VivePracticeEyeStatus.ReadyCandidate;return true;}
            }
            catch(Exception){return Fail("readiness_check_failed");}
            finally{Exit();}
        }
        public bool BindPractice()
        {
            if(!Enter())return false;
            try
            {
                if(bound||task.Phase!=Phase.Practice||task.BlockIndex!=-1||task.LockedControl.HasValue)return Fail("practice_context_unavailable");var fence=CaptureFence();
                var trial=task.CurrentTrial;if(trial==null||!trial.IsPractice||trial.BlockIndex!=-1||trial.Condition.HasValue||
                    trial.PracticeAttempt<1||trial.SessionId!=session)return Fail("practice_trial_context_invalid");
                var events=task.ReadEventsAfter(-1,4096);if(events.Count>=4096)return Fail("practice_event_search_limit");
                JournalEvent matched=null;string expected=trial.PracticeAttempt.ToString(CultureInfo.InvariantCulture);
                foreach(var item in events)
                {
                    if(item.Kind!="practice_started")continue;string value;
                    if(item.Fields==null||!item.Fields.TryGetValue("attempt",out value)||value!=expected)continue;
                    if(matched!=null)return Fail("practice_start_duplicate");matched=item;
                }
                double now;if(!Now(out now))return false;
                if(matched==null||matched.SessionId!=session||matched.ClockId!=clockId||matched.BlockIndex!=-1||matched.Segment<0||
                    !Finite(matched.Time)||matched.Time<=enabledAt||matched.Time>now||matched.Sequence<0)
                    return Fail("practice_start_not_verified");
                if(RecoveryDecisionRequired||!lease.IsClaimedBy(token)||!SameFence(fence))return Fail("practice_context_changed_during_bind");
                lock(stateGate)
                {
                    if(closed||RecoveryDecisionRequired)return false;
                    attempt=trial.PracticeAttempt;startSequence=matched.Sequence;startTime=matched.Time;epoch=now;bound=true;
                    status=VivePracticeEyeStatus.WaitingForFreshFrame;return true;
                }
            }
            catch(Exception){return Fail("practice_binding_failed");}
            finally{Exit();}
        }
        bool CurrentPractice()
        {
            var trial=task.CurrentTrial;
            return bound&&task.BlockIndex==-1&&!task.LockedControl.HasValue&&(task.Phase==Phase.Practice||task.Phase==Phase.PracticeCorrection)&&
                trial!=null&&trial.IsPractice&&trial.BlockIndex==-1&&!trial.Condition.HasValue&&trial.PracticeAttempt==attempt&&trial.SessionId==session;
        }
        public bool TryRead(out VivePracticeEyeCandidate candidate)
        {
            candidate=null;if(!Enter())return false;
            try
            {
                if(!CurrentPractice())return Fail("practice_context_changed_or_paused");var fence=CaptureFence();
                ViveProEyePoll poll;Vec3 mapped;double begin,end;
                bool usable=Read(out poll,out mapped,out begin,out end,false);
                if(RecoveryDecisionRequired)return false;
                double final;if(!Now(out final))return false;
                bool mappingCurrent=usable&&controlledMapper&&LastDerivedDirectionUsable;
                if(RecoveryDecisionRequired||!lease.IsClaimedBy(token)||!CurrentPractice()||!SameFence(fence))return Fail("practice_context_changed_during_read");
                if(!usable)return false;
                if(poll.Eye.ObservedLocalSeconds<=enabledAt||poll.Eye.ObservedLocalSeconds<=startTime||poll.Eye.ObservedLocalSeconds<=epoch)
                {SetStatus(VivePracticeEyeStatus.WaitingForFreshFrame);return false;}
                if(final-poll.Eye.ObservedLocalSeconds>maximumAge)return Fail("observation_stale_before_publish");
                bool headUsable=LastHeadUsable&&poll.Head!=null&&
                    poll.Head.ObservedLocalSeconds>enabledAt&&poll.Head.ObservedLocalSeconds>startTime&&
                    poll.Head.ObservedLocalSeconds>epoch&&final-poll.Head.ObservedLocalSeconds<=maximumAge;
                bool derived=LastDerivedDirectionUsable&&headUsable&&mappingCurrent;
                LastHeadUsable=headUsable;LastDerivedDirectionUsable=derived;
                // Raw eye and head evidence have independent boundaries. Never
                // expose a stale, invalid or pre-boundary head in a raw-only
                // candidate, even though the raw eye itself is acceptable.
                var candidatePoll=headUsable?poll:new ViveProEyePoll(null,poll.Eye,poll.Runtime,poll.Calibration,
                    poll.EyeDeviceConnected,poll.NativeResultCode,poll.NativeRuntimeStatus);
                lock(stateGate)
                {
                    if(closed||RecoveryDecisionRequired)return false;
                    candidate=new VivePracticeEyeCandidate(candidatePoll,derived?(Vec3?)mapped:null,headUsable,version,begin,end,attempt,startSequence,epoch,clock,maximumAge);
                    status=derived?VivePracticeEyeStatus.AcceptedCandidate:VivePracticeEyeStatus.AcceptedRawOnlyCandidate;return true;
                }
            }
            catch(Exception){return Fail("candidate_read_failed");}
            finally{Exit();}
        }
        bool Read(out ViveProEyePoll poll,out Vec3 mapped,out double begin,out double end,bool readiness)
        {
            poll=null;mapped=new Vec3(Double.NaN,Double.NaN,Double.NaN);begin=end=0;
            LastNativeDirectionReportedValid=LastRawDirectionNormalizable=LastEyeUsable=LastHeadUsable=LastDerivedDirectionUsable=false;
            LastNativeResultCode=null;LastHeadDisposition=ViveFrameDisposition.Missing;
            if(!lease.IsClaimedBy(token))return Fail("cooperative_lease_lost");
            if(!Now(out begin))return false;
            try{poll=provider.Poll();}catch(Exception){return Fail("provider_failed");}
            if(RecoveryDecisionRequired||!lease.IsClaimedBy(token)||!Now(out end))return false;
            if(poll==null||poll.Eye==null)
            {SetStatus(VivePracticeEyeStatus.Missing);if(!readiness)return Fail("observation_missing");return false;}
            var eye=poll.Eye;var head=poll.Head;
            LastNativeResultCode=poll.NativeResultCode;
            LastNativeDirectionReportedValid=eye.Combined!=null&&(eye.Combined.ValidityMask&2UL)!=0&&eye.Combined.DirectionReportedValid;
            Vec3 unit;LastRawDirectionNormalizable=LastNativeDirectionReportedValid&&ViveProEyeSdkDirection.TryGetUnitDirection(eye.Combined.Direction,out unit);
            if(!Finite(eye.ObservedLocalSeconds)||eye.ObservedLocalSeconds<0||eye.ObservedLocalSeconds>end)return Fail("observation_time_invalid");
            // The SDK's first signed eye counter/timestamp may be negative;
            // preserve it exactly. Later resets/wraps fail closed. Independently
            // advance head high-water even when the eye frame is duplicated.
            if(head!=null)
            {
                if(!Finite(head.ObservedLocalSeconds)||head.ObservedLocalSeconds<0||head.ObservedLocalSeconds>end)LastHeadDisposition=ViveFrameDisposition.InvalidObservationTime;
                else if(head.ObservationSequence<0)LastHeadDisposition=ViveFrameDisposition.InvalidSequence;
                else if(hasHead&&(head.ObservationSequence<lastHeadSequence||head.ObservedLocalSeconds<lastHead))LastHeadDisposition=ViveFrameDisposition.OutOfOrder;
                else if(hasHead&&head.ObservationSequence==lastHeadSequence)LastHeadDisposition=ViveFrameDisposition.Duplicate;
                else if(end-head.ObservedLocalSeconds>maximumAge)LastHeadDisposition=ViveFrameDisposition.Stale;
                else LastHeadDisposition=ViveFrameDisposition.Accepted;
                if(LastHeadDisposition==ViveFrameDisposition.Accepted||LastHeadDisposition==ViveFrameDisposition.Stale)
                {hasHead=true;lastHeadSequence=head.ObservationSequence;lastHead=head.ObservedLocalSeconds;}
            }
            bool duplicate=hasEye&&eye.FrameSequence==lastSequence&&eye.Timestamp==lastNative&&eye.ObservedLocalSeconds==lastEye;
            if(hasEye&&!duplicate&&(eye.FrameSequence<=lastSequence||eye.Timestamp<=lastNative||eye.ObservedLocalSeconds<=lastEye))return Fail("eye_order_invalid");
            // Record high-water marks even for invalid or pre-bound frames; they
            // must never be replayed as a later valid post-consent observation.
            if(!duplicate){hasEye=true;lastSequence=eye.FrameSequence;lastNative=eye.Timestamp;lastEye=eye.ObservedLocalSeconds;}
            LastEyeUsable=end-eye.ObservedLocalSeconds<=maximumAge&&poll.Runtime==ViveEyeRuntimeState.Working&&
                poll.Calibration==ViveEyeCalibrationState.ReportedReady&&poll.EyeDeviceConnected&&poll.NativeResultCode.HasValue&&
                poll.NativeResultCode.Value==0&&!eye.NoUser&&LastRawDirectionNormalizable;
            LastHeadUsable=head!=null&&LastHeadDisposition==ViveFrameDisposition.Accepted&&head.DeviceConnected&&head.RotationReportedValid&&head.RawTrackingRotation.IsUnit;
            // A readiness observation is only a diagnostic; after binding,
            // head pose must itself postdate consent, the real task event and
            // the epoch before it may be sent to the coordinate mapper.
            if(!readiness&&LastHeadUsable&&
                (head.ObservedLocalSeconds<=enabledAt||head.ObservedLocalSeconds<=startTime||head.ObservedLocalSeconds<=epoch))
                LastHeadUsable=false;
            if(!LastEyeUsable)
            {SetStatus(VivePracticeEyeStatus.Unavailable);if(!readiness)return Fail("observation_unusable");return false;}
            if(duplicate){SetStatus(LastHeadUsable?VivePracticeEyeStatus.Duplicate:VivePracticeEyeStatus.HeadUnavailable);return false;}
            if(!LastHeadUsable)
            {SetStatus(VivePracticeEyeStatus.HeadUnavailable);if(readiness)return false;}
            else if(!controlledMapper)
            {
                // An arbitrary mapper can block or mutate its own version and
                // clock after a freshness check. Keep raw eye independent, but
                // never call it or assert derived readiness on the hot path.
                if(readiness){SetStatus(VivePracticeEyeStatus.Unavailable);return false;}
            }
            else
            {
                try{if(mapper.Version==version&&mapper.TryMapEye(head.RawTrackingRotation,eye.Combined.Direction,out mapped)&&mapped.IsUnit&&mapper.Version==version)LastDerivedDirectionUsable=true;}
                catch(Exception){LastDerivedDirectionUsable=false;}
                if(readiness&&!LastDerivedDirectionUsable){SetStatus(VivePracticeEyeStatus.Unavailable);return false;}
            }
            if(eye.ObservedLocalSeconds<=enabledAt){SetStatus(VivePracticeEyeStatus.WaitingForFreshFrame);return false;}
            return true;
        }
        public void Dispose()
        {
            lock(stateGate)
            {
                if(Thread.CurrentThread.ManagedThreadId!=owner){Fail("owner_thread_violation");return;}
                if(closed)return;if(busy){Fail("reentrant_dispose");return;}closed=true;
            }
            try{lease.CloseClaim(token);}catch(Exception){Fail("lease_release_failed");return;}
            lock(stateGate){if(!RecoveryDecisionRequired)status=VivePracticeEyeStatus.Closed;}
        }
    }
}
