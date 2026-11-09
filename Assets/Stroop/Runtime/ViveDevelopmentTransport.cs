using System;
using System.Collections.Generic;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using AvatarStroop.Core;

namespace AvatarStroop.Runtime
{
    // Presentation-only adapter over wire v2. It does not transport raw SDK
    // observations or support scientific acquisition, recording, or export.
    // Engineering identity is not authentication; remote use requires a trusted
    // network and an explicit opt-in. No peer discovery or source fallback occurs.
    public sealed class ViveDevelopmentTransport : IDisposable
    {
        readonly UdpPeer socket;
        readonly IClock clock;
        readonly PeerIdentity own, expected;
        readonly ReceiveMonitor monitor;
        readonly ClockSynchronizer synchronizer;
        readonly string mappingVersion, operatorStream, operatorDevice;
        readonly double maximumLocalAge, maximumMappedAge, engineeringEpochStart;
        readonly bool engineeringPoseOnly;
        readonly int engineeringOwnerThread;
        readonly SafetyStopRequestState stopRequest=new SafetyStopRequestState();
        readonly SafetyStopInbox stopInbox=new SafetyStopInbox();
        readonly Action applyStop;
        readonly HashSet<ViveDevelopmentPreflightChannel> preflightChannels=new HashSet<ViveDevelopmentPreflightChannel>();
        long outgoing, exchange, trackingSequence, lastSourceSequence=-1;
        int lastSentBlock=-1,lastSentSegment=-1;
        double nextHello, nextClock, lastLocalTime=-1,lastInvalidationTime=-1;
        bool disposed,connectionFaulted,clockFaulted;
        public string LastSendRejection {get;private set;}
        public bool LastInvalidationSent {get;private set;}
        public bool DevelopmentOnly {get{return true;}}
        public bool HardwareCaptureTimeVerified {get{return false;}}
        public bool CanRecordOrExport {get{return false;}}
        public bool EngineeringPoseOnly {get{return engineeringPoseOnly;}}
        public bool RemoteNetworkExplicitlyEnabled {get{return socket.RemoteNetworkExplicitlyEnabled;}}
        public bool SafetyStopPending {get{return stopRequest.Pending;}}
        public bool SafetyStopAcknowledged {get{return stopRequest.Acknowledged;}}

        // The stream binds the out-of-band mapping and explicit epoch to the
        // expected v2 source identity. This detects configuration mismatches;
        // it does not authenticate a peer or transmit per-frame provenance.
        public static string OperatorStream(string mappingVersion,string epoch)
        {
            if(String.IsNullOrWhiteSpace(mappingVersion)||String.IsNullOrWhiteSpace(epoch))throw new ArgumentException("Explicit mapping version and epoch are required");
            using(var sha=SHA256.Create())
            {
                var hash=sha.ComputeHash(Encoding.UTF8.GetBytes(mappingVersion.Length+":"+mappingVersion+epoch.Length+":"+epoch));
                return "vive-pro-eye/operator/dev-v1/"+BitConverter.ToString(hash).Replace("-","").ToLowerInvariant();
            }
        }

        // Separate namespace means engineering datagrams cannot match the
        // ordinary task peer even if an operator copies the same epoch string.
        public static string EngineeringOperatorStream(string mappingVersion,string epoch)
        {
            const string normalPrefix="vive-pro-eye/operator/dev-v1/";
            return "vive-pro-eye/operator/rehearsal-v1/"+OperatorStream(mappingVersion,epoch).Substring(normalPrefix.Length);
        }

