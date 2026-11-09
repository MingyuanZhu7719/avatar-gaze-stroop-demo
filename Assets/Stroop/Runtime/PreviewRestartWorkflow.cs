using System;

namespace AvatarStroop.Runtime
{
    public enum PreviewRestartStage
    {
        Idle, AwaitingAcknowledgement, CloseReady, AwaitingExit, Verifying,
        LaunchReady, LaunchClaimed, Completed, Blocked
    }

    // Pure owner-side orchestration. This never sends a command, closes a
    // process, clears a receiver fault, or resumes an existing experiment.
    // Side-effect claims are consumed before the launcher performs them.
    public sealed class PreviewRestartWorkflow
    {
        readonly double acknowledgementTimeout,closeTimeout;
        double lastNow=-1,stageStarted;
        public PreviewRestartStage Stage {get;private set;}
        public string RequestId {get;private set;}
        public string CommandHash {get;private set;}
        public string FailureReason {get;private set;}
        public bool BlocksActions {get{return Stage!=PreviewRestartStage.Idle&&Stage!=PreviewRestartStage.Completed;}}

        public PreviewRestartWorkflow(double acknowledgementTimeoutSeconds=15,double closeTimeoutSeconds=15)
        {
            if(!Positive(acknowledgementTimeoutSeconds)||!Positive(closeTimeoutSeconds))throw new ArgumentOutOfRangeException("timeoutSeconds");
            acknowledgementTimeout=acknowledgementTimeoutSeconds;closeTimeout=closeTimeoutSeconds;
        }
        static bool Positive(double value){return !Double.IsNaN(value)&&!Double.IsInfinity(value)&&value>0;}
        public bool TryBegin(OwnerCommand command,bool participantFresh,bool commandPending,double now)
        {
            Poll(now);
            if(command==null||command.Kind!=OwnerCommandKind.RestartPreview)throw new ArgumentException("An explicit RestartPreview owner command is required");
            if(Stage!=PreviewRestartStage.Idle||!participantFresh||commandPending)return false;
            RequestId=command.RequestId;CommandHash=OwnerCommandCodec.Hash(command);
            stageStarted=now;Stage=PreviewRestartStage.AwaitingAcknowledgement;return true;
        }
        public void Poll(double now)
        {
            if(Double.IsNaN(now)||Double.IsInfinity(now)||now<0||now<lastNow)throw new ArgumentException("Restart workflow requires a monotonic finite clock");
            lastNow=now;
            if(Stage==PreviewRestartStage.AwaitingAcknowledgement&&now-stageStarted>=acknowledgementTimeout)
                Stop("owner_acknowledgement_timeout;outcome_may_be_uncertain_no_automatic_retry");
            else if(Stage==PreviewRestartStage.AwaitingExit&&now-stageStarted>=closeTimeout)
                Stop("process_exit_timeout_or_unknown;no_force_kill_or_automatic_retry");
        }
        public bool ObserveAcknowledgement(OwnerAcknowledgement acknowledgement,double now)
        {
            Poll(now);
            if(Stage!=PreviewRestartStage.AwaitingAcknowledgement||acknowledgement==null||
                acknowledgement.RequestId!=RequestId||acknowledgement.CommandHash!=CommandHash)return false;
            if(!acknowledgement.Accepted){Stop("owner_command_rejected:"+acknowledgement.Reason);return true;}
            Stage=PreviewRestartStage.CloseReady;stageStarted=now;return true;
        }
        public bool TryRequestClose(double now)
        {
            Poll(now);
            if(Stage!=PreviewRestartStage.CloseReady)return false;
            Stage=PreviewRestartStage.AwaitingExit;stageStarted=now;return true;
        }
        public void ObserveExit(bool participantKnown,bool participantExited,bool operatorKnown,bool operatorExited,double now)
        {
            Poll(now);
            if(Stage==PreviewRestartStage.AwaitingExit&&participantKnown&&participantExited&&operatorKnown&&operatorExited)
                Stage=PreviewRestartStage.Verifying;
        }
        public void ObserveVerification(bool verified,string reason,double now)
        {
            Poll(now);
            if(Stage!=PreviewRestartStage.Verifying)return;
            if(!verified){Stop("closed_session_verification_failed:"+(reason??"unspecified"));return;}
            Stage=PreviewRestartStage.LaunchReady;
        }
        public bool TryClaimLaunch(double now)
        {
            Poll(now);
            if(Stage!=PreviewRestartStage.LaunchReady)return false;
            Stage=PreviewRestartStage.LaunchClaimed;return true;
        }
        public void CompleteLaunch()
        {
            if(Stage!=PreviewRestartStage.LaunchClaimed)throw new InvalidOperationException("Only the claimed fresh launch can complete");
            Stage=PreviewRestartStage.Completed;
        }
        public void Stop(string reason)
        {
            if(Stage==PreviewRestartStage.Idle||Stage==PreviewRestartStage.Completed||Stage==PreviewRestartStage.Blocked)return;
            FailureReason=String.IsNullOrWhiteSpace(reason)?"restart_stopped_no_automatic_retry":reason;
            Stage=PreviewRestartStage.Blocked;
        }
    }
}
