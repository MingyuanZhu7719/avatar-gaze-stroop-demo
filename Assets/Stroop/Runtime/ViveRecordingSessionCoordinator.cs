using System;
using System.Collections.Generic;
using System.Threading;
using AvatarStroop.Core;

namespace AvatarStroop.Runtime
{
    // A future sink adapter must implement this contract on the owner thread.
    // This interface has no implementation that opens a file or polls a device.
    // AbortUnclean must release/preserve partial evidence WITHOUT writing a close
    // marker. It must not delegate to a Dispose which implicitly calls Close.
    // Initially dormant: no storage/device is opened before explicit start. The
    // open method must not be retried on failure; AbortUnclean must tolerate a
    // partial/uncertain open and never manufacture a close marker. Calls must
    // not re-enter the authoritative task or mutate observation data.
    public interface IViveRecordingSegmentPort
    {
        string SessionId {get;}
        string StreamId {get;}
        string DeviceId {get;}
        string MappingVersion {get;}
        string ClockId {get;}
        int BlockIndex {get;}
        int Segment {get;}
        bool IsDormant {get;}
        bool UsesClock(IClock clock);
        bool UsesSource(ViveParticipantEyeSource source);
        bool OpenSegmentAndBeginTaskEvents(long firstSequence,ViveDevelopmentTaskOrigin origin);
        // Acknowledges one exact source attempt without polling or appending
        // a frame. The future bridge adapter must enforce the same generation.
        bool AcknowledgeSourceRead(long generation,ViveParticipantReadStatus status);
        // Persist an explicit unknown coverage interval, never a fabricated
        // device gap or claim about hardware capture time. v1 cannot do this.
        bool RecordUnknownInitialCoverage(double segmentBoundaryLocalSeconds,double firstObservedLocalSeconds);
        // Conservative uncertainty window from last observation to recovery;
        // neither endpoint asserts the exact instant of physical tracking loss.
        bool RecordGap(double lastKnownObservationLocalSeconds,double recoveryObservedLocalSeconds,ViveDevelopmentGapReason reason);
        bool RecordObservation(ViveParticipantEyeObservation observation);
        bool RecordAuthoritativeTaskEvent(JournalEvent item);
        bool CloseSegment();
        bool AbortUnclean(string reason);
    }

    public enum ViveRecordingCoordinatorState { Open, GapOpen, Closed, Aborted, Faulted, CloseOutcomeUncertain }

    // A token is bound to a particular coordinator instance and observation.
    // It cannot be constructed by a caller, replaced while pending or committed
    // twice. It is in-memory only and is never a persistence receipt.
    public sealed class ViveRecordingPendingObservation
    {
        internal readonly ViveRecordingSessionCoordinator Owner;
        internal readonly ViveParticipantEyeObservation Observation;
        internal readonly ParticipantEyeSample Sample;
        internal readonly ViveProEyePoll RawPoll;
        internal readonly ViveFrameDisposition Disposition;
        internal readonly double PollStarted,PollCompleted;
        internal readonly bool NativeValid;
        internal readonly string FailureReason;
        internal readonly ViveParticipantReadStatus Usability;
        internal ViveRecordingPendingObservation(ViveRecordingSessionCoordinator owner,ViveParticipantEyeObservation observation)
        {Owner=owner;Observation=observation;Sample=observation.Sample;RawPoll=observation.RawPoll;Disposition=observation.EyeDisposition;
            PollStarted=observation.PollStartedLocalSeconds;PollCompleted=observation.PollCompletedLocalSeconds;
            NativeValid=observation.NativeDirectionReportedValid;FailureReason=observation.EyeFailureReason;Usability=observation.UsabilityStatus;}
        public double EyeObservedLocalSeconds {get{return Observation.RawPoll.Eye.ObservedLocalSeconds;}}
        public bool EyeUsable {get{return Observation.Sample.DerivedEyeValid;}}
    }

