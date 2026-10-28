using System;
using System.Collections.Generic;
using System.Linq;
using System.Globalization;
using System.Net;
using AvatarStroop.Core;

namespace AvatarStroop.Runtime
{
    public static class SelectedSimulationSource
    {
        public static bool Matches(HeadEyeSample sample,string session,int block,int segment)
        {return sample!=null&&sample.Source==ControlSource.Simulator&&sample.Role==StreamRole.OperatorControl&&sample.StreamId=="simulator/operator"&&sample.SessionId==session&&sample.BlockIndex==block&&sample.Segment==segment;}
        public static bool Ready(HeadEyeSample sample,FaultSnapshot currentHealth,string session,int block,int segment)
        {return Matches(sample,session,block,segment)&&currentHealth!=null&&currentHealth.CaptureAgeVerified&&!currentHealth.RecoveryDecisionRequired&&currentHealth.HeadUsable&&currentHealth.EyeUsable;}
    }
    // These values are visible engineering fixtures; apparatus sessions are blocked by the core.
    public static class DevelopmentFixtures
    {
        public static DevelopmentFaultConfiguration Faults() {return new DevelopmentFaultConfiguration(.25,.25,.25);}
        public static DevelopmentClockConfiguration Clocks() {return new DevelopmentClockConfiguration(3,.2,.2,2,200);}
        public static AutomaticGazeGeometry Geometry()
        {return new AutomaticGazeGeometry(Rotation.Identity,new Vec3(0,0,1),new Rotation(0,Math.Sin(.3),0,Math.Cos(.3)),new Vec3(Math.Sin(.6),0,Math.Cos(.6)),"development-local-geometry-v1");}
    }

    public sealed class ClockAuditRecorder
    {
        long lastRevision=-1;
        public bool Record(ClockSynchronizer synchronizer,AuditLog audit,AuditContext context,double observedLocalSeconds)
        {
            var map=synchronizer.Snapshot();
            if(map.Revision==lastRevision)return false;
            lastRevision=map.Revision;
            string evidence=String.Format(CultureInfo.InvariantCulture,
                "four_timestamp_estimator_not_hardware_validated;revision={0};available={1};retained_samples={2};local_reference={3:R};remote_reference={4:R};rate_min={5};rate_max={6}",
                map.Revision,map.Available,map.SampleCount,map.LocalReferenceSeconds,map.RemoteReferenceSeconds,map.MinimumLocalSecondsPerRemoteSecond,map.MaximumLocalSecondsPerRemoteSecond);
            evidence+=";point_estimate="+map.PointEstimateMethod+";unavailable_reason="+map.UnavailableReason;
            audit.RecordClock(new ClockAuditRecord(context,map.RemoteClockId,map.LocalClockId,"development-affine-bounds-v2",
                map.Available?map.LocalReferenceSeconds:observedLocalSeconds,map.RemoteMinusLocalSeconds,map.RemoteDriftPartsPerMillion,map.UncertaintySeconds,evidence));
            return true;
        }
    }

    public sealed class PacketObservation
    {
        public WirePacket Packet {get;internal set;}
        public double ReceivedSeconds {get;internal set;}
        public bool Accepted {get;internal set;}
        public string Reason {get;internal set;}
        public long MissingBeforePacket {get;internal set;}
    }

    public sealed class ClockExchangeObservation
    {
        public WirePacket Reply {get;internal set;}
        public double ReceivedSeconds {get;internal set;}
        public bool Accepted {get;internal set;}
        public string Rejection {get;internal set;}
        public ClockMappingSnapshot Mapping {get;internal set;}
    }

