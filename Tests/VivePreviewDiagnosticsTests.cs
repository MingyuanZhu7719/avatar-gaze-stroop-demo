using System;
using System.Reflection;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    public static class VivePreviewDiagnosticsTests
    {
        static ViveProEyeObservation Observation(ViveFrameDisposition disposition=ViveFrameDisposition.Accepted,
            ViveEyeRuntimeState runtime=ViveEyeRuntimeState.Working,ViveEyeCalibrationState calibration=ViveEyeCalibrationState.ReportedReady,
            bool connected=true,int? code=0,bool absent=false,bool combinedMissing=false,bool directionValid=true,
            ulong mask=2,Vec3? direction=null,ViveHeadFrame head=null,ViveFrameDisposition headDisposition=ViveFrameDisposition.Accepted,
            bool headUsable=true,bool eyeUsable=false,bool headMissing=false)
        {
            var ray=combinedMissing?null:new ViveEyeRay(mask,false,directionValid,new Vec3(0,0,0),direction??new Vec3(0,0,1));
            var eye=new ViveEyeFrame(7,56,1,absent,null,null,ray);
            return new ViveProEyeObservation {
                RawPoll=new ViveProEyePoll(headMissing?null:head??new ViveHeadFrame(9,1,true,true,Rotation.Identity),eye,runtime,calibration,connected,code,2),
                EyeDisposition=disposition,HeadDisposition=headDisposition,HeadUsable=headUsable,EyeUsable=eyeUsable
            };
        }

        static long Rejected(VivePreviewDiagnostics d)
        {return d.RuntimeRejectedFrames+d.CalibrationRejectedFrames+d.DeviceRejectedFrames+d.NativeRejectedFrames+d.DataAbsentFrames+
            d.CombinedMissingFrames+d.CombinedInvalidFrames+d.DirectionNonfiniteFrames+d.DirectionDegenerateFrames+
            d.DirectionMagnitudeOverflowFrames+d.HeadBlockedFrames+d.MappingBlockedFrames;}
        static void Partitions(VivePreviewDiagnostics d)
        {
            Check.Equal(d.Observations,d.NewEyeFrames+d.MissingViews+d.DuplicateViews+d.StaleViews+d.OtherDispositionViews);
            Check.Equal(d.NewEyeFrames,Rejected(d)+d.UsableWithHeadFrames);
            Check.True(d.UsableOffBoardFrames<=d.UsableWithHeadFrames&&d.HealthyDuplicateViews<=d.DuplicateViews);
            Check.True(d.DirectionNormalizedFrames<=d.DirectionConvertibleFrames);
            Check.Equal(d.DirectionNonunitFrames,d.DirectionNormalizedFrames+d.DirectionNonfiniteFrames+
                d.DirectionDegenerateFrames+d.DirectionMagnitudeOverflowFrames);
        }
        static void Empty(VivePreviewDiagnostics d)
        {
            foreach(var property in typeof(VivePreviewDiagnostics).GetProperties())
                Check.Equal(0L,(long)property.GetValue(d,null),property.Name);
        }
        static void NativeCounts(VivePreviewDiagnostics d,long polls,long reads,long sequences,long timestamps)
        {
            Check.Equal(polls,d.NativePolls);Check.Equal(reads,d.SuccessfulEyeReads);
            Check.Equal(sequences,d.SequenceChanges);Check.Equal(timestamps,d.TimestampChanges);
        }

        public static void Register(TestSuite suite)
        {
            suite.Run("VIVE preview retains native totals after worker reap and resets them for a new run",()=>{
                var d=new VivePreviewDiagnostics();
                d.ObserveNative(new ViveWorkerCaptureDiagnostics(8,6,4,3));NativeCounts(d,8,6,4,3);
                d.ObserveNative(new ViveWorkerCaptureDiagnostics(12,9,6,5));NativeCounts(d,12,9,6,5);
                d.ObserveNative(new ViveWorkerCaptureDiagnostics(0,0,0,0));NativeCounts(d,12,9,6,5);
                d.ObserveNative(new ViveWorkerCaptureDiagnostics(8,6,4,3));NativeCounts(d,12,9,6,5);
                d.ObserveNative(null);NativeCounts(d,12,9,6,5);
                Check.Equal(0L,d.Observations);Partitions(d);
                d.Reset();Empty(d);
                d.ObserveNative(new ViveWorkerCaptureDiagnostics(0,0,0,0));NativeCounts(d,0,0,0,0);
                d.ObserveNative(new ViveWorkerCaptureDiagnostics(1,1,0,0));NativeCounts(d,1,1,0,0);
                d.Reset();Empty(d);
            });
            suite.Run("VIVE preview aggregate excludes duplicate stale and other views from new-frame cohorts",()=>{
                var d=new VivePreviewDiagnostics();
                d.Observe(Observation(eyeUsable:true),true,true);
                foreach(var disposition in new[]{ViveFrameDisposition.Duplicate,ViveFrameDisposition.Stale,ViveFrameDisposition.OutOfOrder,
                    ViveFrameDisposition.InvalidObservationTime,ViveFrameDisposition.InvalidSequence,ViveFrameDisposition.Missing})
                    d.Observe(Observation(disposition,eyeUsable:true),true,false);
                d.Observe(null,true,false);
                d.Observe(new ViveProEyeObservation {EyeDisposition=ViveFrameDisposition.Accepted},true,false);
                var missingEye=Observation();
                missingEye.RawPoll=new ViveProEyePoll(null,null,ViveEyeRuntimeState.Error,ViveEyeCalibrationState.Unknown,false,1036);
                d.Observe(missingEye,false,false);
                Check.Equal(10L,d.Observations);Check.Equal(1L,d.NewEyeFrames);Check.Equal(1L,d.NewEyeFramesWithHeadReady);
                Check.Equal(1L,d.CombinedMaskValidFrames);Check.Equal(1L,d.UsableEyeFrames);Check.Equal(1L,d.UsableWithHeadFrames);
                Check.Equal(1L,d.DuplicateViews);Check.Equal(1L,d.StaleViews);Check.Equal(3L,d.OtherDispositionViews);
                Check.Equal(4L,d.MissingViews);Check.Equal(0L,d.UsableOffBoardFrames);Check.Equal(0L,Rejected(d));Partitions(d);
            });
            suite.Run("VIVE preview aggregate assigns each accepted frame one first blocking reason",()=>{
                var d=new VivePreviewDiagnostics();
                var blocked=new[]{
                    Observation(runtime:ViveEyeRuntimeState.Error,calibration:ViveEyeCalibrationState.Required,connected:false,code:1036,absent:true),
                    Observation(calibration:ViveEyeCalibrationState.Required,connected:false,code:1036,absent:true),
                    Observation(connected:false,code:1036,absent:true),
                    Observation(code:1036,absent:true),
                    Observation(absent:true,combinedMissing:true),
                    Observation(combinedMissing:true),
                    Observation(directionValid:false,mask:0,direction:new Vec3(0,0,3)),
                    Observation(direction:new Vec3(Double.NaN,0,1),headMissing:true),
                    Observation(direction:new Vec3(0,0,0),headMissing:true),
                    Observation(direction:new Vec3(Double.MaxValue,Double.MaxValue,0),headMissing:true),
                    Observation(headMissing:true),
                    Observation(headUsable:false)
                };
                foreach(var observation in blocked)d.Observe(observation,true,false);
                d.Observe(Observation(eyeUsable:true),true,true);
                Check.Equal(1L,d.RuntimeRejectedFrames);Check.Equal(1L,d.CalibrationRejectedFrames);Check.Equal(1L,d.DeviceRejectedFrames);
                Check.Equal(1L,d.NativeRejectedFrames);Check.Equal(1L,d.DataAbsentFrames);Check.Equal(1L,d.CombinedMissingFrames);
                Check.Equal(1L,d.CombinedInvalidFrames);Check.Equal(3L,d.DirectionNonunitFrames);Check.Equal(1L,d.HeadBlockedFrames);
                Check.Equal(1L,d.DirectionNonfiniteFrames);Check.Equal(1L,d.DirectionDegenerateFrames);
                Check.Equal(1L,d.DirectionMagnitudeOverflowFrames);Check.Equal(3L,d.DirectionConvertibleFrames);
                Check.Equal(0L,d.DirectionNormalizedFrames);
                Check.Equal(1L,d.MappingBlockedFrames);Check.Equal(1L,d.UsableWithHeadFrames);Check.Equal(0L,d.UsableOffBoardFrames);
                Check.Equal(13L,d.NewEyeFrames);Partitions(d);
            });
            suite.Run("VIVE preview aggregate separates direction conversion from math rejection and native validity",()=>{
                var d=new VivePreviewDiagnostics();
                var nativeFloatOverflow=new Vec3(1e30f,0,0);Check.True(nativeFloatOverflow.IsFinite);
                foreach(var direction in new[]{new Vec3(0,0,0),new Vec3(Double.NaN,0,1),new Vec3(0,Double.PositiveInfinity,1),
                    new Vec3(Double.MaxValue,Double.MaxValue,0),nativeFloatOverflow})
                    d.Observe(Observation(direction:direction),true,false);
                var convertible=Observation(direction:new Vec3(0,0,2),eyeUsable:true);
                d.Observe(convertible,true,true);
                d.Observe(Observation(absent:true,direction:new Vec3(0,0,2)),true,false);
                d.Observe(Observation(directionValid:false,mask:0,direction:new Vec3(0,0,2)),true,false);
                d.Observe(Observation(mask:0,direction:new Vec3(0,0,2)),true,false);
                Check.Equal(7L,d.CombinedMaskValidFrames);Check.Equal(6L,d.DirectionNonunitFrames);
                Check.Equal(2L,d.DirectionNonfiniteFrames);Check.Equal(1L,d.DirectionDegenerateFrames);
                Check.Equal(2L,d.DirectionMagnitudeOverflowFrames);Check.Equal(1L,d.DirectionConvertibleFrames);
                Check.Equal(1L,d.DirectionNormalizedFrames);Check.Equal(2.0,convertible.RawPoll.Eye.Combined.Direction.Z);
                Check.Equal(1L,d.DataAbsentFrames);Check.Equal(2L,d.CombinedInvalidFrames);Check.Equal(1L,d.UsableWithHeadFrames);Partitions(d);
            });
            suite.Run("VIVE preview conversion cannot revive either invalid native direction signal",()=>{
                var d=new VivePreviewDiagnostics();
                d.Observe(Observation(mask:0,direction:new Vec3(0,0,2),eyeUsable:true),true,true);
                d.Observe(Observation(directionValid:false,direction:new Vec3(0,0,2),eyeUsable:true),true,true);
                Check.Equal(2L,d.CombinedInvalidFrames);Check.Equal(0L,d.DirectionConvertibleFrames);
                Check.Equal(0L,d.DirectionNormalizedFrames);Check.Equal(0L,d.DirectionNonunitFrames);
                Check.Equal(0L,d.UsableWithHeadFrames);Check.Equal(0L,d.UsableOffBoardFrames);Partitions(d);
            });
            suite.Run("VIVE preview aggregate head cohort and source usable totals overlap display rejections",()=>{
                var d=new VivePreviewDiagnostics();
                d.Observe(Observation(eyeUsable:true),false,false);
                d.Observe(Observation(absent:true),true,false);
                d.Observe(Observation(eyeUsable:true),true,true);
                d.Observe(Observation(eyeUsable:true),true,false);
                Check.Equal(4L,d.NewEyeFrames);Check.Equal(3L,d.NewEyeFramesWithHeadReady);
                Check.Equal(3L,d.UsableEyeFrames);Check.Equal(2L,d.UsableWithHeadFrames);Check.Equal(1L,d.UsableOffBoardFrames);
                Check.Equal(1L,d.HeadBlockedFrames);Check.Equal(1L,d.DataAbsentFrames);Partitions(d);
            });
            suite.Run("VIVE preview aggregate keeps raw head rejection separate from mapping failure",()=>{
                var d=new VivePreviewDiagnostics();
                d.Observe(Observation(headDisposition:ViveFrameDisposition.Duplicate),true,false);
                d.Observe(Observation(head:new ViveHeadFrame(9,1,false,true,Rotation.Identity)),true,false);
                d.Observe(Observation(head:new ViveHeadFrame(9,1,true,false,Rotation.Identity)),true,false);
                d.Observe(Observation(head:new ViveHeadFrame(9,1,true,true,new Rotation(0,0,0,2))),true,false);
                d.Observe(Observation(headUsable:false),true,false);
                // Eye mapping can succeed independently of TryMapHead in the source.
                d.Observe(Observation(headUsable:false,eyeUsable:true,code:null),true,true);
                Check.Equal(4L,d.HeadBlockedFrames);Check.Equal(1L,d.MappingBlockedFrames);
                Check.Equal(1L,d.UsableWithHeadFrames);Check.Equal(0L,d.NativeRejectedFrames);Partitions(d);
            });
            suite.Run("VIVE preview healthy duplicates require every current raw and head gate without becoming usable",()=>{
                var d=new VivePreviewDiagnostics();
                var healthy=Observation(ViveFrameDisposition.Duplicate,code:null);
                d.Observe(healthy,true,false);
                var convertible=Observation(ViveFrameDisposition.Duplicate,direction:new Vec3(0,0,2));
                d.Observe(convertible,true,false);
                var unhealthy=new[]{
                    Observation(ViveFrameDisposition.Duplicate,runtime:ViveEyeRuntimeState.Unavailable),
                    Observation(ViveFrameDisposition.Duplicate,calibration:ViveEyeCalibrationState.Unknown),
                    Observation(ViveFrameDisposition.Duplicate,connected:false),
                    Observation(ViveFrameDisposition.Duplicate,code:1036),
                    Observation(ViveFrameDisposition.Duplicate,absent:true),
                    Observation(ViveFrameDisposition.Duplicate,combinedMissing:true),
                    Observation(ViveFrameDisposition.Duplicate,mask:0),
                    Observation(ViveFrameDisposition.Duplicate,directionValid:false),
                    Observation(ViveFrameDisposition.Duplicate,direction:new Vec3(0,0,0)),
                    Observation(ViveFrameDisposition.Duplicate,headDisposition:ViveFrameDisposition.Stale),
                    Observation(ViveFrameDisposition.Duplicate,headUsable:false),
                    Observation(ViveFrameDisposition.Duplicate,headMissing:true),
                    Observation(ViveFrameDisposition.Duplicate,head:new ViveHeadFrame(9,1,false,true,Rotation.Identity)),
                    Observation(ViveFrameDisposition.Duplicate,head:new ViveHeadFrame(9,1,true,false,Rotation.Identity)),
                    Observation(ViveFrameDisposition.Duplicate,head:new ViveHeadFrame(9,1,true,true,new Rotation(0,0,0,2)))
                };
                foreach(var observation in unhealthy)d.Observe(observation,true,false);
                d.Observe(healthy,false,false);
                Check.Equal(18L,d.DuplicateViews);Check.Equal(2L,d.HealthyDuplicateViews);Check.Equal(0L,d.NewEyeFrames);
                Check.Equal(0L,d.UsableEyeFrames);Check.Equal(0L,d.UsableWithHeadFrames);Check.Equal(0L,d.UsableOffBoardFrames);
                Check.Equal(0L,d.DirectionConvertibleFrames);Check.Equal(0L,d.DirectionNormalizedFrames);
                Check.Equal(0L,d.DirectionNonunitFrames);Check.Equal(2.0,convertible.RawPoll.Eye.Combined.Direction.Z);
                Check.True(!healthy.EyeUsable);Check.Equal(ViveFrameDisposition.Duplicate,healthy.EyeDisposition);Partitions(d);
            });
            suite.Run("VIVE preview aggregates survive owner stop and reset without retaining source objects",()=>{
                var retained=new VivePreviewDiagnostics();
                var observation=Observation(eyeUsable:true);
                retained.Observe(observation,true,false);
                // The owner can release its source/observation and retain only this aggregate.
                observation=null;
                var nextRun=new VivePreviewDiagnostics();Empty(nextRun);
                Check.Equal(1L,retained.NewEyeFrames);Check.Equal(1L,retained.UsableOffBoardFrames);
                foreach(var field in typeof(VivePreviewDiagnostics).GetFields(BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public))
                    Check.Equal(typeof(long),field.FieldType,"Aggregate must retain counts only: "+field.Name);
                retained.Observe(Observation(ViveFrameDisposition.Duplicate),true,false);
                retained.Observe(Observation(absent:true),true,false);
                retained.Reset();Empty(retained);Partitions(retained);
                retained.Observe(Observation(eyeUsable:true),true,true);
                Check.Equal(1L,retained.NewEyeFrames);Check.Equal(0L,retained.HealthyDuplicateViews);Check.Equal(0L,retained.UsableOffBoardFrames);
                Empty(nextRun);
            });
        }
    }
}
