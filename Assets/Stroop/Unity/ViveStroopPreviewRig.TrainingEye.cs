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
                development==null||!development.CanWarmupFormalEye||!headReady||!XrFocused)return;
            // The exact same run owns task tickets before and after warm-up.
            // This request does not enable either archival eye queue.
            if(!worker.RequestFormalEyeStart())return;
            formalEyeWarmupRequested=true;
            SetStatus("Checking eye tracking before the formal experiment. Eye data is not being saved yet.");
        }
    }

    public sealed partial class ViveStroopDevelopmentBootstrap
    {
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
                var ready=rig==null?(AvatarStroop.Runtime.ViveProEyeWorker.RecordingStartReadiness?)null:rig.RawStartReadiness;
                return rig!=null&&rig.FormalEyeWarmupRequested&&rig.RecordingWorkerReady&&ready.HasValue&&ready.Value.Ready;
            }
        }
        string FormalEyeFirstBlockNotice
        {
            get
            {
                if(FormalEyeReadyForFirstBlock)return null;
                if(rig==null||!rig.FormalEyeWarmupRequested)return "Waiting to check eye tracking. No formal block or eye recording has started.";
                return "Formal experiment is waiting for eye tracking. "+ViveStroopPreviewRig.DescribeRawStartReadiness(rig.RawStartReadiness);
            }
        }
    }
}
#endif
