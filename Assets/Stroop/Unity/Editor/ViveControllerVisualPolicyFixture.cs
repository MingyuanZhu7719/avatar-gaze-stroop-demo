#if UNITY_EDITOR && STROOP_XR_PREVIEW
using System;
using System.Reflection;
using AvatarStroop.Core;
using UnityEngine;
using UnityEngine.XR;

namespace AvatarStroop.Unity.Editor
{
    // Read-only presentation policy. No loader, controller, eye or writer.
    public static class ViveControllerVisualPolicyFixture
    {
        static int assertions;
        static void Require(bool value,string detail)
        {if(!value)throw new InvalidOperationException("Controller visual policy: "+detail);assertions++;}
        static bool Permitted(bool configured,Phase phase,int block)
        {return (bool)typeof(ViveStroopDevelopmentBootstrap).GetMethod("ControllerVisualPhasePermitted",
            BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,new object[]{configured,phase,block});}
        static bool Head(bool aligned,bool tracked,bool reference,bool present,TrackingOriginModeFlags origin,TrackingOriginModeFlags placed)
        {return (bool)typeof(ViveStroopPreviewRig).GetMethod("ControllerVisualHeadPermitted",
            BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,new object[]{aligned,tracked,reference,present,origin,placed});}
        static bool TerminalPose(Camera camera,bool valid,Vector3 position,Quaternion rotation)
        {return (bool)typeof(ViveStroopPreviewRig).GetMethod("TryApplyTerminalCameraPose",
            BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,new object[]{camera,valid,position,rotation});}
        public static void Validate()
        {
            assertions=0;
            foreach(Phase phase in Enum.GetValues(typeof(Phase)))
            {
                foreach(bool configured in new[]{false,true})foreach(int block in new[]{-1,0,3})
                    Require(Permitted(configured,phase,block),"apparatus remains visible in "+phase+" block "+block);
            }
            foreach(var mode in new[]{TrackingOriginModeFlags.Device,TrackingOriginModeFlags.Floor})
            {
                Require(Head(false,true,true,true,mode,TrackingOriginModeFlags.Unknown),"tracked startup before alignment "+mode);
                Require(Head(true,true,true,true,mode,mode),"tracked aligned origin "+mode);
                Require(!Head(true,true,true,true,mode,mode==TrackingOriginModeFlags.Device?TrackingOriginModeFlags.Floor:TrackingOriginModeFlags.Device),"changed aligned origin");
                foreach(bool aligned in new[]{false,true})
                {
                    Require(!Head(aligned,false,true,true,mode,mode),"untracked head");
                    Require(!Head(aligned,true,false,true,mode,mode),"changed reference");
                    Require(!Head(aligned,true,true,false,mode,mode),"unworn headset");
                }
            }
            Require(!Head(false,true,true,true,TrackingOriginModeFlags.Unknown,TrackingOriginModeFlags.Unknown),"unknown origin");
            var root=new GameObject("Controller visual stopped-rig fixture");
            var cameraRoot=new GameObject("Controller visual fixture camera");
            try
            {
                root.SetActive(false);
                var rig=root.AddComponent<ViveStroopPreviewRig>();
                var camera=cameraRoot.AddComponent<Camera>();camera.cullingMask=0x105;
                var flags=BindingFlags.NonPublic|BindingFlags.Instance;
                typeof(ViveStroopPreviewRig).GetField("previewCamera",flags).SetValue(rig,camera);
                var refresh=typeof(ViveStroopPreviewRig).GetMethod("RefreshTrackedWandVisual",flags);
                Require(refresh!=null,"optional production refresh exists");
                refresh.Invoke(rig,new object[]{camera});
                Require(typeof(ViveStroopPreviewRig).GetProperty("TrackedWandVisual",flags).GetValue(rig)==null,
                    "stopped/unconfigured rig cannot create origin ghost");
                Require(camera.cullingMask==0x105,"visual does not alter camera mask");
                refresh.Invoke(rig,new object[]{null});
                Require(camera.cullingMask==0x105,"unavailable visual cannot unmask task");
                Vector3 position=new Vector3(.1f,1.2f,.3f);Quaternion rotation=Quaternion.Euler(0,15,0);
                object readyFrame=typeof(ViveStroopPreviewRig).GetField("headReadyFrame",flags).GetValue(rig);
                Require(!TerminalPose(camera,true,position,rotation)&&camera.transform.localPosition==Vector3.zero,
                    "terminal camera update touched a task-enabled camera");
                camera.cullingMask=0;
                Require(TerminalPose(camera,true,position,rotation)&&camera.transform.localPosition==position&&
                    Quaternion.Angle(camera.transform.localRotation,rotation)<.01f,"valid same-reference terminal pose was not applied");
                Require(!TerminalPose(camera,false,Vector3.zero,Quaternion.identity)&&camera.transform.localPosition==position,"unverified terminal head was applied");
                Require(!TerminalPose(camera,true,new Vector3(float.NaN,0,0),Quaternion.identity)&&camera.transform.localPosition==position,"nonfinite terminal position was applied");
                Require(!TerminalPose(camera,true,Vector3.zero,new Quaternion(0,0,0,0))&&camera.transform.localPosition==position,"invalid terminal rotation was applied");
                camera.enabled=false;
                Require(!TerminalPose(camera,true,Vector3.zero,Quaternion.identity)&&camera.transform.localPosition==position,"disabled terminal camera was moved");
                Require(camera.cullingMask==0&&!root.activeSelf&&readyFrame.Equals(typeof(ViveStroopPreviewRig).GetField("headReadyFrame",flags).GetValue(rig))&&
                    typeof(ViveStroopPreviewRig).GetProperty("TrackedWandVisual",flags).GetValue(rig)==null,
                    "visual-only terminal pose changed rig readiness or authorized a task camera");
            }
            finally{UnityEngine.Object.DestroyImmediate(root);UnityEngine.Object.DestroyImmediate(cameraRoot);}
            Debug.Log("VIVE_CONTROLLER_VISUAL_POLICY_FIXTURE_PASSED: "+assertions+" assertions; no physical display acceptance.");
        }
    }
}
#endif
