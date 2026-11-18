#if UNITY_EDITOR && STROOP_XR_PREVIEW
using System;
using System.Linq;
using System.Reflection;
using AvatarStroop.Core;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.XR.OpenXR.Features.Interactions;

namespace AvatarStroop.Unity.Editor
{
    // External synthetic tests only. Production player has no focus/action
    // override. A disposable Editor overlay simulates just the two OpenXR
    // vendor queries; actual InputSystem actions, pose and button values stay in use.
    public sealed class SyntheticVivePoseDevice : IDisposable
    {
        const string Layout="VivePoseSafetyOrderFixtureController";
        const string PoseLayout="VivePoseSafetyOrderFixturePose";
        const BindingFlags InstanceHidden=BindingFlags.Instance|BindingFlags.NonPublic;
        const BindingFlags StaticHidden=BindingFlags.Static|BindingFlags.NonPublic;
        public HTCViveControllerProfile.ViveController Device {get;private set;}
        bool ownHaptic,ownVive,ownPose,ownLayout;
        public SyntheticVivePoseDevice(string role="LeftHand",bool useVendorOverlay=true)
        {
            if(InputSystem.devices.Any(d=>d is HTCViveControllerProfile.ViveController)||
                InputSystem.ListLayouts().Contains(Layout)||InputSystem.ListLayouts().Contains(PoseLayout))
                throw new InvalidOperationException("Synthetic Vive device requires an isolated fixture.");
            if(useVendorOverlay){SetVendorFocus(true);SetVendorActionsActive(true);}
            try
            {
                if(!InputSystem.ListLayouts().Contains("Haptic"))
                {InputSystem.RegisterLayout<UnityEngine.XR.OpenXR.Input.HapticControl>("Haptic");ownHaptic=true;}
                var pointer=typeof(HTCViveControllerProfile.ViveController).GetProperty("pointer",
                    BindingFlags.Instance|BindingFlags.Public|BindingFlags.DeclaredOnly);
                if(pointer==null)throw new MissingMemberException("Official ViveController pointer control.");
                InputSystem.RegisterLayout(pointer.PropertyType,PoseLayout);ownPose=true;
                if(!InputSystem.ListLayouts().Contains("ViveController"))
                {InputSystem.RegisterLayout<HTCViveControllerProfile.ViveController>();ownVive=true;}
                InputSystem.RegisterLayout("{\"name\":\""+Layout+"\",\"extend\":\"ViveController\",\"controls\":[{"+
                    "\"name\":\"trackpad\",\"layout\":\"Stick\"},{\"name\":\"pointer\",\"layout\":\""+PoseLayout+
                    "\"},{\"name\":\"devicePose\",\"layout\":\""+PoseLayout+"\"}]}");ownLayout=true;
                Device=InputSystem.AddDevice(Layout) as HTCViveControllerProfile.ViveController;
                if(Device==null)throw new InvalidOperationException("Synthetic layout did not instantiate official ViveController.");
                InputSystem.SetDeviceUsage(Device,role);
            }
            catch{Dispose();throw;}
        }
        static void SetVendorField(string name,bool value)
        {
            var field=typeof(ViveStroopDeviceInput).GetField(name,StaticHidden);
            if(field==null)throw new InvalidOperationException("Disposable Editor vendor-state overlay is required; this fixture cannot run in shipping code.");
            field.SetValue(null,value);
        }
        public static void SetVendorFocus(bool focused){SetVendorField("SyntheticPoseFixtureFocus",focused);}
        public static void SetVendorActionsActive(bool active){SetVendorField("SyntheticPoseFixtureActionsActive",active);}
        public void PrepareBindings(ViveStroopDeviceInput input)
        {
            var type=typeof(ViveStroopDeviceInput);
            if(!(bool)type.GetMethod("SelectUniqueController",InstanceHidden).Invoke(input,null))
                throw new InvalidOperationException("Synthetic device selection is unavailable.");
            foreach(var name in new[]{"position","rotation","tracked","trackingState","trackpad","trackpadClick","trigger","menu"})
            {
                var action=(InputAction)type.GetField(name,InstanceHidden).GetValue(input);
                if(!action.enabled||action.controls.Count!=1||!object.ReferenceEquals(action.controls[0].device,Device))
                    throw new InvalidOperationException("Synthetic binding is unavailable: "+name);
            }
        }
        ButtonControl Button(string name)
        {
            var button=Device.TryGetChildControl<ButtonControl>(name);
            if(button==null)throw new MissingMemberException("Official ViveController button: "+name);
            return button;
        }
        public void SetPose(Vector3 position,Quaternion rotation,bool tracked=true,int flags=3,bool triggerHeld=false,
            bool menuHeld=false,bool trackpadHeld=false,float trackpadX=.75f,float trackpadY=0)
        {
            using(StateEvent.From(Device,out var stateEvent))
            {
                Device.isTracked.WriteValueIntoEvent(tracked?1f:0f,stateEvent);
                Device.trackingState.WriteValueIntoEvent(flags,stateEvent);
                Device.devicePosition.WriteValueIntoEvent(position,stateEvent);
                Device.deviceRotation.WriteValueIntoEvent(rotation,stateEvent);
                Device.trackpad.WriteValueIntoEvent(trackpadHeld?new Vector2(trackpadX,trackpadY):Vector2.zero,stateEvent);
                Button("trackpadClicked").WriteValueIntoEvent(trackpadHeld?1f:0f,stateEvent);
                Button("triggerPressed").WriteValueIntoEvent(triggerHeld?1f:0f,stateEvent);
                Button("menu").WriteValueIntoEvent(menuHeld?1f:0f,stateEvent);
                // Applies just this synthetic state event, with normal action
                // change monitors, without running native Input System update.
                InputState.Change(Device,stateEvent);
            }
        }
        public static bool TryReadVisualPose(ViveStroopDeviceInput input,out Vector3 position,out Quaternion rotation)
        {
            position=default;rotation=default;
            var args=new object[]{null};
            var m=typeof(ViveStroopDeviceInput).GetMethod("TryGetCurrentControllerPose",InstanceHidden);
            if(!(bool)m.Invoke(input,args))return false;
            var value=args[0];var type=value.GetType();
            position=(Vector3)type.GetField("LocalPosition",InstanceHidden).GetValue(value);
            rotation=(Quaternion)type.GetField("LocalRotation",InstanceHidden).GetValue(value);
            return true;
        }
        public void Dispose()
        {
            if(Device!=null&&Device.added)InputSystem.RemoveDevice(Device);
            Device=null;
            if(ownLayout){InputSystem.RemoveLayout(Layout);ownLayout=false;}
            if(ownVive){InputSystem.RemoveLayout("ViveController");ownVive=false;}
            if(ownPose){InputSystem.RemoveLayout(PoseLayout);ownPose=false;}
            if(ownHaptic){InputSystem.RemoveLayout("Haptic");ownHaptic=false;}
        }
    }

