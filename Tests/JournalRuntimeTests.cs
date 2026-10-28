using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    public static class JournalRuntimeTests
    {
        sealed class FakeSocket:IUdpSocket
        {
            public Action<WirePacket> OnSend;
            public IPEndPoint LocalEndpoint {get{return new IPEndPoint(IPAddress.Loopback,40001);}}
            public bool Poll(){return false;}
            public byte[] Receive(ref IPEndPoint sender){throw new InvalidOperationException("No queued data");}
            public void Send(byte[] bytes,IPEndPoint remote){WirePacket packet;string error;Check.True(WireCodec.TryDecode(bytes,out packet,out error),error);if(OnSend!=null)OnSend(packet);}
            public void Dispose(){}
            public UdpPeer Peer(){return new UdpPeer(LocalEndpoint,new IPEndPoint(IPAddress.Loopback,40002),false,this);}
        }
        sealed class FailingStream:MemoryStream
        {
            public bool FailWrite;
            public override void Write(byte[] buffer,int offset,int count){if(FailWrite)throw new IOException("injected recorder write failure");base.Write(buffer,offset,count);}
        }
        static ExportProvenance Provenance(){return new ExportProvenance("runtime-journal-fixture","synthetic fixture acknowledgement","synthetic fixture response");}
        static List<JournalRecord> Read(MemoryStream stream)
        {var rows=new List<JournalRecord>();using(var input=new MemoryStream(stream.ToArray())){var r=JournalPrefixReader.Read(input,rows.Add);Check.True(r.Status==JournalReadStatus.UncleanEnd||r.Status==JournalReadStatus.CleanClose,r.Error);}return rows;}
        public static void Register(TestSuite tests)
        {
            tests.Run("M4 runtime recording is opt-in and setup failure stays visible without study-policy changes",()=>{
                using(var app=new ParticipantSimulationApplication("recording-setup",40001,40002,null,new FakeSocket().Peer()))
                {
                    Check.Equal(null,app.Recording);Check.True(app.CaptureRecording());
                    Check.Throws<ArgumentException>(()=>app.EnableRecording("relative.asj",Provenance(),.1));
                    string absent=Path.Combine(Path.GetTempPath(),"stroop-missing-parent-"+Guid.NewGuid().ToString("N"),"recording.asj");
                    app.EnableRecording(absent,Provenance(),.1);Check.True(app.Recording.IsFaulted);Check.True(app.RecordingWarning.Contains("RECORDING FAILED"));
                    Check.Equal(-1L,app.Recording.LastFlushedSequence);Check.Equal(0L,app.Recording.LastFlushedOffset);app.Tick();
                    Check.Equal(Phase.Preparation,app.Experiment.Phase);Check.True(app.Experiment.EndReason==null);
                    Check.Throws<InvalidOperationException>(()=>app.EnableRecording(absent,Provenance(),.1));
                }
            });
            tests.Run("M4 runtime write and flush failures preserve acknowledgment and never auto-pause or recover",()=>{
                foreach(bool failWrite in new[]{true,false})
                using(var bytes=new FailingStream())
                using(var app=new ParticipantSimulationApplication("recording-failure",40001,40002,null,new FakeSocket().Peer()))
                {
                    bool failFlush=false;var journal=new ContinuousJournal(bytes,()=>{if(failFlush)throw new IOException("injected recorder flush failure");},
                        new JournalIdentity(app.Identity.SessionId,"participant",app.Clock.ClockId,Provenance()),true);
                    app.EnableRecording(journal,10);long seq=journal.LastFlushedSequence,offset=journal.LastFlushedOffset;
                    bytes.FailWrite=failWrite;app.OwnerBeginPractice();failFlush=!failWrite;Check.True(!app.CaptureRecording(true));
                    Check.True(journal.IsFaulted);Check.True(app.RecordingWarning.Contains("last acknowledged sequence="+seq));
                    Check.Equal(seq,journal.LastFlushedSequence);Check.Equal(offset,journal.LastFlushedOffset);Check.Equal(Phase.Practice,app.Experiment.Phase);
                    bytes.FailWrite=false;failFlush=false;Check.True(!app.CaptureRecording(true));Check.Equal(seq,journal.LastFlushedSequence);
                    Check.True(!app.Experiment.Events.Any(x=>x.Kind=="explicit_pause"||x.Kind=="explicit_resume"));
                    Check.True(Read(bytes).Count>=seq+1);
                }
            });
            tests.Run("M4 source acquisition is journaled before failed socket send and delivery stays unacknowledged",()=>{
                var socket=new FakeSocket();using(var bytes=new MemoryStream())
                using(var app=new OperatorSimulationApplication("recording-send",40001,40002,false,false,socket.Peer()))
                {
                    var journal=new ContinuousJournal(bytes,()=>{},new JournalIdentity(app.Identity.SessionId,"operator",app.Clock.ClockId,Provenance()),true);
                    app.EnableRecording(journal,10);bool sawAcquisitionBeforeSend=false;
                    socket.OnSend=packet=>{if(packet.Kind==WireKind.HeadEye){sawAcquisitionBeforeSend=Read(bytes).Any(x=>x.Kind=="operator_acquired_sample"&&x.Fields["sample_sequence"]==packet.Sample.Sequence.ToString());Check.True(sawAcquisitionBeforeSend);throw new SocketException((int)SocketError.ConnectionReset);}};
                    Thread.Sleep(20);double now=app.Clock.Now;var peer=new PeerIdentity(app.Identity.SessionId,"participant",PeerRole.Participant,"simulator/participant",ControlSource.Simulator,"participant-monotonic");
                    for(int i=0;i<2;i++)
                    {double sent=now-.014+i*.004;string id="journal-seed-"+i;Check.True(app.Synchronizer.RegisterRequest(id,sent));Check.True(app.Synchronizer.ObserveReply(WirePacket.TimeReply(peer,i,sent+.001,id,app.Clock.ClockId,sent,sent+.001),sent+.002));}
                    Check.True(app.Monitor.Observe(WirePacket.Hello(peer,0,now-.006,SessionConfiguration.ProtocolVersion),now-.004,app.Synchronizer).Accepted);
                    var task=new TaskSnapshot(app.Identity.SessionId,0,0,"trial-0",Colour.Red,Colour.Blue,Phase.MainTask,Condition.B);
                    Check.True(app.Monitor.Observe(WirePacket.Task(peer,1,now-.003,task),now-.002,app.Synchronizer).Accepted);
                    app.Tick();Check.True(sawAcquisitionBeforeSend);Check.Equal(1,app.AcquiredSamples.Count);Check.Equal(0,app.SamplesSent);Check.Equal(1,app.SamplesSendFailed);
                    Check.True(app.CurrentTask==null);Check.True(app.CaptureRecording(true));
                    var rows=Read(bytes);var acquired=rows.Single(x=>x.Kind=="operator_acquired_sample");var result=rows.Single(x=>x.Kind=="operator_sample_send_result");
                    Check.True(acquired.Sequence<result.Sequence);Check.Equal("false",result.Fields["socket_accepted"]);Check.Equal("true",result.Fields["connection_fault"]);Check.Equal("false",result.Fields["delivery_acknowledged"]);
                    Check.Equal("false",rows.Last(x=>x.Kind=="operator_task_observation").Fields["available"]);Check.True(rows.Any(x=>x.Kind=="peer_fault"&&x.Fields["kind"]=="connection_fault"));
                    Check.True(!rows.Any(x=>x.Kind=="operator_received_sample"));
                }
            });
        }
    }
}
