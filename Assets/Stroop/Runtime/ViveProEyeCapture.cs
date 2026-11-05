using System;
using AvatarStroop.Core;

namespace AvatarStroop.Runtime
{
    public enum ViveEyeRuntimeState { Unknown, Working, Unavailable, Error }
    // ReportedReady is an SDK report, never evidence of quantitative tracking validation.
    public enum ViveEyeCalibrationState { Unknown, Required, ReportedReady }
    public enum ViveFrameDisposition { Missing, Accepted, Duplicate, OutOfOrder, Stale, InvalidObservationTime, InvalidSequence }
    public enum ViveTimestampBasis { LocalPollObservation }

    // Only gaze rays and their validity masks are retained. There are deliberately no
    // eye images, pupil measurements, device serials, files, or persistence callbacks.
    public sealed class ViveEyeRay
    {
        public ulong ValidityMask {get;private set;}
        public bool OriginReportedValid {get;private set;}
        public bool DirectionReportedValid {get;private set;}
        public Vec3 OriginMillimeters {get;private set;}
        public Vec3 Direction {get;private set;}
        public ViveEyeRay(ulong validityMask,bool originReportedValid,bool directionReportedValid,Vec3 originMillimeters,Vec3 direction)
        {ValidityMask=validityMask;OriginReportedValid=originReportedValid;DirectionReportedValid=directionReportedValid;OriginMillimeters=originMillimeters;Direction=direction;}
    }

    public sealed class ViveEyeFrame
    {
        // Preserve signed SDK integers exactly. Timestamp is opaque native time: it
        // has no established relationship to IClock and is not a capture-age estimate.
        public int FrameSequence {get;private set;}
        public int Timestamp {get;private set;}
        public double ObservedLocalSeconds {get;private set;}
        // Legacy adapter property: true means no available eye data. This is the
        // inverse of the installed HTC client's misleadingly named no_user byte,
        // which is positive when eye data is present. It is not physical-wear proof.
        public bool NoUser {get;private set;}
        public ViveEyeRay Left {get;private set;}
        public ViveEyeRay Right {get;private set;}
        public ViveEyeRay Combined {get;private set;}
        public ViveEyeFrame(int frameSequence,int timestamp,double observedLocalSeconds,bool noUser,ViveEyeRay left,ViveEyeRay right,ViveEyeRay combined)
        {FrameSequence=frameSequence;Timestamp=timestamp;ObservedLocalSeconds=observedLocalSeconds;NoUser=noUser;Left=left;Right=right;Combined=combined;}
    }

    public sealed class ViveHeadFrame
    {
        // ObservationSequence identifies a newly read head observation, not a claim
        // about the XR device's hardware frame number or pose prediction timestamp.
        public long ObservationSequence {get;private set;}
        public double ObservedLocalSeconds {get;private set;}
        public bool DeviceConnected {get;private set;}
        public bool RotationReportedValid {get;private set;}
        public Rotation RawTrackingRotation {get;private set;}
        public ViveHeadFrame(long observationSequence,double observedLocalSeconds,bool deviceConnected,bool rotationReportedValid,Rotation rawTrackingRotation)
        {ObservationSequence=observationSequence;ObservedLocalSeconds=observedLocalSeconds;DeviceConnected=deviceConnected;RotationReportedValid=rotationReportedValid;RawTrackingRotation=rawTrackingRotation;}
    }

    public sealed class ViveProEyePoll
    {
        public ViveHeadFrame Head {get;private set;}
        public ViveEyeFrame Eye {get;private set;}
        public ViveEyeRuntimeState Runtime {get;private set;}
        public ViveEyeCalibrationState Calibration {get;private set;}
        public bool EyeDeviceConnected {get;private set;}
        public int? NativeResultCode {get;private set;}
        public int? NativeRuntimeStatus {get;private set;}
        public ViveProEyePoll(ViveHeadFrame head,ViveEyeFrame eye,ViveEyeRuntimeState runtime,ViveEyeCalibrationState calibration,
            bool eyeDeviceConnected,int? nativeResultCode=null,int? nativeRuntimeStatus=null)
        {Head=head;Eye=eye;Runtime=runtime;Calibration=calibration;EyeDeviceConnected=eyeDeviceConnected;NativeResultCode=nativeResultCode;NativeRuntimeStatus=nativeRuntimeStatus;}
    }

