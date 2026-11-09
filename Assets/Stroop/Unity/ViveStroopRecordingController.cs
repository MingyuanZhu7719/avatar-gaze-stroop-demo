#if STROOP_XR_PREVIEW
using System;
using System.IO;
using System.Threading;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Unity
{
    // The VIVE rig owns the upstream worker. Consumers can only read the
    // immutable observation borrowed for one owner-thread update; this bridge
    // never polls the worker for consumers and clears its snapshot after use.
    internal sealed class ViveStroopEyePump : IViveProEyeProvider
    {
        readonly IViveProEyeProvider upstream;
        readonly int owner;
        ViveProEyePoll frame;
        bool active,closed;
        internal ViveStroopEyePump(IViveProEyeProvider upstream)
        {this.upstream=upstream??throw new ArgumentNullException("upstream");owner=Thread.CurrentThread.ManagedThreadId;}
        void RequireOwner()
        {if(Thread.CurrentThread.ManagedThreadId!=owner)throw new InvalidOperationException("Eye pump owner thread required.");}
        internal void BeginFrame()
        {
            RequireOwner();if(closed||active)throw new InvalidOperationException("Eye frame cannot begin.");
            frame=null;
            var captured=upstream.Poll();
            frame=captured;active=true;
        }
        // Named raw safety path only. Busy or invalid worker status never
        // leaves a cached previous frame or marks a frame active.
        internal bool TryBeginRecordingFrame(ViveProEyeWorker worker)
        {
            RequireOwner();if(closed||active||worker==null||!Object.ReferenceEquals(upstream,worker))return false;
            frame=null;ViveProEyePoll captured;
            if(!worker.TryPollForRecording(out captured)||captured==null)return false;
            frame=captured;active=true;return true;
        }
        public ViveProEyePoll Poll()
        {RequireOwner();return !closed&&active?frame:null;}
        internal bool HeadReady
        {
            get
            {
                RequireOwner();var current=!closed&&active?frame:null;
                return current!=null&&current.Head!=null&&current.Head.DeviceConnected&&current.Head.RotationReportedValid;
            }
        }
        internal bool Peek(out int sequence,out int timestamp,out double observed)
        {
            RequireOwner();sequence=0;timestamp=0;observed=0;
            var current=!closed&&active?frame:null;
            if(current==null||current.Eye==null||!current.NativeResultCode.HasValue||current.NativeResultCode.Value!=0)return false;
            var eye=current.Eye;
            if(Double.IsNaN(eye.ObservedLocalSeconds)||Double.IsInfinity(eye.ObservedLocalSeconds)||eye.ObservedLocalSeconds<0)return false;
            sequence=eye.FrameSequence;timestamp=eye.Timestamp;observed=eye.ObservedLocalSeconds;return true;
        }
        internal void EndFrame(){RequireOwner();frame=null;active=false;}
        internal void Close(){RequireOwner();frame=null;active=false;closed=true;}
    }

    // Development-only lifecycle. The bootstrap must route EVERY task mutation
    // through RunTaskMutation (including Tick, trackpad, auxiliary/resume and
    // render witness) and route pause/withdraw through the safety methods. The
    // rig must not poll the worker or another eye source while this owns it.
    // Failure to install those hooks is not a working or safe integration.
    public sealed class ViveStroopRecordingController : IDisposable
    {
        internal delegate IViveRecordingSegmentPort PortFactory(string path,ViveParticipantEyeSource source,IClock clock,
            ViveDevelopmentStreamIdentity identity,ViveDevelopmentParticipant task);
        internal delegate bool EyeMetadata(out int frameSequence,out int nativeTimestamp,out double observedLocalSeconds);
        readonly ViveDevelopmentParticipant task;
        readonly IViveProEyeProvider worker;
        readonly IClock clock;
        readonly string directory,participantId,deviceId;
        readonly PortFactory portFactory;
        readonly Func<bool> workerReady;
        readonly EyeMetadata peekEye;
        readonly int ownerThread;
        ViveParticipantEyeSource source;
        ViveRecordingSessionCoordinator coordinator;
        int block=-1,segment=-1;
        long eventCursor=-1;
        bool disposed,faulted,recoveryPending,hasPreviousEye,waitingForFreshEye,unrecordedTransition;
        int previousFrame,previousNativeTimestamp,candidateFrame,candidateNativeTimestamp;
        readonly double enabledAt;
        double previousEyeTime,epochBoundary,candidateEyeTime,lastClockNow;
        public bool Faulted {get{return faulted;}}
        public bool RecoveryPending {get{return recoveryPending;}}
        public bool WaitingForFreshEye {get{return waitingForFreshEye;}}
        public bool UnrecordedTransition {get{return unrecordedTransition;}}
        public string Status {get;private set;}
        // Diagnostic state only, from the same read already processed here.
        // No additional poll and no gaze sample is returned or retained.
        public bool LastEyeUsable {get;private set;}
        public bool SegmentOpen {get{return coordinator!=null;}}
        public bool ResearchAdmission {get{return false;}}
        public bool FullSessionCoverageProven {get{return false;}}
        // Legacy owner-only choices do not establish participant recording
        // authorization or a protected destination. Keep the live file path
        // closed independently of the UI until those requirements are met.
        internal static bool LegacyLiveRecordingAllowed {get{return false;}}

        internal static ViveStroopRecordingController Enable(ViveDevelopmentParticipant task,IViveProEyeProvider frameProvider,IClock clock,
            string existingAbsoluteDirectory,bool explicitEyeOptIn,bool explicitTaskOptIn,Func<bool> workerReady,EyeMetadata peekEye)
        {
            if(!LegacyLiveRecordingAllowed)
                throw new InvalidOperationException("Live VIVE recording is unavailable until participant authorization and protected storage are verified.");
            if(!explicitEyeOptIn||!explicitTaskOptIn)throw new InvalidOperationException("Both eye and task recording choices are required.");
            if(frameProvider==null||clock==null||workerReady==null||peekEye==null)throw new ArgumentException("A rig-owned frame provider and task clock are required.");
            return new ViveStroopRecordingController(task,frameProvider,clock,existingAbsoluteDirectory,explicitEyeOptIn,explicitTaskOptIn,
                Directory.Exists,workerReady,peekEye,
                (path,source,c,id,t)=>new ViveRecordingSinkPort(path,source,c,id,t,ViveDevelopmentDataSource.RealVive,true,true));
        }
        // Test-only injection is internal: no test opens a device or a file.
        internal ViveStroopRecordingController(ViveDevelopmentParticipant task,IViveProEyeProvider worker,IClock clock,string directory,
            bool eyeOptIn,bool taskOptIn,Func<string,bool> directoryExists,Func<bool> workerReady,EyeMetadata peekEye,PortFactory portFactory)
        {
            if(!eyeOptIn||!taskOptIn)throw new InvalidOperationException("Both eye and task recording choices are required.");
            if(task==null||worker==null||clock==null||directoryExists==null||workerReady==null||peekEye==null||portFactory==null)throw new ArgumentNullException("Recording dependencies");
            if(task.HasNamedOption)throw new InvalidOperationException("Legacy recording cannot represent named-option provenance.");
            if(!task.UsesEventClock(clock)||!AnonymousSession(task.SessionId))throw new ArgumentException("An anonymous development task and its identical clock are required.");
            if(task.Phase==Phase.Ended||task.BlockIndex>0||(task.BlockIndex==0&&task.Phase!=Phase.BlockPreparation&&task.Phase!=Phase.BlockReady))
                throw new InvalidOperationException("Enable recording before the first block countdown.");
            string probe=Path.Combine(directory??String.Empty,"vdev-path-probe.tmp");
            ViveDevelopmentJournalSink.ValidateAbsolutePath(probe);
            if(!directoryExists(directory))throw new DirectoryNotFoundException("An existing recording directory is required.");
            double recordingEnabledAt=clock.Now;
            if(Double.IsNaN(recordingEnabledAt)||Double.IsInfinity(recordingEnabledAt)||recordingEnabledAt<0)throw new ArgumentException("Valid shared clock required.");
            this.task=task;this.worker=worker;this.clock=clock;this.directory=directory;this.workerReady=workerReady;this.peekEye=peekEye;this.portFactory=portFactory;
            enabledAt=recordingEnabledAt;
            lastClockNow=recordingEnabledAt;
            participantId="anonymous-"+Guid.NewGuid().ToString("N");deviceId="vive-ephemeral-device-"+Guid.NewGuid().ToString("N");
            ownerThread=Thread.CurrentThread.ManagedThreadId;Status="Recording enabled for a development task; no study admission.";
        }
        static bool AnonymousSession(string value)
        {
            const string prefix="vive-development-";
            if(value==null||!value.StartsWith(prefix,StringComparison.Ordinal)||value.Length!=prefix.Length+32)return false;
            for(int i=prefix.Length;i<value.Length;i++)if(!Uri.IsHexDigit(value[i]))return false;
            return true;
        }
        void RequireOwner()
        {if(Thread.CurrentThread.ManagedThreadId!=ownerThread)throw new InvalidOperationException("Recording requires its owner thread.");}
        bool IsCurrent {get{return coordinator!=null&&task.Phase!=Phase.Ended&&task.BlockIndex==block&&task.Segment==segment;}}
        bool NeedsEpoch {get{return task.Phase!=Phase.Ended&&task.BlockIndex>=0&&task.LockedControl==ControlSelection.Automatic;}}
        double CheckClock()
        {
            double now=clock.Now;
            if(Double.IsNaN(now)||Double.IsInfinity(now)||now<lastClockNow)throw new InvalidOperationException("Shared observation clock failed or moved backwards.");
            lastClockNow=now;return now;
        }

        // The only ordinary task mutation entry point. It guards retained-source
        // Tick and the initial owner BeginBlock before either can start a timer;
        // after Resume/Ready it opens the new segment synchronously in the same
        // call, before the next task action or render witness can run.
        public bool RunTaskMutation(Action action)
        {
            RequireOwner();if(disposed||faulted||recoveryPending||action==null)return false;
            try
            {
                if(!SynchronizeEpoch())return false;
                action();
                return SynchronizeEpoch();
            }
            catch(Exception e){Fail("task_mutation:"+e.GetType().Name);return false;}
        }
        public void Pause()
        {RequireOwner();if(disposed)return;try{task.OwnerPause(Actor.Owner);if(!faulted)SynchronizeEpoch();}catch(Exception e){Fail("pause:"+e.GetType().Name);}}
        // Physical participant safety input must preserve the actual actor and
        // follow the same event-drain policy as the owner pause. This remains
        // available during recovery and after an observation fault.
        public void ParticipantPause()
        {RequireOwner();if(disposed)return;try{task.ParticipantPause();if(!faulted)SynchronizeEpoch();}catch(Exception e){Fail("participant_pause:"+e.GetType().Name);}}
        public void Withdraw()
        {
            RequireOwner();if(disposed)return;
            // Revoke the recording path before changing task state: in
            // particular, do not drain events or append a clean-close marker
            // after the participant has requested withdrawal.
            disposed=true;faulted=true;recoveryPending=false;LastEyeUsable=false;unrecordedTransition=true;
            bool taskWithdrawn=false,hadSegment=coordinator!=null,abortAcknowledged=!hadSegment;
            try{task.ParticipantWithdraw();taskWithdrawn=task.Phase==Phase.Ended&&task.EndReason==EndReason.Withdrawn;}
            catch(Exception){}
            // Abort only releases the stream and marks the prefix unclean. A
            // FileStream dispose or the OS may still write earlier buffered
            // bytes; this is not an instantaneous cancellation guarantee.
            if(coordinator!=null)
            {
                try{coordinator.Abort("participant_withdrawal");abortAcknowledged=coordinator.AbortAcknowledged;}
                catch(Exception){abortAcknowledged=false;}
            }
            Status=(taskWithdrawn?"Task withdrawn. ":"Withdrawal outcome is not confirmed. ")+
                (!hadSegment?"No active segment. ":abortAcknowledged?"Recording stopped with an unclean prefix. ":"Recording cleanup is not confirmed. ")+
                "Withdrawal event is not written to this journal; earlier buffered data may still reach storage.";
        }
        // Called by the owner update loop while a new segment is waiting for a
        // fresh worker snapshot. It does not advance or resume the task.
        public bool Refresh()
        {RequireOwner();if(disposed||faulted)return false;try{return SynchronizeEpoch();}catch(Exception e){Fail("refresh:"+e.GetType().Name);return false;}}

        bool SynchronizeEpoch()
        {
            if(faulted||disposed)return false;
            CheckClock();
            if(waitingForFreshEye&&coordinator==null&&task.Phase==Phase.Ended)
            {unrecordedTransition=true;Fail("segment_transition_events_not_recorded");return false;}
            if(coordinator!=null)
            {
                if(!Drain()){Fail("task_event_drain_failed");return false;}
                if(!IsCurrent)
                {
                    if(!coordinator.CloseAfterSegment()){Fail("segment_close_failed:"+coordinator.LastIssue);return false;}
                    coordinator=null;source=null;block=segment=-1;eventCursor=-1;LastEyeUsable=false;
                }
            }
            if(NeedsEpoch&&coordinator==null)
            {
                if(!OpenEpoch())return false;
                // A countdown must not start with an uninitialized or unusable
                // eye source. This consumes exactly one source read.
                if(!PollEye()||!LastEyeUsable){if(!faulted)Fail("initial_eye_not_ready");return false;}
            }
            return true;
        }
        bool OpenEpoch()
        {
            try
            {
                if(!workerReady())throw new InvalidOperationException("Eye worker is not ready.");
                block=task.BlockIndex;segment=task.Segment;
                var first=FindFirstEvent();epochBoundary=first.Time;
                bool haveCandidate=peekEye(out candidateFrame,out candidateNativeTimestamp,out candidateEyeTime);
                if(haveCandidate&&(Double.IsNaN(candidateEyeTime)||Double.IsInfinity(candidateEyeTime)||candidateEyeTime>CheckClock()))
                    throw new InvalidOperationException("Eye observation timestamp is invalid or in the future.");
                if(!haveCandidate||candidateEyeTime<=epochBoundary||candidateEyeTime<=enabledAt||
                    (hasPreviousEye&&(candidateFrame<=previousFrame||candidateNativeTimestamp<=previousNativeTimestamp||candidateEyeTime<=previousEyeTime)))
                {
                    waitingForFreshEye=true;LastEyeUsable=false;Status="Waiting for an eye observation newer than the segment boundary.";
                    if(task.Phase==Phase.Countdown||task.Phase==Phase.MainTask)task.OwnerPause(Actor.Owner);
                    return false;
                }
                string epoch=Guid.NewGuid().ToString("N"),stream="participant/vive/"+epoch;
                source=ViveParticipantEyeSource.CreateForAutomatic(task.SessionId,block,segment,stream,deviceId,clock,worker,new ViveProEyeRehearsalCoordinates());
                var identity=new ViveDevelopmentStreamIdentity(task.SessionId,participantId,null,ViveDevelopmentMeasurementRole.Participant,
                    stream,deviceId,clock.ClockId,source.MappingVersion,ViveDevelopmentOperatorStatus.NotApplicable);
                string path=Path.Combine(directory,"vdev-"+epoch+"-b"+block+"-s"+segment+".vdev");
                IViveRecordingSegmentPort port=portFactory(path,source,clock,identity,task);
                coordinator=new ViveRecordingSessionCoordinator(task,source,port,clock,true,true,true);
                eventCursor=first.Sequence-1;
                if(!coordinator.BeginTaskEvents(first.Sequence,true))throw new InvalidOperationException("Segment open or event cursor failed.");
                if(!Drain())throw new InvalidOperationException("Initial event drain failed.");
                waitingForFreshEye=false;
                Status="One development block segment is being recorded. Initial coverage is unknown.";return true;
            }
            catch(Exception e){Fail("segment_open:"+e.GetType().Name);return false;}
        }
        JournalEvent FindFirstEvent()
        {
            long cursor=-1;
            while(true)
            {
                var events=task.ReadEventsAfter(cursor,4096);
                foreach(var item in events){if(item.BlockIndex==block&&item.Segment==segment)return item;cursor=item.Sequence;}
                if(events.Count<4096)throw new InvalidOperationException("No authoritative segment boundary.");
            }
        }
        bool Drain()
        {
            while(true)
            {
                var events=task.ReadEventsAfter(eventCursor,4096);
                foreach(var item in events)
                {
                    if(item.BlockIndex!=block||item.Segment!=segment)return true;
                    if(!coordinator.RecordTaskEvent(item))return false;
                    eventCursor=item.Sequence;
                }
                if(events.Count<4096)return true;
            }
        }
        public bool PollEye()
        {
            RequireOwner();if(disposed||faulted||coordinator==null||!IsCurrent)return false;
            try
            {
                CheckClock();
                if(!workerReady()){Fail("worker_unavailable");return false;}
                ViveParticipantEyeObservation observation;
                bool available=source.TryRead(out observation);
                if(available)
                {
                    var eye=observation.RawPoll==null?null:observation.RawPoll.Eye;
                    if(eye==null||eye.ObservedLocalSeconds<=epochBoundary||eye.ObservedLocalSeconds<=enabledAt||
                        (!coordinator.HasObservation&&(eye.FrameSequence<candidateFrame||eye.Timestamp<candidateNativeTimestamp||eye.ObservedLocalSeconds<candidateEyeTime))||
                        (hasPreviousEye&&(eye.FrameSequence<=previousFrame||eye.Timestamp<=previousNativeTimestamp||eye.ObservedLocalSeconds<=previousEyeTime)))
                    {Fail("stale_or_changed_eye_snapshot");return false;}
                    var pending=coordinator.Observe(observation);
                    if(!coordinator.Commit(pending)){Fail("eye_commit_failed:"+coordinator.LastIssue);return false;}
                    previousFrame=eye.FrameSequence;previousNativeTimestamp=eye.Timestamp;previousEyeTime=eye.ObservedLocalSeconds;hasPreviousEye=true;
                    LastEyeUsable=pending.EyeUsable;
                    Status=recoveryPending?"Eye data returned; task stays paused until owner review.":"Eye observation recorded; accuracy is not verified.";
                    return LastEyeUsable;
                }
                if(source.LastReadStatus==ViveParticipantReadStatus.Duplicate)
                {if(!coordinator.AcknowledgeDuplicateRead()){Fail("duplicate_ack_failed");return false;}return true;}
                LastEyeUsable=false;
                if(!coordinator.MarkUnavailable(Reason(source.LastReadStatus)))
                {Fail("source_unavailable:"+source.LastReadStatus);return false;}
                recoveryPending=true;Status="Eye source unavailable; task paused for explicit owner review.";
                task.OwnerPause(Actor.Owner);
                if(task.Phase!=Phase.Paused&&task.Phase!=Phase.Ended){Fail("pause_not_established");return false;}
                if(!Drain()){Fail("pause_event_drain_failed");return false;}
                return false;
            }
            catch(Exception e){Fail("eye_read:"+e.GetType().Name);return false;}
        }
        static ViveDevelopmentGapReason Reason(ViveParticipantReadStatus status)
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
        // This acknowledges only the recording interruption; it does not resume
        // the task, clear a fault, establish accuracy or bypass the apparatus gate.
        public bool AcknowledgeRecoveredSource()
        {
            RequireOwner();if(disposed||faulted||!recoveryPending||!LastEyeUsable||coordinator==null||
                coordinator.State!=ViveRecordingCoordinatorState.Open||task.Phase!=Phase.Paused)return false;
            recoveryPending=false;Status="Recovery reviewed; an explicit task resume is still required.";return true;
        }
        void Fail(string reason)
        {
            if(faulted)return;faulted=true;recoveryPending=false;LastEyeUsable=false;
            if(coordinator==null&&(waitingForFreshEye||NeedsEpoch))unrecordedTransition=true;
            try{if(coordinator!=null)coordinator.Abort(reason);}catch(Exception){}
            try{if(task.Phase!=Phase.Ended&&task.Phase!=Phase.Paused)task.OwnerPause(Actor.Owner);}catch(Exception){}
            try{if(task.Phase!=Phase.Ended&&task.Phase!=Phase.Paused)task.EndTechnical(Actor.Owner,"recording_failure");}catch(Exception){}
            Status=(task.Phase==Phase.Paused||task.Phase==Phase.Ended?"Recording fault; task stopped or paused: ":
                "Recording fault; safe task stop could not be confirmed: ")+reason;
        }
        public void Dispose()
        {
            RequireOwner();if(disposed)return;
            if(waitingForFreshEye&&coordinator==null&&!faulted)
            {unrecordedTransition=true;Fail("segment_transition_events_not_recorded_on_stop");}
            if(coordinator!=null&&!faulted)
            {
                // No implicit clean close while a segment is active or a source
                // gap is open. Preserve whatever prefix was actually written.
                Fail("recording_controller_stopped");
            }
            disposed=true;LastEyeUsable=false;
        }
    }
}
#endif
