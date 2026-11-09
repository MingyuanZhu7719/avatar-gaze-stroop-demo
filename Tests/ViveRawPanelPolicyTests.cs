using AvatarStroop.Runtime;
using AvatarStroop.Core;

namespace AvatarStroop.Tests
{
    public static class ViveRawPanelPolicyTests
    {
        public static void Register(TestSuite suite)
        {
            suite.Run("raw panel remains protected from another camera after its callback",()=>{
                // The panel remains exposed until it has been globally hidden,
                // even if the pending render token has already been consumed.
                Check.True(ViveRawPanelPolicy.RejectOtherCamera(true,true,false,false,true));
                Check.True(ViveRawPanelPolicy.RejectOtherCamera(true,true,false,true,false));
                Check.True(!ViveRawPanelPolicy.RejectOtherCamera(true,true,true,false,true));
                Check.True(!ViveRawPanelPolicy.RejectOtherCamera(true,false,false,false,true));
                Check.True(!ViveRawPanelPolicy.RejectOtherCamera(false,true,false,false,true));
                Check.True(!ViveRawPanelPolicy.RejectOtherCamera(true,true,false,false,false));
            });
            suite.Run("raw countdown requires the expected unchanged visible number",()=>{
                Check.True(ViveRawPanelPolicy.CountdownTextMatches("3","3","3"));
                Check.True(!ViveRawPanelPolicy.CountdownTextMatches("3","2","2"));
                Check.True(!ViveRawPanelPolicy.CountdownTextMatches("3","3","2"));
                Check.True(!ViveRawPanelPolicy.CountdownTextMatches("3","3",null));
                Check.True(!ViveRawPanelPolicy.CountdownTextMatches("", "", ""));
            });
            suite.Run("raw panel witness cannot cross question intro rating or block changes",()=>{
                var task=new TaskView{Phase=Phase.Questionnaire,BlockIndex=1,QuestionnaireItem="item one",ReadyEnabled=true};
                Check.True(ViveRawPanelPolicy.SamePanelState(Phase.Questionnaire,1,false,3,0,true,"item one",null,task,false,3,0));
                Check.True(!ViveRawPanelPolicy.SamePanelState(Phase.Questionnaire,1,true,0,0,true,"item one",null,task,false,3,0));
                Check.True(!ViveRawPanelPolicy.SamePanelState(Phase.Questionnaire,1,false,3,0,true,"item one",null,task,false,4,0));
                Check.True(!ViveRawPanelPolicy.SamePanelState(Phase.Questionnaire,1,false,3,0,true,"item one",null,task,false,3,1));
                task.QuestionnaireItem="item two";
                Check.True(!ViveRawPanelPolicy.SamePanelState(Phase.Questionnaire,1,false,3,0,true,"item one",null,task,false,3,0));
                task.QuestionnaireItem="item one";task.BlockIndex=2;
                Check.True(!ViveRawPanelPolicy.SamePanelState(Phase.Questionnaire,1,false,3,0,true,"item one",null,task,false,3,0));
                task.BlockIndex=1;task.Phase=Phase.Rest;
                Check.True(!ViveRawPanelPolicy.SamePanelState(Phase.Questionnaire,1,false,3,0,true,"item one",null,task,false,3,0));
            });
            suite.Run("raw non-trial panel rejects trial and ended phases",()=>{
                Check.True(ViveRawPanelPolicy.IsPanelPhase(Phase.Countdown));
                Check.True(ViveRawPanelPolicy.IsPanelPhase(Phase.PracticeCorrection));
                Check.True(ViveRawPanelPolicy.IsPanelPhase(Phase.Questionnaire));
                Check.True(!ViveRawPanelPolicy.IsPanelPhase(Phase.Practice));
                Check.True(!ViveRawPanelPolicy.IsPanelPhase(Phase.MainTask));
                Check.True(!ViveRawPanelPolicy.IsPanelPhase(Phase.Ended));
                Check.True(!ViveRawPanelPolicy.IsPanelPhase(Phase.NeedsProtocolDecision));
            });
        }
    }
}
