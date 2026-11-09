using System;
using AvatarStroop.Core;

namespace AvatarStroop.Runtime
{
    // Proposed in-memory proof shape. No encoder, transport binding, recording,
    // or apparatus admission is implemented. Claimed identity is not authentication.
    public sealed class ViveDevelopmentPreflightProof
    {
        public string SessionId {get;private set;}
        public string OperatorStreamId {get;private set;}
        public string OperatorClockId {get;private set;}
        public string MappingVersion {get;private set;}
        public string Epoch {get;private set;}
        public string Challenge {get;private set;}
        public int Block {get;private set;}
        public int Segment {get;private set;}
        public Phase Phase {get;private set;}
        public long Sequence {get;private set;}
        public long HeadObservationSequence {get;private set;}
        public int NativeEyeFrameSequence {get;private set;}
        public double OperatorChallengeReceivedSeconds {get;private set;}
        public double HeadObservedSeconds {get;private set;}
        public double EyeObservedSeconds {get;private set;}
        public double OperatorSendMethodSeconds {get;private set;}
        public bool HeadUsable {get;private set;}
        public bool EyeUsable {get;private set;}
        public bool HardwareCaptureTimeVerified {get{return false;}}
        public ViveDevelopmentPreflightProof(string sessionId,string operatorStreamId,string operatorClockId,string mappingVersion,string epoch,
            string challenge,int block,int segment,Phase phase,long sequence,long headObservationSequence,int nativeEyeFrameSequence,
            double operatorChallengeReceivedSeconds,double headObservedSeconds,double eyeObservedSeconds,double operatorSendMethodSeconds,bool headUsable,bool eyeUsable)
        {
            WireCodec.RequireIdentity(sessionId);WireCodec.RequireIdentity(operatorStreamId);WireCodec.RequireIdentity(operatorClockId);
            WireCodec.RequireIdentity(mappingVersion);WireCodec.RequireIdentity(epoch);WireCodec.RequireIdentity(challenge);
            if(block<0||block>3||segment<0||(phase!=Phase.BlockReady&&phase!=Phase.Countdown)||sequence<0||headObservationSequence<0||
                !WireCodec.Finite(operatorChallengeReceivedSeconds)||operatorChallengeReceivedSeconds<0||
                !WireCodec.Finite(headObservedSeconds)||headObservedSeconds<=operatorChallengeReceivedSeconds||
                !WireCodec.Finite(eyeObservedSeconds)||eyeObservedSeconds<=operatorChallengeReceivedSeconds||
                !WireCodec.Finite(operatorSendMethodSeconds)||operatorSendMethodSeconds<Math.Max(headObservedSeconds,eyeObservedSeconds))
                throw new ArgumentException("Invalid development preflight context or clock values");
            SessionId=sessionId;OperatorStreamId=operatorStreamId;OperatorClockId=operatorClockId;MappingVersion=mappingVersion;Epoch=epoch;
            Challenge=challenge;Block=block;Segment=segment;Phase=phase;Sequence=sequence;HeadObservationSequence=headObservationSequence;NativeEyeFrameSequence=nativeEyeFrameSequence;
            OperatorChallengeReceivedSeconds=operatorChallengeReceivedSeconds;HeadObservedSeconds=headObservedSeconds;EyeObservedSeconds=eyeObservedSeconds;
            OperatorSendMethodSeconds=operatorSendMethodSeconds;HeadUsable=headUsable;EyeUsable=eyeUsable;
        }
    }

    // Pure candidate validation only. The experiment must NOT use this to admit
    // a countdown until a separate versioned proof codec, endpoint verification,
    // actual app integration, and independent validation exist. Existing wire v2
    // does not carry the challenge, phase or mapping metadata required here.
    public sealed class ViveDevelopmentPreflight
    {
        readonly string sessionId,streamId,operatorClockId,participantClockId,mappingVersion,epoch;
        readonly double maximumObservationAge;
        int block=-1,segment=-1;Phase phase;
        long lastSequence=-1,lastHeadSequence=-1;
        int lastNativeEyeSequence;
        double lastNow=-1,contextStarted,lastInvalidation=-1,lastRemoteHead,lastRemoteEye,challengeReceivedRemote;
        bool candidate,everReadyInContext,recovery,hasNativeEyeSequence,hasChallengeReceipt;
        string challenge;
        public string Challenge {get{return challenge;}}
        public string LastRejection {get;private set;}
        public bool RecoveryDecisionRequired {get{return recovery;}}
        public bool DevelopmentOnly {get{return true;}}
        public bool HardwareCaptureTimeVerified {get{return false;}}
        public bool CanEnterCountdown {get{return false;}}
        public bool IsConnectedToWireOrApp {get{return false;}}

