using System;
using System.Net;
using System.Net.Sockets;
using AvatarStroop.Core;

namespace AvatarStroop.Runtime
{
    // A separate development application supplies these callbacks. They must
    // operate on its actual authoritative task and renderer, not a copy.
    public interface IViveDevelopmentParticipantHost
    {
        TaskSnapshot ReadAuthoritativeTask();
        bool LiveSourceSelected {get;}
        void ClearLivePose();
        void ApplyLivePose(ViveDevelopmentRemotePose pose);
        void PauseByOperator();
        void PauseForTransportLoss();
    }

    public sealed class ViveDevelopmentPeerConfiguration
    {
        readonly IPEndPoint localEndpoint,remoteEndpoint;
        public PeerRole Role {get;private set;}
        public PeerIdentity Own {get;private set;}
        public PeerIdentity Expected {get;private set;}
        public IPEndPoint Local {get{return new IPEndPoint(localEndpoint.Address,localEndpoint.Port);}}
        public IPEndPoint Remote {get{return new IPEndPoint(remoteEndpoint.Address,remoteEndpoint.Port);}}
        public bool AllowRemoteNetwork {get;private set;}
        public string OperatorDeviceId {get;private set;}
        public string MappingVersion {get;private set;}
        public string Epoch {get;private set;}
        public DevelopmentFaultConfiguration Faults {get;private set;}
        public DevelopmentClockConfiguration Clocks {get;private set;}
        public double MaximumObservationAge {get;private set;}
        public double InvalidationRetrySeconds {get;private set;}

        public ViveDevelopmentPeerConfiguration(PeerRole role,PeerIdentity own,PeerIdentity expected,
            IPEndPoint local,IPEndPoint remote,bool allowRemoteNetwork,string operatorDeviceId,string mappingVersion,string epoch,
            DevelopmentFaultConfiguration faults,DevelopmentClockConfiguration clocks,double maximumLocalObservationAgeSeconds,double invalidationRetrySeconds)
        {
            if(own==null||expected==null||local==null||remote==null||faults==null||clocks==null)throw new ArgumentNullException("Explicit peers, endpoints, and policies are required");
            if(!Enum.IsDefined(typeof(PeerRole),role)||own.Role!=role||expected.Role==role||own.SessionId!=expected.SessionId||
                own.PeerId==expected.PeerId||own.ClockId==expected.ClockId||own.Source!=ControlSource.LiveOperator||expected.Source!=ControlSource.LiveOperator)
                throw new ArgumentException("Distinct explicitly paired development peer identities and roles are required");
            if(local.AddressFamily!=remote.AddressFamily||local.Port==0||remote.Port==0||IsWildcard(local.Address)||IsWildcard(remote.Address)||
                local.Equals(remote)||IsMulticastOrBroadcast(local.Address)||IsMulticastOrBroadcast(remote.Address))
                throw new ArgumentException("Explicit distinct unicast endpoints and ports are required");
            if(!allowRemoteNetwork&&(!IPAddress.IsLoopback(local.Address)||!IPAddress.IsLoopback(remote.Address)))
                throw new ArgumentException("Non-loopback networking requires explicit opt-in");
            if(String.IsNullOrWhiteSpace(operatorDeviceId))throw new ArgumentException("Explicit operator device identity required");
            var stream=ViveDevelopmentTransport.OperatorStream(mappingVersion,epoch);
            if((role==PeerRole.Operator?own.SourceId:expected.SourceId)!=stream)
                throw new ArgumentException("Operator source must match the mapping version and explicit epoch");
            if(!WireCodec.Finite(maximumLocalObservationAgeSeconds)||maximumLocalObservationAgeSeconds<=0||
                !WireCodec.Finite(invalidationRetrySeconds)||invalidationRetrySeconds<=0)
                throw new ArgumentOutOfRangeException("Development age and retry intervals must be positive");
            Role=role;Own=own;Expected=expected;localEndpoint=new IPEndPoint(local.Address,local.Port);remoteEndpoint=new IPEndPoint(remote.Address,remote.Port);
            AllowRemoteNetwork=allowRemoteNetwork;OperatorDeviceId=operatorDeviceId;MappingVersion=mappingVersion;Epoch=epoch;
            Faults=faults;Clocks=clocks;MaximumObservationAge=maximumLocalObservationAgeSeconds;InvalidationRetrySeconds=invalidationRetrySeconds;
        }
        static bool IsWildcard(IPAddress value){return value.Equals(IPAddress.Any)||value.Equals(IPAddress.IPv6Any)||value.Equals(IPAddress.None)||value.Equals(IPAddress.IPv6None);}
        static bool IsMulticastOrBroadcast(IPAddress value)
        {
            if(value.AddressFamily==AddressFamily.InterNetworkV6)return value.IsIPv6Multicast;
            var bytes=value.GetAddressBytes();return bytes[0]>=224||value.Equals(IPAddress.Broadcast);
        }
    }

