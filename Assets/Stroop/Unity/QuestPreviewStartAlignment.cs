using System;
using UnityEngine;

namespace AvatarStroop.Unity
{
    // Application-space placement, not a tracking-runtime recenter. The input
    // pose is the same local pose consumed by TrackedPoseDriver. This object has
    // no reset path: tracking loss and phase changes must not move the room again.
    public sealed class QuestPreviewStartAlignment
    {
        public bool Applied { get; private set; }
        static bool Finite(float value) {return !Single.IsNaN(value)&&!Single.IsInfinity(value);}
        static bool Finite(Vector3 value) {return Finite(value.x)&&Finite(value.y)&&Finite(value.z);}

        public bool TryApply(bool ready,Transform origin,Transform cameraParent,Vector3 localPosition,
            Quaternion localRotation,Vector3 nominalParticipant)
        {
            if(Applied||!ready)return false;
            if(origin==null||cameraParent==null||!cameraParent.IsChildOf(origin))
                throw new ArgumentException("Initial alignment requires the existing tracking-origin hierarchy.");
            float norm=localRotation.x*localRotation.x+localRotation.y*localRotation.y+
                localRotation.z*localRotation.z+localRotation.w*localRotation.w;
            if(!Finite(localPosition)||!Finite(nominalParticipant)||!Finite(norm)||Mathf.Abs(norm-1)>.02f)return false;
            Vector3 head=cameraParent.TransformPoint(localPosition);
            Vector3 forward=cameraParent.rotation*localRotation*Vector3.forward;
            Vector3 horizontal=Vector3.ProjectOnPlane(forward,Vector3.up);
            if(!Finite(head)||!Finite(horizontal)||horizontal.sqrMagnitude<.01f)return false;
            float yaw=Vector3.SignedAngle(horizontal,Vector3.forward,Vector3.up);
            origin.RotateAround(head,Vector3.up,yaw);
            Vector3 alignedHead=cameraParent.TransformPoint(localPosition);
            origin.position+=new Vector3(nominalParticipant.x-alignedHead.x,0,nominalParticipant.z-alignedHead.z);
            Applied=true;
            return true;
        }
    }
}
