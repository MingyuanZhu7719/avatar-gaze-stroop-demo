using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    // Bounded diagnostic history: a long session must not accumulate one record per
    // packet or per rendered frame, and the exact totals must survive trimming.
    public static class RetentionTests
    {
        sealed class FloodSocket : IUdpSocket
        {
            public readonly Queue<byte[]> Queued=new Queue<byte[]>();
            public int Sent,Disposed;
            public IPEndPoint LocalEndpoint {get{return new IPEndPoint(IPAddress.Loopback,40001);}}
            public bool Poll(){return Queued.Count>0;}
            public byte[] Receive(ref IPEndPoint sender)
            {sender=new IPEndPoint(IPAddress.Loopback,40002);return Queued.Dequeue();}
            public void Send(byte[] bytes,IPEndPoint remote){Sent++;}
            public void Dispose(){Disposed++;}
            public UdpPeer Peer()
            {return new UdpPeer(LocalEndpoint,new IPEndPoint(IPAddress.Loopback,40002),false,this);}
        }

        public static void Register(TestSuite tests)
        {
            tests.Run("Retention bounds diagnostic fault history and never reuses a sequence",()=>{
                var peer=new PeerIdentity("bounded-events","operator",PeerRole.Operator,"simulator/operator",ControlSource.Simulator,"operator-monotonic");
                var monitor=new ReceiveMonitor(peer,DevelopmentFixtures.Faults(),"simulator-no-hardware","local-monotonic");
                int total=ReceiveMonitor.MaximumRetainedEvents+300;
                for(int i=0;i<total;i++)monitor.RecordTransportRejection("too_short",i*0.001);
                Check.Equal(ReceiveMonitor.MaximumRetainedEvents,monitor.Events.Count);
                Check.Equal((long)(total-ReceiveMonitor.MaximumRetainedEvents),monitor.DiscardedEvents);
                Check.Equal((long)(total-1),monitor.Events[monitor.Events.Count-1].Sequence);
                for(int i=1;i<monitor.Events.Count;i++)Check.Equal(monitor.Events[i-1].Sequence+1,monitor.Events[i].Sequence);
                Check.True(monitor.Events.All(x=>x.Kind=="packet_rejected"));
            });

            tests.Run("Retention bounds packet observations while accepted totals stay exact",()=>{
                var socket=new FloodSocket();
                using(var app=new OperatorSimulationApplication("bounded-packets",40001,40002,false,false,socket.Peer()))
                {
                    var peer=new PeerIdentity("bounded-packets","participant",PeerRole.Participant,"simulator/participant",ControlSource.Simulator,"participant-monotonic");
                    var snapshot=new TaskSnapshot("bounded-packets",-1,0,null,null,null,Phase.Preparation,null);
                    socket.Queued.Enqueue(WireCodec.Encode(WirePacket.Hello(peer,0,0,SessionConfiguration.ProtocolVersion)));
                    long lastSequence=SimulationPeer.MaximumRetainedPackets+50;
                    for(long i=1;i<=lastSequence;i++)
                        socket.Queued.Enqueue(WireCodec.Encode(WirePacket.Task(peer,i,i*0.001,snapshot)));
                    long received=socket.Queued.Count;
                    app.Tick();
                    Check.Equal(SimulationPeer.MaximumRetainedPackets,app.Packets.Count);
                    Check.Equal(received-SimulationPeer.MaximumRetainedPackets,app.DiscardedPacketObservations);
                    Check.Equal(received,app.AcceptedPacketObservations);
                    Check.Equal(0L,app.AcceptedHeadEyePackets);
                    Check.True(app.Packets.All(x=>x.Accepted));
                    // The retained window is the newest observations, not the oldest.
                    Check.Equal(lastSequence,app.Packets[app.Packets.Count-1].Packet.Sequence);
                }
                Check.Equal(1,socket.Disposed);
            });

            tests.Run("Retention bounds participant output history while presentation counters stay exact",()=>{
                var socket=new FloodSocket();
                using(var app=new ParticipantSimulationApplication("bounded-outputs",40001,40002,DevelopmentFixtures.Geometry(),socket.Peer()))
                {
                    app.OwnerBeginPractice();
                    for(int i=0;i<8;i++)
                    {
                        var trial=app.Experiment.CurrentTrial;
                        app.Experiment.MarkPresented(trial.TrialId);
                        app.Experiment.ButtonChanged(trial.Stimulus.Ink,true);
                        app.Experiment.ButtonChanged(trial.Stimulus.Ink,false);
                    }
                    Check.Equal(Phase.BlockPreparation,app.Experiment.Phase);
                    app.OwnerLockNextBlock(ControlSelection.Automatic);
                    int target=ParticipantSimulationApplication.MaximumRetainedOutputs+25;
                    for(int i=0;i<target;i++)app.RefreshPresentation();
                    Check.Equal(ParticipantSimulationApplication.MaximumRetainedOutputs,app.Outputs.Count);
                    Check.Equal((long)target,app.OutputObservations);
                    Check.Equal((long)(target-ParticipantSimulationApplication.MaximumRetainedOutputs),app.DiscardedOutputs);
                    Check.Equal((long)target,app.AutomaticFallbacks);
                    Check.Equal(0L,app.ValidSimulatorPresentations);
                    Check.True(app.Outputs.All(x=>x.ActualSource==ControlSource.Automatic));
                }
            });

            tests.Run("Retention counters stay zero while recorded histories fit their limits",()=>{
                var socket=new FloodSocket();
                using(var app=new OperatorSimulationApplication("fit-within-limit",40001,40002,false,false,socket.Peer()))
                {
                    var peer=new PeerIdentity("fit-within-limit","participant",PeerRole.Participant,"simulator/participant",ControlSource.Simulator,"participant-monotonic");
                    var snapshot=new TaskSnapshot("fit-within-limit",-1,0,null,null,null,Phase.Preparation,null);
                    socket.Queued.Enqueue(WireCodec.Encode(WirePacket.Hello(peer,0,0,SessionConfiguration.ProtocolVersion)));
                    socket.Queued.Enqueue(WireCodec.Encode(WirePacket.Task(peer,1,0.001,snapshot)));
                    app.Tick();
                    Check.Equal(2,app.Packets.Count);
                    Check.Equal(0L,app.DiscardedPacketObservations);
                    Check.Equal(2L,app.AcceptedPacketObservations);
                    Check.Equal(0L,app.Monitor.DiscardedEvents);
                }
            });
        }
    }
}
