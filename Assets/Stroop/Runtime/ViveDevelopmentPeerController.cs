using System;
using AvatarStroop.Core;

namespace AvatarStroop.Runtime
{
    // Isolated, presentation-only orchestration for an explicitly configured
    // pair of development peers. This class does not start a device, alter an
    // experiment, record/export samples, or implement UI/bootstrap wiring.
    // Callers own the physical provider, peer identities, selected source and
    // authoritative task; identity declarations are not authentication.
    public sealed class ViveDevelopmentPeerController : IDisposable
    {
        readonly ViveDevelopmentTransport transport;
        readonly IClock clock;
        readonly string sessionId,clockId;
        readonly PeerRole role;
        readonly Func<TaskSnapshot,ViveProEyeHeadEyeSource> sourceFactory;
        readonly double invalidationRetrySeconds;
        ViveProEyeHeadEyeSource source;
        int sourceBlock=-1,sourceSegment=-1;
        double lastClock=-1,nextInvalidation;
        bool enabled,published,faulted,disposed;

        public ViveDevelopmentRemotePose CurrentPose {get;private set;}
        public string LastIssue {get;private set;}
        public bool OperatorEnabled {get{return enabled;}}
        public bool Faulted {get{return faulted;}}
        public bool DevelopmentOnly {get{return true;}}
        public bool HardwareCaptureTimeVerified {get{return false;}}
        public bool CanRecordOrExport {get{return false;}}
        public bool SafetyStopPending {get{return transport.SafetyStopPending;}}
        public bool SafetyStopAcknowledged {get{return transport.SafetyStopAcknowledged;}}
        // This indicates socket acceptance only, not remote receipt or application.
        public bool LastInvalidationSocketAccepted {get;private set;}

        public ViveDevelopmentPeerController(ViveDevelopmentTransport transport,IClock clock,string sessionId,
            PeerRole role,double invalidationRetrySeconds,Func<TaskSnapshot,ViveProEyeHeadEyeSource> sourceFactory=null)
        {
            if(transport==null||clock==null)throw new ArgumentNullException("Explicit transport and clock are required");
            if(String.IsNullOrWhiteSpace(sessionId)||String.IsNullOrWhiteSpace(clock.ClockId))throw new ArgumentException("Explicit session and clock identity are required");
            if(!Enum.IsDefined(typeof(PeerRole),role))throw new ArgumentOutOfRangeException("role");
            if(!WireCodec.Finite(invalidationRetrySeconds)||invalidationRetrySeconds<=0)throw new ArgumentOutOfRangeException("invalidationRetrySeconds");
            if(role==PeerRole.Operator&&sourceFactory==null)throw new ArgumentNullException("sourceFactory");
            if(role==PeerRole.Participant&&sourceFactory!=null)throw new ArgumentException("Participant peer does not own an operator control source");
            this.transport=transport;this.clock=clock;this.sessionId=sessionId;clockId=clock.ClockId;this.role=role;
            this.invalidationRetrySeconds=invalidationRetrySeconds;this.sourceFactory=sourceFactory;
        }

        double Now()
        {
            double now=clock.Now;
            if(clock.ClockId!=clockId||!WireCodec.Finite(now)||now<0||now<lastClock)
                throw new InvalidOperationException("Controller clock failed; a new peer epoch is required");
            lastClock=now;return now;
        }
        void Require(PeerRole expected)
        {
            if(disposed)throw new ObjectDisposedException("ViveDevelopmentPeerController");
            if(role!=expected)throw new InvalidOperationException("Action belongs to a different peer role");
        }
        static bool UsableTask(TaskSnapshot task,string expectedSession)
        {return task!=null&&task.SessionId==expectedSession&&task.Phase==Phase.MainTask&&task.BlockIndex>=0&&
            task.TrialId!=null&&task.Word.HasValue&&task.Ink.HasValue&&task.Condition.HasValue&&task.Condition.Value!=Condition.D;}

        // This is an explicit owner decision. It does not initialize or start a
        // provider. Disable clears the last published pose as soon as possible.
        public void SetOperatorEnabled(bool value)
        {
            Require(PeerRole.Operator);
            if(faulted&&value)throw new InvalidOperationException("A faulted controller needs a new peer epoch");
            enabled=value;
            if(!value){LastIssue="operator_source_not_enabled";Invalidate(true);}
        }
        // The operator retries explicitly while pending; no remote resume exists.
        public bool RequestSafetyStop()
        {
            Require(PeerRole.Operator);
            if(faulted)return false;
            try{return transport.SendSafetyStop();}
            catch(Exception error){faulted=true;LastIssue="safety_stop_send_fault:"+error.GetType().Name;Invalidate(true);return false;}
        }

