#if STROOP_XR_PREVIEW
using System;
using AvatarStroop.Core;
using AvatarStroop.Runtime;
using UnityEngine;

namespace AvatarStroop.Unity
{
    public sealed partial class ViveStroopPreviewRig
    {
        bool taskOnlyEyeSource,formalEyeWarmupRequested;
        internal bool TaskOnlyRecordingWorkerReady
        {get{return running&&headReady&&worker!=null&&eyePump!=null&&worker.IsTaskOnlyRecordingRun&&!stopping&&!restartBlocked&&
            development!=null&&development.Configured&&object.ReferenceEquals(clock,development.SharedClock)&&worker.UsesClock(clock);}}
        internal bool FormalEyeWarmupRequested {get{return taskOnlyEyeSource&&formalEyeWarmupRequested;}}
        bool TrainingEyeSourceDormant {get{return taskOnlyEyeSource&&!formalEyeWarmupRequested;}}
        void ServiceFormalEyeWarmup()
        {
            if(!taskOnlyEyeSource||formalEyeWarmupRequested||worker==null||!running||stopping||restartBlocked||
                development==null||(!development.CanWarmupFormalEye&&!development.UnrecordedWozEyeWarmup)||!headReady||!XrFocused)return;
            // The exact same run owns task tickets before and after warm-up.
            // This request does not enable either archival eye queue.
            if(!worker.RequestFormalEyeStart())return;
            formalEyeWarmupRequested=true;
            SetStatus("Checking eye tracking. The experiment can continue without gaze; missing eye data will be marked.");
        }
    }

