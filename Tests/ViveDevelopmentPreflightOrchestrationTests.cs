using System;
using System.Net;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    // Pure in-memory byte exchange. No sockets, native providers, eye samples,
    // logs, or persistent/network side effects are used in these tests.
    public static class ViveDevelopmentPreflightOrchestrationTests
    {
        const string Session="synthetic-preflight",OperatorClock="op-clock",ParticipantClock="p-clock",Epoch="synthetic-epoch";
        static readonly string Mapping=ViveProEyeRehearsalCoordinates.MappingVersion;
        static readonly string Stream=ViveDevelopmentTransport.OperatorStream(Mapping,Epoch);
        static readonly IPEndPoint PEndpoint=new IPEndPoint(IPAddress.Loopback,35001),OEndpoint=new IPEndPoint(IPAddress.Loopback,35002);
        sealed class Clock : IClock
        {public double Now {get;set;} public string ClockId {get;private set;} public Clock(string id,double time){ClockId=id;Now=time;}}
        sealed class Provider : IViveProEyeProvider
        {public ViveProEyePoll Next;public ViveProEyePoll Poll(){return Next;}}

        static ClockSynchronizer Map(string local,string remote,double remoteMinusLocal)
        {
            var sync=new ClockSynchronizer(local,remote,new DevelopmentClockConfiguration(3,.2,.2,4,200));
            var peer=new PeerIdentity(Session,"fixture-peer",PeerRole.Operator,Stream,ControlSource.LiveOperator,remote);
            for(int i=0;i<3;i++)
            {
                double sent=1+i*.5;string exchange="synthetic-clock-"+i;
                Check.True(sync.RegisterRequest(exchange,sent));
                Check.True(sync.ObserveReply(WirePacket.TimeReply(peer,i,sent+remoteMinusLocal,exchange,local,sent,sent+remoteMinusLocal),sent),sync.LastRejection);
            }
            Check.True(sync.Snapshot().Available,sync.Snapshot().UnavailableReason);return sync;
        }
        static VivePreflightEndpointConfiguration Config(PeerRole role,bool allowRemote=false,string mapping=null,string epoch=Epoch)
        {
            string actualMapping=mapping??Mapping;
            return new VivePreflightEndpointConfiguration(role,role==PeerRole.Participant?PEndpoint:OEndpoint,role==PeerRole.Participant?OEndpoint:PEndpoint,
                allowRemote,Session,ViveDevelopmentTransport.OperatorStream(actualMapping,epoch),OperatorClock,ParticipantClock,actualMapping,epoch,"vive-pro-eye-no-serial",.5);
        }
        sealed class Pair
        {
            public readonly Clock PClock=new Clock(ParticipantClock,3),OClock=new Clock(OperatorClock,3.5);
            public readonly Provider Sensor=new Provider();
            public readonly VivePreflightParticipant Participant;
            public readonly VivePreflightOperator Operator;
            public readonly ViveProEyeHeadEyeSource Source;
            public TaskSnapshot Task=new TaskSnapshot(Session,0,0,null,null,null,Phase.BlockReady,Condition.A);
            public VivePreflightOperatorTask OperatorTask;
            long packetSequence;
            public Pair(bool emptyOperatorMap=false)
            {
                var pMap=Map(ParticipantClock,OperatorClock,.5);
                var oMap=emptyOperatorMap?new ClockSynchronizer(OperatorClock,ParticipantClock,new DevelopmentClockConfiguration(3,.2,.2,4,200)):Map(OperatorClock,ParticipantClock,-.5);
                Participant=new VivePreflightParticipant(Config(PeerRole.Participant),PClock,pMap);
                Operator=new VivePreflightOperator(Config(PeerRole.Operator),OClock,oMap);
                Source=new ViveProEyeHeadEyeSource(Session,0,0,Stream,OClock,Sensor,new ViveProEyeRehearsalCoordinates(),.5);
            }
            public void Confirm(){Participant.ConfirmSource(true);Operator.ConfirmSource(true);}
            public byte[] Begin(Phase phase=Phase.BlockReady)
            {
                Task=new TaskSnapshot(Session,0,0,null,null,null,phase,Condition.A);ReceiveTask(Task,true,true);
                return Participant.BeginContext(Task,true,true);
            }
            public bool ReceiveTask(TaskSnapshot task,bool accepted,bool live)
            {OperatorTask=new VivePreflightOperatorTask(task,accepted,live,packetSequence++,OClock.Now);return Operator.ObserveTask(OperatorTask);}
            public bool AcceptChallenge(byte[] bytes,IPEndPoint sender){return Operator.AcceptChallenge(bytes,sender,OperatorTask);}
            public byte[] CreateProof(ViveProEyeObservation observation,bool ready){return Operator.CreateProof(observation,ready,OperatorTask);}
            public ViveProEyeObservation Read(long headSequence,int eyeSequence,double headTime,double eyeTime,bool valid=true)
            {
                var ray=new ViveEyeRay(valid?2UL:0UL,false,valid,new Vec3(0,0,0),new Vec3(.5,0,1));
                Sensor.Next=new ViveProEyePoll(new ViveHeadFrame(headSequence,headTime,true,true,Rotation.Identity),
                    new ViveEyeFrame(eyeSequence,eyeSequence*8,eyeTime,false,null,null,ray),ViveEyeRuntimeState.Working,ViveEyeCalibrationState.ReportedReady,true,0);
                HeadEyeSample sample;Check.True(Source.TryRead(out sample));return Source.LastObservation;
            }
            public byte[] ReadyProof()
            {
                Confirm();var challenge=Begin();OClock.Now=3.56;Check.True(AcceptChallenge(challenge,PEndpoint),Operator.LastIssue);
                OClock.Now=3.57;var fresh=Read(1,1,3.57,3.57);OClock.Now=3.58;
                var bytes=CreateProof(fresh,true);Check.True(bytes!=null,Operator.LastIssue);
                PClock.Now=3.12;Check.True(Participant.AcceptProof(bytes,OEndpoint,Task,true,true),Participant.LastIssue);
                Check.True(Participant.CandidateReady(Task,true,true));return bytes;
            }
        }
        public static void Register(TestSuite suite)
        {
            suite.Run("VDPF orchestration requires manual confirmation and authoritative task",()=>{
                var pair=new Pair();
                Check.Throws<InvalidOperationException>(()=>pair.Participant.BeginContext(pair.Task,true,true));
                Check.Throws<InvalidOperationException>(()=>pair.Participant.ConfirmSource(false));
                Check.Throws<InvalidOperationException>(()=>pair.Operator.ConfirmSource(false));
                pair.Confirm();Check.Throws<InvalidOperationException>(()=>pair.Participant.ConfirmSource(true));
                Check.Throws<InvalidOperationException>(()=>pair.Participant.BeginContext(pair.Task,false,true));
                Check.True(!pair.Participant.CandidateReady(pair.Task,true,true)&&!pair.Participant.CanEnterCountdown&&!pair.Participant.CanRecordOrExport);
                Check.True(!pair.Operator.CanRecordOrExport&&!pair.Operator.HardwareCaptureTimeVerified);
            });
            suite.Run("VDPF orchestration exchanges real codec bytes from two fresh synthetic observations",()=>{
                var pair=new Pair();pair.Confirm();
                pair.OClock.Now=3.51;var before=pair.Read(1,1,3.51,3.51);
                var request=pair.Begin();pair.OClock.Now=3.56;
                Check.True(!pair.AcceptChallenge(request,OEndpoint));
                Check.True(pair.AcceptChallenge(request,PEndpoint),pair.Operator.LastIssue);
                Check.Equal(null,pair.CreateProof(before,true));
                pair.OClock.Now=3.565;var fresh=pair.Read(2,2,3.565,3.565);
                pair.OClock.Now=3.57;Check.True(pair.AcceptChallenge(request,PEndpoint));
                pair.OClock.Now=3.58;var proof=pair.CreateProof(fresh,true);Check.True(proof!=null,pair.Operator.LastIssue);
                ViveDevelopmentPreflightChallenge c;ViveDevelopmentPreflightProof decoded;string error;
                Check.True(ViveDevelopmentPreflightWire.TryDecode(proof,out c,out decoded,out error));
                Check.Equal(3.56,decoded.OperatorChallengeReceivedSeconds);
                Check.True(decoded.HeadObservedSeconds<3.57&&decoded.EyeObservedSeconds<3.57);
                pair.PClock.Now=3.12;Check.True(!pair.Participant.AcceptProof(proof,PEndpoint,pair.Task,true,true));
                Check.True(pair.Participant.AcceptProof(proof,OEndpoint,pair.Task,true,true),pair.Participant.LastIssue);
                Check.True(!pair.Participant.AcceptProof(proof,OEndpoint,pair.Task,true,true));Check.True(pair.Participant.CandidateReady(pair.Task,true,true));
                Check.True(!pair.Participant.CanEnterCountdown&&!pair.Participant.HardwareCaptureTimeVerified);
                Check.Equal(null,pair.CreateProof(fresh,true));
            });
            suite.Run("VDPF orchestration binds fresh proofs to exact phase and challenge",()=>{
                var pair=new Pair();var oldProof=pair.ReadyProof();
                pair.PClock.Now=3.15;var countdown=pair.Begin(Phase.Countdown);
                Check.True(!pair.Participant.CandidateReady(pair.Task,true,true));
                pair.OClock.Now=3.70;Check.True(pair.AcceptChallenge(countdown,PEndpoint),pair.Operator.LastIssue);
                pair.PClock.Now=3.16;Check.True(!pair.Participant.AcceptProof(oldProof,OEndpoint,pair.Task,true,true));
                pair.OClock.Now=3.72;var fresh=pair.Read(2,2,3.72,3.72);
                pair.OClock.Now=3.73;var proof=pair.CreateProof(fresh,true);Check.True(proof!=null,pair.Operator.LastIssue);
                pair.PClock.Now=3.25;Check.True(pair.Participant.AcceptProof(proof,OEndpoint,pair.Task,true,true),pair.Participant.LastIssue);
                Check.True(pair.Participant.CandidateReady(pair.Task,true,true));
                pair.PClock.Now=3.26;Check.True(!pair.Participant.AcceptProof(oldProof,OEndpoint,pair.Task,true,true));
                Check.True(pair.Participant.CandidateReady(pair.Task,true,true));Check.True(!pair.Participant.CanEnterCountdown);
            });
            suite.Run("VDPF rejects wrong source context stale challenge and absent mapping",()=>{
                var wrongMap=new Pair(true);wrongMap.Confirm();var request=wrongMap.Begin();wrongMap.OClock.Now=3.56;
                Check.True(!wrongMap.AcceptChallenge(request,PEndpoint));Check.True(!wrongMap.Participant.CandidateReady(wrongMap.Task,true,true));
                var stale=new Pair();stale.Confirm();var oldChallenge=stale.Begin();stale.OClock.Now=4.1;
                Check.True(!stale.AcceptChallenge(oldChallenge,PEndpoint));Check.True(!stale.Participant.CandidateReady(stale.Task,true,true));
                var pair=new Pair();pair.Confirm();var bytes=pair.Begin();pair.OClock.Now=3.56;Check.True(pair.AcceptChallenge(bytes,PEndpoint));
                pair.OClock.Now=3.57;
                var otherSource=new ViveProEyeHeadEyeSource(Session,0,0,"vive-pro-eye/operator/wrong",pair.OClock,pair.Sensor,new ViveProEyeRehearsalCoordinates());
                var sample=pair.Read(1,1,3.57,3.57);HeadEyeSample ignored;Check.True(otherSource.TryRead(out ignored));
                Check.Equal(null,pair.CreateProof(otherSource.LastObservation,true));
                Check.Equal(null,pair.CreateProof(sample,false));
                pair.ReceiveTask(new TaskSnapshot("wrong-session",0,0,null,null,null,Phase.BlockReady,Condition.A),true,true);
                Check.True(pair.Operator.RecoveryDecisionRequired);
                Check.Equal(null,pair.CreateProof(sample,true));
            });
            suite.Run("VDPF loss or expiry cannot silently restore candidate readiness",()=>{
                var pair=new Pair();var bytes=pair.ReadyProof();pair.Operator.SignalLoss("source disconnected");
                Check.True(pair.Operator.RecoveryDecisionRequired);pair.OClock.Now=3.6;Check.Equal(null,pair.CreateProof(pair.Read(2,2,3.6,3.6),true));
                pair.PClock.Now=3.13;pair.Participant.SignalLoss("connection lost");Check.True(pair.Participant.RecoveryDecisionRequired);
                Check.True(!pair.Participant.AcceptProof(bytes,OEndpoint,pair.Task,true,true)&&!pair.Participant.CandidateReady(pair.Task,true,true));
                Check.Throws<InvalidOperationException>(()=>pair.Participant.RetryChallenge(pair.Task,true,true));
                var expired=new Pair();expired.ReadyProof();expired.PClock.Now=3.8;
                Check.True(!expired.Participant.CandidateReady(expired.Task,true,true)&&expired.Participant.RecoveryDecisionRequired);
            });
            suite.Run("VDPF later proof cannot silently bridge an unpolled candidate gap",()=>{
                var pair=new Pair();pair.ReadyProof();
                pair.OClock.Now=3.68;var observation=pair.Read(2,2,3.68,3.68);
                pair.OClock.Now=3.69;var delayed=pair.CreateProof(observation,true);Check.True(delayed!=null,pair.Operator.LastIssue);
                // The receiver did not poll CandidateReady while the previously
                // accepted observation aged out. Late arrival must latch loss.
                pair.PClock.Now=3.8;Check.True(!pair.Participant.AcceptProof(delayed,OEndpoint,pair.Task,true,true));
                Check.True(pair.Participant.RecoveryDecisionRequired&&!pair.Participant.CandidateReady(pair.Task,true,true));
            });
            suite.Run("VDPF operator proof gaps or lost source require new epoch",()=>{
                var gap=new Pair();gap.ReadyProof();gap.OClock.Now=4.2;
                var later=gap.Read(2,2,4.2,4.2);gap.OClock.Now=4.21;
                Check.Equal(null,gap.CreateProof(later,true));Check.True(gap.Operator.RecoveryDecisionRequired);
                var unavailable=new Pair();unavailable.ReadyProof();unavailable.OClock.Now=3.6;
                var observation=unavailable.Read(2,2,3.6,3.6);
                Check.Equal(null,unavailable.CreateProof(observation,false));
                Check.True(unavailable.Operator.RecoveryDecisionRequired);
                Check.Equal(null,unavailable.CreateProof(observation,true));
            });
            suite.Run("VDPF operator waits for first task and cannot reenter old preflight after main task",()=>{
                var pair=new Pair();pair.Confirm();pair.ReceiveTask(null,false,true);
                Check.True(!pair.Operator.RecoveryDecisionRequired);
                var request=pair.Begin();pair.OClock.Now=3.56;Check.True(pair.AcceptChallenge(request,PEndpoint));
                pair.OClock.Now=3.57;var observation=pair.Read(1,1,3.57,3.57);pair.OClock.Now=3.58;
                var proof=pair.CreateProof(observation,true);Check.True(proof!=null);
                pair.PClock.Now=3.12;Check.True(pair.Participant.AcceptProof(proof,OEndpoint,pair.Task,true,true));
                pair.PClock.Now=3.15;var countdown=pair.Begin(Phase.Countdown);
                pair.OClock.Now=3.70;Check.True(pair.AcceptChallenge(countdown,PEndpoint));
                pair.OClock.Now=3.71;pair.ReceiveTask(new TaskSnapshot(Session,0,0,"trial",Colour.Red,Colour.Blue,Phase.MainTask,Condition.A),true,true);
                Check.True(!pair.AcceptChallenge(countdown,PEndpoint));
                pair.OClock.Now=3.72;pair.ReceiveTask(new TaskSnapshot(Session,0,0,null,null,null,Phase.Countdown,Condition.A),true,true);
                Check.True(pair.Operator.RecoveryDecisionRequired);
            });
            suite.Run("VDPF participant latches task exit or unconfirmed task without explicit loss signal",()=>{
                foreach(var kind in new[]{0,1,2,3,4})
                {
                    var pair=new Pair();pair.ReadyProof();pair.PClock.Now=3.13;
                    TaskSnapshot observed=pair.Task;bool confirmed=true;
                    if(kind==0)observed=new TaskSnapshot(Session,0,0,null,null,null,Phase.Paused,Condition.A);
                    if(kind==1)observed=new TaskSnapshot(Session,1,0,null,null,null,Phase.BlockReady,Condition.A);
                    if(kind==2)observed=new TaskSnapshot(Session,0,1,null,null,null,Phase.BlockReady,Condition.A);
                    if(kind==3)observed=new TaskSnapshot("wrong-session",0,0,null,null,null,Phase.BlockReady,Condition.A);
                    if(kind==4)confirmed=false;
                    Check.True(!pair.Participant.CandidateReady(observed,confirmed,true));
                    Check.True(pair.Participant.RecoveryDecisionRequired&&!pair.Participant.CanEnterCountdown);
                    Check.True(!pair.Participant.CandidateReady(pair.Task,true,true));
                }
            });
            suite.Run("VDPF every participant preflight operation verifies current authoritative context",()=>{
                for(int operation=0;operation<3;operation++)
                {
                    var pair=new Pair();var proof=pair.ReadyProof();pair.PClock.Now=3.13;
                    var paused=new TaskSnapshot(Session,0,0,null,null,null,Phase.Paused,Condition.A);
                    if(operation==0)Check.Throws<InvalidOperationException>(()=>pair.Participant.RetryChallenge(paused,true,true));
                    if(operation==1)Check.True(!pair.Participant.AcceptProof(proof,OEndpoint,paused,true,true));
                    if(operation==2)Check.True(!pair.Participant.CandidateReady(paused,true,true));
                    Check.True(pair.Participant.RecoveryDecisionRequired&&!pair.Participant.CanEnterCountdown);
                }
            });
            suite.Run("VDPF challenge expires before first proof on participant and operator",()=>{
                var participant=new Pair();participant.Confirm();participant.Begin();participant.PClock.Now=3.6;
                Check.True(!participant.Participant.CandidateReady(participant.Task,true,true));
                Check.True(participant.Participant.RecoveryDecisionRequired);
                var oper=new Pair();oper.Confirm();var request=oper.Begin();oper.OClock.Now=3.56;
                Check.True(oper.AcceptChallenge(request,PEndpoint));oper.OClock.Now=4.07;
                var observation=oper.Read(1,1,4.07,4.07);oper.OClock.Now=4.08;
                Check.Equal(null,oper.CreateProof(observation,true));
                Check.True(oper.Operator.RecoveryDecisionRequired);
                var retry=new Pair();retry.Confirm();var retryRequest=retry.Begin();retry.OClock.Now=3.56;
                Check.True(retry.AcceptChallenge(retryRequest,PEndpoint));retry.OClock.Now=4.07;
                Check.True(!retry.AcceptChallenge(retryRequest,PEndpoint));Check.True(retry.Operator.RecoveryDecisionRequired);
            });
            suite.Run("VDPF operator refuses a sample from a mismatched configured device",()=>{
                var pair=new Pair();pair.Participant.ConfirmSource(true);var request=pair.Participant.BeginContext(pair.Task,true,true);
                var config=new VivePreflightEndpointConfiguration(PeerRole.Operator,OEndpoint,PEndpoint,false,Session,Stream,
                    OperatorClock,ParticipantClock,Mapping,Epoch,"another-device",.5);
                var op=new VivePreflightOperator(config,pair.OClock,Map(OperatorClock,ParticipantClock,-.5));
                op.ConfirmSource(true);var opTask=new VivePreflightOperatorTask(pair.Task,true,true,0,pair.OClock.Now);op.ObserveTask(opTask);pair.OClock.Now=3.56;
                Check.True(op.AcceptChallenge(request,PEndpoint,opTask));pair.OClock.Now=3.57;
                var observation=pair.Read(1,1,3.57,3.57);pair.OClock.Now=3.58;
                Check.Equal(null,op.CreateProof(observation,true,opTask));Check.Equal("wrong_identity_context_or_stale_observation",op.LastIssue);
            });
            suite.Run("VDPF cached task cannot refresh TTL while fresh eye frames arrive",()=>{
                var pair=new Pair();pair.ReadyProof();pair.OClock.Now=3.8;
                var fresh=pair.Read(2,2,3.8,3.8);pair.OClock.Now=3.81;
                Check.True(pair.CreateProof(fresh,true)!=null,pair.Operator.LastIssue);
                pair.OClock.Now=3.99;Check.True(pair.Operator.ObserveTask(pair.OperatorTask));
                pair.OClock.Now=4.01;var later=pair.Read(3,3,4.01,4.01);pair.OClock.Now=4.02;
                Check.Equal(null,pair.CreateProof(later,true));Check.True(pair.Operator.RecoveryDecisionRequired);
            });
            suite.Run("VDPF new task packet cannot bridge an unpolled task gap in same or new phase",()=>{
                foreach(bool changePhase in new[]{false,true})
                {
                    var pair=new Pair();pair.ReadyProof();pair.OClock.Now=4.01;
                    var phase=changePhase?Phase.Countdown:Phase.BlockReady;
                    var task=new TaskSnapshot(Session,0,0,null,null,null,phase,Condition.A);
                    Check.True(!pair.ReceiveTask(task,true,true));Check.True(pair.Operator.RecoveryDecisionRequired);
                }
            });
            suite.Run("VDPF live source deselection in unchanged phase locks both peers",()=>{
                var p=new Pair();p.ReadyProof();p.PClock.Now=3.13;
                Check.True(!p.Participant.CandidateReady(p.Task,true,false));Check.True(p.Participant.RecoveryDecisionRequired);
                var o=new Pair();o.ReadyProof();o.OClock.Now=3.6;
                Check.True(!o.ReceiveTask(o.Task,true,false));Check.True(o.Operator.RecoveryDecisionRequired);
            });
            suite.Run("VDPF same-block condition change latches both peers within and across phases",()=>{
                foreach(bool changePhase in new[]{false,true})
                {
                    var p=new Pair();p.ReadyProof();p.PClock.Now=3.13;
                    var task=new TaskSnapshot(Session,0,0,null,null,null,changePhase?Phase.Countdown:Phase.BlockReady,Condition.B);
                    if(changePhase)Check.Throws<InvalidOperationException>(()=>p.Participant.BeginContext(task,true,true));
                    else Check.True(!p.Participant.CandidateReady(task,true,true));
                    Check.True(p.Participant.RecoveryDecisionRequired);
                    var o=new Pair();o.ReadyProof();o.OClock.Now=3.6;
                    Check.True(!o.ReceiveTask(task,true,true));Check.True(o.Operator.RecoveryDecisionRequired);
                }
            });
            suite.Run("VDPF endpoint configuration requires explicit remote opt-in and never claims authentication",()=>{
                var local=new IPEndPoint(IPAddress.Parse("192.0.2.1"),35001);var remote=new IPEndPoint(IPAddress.Parse("192.0.2.2"),35002);
                Check.Throws<ArgumentException>(()=>new VivePreflightEndpointConfiguration(PeerRole.Operator,local,remote,false,Session,Stream,OperatorClock,ParticipantClock,Mapping,Epoch,"vive-pro-eye-no-serial",.5));
                var configured=new VivePreflightEndpointConfiguration(PeerRole.Operator,local,remote,true,Session,Stream,OperatorClock,ParticipantClock,Mapping,Epoch,"vive-pro-eye-no-serial",.5);
                Check.True(configured.RemoteNetworkExplicitlyEnabled&&!configured.CryptographicallyAuthenticated);
            });
        }
    }
}
