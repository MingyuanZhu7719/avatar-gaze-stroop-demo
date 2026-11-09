using System;
using AvatarStroop.Core;

namespace AvatarStroop.Runtime
{
    // A host binding names the authoritative session, independently of transient
    // task snapshots. In the production wrapper below the binding comes from the
    // actual Experiment, not from a caller-supplied session flag.
    public interface IVivePreflightBoundParticipantHost : IViveDevelopmentParticipantHost
    {string BoundSessionId {get;}}
    public sealed class VivePreflightExperimentHost : IVivePreflightBoundParticipantHost
    {
        readonly ViveWozParticipantHost inner;
        public string BoundSessionId {get;private set;}
        public VivePreflightExperimentHost(Experiment experiment,Action clearLivePose,bool explicitDevelopmentOnly)
        {
            if(experiment==null)throw new ArgumentNullException("experiment");
            inner=new ViveWozParticipantHost(experiment,clearLivePose,explicitDevelopmentOnly);
            BoundSessionId=experiment.Configuration.SessionId;
        }
        public TaskSnapshot ReadAuthoritativeTask(){return inner.ReadAuthoritativeTask();}
        public bool LiveSourceSelected {get{return inner.LiveSourceSelected;}}
        public void ClearLivePose(){inner.ClearLivePose();}
        public void ApplyLivePose(ViveDevelopmentRemotePose pose){inner.ApplyLivePose(pose);}
        public void PauseByOperator(){inner.PauseByOperator();}
        public void PauseForTransportLoss(){inner.PauseForTransportLoss();}
    }
    // Ports exist to test the lifecycle without opening sockets or a headset.
    // Their flags are local state claims, not peer/device authentication.
    public interface IVivePreflightParticipantPort
    {
        bool Active {get;}
        bool RecoveryDecisionRequired {get;}
        bool Begin(TaskSnapshot task,bool taskConfirmed,bool liveConfirmed);
        bool Poll(TaskSnapshot task,bool taskConfirmed,bool liveConfirmed);
        bool CandidateReady(TaskSnapshot task,bool taskConfirmed,bool liveConfirmed);
        void SignalLoss(string reason);
    }
    public interface IVivePreflightAppPort
    {
        bool IsOpen {get;}
        bool HasExplicitConfirmation {get;}
        bool RecoveryDecisionRequired {get;}
        bool Faulted {get;}
        void TickParticipant(bool presentationReady);
    }
    public sealed class VivePreflightParticipantChannelPort : IVivePreflightParticipantPort
    {
        readonly ViveDevelopmentPreflightChannel channel;
        public VivePreflightParticipantChannelPort(ViveDevelopmentPreflightChannel channel)
        {if(channel==null)throw new ArgumentNullException("channel");this.channel=channel;}
        public bool Active {get{return channel.Active;}}
        public bool RecoveryDecisionRequired {get{return channel.RecoveryDecisionRequired;}}
        public bool Begin(TaskSnapshot task,bool taskConfirmed,bool liveConfirmed){return channel.Begin(task,taskConfirmed,liveConfirmed);}
        public bool Poll(TaskSnapshot task,bool taskConfirmed,bool liveConfirmed){return channel.PollParticipant(task,taskConfirmed,liveConfirmed);}
        public bool CandidateReady(TaskSnapshot task,bool taskConfirmed,bool liveConfirmed){return channel.CandidateReady(task,taskConfirmed,liveConfirmed);}
        public void SignalLoss(string reason){channel.SignalLoss(reason);}
    }
    public sealed class VivePreflightDevelopmentAppPort : IVivePreflightAppPort
    {
        readonly ViveDevelopmentAppAdapter app;
        public VivePreflightDevelopmentAppPort(ViveDevelopmentAppAdapter app)
        {if(app==null)throw new ArgumentNullException("app");this.app=app;}
        public bool IsOpen {get{return app.IsOpen;}}
        public bool HasExplicitConfirmation {get{return app.HasExplicitConfirmation;}}
        public bool RecoveryDecisionRequired {get{return app.RecoveryDecisionRequired;}}
        public bool Faulted {get{return app.Faulted;}}
        public void TickParticipant(bool presentationReady){app.TickParticipant(presentationReady);}
    }