    public sealed partial class ViveStroopDevelopmentBootstrap
    {
        // A click records a bounded intent. The regular guarded input frame
        // and the recording plan receipt still own the actual block start.
        sealed class FirstBlockRequest
        {
            internal ViveDevelopmentParticipant Task;
            internal ViveNamedRawRecordingPump Pump;
            internal MonotonicClock Clock;
            internal double RequestedAt;
        }
        FirstBlockRequest firstBlockRequest;
        string firstBlockRequestNotice;
        bool ParticipantFirstBlockReady
        {get{return participant!=null&&participant.IsResearchSession&&!participant.LegacyWozRecordingDeclared&&
            participant.Phase==Phase.BlockReady&&participant.BlockIndex==0;}}
        bool FirstBlockRequestAvailable
        {get{return ParticipantFirstBlockReady&&isActiveAndEnabled&&configured&&participant.LockedControl==ControlSelection.Automatic&&
            namedRawPump!=null&&sharedClock!=null&&!rawSafetyLatched&&!rawInstallFailed&&!faultLatched&&!rawWithdrawalRequested&&
            !participant.NamedRawRevocation.IsRevoked&&!namedRawPump.WithdrawalRequested&&namedRawPump.State==ViveNamedRawRecordingPump.Outcome.Running;}}
        void RequestParticipantFirstBlock()
        {
            if(firstBlockRequest!=null||!FirstBlockRequestAvailable)return;
            double now;
            if(!sharedClock.TryReadRecordingAdmissionTime(out now)){firstBlockRequestNotice="Start unavailable. Review the session clock.";return;}
            firstBlockRequest=new FirstBlockRequest{Task=participant,Pump=namedRawPump,Clock=sharedClock,RequestedAt=now};
            firstBlockRequestNotice=null;
            Debug.Log("VIVE_PARTICIPANT_FIRST_BLOCK_REQUESTED: operator click accepted; awaiting current head tracking and released controls. Eye availability is recorded separately.");
        }
        void CancelFirstBlockRequest(string notice)
        {
            if(firstBlockRequest==null)return;
            firstBlockRequest=null;firstBlockRequestNotice=notice;
            if(rawPlanStartRequested==2&&rawPendingBlockPlan==null)rawPlanStartRequested=0;
            if(notice!=null)Debug.Log("VIVE_PARTICIPANT_FIRST_BLOCK_CANCELLED: "+notice);
        }
        bool ObserveFirstBlockRequest()
        {
            var request=firstBlockRequest;if(request==null)return false;
            double now;
            if(!FirstBlockRequestAvailable||!object.ReferenceEquals(request.Task,participant)||!object.ReferenceEquals(request.Pump,namedRawPump)||
                !object.ReferenceEquals(request.Clock,sharedClock)||!request.Clock.TryReadRecordingAdmissionTime(out now)||
                !RawIntentTimeCurrent(request.RequestedAt,now))
            {CancelFirstBlockRequest("Start request expired or the session changed. Select Begin first block again when ready.");return false;}
            return true;
        }
        string FirstBlockWaitReason
        {
            get
            {
                if(FirstFormalControllerWaiting)return FormalControllerWaitNotice;
                if(!FormalEyeReadyForFirstBlock)return FormalEyeFirstBlockNotice;
                if(focusPlanConfirmationRequired||focusPlanTriggerAccepted)return FocusPlanAwaitNotice;
                if(!participant.InputReady||!ControllerUiDisplayReady)return "Waiting for headset tracking and released controller controls.";
                return rawPendingBlockPlan==null?"Waiting for a fresh neutral input frame.":"Waiting for the recorded block plan and released controls.";
            }
        }
        internal static bool RawTaskOnlyStartInputsReady(ViveNamedRecordingSetup choices,bool focused,bool sceneTrackingCurrent,
            bool taskWorkerReady,int neutralFrame,int currentFrame)
        {return choices!=null&&choices.CanStart(true,focused,sceneTrackingCurrent,taskWorkerReady,neutralFrame,currentFrame);}
        internal bool DeferEyeForCurrentView
        {get{return !configured||participant!=null&&participant.FormalEyeRecordingOnly;}}
        internal bool FormalEyeRecordingOnly
        {get{return participant!=null&&participant.FormalEyeRecordingOnly;}}
        internal bool CanWarmupFormalEye
        {get{return participant!=null&&participant.FormalEyeRecordingOnly&&participant.RawRecordingRequested&&namedRawPump!=null&&
            !RecordingBlocked&&participant.BlockIndex==0&&(participant.Phase==Phase.BlockPreparation||participant.Phase==Phase.BlockReady);}}
        internal bool WaitingForFormalEyeActivation
        {get{return namedRawPump!=null&&participant!=null&&participant.FormalEyeRecordingOnly&&participant.BlockIndex==0&&
            participant.Phase==Phase.Countdown&&participant.RawCountdownAwaitingSoftwareWitness&&namedRawPump.FormalEyeActivationPending;}}
        internal bool HoldFormalEyeActivation()
        {
            if(!WaitingForFormalEyeActivation)return false;
            // Begin block can change phase after this frame's task-only
            // epoch was staged. Retire that epoch before any countdown panel
            // is revealed or witnessed; the next frame activates persistence.
            pendingRawPanelFrame=-1;pendingRawPanelRevision=-1;
            if(rig!=null)rig.BlockTaskPresentation();
            if(view!=null)view.SetPresentationAvailable(false);
            if(!namedRawPump.DrainTaskEvents())FailNamedRawAfterTask();
            return true;
        }
        internal static bool FormalEyeStartRequired(bool rawRecording,bool formalOnly,int block)
        {return rawRecording&&formalOnly&&block==0;}
        bool FormalEyeReadyForFirstBlock
        {
            get
            {
                if(participant==null)return false;
                if(!FormalEyeStartRequired(participant.RawRecordingRequested,participant.FormalEyeRecordingOnly,participant.BlockIndex))return true;
                // The task carrier is shared with training and remains tied to
                // this head/controller run. Native eye health is optional data,
                // not permission to start or continue a behavioral block.
                return rig!=null&&rig.FormalEyeWarmupRequested&&rig.TaskOnlyRecordingWorkerReady;
            }
        }
        string FormalEyeFirstBlockNotice
        {
            get
            {
                if(FormalEyeReadyForFirstBlock)return null;
                if(rig==null||!rig.FormalEyeWarmupRequested)return "Preparing the formal task input service. Eye data is optional and missing samples will be marked.";
                return "Waiting for current headset tracking and the task input service. Eye availability does not block the experiment.";
            }
        }
    }
}
#endif
