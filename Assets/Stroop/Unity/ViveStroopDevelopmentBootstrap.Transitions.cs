#if STROOP_XR_PREVIEW
using System;
using AvatarStroop.Core;
using AvatarStroop.Runtime;
using UnityEngine;

namespace AvatarStroop.Unity
{
    public sealed partial class ViveStroopDevelopmentBootstrap
    {
        readonly VivePracticeCountdown practiceCountdown=new VivePracticeCountdown();
        ViveDevelopmentParticipant previewPracticeCountdownTask;
        int practiceCountdownFrame=-1;
        readonly VivePracticeCountdown blockCelebration=new VivePracticeCountdown(6);
        ViveDevelopmentParticipant blockCelebrationTask;
        int blockCelebrationIndex=-1,blockCelebrationFinished=-1,blockCelebrationFrame=-1;
        internal bool BlockCelebrationVisible
        {get{return blockCelebrationFrame==Time.frameCount&&participant!=null&&participant.Phase==Phase.Questionnaire&&
            object.ReferenceEquals(blockCelebrationTask,participant)&&blockCelebrationIndex==participant.BlockIndex&&!PresentationBlocked;}}
        internal double BlockCelebrationElapsed {get{return 6-blockCelebration.RemainingSeconds;}}
        internal bool TransitionVisible {get{return PracticeCountdownVisible||BlockCelebrationVisible;}}
        internal bool PracticeCountdownVisible
        {get{return practiceCountdownFrame==Time.frameCount&&participant!=null&&
            RawPlanStartPhaseMatches(true,participant.Phase)&&!RecordingBlocked&&!ObservationBlocked;}}
        internal double PracticeCountdownRemaining {get{return practiceCountdown.RemainingSeconds;}}
        internal string PracticeResponseInstruction
        {get{return ResponseRuleInstructions.Participant(participant==null?ResponseRule.InkColor:participant.ParticipantView.ResponseRule);}}
        void ResetPracticeCountdown(){practiceCountdown.Reset();practiceCountdownFrame=-1;}
        bool ServicePracticeCountdown()
        {
            if(practiceCountdown.CompleteForFrame(Time.realtimeSinceStartupAsDouble,Time.frameCount))return true;
            practiceCountdownFrame=Time.frameCount;
            if(view!=null)view.SetPresentationAvailable(false);
            if(rig!=null)rig.BlockTaskPresentation();
            message="Practice starts in "+Math.Max(1,Math.Ceiling(practiceCountdown.RemainingSeconds))+" seconds. Keep the headset on and release the controls.";
            return false;
        }
        internal void ObservePracticeCountdownRender()
        {
            if(PracticeCountdownVisible&&rig!=null&&rig.XrFocused)
                practiceCountdown.ObserveRendered(Time.realtimeSinceStartupAsDouble,Time.frameCount);
        }
        internal void ObserveBlockCelebrationRender()
        {
            if(BlockCelebrationVisible&&rig!=null&&rig.XrFocused)
                blockCelebration.ObserveRendered(Time.realtimeSinceStartupAsDouble,Time.frameCount);
        }
        bool ServiceBlockCompletionAnimation()
        {
            if(participant==null||participant.Phase!=Phase.Questionnaire||PresentationBlocked)return false;
            if(!object.ReferenceEquals(blockCelebrationTask,participant))
            {blockCelebrationTask=participant;blockCelebrationIndex=-1;blockCelebrationFinished=-1;}
            if(blockCelebrationFinished==participant.BlockIndex)return false;
            if(blockCelebrationIndex!=participant.BlockIndex)
            {
                if(!Mutate(()=>participant.BeginBlockCelebration()))return true;
                blockCelebrationIndex=participant.BlockIndex;blockCelebration.Reset();
            }
            if(blockCelebration.CompleteForFrame(Time.realtimeSinceStartupAsDouble,Time.frameCount))
            {
                if(!Mutate(()=>participant.EndBlockCelebration()))return true;
                blockCelebrationFinished=participant.BlockIndex;blockCelebrationFrame=-1;questionnaireGate.Reset();return false;
            }
            blockCelebrationFrame=Time.frameCount;questionnaireGate.Reset();
            if(view!=null)view.SetPresentationAvailable(false);
            if(rig!=null)rig.BlockTaskPresentation();
            message="Block complete. The headset celebration is followed by the questionnaire.";
            return true;
        }
        void RequestPreviewPracticeCountdown()
        {
            ResetPracticeCountdown();previewPracticeCountdownTask=participant;
            message="Practice requested. A five-second warning will appear in the headset.";
        }
        bool ServicePreviewPracticeCountdown()
        {
            if(previewPracticeCountdownTask==null)return false;
            if(!object.ReferenceEquals(previewPracticeCountdownTask,participant)||participant==null||
                !RawPlanStartPhaseMatches(true,participant.Phase)||RecordingBlocked||ObservationBlocked)
            {previewPracticeCountdownTask=null;ResetPracticeCountdown();return false;}
            if(!ServicePracticeCountdown())return true;
            previewPracticeCountdownTask=null;ResetPracticeCountdown();
            Run(()=>{participant.OwnerBeginPractice(Actor.Owner);if(practicePreview!=null)practicePreview.OnPracticeStarted();});
            return true;
        }
        // Task completion is distinct from file durability. This only allows
        // a thank-you animation on the isolated, input-free hold layer.
        internal bool NormalRawCompletionReady
        {
            get
            {
                var task=participant;var pump=namedRawPump;
                return task!=null&&task.Completed&&rawCompletionRequested&&pump!=null&&
                    pump.State==ViveNamedRawRecordingPump.Outcome.CloseCallsReturned&&
                    !rawInstallFailed&&!rawSafetyLatched&&!rawWithdrawalRequested&&!pump.WithdrawalRequested&&
                    task.NamedRawRevocation!=null&&!task.NamedRawRevocation.IsRevoked&&
                    !task.NamedRawRevocation.ParticipantWithdrawalRequested&&!RawPresentationBoundaryPending;
            }
        }
    }
}
#endif
