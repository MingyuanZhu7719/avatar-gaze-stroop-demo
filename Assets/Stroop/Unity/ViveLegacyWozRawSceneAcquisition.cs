#if STROOP_XR_PREVIEW || STROOP_TEST_BUILD
using System;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Unity
{
    // Pure owner-thread scene decision shared with the executable memory test.
    // Waiting grants no display/input authority and never extends proof age.
    internal static class ViveLegacyWozRawSceneAcquisition
    {
        internal enum State { Ready, Waiting, Stopped }
        internal static State Evaluate(ViveDevelopmentParticipant task,ViveLegacyWozControlledScene scene,bool explicitlyStarted)
        {
            if(task==null||!task.LegacyWozRecordingDeclared)return State.Ready;
            if(task.Completed)return State.Ready;
            if(scene==null)return task.Phase==Phase.MainTask||task.Phase==Phase.Countdown?State.Stopped:State.Ready;
            if(!Object.ReferenceEquals(scene.Owner.Participant,task)||scene.Closed||
                !scene.Owner.ControlledSceneCurrent(scene))return State.Stopped;
            if(!explicitlyStarted)return task.Phase==Phase.BlockReady?State.Waiting:State.Stopped;
            // An already-prepared/presented trial whose proof expired must
            // terminate. Only new, still-unpresented context may wait masked.
            scene.RecheckPresentation();
            if(scene.Closed)return State.Stopped;
            return scene.MayShowTask?State.Ready:State.Waiting;
        }
    }
}
#endif
