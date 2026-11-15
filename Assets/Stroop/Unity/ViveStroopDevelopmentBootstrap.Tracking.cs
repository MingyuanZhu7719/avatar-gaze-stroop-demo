#if STROOP_XR_PREVIEW
using UnityEngine;
using AvatarStroop.Runtime;
using AvatarStroop.Core;

namespace AvatarStroop.Unity
{
    public sealed partial class ViveStroopDevelopmentBootstrap
    {
        bool renderHeadHoldPending;
        internal void HoldRawRenderHeadTracking()
        {renderHeadHoldPending=true;ServicePendingRawRenderHeadTracking();}
        internal bool ServicePendingRawRenderHeadTracking()
        {
            if(!renderHeadHoldPending||!ActiveRawTrackingContext())return false;
            BeginRawTrackingHostBoundary();
            MaskRawTrackingFrame("Headset pose changed during rendering. Task held; writer stays open. Visual exposure and missing measurements are UNKNOWN.");
            if(!namedRawPump.TryAbandonPresentationForFocus(pendingRawPresentation))
            {FailNamedRawAfterTask();return true;}
            pendingRawPresentation=null;pendingRawPanelFrame=-1;pendingRawPanelRevision=-1;
            committedRawTrial=null;committedRawRevision=-1;rawGenericCallbackTrial=null;rawGenericCallbackFrame=-1;
            if(participant.LegacyWozRecording!=null)participant.LegacyWozRecording.InvalidateAppliedPresentation();
            var status=namedRawPump.PrepareStatus();
            if(status==ViveNamedRawRecordingPump.Preparation.Busy)return true;
            if(status!=ViveNamedRawRecordingPump.Preparation.Ready||
                !namedRawPump.ObserveTrackingRenderHeadLoss(TrackingPlanPending)||
                !MaintainRawTrackingWoZ(false)||!MaintainPendingRawPlanReceiptDuringFocus())
            {FailNamedRawBeforeFrame();return true;}
            renderHeadHoldPending=false;
            if(rig!=null&&!rig.XrFocused)ObserveFocusLossDuringTracking();
            return true;
        }
        const int RawTrackingNeutralFrames=3;
        int trackingNeutralCount,trackingNeutralFrame=-1,trackingMaskedFrame=-1;
        bool trackingRequiresExplicitFocusResume;
        internal void ObserveFocusLossDuringTracking()
        {
            ResetRawTrackingNeutralStreak();
            if(namedRawPump!=null&&namedRawPump.TrackingGapOpen)trackingRequiresExplicitFocusResume=true;
        }
        internal bool RawTrackingMasksThisFrame {get{return trackingMaskedFrame==Time.frameCount;}}
        internal string RawTrackingHoldNotice {get;private set;}
        internal void ResetRawTrackingNeutralStreak(){trackingNeutralCount=0;trackingNeutralFrame=-1;}
        bool ActiveRawTrackingContext()
        {return namedRawPump!=null&&participant!=null&&device!=null&&!RecordingBlocked&&!NamedRawCompletionActive&&
            !participant.Completed&&participant.Phase!=Phase.NeedsProtocolDecision;}
        bool TrackingPlanPending {get{return rawPlanStartRequested!=0||rawPendingBlockPlan!=null;}}
        void MaskRawTrackingFrame(string notice)
        {
            trackingMaskedFrame=Time.frameCount;RawTrackingHoldNotice=notice;message=notice;
            suppressInputUntilReleased=true;
            if(questionnaireGate!=null)questionnaireGate.Reset();
            if(rig!=null)rig.BlockTaskPresentation();
            if(view!=null){view.SetPresentationAvailable(false);view.SuppressViveRawStimulus();view.SuppressViveRawPanel();}
        }
        void BeginRawTrackingHostBoundary()
        {
            ResetRawTrackingNeutralStreak();
            if(namedRawPump.TrackingGapOpen)return;
            trackingRequiresExplicitFocusResume=rig!=null&&!rig.XrFocused;
            ClearPendingRawOwnerAction();
            RequirePlanConfirmationAfterXrGap();
            device.ResetTaskInputForFocusGap();
            committedRawTrial=null;committedRawRevision=-1;
            rawGenericCallbackTrial=null;rawGenericCallbackFrame=-1;
        }
        internal bool TryHoldRawHeadTracking(ViveProEyePoll exactInvalidHead)
        {
            if(!ActiveRawTrackingContext()||pendingRawPresentation!=null||pendingRawPanelFrame>=0)return false;
            BeginRawTrackingHostBoundary();
            MaskRawTrackingFrame("Headset pose temporarily unavailable. Task held; recording stays open. Missing head/eye measurements are unknown.");
            if(!namedRawPump.ObserveTrackingHeadLoss(exactInvalidHead,TrackingPlanPending))return false;
            if(rig!=null&&!rig.XrFocused)ObserveFocusLossDuringTracking();
            if(!MaintainRawTrackingWoZ(false))return false;
            return MaintainPendingRawPlanReceiptDuringFocus();
        }
        // This negative maintenance may suspend the old live source, but can
        // never acquire or restart it. Its metadata waits for a fresh eye epoch.
        bool MaintainRawTrackingWoZ(bool freshEpoch)
        {
            if(!legacyWozStarted||legacyWozProcess==null||!FocusHeldLiveWoz)return true;
            if(!legacyWozProcess.TickFocusHoldSuspension())return false;
            if(legacyWozScene!=null&&!legacyWozScene.ClearForSafety())return false;
            return !freshEpoch||legacyWozProcess.RecordFocusSuspensionMetadataForCurrentFrame();
        }
        bool HandleActiveRawTrackingController(ViveStroopDeviceFrame frame)
        {
            bool held=namedRawPump.TrackingGapOpen;
            if(!held&&!frame.SameDevicePoseLoss)return false;
            if(!ActiveRawTrackingContext()||frame.DeviceChanged||
                !frame.DeviceReady&&!frame.SameDevicePoseLoss&&!frame.ActionActivationPending)
            {FailNamedRawBeforeFrame();return true;}
            if(rig!=null&&!rig.XrFocused){ObserveFocusLossDuringTracking();return false;}
            if(frame.SameDevicePoseLoss)
            {
                BeginRawTrackingHostBoundary();
                MaskRawTrackingFrame("Controller pose temporarily unavailable. Task held; recording stays open.");
                if(!namedRawPump.ObserveTrackingControllerLoss(TrackingPlanPending)||!MaintainRawTrackingWoZ(false)||
                    !MaintainPendingRawPlanReceiptDuringFocus())FailNamedRawBeforeFrame();
                return true;
            }
            MaskRawTrackingFrame("Tracking recovery: keep the same headset and controller, and release all controls.");
            if(!MaintainRawTrackingWoZ(true)||!MaintainPendingRawPlanReceiptDuringFocus())
            {FailNamedRawBeforeFrame();return true;}
            bool neutral=frame.InputReady&&!frame.TriggerHeld&&!frame.MenuHeld&&!frame.Trackpad.Pressed;
            bool mustConfirm=trackingRequiresExplicitFocusResume&&participant.IsRawTrackingPaused&&!FocusHeldLiveWoz;
            bool explicitResume=mustConfirm&&trackingNeutralCount>=RawTrackingNeutralFrames&&
                trackingNeutralFrame==Time.frameCount-1&&frame.InputReady&&frame.TriggerHeld&&frame.TriggerDown&&
                !frame.MenuHeld&&!frame.Trackpad.Pressed;
            if(!neutral&&!explicitResume)ResetRawTrackingNeutralStreak();
            else if(trackingNeutralFrame!=Time.frameCount)
            {
                trackingNeutralCount=trackingNeutralFrame==Time.frameCount-1?trackingNeutralCount+1:1;
                trackingNeutralFrame=Time.frameCount;
            }
            if(mustConfirm&&!explicitResume&&trackingNeutralCount>=RawTrackingNeutralFrames)
                MaskRawTrackingFrame("Tracking is stable. XR focus was lost while held; press Trigger to confirm task recovery.");
            if(trackingNeutralCount>=RawTrackingNeutralFrames&&(!mustConfirm||explicitResume))
            {
                ClearPendingRawOwnerAction();
                if(!namedRawPump.EndTrackingGap()){FailNamedRawBeforeFrame();return true;}
                trackingRequiresExplicitFocusResume=false;
                ResetRawTrackingNeutralStreak();device.ResetTaskInputForFocusGap();
                MaskRawTrackingFrame(FocusHeldLiveWoz?LiveWozFocusHoldNotice:TrackingPlanPending?FocusPlanAwaitNotice:
                    "Tracking is stable again. No held response was accepted; an interrupted trial will restart with a fresh presentation.");
            }
            if(!namedRawPump.DrainTaskEvents())FailNamedRawAfterTask();
            return true;
        }
    }
}
#endif