    // This layer is dormant until StartNetwork and a separate source confirmation.
    // It is not a Unity component and has no automatic startup or device access.
    // Wire v2 is presentation-only: it cannot be exported as raw eye data.
    public sealed class ViveDevelopmentAppAdapter : IDisposable
    {
        readonly ViveDevelopmentPeerConfiguration config;
        readonly IClock clock;
        readonly IViveDevelopmentParticipantHost host;
        readonly Func<TaskSnapshot,ViveProEyeHeadEyeSource> sourceFactory;
        ViveDevelopmentTransport transport;
        ViveDevelopmentPeerController controller;
        bool attempted,closed,recovery,faulted,confirmed;
        public bool IsOpen {get{return controller!=null&&!closed;}}
        public bool HasExplicitConfirmation {get{return confirmed;}}
        public bool RecoveryDecisionRequired {get{return recovery;}}
        public bool Faulted {get{return faulted;}}
        public string LastIssue {get;private set;}
        public ViveDevelopmentRemotePose CurrentPose {get;private set;}
        public bool CanRecordOrExport {get{return false;}}
        public bool HardwareCaptureTimeVerified {get{return false;}}
        // Wire v2 cannot establish a phase/challenge-bound readiness proof.
        // Never use CurrentPose or this adapter to admit a block countdown.
        public bool PreflightReady {get{return false;}}
        public bool SafetyStopPending {get{return controller!=null&&controller.SafetyStopPending;}}
        public bool SafetyStopAcknowledged {get{return controller!=null&&controller.SafetyStopAcknowledged;}}

        public bool TryGetOperatorTaskEvidence(out TaskSnapshot task,out long packetSequence,out double localReceivedSeconds)
        {
            RequireRole(PeerRole.Operator);
            if(faulted||recovery){task=null;packetSequence=-1;localReceivedSeconds=0;return false;}
            return transport.TryGetCurrentTaskEvidence(out task,out packetSequence,out localReceivedSeconds);
        }
        public ViveDevelopmentPreflightChannel CreatePreflightChannel(VivePreflightEndpointConfiguration preflight)
        {
            if(!IsOpen||faulted||recovery)throw new InvalidOperationException("An active healthy development peer is required");
            return transport.CreatePreflightChannel(preflight);
        }

