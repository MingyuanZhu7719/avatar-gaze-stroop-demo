using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using AvatarStroop.Core;

namespace AvatarStroop.Runtime
{
    // Closed synthetic input: callers supply only numbers and a marker. No
    // real provider, gaze payload, device, file or network can be attached.
    internal sealed class VivePracticeSyntheticFrameFeed
    {
        sealed class Provider : IViveProEyeProvider
        {
            readonly VivePracticeSyntheticFrameFeed feed;
            internal Provider(VivePracticeSyntheticFrameFeed feed){this.feed=feed;}
            public ViveProEyePoll Poll(){feed.delivered=feed.marker;return feed.frame;}
        }
        readonly IViveProEyeProvider provider;
        ViveProEyePoll frame;byte marker,delivered;
        readonly int owner=Thread.CurrentThread.ManagedThreadId;
        internal VivePracticeSyntheticFrameFeed(){provider=new Provider(this);}
        internal ViveExclusiveObservationProvider.Lease Acquire(IClock clock)
        {return new ViveExclusiveObservationProvider(provider,clock).Acquire();}
        internal byte DeliveredMarker {get{return delivered;}}
        internal void SetFrame(int sequence,int nativeTime,double eyeTime,long headSequence,double headTime,byte syntheticMarker)
        {
            if(Thread.CurrentThread.ManagedThreadId!=owner)throw new InvalidOperationException("Synthetic feed owner required.");
            var ray=new ViveEyeRay(2,true,true,new Vec3(0,0,0),new Vec3(0,0,1));
            var eye=new ViveEyeFrame(sequence,nativeTime,eyeTime,false,ray,ray,ray);
            var head=new ViveHeadFrame(headSequence,headTime,true,true,Rotation.Identity);
            marker=syntheticMarker;frame=new ViveProEyePoll(head,eye,ViveEyeRuntimeState.Working,ViveEyeCalibrationState.ReportedReady,true,0,0);
        }
    }
    // An internal historical candidate, never a real sample or a write permit.
    internal sealed class VivePracticeSyntheticTicket
    {
        internal readonly VivePracticeEyeFencedSource Issuer;
        internal readonly ViveDevelopmentParticipant Participant;
        internal readonly IClock Clock;
        internal readonly VivePracticeConsentAuthority Choices;
        internal readonly VivePracticeContextV3 Context;
        internal readonly ExperimentIncrementalSnapshot Snapshot;
        internal readonly ViveSyntheticStepTrace LastStep;
        internal readonly long Revision,Generation,Version,StartSequence;
        internal readonly int TraceCount,Attempt;
        internal readonly double Epoch,Observed,MaximumAgeSeconds;
        readonly VivePracticeEyeCandidate candidate;
        internal int Consumed;
        internal VivePracticeSyntheticTicket(VivePracticeEyeFencedSource issuer,ViveDevelopmentParticipant participant,IClock clock,
            VivePracticeConsentAuthority choices,VivePracticeContextV3 context,ExperimentIncrementalSnapshot snapshot,ViveSyntheticStepTrace lastStep,long revision,
            long generation,long version,long startSequence,int traceCount,int attempt,double epoch,double observed,double maximumAgeSeconds,VivePracticeEyeCandidate candidate)
        {Issuer=issuer;Participant=participant;Clock=clock;Choices=choices;Context=context;Snapshot=snapshot;LastStep=lastStep;Revision=revision;
            Generation=generation;Version=version;StartSequence=startSequence;TraceCount=traceCount;Attempt=attempt;Epoch=epoch;Observed=observed;MaximumAgeSeconds=maximumAgeSeconds;this.candidate=candidate;}
        internal bool MatchesCandidate(VivePracticeEyeCandidate candidate){return candidate!=null&&Object.ReferenceEquals(this.candidate,candidate);}
    }
    // Development-only low-allocation cooperative consumer. It uses the same
    // participant's single existing mutation pump; it never reads that queue.
    // Its broker prevents another
    // cooperative lease, but cannot stop an owner holding the worker directly.
    // The worker/client factory clock and real consent are not authenticated.
    // No UI, worker start, file, network, experimental gate or writer is invoked.
    public sealed class VivePracticeEyeFencedSource : IDisposable
    {
        readonly ViveDevelopmentParticipant task;readonly IClock clock;readonly string clockId,session,version;
        readonly ViveExclusiveObservationProvider.Lease lease;readonly IViveProEyeProvider provider;
        readonly IViveProEyeCoordinateMapper mapper;readonly bool controlledMapper;readonly object token=new object(),stateGate=new object();readonly int owner;
        readonly double maximumAge,enabledAt;double lastNow,epoch,startTime,lastEye=-1,lastHead=-1,lastAcceptedEye=-1;
        int attempt,lastSequence,lastNative;long startSequence=-1,lastHeadSequence=-1;bool hasEye,hasHead,bound,busy,closed;
        VivePracticeContextV3 practiceContext;
        int traceCount,traceIndex=-1;long traceRevision;ExperimentIncrementalSnapshot traceState;
        int terminal;string issue;
        VivePracticeEyeStatus status;
        VivePracticeSyntheticFrameFeed syntheticFeed;
        VivePracticeConsentAuthority syntheticChoices;
        VivePracticeSyntheticTicket syntheticOutstanding;
        VivePracticeSyntheticTicket syntheticConsumed;
        VivePracticeEyeCandidate syntheticCandidate;
        byte syntheticMarker;
        long syntheticGeneration,syntheticVersion;
        int syntheticBusy,syntheticInvalid;
        // Fake host may hold this lock last (after cursor/choice/participant)
        // through its sealed in-memory publication; it must not perform I/O,
        // call clocks or invoke callbacks while holding it.
        internal object SyntheticTicketGate {get{return stateGate;}}
        void InvalidateSynthetic(){lock(stateGate){syntheticInvalid=1;syntheticConsumed=null;}}
        bool EnterSynthetic()
        {
            lock(stateGate)
            {
                if(Thread.CurrentThread.ManagedThreadId!=owner||syntheticBusy!=0){syntheticInvalid=1;syntheticConsumed=null;return false;}
                syntheticBusy=1;return true;
            }
        }
        void ExitSynthetic(){lock(stateGate)syntheticBusy=0;}
        // Requires host to hold SyntheticTicketGate until its bounded fake
        // reference publication is complete. This never reads a clock or invokes
        // application callbacks and is not a recording/consent authorization.
        internal bool ConsumedTicketStillCurrent(VivePracticeSyntheticTicket ticket)
        {
            if(!Monitor.IsEntered(stateGate)||Thread.CurrentThread.ManagedThreadId!=owner||ticket==null||
                syntheticFeed==null||syntheticChoices==null||syntheticInvalid!=0||syntheticBusy!=0||busy||closed||RecoveryDecisionRequired||
                !Object.ReferenceEquals(syntheticConsumed,ticket)||!Object.ReferenceEquals(ticket.Issuer,this)||
                !Object.ReferenceEquals(ticket.Participant,task)||!Object.ReferenceEquals(ticket.Clock,clock)||
                !Object.ReferenceEquals(ticket.Choices,syntheticChoices)||!Object.ReferenceEquals(ticket.Context,practiceContext)||
                ticket.Version!=syntheticVersion||ticket.Generation!=syntheticGeneration||ticket.Attempt!=attempt||
                ticket.StartSequence!=startSequence||ticket.Epoch!=epoch||ticket.MaximumAgeSeconds!=maximumAge||Interlocked.CompareExchange(ref ticket.Consumed,0,0)!=1)return false;
            return lease.IsClaimedBy(token);
        }
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
        // The proof is limited to one owner-thread Simulation participant; it
        // is not evidence of actual hardware, consent, or apparatus admission.
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

