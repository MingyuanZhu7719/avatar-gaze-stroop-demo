using AvatarStroop.Runtime;
namespace AvatarStroop.Tests
{
    // Pure selection policy: no hardware, input system, files or network.
    public static class ViveSingleControllerSelectionTests
    {
        public static void Register(TestSuite suite)
        {
            suite.Run("single controller waits without locking missing or ambiguous input",()=>{
                var s=new ViveSingleControllerSelection();
                Check.True(!s.Observe(0,0,0)); Check.True(!s.Observe(2,1,1));
                Check.True(!s.Observe(1,1,0)); Check.True(!s.Observe(1,0,1));
                Check.True(s.Observe(1,8,2));
            });
            suite.Run("single controller supports either role without preference",()=>{
                foreach(int role in new[]{1,2}){var s=new ViveSingleControllerSelection();Check.True(s.Observe(1,8,role));Check.True(s.Observe(1,8,role));}
            });
            suite.Run("single controller missing or multiple input never falls back",()=>{
                var s=new ViveSingleControllerSelection();Check.True(s.Observe(1,8,1));
                Check.True(!s.Observe(0,0,0));Check.True(!s.Observe(2,8,1));
                Check.True(s.Observe(1,8,1));
            });
            suite.Run("single controller device replacement latches until new lifecycle",()=>{
                var s=new ViveSingleControllerSelection();Check.True(s.Observe(1,8,1));
                Check.True(!s.Observe(0,0,0));Check.True(!s.Observe(1,9,1));
                Check.True(!s.Observe(1,8,1));Check.True(!s.Observe(0,0,0));
                Check.True(new ViveSingleControllerSelection().Observe(1,9,1));
            });
            suite.Run("single controller role change latches until new lifecycle",()=>{
                var s=new ViveSingleControllerSelection();Check.True(s.Observe(1,8,1));
                Check.True(!s.Observe(1,8,2));Check.True(!s.Observe(1,8,1));
            });
            suite.Run("single controller invalid counts and roles cannot choose",()=>{
                foreach(int count in new[]{-1,0,2,3,int.MaxValue})Check.True(!new ViveSingleControllerSelection().Observe(count,8,1));
                foreach(int role in new[]{-1,0,3,int.MaxValue})Check.True(!new ViveSingleControllerSelection().Observe(1,8,role));
            });
            suite.Run("single controller replacement after ambiguity cannot inherit session",()=>{
                var s=new ViveSingleControllerSelection();Check.True(s.Observe(1,8,1));
                Check.True(!s.Observe(2,9,1));Check.True(!s.Observe(1,9,1));
                Check.True(!s.Observe(1,8,1));
                Check.True(s.Status.Contains("Restart the view"));
            });
            suite.Run("single controller exposes a useful reason for every unavailable state",()=>{
                var s=new ViveSingleControllerSelection();
                Check.True(!s.Observe(0,0,0));Check.True(s.Status.Contains("Connect one"));
                Check.True(!s.Observe(2,8,1));Check.True(s.Status.Contains("More than one"));
                Check.True(!s.Observe(1,8,0));Check.True(s.Status.Contains("role unavailable"));
                Check.True(s.Observe(1,8,2));Check.True(s.Status.Contains("Right"));
                Check.True(!s.Observe(0,0,0));Check.True(s.Observe(1,8,2));
                Check.True(!s.Observe(1,8,1));
                string latched=s.Status;Check.True(!s.Observe(0,0,0));Check.Equal(latched,s.Status);
            });
#if STROOP_XR_PREVIEW
            suite.Run("single controller reconnect requires neutral before a new trigger edge",()=>{
                var s=new ViveSingleControllerSelection();var neutral=new AvatarStroop.Unity.ViveStroopNeutralGate();
                Check.True(s.Observe(1,8,1));
                Check.True(!neutral.Observe(true,false,false,false,false,false).NeutralPending);
                Check.True(neutral.Observe(true,false,false,false,true,false).TriggerDown);
                Check.True(!s.Observe(0,0,0));
                Check.True(neutral.Observe(false,false,false,false,true,false).LostReady);
                Check.True(s.Observe(1,8,1));
                var held=neutral.Observe(true,false,false,false,true,false);
                Check.True(held.NeutralPending&&!held.TriggerDown);
                Check.True(!neutral.Observe(true,false,false,false,false,false).TriggerDown);
                Check.True(neutral.Observe(true,false,false,false,true,false).TriggerDown);
            });
            suite.Run("single controller ambiguity clears an armed input without switching",()=>{
                var s=new ViveSingleControllerSelection();var neutral=new AvatarStroop.Unity.ViveStroopNeutralGate();
                Check.True(s.Observe(1,8,2));neutral.Observe(true,false,false,false,false,false);
                Check.True(!s.Observe(2,9,2));
                Check.True(neutral.Observe(false,false,false,false,false,false).LostReady);
                Check.True(s.Observe(1,8,2));
                var pending=neutral.Observe(true,false,false,true,false,false);
                Check.True(pending.NeutralPending&&!pending.TriggerDown&&!pending.MenuDown);
                var ready=neutral.Observe(true,false,false,false,false,false);
                Check.True(!ready.NeutralPending&&!ready.TriggerDown&&!ready.MenuDown);
                Check.True(!s.Observe(1,9,2));
            });
#endif
        }
    }
}
