using System;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    public static class VivePreviewMarkerContinuityTests
    {
        static readonly Vec3 Point=new Vec3(.35,1.5,2.43);
        sealed class Clock : IClock
        {
            public double Now {get;set;}
            public string ClockId {get;set;}="marker-clock";
        }
        sealed class Provider : IViveProEyeProvider
        {
            public ViveProEyePoll Next;
            public ViveProEyePoll Poll(){return Next;}
        }
        sealed class Fixture
        {
            public readonly Clock Clock=new Clock();
            public readonly Provider Provider=new Provider();
            public readonly ViveProEyeHeadEyeSource Source;
            public readonly VivePreviewMarkerContinuity Display=new VivePreviewMarkerContinuity();
            long headSequence;
            public Fixture(string stream="vive-pro-eye/operator/marker-epoch")
            {Source=new ViveProEyeHeadEyeSource("marker",0,0,stream,Clock,Provider,new ViveProEyeRehearsalCoordinates());}
            public ViveProEyeObservation Read(double now,int sequence=10,int timestamp=80,double? observed=null,string fault=null,Rotation? rotation=null,
                Vec3? rawDirection=null)
            {
                Clock.Now=now;double eyeTime=observed??now;
                Vec3 direction=rawDirection??(fault=="nonunit"?new Vec3(0,0,2):fault=="changed-direction"?new Vec3(1,0,0):new Vec3(0,0,1));
                ulong mask=fault=="mask"?0UL:fault=="changed-mask"?3UL:2UL;
                var ray=fault=="combined"?null:new ViveEyeRay(mask,false,fault!="direction",new Vec3(),direction);
                var eye=fault=="missing"?null:new ViveEyeFrame(sequence,timestamp,eyeTime,fault=="presence",null,null,ray);
                var head=new ViveHeadFrame(++headSequence,now,fault!="head-device",fault!="head-valid",
                    fault=="head-unit"?new Rotation(0,0,0,2):rotation??Rotation.Identity);
                Provider.Next=new ViveProEyePoll(head,eye,fault=="runtime"?ViveEyeRuntimeState.Error:ViveEyeRuntimeState.Working,
                    fault=="calibration"?ViveEyeCalibrationState.Required:ViveEyeCalibrationState.ReportedReady,
                    fault!="eye-device",fault=="native"?1023:0);
                HeadEyeSample sample;Source.TryRead(out sample);return Source.LastObservation;
            }
            public void Fresh(double now=0,Vec3? rawDirection=null)
            {Check.Equal(VivePreviewMarkerState.Fresh,Display.Update(Read(now,rawDirection:rawDirection),now,true,Point));}
        }
        static void Hidden(VivePreviewMarkerContinuity display)
        {Check.Equal(VivePreviewMarkerState.Hidden,display.State);Check.True(!display.Point.HasValue);Check.True(Double.IsNaN(display.AgeSeconds));}
        static void SamePoint(Vec3 expected,Vec3? actual)
        {Check.True(actual.HasValue);Check.Near(expected.X,actual.Value.X);Check.Near(expected.Y,actual.Value.Y);Check.Near(expected.Z,actual.Value.Z);}

        public static void Register(TestSuite suite)
        {
            suite.Run("VIVE marker retains healthy duplicate cadence without inventing source validity or counts",()=>{
                var f=new Fixture();f.Fresh();var observation=f.Read(.01,observed:0);
                Check.Equal(ViveFrameDisposition.Duplicate,observation.EyeDisposition);Check.True(!observation.EyeUsable&&!observation.Sample.EyeValid);
                long emitted=f.Source.EmittedSamples,rejected=f.Source.RejectedEyeFrames;
                Check.Equal(VivePreviewMarkerState.Retained,f.Display.Update(observation,.01,true,new Vec3(9,9,9)));
                SamePoint(Point,f.Display.Point);Check.Near(.01,f.Display.AgeSeconds);
                Check.Equal(VivePreviewMarkerState.Retained,f.Display.Update(observation,.02,true,null));
                Check.Equal(emitted,f.Source.EmittedSamples);Check.Equal(rejected,f.Source.RejectedEyeFrames);
                Check.True(!observation.EyeUsable&&!observation.Sample.EyeValid);Check.Equal(ViveFrameDisposition.Duplicate,observation.EyeDisposition);
                var next=f.Read(.03,11,81);var nextPoint=new Vec3(-.35,1.5,2.43);
                Check.Equal(VivePreviewMarkerState.Fresh,f.Display.Update(next,.03,true,nextPoint));SamePoint(nextPoint,f.Display.Point);
            });
            suite.Run("VIVE marker retains identical convertible raw directions without inventing a new eye sample",()=>{
                var raw=new Vec3(0,0,2);var f=new Fixture();f.Fresh(1,raw);
                var accepted=f.Source.LastObservation;
                Check.True(accepted.EyeUsable&&accepted.Sample.EyeValid);
                Check.Equal(2.0,accepted.RawPoll.Eye.Combined.Direction.Z);
                Check.Near(1,accepted.Sample.RawEyeDirection.Z,1e-12);
                var duplicate=f.Read(1.02,observed:1,rawDirection:raw);
                Check.Equal(ViveFrameDisposition.Duplicate,duplicate.EyeDisposition);
                Check.True(!duplicate.EyeUsable&&!duplicate.Sample.EyeValid);
                long emitted=f.Source.EmittedSamples,rejected=f.Source.RejectedEyeFrames;
                Check.Equal(VivePreviewMarkerState.Retained,f.Display.Update(duplicate,1.02,true,new Vec3(9,9,9)));
                SamePoint(Point,f.Display.Point);Check.Near(.02,f.Display.AgeSeconds);
                Check.Equal(emitted,f.Source.EmittedSamples);Check.Equal(rejected,f.Source.RejectedEyeFrames);
                Check.Equal(2.0,duplicate.RawPoll.Eye.Combined.Direction.Z);
                Check.True(!duplicate.EyeUsable&&!duplicate.Sample.EyeValid);
            });
            suite.Run("VIVE marker changed raw magnitude clears even when derived direction is identical",()=>{
                var f=new Fixture();f.Fresh(1,new Vec3(0,0,2));
                var changed=f.Read(1.02,observed:1,rawDirection:new Vec3(0,0,3));
                Check.Equal(ViveFrameDisposition.Duplicate,changed.EyeDisposition);
                Vec3 firstUnit,changedUnit;
                Check.True(ViveProEyeSdkDirection.TryGetUnitDirection(new Vec3(0,0,2),out firstUnit));
                Check.True(ViveProEyeSdkDirection.TryGetUnitDirection(changed.RawPoll.Eye.Combined.Direction,out changedUnit));
                Check.Equal(firstUnit.Z,changedUnit.Z);
                f.Display.Update(changed,1.02,true,null);Hidden(f.Display);
                f.Display.Update(f.Read(1.03,observed:1,rawDirection:new Vec3(0,0,2)),1.03,true,null);Hidden(f.Display);
            });
            suite.Run("VIVE marker nonfinite degenerate and invalid-native duplicate directions clear converted gaze",()=>{
                foreach(var raw in new[]{new Vec3(Double.NaN,0,1),new Vec3(0,Double.PositiveInfinity,1),new Vec3(0,0,0),
                    new Vec3(0,0,ViveProEyeSdkDirection.MinimumRayMagnitude),new Vec3(Double.MaxValue,Double.MaxValue,0)})
                {
                    var f=new Fixture();f.Fresh(1,new Vec3(0,0,2));
                    f.Display.Update(f.Read(1.02,observed:1,rawDirection:raw),1.02,true,null);Hidden(f.Display);
                }
                foreach(string fault in new[]{"mask","direction"})
                {
                    var f=new Fixture();f.Fresh(1,new Vec3(0,0,2));
                    f.Display.Update(f.Read(1.02,observed:1,fault:fault,rawDirection:new Vec3(0,0,2)),1.02,true,null);Hidden(f.Display);
                }
            });
            suite.Run("VIVE marker expiry uses original eye observation despite repeated native read times",()=>{
                var f=new Fixture();f.Fresh();
                Check.Equal(VivePreviewMarkerState.Retained,f.Display.Update(f.Read(.05),.05,true,null));Check.Near(.05,f.Display.AgeSeconds);
                Check.Equal(VivePreviewMarkerState.Retained,f.Display.Update(f.Read(.1),.1,true,null));Check.Near(.1,f.Display.AgeSeconds);
                f.Display.Update(f.Read(.100001),.100001,true,null);Hidden(f.Display);
                f.Display.Update(f.Read(.11),.11,true,null);Hidden(f.Display);
                Check.Equal(VivePreviewMarkerState.Fresh,f.Display.Update(f.Read(.12,11,81),.12,true,Point));
            });
            suite.Run("VIVE marker clears same-frame invalidity and changed payload before retaining",()=>{
                foreach(string fault in new[]{"runtime","calibration","eye-device","native","presence","combined","direction","nonunit",
                    "mask","changed-mask","changed-direction","head-device","head-valid","head-unit"})
                {
                    var f=new Fixture();f.Fresh();var observation=f.Read(.01,observed:0,fault:fault);
                    Check.Equal(ViveFrameDisposition.Duplicate,observation.EyeDisposition);
                    f.Display.Update(observation,.01,true,null);Hidden(f.Display);
                    f.Display.Update(f.Read(.02,observed:0),.02,true,null);Hidden(f.Display);
                    Check.Equal(VivePreviewMarkerState.Fresh,f.Display.Update(f.Read(.03,11,81),.03,true,Point));
                }
            });
            suite.Run("VIVE marker current head readiness loss clears and duplicates cannot restore it",()=>{
                var f=new Fixture();f.Fresh();f.Display.Update(f.Read(.01,observed:0),.01,false,null);Hidden(f.Display);
                f.Display.Update(f.Read(.02,observed:0),.02,true,null);Hidden(f.Display);
                Check.Equal(VivePreviewMarkerState.Fresh,f.Display.Update(f.Read(.03,11,81),.03,true,Point));
            });
            suite.Run("VIVE marker holds world point through duplicate head motion without reprojection",()=>{
                var f=new Fixture();f.Fresh();var yaw=new Rotation(0,Math.Sin(.1),0,Math.Cos(.1));
                Check.Equal(VivePreviewMarkerState.Retained,f.Display.Update(f.Read(.01,observed:0,rotation:yaw),.01,true,new Vec3(1,1,1)));
                SamePoint(Point,f.Display.Point);Check.Near(.01,f.Display.AgeSeconds);
            });
            suite.Run("VIVE marker missing stale reordered and future eye observations clear previous point",()=>{
                foreach(string fault in new[]{"missing","stale","reordered","future","old-duplicate"})
                {
                    var f=new Fixture();f.Fresh(1);
                    var observation=f.Read(1.02,sequence:fault=="reordered"?9:10,
                        observed:fault=="stale"?.8:fault=="future"?1.03:fault=="old-duplicate"?.99:1,
                        fault:fault=="missing"?"missing":null);
                    f.Display.Update(observation,1.02,true,null);Hidden(f.Display);
                }
            });
            suite.Run("VIVE marker changed native timestamp cannot impersonate the accepted frame",()=>{
                var f=new Fixture();f.Fresh();f.Display.Update(f.Read(.01,timestamp:81),.01,true,null);Hidden(f.Display);
            });
            suite.Run("VIVE marker fresh offboard or nonfinite projection clears previous marker",()=>{
                foreach(Vec3? projected in new Vec3?[]{null,new Vec3(Double.NaN,1,2),new Vec3(1,Double.PositiveInfinity,2)})
                {
                    var f=new Fixture();f.Fresh();f.Display.Update(f.Read(.01,11,81),.01,true,projected);Hidden(f.Display);
                    f.Display.Update(f.Read(.02,11,81),.02,true,null);Hidden(f.Display);
                }
            });
            suite.Run("VIVE marker reset and stream epoch change cannot inherit a prior point",()=>{
                var f=new Fixture();f.Fresh();f.Display.Reset();Hidden(f.Display);
                f.Display.Update(f.Read(.01),.01,true,null);Hidden(f.Display);
                Check.Equal(VivePreviewMarkerState.Fresh,f.Display.Update(f.Read(.02,11,81),.02,true,Point));
            });
            suite.Run("VIVE marker rejects duplicate identity from another source epoch",()=>{
                var f=new Fixture();f.Fresh();var other=new Fixture("vive-pro-eye/operator/other-epoch");other.Read(0);
                f.Display.Update(other.Read(.01),.01,true,null);Hidden(f.Display);
                Check.Equal(VivePreviewMarkerState.Fresh,f.Display.Update(other.Read(.02,11,81),.02,true,Point));
            });
            suite.Run("VIVE marker clock faults latch hidden until explicit reset",()=>{
                foreach(double bad in new[]{-.01,Double.NaN,Double.PositiveInfinity,.99})
                {
                    var f=new Fixture();f.Fresh(1);var duplicate=f.Read(1.01,observed:1);
                    f.Display.Update(duplicate,bad,true,null);Hidden(f.Display);
                    f.Display.Update(f.Read(1.02,11,81),1.02,true,Point);Hidden(f.Display);
                    f.Display.Reset();Check.Equal(VivePreviewMarkerState.Fresh,f.Display.Update(f.Read(1.03,12,82),1.03,true,Point));
                }
            });
        }
    }
}