    // Per-segment development coordination only. It never starts acquisition,
    // opens files or network sockets, or admits an apparatus session. The host
    // supplies a genuine source observation and owns the explicit source/gap
    // detection policy. All source reads must be made serially on the owner
    // thread and each attempt consumed here, including duplicates and failures.
    // The generation detects omitted attempts, not simultaneous-read safety or
    // hardware coverage. This is a development seam, not research admission.
    // An unknown initial interval or open gap at a segment
    // boundary cannot be honestly represented by the current v1 bridge: it is
    // abandoned unclean. Successful close is NOT a full-session, precision,
    // power-loss durability, or hardware capture-time claim.
    public sealed class ViveRecordingSessionCoordinator : IDisposable
    {
        readonly ViveDevelopmentParticipant task;
        readonly ViveParticipantEyeSource source;
        readonly IViveRecordingSegmentPort port;
        readonly IClock clock;
        readonly string sessionId,clockId,streamId,deviceId;
        readonly int block,segment,ownerThread;
        readonly double segmentBoundary;
        double lastNow,lastObservation=-1,gapDetectedAt=-1,gapBoundStart=-1;
        int lastNativeSequence,lastNativeTimestamp;
        long lastSampleSequence=-1,nextEventSequence,accountedReadGeneration;
        bool hasNative,hasObservation,cursorStarted,openAttempted,abortAttempted,abortAcknowledged,closeAttempted;
        ViveDevelopmentGapReason gapReason;
        ViveRecordingPendingObservation pending;
        public ViveRecordingCoordinatorState State {get;private set;}
        public string LastIssue {get;private set;}
        public bool HasPending {get{return pending!=null;}}
        public bool HasObservation {get{return hasObservation;}}
        public bool AbortAttempted {get{return abortAttempted;}}
        // Port acknowledgment is not proof that bytes reached stable storage.
        public bool AbortAcknowledged {get{return abortAcknowledged;}}
        public bool ContinuousCoverageProven {get{return false;}}
        public bool InitialPreObservationCoverageUnknown {get{return true;}}
        public bool CompleteSessionProven {get{return false;}}
        public bool HardwareCaptureTimeVerified {get{return false;}}
        public bool PowerLossDurabilityGuaranteed {get{return false;}}
        public bool ResearchExportAllowed {get{return false;}}
        public string CoverageScope {get{return "ONE_BLOCK_SEGMENT_ONLY";}}

