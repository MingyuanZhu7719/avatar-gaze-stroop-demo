using System;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    public static class ViveWozReferenceAlignmentTests
    {
        const ViveWozReferenceConvention Convention=ViveWozReferenceConvention.HmdLocalToTrackingAndAvatarHeadLocalToRoot;
        const ViveWozHeadAxisAssumption Axes=ViveWozHeadAxisAssumption.HmdAndAvatarHeadLocalAxesAssumedAligned;

        public static void Register(TestSuite suite)
        {
            suite.Run("WoZ reference alignment maps explicit reference without claiming validation",()=>{
                double s=Math.Sqrt(.5);var tracked=new Rotation(0,s,0,s);
                var candidate=ViveWozReferenceAlignment.Create(tracked,Rotation.Identity,Convention,Axes,"reference-a");
                Rotation mapped;Check.True(candidate.Mapper.TryMapHead(tracked,out mapped));
                Check.Near(0,mapped.X);Check.Near(0,mapped.Y);Check.Near(0,mapped.Z);Check.Near(1,Math.Abs(mapped.W));
                Check.True(candidate.FittedReferenceResidualDegrees<.00001);
                Check.True(!candidate.ValidatedForApparatus&&!candidate.Mapper.MappingValidatedForApparatus);
                Check.True(candidate.Mapper.TryMapHead(Rotation.Identity,out mapped));Check.Near(-s,mapped.Y);Check.Near(s,mapped.W);
                Vec3 eye;Check.True(candidate.Mapper.TryMapEye(Rotation.Identity,new Vec3(0,0,1),out eye));
                Check.Near(-1,eye.X);Check.Near(0,eye.Y);Check.Near(0,eye.Z);
            });
            suite.Run("WoZ reference alignment composes orientations in the declared frame order",()=>{
                double s=Math.Sqrt(.5);var h=new Rotation(0,s,0,s);var target=new Rotation(s,0,0,s);
                var c=ViveWozReferenceAlignment.Create(h,target,Convention,Axes,"reference-b");Rotation mapped;
                Check.True(c.Mapper.TryMapHead(h,out mapped));Check.Near(s,mapped.X);Check.Near(0,mapped.Y);
                Check.Near(0,mapped.Z);Check.Near(s,mapped.W);
                double residual;Check.True(c.TryMeasureHoldoutResidualDegrees(h,Rotation.Identity,out residual));Check.Near(90,residual);
                Check.True(c.TryMeasureHoldoutResidualDegrees(h,new Rotation(-s,0,0,-s),out residual));Check.True(residual<.00001);
            });
            suite.Run("WoZ reference alignment equivalent quaternion signs retain the same mapping",()=>{
                double s=Math.Sqrt(.5);var h=new Rotation(0,s,0,s);var target=new Rotation(s,0,0,s);
                var first=ViveWozReferenceAlignment.Create(h,target,Convention,Axes,"same");
                var second=ViveWozReferenceAlignment.Create(new Rotation(0,-s,0,-s),new Rotation(-s,0,0,-s),Convention,Axes,"same");
                Check.Equal(first.Mapper.Version,second.Mapper.Version);
                double residual;Check.True(second.TryMeasureHoldoutResidualDegrees(h,target,out residual));Check.True(residual<.00001);
            });
            suite.Run("WoZ reference alignment requires explicit assumptions and rejects invalid observations",()=>{
                Check.Throws<ArgumentException>(()=>ViveWozReferenceAlignment.Create(Rotation.Identity,Rotation.Identity,0,Axes,"a"));
                Check.Throws<ArgumentException>(()=>ViveWozReferenceAlignment.Create(Rotation.Identity,Rotation.Identity,Convention,0,"a"));
                Check.Throws<ArgumentException>(()=>ViveWozReferenceAlignment.Create(new Rotation(0,0,0,0),Rotation.Identity,Convention,Axes,"a"));
                Check.Throws<ArgumentException>(()=>ViveWozReferenceAlignment.Create(Rotation.Identity,new Rotation(Double.NaN,0,0,1),Convention,Axes,"a"));
                Check.Throws<ArgumentException>(()=>ViveWozReferenceAlignment.Create(Rotation.Identity,Rotation.Identity,Convention,Axes,""));
                var c=ViveWozReferenceAlignment.Create(Rotation.Identity,Rotation.Identity,Convention,Axes,"a");double result;
                Check.True(!c.TryMeasureHoldoutResidualDegrees(new Rotation(0,0,0,0),Rotation.Identity,out result)&&Double.IsNaN(result));
                Check.True(!c.TryMeasureHoldoutResidualDegrees(Rotation.Identity,new Rotation(0,0,0,2),out result)&&Double.IsNaN(result));
            });
        }
    }
}
