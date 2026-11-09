using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using AvatarStroop.Core;

namespace AvatarStroop.Runtime
{
    // Development-only status observation. Never retains gaze, opens a file,
    // starts a device, changes a task or grants research/recording consent.
    public sealed class VivePracticePreviewCoordinator
    {
        readonly ViveDevelopmentParticipant participant;
        readonly IClock clock;
        readonly VivePracticeConsentAuthority choices;
        readonly int owner;
        readonly double maximumAge;
        readonly string clockId;
        readonly object gate=new object();
        // Preallocated before taking authority/participant locks. No gaze or
        // caller callback is retained in this status-only publication.
        sealed class PendingStatus
        {
            internal int Kind,Attempt,Sequence,NativeTime;
            internal long Cursor,Generation;
            internal double EventTime,Now,Epoch,EyeTime;
            internal ViveDevelopmentParticipant.PracticeObservationFence Fence;
        }
        PendingStatus pending;
        internal Action BeforeStatusCommitForTest {get;set;}
        VivePracticeStartBinding binding;
        long generation,lastEventSequence;
        double epochTime,lastNow=-1,lastEyeTime=-1,lastEventTime=-1;
        int lastSequence,lastNativeTime;
        bool started,ended,suspended,sourceSuspended,hasEye,eyeChosen,taskChosen;
        int attempt=1,checkpointCount,unknownIntervals;
        long checkpointRevision;
        ViveSyntheticStepTrace checkpointStep;
        ExperimentIncrementalSnapshot checkpoint;
        long accepted,headValid;
        string status="Choose eye and task observation before practice.";
        public bool IsActive {get{bool active=choices.Active;lock(gate)return !ended&&active;}}
        public bool CanObserve {get{bool active=choices.Active;lock(gate)return started&&!ended&&!suspended&&active;}}
        public bool CanResumeObservation {get{bool active=choices.Active;lock(gate)return started&&!ended&&suspended&&active;}}
        public bool CanChoose {get{bool available=!choices.Revoked&&!choices.Faulted;lock(gate)return !ended&&!started&&available;}}
        public string Status
        {
            get
            {
                string value;lock(gate)value=status;
                // A choice may be revoked immediately after confirmation.
                // Read it without holding the preview lock: choice -> preview
                // is the only nested publication order. This is a display
                // snapshot, not an authorization or a continuously live view.
                if(value=="Choices confirmed; waiting for first practice start."&&choices.Revoked)
                    return "Observation choices withdrawn; nothing is recorded.";
                return value;
            }
        }
        public long AcceptedFrameCount {get{lock(gate)return accepted;}}
        public long HeadValidFrameCount {get{lock(gate)return headValid;}}
        public int UnknownIntervals {get{lock(gate)return unknownIntervals;}}
        public bool SavesData {get{return false;}}
        public bool ResearchAdmission {get{return false;}}
        public bool CoverageProven {get{return false;}}
        public bool SourceAuthenticated {get{return false;}}
        public VivePracticePreviewCoordinator(ViveDevelopmentParticipant participant,IClock clock,double maximumAgeSeconds=.1)
        {
            if(participant==null||clock==null)throw new ArgumentNullException("participant/clock");
            if(Double.IsNaN(maximumAgeSeconds)||Double.IsInfinity(maximumAgeSeconds)||maximumAgeSeconds<=0||maximumAgeSeconds>1)
                throw new ArgumentOutOfRangeException("maximumAgeSeconds");
            this.participant=participant;this.clock=clock;maximumAge=maximumAgeSeconds;
            clockId=clock.ClockId;if(String.IsNullOrWhiteSpace(clockId))throw new ArgumentException("Clock identity required.");
            owner=Thread.CurrentThread.ManagedThreadId;
            choices=new VivePracticeConsentAuthority(participant,clock,true);
        }
        bool Owner(){return Thread.CurrentThread.ManagedThreadId==owner;}
        bool Stop(string message)
        {lock(gate){if(!ended){ended=true;status=message;}}choices.Withdraw();return false;}
        bool Suspend(string message,bool sourceUnavailable=false)
        {lock(gate){if(!ended){suspended=true;sourceSuspended=sourceUnavailable;status=message;}}return false;}
        public void SuspendUnavailable()
        {lock(gate){if(!ended&&started){suspended=true;sourceSuspended=true;status="Eye observation unavailable. Resume explicitly when ready; interval coverage is unknown. Nothing is recorded.";}}}
        public bool ConfirmEye(){return Confirm(true);}
        public bool ConfirmTask(){return Confirm(false);}
        bool Confirm(bool eye)
        {
            if(!Owner())return Stop("Observation stopped: owner thread required.");
            lock(gate){if(ended||started)return false;if(eye?eyeChosen:taskChosen)return false;}
            bool ok=eye?choices.ConfirmEye():choices.ConfirmTask();
            if(!ok)return Stop("Observation stopped: choice could not be confirmed.");
            bool active=choices.Active,revoked=choices.Revoked;
            lock(gate)
            {
                if(ended||!active&&revoked)return false;
                if(eye)eyeChosen=true;else taskChosen=true;
                status=active?"Choices confirmed; waiting for first practice start.":"Choose both eye and task observation.";
                return true;
            }
        }
        // Call synchronously immediately after OwnerBeginPractice, before any
        // other task action. This method never repeats or starts that action.
        public bool OnPracticeStarted()
        {
            if(!Owner())return Stop("Observation stopped: owner thread required.");
            lock(gate){if(ended||started)return false;}
            if(!choices.Active)return Stop("Observation stopped: both choices were not confirmed before practice; nothing was observed.");
            ViveDevelopmentParticipant.PracticeObservationFence fence=null;
            try
            {
                binding=new VivePracticeStartBinding(participant,clock,choices,true);
                VivePracticeStartBinding.Ticket ticket;VivePracticeStartBatch batch;
                if(!binding.TryAcquire(1,4096,out ticket)||!binding.TryTake(ticket,out batch)||batch==null||batch.Events.Count!=3)
                    return Stop("Observation stopped: first practice start was not established; coverage is unknown.");
                var item=batch.Events[0];double now=clock.Now;long choiceGeneration=choices.Generation;
                if(clock.ClockId!=clockId||!Finite(now)||now<item.Time||!choices.IsCurrent(choiceGeneration,participant,clock))
                    return Stop("Observation stopped: start or clock state changed.");
                if(!participant.TryAcquirePracticeObservationFence(clock,out fence)||fence==null||fence.Snapshot==null||fence.Snapshot.Task==null||
                    fence.StepTrace==null||fence.StepTrace.Count<1||fence.MutationStepsObserved!=fence.StepTrace.Count||
                    fence.Revision!=batch.Revision||fence.Snapshot.Task.Phase!=Phase.Practice||
                    fence.Snapshot.Scalars.PracticeAttempt!=1||fence.Snapshot.Scalars.EventCount!=batch.Events[2].Sequence+1||
                    !Object.ReferenceEquals(fence.StepTrace[fence.StepTrace.Count-1].After,fence.Snapshot))
                    return Stop("Observation stopped: start checkpoint unavailable; coverage is unknown.");
                if(!participant.TryConsumePracticeObservationFence(fence)){fence=null;return Stop("Observation stopped: start checkpoint changed.");}
                var verified=fence;fence=null;
                return CommitStatus(new PendingStatus{Kind=0,Fence=verified,Generation=choiceGeneration,Epoch=item.Time,
                    Cursor=batch.Events[batch.Events.Count-1].Sequence,EventTime=batch.Events[batch.Events.Count-1].Time,Now=now});
            }
            catch(Exception){return Stop("Observation stopped: start state unavailable; coverage is unknown.");}
            finally{if(fence!=null)participant.AbandonPracticeObservationFence(fence);if(binding!=null)binding.Close();}
        }
        static bool Finite(double x){return !Double.IsNaN(x)&&!Double.IsInfinity(x)&&x>=0;}
        // Bounded proof of the same participant's immutable mutation lineage and
        // journal positions. This never changes the task or opens a recording.
        bool TryTrace(ViveDevelopmentParticipant.PracticeObservationFence fence,bool recovery,bool allowSamePhase,out long eventCursor,out int nextAttempt,out double eventTime)
        {
            eventCursor=lastEventSequence;nextAttempt=attempt;eventTime=lastEventTime;
            if(fence==null||checkpoint==null||checkpointStep==null||checkpointCount<1||fence.StepTrace==null||
                fence.StepTrace.Count<checkpointCount||fence.StepTrace.Count-checkpointCount>128||
                fence.MutationStepsObserved!=fence.StepTrace.Count||!Object.ReferenceEquals(fence.StepTrace[checkpointCount-1],checkpointStep)||
                !Object.ReferenceEquals(checkpointStep.After,checkpoint)||(long)checkpoint.Scalars.EventCount!=lastEventSequence+1||
                fence.Snapshot==null||fence.Snapshot.Task==null||fence.StepTrace.Count==0||
                !Object.ReferenceEquals(fence.StepTrace[fence.StepTrace.Count-1].After,fence.Snapshot))return false;
            int eventCount=fence.Snapshot.Scalars.EventCount-checkpoint.Scalars.EventCount;
            if(eventCount<0||eventCount>128)return false;
            IReadOnlyList<JournalEvent> events=eventCount==0?new JournalEvent[0]:participant.ReadEventsAfter(lastEventSequence,eventCount+1);
            if(events==null||events.Count!=eventCount)return false;
            var previous=checkpoint;long revision=checkpointRevision;int stepIndex=checkpointStep.Index,index=0;
            bool pause=false,pausedPhase=false,resume=false,resumedPhase=false,completed=false,retryPhase=false,retryStart=false,retryMoved=false;
            double prior=lastEventTime;
            for(int i=checkpointCount;i<fence.StepTrace.Count;i++)
            {
                var step=fence.StepTrace[i];
                if(step==null||step.Before==null||step.After==null||step.After.Task==null||!Object.ReferenceEquals(step.Before,previous)||
                    !((step.Revision==revision&&step.Index==stepIndex+1)||(revision<Int64.MaxValue&&step.Revision==revision+1&&step.Index==0))||
                    step.After.Task.SessionId!=participant.SessionId||step.After.Task.BlockIndex!=-1||step.After.Task.Condition.HasValue||
                    step.After.Selection.HasValue||step.After.Scalars.PracticeAttempt<attempt||step.After.Scalars.PracticeAttempt>attempt+1||
                    step.Before.Scalars.EventCount<0||step.After.Scalars.EventCount<step.Before.Scalars.EventCount||
                    step.After.Scalars.EventCount-step.Before.Scalars.EventCount>1)return false;
                if(step.After.Scalars.EventCount!=step.Before.Scalars.EventCount)
                {
                    if(index>=events.Count)return false;var item=events[index++];
                    if(item==null||item.Sequence!=eventCursor+1||item.Sequence!=step.Before.Scalars.EventCount||
                        item.SessionId!=participant.SessionId||item.ClockId!=clockId||item.BlockIndex!=-1||!Finite(item.Time)||item.Time<prior)return false;
                    prior=item.Time;eventTime=item.Time;eventCursor=item.Sequence;
                    if(item.Kind=="explicit_pause")
                    {if(pause||resume||completed||retryStart||(step.Before.Task.Phase!=Phase.Practice&&step.Before.Task.Phase!=Phase.PracticeCorrection))return false;pause=true;}
                    else if(item.Kind=="explicit_resume")
                    {if(!pause||!pausedPhase||resume||step.Before.Task.Phase!=Phase.Paused)return false;resume=true;}
                    else if(item.Kind=="practice_completed")
                    {
                        string a,planned,correctText;int correct;
                        if(pause||completed||attempt!=1||item.Fields==null||!item.Fields.TryGetValue("attempt",out a)||a!="1"||
                            !item.Fields.TryGetValue("planned",out planned)||planned!="8"||!item.Fields.TryGetValue("correct_first",out correctText)||
                            !Int32.TryParse(correctText,NumberStyles.None,CultureInfo.InvariantCulture,out correct)||correct<0||correct>=6)return false;
                        completed=true;
                    }
                    else if(item.Kind=="practice_started")
                    {
                        string a;if(!completed||!retryPhase||retryStart||item.Fields==null||!item.Fields.TryGetValue("attempt",out a)||a!="2"||
                            step.Before.Task.Phase!=Phase.PracticeRetry)return false;retryStart=true;
                    }
                    else if(item.Kind=="phase_changed")
                    {
                        string from,to;if(item.Fields==null||!item.Fields.TryGetValue("from",out from)||!item.Fields.TryGetValue("to",out to))return false;
                        if(to=="Paused"){if(!pause||pausedPhase||resume)return false;pausedPhase=true;}
                        if(from=="Paused"){if(!resume||resumedPhase)return false;resumedPhase=true;}
                        if(to=="PracticeRetry"){if(!completed||retryPhase||retryStart)return false;retryPhase=true;}
                        if(from=="PracticeRetry"&&to=="Practice"){if(!retryStart||retryMoved)return false;retryMoved=true;}
                    }
                }
                previous=step.After;revision=step.Revision;stepIndex=step.Index;
            }
            if(index!=events.Count||!Object.ReferenceEquals(previous,fence.Snapshot)||fence.Revision!=revision||
                (long)fence.Snapshot.Scalars.EventCount!=eventCursor+1)return false;
            bool interrupted=pause||resume||completed||retryPhase||retryStart||retryMoved;
            if(!recovery)return !interrupted&&fence.Snapshot.Scalars.PracticeAttempt==attempt;
            if(fence.Snapshot.Task.Phase!=Phase.Practice&&fence.Snapshot.Task.Phase!=Phase.PracticeCorrection)return false;
            if(allowSamePhase&&!interrupted&&fence.Snapshot.Scalars.PracticeAttempt==attempt)return true;
            if(pause&&pausedPhase&&resume&&resumedPhase&&!completed&&!retryStart&&fence.Snapshot.Scalars.PracticeAttempt==attempt)return true;
            if(!pause&&completed&&retryPhase&&retryStart&&retryMoved&&attempt==1&&fence.Snapshot.Scalars.PracticeAttempt==2)
            {nextAttempt=2;return true;}
            return false;
        }
        void SaveCheckpoint(ViveDevelopmentParticipant.PracticeObservationFence fence,long cursor,double eventTime,double now)
        {
            checkpoint=fence.Snapshot;checkpointCount=fence.StepTrace.Count;checkpointStep=fence.StepTrace[checkpointCount-1];
            checkpointRevision=fence.Revision;lastEventSequence=cursor;lastEventTime=eventTime;lastNow=now;
        }
        bool CommitStatus(PendingStatus candidate)
        {
            lock(gate){if(ended||pending!=null)return false;pending=candidate;}
            try
            {
                var hook=BeforeStatusCommitForTest;if(hook!=null)hook();
                if(!choices.TryCommitPracticePreview(this,candidate.Fence,candidate.Generation))
                    return Stop("Observation stopped: task or choices changed during status publication; coverage is unknown.");
                return true;
            }
            finally{lock(gate){if(Object.ReferenceEquals(pending,candidate))pending=null;}}
        }
        // Only the sealed authority and participant call this while holding
        // choice -> participant locks. No clock, device, callback or I/O.
        internal bool PublishUnderParticipantLock(VivePracticeConsentAuthority authority,ViveDevelopmentParticipant task,
            ViveDevelopmentParticipant.PracticeObservationFence fence,long expectedGeneration)
        {
            if(!Object.ReferenceEquals(authority,choices)||!Object.ReferenceEquals(task,participant)||
                !authority.StateLockHeld||!task.ObservationFenceLockHeld||!Owner())return false;
            lock(gate)
            {
                var p=pending;if(ended||p==null||!Object.ReferenceEquals(p.Fence,fence)||p.Generation!=expectedGeneration)return false;
                if(p.Kind==0)
                {
                    if(started)return false;generation=p.Generation;epochTime=p.Epoch;
                    SaveCheckpoint(fence,p.Cursor,p.EventTime,p.Now);started=true;
                    status="First practice observed; waiting for a fresh frame. Nothing is recorded.";return true;
                }
                if(!started)return false;
                if(p.Kind==1)
                {
                    if(!suspended||unknownIntervals==Int32.MaxValue)return false;
                    SaveCheckpoint(fence,p.Cursor,p.EventTime,p.Now);attempt=p.Attempt;epochTime=p.Now;unknownIntervals++;
                    hasEye=false;lastSequence=0;lastNativeTime=0;lastEyeTime=-1;suspended=false;sourceSuspended=false;
                    status="Observation resumed in a new epoch; the intervening interval is unknown. Nothing is recorded.";return true;
                }
                if(suspended)return false;
                if(p.Kind<2||p.Kind>4||(p.Kind==4&&(accepted==Int64.MaxValue||headValid==Int64.MaxValue)))return false;
                SaveCheckpoint(fence,p.Cursor,p.EventTime,p.Now);
                if(p.Kind==2){status="Waiting for a frame newer than the observation boundary. Nothing is recorded.";return true;}
                if(p.Kind==3){status="Waiting for a new eye frame. Nothing is recorded.";return true;}
                lastSequence=p.Sequence;lastNativeTime=p.NativeTime;lastEyeTime=p.EyeTime;hasEye=true;accepted++;headValid++;
                status="A fresh eye and head candidate was observed; nothing is recorded.";return true;
            }
        }
        // The task must already have resumed or started its second attempt via
        // its normal owner/participant controls. This button only starts a new
        // status-observation epoch and never advances or repeats a task trial.
        public bool ResumeObservation()
        {
            if(!Owner())return Stop("Observation stopped: owner thread required.");
            if(!choices.Active)return false;
            lock(gate){if(ended||!started||!suspended)return false;}
            if(participant.Phase!=Phase.Practice&&participant.Phase!=Phase.PracticeCorrection)return false;
            ViveDevelopmentParticipant.PracticeObservationFence fence=null;
            try
            {
                if(!choices.IsCurrent(generation,participant,clock)||!participant.TryAcquirePracticeObservationFence(clock,out fence)||fence==null)
                    return Stop("Observation stopped: current participant or choices unavailable.");
                long cursor;int nextAttempt;double eventTime;
                if(!TryTrace(fence,true,sourceSuspended,out cursor,out nextAttempt,out eventTime))
                    return Stop("Observation stopped: bounded recovery evidence is unavailable; coverage remains unknown.");
                double now=clock.Now;
                if(clock.ClockId!=clockId||!Finite(now)||now<lastNow||eventTime>now||!choices.IsCurrent(generation,participant,clock))
                    return Stop("Observation stopped: recovery clock or choices changed.");
                var verified=fence;if(!participant.TryConsumePracticeObservationFence(fence)){fence=null;return Stop("Observation stopped: recovery context changed.");}
                fence=null;
                return CommitStatus(new PendingStatus{Kind=1,Fence=verified,Generation=generation,Cursor=cursor,EventTime=eventTime,Now=now,Attempt=nextAttempt});
            }
            catch(Exception){return Stop("Observation stopped: recovery unavailable; coverage remains unknown.");}
            finally{if(fence!=null)participant.AbandonPracticeObservationFence(fence);}
        }
        // Supplied only during the rig's frame scope. The object and its gaze
        // fields are not retained. Counters are diagnostic, not accuracy tests.
        public bool ObserveFrame(ViveProEyePoll poll)
        {
            if(!Owner())return Stop("Observation stopped: owner thread required.");
            if(!choices.Active)return false;
            lock(gate){if(ended||suspended||!started)return false;}
            ViveDevelopmentParticipant.PracticeObservationFence fence=null;
            try
            {
                if(participant.Phase==Phase.Paused||participant.Phase==Phase.PracticeRetry)
                    return Suspend("Observation paused; resume the task first, then explicitly resume observation. Interval coverage is unknown.");
                if(!choices.IsCurrent(generation,participant,clock)||!participant.TryAcquirePracticeObservationFence(clock,out fence)||fence==null)
                    return Stop("Observation stopped: participant or choice state unavailable.");
                var snapshot=fence.Snapshot;
                if(snapshot!=null&&snapshot.Task!=null&&snapshot.Task.BlockIndex==-1&&attempt==1&&snapshot.Scalars.PracticeAttempt==2&&
                    (snapshot.Task.Phase==Phase.Practice||snapshot.Task.Phase==Phase.PracticeCorrection))
                    return Suspend("Observation paused: practice attempt changed. Explicit recovery requires verified task history; coverage is unknown.");
                if(snapshot==null||snapshot.Task==null||snapshot.Task.BlockIndex!=-1||snapshot.Scalars.PracticeAttempt!=attempt||
                    (snapshot.Task.Phase!=Phase.Practice&&snapshot.Task.Phase!=Phase.PracticeCorrection))
                    return Stop("Observation stopped: the practice context ended; coverage is unknown.");
                long cursor;int nextAttempt;double eventTime;
                if(!TryTrace(fence,false,false,out cursor,out nextAttempt,out eventTime))
                    return Suspend("Observation paused: task history changed or was interrupted. Explicit review is required; coverage is unknown.");
                double now=clock.Now;
                if(clock.ClockId!=clockId||!Finite(now)||now<lastNow||eventTime>now||!choices.IsCurrent(generation,participant,clock))
                    return Stop("Observation stopped: clock or choices changed.");
                if(poll==null||poll.Eye==null)return Suspend("Eye frame unavailable. Resume observation explicitly when ready; coverage is unknown.",true);
                var eye=poll.Eye;
                if(!Finite(eye.ObservedLocalSeconds)||eye.ObservedLocalSeconds>now)
                    return Suspend("Eye frame time is invalid. Resume observation explicitly when ready; coverage is unknown.",true);
                // A cached or repeated eye publication cannot establish current
                // runtime, device or head health.
                Vec3 unit;bool eyeOk=poll.Runtime==ViveEyeRuntimeState.Working&&poll.Calibration==ViveEyeCalibrationState.ReportedReady&&
                    poll.EyeDeviceConnected&&poll.NativeResultCode.HasValue&&poll.NativeResultCode.Value==0&&!eye.NoUser&&eye.Combined!=null&&
                    (eye.Combined.ValidityMask&2UL)!=0&&eye.Combined.DirectionReportedValid&&ViveProEyeSdkDirection.TryGetUnitDirection(eye.Combined.Direction,out unit);
                if(!eyeOk)return Suspend("Eye validity unavailable or invalid. Resume observation explicitly when ready; coverage is unknown.",true);
                bool headHealthy=poll.Head!=null&&poll.Head.DeviceConnected&&poll.Head.RotationReportedValid&&
                    Finite(poll.Head.ObservedLocalSeconds)&&poll.Head.ObservedLocalSeconds<=now&&
                    now-poll.Head.ObservedLocalSeconds<=maximumAge&&poll.Head.RawTrackingRotation.IsUnit;
                if(!headHealthy)return Suspend("Head validity unavailable or invalid. Resume observation explicitly when ready; coverage is unknown.",true);
                // The rig may still hold the last pre-start hardware frame. It
                // is never counted or retimed; wait for a post-start publication.
                if(!hasEye&&(eye.ObservedLocalSeconds<=epochTime||eye.ObservedLocalSeconds<=choices.EnabledAt))
                {
                    if(now-eye.ObservedLocalSeconds>maximumAge)
                        return Suspend("Eye frame is stale. Resume observation explicitly when ready; coverage is unknown.",true);
                    var verified=fence;if(!participant.TryConsumePracticeObservationFence(fence)){fence=null;return Stop("Observation stopped: task changed during frame.");}
                    fence=null;CommitStatus(new PendingStatus{Kind=2,Fence=verified,Generation=generation,Cursor=cursor,EventTime=eventTime,Now=now});return false;
                }
                if(eye.ObservedLocalSeconds<=epochTime||eye.ObservedLocalSeconds<=choices.EnabledAt||now-eye.ObservedLocalSeconds>maximumAge)
                    return Suspend("Eye frame is stale or predates the boundary. Resume observation explicitly when ready; coverage is unknown.",true);
                bool headOk=poll.Head.ObservedLocalSeconds>epochTime;
                if(!headOk)return Suspend("Head validity unavailable or invalid. Resume observation explicitly when ready; coverage is unknown.",true);
                if(hasEye&&eye.FrameSequence==lastSequence&&eye.Timestamp==lastNativeTime&&eye.ObservedLocalSeconds==lastEyeTime)
                {
                    var verified=fence;if(!participant.TryConsumePracticeObservationFence(fence)){fence=null;return Stop("Observation stopped: task changed during frame.");}
                    fence=null;CommitStatus(new PendingStatus{Kind=3,Fence=verified,Generation=generation,Cursor=cursor,EventTime=eventTime,Now=now});return false;
                }
                if(hasEye&&(eye.FrameSequence<=lastSequence||eye.Timestamp<=lastNativeTime||eye.ObservedLocalSeconds<=lastEyeTime))
                    return Suspend("Eye frame is out of order. Resume observation explicitly when ready; coverage is unknown.",true);
                var completedFence=fence;if(!participant.TryConsumePracticeObservationFence(fence)){fence=null;return Stop("Observation stopped: task changed during frame.");}
                fence=null;
                return CommitStatus(new PendingStatus{Kind=4,Fence=completedFence,Generation=generation,Cursor=cursor,EventTime=eventTime,
                    Now=now,Sequence=eye.FrameSequence,NativeTime=eye.Timestamp,EyeTime=eye.ObservedLocalSeconds});
            }
            catch(Exception){return Stop("Observation stopped: frame state unavailable; coverage is unknown.");}
            finally{if(fence!=null)participant.AbandonPracticeObservationFence(fence);}
        }
        public void Withdraw()
        {
            lock(gate)
            {
                ended=true;accepted=0;headValid=0;unknownIntervals=0;hasEye=false;lastSequence=0;lastNativeTime=0;lastEyeTime=-1;
                checkpoint=null;checkpointStep=null;checkpointCount=0;checkpointRevision=0;
                status="Observation withdrawn; nothing was recorded.";
            }
            choices.Withdraw();
        }
    }
}