        public ViveRecordingSessionCoordinator(ViveDevelopmentParticipant task,ViveParticipantEyeSource source,IViveRecordingSegmentPort port,IClock sharedClock,
            bool explicitEyeOptIn,bool explicitTaskOptIn,bool explicitSegmentStart)
        {
            if(!explicitEyeOptIn||!explicitTaskOptIn||!explicitSegmentStart)
                throw new InvalidOperationException("Explicit eye, task and segment recording choices are required");
            if(task==null||source==null||port==null||sharedClock==null)throw new ArgumentNullException("task/source/port/sharedClock");
            if(task.HasNamedOption||task.RawContext!=null)throw new InvalidOperationException("Legacy recording coordinator cannot represent versioned raw or named-option provenance");
            if(!port.IsDormant||!task.UsesEventClock(sharedClock)||!port.UsesClock(sharedClock)||!source.UsesClock(sharedClock)||!port.UsesSource(source)||
                task.EventClockId!=sharedClock.ClockId||port.ClockId!=sharedClock.ClockId||task.SessionId!=port.SessionId||
                source.ClockId!=sharedClock.ClockId||source.SessionId!=port.SessionId||source.StreamId!=port.StreamId||source.DeviceId!=port.DeviceId||
                source.MappingVersion!=port.MappingVersion||source.BlockIndex!=port.BlockIndex||source.Segment!=port.Segment||source.ReadAttemptGenerationExhausted||
                task.BlockIndex!=port.BlockIndex||task.Segment!=port.Segment||task.BlockIndex<0||task.Phase==Phase.Ended)
                throw new ArgumentException("The actual task, port, current segment and same shared clock instance are required");
            WireCodec.RequireIdentity(port.StreamId);WireCodec.RequireIdentity(port.DeviceId);
            double now=sharedClock.Now;if(!WireCodec.Finite(now)||now<0)throw new ArgumentException("Valid local clock required");
            var first=FindFirstSegmentEvent(task,port.BlockIndex,port.Segment);
            if(first==null||first.SessionId!=task.SessionId||first.ClockId!=sharedClock.ClockId||
                !WireCodec.Finite(first.Time)||first.Time<0||first.Time>now)
                throw new ArgumentException("Observed authoritative segment boundary required");
            this.task=task;this.source=source;this.port=port;clock=sharedClock;sessionId=task.SessionId;clockId=sharedClock.ClockId;
            streamId=port.StreamId;deviceId=port.DeviceId;block=port.BlockIndex;segment=port.Segment;
            segmentBoundary=first.Time;nextEventSequence=first.Sequence;lastNow=now;
            accountedReadGeneration=source.ReadAttemptGeneration;
            ownerThread=Thread.CurrentThread.ManagedThreadId;State=ViveRecordingCoordinatorState.Open;
        }
        static JournalEvent FindFirstSegmentEvent(ViveDevelopmentParticipant task,int block,int segment)
        {
            long cursor=-1;
            while(true)
            {
                var page=task.ReadEventsAfter(cursor,4096);
                foreach(var item in page){if(item.BlockIndex==block&&item.Segment==segment)return item;cursor=item.Sequence;}
                if(page.Count<4096)return null;
            }
        }
        void RequireOwner()
        {if(Thread.CurrentThread.ManagedThreadId!=ownerThread)throw new InvalidOperationException("Recording coordinator requires its owner thread");}
        bool Active {get{return State==ViveRecordingCoordinatorState.Open||State==ViveRecordingCoordinatorState.GapOpen;}}
        bool ContextOpen {get{return task.Phase!=Phase.Ended&&task.BlockIndex==block&&task.Segment==segment;}}
        bool NoUnconsumedRead()
        {
            if(source.ReadAttemptGenerationExhausted||source.ReadAttemptGeneration!=accountedReadGeneration)
            {Abort("unconsumed_source_read");return false;}
            return true;
        }
        bool ConsumeSingleRead(bool accepted,bool duplicate)
        {
            long actual=source.ReadAttemptGeneration;
            if(source.ReadAttemptGenerationExhausted||accountedReadGeneration==Int64.MaxValue||actual!=accountedReadGeneration+1||
                (accepted&&source.LastReadStatus!=ViveParticipantReadStatus.Accepted)||
                (duplicate&&source.LastReadStatus!=ViveParticipantReadStatus.Duplicate)||
                (!accepted&&!duplicate&&(source.LastReadStatus==ViveParticipantReadStatus.Accepted||source.LastReadStatus==ViveParticipantReadStatus.Duplicate||source.LastReadStatus==ViveParticipantReadStatus.NotRead)))
            {Abort("source_read_generation_or_status_mismatch");return false;}
            var status=source.LastReadStatus;
            try
            {
                if(!port.AcknowledgeSourceRead(actual,status)||source.ReadAttemptGeneration!=actual||source.LastReadStatus!=status)
                {Abort("source_read_port_acknowledgement_failed");return false;}
            }
            catch(Exception e){Abort("source_read_port_acknowledgement_exception:"+e.GetType().Name);return false;}
            accountedReadGeneration=actual;return true;
        }
        double Now()
        {
            double now;
            try
            {
                now=clock.Now;
                if(clock.ClockId!=clockId||!task.UsesEventClock(clock)||!port.UsesClock(clock)||!source.UsesClock(clock)||!port.UsesSource(source)||
                    source.MappingVersion!=port.MappingVersion||!WireCodec.Finite(now)||now<lastNow)
                    throw new InvalidOperationException("Recording epoch requires the original shared clock and source");
            }
            catch(Exception e){Abort("clock_or_identity_failed:"+e.GetType().Name);throw new InvalidOperationException("Recording clock or source identity failed",e);}
            lastNow=now;return now;
        }

        // The host must begin the real authoritative event stream at the first
        // event of this segment; it cannot choose a later cursor to hide history.
        public bool BeginTaskEvents(long firstSequence,bool explicitStart)
        {
            RequireOwner();if(!Active)return false;Now();
            if(!NoUnconsumedRead())return false;
            if(!explicitStart||cursorStarted||firstSequence!=nextEventSequence)
            {Abort("authoritative_cursor_invalid");return false;}
            if(!ContextOpen){Abort("segment_changed_before_open");return false;}
            openAttempted=true;
            try
            {
                if(!port.OpenSegmentAndBeginTaskEvents(firstSequence,ViveDevelopmentTaskOrigin.SimulationDevelopment))
                {Abort("segment_open_not_acknowledged");return false;}
            }
            catch(Exception e){Abort("segment_open_uncertain:"+e.GetType().Name);return false;}
            cursorStarted=true;return true;
        }
        public bool RecordTaskEvent(JournalEvent supplied)
        {
            RequireOwner();if(!Active)return false;double now=Now();
            if(!NoUnconsumedRead())return false;
            if(!cursorStarted||supplied==null||supplied.Sequence!=nextEventSequence||supplied.Sequence==Int64.MaxValue)
            {Abort("authoritative_event_sequence_invalid");return false;}
            IReadOnlyList<JournalEvent> page;
            try{page=task.ReadEventsAfter(supplied.Sequence-1,1);}
            catch(Exception){Abort("authoritative_event_cursor_invalid");return false;}
            if(page.Count!=1||!SameEvent(page[0],supplied)||supplied.SessionId!=sessionId||supplied.ClockId!=clockId||
                supplied.BlockIndex!=block||supplied.Segment!=segment||!WireCodec.Finite(supplied.Time)||supplied.Time>now)
            {Abort("authoritative_event_mismatch");return false;}
            try{if(!port.RecordAuthoritativeTaskEvent(page[0])){Abort("task_event_write_failed");return false;}}
            catch(Exception e){Abort("task_event_write_exception:"+e.GetType().Name);return false;}
            nextEventSequence=checked(nextEventSequence+1);return true;
        }
        static bool SameEvent(JournalEvent a,JournalEvent b)
        {
            if(a==null||b==null||a.Sequence!=b.Sequence||a.Time!=b.Time||a.ClockId!=b.ClockId||a.SessionId!=b.SessionId||
                a.BlockIndex!=b.BlockIndex||a.Segment!=b.Segment||a.TrialId!=b.TrialId||a.Kind!=b.Kind||
                a.Fields==null||b.Fields==null||a.Fields.Count!=b.Fields.Count)return false;
            foreach(var item in a.Fields){string value;if(!b.Fields.TryGetValue(item.Key,out value)||value!=item.Value)return false;}
            return true;
        }

