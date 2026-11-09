using System;
using AvatarStroop.Core;

namespace AvatarStroop.Runtime
{
    public enum VivePreviewMarkerState { Hidden, Fresh, Retained }

    // Display-only state for the diagnostic board. Retained is a previous point,
    // never a newly usable acquisition sample. No provider, transport or file I/O.
    public sealed class VivePreviewMarkerContinuity
    {
        public const double MaximumAgeSeconds=.1;
        bool hasNow,clockFault;
        double lastNow,originalEyeTime;
        int frameSequence,nativeTimestamp;
        ulong directionMask;
        Vec3 acceptedDirection;
        string streamId,clockId,mappingVersion;

        public VivePreviewMarkerState State {get;private set;}
        public Vec3? Point {get;private set;}
        public double AgeSeconds {get;private set;}

        public VivePreviewMarkerContinuity() {Reset();}

        public void Reset()
        {
            hasNow=false;clockFault=false;lastNow=0;
            Hide();
        }

        // projectedPoint is supplied only for a successfully projected new usable
        // eye frame. Duplicates cannot move the point or refresh its original age.
        public VivePreviewMarkerState Update(ViveProEyeObservation observation,double now,bool headReady,Vec3? projectedPoint)
        {
            if(clockFault||!Finite(now)||now<0||(hasNow&&now<lastNow))
            {clockFault=true;return Hide();}
            hasNow=true;lastNow=now;
            if(!CurrentGatesUsable(observation,now,headReady))return Hide();
            var eye=observation.RawPoll.Eye;
            var sample=observation.Sample;
            if(observation.EyeDisposition==ViveFrameDisposition.Accepted)
            {
                if(!observation.EyeUsable||!sample.EyeValid||!projectedPoint.HasValue||!projectedPoint.Value.IsFinite)return Hide();
                Point=projectedPoint;originalEyeTime=eye.ObservedLocalSeconds;
                frameSequence=eye.FrameSequence;nativeTimestamp=eye.Timestamp;
                directionMask=eye.Combined.ValidityMask;acceptedDirection=eye.Combined.Direction;
                streamId=sample.StreamId;clockId=sample.ClockId;mappingVersion=observation.MappingVersion;
                AgeSeconds=now-originalEyeTime;State=VivePreviewMarkerState.Fresh;return State;
            }
            if(observation.EyeDisposition!=ViveFrameDisposition.Duplicate||!Point.HasValue||
                sample.StreamId!=streamId||sample.ClockId!=clockId||observation.MappingVersion!=mappingVersion||
                eye.FrameSequence!=frameSequence||eye.Timestamp!=nativeTimestamp||eye.ObservedLocalSeconds<originalEyeTime||
                eye.Combined.ValidityMask!=directionMask||!Same(eye.Combined.Direction,acceptedDirection)||
                !Recent(originalEyeTime,now))return Hide();
            AgeSeconds=now-originalEyeTime;State=VivePreviewMarkerState.Retained;return State;
        }

        static bool CurrentGatesUsable(ViveProEyeObservation observation,double now,bool headReady)
        {
            if(!headReady||observation==null||observation.RawPoll==null||observation.Sample==null||
                observation.HeadDisposition!=ViveFrameDisposition.Accepted||!observation.HeadUsable||!observation.Sample.HeadValid)return false;
            var poll=observation.RawPoll;var head=poll.Head;var eye=poll.Eye;
            Vec3 sdkRayDirection;
            // Check raw gates even on duplicates: the source's failure reason gives
            // duplicate precedence over other faults and is not a display permit.
            return head!=null&&head.DeviceConnected&&head.RotationReportedValid&&head.RawTrackingRotation.IsUnit&&Recent(head.ObservedLocalSeconds,now)&&
                eye!=null&&Recent(eye.ObservedLocalSeconds,now)&&poll.Runtime==ViveEyeRuntimeState.Working&&
                poll.Calibration==ViveEyeCalibrationState.ReportedReady&&poll.EyeDeviceConnected&&
                (!poll.NativeResultCode.HasValue||poll.NativeResultCode.Value==0)&&!eye.NoUser&&eye.Combined!=null&&
                (eye.Combined.ValidityMask&2UL)!=0&&eye.Combined.DirectionReportedValid&&
                ViveProEyeSdkDirection.TryGetUnitDirection(eye.Combined.Direction,out sdkRayDirection);
        }

        VivePreviewMarkerState Hide()
        {
            Point=null;AgeSeconds=Double.NaN;State=VivePreviewMarkerState.Hidden;
            originalEyeTime=Double.NaN;frameSequence=nativeTimestamp=0;directionMask=0;acceptedDirection=new Vec3();
            streamId=clockId=mappingVersion=null;return State;
        }
        static bool Same(Vec3 left,Vec3 right) {return left.X==right.X&&left.Y==right.Y&&left.Z==right.Z;}
        static bool Recent(double observed,double now) {return Finite(observed)&&observed>=0&&observed<=now&&now-observed<=MaximumAgeSeconds;}
        static bool Finite(double value) {return !Double.IsNaN(value)&&!Double.IsInfinity(value);}
    }
}