        public ViveDevelopmentPreflight(string sessionId,string operatorStreamId,string operatorClockId,string participantClockId,
            string mappingVersion,string epoch,double maximumObservationAgeSeconds)
        {
            WireCodec.RequireIdentity(sessionId);WireCodec.RequireIdentity(operatorStreamId);WireCodec.RequireIdentity(operatorClockId);
            WireCodec.RequireIdentity(participantClockId);WireCodec.RequireIdentity(mappingVersion);WireCodec.RequireIdentity(epoch);
            if(operatorClockId==participantClockId||operatorStreamId!=ViveDevelopmentTransport.OperatorStream(mappingVersion,epoch))
                throw new ArgumentException("Distinct clocks and the bound operator stream are required");
            if(!WireCodec.Finite(maximumObservationAgeSeconds)||maximumObservationAgeSeconds<=0)
                throw new ArgumentOutOfRangeException("maximumObservationAgeSeconds");
            this.sessionId=sessionId;streamId=operatorStreamId;this.operatorClockId=operatorClockId;this.participantClockId=participantClockId;
            this.mappingVersion=mappingVersion;this.epoch=epoch;maximumObservationAge=maximumObservationAgeSeconds;
        }
        void Time(double now)
        {
            if(!WireCodec.Finite(now)||now<0||now<lastNow)
            {candidate=false;recovery=true;LastRejection="participant_clock_failed";throw new InvalidOperationException("New peer epoch required after clock failure");}
            lastNow=now;
        }
        public string BeginContext(int blockIndex,int segmentIndex,Phase currentPhase,double localNow,ClockSynchronizer phaseMapping=null)
        {
            Time(localNow);if(recovery)throw new InvalidOperationException("New peer epoch required after recovery latch");
            if(blockIndex<0||blockIndex>3||segmentIndex<0||(currentPhase!=Phase.BlockReady&&currentPhase!=Phase.Countdown))
            {
                if(challenge!=null){candidate=false;recovery=true;LastRejection="preflight_context_not_permitted";}
                throw new ArgumentException("Explicit BlockReady or Countdown context is required");
            }
            bool invalidTransition=challenge==null?currentPhase!=Phase.BlockReady:
                blockIndex<block||(blockIndex==block&&(segmentIndex!=segment||phase!=Phase.BlockReady||currentPhase!=Phase.Countdown))||
                (blockIndex>block&&(blockIndex!=block+1||phase!=Phase.Countdown||currentPhase!=Phase.BlockReady));
            if(invalidTransition)
            {candidate=false;recovery=true;LastRejection="invalid_context_transition";throw new InvalidOperationException("New peer epoch required for an invalid or resumed context");}
            if(challenge!=null&&blockIndex==block&&segmentIndex==segment&&currentPhase==Phase.Countdown&&
                !CandidateReady(localNow,phaseMapping))
                throw new InvalidOperationException("Current BlockReady candidate freshness must be revalidated before a Countdown challenge");
            // A new challenge is required even for a repeated call: an old reply
            // cannot prove readiness after a phase/context transition.
            block=blockIndex;segment=segmentIndex;phase=currentPhase;contextStarted=localNow;
            challenge=Guid.NewGuid().ToString("N");candidate=false;everReadyInContext=false;hasChallengeReceipt=false;LastRejection=null;return challenge;
        }
        public bool Observe(ViveDevelopmentPreflightProof proof,bool configuredEndpointMatched,double localReceived,ClockSynchronizer mapping)
        {
            Time(localReceived);if(recovery)return Reject("recovery_decision_required");
            if(challenge==null||proof==null)return Reject("missing_context_or_proof");
            if(!configuredEndpointMatched||proof.SessionId!=sessionId||proof.OperatorStreamId!=streamId||proof.OperatorClockId!=operatorClockId||
                proof.MappingVersion!=mappingVersion||proof.Epoch!=epoch)return Reject("peer_or_provenance_mismatch");
            if(proof.Block!=block||proof.Segment!=segment||proof.Phase!=phase||proof.Challenge!=challenge)
                return Reject("old_or_wrong_context_or_challenge");
            if(proof.Sequence<=lastSequence||proof.HeadObservationSequence<=lastHeadSequence||
                (hasNativeEyeSequence&&proof.NativeEyeFrameSequence<=lastNativeEyeSequence))return Reject("duplicate_or_old_proof");
            if(hasNativeEyeSequence&&(proof.HeadObservedSeconds<lastRemoteHead||proof.EyeObservedSeconds<lastRemoteEye))
            {candidate=false;recovery=true;return Reject("operator_observation_clock_rollback");}
            double earliest;
            if(hasChallengeReceipt&&proof.OperatorChallengeReceivedSeconds!=challengeReceivedRemote)return RejectCurrent("challenge_receipt_changed");
            if((!hasChallengeReceipt&&!TryObservationRange(proof.OperatorChallengeReceivedSeconds,localReceived,mapping,false,out earliest))||
                !TryObservationRange(proof.HeadObservedSeconds,localReceived,mapping,true,out earliest)||
                !TryObservationRange(proof.EyeObservedSeconds,localReceived,mapping,true,out earliest))return RejectCurrent("observation_time_unavailable_or_stale");
            double sent,uncertainty;
            if(!mapping.TryMapRemote(proof.OperatorSendMethodSeconds,out sent,out uncertainty)||sent+uncertainty>localReceived)
                return RejectCurrent("send_time_unavailable_or_future");
            // Commit sequence only after identity, current challenge and timing
            // pass. Explicit invalid data cancels readiness; it never fabricates
            // a sample. A loss after readiness requires a new peer epoch.
            lastSequence=proof.Sequence;lastHeadSequence=proof.HeadObservationSequence;lastNativeEyeSequence=proof.NativeEyeFrameSequence;hasNativeEyeSequence=true;
            lastRemoteHead=proof.HeadObservedSeconds;lastRemoteEye=proof.EyeObservedSeconds;
            challengeReceivedRemote=proof.OperatorChallengeReceivedSeconds;hasChallengeReceipt=true;
            if(!proof.HeadUsable||!proof.EyeUsable)return RejectCurrent("tracking_unavailable");
            candidate=true;everReadyInContext=true;LastRejection=null;return true;
        }
        bool TryObservationRange(double remote,double now,ClockSynchronizer mapping,bool requireFresh,out double earliest)
        {
            earliest=Double.NaN;
            if(mapping==null)return false;
            var snapshot=mapping.Snapshot();
            if(!snapshot.Available||snapshot.LocalClockId!=participantClockId||snapshot.RemoteClockId!=operatorClockId)return false;
            double mapped,uncertainty;
            if(!mapping.TryMapRemote(remote,out mapped,out uncertainty)||!WireCodec.Finite(uncertainty)||uncertainty<0)return false;
            earliest=mapped-uncertainty;
            return earliest>=contextStarted&&earliest>lastInvalidation&&mapped+uncertainty<=now&&(!requireFresh||now-earliest<=maximumObservationAge);
        }
        public bool CandidateReady(double localNow,ClockSynchronizer mapping)
        {
            Time(localNow);if(recovery||!candidate)return false;
            double earliest;
            if(TryObservationRange(lastRemoteHead,localNow,mapping,true,out earliest)&&TryObservationRange(lastRemoteEye,localNow,mapping,true,out earliest))return true;
            RejectCurrent("preflight_expired_or_clock_unavailable");return false;
        }
        // A caller must signal device, connection, context or application loss;
        // it cannot be inferred solely from silence until the freshness deadline.
        public void Invalidate(double localNow,string reason)
        {
            Time(localNow);if(String.IsNullOrWhiteSpace(reason))throw new ArgumentException("Explicit invalidation reason required");
            lastInvalidation=localNow;candidate=false;recovery=true;LastRejection="invalidated:"+reason;
        }
        bool Reject(string reason){LastRejection=reason;return false;}
        bool RejectCurrent(string reason)
        {candidate=false;if(everReadyInContext)recovery=true;return Reject(reason);}
    }
}