        // Observe is deliberately non-writing. The caller obtains the observation
        // from its one shared worker/source and must not poll the v1 bridge first,
        // because that bridge immediately appends and advances its timeline.
        public ViveRecordingPendingObservation Observe(ViveParticipantEyeObservation observation)
        {
            RequireOwner();if(!Active)throw new InvalidOperationException("Recording segment is not open");double now=Now();
            if(!cursorStarted)throw new InvalidOperationException("Begin the authoritative event stream before observing");
            if(!ContextOpen){Abort("segment_changed_before_observation");throw new InvalidOperationException("Recording context is closed");}
            if(pending!=null)throw new InvalidOperationException("Commit or abandon the pending observation first");
            if(!ConsumeSingleRead(true,false))throw new InvalidOperationException("Source read attempt was not consumed in order");
            if(observation==null||!Object.ReferenceEquals(source.LastObservation,observation)||observation.Sample==null||observation.RawPoll==null||observation.RawPoll.Eye==null||
                observation.EyeDisposition!=ViveFrameDisposition.Accepted||observation.HardwareCaptureTimeVerified||observation.MappingValidatedForApparatus)
            {Abort("malformed_observation");throw new ArgumentException("A fresh raw participant eye observation is required","observation");}
            var sample=observation.Sample;var eye=observation.RawPoll.Eye;
            if(sample.Source!=ParticipantEyeSource.RealDevice||sample.Role!=StreamRole.ParticipantMeasurement||
                sample.SessionId!=sessionId||sample.StreamId!=streamId||sample.DeviceId!=deviceId||sample.ClockId!=clockId||
                sample.BlockIndex!=block||sample.Segment!=segment||sample.CaptureLocalSeconds!=eye.ObservedLocalSeconds||
                !WireCodec.Finite(eye.ObservedLocalSeconds)||eye.ObservedLocalSeconds<segmentBoundary||eye.ObservedLocalSeconds>now||
                (hasObservation&&(lastSampleSequence==Int64.MaxValue||sample.Sequence!=lastSampleSequence+1))||eye.ObservedLocalSeconds<lastObservation||
                (hasNative&&(eye.FrameSequence<=lastNativeSequence||eye.Timestamp<=lastNativeTimestamp)))
            {Abort("observation_identity_or_order_invalid");throw new ArgumentException("Eye observation identity, sequence, context or time is not current","observation");}
            if(State==ViveRecordingCoordinatorState.GapOpen&&
                (!sample.DerivedEyeValid||eye.ObservedLocalSeconds<=gapDetectedAt))
            {
                // v1 cannot safely preserve an additional invalid sample within an
                // open gap and later insert that earlier gap. Do not drop it and
                // subsequently claim a continuous, clean recording.
                Abort("unusable_or_old_observation_during_open_gap");
                throw new InvalidOperationException("The open gap cannot be reconciled in journal v1");
            }
            pending=new ViveRecordingPendingObservation(this,observation);return pending;
        }
        public bool Commit(ViveRecordingPendingObservation candidate)
        {
            RequireOwner();if(!Active)return false;Now();
            if(!NoUnconsumedRead())return false;
            if(candidate==null||candidate.Owner!=this||!Object.ReferenceEquals(candidate,pending))
                throw new InvalidOperationException("Only the current pending observation can be committed once");
            if(!ContextOpen){Abort("segment_changed_with_pending_observation");return false;}
            var observed=candidate.Observation;
            if(!Object.ReferenceEquals(source.LastObservation,observed)||!Object.ReferenceEquals(observed.Sample,candidate.Sample)||!Object.ReferenceEquals(observed.RawPoll,candidate.RawPoll)||
                observed.EyeDisposition!=candidate.Disposition||observed.PollStartedLocalSeconds!=candidate.PollStarted||
                observed.PollCompletedLocalSeconds!=candidate.PollCompleted||observed.NativeDirectionReportedValid!=candidate.NativeValid||
                observed.EyeFailureReason!=candidate.FailureReason||observed.UsabilityStatus!=candidate.Usability)
            {Abort("pending_observation_changed");return false;}
            var eye=candidate.Observation.RawPoll.Eye;
            try
            {
                if(!hasObservation&&!port.RecordUnknownInitialCoverage(segmentBoundary,eye.ObservedLocalSeconds))
                {Abort("initial_unknown_coverage_write_failed");return false;}
                if(State==ViveRecordingCoordinatorState.GapOpen)
                {
                    // This is an observed detection window, not an assertion of
                    // the exact instant at which physical tracking was lost.
                    if(!port.RecordGap(gapBoundStart,eye.ObservedLocalSeconds,gapReason))
                    {Abort("gap_write_failed");return false;}
                }
                // Two separate writes are NOT a transaction. A failure after the
                // gap preserves only an unclean verified prefix, never clean close.
                if(!port.RecordObservation(candidate.Observation)){Abort("observation_write_failed");return false;}
            }
            catch(Exception e){Abort("observation_commit_exception:"+e.GetType().Name);return false;}
            lastObservation=eye.ObservedLocalSeconds;lastSampleSequence=candidate.Observation.Sample.Sequence;
            lastNativeSequence=eye.FrameSequence;lastNativeTimestamp=eye.Timestamp;hasNative=true;hasObservation=true;
            pending=null;gapDetectedAt=-1;gapBoundStart=-1;State=ViveRecordingCoordinatorState.Open;
            // The raw invalid observation is retained, but journal v1 cannot
            // represent its unresolved unavailability without risking a false
            // clean close. Do not depend on the host remembering a second call.
            if(!candidate.Sample.DerivedEyeValid){Abort("unusable_observation_recorded");return false;}
            return true;
        }

