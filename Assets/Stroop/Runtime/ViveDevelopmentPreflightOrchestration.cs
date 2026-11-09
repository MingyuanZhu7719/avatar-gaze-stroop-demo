using System;
using System.Net;
using System.Net.Sockets;
using AvatarStroop.Core;

namespace AvatarStroop.Runtime
{
    // Explicit endpoint pairing for a separate VDPF datagram channel. This class
    // does not create a socket. Endpoint matching is not cryptographic identity.
    public sealed class VivePreflightEndpointConfiguration
    {
        readonly IPEndPoint local,remote;
        public PeerRole Role {get;private set;}
        public IPEndPoint Local {get{return new IPEndPoint(local.Address,local.Port);}}
        public IPEndPoint Remote {get{return new IPEndPoint(remote.Address,remote.Port);}}
        public string SessionId {get;private set;}
        public string OperatorStreamId {get;private set;}
        public string OperatorClockId {get;private set;}
        public string ParticipantClockId {get;private set;}
        public string MappingVersion {get;private set;}
        public string Epoch {get;private set;}
        // Sender-side declared instance only. VDPF v1 does not transmit or prove
        // a physical device identity, and this value is not authentication.
        public string ExpectedOperatorDeviceId {get;private set;}
        public double MaximumAgeSeconds {get;private set;}
        public bool RemoteNetworkExplicitlyEnabled {get;private set;}
        public bool CryptographicallyAuthenticated {get{return false;}}
        public VivePreflightEndpointConfiguration(PeerRole role,IPEndPoint localEndpoint,IPEndPoint remoteEndpoint,bool allowRemoteNetwork,
            string sessionId,string operatorStreamId,string operatorClockId,string participantClockId,string mappingVersion,string epoch,string expectedOperatorDeviceId,double maximumAgeSeconds)
        {
            if(!Enum.IsDefined(typeof(PeerRole),role)||localEndpoint==null||remoteEndpoint==null||localEndpoint.Port==0||remoteEndpoint.Port==0||
                localEndpoint.AddressFamily!=remoteEndpoint.AddressFamily||localEndpoint.Equals(remoteEndpoint)||BadAddress(localEndpoint.Address)||BadAddress(remoteEndpoint.Address))
                throw new ArgumentException("Explicit distinct unicast preflight endpoints and role required");
            if(!allowRemoteNetwork&&(!IPAddress.IsLoopback(localEndpoint.Address)||!IPAddress.IsLoopback(remoteEndpoint.Address)))
                throw new ArgumentException("Remote preflight networking requires explicit trusted-network opt-in");
            WireCodec.RequireIdentity(sessionId);WireCodec.RequireIdentity(operatorClockId);WireCodec.RequireIdentity(participantClockId);
            WireCodec.RequireIdentity(mappingVersion);WireCodec.RequireIdentity(epoch);WireCodec.RequireIdentity(expectedOperatorDeviceId);
            if(operatorClockId==participantClockId||operatorStreamId!=ViveDevelopmentTransport.OperatorStream(mappingVersion,epoch)||
                !WireCodec.Finite(maximumAgeSeconds)||maximumAgeSeconds<=0)throw new ArgumentException("Explicit bound stream, distinct clocks and positive age are required");
            Role=role;local=new IPEndPoint(localEndpoint.Address,localEndpoint.Port);remote=new IPEndPoint(remoteEndpoint.Address,remoteEndpoint.Port);
            RemoteNetworkExplicitlyEnabled=allowRemoteNetwork;SessionId=sessionId;OperatorStreamId=operatorStreamId;OperatorClockId=operatorClockId;
            ParticipantClockId=participantClockId;MappingVersion=mappingVersion;Epoch=epoch;ExpectedOperatorDeviceId=expectedOperatorDeviceId;MaximumAgeSeconds=maximumAgeSeconds;
        }
        internal bool MatchesRemote(IPEndPoint endpoint)
        {return endpoint!=null&&endpoint.Port==remote.Port&&endpoint.Address.Equals(remote.Address);}
        static bool BadAddress(IPAddress address)
        {
            if(address.Equals(IPAddress.Any)||address.Equals(IPAddress.IPv6Any)||address.Equals(IPAddress.None)||address.Equals(IPAddress.IPv6None))return true;
            if(address.AddressFamily==AddressFamily.InterNetworkV6)return address.IsIPv6Multicast;
            return address.GetAddressBytes()[0]>=224;
        }
    }

