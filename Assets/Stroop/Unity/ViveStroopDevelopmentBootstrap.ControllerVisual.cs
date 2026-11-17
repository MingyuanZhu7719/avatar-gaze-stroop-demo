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
        // Colour sectors describe Stroop responses only. The questionnaire uses
        // the same pad for ratings, and Visual Search uses a ray and trigger.
        internal static bool ControllerColourMappingPermitted(TaskKind task,Phase phase)
        {
            return task==TaskKind.Stroop&&(phase==Phase.Preparation||phase==Phase.Familiarization||
                phase==Phase.Tutorial||phase==Phase.PracticeExamReady||phase==Phase.Practice||phase==Phase.PracticeCorrection||phase==Phase.PracticeRetry||
                phase==Phase.BlockPreparation||phase==Phase.BlockReady||phase==Phase.Countdown||
                phase==Phase.MainTask||phase==Phase.Rest);
        }
        internal bool ControllerColourMappingAllowed
        {get{return ControllerVisualPhaseAllowed&&ControllerColourMappingPermitted(
            participant==null?selectedTaskKind:participant.TaskKind,participant==null?Phase.Preparation:participant.Phase);}}
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
