#if STROOP_XR_PREVIEW
using AvatarStroop.Core;

namespace AvatarStroop.Unity
{
    public sealed partial class ViveStroopDevelopmentBootstrap
    {
        // Optional apparatus guidance, never task input or recording evidence.
        // Keep every formal block, its preparation, ratings and rests unchanged.
        internal static bool ControllerVisualPhasePermitted(bool configured,Phase phase,int block)
        {
            if(!configured)return true;
            if(block>=0)return false;
            return phase==Phase.Preparation||phase==Phase.Familiarization||phase==Phase.Practice||
                phase==Phase.PracticeCorrection||phase==Phase.PracticeRetry||phase==Phase.Paused;
        }
        internal bool ControllerVisualPhaseAllowed
        {get{return isActiveAndEnabled&&!faultLatched&&!recordingTerminal&&!RawSafetyNoticeActive&&
            ControllerVisualPhasePermitted(configured,participant==null?Phase.Preparation:participant.Phase,
                participant==null?-1:participant.BlockIndex);}}
        internal bool TryGetCurrentControllerPose(out ViveStroopControllerPose pose)
        {
            pose=default(ViveStroopControllerPose);
            return ControllerVisualPhaseAllowed&&device!=null&&device.TryGetCurrentControllerPose(out pose);
        }
    }
}
#endif