    public abstract class SimulationPeer : IDisposable
    {
        readonly UdpPeer socket;
        long outgoing;
        double nextHello,nextSync;
        int exchange;
        readonly List<PacketObservation> packets=new List<PacketObservation>();
        readonly List<ClockExchangeObservation> clockExchanges=new List<ClockExchangeObservation>();
        public MonotonicClock Clock {get;private set;}
        public PeerIdentity Identity {get;private set;}
        public ReceiveMonitor Monitor {get;private set;}
        public ClockSynchronizer Synchronizer {get;private set;}
        public IReadOnlyList<PacketObservation> Packets {get{return packets.AsReadOnly();}}
        public IReadOnlyList<ClockExchangeObservation> ClockExchanges {get{return clockExchanges.AsReadOnly();}}
        protected SimulationPeer(string session,PeerRole role,int localPort,int remotePort)
        {
            string own=role==PeerRole.Participant?"participant":"operator",other=role==PeerRole.Participant?"operator":"participant";
            Clock=new MonotonicClock(own+"-monotonic");
            Identity=new PeerIdentity(session,own,role,"simulator/"+own,ControlSource.Simulator,Clock.ClockId);
            var expected=new PeerIdentity(session,other,role==PeerRole.Participant?PeerRole.Operator:PeerRole.Participant,"simulator/"+other,ControlSource.Simulator,other+"-monotonic");
            Monitor=new ReceiveMonitor(expected,DevelopmentFixtures.Faults(),"simulator-no-hardware",Clock.ClockId);
            Synchronizer=new ClockSynchronizer(Clock.ClockId,expected.ClockId,DevelopmentFixtures.Clocks());
            socket=new UdpPeer(new IPEndPoint(IPAddress.Loopback,localPort),new IPEndPoint(IPAddress.Loopback,remotePort),false);
        }
        protected virtual bool SuppressOutgoing {get{return false;}}
        protected virtual void AcceptedPacket(WirePacket packet,double received){}
        protected long NextPacketSequence() {return outgoing++;}
        protected void Send(WirePacket packet) {if(!SuppressOutgoing)socket.Send(packet);}
        protected void TickNetwork()
        {
            UdpReceiveResult received;
            while((received=socket.Poll())!=null)
            {
                double now=Clock.Now;
                if(!received.Accepted){Monitor.RecordTransportRejection(received.Error,now);packets.Add(new PacketObservation{ReceivedSeconds=now,Reason=received.Error});continue;}
                var decision=Monitor.Observe(received.Packet,now,Synchronizer);
                packets.Add(new PacketObservation{Packet=received.Packet,ReceivedSeconds=now,Accepted=decision.Accepted,Reason=decision.Reason,MissingBeforePacket=decision.MissingBeforePacket});
                if(!decision.Accepted)continue;
                if(received.Packet.Kind==WireKind.TimeRequest)
                    Send(WirePacket.TimeReply(Identity,NextPacketSequence(),Clock.Now,received.Packet.ExchangeId,received.Packet.RequestClockId,received.Packet.RequestSentSeconds,now));
                else if(received.Packet.Kind==WireKind.TimeReply)
                {
                    bool accepted=Synchronizer.ObserveReply(received.Packet,now);
                    clockExchanges.Add(new ClockExchangeObservation{Reply=received.Packet,ReceivedSeconds=now,Accepted=accepted,Rejection=Synchronizer.LastRejection,Mapping=Synchronizer.Snapshot()});
                    if(clockExchanges.Count>256)clockExchanges.RemoveAt(0);
                }
                AcceptedPacket(received.Packet,now);
            }
            if(!SuppressOutgoing && Clock.Now>=nextHello)
            {Send(WirePacket.Hello(Identity,NextPacketSequence(),Clock.Now,SessionConfiguration.ProtocolVersion));nextHello=Clock.Now+.1;}
            if(!SuppressOutgoing && Clock.Now>=nextSync)
            {
                var id=Identity.PeerId+"-exchange-"+(exchange++);double sent=Clock.Now;
                if(Synchronizer.RegisterRequest(id,sent))Send(WirePacket.TimeRequest(Identity,NextPacketSequence(),sent,id));
                nextSync=Clock.Now+.1;
            }
        }
        public void Dispose(){socket.Dispose();}
    }

