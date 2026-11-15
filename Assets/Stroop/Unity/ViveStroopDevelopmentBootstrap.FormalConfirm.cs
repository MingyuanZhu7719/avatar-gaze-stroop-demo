#if STROOP_XR_PREVIEW
using System;
using AvatarStroop.Core;
using AvatarStroop.Runtime;
using UnityEngine;

namespace AvatarStroop.Unity
{
    public sealed partial class ViveStroopDevelopmentBootstrap
    {
        // A desktop request is intent, not an admission stamp. Acquire the
        // ordinary mutation's exact current stamp only after a healthy Poll,
        // neutral controls and the previous exact participant panel render.
        sealed class FormalConfirmationRequest
        {
            internal ViveDevelopmentParticipant Task;
            internal ViveNamedSimulationPlan Plan;
            internal ViveRawSessionContext Context;
            internal ViveNamedRawRecordingPump Pump;
            internal MonotonicClock Clock;
            internal WorldSpaceExperimentView View;
            internal int Block;
            internal double RequestedAt;
        }
        FormalConfirmationRequest formalConfirmationRequest;

        string FormalConfirmationUnavailableReason()
        {
            if(participant==null||participant.Phase!=Phase.BlockPreparation)return "Finish Practice before confirming the formal plan.";
            if(participant.LegacyWozRecordingDeclared||ownerPreviewWoz)return "Use the separate LiveWoZ source controls.";
            if(RecordingBlocked)return "Recording has stopped or faulted. This session cannot continue; review the recording fault below.";
            if(ObservationBlocked)return "Task observation is blocked. Review the fault below.";
            if(NamedRawCompletionActive||participant.Completed)return "This session is ending; formal confirmation is unavailable.";
            if(formalConfirmationRequest!=null||pendingRawOwnerAction!=null)return "A confirmation is already pending.";
            if(participant.RawRecordingRequested&&(namedRawPump==null||sharedClock==null||participant.RawContext==null))return "The recording session is not ready for formal confirmation.";
            if(namedRawPump==null&&!participant.InputReady)return "Restore headset/controller readiness and release all controls.";
            return null;
        }
        internal void RequestFormalConfirmation()
        {
            string blocked=FormalConfirmationUnavailableReason();
            if(blocked!=null){message=blocked;Debug.Log("VIVE_FORMAL_CONFIRM_NOT_REQUESTED: "+blocked);return;}
            if(namedRawPump==null){Run(ApplyFormalConfirmation);return;}
            double now;
            if(!sharedClock.TryReadRecordingAdmissionTime(out now))
            {message="Confirmation not requested: the recording clock is unavailable.";return;}
            formalConfirmationRequest=new FormalConfirmationRequest{Task=participant,Plan=participant.NamedPlan,Context=participant.RawContext,
                Pump=namedRawPump,Clock=sharedClock,View=view,Block=participant.BlockIndex,RequestedAt=now};
            message=FormalConfirmationStatus;
            Debug.Log("VIVE_FORMAL_CONFIRM_REQUESTED: desktop intent captured; waiting for exact headset panel and neutral inputs. No formal block started.");
        }
        void ApplyFormalConfirmation()
        {
            if(participant.NamedPlan!=null)participant.OwnerConfirmNamedPlan(Actor.Owner,"local-development-owner");
            else if(participant.HasNamedOption&&participant.NamedOption.Option==ViveNamedOption.NoAvatar)
                participant.OwnerConfirmNamedNoAvatar(Actor.Owner,"local-development-owner");
            else participant.OwnerLockSource(Actor.Owner,ControlSelection.Automatic,"local-development-owner");
        }
        bool FormalConfirmationStillCurrent(FormalConfirmationRequest request)
        {
            double now;
            return request!=null&&request.Pump!=null&&request.Clock!=null&&request.Clock.TryReadRecordingAdmissionTime(out now)&&RawIntentTimeCurrent(request.RequestedAt,now)&&
                participant!=null&&participant.Phase==Phase.BlockPreparation&&!participant.Completed&&!NamedRawCompletionActive&&
                !RecordingBlocked&&!ObservationBlocked&&!ownerPreviewWoz&&!participant.LegacyWozRecordingDeclared&&
                FormalConfirmationIdentityMatches(request);
        }
        bool FormalConfirmationIdentityMatches(FormalConfirmationRequest request)
        {
            return request!=null&&participant!=null&&participant.Phase==Phase.BlockPreparation&&
                object.ReferenceEquals(request.Task,participant)&&object.ReferenceEquals(request.Plan,participant.NamedPlan)&&
                object.ReferenceEquals(request.Context,participant.RawContext)&&object.ReferenceEquals(request.Pump,namedRawPump)&&
                object.ReferenceEquals(request.Clock,sharedClock)&&object.ReferenceEquals(request.View,view)&&request.Block==participant.BlockIndex;
        }
        void CancelFormalConfirmation(string reason)
        {
            if(formalConfirmationRequest==null)return;
            formalConfirmationRequest=null;message=reason;
            Debug.Log("VIVE_FORMAL_CONFIRM_CANCELED: "+reason);
        }
        void ObserveFormalConfirmationRequest()
        {
            if(formalConfirmationRequest!=null&&!FormalConfirmationStillCurrent(formalConfirmationRequest))
                CancelFormalConfirmation("Formal confirmation expired or this session changed/stopped. No automatic retry; review before clicking again.");
        }
        bool FormalConfirmationPanelCurrent()
        {
            if(view==null||participant==null)return false;
            int answered=Math.Min(Questionnaire.Count,Math.Max(0,participant.RatingCount-Math.Max(0,participant.BlockIndex)*Questionnaire.Count));
            return view.HasPriorViveRawPanelWitness(participant.ParticipantView,participant.QuestionnaireInstructionPending,
                participant.CandidateRating,answered,Time.frameCount);
        }
        void ServiceFormalConfirmationRequest(ViveStroopDeviceFrame frame)
        {
            var request=formalConfirmationRequest;if(request==null)return;
            if(!FormalConfirmationStillCurrent(request))
            {CancelFormalConfirmation("Formal confirmation expired or its session changed. No automatic retry.");return;}
            if(pendingRawOwnerAction!=null||rawSelectionProvenancePending||rig==null||!rig.HeadReady||!rig.XrFocused||!rig.RawSceneTrackingCurrent()||
                frame==null||!frame.InputReady||!participant.InputReady||MustWaitForRawPlanNeutral(frame.NeutralPending,frame.TriggerHeld,frame.MenuHeld,frame.Trackpad.Pressed)||
                !FormalConfirmationPanelCurrent())return;
            if(!FormalConfirmationStillCurrent(request))
            {CancelFormalConfirmation("Formal confirmation expired before execution. No automatic retry.");return;}
            // Consume once before the existing authoritative owner path. A
            // refusal or uncertain side effect is never an automatic retry.
            formalConfirmationRequest=null;
            Run(ApplyFormalConfirmation);
            Debug.Log(pendingRawOwnerAction!=null?
                "VIVE_FORMAL_CONFIRM_QUEUED: exact current recording stamp captured; existing reserved owner mutation will run once.":
                "VIVE_FORMAL_CONFIRM_NOT_QUEUED: no automatic retry; review owner status.");
        }
        string FormalConfirmationStatus
        {
            get
            {
                if(formalConfirmationRequest==null)return null;
                if(RecordingBlocked)return "Confirmation cannot run: recording stopped or faulted. Review the fault below.";
                if(rig==null||!rig.HeadReady)return "Confirmation requested. Restore headset tracking; no formal block has started.";
                if(!rig.XrFocused)return "Confirmation requested. Return to the focused headset view; no formal block has started.";
                if(!ControllerUiReady||participant==null||!participant.InputReady)return "Confirmation requested. Restore the controller and release Trigger, Menu and trackpad.";
                if(!FormalConfirmationPanelCurrent())return "Confirmation requested. Waiting for the current plan panel to render in the headset; release all controls.";
                return "Confirmation requested. Waiting for the next healthy neutral recording frame. Begin first block remains a separate operator action.";
            }
        }
        void DrawFormalConfirmationControls()
        {
            if(participant==null||participant.Phase!=Phase.BlockPreparation||participant.LegacyWozRecordingDeclared||ownerPreviewWoz)return;
            string blocked=FormalConfirmationUnavailableReason();bool enabled=GUI.enabled;
            GUI.enabled=enabled&&blocked==null;
            bool noAvatar=participant.HasNamedOption&&participant.NamedPlan==null&&participant.NamedOption.Option==ViveNamedOption.NoAvatar;
            if(GUILayout.Button(participant.NamedPlan!=null?"Confirm complete plan":noAvatar?"Confirm No Avatar":"Confirm Automatic"))RequestFormalConfirmation();
            GUI.enabled=enabled;
            string status=FormalConfirmationStatus??blocked;
            if(!String.IsNullOrEmpty(status))GUILayout.Label(status,recordingWrap);
            if(formalConfirmationRequest!=null&&GUILayout.Button("Cancel pending formal confirmation"))
                CancelFormalConfirmation("Pending formal confirmation canceled. No source was confirmed and no formal block started.");
        }
    }
}
#endif