    public static class ViveControllerPoseSafetyOrderFixture
    {
        static int checks;
        static void Check(bool value,string message)
        {if(!value)throw new InvalidOperationException("VIVE safety order: "+message);checks++;}
        static bool Pose(ViveStroopDeviceInput input,out Vector3 pos,out Quaternion rot)
        {return SyntheticVivePoseDevice.TryReadVisualPose(input,out pos,out rot);}
        static bool HasPose(ViveStroopDeviceInput input)
        {return Pose(input,out _,out _);}
        public static void Validate()
        {
            checks=0;
            using(var controller=new SyntheticVivePoseDevice())
            using(var input=new ViveStroopDeviceInput(VivePreviewControllerHand.Automatic))
            {
                controller.PrepareBindings(input);
                var p=new Vector3(.34f,1.1f,.43f);var q=Quaternion.AngleAxis(23,Vector3.up);
                Vector3 observedPos;Quaternion observedRot;
                controller.SetPose(p,q);
                Check(!input.ReadSafetyMenu(true)&&!HasPose(input),"safety-only poll manufactured a visual sample");
                controller.SetPose(p,q,triggerHeld:true);
                var held=input.Read(Phase.Practice,true);
                Check(held.DeviceReady&&held.NeutralPending&&held.TriggerHeld&&!held.TriggerDown,
                    "real ordinary read did not preserve the pre-neutral held trigger: "+held.Diagnostic);
                Check(HasPose(input),"ordinary tracked read had no valid pose before permissive safety poll");
                Check(!input.ReadSafetyMenu(true),"neutral menu synthesized an edge");
                Check(Pose(input,out observedPos,out observedRot)&&observedPos==p&&observedRot==q,
                    "same-frame valid safety poll erased the ordinary tracked pose");
                // A later valid safety observation of the same device cannot
                // replace the p/q captured in the original ordinary read.
                controller.SetPose(p+Vector3.one,Quaternion.identity);
                Check(!input.ReadSafetyMenu(true)&&Pose(input,out observedPos,out observedRot)&&observedPos==p&&observedRot==q,
                    "safety path refreshed p/q or erased ordinary pose");
                controller.SetPose(p,q);
                var released=input.Read(Phase.Practice,true);
                Check(released.InputReady&&!released.TriggerDown&&!released.MenuDown,"real neutral release invented an edge");
                Check(!input.ReadSafetyMenu(true)&&HasPose(input),"ordinary neutral sample did not survive safety poll");
                controller.SetPose(p,q,menuHeld:true);
                var menu=input.Read(Phase.Practice,true);
                Check(menu.DeviceReady&&menu.MenuHeld&&menu.MenuDown,"ordinary menu edge missing");
                Check(input.ReadSafetyMenu(true)&&HasPose(input),"dedicated safety menu edge changed or erased pose");
                Check(!input.ReadSafetyMenu(true)&&HasPose(input),"a held menu repeated an edge or erased pose");

                controller.SetPose(p,q);
                Check(input.Read(Phase.Practice,true).DeviceReady,"loss case did not start with a valid ordinary sample");
                controller.SetPose(p,q,tracked:false,flags:0);
                Check(!input.ReadSafetyMenu(true)&&!HasPose(input),"physical tracking loss did not clear pose");
                controller.SetPose(p,q);
                Check(!input.ReadSafetyMenu(true)&&!HasPose(input),"tracking restoration resurrected the old pose");

                Check(input.Read(Phase.Practice,true).DeviceReady&&HasPose(input),"focus case did not start with a valid sample");
                SyntheticVivePoseDevice.SetVendorFocus(false);
                Check(!input.ReadSafetyMenu(true)&&!HasPose(input),"OpenXR focus loss did not clear pose");
                SyntheticVivePoseDevice.SetVendorFocus(true);
                Check(!input.ReadSafetyMenu(true)&&!HasPose(input),"focus restoration resurrected the old pose");

                Check(input.Read(Phase.Practice,true).DeviceReady&&HasPose(input),"identity case did not start with a valid sample");
                InputSystem.SetDeviceUsage(controller.Device,"RightHand");
                Check(!input.ReadSafetyMenu(true)&&!HasPose(input),"selected-device role replacement exposed old pose");
                InputSystem.SetDeviceUsage(controller.Device,"LeftHand");
                Check(!input.ReadSafetyMenu(true)&&!HasPose(input),"identity restoration accidentally resumed old pose");
            }
            Debug.Log("VIVE_CONTROLLER_POSE_SAFETY_ORDER_SYNTHETIC_FIXTURE_PASSED: "+checks+
                " checks; real adapter calls and synthetic official ViveController events. Simulated OpenXR vendor activity; no headset or native loader.");
        }
    }
}
#endif
