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
            internal string WaitReason;
            internal int WaitFrame=-1,WaitLogCount;
        }
        FormalConfirmationRequest formalConfirmationRequest;
        const int FormalConfirmationWaitLogLimit=32;

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
            Debug.Log("VIVE_FORMAL_CONFIRM_CALLBACK: frame="+Time.frameCount+"; phase="+(participant==null?"none":participant.Phase.ToString())+
                "; desktopFocused="+Application.isFocused+"; event="+(Event.current==null?"none":Event.current.rawType.ToString()));
            string blocked=FormalConfirmationUnavailableReason();
            if(blocked!=null){message=blocked;Debug.Log("VIVE_FORMAL_CONFIRM_NOT_REQUESTED: "+blocked);return;}
            if(namedRawPump==null){Run(ApplyFormalConfirmation);return;}
            double now;
            if(!sharedClock.TryReadRecordingAdmissionTime(out now))
            {message="Confirmation not requested: the recording clock is unavailable.";Debug.Log("VIVE_FORMAL_CONFIRM_NOT_REQUESTED: "+message);return;}
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
            var request=formalConfirmationRequest;
            if(request!=null)
            {
                if(rig==null||!rig.HeadReady)ReportFormalConfirmationWait(request,"headset_unavailable");
                else if(!rig.XrFocused)ReportFormalConfirmationWait(request,"xr_not_focused");
            }
        }
        bool FormalConfirmationPanelCurrent()
        {
            if(view==null||participant==null)return false;
            int answered=Math.Min(Questionnaire.Count,Math.Max(0,participant.RatingCount-Math.Max(0,participant.BlockIndex)*Questionnaire.Count));
            return view.HasPriorViveRawPanelWitness(participant.ParticipantView,participant.QuestionnaireInstructionPending,
                participant.CandidateRating,answered,Time.frameCount);
        }
        // Describe the existing sample; never read a second controller sample
        // from UI/logging, which could consume its neutral or trigger edge.
        internal static string FormalConfirmationInputWait(ViveStroopDeviceFrame frame,bool taskReady)
        {
            if(frame==null)return "controller_not_polled";
            if(!frame.InputReady)return frame.NeutralPending?"controller_neutral_pending":"controller_unavailable";
            if(!taskReady)return "task_input_not_ready";
            if(MustWaitForRawPlanNeutral(frame.NeutralPending,frame.TriggerHeld,frame.MenuHeld,frame.Trackpad.Pressed))
                return frame.TriggerHeld?"trigger_held":frame.MenuHeld?"menu_held":frame.Trackpad.Pressed?"trackpad_held":"controller_neutral_pending";
            return null;
        }
        internal static string FormalConfirmationWaitNotice(string reason)
        {
            switch(reason)
            {
                case "owner_action_pending":return "Confirmation requested. Waiting for the existing owner action to finish.";
                case "choice_provenance_pending":return "Confirmation requested. Waiting for the recording choices to enter the session record.";
                case "headset_unavailable":return "Confirmation requested. Restore headset tracking; no formal block has started.";
                case "xr_not_focused":return "Confirmation requested. Return to the focused headset view; no formal block has started.";
                case "scene_tracking_not_current":return "Confirmation requested. Waiting for a current headset pose and scene check.";
                case "controller_not_polled":return "Confirmation requested. Waiting for the next controller check.";
                case "controller_unavailable":return "Confirmation requested. Restore the connected controller.";
                case "controller_neutral_pending":return "Confirmation requested. Release Trigger, Menu and trackpad to arm the controller.";
                case "task_input_not_ready":return "Confirmation requested. Waiting for the task to accept the current controller readiness.";
                case "trigger_held":return "Confirmation requested. Release the controller Trigger.";
                case "menu_held":return "Confirmation requested. Release the controller Menu button.";
                case "trackpad_held":return "Confirmation requested. Release the controller trackpad.";
                case "panel_not_witnessed":return "Confirmation requested. Face the plan panel at the front of the classroom and release all controls.";
                default:return "Confirmation requested. Waiting for the next healthy neutral recording frame. Begin first block remains a separate operator action.";
            }
        }
        void ReportFormalConfirmationWait(FormalConfirmationRequest request,string reason)
        {
            bool changed=request.WaitReason!=reason;request.WaitReason=reason;request.WaitFrame=Time.frameCount;
            if(!changed||request.WaitLogCount>=FormalConfirmationWaitLogLimit)return;
            request.WaitLogCount++;
            Debug.Log("VIVE_FORMAL_CONFIRM_WAITING: "+reason+"; frame="+Time.frameCount+
                (request.WaitLogCount==FormalConfirmationWaitLogLimit?"; transition log limit reached; status remains live.":""));
        }
        void ObserveFormalConfirmationInputs(ViveStroopDeviceFrame frame)
        {
            var request=formalConfirmationRequest;if(request==null)return;
            string reason=FormalConfirmationInputWait(frame,participant!=null&&participant.InputReady);
            if(reason!=null)ReportFormalConfirmationWait(request,reason);
        }
        void ServiceFormalConfirmationRequest(ViveStroopDeviceFrame frame)
        {
            var request=formalConfirmationRequest;if(request==null)return;
            if(!FormalConfirmationStillCurrent(request))
            {CancelFormalConfirmation("Formal confirmation expired or its session changed. No automatic retry.");return;}
            string waiting=pendingRawOwnerAction!=null?"owner_action_pending":rawSelectionProvenancePending?"choice_provenance_pending":
                rig==null||!rig.HeadReady?"headset_unavailable":!rig.XrFocused?"xr_not_focused":
                !rig.RawSceneTrackingCurrent()?"scene_tracking_not_current":FormalConfirmationInputWait(frame,participant.InputReady);
            if(waiting==null&&!FormalConfirmationPanelCurrent())waiting="panel_not_witnessed";
            if(waiting!=null){ReportFormalConfirmationWait(request,waiting);return;}
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
                if(formalConfirmationRequest.WaitFrame==Time.frameCount&&formalConfirmationRequest.WaitReason!=null)
                    return FormalConfirmationWaitNotice(formalConfirmationRequest.WaitReason);
                if(!ControllerUiReady||participant==null||!participant.InputReady)return "Confirmation requested. Restore the controller and release Trigger, Menu and trackpad.";
                if(!FormalConfirmationPanelCurrent())return FormalConfirmationWaitNotice("panel_not_witnessed");
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
            ViveOwnerDesktopLayout.Observe("formal-confirm",GUILayoutUtility.GetLastRect());
            GUI.enabled=enabled;
            string status=FormalConfirmationStatus??blocked;
            if(!String.IsNullOrEmpty(status))GUILayout.Label(status,recordingWrap);
            if(formalConfirmationRequest!=null&&GUILayout.Button("Cancel pending formal confirmation"))
                CancelFormalConfirmation("Pending formal confirmation canceled. No source was confirmed and no formal block started.");
        }
    }
}
#endif