        public VivePracticeEyeFencedSource(ViveDevelopmentParticipant task,IClock clock,ViveProEyeWorker worker,
            IViveProEyeCoordinateMapper mapper,double maximumAgeSeconds,bool explicitEyeOptIn,bool explicitTaskOptIn)
            :this(task,clock,Acquire(worker,clock),mapper,maximumAgeSeconds,explicitEyeOptIn,explicitTaskOptIn){}

        static ViveExclusiveObservationProvider.Lease Acquire(ViveProEyeWorker worker,IClock clock)
        {
            if(worker==null||clock==null||!worker.UsesClock(clock))throw new ArgumentException("Worker must share the actual task clock object.");
            return new ViveExclusiveObservationProvider(worker,clock).Acquire();
        }
        // The fake-only entry point enables deterministic tests. Caller claims
        // are not proof of hardware origin or of any global exclusion.
        internal static VivePracticeEyeFencedSource CreateSynthetic(ViveDevelopmentParticipant task,IClock clock,
            ViveExclusiveObservationProvider.Lease lease,IViveProEyeCoordinateMapper mapper,double maximumAgeSeconds,bool eyeOptIn,bool taskOptIn)
        {return new VivePracticeEyeFencedSource(task,clock,lease,mapper,maximumAgeSeconds,eyeOptIn,taskOptIn);}
        internal static VivePracticeEyeFencedSource CreateSealedSyntheticTicketSource(ViveDevelopmentParticipant task,IClock clock,
            VivePracticeConsentAuthority choices,IViveProEyeCoordinateMapper mapper,double maximumAgeSeconds,out VivePracticeSyntheticFrameFeed feed)
        {
            feed=null;
            if(choices==null||!choices.IsCurrent(choices.Generation,task,clock))throw new InvalidOperationException("Current simulation choices required.");
            var synthetic=new VivePracticeSyntheticFrameFeed();
            var source=new VivePracticeEyeFencedSource(task,clock,synthetic.Acquire(clock),mapper,maximumAgeSeconds,true,true);
            if(!choices.IsCurrent(choices.Generation,task,clock)){source.Dispose();throw new InvalidOperationException("Choices changed.");}
            source.syntheticFeed=synthetic;source.syntheticChoices=choices;source.syntheticGeneration=choices.Generation;feed=synthetic;return source;
        }
        VivePracticeEyeFencedSource(ViveDevelopmentParticipant participant,IClock sharedClock,ViveExclusiveObservationProvider.Lease claimedLease,
            IViveProEyeCoordinateMapper coordinateMapper,double age,bool eyeOptIn,bool taskOptIn)
        {
            if(claimedLease==null)throw new ArgumentNullException("claimedLease");
            ViveDevelopmentParticipant.PracticeObservationFence fence=null;
            try
            {
                if(!eyeOptIn||!taskOptIn)throw new InvalidOperationException("Both explicit recording choices are required.");
                if(participant==null||sharedClock==null||coordinateMapper==null||!participant.UsesEventClock(sharedClock)||!claimedLease.UsesClock(sharedClock))
                    throw new ArgumentException("Task and cooperative lease must use the same clock object.");
                if(!participant.TryAcquirePracticeObservationFence(sharedClock,out fence)||fence==null)
                    throw new InvalidOperationException("A current, owner-thread observed participant is required.");
                string checkedClockId=sharedClock.ClockId,checkedMapping=coordinateMapper.Version;
                if(String.IsNullOrWhiteSpace(checkedClockId)||String.IsNullOrWhiteSpace(checkedMapping)||
                    Double.IsNaN(age)||Double.IsInfinity(age)||age<=0||age>1)throw new ArgumentException("Explicit clock, mapping and bounded freshness required.");
                if(participant.IsResearchSession||!BeforePractice(fence.Snapshot))
                    throw new InvalidOperationException("Arm only before a simulation practice attempt and before source selection.");
                double now=sharedClock.Now;if(!Finite(now)||now<0)throw new ArgumentException("Valid local clock required.");
                if(sharedClock.ClockId!=checkedClockId||coordinateMapper.Version!=checkedMapping||!participant.TryConsumePracticeObservationFence(fence))
                    throw new InvalidOperationException("Participant context changed during source initialization.");
                fence=null;
                task=participant;clock=sharedClock;clockId=checkedClockId;session=task.SessionId;mapper=coordinateMapper;version=checkedMapping;
                controlledMapper=coordinateMapper.GetType()==typeof(ViveProEyeRehearsalCoordinates);
                maximumAge=age;enabledAt=lastNow=now;owner=Thread.CurrentThread.ManagedThreadId;lease=claimedLease;
                provider=lease.ClaimForPump(token);status=VivePracticeEyeStatus.NotRead;
            }
            catch {if(fence!=null&&participant!=null)participant.AbandonPracticeObservationFence(fence);claimedLease.Dispose();throw;}
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
        static bool BeforePractice(ExperimentIncrementalSnapshot snapshot)
        {return snapshot!=null&&snapshot.Task.BlockIndex==-1&&!snapshot.Selection.HasValue&&
            (snapshot.Task.Phase==Phase.Preparation||snapshot.Task.Phase==Phase.Familiarization||snapshot.Task.Phase==Phase.PracticeRetry);}
        bool TakeFence(out ViveDevelopmentParticipant.PracticeObservationFence fence)
        {fence=null;if(!task.TryAcquirePracticeObservationFence(clock,out fence)||fence==null)return Fail("task_fence_unavailable");return true;}
        bool FinishFence(ref ViveDevelopmentParticipant.PracticeObservationFence fence)
        {
            if(fence==null)return Fail("task_fence_missing");
            // Keep the token reachable until consume returns, so the caller's
            // finally can abandon it if an unexpected exception interrupts it.
            bool current=task.TryConsumePracticeObservationFence(fence);fence=null;
            if(!current)return Fail("task_fence_changed");return true;
        }
        void AbandonFence(ViveDevelopmentParticipant.PracticeObservationFence fence)
        {if(fence!=null)try{task.AbandonPracticeObservationFence(fence);}catch(Exception){Fail("task_fence_abandon_failed");}}
        struct TraceCheckpoint
        {
            internal int Count,Index;internal long Revision;internal ExperimentIncrementalSnapshot State;
        }
        bool InitialTrace(ViveDevelopmentParticipant.PracticeObservationFence fence,out TraceCheckpoint next)
        {
            next=new TraceCheckpoint();var items=fence.StepTrace;
            if(items==null||items.Count==0||fence.MutationStepsObserved!=items.Count)return Fail("practice_trace_unavailable");
            var last=items[items.Count-1];
            if(last==null||last.After==null||!Object.ReferenceEquals(last.After,fence.Snapshot))return Fail("practice_trace_head_mismatch");
            next.Count=items.Count;next.Index=last.Index;next.Revision=last.Revision;next.State=fence.Snapshot;return true;
        }
        bool ScanNewPracticeSteps(ViveDevelopmentParticipant.PracticeObservationFence fence,out TraceCheckpoint next)
        {
            next=new TraceCheckpoint{Count=traceCount,Index=traceIndex,Revision=traceRevision,State=traceState};
            var items=fence.StepTrace;
            if(items==null||fence.MutationStepsObserved!=items.Count||items.Count<traceCount||traceState==null)
                return Fail("practice_trace_missing_or_regressed");
            for(int i=traceCount;i<items.Count;i++)
            {
                var step=items[i];
                if(step==null||step.Before==null||step.After==null||!Object.ReferenceEquals(step.Before,next.State)||
                    !((step.Revision==next.Revision&&step.Index==next.Index+1)||
                      (next.Revision<Int64.MaxValue-1&&step.Revision==next.Revision+1&&step.Index==0)))
                    return Fail("practice_trace_gap_or_replay");
                var after=step.After;
                if(after.Task==null||after.Task.SessionId!=session||after.Task.BlockIndex!=-1||after.Task.Condition.HasValue||
                    after.Selection.HasValue||after.Scalars.PracticeAttempt!=attempt||
                    (after.Task.Phase!=Phase.Practice&&after.Task.Phase!=Phase.PracticeCorrection))
                    return Fail("practice_interruption_or_context_change");
                if(after.CurrentTrial.HasValue)
                {
                    var trial=after.CurrentTrial.Value;
                    if(!trial.IsPractice||trial.SessionId!=session||trial.BlockIndex!=-1||trial.Condition.HasValue||trial.PracticeAttempt!=attempt)
                        return Fail("practice_trace_trial_context_changed");
                }
                next.State=after;next.Index=step.Index;next.Revision=step.Revision;next.Count++;
            }
            if(!Object.ReferenceEquals(next.State,fence.Snapshot))return Fail("practice_trace_head_mismatch");
            return true;
        }
        void CommitTrace(TraceCheckpoint next)
        {traceCount=next.Count;traceIndex=next.Index;traceRevision=next.Revision;traceState=next.State;}
        // Readiness never returns a sample. Its high-water marks prevent this
        // pre-bound observation from becoming the first post-bound candidate.
        public bool PollReadiness()
        {
            if(!Enter())return false;
            ViveDevelopmentParticipant.PracticeObservationFence fence=null;
            try
            {
                if(bound||!TakeFence(out fence)||!BeforePractice(fence.Snapshot))return Fail("readiness_context_changed");
                ViveProEyePoll poll;Vec3 mapped;double begin,end;
                bool usable=Read(out poll,out mapped,out begin,out end,true);
                if(RecoveryDecisionRequired)return false;
                double final;if(!Now(out final))return false;
                bool mappingCurrent=usable&&controlledMapper&&LastDerivedDirectionUsable;
                if(RecoveryDecisionRequired||!lease.IsClaimedBy(token)||!FinishFence(ref fence))return Fail("readiness_context_changed");
                if(!usable)return false;
                if(!mappingCurrent){LastDerivedDirectionUsable=false;SetStatus(VivePracticeEyeStatus.Unavailable);return false;}
                if(final-poll.Eye.ObservedLocalSeconds>maximumAge||final-poll.Head.ObservedLocalSeconds>maximumAge){SetStatus(VivePracticeEyeStatus.Unavailable);return false;}
                lock(stateGate){if(closed||RecoveryDecisionRequired)return false;status=VivePracticeEyeStatus.ReadyCandidate;return true;}
            }
            catch(Exception){return Fail("readiness_check_failed");}
            finally{AbandonFence(fence);Exit();}
        }
        public bool BindPractice(VivePracticeContextV3 context)
        {
            if(!Enter())return false;
            ViveDevelopmentParticipant.PracticeObservationFence fence=null;
            try
            {
                if(bound||context==null||!TakeFence(out fence))return Fail("practice_context_unavailable");
                var snapshot=fence.Snapshot;var currentTrial=snapshot.CurrentTrial;
                if(snapshot.Task.Phase!=Phase.Practice||snapshot.Task.BlockIndex!=-1||snapshot.Selection.HasValue||!currentTrial.HasValue)
                    return Fail("practice_context_unavailable");
                var trial=currentTrial.Value;if(!trial.IsPractice||trial.BlockIndex!=-1||trial.Condition.HasValue||
                    trial.PracticeAttempt<1||trial.SessionId!=session||context.SessionId!=session||context.PracticeAttempt!=trial.PracticeAttempt||
                    context.ClockId!=clockId||context.MappingVersion!=version)return Fail("practice_trial_context_invalid");
                // This isolated source has no trusted host epoch ledger. It can
                // only bind the first attempt's initial epoch; a retry or a
                // recovery must not be promoted from caller declarations.
                if(trial.PracticeAttempt!=1||context.EpochReason!=VivePracticeEpochReasonV3.Initial||
                    context.RecoveryOrdinal!=0||context.PreviousEpochId!=null)return Fail("practice_epoch_linkage_unverified");
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
                    !Finite(matched.Time)||matched.Time<=enabledAt||matched.Time>now||matched.Sequence<0||
                    !context.ObservedPracticeStartLocalSeconds.HasValue||
                    BitConverter.DoubleToInt64Bits(context.ObservedPracticeStartLocalSeconds.Value)!=BitConverter.DoubleToInt64Bits(matched.Time))
                    return Fail("practice_start_not_verified");
                // An Initial epoch must not be assigned after an interruption,
                // including a pause/resume that returned to this same attempt.
                // This is a one-time scan of the same participant's original
                // events, never a substitute for a mutation revision.
                foreach(var item in events)
                    if(item.Sequence>matched.Sequence&&(item.Kind=="explicit_pause"||item.Kind=="explicit_resume"||
                        item.Kind=="trial_interrupted"||item.Kind=="practice_resume_next_planned_trial"))
                        return Fail("practice_interruption_before_bind");
                TraceCheckpoint checkpoint;if(!InitialTrace(fence,out checkpoint))return false;
                if(RecoveryDecisionRequired||!lease.IsClaimedBy(token)||!FinishFence(ref fence))return Fail("practice_context_changed_during_bind");
                lock(stateGate)
                {
                    if(closed||RecoveryDecisionRequired)return false;
                    attempt=trial.PracticeAttempt;startSequence=matched.Sequence;startTime=matched.Time;epoch=now;bound=true;
                    practiceContext=context;CommitTrace(checkpoint);
                    status=VivePracticeEyeStatus.WaitingForFreshFrame;return true;
                }
            }
            catch(Exception){return Fail("practice_binding_failed");}
            finally{AbandonFence(fence);Exit();}
        }
        bool CurrentPractice(ExperimentIncrementalSnapshot snapshot)
        {
            if(!bound||snapshot==null||snapshot.Task.BlockIndex!=-1||snapshot.Selection.HasValue||
                (snapshot.Task.Phase!=Phase.Practice&&snapshot.Task.Phase!=Phase.PracticeCorrection)||!snapshot.CurrentTrial.HasValue)return false;
            var trial=snapshot.CurrentTrial.Value;
            return trial.IsPractice&&trial.BlockIndex==-1&&!trial.Condition.HasValue&&trial.PracticeAttempt==attempt&&trial.SessionId==session;
        }
        public bool TryRead(out VivePracticeEyeCandidate candidate)
        {
            candidate=null;if(!Enter())return false;
            ViveDevelopmentParticipant.PracticeObservationFence fence=null;
            try
            {
                lock(stateGate)
                {
                    if(syntheticFeed!=null)
                    {syntheticConsumed=null;if(syntheticVersion==Int64.MaxValue)return Fail("synthetic_version_exhausted");syntheticVersion++;}
                }
                if(!TakeFence(out fence)||!CurrentPractice(fence.Snapshot))return Fail("practice_context_changed_or_paused");
                TraceCheckpoint checkpoint;if(!ScanNewPracticeSteps(fence,out checkpoint))return false;
                ViveProEyePoll poll;Vec3 mapped;double begin,end;
                bool usable=Read(out poll,out mapped,out begin,out end,false);
                if(RecoveryDecisionRequired)return false;
                double final;if(!Now(out final))return false;
                bool mappingCurrent=usable&&controlledMapper&&LastDerivedDirectionUsable;
                if(lastAcceptedEye>=0&&final-lastAcceptedEye>maximumAge)return Fail("eye_coverage_gap");
                if(RecoveryDecisionRequired||!lease.IsClaimedBy(token)||!FinishFence(ref fence))return Fail("practice_context_changed_during_read");
                CommitTrace(checkpoint);
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
                    candidate=new VivePracticeEyeCandidate(candidatePoll,derived?(Vec3?)mapped:null,headUsable,version,begin,end,attempt,startSequence,epoch,clock,maximumAge,practiceContext);
                    lastAcceptedEye=poll.Eye.ObservedLocalSeconds;
                    status=derived?VivePracticeEyeStatus.AcceptedCandidate:VivePracticeEyeStatus.AcceptedRawOnlyCandidate;return true;
                }
            }
            catch(Exception){return Fail("candidate_read_failed");}
            finally{AbandonFence(fence);Exit();}
        }
        // Source-owned single-use synthetic marker only. No caller-supplied
        // candidate or raw poll is accepted; this is not an I/O authorization.
        internal bool TryIssueSyntheticTicket(out VivePracticeSyntheticTicket ticket)
        {
            ticket=null;
            if(syntheticFeed==null||syntheticChoices==null)return false;
            if(!EnterSynthetic())return false;
            ViveDevelopmentParticipant.PracticeObservationFence fence=null;
            try
            {
                if(Interlocked.CompareExchange(ref syntheticInvalid,0,0)!=0||syntheticOutstanding!=null||!syntheticChoices.IsCurrent(syntheticGeneration,task,clock))return false;
                VivePracticeEyeCandidate candidate;
                if(!TryRead(out candidate)||candidate==null||!syntheticChoices.IsCurrent(syntheticGeneration,task,clock))return false;
                if(!task.TryAcquirePracticeObservationFence(clock,out fence)||fence==null)return false;
                var trace=fence.StepTrace;double now;
                if(trace==null||trace.Count!=traceCount||traceCount<1||fence.MutationStepsObserved!=traceCount||fence.Revision!=traceRevision||
                    !Object.ReferenceEquals(fence.Snapshot,traceState)||trace[traceCount-1].Revision!=traceRevision||
                    trace[traceCount-1].Index!=traceIndex||!Object.ReferenceEquals(trace[traceCount-1].After,traceState)||!Now(out now)||candidate.RawPoll==null||candidate.RawPoll.Eye==null||
                    candidate.RawPoll.Eye.ObservedLocalSeconds>now||now-candidate.RawPoll.Eye.ObservedLocalSeconds>maximumAge||
                    !lease.IsClaimedBy(token)||!syntheticChoices.IsCurrent(syntheticGeneration,task,clock))return false;
                var completed=fence;bool valid=task.TryConsumePracticeObservationFence(fence);fence=null;
                if(!valid||Interlocked.CompareExchange(ref syntheticInvalid,0,0)!=0||!task.PracticeGenerationStillCurrent(completed)||!syntheticChoices.IsCurrent(syntheticGeneration,task,clock)||
                    closed||RecoveryDecisionRequired)return false;
                ticket=new VivePracticeSyntheticTicket(this,task,clock,syntheticChoices,practiceContext,traceState,trace[traceCount-1],traceRevision,
                    syntheticGeneration,syntheticVersion,startSequence,traceCount,attempt,epoch,candidate.RawPoll.Eye.ObservedLocalSeconds,maximumAge,candidate);
                lock(stateGate)
                {
                    if(syntheticInvalid!=0||closed||RecoveryDecisionRequired){ticket=null;return false;}
                    syntheticMarker=syntheticFeed.DeliveredMarker;syntheticCandidate=candidate;syntheticOutstanding=ticket;return true;
                }
            }
            catch(Exception){return false;}
            finally{AbandonFence(fence);ExitSynthetic();}
        }
        internal bool TryConsumeSyntheticTicket(VivePracticeSyntheticTicket ticket,ExperimentIncrementalSnapshot expectedSnapshot,
            long expectedRevision,int expectedTraceCount,out byte marker)
        {
            marker=0;
            if(ticket==null)return false;
            if(Interlocked.CompareExchange(ref ticket.Consumed,1,0)!=0)
            {if(Interlocked.CompareExchange(ref syntheticBusy,0,0)!=0)InvalidateSynthetic();return false;}
            if(!EnterSynthetic())return false;
            ViveDevelopmentParticipant.PracticeObservationFence fence=null;
            try
            {
                if(Interlocked.CompareExchange(ref syntheticInvalid,0,0)!=0||!Object.ReferenceEquals(syntheticOutstanding,ticket)||!ticket.MatchesCandidate(syntheticCandidate)||!Object.ReferenceEquals(ticket.Issuer,this)||
                    !Object.ReferenceEquals(ticket.Participant,task)||!Object.ReferenceEquals(ticket.Clock,clock)||
                    !Object.ReferenceEquals(ticket.Choices,syntheticChoices)||!Object.ReferenceEquals(ticket.Context,practiceContext)||!Object.ReferenceEquals(ticket.Snapshot,expectedSnapshot)||
                    ticket.Revision!=expectedRevision||ticket.TraceCount!=expectedTraceCount||ticket.Generation!=syntheticGeneration||
                    ticket.Version!=syntheticVersion||ticket.Attempt!=attempt||ticket.StartSequence!=startSequence||ticket.Epoch!=epoch||ticket.MaximumAgeSeconds!=maximumAge||
                    closed||RecoveryDecisionRequired||!syntheticChoices.IsCurrent(ticket.Generation,task,clock)||!lease.IsClaimedBy(token))return false;
                if(!task.TryAcquirePracticeObservationFence(clock,out fence)||fence==null)return false;
                var trace=fence.StepTrace;double now;
                if(!Object.ReferenceEquals(fence.Snapshot,ticket.Snapshot)||fence.Revision!=ticket.Revision||
                    trace==null||trace.Count!=ticket.TraceCount||fence.MutationStepsObserved!=ticket.TraceCount||ticket.TraceCount<1||
                    !Object.ReferenceEquals(trace[ticket.TraceCount-1],ticket.LastStep)||!Now(out now)||ticket.Observed>now||
                    now-ticket.Observed>maximumAge||!syntheticChoices.IsCurrent(ticket.Generation,task,clock))return false;
                var completed=fence;bool valid=task.TryConsumePracticeObservationFence(fence);fence=null;
                if(!valid||Interlocked.CompareExchange(ref syntheticInvalid,0,0)!=0||!task.PracticeGenerationStillCurrent(completed)||!syntheticChoices.IsCurrent(ticket.Generation,task,clock)||
                    closed||RecoveryDecisionRequired||!lease.IsClaimedBy(token))return false;
                lock(stateGate)
                {
                    if(syntheticInvalid!=0||closed||RecoveryDecisionRequired||ticket.Version!=syntheticVersion)return false;
                    syntheticConsumed=ticket;marker=syntheticMarker;return true;
                }
            }
            catch(Exception){return false;}
            finally{lock(stateGate){syntheticOutstanding=null;syntheticCandidate=null;syntheticMarker=0;}AbandonFence(fence);ExitSynthetic();}
        }
        bool Read(out ViveProEyePoll poll,out Vec3 mapped,out double begin,out double end,bool readiness)
        {
            poll=null;mapped=new Vec3(Double.NaN,Double.NaN,Double.NaN);begin=end=0;
            LastNativeDirectionReportedValid=LastRawDirectionNormalizable=LastEyeUsable=LastHeadUsable=LastDerivedDirectionUsable=false;
            LastNativeResultCode=null;LastHeadDisposition=ViveFrameDisposition.Missing;
            if(!lease.IsClaimedBy(token))return Fail("cooperative_lease_lost");
            if(!Now(out begin))return false;
            // A later fresh frame cannot bridge an elapsed unobserved interval.
            // The caller must explicitly handle the unknown gap/new epoch.
            if(!readiness&&lastAcceptedEye>=0&&begin-lastAcceptedEye>maximumAge)return Fail("eye_coverage_gap");
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
                syntheticOutstanding=null;syntheticConsumed=null;syntheticCandidate=null;syntheticMarker=0;
            }
            try{lease.CloseClaim(token);}catch(Exception){Fail("lease_release_failed");return;}
            lock(stateGate){if(!RecoveryDecisionRequired)status=VivePracticeEyeStatus.Closed;}
        }
    }
}
