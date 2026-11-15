using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using AvatarStroop.Core;

namespace AvatarStroop.Runtime
{
    // A snapshot of one explicitly defined analytic primitive, not a collider or
    // a claim that this region was visible through the headset lenses.
    public sealed class ViveEyeAoiGeometry
    {
        public string Name {get;private set;}
        public bool Enabled {get;private set;}
        public Vec3 PositionWorld {get;private set;}
        public Rotation RotationWorld {get;private set;}
        public Vec3 ScaleWorld {get;private set;}
        public ViveEyeAoiGeometry(string name,bool enabled,Vec3 positionWorld,Rotation rotationWorld,Vec3 scaleWorld)
        {Name=name;Enabled=enabled;PositionWorld=positionWorld;RotationWorld=rotationWorld;ScaleWorld=scaleWorld;}
    }

    // Optional software-time association for an admitted eye frame. Acquisition
    // samples without this attachment have no measured world-pose/AOI association.
    // No interpolation, native-clock alignment or quantitative validation is implied.
    public sealed class ViveEyeAoiObservation
    {
        public const string PolicyVersion="vive-classroom-exclusive-render-surface-proxies-v2";
        public const string CoordinateBasis="unity-world-metres-sdk-x-reflected-origin-mm-to-m";
        public const string PoseTimeMeaning="current-unity-camera-pose-not-native-acquisition-pose";
        public const string ClassificationMeaning="last-completed-render-scene-analytic-surface-proxy-not-validated-fixation-or-full-scene-occlusion";
        public double CaptureLocalSeconds {get;private set;}
        public double? CaptureCompletedLocalSeconds {get;private set;}
        public double UnitySeconds {get;private set;}
        public int UnityFrame {get;private set;}
        public double? SceneCaptureLocalSeconds {get;private set;}
        public double? SceneCaptureCompletedLocalSeconds {get;private set;}
        public double? SceneUnitySeconds {get;private set;}
        public int SceneUnityFrame {get;private set;}
        public long TaskRevision {get;private set;}
        public long AvatarRevision {get;private set;}
        public int LastRenderFrame {get;private set;}
        public double? LastRenderUnitySeconds {get;private set;}
        public long LastRenderTaskRevision {get;private set;}
        public long LastRenderAvatarRevision {get;private set;}
        public Phase Phase {get;private set;}
        public int Block {get;private set;}
        public string Trial {get;private set;}
        public int NativeFrame {get;private set;}
        public int NativeTimestamp {get;private set;}
        public double EyeObservedLocalSeconds {get;private set;}
        public string Category {get;private set;}
        public string Reason {get;private set;}
        public bool ActiveTask {get;private set;}
        public bool AvatarAoiApplicable {get;private set;}
        public bool OriginValid {get;private set;}
        public bool DirectionValid {get;private set;}
        public Vec3 CameraPositionWorld {get;private set;}
        public Rotation CameraRotationWorld {get;private set;}
        public Vec3 WorldEyeOrigin {get;private set;}
        public Vec3 WorldEyeDirection {get;private set;}
        public IReadOnlyList<ViveEyeAoiGeometry> Geometry {get;private set;}
        public ViveEyeAoiObservation(double captureLocalSeconds,double? captureCompletedLocalSeconds,double unitySeconds,int unityFrame,
            double? sceneCaptureLocalSeconds,double? sceneCaptureCompletedLocalSeconds,double? sceneUnitySeconds,int sceneUnityFrame,long taskRevision,long avatarRevision,
            int lastRenderFrame,double? lastRenderUnitySeconds,long lastRenderTaskRevision,long lastRenderAvatarRevision,
            Phase phase,int block,string trial,int nativeFrame,int nativeTimestamp,
            double eyeObservedLocalSeconds,string category,string reason,bool activeTask,bool avatarAoiApplicable,
            bool originValid,bool directionValid,Vec3 cameraPositionWorld,Rotation cameraRotationWorld,Vec3 worldEyeOrigin,
            Vec3 worldEyeDirection,IEnumerable<ViveEyeAoiGeometry> geometry)
        {
            if(geometry==null)throw new ArgumentNullException("geometry");
            var copy=new List<ViveEyeAoiGeometry>(geometry);
            if(copy.Count>8||copy.Contains(null))throw new ArgumentException("At most eight non-null AOI geometry snapshots are supported.","geometry");
            CaptureLocalSeconds=captureLocalSeconds;CaptureCompletedLocalSeconds=captureCompletedLocalSeconds;
            UnitySeconds=unitySeconds;UnityFrame=unityFrame;TaskRevision=taskRevision;AvatarRevision=avatarRevision;
            SceneCaptureLocalSeconds=sceneCaptureLocalSeconds;SceneCaptureCompletedLocalSeconds=sceneCaptureCompletedLocalSeconds;
            SceneUnitySeconds=sceneUnitySeconds;SceneUnityFrame=sceneUnityFrame;
            LastRenderFrame=lastRenderFrame;LastRenderUnitySeconds=lastRenderUnitySeconds;
            LastRenderTaskRevision=lastRenderTaskRevision;LastRenderAvatarRevision=lastRenderAvatarRevision;Phase=phase;Block=block;Trial=trial;
            NativeFrame=nativeFrame;NativeTimestamp=nativeTimestamp;EyeObservedLocalSeconds=eyeObservedLocalSeconds;Category=category;Reason=reason;
            ActiveTask=activeTask;AvatarAoiApplicable=avatarAoiApplicable;OriginValid=originValid;DirectionValid=directionValid;
            CameraPositionWorld=cameraPositionWorld;CameraRotationWorld=cameraRotationWorld;WorldEyeOrigin=worldEyeOrigin;WorldEyeDirection=worldEyeDirection;
            Geometry=new ReadOnlyCollection<ViveEyeAoiGeometry>(copy);
        }
    }
}
