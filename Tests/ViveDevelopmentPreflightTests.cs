using System;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    public static class ViveDevelopmentPreflightTests
    {
        const string Session="preflight-session", OperatorClock="operator-clock", ParticipantClock="participant-clock", Mapping="explicit-mapping-v1", Epoch="epoch-a";
        static readonly string Stream=ViveDevelopmentTransport.OperatorStream(Mapping,Epoch);

        static ClockSynchronizer MappingClock(string local=ParticipantClock,string remote=OperatorClock)
        {
            var mapping=new ClockSynchronizer(local,remote,new DevelopmentClockConfiguration(3,.2,.2,2,200));
            var peer=new PeerIdentity(Session,"operator",PeerRole.Operator,Stream,ControlSource.LiveOperator,remote);
            for(int i=0;i<3;i++)
            {
                string id="proof-clock-"+i;double t=i;
                Check.True(mapping.RegisterRequest(id,t));
                Check.True(mapping.ObserveReply(WirePacket.TimeReply(peer,i,t+.5,id,local,t,t+.5),t),mapping.LastRejection);
            }
            Check.True(mapping.Snapshot().Available,mapping.Snapshot().UnavailableReason);return mapping;
        }
        static ViveDevelopmentPreflight Gate(double maximumAge=.5)
        {return new ViveDevelopmentPreflight(Session,Stream,OperatorClock,ParticipantClock,Mapping,Epoch,maximumAge);}
        static ViveDevelopmentPreflightProof Proof(string challenge,Phase phase=Phase.BlockReady,int block=0,int segment=0,long sequence=10,
            long headSequence=10,int eyeSequence=10,double challengeReceived=3.6,double head=3.65,double eye=3.67,double sent=3.68,
            bool headUsable=true,bool eyeUsable=true,string session=Session,string stream=null,string remoteClock=OperatorClock,
            string mapping=Mapping,string epoch=Epoch)
        {return new ViveDevelopmentPreflightProof(session,stream??Stream,remoteClock,mapping,epoch,challenge,block,segment,phase,sequence,
            headSequence,eyeSequence,challengeReceived,head,eye,sent,headUsable,eyeUsable);}

        public static void Register(TestSuite suite)
        {
            suite.Run("VIVE preflight fails closed until a fresh current challenge proof, never admits countdown",()=>{
                var gate=Gate();var mapping=MappingClock();var challenge=gate.BeginContext(0,0,Phase.BlockReady,3);
                Check.True(challenge.Length==32&&!gate.CandidateReady(3.01,mapping));
                Check.True(!gate.CanEnterCountdown&&!gate.IsConnectedToWireOrApp&&!gate.HardwareCaptureTimeVerified);
                var proof=Proof(challenge);Check.True(!proof.HardwareCaptureTimeVerified);
                Check.True(gate.Observe(proof,true,3.2,mapping),gate.LastRejection);
                Check.True(gate.CandidateReady(3.21,mapping));
                Check.True(!gate.CanEnterCountdown&&!gate.IsConnectedToWireOrApp);
            });
            suite.Run("VIVE preflight checks endpoint identity mapping epoch and exact task context",()=>{
                var gate=Gate();var mapping=MappingClock();var challenge=gate.BeginContext(0,0,Phase.BlockReady,3);
                Check.True(!gate.Observe(Proof(challenge),false,3.2,mapping));
                Check.True(!gate.Observe(Proof(challenge,session:"other-session"),true,3.2,mapping));
                Check.True(!gate.Observe(Proof(challenge,stream:"vive-pro-eye/operator/other"),true,3.2,mapping));
                Check.True(!gate.Observe(Proof(challenge,remoteClock:"other-clock"),true,3.2,mapping));
                Check.True(!gate.Observe(Proof(challenge,mapping:"other-mapping"),true,3.2,mapping));
                Check.True(!gate.Observe(Proof(challenge,epoch:"other-epoch"),true,3.2,mapping));
                Check.True(!gate.Observe(Proof(challenge,phase:Phase.Countdown),true,3.2,mapping));
                Check.True(!gate.Observe(Proof(challenge,block:1),true,3.2,mapping));
                Check.True(!gate.Observe(Proof(challenge,segment:1),true,3.2,mapping));
                Check.True(!gate.Observe(Proof("wrong-challenge"),true,3.2,mapping));
                Check.True(!gate.CandidateReady(3.2,mapping));
                Check.True(gate.Observe(Proof(challenge),true,3.2,mapping),gate.LastRejection);
            });
            suite.Run("VIVE preflight phase transitions require a new challenge and later independent frames",()=>{
                var gate=Gate();var mapping=MappingClock();var oldChallenge=gate.BeginContext(0,0,Phase.BlockReady,3);
                var old=Proof(oldChallenge);Check.True(gate.Observe(old,true,3.2,mapping),gate.LastRejection);
                var current=gate.BeginContext(0,0,Phase.Countdown,3.3,mapping);Check.True(current!=oldChallenge);
                Check.True(!gate.CandidateReady(3.31,mapping));
                Check.True(!gate.Observe(old,true,3.32,mapping));
                Check.True(!gate.Observe(Proof(current,phase:Phase.Countdown,sequence:10,headSequence:11,eyeSequence:11,
                    challengeReceived:3.9,head:3.95,eye:3.97,sent:3.98),true,3.5,mapping));
                Check.True(!gate.Observe(Proof(current,phase:Phase.Countdown,sequence:11,headSequence:10,eyeSequence:11,
                    challengeReceived:3.9,head:3.95,eye:3.97,sent:3.98),true,3.5,mapping));
                Check.True(!gate.Observe(Proof(current,phase:Phase.Countdown,sequence:11,headSequence:11,eyeSequence:10,
                    challengeReceived:3.9,head:3.95,eye:3.97,sent:3.98),true,3.5,mapping));
                var next=Proof(current,phase:Phase.Countdown,sequence:11,headSequence:11,eyeSequence:11,
                    challengeReceived:3.9,head:3.95,eye:3.97,sent:3.98);
                Check.True(gate.Observe(next,true,3.5,mapping),gate.LastRejection);
                Check.True(!gate.Observe(old,true,3.51,mapping));
                Check.True(gate.CandidateReady(3.52,mapping),"Delayed prior-context data cannot destroy the newer valid candidate");
                Check.True(!gate.Observe(next,true,3.53,mapping));
                Check.True(gate.CandidateReady(3.54,mapping),"Duplicate proof cannot mutate the candidate");
            });
            suite.Run("VIVE preflight requires independent head and eye observations after challenge receipt",()=>{
                Check.Throws<ArgumentException>(()=>Proof("challenge",challengeReceived:3.6,head:3.6,eye:3.67));
                Check.Throws<ArgumentException>(()=>Proof("challenge",challengeReceived:3.6,head:3.65,eye:3.6));
                Check.Throws<ArgumentException>(()=>Proof("challenge",challengeReceived:3.6,head:3.65,eye:3.67,sent:3.66));
                Check.Throws<ArgumentException>(()=>Proof("challenge",head:Double.NaN));
                Check.Throws<ArgumentException>(()=>Proof("challenge",headSequence:-1));
                var gate=Gate();var challenge=gate.BeginContext(0,0,Phase.BlockReady,3);
                Check.True(!gate.Observe(Proof(challenge),true,3.2,null));
                Check.True(!gate.Observe(Proof(challenge),true,3.2,MappingClock("wrong-local",OperatorClock)));
                Check.True(!gate.Observe(Proof(challenge),true,3.2,MappingClock(ParticipantClock,"wrong-remote")));
                Check.True(!gate.CandidateReady(3.2,null));
            });
            suite.Run("VIVE preflight rejects old and future observations using clock uncertainty",()=>{
                var mapping=MappingClock();var gate=Gate();var challenge=gate.BeginContext(0,0,Phase.BlockReady,3);
                // An observation exactly at issuance is unsafe because mapping has uncertainty.
                Check.True(!gate.Observe(Proof(challenge,challengeReceived:3.5,head:3.65,eye:3.67,sent:3.68),true,3.2,mapping));
                Check.True(!gate.Observe(Proof(challenge,challengeReceived:3.6,head:3.65,eye:3.67,sent:3.75),true,3.2,mapping));
                Check.True(!gate.CandidateReady(3.2,mapping));
                var staleGate=Gate(.05);var staleChallenge=staleGate.BeginContext(0,0,Phase.BlockReady,3);
                Check.True(!staleGate.Observe(Proof(staleChallenge),true,3.2,mapping));
                Check.True(!staleGate.CandidateReady(3.2,mapping));
            });
            suite.Run("VIVE preflight current invalidity loss and expiry latch recovery until a new epoch",()=>{
                var mapping=MappingClock();var gate=Gate();var challenge=gate.BeginContext(0,0,Phase.BlockReady,3);
                Check.True(gate.Observe(Proof(challenge),true,3.2,mapping),gate.LastRejection);
                var invalid=Proof(challenge,sequence:11,headSequence:11,eyeSequence:11,headUsable:false,
                    challengeReceived:3.6,head:3.70,eye:3.72,sent:3.73);
                Check.True(!gate.Observe(invalid,true,3.3,mapping));Check.True(gate.RecoveryDecisionRequired);
                Check.True(!gate.CandidateReady(3.31,mapping));
                Check.True(!gate.Observe(Proof(challenge,sequence:12,headSequence:12,eyeSequence:12,
                    challengeReceived:3.6,head:3.8,eye:3.82,sent:3.83),true,3.4,mapping));
                Check.Throws<InvalidOperationException>(()=>gate.BeginContext(0,0,Phase.Countdown,3.5));

                var expired=Gate();var ch=expired.BeginContext(0,0,Phase.BlockReady,3);
                Check.True(expired.Observe(Proof(ch),true,3.2,mapping),expired.LastRejection);
                Check.True(!expired.CandidateReady(3.9,mapping));Check.True(expired.RecoveryDecisionRequired);
                var lost=Gate();var ch2=lost.BeginContext(0,0,Phase.BlockReady,3);
                Check.True(lost.Observe(Proof(ch2),true,3.2,mapping),lost.LastRejection);
                lost.Invalidate(3.21,"connection lost");Check.True(lost.RecoveryDecisionRequired&&!lost.CandidateReady(3.22,mapping));
            });
            suite.Run("VIVE preflight participant clock rollback latches failure",()=>{
                var gate=Gate();var mapping=MappingClock();var challenge=gate.BeginContext(0,0,Phase.BlockReady,3);
                Check.True(gate.Observe(Proof(challenge),true,3.2,mapping),gate.LastRejection);
                Check.Throws<InvalidOperationException>(()=>gate.CandidateReady(3.1,mapping));
                Check.True(gate.RecoveryDecisionRequired&&!gate.CanEnterCountdown);
            });
            suite.Run("VIVE preflight loss before readiness still requires a new peer epoch",()=>{
                var mapping=MappingClock();var gate=Gate();var challenge=gate.BeginContext(0,0,Phase.BlockReady,3);
                gate.Invalidate(3.1,"device_or_connection_lost");
                Check.True(gate.RecoveryDecisionRequired&&!gate.CandidateReady(3.11,mapping));
                Check.True(!gate.Observe(Proof(challenge),true,3.2,mapping));
                Check.Throws<InvalidOperationException>(()=>gate.BeginContext(0,0,Phase.Countdown,3.3,mapping));
                var beforeContext=Gate();beforeContext.Invalidate(0,"connection_lost");
                Check.Throws<InvalidOperationException>(()=>beforeContext.BeginContext(0,0,Phase.BlockReady,1));
            });
            suite.Run("VIVE preflight non-preflight phase after ready revokes and latches candidate",()=>{
                var mapping=MappingClock();
                foreach(var phase in new[]{Phase.Paused,Phase.MainTask,Phase.BlockPreparation,Phase.Ended})
                {
                    var gate=Gate();var challenge=gate.BeginContext(0,0,Phase.BlockReady,3);
                    Check.True(gate.Observe(Proof(challenge),true,3.2,mapping),gate.LastRejection);
                    Check.Throws<ArgumentException>(()=>gate.BeginContext(0,0,phase,3.3,mapping));
                    Check.True(gate.RecoveryDecisionRequired&&!gate.CandidateReady(3.31,mapping)&&!gate.CanEnterCountdown);
                }
            });
            suite.Run("VIVE preflight accepts renewed samples with one challenge receipt but latches changed receipt",()=>{
                var gate=Gate();var mapping=MappingClock();var challenge=gate.BeginContext(0,0,Phase.BlockReady,3);
                Check.True(gate.Observe(Proof(challenge),true,3.2,mapping),gate.LastRejection);
                var renewal=Proof(challenge,sequence:11,headSequence:11,eyeSequence:11,
                    challengeReceived:3.6,head:4.05,eye:4.07,sent:4.08);
                Check.True(gate.Observe(renewal,true,3.6,mapping),gate.LastRejection);
                Check.True(gate.CandidateReady(3.61,mapping));
                var alteredReceipt=Proof(challenge,sequence:12,headSequence:12,eyeSequence:12,
                    challengeReceived:3.62,head:4.1,eye:4.12,sent:4.13);
                Check.True(!gate.Observe(alteredReceipt,true,3.65,mapping));
                Check.Equal("challenge_receipt_changed",gate.LastRejection);
                Check.True(gate.RecoveryDecisionRequired&&!gate.CandidateReady(3.66,mapping));
            });
            suite.Run("VIVE preflight source-time rollback and unavailable mapping latch a ready candidate",()=>{
                var mapping=MappingClock();var gate=Gate();var challenge=gate.BeginContext(0,0,Phase.BlockReady,3);
                Check.True(gate.Observe(Proof(challenge),true,3.2,mapping),gate.LastRejection);
                var rollback=Proof(challenge,sequence:11,headSequence:11,eyeSequence:11,head:3.64,eye:3.70,sent:3.73);
                Check.True(!gate.Observe(rollback,true,3.3,mapping));
                Check.Equal("operator_observation_clock_rollback",gate.LastRejection);Check.True(gate.RecoveryDecisionRequired);
                var gate2=Gate();var challenge2=gate2.BeginContext(0,0,Phase.BlockReady,3);
                Check.True(gate2.Observe(Proof(challenge2),true,3.2,mapping),gate2.LastRejection);
                Check.True(!gate2.CandidateReady(3.21,null));Check.True(gate2.RecoveryDecisionRequired);
            });
            suite.Run("VIVE preflight refuses countdown transitions without fresh BlockReady proof",()=>{
                var mapping=MappingClock();var withoutReady=Gate();withoutReady.BeginContext(0,0,Phase.BlockReady,3);
                Check.Throws<InvalidOperationException>(()=>withoutReady.BeginContext(0,0,Phase.Countdown,3.1,mapping));
                Check.True(!withoutReady.CanEnterCountdown&&!withoutReady.CandidateReady(3.11,mapping));
                var expired=Gate();var challenge=expired.BeginContext(0,0,Phase.BlockReady,3);
                Check.True(expired.Observe(Proof(challenge),true,3.2,mapping),expired.LastRejection);
                Check.Throws<InvalidOperationException>(()=>expired.BeginContext(0,0,Phase.Countdown,3.9,mapping));
                Check.True(expired.RecoveryDecisionRequired&&!expired.CanEnterCountdown);
                var noMapping=Gate();var challenge2=noMapping.BeginContext(0,0,Phase.BlockReady,3);
                Check.True(noMapping.Observe(Proof(challenge2),true,3.2,mapping),noMapping.LastRejection);
                Check.Throws<InvalidOperationException>(()=>noMapping.BeginContext(0,0,Phase.Countdown,3.3));
                Check.True(noMapping.RecoveryDecisionRequired&&!noMapping.CanEnterCountdown);
            });
            suite.Run("VIVE preflight rejects invalid phase and segment transitions for an epoch",()=>{
                var initial=Gate();Check.Throws<InvalidOperationException>(()=>initial.BeginContext(0,0,Phase.Countdown,3));
                Check.True(initial.RecoveryDecisionRequired);
                var repeated=Gate();repeated.BeginContext(0,0,Phase.BlockReady,3);
                Check.Throws<InvalidOperationException>(()=>repeated.BeginContext(0,0,Phase.BlockReady,3.1));
                Check.True(repeated.RecoveryDecisionRequired);
                var resumed=Gate();resumed.BeginContext(0,0,Phase.BlockReady,3);
                Check.Throws<InvalidOperationException>(()=>resumed.BeginContext(0,1,Phase.Countdown,3.1,MappingClock()));
                Check.True(resumed.RecoveryDecisionRequired);
                var backward=Gate();backward.BeginContext(1,0,Phase.BlockReady,3);
                Check.Throws<InvalidOperationException>(()=>backward.BeginContext(0,0,Phase.BlockReady,3.1));
                Check.True(backward.RecoveryDecisionRequired);
            });
            suite.Run("VIVE preflight requires ordered block transitions with fresh challenge per block",()=>{
                var mapping=MappingClock();
                var premature=Gate();premature.BeginContext(0,0,Phase.BlockReady,3);
                Check.Throws<InvalidOperationException>(()=>premature.BeginContext(1,0,Phase.BlockReady,3.1));
                Check.True(premature.RecoveryDecisionRequired);
                var skipped=Gate();var skipReady=skipped.BeginContext(0,0,Phase.BlockReady,3);
                Check.True(skipped.Observe(Proof(skipReady),true,3.2,mapping),skipped.LastRejection);
                skipped.BeginContext(0,0,Phase.Countdown,3.3,mapping);
                Check.Throws<InvalidOperationException>(()=>skipped.BeginContext(2,0,Phase.BlockReady,3.4));
                Check.True(skipped.RecoveryDecisionRequired);

                var ordered=Gate();var first=ordered.BeginContext(0,0,Phase.BlockReady,3);
                var old=Proof(first);Check.True(ordered.Observe(old,true,3.2,mapping),ordered.LastRejection);
                var countdown=ordered.BeginContext(0,0,Phase.Countdown,3.3,mapping);
                var cd=Proof(countdown,phase:Phase.Countdown,sequence:11,headSequence:11,eyeSequence:11,
                    challengeReceived:3.9,head:3.95,eye:3.97,sent:3.98);
                Check.True(ordered.Observe(cd,true,3.5,mapping),ordered.LastRejection);
                var second=ordered.BeginContext(1,0,Phase.BlockReady,3.6,mapping);
                Check.True(second!=countdown&&second!=first&&!ordered.CandidateReady(3.61,mapping));
                Check.True(!ordered.Observe(cd,true,3.62,mapping));
                var next=Proof(second,block:1,sequence:12,headSequence:12,eyeSequence:12,
                    challengeReceived:4.2,head:4.25,eye:4.27,sent:4.28);
                Check.True(ordered.Observe(next,true,3.8,mapping),ordered.LastRejection);
                Check.True(ordered.CandidateReady(3.81,mapping));Check.True(!ordered.CanEnterCountdown);
            });
        }
    }
}
