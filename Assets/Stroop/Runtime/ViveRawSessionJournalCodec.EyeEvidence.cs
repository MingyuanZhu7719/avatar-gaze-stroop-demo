using System;
using System.Collections.Generic;
using System.IO;
using AvatarStroop.Core;

namespace AvatarStroop.Runtime
{
    internal static partial class ViveRawSessionJournalCodec
    {
        // Additive VRAW3+ kind 6 and kind-2 extension version 1. Updated readers
        // accept older files; older readers reject these new records/extensions.
        // Acquisitions preserve invalid SDK data independently of task admission.
        internal static bool ValidAcquisition(ViveRawEyeAcquisition value)
        {
            if(value==null||value.Sequence<0||value.Sequence==Int64.MaxValue||!LocalTime(value.WorkerPublishedLocalSeconds)||value.Poll==null||value.Poll.Eye==null||
                value.EligiblePolls<value.Sequence+1||value.DuplicatePolls<0||value.DuplicatePolls>value.EligiblePolls-(value.Sequence+1)||
                value.NativeSequenceGapCount<0||value.NativeSequenceDiscontinuities<0)return false;
            var p=value.Poll;var e=p.Eye;var d=p.NativeDiagnostics;
            if(!Enum.IsDefined(typeof(ViveEyeRuntimeState),p.Runtime)||!Enum.IsDefined(typeof(ViveEyeCalibrationState),p.Calibration)||
                !LocalTime(e.ObservedLocalSeconds)||e.ObservedLocalSeconds>value.WorkerPublishedLocalSeconds)return false;
            if(d==null)return true; // Older/synthetic provider has no timing telemetry.
            if(d.ProviderPollSequence<0||!LocalTime(d.PollStartedLocalSeconds)||!LocalTime(d.PollReturnedLocalSeconds)||
                d.PollStartedLocalSeconds>d.PollReturnedLocalSeconds||d.PollReturnedLocalSeconds>value.WorkerPublishedLocalSeconds||
                e.ObservedLocalSeconds<d.PollStartedLocalSeconds||e.ObservedLocalSeconds>d.PollReturnedLocalSeconds)return false;
            double previous=d.PollStartedLocalSeconds;
            foreach(double? at in new[]{d.StatusReturnedLocalSeconds,d.DeviceReturnedLocalSeconds,d.CalibrationReturnedLocalSeconds,d.ReadStartedLocalSeconds,d.ReadReturnedLocalSeconds})
            {if(at.HasValue){if(!LocalTime(at.Value)||at.Value<previous||at.Value>d.PollReturnedLocalSeconds)return false;previous=at.Value;}}
            return true;
        }
        internal static byte[] AcquisitionPayload(long ordinal,ViveRawEyeAcquisition value)
        {
            if(ordinal<1||!ValidAcquisition(value))throw new InvalidOperationException("Invalid native acquisition evidence.");
            return Build(w=>{
                var p=value.Poll;var e=p.Eye;w.Write(ordinal);w.Write((byte)6);w.Write(1);
                w.Write(value.Sequence);w.Write(value.WorkerPublishedLocalSeconds);w.Write(value.EligiblePolls);w.Write(value.DuplicatePolls);
                w.Write(value.NativeSequenceGapCount);w.Write(value.NativeSequenceDiscontinuities);
                w.Write(e.FrameSequence);w.Write(e.Timestamp);w.Write(e.ObservedLocalSeconds);w.Write((int)p.Runtime);w.Write((int)p.Calibration);
                Bool(w,p.EyeDeviceConnected);OptionalInt(w,p.NativeResultCode);OptionalInt(w,p.NativeRuntimeStatus);Bool(w,e.NoUser);
                NativeRay(w,e.Left);NativeRay(w,e.Right);NativeRay(w,e.Combined);WritePollDiagnostics(w,p.NativeDiagnostics);
            });
        }
        static void WritePollDiagnostics(BinaryWriter w,ViveNativePollDiagnostics d)
        {
            Bool(w,d!=null);if(d==null)return;
            w.Write(d.ProviderPollSequence);w.Write(d.PollStartedLocalSeconds);w.Write(d.PollReturnedLocalSeconds);
            OptionalDouble(w,d.StatusReturnedLocalSeconds);OptionalDouble(w,d.DeviceReturnedLocalSeconds);OptionalDouble(w,d.CalibrationReturnedLocalSeconds);
            OptionalDouble(w,d.ReadStartedLocalSeconds);OptionalDouble(w,d.ReadReturnedLocalSeconds);OptionalInt(w,d.StatusResult);
            OptionalInt(w,d.CalibrationResult);OptionalBool(w,d.CalibrationNeeded);OptionalInt(w,d.ReadResult);
        }
        static void NativeRay(BinaryWriter w,ViveEyeRay ray)
        {Ray(w,ray==null?null:new ViveNamedSessionFrameAdmission.RawEyeRay(ray));}
        static void OptionalDouble(BinaryWriter w,double? value){Bool(w,value.HasValue);if(value.HasValue)w.Write(value.Value);}
        static void Vector(BinaryWriter w,Vec3 value){w.Write(value.X);w.Write(value.Y);w.Write(value.Z);}
        static void Quaternion(BinaryWriter w,Rotation value){w.Write(value.X);w.Write(value.Y);w.Write(value.Z);w.Write(value.W);}
        static bool LocalTime(double value){return Finite(value)&&value>=0;}
        internal static bool ValidAoi(ViveEyeAoiObservation a,int nativeFrame,int nativeTimestamp,double eyeObserved,double checkedAt)
        {
            if(a==null||!LocalTime(a.CaptureLocalSeconds)||a.CaptureLocalSeconds<eyeObserved||a.CaptureLocalSeconds>checkedAt||
                (a.CaptureCompletedLocalSeconds.HasValue&&(!LocalTime(a.CaptureCompletedLocalSeconds.Value)||a.CaptureCompletedLocalSeconds.Value<a.CaptureLocalSeconds||a.CaptureCompletedLocalSeconds.Value>checkedAt))||
                a.NativeFrame!=nativeFrame||a.NativeTimestamp!=nativeTimestamp||a.EyeObservedLocalSeconds!=eyeObserved||
                !LocalTime(a.UnitySeconds)||a.UnityFrame<0||a.TaskRevision< -1||a.AvatarRevision< -1||
                a.SceneUnityFrame< -1||a.SceneUnityFrame>a.UnityFrame||
                (a.SceneCaptureLocalSeconds.HasValue&&(!LocalTime(a.SceneCaptureLocalSeconds.Value)||a.SceneCaptureLocalSeconds.Value>a.CaptureLocalSeconds))||
                (a.SceneCaptureCompletedLocalSeconds.HasValue&&(!a.SceneCaptureLocalSeconds.HasValue||!LocalTime(a.SceneCaptureCompletedLocalSeconds.Value)||
                    a.SceneCaptureCompletedLocalSeconds.Value<a.SceneCaptureLocalSeconds.Value||a.SceneCaptureCompletedLocalSeconds.Value>a.CaptureLocalSeconds))||
                (a.SceneUnitySeconds.HasValue&&(!LocalTime(a.SceneUnitySeconds.Value)||a.SceneUnitySeconds.Value>a.UnitySeconds))||
                a.LastRenderFrame< -1||a.LastRenderFrame>a.UnityFrame||a.LastRenderTaskRevision< -1||a.LastRenderAvatarRevision< -1||
                (a.LastRenderUnitySeconds.HasValue&&(!LocalTime(a.LastRenderUnitySeconds.Value)||a.LastRenderUnitySeconds.Value>a.UnitySeconds))||
                !Enum.IsDefined(typeof(Phase),a.Phase)||a.Block< -1||a.Block>3||
                String.IsNullOrWhiteSpace(a.Reason)||Utf8.GetByteCount(a.Reason)>256||
                (a.Trial!=null&&Utf8.GetByteCount(a.Trial)>32768)||a.Geometry==null||a.Geometry.Count>8)return false;
            if(a.Category!="task"&&a.Category!="eyes"&&a.Category!="remaining_face"&&a.Category!="other"&&a.Category!="invalid"&&a.Category!="outside_task")return false;
            if((a.Category=="eyes"||a.Category=="remaining_face")&&!a.AvatarAoiApplicable)return false;
            if(a.Category!="invalid"&&a.Category!="outside_task"&&(!a.ActiveTask||!a.OriginValid||!a.DirectionValid))return false;
            if(a.OriginValid&&!a.WorldEyeOrigin.IsFinite||a.DirectionValid&&!a.WorldEyeDirection.IsUnit)return false;
            var names=new HashSet<string>(StringComparer.Ordinal);
            foreach(var g in a.Geometry)
            {
                if(g==null||String.IsNullOrWhiteSpace(g.Name)||Utf8.GetByteCount(g.Name)>32||!names.Add(g.Name))return false;
                // Disabled/absent primitives may explicitly carry NaN. Keep
                // those bits instead of treating a missing AOI as task failure.
                if(g.Enabled&&a.Category!="invalid"&&a.Category!="outside_task"&&(!g.PositionWorld.IsFinite||!g.RotationWorld.IsUnit||!g.ScaleWorld.IsFinite||
                    g.ScaleWorld.X<=0||g.ScaleWorld.Y<=0||g.ScaleWorld.Z<=0))return false;
            }
            return true;
        }
        static void WriteAoi(BinaryWriter w,ViveEyeAoiObservation a,int nativeFrame,int nativeTimestamp,double eyeObserved,double checkedAt)
        {
            if(!ValidAoi(a,nativeFrame,nativeTimestamp,eyeObserved,checkedAt))throw new InvalidOperationException("Invalid AOI software association.");
            w.Write(1);Text(w,ViveEyeAoiObservation.PolicyVersion);Text(w,ViveEyeAoiObservation.CoordinateBasis);
            Text(w,ViveEyeAoiObservation.PoseTimeMeaning);Text(w,ViveEyeAoiObservation.ClassificationMeaning);
            w.Write(a.CaptureLocalSeconds);OptionalDouble(w,a.CaptureCompletedLocalSeconds);w.Write(a.UnitySeconds);w.Write(a.UnityFrame);
            OptionalDouble(w,a.SceneCaptureLocalSeconds);OptionalDouble(w,a.SceneCaptureCompletedLocalSeconds);OptionalDouble(w,a.SceneUnitySeconds);w.Write(a.SceneUnityFrame);
            w.Write(a.TaskRevision);w.Write(a.AvatarRevision);
            w.Write(a.LastRenderFrame);OptionalDouble(w,a.LastRenderUnitySeconds);w.Write(a.LastRenderTaskRevision);w.Write(a.LastRenderAvatarRevision);
            w.Write((int)a.Phase);w.Write(a.Block);Text(w,a.Trial);Text(w,a.Category);Text(w,a.Reason);
            Bool(w,a.ActiveTask);Bool(w,a.AvatarAoiApplicable);Bool(w,a.OriginValid);Bool(w,a.DirectionValid);
            Vector(w,a.CameraPositionWorld);Quaternion(w,a.CameraRotationWorld);Vector(w,a.WorldEyeOrigin);Vector(w,a.WorldEyeDirection);
            w.Write(a.Geometry.Count);
            foreach(var g in a.Geometry){Text(w,g.Name);Bool(w,g.Enabled);Vector(w,g.PositionWorld);Quaternion(w,g.RotationWorld);Vector(w,g.ScaleWorld);}
        }
    }
}
