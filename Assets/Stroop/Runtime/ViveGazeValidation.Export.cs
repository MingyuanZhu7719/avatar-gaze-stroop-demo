using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using AvatarStroop.Core;

namespace AvatarStroop.Runtime
{
    public sealed partial class ViveGazeValidationSession
    {
        // Called once capture has stopped, usually on the caller's export task.
        // FileMode.CreateNew and non-overwriting moves preserve earlier exports.
        // session.txt is published last, after every CSV and summary call returned.
        public string Save(string folder)
        {
            if(!Finished)throw new InvalidOperationException("End capture before exporting validation.");
            if(String.IsNullOrWhiteSpace(folder))throw new ArgumentException("An output folder is required.");
            string full=Path.GetFullPath(folder);Directory.CreateDirectory(full);
            string[] names={"samples.csv","targets.csv","summary.csv","summary.txt","session.txt"};
            foreach(string name in names)
                if(File.Exists(Path.Combine(full,name))||File.Exists(Path.Combine(full,name+".partial")))
                    throw new IOException("Validation output already exists: "+name);
            for(int i=0;i<names.Length;i++)
            {
                string path=Path.Combine(full,names[i]+".partial");
                using(var stream=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.Read))
                using(var writer=new StreamWriter(stream,new UTF8Encoding(false)))
                {
                    if(i==0)WriteSamplesCsv(writer);
                    else if(i==1)WriteTargetsCsv(writer);
                    else if(i==2)WriteSummaryCsv(writer);
                    else if(i==3)WriteSummaryText(writer);
                    else WriteSessionText(writer);
                }
            }
            foreach(string name in names)File.Move(Path.Combine(full,name+".partial"),Path.Combine(full,name));
            return Path.Combine(full,"samples.csv");
        }
        public void WriteSamplesCsv(TextWriter writer)
        {
            if(writer==null)throw new ArgumentNullException("writer");
            var header=new List<string>(new[]{"schema","session_id","sample_sequence","target_sequence","target_id","expected_aoi","pass",
                "target_onset_local_s","target_offset_local_s","target_age_s","settled","raw_combined_valid","stable_geometry_eligible","stable_ready_eligible",
                "geometry_exclusion","ready_exclusion","native_frame","native_timestamp_opaque","callback_observed_local_s","no_eye_data",
                "runtime","calibration","eye_device_connected","native_result","native_runtime_status","capture_mode","native_result_source",
                "provider_poll_sequence","poll_started_local_s","poll_returned_local_s","callback_local_s","health_query_started_local_s","health_query_completed_local_s",
                "health_age_at_callback_s","status_returned_local_s","device_returned_local_s","calibration_returned_local_s","status_result","calibration_result",
                "calibration_needed","read_started_local_s","read_returned_local_s","read_result","callback_registration_result",
                "worker_acquisition_sequence","worker_published_local_s","worker_eligible_polls","worker_duplicate_polls","worker_native_gaps","worker_native_discontinuities"});
            foreach(string eye in new[]{"left","right","combined"})
                header.AddRange(new[]{eye+"_present",eye+"_validity_mask",eye+"_origin_valid",eye+"_direction_valid",eye+"_origin_mm_x",eye+"_origin_mm_y",eye+"_origin_mm_z",eye+"_direction_x",eye+"_direction_y",eye+"_direction_z"});
            header.AddRange(new[]{"snapshot_target_id","snapshot_pass","render_started_local_s","render_completed_local_s","render_unity_frame","pose_local_s",
                "pose_minus_callback_s","pose_minus_render_s","pose_valid","target_world_x","target_world_y","target_world_z",
                "camera_world_x","camera_world_y","camera_world_z","camera_rotation_x","camera_rotation_y","camera_rotation_z","camera_rotation_w",
                "world_eye_origin_x","world_eye_origin_y","world_eye_origin_z","world_eye_direction_x","world_eye_direction_y","world_eye_direction_z",
                "observed_aoi","aoi_reason","expected_aoi_match","angular_error_deg","yaw_error_world_deg","pitch_error_world_deg","geometry_snapshots"});
            Row(writer,header);
            foreach(var m in samples)
            {
                var p=m.Poll;var e=p.Eye;var n=p.NativeDiagnostics;var a=m.Acquisition;var s=m.Snapshot;var w=m.Window;
                var values=new List<string>();
                Add(values,SchemaVersion,SessionId,m.Sequence,w==null?(object)null:w.Sequence,w==null?null:w.Target.Id,w==null?null:w.Target.ExpectedAoi,w==null?null:w.Target.Pass,
                    w==null?(object)null:w.OnsetLocalSeconds,w==null?null:w.OffsetLocalSeconds,w==null?(object)null:e.ObservedLocalSeconds-w.OnsetLocalSeconds,
                    m.Settled,m.RawCombinedValid,m.StableGeometryEligible,m.StableReadyEligible,m.GeometryExclusion,m.ReadyExclusion,
                    e.FrameSequence,e.Timestamp,e.ObservedLocalSeconds,e.NoUser,p.Runtime,p.Calibration,p.EyeDeviceConnected,p.NativeResultCode,p.NativeRuntimeStatus,
                    n==null?null:n.CaptureMode,n==null?null:n.NativeResultSource,n==null?(object)null:n.ProviderPollSequence,n==null?(object)null:n.PollStartedLocalSeconds,
                    n==null?(object)null:n.PollReturnedLocalSeconds,n==null?null:n.CallbackObservedLocalSeconds,n==null?null:n.HealthQueryStartedLocalSeconds,n==null?null:n.HealthQueryCompletedLocalSeconds,
                    n==null||!n.HealthQueryCompletedLocalSeconds.HasValue?(object)null:e.ObservedLocalSeconds-n.HealthQueryCompletedLocalSeconds.Value,
                    n==null?null:n.StatusReturnedLocalSeconds,n==null?null:n.DeviceReturnedLocalSeconds,n==null?null:n.CalibrationReturnedLocalSeconds,n==null?null:n.StatusResult,
                    n==null?null:n.CalibrationResult,n==null?null:n.CalibrationNeeded,n==null?null:n.ReadStartedLocalSeconds,n==null?null:n.ReadReturnedLocalSeconds,
                    n==null?null:n.ReadResult,n==null?null:n.CallbackRegistrationResult,a==null?(object)null:a.Sequence,a==null?(object)null:a.WorkerPublishedLocalSeconds,
                    a==null?(object)null:a.EligiblePolls,a==null?(object)null:a.DuplicatePolls,a==null?(object)null:a.NativeSequenceGapCount,a==null?(object)null:a.NativeSequenceDiscontinuities);
                Ray(values,e.Left);Ray(values,e.Right);Ray(values,e.Combined);
                Add(values,s==null?null:s.TargetId,s==null?null:s.Pass,s==null?(object)null:s.RenderStartedLocalSeconds,s==null?(object)null:s.RenderCompletedLocalSeconds,
                    s==null?(object)null:s.RenderUnityFrame,s==null?(object)null:s.PoseLocalSeconds,s==null?(object)null:s.PoseLocalSeconds-e.ObservedLocalSeconds,
                    s==null?(object)null:s.PoseLocalSeconds-s.RenderCompletedLocalSeconds,s==null?(object)null:s.PoseValid);
                Vector(values,s==null?(Vec3?)null:s.TargetPositionWorld);Vector(values,s==null?(Vec3?)null:s.CameraPositionWorld);
                Add(values,s==null?(object)null:s.CameraRotationWorld.X,s==null?(object)null:s.CameraRotationWorld.Y,s==null?(object)null:s.CameraRotationWorld.Z,s==null?(object)null:s.CameraRotationWorld.W);
                Vector(values,s==null?(Vec3?)null:s.WorldEyeOrigin);Vector(values,s==null?(Vec3?)null:s.WorldEyeDirection);
                Add(values,s==null?null:s.AoiCategory,s==null?null:s.AoiReason,
                    s==null||w==null||!m.AngularErrorDegrees.HasValue?(object)null:s.AoiCategory==w.Target.ExpectedAoi,
                    m.AngularErrorDegrees,m.YawErrorDegrees,m.PitchErrorDegrees,s==null?null:Geometry(s));
                if(values.Count!=header.Count)throw new InvalidOperationException("Validation sample CSV column mismatch.");
                Row(writer,values);
            }
        }
        public void WriteTargetsCsv(TextWriter writer)
        {
            Row(writer,new[]{"schema","session_id","target_sequence","target_id","expected_aoi","pass","onset_render_completed_local_s","onset_unity_frame","offset_requested_local_s",
                "initial_target_world_x","initial_target_world_y","initial_target_world_z","settling_s","session_completed","finish_reason"});
            foreach(var w in windows)
            {
                var values=new List<string>();Add(values,SchemaVersion,SessionId,w.Sequence,w.Target.Id,w.Target.ExpectedAoi,w.Target.Pass,w.OnsetLocalSeconds,w.OnsetUnityFrame,w.OffsetLocalSeconds);
                Vector(values,w.Target.InitialPositionWorld);Add(values,SettlingSeconds,Completed,FinishReason);Row(writer,values);
            }
        }
        sealed class Stats
        {
            internal long Total,RawInvalid,Settled,Geometry,Ready,Required,Unknown,Task,Eyes,Face,Other,Match;
            internal List<double> Errors=new List<double>(),ReadyErrors=new List<double>(),RequiredErrors=new List<double>(),Yaw=new List<double>(),Pitch=new List<double>();
            internal void Add(ViveGazeValidationMeasurement m)
            {
                Total++;if(!m.RawCombinedValid)RawInvalid++;
                if(m.Poll.Calibration==ViveEyeCalibrationState.Required)Required++;else if(m.Poll.Calibration==ViveEyeCalibrationState.Unknown)Unknown++;
                if(!m.Settled)return;Settled++;
                if(!m.StableGeometryEligible)return;Geometry++;Errors.Add(m.AngularErrorDegrees.Value);Yaw.Add(m.YawErrorDegrees.Value);Pitch.Add(m.PitchErrorDegrees.Value);
                if(m.Poll.Calibration==ViveEyeCalibrationState.Required)RequiredErrors.Add(m.AngularErrorDegrees.Value);
                if(m.StableReadyEligible){Ready++;ReadyErrors.Add(m.AngularErrorDegrees.Value);}
                switch(m.Snapshot.AoiCategory){case "task":Task++;break;case "eyes":Eyes++;break;case "remaining_face":Face++;break;case "other":Other++;break;}
                if(m.Snapshot.AoiCategory==m.Window.Target.ExpectedAoi)Match++;
            }
        }
        Stats Statistics(ViveGazeValidationWindow window)
        {
            var stats=new Stats();foreach(var m in samples)if(window==null||Object.ReferenceEquals(m.Window,window))stats.Add(m);return stats;
        }
        public void WriteSummaryCsv(TextWriter writer)
        {
            Row(writer,new[]{"schema","session_id","scope","target_sequence","target_id","expected_aoi","pass","total_unique_samples","raw_invalid_samples","raw_invalid_fraction",
                "settled_samples","stable_geometry_samples","stable_ready_samples","calibration_required_samples","calibration_unknown_samples","geometry_coverage_of_settled",
                "angular_error_median_deg","angular_error_p95_deg","angular_error_rms_deg","yaw_bias_world_deg","pitch_bias_world_deg","yaw_pitch_scatter_rms_deg",
                "ready_error_median_deg","required_geometry_samples","required_error_median_deg","aoi_task","aoi_eyes","aoi_remaining_face","aoi_other","aoi_expected_matches","aoi_match_fraction",
                "session_completed","finish_reason"});
            WriteStats(writer,null,Statistics(null));foreach(var w in windows)WriteStats(writer,w,Statistics(w));
        }
        void WriteStats(TextWriter writer,ViveGazeValidationWindow w,Stats s)
        {
            var values=new List<string>();
            Add(values,SchemaVersion,SessionId,w==null?"session":"target",w==null?(object)null:w.Sequence,w==null?null:w.Target.Id,w==null?null:w.Target.ExpectedAoi,w==null?null:w.Target.Pass,
                s.Total,s.RawInvalid,Fraction(s.RawInvalid,s.Total),s.Settled,s.Geometry,s.Ready,s.Required,s.Unknown,Fraction(s.Geometry,s.Settled),
                Percentile(s.Errors,.5),Percentile(s.Errors,.95),Rms(s.Errors),Mean(s.Yaw),Mean(s.Pitch),Scatter(s.Yaw,s.Pitch),
                Percentile(s.ReadyErrors,.5),s.RequiredErrors.Count,Percentile(s.RequiredErrors,.5),s.Task,s.Eyes,s.Face,s.Other,s.Match,Fraction(s.Match,s.Geometry),Completed,FinishReason);
            Row(writer,values);
        }
        public string SummaryText()
        {using(var w=new StringWriter(CultureInfo.InvariantCulture)){WriteSummaryText(w);return w.ToString();}}
        public void WriteSummaryText(TextWriter writer)
        {
            var s=Statistics(null);
            writer.WriteLine("VIVE fixed-target gaze validation");writer.WriteLine("Session: "+SessionId);
            writer.WriteLine("Run status: "+(Finished?(Completed?"COMPLETED":"INCOMPLETE") : "RUNNING")+"; reason: "+FinishReason);
            writer.WriteLine("Targets: "+windows.Count+"; unique native samples: "+samples.Count+"; duplicate observations ignored: "+DuplicateSamples);
            writer.WriteLine("Native counter gaps: "+NativeGapCount+"; discontinuities: "+NativeDiscontinuities+"; missing-eye polls: "+MissingEyePolls);
            writer.WriteLine("Unique callback arrival rate (Hz): "+Display(CallbackArrivalRateHz)+"; largest callback interval (seconds): "+F(MaximumCallbackGapSeconds)+"; callback-time discontinuities: "+CallbackTimeDiscontinuities);
            writer.WriteLine("Raw invalid combined rays: "+s.RawInvalid+" / "+s.Total);
            writer.WriteLine("Stable geometric measurements: "+s.Geometry+" / "+s.Settled+" stable samples; SDK-ready subset: "+s.Ready);
            writer.WriteLine("Stable angular error median / p95 (degrees): "+Display(Percentile(s.Errors,.5))+" / "+Display(Percentile(s.Errors,.95)));
            writer.WriteLine("Expected AOI matches among stable geometric measurements: "+s.Match+" / "+s.Geometry);
            writer.WriteLine("SDK calibration-required samples: "+s.Required+"; unknown: "+s.Unknown);
            writer.WriteLine("Capture outcome: "+CaptureOutcome);
            writer.WriteLine();
            writer.WriteLine("Interpretation: descriptive engineering measurements, no automatic pass/fail or validated fixation label.");
            writer.WriteLine("Inspect each target/pass in summary.csv; session aggregates can hide target-specific errors. Zero measurements means unassessed, not zero error.");
            writer.WriteLine("The first "+F(SettlingSeconds)+" seconds after each observed target render are retained but excluded from stable metrics. This is a fixed analysis window, not a detected fixation.");
            writer.WriteLine("Geometry metrics retain valid rays with calibration-required/unknown reports; ready metrics require fresh SDK-ready health. Compare these cohorts before interpreting SDK status changes.");
            writer.WriteLine("World yaw/pitch bias and scatter use world axes. Scatter includes participant movement and residual pose/eye timing mismatch, not instrument precision alone.");
            writer.WriteLine("Callback arrival, completed render and later camera pose are separate software-clock observations. Native timestamps are opaque; sensor exposure and photon onset are not synchronized by this export.");
            writer.WriteLine("AOIs use analytic scene surface proxies. Reference targets are instructed fixation locations, not proof the participant looked at them. Validation data are separate from study trials.");
        }
        void WriteSessionText(TextWriter writer)
        {
            writer.WriteLine("schema="+SchemaVersion);writer.WriteLine("session_id="+SessionId);writer.WriteLine("run_completed="+Completed);
            writer.WriteLine("finish_reason="+FinishReason);writer.WriteLine("finished_local_s="+F(FinishedLocalSeconds));
            writer.WriteLine("capture_outcome="+CaptureOutcome);writer.WriteLine("settling_s="+F(SettlingSeconds));
            writer.WriteLine("maximum_pose_callback_skew_s="+F(MaximumPoseSkewSeconds));writer.WriteLine("maximum_render_age_s="+F(MaximumSceneAgeSeconds));
            writer.WriteLine("coordinate_basis="+ViveEyeAoiObservation.CoordinateBasis);
            writer.WriteLine("export_status=All CSV and summary write/close calls returned; this marker was published last.");
            writer.WriteLine("sample_files=samples.csv,targets.csv,summary.csv,summary.txt");
            writer.WriteLine("geometry_snapshots_format=name|enabled|position_xyz|rotation_xyzw|scale_xyz; repeated objects separated by semicolon");
        }
        static double? Fraction(long a,long b){return b==0?(double?)null:(double)a/b;}
        static double? Mean(List<double> a){if(a.Count==0)return null;double sum=0;foreach(var x in a)sum+=x;return sum/a.Count;}
        static double? Rms(List<double> a){if(a.Count==0)return null;double sum=0;foreach(var x in a)sum+=x*x;return Math.Sqrt(sum/a.Count);}
        static double? Percentile(List<double> a,double p)
        {
            if(a.Count==0)return null;var copy=a.ToArray();Array.Sort(copy);double position=(copy.Length-1)*p;int lower=(int)position,upper=Math.Min(lower+1,copy.Length-1);
            return copy[lower]+(copy[upper]-copy[lower])*(position-lower);
        }
        static double? Scatter(List<double> yaw,List<double> pitch)
        {
            if(yaw.Count==0)return null;double y=Mean(yaw).Value,p=Mean(pitch).Value,sum=0;
            for(int i=0;i<yaw.Count;i++)sum+=(yaw[i]-y)*(yaw[i]-y)+(pitch[i]-p)*(pitch[i]-p);return Math.Sqrt(sum/yaw.Count);
        }
        static string Display(double? n){return n.HasValue?n.Value.ToString("F3",CultureInfo.InvariantCulture):"unassessed";}
        static void Ray(List<string> row,ViveEyeRay ray)
        {
            Add(row,ray!=null,ray==null?(object)null:ray.ValidityMask,ray==null?(object)null:ray.OriginReportedValid,ray==null?(object)null:ray.DirectionReportedValid);
            Vector(row,ray==null?(Vec3?)null:ray.OriginMillimeters);Vector(row,ray==null?(Vec3?)null:ray.Direction);
        }
        static void Vector(List<string> row,Vec3? v){Add(row,v.HasValue?(object)v.Value.X:null,v.HasValue?(object)v.Value.Y:null,v.HasValue?(object)v.Value.Z:null);}
        static string Geometry(ViveGazeValidationSnapshot s)
        {
            var result=new List<string>();foreach(var g in s.Geometry)
                result.Add(g.Name+"|"+F(g.Enabled)+"|"+F(g.PositionWorld.X)+","+F(g.PositionWorld.Y)+","+F(g.PositionWorld.Z)+"|"+
                    F(g.RotationWorld.X)+","+F(g.RotationWorld.Y)+","+F(g.RotationWorld.Z)+","+F(g.RotationWorld.W)+"|"+F(g.ScaleWorld.X)+","+F(g.ScaleWorld.Y)+","+F(g.ScaleWorld.Z));
            return String.Join(";",result.ToArray());
        }
        static void Add(List<string> row,params object[] values){foreach(var value in values)row.Add(F(value));}
        static string F(object value)
        {
            if(value==null)return "";
            if(value is double)return ((double)value).ToString("R",CultureInfo.InvariantCulture);
            if(value is bool)return (bool)value?"true":"false";
            return Convert.ToString(value,CultureInfo.InvariantCulture);
        }
        static void Row(TextWriter writer,IEnumerable<string> values)
        {
            bool first=true;foreach(string value in values)
            {
                if(!first)writer.Write(',');first=false;string text=value??"";
                if(text.IndexOfAny(new[]{',','"','\r','\n'})>=0){writer.Write('"');writer.Write(text.Replace("\"","\"\""));writer.Write('"');}
                else writer.Write(text);
            }
            writer.WriteLine();
        }
    }
}
