using System;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    public static class ViveParticipantEyeSourceTests
    {
        sealed class Clock : IClock { public double Now {get;set;} public string ClockId {get;set;}="participant-clock"; }
        sealed class Provider : IViveProEyeProvider { public ViveProEyePoll Next; public bool Throw; public int Calls; public ViveProEyePoll Poll(){Calls++;if(Throw)throw new InvalidOperationException("private eye data");return Next;} }
        static ViveEyeRay Ray(Vec3 direction,bool valid=true,ulong mask=2)
        { return new ViveEyeRay(mask,true,valid,new Vec3(1,2,3),direction); }
        static ViveProEyePoll Poll(int eyeSequence,int timestamp,double eyeTime,Vec3? direction=null,
            bool directionValid=true,ulong mask=2,bool noUser=false,
            ViveEyeRuntimeState runtime=ViveEyeRuntimeState.Working,
            ViveEyeCalibrationState calibration=ViveEyeCalibrationState.ReportedReady,bool connected=true,int? result=0)
        {
            var eye=new ViveEyeFrame(eyeSequence,timestamp,eyeTime,noUser,
                Ray(new Vec3(1,0,0),false,7),Ray(new Vec3(0,1,0),true,6),Ray(direction??new Vec3(0,0,1),directionValid,mask));
            return new ViveProEyePoll(new ViveHeadFrame(eyeSequence,eyeTime+.01,true,true,Rotation.Identity),eye,runtime,calibration,connected,result,2);
        }
        static ViveParticipantEyeSource Source(Clock clock,Provider provider,string stream="participant/vive/epoch-1",string device="participant-device",string operatorStream="operator/1",string operatorDevice="operator-device")
        { return new ViveParticipantEyeSource("session",1,2,stream,device,operatorStream,operatorDevice,clock,provider,new ViveProEyeRehearsalCoordinates()); }
        public static void Register(TestSuite suite)
        {
            suite.Run("VIVE participant read generation exposes every attempted read including failure and duplicate",()=>{
                var clock=new Clock{Now=1.01};var provider=new Provider{Next=Poll(1,1,1)};var source=Source(clock,provider);
                ViveParticipantEyeObservation observation;Check.Equal(0L,source.ReadAttemptGeneration);Check.True(!source.ReadAttemptGenerationExhausted);
                Check.True(source.TryRead(out observation));Check.Equal(1L,source.ReadAttemptGeneration);
                Check.True(!source.TryRead(out observation));Check.Equal(ViveParticipantReadStatus.Duplicate,source.LastReadStatus);Check.Equal(2L,source.ReadAttemptGeneration);
                provider.Throw=true;Check.True(!source.TryRead(out observation));Check.Equal(ViveParticipantReadStatus.ProviderFailure,source.LastReadStatus);Check.Equal(3L,source.ReadAttemptGeneration);
                provider.Throw=false;provider.Next=Poll(2,2,1);Check.True(source.TryRead(out observation));Check.Equal(4L,source.ReadAttemptGeneration);
                Check.Equal(4,provider.Calls);
            });
            suite.Run("VIVE participant read generation exhaustion permanently blocks provider access",()=>{
                var clock=new Clock{Now=1.01};var provider=new Provider{Next=Poll(1,1,1)};var source=Source(clock,provider);
                var field=typeof(ViveParticipantEyeSource).GetField("readAttemptGeneration",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic);
                field.SetValue(source,Int64.MaxValue-2);ViveParticipantEyeObservation observation;
                Check.True(source.TryRead(out observation));Check.Equal(Int64.MaxValue-1,source.ReadAttemptGeneration);Check.Equal(1,provider.Calls);
                Check.True(!source.TryRead(out observation));Check.True(observation==null&&source.LastObservation==null&&source.ReadAttemptGenerationExhausted);
                Check.Equal(ViveParticipantReadStatus.ReadAttemptGenerationExhausted,source.LastReadStatus);Check.Equal(1,provider.Calls);
                Check.True(!source.TryRead(out observation));Check.Equal(Int64.MaxValue,source.ReadAttemptGeneration);Check.Equal(1,provider.Calls);
            });
            suite.Run("VIVE participant sample preserves independent identity raw rays and observation time",()=>{
                var clock=new Clock {Now=10};var provider=new Provider {Next=Poll(5,-120,9.98)};var source=Source(clock,provider);
                ViveParticipantEyeObservation observation;Check.True(source.TryRead(out observation));var sample=observation.Sample;
                Check.Equal("session",sample.SessionId);Check.Equal("participant/vive/epoch-1",sample.StreamId);
                Check.Equal("participant-device",sample.DeviceId);Check.Equal("participant-clock",sample.ClockId);
                Check.Equal(1,sample.BlockIndex);Check.Equal(2,sample.Segment);Check.Equal(0L,sample.Sequence);
                Check.Equal(ParticipantEyeSource.RealDevice,sample.Source);Check.Equal(StreamRole.ParticipantMeasurement,sample.Role);
                Check.True(sample.EyeValid&&sample.DerivedEyeValid&&observation.NativeDirectionReportedValid);Check.Near(9.98,sample.CaptureLocalSeconds);Check.Equal(-120,observation.RawPoll.Eye.Timestamp);
                Check.Equal(5,observation.RawPoll.Eye.FrameSequence);Check.Equal(7UL,observation.RawPoll.Eye.Left.ValidityMask);
                Check.True(!observation.RawPoll.Eye.Left.DirectionReportedValid);Check.Equal(6UL,observation.RawPoll.Eye.Right.ValidityMask);
                Check.Near(2,observation.RawPoll.Eye.Right.OriginMillimeters.Y);
                Check.Equal(ViveTimestampBasis.LocalPollObservation,observation.TimestampBasis);
                Check.True(!observation.HardwareCaptureTimeVerified&&!observation.MappingValidatedForApparatus&&!source.MappingValidatedForApparatus);
                Check.Near(10,observation.PollCompletedLocalSeconds);
            });
            suite.Run("VIVE participant declarations must be separate from operator and simulator identities",()=>{
                var c=new Clock();var p=new Provider();
                Check.Throws<ArgumentException>(()=>Source(c,p,stream:"operator/1"));
                Check.Throws<ArgumentException>(()=>Source(c,p,device:"operator-device"));
                Check.Throws<ArgumentException>(()=>Source(c,p,stream:"simulator/participant"));
                Check.Throws<ArgumentException>(()=>Source(c,p,stream:"vive-pro-eye/operator/reused"));
                Check.Throws<ArgumentException>(()=>Source(c,p,device:" "));
                Check.Throws<ArgumentException>(()=>Source(c,p,operatorDevice:" "));
            });
            suite.Run("VIVE participant invalid gates stay invalid and retain raw payload",()=>{
                var polls=new[]{Poll(1,1,1,directionValid:false),Poll(2,2,1,mask:0),Poll(3,3,1,noUser:true),
                    Poll(4,4,1,runtime:ViveEyeRuntimeState.Unknown),Poll(5,5,1,calibration:ViveEyeCalibrationState.Required),
                    Poll(6,6,1,connected:false),Poll(7,7,1,result:1),Poll(8,8,1,direction:new Vec3(0,0,0))};
                var clock=new Clock {Now=1.01};var provider=new Provider();var source=Source(clock,provider);
                var expected=new[]{ViveParticipantReadStatus.EyeDirectionInvalid,ViveParticipantReadStatus.EyeDirectionInvalid,
                    ViveParticipantReadStatus.EyeDataUnavailable,ViveParticipantReadStatus.RuntimeUnavailable,
                    ViveParticipantReadStatus.CalibrationUnavailable,ViveParticipantReadStatus.DeviceUnavailable,
                    ViveParticipantReadStatus.NativeFailure,ViveParticipantReadStatus.EyeDirectionInvalid};
                for(int i=0;i<polls.Length;i++){
                    provider.Next=polls[i];ViveParticipantEyeObservation observation;Check.True(source.TryRead(out observation));
                    Check.Equal(ViveParticipantReadStatus.Accepted,source.LastReadStatus);Check.Equal(expected[i],observation.UsabilityStatus);
                    Check.True(!observation.Sample.EyeValid&&!observation.Sample.DerivedEyeValid&&!observation.Sample.DerivedEyeDirection.HasValue);Check.Equal((long)i,observation.Sample.Sequence);
                    Check.Equal(i>=2,observation.NativeDirectionReportedValid);
                    Check.Equal(polls[i].Eye.Combined.Direction.Z,observation.Sample.RawEyeDirection.Z);
                    Check.Equal(polls[i].Eye.Combined.ValidityMask,observation.RawPoll.Eye.Combined.ValidityMask);
                }
            });
            suite.Run("VIVE participant converted direction has explicit provenance and preserves the nonunit native ray",()=>{
                var clock=new Clock {Now=1.01};var provider=new Provider {Next=Poll(1,1,1,direction:new Vec3(1.2,0,1.6))};
                var source=Source(clock,provider);ViveParticipantEyeObservation observation;Check.True(source.TryRead(out observation));
                var sample=observation.Sample;
                Check.True(observation.NativeDirectionReportedValid&&!sample.EyeValid&&sample.DerivedEyeValid);
                Check.Near(1.2,sample.RawEyeDirection.X);Check.Near(1.6,sample.RawEyeDirection.Z);
                Check.Near(1.2,observation.RawPoll.Eye.Combined.Direction.X);Check.Near(1.6,observation.RawPoll.Eye.Combined.Direction.Z);
                Check.True(sample.DerivedEyeDirection.HasValue);Check.Near(-.6,sample.DerivedEyeDirection.Value.X);Check.Near(.8,sample.DerivedEyeDirection.Value.Z);
                Check.Equal(ViveProEyeRehearsalCoordinates.MappingVersion,sample.DerivedDirectionMappingVersion);
                Check.Equal("unity-tracking-local-rehearsal-unvalidated",sample.DerivedDirectionCoordinateSpace);
                Check.True(!observation.MappingValidatedForApparatus&&!observation.HardwareCaptureTimeVerified);
                // Existing interval/export schema only knows the raw vector. A
                // derived-only valid ray cannot masquerade as a classified AOI.
                var window=new GazeObservationWindow(1,2,Condition.A,Phase.MainTask,1,2);
                Check.Throws<ArgumentException>(()=>new ParticipantGazeInterval(sample,window,null,1,1.5,ParticipantAoi.Task,
                    sample.DerivedDirectionMappingVersion,"unvalidated"));
            });
            suite.Run("Participant derived ray cannot bypass reported validity or raw and derived numerical gates",()=>{
                Func<Vec3,bool,Vec3,ParticipantEyeSample> sample=(raw,valid,derived)=>new ParticipantEyeSample("s","device/stream","d","c",0,0,0,0,
                    ParticipantEyeSource.RealDevice,StreamRole.ParticipantMeasurement,raw,valid,derived,"map-v1","head-local");
                Check.True(sample(new Vec3(0,0,2),true,new Vec3(0,0,1)).DerivedEyeValid);
                Check.True(!sample(new Vec3(0,0,2),false,new Vec3(0,0,1)).DerivedEyeValid);
                Check.True(!sample(new Vec3(0,0,0),true,new Vec3(0,0,1)).DerivedEyeValid);
                Check.True(!sample(new Vec3(Double.NaN,0,1),true,new Vec3(0,0,1)).DerivedEyeValid);
                Check.True(!sample(new Vec3(0,0,2),true,new Vec3(0,0,2)).DerivedEyeValid);
                Check.Throws<ArgumentException>(()=>new ParticipantEyeSample("s","device/stream","d","c",0,0,0,0,
                    ParticipantEyeSource.RealDevice,StreamRole.ParticipantMeasurement,new Vec3(0,0,2),true,new Vec3(0,0,1),"","head-local"));
                var legacy=new ParticipantEyeSample("s","simulator/p","d","c",0,0,0,0,ParticipantEyeSource.Simulator,StreamRole.ParticipantMeasurement,new Vec3(0,0,1),true);
                Check.True(legacy.EyeValid&&!legacy.DerivedEyeValid&&!legacy.DerivedEyeDirection.HasValue);
            });
            suite.Run("VIVE participant duplicate stale missing and out of order frames do not create samples",()=>{
                var clock=new Clock {Now=1.01};var provider=new Provider {Next=Poll(1,10,1)};var source=Source(clock,provider);
                ViveParticipantEyeObservation observation;Check.True(source.TryRead(out observation));
                Check.True(!source.TryRead(out observation));Check.True(observation==null&&source.LastObservation==null);
                provider.Next=Poll(0,9,1);Check.True(!source.TryRead(out observation));
                provider.Next=new ViveProEyePoll(null,null,ViveEyeRuntimeState.Working,ViveEyeCalibrationState.ReportedReady,true,0);
                Check.True(!source.TryRead(out observation));clock.Now=2;provider.Next=Poll(2,11,1);Check.True(!source.TryRead(out observation));
                provider.Next=Poll(3,12,1.99);Check.True(source.TryRead(out observation));Check.Equal(1L,observation.Sample.Sequence);
            });
            suite.Run("VIVE participant source clock failures cannot fabricate a measurement",()=>{
                var clock=new Clock {Now=1.01};var provider=new Provider {Next=Poll(1,1,1)};var source=Source(clock,provider);
                ViveParticipantEyeObservation observation;Check.True(source.TryRead(out observation));
                clock.ClockId="other-clock";provider.Next=Poll(2,2,1);Check.True(!source.TryRead(out observation));Check.Equal(ViveParticipantReadStatus.ClockFailure,source.LastReadStatus);
                clock.ClockId="participant-clock";Check.True(!source.TryRead(out observation));Check.Equal(ViveParticipantReadStatus.ClockFailure,source.LastReadStatus);
            });
            suite.Run("VIVE participant source exposes sanitized failure status and immutable stream context",()=>{
                var clock=new Clock{Now=1.01};var provider=new Provider{Next=Poll(1,1,1)};var source=Source(clock,provider);
                Check.Equal(ViveParticipantReadStatus.NotRead,source.LastReadStatus);Check.True(source.UsesClock(clock));
                Check.True(!source.UsesClock(new Clock{Now=1.01}));Check.Equal("session",source.SessionId);Check.Equal("participant-clock",source.ClockId);
                Check.Equal(1,source.BlockIndex);Check.Equal(2,source.Segment);Check.Equal(ViveProEyeRehearsalCoordinates.MappingVersion,source.MappingVersion);
                ViveParticipantEyeObservation obs;Check.True(source.TryRead(out obs));Check.Equal(ViveParticipantReadStatus.Accepted,source.LastReadStatus);
                Check.True(!source.TryRead(out obs));Check.Equal(ViveParticipantReadStatus.Duplicate,source.LastReadStatus);
                provider.Next=new ViveProEyePoll(null,null,ViveEyeRuntimeState.Working,ViveEyeCalibrationState.ReportedReady,true,0);
                Check.True(!source.TryRead(out obs));Check.Equal(ViveParticipantReadStatus.Missing,source.LastReadStatus);
                provider.Throw=true;Check.True(!source.TryRead(out obs));Check.Equal(ViveParticipantReadStatus.ProviderFailure,source.LastReadStatus);
                Check.True(obs==null&&source.LastObservation==null);Check.True(!source.LastReadStatus.ToString().Contains("private"));
            });
            suite.Run("VIVE participant source reports runtime loss even when eye payload is absent",()=>{
                var clock=new Clock{Now=1};var provider=new Provider{Next=new ViveProEyePoll(null,null,ViveEyeRuntimeState.Unavailable,ViveEyeCalibrationState.Unknown,false)};
                var source=Source(clock,provider);ViveParticipantEyeObservation obs;
                Check.True(!source.TryRead(out obs));Check.Equal(ViveParticipantReadStatus.RuntimeUnavailable,source.LastReadStatus);
                provider.Next=new ViveProEyePoll(null,null,ViveEyeRuntimeState.Working,ViveEyeCalibrationState.Required,true,0);
                Check.True(!source.TryRead(out obs));Check.Equal(ViveParticipantReadStatus.CalibrationUnavailable,source.LastReadStatus);
            });
        }
    }
}