    public sealed class OperatorSimulationApplication : SimulationPeer
    {
        DeterministicSimulatorSource source;
        int sourceBlock=-1,sourceSegment=-1;
        double nextSample;
        double? mainStarted;
        readonly bool injectFaults;
        readonly bool scriptedStop;
        double nextStopRetry;
        bool scriptedStopSent;
        public SafetyStopRequestState StopDelivery {get;private set;}=new SafetyStopRequestState();
        readonly Dictionary<string,string> observedTrials=new Dictionary<string,string>();
        readonly List<HeadEyeSample> acquired=new List<HeadEyeSample>();
        public TaskSnapshot CurrentTask {get;private set;}
        public int SamplesSent {get;private set;}
        public IReadOnlyDictionary<string,string> ObservedTrials {get{return observedTrials;}}
        public IReadOnlyList<HeadEyeSample> AcquiredSamples {get{return acquired.AsReadOnly();}}
        public OperatorSimulationApplication(string session,int localPort,int remotePort,bool injectFaults,bool scriptedStop=false)
            :base(session,PeerRole.Operator,localPort,remotePort){this.injectFaults=injectFaults;this.scriptedStop=scriptedStop;}
        public void RequestSafetyStop(){StopDelivery.Request();nextStopRetry=0;}
        protected override void AcceptedPacket(WirePacket packet,double received)
        {if(packet.Kind==WireKind.SafetyStopAck)StopDelivery.Acknowledge(packet.StopRequestId);}
        protected override bool SuppressOutgoing
        {get{return injectFaults && mainStarted.HasValue && Clock.Now-mainStarted.Value>=.9 && Clock.Now-mainStarted.Value<1.35;}}
        public void Tick()
        {
            TickNetwork();CurrentTask=Monitor.CurrentTask(Clock.Now,Synchronizer);
            if(scriptedStop&&!scriptedStopSent&&mainStarted.HasValue&&Clock.Now-mainStarted.Value>=.3)
            {scriptedStopSent=true;RequestSafetyStop();}
            if(StopDelivery.Pending&&Clock.Now>=nextStopRetry)
            {Send(WirePacket.SafetyStopRequest(Identity,NextPacketSequence(),Clock.Now,StopDelivery.RequestId));nextStopRetry=Clock.Now+.05;}
            if(CurrentTask==null)return;
            if(CurrentTask.Phase==Phase.MainTask && !mainStarted.HasValue)mainStarted=Clock.Now;
            if(CurrentTask.TrialId!=null && CurrentTask.Word.HasValue)
                observedTrials[CurrentTask.TrialId]=CurrentTask.Word+"/"+CurrentTask.Ink;
            if(CurrentTask.BlockIndex<0 || Clock.Now<nextSample || SuppressOutgoing)return;
            if(source==null || sourceBlock!=CurrentTask.BlockIndex || sourceSegment!=CurrentTask.Segment)
            {
                sourceBlock=CurrentTask.BlockIndex;sourceSegment=CurrentTask.Segment;
                source=new DeterministicSimulatorSource(Identity.SessionId,RunKind.Simulation,sourceBlock,sourceSegment,Identity.SourceId,Clock);
            }
            HeadEyeSample raw;source.TryRead(out raw);double elapsed=mainStarted.HasValue?Clock.Now-mainStarted.Value:-1;
            bool eye=!(injectFaults && elapsed>=.3 && elapsed<.45),head=!(injectFaults && elapsed>=.6 && elapsed<.75);
            var sample=new HeadEyeSample(raw.SessionId,raw.StreamId,raw.DeviceId,raw.ClockId,raw.BlockIndex,raw.Segment,raw.Sequence,
                raw.CaptureLocalSeconds,Clock.Now,raw.Source,raw.Role,raw.RawHeadRotation,raw.RawEyeDirection,head,eye);
            acquired.Add(sample);Send(WirePacket.Tracking(Identity,NextPacketSequence(),sample));SamplesSent++;nextSample=Clock.Now+.01;
        }
        public TaskView View()
        {
            var task=CurrentTask;
            return new TaskView{Phase=task==null?Phase.Preparation:task.Phase,BlockIndex=task==null?-1:task.BlockIndex,Segment=task==null?0:task.Segment,TrialId=task==null?null:task.TrialId,
                Stimulus=task!=null&&task.Word.HasValue?new Stimulus(task.Word.Value,task.Ink.Value):null,
                ParticipantAvatarVisible=task!=null&&task.Phase==Phase.MainTask,
                OperatorViewProvisional=task==null||task.Phase!=Phase.MainTask||task.Condition==Condition.D,
                SourceLabel="SIMULATOR REHEARSAL — NOT LIVE WOZ"};
        }
    }

