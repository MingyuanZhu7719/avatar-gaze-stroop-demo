using System;
using System.Collections.Generic;
using System.Net;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    // Byte arrays, manual clocks and a memory sink only. Endpoint objects below
    // are labels, never sockets. No reflection, native provider or persisted data.
    // The participant's Live entry is intentionally closed: downstream lifecycle
    // checks use a SEPARATE public Core Experiment simulation, not a bypass of it.
    public static class ViveWozOfflineLifecycleTests
    {
        const string Session="offline-woz",Epoch="offline-epoch",Owner="offline-owner";
        static readonly string Mapping=ViveProEyeRehearsalCoordinates.MappingVersion;
        static readonly string Stream=ViveDevelopmentTransport.OperatorStream(Mapping,Epoch);
        sealed class Clock:IClock
        {public string ClockId {get;private set;}public double Now {get;set;}public Clock(string id,double now){ClockId=id;Now=now;}}
        sealed class Sink:IViveLivePresentationSink
        {
            public AvatarOutput Current;public int Applies;public bool FailClear;public Action OnClear;
            public void Apply(AvatarOutput value){Current=value;Applies++;}
            public void Clear(){var action=OnClear;OnClear=null;if(action!=null)action();if(FailClear)throw new InvalidOperationException("synthetic clear failure");Current=null;}
        }
        sealed class App:IVivePreflightAppPort
        {
            public bool IsOpen {get{return true;}}public bool HasExplicitConfirmation {get{return true;}}
            public bool RecoveryDecisionRequired {get{return false;}}public bool Faulted {get{return false;}}
            public void TickParticipant(bool ready){Check.True(!ready,"Candidate must not authorize presentation");}
        }
        sealed class PreflightPort:IVivePreflightParticipantPort
        {
            readonly VivePreflightParticipant candidate;
            public byte[] Challenge;public bool Active {get;private set;}=true;
            public bool RecoveryDecisionRequired {get{return candidate.RecoveryDecisionRequired;}}
            public PreflightPort(VivePreflightParticipant candidate){this.candidate=candidate;}
            public bool Begin(TaskSnapshot task,bool confirmed,bool live){Challenge=candidate.BeginContext(task,confirmed,live);return Challenge!=null;}
            public bool Poll(TaskSnapshot task,bool confirmed,bool live){return CandidateReady(task,confirmed,live);}
            public bool CandidateReady(TaskSnapshot task,bool confirmed,bool live){return candidate.CandidateReady(task,confirmed,live);}
            public void SignalLoss(string reason){Active=false;candidate.SignalLoss(reason);}
        }
        sealed class ClosedPreflight:IVivePreflightParticipantPort
        {
            public bool Active {get{return false;}}public bool RecoveryDecisionRequired {get{return true;}}
            public bool Begin(TaskSnapshot t,bool a,bool b){return false;}public bool Poll(TaskSnapshot t,bool a,bool b){return false;}
            public bool CandidateReady(TaskSnapshot t,bool a,bool b){return false;}public void SignalLoss(string reason){}
        }
        sealed class Eye:IViveProEyeProvider
        {public ViveProEyePoll Value;public ViveProEyePoll Poll(){return Value;}}
        static SessionConfiguration Configuration()
        {return new SessionConfiguration(Session,42,new Allocation("offline-BCAD",new[]{Condition.B,Condition.C,Condition.A,Condition.D},GazeTarget.Participant,new[]{0,1,2,3}),RunKind.Simulation,"en",3,30,true,0,true,true,true);}
        static ClockSynchronizer Map(Clock local,Clock remote)
        {
            var sync=new ClockSynchronizer(local.ClockId,remote.ClockId,new DevelopmentClockConfiguration(3,.2,.2,100,50));
            var peer=new PeerIdentity(Session,"offline-peer",PeerRole.Operator,Stream,ControlSource.LiveOperator,remote.ClockId);
            for(int i=0;i<3;i++){double t=1+i*.5;string id="offline-sync-"+i;Check.True(sync.RegisterRequest(id,t));
                Check.True(sync.ObserveReply(WirePacket.TimeReply(peer,i,t,id,local.ClockId,t,t),t),sync.LastRejection);}
            return sync;
        }
        static ViveDevelopmentParticipant Participant(Clock clock)
        {
            var map=new Dictionary<ViveTrackpadDirection,Colour>{{ViveTrackpadDirection.Up,Colour.Red},{ViveTrackpadDirection.Right,Colour.Green},{ViveTrackpadDirection.Down,Colour.Blue},{ViveTrackpadDirection.Left,Colour.Yellow}};
            return new ViveDevelopmentParticipant(Configuration(),clock,DevelopmentFixtures.Geometry(),map,.5,true,new ExperimentIncrementalOptions(4096,128,16L*1024*1024),4096);
        }
        static void Neutral(ViveDevelopmentParticipant p){p.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,false,0,0));}
        static void Answer(ViveDevelopmentParticipant p)
        {
            Neutral(p);var t=p.CurrentTrial;p.MarkPresented(t.TrialId);var c=t.Stimulus.Ink;
            p.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,true,c==Colour.Green?1:c==Colour.Yellow?-1:0,c==Colour.Red?1:c==Colour.Blue?-1:0));Neutral(p);
        }
        static void Practice(ViveDevelopmentParticipant p)
        {Neutral(p);p.OwnerBeginPractice(Actor.Owner);for(int i=0;i<8;i++)Answer(p);Check.Equal(Phase.BlockPreparation,p.Phase);}
        static void Answer(Experiment e)
        {var trial=e.CurrentTrial;e.MarkPresented(trial.TrialId);Check.True(e.ButtonChanged(trial.Stimulus.Ink,true));e.ButtonChanged(trial.Stimulus.Ink,false);}
        static Experiment Engine(Clock clock,bool live=true)
        {
            var e=new Experiment(Configuration(),clock);e.BeginFamiliarization(Actor.Owner);e.BeginPractice(Actor.Owner);
            for(int i=0;i<8;i++)Answer(e);e.LockBlock(Actor.Owner,live?ControlSelection.LiveWoz:ControlSelection.Automatic,Owner);return e;
        }
        static TaskSnapshot Task(Experiment e)
        {var v=e.View(Actor.Operator);return new TaskSnapshot(Session,e.BlockIndex,e.Segment,v.TrialId,v.Stimulus==null?(Colour?)null:v.Stimulus.Word,v.Stimulus==null?(Colour?)null:v.Stimulus.Ink,e.Phase,e.CurrentCondition);}
        sealed class PoseLoop
        {
            public readonly Clock P=new Clock("offline-participant",3),O=new Clock("offline-operator",3);
            public readonly Experiment Experiment;public readonly Sink Sink=new Sink();public readonly ViveLivePresentationAdapter View;
            public readonly ViveSimulationTrialPoseSender Sender;public readonly ViveSimulationTrialPoseReceiver Receiver;
            long revision,packet;public TaskSnapshot Current;public long Revision {get{return revision;}}
            public PoseLoop()
            {
                Experiment=Engine(P);View=new ViveLivePresentationAdapter(P,Session,Mapping,.5,Sink,true);
                Sender=new ViveSimulationTrialPoseSender(O,Session,Epoch,Mapping,P.ClockId,.5,true);
                Receiver=new ViveSimulationTrialPoseReceiver(P,Map(P,O),Session,Epoch,Mapping,O.ClockId,.5,64,true);
                Begin();
            }
            public void Begin(){Experiment.BeginCountdown(Actor.Owner,true);P.Now+=3;Experiment.Tick();Publish();Hidden();}
            public void Publish()
            {
                P.Now=Math.Max(P.Now,O.Now)+.02;
                Current=Task(Experiment);revision++;
                var sent=new ViveSimulationTrialTask(Current,Epoch,Mapping,P.ClockId,revision,packet++,P.Now);
                Receiver.RecordTaskSocketAccepted(sent,true);O.Now=P.Now+.02;
                bool usable=Current.Phase==Phase.MainTask&&Current.Condition!=Condition.D;
                Check.Equal(usable,Sender.ObserveTask(ViveSimulationTrialPoseCodec.EncodeTask(sent),true,O.Now));
            }
            public byte[] Produce(){O.Now=Math.Max(O.Now,P.Now)+.02;byte[] bytes;Check.True(Sender.TryCreatePose(O.Now,O.Now,Rotation.Identity,new Vec3(0,0,1),out bytes),Sender.LastIssue);return bytes;}
            public bool Receive(byte[] bytes){P.Now=Math.Max(P.Now,O.Now)+.02;return Receiver.Observe(bytes,true,P.Now,Current,revision);}
            public bool Render()
            {
                ViveSimulationTrialPoseFrame pose;double receipt;
                var frame=Receiver.TryGetCurrent(Current,revision,out pose,out receipt)?new ViveLivePresentationFrame(Session,Current.BlockIndex,Current.Segment,Current.TrialId,Current.Condition.Value,pose.Sequence,receipt,Mapping,pose.Head,pose.Eye,true,true):null;
                return View.Present(Current,Experiment.LockedControl,frame);
            }
            public void Fresh(bool expected=true){Check.True(Receive(Produce()),Receiver.LastIssue);Check.Equal(expected,Render());}
            public void Hidden(){Check.True(!Render());Check.True(Sink.Current==null);}
        }
        public static void Register(TestSuite suite)
        {
            suite.Run("offline real participant source and challenge proof remain blocked before Live countdown",()=>{
                var pClock=new Clock("offline-participant",3);var oClock=new Clock("offline-operator",3);var p=Participant(pClock);Practice(p);
                var sink=new Sink();var view=new ViveLivePresentationAdapter(pClock,Session,Mapping,.5,sink,true);var host=p.CreateSyntheticWozAuthority(view.Clear);
                p.LockSyntheticWozForTest(Actor.Owner,Owner);Check.True(p.ConfirmSyntheticWozForTest(Actor.Owner));var task=host.ReadAuthoritativeTask();
                var context=new ViveWozSourceContext(Session,Owner,Stream,Mapping,Epoch,"source-choice",pClock.ClockId,oClock.ClockId,0,0,Condition.B);
                var owner=new ViveWozSourceOwner(context,pClock,host);var remote=new ViveWozSourceReceiver(context,oClock,Map(oClock,pClock),host,.5,.1,true);
                pClock.Now=3.01;var confirmation=owner.Confirm(true);oClock.Now=3.02;Check.True(remote.Observe(confirmation,true),remote.LastIssue);
                var pa=new IPEndPoint(IPAddress.Loopback,45101);var oa=new IPEndPoint(IPAddress.Loopback,45102);
                var pc=new VivePreflightEndpointConfiguration(PeerRole.Participant,pa,oa,false,Session,Stream,oClock.ClockId,pClock.ClockId,Mapping,Epoch,"vive-pro-eye-no-serial",.5);
                var oc=new VivePreflightEndpointConfiguration(PeerRole.Operator,oa,pa,false,Session,Stream,oClock.ClockId,pClock.ClockId,Mapping,Epoch,"vive-pro-eye-no-serial",.5);
                var participant=new VivePreflightParticipant(pc,pClock,Map(pClock,oClock));var op=new VivePreflightOperator(oc,oClock,Map(oClock,pClock));
                participant.ConfirmSource(true);op.ConfirmSource(true);var port=new PreflightPort(participant);var coordinator=new ViveDevelopmentPreflightCoordinator(Session,host,new App(),port);
                coordinator.Tick();var ot=new VivePreflightOperatorTask(task,true,true,0,oClock.Now);Check.True(op.ObserveTask(ot));
                oClock.Now=3.05;Check.True(op.AcceptChallenge(port.Challenge,pa,ot),op.LastIssue);oClock.Now=3.06;
                var sensor=new Eye{Value=new ViveProEyePoll(new ViveHeadFrame(1,oClock.Now,true,true,Rotation.Identity),new ViveEyeFrame(1,8,oClock.Now,false,null,null,new ViveEyeRay(2,false,true,new Vec3(0,0,0),new Vec3(.5,0,1))),ViveEyeRuntimeState.Working,ViveEyeCalibrationState.ReportedReady,true,0)};
                var source=new ViveProEyeHeadEyeSource(Session,0,0,Stream,oClock,sensor,new ViveProEyeRehearsalCoordinates(),.5);HeadEyeSample sample;Check.True(source.TryRead(out sample));
                oClock.Now=3.07;var proof=op.CreateProof(source.LastObservation,true,ot);Check.True(proof!=null,op.LastIssue);
                pClock.Now=3.10;Check.True(participant.AcceptProof(proof,oa,task,true,true),participant.LastIssue);coordinator.Tick();
                Check.True(coordinator.CandidateObserved&&!coordinator.CanEnterCountdown&&!coordinator.CryptographicallyAuthenticated&&!remote.CanEnterCountdown);
                sink.OnClear=()=>{owner.Revoke(true);remote.SignalLoss("offline_callback_revoke");};coordinator.SignalRemoteLoss();
                Check.True(owner.Terminal&&!owner.LocallyConfirmed&&remote.MustClearLivePose&&remote.MustPauseLiveTask);Check.True(sink.Current==null);
                pClock.Now+=10;p.Tick();Check.Equal(Phase.BlockReady,p.Phase);Check.True(coordinator.RecoveryDecisionRequired);
                Check.Throws<InvalidOperationException>(()=>p.OwnerBeginBlock(Actor.Owner));Check.True(p.ObservationFaulted&&host.ReadCurrent()==null);
                Console.WriteLine("BOUNDARY: real participant rejects Live countdown; downstream tests use separate public Core simulation only.");
            });
            suite.Run("offline core source loss pauses countdown before task tick",()=>{
                var c=new Clock("offline-participant",3);var e=Engine(c);var sink=new Sink();var view=new ViveLivePresentationAdapter(c,Session,Mapping,.5,sink,true);
                var host=new VivePreflightExperimentHost(e,view.Clear,true);var coordinator=new ViveDevelopmentPreflightCoordinator(Session,host,new App(),new ClosedPreflight());
                e.BeginCountdown(Actor.Owner,true);coordinator.Tick();Check.Equal(Phase.Paused,e.Phase);Check.True(coordinator.PauseVerified);
                c.Now+=30;e.Tick();Check.Equal(Phase.Paused,e.Phase);Check.True(e.CurrentTrial==null&&sink.Current==null&&!coordinator.CanEnterCountdown);
            });
            suite.Run("offline trial bytes preserve receipt and duplicate or reordered pose clears sink",()=>{
                var f=new PoseLoop();var first=f.Produce();Check.True(f.Receive(first));Check.True(f.Render());
                ViveSimulationTrialPoseFrame pose;double receipt;Check.True(f.Receiver.TryGetCurrent(f.Current,f.Revision,out pose,out receipt));double original=receipt;
                f.P.Now+=.01;Check.True(f.Receiver.TryGetCurrent(f.Current,f.Revision,out pose,out receipt));Check.Near(original,receipt);
                Check.True(!f.Receive(first));f.Hidden();var second=f.Produce();var third=f.Produce();Check.True(f.Receive(third));Check.True(f.Render());Check.True(!f.Receive(second));f.Hidden();
                Check.True(!f.Receiver.CryptographicallyAuthenticated&&!f.Receiver.CanRecordOrExport&&!f.View.CanEnterLiveCountdown);
            });
            suite.Run("offline stale accepted pose expires without refreshed receipt",()=>{var f=new PoseLoop();f.Fresh();f.P.Now+=.6;f.Hidden();});
            suite.Run("offline actual next trial rejects delayed previous pose",()=>{
                var f=new PoseLoop();f.Fresh();var delayed=f.Produce();string previous=f.Current.TrialId;Answer(f.Experiment);f.Publish();
                Check.True(f.Current.TrialId!=previous);Check.True(!f.Receive(delayed));f.Hidden();f.Fresh();
            });
            suite.Run("offline actual interrupted trial changes segment and rejects old pose",()=>{
                var f=new PoseLoop();f.Fresh();var old=f.Produce();var interrupted=f.Experiment.CurrentTrial;f.Experiment.Pause(Actor.Owner);f.Publish();f.Hidden();
                Check.Equal(TrialOutcome.Interrupted,interrupted.Outcome);f.Experiment.Resume(true);f.P.Now+=3;f.Experiment.Tick();f.Publish();
                Check.Equal(1,f.Current.Segment);Check.True(f.Current.TrialId!=interrupted.TrialId);Check.True(!f.Receive(old));f.Hidden();f.Fresh();
            });
            suite.Run("offline four actual blocks clear questionnaire rest D and completion",()=>{
                var f=new PoseLoop();int hiddenD=0;
                for(int b=0;b<4;b++){
                    Check.Equal(b,f.Experiment.BlockIndex);
                    if(f.Current.Condition==Condition.D){byte[] bytes;f.O.Now+=.01;Check.True(!f.Sender.TryCreatePose(f.O.Now,f.O.Now,Rotation.Identity,new Vec3(0,0,1),out bytes));f.Hidden();hiddenD++;}
                    else f.Fresh();
                    for(int i=0;i<24;i++)Answer(f.Experiment);f.Publish();Check.Equal(Phase.Questionnaire,f.Current.Phase);f.Hidden();
                    Check.True(f.Experiment.AcknowledgeQuestionnaireInstruction());for(int i=0;i<5;i++)f.Experiment.Rate(3);
                    f.Publish();f.Hidden();if(b<3){Check.Equal(Phase.Rest,f.Current.Phase);Check.True(f.Experiment.Ready());f.Publish();f.Hidden();f.Begin();}
                }
                Check.Equal(1,hiddenD);Check.Equal(Phase.Ended,f.Experiment.Phase);Check.Equal(104,f.Experiment.Trials.Count);Check.Equal(20,f.Experiment.Ratings.Count);
            });
            suite.Run("offline failed sink clear latches without claiming physical hide",()=>{
                var f=new PoseLoop();f.Fresh();int before=f.Sink.Applies;f.Sink.FailClear=true;Check.True(!f.Render());Check.True(f.View.Faulted);
                Check.True(f.Sink.Current!=null,"A failing sink cannot prove disappearance");f.Sink.FailClear=false;f.Fresh(false);Check.Equal(before,f.Sink.Applies);Check.True(f.Sink.Current==null);
            });
            suite.Run("offline live projection cannot change real participant Automatic output",()=>{
                var clock=new Clock("offline-participant",3);var p=Participant(clock);Practice(p);p.OwnerLockSource(Actor.Owner,ControlSelection.Automatic,Owner);p.OwnerBeginBlock(Actor.Owner);clock.Now+=3;p.Tick();Neutral(p);p.MarkPresented(p.CurrentTrial.TrialId);
                var sink=new Sink();var view=new ViveLivePresentationAdapter(clock,Session,Mapping,.5,sink,true);var host=p.CreateSyntheticWozAuthority(view.Clear);var state=host.ReadCurrent();var before=p.CurrentAvatar;int count=p.AuthoritativeEventCount;
                var frame=new ViveLivePresentationFrame(Session,0,0,state.Task.TrialId,Condition.B,1,clock.Now,Mapping,Rotation.Identity,new Vec3(0,0,1),true,true);
                Check.True(!view.Present(state.Task,state.LockedSelection,frame));Check.True(sink.Current==null);var after=p.CurrentAvatar;
                Check.Equal(ControlSelection.Automatic,p.LockedControl.Value);Check.Equal(count,p.AuthoritativeEventCount);Check.Equal(before.ActualSource,after.ActualSource);
                Check.Near(before.ActualPose.Value.HeadRotation.W,after.ActualPose.Value.HeadRotation.W);Check.True(ViveWozPresentationPolicy.IsAutomaticOnly(after));
            });
        }
    }
}
