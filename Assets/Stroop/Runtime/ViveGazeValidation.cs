using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using AvatarStroop.Core;

namespace AvatarStroop.Runtime
{
    public sealed class ViveGazeValidationTarget
    {
        public string Id {get;private set;}
        public string ExpectedAoi {get;private set;}
        public string Pass {get;private set;}
        public Vec3? InitialPositionWorld {get;private set;}
        public ViveGazeValidationTarget(string id,string expectedAoi,string pass,Vec3? initialPositionWorld=null)
        {
            if(String.IsNullOrWhiteSpace(id)||String.IsNullOrWhiteSpace(pass))throw new ArgumentException("Target and pass identities are required.");
            if(expectedAoi!="task"&&expectedAoi!="eyes"&&expectedAoi!="remaining_face"&&expectedAoi!="other")throw new ArgumentException("Unknown expected AOI.");
            if(initialPositionWorld.HasValue&&!initialPositionWorld.Value.IsFinite)throw new ArgumentException("Initial target position must be finite.");
            Id=id;ExpectedAoi=expectedAoi;Pass=pass;InitialPositionWorld=initialPositionWorld;
        }
    }

    // Render geometry and the later camera-pose observation have distinct times.
    // Neither is a measurement of panel scanout or native sensor exposure.
    public sealed class ViveGazeValidationSnapshot
    {
        public string TargetId {get;private set;}
        public string Pass {get;private set;}
        public double RenderStartedLocalSeconds {get;private set;}
        public double RenderCompletedLocalSeconds {get;private set;}
        public int RenderUnityFrame {get;private set;}
        public double PoseLocalSeconds {get;private set;}
        public Vec3 TargetPositionWorld {get;private set;}
        public Vec3 CameraPositionWorld {get;private set;}
        public Rotation CameraRotationWorld {get;private set;}
        public Vec3 WorldEyeOrigin {get;private set;}
        public Vec3 WorldEyeDirection {get;private set;}
        public string AoiCategory {get;private set;}
        public string AoiReason {get;private set;}
        public bool PoseValid {get;private set;}
        public IReadOnlyList<ViveEyeAoiGeometry> Geometry {get;private set;}
        public ViveGazeValidationSnapshot(string targetId,string pass,double renderStartedLocalSeconds,double renderCompletedLocalSeconds,
            int renderUnityFrame,double poseLocalSeconds,Vec3 targetPositionWorld,Vec3 cameraPositionWorld,Rotation cameraRotationWorld,
            Vec3 worldEyeOrigin,Vec3 worldEyeDirection,string aoiCategory,string aoiReason,bool poseValid,
            IEnumerable<ViveEyeAoiGeometry> geometry=null)
        {
            TargetId=targetId;Pass=pass;RenderStartedLocalSeconds=renderStartedLocalSeconds;RenderCompletedLocalSeconds=renderCompletedLocalSeconds;
            RenderUnityFrame=renderUnityFrame;PoseLocalSeconds=poseLocalSeconds;TargetPositionWorld=targetPositionWorld;
            CameraPositionWorld=cameraPositionWorld;CameraRotationWorld=cameraRotationWorld;WorldEyeOrigin=worldEyeOrigin;
            WorldEyeDirection=worldEyeDirection;AoiCategory=aoiCategory;AoiReason=aoiReason;PoseValid=poseValid;
            var copy=geometry==null?new List<ViveEyeAoiGeometry>():new List<ViveEyeAoiGeometry>(geometry);
            if(copy.Count>8||copy.Contains(null))throw new ArgumentException("At most eight non-null AOI geometry snapshots are supported.");
            Geometry=new ReadOnlyCollection<ViveEyeAoiGeometry>(copy);
        }
    }

    public sealed class ViveGazeValidationWindow
    {
        public int Sequence {get;internal set;}
        public ViveGazeValidationTarget Target {get;internal set;}
        public double OnsetLocalSeconds {get;internal set;}
        public int OnsetUnityFrame {get;internal set;}
        public double? OffsetLocalSeconds {get;internal set;}
    }

    public sealed class ViveGazeValidationMeasurement
    {
        public long Sequence {get;internal set;}
        public ViveRawEyeAcquisition Acquisition {get;internal set;}
        public ViveProEyePoll Poll {get;internal set;}
        public ViveGazeValidationSnapshot Snapshot {get;internal set;}
        public ViveGazeValidationWindow Window {get;internal set;}
        public bool RawCombinedValid {get;internal set;}
        public bool Settled {get;internal set;}
        public string GeometryExclusion {get;internal set;}
        public string ReadyExclusion {get;internal set;}
        public double? AngularErrorDegrees {get;internal set;}
        public double? YawErrorDegrees {get;internal set;}
        public double? PitchErrorDegrees {get;internal set;}
        public bool StableGeometryEligible {get{return Settled&&AngularErrorDegrees.HasValue;}}
        public bool StableReadyEligible {get{return StableGeometryEligible&&ReadyExclusion==null;}}
    }

