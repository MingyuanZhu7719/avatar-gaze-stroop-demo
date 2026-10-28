using System;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using AvatarStroop.Core;

namespace AvatarStroop.Tests
{
    public static class FaultTransportTests
    {
        sealed class FaultSocket : IUdpSocket
        {
            public string FailOperation;
            public Exception Failure;
            public int Polls,Receives,Sends,Closes;
            public IPEndPoint LocalEndpoint {get{return new IPEndPoint(IPAddress.Loopback,12001);}}
            void Fail(string operation){if(operation==FailOperation)throw Failure;}
            public bool Poll(){Polls++;Fail("poll");return true;}
            public byte[] Receive(ref IPEndPoint sender){Receives++;Fail("receive");sender=new IPEndPoint(IPAddress.Loopback,12002);return WireCodec.Encode(WirePacket.Hello(Peer(),0,0,SessionConfiguration.ProtocolVersion));}
            public void Send(byte[] bytes,IPEndPoint remote){Sends++;Fail("send");}
            public void Dispose(){Closes++;}
        }
        static UdpPeer Injected(FaultSocket socket)
        {return new UdpPeer(socket.LocalEndpoint,new IPEndPoint(IPAddress.Loopback,12002),false,socket);}
        static PeerIdentity Peer(string session="wire-session",string peer="operator-process",PeerRole role=PeerRole.Operator,string source="simulator/operator",ControlSource origin=ControlSource.Simulator,string clock="remote-clock")
        {return new PeerIdentity(session,peer,role,source,origin,clock);}
        static HeadEyeSample Sample(long sequence,double capture,double sent,bool head=true,bool eye=true,string device="operator-device",int block=0,int segment=0)
        {return new HeadEyeSample("wire-session","simulator/operator",device,"remote-clock",block,segment,sequence,capture,sent,ControlSource.Simulator,StreamRole.OperatorControl,Rotation.Identity,new Vec3(0,0,1),head,eye);}
        static WirePacket Track(long packetSequence,long sampleSequence,double time,bool head=true,bool eye=true)
        {return WirePacket.Tracking(Peer(),packetSequence,Sample(sampleSequence,time,time,head,eye));}
        static DevelopmentClockConfiguration ClockConfig(double span=5,double horizon=100)
        {return new DevelopmentClockConfiguration(3,span,1,horizon,50);}
        static ClockSynchronizer Map(double offset=0)
        {var map=new ClockSynchronizer("local-clock","remote-clock",ClockConfig());Exchange(map,"first",0,offset,1,.01,.01,.002);Exchange(map,"second",10,offset,1,.01,.01,.002);Exchange(map,"third",20,offset,1,.01,.01,.002);return map;}
        static void Exchange(ClockSynchronizer map,string id,double t0,double offset,double rate,double outbound,double inbound,double processing)
        {
            Check.True(map.RegisterRequest(id,t0));
            double t1=offset+rate*(t0+outbound),t2=offset+rate*(t0+outbound+processing),t3=t0+outbound+processing+inbound;
            Check.True(map.ObserveReply(WirePacket.TimeReply(Peer(),0,t2,id,"local-clock",t0,t1),t3),map.LastRejection);
        }
        static ReceiveMonitor Monitor(PeerIdentity peer=null)
        {return new ReceiveMonitor(peer??Peer(),new DevelopmentFaultConfiguration(.5,.2,.2),"operator-device","local-clock");}
        static void Hello(ReceiveMonitor monitor,double now=0)
        {Check.True(monitor.Observe(WirePacket.Hello(Peer(),0,now,SessionConfiguration.ProtocolVersion),now).Accepted);}
        static WirePacket Round(WirePacket packet)
        {WirePacket decoded;string error;Check.True(WireCodec.TryDecode(WireCodec.Encode(packet),out decoded,out error),error);return decoded;}
        static bool Warning(FaultSnapshot snapshot,FaultKind kind)
        {foreach(var w in snapshot.Warnings)if(w.Kind==kind)return true;return false;}
        static void Corrupt(byte[] bytes,int field,string value)
        {var f=Encoding.ASCII.GetString(bytes).Split('|');f[field]=value;WirePacket packet;string error;Check.True(!WireCodec.TryDecode(Encoding.ASCII.GetBytes(String.Join("|",f)),out packet,out error));Check.True(packet==null && error!=null);}
        public static void Register(TestSuite suite)
        {
            foreach(var operation in new[]{"poll","receive","send"})
            {
                suite.Run("M3 UDP "+operation+" connection reset closes epoch and prevents all later socket I/O",()=>{
                    var boundary=new FaultSocket{FailOperation=operation,Failure=new SocketException((int)SocketError.ConnectionReset)};
                    using(var peer=Injected(boundary))
                    {
                        var packet=WirePacket.Hello(Peer(),0,0,SessionConfiguration.ProtocolVersion);
                        UdpConnectionFault fault;
                        if(operation=="send")
                        {var result=peer.Send(packet);Check.True(!result.Sent);fault=result.ConnectionFault;}
                        else
                        {var result=peer.Poll();Check.True(!result.Accepted&&result.Packet==null&&result.RemoteEndpoint==null);fault=result.ConnectionFault;Check.Equal("udp_connection_reset:"+operation,result.Error);}
                        Check.True(fault!=null&&Object.ReferenceEquals(fault,peer.ConnectionFault));
                        Check.Equal(SocketError.ConnectionReset,fault.SocketErrorCode);Check.Equal(operation,fault.Operation);Check.Equal(1,boundary.Closes);
                        int polls=boundary.Polls,receives=boundary.Receives,sends=boundary.Sends;
                        Check.Equal(operation=="send"?0:1,polls);Check.Equal(operation=="receive"?1:0,receives);Check.Equal(operation=="send"?1:0,sends);
                        boundary.Failure=new InvalidOperationException("A closed socket must never be touched");
                        for(int i=0;i<3;i++)
                        {
                            Check.True(peer.Poll()==null);var after=peer.Send(packet);
                            Check.True(!after.Sent&&Object.ReferenceEquals(fault,after.ConnectionFault));
                            Check.Equal(12001,peer.LocalEndpoint.Port);
                        }
                        peer.Dispose();peer.Dispose();Check.Equal(1,boundary.Closes);
                        Check.Equal(polls,boundary.Polls);Check.Equal(receives,boundary.Receives);Check.Equal(sends,boundary.Sends);
                    }
                    Check.Equal(1,boundary.Closes);
                });
                suite.Run("M3 UDP "+operation+" unrelated exceptions propagate unchanged",()=>{
                    foreach(var failure in new Exception[]{new SocketException((int)SocketError.NetworkReset),new SocketException((int)SocketError.WouldBlock),new SocketException((int)SocketError.AccessDenied),new InvalidOperationException("programming failure")})
                    {
                        var boundary=new FaultSocket{FailOperation=operation,Failure=failure};
                        using(var peer=Injected(boundary))
                        {
                            Exception observed=null;
                            try {if(operation=="send")peer.Send(WirePacket.Hello(Peer(),0,0,SessionConfiguration.ProtocolVersion));else peer.Poll();}
                            catch(Exception error){observed=error;}
                            Check.True(Object.ReferenceEquals(failure,observed));Check.True(peer.ConnectionFault==null);Check.Equal(0,boundary.Closes);
                        }
                        Check.Equal(1,boundary.Closes);
                    }
                });
            }
            suite.Run("M3 explicit UDP fault immediately invalidates fresh axes without forging rejected traffic",()=>{
                var m=Monitor();var map=Map();Hello(m);m.Observe(Track(1,0,.01),.02,map);
                Check.True(m.CurrentSample(.03,map)!=null);var raw=m.LastSample;
                m.RecordConnectionFault("udp_connection_reset:receive",.04);
                var fault=m.Snapshot(.04,map);Check.True(fault.RecoveryDecisionRequired&&fault.LocalHeadFresh&&fault.LocalEyeFresh);
                Check.True(!fault.HeadUsable&&!fault.EyeUsable&&m.CurrentSample(.04,map)==null);
                Check.True(Object.ReferenceEquals(raw,m.LastSample));Check.Equal(0L,fault.RejectedPackets);Check.True(!fault.AutomaticPauseOrFallbackSelected);
                Check.True(Warning(fault,FaultKind.ConnectionFault)&&Warning(fault,FaultKind.RecoveryDecisionRequired));
                m.RecordConnectionFault("udp_connection_reset:send",.05);
                int events=0;foreach(var item in m.Events)if(item.Kind=="connection_fault"){events++;Check.Equal("udp_connection_reset:receive",item.Detail);Check.Near(.04,item.LocalSeconds);}
                Check.Equal(1,events);
                Check.True(m.Observe(WirePacket.Hello(Peer(),2,.06,SessionConfiguration.ProtocolVersion),.07,map).Accepted);
                Check.True(m.Observe(Track(3,1,.08),.09,map).Accepted);Check.True(m.CurrentSample(.1,map)==null);
                Check.True(map.Snapshot().Available);
            });
            suite.Run("M3 explicit UDP fault gates task correspondence even before handshake",()=>{
                var p=Peer(role:PeerRole.Participant,source:"task-host",origin:ControlSource.Automatic);var m=Monitor(p);var map=Map();
                m.RecordConnectionFault("udp_connection_reset:poll",0);Check.True(m.Snapshot(0,map).RecoveryDecisionRequired);
                Check.True(m.Observe(WirePacket.Hello(p,0,.01,SessionConfiguration.ProtocolVersion),.02,map).Accepted);
                var task=new TaskSnapshot("wire-session",0,0,"wire-session/b0/t0",Colour.Green,Colour.Red,Phase.MainTask,Condition.B);
                Check.True(m.Observe(WirePacket.Task(p,1,.03,task),.04,map).Accepted);Check.True(m.CurrentTask(.04,map)==null);Check.True(Object.ReferenceEquals(task,m.LastTask));
            });
            suite.Run("M2 wire invariant round trips preserve packet/sample identities and separate validity",()=>{
                var culture=Thread.CurrentThread.CurrentCulture;
                try
                {
                    Thread.CurrentThread.CurrentCulture=new CultureInfo("de-DE");
                    var hello=Round(WirePacket.Hello(Peer(),0,.123,SessionConfiguration.ProtocolVersion));Check.Equal(SessionConfiguration.ProtocolVersion,hello.ProtocolVersion);
                    var tracking=Round(WirePacket.Tracking(Peer(),17,Sample(4,1.1,1.2,true,false)));
                    Check.Equal(17L,tracking.Sequence);Check.Equal(4L,tracking.Sample.Sequence);Check.Near(1.1,tracking.Sample.CaptureLocalSeconds);Check.True(tracking.Sample.HeadValid&&!tracking.Sample.EyeValid);
                    Check.Equal("operator-device",tracking.Sample.DeviceId);Check.Equal("simulator/operator",tracking.Sample.StreamId);Check.Equal("remote-clock",tracking.Sample.ClockId);
                    var participant=Peer(peer:"participant-process",role:PeerRole.Participant,source:"task-host",origin:ControlSource.Automatic,clock:"local-clock");
                    var task=Round(WirePacket.Task(participant,2,2,new TaskSnapshot("wire-session",1,2,"wire-session/b1/t3",Colour.Red,Colour.Blue,Phase.MainTask,Condition.C)));
                    Check.Equal("wire-session/b1/t3",task.Snapshot.TrialId);Check.Equal((Colour?)Colour.Blue,task.Snapshot.Ink);Check.Equal(2,task.Snapshot.Segment);Check.Equal((Condition?)Condition.C,task.Snapshot.Condition);
                    var paused=Round(WirePacket.Task(participant,3,2.1,new TaskSnapshot("wire-session",1,2,null,null,null,Phase.Paused,Condition.C)));Check.True(paused.Snapshot.TrialId==null);
                    var request=Round(WirePacket.TimeRequest(Peer(),8,2.5,"exchange/1"));Check.Equal("exchange/1",request.ExchangeId);Check.Near(2.5,request.RequestSentSeconds);
                    var reply=Round(WirePacket.TimeReply(Peer(),9,3,"exchange/1","local-clock",1,2.9));Check.Near(2.9,reply.RemoteReceivedSeconds);Check.Equal("local-clock",reply.RequestClockId);
                }
                finally {Thread.CurrentThread.CurrentCulture=culture;}
            });
            suite.Run("M2 strict wire rejects corruption version extra fields and nonfinite numbers",()=>{
                byte[] bytes=WireCodec.Encode(Track(3,1,1));
                Corrupt(bytes,1,"999");Corrupt(bytes,2,"999");Corrupt(bytes,3,"!!!");Corrupt(bytes,5,"Owner");Corrupt(bytes,7,"Unspecified");Corrupt(bytes,9,"-1");Corrupt(bytes,9,"01");Corrupt(bytes,11,"-1");Corrupt(bytes,16,"2");Corrupt(bytes,17,"true");
                foreach(var bad in new[]{"NaN","Infinity","-Infinity","1e999"," 1","1,5"})Corrupt(bytes,19,bad);
                WirePacket p;string e;Check.True(!WireCodec.TryDecode(new byte[2049],out p,out e));Check.True(!WireCodec.TryDecode(Encoding.ASCII.GetBytes(Encoding.ASCII.GetString(bytes)+"|extra"),out p,out e));
                bytes[0]=255;Check.True(!WireCodec.TryDecode(bytes,out p,out e));
                var invalid=new HeadEyeSample("wire-session","simulator/operator","operator-device","remote-clock",0,0,1,1,1,ControlSource.Simulator,StreamRole.OperatorControl,new Rotation(Double.NaN,0,0,1),new Vec3(0,0,1),false,true);
                Check.Throws<ArgumentException>(()=>WireCodec.Encode(WirePacket.Tracking(Peer(),1,invalid)));
                var zero=new HeadEyeSample("wire-session","simulator/operator","operator-device","remote-clock",0,0,1,1,1,ControlSource.Simulator,StreamRole.OperatorControl,new Rotation(0,0,0,0),new Vec3(0,0,1),true,true);
                Check.True(!Round(WirePacket.Tracking(Peer(),1,zero)).Sample.HeadValid);
            });
            suite.Run("M2 handshake and explicit session role source clock device gates",()=>{
                var m=Monitor();Check.Equal("handshake_required",m.Observe(Track(1,0,0),0).Reason);
                Check.Equal("wrong_session",m.Observe(WirePacket.Hello(Peer(session:"other"),0,0,SessionConfiguration.ProtocolVersion),0).Reason);
                Check.Equal("wrong_role",m.Observe(WirePacket.Hello(Peer(role:PeerRole.Participant),0,0,SessionConfiguration.ProtocolVersion),0).Reason);
                foreach(var p in new[]{Peer(source:"simulator/wrong"),Peer(peer:"wrong"),Peer(origin:ControlSource.LiveOperator),Peer(clock:"other-clock")})
                    Check.Equal("wrong_source_or_clock",m.Observe(WirePacket.Hello(p,0,0,SessionConfiguration.ProtocolVersion),0).Reason);
                Check.Equal("protocol_version_mismatch",m.Observe(WirePacket.Hello(Peer(),0,0,"other-version"),0).Reason);Hello(m);
                Check.Equal("wrong_tracking_device",m.Observe(WirePacket.Tracking(Peer(),1,Sample(0,.01,.01,device:"other-device")),.02).Reason);
                Check.True(!m.Snapshot(.02).RecoveryDecisionRequired);
                var participantSample=new HeadEyeSample("wire-session","simulator/operator","operator-device","remote-clock",0,0,0,0,0,ControlSource.Simulator,StreamRole.ParticipantMeasurement,Rotation.Identity,new Vec3(0,0,1),true,true);
                Check.Throws<ArgumentException>(()=>WirePacket.Tracking(Peer(role:PeerRole.Participant),1,participantSample));
            });
            suite.Run("M2 packet gaps duplicates reorder do not replace latest raw sample",()=>{
                var m=Monitor();Hello(m);Check.True(m.Observe(Track(1,0,.01),.02).Accepted);
                Check.Equal(1L,m.Observe(Track(3,1,.03),.04).MissingBeforePacket);
                Check.Equal("out_of_order",m.Observe(Track(2,9,.02),.05).Reason);
                Check.Equal("duplicate",m.Observe(Track(1,0,.01),.06).Reason);
                Check.Equal(1L,m.LastSample.Sequence);var s=m.Snapshot(.06);Check.Equal(1L,s.MissingSequencePositions);Check.Equal(1L,s.DuplicatePackets);Check.Equal(1L,s.OutOfOrderPackets);
            });
            suite.Run("M2 sample sequence distinct from interleaved packets with block epoch validation",()=>{
                var m=Monitor();Hello(m);Check.True(m.Observe(WirePacket.TimeRequest(Peer(),1,.01,"request"),.02).Accepted);
                Check.True(m.Observe(Track(2,0,.03),.04).Accepted);Check.True(m.Observe(WirePacket.TimeRequest(Peer(),3,.05,"request2"),.06).Accepted);
                Check.Equal("duplicate_or_old_sample",m.Observe(Track(4,0,.07),.08).Reason);
                Check.True(m.Observe(WirePacket.Tracking(Peer(),5,Sample(0,.09,.09,block:1)),.1).Accepted);
                Check.Equal("old_tracking_context",m.Observe(WirePacket.Tracking(Peer(),6,Sample(10,.11,.11)),.12).Reason);
                Check.Equal(1,m.LastSample.BlockIndex);Check.Equal(0L,m.LastSample.Sequence);
            });
            suite.Run("M2 clock absent or wrong local clock prevents current render sample",()=>{
                var m=Monitor();Hello(m);m.Observe(Track(1,0,.1),.11);
                var s=m.Snapshot(.12);Check.True(s.LocalHeadFresh&&s.LocalEyeFresh);Check.True(!s.HeadUsable&&!s.EyeUsable&&!s.CaptureAgeVerified);Check.True(m.CurrentSample(.12)==null);Check.True(Warning(s,FaultKind.ClockMappingUnavailable));
                var wrong=new ClockSynchronizer("another-local","remote-clock",ClockConfig());Check.True(!m.Snapshot(.12,wrong).HeadUsable);
                Check.True(m.CurrentSample(.12,Map())!=null);
            });
            suite.Run("M2 independent head eye failure recovery and local staleness warnings",()=>{
                var m=Monitor();var map=Map();Hello(m);
                m.Observe(Track(1,0,.01),.02,map);Check.True(m.Snapshot(.03,map).HeadUsable&&m.Snapshot(.03,map).EyeUsable);
                m.Observe(Track(2,1,.04,true,false),.05,map);var eye=m.Snapshot(.06,map);Check.True(eye.HeadUsable&&!eye.EyeUsable);Check.True(Warning(eye,FaultKind.EyeInvalid));
                m.Observe(Track(3,2,.07,false,true),.08,map);var head=m.Snapshot(.09,map);Check.True(!head.HeadUsable&&head.EyeUsable);Check.True(Warning(head,FaultKind.HeadInvalid));
                m.Observe(Track(4,3,.1),.11,map);Check.True(m.Snapshot(.12,map).HeadUsable&&m.Snapshot(.12,map).EyeUsable);
                var stale=m.Snapshot(.32,map);Check.True(!stale.HeadUsable&&!stale.EyeUsable);Check.True(Warning(stale,FaultKind.HeadStale)&&Warning(stale,FaultKind.EyeStale));Check.True(!stale.AutomaticPauseOrFallbackSelected);
                bool eyeCleared=false;foreach(var e in m.Events)if(e.Kind=="fault_cleared"&&e.Detail=="EyeInvalid")eyeCleared=true;Check.True(eyeCleared);
            });
            suite.Run("M2 mapped stale future and rollback captures cannot become current",()=>{
                var m=Monitor();var map=Map();Hello(m);
                Check.Equal("stale_capture",m.Observe(WirePacket.Tracking(Peer(),1,Sample(0,.01,.4)),.41,map).Reason);
                Check.Equal("future_capture",m.Observe(Track(2,1,.49),.42,map).Reason);
                Check.True(m.Observe(Track(3,2,.43),.44,map).Accepted);
                Check.Equal("capture_clock_rollback",m.Observe(WirePacket.Tracking(Peer(),4,Sample(3,.42,.45)),.46,map).Reason);
                Check.Equal("source_clock_rollback",m.Observe(WirePacket.Hello(Peer(),5,.4,SessionConfiguration.ProtocolVersion),.47,map).Reason);
                Check.Equal(2L,m.LastSample.Sequence);
            });
            suite.Run("M2 disconnect latches recovery decision and later Hello cannot revive output",()=>{
                var m=Monitor();var map=Map();Hello(m);m.Observe(Track(1,0,.01),.02,map);Check.True(m.CurrentSample(.03,map)!=null);
                Check.True(m.Snapshot(.6,map).RecoveryDecisionRequired);
                Check.True(m.Observe(WirePacket.Hello(Peer(),2,.61,SessionConfiguration.ProtocolVersion),.62,map).Accepted);
                Check.True(m.CurrentSample(.63,map)==null);Check.True(m.Observe(Track(3,1,.64),.65,map).Accepted);
                var after=m.Snapshot(.66,map);Check.True(after.LocalHeadFresh&&after.RecoveryDecisionRequired&&!after.HeadUsable);Check.True(Warning(after,FaultKind.RecoveryDecisionRequired));
                Check.Equal("duplicate",m.Observe(WirePacket.Hello(Peer(),0,.67,SessionConfiguration.ProtocolVersion),.68,map).Reason);
            });
            suite.Run("M2 operator task correspondence clears on pause stale and reordered updates",()=>{
                var p=Peer(role:PeerRole.Participant,source:"task-host",origin:ControlSource.Automatic);var m=Monitor(p);var map=Map();
                Check.True(m.Observe(WirePacket.Hello(p,0,0,SessionConfiguration.ProtocolVersion),0).Accepted);
                var task=new TaskSnapshot("wire-session",0,1,"wire-session/b0/t5",Colour.Green,Colour.Red,Phase.MainTask,Condition.B);
                Check.True(m.Observe(WirePacket.Task(p,2,.02,task),.03,map).Accepted);Check.Equal(task.TrialId,m.CurrentTask(.04,map).TrialId);
                var paused=new TaskSnapshot("wire-session",0,1,null,null,null,Phase.Paused,Condition.B);
                Check.True(m.Observe(WirePacket.Task(p,3,.05,paused),.06,map).Accepted);Check.True(m.CurrentTask(.07,map).TrialId==null);
                Check.Equal("out_of_order",m.Observe(WirePacket.Task(p,1,.01,task),.08,map).Reason);Check.Equal(Phase.Paused,m.CurrentTask(.09,map).Phase);
                Check.True(m.CurrentTask(.4,map)==null);Check.True(m.CurrentTask(.4)==null);
                m.Snapshot(.7,map);m.Observe(WirePacket.Hello(p,4,.71,SessionConfiguration.ProtocolVersion),.72,map);Check.True(m.CurrentTask(.73,map)==null);
            });
            suite.Run("M2 clock offset drift uncertainty synthetic known 120 ppm mapping",()=>{
                var map=new ClockSynchronizer("local-clock","remote-clock",ClockConfig());double local,u;Check.True(!map.TryMapRemote(37,out local,out u));Check.True(Double.IsNaN(local));
                Exchange(map,"a",0,37,1.00012,.01,.01,.002);Check.True(map.Snapshot().Available&&!map.Snapshot().DriftAvailable);
                Exchange(map,"b",10,37,1.00012,.01,.01,.002);Exchange(map,"c",20,37,1.00012,.01,.01,.002);
                var s=map.Snapshot();Check.True(s.Available&&s.DriftAvailable);Check.Near(120,s.RemoteDriftPartsPerMillion.Value,.000001);
                Check.True(map.TryMapRemote(37+1.00012*25,out local,out u));Check.Near(25,local,.0000001);Check.True(u>=.009999&&u<.016);Check.Near(37+1.00012*s.LocalReferenceSeconds-s.LocalReferenceSeconds,s.RemoteMinusLocalSeconds.Value,.0000001);
            });
            suite.Run("M2 clock delayed drift availability asymmetric uncertainty and horizon",()=>{
                var map=new ClockSynchronizer("local-clock","remote-clock",ClockConfig(5,2));Exchange(map,"a",0,37,1,.03,.01,.002);Exchange(map,"b",.1,37,1,.03,.01,.002);Exchange(map,"c",.2,37,1,.03,.01,.002);
                Check.True(map.Snapshot().Available&&!map.Snapshot().DriftAvailable);double local,u;Check.True(map.TryMapRemote(38,out local,out u));Check.Near(.99,local);Check.True(Math.Abs(local-1)<=u);
                double nearU=u;Check.True(map.TryMapRemote(38.5,out local,out u));Check.True(u>nearU);Check.True(!map.TryMapRemote(50,out local,out u));
            });
            suite.Run("M2 changing transit asymmetry cannot make a stale capture appear fresh",()=>{
                var map=new ClockSynchronizer("local-clock","remote-clock",ClockConfig());
                Exchange(map,"a",0,37,1,.11,.01,.002);Exchange(map,"b",10,37,1,.06,.06,.002);Exchange(map,"c",20,37,1,.01,.11,.002);
                var estimate=map.Snapshot();Check.True(estimate.Available&&estimate.DriftAvailable);Check.True(estimate.MinimumLocalSecondsPerRemoteSecond.Value<=1&&estimate.MaximumLocalSecondsPerRemoteSecond.Value>=1);
                double mapped,u;Check.True(map.TryMapRemote(136.5,out mapped,out u));Check.True(mapped-u<=99.5&&mapped+u>=99.5);Check.True(100-(mapped-u)>.2);
                var m=Monitor();Check.True(m.Observe(WirePacket.Hello(Peer(),0,136.9,SessionConfiguration.ProtocolVersion),99.9).Accepted);
                var late=WirePacket.Tracking(Peer(),1,Sample(0,136.5,136.99));m.Observe(late,100,map);
                Check.True(m.CurrentSample(100,map)==null);Check.True(!m.Snapshot(100,map).CaptureAgeVerified);
            });
            suite.Run("M3 feasible rate midpoint avoids one-ULP false clock unavailability",()=>{
                // A physically valid rate=1 clock and positive transit in both directions.
                // Clipping the unconstrained fit to the minimum rate formerly made the
                // intercept lower exceed upper by 8.881784197001252e-16 seconds.
                const double offset=.0989667077982333;
                double[,] times={
                    {3.4878784238131733,3.5869235882372026,3.5893332827885285,3.4904179974608978},
                    {4.4276171627569862,4.5266224444939924,4.5299785227733116,4.4310548763095179},
                    {6.307094640644614,6.4096992346883264,6.4101753500038061,6.3112998213801514}};
                var map=new ClockSynchronizer("local-clock","remote-clock",ClockConfig(.2));
                for(int i=0;i<3;i++) {
                    Check.True(times[i,1]-offset>=times[i,0]);Check.True(times[i,3]>=times[i,2]-offset);
                    string id="boundary-"+i;Check.True(map.RegisterRequest(id,times[i,0]));
                    Check.True(map.ObserveReply(WirePacket.TimeReply(Peer(),i,times[i,2],id,"local-clock",times[i,0],times[i,1]),times[i,3]));
                    Check.True(map.Snapshot().Available,map.Snapshot().UnavailableReason);
                }
                var snapshot=map.Snapshot();Check.Equal("feasible_rate_interval_midpoint",snapshot.PointEstimateMethod);
                Check.True(snapshot.MinimumLocalSecondsPerRemoteSecond<=1&&snapshot.MaximumLocalSecondsPerRemoteSecond>=1);
                Check.True(snapshot.LocalSecondsPerRemoteSecond>snapshot.MinimumLocalSecondsPerRemoteSecond&&snapshot.LocalSecondsPerRemoteSecond<snapshot.MaximumLocalSecondsPerRemoteSecond);
                foreach(double local in new[]{6.31,7,20,80}) {
                    double mapped,uncertainty;Check.True(map.TryMapRemote(local+offset,out mapped,out uncertainty));
                    Check.True(mapped-uncertainty<=local&&mapped+uncertainty>=local,"True affine clock must remain inside uncertainty");
                }
                var receiver=Monitor();Check.True(receiver.Observe(WirePacket.Hello(Peer(),0,20+offset,SessionConfiguration.ProtocolVersion),20).Accepted);
                receiver.Observe(WirePacket.Tracking(Peer(),1,Sample(0,19.5+offset,20+offset)),20.01,map);
                Check.True(receiver.CurrentSample(20.01,map)==null);Check.True(!receiver.Snapshot(20.01,map).CaptureAgeVerified);
            });
            suite.Run("M2 clock correlated replies reject duplicates malformed timing and rollback",()=>{
                var map=new ClockSynchronizer("local-clock","remote-clock",ClockConfig());Exchange(map,"first",0,0,1,.01,.01,.002);var initial=map.Snapshot();
                Check.True(!map.ObserveReply(WirePacket.TimeReply(Peer(),1,1,"unknown","local-clock",0,.9),1));Check.Equal("uncorrelated_reply",map.LastRejection);
                Check.True(!map.RegisterRequest("first",1));Check.Equal("duplicate_exchange_id",map.LastRejection);
                Check.True(map.RegisterRequest("bad",1));Check.True(!map.ObserveReply(WirePacket.TimeReply(Peer(),2,1.1,"bad","local-clock",1,1.05),Double.NaN));Check.True(map.Snapshot().Available);
                Check.True(map.RegisterRequest("wrongclock",2));Check.True(!map.ObserveReply(WirePacket.TimeReply(Peer(clock:"wrong"),3,2.1,"wrongclock","local-clock",2,2.05),2.2));Check.True(map.ExpireRequest("wrongclock"));
                Check.True(map.RegisterRequest("rollback",3));Check.True(!map.ObserveReply(WirePacket.TimeReply(Peer(),4,.001,"rollback","local-clock",3,0),3.02));Check.True(!map.Snapshot().Available);
                Check.True(initial.Available);
            });
            suite.Run("M2 real loopback datagrams endpoint validation and remote opt-in",()=>{
                Check.Throws<ArgumentException>(()=>new UdpPeer(new IPEndPoint(IPAddress.Any,0),new IPEndPoint(IPAddress.Loopback,9999),false));
                using(var receiver=new UdpClient(new IPEndPoint(IPAddress.Loopback,0)))
                using(var peer=new UdpPeer(new IPEndPoint(IPAddress.Loopback,0),(IPEndPoint)receiver.Client.LocalEndPoint,false))
                {
                    receiver.Client.ReceiveTimeout=1000;peer.Send(WirePacket.Hello(Peer(),0,.1,SessionConfiguration.ProtocolVersion));IPEndPoint sender=null;
                    var bytes=receiver.Receive(ref sender);WirePacket decoded;string error;Check.True(WireCodec.TryDecode(bytes,out decoded,out error));Check.Equal(Peer().SessionId,decoded.Peer.SessionId);
                    receiver.Send(bytes,bytes.Length,peer.LocalEndpoint);var result=Wait(peer);Check.True(result.Accepted);
                    using(var outsider=new UdpClient(new IPEndPoint(IPAddress.Loopback,0))){outsider.Send(bytes,bytes.Length,peer.LocalEndpoint);Check.Equal("unexpected_endpoint",Wait(peer).Error);}
                }
            });
            suite.Run("M2 development thresholds explicit finite and receiver monotonic time enforced",()=>{
                Check.Throws<ArgumentException>(()=>new DevelopmentFaultConfiguration(0,.2,.2));Check.Throws<ArgumentException>(()=>new DevelopmentClockConfiguration(2,1,1,1,50));Check.Throws<ArgumentException>(()=>new DevelopmentClockConfiguration(3,1,1,1,Double.NaN));
                var m=Monitor();Hello(m);m.Snapshot(.1);Check.Throws<InvalidOperationException>(()=>m.Snapshot(.09));Check.Throws<InvalidOperationException>(()=>m.Snapshot(Double.NaN));
            });
        }
        static AvatarStroop.Core.UdpReceiveResult Wait(UdpPeer peer)
        {
            var timer=System.Diagnostics.Stopwatch.StartNew();
            while(timer.Elapsed.TotalSeconds<1){var value=peer.Poll();if(value!=null)return value;Thread.Sleep(1);}
            throw new Exception("Loopback datagram did not arrive within development test budget");
        }
    }
}