    public interface IViveProEyeProvider
    {
        // Both observation times must be read from the same IClock as the source,
        // immediately after each successful read. Recent queued observations are
        // permitted but keep their original observation times and sequences; dequeuing
        // them does not refresh their age. Cached eye data keeps its native sequence/time.
        // Missing head/eye is null; never substitute head-forward gaze.
        ViveProEyePoll Poll();
    }

    public interface IViveProEyeCoordinateMapper
    {
        string Version {get;}
        bool TryMapHead(Rotation rawTrackingRotation,out Rotation mappedHead);
        bool TryMapEye(Rotation rawTrackingRotation,Vec3 rawSdkDirection,out Vec3 mappedEye);
    }

    // A named engineering convention only: a unit ray direction is derived using
    // the installed SDK's Unity Ray convention, X is reflected, then the direction
    // is rotated into Unity tracking-local coordinates. Tracking-local is
    // assumed to coincide with avatar-root-local only in the dedicated rehearsal.
    // No eye origin/world position, anatomical calibration, recentering or apparatus
    // alignment is inferred. Raw vector/quaternion values are never modified;
    // the quaternion validity rule remains unchanged.
    public sealed class ViveProEyeRehearsalCoordinates : IViveProEyeCoordinateMapper
    {
        public const string MappingVersion="vive-sranipal-ray-normalized-x-reflection-tracking-to-avatar-rehearsal-v3-unvalidated";
        public string Version {get{return MappingVersion;}}
        public bool TryMapHead(Rotation rawTrackingRotation,out Rotation mappedHead)
        {mappedHead=rawTrackingRotation;return rawTrackingRotation.IsUnit;}
        public bool TryMapEye(Rotation rawTrackingRotation,Vec3 rawSdkDirection,out Vec3 mappedEye)
        {
            mappedEye=new Vec3(Double.NaN,Double.NaN,Double.NaN);
            Vec3 sdkRayDirection;
            if(!rawTrackingRotation.IsUnit||!ViveProEyeSdkDirection.TryGetUnitDirection(rawSdkDirection,out sdkRayDirection))return false;
            double x=-sdkRayDirection.X,y=sdkRayDirection.Y,z=sdkRayDirection.Z;
            double qx=rawTrackingRotation.X,qy=rawTrackingRotation.Y,qz=rawTrackingRotation.Z,qw=rawTrackingRotation.W;
            // q * (v,0) * inverse(q) preserves vector length even when an accepted
            // finite-precision quaternion is not exactly unit length. The raw
            // quaternion and its validity gate remain unchanged.
            double ix=qw*x+qy*z-qz*y,iy=qw*y+qz*x-qx*z,iz=qw*z+qx*y-qy*x,iw=-qx*x-qy*y-qz*z;
            double normSquared=rawTrackingRotation.LengthSquared;
            mappedEye=new Vec3((ix*qw-iw*qx-iy*qz+iz*qy)/normSquared,
                (iy*qw-iw*qy-iz*qx+ix*qz)/normSquared,(iz*qw-iw*qz-ix*qy+iy*qx)/normSquared);
            return mappedEye.IsUnit;
        }
    }

    public sealed class ViveProEyeObservation
    {
        public ViveProEyePoll RawPoll {get;internal set;}
        public double PollStartedLocalSeconds {get;internal set;}
        public double PollCompletedLocalSeconds {get;internal set;}
        public ViveFrameDisposition HeadDisposition {get;internal set;}
        public ViveFrameDisposition EyeDisposition {get;internal set;}
        public bool HeadUsable {get;internal set;}
        public bool EyeUsable {get;internal set;}
        public string MappingVersion {get;internal set;}
        public bool MappingValidatedForApparatus {get{return false;}}
        public ViveTimestampBasis TimestampBasis {get{return ViveTimestampBasis.LocalPollObservation;}}
        public bool HardwareCaptureTimeVerified {get{return false;}}
        public HeadEyeSample Sample {get;internal set;}
        public string FailureReason {get;internal set;}
        // First eye-output blocking gate, even when the independent head is usable.
        // Diagnostic only: this does not relax freshness or retain another sample.
        public string EyeFailureReason {get;internal set;}
    }

