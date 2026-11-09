using AvatarStroop.Runtime;
namespace AvatarStroop.Tests
{
    public static class ViveStartupAlignmentGateTests
    {
        static bool S(ViveStartupAlignmentGate g,int f,bool focus=true,bool cap=false,bool present=true,bool seen=true,bool ready=true,bool changed=false,bool trigger=false,bool pad=false,bool menu=false,bool running=true)
        {return g.Observe(f,running,focus,cap,present,seen,ready,changed,trigger,pad,menu);}
        public static void Register(TestSuite suite)
        {
            suite.Run("VIVE startup unsupported presence and focus cannot auto confirm",()=>{
                var g=new ViveStartupAlignmentGate();for(int i=0;i<20;i++)Check.True(!S(g,i));
                Check.True(!g.AutomaticWearVerified&&!g.ManualConfirmed);
            });
            suite.Run("VIVE startup initial true capability needs fresh explicit trigger",()=>{
                var g=new ViveStartupAlignmentGate();Check.True(!S(g,1,cap:true,trigger:true));
                Check.True(!S(g,2,cap:true,trigger:true));Check.True(!S(g,3,cap:true));
                Check.True(S(g,4,cap:true,trigger:true));Check.True(g.ManualConfirmed&&!g.AutomaticWearVerified);
            });
            suite.Run("VIVE startup verified absent then present enables automatic placement",()=>{
                var g=new ViveStartupAlignmentGate();Check.True(!S(g,1,focus:false,cap:true,present:false,ready:false));
                Check.True(g.KnownAbsent);Check.True(S(g,2,cap:true,ready:false));Check.True(g.AutomaticWearVerified);
            });
            suite.Run("VIVE startup known absent never accepts a trigger override",()=>{
                var g=new ViveStartupAlignmentGate();Check.True(!S(g,1,cap:true,present:false));
                Check.True(!S(g,2,cap:true,present:false,trigger:true));Check.True(!g.ManualConfirmed);
            });
            suite.Run("VIVE startup focus loss cancels confirmation and held recovery",()=>{
                var g=new ViveStartupAlignmentGate();Check.True(!S(g,1));Check.True(S(g,2,trigger:true));
                Check.True(!S(g,3,focus:false,trigger:true));Check.True(!S(g,4,trigger:true));
                Check.True(!S(g,5));Check.True(S(g,6,trigger:true));
            });
            suite.Run("VIVE startup requires visible prompt and neutral controller",()=>{
                var g=new ViveStartupAlignmentGate();Check.True(!S(g,1,seen:false));Check.True(!S(g,2,trigger:true));
                Check.True(!S(g,3,pad:true));Check.True(!S(g,4,trigger:true));Check.True(!S(g,5));
                Check.True(S(g,6,trigger:true));Check.True(!S(g,7,menu:true));Check.True(!S(g,8,trigger:true));
            });
            suite.Run("VIVE startup device loss replacement and duplicate frames cannot confirm",()=>{
                var g=new ViveStartupAlignmentGate();Check.True(!S(g,1));Check.True(!S(g,1,trigger:true));
                Check.True(!S(g,2,changed:true,trigger:true));Check.True(!S(g,3,trigger:true));
                Check.True(!S(g,4));Check.True(S(g,5,trigger:true));Check.True(!S(g,6,ready:false));
                Check.True(!S(g,7,trigger:true));
            });
            suite.Run("VIVE startup confirmation survives the preparing prompt transition",()=>{
                var g=new ViveStartupAlignmentGate();Check.True(!S(g,1));Check.True(S(g,2,trigger:true));
                Check.True(S(g,3,seen:false));Check.True(S(g,4,seen:true));
                Check.True(!S(g,5,focus:false));Check.True(!S(g,6,seen:false,trigger:true));
            });
            suite.Run("VIVE alignment requires uninterrupted recent samples for three quarters of a second",()=>{
                var g=new ViveStartPoseStability(.75,.15);
                for(int i=0;i<8;i++)Check.True(!g.Observe(true,2,i,1+i*.1,0,1.6,0,0,0,0,1));
                Check.True(g.Observe(true,2,8,1.81,0,1.6,0,0,0,0,1));
                Check.True(!g.Observe(true,2,9,3,0,1.6,0,0,0,0,1));
                for(int i=1;i<8;i++)Check.True(!g.Observe(true,2,9+i,3+i*.1,0,1.6,0,0,0,0,1));
                Check.True(g.Observe(true,2,17,3.81,0,1.6,0,0,0,0,1));
                Check.True(!g.Observe(false,2,18,3.9,0,1.6,0,0,0,0,1));
                Check.True(!g.Observe(true,2,19,4,0,1.6,0,0,0,0,1));
            });
            suite.Run("VIVE startup session restart and capability loss revoke automatic proof",()=>{
                var g=new ViveStartupAlignmentGate();Check.True(!S(g,1,cap:true,present:false));Check.True(S(g,2,cap:true));
                Check.True(!S(g,3,cap:false,ready:false));Check.True(!S(g,4,cap:true,ready:false));
                Check.True(!S(g,5,cap:true,present:false));Check.True(S(g,6,cap:true));
                Check.True(!S(g,7,running:false));Check.True(!S(g,8,cap:true,ready:false));
            });
        }
    }
}