        public ViveDevelopmentTransport(UdpPeer socket,IClock clock,PeerIdentity own,PeerIdentity expected,
            string expectedOperatorDevice,string mappingVersion,string epoch,DevelopmentFaultConfiguration faults,
            DevelopmentClockConfiguration clocks,double maximumLocalObservationAgeSeconds,Action applyStop=null,bool engineeringPoseOnly=false)
            :this(socket,clock,own,expected,expectedOperatorDevice,mappingVersion,epoch,faults,clocks,maximumLocalObservationAgeSeconds,applyStop,engineeringPoseOnly,false)
        {}
        ViveDevelopmentTransport(UdpPeer socket,IClock clock,PeerIdentity own,PeerIdentity expected,
            string expectedOperatorDevice,string mappingVersion,string epoch,DevelopmentFaultConfiguration faults,
            DevelopmentClockConfiguration clocks,double maximumLocalObservationAgeSeconds,Action applyStop,bool engineeringPoseOnly,bool inMemoryFixture)
        {
            if(!inMemoryFixture)throw new InvalidOperationException("Unauthenticated VIVE transport is disabled; only sealed in-memory simulation is available");
            if(socket==null||clock==null||own==null||expected==null||faults==null||clocks==null)throw new ArgumentNullException("Explicit transport, clocks, peers and thresholds are required");
            if(own.SessionId!=expected.SessionId||own.Role==expected.Role||own.PeerId==expected.PeerId||own.ClockId==expected.ClockId||own.ClockId!=clock.ClockId||
                own.Source!=ControlSource.LiveOperator||expected.Source!=ControlSource.LiveOperator)
                throw new ArgumentException("Distinct explicitly paired live development peers and clocks are required");
            if(!WireCodec.Finite(maximumLocalObservationAgeSeconds)||maximumLocalObservationAgeSeconds<=0)throw new ArgumentOutOfRangeException("maximumLocalObservationAgeSeconds");
            if(engineeringPoseOnly&&applyStop!=null)throw new ArgumentException("Engineering pose rehearsal has no task or safety-stop callback");
            operatorStream=engineeringPoseOnly?EngineeringOperatorStream(mappingVersion,epoch):OperatorStream(mappingVersion,epoch);
            if((own.Role==PeerRole.Operator?own.SourceId:expected.SourceId)!=operatorStream)
                throw new ArgumentException("Operator stream does not match the development mapping and epoch");
            this.socket=socket;this.clock=clock;this.own=own;this.expected=expected;this.mappingVersion=mappingVersion;operatorDevice=expectedOperatorDevice;
            maximumLocalAge=maximumLocalObservationAgeSeconds;this.applyStop=applyStop;this.engineeringPoseOnly=engineeringPoseOnly;
            maximumMappedAge=faults.MaximumMappedSampleAgeSeconds;engineeringOwnerThread=Thread.CurrentThread.ManagedThreadId;
            if(engineeringPoseOnly)
            {
                double start=clock.Now;
                if(clock.ClockId!=own.ClockId||!WireCodec.Finite(start)||start<0)
                    throw new ArgumentException("A finite engineering epoch start is required", "clock");
                engineeringEpochStart=start;lastLocalTime=start;
            }
            monitor=new ReceiveMonitor(expected,faults,expectedOperatorDevice,clock.ClockId);
            synchronizer=new ClockSynchronizer(clock.ClockId,expected.ClockId,clocks);
        }
        // This internal factory accepts only a sealed process-memory socket,
        // never an IUdpSocket supplied by a caller or a real/loopback socket.
        internal static ViveDevelopmentTransport CreateInMemoryFixture(ViveInMemoryDatagramSocket socket,IPEndPoint remote,IClock clock,PeerIdentity own,PeerIdentity expected,
            string expectedOperatorDevice,string mappingVersion,string epoch,DevelopmentFaultConfiguration faults,DevelopmentClockConfiguration clocks,
            double maximumLocalObservationAgeSeconds,Action applyStop=null,bool engineeringPoseOnly=false)
        {
            if(socket==null||remote==null||!IPAddress.IsLoopback(remote.Address)||remote.Port==0)throw new ArgumentException("A sealed in-memory fixture and loopback identity are required");
            var peer=new UdpPeer(socket.LocalEndpoint,remote,false,socket);
            try{return new ViveDevelopmentTransport(peer,clock,own,expected,expectedOperatorDevice,mappingVersion,epoch,faults,clocks,maximumLocalObservationAgeSeconds,applyStop,engineeringPoseOnly,true);}
            catch{peer.Dispose();throw;}
        }

