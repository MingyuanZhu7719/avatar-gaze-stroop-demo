#if STROOP_XR_PREVIEW
using System;
using AvatarStroop.Runtime;
using UnityEngine;
using UnityEngine.XR;

namespace AvatarStroop.Unity
{
    // One-time VIVE development-view placement. This moves the application's
    // tracking root, not the runtime reference space or the wearer's head.
    // Aligning the first view to the task board may move the virtual floor
    // relative to the wearer's physical floor; it is not a height estimate.
    public sealed class VivePreviewStartAlignment
    {
        readonly QuestPreviewStartAlignment horizontal=new QuestPreviewStartAlignment();
        readonly ViveStartPoseStability stable=new ViveStartPoseStability(.75,.15);
        public bool Applied {get{return horizontal.Applied;}}
        public bool HeightRejected {get;private set;}
        static bool Finite(float v){return !Single.IsNaN(v)&&!Single.IsInfinity(v);}
        static bool Finite(Vector3 v){return Finite(v.x)&&Finite(v.y)&&Finite(v.z);}

        public void ResetPending(){HeightRejected=false;stable.Observe(false,0,-1,0,0,0,0,0,0,0,1);}

        public bool TryApply(bool poseReady,TrackingOriginModeFlags mode,int frame,double now,
            Transform origin,Transform cameraParent,Vector3 localPosition,Quaternion localRotation,Vector3 boardTarget)
        {
            if(Applied)return false;
            int referenceMode=mode==TrackingOriginModeFlags.Device?1:mode==TrackingOriginModeFlags.Floor?2:0;
            if(!Finite(boardTarget)) {ResetPending();return false;}
            if(!stable.Observe(poseReady,referenceMode,frame,now,localPosition.x,localPosition.y,localPosition.z,
                localRotation.x,localRotation.y,localRotation.z,localRotation.w))return false;
            if(origin==null||cameraParent==null||!cameraParent.IsChildOf(origin))
                throw new ArgumentException("Initial VIVE alignment requires the tracking-origin hierarchy.");
            // Device and Floor are both interpreted relative to the tracked pose.
            // The final virtual Y is the actual board target, not a guessed HMD height.
            if(!horizontal.PrepareReferenceHeight(true,referenceMode==2,cameraParent,0))return false;
            Vector3 before=cameraParent.TransformPoint(localPosition);
            // Bound a single virtual-world shift, not a claimed physical height.
            // A suspect reference remains masked rather than jumping the view.
            HeightRejected=!Finite(before)||!ViveStartPoseStability.AcceptsVerticalShift(before.y,boardTarget.y);
            if(HeightRejected)return false;
            if(!horizontal.TryApply(true,origin,cameraParent,localPosition,localRotation,boardTarget))return false;
            Vector3 head=cameraParent.TransformPoint(localPosition);
            origin.position+=new Vector3(0,boardTarget.y-head.y,0);
            return true;
        }
    }
}
#endif