    // Bounded, owner-thread session. No disk writes occur while samples arrive.
    // A target's first completed render opens its software-time interval; delayed
    // callbacks are matched to that history instead of the currently shown target.
    public sealed partial class ViveGazeValidationSession
    {
        public const string SchemaVersion="vive-gaze-validation-v1";
        public const int MaximumSamples=65536,MaximumTargets=256;
        public const double SettlingSeconds=.5,MaximumPoseSkewSeconds=.05,MaximumSceneAgeSeconds=.1;
        readonly List<ViveGazeValidationWindow> windows=new List<ViveGazeValidationWindow>();
        readonly List<ViveGazeValidationMeasurement> samples=new List<ViveGazeValidationMeasurement>();
        readonly HashSet<ulong> identities=new HashSet<ulong>();
        double lastEventTime=-1;
        bool hasNative;
        int lastNative;
        double? firstCallback,lastCallback;
        public string SessionId {get;private set;}
        public bool Finished {get;private set;}
        public bool Completed {get;private set;}
        public string FinishReason {get;private set;}
        public double? FinishedLocalSeconds {get;private set;}
        public long DuplicateSamples {get;private set;}
        public long MissingEyePolls {get;private set;}
        public long NativeGapCount {get;private set;}
        public long NativeDiscontinuities {get;private set;}
        public long CallbackTimeDiscontinuities {get;private set;}
        public double MaximumCallbackGapSeconds {get;private set;}
        public double? CallbackArrivalRateHz {get{return samples.Count>1&&CallbackTimeDiscontinuities==0&&firstCallback.HasValue&&lastCallback>firstCallback?
            (samples.Count-1)/(lastCallback.Value-firstCallback.Value):(double?)null;}}
        public string CaptureOutcome {get;private set;}
        public int SampleCount {get{return samples.Count;}}
        public int TargetCount {get{return windows.Count;}}
        public IReadOnlyList<ViveGazeValidationMeasurement> Samples {get{return samples.AsReadOnly();}}
        public IReadOnlyList<ViveGazeValidationWindow> Targets {get{return windows.AsReadOnly();}}
        public ViveGazeValidationSession(string sessionId)
        {
            if(String.IsNullOrWhiteSpace(sessionId))throw new ArgumentException("Session identity is required.","sessionId");
            SessionId=sessionId;
        }
        public void BeginTarget(ViveGazeValidationTarget target,double renderCompletedOnset,int unityFrame)
        {
            EnsureActive();CheckEventTime(renderCompletedOnset);
            if(target==null)throw new ArgumentNullException("target");
            if(unityFrame<0)throw new ArgumentOutOfRangeException("unityFrame");
            if(windows.Count>0&&!windows[windows.Count-1].OffsetLocalSeconds.HasValue)throw new InvalidOperationException("End the previous target first.");
            if(windows.Count>=MaximumTargets)throw new InvalidOperationException("Validation target budget exhausted.");
            windows.Add(new ViveGazeValidationWindow {Sequence=windows.Count,Target=target,OnsetLocalSeconds=renderCompletedOnset,OnsetUnityFrame=unityFrame});
            lastEventTime=renderCompletedOnset;
        }
        public void EndTarget(double offsetLocalSeconds)
        {
            EnsureActive();CheckEventTime(offsetLocalSeconds);
            if(windows.Count==0||windows[windows.Count-1].OffsetLocalSeconds.HasValue)throw new InvalidOperationException("There is no open target.");
            windows[windows.Count-1].OffsetLocalSeconds=offsetLocalSeconds;lastEventTime=offsetLocalSeconds;
        }
        public bool Observe(ViveProEyePoll poll,ViveGazeValidationSnapshot snapshot)
        {return ObserveCore(poll,null,snapshot);}
        public bool Observe(ViveRawEyeAcquisition acquisition,ViveGazeValidationSnapshot snapshot)
        {
            if(acquisition==null)throw new ArgumentNullException("acquisition");
            return ObserveCore(acquisition.Poll,acquisition,snapshot);
        }
        bool ObserveCore(ViveProEyePoll poll,ViveRawEyeAcquisition acquisition,ViveGazeValidationSnapshot snapshot)
        {
            EnsureActive();
            if(poll==null||poll.Eye==null){MissingEyePolls++;return false;}
            var eye=poll.Eye;
            ulong identity=((ulong)(uint)eye.FrameSequence<<32)|(uint)eye.Timestamp;
            if(identities.Contains(identity)){DuplicateSamples++;return false;}
            if(samples.Count>=MaximumSamples)throw new InvalidOperationException("Validation sample budget exhausted; stop and export an incomplete session.");
            identities.Add(identity);
            if(hasNative)
            {
                long delta=(long)eye.FrameSequence-lastNative;
                if(delta>1)NativeGapCount+=delta-1;else if(delta<=0)NativeDiscontinuities++;
            }
            hasNative=true;lastNative=eye.FrameSequence;
            if(!Finite(eye.ObservedLocalSeconds)||eye.ObservedLocalSeconds<0)CallbackTimeDiscontinuities++;
            else
            {
                if(lastCallback.HasValue)
                {
                    double delta=eye.ObservedLocalSeconds-lastCallback.Value;
                    if(delta<=0)CallbackTimeDiscontinuities++;else MaximumCallbackGapSeconds=Math.Max(MaximumCallbackGapSeconds,delta);
                }
                else firstCallback=eye.ObservedLocalSeconds;
                lastCallback=eye.ObservedLocalSeconds;
            }
            var m=new ViveGazeValidationMeasurement {Sequence=samples.Count,Acquisition=acquisition,Poll=poll,Snapshot=snapshot};
            if(Finite(eye.ObservedLocalSeconds)&&eye.ObservedLocalSeconds>=0)
                for(int i=windows.Count-1;i>=0;i--)
                {
                    var w=windows[i];
                    if(eye.ObservedLocalSeconds>=w.OnsetLocalSeconds&&(!w.OffsetLocalSeconds.HasValue||eye.ObservedLocalSeconds<w.OffsetLocalSeconds.Value))
                    {m.Window=w;break;}
                }
            m.Settled=m.Window!=null&&eye.ObservedLocalSeconds-m.Window.OnsetLocalSeconds>=SettlingSeconds;
            Vec3 unit;
            m.RawCombinedValid=!eye.NoUser&&eye.Combined!=null&&(eye.Combined.ValidityMask&3UL)==3UL&&
                eye.Combined.OriginReportedValid&&eye.Combined.DirectionReportedValid&&eye.Combined.OriginMillimeters.IsFinite&&
                ViveProEyeSdkDirection.TryGetUnitDirection(eye.Combined.Direction,out unit);
            m.GeometryExclusion=GeometryReason(m);
            if(m.GeometryExclusion==null)
            {
                double angle,yaw,pitch;
                if(TryAngularError(snapshot.WorldEyeOrigin,snapshot.WorldEyeDirection,snapshot.TargetPositionWorld,out angle,out yaw,out pitch))
                {m.AngularErrorDegrees=angle;m.YawErrorDegrees=yaw;m.PitchErrorDegrees=pitch;}
                else m.GeometryExclusion="invalid_target_or_world_ray";
            }
            m.ReadyExclusion=ReadyReason(poll);
            samples.Add(m);return true;
        }
        static string GeometryReason(ViveGazeValidationMeasurement m)
        {
            var eye=m.Poll.Eye;var s=m.Snapshot;
            if(!Finite(eye.ObservedLocalSeconds)||eye.ObservedLocalSeconds<0)return "invalid_callback_time";
            if(m.Window==null)return "outside_target_interval";
            if(!m.RawCombinedValid)return "invalid_combined_ray";
            if(s==null)return "no_render_pose_snapshot";
            if(s.TargetId!=m.Window.Target.Id||s.Pass!=m.Window.Target.Pass)return "snapshot_target_mismatch";
            if(!Finite(s.RenderStartedLocalSeconds)||!Finite(s.RenderCompletedLocalSeconds)||!Finite(s.PoseLocalSeconds)||
                s.RenderStartedLocalSeconds<0||s.RenderCompletedLocalSeconds<s.RenderStartedLocalSeconds||s.PoseLocalSeconds<s.RenderCompletedLocalSeconds||
                s.RenderCompletedLocalSeconds<m.Window.OnsetLocalSeconds||s.RenderUnityFrame<m.Window.OnsetUnityFrame)return "invalid_render_pose_clock";
            if(s.PoseLocalSeconds-s.RenderCompletedLocalSeconds>MaximumSceneAgeSeconds)return "stale_render_scene";
            if(s.RenderCompletedLocalSeconds>eye.ObservedLocalSeconds)return "render_after_callback";
            if(s.PoseLocalSeconds<eye.ObservedLocalSeconds||s.PoseLocalSeconds-eye.ObservedLocalSeconds>MaximumPoseSkewSeconds)return "pose_callback_skew";
            if(!s.PoseValid||!s.CameraPositionWorld.IsFinite||!s.CameraRotationWorld.IsUnit)return "invalid_head_pose";
            if(s.AoiCategory!="task"&&s.AoiCategory!="eyes"&&s.AoiCategory!="remaining_face"&&s.AoiCategory!="other")return "invalid_aoi_snapshot";
            if(s.AoiReason!="valid_surface_proxy")return "invalid_aoi_snapshot";
            return null;
        }
        static string ReadyReason(ViveProEyePoll poll)
        {
            if(poll.Runtime!=ViveEyeRuntimeState.Working)return "runtime_not_working";
            if(!poll.EyeDeviceConnected)return "eye_device_disconnected";
            if(poll.NativeResultCode!=0)return "native_result_not_success";
            if(poll.Calibration!=ViveEyeCalibrationState.ReportedReady)return poll.Calibration==ViveEyeCalibrationState.Required?"calibration_required":"calibration_unknown";
            var n=poll.NativeDiagnostics;
            if(n==null||!n.HealthQueryCompletedLocalSeconds.HasValue)return "no_health_query_time";
            double age=poll.Eye.ObservedLocalSeconds-n.HealthQueryCompletedLocalSeconds.Value;
            if(!Finite(age)||age<0||age>ViveNativePollDiagnostics.MaximumCachedHealthAgeSeconds)return "health_query_not_fresh_at_callback";
            return null;
        }
        public void Finish(double localSeconds,bool completed,string reason)
        {
            EnsureActive();CheckEventTime(localSeconds);
            if(String.IsNullOrWhiteSpace(reason))throw new ArgumentException("An explicit completion or abort reason is required.");
            if(windows.Count>0&&!windows[windows.Count-1].OffsetLocalSeconds.HasValue)EndTarget(localSeconds);
            Finished=true;Completed=completed;FinishReason=reason;FinishedLocalSeconds=localSeconds;lastEventTime=localSeconds;
        }
        public void SetCaptureOutcome(string outcome)
        {CaptureOutcome=outcome;}
        void EnsureActive(){if(Finished)throw new InvalidOperationException("Validation session has ended.");}
        void CheckEventTime(double t)
        {if(!Finite(t)||t<0||t<lastEventTime)throw new ArgumentOutOfRangeException("localSeconds","Event clock must be finite and monotonic.");}
        internal static bool Finite(double n){return !Double.IsNaN(n)&&!Double.IsInfinity(n);}
        static bool Unit(Vec3 v,out Vec3 u)
        {
            u=new Vec3(Double.NaN,Double.NaN,Double.NaN);if(!v.IsFinite)return false;
            double scale=Math.Max(Math.Abs(v.X),Math.Max(Math.Abs(v.Y),Math.Abs(v.Z)));if(scale<=0)return false;
            double x=v.X/scale,y=v.Y/scale,z=v.Z/scale,length=Math.Sqrt(x*x+y*y+z*z);
            u=new Vec3(x/length,y/length,z/length);return u.IsUnit;
        }
        public static bool TryAngularError(Vec3 origin,Vec3 direction,Vec3 target,out double angle,out double yaw,out double pitch)
        {
            angle=yaw=pitch=Double.NaN;Vec3 gaze,expected;
            if(!origin.IsFinite||!target.IsFinite||!Unit(direction,out gaze)||
                !Unit(new Vec3(target.X-origin.X,target.Y-origin.Y,target.Z-origin.Z),out expected))return false;
            double dot=Math.Max(-1,Math.Min(1,gaze.X*expected.X+gaze.Y*expected.Y+gaze.Z*expected.Z));
            angle=Math.Acos(dot)*180/Math.PI;
            yaw=(Math.Atan2(gaze.X,gaze.Z)-Math.Atan2(expected.X,expected.Z))*180/Math.PI;
            if(yaw>180)yaw-=360;if(yaw< -180)yaw+=360;
            pitch=(Math.Asin(Math.Max(-1,Math.Min(1,gaze.Y)))-Math.Asin(Math.Max(-1,Math.Min(1,expected.Y))))*180/Math.PI;
            return Finite(angle)&&Finite(yaw)&&Finite(pitch);
        }
    }
}