    // In-memory diagnostic/rehearsal adapter. Instantiating this source never starts
    // an SDK, calibrates a device, opens consent, records a session, or changes the
    // apparatus gate. The caller owns provider lifetime and explicit start/stop.
    public sealed class ViveProEyeHeadEyeSource : IHeadEyeSource
    {
        readonly string sessionId,clockId,mappingVersion;
        readonly int blockIndex,segment;
        readonly IClock clock;
        readonly IViveProEyeProvider provider;
        readonly IViveProEyeCoordinateMapper mapper;
        readonly double maximumObservationAgeSeconds;
        bool clockFault,hasHead,hasEye;
        long lastHeadSequence,sequence;
        int lastEyeSequence,lastEyeTimestamp;
        double lastClock,lastHeadObservation,lastEyeObservation;
        public ControlSource Source {get{return ControlSource.LiveOperator;}}
        public StreamRole Role {get{return StreamRole.OperatorControl;}}
        public string StreamId {get;private set;}
        public string DeviceId {get{return "vive-pro-eye-no-serial";}}
        public bool MappingValidatedForApparatus {get{return false;}}
        public ViveProEyeObservation LastObservation {get;private set;}
        public long EmittedSamples {get;private set;}
        public long RejectedEyeFrames {get;private set;}
        public long RejectedHeadFrames {get;private set;}
        public bool Available
        {
            get
            {
                if(clockFault||LastObservation==null||LastObservation.Sample==null||(!LastObservation.HeadUsable&&!LastObservation.EyeUsable))return false;
                double now=clock.Now;
                return clock.ClockId==clockId&&Finite(now)&&now>=lastClock&&now-LastObservation.Sample.CaptureLocalSeconds<=maximumObservationAgeSeconds;
            }
        }
        public string UnavailableReason
        {get{return Available?null:clockFault?"Local observation clock failed; create a new source and stream epoch":LastObservation==null?"No observation has been read":LastObservation.FailureReason??"No fresh usable channel is available";}}

        public ViveProEyeHeadEyeSource(string sessionId,int blockIndex,int segment,string streamId,IClock clock,
            IViveProEyeProvider provider,IViveProEyeCoordinateMapper mapper,double maximumObservationAgeSeconds=.1)
        {
            Identity(sessionId,"sessionId");Identity(streamId,"streamId");
            if(!streamId.StartsWith("vive-pro-eye/operator/",StringComparison.Ordinal)||streamId.Length=="vive-pro-eye/operator/".Length)
                throw new ArgumentException("Explicit VIVE operator stream/epoch required","streamId");
            if(blockIndex<0||blockIndex>3||segment<0)throw new ArgumentOutOfRangeException("blockIdentity");
            if(clock==null)throw new ArgumentNullException("clock");
            if(provider==null)throw new ArgumentNullException("provider");
            if(mapper==null)throw new ArgumentNullException("mapper");
            if(!Finite(maximumObservationAgeSeconds)||maximumObservationAgeSeconds<=0)throw new ArgumentOutOfRangeException("maximumObservationAgeSeconds");
            Identity(clock.ClockId,"clock.ClockId");Identity(mapper.Version,"mapper.Version");
            double now=clock.Now;if(!Finite(now)||now<0)throw new ArgumentException("Invalid local observation clock","clock");
            this.sessionId=sessionId;this.blockIndex=blockIndex;this.segment=segment;StreamId=streamId;
            this.clock=clock;this.provider=provider;this.mapper=mapper;this.maximumObservationAgeSeconds=maximumObservationAgeSeconds;
            clockId=clock.ClockId;mappingVersion=mapper.Version;lastClock=now;
        }

