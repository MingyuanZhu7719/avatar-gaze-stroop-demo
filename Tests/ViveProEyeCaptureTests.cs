using System;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    public static class ViveProEyeCaptureTests
    {
        sealed class Provider : IViveProEyeProvider
        {
            public ViveProEyePoll Next;
            public Func<ViveProEyePoll> Read;
            public int Calls;
            public ViveProEyePoll Poll(){Calls++;return Read==null?Next:Read();}
        }
        sealed class Clock : IClock
        {
            public double Now {get;set;}
            public string ClockId {get;set;}="vive-test-clock";
        }
        sealed class InvalidMapper : IViveProEyeCoordinateMapper
        {
            public string Version {get;set;}="bad-mapper";
            public bool Throw;
            public bool TryMapHead(Rotation raw,out Rotation mapped)
            {if(Throw)throw new InvalidOperationException("private path");mapped=new Rotation(0,0,0,2);return true;}
            public bool TryMapEye(Rotation head,Vec3 raw,out Vec3 mapped)
            {mapped=new Vec3(0,0,3);return true;}
        }
        static readonly Vec3 Forward=new Vec3(0,0,1);
        static ViveEyeRay Ray(Vec3? direction=null,bool valid=true,ulong mask=2)
        {return new ViveEyeRay(mask,false,valid,new Vec3(Double.NaN,123,-456),direction??Forward);}
        static ViveHeadFrame Head(long sequence,double observed,Rotation? rotation=null,bool valid=true,bool connected=true)
        {return new ViveHeadFrame(sequence,observed,connected,valid,rotation??Rotation.Identity);}
        static ViveEyeFrame Eye(int sequence,int timestamp,double observed,Vec3? direction=null,bool valid=true,bool noUser=false)
        {return new ViveEyeFrame(sequence,timestamp,observed,noUser,Ray(new Vec3(1,0,0),false,ulong.MaxValue),null,Ray(direction,valid));}
        static ViveProEyePoll Poll(long headSequence,int eyeSequence,double observed,int? timestamp=null,
            ViveEyeRuntimeState runtime=ViveEyeRuntimeState.Working,ViveEyeCalibrationState calibration=ViveEyeCalibrationState.ReportedReady,
            bool eyeConnected=true,int? code=0)
        {return new ViveProEyePoll(Head(headSequence,observed),Eye(eyeSequence,timestamp??eyeSequence*8,observed),runtime,calibration,eyeConnected,code,2);}
        static ViveProEyeHeadEyeSource Source(Clock clock,Provider provider,IViveProEyeCoordinateMapper mapper=null,double age=.1,string stream="vive-pro-eye/operator/test-epoch-1")
        {return new ViveProEyeHeadEyeSource("diagnostic-session",1,2,stream,clock,provider,mapper??new ViveProEyeRehearsalCoordinates(),age);}
        static HeadEyeSample Read(ViveProEyeHeadEyeSource source)
        {HeadEyeSample sample;Check.True(source.TryRead(out sample));Check.True(sample!=null);return sample;}

        public static void Register(TestSuite suite)
        {
            suite.Run("VIVE preserves raw rays and provenance with observation-only timestamps",()=>{
                var clock=new Clock {Now=10};var provider=new Provider {Next=Poll(5,71,9.98,timestamp:887123)};var source=Source(clock,provider);
                Check.True(!source.Available);var sample=Read(source);var observation=source.LastObservation;
                Check.Equal(ControlSource.LiveOperator,sample.Source);Check.Equal(StreamRole.OperatorControl,sample.Role);
                Check.Equal("vive-pro-eye/operator/test-epoch-1",sample.StreamId);Check.Equal("diagnostic-session",sample.SessionId);
                Check.Equal("vive-pro-eye-no-serial",sample.DeviceId);Check.Equal("vive-test-clock",sample.ClockId);
                Check.Equal(1,sample.BlockIndex);Check.Equal(2,sample.Segment);Check.Equal(0L,sample.Sequence);
                Check.Near(9.98,sample.CaptureLocalSeconds);Check.Near(10,sample.SendLocalSeconds);
                Check.Equal(887123,observation.RawPoll.Eye.Timestamp);Check.Equal(71,observation.RawPoll.Eye.FrameSequence);
                Check.Equal(ulong.MaxValue,observation.RawPoll.Eye.Left.ValidityMask);Check.True(Double.IsNaN(observation.RawPoll.Eye.Combined.OriginMillimeters.X));
                Check.Equal(ViveTimestampBasis.LocalPollObservation,observation.TimestampBasis);Check.True(!observation.HardwareCaptureTimeVerified);
                Check.True(!observation.MappingValidatedForApparatus&&!source.MappingValidatedForApparatus);
                Check.True(sample.HeadValid&&sample.EyeValid&&source.Available);Check.Equal(1L,source.EmittedSamples);
                clock.Now=10.2;Check.True(!source.Available);
            });
            suite.Run("VIVE mapper derives SDK ray direction reflects X and rotates once",()=>{
                var mapper=new ViveProEyeRehearsalCoordinates();Vec3 result;Rotation head;
                Check.True(mapper.TryMapEye(Rotation.Identity,new Vec3(1,0,0),out result));Check.Near(-1,result.X);Check.Near(0,result.Z);
                var yaw=new Rotation(0,Math.Sqrt(.5),0,Math.Sqrt(.5));
                Check.True(mapper.TryMapEye(yaw,Forward,out result));Check.Near(1,result.X);Check.Near(0,result.Z);
                Check.True(mapper.TryMapHead(yaw,out head));Check.Near(yaw.Y,head.Y);
                Check.True(mapper.TryMapEye(yaw,new Vec3(0,0,2),out result));Check.Near(1,result.X);Check.Near(0,result.Z);
                Check.True(!mapper.TryMapEye(new Rotation(0,0,0,2),Forward,out result));
                Check.True(!mapper.TryMapHead(new Rotation(0,0,0,0),out head));Check.Near(0,head.LengthSquared);
            });
            suite.Run("VIVE accepted float quaternion rotates without scaling and preserves raw observations",()=>{
                var rotation=new Rotation(0,0,0,1.0000003576278687f);
                Check.True(rotation.IsUnit&&rotation.LengthSquared>1);
                var clock=new Clock {Now=1};var provider=new Provider {Next=new ViveProEyePoll(Head(1,1,rotation),Eye(1,1,1),
                    ViveEyeRuntimeState.Working,ViveEyeCalibrationState.ReportedReady,true,0)};
                var source=Source(clock,provider);var sample=Read(source);
                Check.True(sample.HeadValid&&sample.EyeValid);
                Check.Near(1,sample.RawEyeDirection.Z,1e-12);Check.Near(1,sample.RawEyeDirection.LengthSquared,1e-12);
                Check.Equal(rotation.W,sample.RawHeadRotation.W);
                Check.Equal(rotation.W,source.LastObservation.RawPoll.Head.RawTrackingRotation.W);
                Check.Equal(Forward.Z,source.LastObservation.RawPoll.Eye.Combined.Direction.Z);
            });
            suite.Run("VIVE rotation preserves derived unit ray near both quaternion norm boundaries",()=>{
                var mapper=new ViveProEyeRehearsalCoordinates();
                var direction=new Vec3(0,0,1.0000003576278687f);
                Check.True(direction.IsUnit&&direction.LengthSquared>1);
                foreach(float component in new[]{.7071070671081543f,.7071064710617065f})
                {
                    var yaw=new Rotation(0,component,0,component);Vec3 result;Rotation mappedHead;
                    Check.True(yaw.IsUnit&&Math.Abs(yaw.LengthSquared-1)>.0000008);
                    Check.True(mapper.TryMapEye(yaw,direction,out result));
                    Check.Near(1,result.X,1e-12);Check.Near(0,result.Y,1e-12);Check.Near(0,result.Z,1e-12);
                    Check.Near(1,result.LengthSquared,1e-12);
                    Check.Equal((double)1.0000003576278687f,direction.Z);
                    Check.True(mapper.TryMapHead(yaw,out mappedHead));
                    Check.Equal(yaw.Y,mappedHead.Y);Check.Equal(yaw.W,mappedHead.W);
                }
            });
            suite.Run("VIVE SDK direction conversion leaves shared unit rules and quaternion rejection unchanged",()=>{
                var mapper=new ViveProEyeRehearsalCoordinates();Vec3 result;
                var rejectedRotation=new Rotation(0,0,0,1.0000005960464478f);
                var rawDirection=new Vec3(0,0,1.0000005960464478f);
                Check.True(!rejectedRotation.IsUnit&&!rawDirection.IsUnit);
                Check.True(!mapper.TryMapEye(rejectedRotation,Forward,out result));Check.True(Double.IsNaN(result.Z));
                Check.True(mapper.TryMapEye(Rotation.Identity,rawDirection,out result));Check.Near(1,result.Z,1e-12);
                Check.Equal((double)1.0000005960464478f,rejectedRotation.W);
                Check.Equal((double)1.0000005960464478f,rawDirection.Z);Check.True(!rawDirection.IsUnit);
            });
            suite.Run("VIVE SDK ray conversion safely derives scale-independent unit directions",()=>{
                foreach(double scale in new[]{.125,.9998477,1.0,2.0,1e10})
                {
                    var raw=new Vec3(3*scale,4*scale,0);Vec3 derived;
                    Check.Equal(ViveSdkDirectionStatus.Convertible,ViveProEyeSdkDirection.ConvertForRay(raw,out derived));
                    Check.True(derived.IsUnit);Check.Near(.6,derived.X,1e-12);Check.Near(.8,derived.Y,1e-12);Check.Near(0,derived.Z,1e-12);
                    Check.Equal(3*scale,raw.X);Check.Equal(4*scale,raw.Y);
                }
                double minimum=ViveProEyeSdkDirection.MinimumRayMagnitude;Vec3 near;
                Check.True(ViveProEyeSdkDirection.TryGetUnitDirection(new Vec3(minimum*.8,minimum*.8,0),out near));
                Check.True(near.IsUnit);
                Check.True(ViveProEyeSdkDirection.TryGetUnitDirection(new Vec3(0,0,minimum*(1+1e-6)),out near));
                Check.Near(1,near.Z,1e-12);
            });
            suite.Run("VIVE SDK ray conversion rejects nonfinite degenerate and overflowing magnitudes",()=>{
                double minimum=ViveProEyeSdkDirection.MinimumRayMagnitude;
                var rejected=new[]{new Vec3(Double.NaN,0,1),new Vec3(0,Double.PositiveInfinity,1),
                    new Vec3(0,0,0),new Vec3(0,0,minimum*.5),new Vec3(0,0,minimum),
                    new Vec3(Double.MaxValue,Double.MaxValue,0),new Vec3(1e30f,0,0),new Vec3(Single.MaxValue,0,0)};
                var expected=new[]{ViveSdkDirectionStatus.Nonfinite,ViveSdkDirectionStatus.Nonfinite,
                    ViveSdkDirectionStatus.Degenerate,ViveSdkDirectionStatus.Degenerate,ViveSdkDirectionStatus.Degenerate,
                    ViveSdkDirectionStatus.MagnitudeOverflow,ViveSdkDirectionStatus.MagnitudeOverflow,ViveSdkDirectionStatus.MagnitudeOverflow};
                for(int i=0;i<rejected.Length;i++)
                {
                    Vec3 derived;
                    Check.Equal(expected[i],ViveProEyeSdkDirection.ConvertForRay(rejected[i],out derived));
                    Check.True(!derived.IsFinite);
                    Check.True(!ViveProEyeSdkDirection.TryGetUnitDirection(rejected[i],out derived));Check.True(!derived.IsFinite);
                }
            });
            suite.Run("VIVE source retains raw nonunit gaze while emitting SDK-derived mapped direction",()=>{
                var yaw=new Rotation(0,Math.Sqrt(.5),0,Math.Sqrt(.5));
                foreach(double scale in new[]{.125,.9998477,1.0,2.0})
                {
                    var rawDirection=new Vec3(3*scale,0,4*scale);var eye=Eye(1,1,1,rawDirection);
                    var provider=new Provider {Next=new ViveProEyePoll(Head(1,1,yaw),eye,
                        ViveEyeRuntimeState.Working,ViveEyeCalibrationState.ReportedReady,true,0)};
                    var source=Source(new Clock {Now=1},provider);var sample=Read(source);
                    Check.True(sample.HeadValid&&sample.EyeValid&&source.LastObservation.EyeUsable);
                    Check.Near(.8,sample.RawEyeDirection.X,1e-12);Check.Near(0,sample.RawEyeDirection.Y,1e-12);Check.Near(.6,sample.RawEyeDirection.Z,1e-12);
                    Check.Equal(rawDirection.X,source.LastObservation.RawPoll.Eye.Combined.Direction.X);
                    Check.Equal(rawDirection.Z,source.LastObservation.RawPoll.Eye.Combined.Direction.Z);
                    Check.Equal(2UL,source.LastObservation.RawPoll.Eye.Combined.ValidityMask);
                    Check.Equal(yaw.Y,source.LastObservation.RawPoll.Head.RawTrackingRotation.Y);
                    Check.Equal(null,source.LastObservation.EyeFailureReason);
                }
            });
            suite.Run("VIVE direction conversion never revives native-invalid mask or flag",()=>{
                foreach(var ray in new[]{Ray(new Vec3(0,0,2),valid:false,mask:2),Ray(new Vec3(0,0,2),valid:true,mask:0)})
                {
                    var eye=new ViveEyeFrame(1,1,1,false,null,null,ray);
                    var provider=new Provider {Next=new ViveProEyePoll(Head(1,1),eye,ViveEyeRuntimeState.Working,ViveEyeCalibrationState.ReportedReady,true,0)};
                    var source=Source(new Clock {Now=1},provider);var sample=Read(source);
                    Check.True(sample.HeadValid&&!sample.EyeValid);Check.True(!source.LastObservation.EyeUsable);
                    Check.Equal("Combined eye direction is not reported valid",source.LastObservation.EyeFailureReason);
                    Check.Equal(ray.ValidityMask,source.LastObservation.RawPoll.Eye.Combined.ValidityMask);
                    Check.Equal(2.0,source.LastObservation.RawPoll.Eye.Combined.Direction.Z);
                }
            });
            suite.Run("VIVE rejects malformed math while retaining reported validity and original values",()=>{
                foreach(Vec3 direction in new[]{new Vec3(0,0,0),new Vec3(0,0,ViveProEyeSdkDirection.MinimumRayMagnitude),
                    new Vec3(Double.NaN,0,1),new Vec3(0,Double.PositiveInfinity,1),new Vec3(Double.MaxValue,Double.MaxValue,0)})
                {
                    var clock=new Clock {Now=1};var eye=Eye(1,1,1,direction);
                    var provider=new Provider {Next=new ViveProEyePoll(Head(1,1),eye,ViveEyeRuntimeState.Working,ViveEyeCalibrationState.ReportedReady,true,0)};
                    var source=Source(clock,provider);var sample=Read(source);Check.True(sample.HeadValid&&!sample.EyeValid);
                    string reason=!direction.IsFinite?"Combined eye direction has nonfinite components":
                        direction.X==Double.MaxValue?"Combined eye direction magnitude is not finite":
                        "Combined eye direction is zero or below the SDK ray magnitude threshold";
                    Check.Equal(reason,source.LastObservation.EyeFailureReason);
                    Check.True(source.LastObservation.RawPoll.Eye.Combined.DirectionReportedValid);
                    Check.Equal(direction.Z,source.LastObservation.RawPoll.Eye.Combined.Direction.Z);
                }
                foreach(Rotation rotation in new[]{new Rotation(0,0,0,0),new Rotation(0,0,0,2),new Rotation(Double.NaN,0,0,1)})
                {
                    var clock=new Clock {Now=1};var provider=new Provider {Next=new ViveProEyePoll(Head(1,1,rotation),Eye(1,1,1),ViveEyeRuntimeState.Working,ViveEyeCalibrationState.ReportedReady,true,0)};
                    var source=Source(clock,provider);var sample=Read(source);Check.True(!sample.HeadValid&&!sample.EyeValid);
                    Check.Equal("Head rotation is nonfinite or nonunit",source.LastObservation.EyeFailureReason);
                    Check.True(source.LastObservation.RawPoll.Head.RotationReportedValid);Check.Equal(rotation.W,sample.RawHeadRotation.W);
                }
            });
            suite.Run("VIVE head remains independent of eye runtime device calibration and native failure",()=>{
                var cases=new[]{Poll(1,1,1,runtime:ViveEyeRuntimeState.Unknown),Poll(1,1,1,runtime:ViveEyeRuntimeState.Error),
                    Poll(1,1,1,runtime:ViveEyeRuntimeState.Unavailable),Poll(1,1,1,calibration:ViveEyeCalibrationState.Unknown),
                    Poll(1,1,1,calibration:ViveEyeCalibrationState.Required),Poll(1,1,1,eyeConnected:false),Poll(1,1,1,code:1023)};
                var reasons=new[]{"Eye runtime: Unknown","Eye runtime: Error","Eye runtime: Unavailable","Eye calibration: Unknown",
                    "Eye calibration: Required","Eye device is disconnected","Eye native result: 1023"};
                for(int i=0;i<cases.Length;i++)
                {
                    var poll=cases[i];
                    var source=Source(new Clock {Now=1},new Provider {Next=poll});var sample=Read(source);
                    Check.True(sample.HeadValid&&!sample.EyeValid);Check.True(source.LastObservation.RawPoll.Eye.Combined.DirectionReportedValid);
                    Check.Equal(poll.NativeResultCode,source.LastObservation.RawPoll.NativeResultCode);Check.True(source.Available);
                    Check.Equal(null,source.LastObservation.FailureReason);Check.Equal(reasons[i],source.LastObservation.EyeFailureReason);
                }
            });
            suite.Run("VIVE no-user and raw invalid direction never fall back to head-forward",()=>{
                foreach(var eye in new[]{Eye(1,1,1,noUser:true),Eye(1,1,1,valid:false)})
                {
                    var source=Source(new Clock {Now=1},new Provider {Next=new ViveProEyePoll(Head(1,1),eye,ViveEyeRuntimeState.Working,ViveEyeCalibrationState.ReportedReady,true,0)});
                    var sample=Read(source);Check.True(sample.HeadValid&&!sample.EyeValid);Check.True(Double.IsNaN(sample.RawEyeDirection.Z));
                    Check.Equal(eye.NoUser?"HTC reports no available eye data":"Combined eye direction is not reported valid",source.LastObservation.EyeFailureReason);
                }
            });
            suite.Run("VIVE idle wearer transition reports eye gates and recovers only on a fresh frame",()=>{
                var clock=new Clock {Now=1};var provider=new Provider {Next=new ViveProEyePoll(Head(1,1,valid:false),Eye(0,0,1,noUser:true),
                    ViveEyeRuntimeState.Working,ViveEyeCalibrationState.ReportedReady,true,0)};var source=Source(clock,provider);
                var idle=Read(source);Check.True(!idle.HeadValid&&!idle.EyeValid);Check.Equal("HTC reports no available eye data",source.LastObservation.EyeFailureReason);
                clock.Now=1.01;provider.Next=Poll(2,0,1.01,timestamp:0);
                var repeated=Read(source);Check.True(repeated.HeadValid&&!repeated.EyeValid);Check.Equal("Eye frame: Duplicate",source.LastObservation.EyeFailureReason);
                clock.Now=1.02;provider.Next=Poll(3,1,1.02,timestamp:1);
                var recovered=Read(source);Check.True(recovered.HeadValid&&recovered.EyeValid);Check.Equal(null,source.LastObservation.EyeFailureReason);
                Check.Equal(1L,source.RejectedEyeFrames);
            });
            suite.Run("VIVE combined ray and current head gates remain distinguishable",()=>{
                var clock=new Clock {Now=1};var missingCombined=new ViveEyeFrame(1,1,1,false,Ray(),Ray(),null);
                var provider=new Provider {Next=new ViveProEyePoll(Head(1,1),missingCombined,ViveEyeRuntimeState.Working,ViveEyeCalibrationState.ReportedReady,true,0)};
                var source=Source(clock,provider);Check.True(!Read(source).EyeValid);Check.Equal("Combined eye ray is missing",source.LastObservation.EyeFailureReason);
                clock.Now=1.01;provider.Next=new ViveProEyePoll(Head(2,1.01,connected:false),Eye(2,2,1.01),ViveEyeRuntimeState.Working,ViveEyeCalibrationState.ReportedReady,true,0);
                Check.True(!Read(source).EyeValid);Check.Equal("Head device is disconnected",source.LastObservation.EyeFailureReason);
                clock.Now=1.02;provider.Next=new ViveProEyePoll(Head(3,1.02,valid:false),Eye(3,3,1.02),ViveEyeRuntimeState.Working,ViveEyeCalibrationState.ReportedReady,true,0);
                Check.True(!Read(source).EyeValid);Check.Equal("Head rotation is not reported valid",source.LastObservation.EyeFailureReason);
            });
            suite.Run("VIVE duplicate eyes do not become fresh when head or calibration changes",()=>{
                var clock=new Clock {Now=1};var provider=new Provider {Next=Poll(1,1,1,calibration:ViveEyeCalibrationState.Unknown)};var source=Source(clock,provider);
                Check.True(!Read(source).EyeValid);clock.Now=1.01;provider.Next=Poll(2,1,1.01);
                var sample=Read(source);Check.True(sample.HeadValid&&!sample.EyeValid);Check.Equal(ViveFrameDisposition.Duplicate,source.LastObservation.EyeDisposition);
                Check.Equal("Eye frame: Duplicate",source.LastObservation.EyeFailureReason);
                provider.Next=Poll(2,1,1.01);HeadEyeSample none;Check.True(!source.TryRead(out none));Check.Equal(null,none);
                Check.Equal(2L,source.EmittedSamples);Check.Equal(2L,source.RejectedEyeFrames);Check.Equal(1L,source.RejectedHeadFrames);
                clock.Now=1.02;provider.Next=Poll(3,2,1.02);Check.True(Read(source).EyeValid);
            });
            suite.Run("VIVE rejects reordered eye sequence timestamp and head observations",()=>{
                var clock=new Clock {Now=1};var provider=new Provider {Next=Poll(8,8,1,timestamp:80)};var source=Source(clock,provider);Read(source);
                clock.Now=1.01;provider.Next=Poll(9,7,1.01,timestamp:81);Check.True(!Read(source).EyeValid);Check.Equal(ViveFrameDisposition.OutOfOrder,source.LastObservation.EyeDisposition);
                clock.Now=1.02;provider.Next=Poll(10,9,1.02,timestamp:80);Check.True(!Read(source).EyeValid);Check.Equal(ViveFrameDisposition.OutOfOrder,source.LastObservation.EyeDisposition);
                clock.Now=1.03;provider.Next=Poll(7,10,1.03,timestamp:82);var sample=Read(source);
                Check.True(!sample.HeadValid&&!sample.EyeValid);Check.Equal(ViveFrameDisposition.OutOfOrder,source.LastObservation.HeadDisposition);
                Check.True(source.LastObservation.RawPoll.Eye.Combined.DirectionReportedValid);
                clock.Now=1.04;provider.Next=Poll(11,11,1.04,timestamp:83);Check.True(Read(source).EyeValid);
            });
            suite.Run("VIVE per-channel stale observations cannot borrow freshness",()=>{
                var clock=new Clock {Now=10};var provider=new Provider {Next=new ViveProEyePoll(Head(1,10),Eye(1,1,9),ViveEyeRuntimeState.Working,ViveEyeCalibrationState.ReportedReady,true,0)};
                var source=Source(clock,provider);var first=Read(source);Check.True(first.HeadValid&&!first.EyeValid);Check.Equal(ViveFrameDisposition.Stale,source.LastObservation.EyeDisposition);
                clock.Now=10.01;provider.Next=new ViveProEyePoll(Head(2,9),Eye(2,2,10.01),ViveEyeRuntimeState.Working,ViveEyeCalibrationState.ReportedReady,true,0);
                var second=Read(source);Check.True(!second.HeadValid&&!second.EyeValid);Check.Equal(ViveFrameDisposition.Stale,source.LastObservation.HeadDisposition);
                Check.Equal(ViveFrameDisposition.Accepted,source.LastObservation.EyeDisposition);
                provider.Next=new ViveProEyePoll(Head(3,9),Eye(3,3,9),ViveEyeRuntimeState.Working,ViveEyeCalibrationState.ReportedReady,true,0);
                HeadEyeSample none;Check.True(!source.TryRead(out none));Check.True(!source.Available);
            });
            suite.Run("VIVE invalid future or nonfinite observation times preserve raw data and fail closed",()=>{
                foreach(double observed in new[]{Double.NaN,Double.PositiveInfinity,-1,1.01})
                {
                    var clock=new Clock {Now=1};var provider=new Provider {Next=Poll(1,1,observed)};var source=Source(clock,provider);HeadEyeSample none;
                    Check.True(!source.TryRead(out none));Check.Equal(ViveFrameDisposition.InvalidObservationTime,source.LastObservation.HeadDisposition);
                    Check.Equal(ViveFrameDisposition.InvalidObservationTime,source.LastObservation.EyeDisposition);
                    Check.Equal(observed,source.LastObservation.RawPoll.Eye.ObservedLocalSeconds);
                }
            });
            suite.Run("VIVE conservative sample time is oldest included local observation",()=>{
                var clock=new Clock {Now=5};var provider=new Provider {Read=()=>{clock.Now=5.02;return new ViveProEyePoll(Head(1,5.01),Eye(1,123456,5.005),ViveEyeRuntimeState.Working,ViveEyeCalibrationState.ReportedReady,true,0);}};
                var source=Source(clock,provider);var sample=Read(source);Check.Near(5.005,sample.CaptureLocalSeconds);Check.Near(5.02,sample.SendLocalSeconds);
                Check.Near(5,source.LastObservation.PollStartedLocalSeconds);Check.Near(5.02,source.LastObservation.PollCompletedLocalSeconds);
                Check.True(!source.LastObservation.HardwareCaptureTimeVerified);
            });
            suite.Run("VIVE opaque signed native counter wrap and reset require a new source epoch",()=>{
                var clock=new Clock {Now=1};var provider=new Provider {Next=Poll(1,Int32.MaxValue,1,timestamp:Int32.MaxValue)};var source=Source(clock,provider);Read(source);
                clock.Now=1.01;provider.Next=Poll(2,Int32.MinValue,1.01,timestamp:Int32.MinValue);Check.True(!Read(source).EyeValid);
                Check.Equal(ViveFrameDisposition.OutOfOrder,source.LastObservation.EyeDisposition);Check.Equal(Int32.MinValue,source.LastObservation.RawPoll.Eye.Timestamp);
                var negativeBaseline=Source(clock,provider,stream:"vive-pro-eye/operator/negative-epoch");Check.True(Read(negativeBaseline).EyeValid);
                clock.Now=1.015;provider.Next=Poll(3,Int32.MinValue+1,1.015,timestamp:Int32.MinValue+1);Check.True(Read(negativeBaseline).EyeValid);
                clock.Now=1.02;provider.Next=Poll(3,0,1.02,timestamp:0);Check.True(!Read(source).EyeValid);Check.Equal(ViveFrameDisposition.OutOfOrder,source.LastObservation.EyeDisposition);
                var recovered=Source(clock,provider,stream:"vive-pro-eye/operator/test-epoch-2");Check.True(Read(recovered).EyeValid);
            });
            suite.Run("VIVE local clock regression and clock identity change latch source failure",()=>{
                var clock=new Clock {Now=1};var provider=new Provider {Next=Poll(1,1,1)};var source=Source(clock,provider);Read(source);
                clock.Now=.99;HeadEyeSample none;Check.True(!source.TryRead(out none));Check.Equal(1,provider.Calls);
                clock.Now=2;provider.Next=Poll(2,2,2);Check.True(!source.TryRead(out none));Check.Equal(1,provider.Calls);Check.True(!source.Available);
                var second=Source(clock,provider);clock.ClockId="different-clock";Check.True(!second.TryRead(out none));Check.True(second.UnavailableReason.Contains("clock failed"));
                var thirdClock=new Clock {Now=2};var thirdProvider=new Provider {Read=()=>{thirdClock.Now=1;return Poll(1,1,1);}};
                var third=Source(thirdClock,thirdProvider);Check.True(!third.TryRead(out none));Check.True(!third.Available);
            });
            suite.Run("VIVE provider and mapper failures do not reuse previous samples or disclose exception text",()=>{
                var clock=new Clock {Now=1};var provider=new Provider {Next=Poll(1,1,1)};var source=Source(clock,provider);Read(source);
                provider.Read=()=>{throw new InvalidOperationException("secret-device-serial");};HeadEyeSample none;
                Check.True(!source.TryRead(out none));Check.True(!source.Available);Check.Equal(null,source.LastObservation.Sample);
                Check.Equal("Provider failure: InvalidOperationException",source.LastObservation.FailureReason);
                Check.Equal(source.LastObservation.FailureReason,source.LastObservation.EyeFailureReason);
                provider.Read=null;var mapper=new InvalidMapper();var malformed=Source(clock,provider,mapper);var sample=Read(malformed);
                Check.True(!sample.HeadValid&&!sample.EyeValid);Check.True(!malformed.Available);
                Check.Equal("Eye coordinate mapping did not produce a unit direction",malformed.LastObservation.EyeFailureReason);
                mapper.Throw=true;provider.Next=Poll(2,2,1);sample=Read(malformed);Check.True(!sample.HeadValid&&!sample.EyeValid);
                Check.Equal("Coordinate mapper failure: InvalidOperationException",malformed.LastObservation.FailureReason);
                Check.Equal(malformed.LastObservation.FailureReason,malformed.LastObservation.EyeFailureReason);
            });
            suite.Run("VIVE missing channels are explicit and stale cached poses are never returned",()=>{
                var clock=new Clock {Now=1};var provider=new Provider {Next=new ViveProEyePoll(Head(1,1),null,ViveEyeRuntimeState.Unavailable,ViveEyeCalibrationState.Unknown,false)};
                var source=Source(clock,provider);var sample=Read(source);Check.True(sample.HeadValid&&!sample.EyeValid);Check.True(Double.IsNaN(sample.RawEyeDirection.Z));
                Check.Equal("Eye frame: Missing",source.LastObservation.EyeFailureReason);
                clock.Now=1.01;provider.Next=new ViveProEyePoll(null,Eye(1,1,1.01),ViveEyeRuntimeState.Working,ViveEyeCalibrationState.ReportedReady,true,0);
                sample=Read(source);Check.True(!sample.HeadValid&&!sample.EyeValid);Check.True(Double.IsNaN(sample.RawHeadRotation.W));
                Check.Equal("Current head frame: Missing",source.LastObservation.EyeFailureReason);
                provider.Next=null;HeadEyeSample none;Check.True(!source.TryRead(out none));Check.Equal(null,none);
            });
            suite.Run("VIVE injected mapper identity is stable and cannot unlock apparatus mapping",()=>{
                var clock=new Clock {Now=1};var provider=new Provider {Next=Poll(1,1,1)};var source=Source(clock,provider);var sample=Read(source);
                var geometry=new AutomaticGazeGeometry(Rotation.Identity,Forward,Rotation.Identity,Forward,"fixture");
                var controller=new AvatarController("diagnostic-session",RunKind.Apparatus,1,2,ControlSelection.LiveWoz,Condition.A,GazeTarget.Participant,geometry,source.StreamId);
                var output=controller.RenderValidated(Phase.MainTask,0,sample,true,true);Check.Equal(GazeOutputStatus.MappingUnavailable,output.Status);
                Check.True(!output.HasActualPose&&!output.HeadValid&&!output.EyeValid&&!output.MappingValidatedForApparatus);
                var mapper=new InvalidMapper();var changed=Source(clock,provider,mapper);mapper.Version="changed";
                sample=Read(changed);Check.True(!sample.HeadValid&&!sample.EyeValid);Check.Equal("bad-mapper",changed.LastObservation.MappingVersion);
            });
            suite.Run("VIVE rejects ambiguous identity and invalid policy before invoking provider",()=>{
                var clock=new Clock {Now=1};var provider=new Provider();
                foreach(string stream in new[]{"simulator/operator","live/operator","vive-pro-eye/operator/"," "})
                    Check.Throws<ArgumentException>(()=>Source(clock,provider,stream:stream));
                Check.Throws<ArgumentOutOfRangeException>(()=>Source(clock,provider,age:0));
                Check.Throws<ArgumentOutOfRangeException>(()=>Source(clock,provider,age:Double.NaN));
                Check.Throws<ArgumentNullException>(()=>Source(clock,null));Check.Equal(0,provider.Calls);
            });
        }
    }
}
