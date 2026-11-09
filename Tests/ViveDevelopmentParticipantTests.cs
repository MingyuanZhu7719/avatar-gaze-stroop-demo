using System;
using System.Collections.Generic;
using System.Net;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    public static class ViveDevelopmentParticipantTests
    {
        static Dictionary<ViveTrackpadDirection,Colour> Map()
        {return new Dictionary<ViveTrackpadDirection,Colour>{{ViveTrackpadDirection.Up,Colour.Red},{ViveTrackpadDirection.Right,Colour.Green},{ViveTrackpadDirection.Down,Colour.Blue},{ViveTrackpadDirection.Left,Colour.Yellow}};}
        static SessionConfiguration Config(RunKind kind=RunKind.Simulation)
        {return new SessionConfiguration("explicit-preview",42,new Allocation("fixture-BCAD",new[]{Condition.B,Condition.C,Condition.A,Condition.D},GazeTarget.Participant,new[]{0,1,2,3}),kind,"en",3,30,true,0,true,true,true);}
        static ViveDevelopmentParticipant New(ManualClock clock)
        {return new ViveDevelopmentParticipant(Config(),clock,DevelopmentFixtures.Geometry(),Map(),.5,true);}
        static VivePreviewTrackpadSample Sample(bool pressed=false,double x=0,double y=0,bool ready=true)
        {return new VivePreviewTrackpadSample(ready,pressed,x,y);}
        static VivePreviewTrackpadSample ColourDown(Colour colour)
        {
            switch(colour){case Colour.Red:return Sample(true,0,1);case Colour.Green:return Sample(true,1,0);case Colour.Blue:return Sample(true,0,-1);case Colour.Yellow:return Sample(true,-1,0);default:throw new ArgumentOutOfRangeException();}
        }
        static void Ready(ViveDevelopmentParticipant p){p.UpdateTrackpad(true,Sample());}
        static void Respond(ViveDevelopmentParticipant p)
        {
            var trial=p.CurrentTrial;p.UpdateTrackpad(true,Sample());p.MarkPresented(trial.TrialId);
            p.UpdateTrackpad(true,ColourDown(trial.Stimulus.Ink));p.UpdateTrackpad(true,Sample());
        }
        static void Practice(ViveDevelopmentParticipant p)
        {Ready(p);p.OwnerBeginPractice(Actor.Owner);for(int i=0;i<8;i++)Respond(p);Check.Equal(Phase.BlockPreparation,p.Phase);}
        static void Start(ViveDevelopmentParticipant p,ManualClock clock)
        {p.OwnerLockSource(Actor.Owner,ControlSelection.Automatic,"explicit-owner");p.OwnerBeginBlock(Actor.Owner);clock.Advance(3);p.Tick();Check.Equal(Phase.MainTask,p.Phase);}
        static void Aux(ViveDevelopmentParticipant p,bool confirm=false,int rating=0,bool display=true)
        {p.UpdateAuxiliary(confirm,false,false,rating,display);}

        sealed class SyntheticPreflight : IVivePreflightParticipantPort
        {
            public bool Active {get;set;}=true;
            public bool RecoveryDecisionRequired {get;private set;}
            public bool Ready;public int Begins,Polls,Losses;
            public bool Begin(TaskSnapshot task,bool confirmed,bool live)
            {Begins++;return task!=null&&task.Phase==Phase.BlockReady&&confirmed&&live;}
            public bool Poll(TaskSnapshot task,bool confirmed,bool live){Polls++;return Active;}
            public bool CandidateReady(TaskSnapshot task,bool confirmed,bool live){return Ready&&Active;}
            public void SignalLoss(string reason){Losses++;RecoveryDecisionRequired=true;Active=false;}
        }
        sealed class SyntheticApp : IVivePreflightAppPort
        {
            public bool IsOpen {get;set;}=true;public bool HasExplicitConfirmation {get;set;}=true;
            public bool RecoveryDecisionRequired {get;set;}public bool Faulted {get;set;}
            public int Ticks;
            public void TickParticipant(bool presentationReady){Check.True(!presentationReady);Ticks++;}
        }
        sealed class SyntheticClock:IClock {public double Now {get;set;}public string ClockId {get;private set;}public SyntheticClock(string id,double now){ClockId=id;Now=now;}}
        sealed class SyntheticEye:IViveProEyeProvider {public ViveProEyePoll Value;public ViveProEyePoll Poll(){return Value;}}
        sealed class SyntheticDatagrams:IUdpSocket
        {
            public IPEndPoint LocalEndpoint {get;private set;}public SyntheticDatagrams Other;
            readonly Queue<Tuple<byte[],IPEndPoint>> incoming=new Queue<Tuple<byte[],IPEndPoint>>();
            public SyntheticDatagrams(IPEndPoint local){LocalEndpoint=local;}
            public bool Poll(){return incoming.Count>0;}
            public byte[] Receive(ref IPEndPoint sender){var item=incoming.Dequeue();sender=item.Item2;return item.Item1;}
            public void Send(byte[] data,IPEndPoint remote){if(Other==null||!Other.LocalEndpoint.Equals(remote))throw new InvalidOperationException("Unexpected synthetic endpoint");Other.incoming.Enqueue(Tuple.Create((byte[])data.Clone(),LocalEndpoint));}
            public void Dispose(){}
        }
        static ClockSynchronizer SyntheticMap(string session,string stream,string local,string remote,double offset)
        {
            var sync=new ClockSynchronizer(local,remote,new DevelopmentClockConfiguration(3,.2,.2,4,200));
            var peer=new PeerIdentity(session,"synthetic-peer",PeerRole.Operator,stream,ControlSource.LiveOperator,remote);
            for(int i=0;i<3;i++){double sent=1+i*.5;string id="clock-"+i;Check.True(sync.RegisterRequest(id,sent));
                Check.True(sync.ObserveReply(WirePacket.TimeReply(peer,i,sent+offset,id,local,sent,sent+offset),sent));}
            return sync;
        }

        public static void Register(TestSuite suite)
        {
            suite.Run("VIVE observed participant binds simulated pose to the task actually sent and invalidates the previous trial",()=>{
                var clock=new ManualClock();var opClock=new SyntheticClock("operator-clock",3.52);
                var p=new ViveDevelopmentParticipant(Config(),clock,DevelopmentFixtures.Geometry(),Map(),.5,true,
                    new ExperimentIncrementalOptions(4096,128,16L*1024*1024),4096);
                Practice(p);p.OwnerLockSource(Actor.Owner,ControlSelection.Automatic,"fixture-owner");p.OwnerBeginBlock(Actor.Owner);
                clock.Advance(3);p.Tick();Check.Equal(Phase.MainTask,p.Phase);Ready(p);p.MarkPresented(p.CurrentTrial.TrialId);
                var host=p.CreateSyntheticWozAuthority(()=>{});var state=host.ReadCurrent();
                Check.True(state!=null&&state.TaskFresh&&state.Task.TrialId==p.CurrentTrial.TrialId&&state.LockedSelection==ControlSelection.Automatic);
                string epoch="fixture-epoch",mapping="fixture-mapping";var pAddress=new IPEndPoint(IPAddress.Loopback,43601);var oAddress=new IPEndPoint(IPAddress.Loopback,43602);
                var ps=new SyntheticDatagrams(pAddress);var os=new SyntheticDatagrams(oAddress);ps.Other=os;os.Other=ps;
                var sent=new ViveSimulationTrialTask(state.Task,epoch,mapping,clock.ClockId,state.TaskRevision,0,clock.Now);
                var packet=ViveSimulationTrialPoseCodec.EncodeTask(sent);
                var receiver=new ViveSimulationTrialPoseReceiver(clock,SyntheticMap(p.SessionId,"simulation-stream",clock.ClockId,opClock.ClockId,.5),p.SessionId,epoch,mapping,opClock.ClockId,.5,8,true);
                var sender=new ViveSimulationTrialPoseSender(opClock,p.SessionId,epoch,mapping,clock.ClockId,.5,true);
                ps.Send(packet,oAddress);receiver.RecordTaskSocketAccepted(sent,true);
                IPEndPoint remote=null;var receivedTask=os.Receive(ref remote);double taskReceipt=opClock.Now;
                Check.Equal(pAddress,remote);Check.True(sender.ObserveTask(receivedTask,true,taskReceipt),sender.LastIssue);
                opClock.Now=3.57;byte[] pose;Check.True(sender.TryCreatePose(opClock.Now,opClock.Now,Rotation.Identity,new Vec3(0,0,1),out pose),sender.LastIssue);
                os.Send(pose,pAddress);opClock.Now=3.59;byte[] delayed;
                Check.True(sender.TryCreatePose(opClock.Now,opClock.Now,Rotation.Identity,new Vec3(0,0,1),out delayed),sender.LastIssue);
                clock.Now=3.12;remote=null;var receivedPose=ps.Receive(ref remote);double poseReceipt=clock.Now;
                Check.Equal(oAddress,remote);Check.True(receiver.Observe(receivedPose,true,poseReceipt,state.Task,state.TaskRevision),receiver.LastIssue);
                ViveSimulationTrialPoseFrame visible;double originalReceipt;
                Check.True(receiver.TryGetCurrent(state.Task,state.TaskRevision,out visible,out originalReceipt));
                Check.Equal(p.CurrentTrial.TrialId,visible.Task.Task.TrialId);Check.Near(poseReceipt,originalReceipt);
                // No invalidation datagram exists in this simulation protocol.
                // An operator-side failure alone cannot instantaneously clear a
                // remote receiver; the owner must clear it on an independent
                // loss signal, or the old candidate expires by age.
                opClock.Now=3.60;byte[] invalid;
                Check.True(!sender.TryCreatePose(opClock.Now,opClock.Now,Rotation.Identity,new Vec3(0,0,0),out invalid));
                Check.True(receiver.Current(state.Task,state.TaskRevision)!=null);
                receiver.Clear();Check.True(receiver.Current(state.Task,state.TaskRevision)==null);
                var oldTrial=p.CurrentTrial.TrialId;p.UpdateTrackpad(true,ColourDown(p.CurrentTrial.Stimulus.Ink));
                var next=host.ReadCurrent();Check.True(next!=null&&next.Task.TrialId!=oldTrial&&next.TaskRevision>state.TaskRevision);
                Check.True(receiver.Current(next.Task,next.TaskRevision)==null);
                clock.Advance(.01);Check.True(!receiver.Observe(delayed,true,clock.Now,next.Task,next.TaskRevision));
                Check.True(receiver.Current(next.Task,next.TaskRevision)==null&&!receiver.CanEnterCountdown&&!receiver.CanRecordOrExport);
                Check.True(!receiver.CryptographicallyAuthenticated&&!p.IsResearchSession);
            });
            suite.Run("VIVE observed participant uses synthetic challenge proof channel without granting Live entry",()=>{
                var clock=new ManualClock{Now=3};var opClock=new SyntheticClock("operator-clock",3.5);
                var p=new ViveDevelopmentParticipant(Config(),clock,DevelopmentFixtures.Geometry(),Map(),.5,true,
                    new ExperimentIncrementalOptions(4096,128,16L*1024*1024),4096);
                Practice(p);int clears=0;var host=p.CreateSyntheticWozAuthority(()=>clears++);
                p.LockSyntheticWozForTest(Actor.Owner,"fixture-owner");Check.True(p.ConfirmSyntheticWozForTest(Actor.Owner));
                var task=host.ReadAuthoritativeTask();string mapping=ViveProEyeRehearsalCoordinates.MappingVersion,epoch="simulation-preflight";
                string stream=ViveDevelopmentTransport.OperatorStream(mapping,epoch);var pAddress=new IPEndPoint(IPAddress.Loopback,43501);var oAddress=new IPEndPoint(IPAddress.Loopback,43502);
                var pConfig=new VivePreflightEndpointConfiguration(PeerRole.Participant,pAddress,oAddress,false,p.SessionId,stream,opClock.ClockId,clock.ClockId,mapping,epoch,"vive-pro-eye-no-serial",.5);
                var oConfig=new VivePreflightEndpointConfiguration(PeerRole.Operator,oAddress,pAddress,false,p.SessionId,stream,opClock.ClockId,clock.ClockId,mapping,epoch,"vive-pro-eye-no-serial",.5);
                using(var pc=new ViveDevelopmentPreflightChannel(pConfig,clock,SyntheticMap(p.SessionId,stream,clock.ClockId,opClock.ClockId,.5)))
                using(var oc=new ViveDevelopmentPreflightChannel(oConfig,opClock,SyntheticMap(p.SessionId,stream,opClock.ClockId,clock.ClockId,-.5)))
                {
                    var ps=new ViveInMemoryDatagramSocket(pAddress);var os=new ViveInMemoryDatagramSocket(oAddress);ps.Other=os;os.Other=ps;
                    pc.StartForTest(ps);oc.StartForTest(os);pc.ConfirmSource(true);oc.ConfirmSource(true);
                    var operatorTask=new VivePreflightOperatorTask(task,true,true,0,opClock.Now);
                    Check.True(oc.ObserveOperatorTask(operatorTask));
                    var coordinator=new ViveDevelopmentPreflightCoordinator(p.SessionId,host,new SyntheticApp(),new VivePreflightParticipantChannelPort(pc));
                    coordinator.Tick();Check.True(!coordinator.CandidateObserved&&!coordinator.CanEnterCountdown);
                    opClock.Now=3.56;Check.True(oc.PollOperator(operatorTask),oc.LastIssue);
                    var sensor=new SyntheticEye();opClock.Now=3.57;
                    var ray=new ViveEyeRay(2,false,true,new Vec3(0,0,0),new Vec3(.5,0,1));
                    sensor.Value=new ViveProEyePoll(new ViveHeadFrame(1,opClock.Now,true,true,Rotation.Identity),new ViveEyeFrame(1,8,opClock.Now,false,null,null,ray),ViveEyeRuntimeState.Working,ViveEyeCalibrationState.ReportedReady,true,0);
                    var source=new ViveProEyeHeadEyeSource(p.SessionId,task.BlockIndex,task.Segment,stream,opClock,sensor,new ViveProEyeRehearsalCoordinates(),.5);
                    HeadEyeSample sample;Check.True(source.TryRead(out sample));opClock.Now=3.58;Check.True(oc.SendProof(source.LastObservation,true,operatorTask),oc.LastIssue);
                    clock.Now=3.12;coordinator.Tick();Check.True(coordinator.CandidateObserved&&!coordinator.CanEnterCountdown&&!pc.CanEnterCountdown);
                    Check.Equal(Phase.BlockReady,p.Phase);Check.True(!p.IsResearchSession&&!pc.CryptographicallyAuthenticated);
                    clock.Now=3.8;coordinator.Tick();Check.True(coordinator.RecoveryDecisionRequired&&!coordinator.CandidateObserved);
                    Check.Equal(Phase.BlockReady,p.Phase);Check.True(clears>0);
                }
            });
            suite.Run("VIVE observed participant simulation binds actual source choice and preflight without admitting countdown",()=>{
                var clock=new ManualClock();var p=new ViveDevelopmentParticipant(Config(),clock,DevelopmentFixtures.Geometry(),Map(),.5,true,
                    new ExperimentIncrementalOptions(4096,128,16L*1024*1024),4096);
                Practice(p);int clears=0;var host=p.CreateSyntheticWozAuthority(()=>clears++);
                Check.Throws<UnauthorizedAccessException>(()=>p.LockSyntheticWozForTest(Actor.Participant,"fixture-owner"));
                Check.Throws<InvalidOperationException>(()=>p.OwnerLockSource(Actor.Owner,ControlSelection.LiveWoz,"fixture-owner"));
                p.LockSyntheticWozForTest(Actor.Owner,"fixture-owner");Check.Equal(Phase.BlockReady,p.Phase);
                var task=host.ReadAuthoritativeTask();Check.Equal(p.SessionId,task.SessionId);Check.Equal(Condition.B,task.Condition.Value);
                string mapping="synthetic-map",epoch="synthetic-epoch";
                var context=new ViveWozSourceContext(p.SessionId,"fixture-owner",ViveDevelopmentTransport.OperatorStream(mapping,epoch),mapping,epoch,
                    "synthetic-challenge",clock.ClockId,"operator-clock",task.BlockIndex,task.Segment,task.Condition.Value);
                var source=new ViveWozSourceOwner(context,clock,host);
                Check.Throws<InvalidOperationException>(()=>source.Confirm(true));
                Check.True(p.ConfirmSyntheticWozForTest(Actor.Owner));var current=host.ReadCurrent();
                Check.True(current!=null&&current.ExplicitLocalChoiceStillActive&&current.TaskFresh&&current.LockedSelection==ControlSelection.LiveWoz);
                Check.Equal("fixture-owner",current.BoundOwnerId);Check.True(current.TaskRevision>0);
                Check.True(source.Confirm(true).Length>0&&source.LocallyConfirmed);
                var app=new SyntheticApp();var port=new SyntheticPreflight{Ready=true};
                var coordinator=new ViveDevelopmentPreflightCoordinator(p.SessionId,host,app,port);
                coordinator.Tick();Check.Equal(1,port.Begins);Check.Equal(1,port.Polls);Check.Equal(1,app.Ticks);
                Check.True(coordinator.CandidateObserved&&!coordinator.CanEnterCountdown&&!coordinator.CryptographicallyAuthenticated);
                clock.Advance(10);p.Tick();Check.Equal(Phase.BlockReady,p.Phase);
                coordinator.SignalRemoteLoss();Check.True(coordinator.RecoveryDecisionRequired&&!coordinator.CandidateObserved);
                Check.Equal(1,port.Losses);Check.True(clears>0);Check.Equal(Phase.BlockReady,p.Phase);
                Check.Throws<InvalidOperationException>(()=>p.OwnerBeginBlock(Actor.Owner));
                Check.True(p.ObservationFaulted&&host.ReadAuthoritativeTask()==null&&host.ReadCurrent()==null&&host.LiveSourceSelected);
                p.ParticipantWithdraw();Check.True(!source.LocallyConfirmed);Check.Equal(Phase.Ended,p.Phase);
            });
            suite.Run("VIVE synthetic WoZ seam requires observed participant and never changes public source policy",()=>{
                var p=New(new ManualClock());Practice(p);
                Check.Throws<InvalidOperationException>(()=>p.LockSyntheticWozForTest(Actor.Owner,"fixture-owner"));
                Check.Throws<InvalidOperationException>(()=>p.ConfirmSyntheticWozForTest(Actor.Owner));
                Check.Throws<ArgumentException>(()=>p.CreateSyntheticWozAuthority(()=>{}));
                Check.Throws<InvalidOperationException>(()=>p.OwnerLockSource(Actor.Owner,ControlSelection.LiveWoz,"fixture-owner"));
                Check.Equal(Phase.BlockPreparation,p.Phase);
            });
            suite.Run("VIVE task requires explicit mapping owner configuration and simulation-only exact preview policy",()=>{
                var clock=new ManualClock();var geometry=DevelopmentFixtures.Geometry();
                Check.Throws<InvalidOperationException>(()=>new ViveDevelopmentParticipant(Config(),clock,geometry,Map(),.5,false));
                Check.Throws<ArgumentNullException>(()=>new ViveDevelopmentParticipant(Config(),clock,geometry,null,.5,true));
                var duplicate=Map();duplicate[ViveTrackpadDirection.Up]=Colour.Green;
                Check.Throws<ArgumentException>(()=>new ViveDevelopmentParticipant(Config(),clock,geometry,duplicate,.5,true));
                Check.Throws<ArgumentOutOfRangeException>(()=>new ViveDevelopmentParticipant(Config(),clock,geometry,Map(),0,true));
                Check.Throws<InvalidOperationException>(()=>new ViveDevelopmentParticipant(Config(RunKind.Apparatus),clock,geometry,Map(),.5,true));
                var altered=new SessionConfiguration("wrong-flow",42,Config().Allocation,RunKind.Simulation,"en",1,30,true,0,true,true,true);
                Check.Throws<ArgumentException>(()=>new ViveDevelopmentParticipant(altered,clock,geometry,Map(),.5,true));
                var p=New(clock);Check.True(!p.InputReady&&!p.RecordsEyeSamples&&!p.RecordsTaskFiles&&!p.UsesNetwork&&!p.IsResearchSession);
                Check.True(p.ScopeLabel.Contains("SIMULATED")&&p.ScopeLabel.Contains("NO RESEARCH"));
                Check.Throws<InvalidOperationException>(()=>p.OwnerBeginPractice(Actor.Owner));
                Check.Throws<UnauthorizedAccessException>(()=>p.OwnerBeginPractice(Actor.Participant));
                Check.Equal(Phase.Preparation,p.Phase);Check.Equal(0,p.TrialCount);
            });
            suite.Run("VIVE task response requires presentation new edge and matching explicit map",()=>{
                var p=New(new ManualClock());Ready(p);p.OwnerBeginPractice(Actor.Owner);
                var first=p.CurrentTrial;p.UpdateTrackpad(true,Sample());p.UpdateTrackpad(true,ColourDown(first.Stimulus.Ink));
                Check.Equal(TrialOutcome.Pending,first.Outcome);Check.Equal(1,p.TrialCount);
                p.MarkPresented(first.TrialId);p.UpdateTrackpad(true,ColourDown(first.Stimulus.Ink));Check.Equal(TrialOutcome.Pending,first.Outcome);
                p.UpdateTrackpad(true,Sample());p.UpdateTrackpad(true,ColourDown(first.Stimulus.Ink));
                Check.Equal(TrialOutcome.Responded,first.Outcome);Check.Equal((Colour?)first.Stimulus.Ink,first.Response);Check.Equal(true,first.Correct.Value);
                Check.Equal(2,p.TrialCount);p.UpdateTrackpad(true,ColourDown(first.Stimulus.Ink));Check.Equal(2,p.TrialCount);
            });
            suite.Run("VIVE Trigger confirm cannot manually pause practice countdown or main trials",()=>{
                var clock=new ManualClock();var p=New(clock);Ready(p);p.OwnerBeginPractice(Actor.Owner);Ready(p);
                var practice=p.CurrentTrial;p.MarkPresented(practice.TrialId);
                Aux(p);Aux(p,true);Aux(p,true);Aux(p);Aux(p,true);
                Check.Equal(Phase.Practice,p.Phase);Check.Equal(TrialOutcome.Pending,practice.Outcome);
                for(int i=0;i<8;i++)Respond(p);
                p.OwnerLockSource(Actor.Owner,ControlSelection.Automatic,"explicit-owner");p.OwnerBeginBlock(Actor.Owner);
                Ready(p);Aux(p);Aux(p,true);Aux(p,true);Check.Equal(Phase.Countdown,p.Phase);
                clock.Advance(3);p.Tick();Ready(p);var main=p.CurrentTrial;p.MarkPresented(main.TrialId);
                Aux(p);Aux(p,true);Aux(p,true);Aux(p);Aux(p,true);
                Check.Equal(Phase.MainTask,p.Phase);Check.Equal(TrialOutcome.Pending,main.Outcome);
                foreach(var ev in p.ReadEventsAfter(-1,4096))Check.True(ev.Kind!="explicit_pause");
                p.OwnerPause(Actor.Owner);Check.Equal(Phase.Paused,p.Phase);Check.Equal(TrialOutcome.Interrupted,main.Outcome);
                Ready(p);Aux(p,true);Check.Equal(Phase.Paused,p.Phase);
                Aux(p);Aux(p,true);Check.Equal(Phase.Countdown,p.Phase);
                clock.Advance(3);p.Tick();Check.Equal(Phase.MainTask,p.Phase);
                Check.True(p.CurrentTrial.TrialId!=main.TrialId);
            });
            suite.Run("VIVE pause diagnostic retains the first loss through recovery and repeated owner requests",()=>{
                var p=New(new ManualClock());Ready(p);p.OwnerBeginPractice(Actor.Owner);Ready(p);
                Check.Equal(0L,p.PauseDiagnosticRevision);Check.True(p.LastPauseDiagnostic==null);
                p.UpdateNeutralPending(true);Check.Equal(Phase.Practice,p.Phase);Check.Equal(0L,p.PauseDiagnosticRevision);
                p.UpdateTrackpad(false,Sample());Check.Equal(Phase.Paused,p.Phase);
                Check.Equal(1L,p.PauseDiagnosticRevision);string first=p.LastPauseDiagnostic;
                Check.True(first.Contains("head/input"));Ready(p);p.OwnerPause(Actor.Owner);
                Check.Equal(1L,p.PauseDiagnosticRevision);Check.Equal(first,p.LastPauseDiagnostic);
                Aux(p,true);Check.Equal(Phase.Paused,p.Phase);Aux(p);Aux(p,true);Check.Equal(Phase.Practice,p.Phase);
                Check.Equal(first,p.LastPauseDiagnostic);
                p.UpdateTrackpad(true,Sample(false,0,0,false));Check.Equal(Phase.Paused,p.Phase);
                Check.Equal(2L,p.PauseDiagnosticRevision);Check.True(p.LastPauseDiagnostic.Contains("controller sample"));
                p.UpdateTrackpad(false,Sample());Check.Equal(2L,p.PauseDiagnosticRevision);
                Ready(p);Aux(p);Aux(p,true);Check.Equal(Phase.Practice,p.Phase);
                p.OwnerPause(Actor.Owner);Check.Equal(3L,p.PauseDiagnosticRevision);
                Check.True(p.LastPauseDiagnostic.Contains("Owner or software"));
            });
            suite.Run("VIVE task loss releases held input interrupts current trial and requires explicit resume",()=>{
                var p=New(new ManualClock());Ready(p);p.OwnerBeginPractice(Actor.Owner);p.UpdateTrackpad(true,Sample());
                var first=p.CurrentTrial;p.MarkPresented(first.TrialId);p.UpdateTrackpad(false,ColourDown(first.Stimulus.Ink));
                Check.True(!p.InputReady);Check.Equal(Phase.Paused,p.Phase);Check.Equal(TrialOutcome.Interrupted,first.Outcome);
                p.UpdateTrackpad(true,ColourDown(first.Stimulus.Ink));p.Tick();Check.Equal(Phase.Paused,p.Phase);
                p.UpdateAuxiliary(true,false,false,0,true);Check.Equal(Phase.Paused,p.Phase);
                p.UpdateAuxiliary(false,false,false,0,true);p.UpdateAuxiliary(true,false,false,0,true);
                Check.Equal(Phase.Practice,p.Phase);Check.True(p.CurrentTrial.TrialId!=first.TrialId);Check.Equal(TrialOutcome.Interrupted,first.Outcome);
                p.UpdateTrackpad(true,ColourDown(first.Stimulus.Ink));Check.Equal(TrialOutcome.Pending,p.CurrentTrial.Outcome);
            });
            suite.Run("VIVE phase neutral wait suppresses actions without fabricating a tracking-loss pause",()=>{
                var p=New(new ManualClock());Ready(p);p.OwnerBeginPractice(Actor.Owner);p.UpdateTrackpad(true,Sample());
                var trial=p.CurrentTrial;p.MarkPresented(trial.TrialId);
                var wrong=(Colour)(((int)trial.Stimulus.Ink+1)%4);
                p.UpdateTrackpad(true,ColourDown(wrong));Check.Equal(Phase.PracticeCorrection,p.Phase);
                p.UpdateNeutralPending(true);Check.Equal(Phase.PracticeCorrection,p.Phase);Check.True(!p.InputReady);
                p.UpdateAuxiliary(true,false,false,0,true);Check.Equal(Phase.PracticeCorrection,p.Phase);
                p.UpdateNeutralPending(true);Check.Equal(Phase.PracticeCorrection,p.Phase);
                Ready(p);p.UpdateAuxiliary(false,false,false,0,true);p.UpdateAuxiliary(true,false,false,0,true);
                Check.Equal(Phase.Practice,p.Phase);Check.True(p.CurrentTrial.TrialId!=trial.TrialId);
                p.UpdateNeutralPending(false);Check.Equal(Phase.Paused,p.Phase);Check.Equal(TrialOutcome.Interrupted,p.CurrentTrial.Outcome);
            });
            suite.Run("VIVE owner pause and participant withdrawal remain available without ready input",()=>{
                var p=New(new ManualClock());Ready(p);p.OwnerBeginPractice(Actor.Owner);p.MarkPresented(p.CurrentTrial.TrialId);
                Check.Throws<UnauthorizedAccessException>(()=>p.OwnerPause(Actor.Participant));
                p.OwnerPause(Actor.Owner);Check.Equal(Phase.Paused,p.Phase);Check.Equal(TrialOutcome.Interrupted,p.CurrentTrial.Outcome);
                p.UpdateTrackpad(false,Sample());Check.True(!p.InputReady);p.ParticipantWithdraw();
                Check.Equal(Phase.Ended,p.Phase);Check.Equal(EndReason.Withdrawn,p.EndReason.Value);Check.True(!p.Completed);
            });
            suite.Run("VIVE task owner cannot select live source or enter main without current readiness",()=>{
                var clock=new ManualClock();var p=New(clock);Practice(p);
                Check.Throws<UnauthorizedAccessException>(()=>p.OwnerLockSource(Actor.Participant,ControlSelection.Automatic,"x"));
                Check.Throws<InvalidOperationException>(()=>p.OwnerLockSource(Actor.Owner,ControlSelection.LiveWoz,"x"));
                Check.Throws<InvalidOperationException>(()=>p.OwnerLockSource(Actor.Owner,ControlSelection.SimulatorRehearsal,"x"));
                p.OwnerLockSource(Actor.Owner,ControlSelection.Automatic,"explicit-owner");
                p.UpdateTrackpad(false,Sample());Check.Throws<InvalidOperationException>(()=>p.OwnerBeginBlock(Actor.Owner));
                Ready(p);p.OwnerBeginBlock(Actor.Owner);clock.Advance(3);p.Tick();Check.Equal(Phase.MainTask,p.Phase);
                Check.Equal(ControlSource.Automatic,p.CurrentAvatar.ActualSource.Value);
            });
            suite.Run("VIVE task full four-block flow preserves intro rating direction rests and source lock",()=>{
                var clock=new ManualClock();var p=New(clock);Practice(p);Start(p,clock);
                for(int block=0;block<4;block++)
                {
                    Check.Equal(block,p.BlockIndex);Check.Equal(Phase.MainTask,p.Phase);
                    Check.Equal(block!=3,p.CurrentAvatar.AvatarVisible);
                    for(int trial=0;trial<24;trial++)Respond(p);
                    Check.Equal(Phase.Questionnaire,p.Phase);Check.True(p.QuestionnaireInstructionPending);
                    Aux(p);Aux(p,true);Check.True(!p.QuestionnaireInstructionPending);Check.Equal(0,p.RatingCount-block*5);
                    Aux(p,true);Check.Equal(0,p.RatingCount-block*5);Aux(p,false);
                    // UP decreases 3 to 2, and the held confirm that dismissed the
                    // instruction cannot also submit the rating.
                    Aux(p,false,-1);Check.Equal(2,p.CandidateRating);
                    Check.True(ViveQuestionnairePresentation.Selection(p.CandidateRating,"en").Contains("SELECTED: 2"));
                    Check.True(ViveQuestionnairePresentation.Option(2,p.CandidateRating,"en").StartsWith("> ",StringComparison.Ordinal));
                    Aux(p,false,0);Aux(p,true,0);
                    Check.Equal(block*5+1,p.RatingCount);
                    for(int i=1;i<5;i++){Aux(p,false);Aux(p,true);Check.Equal(block*5+i+1,p.RatingCount);}
                    if(block<3)
                    {
                        Check.Equal(Phase.Rest,p.Phase);Aux(p,false);Aux(p,true);Check.Equal(Phase.BlockReady,p.Phase);
                        Check.Equal(ControlSelection.Automatic,p.LockedControl.Value);p.Tick();Check.Equal(Phase.Countdown,p.Phase);
                        clock.Advance(3);p.Tick();Check.Equal(Phase.MainTask,p.Phase);
                    }
                }
                Check.Equal(Phase.Ended,p.Phase);Check.Equal(EndReason.Completed,p.EndReason.Value);
                Check.Equal(104,p.TrialCount);Check.Equal(20,p.RatingCount);
            });
            suite.Run("VIVE auxiliary display gates and held input cannot dismiss and rate in same edge",()=>{
                var clock=new ManualClock();var p=New(clock);Practice(p);Start(p,clock);for(int i=0;i<24;i++)Respond(p);
                Aux(p,false,0,false);Aux(p,true,0,false);Check.True(p.QuestionnaireInstructionPending);
                Aux(p,true,0,true);Check.True(p.QuestionnaireInstructionPending);
                Aux(p,false,0,true);Aux(p,true,0,true);Check.True(!p.QuestionnaireInstructionPending);
                Aux(p,true,0,true);Check.Equal(0,p.RatingCount);Aux(p,false,0,true);
                Aux(p,false,1,true);Check.Equal(4,p.CandidateRating);
                Check.True(ViveQuestionnairePresentation.Selection(p.CandidateRating,"en").Contains("SELECTED: 4"));
                Aux(p,false,1,true);Check.Equal(4,p.CandidateRating);
                Aux(p,true,1,true);Check.Equal(1,p.RatingCount);
            });
            suite.Run("VIVE task events expose bounded immutable authoritative snapshots with exact identity",()=>{
                var clock=new ManualClock();var p=New(clock);
                Check.Equal("explicit-preview",p.SessionId);Check.Equal(clock.ClockId,p.EventClockId);
                var initial=p.ReadEventsAfter(-1,1);Check.Equal(1,initial.Count);Check.Equal(0L,initial[0].Sequence);
                var fields=(IDictionary<string,string>)initial[0].Fields;
                Check.Throws<NotSupportedException>(()=>fields.Add("synthetic-change","denied"));
                Ready(p);p.OwnerBeginPractice(Actor.Owner);var following=p.ReadEventsAfter(initial[0].Sequence,4096);
                Check.True(following.Count>0);long previous=initial[0].Sequence;
                foreach(var item in following){Check.True(item.Sequence>previous);Check.Equal(p.SessionId,item.SessionId);Check.Equal(p.EventClockId,item.ClockId);previous=item.Sequence;}
                Check.Equal(0,p.ReadEventsAfter(previous,1).Count);Check.Equal(1,initial.Count);
                Check.Throws<ArgumentOutOfRangeException>(()=>p.ReadEventsAfter(-2,1));
                Check.Throws<ArgumentOutOfRangeException>(()=>p.ReadEventsAfter(-1,0));
                Check.Throws<ArgumentOutOfRangeException>(()=>p.ReadEventsAfter(-1,4097));
                Check.Throws<ArgumentOutOfRangeException>(()=>p.ReadEventsAfter(previous+1,1));
            });
        }
    }
}
