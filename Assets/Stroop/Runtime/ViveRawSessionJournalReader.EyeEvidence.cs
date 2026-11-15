using System;
using System.Collections.Generic;
using System.IO;
using AvatarStroop.Core;

namespace AvatarStroop.Runtime
{
    internal static partial class ViveRawSessionJournalReader
    {
        static double? OptionalF64(BinaryReader r,Rows rows,string name)
        {return Flag(r,rows,name+"_present")?(double?)F64(r,rows,name,true):null;}
        static Vec3 ReadVector(BinaryReader r,Rows rows,string name)
        {return new Vec3(F64(r,rows,name+"_x"),F64(r,rows,name+"_y"),F64(r,rows,name+"_z"));}
        static Rotation ReadRotation(BinaryReader r,Rows rows,string name)
        {return new Rotation(F64(r,rows,name+"_x"),F64(r,rows,name+"_y"),F64(r,rows,name+"_z"),F64(r,rows,name+"_w"));}
        static ViveEyeRay ReadNativeRay(BinaryReader r,Rows rows,string name)
        {
            if(!Flag(r,rows,name+"_present"))return null;
            ulong mask=r.ReadUInt64();rows.UInt(name+"_mask",mask);bool origin=Flag(r,rows,name+"_origin_valid"),direction=Flag(r,rows,name+"_direction_valid");
            return new ViveEyeRay(mask,origin,direction,ReadVector(r,rows,name+"_origin"),ReadVector(r,rows,name+"_direction"));
        }
        static ViveRawEyeAcquisition ReadAcquisition(BinaryReader r,Rows rows)
        {
            int version=I32(r,rows,"acquisition_version");if(version!=1&&version!=2)throw new Invalid();
            long sequence=I64(r,rows,"acquisition_sequence");double published=F64(r,rows,"worker_published",true);
            long eligible=I64(r,rows,"eligible_polls"),duplicates=I64(r,rows,"duplicate_polls"),gaps=I64(r,rows,"native_sequence_gaps"),discontinuities=I64(r,rows,"native_sequence_discontinuities");
            int nativeFrame=I32(r,rows,"native_frame"),nativeTime=I32(r,rows,"native_timestamp");double observed=F64(r,rows,"eye_observed",true);
            int runtime=I32(r,rows,"runtime"),calibration=I32(r,rows,"calibration");bool connected=Flag(r,rows,"eye_connected");
            int? result=OptionalI32(r,rows,"native_result"),status=OptionalI32(r,rows,"runtime_status");bool noUser=Flag(r,rows,"no_user");
            var left=ReadNativeRay(r,rows,"left");var right=ReadNativeRay(r,rows,"right");var combined=ReadNativeRay(r,rows,"combined");
            string mode=null,resultSource=null;
            if(version>=2)
            {
                mode=Text(r,rows,"capture_mode",true);resultSource=Text(r,rows,"native_result_source",true);
                if(!ViveRawSessionJournalCodec.ValidCaptureSource(mode,resultSource))throw new Invalid();
            }
            ViveNativePollDiagnostics diagnostics=null;
            if(Flag(r,rows,"poll_diagnostics_present"))
            {
                long pollSequence=I64(r,rows,"provider_poll_sequence");double start=F64(r,rows,"poll_started",true),end=F64(r,rows,"poll_returned",true);
                double? statusAt=OptionalF64(r,rows,"status_returned"),deviceAt=OptionalF64(r,rows,"device_returned"),calibrationAt=OptionalF64(r,rows,"calibration_returned"),
                    readStart=OptionalF64(r,rows,"read_started"),readEnd=OptionalF64(r,rows,"read_returned");
                int? statusResult=OptionalI32(r,rows,"status_result"),calibrationResult=OptionalI32(r,rows,"calibration_result");
                bool? needed=OptionalBool(r,rows,"calibration_needed");int? readResult=OptionalI32(r,rows,"read_result");
                bool callback=false;double? callbackAt=null,healthStart=null,healthEnd=null;int? registration=null;
                if(version>=2)
                {
                    callback=Flag(r,rows,"callback_based");callbackAt=OptionalF64(r,rows,"callback_observed");
                    healthStart=OptionalF64(r,rows,"health_query_started");healthEnd=OptionalF64(r,rows,"health_query_completed");
                    registration=OptionalI32(r,rows,"callback_registration_result");
                }
                diagnostics=new ViveNativePollDiagnostics(pollSequence,start,end,statusAt,deviceAt,calibrationAt,readStart,readEnd,statusResult,calibrationResult,needed,readResult,
                    callback,callbackAt,healthStart,healthEnd,registration);
                if(callback&&callbackAt.HasValue&&healthEnd.HasValue)
                {
                    rows.DoubleBits("health_age_at_callback_seconds",BitConverter.DoubleToInt64Bits(callbackAt.Value-healthEnd.Value));
                    rows.DoubleBits("callback_to_dequeue_seconds",BitConverter.DoubleToInt64Bits(end-callbackAt.Value));
                }
            }
            var poll=new ViveProEyePoll(null,new ViveEyeFrame(nativeFrame,nativeTime,observed,noUser,left,right,combined),
                (ViveEyeRuntimeState)runtime,(ViveEyeCalibrationState)calibration,connected,result,status,diagnostics);
            var value=new ViveRawEyeAcquisition(sequence,published,poll,eligible,duplicates,gaps,discontinuities);
            if(version>=2&&(mode!=(diagnostics==null?"native_poll":diagnostics.CaptureMode)||
                resultSource!=(diagnostics==null?"eye_read":diagnostics.NativeResultSource)))throw new Invalid();
            if(!ViveRawSessionJournalCodec.ValidAcquisition(value))throw new Invalid();return value;
        }
        static void ReadEyeFrameExtension(BinaryReader r,Rows rows,int nativeFrame,int nativeTimestamp,double eyeObserved,double checkedAt)
        {
            int version=r.ReadInt32();
            if(version==1){ReadAoi(r,rows,nativeFrame,nativeTimestamp,eyeObserved,checkedAt,true);return;}
            if(version!=2)throw new Invalid();rows.Int("recording_extension_version",version);
            string mode=Text(r,rows,"capture_mode",true),source=Text(r,rows,"native_result_source",true);
            if(!ViveRawSessionJournalCodec.ValidCaptureSource(mode,source))throw new Invalid();
            if(Flag(r,rows,"aoi_present"))ReadAoi(r,rows,nativeFrame,nativeTimestamp,eyeObserved,checkedAt);
        }
        static void ReadAoi(BinaryReader r,Rows rows,int nativeFrame,int nativeTimestamp,double eyeObserved,double checkedAt,bool versionAlreadyRead=false)
        {
            if(versionAlreadyRead)rows.Int("aoi_extension_version",1);
            else if(I32(r,rows,"aoi_extension_version")!=1)throw new Invalid();
            string policy=Text(r,rows,"aoi_policy",true);
            if(Text(r,rows,"aoi_coordinate_basis",true)!=ViveEyeAoiObservation.CoordinateBasis||
                Text(r,rows,"aoi_pose_time_meaning",true)!=ViveEyeAoiObservation.PoseTimeMeaning)throw new Invalid();
            string meaning=Text(r,rows,"aoi_classification_meaning",true);
            bool historical=policy=="vive-classroom-exclusive-surface-proxies-v1"&&
                (meaning=="before-mask-scene-analytic-surface-proxy-not-validated-fixation-or-full-scene-occlusion"||
                 meaning=="analytic-surface-proxy-not-validated-fixation-or-full-scene-occlusion");
            bool current=policy==ViveEyeAoiObservation.PolicyVersion&&meaning==ViveEyeAoiObservation.ClassificationMeaning;
            if(!historical&&!current)throw new Invalid();
            double captured=F64(r,rows,"aoi_capture_local",true);double? completed=OptionalF64(r,rows,"aoi_capture_completed_local");
            double unitySeconds=F64(r,rows,"aoi_unity_seconds",true);int unityFrame=I32(r,rows,"aoi_unity_frame");
            double? sceneStart=OptionalF64(r,rows,"aoi_scene_capture_local"),sceneEnd=OptionalF64(r,rows,"aoi_scene_capture_completed_local"),sceneUnity=OptionalF64(r,rows,"aoi_scene_unity_seconds");
            int sceneFrame=I32(r,rows,"aoi_scene_unity_frame");
            long taskRevision=I64(r,rows,"aoi_task_revision"),avatarRevision=I64(r,rows,"aoi_avatar_revision");
            int renderFrame=I32(r,rows,"aoi_last_render_frame");double? renderSeconds=OptionalF64(r,rows,"aoi_last_render_unity_seconds");
            long renderTask=I64(r,rows,"aoi_last_render_task_revision"),renderAvatar=I64(r,rows,"aoi_last_render_avatar_revision");
            int phase=I32(r,rows,"aoi_phase"),block=I32(r,rows,"aoi_block");string trial=Text(r,rows,"aoi_trial"),category=Text(r,rows,"aoi_category",true),reason=Text(r,rows,"aoi_reason",true);
            bool active=Flag(r,rows,"aoi_active_task"),applicable=Flag(r,rows,"aoi_avatar_applicable"),origin=Flag(r,rows,"aoi_origin_valid"),direction=Flag(r,rows,"aoi_direction_valid");
            Vec3 camera=ReadVector(r,rows,"aoi_camera_position");Rotation rotation=ReadRotation(r,rows,"aoi_camera_rotation");
            Vec3 worldOrigin=ReadVector(r,rows,"aoi_world_origin"),worldDirection=ReadVector(r,rows,"aoi_world_direction");
            int count=I32(r,rows,"aoi_geometry_count");if(count<0||count>8)throw new Invalid();
            var geometry=new List<ViveEyeAoiGeometry>();
            for(int i=0;i<count;i++)
            {
                string prefix="aoi_geometry_"+i;string name=Text(r,rows,prefix+"_name",true);bool enabled=Flag(r,rows,prefix+"_enabled");
                geometry.Add(new ViveEyeAoiGeometry(name,enabled,ReadVector(r,rows,prefix+"_position"),ReadRotation(r,rows,prefix+"_rotation"),ReadVector(r,rows,prefix+"_scale")));
            }
            var value=new ViveEyeAoiObservation(captured,completed,unitySeconds,unityFrame,sceneStart,sceneEnd,sceneUnity,sceneFrame,taskRevision,avatarRevision,renderFrame,renderSeconds,
                renderTask,renderAvatar,(Phase)phase,block,trial,nativeFrame,nativeTimestamp,eyeObserved,category,reason,active,applicable,
                origin,direction,camera,rotation,worldOrigin,worldDirection,geometry);
            if(!ViveRawSessionJournalCodec.ValidAoi(value,nativeFrame,nativeTimestamp,eyeObserved,checkedAt))throw new Invalid();
            // Paired process/Unity clock observations; this is not photon onset
            // or a native timestamp conversion. Keep scene vs ingest context distinct.
            rows.DoubleBits("aoi_eye_to_pose_seconds",BitConverter.DoubleToInt64Bits(captured-eyeObserved));
            if(sceneStart.HasValue)rows.DoubleBits("aoi_eye_to_scene_seconds",BitConverter.DoubleToInt64Bits(sceneStart.Value-eyeObserved));
            if(sceneStart.HasValue&&sceneUnity.HasValue)rows.DoubleBits("aoi_scene_unity_clock_offset_seconds",BitConverter.DoubleToInt64Bits(sceneStart.Value-sceneUnity.Value));
            if(sceneEnd.HasValue)rows.DoubleBits("aoi_scene_to_pose_seconds",BitConverter.DoubleToInt64Bits(captured-sceneEnd.Value));
            if(sceneFrame>=0)rows.Int("aoi_scene_frame_age",unityFrame-sceneFrame);
            rows.DoubleBits("aoi_unity_clock_offset_seconds",BitConverter.DoubleToInt64Bits(captured-unitySeconds));
        }
    }
}
