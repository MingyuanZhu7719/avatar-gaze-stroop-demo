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
        static HTCViveControllerProfile.ViveController Add(string role)
        {
            var device=InputSystem.AddDevice<HTCViveControllerProfile.ViveController>();
            InputSystem.SetDeviceUsage(device,role);return device;
        }
        public static void Validate()
        {
            assertions=0;
            Require(!InputSystem.devices.Any(device=>device is HTCViveControllerProfile.ViveController),
                "isolated fixture must not replace existing controller devices");
            bool ownLayout=!InputSystem.ListLayouts().Contains("ViveController");
            if(ownLayout)InputSystem.RegisterLayout<HTCViveControllerProfile.ViveController>();
            ViveStroopDeviceInput input=null;
            HTCViveControllerProfile.ViveController right=null,left=null,second=null;
            try
            {
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
                InputState.Change(left.isTracked,1f);InputState.Change(left.trackingState,3);
                InputState.Change(left.devicePosition,Vector3.zero);InputState.Change(left.deviceRotation,Quaternion.identity);
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
                InputState.Change(right.isTracked,1f);InputState.Change(right.trackingState,3);
                InputState.Change(right.devicePosition,Vector3.zero);InputState.Change(right.deviceRotation,Quaternion.identity);
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
                Debug.Log("VIVE_AUTOMATIC_CONTROLLER_INPUT_SYNTHETIC_FIXTURE_PASSED: "+assertions+" assertions; both groups enabled, unready candidate unlocked, symmetric synthetic Left/Right commits, multiple-device and established-role guards; no XR or physical input.");
            }
            finally
            {
                if(input!=null)input.Dispose();
                if(second!=null&&second.added)InputSystem.RemoveDevice(second);
                if(left!=null&&left.added)InputSystem.RemoveDevice(left);
                if(right!=null&&right.added)InputSystem.RemoveDevice(right);
                if(ownLayout)InputSystem.RemoveLayout("ViveController");
            }
        }
    }
}
#endif
