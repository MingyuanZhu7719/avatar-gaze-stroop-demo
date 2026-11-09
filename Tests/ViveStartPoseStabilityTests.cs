using System;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    public static class ViveStartPoseStabilityTests
    {
        static bool Sample(ViveStartPoseStability gate,bool ready,int mode,int frame,double now,double x=0,double y=1.2,double z=0,double qy=0,double qw=1)
        {return gate.Observe(ready,mode,frame,now,x,y,z,0,qy,0,qw);}
        public static void Register(TestSuite suite)
        {
            suite.Run("VIVE first view requires stable tracked pose over time",()=>{
                var gate=new ViveStartPoseStability();
                Check.True(!Sample(gate,true,2,1,1));Check.True(!Sample(gate,true,2,2,1.1,.01));
                Check.True(Sample(gate,true,2,3,1.21,.02));
            });
            suite.Run("VIVE first view cannot use repeated frame or insufficient samples",()=>{
                var gate=new ViveStartPoseStability();
                Check.True(!Sample(gate,true,1,1,1));Check.True(!Sample(gate,true,1,1,2));
                Check.True(!Sample(gate,true,1,2,2.1));Check.True(Sample(gate,true,1,3,2.2));
            });
            suite.Run("VIVE first view restarts stability on movement rotation or mode change",()=>{
                var gate=new ViveStartPoseStability();
                Check.True(!Sample(gate,true,1,1,1));Check.True(!Sample(gate,true,1,2,1.1,.05));
                Check.True(!Sample(gate,true,1,3,1.2,.05,1.2,0,.0871557427,.9961946981));
                Check.True(!Sample(gate,true,2,4,1.3,.05,1.2,0,.0871557427,.9961946981));
                Check.True(!Sample(gate,true,2,5,1.4,.05,1.2,0,.0871557427,.9961946981));
                Check.True(Sample(gate,true,2,6,1.51,.05,1.2,0,.0871557427,.9961946981));
            });
            suite.Run("VIVE first view rejects lost tracking invalid pose and clock rollback",()=>{
                var gate=new ViveStartPoseStability();
                Check.True(!Sample(gate,true,2,1,1));Check.True(!Sample(gate,false,2,2,1.1));
                Check.True(!Sample(gate,true,2,3,1.2));Check.True(!Sample(gate,true,2,4,.5));
                Check.True(!Sample(gate,true,2,5,.6,Double.NaN));
                Check.True(!Sample(gate,true,2,6,.7,0,1.2,0,0,0));
                Check.True(!Sample(gate,true,0,7,.8));
                Check.True(!Sample(gate,true,2,8,.9));Check.True(!Sample(gate,true,2,9,1));
                Check.True(Sample(gate,true,2,10,1.11));
            });
            suite.Run("VIVE first view rejects unreasonable tracking coordinates",()=>{
                var gate=new ViveStartPoseStability();
                Check.True(!Sample(gate,true,2,1,1,21));Check.True(!Sample(gate,true,2,2,1.1,0,4.1));
                Check.True(!Sample(gate,true,2,3,1.2,0,1.2,-21));
                Check.True(!Sample(gate,true,2,4,1.3));Check.True(!Sample(gate,true,2,5,1.4));
                Check.True(Sample(gate,true,2,6,1.51));
            });
            suite.Run("VIVE stable valid pose can reach the separate vertical-shift guard",()=>{
                var gate=new ViveStartPoseStability();
                Check.True(!Sample(gate,true,2,1,1,0,3.9));
                Check.True(!Sample(gate,true,2,2,1.1,0,3.9));
                Check.True(Sample(gate,true,2,3,1.21,0,3.9));
                Check.True(!ViveStartPoseStability.AcceptsVerticalShift(3.9f,1.55f));
                Check.True(ViveStartPoseStability.AcceptsVerticalShift(0,2));
                Check.True(!ViveStartPoseStability.AcceptsVerticalShift(0,2.001f));
                Check.True(!ViveStartPoseStability.AcceptsVerticalShift(Single.NaN,1.55f));
                Check.True(!ViveStartPoseStability.AcceptsVerticalShift(0,Single.PositiveInfinity));
            });
        }
    }
}