    // Generates challenge bytes for an external datagram channel and consumes
    // proof bytes from the explicitly configured endpoint. This is never wired
    // to Experiment or an avatar and cannot unlock apparatus or a countdown.
    public sealed class VivePreflightParticipant
    {
        readonly VivePreflightEndpointConfiguration config;
        readonly IClock clock;
        readonly ClockSynchronizer mapping;
        readonly ViveDevelopmentPreflight validator;
        byte[] challengeBytes;
        bool confirmed,stopped,recovery,hadCandidate;
        double lastNow=-1,challengeIssuedLocal=-1;
        int activeBlock=-1,activeSegment=-1;
        Phase activePhase;
        Condition? activeCondition;
        public string LastIssue {get;private set;}
        public bool RecoveryDecisionRequired {get{return recovery||validator.RecoveryDecisionRequired;}}
        public bool CanEnterCountdown {get{return false;}}
        public bool HardwareCaptureTimeVerified {get{return false;}}
        public bool CanRecordOrExport {get{return false;}}
        public VivePreflightParticipant(VivePreflightEndpointConfiguration config,IClock clock,ClockSynchronizer mapping)
        {
            if(config==null||clock==null||mapping==null)throw new ArgumentNullException("Explicit configuration and clocks required");
            if(config.Role!=PeerRole.Participant||clock.ClockId!=config.ParticipantClockId)throw new ArgumentException("Participant role and clock required");
            this.config=config;this.clock=clock;this.mapping=mapping;
            validator=new ViveDevelopmentPreflight(config.SessionId,config.OperatorStreamId,config.OperatorClockId,config.ParticipantClockId,
                config.MappingVersion,config.Epoch,config.MaximumAgeSeconds);
        }
        double Now()
        {
            double now=clock.Now;
            if(clock.ClockId!=config.ParticipantClockId||!WireCodec.Finite(now)||now<0||now<lastNow)
            {recovery=true;challengeBytes=null;throw new InvalidOperationException("Participant preflight clock failed; new epoch required");}
            lastNow=now;return now;
        }
        public void ConfirmSource(bool explicitOwnerConfirmation)
        {if(!explicitOwnerConfirmation||confirmed||stopped||recovery)throw new InvalidOperationException("Explicit one-time source confirmation required");confirmed=true;}
        public byte[] BeginContext(TaskSnapshot task,bool authoritativeTaskConfirmed,bool liveSourceStillSelectedAndConfirmed)
        {
            if(!confirmed||stopped||RecoveryDecisionRequired)throw new InvalidOperationException("Confirmed source and authoritative task required");
            if(!authoritativeTaskConfirmed||!liveSourceStillSelectedAndConfirmed||task==null||task.SessionId!=config.SessionId||!task.Condition.HasValue)
            {if(challengeBytes!=null)SignalLoss("authoritative_task_unavailable");throw new InvalidOperationException("Fresh authoritative task required");}
            if(challengeBytes!=null&&task.BlockIndex==activeBlock&&task.Segment==activeSegment&&task.Condition!=activeCondition)
            {SignalLoss("condition_changed_within_block");throw new InvalidOperationException("Condition changed within the current block");}
            double now=Now();string nonce=validator.BeginContext(task.BlockIndex,task.Segment,task.Phase,now,mapping);
            var challenge=new ViveDevelopmentPreflightChallenge(config.SessionId,config.OperatorStreamId,config.OperatorClockId,config.ParticipantClockId,
                config.MappingVersion,config.Epoch,nonce,task.BlockIndex,task.Segment,task.Phase,now);
            activeBlock=task.BlockIndex;activeSegment=task.Segment;activePhase=task.Phase;activeCondition=task.Condition;
            challengeBytes=ViveDevelopmentPreflightWire.EncodeChallenge(challenge);challengeIssuedLocal=now;hadCandidate=false;LastIssue=null;return (byte[])challengeBytes.Clone();
        }
        // Reuses exactly the existing nonce and issue time; no fresh readiness is implied.
        public byte[] RetryChallenge(TaskSnapshot task,bool authoritativeTaskConfirmed,bool liveSourceStillSelectedAndConfirmed)
        {if(!confirmed||challengeBytes==null||stopped||RecoveryDecisionRequired)throw new InvalidOperationException("No active challenge");double now=Now();if(!CurrentContext(task,authoritativeTaskConfirmed,liveSourceStillSelectedAndConfirmed))throw new InvalidOperationException("Current authoritative task changed; new epoch required");if(InitialChallengeExpired(now))throw new InvalidOperationException("Preflight challenge expired; new epoch required");if(hadCandidate&&!validator.CandidateReady(now,mapping))throw new InvalidOperationException("Preflight freshness lost; new epoch required");return (byte[])challengeBytes.Clone();}
        public bool AcceptProof(byte[] bytes,IPEndPoint sender,TaskSnapshot task,bool authoritativeTaskConfirmed,bool liveSourceStillSelectedAndConfirmed)
        {
            double now=Now();if(!confirmed||stopped||RecoveryDecisionRequired||challengeBytes==null){LastIssue="inactive_or_recovery";return false;}
            if(!CurrentContext(task,authoritativeTaskConfirmed,liveSourceStillSelectedAndConfirmed))return false;
            if(InitialChallengeExpired(now))return false;
            // Do not allow a later proof to silently bridge an elapsed tracking
            // gap merely because the caller did not poll CandidateReady().
            if(hadCandidate&&!validator.CandidateReady(now,mapping)){LastIssue="preflight_expired_or_clock_unavailable";return false;}
            if(!config.MatchesRemote(sender)){LastIssue="unexpected_endpoint";return false;}
            ViveDevelopmentPreflightChallenge challenge;ViveDevelopmentPreflightProof proof;string error;
            if(!ViveDevelopmentPreflightWire.TryDecode(bytes,out challenge,out proof,out error)||proof==null)
            {LastIssue="invalid_preflight_packet:"+(error??"wrong_kind");return false;}
            bool accepted=validator.Observe(proof,true,now,mapping);if(accepted)hadCandidate=true;LastIssue=validator.LastRejection;return accepted;
        }
        // Candidate evidence only; never a formal readiness or timing guarantee.
        public bool CandidateReady(TaskSnapshot task,bool authoritativeTaskConfirmed,bool liveSourceStillSelectedAndConfirmed)
        {double now=Now();if(!confirmed||stopped||RecoveryDecisionRequired||challengeBytes==null)return false;if(!CurrentContext(task,authoritativeTaskConfirmed,liveSourceStillSelectedAndConfirmed))return false;if(InitialChallengeExpired(now))return false;return validator.CandidateReady(now,mapping);}
        bool CurrentContext(TaskSnapshot task,bool authoritativeTaskConfirmed,bool liveSourceStillSelectedAndConfirmed)
        {
            if(authoritativeTaskConfirmed&&liveSourceStillSelectedAndConfirmed&&task!=null&&task.SessionId==config.SessionId&&task.BlockIndex==activeBlock&&
                task.Segment==activeSegment&&task.Phase==activePhase&&task.Condition==activeCondition)return true;
            SignalLoss("authoritative_task_or_source_changed");return false;
        }
        bool InitialChallengeExpired(double now)
        {
            if(hadCandidate||challengeIssuedLocal<0||now-challengeIssuedLocal<=config.MaximumAgeSeconds)return false;
            SignalLoss("initial_challenge_expired");return true;
        }
        public void SignalLoss(string reason)
        {
            double now=Now();if(String.IsNullOrWhiteSpace(reason))throw new ArgumentException("Explicit loss reason required");
            validator.Invalidate(now,reason);recovery=true;hadCandidate=false;challengeBytes=null;LastIssue="preflight_loss_new_epoch_required";
        }
        public void Stop(){if(stopped)return;stopped=true;confirmed=false;challengeBytes=null;}
    }