    // Development participant lifecycle only. Owns no socket or headset and
    // cannot authorize an experiment; app/port must already be explicitly started.
    // The candidate is NOT authentication, device proof or formal readiness.
    public sealed class ViveDevelopmentPreflightCoordinator
    {
        readonly string sessionId;
        readonly IVivePreflightBoundParticipantHost host;
        readonly IVivePreflightAppPort app;
        readonly IVivePreflightParticipantPort preflight;
        bool begun,failed,wasLive,pauseWhenActive;
        int block=-1,segment=-1;
        Condition? condition;
        public bool CandidateObserved {get;private set;}
        public bool RecoveryDecisionRequired {get{return failed;}}
        public bool PauseVerified {get;private set;}
        public string LastIssue {get;private set;}
        public bool CanEnterCountdown {get{return false;}}
        public bool CryptographicallyAuthenticated {get{return false;}}
        public ViveDevelopmentPreflightCoordinator(string sessionId,IVivePreflightBoundParticipantHost host,IVivePreflightAppPort app,IVivePreflightParticipantPort preflight)
        {
            if(String.IsNullOrWhiteSpace(sessionId)||host==null||app==null||preflight==null)throw new ArgumentException("Explicit session, host, app and preflight are required");
            WireCodec.RequireIdentity(sessionId);
            if(host.BoundSessionId!=sessionId)throw new ArgumentException("Host must bind the same authoritative session", "host");
            this.sessionId=sessionId;this.host=host;this.app=app;this.preflight=preflight;
        }
        static bool Pausable(Phase phase)
        {return phase==Phase.Practice||phase==Phase.PracticeCorrection||phase==Phase.Countdown||phase==Phase.MainTask||phase==Phase.Questionnaire||phase==Phase.Rest;}
        public void Tick()
        {
            if(failed){MaintainFailure();return;}
            TaskSnapshot task=null;bool live=wasLive,liveKnown=false;
            try
            {
                if(host.BoundSessionId!=sessionId){Fail("host_session_binding_changed",null,false);return;}
                task=host.ReadAuthoritativeTask();live=host.LiveSourceSelected;liveKnown=true;
                if(task!=null&&task.SessionId!=sessionId){Fail("foreign_task",task,false);return;}
                if(task==null)
                {if(wasLive||live||app.HasExplicitConfirmation)Fail("authoritative_task_unavailable",null,wasLive||live);return;}
                if(!live)
                {
                    if(wasLive){Fail("live_source_changed",task,true);return;}
                    TryClear();return;
                }
                wasLive=true;
                // The shared adapter may exchange development task state, but it
                // must not display live data before full integration/validation.
                if(!app.IsOpen||!app.HasExplicitConfirmation||app.Faulted||app.RecoveryDecisionRequired||!preflight.Active||preflight.RecoveryDecisionRequired)
                {Fail("peer_or_preflight_unavailable",task,true);return;}
                host.ClearLivePose();
                app.TickParticipant(false);
                task=host.ReadAuthoritativeTask();
                if(task==null||task.SessionId!=sessionId||!host.LiveSourceSelected||app.Faulted||app.RecoveryDecisionRequired||preflight.RecoveryDecisionRequired)
                {Fail("peer_or_task_changed",task,true);return;}
                if(task.Phase!=Phase.BlockReady)
                {
                    // No preflight candidate can authorize countdown. A future
                    // phase-bound challenge transport must be integrated first.
                    Fail("phase_not_authorized",task,true);return;
                }
                if(!task.Condition.HasValue||task.BlockIndex<0||task.BlockIndex>3||task.Segment<0)
                {Fail("invalid_task_context",task,true);return;}
                if(!begun)
                {
                    if(!preflight.Begin(task,true,true)){Fail("challenge_not_accepted_by_socket",task,true);return;}
                    block=task.BlockIndex;segment=task.Segment;condition=task.Condition;begun=true;
                }
                else if(task.BlockIndex!=block||task.Segment!=segment||task.Condition!=condition)
                {Fail("task_context_changed",task,true);return;}
                preflight.Poll(task,true,true);
                CandidateObserved=preflight.CandidateReady(task,true,true);
                if(preflight.RecoveryDecisionRequired||!preflight.Active){Fail("preflight_gap_or_loss",task,true);return;}
                LastIssue=CandidateObserved?"development_candidate_only_not_countdown_ready":"waiting_for_preflight_proof";
            }
            catch(Exception error)
            {
                // If the source/host could not be read, do not assume Automatic.
                // An unknown live task must be paused if the host permits it.
                try{task=host.ReadAuthoritativeTask();}catch(Exception){task=null;}
                if(!liveKnown){try{live=host.LiveSourceSelected;liveKnown=true;}catch(Exception){}}
                Fail("coordinator_fault:"+error.GetType().Name,task,wasLive||!liveKnown||live);
            }
        }
        public void SignalRemoteLoss()
        {
            if(failed){MaintainFailure();return;}TaskSnapshot task=null;bool live=wasLive;
            try{task=host.ReadAuthoritativeTask();}catch(Exception){}
            try{live=host.LiveSourceSelected||wasLive;}catch(Exception){live=true;}
            Fail("remote_loss",task,live);
        }
        void Fail(string reason,TaskSnapshot task,bool pauseIfNeeded)
        {
            if(failed)return;failed=true;pauseWhenActive=pauseIfNeeded;CandidateObserved=false;LastIssue=reason;
            try{preflight.SignalLoss(reason);}catch(Exception error){AppendIssue("preflight_loss_failed:"+error.GetType().Name);}
            HandleFailedTask(task,pauseIfNeeded);
        }
        void MaintainFailure()
        {
            TaskSnapshot task=null;
            try{task=host.ReadAuthoritativeTask();}catch(Exception){}
            bool bound=false;
            try
            {
                bound=host.BoundSessionId==sessionId;
                if(bound&&(task==null||task.SessionId==sessionId)&&host.LiveSourceSelected)pauseWhenActive=true;
            }
            catch(Exception){if(bound)pauseWhenActive=true;}
            HandleFailedTask(task,pauseWhenActive);
        }
        void HandleFailedTask(TaskSnapshot task,bool pauseIfNeeded)
        {
            // This flag is a current readback, not a historical pause receipt.
            PauseVerified=false;
            try{if(host.BoundSessionId!=sessionId){AppendIssue("host_session_unverified");return;}}
            catch(Exception){AppendIssue("host_session_unverified");return;}
            // Even a bound host with an inconsistent foreign snapshot must not
            // mutate that foreign task or its renderer.
            if(task!=null&&task.SessionId!=sessionId){AppendIssue("foreign_session_untouched");return;}
            TryClear();
            if(!pauseIfNeeded)return;
            // A failed read is unknown, not evidence that no live task exists.
            // Attempt pause in that case; never claim success without readback.
            if(task!=null&&(task.Phase==Phase.Paused||task.Phase==Phase.Ended))
            {PauseVerified=task.SessionId==sessionId;return;}
            if(task!=null&&task.SessionId==sessionId&&!Pausable(task.Phase))return;
            try
            {
                host.PauseForTransportLoss();var after=host.ReadAuthoritativeTask();
                PauseVerified=after!=null&&after.SessionId==sessionId&&(after.Phase==Phase.Paused||after.Phase==Phase.Ended);
                if(!PauseVerified)AppendIssue("pause_not_verified");
            }
            catch(Exception error)
            {
                AppendIssue("pause_callback_failed:"+error.GetType().Name);
                // Some hosts pause first, then fail while clearing presentation.
                // Preserve the fault but read back actual authoritative state.
                try{var after=host.ReadAuthoritativeTask();PauseVerified=after!=null&&after.SessionId==sessionId&&(after.Phase==Phase.Paused||after.Phase==Phase.Ended);}
                catch(Exception){PauseVerified=false;}
            }
        }
        void TryClear()
        {try{host.ClearLivePose();}catch(Exception error){AppendIssue("clear_failed:"+error.GetType().Name);}}
        void AppendIssue(string value)
        {
            string suffix=";"+value;
            if(LastIssue==null)LastIssue=value;
            else if(LastIssue.IndexOf(suffix,StringComparison.Ordinal)<0)LastIssue+=suffix;
            if(LastIssue.Length>1024)LastIssue=LastIssue.Substring(0,1024);
        }
    }
}
