#if STROOP_XR_PREVIEW
using AvatarStroop.Core;
using UnityEngine;

namespace AvatarStroop.Unity
{
    public sealed partial class ViveStroopDevelopmentBootstrap
    {
        ResponseRule selectedResponseRule=ResponseRule.InkColor;
        TaskKind selectedTaskKind=TaskKind.Stroop;
        void DrawResponseRuleChoice()
        {
            GUILayout.Label("Task (practice and every block)");
            bool previous=GUI.enabled;
            GUI.enabled=previous&&!configured&&preSessionChoice==null&&recordingSetup==null;
            int choice=ViveOwnerDesktopLayout.Selection("response-rule",selectedTaskKind==TaskKind.VisualSearch?2:(int)selectedResponseRule,
                new[]{"Ink colour","Word meaning","Visual search (12 / 24 items)"},1);
            if(choice==0||choice==1){selectedTaskKind=TaskKind.Stroop;selectedResponseRule=(ResponseRule)choice;}
            else if(choice==2){selectedTaskKind=TaskKind.VisualSearch;selectedDesign=0;ownerPreviewWoz=false;observationOptIn=false;}
            GUI.enabled=previous;
            GUILayout.Label(selectedTaskKind==TaskKind.VisualSearch?
                "Find the matching backpack. Point with the controller and pull the trigger. Each block: 12 trials with 12 items, 12 with 24 items. 30 seconds per trial.":selectedResponseRule==ResponseRule.WordMeaning?
                "Read the word. RED printed in green: choose RED.":
                "Choose the ink colour. RED printed in green: choose GREEN.");
            GUILayout.Label("The task is fixed when session setup starts.");
        }
    }
}
#endif