        // Consume one observed unavailable source read, not a duplicate render
        // poll or an unverified external assertion. The persisted bound begins
        // at the last known observation, not at the later handling time. It is
        // an uncertainty window, not proof of the exact physical loss onset.
        // First-observation gaps are unrepresentable in journal v1.
        public bool MarkUnavailable(ViveDevelopmentGapReason reason)
        {
            RequireOwner();if(!Active)return false;double now=Now();
            if(!Enum.IsDefined(typeof(ViveDevelopmentGapReason),reason))throw new ArgumentOutOfRangeException("reason");
            if(!ContextOpen){Abort("segment_changed_before_loss");return false;}
            if(!cursorStarted)throw new InvalidOperationException("Begin the authoritative event stream before loss detection");
            if(pending!=null){Abort("loss_with_pending_observation");return false;}
            if(source.ReadAttemptGeneration==accountedReadGeneration)
                return State==ViveRecordingCoordinatorState.GapOpen&&reason==gapReason;
            else
            {
                if(!ConsumeSingleRead(false,false))return false;
                var status=source.LastReadStatus;
                if(status==ViveParticipantReadStatus.ProviderFailure||status==ViveParticipantReadStatus.ClockFailure||
                    status==ViveParticipantReadStatus.MappingFailure||status==ViveParticipantReadStatus.UnknownFailure||
                    status==ViveParticipantReadStatus.ReadAttemptGenerationExhausted)
                {Abort("source_failure_not_recoverable_as_gap:"+status);return false;}
                if(reason!=ReasonForStatus(status)){Abort("gap_reason_disagrees_with_source");return false;}
            }
            if(source.ReadAttemptGenerationExhausted){Abort("source_read_generation_exhausted");return false;}
            if(State==ViveRecordingCoordinatorState.GapOpen)
            {
                if(reason!=gapReason){Abort("additional_gap_reason_not_representable");return false;}
                return true; // retain FIRST detection time
            }
            if(!hasObservation){Abort("initial_unavailability_not_representable");return false;}
            if(now<lastObservation){Abort("loss_before_last_observation");return false;}
            gapDetectedAt=now;gapBoundStart=lastObservation;gapReason=reason;State=ViveRecordingCoordinatorState.GapOpen;return true;
        }
        static ViveDevelopmentGapReason ReasonForStatus(ViveParticipantReadStatus status)
        {
            switch(status)
            {
                case ViveParticipantReadStatus.Missing:return ViveDevelopmentGapReason.Missing;
                case ViveParticipantReadStatus.Stale:return ViveDevelopmentGapReason.Stale;
                case ViveParticipantReadStatus.OutOfOrder:return ViveDevelopmentGapReason.OutOfOrder;
                case ViveParticipantReadStatus.DeviceUnavailable:return ViveDevelopmentGapReason.DeviceUnavailable;
                case ViveParticipantReadStatus.CalibrationUnavailable:return ViveDevelopmentGapReason.CalibrationUnavailable;
                case ViveParticipantReadStatus.RuntimeUnavailable:
                case ViveParticipantReadStatus.NativeFailure:return ViveDevelopmentGapReason.RuntimeFailure;
                default:return ViveDevelopmentGapReason.Other;
            }
        }
        // A repeated frame is not an observed gap. Still consume its read
        // generation so no unseen failure can be hidden by a later success.
        public bool AcknowledgeDuplicateRead()
        {
            RequireOwner();if(!Active)return false;Now();
            if(!cursorStarted||!ContextOpen||pending!=null){Abort("duplicate_read_outside_open_context");return false;}
            return ConsumeSingleRead(false,true);
        }

