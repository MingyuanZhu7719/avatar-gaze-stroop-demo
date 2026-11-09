using System;
using System.IO;
using System.Net;
using System.Threading;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    public static class ViveDevelopmentPreflightChannelTests
    {
        const string Session="preflight-channel-test", Epoch="test-epoch", PClockId="p-clock", OClockId="o-clock";
        static readonly string Mapping=ViveProEyeRehearsalCoordinates.MappingVersion;
        static readonly string Stream=ViveDevelopmentTransport.OperatorStream(Mapping,Epoch);
        sealed class Clock:IClock {double now;public bool Fail;public double Now{get{if(Fail)throw new IOException("synthetic clock failure");return now;}set{now=value;}} public string ClockId{get;private set;} public Clock(string id,double time){ClockId=id;Now=time;}}
        sealed class Provider:IViveProEyeProvider {public ViveProEyePoll Value;public ViveProEyePoll Poll(){return Value;}}
        static ClockSynchronizer Map(string local,string remote,double offset)
        {
            var sync=new ClockSynchronizer(local,remote,new DevelopmentClockConfiguration(3,.2,.2,4,200));
            var peer=new PeerIdentity(Session,"peer",PeerRole.Operator,Stream,ControlSource.LiveOperator,remote);
            for(int i=0;i<3;i++){double sent=1+i*.5;string id="clock-"+i;Check.True(sync.RegisterRequest(id,sent));
                Check.True(sync.ObserveReply(WirePacket.TimeReply(peer,i,sent+offset,id,local,sent,sent+offset),sent));}
            Check.True(sync.Snapshot().Available);return sync;
        }
        static VivePreflightEndpointConfiguration Config(PeerRole role,IPEndPoint p,IPEndPoint o,bool remote=false)
        {return new VivePreflightEndpointConfiguration(role,role==PeerRole.Participant?p:o,role==PeerRole.Participant?o:p,remote,Session,Stream,OClockId,PClockId,Mapping,Epoch,"vive-pro-eye-no-serial",.5);}
        sealed class Pair:IDisposable
        {
            public readonly IPEndPoint P,O;
            public readonly Clock Pc=new Clock(PClockId,3),Oc=new Clock(OClockId,3.5);
            public readonly Provider Sensor=new Provider();public readonly ViveProEyeHeadEyeSource Source;
            public readonly ViveDevelopmentPreflightChannel Pch,Och;
            public readonly TaskSnapshot Task=new TaskSnapshot(Session,0,0,null,null,null,Phase.BlockReady,Condition.A);
            public VivePreflightOperatorTask Ot;
            public Pair(IPEndPoint p,IPEndPoint o)
            {
                P=p;O=o;var pc=Config(PeerRole.Participant,p,o);var oc=Config(PeerRole.Operator,p,o);
                Source=new ViveProEyeHeadEyeSource(Session,0,0,Stream,Oc,Sensor,new ViveProEyeRehearsalCoordinates(),.5);
                Pch=new ViveDevelopmentPreflightChannel(pc,Pc,Map(PClockId,OClockId,.5));Och=new ViveDevelopmentPreflightChannel(oc,Oc,Map(OClockId,PClockId,-.5));
            }
            public void Confirm(){Pch.ConfirmSource(true);Och.ConfirmSource(true);Ot=new VivePreflightOperatorTask(Task,true,true,0,Oc.Now);Check.True(Och.ObserveOperatorTask(Ot));}
            public ViveProEyeObservation Observe(int frame,double t)
            {
                Oc.Now=t;var ray=new ViveEyeRay(2,false,true,new Vec3(0,0,0),new Vec3(.5,0,1));
                Sensor.Value=new ViveProEyePoll(new ViveHeadFrame(frame,t,true,true,Rotation.Identity),new ViveEyeFrame(frame,frame*8,t,false,null,null,ray),ViveEyeRuntimeState.Working,ViveEyeCalibrationState.ReportedReady,true,0);
                HeadEyeSample ignored;Check.True(Source.TryRead(out ignored));return Source.LastObservation;
            }
            public void Dispose(){Pch.Dispose();Och.Dispose();}
        }
        // The legacy parameter is retained for callers, but even an explicit
        // request now tests rejection and never opens a real socket.
        public static void Register(TestSuite suite,bool includeLoopback=false)
        {
            suite.Run("VDPF channel requires explicit start and trusted remote opt in",()=>{
                var local=new IPEndPoint(IPAddress.Parse("192.0.2.1"),34001);var remote=new IPEndPoint(IPAddress.Parse("192.0.2.2"),34002);
                var cfg=Config(PeerRole.Participant,local,remote,true);var clock=new Clock(PClockId,3);
                using(var ch=new ViveDevelopmentPreflightChannel(cfg,clock,Map(PClockId,OClockId,.5))){
                    Check.Throws<InvalidOperationException>(()=>ch.Start(false,false));Check.Throws<InvalidOperationException>(()=>ch.Start(true,false));
                    Check.Throws<InvalidOperationException>(()=>ch.Start(true,true));
                    var fake=new ViveInMemoryDatagramSocket(new IPEndPoint(IPAddress.Loopback,34001));
                    Check.Throws<ArgumentException>(()=>ch.StartForTest(fake));Check.True(!fake.Closed);
                    Check.True(!ch.Active&&!ch.CanEnterCountdown&&!ch.CryptographicallyAuthenticated);
                }
            });
            suite.Run("VDPF channel rejects unexpected and oversized datagrams",()=>{
                var p=new IPEndPoint(IPAddress.Loopback,34011);var o=new IPEndPoint(IPAddress.Loopback,34012);
                using(var pair=new Pair(p,o)){
                    pair.Confirm();var fake=new ViveInMemoryDatagramSocket(p);pair.Pch.StartForTest(fake);pair.Pch.Begin(pair.Task,true,true);
                    Check.True(!pair.Pch.PollParticipant(pair.Task,true,true));Check.Equal("no_datagram",pair.Pch.LastIssue);
                    fake.Incoming.Enqueue(Tuple.Create(new byte[]{1},new IPEndPoint(IPAddress.Loopback,34013)));
                    Check.True(!pair.Pch.PollParticipant(pair.Task,true,true));Check.Equal("unexpected_endpoint",pair.Pch.LastIssue);
                    fake.Incoming.Enqueue(Tuple.Create(new byte[2049],o));Check.True(!pair.Pch.PollParticipant(pair.Task,true,true));Check.Equal("packet_size",pair.Pch.LastIssue);
                    fake.MessageTooLarge=true;fake.Incoming.Enqueue(Tuple.Create(new byte[]{1},o));
                    Check.True(!pair.Pch.PollParticipant(pair.Task,true,true));Check.Equal("packet_size",pair.Pch.LastIssue);Check.True(pair.Pch.Active);
                    Check.True(!pair.Pch.CandidateReady(pair.Task,true,true)&&!pair.Pch.CanEnterCountdown);
                }
            });
            suite.Run("VDPF pre-start fault cannot bind or adopt a socket",()=>{
                var p=new IPEndPoint(IPAddress.Loopback,34017);var o=new IPEndPoint(IPAddress.Loopback,34018);
                using(var pair=new Pair(p,o)){
                    pair.Confirm();Check.Throws<InvalidOperationException>(()=>pair.Pch.ConfirmSource(true));
                    Check.True(pair.Pch.RecoveryDecisionRequired&&!pair.Pch.Active);
                    var fake=new ViveInMemoryDatagramSocket(p);Check.Throws<InvalidOperationException>(()=>pair.Pch.StartForTest(fake));
                    Check.Throws<InvalidOperationException>(()=>pair.Pch.Start(true,false));Check.True(!pair.Pch.Active&&!fake.Closed);
                }
            });
            suite.Run("VDPF pre-start nonexception recovery cannot bind a socket",()=>{
                var p=new IPEndPoint(IPAddress.Loopback,34019);var o=new IPEndPoint(IPAddress.Loopback,34020);
                using(var pair=new Pair(p,o)){
                    pair.Confirm();Check.True(!pair.Och.ObserveOperatorTask(null));
                    Check.True(pair.Och.RecoveryDecisionRequired&&!pair.Och.Active);
                    var fake=new ViveInMemoryDatagramSocket(o);Check.Throws<InvalidOperationException>(()=>pair.Och.StartForTest(fake));
                    Check.Throws<InvalidOperationException>(()=>pair.Och.Start(true,false));Check.True(!fake.Closed);
                }
            });
            suite.Run("VDPF channel derives its validator from the same configuration and role clock",()=>{
                var p=new IPEndPoint(IPAddress.Loopback,34015);var o=new IPEndPoint(IPAddress.Loopback,34016);
                var cfg=Config(PeerRole.Participant,p,o);
                Check.Throws<ArgumentException>(()=>new ViveDevelopmentPreflightChannel(cfg,new Clock(OClockId,3),Map(PClockId,OClockId,.5)));
                Check.Throws<ArgumentNullException>(()=>new ViveDevelopmentPreflightChannel(cfg,new Clock(PClockId,3),null));
            });
            suite.Run("VDPF channel socket loss latches recovery and forbids reuse",()=>{
                var p=new IPEndPoint(IPAddress.Loopback,34021);var o=new IPEndPoint(IPAddress.Loopback,34022);
                using(var pair=new Pair(p,o)){
                    pair.Confirm();var fake=new ViveInMemoryDatagramSocket(p){FailSend=true};pair.Pch.StartForTest(fake);
                    Check.True(!pair.Pch.Begin(pair.Task,true,true));Check.True(pair.Pch.RecoveryDecisionRequired&&!pair.Pch.Active&&fake.Closed);
                    Check.Throws<InvalidOperationException>(()=>pair.Pch.Start(true,false));
                }
            });
            suite.Run("VDPF channel treats nonblocking would-block receive as idle",()=>{
                var p=new IPEndPoint(IPAddress.Loopback,34023);var o=new IPEndPoint(IPAddress.Loopback,34024);
                using(var pair=new Pair(p,o)){
                    pair.Confirm();var fake=new ViveInMemoryDatagramSocket(p){WouldBlockPoll=true};pair.Pch.StartForTest(fake);
                    Check.True(pair.Pch.Begin(pair.Task,true,true));Check.True(!pair.Pch.PollParticipant(pair.Task,true,true));
                    Check.Equal("no_datagram",pair.Pch.LastIssue);Check.True(pair.Pch.Active&&!pair.Pch.RecoveryDecisionRequired);
                }
            });
            suite.Run("VDPF channel passes challenge and proof through bounded synthetic datagrams",()=>{
                var p=new IPEndPoint(IPAddress.Loopback,34025);var o=new IPEndPoint(IPAddress.Loopback,34026);
                using(var pair=new Pair(p,o)){
                    pair.Confirm();var pSocket=new ViveInMemoryDatagramSocket(p);var oSocket=new ViveInMemoryDatagramSocket(o);
                    pair.Pch.StartForTest(pSocket);pair.Och.StartForTest(oSocket);
                    Check.True(pair.Pch.Begin(pair.Task,true,true));Check.Equal(o,pSocket.Destination);
                    oSocket.Incoming.Enqueue(Tuple.Create(pSocket.Sent,p));pair.Oc.Now=3.56;
                    Check.True(pair.Och.PollOperator(pair.Ot),pair.Och.LastIssue);
                    var observation=pair.Observe(1,3.57);pair.Oc.Now=3.58;
                    Check.True(pair.Och.SendProof(observation,true,pair.Ot));Check.Equal(p,oSocket.Destination);
                    pSocket.Incoming.Enqueue(Tuple.Create(oSocket.Sent,o));pair.Pc.Now=3.12;
                    Check.True(pair.Pch.PollParticipant(pair.Task,true,true),pair.Pch.LastIssue);
                    Check.True(pair.Pch.CandidateReady(pair.Task,true,true));
                    // Replay is rejected by the existing freshness / sequence validator.
                    pSocket.Incoming.Enqueue(Tuple.Create(oSocket.Sent,o));
                    Check.True(!pair.Pch.PollParticipant(pair.Task,true,true));Check.True(!pair.Pch.CanEnterCountdown);
                    pair.Pc.Now=3.8;Check.True(!pair.Pch.CandidateReady(pair.Task,true,true));Check.True(pair.Pch.RecoveryDecisionRequired);
                }
            });
            suite.Run("VDPF channel phase or source changes latch candidate loss",()=>{
                var p=new IPEndPoint(IPAddress.Loopback,34027);var o=new IPEndPoint(IPAddress.Loopback,34028);
                foreach(bool phaseChange in new[]{false,true})using(var pair=new Pair(p,o)){
                    pair.Confirm();var ps=new ViveInMemoryDatagramSocket(p);var os=new ViveInMemoryDatagramSocket(o);pair.Pch.StartForTest(ps);pair.Och.StartForTest(os);
                    Check.True(pair.Pch.Begin(pair.Task,true,true));os.Incoming.Enqueue(Tuple.Create(ps.Sent,p));pair.Oc.Now=3.56;
                    Check.True(pair.Och.PollOperator(pair.Ot));var obs=pair.Observe(1,3.57);pair.Oc.Now=3.58;
                    Check.True(pair.Och.SendProof(obs,true,pair.Ot));ps.Incoming.Enqueue(Tuple.Create(os.Sent,o));pair.Pc.Now=3.12;
                    Check.True(pair.Pch.PollParticipant(pair.Task,true,true));Check.True(pair.Pch.CandidateReady(pair.Task,true,true));
                    var next=phaseChange?new TaskSnapshot(Session,0,0,null,null,null,Phase.Paused,Condition.A):pair.Task;
                    Check.True(!pair.Pch.CandidateReady(next,true,phaseChange));Check.True(pair.Pch.RecoveryDecisionRequired);
                    Check.Throws<InvalidOperationException>(()=>pair.Pch.CandidateReady(pair.Task,true,true));
                }
            });
            suite.Run("VDPF channel receive loss or explicit loss ends epoch",()=>{
                var p=new IPEndPoint(IPAddress.Loopback,34031);var o=new IPEndPoint(IPAddress.Loopback,34032);
                using(var pair=new Pair(p,o)){
                    pair.Confirm();var fake=new ViveInMemoryDatagramSocket(o){FailReceive=true};pair.Och.StartForTest(fake);
                    Check.True(!pair.Och.PollOperator(pair.Ot));Check.True(pair.Och.RecoveryDecisionRequired&&fake.Closed);
                    Check.Throws<InvalidOperationException>(()=>pair.Och.Start(true,false));
                }
                using(var pair=new Pair(p,o)){
                    pair.Confirm();var fake=new ViveInMemoryDatagramSocket(p);pair.Pch.StartForTest(fake);pair.Pch.SignalLoss("peer missing");
                    Check.True(pair.Pch.RecoveryDecisionRequired&&fake.Closed&&!pair.Pch.Active);
                }
            });
            suite.Run("VDPF channel always closes socket when the loss clock throws",()=>{
                var p=new IPEndPoint(IPAddress.Loopback,34041);var o=new IPEndPoint(IPAddress.Loopback,34042);
                using(var pair=new Pair(p,o)){
                    pair.Confirm();var fake=new ViveInMemoryDatagramSocket(o);pair.Och.StartForTest(fake);pair.Oc.Fail=true;
                    pair.Och.SignalLoss("synthetic socket fault");Check.True(fake.Closed&&!pair.Och.Active&&pair.Och.RecoveryDecisionRequired);
                    Check.True(pair.Och.LastIssue.Contains("IOException"));
                }
            });
            suite.Run("VDPF channel clock exception after candidate readiness closes and latches",()=>{
                var p=new IPEndPoint(IPAddress.Loopback,34043);var o=new IPEndPoint(IPAddress.Loopback,34044);
                using(var pair=new Pair(p,o)){
                    pair.Confirm();var ps=new ViveInMemoryDatagramSocket(p);var os=new ViveInMemoryDatagramSocket(o);pair.Pch.StartForTest(ps);pair.Och.StartForTest(os);
                    Check.True(pair.Pch.Begin(pair.Task,true,true));os.Incoming.Enqueue(Tuple.Create(ps.Sent,p));pair.Oc.Now=3.56;
                    Check.True(pair.Och.PollOperator(pair.Ot));var obs=pair.Observe(1,3.57);pair.Oc.Now=3.58;
                    Check.True(pair.Och.SendProof(obs,true,pair.Ot));ps.Incoming.Enqueue(Tuple.Create(os.Sent,o));pair.Pc.Now=3.12;
                    Check.True(pair.Pch.PollParticipant(pair.Task,true,true));Check.True(pair.Pch.CandidateReady(pair.Task,true,true));
                    pair.Pc.Fail=true;Check.Throws<IOException>(()=>pair.Pch.CandidateReady(pair.Task,true,true));
                    Check.True(ps.Closed&&pair.Pch.RecoveryDecisionRequired&&!pair.Pch.Active);pair.Pc.Fail=false;
                    Check.Throws<InvalidOperationException>(()=>pair.Pch.CandidateReady(pair.Task,true,true));
                }
                using(var pair=new Pair(p,o)){
                    pair.Confirm();var os=new ViveInMemoryDatagramSocket(o);pair.Och.StartForTest(os);pair.Oc.Fail=true;
                    Check.Throws<IOException>(()=>pair.Och.ObserveOperatorTask(pair.Ot));
                    Check.True(os.Closed&&pair.Och.RecoveryDecisionRequired&&!pair.Och.Active);pair.Oc.Fail=false;
                    Check.Throws<InvalidOperationException>(()=>pair.Och.ObserveOperatorTask(pair.Ot));
                }
            });
            suite.Run("VDPF channel prevents socket calls from a second thread",()=>{
                var p=new IPEndPoint(IPAddress.Loopback,34051);var o=new IPEndPoint(IPAddress.Loopback,34052);
                using(var pair=new Pair(p,o)){
                    pair.Confirm();var fake=new ViveInMemoryDatagramSocket(p);pair.Pch.StartForTest(fake);Exception failure=null;
                    var thread=new Thread(()=>{try{pair.Pch.Begin(pair.Task,true,true);}catch(Exception e){failure=e;}});
                    thread.Start();Check.True(thread.Join(1000));Check.True(failure is InvalidOperationException);
                    Check.Equal<byte[]>(null,fake.Sent);Check.True(pair.Pch.Active);
                }
            });
            suite.Run("VDPF channel refuses unauthenticated real start even on loopback",()=>{
                var p=new IPEndPoint(IPAddress.Loopback,34061);var o=new IPEndPoint(IPAddress.Loopback,34062);
                using(var pair=new Pair(p,o)){
                    pair.Confirm();
                    Check.Throws<InvalidOperationException>(()=>pair.Pch.Start(true,false));
                    Check.Throws<InvalidOperationException>(()=>pair.Och.Start(true,false));
                    Check.True(!pair.Pch.Active&&!pair.Och.Active&&!pair.Pch.CanEnterCountdown&&!pair.Och.CanEnterCountdown);
                }
            });
        }
    }
}