        public ViveDevelopmentAppAdapter(ViveDevelopmentPeerConfiguration config,IClock clock,IViveDevelopmentParticipantHost host,
            Func<TaskSnapshot,ViveProEyeHeadEyeSource> sourceFactory)
        {
            if(config==null||clock==null)throw new ArgumentNullException("Explicit configuration and clock required");
            if(clock.ClockId!=config.Own.ClockId)throw new ArgumentException("Clock identity must match configured peer");
            if(config.Role==PeerRole.Participant&&(host==null||sourceFactory!=null))throw new ArgumentException("Participant requires its authoritative host only");
            if(config.Role==PeerRole.Operator&&(host!=null||sourceFactory==null))throw new ArgumentException("Operator requires its explicit source factory only");
            this.config=config;this.clock=clock;this.host=host;this.sourceFactory=sourceFactory;
        }
        public void StartNetwork(bool explicitOwnerAction,bool trustedRemoteConfirmed)
        {
            if(!explicitOwnerAction)throw new InvalidOperationException("Explicit owner network start required");
            if(attempted||closed)throw new InvalidOperationException("A new peer epoch is required before restarting");
            if(config.AllowRemoteNetwork&&!trustedRemoteConfirmed)throw new InvalidOperationException("Explicit trusted development network confirmation required");
            throw new InvalidOperationException("Unauthenticated VIVE networking is disabled, including loopback");
        }
        // Test harness only: the sealed socket has no OS network access.
        internal void StartInMemoryFixture(ViveInMemoryDatagramSocket socket)
        {
            if(socket==null||attempted||closed||config.AllowRemoteNetwork||!socket.LocalEndpoint.Equals(config.Local))
                throw new InvalidOperationException("A fresh matching in-memory fixture is required");
            attempted=true;
            try
            {
                transport=ViveDevelopmentTransport.CreateInMemoryFixture(socket,config.Remote,clock,config.Own,config.Expected,config.OperatorDeviceId,
                    config.MappingVersion,config.Epoch,config.Faults,config.Clocks,config.MaximumObservationAge,
                    config.Role==PeerRole.Participant?(Action)PauseByOperator:null);
                controller=new ViveDevelopmentPeerController(transport,clock,config.Own.SessionId,config.Role,config.InvalidationRetrySeconds,sourceFactory);
                LastIssue="in_memory_fixture_started_source_not_confirmed";
            }
            catch{socket.Dispose();closed=true;throw;}
        }
        public void ConfirmLiveSource(bool explicitOwnerConfirmation)
        {
            if(!explicitOwnerConfirmation)throw new InvalidOperationException("Explicit owner source confirmation required");
            if(!IsOpen||confirmed||faulted||recovery)throw new InvalidOperationException("A new configured peer epoch is required");
            if(config.Role==PeerRole.Operator)controller.SetOperatorEnabled(true);
            confirmed=true;LastIssue=null;
        }
        public void TickOperator(bool sourceReady)
        {
            RequireRole(PeerRole.Operator);
            if(faulted||recovery)return;
            try
            {
                controller.TickOperator(confirmed&&sourceReady);
                LastIssue=controller.LastIssue;
                if(controller.Faulted){faulted=true;transport.InvalidatePreflightChannels();LastIssue=controller.LastIssue;}
                CheckRecovery();
            }
            catch(Exception error){faulted=true;transport.InvalidatePreflightChannels();LastIssue="operator_adapter_fault:"+error.GetType().Name;TryDisableOperator();}
        }
        public void TickParticipant(bool presentationReady)
        {
            RequireRole(PeerRole.Participant);CurrentPose=null;
            // If the host cannot report source selection at all after owner
            // confirmation, treat it as potentially live and fail closed.
            bool selectedLive=confirmed;
            try
            {
                selectedLive=confirmed&&host.LiveSourceSelected;
                // Clear first, including every failed or interrupted update. A
                // host implementation must actually clear its rendered output.
                host.ClearLivePose();
                if(faulted||recovery)return;
                controller.TickParticipant(()=>host.ReadAuthoritativeTask(),selectedLive,presentationReady);
                LastIssue=controller.LastIssue;
                if(controller.Faulted){FailParticipant(selectedLive);return;}
                CheckRecovery();if(recovery)return;
                CurrentPose=controller.CurrentPose;
                if(CurrentPose!=null)host.ApplyLivePose(CurrentPose);
            }
            catch(Exception error){LastIssue="participant_adapter_fault:"+error.GetType().Name;FailParticipant(selectedLive);}
        }
        public bool RequestRemotePause()
        {RequireRole(PeerRole.Operator);return !faulted&&!recovery&&controller.RequestSafetyStop();}
        void PauseByOperator()
        {
            host.PauseByOperator();
            var task=host.ReadAuthoritativeTask();
            // The transport acknowledges only after this callback returns.
            if(task==null||(task.Phase!=Phase.Paused&&task.Phase!=Phase.Ended))
                throw new InvalidOperationException("Operator pause was not applied by the authoritative task");
        }
        void CheckRecovery()
        {
            if(!transport.RecoveryDecisionRequired)return;
            bool pauseLiveTask=config.Role==PeerRole.Participant&&confirmed&&host.LiveSourceSelected;
            recovery=true;confirmed=false;CurrentPose=null;LastIssue="peer_recovery_decision_required_new_epoch_required";
            transport.InvalidatePreflightChannels();
            if(config.Role==PeerRole.Operator){TryDisableOperator();return;}
            TryClear();
            var task=host.ReadAuthoritativeTask();
            if(pauseLiveTask&&task!=null&&Pausable(task.Phase))
            {
                host.PauseForTransportLoss();task=host.ReadAuthoritativeTask();
                if(task==null||(task.Phase!=Phase.Paused&&task.Phase!=Phase.Ended))
                    throw new InvalidOperationException("Transport-loss pause was not applied by the authoritative task");
            }
        }
        static bool Pausable(Phase phase)
        {return phase==Phase.Practice||phase==Phase.PracticeCorrection||phase==Phase.Countdown||phase==Phase.MainTask||phase==Phase.Questionnaire||phase==Phase.Rest;}
        void FailParticipant(bool selectedLive)
        {
            CurrentPose=null;faulted=true;confirmed=false;transport.InvalidatePreflightChannels();TryClear();
            if(!selectedLive)return;
            try
            {
                var task=host.ReadAuthoritativeTask();
                if(task==null||!Pausable(task.Phase))return;
                host.PauseForTransportLoss();task=host.ReadAuthoritativeTask();
                if(task==null||(task.Phase!=Phase.Paused&&task.Phase!=Phase.Ended))
                    throw new InvalidOperationException("Fault pause was not applied by the authoritative task");
            }
            catch(Exception error){LastIssue=(LastIssue??"participant_unavailable")+";fault_pause_failed:"+error.GetType().Name;}
        }
        void RequireRole(PeerRole role)
        {if(!IsOpen||config.Role!=role)throw new InvalidOperationException("Configured open peer role required");}
        void TryDisableOperator()
        {try{if(controller!=null)controller.SetOperatorEnabled(false);}catch(Exception error){LastIssue=(LastIssue??"operator_unavailable")+";invalidation_failed:"+error.GetType().Name;}}
        void TryClear()
        {try{if(host!=null)host.ClearLivePose();}catch(Exception error){LastIssue=(LastIssue??"participant_unavailable")+";clear_failed:"+error.GetType().Name;}}
        public void Dispose()
        {
            if(closed)return;closed=true;confirmed=false;CurrentPose=null;TryClear();
            if(controller!=null)controller.Dispose();else if(transport!=null)transport.Dispose();
        }
    }
}
