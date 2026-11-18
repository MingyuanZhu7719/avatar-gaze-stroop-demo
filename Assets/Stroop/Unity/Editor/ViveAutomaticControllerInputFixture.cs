#if UNITY_EDITOR && STROOP_XR_PREVIEW
using System;
using System.Linq;
using System.Reflection;
using AvatarStroop.Core;
using AvatarStroop.Runtime;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.XR.OpenXR.Features.Interactions;

namespace AvatarStroop.Unity.Editor
{
    // Isolated Input System devices only. No XR loader, SteamVR client, eye
    // worker, physical controller, socket, participant task or recording.
    // The positive commit seam uses explicitly verified synthetic pose state;
    // it is not proof of a focused native OpenXR session or hardware acceptance.
    public static class ViveAutomaticControllerInputFixture
    {
        const string SyntheticLayout="ViveAutomaticControllerFixture";
        const string SyntheticPoseLayout="ViveAutomaticControllerFixturePose";
        static int assertions;
        static void Require(bool value,string message)
        {if(!value)throw new InvalidOperationException("Automatic controller input fixture: "+message);assertions++;}
        static object Get(object target,string name)
        {
            var field=target.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic);
            Require(field!=null,"missing field "+name);return field.GetValue(target);
        }
        static object Call(object target,string name,params object[] arguments)
        {
            var method=target.GetType().GetMethod(name,BindingFlags.Instance|BindingFlags.NonPublic);
            Require(method!=null,"missing method "+name);return method.Invoke(target,arguments);
        }
        // Invoke the physical classification used when OpenXR reports inactive
        // button actions. This fixture supplies pose/binding states only.
        static void ExpectActivationCandidate(ViveStroopDeviceInput input,bool activationPending,bool sameDevicePoseLoss,string reason)
        {
            var arguments=new object[]{false};
            bool actual=(bool)Call(input,"TryReadActivationCandidate",arguments);
            Require(actual==activationPending&&(bool)arguments[0]==sameDevicePoseLoss,reason);
        }
        static HTCViveControllerProfile.ViveController Add(string role)
        {
            var device=InputSystem.AddDevice(SyntheticLayout) as HTCViveControllerProfile.ViveController;
            Require(device!=null,"synthetic layout must instantiate the official ViveController type");
            InputSystem.SetDeviceUsage(device,role);return device;
        }
        static void SetSyntheticTrackedPose(HTCViveControllerProfile.ViveController device)
        {
            // Write through the controls into one full device state event:
            // isTracked can be a bitfield and cannot use Change(control,T).
            // Apply only this synthetic event, without polling native input.
            using(StateEvent.From(device,out var stateEvent))
            {
                device.isTracked.WriteValueIntoEvent(1f,stateEvent);
                device.trackingState.WriteValueIntoEvent(3,stateEvent);
                device.devicePosition.WriteValueIntoEvent(Vector3.zero,stateEvent);
                device.deviceRotation.WriteValueIntoEvent(Quaternion.identity,stateEvent);
                InputState.Change(device,stateEvent);
            }
        }
        public static void Validate()
        {
            assertions=0;
            Require(!InputSystem.devices.Any(device=>device is HTCViveControllerProfile.ViveController),
                "isolated fixture must not replace existing controller devices");
            Require(!InputSystem.ListLayouts().Contains(SyntheticLayout),"isolated synthetic layout name is already in use");
            Require(!InputSystem.ListLayouts().Contains(SyntheticPoseLayout),"isolated synthetic pose layout name is already in use");
            bool ownLayout=false,ownHaptic=false,ownPose=false,ownSynthetic=false;
            ViveStroopDeviceInput input=null;
            HTCViveControllerProfile.ViveController right=null,left=null,second=null;
            try
            {
                // OpenXRInput.RegisterLayouts normally registers these control
                // dependencies before the interaction profile. This isolated
                // fixture does not initialize OpenXR. Preserve existing
                // global layouts, including a Pose registered by Input System.
                if(!InputSystem.ListLayouts().Contains("Haptic"))
                {
                    InputSystem.RegisterLayout<UnityEngine.XR.OpenXR.Input.HapticControl>("Haptic");ownHaptic=true;
                }
                var pose=typeof(HTCViveControllerProfile.ViveController).GetProperty("pointer",BindingFlags.Instance|BindingFlags.Public|BindingFlags.DeclaredOnly);
                Require(pose!=null,"official ViveController pointer control type is missing");
                InputSystem.RegisterLayout(pose.PropertyType,SyntheticPoseLayout);ownPose=true;
                if(!InputSystem.ListLayouts().Contains("ViveController"))
                {
                    InputSystem.RegisterLayout<HTCViveControllerProfile.ViveController>();ownLayout=true;
                }
                // The package's ViveController.FinishSetup requests a
                // StickControl even when its compiled trackpad property is
                // Vector2Control. Explicitly satisfy that constructor in a
                // fixture-only derived layout; preserve the official device
                // type, inherited action paths and all same-device checks.
                // This local synthetic layout is not native-profile parity.
                // OpenXR and Input System both define a class named
                // PoseControl. Bind both pose children to the profile's exact
                // compiled pointer type under a private layout name, rather
                // than assuming the current global Pose uses that type.
                InputSystem.RegisterLayout("{\"name\":\""+SyntheticLayout+"\",\"extend\":\"ViveController\",\"controls\":[{\"name\":\"trackpad\",\"layout\":\"Stick\"},{\"name\":\"pointer\",\"layout\":\""+SyntheticPoseLayout+"\"},{\"name\":\"devicePose\",\"layout\":\""+SyntheticPoseLayout+"\"}]}");
                ownSynthetic=true;
                input=new ViveStroopDeviceInput(VivePreviewControllerHand.Automatic);
                var actions=(InputActionMap)Get(input,"actions");
                Require(actions.actions.Count==16,"automatic constructor must create both eight-action groups");
                foreach(var action in actions.actions)Require(action.enabled,"both role groups must be enabled before XR starts: "+action.name);
                var selection=(ViveSingleControllerSelection)Get(input,"selection");

                right=Add("RightHand");
                var unavailable=input.Read(Phase.Preparation,false);
                Require(!unavailable.DeviceReady&&!selection.IsCommitted,"head-unavailable Right candidate acquired ownership");
                Require(!input.ReadSafetyMenu(false)&&!selection.IsCommitted,"safety read acquired an unready initial identity");
                Require(input.LogicalDeviceDiagnostic.Contains("candidate only")&&input.LogicalDeviceDiagnostic.Contains("RightHand"),"candidate metadata snapshot missing");
                Require(!unavailable.Diagnostic.Contains("id="),"logical IDs must not enter task-facing diagnostics");
                InputSystem.RemoveDevice(right);right=null;
                left=Add("LeftHand");input.Read(Phase.Preparation,false);
                Require(!selection.IsCommitted&&input.Hand==VivePreviewControllerHand.Left,"unready Right candidate poisoned later Left inspection");

                // A tracked synthetic Left plus an untracked synthetic Right
                // is still ambiguous. Never filter multiple devices by pose.
                SetSyntheticTrackedPose(left);
                second=Add("RightHand");
                unavailable=input.Read(Phase.Preparation,false);
                Require(!unavailable.DeviceReady&&!selection.IsCommitted&&unavailable.Diagnostic.Contains("More than one"),"multiple-device gate filtered out an untracked controller");
                InputSystem.RemoveDevice(second);second=null;

                Require((bool)Call(input,"SelectUniqueController"),"unique Left candidate could not be inspected");
                foreach(string field in new[]{"position","rotation","tracked","trackingState","trackpad","trackpadClick","trigger","menu"})
                {
                    var action=(InputAction)Get(input,field);
                    Require(action.enabled&&action.controls.Count==1&&object.ReferenceEquals(action.controls[0].device,left),"selected action did not resolve to the same Left device: "+field);
                }
                Require(left.isTracked.ReadValue()>.5f&&(left.trackingState.ReadValue()&3)==3&&left.deviceRotation.ReadValue()==Quaternion.identity,
                    "explicit synthetic positive pose prerequisite is missing");
                second=Add("RightHand");
                Require(!(bool)Call(input,"CommitCandidate",second),"mismatched device may not commit a controller candidate");
                Require(!selection.IsCommitted,"mismatched bound device acquired ownership");
                InputSystem.RemoveDevice(second);second=null;
                Require((bool)Call(input,"CommitCandidate",left)&&selection.IsCommitted&&selection.SelectedDeviceId==left.deviceId&&selection.SelectedRole==1,
                    "validated synthetic Left candidate did not acquire first ownership");
                // Existing ownership remains strict even when presentation is
                // unavailable: inspection must detect a new role before it.
                InputSystem.SetDeviceUsage(left,"RightHand");
                unavailable=input.Read(Phase.Preparation,false);
                Require(!unavailable.DeviceReady&&unavailable.Diagnostic.Contains("identity changed"),"postcommit role change was not latched before presentation checks");
                InputSystem.SetDeviceUsage(left,"LeftHand");
                Require(!input.Read(Phase.Preparation,false).DeviceReady&&input.ControllerStatus.Contains("identity changed"),"restoring the old role revived a latched lifecycle");

                // Symmetric positive case in a fresh adapter lifecycle. This
                // remains a synthetic validated-pose commit seam, not proof of
                // native OpenXR focus or a physical right-hand controller.
                input.Dispose();input=null;InputSystem.RemoveDevice(left);left=null;
                input=new ViveStroopDeviceInput(VivePreviewControllerHand.Automatic);
                right=Add("RightHand");selection=(ViveSingleControllerSelection)Get(input,"selection");
                Require(!input.Read(Phase.Preparation,false).DeviceReady&&!selection.IsCommitted,
                    "fresh unready Right candidate acquired ownership before validation");
                SetSyntheticTrackedPose(right);
                Require((bool)Call(input,"SelectUniqueController"),"unique Right candidate could not be inspected");
                foreach(string field in new[]{"position","rotation","tracked","trackingState","trackpad","trackpadClick","trigger","menu"})
                {
                    var action=(InputAction)Get(input,field);
                    Require(action.enabled&&action.controls.Count==1&&object.ReferenceEquals(action.controls[0].device,right),
                        "selected action did not resolve to the same Right device: "+field);
                }
                Require(right.isTracked.ReadValue()>.5f&&(right.trackingState.ReadValue()&3)==3&&right.deviceRotation.ReadValue()==Quaternion.identity,
                    "explicit synthetic Right positive pose prerequisite is missing");
                Require((bool)Call(input,"CommitCandidate",right)&&selection.IsCommitted&&selection.SelectedDeviceId==right.deviceId&&selection.SelectedRole==2,
                    "validated synthetic Right candidate did not acquire first ownership");
                ExpectActivationCandidate(input,true,false,"a valid pose did not permit waiting for button activation");
                using(StateEvent.From(right,out var lostTracking))
                {right.trackingState.WriteValueIntoEvent(1,lostTracking);InputState.Change(right,lostTracking);}
                ExpectActivationCandidate(input,false,false,"an unseen controller supplied recovery evidence before a validated read");
                var last=typeof(ViveStroopDeviceInput).GetField("lastDevice",BindingFlags.Instance|BindingFlags.NonPublic);
                Require(last!=null,"validated identity field missing");
                last.SetValue(input,right);
                ExpectActivationCandidate(input,false,true,"inactive buttons with partial tracking on the established controller lost the recoverable hold");
                using(StateEvent.From(right,out var untracked))
                {
                    right.isTracked.WriteValueIntoEvent(0f,untracked);
                    right.trackingState.WriteValueIntoEvent(0,untracked);
                    InputState.Change(right,untracked);
                }
                ExpectActivationCandidate(input,false,true,"inactive buttons with no tracking on the established controller lost the recoverable hold");
                Call(input,"Unavailable","synthetic unavailable",false,false);
                Require(object.ReferenceEquals(last.GetValue(input),right),"invalid frame erased established identity");
                second=Add("LeftHand");
                last.SetValue(input,second);
                ExpectActivationCandidate(input,false,false,"a substituted device supplied recovery evidence");
                last.SetValue(input,right);
                var triggerField=typeof(ViveStroopDeviceInput).GetField("trigger",BindingFlags.Instance|BindingFlags.NonPublic);
                var rightTrigger=(InputAction)Get(input,"trigger");
                var leftTrigger=((InputAction[])Get(input,"leftActions"))[6];
                Require(leftTrigger.controls.Count==1&&object.ReferenceEquals(leftTrigger.controls[0].device,second),"cross-device trigger setup failed");
                triggerField.SetValue(input,leftTrigger);
                ExpectActivationCandidate(input,false,false,"a button bound to a different controller supplied recovery evidence");
                triggerField.SetValue(input,rightTrigger);
                InputSystem.RemoveDevice(second);second=null;
                rightTrigger.Disable();
                ExpectActivationCandidate(input,false,false,"a disabled button binding supplied recovery evidence");
                rightTrigger.Enable();
                using(StateEvent.From(right,out var malformed))
                {
                    right.devicePosition.WriteValueIntoEvent(new Vector3(float.NaN,0,0),malformed);
                    InputState.Change(right,malformed);
                }
                Require(float.IsNaN(right.devicePosition.ReadValue().x),"nonfinite physical position did not reach the synthetic controller");
                ExpectActivationCandidate(input,false,false,"a nonfinite position was accepted as a recoverable pose outage");
                SetSyntheticTrackedPose(right);
                ExpectActivationCandidate(input,true,false,"the same controller could not await button activation after reacquiring its pose");
                var neutral=(ViveStroopNeutralGate)Get(input,"neutralGate");
                neutral.Observe(true,false,false,false,false,false);
                Require(neutral.Observe(true,false,false,false,true,false).TriggerDown,"synthetic pre-gap trigger edge missing");
                Call(input,"ResetTaskInputForFocusGap");
                Require(selection.IsCommitted&&selection.SelectedDeviceId==right.deviceId&&selection.SelectedRole==2,
                    "focus input reset replaced an established controller identity");
                Require(input.DiagnosticSnapshotFrame==-1,"focus reset retained a current input diagnostic");
                var held=neutral.Observe(true,false,false,false,true,false);
                Require(held.NeutralPending&&!held.TriggerDown,"held trigger across a focus gap became a response");
                Require(!neutral.Observe(true,false,false,false,false,false).TriggerDown,"neutral release became a response");
                Require(neutral.Observe(true,false,false,false,true,false).TriggerDown,"fresh trigger after focus release was lost");
                Debug.Log("VIVE_AUTOMATIC_CONTROLLER_INPUT_SYNTHETIC_FIXTURE_PASSED: "+assertions+" assertions; both groups enabled, unready candidate unlocked, symmetric synthetic Left/Right commits, multiple-device and established-role guards; fixture-only derived layout, not native-profile parity; no XR or physical input.");
            }
            finally
            {
                if(input!=null)input.Dispose();
                if(second!=null&&second.added)InputSystem.RemoveDevice(second);
                if(left!=null&&left.added)InputSystem.RemoveDevice(left);
                if(right!=null&&right.added)InputSystem.RemoveDevice(right);
                if(ownSynthetic)InputSystem.RemoveLayout(SyntheticLayout);
                if(ownLayout)InputSystem.RemoveLayout("ViveController");
                if(ownPose)InputSystem.RemoveLayout(SyntheticPoseLayout);
                if(ownHaptic)InputSystem.RemoveLayout("Haptic");
            }
        }
    }
}
#endif
