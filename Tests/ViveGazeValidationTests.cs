using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    public static class ViveGazeValidationTests
    {
        static readonly Vec3 Origin=new Vec3(0,0,0),Forward=new Vec3(0,0,1),Target=new Vec3(0,0,2);
        static ViveProEyePoll Poll(int frame,double time,ViveEyeCalibrationState calibration=ViveEyeCalibrationState.ReportedReady,
            ulong mask=3,bool absent=false,Vec3? rawDirection=null,double healthAge=.01)
        {
            var ray=new ViveEyeRay(mask,(mask&1)!=0,(mask&2)!=0,Origin,rawDirection??Forward);
            var diagnostics=new ViveNativePollDiagnostics(frame,time,time+.001,time-healthAge,time-healthAge,time-healthAge,
                null,null,0,0,calibration==ViveEyeCalibrationState.Required,null,true,time,time-healthAge-.001,time-healthAge,0);
            return new ViveProEyePoll(null,new ViveEyeFrame(frame,frame*8,time,absent,ray,ray,ray),ViveEyeRuntimeState.Working,calibration,true,0,2,diagnostics);
        }
        static ViveGazeValidationSnapshot Snapshot(double time,string targetId="board",string pass="stable_1",Vec3? direction=null,
            Vec3? target=null,string category="task",bool poseValid=true,double renderAge=.01,double poseSkew=.005,string reason="valid_surface_proxy")
        {
            return new ViveGazeValidationSnapshot(targetId,pass,time+poseSkew-renderAge-.001,time+poseSkew-renderAge,1,time+poseSkew,
                target??Target,Origin,Rotation.Identity,Origin,direction??Forward,category,reason,poseValid);
        }
        static ViveGazeValidationSession Session(string id="validation-test")
        {
            var s=new ViveGazeValidationSession(id);s.BeginTarget(new ViveGazeValidationTarget("board","task","stable_1",Target),1,1);return s;
        }
        static List<string[]> Parse(string text)
        {
            var result=new List<string[]>();var row=new List<string>();var field=new System.Text.StringBuilder();bool quoted=false;
            for(int i=0;i<text.Length;i++)
            {
                char c=text[i];
                if(c=='"')
                {
                    if(quoted&&i+1<text.Length&&text[i+1]=='"'){field.Append('"');i++;}
                    else quoted=!quoted;
                }
                else if(c==','&&!quoted){row.Add(field.ToString());field.Length=0;}
                else if(c=='\n'&&!quoted){row.Add(field.ToString().TrimEnd('\r'));field.Length=0;result.Add(row.ToArray());row.Clear();}
                else field.Append(c);
            }
            return result;
        }
        static string Field(List<string[]> csv,int row,string column){return csv[row][Array.IndexOf(csv[0],column)];}
        public static void Register(TestSuite suite)
        {
            suite.Run("gaze validation angular error reports signed bias and rejects degenerate geometry",()=>{
                double a,y,p;
                Check.True(ViveGazeValidationSession.TryAngularError(Origin,new Vec3(1,0,1),Target,out a,out y,out p));
                Check.Near(45,a);Check.Near(45,y);Check.Near(0,p);
                Check.True(ViveGazeValidationSession.TryAngularError(Origin,new Vec3(0,-1,1),Target,out a,out y,out p));
                Check.Near(45,a);Check.Near(-45,p);
                Check.True(!ViveGazeValidationSession.TryAngularError(Origin,Origin,Target,out a,out y,out p));
                Check.True(!ViveGazeValidationSession.TryAngularError(Origin,Forward,Origin,out a,out y,out p));
                Check.True(!ViveGazeValidationSession.TryAngularError(Origin,new Vec3(Double.NaN,0,1),Target,out a,out y,out p));
            });
            suite.Run("gaze validation wraps world yaw difference at the rear hemisphere",()=>{
                double a,y,p;double r=Math.PI/180;
                Check.True(ViveGazeValidationSession.TryAngularError(Origin,new Vec3(Math.Sin(-179*r),0,Math.Cos(-179*r)),new Vec3(Math.Sin(179*r),0,Math.Cos(179*r)),out a,out y,out p));
                Check.Near(2,a);Check.Near(2,y);
            });
            suite.Run("gaze validation excludes target transitions but retains their samples",()=>{
                var s=Session();s.Observe(Poll(1,1.49),Snapshot(1.49));s.Observe(Poll(2,1.5),Snapshot(1.5));
                Check.Equal(2,s.SampleCount);Check.True(!s.Samples[0].StableGeometryEligible);Check.True(s.Samples[1].StableGeometryEligible);
                Check.True(s.Samples[1].StableReadyEligible);Check.Near(0,s.Samples[1].AngularErrorDegrees.Value);
            });
            suite.Run("gaze validation assigns delayed callbacks to their historical target interval",()=>{
                var s=Session();s.EndTarget(2);s.BeginTarget(new ViveGazeValidationTarget("eye","eyes","stable_1"),2.1,2);
                s.Observe(Poll(1,1.9),Snapshot(1.9));
                Check.Equal("board",s.Samples[0].Window.Target.Id);Check.True(s.Samples[0].StableGeometryEligible);
                s.Observe(Poll(2,1.91),Snapshot(1.91,"eye"));Check.Equal("snapshot_target_mismatch",s.Samples[1].GeometryExclusion);
                s.Observe(Poll(3,2.05),Snapshot(2.05,"eye"));Check.Equal("outside_target_interval",s.Samples[2].GeometryExclusion);
            });
            suite.Run("gaze validation retains invalid rays and calibration reports without inventing measurements",()=>{
                var s=Session();s.Observe(Poll(1,1.6,mask:0),Snapshot(1.6));s.Observe(Poll(2,1.61,absent:true),Snapshot(1.61));
                s.Observe(Poll(3,1.62,rawDirection:new Vec3(Double.NaN,0,1)),Snapshot(1.62));
                s.Observe(Poll(4,1.63,ViveEyeCalibrationState.Required),Snapshot(1.63));
                Check.Equal(4,s.SampleCount);
                for(int i=0;i<3;i++){Check.True(!s.Samples[i].RawCombinedValid);Check.True(!s.Samples[i].AngularErrorDegrees.HasValue);}
                Check.True(s.Samples[3].StableGeometryEligible);Check.True(!s.Samples[3].StableReadyEligible);
                Check.Equal("calibration_required",s.Samples[3].ReadyExclusion);Check.Equal(ViveEyeCalibrationState.Required,s.Samples[3].Poll.Calibration);
            });
            suite.Run("gaze validation rejects stale mismatched and invalid pose snapshots",()=>{
                var s=Session();
                s.Observe(Poll(1,1.6),Snapshot(1.6,renderAge:.2));
                s.Observe(Poll(2,1.61),Snapshot(1.61,renderAge:.07,poseSkew:.06));
                s.Observe(Poll(3,1.62),Snapshot(1.62,poseValid:false));
                s.Observe(Poll(4,1.63),Snapshot(1.63,category:"invalid"));
                s.Observe(Poll(5,1.64),Snapshot(1.64,pass:"gentle_turn_1"));
                s.Observe(Poll(6,1.65),Snapshot(1.65,reason:"geometry_unavailable"));
                s.Observe(Poll(7,1.66),Snapshot(1.66,renderAge:.001));
                s.Observe(Poll(8,1.67),Snapshot(1.67,poseSkew:-.001));
                Check.Equal("stale_render_scene",s.Samples[0].GeometryExclusion);Check.Equal("pose_callback_skew",s.Samples[1].GeometryExclusion);
                Check.Equal("invalid_head_pose",s.Samples[2].GeometryExclusion);Check.Equal("invalid_aoi_snapshot",s.Samples[3].GeometryExclusion);
                Check.Equal("snapshot_target_mismatch",s.Samples[4].GeometryExclusion);Check.Equal("invalid_aoi_snapshot",s.Samples[5].GeometryExclusion);
                Check.Equal("render_after_callback",s.Samples[6].GeometryExclusion);
                Check.Equal("pose_callback_skew",s.Samples[7].GeometryExclusion);
                foreach(var m in s.Samples)Check.True(!m.AngularErrorDegrees.HasValue);
            });
            suite.Run("gaze validation separates stale SDK health from geometric measurement",()=>{
                var s=Session();s.Observe(Poll(1,1.6,healthAge:.3),Snapshot(1.6));s.Observe(Poll(2,1.61,healthAge:-.01),Snapshot(1.61));
                foreach(var m in s.Samples){Check.True(m.StableGeometryEligible);Check.True(!m.StableReadyEligible);Check.Equal("health_query_not_fresh_at_callback",m.ReadyExclusion);}
            });
            suite.Run("gaze validation deduplicates identities while preserving native gaps and callback frequency",()=>{
                var s=Session();var p=Poll(10,1.6);Check.True(s.Observe(p,Snapshot(1.6)));Check.True(!s.Observe(p,Snapshot(1.6)));
                s.Observe(Poll(12,1.62),Snapshot(1.62));
                Check.Equal(2,s.SampleCount);Check.Equal(1L,s.DuplicateSamples);Check.Equal(1L,s.NativeGapCount);Check.Near(50,s.CallbackArrivalRateHz.Value);
                s.Observe(Poll(11,1.61),Snapshot(1.61));Check.Equal(1L,s.NativeDiscontinuities);Check.Equal(1L,s.CallbackTimeDiscontinuities);Check.True(!s.CallbackArrivalRateHz.HasValue);
            });
            suite.Run("gaze validation missing frame polls cannot become samples",()=>{
                var s=Session();Check.True(!s.Observe((ViveProEyePoll)null,null));
                Check.True(!s.Observe(new ViveProEyePoll(null,null,ViveEyeRuntimeState.Unknown,ViveEyeCalibrationState.Unknown,false),null));
                Check.Equal(0,s.SampleCount);Check.Equal(2L,s.MissingEyePolls);
            });
            suite.Run("gaze validation export retains native payloads and fixed column counts across null snapshots",()=>{
                var s=Session("test,\"quoted\"\nidentity");s.Observe(Poll(1,1.6),Snapshot(1.6));s.Observe(Poll(2,1.61,mask:0,rawDirection:new Vec3(Double.NaN,0,1)),null);
                s.Finish(2,false,"operator stopped");var w=new StringWriter(CultureInfo.InvariantCulture);s.WriteSamplesCsv(w);var csv=Parse(w.ToString());
                Check.Equal(3,csv.Count);Check.Equal(csv[0].Length,csv[1].Length);Check.Equal(csv[0].Length,csv[2].Length);
                Check.Equal("test,\"quoted\"\nidentity",Field(csv,1,"session_id"));Check.Equal("8",Field(csv,1,"native_timestamp_opaque"));
                Check.Equal("NaN",Field(csv,2,"combined_direction_x"));Check.Equal("",Field(csv,2,"angular_error_deg"));Check.Equal("2",Field(csv,1,"target_offset_local_s"));
            });
            suite.Run("gaze validation summary partitions AOIs and calibration-required metrics",()=>{
                var s=Session();s.Observe(Poll(1,1.6),Snapshot(1.6));s.Observe(Poll(2,1.61,ViveEyeCalibrationState.Required),Snapshot(1.61,direction:new Vec3(1,0,1),category:"other"));
                s.Observe(Poll(3,1.62,mask:0),Snapshot(1.62));s.Finish(2,true,"sequence completed");
                var w=new StringWriter();s.WriteSummaryCsv(w);var csv=Parse(w.ToString());
                Check.Equal(3,csv.Count);foreach(var row in csv)Check.Equal(csv[0].Length,row.Length);
                Check.Equal("3",Field(csv,1,"total_unique_samples"));Check.Equal("2",Field(csv,1,"stable_geometry_samples"));Check.Equal("1",Field(csv,1,"stable_ready_samples"));
                Check.Equal("1",Field(csv,1,"aoi_task"));Check.Equal("1",Field(csv,1,"aoi_other"));Check.Equal("0.5",Field(csv,1,"aoi_match_fraction"));
                Check.Near(22.5,Double.Parse(Field(csv,1,"angular_error_median_deg"),CultureInfo.InvariantCulture));
                Check.Near(45,Double.Parse(Field(csv,1,"required_error_median_deg"),CultureInfo.InvariantCulture));
            });
            suite.Run("gaze validation zero data remains unassessed and target onset position is exported",()=>{
                var s=Session();s.Finish(2,false,"no eye data");var w=new StringWriter();s.WriteSummaryCsv(w);var csv=Parse(w.ToString());
                Check.Equal("",Field(csv,1,"angular_error_median_deg"));Check.Equal("",Field(csv,1,"raw_invalid_fraction"));Check.True(s.SummaryText().Contains("unassessed"));
                w=new StringWriter();s.WriteTargetsCsv(w);csv=Parse(w.ToString());Check.Equal("2",Field(csv,1,"initial_target_world_z"));Check.Equal("false",Field(csv,1,"session_completed"));
            });
            suite.Run("gaze validation state forbids overlapping targets and nonmonotonic events",()=>{
                var s=Session();Check.Throws<InvalidOperationException>(()=>s.BeginTarget(new ViveGazeValidationTarget("eye","eyes","stable_1"),1.1,2));
                Check.Throws<ArgumentOutOfRangeException>(()=>s.EndTarget(.9));Check.Throws<ArgumentOutOfRangeException>(()=>s.EndTarget(Double.NaN));
                s.Finish(2,false,"stop");Check.Throws<InvalidOperationException>(()=>s.Observe(Poll(1,1.6),null));Check.Throws<InvalidOperationException>(()=>s.Finish(3,true,"again"));
            });
            suite.Run("gaze validation target history has a hard memory bound",()=>{
                var s=new ViveGazeValidationSession("bounded");
                for(int i=0;i<ViveGazeValidationSession.MaximumTargets;i++){s.BeginTarget(new ViveGazeValidationTarget("x","task","stable"),i*2,i);s.EndTarget(i*2+1);}
                Check.Throws<InvalidOperationException>(()=>s.BeginTarget(new ViveGazeValidationTarget("x","task","stable"),1000,1000));
                Check.Equal(ViveGazeValidationSession.MaximumTargets,s.TargetCount);
            });
            suite.Run("gaze validation saves a completed export marker last without replacing old data",()=>{
                string folder=Path.Combine(Path.GetTempPath(),"vive-validation-test-"+Guid.NewGuid().ToString("N"));
                try
                {
                    Directory.CreateDirectory(folder);var s=Session();Check.Throws<InvalidOperationException>(()=>s.Save(folder));
                    s.Observe(Poll(1,1.6),Snapshot(1.6));s.SetCaptureOutcome("drained=true; cleanup=true");s.Finish(2,false,"operator stopped");
                    string csv=s.Save(folder);Check.True(File.Exists(csv));Check.True(File.Exists(Path.Combine(folder,"session.txt")));
                    Check.Equal(0,Directory.GetFiles(folder,"*.partial").Length);Check.True(File.ReadAllText(Path.Combine(folder,"session.txt")).Contains("run_completed=False"));
                    string before=File.ReadAllText(csv);Check.Throws<IOException>(()=>s.Save(folder));Check.Equal(before,File.ReadAllText(csv));
                }
                finally{if(Directory.Exists(folder))Directory.Delete(folder,true);}
            });
        }
    }
}
