#if STROOP_XR_PREVIEW
using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace AvatarStroop.Unity
{
    public sealed partial class ViveStroopPreviewRig
    {
        ViveTrackedWandVisual trackedWandVisual;
        bool wandVisualFailed,controllerVisualUnavailable;
        internal ViveTrackedWandVisual TrackedWandVisual {get{return trackedWandVisual;}}
        bool ControllerVisualUnavailable {get{return controllerVisualUnavailable;}}

        void LateUpdate(){RefreshTrackedWandVisual(previewCamera);}
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
        // Read only the current successful controller observation. Never call
        // device.Read here: that would consume edges a second time. A visual
        // failure hides this ornament only; it cannot affect camera masks,
        // task authority, render receipts, responses, eye quality or recording.
        void RefreshTrackedWandVisual(Camera camera)
        {
            controllerVisualUnavailable=false;
            try
            {
                if(wandVisualFailed||camera==null||camera!=previewCamera||development==null||
                    !development.ControllerVisualPhaseAllowed||!WandTrackingSpaceCurrent())
                {HideTrackedWandVisual();return;}
                ViveStroopControllerPose pose;
                bool valid=development.TryGetCurrentControllerPose(out pose);
                controllerVisualUnavailable=!valid;
                if(trackedWandVisual==null)
                {
                    trackedWandVisual=ViveTrackedWandVisual.Create(cameraOffset,0);
                    trackedWandVisual.ConfigureCamera(previewCamera,0);
                }
                if(valid)trackedWandVisual.Apply(true,true,pose.LocalPosition,pose.LocalRotation,pose.Frame);
                else trackedWandVisual.ShowTrackingUnavailable(true,Time.frameCount);
            }
            catch(Exception error) when(!(error is OutOfMemoryException||error is StackOverflowException||error is AccessViolationException))
            {
                HideTrackedWandVisual();wandVisualFailed=true;
                Debug.LogWarning("VIVE_CONTROLLER_VISUAL_UNAVAILABLE: "+error.GetType().Name+"; task input and recording are unchanged.");
            }
        }
        void HideTrackedWandVisual()
        {if(trackedWandVisual!=null)trackedWandVisual.Hide();}
        void DisposeTrackedWandVisual()
        {
            var old=trackedWandVisual;trackedWandVisual=null;controllerVisualUnavailable=false;
            if(old==null)return;
            old.Hide();
            if(Application.isPlaying)Destroy(old.gameObject);else DestroyImmediate(old.gameObject);
        }
    }
}
#endif
