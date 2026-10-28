using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;

namespace AvatarStroop.Core
{
    public sealed class MonotonicClock : IClock
    {
        readonly Stopwatch timer=Stopwatch.StartNew();
        public double Now {get{return timer.Elapsed.TotalSeconds;}}
        public string ClockId {get;private set;}
        public MonotonicClock(string clockId) {WireCodec.RequireIdentity(clockId);ClockId=clockId;}
    }

    // All values are explicit engineering choices. None are approved study thresholds.
    public sealed class DevelopmentClockConfiguration
    {
        public bool DevelopmentOnly {get{return true;}}
        public int MinimumDriftSamples {get;private set;}
        public double MinimumDriftSpanSeconds {get;private set;}
        public double MaximumRoundTripSeconds {get;private set;}
        public double MaximumExtrapolationSeconds {get;private set;}
        public double UnmodeledDriftPartsPerMillion {get;private set;}
        public DevelopmentClockConfiguration(int minimumDriftSamples,double minimumDriftSpanSeconds,double maximumRoundTripSeconds,double maximumExtrapolationSeconds,double unmodeledDriftPartsPerMillion)
        {
            if(minimumDriftSamples<3 || minimumDriftSamples>64 || !Positive(minimumDriftSpanSeconds) || !Positive(maximumRoundTripSeconds) || !Positive(maximumExtrapolationSeconds) || !Positive(unmodeledDriftPartsPerMillion))
                throw new ArgumentException("Explicit positive development clock settings required; at least three drift samples");
            MinimumDriftSamples=minimumDriftSamples;MinimumDriftSpanSeconds=minimumDriftSpanSeconds;MaximumRoundTripSeconds=maximumRoundTripSeconds;MaximumExtrapolationSeconds=maximumExtrapolationSeconds;UnmodeledDriftPartsPerMillion=unmodeledDriftPartsPerMillion;
        }
        static bool Positive(double value) {return WireCodec.Finite(value)&&value>0;}
    }

    public sealed class ClockMappingSnapshot
    {
        public bool Available {get;internal set;}
        public bool DriftAvailable {get;internal set;}
        public bool DevelopmentOnly {get{return true;}}
        public string LocalClockId {get;internal set;}
        public string RemoteClockId {get;internal set;}
        public int SampleCount {get;internal set;}
        public double LocalReferenceSeconds {get;internal set;}
        public double RemoteReferenceSeconds {get;internal set;}
        // Offset is remote minus local at the reference instant, not a Unix-clock offset.
        public double? RemoteMinusLocalSeconds {get;internal set;}
        public double? RemoteDriftPartsPerMillion {get;internal set;}
        public double LocalSecondsPerRemoteSecond {get;internal set;}
        public double? MinimumLocalSecondsPerRemoteSecond {get;internal set;}
        public double? MaximumLocalSecondsPerRemoteSecond {get;internal set;}
        public double? RateUncertaintyLocalSecondsPerRemoteSecond {get;internal set;}
        public double? UncertaintySeconds {get;internal set;}
        public string UncertaintyLabel {get{return "development_estimate_not_hardware_validated";}}
    }

