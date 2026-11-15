#if UNITY_EDITOR && STROOP_XR_PREVIEW
using System;
using System.Reflection;
using AvatarStroop.Core;
using UnityEngine;

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
        public static void Validate()
        {
            assertions=0;
            foreach(Phase phase in Enum.GetValues(typeof(Phase)))
            {
                bool preformal=phase==Phase.Preparation||phase==Phase.Familiarization||phase==Phase.Practice||
                    phase==Phase.PracticeCorrection||phase==Phase.PracticeRetry||phase==Phase.Paused;
                Require(Permitted(true,phase,-1)==preformal,"preformal "+phase);
                Require(!Permitted(true,phase,0),"formal first "+phase);
                Require(!Permitted(true,phase,3),"formal last "+phase);
            }
            Require(Permitted(false,Phase.Preparation,-1),"setup allowed");
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
            }
            finally{UnityEngine.Object.DestroyImmediate(root);UnityEngine.Object.DestroyImmediate(cameraRoot);}
            Debug.Log("VIVE_CONTROLLER_VISUAL_POLICY_FIXTURE_PASSED: "+assertions+" assertions; no physical display acceptance.");
        }
    }
}
#endif
