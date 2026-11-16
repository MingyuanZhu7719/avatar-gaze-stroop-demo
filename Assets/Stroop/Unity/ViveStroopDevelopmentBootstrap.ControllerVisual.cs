#if STROOP_XR_PREVIEW
using AvatarStroop.Core;

namespace AvatarStroop.Unity
{
    public sealed partial class ViveStroopDevelopmentBootstrap
    {
        // Apparatus visibility is independent of task authority, recording and
        // phase. The rig separately requires fresh real head/controller tracking.
        internal static bool ControllerVisualPhasePermitted(bool configured,Phase phase,int block)
        {return true;}
        internal bool ControllerVisualPhaseAllowed {get{return isActiveAndEnabled;}}
        internal VivePreviewControllerHand ControllerVisualHand
        {get{return device==null?VivePreviewControllerHand.Automatic:device.Hand;}}
        internal bool TryGetCurrentControllerPose(out ViveStroopControllerPose pose)
        {
            pose=default(ViveStroopControllerPose);
            return ControllerVisualPhaseAllowed&&device!=null&&device.TryGetCurrentControllerPose(out pose);
        }
    }
}
#endif