    public sealed class ClockSynchronizer
    {
        sealed class Observation {public double T0,T1,T2,T3,Local,Remote;}
        readonly DevelopmentClockConfiguration config;
        readonly List<Observation> observations=new List<Observation>();
        readonly Dictionary<string,double> pending=new Dictionary<string,double>();
        readonly HashSet<string> issued=new HashSet<string>();
        readonly string localClockId,remoteClockId;
        double lastRequest=-1,lastLocalReceive=-1,lastRemoteReceive=-1,lastAcceptedRequest=-1;
        bool invalidated;
        public string LastRejection {get;private set;}
        public ClockSynchronizer(string localClockId,string remoteClockId,DevelopmentClockConfiguration config)
        {
            WireCodec.RequireIdentity(localClockId);WireCodec.RequireIdentity(remoteClockId);
            if(config==null || localClockId==remoteClockId) throw new ArgumentException("Two distinct named clocks and explicit settings required");
            this.localClockId=localClockId;this.remoteClockId=remoteClockId;this.config=config;
        }
        // Caller records a request before sending it. A bounded set prevents unbounded unanswered traffic.
        public bool RegisterRequest(string exchangeId,double localSent)
        {
            WireCodec.RequireIdentity(exchangeId);
            if(!WireCodec.Finite(localSent)||localSent<0||localSent<lastRequest) {invalidated=true;return Reject("local_request_clock_invalid");}
            if(issued.Contains(exchangeId)) return Reject("duplicate_exchange_id");
            if(pending.Count>=64) return Reject("pending_limit_expire_requests_explicitly");
            pending.Add(exchangeId,localSent);issued.Add(exchangeId);lastRequest=localSent;LastRejection=null;return true;
        }
        public bool ExpireRequest(string exchangeId) {return pending.Remove(exchangeId);}
        public bool ObserveReply(WirePacket reply,double localReceived)
        {
            if(reply==null || reply.Kind!=WireKind.TimeReply || reply.Peer.ClockId!=remoteClockId || reply.RequestClockId!=localClockId) return Reject("clock_or_reply_mismatch");
            double sent;
            if(!pending.TryGetValue(reply.ExchangeId,out sent) || sent!=reply.RequestSentSeconds) return Reject("uncorrelated_reply");
            pending.Remove(reply.ExchangeId);
            double t1=reply.RemoteReceivedSeconds,t2=reply.RemoteSentSeconds;
            if(!WireCodec.Finite(localReceived)||localReceived<sent || t1<0 || t2<t1) return Reject("invalid_exchange_order");
            // A delayed reply is rejected without invalidating a newer map. A clock reset requires an explicit new synchronizer.
            if(localReceived<lastLocalReceive) {invalidated=true;return Reject("local_clock_rollback");}
            if(t1<lastRemoteReceive) {if(sent>=lastAcceptedRequest)invalidated=true;return Reject("reordered_reply_or_remote_clock_rollback");}
            double slope=Snapshot().LocalSecondsPerRemoteSecond;
            if(slope<=0) slope=1;
            double transit=(localReceived-sent)-slope*(t2-t1);
            if(!WireCodec.Finite(transit)||transit<0 || transit>config.MaximumRoundTripSeconds) return Reject("unusable_round_trip");
            observations.Add(new Observation{T0=sent,T1=t1,T2=t2,T3=localReceived,Local=sent+(localReceived-sent)/2,Remote=t1+(t2-t1)/2});
            if(observations.Count>64) observations.RemoveAt(0);
            lastLocalReceive=localReceived;lastRemoteReceive=t1;lastAcceptedRequest=sent;LastRejection=null;return true;
        }
        bool Reject(string reason) {LastRejection=reason;return false;}
        public ClockMappingSnapshot Snapshot()
        {
            var result=new ClockMappingSnapshot{LocalClockId=localClockId,RemoteClockId=remoteClockId,SampleCount=observations.Count,LocalSecondsPerRemoteSecond=1};
            if(observations.Count==0 || invalidated) return result;
            Observation best=observations[0];
            foreach(var o in observations) if((o.T3-o.T0)-(o.T2-o.T1)<(best.T3-best.T0)-(best.T2-best.T1)) best=o;
            double local=best.Local,remote=best.Remote,slope=1;
            // Every feasible affine map must place remote receive after local send and remote send before local receive.
            // For all i,j: slope*(T2[j]-T1[i]) <= T3[j]-T0[i]. This captures transit-asymmetry uncertainty in the fitted rate.
            double minimumRate=0,maximumRate=Double.PositiveInfinity;
            foreach(var a in observations)foreach(var b in observations)
            {
                double denominator=b.T2-a.T1,numerator=b.T3-a.T0;
                if(denominator>0)maximumRate=Math.Min(maximumRate,numerator/denominator);
                else if(denominator<0)minimumRate=Math.Max(minimumRate,numerator/denominator);
                else if(numerator<0)return result;
            }
            if(!WireCodec.Finite(minimumRate)||maximumRate<=0||minimumRate>maximumRate)return result;
            var first=observations[0];var last=observations[observations.Count-1];
            if(observations.Count>=config.MinimumDriftSamples && last.Local-first.Local>=config.MinimumDriftSpanSeconds)
            {
                // Center relative to the first observation to retain precision for long monotonic runs.
                double meanX=0,meanY=0;
                foreach(var o in observations){meanX+=o.Remote-first.Remote;meanY+=o.Local-first.Local;}
                meanX/=observations.Count;meanY/=observations.Count;
                double xx=0,xy=0;
                foreach(var o in observations){double x=(o.Remote-first.Remote)-meanX;xx+=x*x;xy+=x*((o.Local-first.Local)-meanY);}
                if(xx>0 && WireCodec.Finite(xx) && WireCodec.Finite(xy))
                {
                    slope=xy/xx;
                    if(!WireCodec.Finite(slope)||slope<=0) return result;
                    slope=Math.Max(minimumRate,Math.Min(maximumRate,slope));
                    remote=last.Remote;local=first.Local+meanY+slope*((remote-first.Remote)-meanX);result.DriftAvailable=true;
                }
            }
            // The central estimate must also have a feasible intercept at its chosen rate.
            double lowerLocal=Double.NegativeInfinity,upperLocal=Double.PositiveInfinity;
            foreach(var o in observations){lowerLocal=Math.Max(lowerLocal,o.T0+slope*(remote-o.T1));upperLocal=Math.Min(upperLocal,o.T3+slope*(remote-o.T2));}
            if(lowerLocal>upperLocal)return result;
            local=Math.Max(lowerLocal,Math.Min(upperLocal,local));
            // RTT is expressed in local seconds after the affine rate estimate. Residual covers model disagreement.
            double uncertainty=0;
            foreach(var o in observations)
            {
                double transit=(o.T3-o.T0)-slope*(o.T2-o.T1);
                if(transit<0 || !WireCodec.Finite(transit)) return new ClockMappingSnapshot{LocalClockId=localClockId,RemoteClockId=remoteClockId,SampleCount=observations.Count,LocalSecondsPerRemoteSecond=1};
                double residual=Math.Abs(o.Local-(local+slope*(o.Remote-remote)));
                uncertainty=Math.Max(uncertainty,transit/2+residual);
            }
            // Bound the reference instant using its own exchange, allowing every feasible rate.
            // Extrapolation later propagates the full rate interval, not merely a configured oscillator allowance.
            var anchor=result.DriftAvailable?last:best;
            double processing=anchor.T2-anchor.T1;
            uncertainty=Math.Max(uncertainty,Math.Max(Math.Abs(local-(anchor.T0+minimumRate*processing/2)),Math.Abs((anchor.T3-minimumRate*processing/2)-local)));
            if(!WireCodec.Finite(local)||!WireCodec.Finite(remote)||!WireCodec.Finite(uncertainty)) return result;
            result.Available=true;result.LocalReferenceSeconds=local;result.RemoteReferenceSeconds=remote;result.RemoteMinusLocalSeconds=remote-local;
            result.LocalSecondsPerRemoteSecond=slope;result.UncertaintySeconds=uncertainty;
            result.MinimumLocalSecondsPerRemoteSecond=minimumRate;
            result.MaximumLocalSecondsPerRemoteSecond=WireCodec.Finite(maximumRate)?(double?)maximumRate:null;
            result.RateUncertaintyLocalSecondsPerRemoteSecond=WireCodec.Finite(maximumRate)?(double?)Math.Max(Math.Abs(slope-minimumRate),Math.Abs(maximumRate-slope)):null;
            result.RemoteDriftPartsPerMillion=result.DriftAvailable?(double?)((1/slope-1)*1000000):null;
            return result;
        }
        public bool TryMapRemote(double remoteSeconds,out double localSeconds,out double uncertaintySeconds)
        {
            localSeconds=Double.NaN;uncertaintySeconds=Double.NaN;
            var map=Snapshot();
            if(!map.Available || !WireCodec.Finite(remoteSeconds)||remoteSeconds<0) return false;
            double delta=(remoteSeconds-map.RemoteReferenceSeconds)*map.LocalSecondsPerRemoteSecond;
            if(!WireCodec.Finite(delta)||Math.Abs(delta)>config.MaximumExtrapolationSeconds) return false;
            double remoteDelta=remoteSeconds-map.RemoteReferenceSeconds;
            if(!map.RateUncertaintyLocalSecondsPerRemoteSecond.HasValue && remoteDelta!=0)return false;
            double mapped=map.LocalReferenceSeconds+delta;
            double uncertainty=map.UncertaintySeconds.Value+Math.Abs(remoteDelta)*(map.RateUncertaintyLocalSecondsPerRemoteSecond??0)+Math.Abs(delta)*config.UnmodeledDriftPartsPerMillion/1000000;
            if(!WireCodec.Finite(mapped)||!WireCodec.Finite(uncertainty)||mapped<0) return false;
            localSeconds=mapped;uncertaintySeconds=uncertainty;return true;
        }
    }