        bool HasUndrainedSegmentEvents()
        {
            long cursor=nextEventSequence-1;
            while(true)
            {
                var page=task.ReadEventsAfter(cursor,4096);
                foreach(var item in page){if(item.BlockIndex==block&&item.Segment==segment)return true;cursor=item.Sequence;}
                if(page.Count<4096)return false;
            }
        }
        // Call only after the authoritative task has exited this segment or
        // ended and its old-segment events have been drained. It does not imply
        // that practice, other segments or the full session have been recorded.
        public bool CloseAfterSegment()
        {
            RequireOwner();if(!Active)return false;Now();
            if(!NoUnconsumedRead())return false;
            if(ContextOpen)return false;
            if(pending!=null){Abort("pending_observation_at_segment_end");return false;}
            if(State==ViveRecordingCoordinatorState.GapOpen){Abort("open_gap_at_segment_end");return false;}
            if(!hasObservation){Abort("initial_coverage_unknown_at_segment_end");return false;}
            if(source.LastReadStatus!=ViveParticipantReadStatus.Accepted&&source.LastReadStatus!=ViveParticipantReadStatus.Duplicate)
            {Abort("source_read_failed_before_segment_close");return false;}
            if(!cursorStarted||HasUndrainedSegmentEvents())return false;
            closeAttempted=true;
            try
            {
                if(!port.CloseSegment()){State=ViveRecordingCoordinatorState.Faulted;LastIssue="segment_close_not_acknowledged";return false;}
                State=ViveRecordingCoordinatorState.Closed;LastIssue=null;return true;
            }
            catch(Exception e)
            {
                // Close may already have written a marker: outcome is unknown.
                // Never invoke another close/abort automatically in this case.
                State=ViveRecordingCoordinatorState.CloseOutcomeUncertain;LastIssue="segment_close_uncertain:"+e.GetType().Name;return false;
            }
        }
        public void Abort(string reason)
        {
            RequireOwner();
            if(!Active||closeAttempted||abortAttempted)return;
            if(String.IsNullOrWhiteSpace(reason))throw new ArgumentException("Explicit abort reason required","reason");
            if(!openAttempted){pending=null;LastIssue=reason;State=ViveRecordingCoordinatorState.Aborted;return;}
            pending=null;LastIssue=reason;abortAttempted=true;
            try{abortAcknowledged=port.AbortUnclean(reason);State=abortAcknowledged?ViveRecordingCoordinatorState.Aborted:ViveRecordingCoordinatorState.Faulted;}
            catch(Exception e){abortAcknowledged=false;State=ViveRecordingCoordinatorState.Faulted;LastIssue=reason+";abort_uncertain:"+e.GetType().Name;}
        }
        public void Dispose()
        {RequireOwner();if(Active)Abort("coordinator_disposed_before_clean_segment_close");}
    }
}