    // A task packet already checked by the caller's configured participant-peer
    // transport. Receipt time is operator-local, not a hardware time. These
    // claims are not cryptographic authentication or proof of real devices.
    public sealed class VivePreflightOperatorTask
    {
        public TaskSnapshot Task {get;private set;}
        public bool PeerTaskAcceptedAndFresh {get;private set;}
        public bool LiveSourceStillSelectedAndConfirmed {get;private set;}
        public long PacketSequence {get;private set;}
        public double LocalReceivedSeconds {get;private set;}
        public VivePreflightOperatorTask(TaskSnapshot task,bool peerTaskAcceptedAndFresh,bool liveSourceStillSelectedAndConfirmed,long packetSequence,double localReceivedSeconds)
        {
            if(packetSequence<0||!WireCodec.Finite(localReceivedSeconds)||localReceivedSeconds<0)throw new ArgumentException("Explicit packet sequence and local receive time required");
            Task=task;PeerTaskAcceptedAndFresh=peerTaskAcceptedAndFresh;LiveSourceStillSelectedAndConfirmed=liveSourceStillSelectedAndConfirmed;
            PacketSequence=packetSequence;LocalReceivedSeconds=localReceivedSeconds;
        }
    }

    // Operator-side parser and proof producer. The source observation must be
    // produced by a separately owned, explicitly started provider. No native
    // reader, file, socket, task or experiment is started here.
    public sealed class VivePreflightOperator
    {
        readonly VivePreflightEndpointConfiguration config;
        readonly IClock clock;
        readonly ClockSynchronizer participantClockMap;
        ViveDevelopmentPreflightChallenge challenge;
        int block=-1,segment=-1,lastNativeEye;
        long proofSequence,lastHeadSequence=-1,lastTaskPacketSequence=-1;
        double lastNow=-1,challengeReceived,lastIssueTime=-1,lastProofTime=-1,lastFreshTaskAt=-1;
        Phase phase;
        Condition? condition;
        TaskSnapshot lastTask;
        bool confirmed,recovery,stopped,contextSet,preflightActive,hasNativeEye;
        public string LastIssue {get;private set;}
        public bool RecoveryDecisionRequired {get{return recovery;}}
        public bool HardwareCaptureTimeVerified {get{return false;}}
        public bool CanRecordOrExport {get{return false;}}
        public VivePreflightOperator(VivePreflightEndpointConfiguration config,IClock clock,ClockSynchronizer participantClockMap)
        {
            if(config==null||clock==null||participantClockMap==null)throw new ArgumentNullException("Explicit configuration and clocks required");
            if(config.Role!=PeerRole.Operator||clock.ClockId!=config.OperatorClockId)throw new ArgumentException("Operator role and clock required");
            this.config=config;this.clock=clock;this.participantClockMap=participantClockMap;
        }
        double Now()
        {
            double now=clock.Now;
            if(clock.ClockId!=config.OperatorClockId||!WireCodec.Finite(now)||now<0||now<lastNow)
            {recovery=true;challenge=null;throw new InvalidOperationException("Operator preflight clock failed; new epoch required");}
            lastNow=now;return now;
        }
        public void ConfirmSource(bool explicitOwnerConfirmation)
        {if(!explicitOwnerConfirmation||confirmed||stopped||recovery)throw new InvalidOperationException("Explicit one-time source confirmation required");confirmed=true;}
        // Only a newly received, validated task packet updates task freshness.
        // Re-reading one cached packet never extends its original receipt time.
        public bool ObserveTask(VivePreflightOperatorTask status)
        {
            double now=Now();if(!confirmed||stopped||recovery)throw new InvalidOperationException("Active confirmed operator source required");
            // A newly arriving packet must not erase an elapsed task gap merely
            // because no one polled this peer during the interruption.
            if(contextSet&&lastFreshTaskAt>=0&&now-lastFreshTaskAt>config.MaximumAgeSeconds)
            {SignalLoss("authoritative_task_gap");return false;}
            var task=status==null?null:status.Task;
            if(status==null||!status.PeerTaskAcceptedAndFresh||!status.LiveSourceStillSelectedAndConfirmed||task==null||task.SessionId!=config.SessionId||task.BlockIndex<0||!task.Condition.HasValue)
            {if(contextSet)SignalLoss("authoritative_preflight_task_unavailable");else LastIssue="awaiting_authoritative_preflight_task";return false;}
            if(status.PacketSequence<lastTaskPacketSequence)return Reject("old_task_packet");
            if(status.PacketSequence==lastTaskPacketSequence)
            {
                if(status.LocalReceivedSeconds!=lastFreshTaskAt||!SameTask(task,lastTask))
                {SignalLoss("cached_task_changed");return false;}
                if(now-lastFreshTaskAt>config.MaximumAgeSeconds){SignalLoss("authoritative_task_stale");return false;}
                return preflightActive;
            }
            if(status.LocalReceivedSeconds>now||status.LocalReceivedSeconds<lastFreshTaskAt||now-status.LocalReceivedSeconds>config.MaximumAgeSeconds)
            {if(contextSet)SignalLoss("authoritative_task_stale_or_invalid");else LastIssue="awaiting_fresh_authoritative_task";return false;}
            if(task.Phase!=Phase.BlockReady&&task.Phase!=Phase.Countdown)
            {
                // A confirmed transition out of Countdown ends this preflight;
                // no old proof may be generated during task/questionnaire/rest.
                // Pause or unexpected early transitions require a new epoch.
                if(task.Phase==Phase.Paused||task.Phase==Phase.NeedsProtocolDecision)
                {if(contextSet)SignalLoss("preflight_interrupted");else LastIssue="awaiting_authoritative_preflight_task";return false;}
                if(contextSet&&(phase!=Phase.Countdown||task.BlockIndex!=block||task.Segment!=segment))
                {SignalLoss("unexpected_preflight_exit");return false;}
                preflightActive=false;challenge=null;lastProofTime=-1;lastFreshTaskAt=status.LocalReceivedSeconds;
                lastTaskPacketSequence=status.PacketSequence;lastTask=task;LastIssue="outside_preflight";return false;
            }
            if(contextSet&&!preflightActive&&task.BlockIndex==block)
            {SignalLoss("preflight_phase_reentered_after_exit");return false;}
            if(contextSet&&task.BlockIndex==block&&task.Segment==segment&&task.Condition!=condition)
            {SignalLoss("condition_changed_within_block");return false;}
            bool invalidTransition=!contextSet?task.Phase!=Phase.BlockReady:
                task.BlockIndex<block||(task.BlockIndex==block&&(task.Segment!=segment||(phase==Phase.Countdown&&task.Phase!=Phase.Countdown)))||
                (task.BlockIndex>block&&(task.BlockIndex!=block+1||phase!=Phase.Countdown||task.Phase!=Phase.BlockReady));
            if(invalidTransition){SignalLoss("task_context_regressed_or_segment_changed");return false;}
            if(!contextSet||task.BlockIndex!=block||task.Segment!=segment||task.Phase!=phase)
            {challenge=null;lastProofTime=-1;block=task.BlockIndex;segment=task.Segment;phase=task.Phase;condition=task.Condition;contextSet=true;}
            preflightActive=true;lastFreshTaskAt=status.LocalReceivedSeconds;lastTaskPacketSequence=status.PacketSequence;lastTask=task;return true;
        }
        static bool SameTask(TaskSnapshot a,TaskSnapshot b)
        {return a!=null&&b!=null&&a.SessionId==b.SessionId&&a.BlockIndex==b.BlockIndex&&a.Segment==b.Segment&&a.Phase==b.Phase&&
            a.Condition==b.Condition&&a.TrialId==b.TrialId&&a.Word==b.Word&&a.Ink==b.Ink;}
        public bool AcceptChallenge(byte[] bytes,IPEndPoint sender,VivePreflightOperatorTask task)
        {
            if(!confirmed||stopped||recovery)return Reject("inactive_or_recovery");
            if(!ObserveTask(task))return false;
            double now=Now();if(!confirmed||stopped||recovery||!contextSet||!preflightActive)return Reject("inactive_or_recovery");
            if(lastFreshTaskAt<0||now-lastFreshTaskAt>config.MaximumAgeSeconds){SignalLoss("authoritative_task_stale");return false;}
            if((lastProofTime<0&&challenge!=null&&now-challengeReceived>config.MaximumAgeSeconds)||
                (lastProofTime>=0&&now-lastProofTime>config.MaximumAgeSeconds)){SignalLoss("operator_proof_gap");return false;}
            if(!config.MatchesRemote(sender))return Reject("unexpected_endpoint");
            ViveDevelopmentPreflightChallenge incoming;ViveDevelopmentPreflightProof ignored;string error;
            if(!ViveDevelopmentPreflightWire.TryDecode(bytes,out incoming,out ignored,out error)||incoming==null)return Reject("invalid_preflight_packet:"+(error??"wrong_kind"));
            if(incoming.SessionId!=config.SessionId||incoming.OperatorStreamId!=config.OperatorStreamId||incoming.OperatorClockId!=config.OperatorClockId||
                incoming.ParticipantClockId!=config.ParticipantClockId||incoming.MappingVersion!=config.MappingVersion||incoming.Epoch!=config.Epoch||
                incoming.Block!=block||incoming.Segment!=segment||incoming.Phase!=phase)return Reject("wrong_peer_or_context");
            if(challenge!=null)
            {
                if(incoming.Challenge==challenge.Challenge&&incoming.IssuedLocalSeconds==challenge.IssuedLocalSeconds)
                {LastIssue=null;return true;} // Retain the original receipt time.
                return Reject("unexpected_challenge_for_active_context");
            }
            if(incoming.IssuedLocalSeconds<=lastIssueTime)return Reject("duplicate_or_old_challenge");
            var map=participantClockMap.Snapshot();double mapped,uncertainty;
            if(!map.Available||map.LocalClockId!=config.OperatorClockId||map.RemoteClockId!=config.ParticipantClockId||
                !participantClockMap.TryMapRemote(incoming.IssuedLocalSeconds,out mapped,out uncertainty)||uncertainty<0||
                mapped+uncertainty>now||now-(mapped-uncertainty)>config.MaximumAgeSeconds)
                return Reject("challenge_time_unavailable_or_stale");
            challenge=incoming;challengeReceived=now;lastIssueTime=incoming.IssuedLocalSeconds;LastIssue=null;return true;
        }
        public byte[] CreateProof(ViveProEyeObservation observation,bool currentSourceReady,VivePreflightOperatorTask task)
        {
            if(!confirmed||stopped||recovery){LastIssue="inactive_or_recovery";return null;}
            if(!ObserveTask(task))return null;
            double now=Now();
            if(!confirmed||stopped||recovery||!contextSet||!preflightActive||challenge==null){LastIssue="source_or_challenge_unavailable";return null;}
            if(lastFreshTaskAt<0||now-lastFreshTaskAt>config.MaximumAgeSeconds){SignalLoss("authoritative_task_stale");return null;}
            if((lastProofTime<0&&now-challengeReceived>config.MaximumAgeSeconds)||
                (lastProofTime>=0&&now-lastProofTime>config.MaximumAgeSeconds)){SignalLoss("operator_proof_gap");return null;}
            if(!currentSourceReady){if(lastProofTime>=0)SignalLoss("source_not_ready");else LastIssue="source_or_challenge_unavailable";return null;}
            if(observation==null||observation.Sample==null||observation.RawPoll==null||observation.RawPoll.Head==null||observation.RawPoll.Eye==null||
                observation.MappingVersion!=config.MappingVersion||observation.HardwareCaptureTimeVerified||observation.MappingValidatedForApparatus||
                !observation.HeadUsable||!observation.EyeUsable||observation.HeadDisposition!=ViveFrameDisposition.Accepted||observation.EyeDisposition!=ViveFrameDisposition.Accepted)
            {if(lastProofTime>=0)SignalLoss("head_or_eye_unavailable");else LastIssue="fresh_usable_head_and_eye_required";return null;}
            var sample=observation.Sample;var head=observation.RawPoll.Head;var eye=observation.RawPoll.Eye;
            if(sample.SessionId!=config.SessionId||sample.StreamId!=config.OperatorStreamId||sample.ClockId!=config.OperatorClockId||sample.DeviceId!=config.ExpectedOperatorDeviceId||
                sample.Source!=ControlSource.LiveOperator||sample.Role!=StreamRole.OperatorControl||sample.BlockIndex!=block||sample.Segment!=segment)
            {if(lastProofTime>=0)SignalLoss("observation_identity_or_context_mismatch");else LastIssue="wrong_identity_context_or_stale_observation";return null;}
            if(!sample.HeadValid||!sample.EyeValid||head.ObservationSequence<=lastHeadSequence||(hasNativeEye&&eye.FrameSequence<=lastNativeEye)||
                head.ObservedLocalSeconds<=challengeReceived||eye.ObservedLocalSeconds<=challengeReceived||
                head.ObservedLocalSeconds>now||eye.ObservedLocalSeconds>now||
                now-head.ObservedLocalSeconds>config.MaximumAgeSeconds||now-eye.ObservedLocalSeconds>config.MaximumAgeSeconds)
            {LastIssue="wrong_identity_context_or_stale_observation";return null;}
            var proof=new ViveDevelopmentPreflightProof(config.SessionId,config.OperatorStreamId,config.OperatorClockId,config.MappingVersion,config.Epoch,
                challenge.Challenge,block,segment,phase,checked(proofSequence++),head.ObservationSequence,eye.FrameSequence,
                challengeReceived,head.ObservedLocalSeconds,eye.ObservedLocalSeconds,now,true,true);
            lastHeadSequence=head.ObservationSequence;lastNativeEye=eye.FrameSequence;hasNativeEye=true;lastProofTime=now;LastIssue=null;
            return ViveDevelopmentPreflightWire.EncodeProof(proof);
        }
        bool Reject(string reason){LastIssue=reason;return false;}
        public void SignalLoss(string reason)
        {Now();if(String.IsNullOrWhiteSpace(reason))throw new ArgumentException("Explicit loss reason required");challenge=null;preflightActive=false;recovery=true;LastIssue="preflight_loss_new_epoch_required";}
        public void Stop(){stopped=true;confirmed=false;challenge=null;}
    }
}