    public sealed class DevelopmentFaultConfiguration
    {
        public bool DevelopmentOnly {get{return true;}}
        public double ConnectionStaleSeconds {get;private set;}
        public double TrackingStaleSeconds {get;private set;}
        public double MaximumMappedSampleAgeSeconds {get;private set;}
        public DevelopmentFaultConfiguration(double connectionStaleSeconds,double trackingStaleSeconds,double maximumMappedSampleAgeSeconds)
        {
            if(!WireCodec.Finite(connectionStaleSeconds)||connectionStaleSeconds<=0||!WireCodec.Finite(trackingStaleSeconds)||trackingStaleSeconds<=0||!WireCodec.Finite(maximumMappedSampleAgeSeconds)||maximumMappedSampleAgeSeconds<=0)
                throw new ArgumentException("Explicit positive development fault thresholds required");
            ConnectionStaleSeconds=connectionStaleSeconds;TrackingStaleSeconds=trackingStaleSeconds;MaximumMappedSampleAgeSeconds=maximumMappedSampleAgeSeconds;
        }
    }
    public enum FaultKind { AwaitingHandshake, ConnectionStale, AwaitingTracking, HeadInvalid, EyeInvalid, HeadStale, EyeStale, ClockMappingUnavailable, CaptureAgeUncertain, RecoveryDecisionRequired }
    public sealed class FaultWarning
    {
        public FaultKind Kind {get;internal set;}
        public string Message {get;internal set;}
        public bool DevelopmentOnly {get{return true;}}
        public bool RequiresProtocolDecision {get{return true;}}
    }
    public sealed class FaultEvent
    {
        public long Sequence {get;internal set;}
        public double LocalSeconds {get;internal set;}
        public string Kind {get;internal set;}
        public string Detail {get;internal set;}
        public string SessionId {get;internal set;}
        public string SourceId {get;internal set;}
    }
    public sealed class FaultSnapshot
    {
        public bool DevelopmentOnly {get{return true;}}
        public bool HandshakeAccepted {get;internal set;}
        public bool RecoveryDecisionRequired {get;internal set;}
        public bool CaptureAgeVerified {get;internal set;}
        public bool LocalHeadFresh {get;internal set;}
        public bool LocalEyeFresh {get;internal set;}
        public bool HeadUsable {get;internal set;}
        public bool EyeUsable {get;internal set;}
        public double? SecondsSinceReceipt {get;internal set;}
        public double? MappedSampleAgeSeconds {get;internal set;}
        public double? ClockUncertaintySeconds {get;internal set;}
        public IReadOnlyList<FaultWarning> Warnings {get;internal set;}
        public long MissingSequencePositions {get;internal set;}
        public long DuplicatePackets {get;internal set;}
        public long OutOfOrderPackets {get;internal set;}
        public long RejectedPackets {get;internal set;}
        public bool AutomaticPauseOrFallbackSelected {get{return false;}}
    }
    public sealed class ReceiveDecision
    {
        public bool Accepted {get;internal set;}
        public string Reason {get;internal set;}
        public long MissingBeforePacket {get;internal set;}
    }

