using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    public static class RuntimeTests
    {
        public static void Register(TestSuite tests)
        {
            UdpRuntimeTests.Register(tests);
            tests.Run("M3 readiness rejects otherwise fresh samples from another block or segment",()=>{
                var sample=new HeadEyeSample("session","simulator/operator","simulator-no-hardware","clock",0,0,0,0,0,
                    ControlSource.Simulator,StreamRole.OperatorControl,Rotation.Identity,new Vec3(0,0,1),true,true);
                var health=new FaultSnapshot{CaptureAgeVerified=true,HeadUsable=true,EyeUsable=true};
                Check.True(SelectedSimulationSource.Ready(sample,health,"session",0,0));
                Check.True(!SelectedSimulationSource.Ready(sample,health,"session",1,0));
                Check.True(!SelectedSimulationSource.Ready(sample,health,"session",0,1));
                Check.True(!SelectedSimulationSource.Ready(sample,health,"different-session",0,0));
                health.RecoveryDecisionRequired=true;Check.True(!SelectedSimulationSource.Ready(sample,health,"session",0,0));
                health.RecoveryDecisionRequired=false;health.CaptureAgeVerified=false;Check.True(!SelectedSimulationSource.Ready(sample,health,"session",0,0));
            });
            tests.Run("M3 remote stop request retry ack and idempotence never remotely resume",()=>{
                var state=new SafetyStopRequestState();string request=state.Request();Check.True(state.Pending);Check.Equal(request,state.Request());
                Check.True(!state.Acknowledge("unrelated"));Check.True(state.Pending);
                var clock=new ManualClock();var experiment=Fixtures.New(clock);var inbox=new SafetyStopInbox();
                Check.True(inbox.ApplyOnce(request,()=>experiment.Pause(Actor.Operator)));Check.Equal(Phase.Paused,experiment.Phase);
                Check.True(!inbox.ApplyOnce(request,()=>experiment.Pause(Actor.Operator)));Check.Equal(1,inbox.AppliedCount);
                Check.True(state.Acknowledge(request));Check.True(!state.Pending);Check.True(state.Acknowledged);
                experiment.Resume(true);Check.Equal(Phase.Preparation,experiment.Phase);
                Check.True(!inbox.ApplyOnce(request,()=>experiment.Pause(Actor.Operator)));Check.Equal(Phase.Preparation,experiment.Phase);
                Check.True(state.Request()!=request);Check.True(state.Pending);
            });
            tests.Run("M3 stop wire allows only operator request and participant acknowledgement",()=>{
                var op=new PeerIdentity("s","operator",PeerRole.Operator,"simulator/operator",ControlSource.Simulator,"operator-clock");
                var participant=new PeerIdentity("s","participant",PeerRole.Participant,"simulator/participant",ControlSource.Simulator,"participant-clock");
                WirePacket decoded;string error;
                Check.True(WireCodec.TryDecode(WireCodec.Encode(WirePacket.SafetyStopRequest(op,4,1,"stop-1")),out decoded,out error));
                Check.Equal(WireKind.SafetyStopRequest,decoded.Kind);Check.Equal("stop-1",decoded.StopRequestId);
                Check.True(WireCodec.TryDecode(WireCodec.Encode(WirePacket.SafetyStopAck(participant,7,2,"stop-1")),out decoded,out error));
                Check.Equal(WireKind.SafetyStopAck,decoded.Kind);
                Check.Throws<ArgumentException>(()=>WirePacket.SafetyStopRequest(participant,0,0,"stop"));
                Check.Throws<ArgumentException>(()=>WirePacket.SafetyStopAck(op,0,0,"stop"));
                var receiver=new ReceiveMonitor(op,new DevelopmentFaultConfiguration(1,1,1),"device","participant-clock");
                Check.True(receiver.Observe(WirePacket.Hello(op,0,0,SessionConfiguration.ProtocolVersion),0).Accepted);
                var wrong=new PeerIdentity("other","operator",PeerRole.Operator,"simulator/operator",ControlSource.Simulator,"operator-clock");
                Check.True(!receiver.Observe(WirePacket.SafetyStopRequest(wrong,1,.1,"stop"),.1).Accepted);
                Check.True(receiver.Observe(WirePacket.SafetyStopRequest(op,2,.2,"stop"),.2).Accepted);
                Check.True(!receiver.Observe(WirePacket.SafetyStopRequest(op,1,.1,"old"),.3).Accepted);
            });
            tests.Run("M3 paired apps pause resume next block and render transitions retain correct context",()=>{
                int a=FreePort(),b=FreePort();while(a==b)b=FreePort();
                using(var participant=new ParticipantSimulationApplication("runtime-pair",a,b))
                using(var op=new OperatorSimulationApplication("runtime-pair",b,a,false))
                {
                    var e=participant.Experiment;participant.OwnerBeginPractice();
                    for(int i=0;i<8;i++)Answer(e);
                    participant.OwnerLockNextBlock(ControlSelection.SimulatorRehearsal);
                    Pump(participant,op,()=>participant.SourceReady(ControlSelection.SimulatorRehearsal));
                    participant.OwnerBeginBlock();Pump(participant,op,()=>e.Phase==Phase.MainTask);
                    e.MarkPresented(e.CurrentTrial.TrialId);e.Pause(Actor.Participant);participant.RefreshPresentation();
                    Check.True(!participant.CurrentAvatar.AvatarVisible);Check.Equal(Phase.Paused,e.View(Actor.Participant).Phase);
                    Check.Equal(null,e.View(Actor.Participant).TrialId);Check.True(participant.SourceReady(ControlSelection.SimulatorRehearsal));
                    e.Resume(true);participant.RefreshPresentation();Check.True(!participant.SourceReady(ControlSelection.SimulatorRehearsal));
                    Check.True(!participant.CurrentAvatar.HasActualPose);Check.True(participant.ApplicationWarnings.Count>0);
                    Pump(participant,op,()=>e.Phase==Phase.MainTask);Check.Equal(1,e.CurrentTrial.PlannedIndex);
                    Check.Equal(1,participant.CurrentAvatar.InputSample.Segment);Check.True(participant.CurrentAvatar.HeadValid);
                    for(int i=1;i<24;i++)Answer(e);participant.RefreshPresentation();
                    var view=e.View(Actor.Participant);Check.Equal(Phase.Questionnaire,view.Phase);Check.Equal(null,view.TrialId);
                    Check.True(!participant.CurrentAvatar.AvatarVisible);
                    participant.Audit.RecordPresentation(new AvatarPresentationRecord(new AuditContext(view.BlockIndex,view.Segment,view.TrialId),"test-render",participant.Clock.Now,"runtime model only",view.Phase,participant.CurrentAvatar));
                    Check.Equal(1,participant.Audit.Presentations[0].Context.Segment);Check.True(!participant.Audit.Presentations[0].AvatarVisible);
                    for(int i=0;i<5;i++)e.Rate(3);
                    Pump(participant,op,()=>e.ReadyEnabled,35);
                    Check.True(e.Ready());participant.RefreshPresentation();
                    Check.Equal(Phase.BlockPreparation,e.Phase);Check.Equal(1,e.BlockIndex);Check.Equal(0,e.Segment);
                    Check.Equal(null,participant.CurrentAvatar);
                    participant.OwnerLockNextBlock(ControlSelection.SimulatorRehearsal);
                    Check.True(!participant.SourceReady(ControlSelection.SimulatorRehearsal));
                    Pump(participant,op,()=>participant.SourceReady(ControlSelection.SimulatorRehearsal));
                    participant.OwnerBeginBlock();Pump(participant,op,()=>e.Phase==Phase.MainTask);
                    Check.Equal(1,participant.CurrentAvatar.InputSample.BlockIndex);Check.Equal(0,participant.CurrentAvatar.InputSample.Segment);
                    Check.True(participant.CurrentAvatar.HeadValid&&participant.CurrentAvatar.EyeValid);
                    Check.Equal(0,e.CurrentTrial.PlannedIndex);

                }
            });
        }
        static int FreePort(){using(var socket=new UdpClient(new IPEndPoint(IPAddress.Loopback,0)))return ((IPEndPoint)socket.Client.LocalEndPoint).Port;}
        static void Answer(Experiment e){e.MarkPresented(e.CurrentTrial.TrialId);var colour=e.CurrentTrial.Stimulus.Ink;e.ButtonChanged(colour,true);e.ButtonChanged(colour,false);}
        static void Pump(ParticipantSimulationApplication participant,OperatorSimulationApplication op,Func<bool> done,double timeout=4)
        {
            double deadline=participant.Clock.Now+timeout;
            while(!done()&&participant.Clock.Now<deadline){op.Tick();participant.Tick();Thread.Sleep(2);}
            Check.True(done(),"Paired apps did not reach the expected state");
        }
    }
}