        bool Send(WirePacket packet)
        {
            UdpSendResult sent;
            try{sent=socket.Send(packet);}
            catch{connectionFaulted=true;InvalidatePreflightChannels();throw;}
            if(sent.ConnectionFault!=null){RecordConnectionFault(sent.ConnectionFault);return false;}
            return sent.Sent; // Socket acceptance is not delivery.
        }
        void RequireEngineeringOwner()
        {
            // The clock map and receiver evidence are mutable and not thread-safe.
            // Restrict the engineering fixture to the peer's owner thread so a
            // pose decision cannot race a clock reply or tracking packet update.
            if(engineeringPoseOnly&&Thread.CurrentThread.ManagedThreadId!=engineeringOwnerThread)
                throw new InvalidOperationException("Engineering pose transport belongs to its creating thread");
        }
        double Now()
        {
            RequireEngineeringOwner();
            try
            {
                double now=clock.Now;
                if(clockFaulted||clock.ClockId!=own.ClockId||!WireCodec.Finite(now)||now<0||now<lastLocalTime)
                    throw new InvalidOperationException("Development transport clock failed; a new transport epoch is required");
                lastLocalTime=now;return now;
            }
            catch{clockFaulted=true;InvalidatePreflightChannels();throw;}
        }
        void RecordConnectionFault(UdpConnectionFault fault)
        {if(connectionFaulted)return;connectionFaulted=true;InvalidatePreflightChannels();monitor.RecordConnectionFault(fault.Reason,Now());}
        long Next(){return checked(outgoing++);}
        public void Tick()
        {
            RequireEngineeringOwner();
            if(disposed)throw new ObjectDisposedException("ViveDevelopmentTransport");
            try{TickCore();}
            catch{connectionFaulted=true;InvalidatePreflightChannels();throw;}
            finally{if(preflightChannels.Count>0&&!PreflightTransportHealthy())InvalidatePreflightChannels();}
        }
        void TickCore()
        {
            if(connectionFaulted)return;
            for(int count=0;count<128;count++)
            {
                var received=socket.Poll();if(received==null)break;
                double now=Now();
                if(received.ConnectionFault!=null){RecordConnectionFault(received.ConnectionFault);return;}
                if(!received.Accepted){monitor.RecordTransportRejection(received.Error,now);continue;}
                var packet=received.Packet;
                var decision=monitor.Observe(packet,now,synchronizer);if(!decision.Accepted)continue;
                if(packet.Kind==WireKind.TimeRequest)
                    Send(WirePacket.TimeReply(own,Next(),Now(),packet.ExchangeId,packet.RequestClockId,packet.RequestSentSeconds,now));
                else if(packet.Kind==WireKind.TimeReply)synchronizer.ObserveReply(packet,now);
                else if(packet.Kind==WireKind.SafetyStopRequest && own.Role==PeerRole.Participant && applyStop!=null)
                {stopInbox.ApplyOnce(packet.StopRequestId,applyStop);Send(WirePacket.SafetyStopAck(own,Next(),Now(),packet.StopRequestId));}
                else if(packet.Kind==WireKind.SafetyStopAck && own.Role==PeerRole.Operator)stopRequest.Acknowledge(packet.StopRequestId);
                if(connectionFaulted)return;
            }
            double current=Now();
            if(current>=nextHello){Send(WirePacket.Hello(own,Next(),current,SessionConfiguration.ProtocolVersion));nextHello=current+.1;}
            if(connectionFaulted)return;
            if(current>=nextClock)
            {
                string id=own.PeerId+"-dev-"+checked(exchange++);double sent=Now();
                if(synchronizer.RegisterRequest(id,sent))Send(WirePacket.TimeRequest(own,Next(),sent,id));
                nextClock=current+.1;
            }
        }
        public bool SendTask(TaskSnapshot task)
        {if(engineeringPoseOnly)throw new InvalidOperationException("Pose rehearsal does not send task state");if(own.Role!=PeerRole.Participant)throw new InvalidOperationException("Only participant host sends task state");return Send(WirePacket.Task(own,Next(),Now(),task));}
        public bool SendSafetyStop()
        {if(engineeringPoseOnly)throw new InvalidOperationException("Pose rehearsal has no task to stop");if(own.Role!=PeerRole.Operator)throw new InvalidOperationException("Only operator requests stop");return Send(WirePacket.SafetyStopRequest(own,Next(),Now(),stopRequest.Request()));}