    // Reports diagnostic facts only; the accepted study's pause/recovery/notification policy is still unresolved.
    public sealed class ReceiveMonitor
    {
        readonly PeerIdentity expected;
        readonly string expectedDeviceId,localClockId;
        readonly DevelopmentFaultConfiguration config;
        readonly List<FaultEvent> events=new List<FaultEvent>();
        readonly HashSet<long> seen=new HashSet<long>();
        readonly Queue<long> seenOrder=new Queue<long>();
        HashSet<FaultKind> active=new HashSet<FaultKind>();
        long highest=-1,missing,duplicates,outOfOrder,rejected;
        double lastNow=-1,lastSent=-1,lastCapture=-1;
        double? lastReceipt,lastTracking,lastHeadGood,lastEyeGood,lastTaskReceived,lastTaskSent;
        bool handshake,recoveryRequired;
        public HeadEyeSample LastSample {get;private set;}
        public TaskSnapshot LastTask {get;private set;}
        public IReadOnlyList<FaultEvent> Events {get{return new ReadOnlyCollection<FaultEvent>(events);}}
        public ReceiveMonitor(PeerIdentity expected,DevelopmentFaultConfiguration config,string expectedDeviceId,string localClockId)
        {
            if(expected==null||config==null) throw new ArgumentException("Explicit expected peer and thresholds required");
            WireCodec.RequireIdentity(expectedDeviceId);WireCodec.RequireIdentity(localClockId);
            this.expected=expected;this.config=config;this.expectedDeviceId=expectedDeviceId;this.localClockId=localClockId;
        }
        void Time(double now)
        {
            if(!WireCodec.Finite(now)||now<0||now<lastNow) throw new InvalidOperationException("Receiver monotonic clock invalid or moved backward");
            lastNow=now;
            if(handshake && lastReceipt.HasValue && now-lastReceipt.Value>config.ConnectionStaleSeconds)recoveryRequired=true;
        }
        void Event(double now,string kind,string detail)
        {events.Add(new FaultEvent{Sequence=events.Count,LocalSeconds=now,Kind=kind,Detail=detail,SessionId=expected.SessionId,SourceId=expected.SourceId});}
        ReceiveDecision Reject(double now,string reason)
        {rejected++;Event(now,"packet_rejected",reason);return new ReceiveDecision{Reason=reason};}
        public void RecordTransportRejection(string reason,double localReceived)
        {Time(localReceived);Reject(localReceived,reason??"unspecified_transport_rejection");}
        public ReceiveDecision Observe(WirePacket packet,double localReceived,ClockSynchronizer mapping=null)
        {
            Time(localReceived);
            if(packet==null)return Reject(localReceived,"null_packet");
            var p=packet.Peer;
            if(p.SessionId!=expected.SessionId)return Reject(localReceived,"wrong_session");
            if(p.Role!=expected.Role)return Reject(localReceived,"wrong_role");
            if(p.PeerId!=expected.PeerId || p.SourceId!=expected.SourceId || p.Source!=expected.Source || p.ClockId!=expected.ClockId)return Reject(localReceived,"wrong_source_or_clock");
            if(packet.Kind==WireKind.Hello && packet.ProtocolVersion!=SessionConfiguration.ProtocolVersion)return Reject(localReceived,"protocol_version_mismatch");
            if(!handshake && packet.Kind!=WireKind.Hello)return Reject(localReceived,"handshake_required");
            if(packet.Sequence<=highest)
            {
                if(seen.Contains(packet.Sequence)){duplicates++;return Reject(localReceived,"duplicate");}
                outOfOrder++;return Reject(localReceived,"out_of_order");
            }
            if(packet.SentLocalSeconds<lastSent)return Reject(localReceived,"source_clock_rollback");
            if(packet.Kind==WireKind.HeadEye)
            {
                var s=packet.Sample;
                if(s.DeviceId!=expectedDeviceId)return Reject(localReceived,"wrong_tracking_device");
                if(s.CaptureLocalSeconds<lastCapture)return Reject(localReceived,"capture_clock_rollback");
                if(LastSample!=null && (s.BlockIndex<LastSample.BlockIndex || (s.BlockIndex==LastSample.BlockIndex && s.Segment<LastSample.Segment)))return Reject(localReceived,"old_tracking_context");
                if(LastSample!=null && s.BlockIndex==LastSample.BlockIndex && s.Segment==LastSample.Segment && s.Sequence<=LastSample.Sequence)return Reject(localReceived,"duplicate_or_old_sample");
                double capture,uncertainty;
                if(MatchMap(mapping) && mapping.TryMapRemote(s.CaptureLocalSeconds,out capture,out uncertainty))
                {
                    if(localReceived-(capture+uncertainty)>config.MaximumMappedSampleAgeSeconds)return Reject(localReceived,"stale_capture");
                    if(capture-uncertainty>localReceived)return Reject(localReceived,"future_capture");
                }
            }
            long gap=packet.Sequence-highest-1;missing+=gap;highest=packet.Sequence;lastSent=packet.SentLocalSeconds;lastReceipt=localReceived;
            seen.Add(packet.Sequence);seenOrder.Enqueue(packet.Sequence);if(seenOrder.Count>4096)seen.Remove(seenOrder.Dequeue());
            if(gap>0)Event(localReceived,"sequence_gap",gap.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if(packet.Kind==WireKind.Hello){handshake=true;Event(localReceived,"handshake_accepted",packet.ProtocolVersion);}
            if(packet.Kind==WireKind.HeadEye)
            {
                LastSample=packet.Sample;lastTracking=localReceived;lastCapture=LastSample.CaptureLocalSeconds;
                if(LastSample.HeadValid)lastHeadGood=localReceived;
                if(LastSample.EyeValid)lastEyeGood=localReceived;
            }
            if(packet.Kind==WireKind.Task){LastTask=packet.Snapshot;lastTaskReceived=localReceived;lastTaskSent=packet.SentLocalSeconds;}
            return new ReceiveDecision{Accepted=true,MissingBeforePacket=gap};
        }
        bool MatchMap(ClockSynchronizer map) {return map!=null && map.Snapshot().RemoteClockId==expected.ClockId && map.Snapshot().LocalClockId==localClockId;}
        public HeadEyeSample CurrentSample(double now,ClockSynchronizer mapping=null)
        {var status=Snapshot(now,mapping);return status.HeadUsable||status.EyeUsable?LastSample:null;}
        public TaskSnapshot CurrentTask(double now,ClockSynchronizer mapping=null)
        {
            Time(now);double mapped,u;
            if(recoveryRequired||LastTask==null||!lastTaskReceived.HasValue||now-lastTaskReceived.Value>config.ConnectionStaleSeconds||!MatchMap(mapping)||!mapping.TryMapRemote(lastTaskSent.Value,out mapped,out u)||now-(mapped-u)>config.MaximumMappedSampleAgeSeconds||mapped-u>now)return null;
            return LastTask;
        }
        public FaultSnapshot Snapshot(double now,ClockSynchronizer mapping=null)
        {
            Time(now);
            var warnings=new List<FaultWarning>();
            Action<FaultKind,string> add=(kind,message)=>warnings.Add(new FaultWarning{Kind=kind,Message=message});
            bool connectionFresh=lastReceipt.HasValue && now-lastReceipt.Value<=config.ConnectionStaleSeconds;
            if(recoveryRequired)add(FaultKind.RecoveryDecisionRequired,"Connection freshness was lost. A new explicit receiver epoch and synchronization are required; recovery policy is unresolved.");
            if(!handshake)add(FaultKind.AwaitingHandshake,"Expected peer has not completed the versioned handshake.");
            else if(!connectionFresh)add(FaultKind.ConnectionStale,"Expected peer traffic exceeded the configured development freshness interval.");
            bool head=false,eye=false,localHead=false,localEye=false,ageVerified=false; double? mappedAge=null,uncertainty=null;
            if(LastSample==null)add(FaultKind.AwaitingTracking,"No accepted tracking sample from the selected source.");
            else
            {
                bool trackingFresh=lastTracking.HasValue && now-lastTracking.Value<=config.TrackingStaleSeconds;
                head=connectionFresh && trackingFresh && LastSample.HeadValid && lastHeadGood.HasValue && now-lastHeadGood.Value<=config.TrackingStaleSeconds;
                eye=connectionFresh && trackingFresh && LastSample.EyeValid && lastEyeGood.HasValue && now-lastEyeGood.Value<=config.TrackingStaleSeconds;
                localHead=head;localEye=eye;
                if(!LastSample.HeadValid)add(FaultKind.HeadInvalid,"Selected source reports invalid head rotation.");
                if(!LastSample.EyeValid)add(FaultKind.EyeInvalid,"Selected source reports invalid eye direction.");
                if(!trackingFresh || (lastHeadGood.HasValue && now-lastHeadGood.Value>config.TrackingStaleSeconds))add(FaultKind.HeadStale,"Head samples exceeded the configured development freshness interval.");
                if(!trackingFresh || (lastEyeGood.HasValue && now-lastEyeGood.Value>config.TrackingStaleSeconds))add(FaultKind.EyeStale,"Eye samples exceeded the configured development freshness interval.");
                double capture,u;
                if(MatchMap(mapping) && mapping.TryMapRemote(LastSample.CaptureLocalSeconds,out capture,out u))
                {
                    mappedAge=now-capture;uncertainty=u;ageVerified=capture-u<=now && now-(capture-u)<=config.MaximumMappedSampleAgeSeconds;
                    if(!ageVerified){head=false;eye=false;add(FaultKind.CaptureAgeUncertain,"Clock mapping cannot establish capture freshness within the configured development interval.");}
                    if(now-(capture+u)>config.MaximumMappedSampleAgeSeconds){head=false;eye=false;if(trackingFresh){add(FaultKind.HeadStale,"Mapped head capture is stale beyond timing uncertainty.");add(FaultKind.EyeStale,"Mapped eye capture is stale beyond timing uncertainty.");}}
                }
                else {head=false;eye=false;add(FaultKind.ClockMappingUnavailable,"Cross-clock age is unavailable; local receipt freshness is reported separately.");}
            }
            if(recoveryRequired){head=false;eye=false;}
            var next=new HashSet<FaultKind>();foreach(var warning in warnings)next.Add(warning.Kind);
            foreach(var fault in next)if(!active.Contains(fault))Event(now,"fault_started",fault.ToString());
            foreach(var fault in active)if(!next.Contains(fault))Event(now,"fault_cleared",fault.ToString());
            active=next;
            return new FaultSnapshot{HandshakeAccepted=handshake,RecoveryDecisionRequired=recoveryRequired,CaptureAgeVerified=ageVerified,LocalHeadFresh=localHead,LocalEyeFresh=localEye,HeadUsable=head,EyeUsable=eye,SecondsSinceReceipt=lastReceipt.HasValue?(double?)(now-lastReceipt.Value):null,MappedSampleAgeSeconds=mappedAge,ClockUncertaintySeconds=uncertainty,Warnings=warnings.AsReadOnly(),MissingSequencePositions=missing,DuplicatePackets=duplicates,OutOfOrderPackets=outOfOrder,RejectedPackets=rejected};
        }
    }
}
