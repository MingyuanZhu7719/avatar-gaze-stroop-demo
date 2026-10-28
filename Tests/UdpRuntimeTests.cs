using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    public static class UdpRuntimeTests
    {
        // No OS sockets: reset precisely at the selected boundary or packet kind.
        sealed class ResetSocket : IUdpSocket
        {
            public string ResetOperation;
            public WireKind? ResetSendKind;
            public int PollCalls,ReceiveCalls,SendCalls,DisposeCalls;
            public IPEndPoint LocalEndpoint {get{return new IPEndPoint(IPAddress.Loopback,40001);}}
            public bool Poll()
            {
                PollCalls++;
                if(ResetOperation=="poll")throw Reset();
                return ResetOperation=="receive";
            }
            public byte[] Receive(ref IPEndPoint sender)
            {
                ReceiveCalls++;
                if(ResetOperation=="receive")throw Reset();
                throw new InvalidOperationException("Test did not queue a datagram");
            }
            public void Send(byte[] bytes,IPEndPoint remote)
            {
                SendCalls++;WirePacket packet;string error;
                Check.True(WireCodec.TryDecode(bytes,out packet,out error),error);
                if(ResetSendKind==packet.Kind)throw Reset();
            }
            public void Dispose(){DisposeCalls++;}
            static SocketException Reset(){return new SocketException((int)SocketError.ConnectionReset);}
            public UdpPeer Peer()
            {return new UdpPeer(LocalEndpoint,new IPEndPoint(IPAddress.Loopback,40002),false,this);}
        }

        public static void Register(TestSuite tests)
        {
            tests.Run("UDP runtime reset before handshake records a terminal fault without outgoing I/O",()=>{
                foreach(string operation in new[]{"poll","receive"})
                {
                    var socket=new ResetSocket{ResetOperation=operation};
                    using(var app=new OperatorSimulationApplication("reset-before-handshake",40001,40002,false,false,socket.Peer()))
                    {
                        app.Tick();var state=app.Monitor.Snapshot(app.Clock.Now,app.Synchronizer);
                        Check.True(!state.HandshakeAccepted&&state.RecoveryDecisionRequired);
                        Check.Equal(0,socket.SendCalls);Check.Equal(0,app.SamplesSent);
                        AssertFault(app,"udp_connection_reset:"+operation);
                        AssertNoMoreIo(socket,app.Tick);
                    }
                    Check.Equal(1,socket.DisposeCalls);
                }
            });
            tests.Run("UDP runtime stop send reset clears the operator task immediately and keeps acknowledgement pending",()=>{
                var socket=new ResetSocket();
                using(var app=new OperatorSimulationApplication("reset-stop-send",40001,40002,false,false,socket.Peer()))
                {
                    SeedTask(app);app.Tick();Check.True(app.CurrentTask!=null&&app.View().Stimulus!=null);
                    int samples=app.SamplesSent,acquired=app.AcquiredSamples.Count;
                    socket.ResetSendKind=WireKind.SafetyStopRequest;app.RequestSafetyStop();app.Tick();
                    Check.True(app.CurrentTask==null&&app.View().Stimulus==null&&!app.View().ParticipantAvatarVisible);
                    Check.True(app.StopDelivery.Pending&&!app.StopDelivery.Acknowledged);
                    Check.Equal(samples,app.SamplesSent);Check.Equal(acquired,app.AcquiredSamples.Count);
                    AssertFault(app,"udp_connection_reset:send");AssertNoMoreIo(socket,app.Tick);
                }
            });
            tests.Run("UDP runtime tracking send reset retains acquisition but does not count a failed transmission",()=>{
                var socket=new ResetSocket();
                using(var app=new OperatorSimulationApplication("reset-tracking-send",40001,40002,false,false,socket.Peer()))
                {
                    SeedTask(app);app.Tick();Check.Equal(1,app.SamplesSent);Check.Equal(1,app.AcquiredSamples.Count);
                    Check.Equal(0,app.SamplesSendFailed);Check.Equal(0,app.SamplesSuppressed);
                    Thread.Sleep(15);socket.ResetSendKind=WireKind.HeadEye;app.Tick();
                    Check.Equal(1,app.SamplesSent);Check.Equal(2,app.AcquiredSamples.Count);
                    Check.Equal(1,app.SamplesSendFailed);Check.Equal(0,app.SamplesSuppressed);
                    Check.Equal(app.AcquiredSamples.Count,app.SamplesSent+app.SamplesSendFailed+app.SamplesSuppressed);
                    Check.True(app.AcquiredSamples[1].HeadValid&&app.AcquiredSamples[1].EyeValid);
                    Check.Equal(ControlSource.Simulator,app.AcquiredSamples[1].Source);
                    Check.True(app.CurrentTask==null&&app.View().Stimulus==null);
                    AssertFault(app,"udp_connection_reset:send");AssertNoMoreIo(socket,app.Tick);
                    Check.Equal(2,app.AcquiredSamples.Count);
                }
            });
            tests.Run("UDP runtime receive reset invalidates an established operator task and later packets cannot revive it",()=>{
                var socket=new ResetSocket();
                using(var app=new OperatorSimulationApplication("reset-receive",40001,40002,false,false,socket.Peer()))
                {
                    SeedTask(app);app.Tick();Check.True(app.CurrentTask!=null);
                    socket.ResetOperation="receive";app.Tick();
                    Check.True(app.CurrentTask==null&&app.View().Stimulus==null);
                    AssertFault(app,"udp_connection_reset:receive");
                    // Even directly observed fresh traffic cannot clear the receiver's recovery latch.
                    Thread.Sleep(5);var remote=Remote(app,PeerRole.Participant);double now=app.Clock.Now;
                    Check.True(app.Monitor.Observe(WirePacket.Hello(remote,2,now-.002,SessionConfiguration.ProtocolVersion),now,app.Synchronizer).Accepted);
                    Check.True(app.Monitor.Observe(WirePacket.Task(remote,3,now-.001,Task(app.Identity.SessionId)),now,app.Synchronizer).Accepted);
                    AssertNoMoreIo(socket,app.Tick);Check.True(app.CurrentTask==null);
                    AssertFault(app,"udp_connection_reset:receive");
                }
            });
            tests.Run("UDP runtime task broadcast reset invalidates same-frame participant pose and audits without fallback or pause",()=>{
                var socket=new ResetSocket();
                using(var app=new ParticipantSimulationApplication("reset-task-send",40001,40002,null,socket.Peer()))
                {
                    var experiment=app.Experiment;app.OwnerBeginPractice();
                    for(int i=0;i<8;i++)Answer(experiment);
                    app.OwnerLockNextBlock(ControlSelection.SimulatorRehearsal);
                    // Exercise the existing one-second development countdown unchanged.
                    experiment.BeginCountdown(Actor.Owner,true);Thread.Sleep(1010);
                    SeedTracking(app);app.Tick();
                    Check.Equal(Phase.MainTask,experiment.Phase);
                    Check.True(app.CurrentAvatar.HeadValid&&app.CurrentAvatar.EyeValid&&app.CurrentAvatar.HasActualPose);
                    Check.True(app.SourceReady(ControlSelection.SimulatorRehearsal));
                    string trial=experiment.CurrentTrial.TrialId;experiment.MarkPresented(trial);
                    Thread.Sleep(15);socket.ResetSendKind=WireKind.Task;app.Tick();
                    Check.Equal(Phase.MainTask,experiment.Phase);Check.Equal(trial,experiment.CurrentTrial.TrialId);
                    Check.Equal((ControlSelection?)ControlSelection.SimulatorRehearsal,experiment.LockedControl);
                    Check.True(experiment.CurrentTrial.TechnicalIssue);
                    Check.True(app.CurrentFaults.RecoveryDecisionRequired&&!app.CurrentFaults.HeadUsable&&!app.CurrentFaults.EyeUsable);
                    Check.True(!app.CurrentAvatar.HeadValid&&!app.CurrentAvatar.EyeValid&&!app.CurrentAvatar.HasActualPose);
                    Check.True(app.CurrentAvatar.ActualSource!=ControlSource.Automatic);
                    Check.True(!app.SourceReady(ControlSelection.SimulatorRehearsal));
                    Check.Equal(1,app.Audit.Transport.Count(x=>x.Kind=="connection_fault"&&x.Detail=="udp_connection_reset:send"));
                    AssertFault(app,"udp_connection_reset:send");AssertNoMoreIo(socket,app.Tick);
                    Check.Equal(trial,experiment.CurrentTrial.TrialId);Check.Equal(Phase.MainTask,experiment.Phase);
                    Check.Equal(1,app.Audit.Transport.Count(x=>x.Kind=="connection_fault"));
                }
            });
        }

        static void AssertNoMoreIo(ResetSocket socket,Action tick)
        {
            int polls=socket.PollCalls,receives=socket.ReceiveCalls,sends=socket.SendCalls;
            Check.Equal(1,socket.DisposeCalls);
            for(int i=0;i<3;i++)tick();
            Check.Equal(polls,socket.PollCalls);Check.Equal(receives,socket.ReceiveCalls);Check.Equal(sends,socket.SendCalls);
            Check.Equal(1,socket.DisposeCalls);
        }
        static void AssertFault(SimulationPeer app,string reason)
        {
            var state=app.Monitor.Snapshot(app.Clock.Now,app.Synchronizer);
            Check.True(state.RecoveryDecisionRequired&&!state.HeadUsable&&!state.EyeUsable);
            Check.True(!state.AutomaticPauseOrFallbackSelected);
            Check.True(state.Warnings.Any(x=>x.Kind==FaultKind.ConnectionFault));
            Check.True(state.Warnings.Any(x=>x.Kind==FaultKind.RecoveryDecisionRequired));
            Check.Equal(0L,state.RejectedPackets);
            Check.Equal(1,app.Monitor.Events.Count(x=>x.Kind=="connection_fault"&&x.Detail==reason));
        }
        static PeerIdentity Remote(SimulationPeer app,PeerRole role)
        {
            string name=role==PeerRole.Operator?"operator":"participant";
            return new PeerIdentity(app.Identity.SessionId,name,role,"simulator/"+name,ControlSource.Simulator,name+"-monotonic");
        }
        static void SeedMap(SimulationPeer app,PeerIdentity remote,double now)
        {
            // Two separated exchanges bound the clock rate; a lone zero-processing
            // exchange deliberately cannot establish freshness away from its anchor.
            for(int i=0;i<2;i++)
            {
                double sent=now-.014+i*.004;string id="reset-test-seed-"+i;
                Check.True(app.Synchronizer.RegisterRequest(id,sent));
                Check.True(app.Synchronizer.ObserveReply(WirePacket.TimeReply(remote,i,sent+.001,id,app.Clock.ClockId,sent,sent+.001),sent+.002));
            }
            Check.True(app.Synchronizer.Snapshot().Available);
            Check.True(app.Monitor.Observe(WirePacket.Hello(remote,0,now-.006,SessionConfiguration.ProtocolVersion),now-.004,app.Synchronizer).Accepted);
        }
        static TaskSnapshot Task(string session)
        {return new TaskSnapshot(session,0,0,session+"/b0/t0",Colour.Red,Colour.Blue,Phase.MainTask,Condition.B);}
        static void SeedTask(OperatorSimulationApplication app)
        {
            Thread.Sleep(20);double now=app.Clock.Now;var remote=Remote(app,PeerRole.Participant);SeedMap(app,remote,now);
            Check.True(app.Monitor.Observe(WirePacket.Task(remote,1,now-.003,Task(app.Identity.SessionId)),now-.002,app.Synchronizer).Accepted);
        }
        static void SeedTracking(ParticipantSimulationApplication app)
        {
            double now=app.Clock.Now;var remote=Remote(app,PeerRole.Operator);SeedMap(app,remote,now);
            var sample=new HeadEyeSample(app.Identity.SessionId,"simulator/operator","simulator-no-hardware","operator-monotonic",0,0,0,
                now-.003,now-.003,ControlSource.Simulator,StreamRole.OperatorControl,Rotation.Identity,new Vec3(0,0,1),true,true);
            Check.True(app.Monitor.Observe(WirePacket.Tracking(remote,1,sample),now-.002,app.Synchronizer).Accepted);
        }
        static void Answer(Experiment experiment)
        {
            experiment.MarkPresented(experiment.CurrentTrial.TrialId);var answer=experiment.CurrentTrial.Stimulus.Ink;
            experiment.ButtonChanged(answer,true);experiment.ButtonChanged(answer,false);
        }
    }
}