        public bool SendObservation(ViveProEyeObservation observation)
        {
            if(own.Role!=PeerRole.Operator)throw new InvalidOperationException("Only operator sends a control observation");
            LastSendRejection=null;LastInvalidationSent=false;
            if(observation==null||observation.Sample==null||observation.RawPoll==null||observation.MappingVersion!=mappingVersion||
                observation.HardwareCaptureTimeVerified||observation.MappingValidatedForApparatus)
            {LastSendRejection="missing_or_incompatible_development_provenance";return false;}
            var sample=observation.Sample;double now=Now();
            if(sample.Source!=ControlSource.LiveOperator||sample.Role!=StreamRole.OperatorControl||sample.SessionId!=own.SessionId||
                sample.StreamId!=operatorStream||sample.ClockId!=own.ClockId||sample.DeviceId!=operatorDevice)
            {LastSendRejection="wrong_source_identity";return false;}
            if(!WireCodec.Finite(now)||now<sample.SendLocalSeconds||now-sample.CaptureLocalSeconds>maximumLocalAge)
            {LastSendRejection="stale_or_future_local_observation";return false;}
            if(engineeringPoseOnly)
            {
                // Reserved wire coordinates carry no block or trial semantics.
                if(sample.BlockIndex!=0||sample.Segment!=0){LastSendRejection="invalid_rehearsal_envelope";return false;}
            }
            else
            {
                var task=monitor.CurrentTask(now,synchronizer);
                if(task==null||task.BlockIndex!=sample.BlockIndex||task.Segment!=sample.Segment)
                {LastSendRejection="current_task_or_context_unavailable";return false;}
            }
            if(sample.BlockIndex==lastSentBlock&&sample.Segment==lastSentSegment&&sample.Sequence<=lastSourceSequence)
            {LastSendRejection="duplicate_or_old_source_observation";return false;}
            if(sample.CaptureLocalSeconds<=lastInvalidationTime)
            {LastSendRejection="observation_predates_invalidation";return false;}
            if(!sample.HeadValid||!sample.EyeValid||!observation.HeadUsable||!observation.EyeUsable||
                !sample.RawHeadRotation.IsFinite||!sample.RawEyeDirection.IsFinite)
            {
                LastSendRejection="unusable_or_unencodable_channels";
                // Explicit invalid status, never a replacement measurement. Both
                // channels are invalidated conservatively and v2's finite zero
                // payloads are invalid markers, not gaze/head observations.
                LastInvalidationSent=SendInvalidState(sample.BlockIndex,sample.Segment,now);
                if(LastInvalidationSent){lastSentBlock=sample.BlockIndex;lastSentSegment=sample.Segment;lastSourceSequence=sample.Sequence;}
                return false;
            }
            // Make a presentation-only envelope copy: the source sample is never
            // modified. The capture field is the local observation time; send is a
            // transport-method clock reading, not measured network emission. A
            // newer periodic Hello may already have been sent after acquisition.
            // RawEyeDirection in this legacy wire type is the derived direction.
            var outgoingSample=new HeadEyeSample(sample.SessionId,sample.StreamId,sample.DeviceId,sample.ClockId,
                sample.BlockIndex,sample.Segment,checked(trackingSequence++),sample.CaptureLocalSeconds,now,sample.Source,sample.Role,
                sample.RawHeadRotation,sample.RawEyeDirection,sample.HeadReportedValid,sample.EyeReportedValid);
            bool accepted=Send(WirePacket.Tracking(own,Next(),outgoingSample));
            if(accepted){lastSentBlock=sample.BlockIndex;lastSentSegment=sample.Segment;lastSourceSequence=sample.Sequence;}
            return accepted;
        }

        // Called when the operator application has no fresh observation at all.
        // It only targets the last context this transport actually published.
        public bool InvalidateLastPublishedPose()
        {
            if(own.Role!=PeerRole.Operator)throw new InvalidOperationException("Only operator invalidates control pose");
            LastInvalidationSent=false;
            if(lastSentBlock<0)return false;
            LastInvalidationSent=SendInvalidState(lastSentBlock,lastSentSegment,Now());
            return LastInvalidationSent;
        }
        bool SendInvalidState(int block,int segment,double now)
        {
            var invalid=new HeadEyeSample(own.SessionId,operatorStream,operatorDevice,own.ClockId,block,segment,
                checked(trackingSequence++),now,now,ControlSource.LiveOperator,StreamRole.OperatorControl,
                new Rotation(0,0,0,0),new Vec3(0,0,0),false,false);
            bool accepted=Send(WirePacket.Tracking(own,Next(),invalid));
            if(accepted)lastInvalidationTime=now;
            return accepted;
        }

