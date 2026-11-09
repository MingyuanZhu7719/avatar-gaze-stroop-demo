using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    // Pure gate tests: no Unity input, physical device, file, or network.
    public static class ViveStroopSafetyMenuGateTests
    {
        public static void Register(TestSuite suite)
        {
            suite.Run("VIVE safety menu requires release after entering blocked while held",()=>{
                var gate=new ViveStroopSafetyMenuGate();
                Check.True(!gate.Observe(true,false,true));
                Check.True(!gate.Observe(true,false,true));
                Check.True(!gate.Observe(true,false,false));
                Check.True(gate.Observe(true,false,true));
                Check.True(!gate.Observe(true,false,true));
                Check.True(!gate.Observe(true,false,false));
                Check.True(gate.Observe(true,false,true));
            });
            suite.Run("VIVE safety menu starts neutral then emits one edge per new press",()=>{
                var gate=new ViveStroopSafetyMenuGate();
                Check.True(!gate.Observe(true,false,false));
                Check.True(!gate.Observe(true,false,false));
                Check.True(gate.Observe(true,false,true));
                for(int i=0;i<8;i++)Check.True(!gate.Observe(true,false,true));
                Check.True(!gate.Observe(true,false,false));
                Check.True(gate.Observe(true,false,true));
            });
            suite.Run("VIVE safety menu disconnection or focus loss requires a fresh release",()=>{
                foreach(bool heldAtLoss in new[]{false,true})
                {
                    var gate=new ViveStroopSafetyMenuGate();
                    Check.True(!gate.Observe(true,false,false));
                    Check.True(!gate.Observe(false,false,heldAtLoss));
                    Check.True(!gate.Observe(false,false,true));
                    Check.True(!gate.Observe(true,false,true));
                    Check.True(!gate.Observe(true,false,true));
                    Check.True(!gate.Observe(true,false,false));
                    Check.True(gate.Observe(true,false,true));
                }
            });
            suite.Run("VIVE safety menu device replacement cannot replay a held press",()=>{
                var gate=new ViveStroopSafetyMenuGate();
                Check.True(!gate.Observe(true,false,false));
                Check.True(gate.Observe(true,false,true));
                Check.True(!gate.Observe(true,true,true));
                Check.True(!gate.Observe(true,false,true));
                Check.True(!gate.Observe(true,false,false));
                Check.True(gate.Observe(true,false,true));
                Check.True(!gate.Observe(true,true,false));
                Check.True(gate.Observe(true,false,true));
            });
            suite.Run("VIVE safety menu explicit reset protects blocked exit and re-entry",()=>{
                var gate=new ViveStroopSafetyMenuGate();
                Check.True(!gate.Observe(true,false,false));
                Check.True(gate.Observe(true,false,true));
                gate.Reset();gate.Reset();
                Check.True(!gate.Observe(true,false,true));
                Check.True(!gate.Observe(true,false,false));
                Check.True(gate.Observe(true,false,true));
            });
            suite.Run("VIVE safety menu never emits on not-ready or a held gate entry",()=>{
                for(int mask=0;mask<4096;mask++)
                {
                    var gate=new ViveStroopSafetyMenuGate();bool neutralSeen=false,previous=false;
                    for(int i=0;i<4;i++)
                    {
                        bool ready=(mask&(1<<(3*i)))!=0,changed=(mask&(1<<(3*i+1)))!=0,held=(mask&(1<<(3*i+2)))!=0;
                        bool edge=gate.Observe(ready,changed,held);
                        if(!ready||changed){neutralSeen=false;previous=false;}
                        Check.True(!edge||(ready&&!changed&&neutralSeen&&held&&!previous),"Unexpected edge without a fresh release and press");
                        if(ready&&!neutralSeen&&!held)neutralSeen=true;
                        previous=ready&&held;
                    }
                }
            });
        }
    }
}
