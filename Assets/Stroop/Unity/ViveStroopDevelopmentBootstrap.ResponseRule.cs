#if STROOP_XR_PREVIEW
using AvatarStroop.Core;
using UnityEngine;

namespace AvatarStroop.Unity
{
    public sealed partial class ViveStroopDevelopmentBootstrap
    {
        ResponseRule selectedResponseRule=ResponseRule.InkColor;
        void DrawResponseRuleChoice()
        {
            GUILayout.Label("Response rule (practice and every block)");
            bool previous=GUI.enabled;
            GUI.enabled=previous&&!configured&&preSessionChoice==null&&recordingSetup==null;
            int choice=ViveOwnerDesktopLayout.Selection("response-rule",(int)selectedResponseRule,
                new[]{"Ink colour","Word meaning"},2);
            if(choice==0||choice==1)selectedResponseRule=(ResponseRule)choice;
            GUI.enabled=previous;
            GUILayout.Label(selectedResponseRule==ResponseRule.WordMeaning?
                "Read the word. RED printed in green: choose RED.":
                "Choose the ink colour. RED printed in green: choose GREEN.");
            GUILayout.Label("The response rule is fixed when session setup starts.");
        }
    }
}
#endif
