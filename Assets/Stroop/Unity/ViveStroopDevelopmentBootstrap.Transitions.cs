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
        bool practiceCountdownRenderLogged;
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
        {get{return participant!=null&&participant.TaskKind==TaskKind.VisualSearch?"Find the matching backpack. Point and pull the trigger.":
            ResponseRuleInstructions.Participant(participant==null?ResponseRule.InkColor:participant.ParticipantView.ResponseRule);}}
        void ResetPracticeCountdown(){practiceCountdown.Reset();practiceCountdownFrame=-1;practiceCountdownRenderLogged=false;}
        bool ServicePracticeCountdown()
        {
            if(practiceCountdown.CompleteForFrame(Time.realtimeSinceStartupAsDouble,Time.frameCount))return true;
            practiceCountdownFrame=Time.frameCount;
            if(view!=null)view.SetPresentationAvailable(false);
            if(rig!=null)rig.BlockTaskPresentation();
            message=(participant!=null&&participant.UsesTutorial?"Practice exam starts in ":"Practice starts in ")+Math.Max(1,Math.Ceiling(practiceCountdown.RemainingSeconds))+" seconds. Keep the headset on and release the controls.";
            return false;
        }
        internal void ObservePracticeCountdownRender()
        {
            if(PracticeCountdownVisible&&rig!=null&&rig.XrFocused)
            {
                practiceCountdown.ObserveRendered(Time.realtimeSinceStartupAsDouble,Time.frameCount);
                if(!practiceCountdownRenderLogged)
                {
                    practiceCountdownRenderLogged=true;
                    Debug.Log("VIVE_PRACTICE_COUNTDOWN_RENDERED: first camera witness; frame="+Time.frameCount);
                }
            }
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
        const string PreviewPracticePendingNotice="Practice requested. Waiting for current tracking, released controls and the five-second headset countdown.";
        bool PreviewPracticeRequestCurrent
        {get{return previewPracticeCountdownTask!=null&&isActiveAndEnabled&&configured&&
            object.ReferenceEquals(previewPracticeCountdownTask,participant)&&participant!=null&&!participant.RawRecordingRequested&&
            RawPlanStartPhaseMatches(true,participant.Phase)&&!faultLatched&&!RecordingBlocked&&!ObservationBlocked&&rig!=null&&rig.ViewRunning;}}
        void CancelPreviewPracticeRequest(string reason)
        {
            if(previewPracticeCountdownTask==null)return;
            previewPracticeCountdownTask=null;ResetPracticeCountdown();message=reason;
            Debug.Log("VIVE_PREVIEW_PRACTICE_CANCELED: "+reason);
        }
        void ObservePreviewPracticeRequest(bool headReady,bool xrFocused)
        {
            if(previewPracticeCountdownTask==null)return;
            if(!PreviewPracticeRequestCurrent)
            {CancelPreviewPracticeRequest("Practice request canceled because the task, view or safety state changed.");return;}
            if(!headReady||!xrFocused)ResetPracticeCountdown();
        }
        void RequestPreviewPracticeCountdown()
        {
            ResetPracticeCountdown();previewPracticeCountdownTask=participant;
            message=PreviewPracticePendingNotice;
            Debug.Log("VIVE_PREVIEW_PRACTICE_REQUESTED: explicit desktop intent; no countdown or trial has started.");
        }
        bool ServicePreviewPracticeCountdown()
        {
            if(previewPracticeCountdownTask==null)return false;
            if(!PreviewPracticeRequestCurrent)
            {CancelPreviewPracticeRequest("Practice request canceled because the task, view or safety state changed.");return false;}
            if(participant.UsesTutorial&&participant.Phase==Phase.Preparation)
            {
                previewPracticeCountdownTask=null;ResetPracticeCountdown();
                Run(()=>participant.OwnerBeginTutorial(Actor.Owner));return true;
            }
            if(!ServicePracticeCountdown())return true;
            previewPracticeCountdownTask=null;ResetPracticeCountdown();
            Run(()=>{participant.OwnerBeginPractice(Actor.Owner);if(practicePreview!=null)practicePreview.OnPracticeStarted();});
            return true;
        }
        // A completed task can thank the wearer while CSV conversion and eye
        // cleanup continue. This input-free hold-layer presentation makes no
        // recording-success claim; the desktop keeps the actual I/O result.
        // In particular, an export fault must not send a finished participant
        // back to an actionless "Task on hold" screen.
        internal bool CompletedTaskThankYouReady
        {
            get
            {
                var task=participant;var pump=namedRawPump;
                return task!=null&&task.Completed&&rawCompletionRequested&&
                    !rawInstallFailed&&!rawWithdrawalRequested&&(pump==null||!pump.WithdrawalRequested)&&
                    task.NamedRawRevocation!=null&&
                    !task.NamedRawRevocation.ParticipantWithdrawalRequested&&!RawPresentationBoundaryPending;
            }
        }
    }
}
#endif