        public bool TryRead(out HeadEyeSample sample)
        {
            sample=null;
            double start=clock.Now;
            var observation=new ViveProEyeObservation {PollStartedLocalSeconds=start,PollCompletedLocalSeconds=start,MappingVersion=mappingVersion};
            LastObservation=observation;
            if(clockFault||!ClockUsable(start))return ClockFailure(observation);
            ViveProEyePoll poll;
            try {poll=provider.Poll();}
            catch(Exception error)
            {
                // Do not copy arbitrary provider exception text (which can contain
                // serials, paths or other personal data) into diagnostic output.
                observation.FailureReason="Provider failure: "+error.GetType().Name;
                observation.EyeFailureReason=observation.FailureReason;
                double failedAt=clock.Now;observation.PollCompletedLocalSeconds=failedAt;
                if(!ClockUsable(failedAt)||failedAt<start)return ClockFailure(observation);
                lastClock=failedAt;return false;
            }
            double now=clock.Now;observation.RawPoll=poll;observation.PollCompletedLocalSeconds=now;
            if(!ClockUsable(now)||now<start)return ClockFailure(observation);
            lastClock=now;
            if(poll==null){observation.FailureReason=observation.EyeFailureReason="Provider returned no observation";return false;}
            observation.HeadDisposition=InspectHead(poll.Head,now);
            observation.EyeDisposition=InspectEye(poll.Eye,now);
            bool headNew=observation.HeadDisposition==ViveFrameDisposition.Accepted,eyeNew=observation.EyeDisposition==ViveFrameDisposition.Accepted;
            if(poll.Head!=null&&!headNew)RejectedHeadFrames++;
            if(poll.Eye!=null&&!eyeNew)RejectedEyeFrames++;
            Rotation head=new Rotation(Double.NaN,Double.NaN,Double.NaN,Double.NaN);
            Vec3 eye=new Vec3(Double.NaN,Double.NaN,Double.NaN);
            if(poll.Head!=null)head=poll.Head.RawTrackingRotation;
            bool validHead=headNew&&poll.Head.DeviceConnected&&poll.Head.RotationReportedValid&&head.IsUnit;
            Vec3 sdkRayDirection;
            bool validEye=eyeNew&&poll.Runtime==ViveEyeRuntimeState.Working&&poll.Calibration==ViveEyeCalibrationState.ReportedReady&&
                poll.EyeDeviceConnected&&(!poll.NativeResultCode.HasValue||poll.NativeResultCode.Value==0)&&!poll.Eye.NoUser&&
                poll.Eye.Combined!=null&&(poll.Eye.Combined.ValidityMask&2UL)!=0&&poll.Eye.Combined.DirectionReportedValid&&
                ViveProEyeSdkDirection.TryGetUnitDirection(poll.Eye.Combined.Direction,out sdkRayDirection);
            observation.EyeFailureReason=EyeBlockReason(poll,observation,validHead);
            try
            {
                if(mapper.Version!=mappingVersion)throw new InvalidOperationException("Mapping identity changed");
                if(validHead)observation.HeadUsable=mapper.TryMapHead(poll.Head.RawTrackingRotation,out head)&&head.IsUnit;
                // Mapping a new HMD-local gaze requires a fresh, valid current head
                // observation. Duplicate/stale head poses cannot masquerade as new.
                if(validHead&&validEye)observation.EyeUsable=mapper.TryMapEye(poll.Head.RawTrackingRotation,poll.Eye.Combined.Direction,out eye)&&eye.IsUnit;
            }
            catch(Exception error)
            {
                observation.HeadUsable=false;observation.EyeUsable=false;observation.FailureReason="Coordinate mapper failure: "+error.GetType().Name;
                if(observation.EyeFailureReason==null)observation.EyeFailureReason=observation.FailureReason;
            }
            if(!observation.EyeUsable&&observation.EyeFailureReason==null)
                observation.EyeFailureReason="Eye coordinate mapping did not produce a unit direction";
            if(!headNew&&!eyeNew)
            {observation.FailureReason="No new timely channel observation";return false;}
            // The legacy sample has one time field. It conservatively uses the oldest
            // included local observation, NOT the native eye timestamp or a verified
            // hardware capture time. Keep this companion record with diagnostic use;
            // this adapter is not wired to study transport or the acquisition journal.
            double observed=headNew?poll.Head.ObservedLocalSeconds:poll.Eye.ObservedLocalSeconds;
            if(eyeNew)observed=Math.Min(observed,poll.Eye.ObservedLocalSeconds);
            sample=new HeadEyeSample(sessionId,StreamId,DeviceId,clockId,blockIndex,segment,sequence,observed,now,Source,Role,
                head,eye,observation.HeadUsable,observation.EyeUsable);
            observation.Sample=sample;sequence=checked(sequence+1);EmittedSamples++;
            if(!observation.HeadUsable&&!observation.EyeUsable&&observation.FailureReason==null)
                observation.FailureReason="New observation has no usable channel; inspect raw runtime, calibration and tracking flags";
            return true;
        }

        ViveFrameDisposition InspectHead(ViveHeadFrame head,double now)
        {
            if(head==null)return ViveFrameDisposition.Missing;
            var time=InspectTime(head.ObservedLocalSeconds,now);if(time!=ViveFrameDisposition.Accepted)return time;
            if(head.ObservationSequence<0)return ViveFrameDisposition.InvalidSequence;
            if(hasHead&&head.ObservationSequence==lastHeadSequence)return ViveFrameDisposition.Duplicate;
            if(hasHead&&(head.ObservationSequence<lastHeadSequence||head.ObservedLocalSeconds<lastHeadObservation))return ViveFrameDisposition.OutOfOrder;
            hasHead=true;lastHeadSequence=head.ObservationSequence;lastHeadObservation=head.ObservedLocalSeconds;
            return ViveFrameDisposition.Accepted;
        }