        public ViveDevelopmentRemotePose CurrentPose(int blockIndex,int segment)
        {
            RequireEngineeringOwner();
            if(own.Role!=PeerRole.Participant)throw new InvalidOperationException("Only participant receives operator pose");
            if(disposed||connectionFaulted||clockFaulted)return null;
            HeadEyeSample sample;long packetSequence;double received;
            double now=Now();
            if(!monitor.TryGetCurrentTrackingEvidence(now,synchronizer,out sample,out packetSequence,out received)||
                sample.BlockIndex!=blockIndex||sample.Segment!=segment)return null;
            if(engineeringPoseOnly)
            {
                // Local Poll is an observation timestamp, not verified hardware
                // capture time. Neither endpoint identity nor this timing check
                // authenticates the peer. Reject if the entire mapped interval
                // cannot be shown to precede this packet's original receipt.
                // CaptureLocalSeconds is the earlier of the two observations;
                // SendLocalSeconds bounds the later one in this fixture. It is
                // still only a sender assertion, never authenticated evidence.
                double mapped,uncertainty,mappedSend,sendUncertainty;
                if(sample.SendLocalSeconds<sample.CaptureLocalSeconds||
                    !synchronizer.TryMapRemote(sample.CaptureLocalSeconds,out mapped,out uncertainty)||
                    !synchronizer.TryMapRemote(sample.SendLocalSeconds,out mappedSend,out sendUncertainty)||
                    !ConservativeEngineeringCaptureTime(mapped,uncertainty,mappedSend,sendUncertainty,
                        received,now,maximumMappedAge,engineeringEpochStart))return null;
            }
            return new ViveDevelopmentRemotePose(sample,packetSequence,received,mappingVersion);
        }
        internal static bool ConservativeEngineeringCaptureTime(double mapped,double uncertainty,double mappedSend,double sendUncertainty,
            double received,double now,double maximumAge,double epochStart)
        {
            if(!WireCodec.Finite(mapped)||mapped<0||!WireCodec.Finite(uncertainty)||uncertainty<0||
                !WireCodec.Finite(mappedSend)||mappedSend<mapped||!WireCodec.Finite(sendUncertainty)||sendUncertainty<0||
                !WireCodec.Finite(received)||received<0||!WireCodec.Finite(now)||now<received||
                !WireCodec.Finite(maximumAge)||maximumAge<=0||!WireCodec.Finite(epochStart)||epochStart<0)return false;
            double earliest=mapped-uncertainty,latest=mapped+uncertainty,latestSend=mappedSend+sendUncertainty;
            return WireCodec.Finite(earliest)&&earliest>=epochStart&&WireCodec.Finite(latest)&&latest<=received&&
                WireCodec.Finite(latestSend)&&latestSend<=received&&
                WireCodec.Finite(now-earliest)&&now-earliest<=maximumAge;
        }
        public bool RecoveryDecisionRequired {get{return monitor.Snapshot(Now(),synchronizer).RecoveryDecisionRequired;}}
        public bool DevelopmentClockAvailable {get{RequireEngineeringOwner();return synchronizer.Snapshot().Available;}}
        public TaskSnapshot CurrentTask
        {get{if(own.Role!=PeerRole.Operator)throw new InvalidOperationException("Only operator receives task state");return monitor.CurrentTask(Now(),synchronizer);}}
        // Evidence is copied from the accepted task datagram, never fabricated
        // from the time of a later read. It does not assert a live source lock.
        public bool TryGetCurrentTaskEvidence(out TaskSnapshot task,out long packetSequence,out double localReceivedSeconds)
        {
            if(own.Role!=PeerRole.Operator)throw new InvalidOperationException("Only operator receives task state");
            if(disposed||connectionFaulted||clockFaulted){task=null;packetSequence=-1;localReceivedSeconds=0;return false;}
            return monitor.TryGetCurrentTaskEvidence(Now(),synchronizer,out task,out packetSequence,out localReceivedSeconds);
        }
        // Creates a dormant channel with the transport's existing clock mapping.
        // It does not open a socket, authenticate an endpoint or admit a task.
        public ViveDevelopmentPreflightChannel CreatePreflightChannel(VivePreflightEndpointConfiguration preflight)
        {
            if(engineeringPoseOnly)throw new InvalidOperationException("Pose rehearsal cannot create an experiment preflight channel");
            if(preflight==null)throw new ArgumentNullException("preflight");
            if(!PreflightTransportHealthy()){InvalidatePreflightChannels();throw new InvalidOperationException("A healthy open development transport is required");}
            string operatorClock=own.Role==PeerRole.Operator?own.ClockId:expected.ClockId;
            string participantClock=own.Role==PeerRole.Participant?own.ClockId:expected.ClockId;
            if(preflight.Role!=own.Role||preflight.SessionId!=own.SessionId||preflight.OperatorStreamId!=operatorStream||
                preflight.OperatorClockId!=operatorClock||preflight.ParticipantClockId!=participantClock||
                preflight.MappingVersion!=mappingVersion||preflight.ExpectedOperatorDeviceId!=operatorDevice||
                !preflight.Local.Address.Equals(socket.LocalEndpoint.Address)||!preflight.Remote.Address.Equals(socket.RemoteEndpoint.Address)||
                preflight.RemoteNetworkExplicitlyEnabled!=socket.RemoteNetworkExplicitlyEnabled)
                throw new ArgumentException("Preflight identity must match this transport epoch", "preflight");
            var channel=new ViveDevelopmentPreflightChannel(preflight,clock,synchronizer);
            channel.AttachTransport(PreflightTransportHealthy,DetachPreflightChannel);preflightChannels.Add(channel);return channel;
        }
        bool PreflightTransportHealthy()
        {
            if(disposed||connectionFaulted||clockFaulted)return false;
            try{return !monitor.Snapshot(Now(),synchronizer).RecoveryDecisionRequired;}
            catch(Exception){clockFaulted=true;return false;}
        }
        void DetachPreflightChannel(ViveDevelopmentPreflightChannel channel){preflightChannels.Remove(channel);}
        internal void InvalidatePreflightChannels()
        {foreach(var channel in new List<ViveDevelopmentPreflightChannel>(preflightChannels))channel.InvalidateFromTransport();}
        public void Dispose(){RequireEngineeringOwner();if(disposed)return;disposed=true;InvalidatePreflightChannels();socket.Dispose();}
    }

