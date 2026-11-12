#if UNITY_EDITOR && STROOP_XR_PREVIEW
using System;
using AvatarStroop.Runtime;
using UnityEngine;

namespace AvatarStroop.Unity.Editor
{
    // Pure synthetic policy and neutral-gate checks against the production
    // classes. No scene, Input System device, XR loader, eye worker, socket,
    // participant task, journal or recording file is created or read.
    public static class ViveSingleControllerFixture
    {
        static void Require(bool value,string message)
        {if(!value)throw new InvalidOperationException("Single-controller fixture: "+message);}

        static ViveStroopNeutralState Observe(ViveSingleControllerSelection selection,
            ViveStroopNeutralGate neutral,int count,int id,int role,
            bool trackpadHeld=false,bool triggerHeld=false,bool menuHeld=false)
        {
            // Matches adapter unavailability semantics: an unavailable source
            // disarms the ordinary gate and cannot contribute held controls.
            bool available=selection.Observe(count,id,role);
            return neutral.Observe(available,false,false,available&&trackpadHeld,
                available&&triggerHeld,available&&menuHeld);
        }

        static void ReconnectRequiresNeutral()
        {
            var selection=new ViveSingleControllerSelection();var neutral=new ViveStroopNeutralGate();
            var initial=Observe(selection,neutral,1,8,1);
            Require(initial.PhysicalReady&&!initial.NeutralPending&&!initial.TriggerDown,"initial neutral left wand must arm without an edge");
            Require(Observe(selection,neutral,1,8,1,triggerHeld:true).TriggerDown,"armed trigger must emit one edge");
            var missing=Observe(selection,neutral,0,0,0,triggerHeld:true);
            Require(!missing.PhysicalReady&&missing.LostReady&&!missing.TriggerDown,"disconnect must disarm and report lost readiness");
            var held=Observe(selection,neutral,1,8,1,triggerHeld:true);
            Require(held.PhysicalReady&&held.NeutralPending&&!held.TriggerDown&&!held.MenuDown,"reconnect with held trigger must wait for neutral");
            Require(Observe(selection,neutral,1,8,1,triggerHeld:true).NeutralPending,"continuing to hold after reconnect must remain blocked");
            var release=Observe(selection,neutral,1,8,1);
            Require(!release.NeutralPending&&!release.TriggerDown&&!release.MenuDown,"release must rearm without inventing a press");
            Require(Observe(selection,neutral,1,8,1,triggerHeld:true).TriggerDown,"only a new post-release trigger press may confirm");
            Require(!Observe(selection,neutral,1,8,1,triggerHeld:true).TriggerDown,"held trigger must not repeat its edge");
        }

        static void AmbiguityCannotTransferSource()
        {
            var selection=new ViveSingleControllerSelection();var neutral=new ViveStroopNeutralGate();
            Require(Observe(selection,neutral,1,8,2).PhysicalReady,"initial neutral right wand must be supported");
            var ambiguous=Observe(selection,neutral,2,9,2,triggerHeld:true,menuHeld:true);
            Require(!ambiguous.PhysicalReady&&ambiguous.LostReady&&!ambiguous.TriggerDown&&!ambiguous.MenuDown,
                "multiple wands must block and disarm rather than choose one");
            var held=Observe(selection,neutral,1,8,2,trackpadHeld:true);
            Require(held.PhysicalReady&&held.NeutralPending&&!held.TriggerDown&&!held.MenuDown,
                "original wand returning while trackpad is held must wait for neutral");
            var release=Observe(selection,neutral,1,8,2);
            Require(!release.NeutralPending&&!release.TriggerDown&&!release.MenuDown,
                "original wand may rearm only without a spurious edge");
            var replacement=Observe(selection,neutral,1,9,2,triggerHeld:true);
            Require(!replacement.PhysicalReady&&replacement.LostReady&&!replacement.TriggerDown,
                "replacement wand must not inherit a live session");
            Require(!Observe(selection,neutral,1,8,2).PhysicalReady,
                "identity-change block must remain latched even if the original returns");
            Require(selection.Status.Contains("Restart the view"),"latched source change must explain the required recovery");
        }

        public static void Validate()
        {
            ReconnectRequiresNeutral();
            AmbiguityCannotTransferSource();
            Debug.Log("VIVE_SINGLE_CONTROLLER_SYNTHETIC_FIXTURE_PASSED: 2 cases; reconnect requires neutral and ambiguity cannot transfer source; no XR, Input System device, participant task or recording.");
        }
    }
}
#endif
