using System;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    public static class VivePreSessionRecordingChoiceTests
    {
        static void Sample(VivePreSessionRecordingChoice gate,bool ready=true,bool visible=true,bool trigger=false,bool menu=false,bool trackpad=false,bool changed=false)
        {gate.Observe(ready,visible,trigger,menu,trackpad,changed);}
        public static void Register(TestSuite suite)
        {
            suite.Run("pre-session choices require an explicit request and separately neutral-armed inputs",()=>{
                Check.Throws<ArgumentException>(()=>new VivePreSessionRecordingChoice(false,false));
                var gate=new VivePreSessionRecordingChoice(true,true);
                Sample(gate,trigger:true);Sample(gate,trigger:true);Check.Equal(VivePreSessionRecordingChoice.Step.Task,gate.Current);
                Sample(gate);Sample(gate,trigger:true);Check.Equal(VivePreSessionRecordingChoice.Step.Eye,gate.Current);
                Sample(gate,trigger:true);Sample(gate,trigger:true);Check.Equal(VivePreSessionRecordingChoice.Step.Eye,gate.Current);
                Sample(gate);Sample(gate,trigger:true);Check.Equal(VivePreSessionRecordingChoice.Step.Confirmed,gate.Current);
                Check.True(gate.TaskSelectedInRehearsal&&gate.EyeSelectedInRehearsal);
            });
            suite.Run("pre-session prompt must be visible and menu takes precedence",()=>{
                var gate=new VivePreSessionRecordingChoice(true,false);
                Sample(gate,visible:false);Sample(gate,visible:false,trigger:true);Sample(gate,trigger:true);
                Check.Equal(VivePreSessionRecordingChoice.Step.Task,gate.Current);
                Sample(gate);Sample(gate,trigger:true,menu:true);
                Check.Equal(VivePreSessionRecordingChoice.Step.Declined,gate.Current);Check.True(!gate.TaskSelectedInRehearsal&&!gate.EyeSelectedInRehearsal);
            });
            suite.Run("pre-session focus or selected device change clears previous choices",()=>{
                var gate=new VivePreSessionRecordingChoice(true,true);
                Sample(gate);Sample(gate,trigger:true);Sample(gate,ready:false);
                Check.Equal(VivePreSessionRecordingChoice.Step.Task,gate.Current);
                Sample(gate);Sample(gate,trigger:true);Sample(gate,changed:true);
                Check.Equal(VivePreSessionRecordingChoice.Step.Task,gate.Current);
                Sample(gate,trigger:true);Check.Equal(VivePreSessionRecordingChoice.Step.Task,gate.Current);
            });
            suite.Run("pre-session cancellation cannot leave an accepted capability",()=>{
                var gate=new VivePreSessionRecordingChoice(false,true);
                Sample(gate);Sample(gate,trigger:true);Check.True(gate.EyeSelectedInRehearsal&&!gate.TaskSelectedInRehearsal);
                gate.Cancel();Check.Equal(VivePreSessionRecordingChoice.Step.Cancelled,gate.Current);Check.True(!gate.EyeSelectedInRehearsal&&!gate.TaskSelectedInRehearsal);
                Sample(gate);Sample(gate,trigger:true);Check.Equal(VivePreSessionRecordingChoice.Step.Cancelled,gate.Current);
            });
            suite.Run("completed rehearsal is invalidated by tracking loss or a different device",()=>{
                foreach(bool changed in new[]{false,true})
                {
                    var gate=new VivePreSessionRecordingChoice(false,true);Sample(gate);Sample(gate,trigger:true);
                    Check.Equal(VivePreSessionRecordingChoice.Step.Confirmed,gate.Current);
                    Sample(gate,ready:changed,changed:changed);
                    Check.Equal(VivePreSessionRecordingChoice.Step.Cancelled,gate.Current);Check.True(!gate.EyeSelectedInRehearsal);
                }
            });
            suite.Run("pre-session held trackpad does not arm trigger",()=>{
                var gate=new VivePreSessionRecordingChoice(true,false);
                Sample(gate,trackpad:true);Sample(gate,trackpad:true,trigger:true);Check.Equal(VivePreSessionRecordingChoice.Step.Task,gate.Current);
                Sample(gate);Sample(gate,trigger:true);Check.Equal(VivePreSessionRecordingChoice.Step.Confirmed,gate.Current);
            });
        }
    }
}
