using System;
using System.Collections.Generic;
using System.Linq;
using System.Globalization;
using System.IO;
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
        bool connectionFaultRecorded;
        bool networkStarted;
        double nextRecordingFlush,recordingFlushInterval;
        long recordingFaultSequence=-1,recordedDiscardedPackets;
        long recordingClockRevision=-1;
        protected SimulationJournalCapture RecordingCapture {get;private set;}
        public ContinuousJournal Recording {get;private set;}
        public string RecordingWarning {get{return Recording!=null&&Recording.IsFaulted?
            "RECORDING FAILED: "+Recording.Error+"; last acknowledged sequence="+Recording.LastFlushedSequence.ToString(CultureInfo.InvariantCulture)+
            ", bytes="+Recording.LastFlushedOffset.ToString(CultureInfo.InvariantCulture):null;}}
        // Diagnostic packet history is a bounded safety valve, not a sampling policy:
        // the participant audits every observation each tick, so trimming only occurs
        // if auditing stops consuming. Received/accepted totals stay exact in the
        // counters below, so a bounded list can never change a reported count.
        public const int MaximumRetainedPackets=8192;
        readonly List<PacketObservation> packets=new List<PacketObservation>();
        readonly List<ClockExchangeObservation> clockExchanges=new List<ClockExchangeObservation>();
        public MonotonicClock Clock {get;private set;}
        public PeerIdentity Identity {get;private set;}
        public ReceiveMonitor Monitor {get;private set;}
        public ClockSynchronizer Synchronizer {get;private set;}
        public IReadOnlyList<PacketObservation> Packets {get{return packets.AsReadOnly();}}
        public IReadOnlyList<ClockExchangeObservation> ClockExchanges {get{return clockExchanges.AsReadOnly();}}
        public long DiscardedPacketObservations {get;private set;}
        public long AcceptedPacketObservations {get;private set;}
        public long AcceptedHeadEyePackets {get;private set;}
        // Lets the participant keep its audit cursor aligned when the list is trimmed.
        protected virtual void PacketsDiscarded(int removed){}
        protected SimulationPeer(string session,PeerRole role,int localPort,int remotePort,UdpPeer transport=null)
        {
            string own=role==PeerRole.Participant?"participant":"operator",other=role==PeerRole.Participant?"operator":"participant";
            Clock=new MonotonicClock(own+"-monotonic");
            Identity=new PeerIdentity(session,own,role,"simulator/"+own,ControlSource.Simulator,Clock.ClockId);
            var expected=new PeerIdentity(session,other,role==PeerRole.Participant?PeerRole.Operator:PeerRole.Participant,"simulator/"+other,ControlSource.Simulator,other+"-monotonic");
            Monitor=new ReceiveMonitor(expected,DevelopmentFixtures.Faults(),"simulator-no-hardware",Clock.ClockId);
            Synchronizer=new ClockSynchronizer(Clock.ClockId,expected.ClockId,DevelopmentFixtures.Clocks());
            socket=transport??new UdpPeer(new IPEndPoint(IPAddress.Loopback,localPort),new IPEndPoint(IPAddress.Loopback,remotePort),false);
        }
        protected virtual bool SuppressOutgoing {get{return false;}}
        protected virtual SimulationJournalCapture CreateRecordingCapture(ContinuousJournal journal){return new SimulationJournalCapture(journal);}
        public void EnableRecording(string path,ExportProvenance provenance,double flushIntervalSeconds)
        {
            if(Recording!=null||networkStarted)throw new InvalidOperationException("Enable recording once before the first network tick");
            if(String.IsNullOrWhiteSpace(path)||!Path.IsPathRooted(path))throw new ArgumentException("An explicit absolute journal path is required");
            if(Double.IsNaN(flushIntervalSeconds)||Double.IsInfinity(flushIntervalSeconds)||flushIntervalSeconds<=0)throw new ArgumentOutOfRangeException("flushIntervalSeconds");
            string role=Identity.Role==PeerRole.Participant?"participant":"operator";
            EnableRecording(ContinuousJournal.CreateNew(path,new JournalIdentity(Identity.SessionId,role,Clock.ClockId,provenance)),flushIntervalSeconds);
        }
        internal void EnableRecording(ContinuousJournal journal,double flushIntervalSeconds)
        {
            if(Recording!=null||networkStarted)throw new InvalidOperationException("Enable recording once before the first network tick");
            if(Double.IsNaN(flushIntervalSeconds)||Double.IsInfinity(flushIntervalSeconds)||flushIntervalSeconds<=0)throw new ArgumentOutOfRangeException("flushIntervalSeconds");
            string role=Identity.Role==PeerRole.Participant?"participant":"operator";
            if(journal==null||journal.Identity.SessionId!=Identity.SessionId||journal.Identity.Role!=role||journal.Identity.ClockId!=Clock.ClockId)throw new ArgumentException("Recorder identity must match this application");
            Recording=journal;
            RecordingCapture=CreateRecordingCapture(Recording);recordingFlushInterval=flushIntervalSeconds;
            if(!Recording.IsFaulted)Recording.TryAppend("recording_configuration",SimulationJournalCapture.Fields("started_local_seconds",Clock.Now,
                "flush_interval_seconds",flushIntervalSeconds,"flush_policy","development_owner_thread_periodic;loop_stalls_can_delay_flush",
                "durability_scope","successful_FileStream_Flush_true_return;physical_power_loss_not_verified","recovery_policy","offline_evidence_only_no_task_resume"));
            CaptureRecording(true);
        }
        // allowPeriodicFlush=false lets a camera render callback append evidence without
        // performing a durable FileStream.Flush(true) on the render thread; the normal
        // update loop still performs the periodic flush.
        public bool CaptureRecording(bool flush=false,bool allowPeriodicFlush=true)
        {
            if(Recording==null)return true;
            if(!RecordingCapture.Capture())return false;
            foreach(var e in Monitor.Events)
            {
                // Diagnostic fault history is bounded, so the cursor is the record's
                // monotonic sequence, never a list index that trimming would shift.
                if(e.Sequence<=recordingFaultSequence)continue;
                if(!Recording.TryAppend("peer_fault",SimulationJournalCapture.Fields("fault_sequence",e.Sequence,"kind",e.Kind,"source_id",e.SourceId,
                    "local_seconds",e.LocalSeconds,"clock_id",Clock.ClockId,"detail",e.Detail,
                    "discarded_fault_events",Monitor.DiscardedEvents)))return false;
                recordingFaultSequence=e.Sequence;
            }
            if(DiscardedPacketObservations!=recordedDiscardedPackets)
            {
                if(!Recording.TryAppend("packet_retention",SimulationJournalCapture.Fields("discarded_packet_observations",DiscardedPacketObservations,
                    "retained_packet_observations",packets.Count,"limit",MaximumRetainedPackets,
                    "policy","bounded_diagnostic_history;totals_are_exact_counters")))return false;
                recordedDiscardedPackets=DiscardedPacketObservations;
            }
            if(Identity.Role==PeerRole.Operator)
            {
                var map=Synchronizer.Snapshot();
                if(map.Revision!=recordingClockRevision)
                {
                    if(!Recording.TryAppend("peer_clock_snapshot",SimulationJournalCapture.Fields("revision",map.Revision,"available",map.Available,"unavailable_reason",map.UnavailableReason,
                        "local_clock_id",map.LocalClockId,"remote_clock_id",map.RemoteClockId,"local_reference_seconds",map.LocalReferenceSeconds,"remote_reference_seconds",map.RemoteReferenceSeconds,
                        "offset_seconds",map.RemoteMinusLocalSeconds,"drift_parts_per_million",map.RemoteDriftPartsPerMillion,"uncertainty_seconds",map.UncertaintySeconds,
                        "minimum_rate",map.MinimumLocalSecondsPerRemoteSecond,"maximum_rate",map.MaximumLocalSecondsPerRemoteSecond,"point_estimate_method",map.PointEstimateMethod)))return false;
                    recordingClockRevision=map.Revision;
                }
            }
            if(flush||(allowPeriodicFlush&&Clock.Now>=nextRecordingFlush))
            {if(!Recording.Flush())return false;nextRecordingFlush=Clock.Now+recordingFlushInterval;}
            return !Recording.IsFaulted;
        }
        public bool CloseRecording()
        {return Recording==null||CaptureRecording()&&Recording.Close();}
        protected virtual void AcceptedPacket(WirePacket packet,double received){}
        protected virtual void ConnectionFaulted(){}
        protected long NextPacketSequence() {return outgoing++;}
        void RecordConnectionFault(UdpConnectionFault fault)
        {
            if(connectionFaultRecorded)return;
            connectionFaultRecorded=true;Monitor.RecordConnectionFault(fault.Reason,Clock.Now);ConnectionFaulted();
        }
        protected UdpSendResult Send(WirePacket packet)
        {
            if(connectionFaultRecorded)return new UdpSendResult{ConnectionFault=socket.ConnectionFault};
            // No socket attempt: the simulation fault script may deliberately suppress traffic.
            if(SuppressOutgoing)return new UdpSendResult();
            var result=socket.Send(packet);
            if(result.ConnectionFault!=null)RecordConnectionFault(result.ConnectionFault);
            return result;
        }
        protected void TickNetwork()
        {
            networkStarted=true;
            try { TickNetworkCore(); }
            finally { RetainRecentPackets(); }
        }
        void RetainRecentPackets()
        {
            if(packets.Count<=MaximumRetainedPackets)return;
            int removed=packets.Count-MaximumRetainedPackets;
            packets.RemoveRange(0,removed);
            DiscardedPacketObservations+=removed;
            PacketsDiscarded(removed);
        }
        void TickNetworkCore()
        {
            if(connectionFaultRecorded)return;
            UdpReceiveResult received;
            while((received=socket.Poll())!=null)
            {
                double now=Clock.Now;
                if(received.ConnectionFault!=null){RecordConnectionFault(received.ConnectionFault);return;}
                if(!received.Accepted){Monitor.RecordTransportRejection(received.Error,now);packets.Add(new PacketObservation{ReceivedSeconds=now,Reason=received.Error});continue;}
                var decision=Monitor.Observe(received.Packet,now,Synchronizer);
                packets.Add(new PacketObservation{Packet=received.Packet,ReceivedSeconds=now,Accepted=decision.Accepted,Reason=decision.Reason,MissingBeforePacket=decision.MissingBeforePacket});
                if(!decision.Accepted)continue;
                AcceptedPacketObservations++;
                if(received.Packet.Kind==WireKind.HeadEye)AcceptedHeadEyePackets++;
                if(received.Packet.Kind==WireKind.TimeRequest)
                    Send(WirePacket.TimeReply(Identity,NextPacketSequence(),Clock.Now,received.Packet.ExchangeId,received.Packet.RequestClockId,received.Packet.RequestSentSeconds,now));
                else if(received.Packet.Kind==WireKind.TimeReply)
                {
                    bool accepted=Synchronizer.ObserveReply(received.Packet,now);
                    clockExchanges.Add(new ClockExchangeObservation{Reply=received.Packet,ReceivedSeconds=now,Accepted=accepted,Rejection=Synchronizer.LastRejection,Mapping=Synchronizer.Snapshot()});
                    if(Recording!=null&&!Recording.IsFaulted)
                    {
                        var reply=received.Packet;var map=Synchronizer.Snapshot();
                        Recording.TryAppend("clock_exchange",SimulationJournalCapture.Fields("exchange_id",reply.ExchangeId,"local_clock_id",reply.RequestClockId,"remote_clock_id",reply.Peer.ClockId,
                            "t0_local_sent",reply.RequestSentSeconds,"t1_remote_received",reply.RemoteReceivedSeconds,"t2_remote_sent",reply.RemoteSentSeconds,"t3_local_received",now,
                            "accepted",accepted,"rejection",Synchronizer.LastRejection,"mapping_revision",map.Revision,"mapping_available",map.Available,"mapping_unavailable_reason",map.UnavailableReason));
                    }
                    if(clockExchanges.Count>256)clockExchanges.RemoveAt(0);
                }
                if(connectionFaultRecorded)return;
                AcceptedPacket(received.Packet,now);
                if(connectionFaultRecorded)return;
            }
            if(!SuppressOutgoing && Clock.Now>=nextHello)
            {Send(WirePacket.Hello(Identity,NextPacketSequence(),Clock.Now,SessionConfiguration.ProtocolVersion));nextHello=Clock.Now+.1;}
            if(connectionFaultRecorded)return;
            if(!SuppressOutgoing && Clock.Now>=nextSync)
            {
                var id=Identity.PeerId+"-exchange-"+(exchange++);double sent=Clock.Now;
                if(Synchronizer.RegisterRequest(id,sent))Send(WirePacket.TimeRequest(Identity,NextPacketSequence(),sent,id));
                nextSync=Clock.Now+.1;
            }
        }
        public void Dispose(){try{if(Recording!=null){CloseRecording();Recording.Dispose();}}finally{socket.Dispose();}}
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
        string recordedTaskKey;
        public SafetyStopRequestState StopDelivery {get;private set;}=new SafetyStopRequestState();
        readonly Dictionary<string,string> observedTrials=new Dictionary<string,string>();
        readonly List<HeadEyeSample> acquired=new List<HeadEyeSample>();
        public TaskSnapshot CurrentTask {get;private set;}
        public int SamplesSent {get;private set;}
        public int SamplesSendFailed {get;private set;}
        public int SamplesSuppressed {get;private set;}
        public IReadOnlyDictionary<string,string> ObservedTrials {get{return observedTrials;}}
        public IReadOnlyList<HeadEyeSample> AcquiredSamples {get{return acquired.AsReadOnly();}}
        public OperatorSimulationApplication(string session,int localPort,int remotePort,bool injectFaults,bool scriptedStop=false)
            :this(session,localPort,remotePort,injectFaults,scriptedStop,null){}
        internal OperatorSimulationApplication(string session,int localPort,int remotePort,bool injectFaults,bool scriptedStop,UdpPeer transport)
            :base(session,PeerRole.Operator,localPort,remotePort,transport){this.injectFaults=injectFaults;this.scriptedStop=scriptedStop;}
        protected override void ConnectionFaulted(){CurrentTask=null;RecordCurrentTask();}
        public void RequestSafetyStop(){StopDelivery.Request();nextStopRetry=0;}
        protected override void AcceptedPacket(WirePacket packet,double received)
        {if(packet.Kind==WireKind.SafetyStopAck)StopDelivery.Acknowledge(packet.StopRequestId);}
        protected override bool SuppressOutgoing
        {get{return injectFaults && mainStarted.HasValue && Clock.Now-mainStarted.Value>=.9 && Clock.Now-mainStarted.Value<1.35;}}
        public void Tick()
        {try{TickCore();}finally{CaptureRecording();}}
        void TickCore()
        {
            TickNetwork();CurrentTask=Monitor.CurrentTask(Clock.Now,Synchronizer);
            RecordCurrentTask();
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
            acquired.Add(sample);
            if(RecordingCapture!=null)RecordingCapture.RecordAcquisition(sample);
            double sendMethodEntered=Clock.Now;
            var transmission=Send(WirePacket.Tracking(Identity,NextPacketSequence(),sample));
            double sendMethodReturned=Clock.Now;
            if(transmission.Sent)SamplesSent++;
            else if(transmission.ConnectionFault!=null)SamplesSendFailed++;
            else SamplesSuppressed++;
            if(Recording!=null&&!Recording.IsFaulted)Recording.TryAppend("operator_sample_send_result",SimulationJournalCapture.Fields("stream_id",sample.StreamId,"block_index",sample.BlockIndex,
                "segment",sample.Segment,"sample_sequence",sample.Sequence,"socket_accepted",transmission.Sent,"connection_fault",transmission.ConnectionFault!=null,
                "suppressed",!transmission.Sent&&transmission.ConnectionFault==null,"delivery_acknowledged",false,
                "clock_id",Clock.ClockId,"send_method_entered_seconds",sendMethodEntered,"send_method_returned_seconds",sendMethodReturned,
                "timestamp_semantics","application_Send_method_boundaries;not_network_emission_or_delivery"));
            nextSample=Clock.Now+.01;
        }
        void RecordCurrentTask()
        {
            if(Recording==null||Recording.IsFaulted)return;
            var task=CurrentTask;
            string key=task==null?"unavailable":task.BlockIndex+"/"+task.Segment+"/"+task.Phase+"/"+task.TrialId+"/"+task.Word+"/"+task.Ink;
            if(key==recordedTaskKey)return;
            if(Recording.TryAppend("operator_task_observation",SimulationJournalCapture.Fields("available",task!=null,"observed_local_seconds",Clock.Now,
                "phase",task==null?null:task.Phase.ToString(),"block_index",task==null?(int?)null:task.BlockIndex,"segment",task==null?(int?)null:task.Segment,
                "trial_id",task==null?null:task.TrialId,"word",task==null?null:task.Word,"ink",task==null?null:task.Ink,"condition",task==null?null:task.Condition)))recordedTaskKey=key;
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
        // Bounded diagnostic history; the counters below stay exact so a reported
        // count never depends on how many frames are retained.
        public const int MaximumRetainedOutputs=4096;
        readonly List<AvatarOutput> outputs=new List<AvatarOutput>();
        readonly List<string> applicationWarnings=new List<string>();
        readonly HashSet<string> trialFaults=new HashSet<string>();
        readonly SafetyStopInbox stopInbox=new SafetyStopInbox();
        readonly AutomaticGazeGeometry geometry;
        int packetAuditCursor;
        long faultAuditSequence=-1;
        readonly ClockAuditRecorder clockAudit=new ClockAuditRecorder();
        public int AppliedRemoteStops {get{return stopInbox.AppliedCount;}}
        public AuditLog Audit {get;private set;}
        public Experiment Experiment {get;private set;}
        public AvatarOutput CurrentAvatar {get;private set;}
        public FaultSnapshot CurrentFaults {get;private set;}
        public IReadOnlyList<AvatarOutput> Outputs {get{return outputs.AsReadOnly();}}
        public long OutputObservations {get;private set;}
        public long ValidSimulatorPresentations {get;private set;}
        public long AutomaticFallbacks {get;private set;}
        public long DiscardedOutputs {get;private set;}
        public IReadOnlyList<string> ApplicationWarnings {get{return applicationWarnings.AsReadOnly();}}
        public ParticipantSimulationApplication(string session,int localPort,int remotePort,AutomaticGazeGeometry geometry=null)
            :this(session,localPort,remotePort,geometry,null){}
        internal ParticipantSimulationApplication(string session,int localPort,int remotePort,AutomaticGazeGeometry geometry,UdpPeer transport)
            :base(session,PeerRole.Participant,localPort,remotePort,transport)
        {
            this.geometry=geometry??DevelopmentFixtures.Geometry();
            // Deliberately named integration fixture, not a participant allocation schedule.
            var allocation=new Allocation("simulation-integration-fixture-BCAD",new[]{Condition.B,Condition.C,Condition.A,Condition.D},GazeTarget.Participant,new[]{0,1,2,3});
            Experiment=new Experiment(new SessionConfiguration(session,20261028,allocation,RunKind.Simulation,"en",1),Clock);
            Audit=new AuditLog(session,RunKind.Simulation);
            Experiment.RecordPreparation(Actor.Owner,"consent_screening_calibration","not_applicable_synthetic_software_rehearsal");
        }
        protected override void ConnectionFaulted(){RefreshPresentation();}
        protected override void PacketsDiscarded(int removed){packetAuditCursor=Math.Max(0,packetAuditCursor-removed);}
        protected override SimulationJournalCapture CreateRecordingCapture(ContinuousJournal journal)
        {return new SimulationJournalCapture(journal,Experiment,Audit);}
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
            RefreshPresentation();BroadcastTask();RecordNetworkAudit();CaptureRecording();
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
                outputs.Add(CurrentAvatar);OutputObservations++;
                if(CurrentAvatar.HeadValid&&CurrentAvatar.EyeValid&&CurrentAvatar.ActualSource==ControlSource.Simulator)ValidSimulatorPresentations++;
                if(CurrentAvatar.ActualSource==ControlSource.Automatic)AutomaticFallbacks++;
                if(outputs.Count>MaximumRetainedOutputs)
                {int removed=outputs.Count-MaximumRetainedOutputs;outputs.RemoveRange(0,removed);DiscardedOutputs+=removed;}
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
            foreach(var fault in Monitor.Events)
            {
                if(fault.Sequence<=faultAuditSequence)continue;
                Audit.RecordTransport(new TransportAuditRecord(CurrentAuditContext(),ControlSource.Simulator,fault.SourceId,null,Clock.ClockId,fault.LocalSeconds,
                    fault.Kind,null,null,null,null,fault.Detail));
                faultAuditSequence=fault.Sequence;
            }
            clockAudit.Record(Synchronizer,Audit,CurrentAuditContext(),Clock.Now);
        }
        public void RecordControllerEvaluation()
        {
            if(CurrentAvatar!=null)Audit.RecordPresentation(new AvatarPresentationRecord(CurrentAuditContext(),Clock.ClockId,Clock.Now,
                "headless_controller_evaluation_not_rendered",Experiment.Phase,CurrentAvatar));
            CaptureRecording();
        }
        public ExportResult Export(string parentDirectory,ExportProvenance provenance)
        {RecordNetworkAudit();CaptureRecording(true);return AuditExporter.Export(Experiment,Audit,parentDirectory,provenance);}
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
        {if(Experiment.Phase==Phase.Preparation)Experiment.BeginFamiliarization(Actor.Owner);Experiment.BeginPractice(Actor.Owner);CaptureRecording();}
        public void OwnerLockNextBlock(ControlSelection source)
        {Experiment.LockBlock(Actor.Owner,source,"development-owner");CaptureRecording();}
        public void OwnerBeginBlock()
        {Experiment.BeginCountdown(Actor.Owner,Experiment.LockedControl.HasValue&&SourceReady(Experiment.LockedControl.Value));CaptureRecording();}

        // Opt-in automated test driver. No GUI input path calls this method.
        public void DriveHeadlessSimulation()
        {try{DriveHeadlessCore();}finally{CaptureRecording();}}
        void DriveHeadlessCore()
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