        static string EyeBlockReason(ViveProEyePoll poll,ViveProEyeObservation observation,bool validHead)
        {
            if(observation.EyeDisposition!=ViveFrameDisposition.Accepted)return "Eye frame: "+observation.EyeDisposition;
            if(poll.Runtime!=ViveEyeRuntimeState.Working)return "Eye runtime: "+poll.Runtime;
            if(poll.Calibration!=ViveEyeCalibrationState.ReportedReady)return "Eye calibration: "+poll.Calibration;
            if(!poll.EyeDeviceConnected)return "Eye device is disconnected";
            if(poll.NativeResultCode.HasValue&&poll.NativeResultCode.Value!=0)return "Eye native result: "+poll.NativeResultCode.Value;
            if(poll.Eye.NoUser)return "HTC reports no available eye data";
            if(poll.Eye.Combined==null)return "Combined eye ray is missing";
            if((poll.Eye.Combined.ValidityMask&2UL)==0||!poll.Eye.Combined.DirectionReportedValid)return "Combined eye direction is not reported valid";
            Vec3 sdkRayDirection;
            var directionStatus=ViveProEyeSdkDirection.ConvertForRay(poll.Eye.Combined.Direction,out sdkRayDirection);
            if(directionStatus==ViveSdkDirectionStatus.Nonfinite)return "Combined eye direction has nonfinite components";
            if(directionStatus==ViveSdkDirectionStatus.Degenerate)return "Combined eye direction is zero or below the SDK ray magnitude threshold";
            if(directionStatus==ViveSdkDirectionStatus.MagnitudeOverflow)return "Combined eye direction magnitude is not finite";
            if(!validHead)
            {
                if(observation.HeadDisposition!=ViveFrameDisposition.Accepted)return "Current head frame: "+observation.HeadDisposition;
                if(!poll.Head.DeviceConnected)return "Head device is disconnected";
                if(!poll.Head.RotationReportedValid)return "Head rotation is not reported valid";
                return "Head rotation is nonfinite or nonunit";
            }
            return null;
        }

        ViveFrameDisposition InspectEye(ViveEyeFrame eye,double now)
        {
            if(eye==null)return ViveFrameDisposition.Missing;
            var time=InspectTime(eye.ObservedLocalSeconds,now);if(time!=ViveFrameDisposition.Accepted)return time;
            if(hasEye&&eye.FrameSequence==lastEyeSequence)return ViveFrameDisposition.Duplicate;
            // Native signed values are opaque: the initial baseline may be negative.
            // Counter/timestamp reset or signed wrap within a stream is not guessed
            // around. Restart the provider with a new source/stream epoch explicitly.
            if(hasEye&&(eye.FrameSequence<lastEyeSequence||eye.Timestamp<=lastEyeTimestamp||eye.ObservedLocalSeconds<lastEyeObservation))
                return ViveFrameDisposition.OutOfOrder;
            hasEye=true;lastEyeSequence=eye.FrameSequence;lastEyeTimestamp=eye.Timestamp;lastEyeObservation=eye.ObservedLocalSeconds;
            return ViveFrameDisposition.Accepted;
        }

        ViveFrameDisposition InspectTime(double observed,double now)
        {
            if(!Finite(observed)||observed<0||observed>now)return ViveFrameDisposition.InvalidObservationTime;
            return now-observed>maximumObservationAgeSeconds?ViveFrameDisposition.Stale:ViveFrameDisposition.Accepted;
        }
        bool ClockUsable(double now){return clock.ClockId==clockId&&Finite(now)&&now>=0&&now>=lastClock;}
        bool ClockFailure(ViveProEyeObservation observation)
        {clockFault=true;observation.FailureReason=observation.EyeFailureReason="Local observation clock failed; create a new source and stream epoch";return false;}
        static bool Finite(double value){return !Double.IsNaN(value)&&!Double.IsInfinity(value);}
        static void Identity(string value,string name){if(String.IsNullOrWhiteSpace(value))throw new ArgumentException("Explicit identity required",name);}
    }
}