        // Must be called repeatedly by the operator application's own loop.
        // A false read, readiness loss or exception immediately invalidates the
        // last publication and retries at the explicit development cadence.
        public void TickOperator(bool sourceReady)
        {
            Require(PeerRole.Operator);
            if(faulted){Invalidate(false);return;}
            try
            {
                Now();transport.Tick();
                if(!enabled||!sourceReady||transport.RecoveryDecisionRequired)
                {LastIssue=!enabled?"operator_source_not_enabled":!sourceReady?"operator_source_not_ready":"peer_recovery_decision_required";Invalidate(false);return;}
                var task=transport.CurrentTask;
                if(!UsableTask(task,sessionId))
                {LastIssue="current_main_task_unavailable_or_avatar_hidden";Invalidate(false);return;}
                if(source==null||sourceBlock!=task.BlockIndex||sourceSegment!=task.Segment)
                {
                    // Clear the previous context before starting a new one. A fresh
                    // source is only constructed when the task context changes.
                    if(published)Invalidate(true);
                    source=sourceFactory(task);sourceBlock=task.BlockIndex;sourceSegment=task.Segment;
                    if(source==null)throw new InvalidOperationException("Source factory did not supply a source");
                }
                HeadEyeSample sample;
                if(!source.TryRead(out sample)||sample==null||source.LastObservation==null)
                {LastIssue="no_fresh_operator_observation";Invalidate(false);return;}
                if(!transport.SendObservation(source.LastObservation))
                {
                    LastIssue=transport.LastSendRejection??"operator_transport_send_failed";
                    LastInvalidationSocketAccepted=transport.LastInvalidationSent;
                    if(!transport.LastInvalidationSent)Invalidate(false);
                    else nextInvalidation=Now()+invalidationRetrySeconds;
                    return;
                }
                published=true;nextInvalidation=0;LastInvalidationSocketAccepted=false;LastIssue=null;
            }
            catch(Exception error)
            {
                faulted=true;LastIssue="operator_controller_fault:"+error.GetType().Name;
                Invalidate(true);
            }
        }

        // The participant remains task-authoritative and chooses whether live
        // operator input is selected. Condition D and non-main phases never
        // expose a live pose. The caller must clear any prior rendered output
        // whenever this property is null; this class does not drive a renderer.
        public void TickParticipant(Func<TaskSnapshot> readAuthoritativeTask,bool liveSourceSelected,bool presentationReady)
        {
            Require(PeerRole.Participant);CurrentPose=null;
            if(faulted)return;
            try
            {
                Now();transport.Tick();
                // A stop callback can pause the task during Tick. Read the
                // authoritative snapshot only after that callback completes.
                var authoritativeTask=readAuthoritativeTask==null?null:readAuthoritativeTask();
                if(authoritativeTask==null||authoritativeTask.SessionId!=sessionId)
                {LastIssue="authoritative_task_unavailable";return;}
                if(!transport.SendTask(authoritativeTask))
                {LastIssue="task_socket_not_accepted";return;}
                if(!liveSourceSelected||!presentationReady||!UsableTask(authoritativeTask,sessionId)||transport.RecoveryDecisionRequired)
                {LastIssue="live_presentation_not_ready";return;}
                CurrentPose=transport.CurrentPose(authoritativeTask.BlockIndex,authoritativeTask.Segment);
                LastIssue=CurrentPose==null?"current_operator_pose_unavailable":null;
            }
            catch(Exception error)
            {CurrentPose=null;faulted=true;LastIssue="participant_controller_fault:"+error.GetType().Name;}
        }

        void Invalidate(bool force)
        {
            if(!published)return;
            try
            {
                double now=Now();if(!force&&now<nextInvalidation)return;
                LastInvalidationSocketAccepted=transport.InvalidateLastPublishedPose();
                nextInvalidation=now+invalidationRetrySeconds;
                if(!LastInvalidationSocketAccepted)LastIssue=(LastIssue??"operator_unavailable")+";invalidation_socket_not_accepted";
            }
            catch(Exception error)
            {LastInvalidationSocketAccepted=false;LastIssue=(LastIssue??"operator_unavailable")+";invalidation_failed:"+error.GetType().Name;}
        }
        public void Dispose()
        {
            if(disposed)return;
            if(role==PeerRole.Operator)Invalidate(true);
            CurrentPose=null;disposed=true;transport.Dispose();
        }
    }
}
