using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
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

            tests.Run("Retention archives a participant burst before packet and fault eviction",()=>{
                var socket=new FloodSocket();
                string directory=Path.Combine(Path.GetTempPath(),"stroop-retention-"+Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(directory);
                try
                {
                    using(var bytes=new MemoryStream())
                    using(var app=new ParticipantSimulationApplication("burst-evidence",40001,40002,null,socket.Peer()))
                    {
                        var provenance=new ExportProvenance("retention-fixture","synthetic","synthetic");
                        var journal=new ContinuousJournal(bytes,()=>{},new JournalIdentity(app.Identity.SessionId,"participant",app.Clock.ClockId,provenance),true);
                        app.EnableRecording(journal,10);
                        var peer=new PeerIdentity(app.Identity.SessionId,"operator",PeerRole.Operator,"simulator/operator",ControlSource.Simulator,"operator-monotonic");
                        socket.Queued.Enqueue(WireCodec.Encode(WirePacket.Hello(peer,0,0,SessionConfiguration.ProtocolVersion)));
                        int total=SimulationPeer.MaximumRetainedPackets+100,rejected=ReceiveMonitor.MaximumRetainedEvents+100;
                        for(int i=0;i<total;i++)
                        {
                            var sample=new HeadEyeSample(app.Identity.SessionId,peer.SourceId,"simulator-no-hardware",peer.ClockId,0,0,i,
                                i*.000001,i*.000001,ControlSource.Simulator,StreamRole.OperatorControl,Rotation.Identity,new Vec3(0,0,1),true,true);
                            socket.Queued.Enqueue(WireCodec.Encode(WirePacket.Tracking(peer,i+1,sample)));
                        }
                        for(int i=0;i<rejected;i++)socket.Queued.Enqueue(new byte[]{0});
                        app.Tick();
                        Check.Equal((long)total,app.AcceptedHeadEyePackets);
                        Check.Equal(total,app.Audit.OperatorSamples.Count);
                        for(int i=0;i<total;i++)Check.Equal((long)i,app.Audit.OperatorSamples[i].Sample.Sequence);
                        Check.Equal(total+1,app.Audit.Transport.Count(x=>x.Kind=="packet_accepted"));
                        // Both the decode observation and its diagnostic fault are archived.
                        Check.Equal(rejected*2,app.Audit.Transport.Count(x=>x.Kind=="packet_rejected"));
                        Check.Equal(SimulationPeer.MaximumRetainedPackets,app.Packets.Count);
                        Check.True(app.Monitor.DiscardedEvents>0);
                        app.Tick();Check.Equal(total,app.Audit.OperatorSamples.Count);
                        Check.Equal(rejected*2,app.Audit.Transport.Count(x=>x.Kind=="packet_rejected"));
                        var exported=app.Export(directory,provenance);Check.True(exported.Success,exported.Error);
                        Check.Equal(total+1,File.ReadAllLines(Path.Combine(exported.DirectoryPath,"operator_samples.csv")).Length);
                        Check.True(app.CaptureRecording());Check.True(app.CaptureRecording());
                        Check.True(app.CloseRecording());
                        var rows=new List<JournalRecord>();
                        using(var input=new MemoryStream(bytes.ToArray()))
                        {
                            var result=JournalPrefixReader.Read(input,rows.Add);
                            Check.Equal(JournalReadStatus.CleanClose,result.Status);
                        }
                        Check.Equal(total,rows.Count(x=>x.Kind=="operator_received_sample"));
                        Check.Equal(rejected*2,rows.Count(x=>x.Kind=="transport_observation"&&x.Fields["transport_kind"]=="packet_rejected"));
                        var faults=rows.Where(x=>x.Kind=="peer_fault"&&x.Fields["kind"]=="packet_rejected").ToArray();
                        Check.Equal(rejected,faults.Length);
                        Check.Equal(rejected,faults.Select(x=>x.Fields["fault_sequence"]).Distinct().Count());
                    }
                }
                finally{Directory.Delete(directory,true);}
            });

            tests.Run("Presentation audit records transient holds and culling without recording every pose",()=>{
                var sampler=new PresentationAuditSampler();var context=new AuditContext(0,0,"trial");
                var output=new AvatarOutput{HeadValid=true,EyeValid=true,AvatarVisible=true,SelectedSource=ControlSource.Simulator,
                    ActualSource=ControlSource.Simulator,Status=GazeOutputStatus.SampleAvailable,MappingVersion="fixture",
                    ActualPose=new AvatarPose(Rotation.Identity,new Vec3(0,0,1))};
                Check.True(sampler.ShouldRecord(0,context,Phase.MainTask,output,true,false,false,true));
                output.ActualPose=new AvatarPose(Rotation.Identity,new Vec3(1,0,0));
                Check.True(!sampler.ShouldRecord(.01,context,Phase.MainTask,output,true,false,false,true));
                output.EyeValid=false;
                Check.True(sampler.ShouldRecord(.02,context,Phase.MainTask,output,true,false,true,true));
                output.EyeValid=true;
                Check.True(sampler.ShouldRecord(.04,context,Phase.MainTask,output,true,false,false,true));
                Check.True(sampler.ShouldRecord(.05,context,Phase.MainTask,output,false,false,false,true));
                Check.True(sampler.ShouldRecord(.06,context,Phase.MainTask,output,true,false,false,true));
                Check.True(!sampler.ShouldRecord(.07,context,Phase.MainTask,output,true,false,false,true));
                Check.True(sampler.ShouldRecord(.17,context,Phase.MainTask,output,true,false,false,true));
            });

            tests.Run("Presentation audit records context and source changes even without a trial",()=>{
                var sampler=new PresentationAuditSampler();var context=new AuditContext(0,0);
                var output=new AvatarOutput{HeadValid=true,EyeValid=true,SelectedSource=ControlSource.Simulator,ActualSource=ControlSource.Simulator};
                Check.True(sampler.ShouldRecord(0,context,Phase.Countdown,output,false,false,false,false));
                Check.True(sampler.ShouldRecord(.01,new AuditContext(0,1),Phase.Countdown,output,false,false,false,false));
                context=new AuditContext(1,1);
                Check.True(sampler.ShouldRecord(.02,context,Phase.Countdown,output,false,false,false,false));
                output.ActualSource=null;
                Check.True(sampler.ShouldRecord(.03,context,Phase.Countdown,output,false,false,false,false));
                output.MappingVersion="new-mapping";
                Check.True(sampler.ShouldRecord(.04,context,Phase.Countdown,output,false,false,false,false));
                Check.True(sampler.ShouldRecord(.05,context,Phase.Countdown,output,false,false,false,true));
                output.HeadValid=false;
                Check.True(sampler.ShouldRecord(.06,context,Phase.Countdown,output,false,true,false,true));
                Check.True(!sampler.ShouldRecord(.07,context,Phase.Countdown,output,false,true,false,true));
                output.Status=GazeOutputStatus.SampleMissing;
                Check.True(sampler.ShouldRecord(.08,context,Phase.Countdown,output,false,true,false,true));
                Check.True(sampler.ShouldRecord(.09,context,Phase.Paused,output,false,true,false,true));
                Check.True(sampler.ShouldRecord(.095,new AuditContext(1,1,"next-trial"),Phase.Paused,output,false,true,false,true));
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

