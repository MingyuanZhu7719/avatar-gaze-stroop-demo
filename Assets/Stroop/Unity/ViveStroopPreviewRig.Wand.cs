#if STROOP_XR_PREVIEW
using System;
using AvatarStroop.Runtime;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.XR;
using UnityEngine.XR.OpenXR;

namespace AvatarStroop.Unity
{
    public sealed partial class ViveStroopPreviewRig
    {
        ViveTrackedWandVisual trackedWandVisual;
        ViveSteamVrRenderModelSource wandModels;
        UnityEngine.XR.InputDevice visualController;
        bool hasVisualController,controllerVisualUnavailable;
        double nextModelAttempt;
        string visualModelIssue;
        string resolvedModel;
        float[] resolvedGrip;
        uint resolvedContext,installedContext;
        int resolvedHand;
        internal ViveTrackedWandVisual TrackedWandVisual {get{return trackedWandVisual;}}
        bool ControllerVisualUnavailable {get{return controllerVisualUnavailable;}}

        void LateUpdate(){RefreshTrackedWandVisual(previewCamera);}
        // Task ray/input admission keeps the stricter existing head-ready gate.
        bool WandTrackingSpaceCurrent()
        {
#if UNITY_EDITOR
            if(SyntheticRawTrackingCurrent)return cameraOffset!=null;
#endif
            return isActiveAndEnabled&&running&&!stopping&&headReady&&headReadyFrame==Time.frameCount&&
                alignment!=null&&alignment.Applied&&!referenceChanged&&cameraOffset!=null&&
                previewCamera!=null&&previewCamera.isActiveAndEnabled&&GraphicsSettings.currentRenderPipeline==null&&
                display!=null&&display.running&&input!=null&&input.running&&input.GetTrackingOriginMode()==alignedMode&&XrFocused;
        }
        bool WandVisualTrackingSpaceCurrent()
        {
            // Do not depend on task polling: terminal/hold screens may stop it.
            // RawHoldPoseCurrent independently checks the current real HMD pose,
            // unchanged reference space/session, presence and running XR systems.
            if(!isActiveAndEnabled||!running||cameraOffset==null||alignment==null||!hasHeadDevice||
                previewCamera==null||!previewCamera.isActiveAndEnabled||GraphicsSettings.currentRenderPipeline!=null||
                display==null||!display.running||input==null||!input.running||!XrFocused)return false;
            var presence=OpenXRSettings.Instance==null?null:OpenXRSettings.Instance.GetFeature<ViveVerifiedPresenceFeature>();
            bool present=presence==null||hasPresenceEpoch&&presence.SessionEpoch==presenceEpoch&&
                (!presence.enabled||!presence.CapabilityVerified||OpenXRUtility.IsUserPresent);
            var head=InputDevices.GetDeviceAtXRNode(XRNode.Head);
            if(!ControllerVisualHeadPermitted(alignment.Applied,head.Equals(headDevice)&&ValidTrackedHeadPose(head),
                !referenceChanged,present,input.GetTrackingOriginMode(),alignedMode))return false;
            // Before placement, the camera and controller both use the same raw
            // tracking space. The startup trigger prompt can therefore show the
            // actual wand without authorizing task presentation or alignment.
            return !alignment.Applied||RawHoldPoseCurrent();
        }
        internal static bool ControllerVisualHeadPermitted(bool aligned,bool currentHead,bool referenceCurrent,bool present,
            TrackingOriginModeFlags origin,TrackingOriginModeFlags placedOrigin)
        {return currentHead&&referenceCurrent&&present&&(origin==TrackingOriginModeFlags.Device||origin==TrackingOriginModeFlags.Floor)&&
            (!aligned||origin==placedOrigin);}
        // Completion/fault paths intentionally stop task/head polling. Keep the
        // already masked neutral camera in the same live tracking coordinates
        // as the controller, without publishing any head/task frame authority.
        void RefreshTerminalCameraPose()
        {
            try
            {
                if(!isActiveAndEnabled||previewCamera==null||previewCamera.cullingMask!=0||!XrFocused||!RawHoldPoseCurrent())return;
                var head=InputDevices.GetDeviceAtXRNode(XRNode.Head);Vector3 position;Quaternion rotation;
                if(!head.Equals(headDevice)||!ValidTrackedHeadPose(head)||
                    !head.TryGetFeatureValue(CommonUsages.devicePosition,out position)||
                    !head.TryGetFeatureValue(CommonUsages.deviceRotation,out rotation))return;
                TryApplyTerminalCameraPose(previewCamera,true,position,rotation);
            }
            catch(Exception error) when(!(error is OutOfMemoryException||error is StackOverflowException||error is AccessViolationException))
            { /* Optional neutral-view pose cannot mutate task/recording state. */ }
        }
        internal static bool TryApplyTerminalCameraPose(Camera camera,bool validSameReferenceHead,Vector3 position,Quaternion rotation)
        {
            if(camera==null||!camera.isActiveAndEnabled||camera.cullingMask!=0||!validSameReferenceHead||!Finite(position)||!Finite(rotation))return false;
            float n=rotation.x*rotation.x+rotation.y*rotation.y+rotation.z*rotation.z+rotation.w*rotation.w;
            if(!Finite(n)||Mathf.Abs(n-1)>.000001f)return false;
            camera.transform.SetLocalPositionAndRotation(position,rotation);return true;
        }
        static bool FreshVisualPose(UnityEngine.XR.InputDevice candidate,out Vector3 position,out Quaternion rotation)
        {
            position=default(Vector3);rotation=default(Quaternion);bool tracked;InputTrackingState state;
            if(!candidate.isValid||!candidate.TryGetFeatureValue(CommonUsages.isTracked,out tracked)||!tracked||
                !candidate.TryGetFeatureValue(CommonUsages.trackingState,out state)||
                (state&(InputTrackingState.Position|InputTrackingState.Rotation))!=(InputTrackingState.Position|InputTrackingState.Rotation)||
                !candidate.TryGetFeatureValue(CommonUsages.devicePosition,out position)||!candidate.TryGetFeatureValue(CommonUsages.deviceRotation,out rotation))return false;
            float n=rotation.x*rotation.x+rotation.y*rotation.y+rotation.z*rotation.z+rotation.w*rotation.w;
            return !float.IsNaN(position.x)&&!float.IsInfinity(position.x)&&!float.IsNaN(position.y)&&!float.IsInfinity(position.y)&&
                !float.IsNaN(position.z)&&!float.IsInfinity(position.z)&&!float.IsNaN(n)&&!float.IsInfinity(n)&&Mathf.Abs(n-1)<.02f;
        }
        bool ReadVisualPose(out UnityEngine.XR.InputDevice current,out int hand,out Vector3 position,out Quaternion rotation)
        {
            current=default(UnityEngine.XR.InputDevice);hand=0;position=default(Vector3);rotation=default(Quaternion);
            var choice=development.ControllerVisualHand;
            if(choice==VivePreviewControllerHand.Left||choice==VivePreviewControllerHand.Right)
            {
                hand=(int)choice;current=InputDevices.GetDeviceAtXRNode(hand==1?XRNode.LeftHand:XRNode.RightHand);
                return FreshVisualPose(current,out position,out rotation);
            }
            var left=InputDevices.GetDeviceAtXRNode(XRNode.LeftHand);var right=InputDevices.GetDeviceAtXRNode(XRNode.RightHand);
            Vector3 lp,rp;Quaternion lr,rr;
            bool l=FreshVisualPose(left,out lp,out lr),r=FreshVisualPose(right,out rp,out rr);
            if(l==r)return false;
            hand=l?1:2;current=l?left:right;position=l?lp:rp;rotation=l?lr:rr;return true;
        }
        // XR feature reads are independent of the task adapter. No button/action
        // Read, neutral gate, selected-device mutation or input edge is consumed.
        void RefreshTrackedWandVisual(Camera camera)
        {
            controllerVisualUnavailable=false;
            if(camera!=previewCamera)return;
            try
            {
                if(camera==null||development==null||!development.ControllerVisualPhaseAllowed||!WandVisualTrackingSpaceCurrent())
                {HideTrackedWandVisual();return;}
                UnityEngine.XR.InputDevice current;int hand;Vector3 position;Quaternion rotation;
                if(!ReadVisualPose(out current,out hand,out position,out rotation))
                {controllerVisualUnavailable=true;if(trackedWandVisual!=null)trackedWandVisual.ShowTrackingUnavailable(true,Time.frameCount);return;}
                if(!hasVisualController||!visualController.Equals(current))
                {
                    DisposeWandGeometry();visualController=current;hasVisualController=true;resolvedModel=null;resolvedGrip=null;
                }
                if(trackedWandVisual==null)
                {
                    trackedWandVisual=ViveTrackedWandVisual.Create(cameraOffset,0);
                    trackedWandVisual.ConfigureCamera(previewCamera,0);
                }
                if(wandModels==null)wandModels=new ViveSteamVrRenderModelSource();
                if(!wandModels.Ready)
                {
                    if(Time.realtimeSinceStartupAsDouble<nextModelAttempt){trackedWandVisual.Hide();return;}
                    nextModelAttempt=Time.realtimeSinceStartupAsDouble+2;
                    if(!wandModels.TryOpen()){NoteWandModelIssue(wandModels.LastIssue);trackedWandVisual.Hide();return;}
                    Debug.Log("VIVE_CONTROLLER_NATIVE_CONTEXT: selected="+wandModels.RuntimeLibraryPath+
                        "; loaded="+wandModels.LoadedRuntimeLibraryPath+"; mode="+wandModels.ContextInitializationMode+
                        "; token="+wandModels.ContextToken);
                }
                // Model and grip calibration are static for this device/context;
                // keep fresh pose reads every frame without querying/allocating
                // native model properties twice on every rendered frame.
                if(resolvedModel==null||resolvedContext!=wandModels.ContextToken||resolvedHand!=hand)
                {
                    resolvedModel=null;resolvedGrip=null;
                    if(!wandModels.TryResolve(hand,current.serialNumber,out resolvedModel,out resolvedGrip))
                    {resolvedModel=null;trackedWandVisual.Hide();NoteWandModelIssue("tracked_device_model_unavailable");return;}
                    resolvedContext=wandModels.ContextToken;resolvedHand=hand;
                }
                if(trackedWandVisual.ModelName!=resolvedModel||installedContext!=resolvedContext)
                {
                    var model=wandModels.PollModel(resolvedModel);
                    if(model==null){trackedWandVisual.Hide();if(wandModels.LastIssue!=null)NoteWandModelIssue(wandModels.LastIssue);return;}
                    trackedWandVisual.InstallModel(model,resolvedGrip);
                    installedContext=resolvedContext;
                    Debug.Log("VIVE_CONTROLLER_RUNTIME_MODEL: "+resolvedModel+"; OpenXR grip alignment; current tracked hand="+hand);
                    visualModelIssue=null;
                }
                // Initial runtime loading can span multiple native calls; sample
                // the current grip again before publishing that first model.
                if(!WandVisualTrackingSpaceCurrent()||!FreshVisualPose(current,out position,out rotation))
                {trackedWandVisual.Hide();return;}
                trackedWandVisual.SetTrackpadMapping(camera.cullingMask!=0&&development.ControllerColourMappingAllowed);
                trackedWandVisual.Apply(true,true,position,rotation,Time.frameCount);
            }
            catch(Exception error) when(!(error is OutOfMemoryException||error is StackOverflowException||error is AccessViolationException))
            {HideTrackedWandVisual();NoteWandModelIssue(error.GetType().Name);}
        }
        void NoteWandModelIssue(string issue)
        {
            if(issue==visualModelIssue)return;visualModelIssue=issue;
            Debug.LogWarning("VIVE_CONTROLLER_VISUAL_UNAVAILABLE: "+issue+"; task input and recording are unchanged.");
        }
        void HideTrackedWandVisual(){if(trackedWandVisual!=null)trackedWandVisual.Hide();}
        void DisposeWandGeometry()
        {
            var old=trackedWandVisual;trackedWandVisual=null;
            if(old==null)return;old.Hide();
            if(Application.isPlaying)Destroy(old.gameObject);else DestroyImmediate(old.gameObject);
        }
        void DisposeTrackedWandVisual()
        {
            DisposeWandGeometry();controllerVisualUnavailable=false;hasVisualController=false;resolvedModel=null;resolvedGrip=null;
            if(wandModels!=null){wandModels.Dispose();wandModels=null;}
        }
    }
}
#endif