    // A transient presentation value, never an acquisition record.
    public sealed class ViveDevelopmentRemotePose
    {
        public Rotation MappedHeadRotation {get;private set;}
        public Vec3 DerivedMappedEyeDirection {get;private set;}
        public string MappingVersion {get;private set;}
        public string SessionId {get;private set;}
        public string StreamId {get;private set;}
        public string RemoteClockId {get;private set;}
        public int BlockIndex {get;private set;}
        public int Segment {get;private set;}
        public long TrackingPacketSequence {get;private set;}
        public long SampleSequence {get;private set;}
        public double LocalReceivedSeconds {get;private set;}
        public double RemoteObservedSeconds {get;private set;}
        // Wire v2 carries neither trial identity nor authenticated provenance.
        public bool TrialBound {get{return false;}}
        public bool CryptographicallyAuthenticated {get{return false;}}
        public bool HardwareCaptureTimeVerified {get{return false;}}
        public bool ApparatusMappingValidated {get{return false;}}
        internal ViveDevelopmentRemotePose(HeadEyeSample sample,long packetSequence,double localReceivedSeconds,string mappingVersion)
        {
            MappedHeadRotation=sample.RawHeadRotation;DerivedMappedEyeDirection=sample.RawEyeDirection;MappingVersion=mappingVersion;
            SessionId=sample.SessionId;StreamId=sample.StreamId;RemoteClockId=sample.ClockId;
            BlockIndex=sample.BlockIndex;Segment=sample.Segment;TrackingPacketSequence=packetSequence;
            SampleSequence=sample.Sequence;LocalReceivedSeconds=localReceivedSeconds;RemoteObservedSeconds=sample.CaptureLocalSeconds;
        }
    }
}
