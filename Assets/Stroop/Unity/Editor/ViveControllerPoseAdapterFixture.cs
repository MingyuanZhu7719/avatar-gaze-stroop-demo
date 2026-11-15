#if UNITY_EDITOR && STROOP_XR_PREVIEW
using System;
using System.Linq;
using System.Reflection;
using AvatarStroop.Core;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.XR.OpenXR.Features.Interactions;

namespace AvatarStroop.Unity.Editor
{
    // Real Input System metadata and tracking bits; no loader, runtime focus,
    // native controls, physical wand, participant task, or recording.
    public static class ViveControllerPoseAdapterFixture
    {
        const string Layout="ViveControllerPoseAdapterFixture";
        const string PoseLayout="ViveControllerPoseAdapterFixturePose";
        const BindingFlags Hidden=BindingFlags.Instance|BindingFlags.NonPublic;
        static int verified;
        static void Check(bool value,string message)
        {if(!value)throw new InvalidOperationException("VIVE pose adapter: "+message);verified++;}
        static object Invoke(ViveStroopDeviceInput input,string name,params object[] args)
        {
            var method=typeof(ViveStroopDeviceInput).GetMethod(name,Hidden);
            if(method==null)throw new MissingMethodException(name);
            return method.Invoke(input,args);
        }
        static bool ReadVisualWithoutNativeFocus(ViveStroopDeviceInput input,out object pose)
        {
            // Only exercise private visual validation. Production's public
            // entry still independently insists on real OpenXR focus.
            var args=new object[]{null};
            bool ready=(bool)Invoke(input,"TryGetValidatedVisualPose",args);
            pose=args[0];return ready;
        }
        static T Field<T>(object value,string field)
        {
            var f=value.GetType().GetField(field,Hidden);
            if(f==null)throw new MissingFieldException(field);
            return (T)f.GetValue(value);
        }
        static HTCViveControllerProfile.ViveController Add(string role)
        {
            var device=InputSystem.AddDevice(Layout) as HTCViveControllerProfile.ViveController;
            if(device==null)throw new InvalidOperationException("Synthetic device did not instantiate the official type.");
            InputSystem.SetDeviceUsage(device,role);return device;
        }
        static void Set(HTCViveControllerProfile.ViveController device,bool tracked,int flags,Vector3 position,Quaternion rotation)
        {
            // isTracked can be a bitfield. Apply only this fixture state event,
            // without invoking an input update or native enumeration.
            using(StateEvent.From(device,out var stateEvent))
            {
                device.isTracked.WriteValueIntoEvent(tracked?1f:0f,stateEvent);
                device.trackingState.WriteValueIntoEvent(flags,stateEvent);
                device.devicePosition.WriteValueIntoEvent(position,stateEvent);
                device.deviceRotation.WriteValueIntoEvent(rotation,stateEvent);
                InputState.Change(device,stateEvent);
            }
        }
        public static void Validate()
        {
            verified=0;
            Check(!InputSystem.devices.Any(d=>d is HTCViveControllerProfile.ViveController),
                "fixture will not override an already-present controller");
            Check(!InputSystem.ListLayouts().Contains(Layout)&&!InputSystem.ListLayouts().Contains(PoseLayout),
                "temporary layout names already in use");
            bool ownHaptic=false,ownVive=false,ownPose=false,ownSynthetic=false;
            ViveStroopDeviceInput input=null;
            HTCViveControllerProfile.ViveController left=null,second=null;
            try
            {
                if(!InputSystem.ListLayouts().Contains("Haptic"))
                {InputSystem.RegisterLayout<UnityEngine.XR.OpenXR.Input.HapticControl>("Haptic");ownHaptic=true;}
                var pointer=typeof(HTCViveControllerProfile.ViveController).GetProperty("pointer",
                    BindingFlags.Instance|BindingFlags.Public|BindingFlags.DeclaredOnly);
                Check(pointer!=null,"official pointer control missing");
                InputSystem.RegisterLayout(pointer.PropertyType,PoseLayout);ownPose=true;
                if(!InputSystem.ListLayouts().Contains("ViveController"))
                {InputSystem.RegisterLayout<HTCViveControllerProfile.ViveController>();ownVive=true;}
                // Fixture-specific Stick/Pose types satisfy the package's
                // FinishSetup contract without initializing native OpenXR.
                InputSystem.RegisterLayout("{\"name\":\""+Layout+"\",\"extend\":\"ViveController\",\"controls\":[{"+
                    "\"name\":\"trackpad\",\"layout\":\"Stick\"},{\"name\":\"pointer\",\"layout\":\""+PoseLayout+
                    "\"},{\"name\":\"devicePose\",\"layout\":\""+PoseLayout+"\"}]}");ownSynthetic=true;
                input=new ViveStroopDeviceInput(VivePreviewControllerHand.Automatic);left=Add("LeftHand");
                object pose;
                Check(!ReadVisualWithoutNativeFocus(input,out pose),"unobserved device could show a pose");
                var position=new Vector3(.3f,.8f,-.2f);var rotation=Quaternion.AngleAxis(20,Vector3.up);
                Set(left,true,3,position,rotation);
                Check((bool)Invoke(input,"SelectUniqueController")&&(bool)Invoke(input,"CommitCandidate",left),
                    "synthetic official-device ownership did not commit");
                // Same values as the validated ordinary read would hand off.
                // No invocation or bypass of the ordinary input/focus gate.
                Action capture=()=>Invoke(input,"TryCaptureVisualPose",left,position,rotation);
                capture();
                Check(ReadVisualWithoutNativeFocus(input,out pose),"matching same-frame tracked pose was blocked");
                Check(Field<int>(pose,"Frame")==Time.frameCount&&Field<int>(pose,"DeviceId")==left.deviceId&&
                    Field<VivePreviewControllerHand>(pose,"Hand")==VivePreviewControllerHand.Left,
                    "snapshot did not preserve frame, role and device identity");
                Check(Field<Vector3>(pose,"LocalPosition")==position&&Field<Quaternion>(pose,"LocalRotation")==rotation,
                    "snapshot did not preserve captured tracking-space pose");
                Set(left,true,3,Vector3.one,Quaternion.identity);
                Check(ReadVisualWithoutNativeFocus(input,out pose)&&Field<Vector3>(pose,"LocalPosition")==position&&
                    Field<Quaternion>(pose,"LocalRotation")==rotation,"render validation replaced the ordinary pose values");
                Set(left,false,3,position,rotation);
                Check(!ReadVisualWithoutNativeFocus(input,out pose),"same-frame tracked-bit loss left the wand visible");
                Set(left,true,3,position,rotation);
                Check(!ReadVisualWithoutNativeFocus(input,out pose),"restoration resurrected a stale pose");
                foreach(int flags in new[]{1,2})
                {
                    Set(left,true,3,position,rotation);capture();
                    Set(left,true,flags,position,rotation);
                    Check(!ReadVisualWithoutNativeFocus(input,out pose),"loss of a position/rotation flag left the wand visible");
                }
                Set(left,true,3,position,rotation);capture();second=Add("RightHand");
                Check(!ReadVisualWithoutNativeFocus(input,out pose),"ambiguous inventory exposed an old pose");
                InputSystem.RemoveDevice(second);second=null;
                Check(!ReadVisualWithoutNativeFocus(input,out pose),"ambiguity removal resurrected a stale pose");
                capture();
                Check(!input.Read(Phase.Preparation,false).DeviceReady&&!ReadVisualWithoutNativeFocus(input,out pose),
                    "failed ordinary read did not clear the pose");
                capture();Invoke(input,"ResetTaskInputForFocusGap");
                Check(!ReadVisualWithoutNativeFocus(input,out pose),"focus reset did not clear the pose");
                capture();input.Dispose();
                Check(!ReadVisualWithoutNativeFocus(input,out pose),"disposed adapter retained a visible pose");
            }
            finally
            {
                if(input!=null)input.Dispose();
                if(second!=null&&second.added)InputSystem.RemoveDevice(second);
                if(left!=null&&left.added)InputSystem.RemoveDevice(left);
                if(ownSynthetic)InputSystem.RemoveLayout(Layout);
                if(ownVive)InputSystem.RemoveLayout("ViveController");
                if(ownPose)InputSystem.RemoveLayout(PoseLayout);
                if(ownHaptic)InputSystem.RemoveLayout("Haptic");
            }
            Debug.Log("VIVE_CONTROLLER_POSE_ADAPTER_SYNTHETIC_FIXTURE_PASSED: "+verified+" checks. No native runtime, headset or recording.");
        }
    }
}
#endif
