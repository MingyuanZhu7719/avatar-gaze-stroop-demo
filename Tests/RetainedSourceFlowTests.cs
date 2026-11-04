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
    // Synthetic transport only: these checks never open an OS socket or a player.
    public static class RetainedSourceFlowTests
    {
        sealed class FakeSocket : IUdpSocket
        {
            public bool ResetOnPoll;
            public int PollCalls,SendCalls,DisposeCalls;
            public TaskSnapshot LastTask;
            public IPEndPoint LocalEndpoint {get{return new IPEndPoint(IPAddress.Loopback,42001);}}
            public bool Poll()
            {
                PollCalls++;
                if(ResetOnPoll)throw new SocketException((int)SocketError.ConnectionReset);
                return false;
            }
            public byte[] Receive(ref IPEndPoint sender){throw new InvalidOperationException("No queued synthetic datagram");}
            public void Send(byte[] bytes,IPEndPoint remote)
            {
                SendCalls++;WirePacket packet;string reason;
                Check.True(WireCodec.TryDecode(bytes,out packet,out reason),reason);
                if(packet.Kind==WireKind.Task)LastTask=packet.Snapshot;
            }
            public void Dispose(){DisposeCalls++;}
            public UdpPeer Peer(){return new UdpPeer(LocalEndpoint,new IPEndPoint(IPAddress.Loopback,42002),false,this);}
        }

        static ParticipantSimulationApplication Participant(string session,FakeSocket socket)
        {return new ParticipantSimulationApplication(session,42001,42002,null,socket.Peer(),true);}
        static PeerIdentity Remote(ParticipantSimulationApplication app)
        {return new PeerIdentity(app.Identity.SessionId,"operator",PeerRole.Operator,"simulator/operator",ControlSource.Simulator,"operator-monotonic");}
        static void SeedMapBeforeNetworkTick(ParticipantSimulationApplication app)
        {
            double now=app.Clock.Now;var remote=Remote(app);
            // The two separated exchanges bound extrapolation. They precede the
            // application's first network tick, so request times stay monotonic.
            for(int i=0;i<2;i++)
            {
                double sent=now-.014+i*.004;string id="retained-source-seed-"+i;
                Check.True(app.Synchronizer.RegisterRequest(id,sent));
                Check.True(app.Synchronizer.ObserveReply(WirePacket.TimeReply(remote,i,sent+.001,id,app.Clock.ClockId,sent,sent+.001),sent+.002));
            }
            Check.True(app.Synchronizer.Snapshot().Available);
        }
        static void Hello(ParticipantSimulationApplication app,long sequence)
        {
            double now=app.Clock.Now;
            Check.True(app.Monitor.Observe(WirePacket.Hello(Remote(app),sequence,now,SessionConfiguration.ProtocolVersion),now,app.Synchronizer).Accepted);
        }
        static void Tracking(ParticipantSimulationApplication app,long sequence,int block,bool head=true,bool eye=true)
        {
            double now=app.Clock.Now;
            var sample=new HeadEyeSample(app.Identity.SessionId,"simulator/operator","simulator-no-hardware","operator-monotonic",
                block,0,sequence,now-.001,now,ControlSource.Simulator,StreamRole.OperatorControl,Rotation.Identity,new Vec3(0,0,1),head,eye);
            var decision=app.Monitor.Observe(WirePacket.Tracking(Remote(app),sequence,sample),now,app.Synchronizer);
            Check.True(decision.Accepted,decision.Reason);
        }
        static void Answer(Experiment experiment)
        {
            experiment.MarkPresented(experiment.CurrentTrial.TrialId);var answer=experiment.CurrentTrial.Stimulus.Ink;
            Check.True(experiment.ButtonChanged(answer,true));experiment.ButtonChanged(answer,false);
        }
        static void BeginFirstFixtureCountdown(ParticipantSimulationApplication app)
        {
            app.OwnerBeginPractice();for(int i=0;i<8;i++)Answer(app.Experiment);
            app.OwnerLockNextBlock(ControlSelection.SimulatorRehearsal);
            // This explicit synthetic readiness assertion prepares block one.
            // The second-block runtime path below exercises real readiness gates.
            app.Experiment.BeginCountdown(Actor.Owner,true);
        }
        static void FinishFirstFixtureBlock(ParticipantSimulationApplication app)
        {
            var experiment=app.Experiment;experiment.Tick();Check.Equal(Phase.MainTask,experiment.Phase);
            for(int i=0;i<24;i++)Answer(experiment);
            Check.True(experiment.AcknowledgeQuestionnaireInstruction());
            for(int i=0;i<Questionnaire.Count;i++)experiment.Rate(3);
            Check.Equal(Phase.Rest,experiment.Phase);Check.True(experiment.ReadyEnabled);
            Check.True(experiment.Ready());Check.Equal(Phase.BlockReady,experiment.Phase);
            Check.Equal(1,experiment.BlockIndex);Check.True(experiment.RetainedSourceContinuationPending);
            Check.Equal((ControlSelection?)ControlSelection.SimulatorRehearsal,experiment.LockedControl);
        }
        static void AssertWaiting(ParticipantSimulationApplication app)
        {
            Check.Equal(Phase.BlockReady,app.Experiment.Phase);Check.True(app.WaitingForRetainedSource);
            Check.True(!app.SourceReady(ControlSelection.SimulatorRehearsal));
            Check.Equal((ControlSelection?)ControlSelection.SimulatorRehearsal,app.Experiment.LockedControl);
            Check.Equal(24,app.Experiment.Trials.Count(x=>!x.IsPractice));
            Check.Equal(0,app.Experiment.Events.Count(x=>x.Kind=="retained_source_countdown_started"));
            Check.Equal(0L,app.AutomaticFallbacks);
        }

        public static void Register(TestSuite suite)
        {
            suite.Run("Retained source runtime requires host admission fresh context and explicit pause resume while preserving recovery and restart evidence",()=>{
                var readySocket=new FakeSocket();var failedSocket=new FakeSocket();
                using(var bytes=new MemoryStream())
                using(var ready=Participant("retained-source-ready",readySocket))
                using(var failed=Participant("retained-source-fault",failedSocket))
                {
                    var provenance=new ExportProvenance("retained-source-regression","synthetic fixture acknowledgement","synthetic fixture response");
                    var journal=new ContinuousJournal(bytes,()=>{},new JournalIdentity(failed.Identity.SessionId,"participant",failed.Clock.ClockId,provenance),true);
                    failed.EnableRecording(journal,10);
                    BeginFirstFixtureCountdown(ready);BeginFirstFixtureCountdown(failed);
                    // Both independent applications share this single wall-clock
                    // wait; no peer handshake exists yet, so no recovery is hidden.
                    Thread.Sleep(3010);
                    FinishFirstFixtureBlock(ready);FinishFirstFixtureBlock(failed);

                    SeedMapBeforeNetworkTick(ready);ready.Tick();AssertWaiting(ready);
                    Check.True(ready.ApplicationWarnings.Any(x=>x.Contains("Waiting for the confirmed source")));
                    Check.Equal(Phase.BlockReady,readySocket.LastTask.Phase);Check.Equal(1,readySocket.LastTask.BlockIndex);
                    Hello(ready,0);Tracking(ready,1,0);ready.Tick();AssertWaiting(ready);
                    Check.True(ready.CurrentFaults.HeadUsable&&ready.CurrentFaults.EyeUsable&&ready.CurrentFaults.CaptureAgeVerified);
                    Check.True(!ready.CurrentFaults.RecoveryDecisionRequired);
                    Check.True(!ready.CurrentAvatar.HasActualPose,"Previous-block input must not supply the next block pose");
                    Tracking(ready,2,1,false,true);ready.Tick();AssertWaiting(ready);
                    Tracking(ready,3,1,true,false);ready.Tick();AssertWaiting(ready);

                    ready.Experiment.Pause(Actor.Participant);Tracking(ready,4,1);ready.Tick();
                    Check.Equal(Phase.Paused,ready.Experiment.Phase);Check.True(ready.SourceReady(ControlSelection.SimulatorRehearsal));
                    Check.True(!ready.Experiment.TryBeginRetainedBlockCountdown(true));
                    Check.Equal(0,ready.Experiment.Events.Count(x=>x.Kind=="retained_source_countdown_started"));
                    ready.Experiment.Resume(true);
                    // A newly ready source must not start the countdown while
                    // host prerequisites such as HMD input or recording fail.
                    ready.Tick(false);ready.Tick(false);
                    Check.Equal(Phase.BlockReady,ready.Experiment.Phase);Check.True(ready.Experiment.RetainedSourceContinuationPending);
                    Check.True(ready.SourceReady(ControlSelection.SimulatorRehearsal));Check.True(!ready.WaitingForRetainedSource);
                    Check.Equal(0,ready.Experiment.Events.Count(x=>x.Kind=="retained_source_countdown_started"));
                    Check.Equal(24,ready.Experiment.Trials.Count(x=>!x.IsPractice));Check.Equal(0L,ready.AutomaticFallbacks);
                    // Admission recovering does not waive the source gate: a
                    // later invalid sample must still block the same block.
                    Tracking(ready,5,1,false,true);ready.Tick(true);AssertWaiting(ready);
                    Tracking(ready,6,1);ready.Tick(true);Check.Equal(Phase.Countdown,ready.Experiment.Phase);
                    Check.True(ready.Experiment.CountdownRemainingSeconds>2.5&&ready.Experiment.CountdownRemainingSeconds<=3);
                    ready.Tick();Check.Equal(1,ready.Experiment.Events.Count(x=>x.Kind=="retained_source_countdown_started"));
                    Check.Equal(24,ready.Experiment.Trials.Count(x=>!x.IsPractice));Check.Equal(0L,ready.AutomaticFallbacks);
                    Check.Throws<InvalidOperationException>(()=>ready.OwnerLockNextBlock(ControlSelection.Automatic));

                    SeedMapBeforeNetworkTick(failed);Hello(failed,0);Tracking(failed,1,1);
                    Check.True(failed.SourceReady(ControlSelection.SimulatorRehearsal));
                    failedSocket.ResetOnPoll=true;failed.Tick();AssertWaiting(failed);
                    Check.True(failed.CurrentFaults.RecoveryDecisionRequired);Check.Equal(1,failedSocket.DisposeCalls);
                    Check.Equal(0,failedSocket.SendCalls);int polls=failedSocket.PollCalls;
                    Hello(failed,2);Tracking(failed,3,1);failed.Tick();AssertWaiting(failed);
                    Check.True(failed.CurrentFaults.RecoveryDecisionRequired);
                    Check.Equal(polls,failedSocket.PollCalls);Check.Equal(0,failedSocket.SendCalls);
                    Check.True(!failed.CurrentAvatar.HasActualPose&&failed.CurrentAvatar.ActualSource!=ControlSource.Automatic);
                    Check.Throws<InvalidOperationException>(()=>failed.OwnerBeginBlock());
                    Check.Throws<InvalidOperationException>(()=>failed.OwnerLockNextBlock(ControlSelection.Automatic));

                    int trialCount=failed.Experiment.Trials.Count,ratingCount=failed.Experiment.Ratings.Count;
                    failed.OwnerEndForRestart("0123456789abcdef0123456789abcdef");
                    Check.Equal(Phase.Ended,failed.Experiment.Phase);Check.Equal((EndReason?)EndReason.TechnicalEnd,failed.Experiment.EndReason);
                    Check.Equal((ControlSelection?)ControlSelection.SimulatorRehearsal,failed.Experiment.LockedControl);
                    Check.Equal(trialCount,failed.Experiment.Trials.Count);Check.Equal(ratingCount,failed.Experiment.Ratings.Count);
                    Check.Equal(1,failed.Experiment.Events.Count(x=>x.Kind=="session_source_confirmed"));
                    Check.Equal(2,failed.Experiment.Events.Count(x=>x.Kind=="block_configuration_locked"));
                    Check.Equal(0L,failed.AutomaticFallbacks);Check.Equal(journal.LastWrittenSequence,journal.LastFlushedSequence);
                    var rows=new List<JournalRecord>();
                    using(var input=new MemoryStream(bytes.ToArray()))
                    {var result=JournalPrefixReader.Read(input,rows.Add);Check.Equal(JournalReadStatus.UncleanEnd,result.Status);}
                    Check.True(rows.Any(x=>x.Kind=="engine_event"&&x.Fields["event_kind"]=="block_configuration_locked"&&
                        x.Fields["block_index"]=="1"&&x.Fields["field:selection"]=="SimulatorRehearsal"&&x.Fields["field:selection_origin"]=="retained_session"));
                    var final=rows.Last(x=>x.Kind=="session_observation");
                    Check.Equal("TechnicalEnd",final.Fields["end_reason"]);Check.Equal("false",final.Fields["session_completed"]);
                    Check.Equal("SimulatorRehearsal",final.Fields["locked_control"]);
                    using(var fresh=Participant("retained-source-new-session",new FakeSocket()))
                    {Check.Equal(Phase.Preparation,fresh.Experiment.Phase);Check.True(!fresh.Experiment.LockedControl.HasValue);Check.Equal(0,fresh.Experiment.Trials.Count);}
                }
            });
        }
    }
}
