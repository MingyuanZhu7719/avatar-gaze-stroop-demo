#if STROOP_XR_PREVIEW
using UnityEngine;

namespace AvatarStroop.Unity
{
    public sealed partial class ViveStroopPreviewRig
    {
        // Same tracking-space parent as ViveTrackedWandVisual. The pose is
        // read once by the input owner; converting it never consumes an edge.
        internal bool TryGetVisualSearchWorldRay(ViveStroopControllerPose pose,out Vector3 origin,out Vector3 direction)
        {
            origin=Vector3.zero;direction=Vector3.forward;
            if(!HeadReady||!XrFocused||pose.Frame!=Time.frameCount||!WandTrackingSpaceCurrent()||cameraOffset==null)return false;
            var p=pose.LocalPosition;var q=pose.LocalRotation;
            if(!SearchFinite(p.x)||!SearchFinite(p.y)||!SearchFinite(p.z)||!SearchFinite(q.x)||!SearchFinite(q.y)||!SearchFinite(q.z)||!SearchFinite(q.w))return false;
            float norm=q.x*q.x+q.y*q.y+q.z*q.z+q.w*q.w;if(Mathf.Abs(norm-1f)>.02f)return false;
            origin=cameraOffset.TransformPoint(p);
            direction=cameraOffset.TransformDirection(q.normalized*Vector3.forward).normalized;
            return SearchFinite(origin.x)&&SearchFinite(origin.y)&&SearchFinite(origin.z)&&direction.sqrMagnitude>.99f;
        }
        static bool SearchFinite(float value){return !float.IsNaN(value)&&!float.IsInfinity(value);}
    }
}
#endif