    public sealed class ParticipantSimulationApplication : SimulationPeer
    {
        AvatarController avatar;
        int avatarBlock=-1,avatarSegment=-1;
        double nextSnapshot,nextDriver;
        string previousSnapshotKey;
        readonly List<AvatarOutput> outputs=new List<AvatarOutput>();
        readonly List<string> applicationWarnings=new List<string>();
        readonly HashSet<string> trialFaults=new HashSet<string>();
        readonly SafetyStopInbox stopInbox=new SafetyStopInbox();
        readonly AutomaticGazeGeometry geometry;
        int packetAuditCursor,faultAuditCursor;
        readonly ClockAuditRecorder clockAudit=new ClockAuditRecorder();
        public int AppliedRemoteStops {get{return stopInbox.AppliedCount;}}
        public AuditLog Audit {get;private set;}
        public Experiment Experiment {get;private set;}
        public AvatarOutput CurrentAvatar {get;private set;}
        public FaultSnapshot CurrentFaults {get;private set;}
        public IReadOnlyList<AvatarOutput> Outputs {get{return outputs.AsReadOnly();}}
        public IReadOnlyList<string> ApplicationWarnings {get{return applicationWarnings.AsReadOnly();}}
        public ParticipantSimulationApplication(string session,int localPort,int remotePort,AutomaticGazeGeometry geometry=null)
            :base(session,PeerRole.Participant,localPort,remotePort)
        {
            this.geometry=geometry??DevelopmentFixtures.Geometry();
            // Deliberately named integration fixture, not a participant allocation schedule.
            var allocation=new Allocation("simulation-integration-fixture-BCAD",new[]{Condition.B,Condition.C,Condition.A,Condition.D},GazeTarget.Participant,new[]{0,1,2,3});
            Experiment=new Experiment(new SessionConfiguration(session,20261028,allocation,RunKind.Simulation,"en",1),Clock);
            Audit=new AuditLog(session,RunKind.Simulation);
            Experiment.RecordPreparation(Actor.Owner,"consent_screening_calibration","not_applicable_synthetic_software_rehearsal");
        }
        protected override void AcceptedPacket(WirePacket packet,double received)
        {
            if(packet.Kind!=WireKind.SafetyStopRequest)return;
            stopInbox.ApplyOnce(packet.StopRequestId,()=>Experiment.Pause(Actor.Operator));
            Send(WirePacket.SafetyStopAck(Identity,NextPacketSequence(),Clock.Now,packet.StopRequestId));
        }
        public void Tick()
        {
            TickNetwork();
            if(Experiment.Phase!=Phase.Countdown||!Experiment.LockedControl.HasValue||SourceReady(Experiment.LockedControl.Value))Experiment.Tick();
            RefreshPresentation();BroadcastTask();RecordNetworkAudit();
        }
        // Call again after local input so frame state cannot refer to the prior phase/block.
        public void RefreshPresentation()
        {
            CurrentFaults=Monitor.Snapshot(Clock.Now,Synchronizer);applicationWarnings.Clear();
            if(!Experiment.LockedControl.HasValue){CurrentAvatar=null;return;}
            if(Experiment.LockedControl.HasValue && Experiment.BlockIndex>=0)
            {
                if(avatar==null||avatarBlock!=Experiment.BlockIndex||avatarSegment!=Experiment.Segment)
                {
                    avatarBlock=Experiment.BlockIndex;avatarSegment=Experiment.Segment;
                    avatar=new AvatarController(Identity.SessionId,RunKind.Simulation,avatarBlock,avatarSegment,
                        Experiment.LockedControl.Value,Experiment.CurrentCondition.Value,Experiment.Configuration.Allocation.BInitialTarget,
                        geometry,"simulator/operator");
                }
                HeadEyeSample sample=Experiment.LockedControl==ControlSelection.Automatic?null:Monitor.CurrentSample(Clock.Now,Synchronizer);
                if(!SelectedSimulationSource.Matches(sample,Identity.SessionId,avatarBlock,avatarSegment))sample=null;
                if(Experiment.LockedControl!=ControlSelection.Automatic&&sample==null)
                    applicationWarnings.Add("No current selected-source sample for this block and segment; source reacquisition is required.");
                CurrentAvatar=Experiment.LockedControl==ControlSelection.Automatic
                    ?avatar.Render(Experiment.Phase,Experiment.ActiveSeconds)
                    :avatar.RenderValidated(Experiment.Phase,Experiment.ActiveSeconds,sample,sample!=null&&CurrentFaults.HeadUsable,sample!=null&&CurrentFaults.EyeUsable);
                outputs.Add(CurrentAvatar);
                if(Experiment.Phase==Phase.MainTask && Experiment.LockedControl!=ControlSelection.Automatic)
                {
                    foreach(var warning in CurrentFaults.Warnings)FlagTrialOnce(warning.Kind.ToString());
                    if(sample==null)FlagTrialOnce("selected_sample_unavailable_for_context");
                }
            }
        }
        void BroadcastTask()
        {
            var taskView=Experiment.View(Actor.Operator);
            string snapshotKey=Experiment.Phase+"/"+taskView.TrialId+"/"+Experiment.Segment;
            if(Clock.Now>=nextSnapshot||snapshotKey!=previousSnapshotKey)
            {
                var view=taskView;var stimulus=view.Stimulus;
                Send(WirePacket.Task(Identity,NextPacketSequence(),Clock.Now,new TaskSnapshot(Identity.SessionId,Experiment.BlockIndex,Experiment.Segment,
                    view.TrialId,stimulus==null?(Colour?)null:stimulus.Word,stimulus==null?(Colour?)null:stimulus.Ink,Experiment.Phase,Experiment.CurrentCondition)));
                nextSnapshot=Clock.Now+.01;previousSnapshotKey=snapshotKey;
            }
        }
        public AuditContext CurrentAuditContext()
        {return new AuditContext(Experiment.BlockIndex,Experiment.Segment,Experiment.CurrentTrial==null?null:Experiment.CurrentTrial.TrialId);}
        void RecordNetworkAudit()
        {
            for(;packetAuditCursor<Packets.Count;packetAuditCursor++)
            {
                var item=Packets[packetAuditCursor];var packet=item.Packet;var sample=packet==null?null:packet.Sample;
                var context=sample==null?CurrentAuditContext():new AuditContext(sample.BlockIndex,sample.Segment,
                    sample.BlockIndex==Experiment.BlockIndex&&sample.Segment==Experiment.Segment&&Experiment.CurrentTrial!=null?Experiment.CurrentTrial.TrialId:null);
                Audit.RecordTransport(new TransportAuditRecord(context,ControlSource.Simulator,"simulator/operator",packet==null?(long?)null:packet.Sequence,
                    Clock.ClockId,item.ReceivedSeconds,item.Accepted?"packet_accepted":"packet_rejected",item.MissingBeforePacket,null,null,null,
                    (packet==null?"decode":packet.Kind.ToString())+":"+item.Reason,sample==null?(long?)null:sample.Sequence));
                if(item.Accepted&&sample!=null)Audit.RecordOperatorSample(sample,context.TrialId);
            }
            for(;faultAuditCursor<Monitor.Events.Count;faultAuditCursor++)
            {
                var fault=Monitor.Events[faultAuditCursor];
                Audit.RecordTransport(new TransportAuditRecord(CurrentAuditContext(),ControlSource.Simulator,fault.SourceId,null,Clock.ClockId,fault.LocalSeconds,
                    fault.Kind,null,null,null,null,fault.Detail));
            }
            clockAudit.Record(Synchronizer,Audit,CurrentAuditContext(),Clock.Now);
        }
        public void RecordControllerEvaluation()
        {
            if(CurrentAvatar!=null)Audit.RecordPresentation(new AvatarPresentationRecord(CurrentAuditContext(),Clock.ClockId,Clock.Now,
                "headless_controller_evaluation_not_rendered",Experiment.Phase,CurrentAvatar));
        }
        public ExportResult Export(string parentDirectory,ExportProvenance provenance)
        {RecordNetworkAudit();return AuditExporter.Export(Experiment,Audit,parentDirectory,provenance);}
        public bool SourceReady(ControlSelection source)
        {
            if(source==ControlSelection.Automatic)return true;
            if(source!=ControlSelection.SimulatorRehearsal)return false;
            var sample=Monitor.CurrentSample(Clock.Now,Synchronizer);var health=Monitor.Snapshot(Clock.Now,Synchronizer);
            return SelectedSimulationSource.Ready(sample,health,Identity.SessionId,Experiment.BlockIndex,Experiment.Segment);
        }
        void FlagTrialOnce(string code)
        {
            string key=(Experiment.CurrentTrial==null?"none":Experiment.CurrentTrial.TrialId)+"/"+code;
            if(trialFaults.Add(key))Experiment.FlagTechnicalIssue(code);
        }
        public void OwnerBeginPractice()
        {if(Experiment.Phase==Phase.Preparation)Experiment.BeginFamiliarization(Actor.Owner);Experiment.BeginPractice(Actor.Owner);}
        public void OwnerLockNextBlock(ControlSelection source)
        {Experiment.LockBlock(Actor.Owner,source,"development-owner");}
        public void OwnerBeginBlock()
        {Experiment.BeginCountdown(Actor.Owner,Experiment.LockedControl.HasValue&&SourceReady(Experiment.LockedControl.Value));}

