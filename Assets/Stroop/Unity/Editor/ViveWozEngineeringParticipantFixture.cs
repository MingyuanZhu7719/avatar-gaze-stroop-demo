#if STROOP_XR_PREVIEW && UNITY_EDITOR
using System;
using AvatarStroop.Core;
using AvatarStroop.Unity;
using UnityEngine;

namespace AvatarStroop.Unity.Editor
{
    // Synthetic Edit Mode scene check only; no XR, socket or worker starts.
    public static class ViveWozEngineeringParticipantFixture
    {
        public static void Run()
        {
            if(!Application.isBatchMode||Application.isPlaying)throw new InvalidOperationException("Batch Edit Mode is required.");
            var cameraObject=new GameObject("EngineeringFixtureCamera");var root=new GameObject("EngineeringFixtureView");
            try
            {
                var view=root.AddComponent<WorldSpaceExperimentView>();view.Initialize(cameraObject.AddComponent<Camera>(),Actor.Participant);
                view.SetViveUnconfiguredPreview("Engineering test only");view.ClearAvatarOutput();
                if(view.AvatarRoot.gameObject.activeSelf)throw new InvalidOperationException("Initial avatar must be hidden.");
                var eye=new Vec3(0,0,1);
                if(!ViveWozEngineeringParticipantBootstrap.PresentEngineeringPose(view,Rotation.Identity,eye,"fixture",true)||!view.AvatarRoot.gameObject.activeSelf)
                    throw new InvalidOperationException("Complete synthetic pose did not show the engineering avatar.");
                if(view.HeadHeld||view.EyesHeld)throw new InvalidOperationException("Synthetic pose axes unexpectedly held.");
                if(ViveWozEngineeringParticipantBootstrap.PresentEngineeringPose(view,Rotation.Identity,eye,"fixture",false)||view.AvatarRoot.gameObject.activeSelf)
                    throw new InvalidOperationException("Unavailable viewer must hide the avatar.");
                if(ViveWozEngineeringParticipantBootstrap.PresentEngineeringPose(view,Rotation.Identity,new Vec3(0,0,0),"fixture",true)||view.AvatarRoot.gameObject.activeSelf)
                    throw new InvalidOperationException("Invalid eye must hide the avatar.");
                if(ViveWozEngineeringParticipantBootstrap.PresentEngineeringPose(view,new Rotation(0,0,0,0),eye,"fixture",true)||view.AvatarRoot.gameObject.activeSelf)
                    throw new InvalidOperationException("Invalid head must hide the avatar.");
                Debug.Log("VIVE_WOZ_ENGINEERING_PARTICIPANT_FIXTURE: synthetic complete/incomplete pose visibility passed; no XR or network started.");
            }
            finally{UnityEngine.Object.DestroyImmediate(root);UnityEngine.Object.DestroyImmediate(cameraObject);}
        }
    }
}
#endif
