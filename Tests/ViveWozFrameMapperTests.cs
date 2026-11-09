using System;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    public static class ViveWozFrameMapperTests
    {
        sealed class Clock : IClock { public double Value; public double Now { get { return Value; } } public string ClockId { get { return "woz-frame-clock"; } } }
        sealed class Provider : IViveProEyeProvider { public ViveProEyePoll Next; public ViveProEyePoll Poll() { return Next; } }
        static ViveProEyePoll Poll(long sequence, double at, bool eyeValid)
        {
            var ray=new ViveEyeRay(eyeValid?2UL:0UL,false,eyeValid,new Vec3(0,0,0),new Vec3(0,0,1));
            return new ViveProEyePoll(new ViveHeadFrame(sequence,at,true,true,Rotation.Identity),
                new ViveEyeFrame((int)sequence,(int)sequence,at,false,null,null,ray),
                ViveEyeRuntimeState.Working,ViveEyeCalibrationState.ReportedReady,true,0);
        }
        public static void Register(TestSuite suite)
        {
            suite.Run("WoZ frame mapper identity preserves rehearsal head and tracking eye",()=>{
                var m=new ViveWozFrameMapper(Rotation.Identity,"explicit-alignment-a");
                var yaw=new Rotation(0,Math.Sqrt(.5),0,Math.Sqrt(.5));
                Rotation head;Vec3 eye;
                Check.True(m.TryMapHead(yaw,out head));Check.Near(yaw.Y,head.Y);Check.Near(yaw.W,head.W);
                Check.True(m.TryMapEye(yaw,new Vec3(0,0,3),out eye));Check.Near(1,eye.X);Check.Near(0,eye.Y);Check.Near(0,eye.Z);
                Check.True(!m.MappingValidatedForApparatus);
            });
            suite.Run("WoZ frame mapper rotates head and eye consistently into avatar root",()=>{
                double s=Math.Sqrt(.5);
                var m=new ViveWozFrameMapper(new Rotation(0,s,0,s),"explicit-alignment-a");
                Rotation head;Vec3 eye;
                Check.True(m.TryMapHead(Rotation.Identity,out head));Check.Near(s,head.Y);Check.Near(s,head.W);
                Check.True(m.TryMapEye(Rotation.Identity,new Vec3(0,0,1),out eye));Check.Near(1,eye.X);Check.Near(0,eye.Y);Check.Near(0,eye.Z);
                Check.True(m.TryMapEye(Rotation.Identity,new Vec3(1,0,0),out eye));Check.Near(0,eye.X);Check.Near(0,eye.Y);Check.Near(1,eye.Z);
                var pitch=new Rotation(s,0,0,s);
                Check.True(m.TryMapHead(pitch,out head));
                Check.Near(.5,head.X);Check.Near(.5,head.Y);Check.Near(-.5,head.Z);Check.Near(.5,head.W);
                Check.True(m.TryMapEye(pitch,new Vec3(0,0,1),out eye));Check.Near(0,eye.X);Check.Near(-1,eye.Y);Check.Near(0,eye.Z);
            });
            suite.Run("WoZ frame mapper rejects invalid rotation and SDK direction without fallback",()=>{
                Check.Throws<ArgumentException>(()=>new ViveWozFrameMapper(new Rotation(0,0,0,0),"alignment"));
                Check.Throws<ArgumentException>(()=>new ViveWozFrameMapper(new Rotation(Double.NaN,0,0,1),"alignment"));
                Check.Throws<ArgumentException>(()=>new ViveWozFrameMapper(Rotation.Identity,""));
                Check.Throws<ArgumentException>(()=>new ViveWozFrameMapper(Rotation.Identity,"line\nbreak"));
                var m=new ViveWozFrameMapper(Rotation.Identity,"alignment");Rotation head;Vec3 eye;
                Check.True(!m.TryMapHead(new Rotation(0,0,0,0),out head));Check.True(Double.IsNaN(head.X));
                Check.True(!m.TryMapEye(Rotation.Identity,new Vec3(0,0,0),out eye));Check.True(Double.IsNaN(eye.X));
                Check.True(!m.TryMapEye(new Rotation(0,0,0,2),new Vec3(0,0,1),out eye));Check.True(Double.IsNaN(eye.X));
                Check.True(!m.TryMapEye(Rotation.Identity,new Vec3(Double.PositiveInfinity,0,1),out eye));Check.True(Double.IsNaN(eye.X));
            });
            suite.Run("WoZ frame rotations handle opposite angles and equivalent quaternion signs",()=>{
                double s=Math.Sqrt(.5);
                var positive=new ViveWozFrameMapper(new Rotation(0,s,0,s),"alignment");
                var equivalent=new ViveWozFrameMapper(new Rotation(0,-s,0,-s),"alignment");
                var negative=new ViveWozFrameMapper(new Rotation(0,-s,0,s),"alignment");
                Vec3 a,b,c;Rotation h;
                Check.True(positive.TryMapEye(Rotation.Identity,new Vec3(0,0,1),out a));
                Check.True(equivalent.TryMapEye(Rotation.Identity,new Vec3(0,0,1),out b));
                Check.Near(a.X,b.X);Check.Near(a.Y,b.Y);Check.Near(a.Z,b.Z);
                Check.True(negative.TryMapEye(Rotation.Identity,new Vec3(0,0,1),out c));Check.Near(-1,c.X);Check.Near(0,c.Z);
                Check.True(positive.TryMapHead(new Rotation(-s,0,0,s),out h));
                Check.Near(-.5,h.X);Check.Near(.5,h.Y);Check.Near(.5,h.Z);Check.Near(.5,h.W);
                Check.True(positive.TryMapEye(new Rotation(-s,0,0,s),new Vec3(0,0,1),out a));
                Check.Near(0,a.X);Check.Near(1,a.Y);Check.Near(0,a.Z);
            });
            suite.Run("WoZ frame mapping version binds explicit rotation and alignment identity",()=>{
                var one=new ViveWozFrameMapper(Rotation.Identity,"same");
                var same=new ViveWozFrameMapper(Rotation.Identity,"same");
                var anotherName=new ViveWozFrameMapper(Rotation.Identity,"other");
                var changed=new ViveWozFrameMapper(new Rotation(0,Math.Sqrt(.5),0,Math.Sqrt(.5)),"same");
                var changedEquivalent=new ViveWozFrameMapper(new Rotation(-0.0,-Math.Sqrt(.5),-0.0,-Math.Sqrt(.5)),"same");
                Check.Equal(one.Version,same.Version);Check.True(one.Version!=anotherName.Version&&one.Version!=changed.Version);
                Check.Equal(changed.Version,changedEquivalent.Version);
                Check.True(one.Version.Length<=128);
            });
            suite.Run("WoZ aligned source retains invalid eye as invalid without holding the last direction",()=>{
                double s=Math.Sqrt(.5);var clock=new Clock{Value=1};var provider=new Provider{Next=Poll(1,1,true)};
                var mapper=new ViveWozFrameMapper(new Rotation(0,s,0,s),"synthetic-alignment");
                var source=new ViveProEyeHeadEyeSource("session",0,0,"vive-pro-eye/operator/fixture",clock,provider,mapper,.1);
                HeadEyeSample first;Check.True(source.TryRead(out first));Check.True(first.HeadValid&&first.EyeValid);
                Check.Near(1,first.RawEyeDirection.X);Check.True(!source.MappingValidatedForApparatus);
                clock.Value=1.01;provider.Next=Poll(2,1.01,false);HeadEyeSample invalid;
                Check.True(source.TryRead(out invalid));Check.True(invalid.HeadValid&&!invalid.EyeValid);
                Check.True(Double.IsNaN(invalid.RawEyeDirection.X));
                Check.Equal("Combined eye direction is not reported valid",source.LastObservation.EyeFailureReason);
                clock.Value=1.2;Check.True(!source.Available);
            });
        }
    }
}