        // Opt-in automated test driver. No GUI input path calls this method.
        public void DriveHeadlessSimulation()
        {
            if((Experiment.Phase==Phase.Practice||Experiment.Phase==Phase.MainTask)&&!Experiment.CurrentTrial.Onset.HasValue)
            {
                // The synthetic response comes after this trial's own acknowledgement,
                // including the first trial; never answer it invisibly in the creation tick.
                Experiment.MarkPresented(Experiment.CurrentTrial.TrialId);
                nextDriver=Clock.Now+(Experiment.Phase==Phase.MainTask?.12:.04);return;
            }
            if(Clock.Now<nextDriver)return;
            switch(Experiment.Phase)
            {
                case Phase.Preparation:OwnerBeginPractice();break;
                case Phase.Practice:
                case Phase.MainTask:
                    var trial=Experiment.CurrentTrial;
                    // This is a headless software acknowledgement, not a display onset.
                    var answer=trial.Stimulus.Ink;Experiment.ButtonChanged(answer,true);Experiment.ButtonChanged(answer,false);break;
                case Phase.PracticeCorrection:Experiment.ContinuePractice();break;
                case Phase.BlockPreparation:OwnerLockNextBlock(ControlSelection.SimulatorRehearsal);break;
                case Phase.BlockReady:if(SourceReady(ControlSelection.SimulatorRehearsal))OwnerBeginBlock();break;
                case Phase.Questionnaire:Experiment.Rate(3);break;
            }
            nextDriver=Clock.Now+(Experiment.Phase==Phase.MainTask?.12:.04);
        }
    }
}
