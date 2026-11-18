#if STROOP_XR_PREVIEW
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Unity
{
    public sealed partial class ViveStroopDevelopmentBootstrap
    {
        bool desktopFocusObserved,desktopFocusKnown,xrFocusGapActive;
        bool rawXrFocusLossUnrecorded;
        int rawFocusBoundaryFrame=-1;
        bool RawFocusBoundaryMasksThisFrame {get{return rawFocusBoundaryFrame==UnityEngine.Time.frameCount;}}
        bool previewFocusKnown,previewDesktopFocus,previewXrFocus;
        bool focusRecoveryPending,focusPausedTask;
        readonly System.Collections.Generic.Queue<bool> desktopFocusTransitions=new System.Collections.Generic.Queue<bool>(16);
        bool desktopFocusTransitionOverflow;
        bool rawFocusBaselinePending,rawBaselineDesktopFocus,rawBaselineXrFocus;
        bool rawFocusMetadataKnown,lastRecordedDesktopFocus,lastRecordedXrFocus;
        bool focusOwnerActionWasCancelled;
        int explicitWithdrawalFrame=-1;
        ViveBlockPlanRequest focusReadyPlanRequest;
        ViveRawSessionJournalSink.BlockPlanWriteReceipt focusReadyPlanReceipt;
        double focusReadyPlanObserved=-1;
        bool focusPlanConfirmationRequired,focusPlanConfirmationArmed,focusPlanTriggerAccepted;
        int focusPlanPromptFrame=-1;
        int maskedLiveWozHoldFrame=-1;
        ViveNamedRecordingSetup loggedCancelledSetup;
        bool DesktopFocusObserved {get{return desktopFocusKnown?desktopFocusObserved:UnityEngine.Application.isFocused;}}

        // Desktop focus is metadata, not revocation of local recording choices
        // or source authority. Process it with the next admitted raw frame;
        // this callback must never interfere with a pending render reservation.
        void ObserveDesktopFocus(bool focused)
        {
            bool changed=!desktopFocusKnown||desktopFocusObserved!=focused;
            if(namedRawPump!=null&&!NamedRawCompletionActive&&participant!=null&&!participant.Completed&&participant.Phase!=Phase.NeedsProtocolDecision&&changed)
            {
                if(desktopFocusTransitions.Count<32)desktopFocusTransitions.Enqueue(focused);
                else desktopFocusTransitionOverflow=true;
            }
            desktopFocusObserved=focused;desktopFocusKnown=true;
            recordingSelectionFreshFrame=-1;
        }
        internal bool PollExplicitDesktopWithdrawal()
        {
            // Edit-mode fixtures do not have a live legacy input backend.
            // The bool-based handler below exercises the actual stop routing.
            if(!UnityEngine.Application.isPlaying)return false;
            return HandleExplicitDesktopWithdrawal(UnityEngine.Input.GetKeyDown(UnityEngine.KeyCode.Escape));
        }
        internal bool HandleExplicitDesktopWithdrawal(bool pressed)
        {
            if(!pressed)return false;
            if(explicitWithdrawalFrame==UnityEngine.Time.frameCount)return true;
            if(preSessionChoice!=null)
            {explicitWithdrawalFrame=UnityEngine.Time.frameCount;preSessionChoice.Cancel();RenderPreSessionChoice();return true;}
            if(recordingSetup!=null&&!configured)
            {explicitWithdrawalFrame=UnityEngine.Time.frameCount;CancelRecordingChoices();RenderPreSessionChoice();return true;}
            if(!configured||participant==null)return false;
            explicitWithdrawalFrame=UnityEngine.Time.frameCount;
            if(recordingSetup!=null&&namedRawPump==null)CancelRecordingChoices();
            WithdrawTask();return true;
        }
        internal void InitializeRawFocusObservations(bool desktopFocused,bool xrFocused)
        {
            // Capture begins here. Earlier setup/SDK-window history is not a
            // recording event. The initial observation and subsequent callback
            // transitions are journaled at a later admitted processing time.
            desktopFocusTransitions.Clear();desktopFocusTransitionOverflow=false;
            desktopFocusObserved=desktopFocused;desktopFocusKnown=true;
            rawBaselineDesktopFocus=desktopFocused;rawBaselineXrFocus=xrFocused;rawFocusBaselinePending=true;
            xrFocusGapActive=false;focusRecoveryPending=false;focusPausedTask=false;
            rawXrFocusLossUnrecorded=false;rawFocusBoundaryFrame=-1;
            rawFocusMetadataKnown=false;focusOwnerActionWasCancelled=false;
            ClearRawPlanReadyObservation();
            focusPlanConfirmationRequired=focusPlanConfirmationArmed=focusPlanTriggerAccepted=false;focusPlanPromptFrame=-1;
        }

        internal void RequirePlanConfirmationAfterXrGap()
        {
            NoteConsentAutoStartInterruption();
            if(rawPlanStartRequested==0&&rawPendingBlockPlan==null)return;
            if(rawPlanStartRequested==1)ResetPracticeCountdown();
            focusPlanConfirmationRequired=true;focusPlanConfirmationArmed=focusPlanTriggerAccepted=false;focusPlanPromptFrame=-1;
        }
        // Input levels and the fresh edge are from the existing real adapter
        // sample. This helper changes no task, source, receipt or I/O state.
        internal bool ObserveFocusPlanConfirmation(bool inputReady,bool neutralPending,bool triggerHeld,bool triggerDown,bool menuHeld,bool trackpadHeld)
        {
            if(!focusPlanConfirmationRequired)return true;
            if(!inputReady||neutralPending){focusPlanConfirmationArmed=false;return false;}
            if(!focusPlanConfirmationArmed)
            {
                if(!triggerHeld&&!menuHeld&&!trackpadHeld)focusPlanConfirmationArmed=true;
                return false;
            }
            if(!triggerDown||!triggerHeld||menuHeld||trackpadHeld)return false;
            focusPlanConfirmationRequired=false;focusPlanConfirmationArmed=false;focusPlanPromptFrame=-1;
            return true;
        }
        bool ObservePreparedPlanStartConfirmation(ViveStroopDeviceFrame frame)
        {
            if(focusPlanTriggerAccepted)
            {
                if(!frame.InputReady||frame.NeutralPending||frame.TriggerHeld||frame.MenuHeld||frame.Trackpad.Pressed)return false;
                focusPlanTriggerAccepted=false;
                UnityEngine.Debug.Log("VIVE_RAW_PLAN_RECONFIRMED: fresh trigger press and release; frame="+UnityEngine.Time.frameCount);
                return true;
            }
            if(!focusPlanConfirmationRequired)return true;
            if(ObserveFocusPlanConfirmation(frame.InputReady,frame.NeutralPending,frame.TriggerHeld,frame.TriggerDown,frame.MenuHeld,frame.Trackpad.Pressed))
                focusPlanTriggerAccepted=true;
            return false;
        }
        bool FocusPlanPromptCurrent
        {get{return (focusPlanConfirmationRequired||focusPlanTriggerAccepted)&&focusPlanPromptFrame==UnityEngine.Time.frameCount&&participant!=null&&
            RawPlanStartPhaseMatches(rawPlanStartRequested==1,participant.Phase);}}
        string FocusPlanAwaitNotice {get{return "Release all controls, then press and release Trigger in the headset to confirm the prepared plan. "+
            (namedRawPump!=null?"Recording continues; the task has not started.":"Recording has not started.");}}

        internal bool WouldRecordRawFocusObservation(bool desktopFocused,bool xrFocused)
        {return !rawFocusMetadataKnown||lastRecordedDesktopFocus!=desktopFocused||lastRecordedXrFocus!=xrFocused;}

        // A queued owner delegate has not run or produced I/O. A new journal
        // event would invalidate its exact cursor stamp, so cancel that request
        // explicitly rather than quietly refreshing its authority or faulting
        // the recorder. Prepared block-plan I/O and receipts are untouched.
        internal bool CancelUnexecutedOwnerActionForFocusMetadata(bool desktopFocused,bool xrFocused)
        {
            if(!WouldRecordRawFocusObservation(desktopFocused,xrFocused)||pendingRawOwnerAction==null)return false;
            ClearPendingRawOwnerAction();focusOwnerActionWasCancelled=true;
            message="The queued owner action was cancelled before execution because focus metadata changed. Review the task and click the action again. Recording continues.";
            return true;
        }
        bool RecordRawFocusObservation(bool desktopFocused,bool xrFocused)
        {
            CancelUnexecutedOwnerActionForFocusMetadata(desktopFocused,xrFocused);
            if(!namedRawPump.ProcessFocusState(desktopFocused,xrFocused))return false;
            rawFocusMetadataKnown=true;lastRecordedDesktopFocus=desktopFocused;lastRecordedXrFocus=xrFocused;
            return true;
        }

        // A Ready observation means only that the exact writer receipt was
        // actually returned before the I/O deadline. It grants no right to
        // start a task and never substitutes a later context/source check.
        internal static bool RawPlanWaitObservationAllows(double deadline,double now,bool matchingReadyObserved,double observed)
        {
            if(double.IsNaN(now)||double.IsInfinity(now)||now<0)return false;
            if(!matchingReadyObserved)return IsWithinRawPlanDeadline(deadline,now);
            return IsWithinRawPlanDeadline(deadline,observed)&&now>=observed;
        }
        bool ExactReadyPlanObserved
        {get{return rawPendingBlockPlan!=null&&object.ReferenceEquals(rawPendingBlockPlan,focusReadyPlanRequest)&&focusReadyPlanReceipt!=null;}}
        void ClearRawPlanReadyObservation()
        {focusReadyPlanRequest=null;focusReadyPlanReceipt=null;focusReadyPlanObserved=-1;}
        bool ObserveReadyRawPlanReceipt(ViveRawSessionJournalSink.BlockPlanWriteReceipt receipt)
        {
            double now;
            if(receipt==null||rawPendingBlockPlan==null||sharedClock==null||!sharedClock.TryReadRecordingAdmissionTime(out now))return false;
            if(ExactReadyPlanObserved)return object.ReferenceEquals(receipt,focusReadyPlanReceipt)&&
                RawPlanWaitObservationAllows(rawPlanDeadline,now,true,focusReadyPlanObserved);
            if(!RawPlanWaitObservationAllows(rawPlanDeadline,now,false,-1))return false;
            focusReadyPlanRequest=rawPendingBlockPlan;focusReadyPlanReceipt=receipt;focusReadyPlanObserved=now;
            return true;
        }
        bool TryPollPendingRawPlanReceipt(out ViveRawSessionJournalSink.BlockPlanReceiptPoll state,
            out ViveRawSessionJournalSink.BlockPlanWriteReceipt receipt)
        {
            state=ViveRawSessionJournalSink.BlockPlanReceiptPoll.Faulted;receipt=null;
            if(rawPendingBlockPlan==null||rawPlanStartRequested==0||namedRawPump==null)return false;
            if(!RawPlanDeadlineStillValid())
            {FailNamedRawBeforeFrame();message+=" Block plan confirmation timed out or its clock failed; in-flight I/O is unknown.";return false;}
            if(!rawPendingBlockPlan.TryValidateCurrentForReceipt(participant,sharedClock))
            {FailNamedRawBeforeFrame();message+=" Block plan changed while waiting; its outcome is unknown.";return false;}
            state=namedRawPump.TryGetBlockPlanReceipt(rawPendingBlockPlan,out receipt);
            if(state==ViveRawSessionJournalSink.BlockPlanReceiptPoll.Pending&&!ExactReadyPlanObserved)return true;
            if(state==ViveRawSessionJournalSink.BlockPlanReceiptPoll.Ready&&ObserveReadyRawPlanReceipt(receipt))return true;
            FailNamedRawBeforeFrame();message+=" Block-plan writer receipt was unavailable, replaced or revoked; no task was started.";return false;
        }
        internal bool MaintainPendingRawPlanReceiptDuringFocus()
        {
            // A deliberately suspended LiveWoZ source has no resume/start
            // path in this build. Preserve submitted I/O as unconsumed evidence;
            // do not turn that source change into a plan deadline/context fault.
            // The writer's own real errors remain visible through pump status.
            if(FocusHeldLiveWoz)return true;
            if(rawPendingBlockPlan==null)return true;
            ViveRawSessionJournalSink.BlockPlanReceiptPoll state;
            ViveRawSessionJournalSink.BlockPlanWriteReceipt receipt;
            return TryPollPendingRawPlanReceipt(out state,out receipt);
        }
        internal bool NamedRawFocusHoldMasked
        {get{return namedRawPump!=null&&maskedLiveWozHoldFrame==UnityEngine.Time.frameCount&&FocusHeldLiveWoz;}}
        bool HoldSuspendedLiveWozAfterCurrentGuards()
        {
            if(!FocusHeldLiveWoz)return false;
            if(!MaintainLegacyWozDuringFocusGap())
            {if(namedRawPump!=null)FailNamedRawBeforeFrame();return true;}
            if(questionnaireGate!=null)questionnaireGate.Reset();
            if(rig!=null)rig.BlockTaskPresentation();
            if(view!=null){view.SetPresentationAvailable(false);view.SuppressViveRawStimulus();view.SuppressViveRawPanel();}
            if(namedRawPump!=null&&!DrainNamedRawTaskEvents())return true;
            maskedLiveWozHoldFrame=UnityEngine.Time.frameCount;
            message=LiveWozFocusHoldNotice;
            return true;
        }

        internal void HoldPreStartTracking(string cause,string detail=null)
        {
            if(recordingSetup==null||namedRawPump!=null)return;
            NoteConsentAutoStartInterruption();
            bool first=!recordingSetup.TrackingSuspended;
            recordingSetup.SuspendInputForTracking();recordingSelectionFreshFrame=-1;
            if(device!=null)device.ResetTaskInputForFocusGap();
            if(view!=null)view.SetPresentationAvailable(false);
            message="Tracking temporarily unavailable. Recording choices and selected folder are retained. Restore tracking and release controls; then start recording explicitly. No recording has started.";
            if(first)UnityEngine.Debug.LogWarning("VIVE_RECORDING_SETUP_HELD: cause="+cause+
                "; frame="+UnityEngine.Time.frameCount+"; head_status="+(rig==null?"unavailable":rig.CurrentHeadDiagnostic)+
                (System.String.IsNullOrEmpty(detail)?"":"; controller="+detail));
        }
        internal static string BoundedPreStartControllerDiagnostic(ViveStroopDeviceFrame frame)
        {
            if(frame==null)return "no current controller poll";
            if(frame.DeviceChanged)return "controller identity changed";
            if(frame.SameDevicePoseLoss)return "same controller; pose not tracked";
            if(frame.ActionActivationPending)return "controller actions not active";
            var diagnostic=frame.Diagnostic??"";
            if(diagnostic.IndexOf("More than one",System.StringComparison.OrdinalIgnoreCase)>=0)return "multiple controllers";
            if(diagnostic.IndexOf("identity changed",System.StringComparison.OrdinalIgnoreCase)>=0)return "controller identity changed";
            if(diagnostic.IndexOf("No connected",System.StringComparison.OrdinalIgnoreCase)>=0)return "no connected controller";
            if(diagnostic.IndexOf("bind",System.StringComparison.OrdinalIgnoreCase)>=0)return "controller bindings unavailable";
            if(diagnostic.IndexOf("focus",System.StringComparison.OrdinalIgnoreCase)>=0)return "XR focus unavailable";
            if(diagnostic.IndexOf("pose",System.StringComparison.OrdinalIgnoreCase)>=0)return "controller pose invalid";
            return "controller input unavailable; exact cause unconfirmed";
        }
        internal bool HandlePreStartControllerFrame(ViveStroopDeviceFrame frame,MonotonicClock clock)
        {
            if(recordingSetup==null||namedRawPump!=null)return false;
            if(frame==null||!frame.DeviceReady&&!frame.DeviceChanged)
            {HoldPreStartTracking("controller_tracking_unavailable",BoundedPreStartControllerDiagnostic(frame));return true;}
            recordingSetup.Observe(frame.DeviceReady,true,frame.TriggerHeld,frame.MenuHeld,frame.Trackpad.Pressed,frame.DeviceChanged,clock);
            if(recordingSetup.Current==ViveNamedRecordingSetup.Step.Cancelled)
            {
                recordingSelectionFreshFrame=-1;if(view!=null)view.SetPresentationAvailable(false);
                message="Recording setup invalidated: "+recordingSetup.CancellationMessage+" No recording was started.";return true;
            }
            return false;
        }

        void CancelRecordingChoices(ViveNamedRecordingSetup.CancellationCause cause=ViveNamedRecordingSetup.CancellationCause.ExplicitCancellation)
        {
            CancelConsentAutoStart("Automatic start canceled with the recording choices. No automatic retry.");
            if(recordingSetup==null)return;
            CancelRequestedRawRecordingStart();
            recordingSetup.Cancel(cause);PublishRecordingChoiceCancellation();
        }
        void PublishRecordingChoiceCancellation()
        {
            var setup=recordingSetup;
            if(setup==null||setup.Current!=ViveNamedRecordingSetup.Step.Cancelled||object.ReferenceEquals(setup,loggedCancelledSetup))return;
            loggedCancelledSetup=setup;
            UnityEngine.Debug.LogWarning("VIVE_RECORDING_SETUP_CANCELLED: cause="+setup.Cancellation+"; frame="+UnityEngine.Time.frameCount);
        }

        void ObservePreviewFocusState(bool xrFocused)
        {
            if(participant==null||participant.RawRecordingRequested||participant.Completed||participant.Phase==Phase.NeedsProtocolDecision)return;
            bool desktop=DesktopFocusObserved;
            if(previewFocusKnown&&previewDesktopFocus==desktop&&previewXrFocus==xrFocused)return;
            participant.RecordFocusState(desktop,xrFocused,!previewFocusKnown);
            if(!xrFocused&&participant.Phase==Phase.Paused)focusPausedTask=true;
            else if(xrFocused&&participant.Phase!=Phase.Paused)focusPausedTask=false;
            previewFocusKnown=true;previewDesktopFocus=desktop;previewXrFocus=xrFocused;
        }

        // Called before prior-frame render expiry and before the next raw
        // sample is staged. Only a genuinely uncommitted reservation may be
        // abandoned; structural/data/storage failures keep their stop path.
        internal bool PrepareRawFocusBoundary(bool xrFocused)
        {
            if(namedRawPump==null||NamedRawCompletionActive||participant==null||participant.Completed||participant.Phase==Phase.NeedsProtocolDecision)return true;
            if(xrFocused)
            {
                if(xrFocusGapActive)
                {
                    xrFocusGapActive=false;suppressInputUntilReleased=true;
                    if(device!=null)device.ResetTaskInputForFocusGap();
                }
                return true;
            }
            rawFocusBoundaryFrame=UnityEngine.Time.frameCount;
            if(!xrFocusGapActive)
            {
                xrFocusGapActive=true;rawXrFocusLossUnrecorded=true;
                focusRecoveryPending=true;suppressInputUntilReleased=true;
                if(device!=null)device.ResetTaskInputForFocusGap();
                if(questionnaireGate!=null)questionnaireGate.Reset();
            }
            RequirePlanConfirmationAfterXrGap();
            var token=pendingRawPresentation;
            if(!namedRawPump.TryAbandonPresentationForFocus(token))
            {FailNamedRawAfterTask();return false;}
            pendingRawPresentation=null;pendingRawPanelFrame=-1;pendingRawPanelRevision=-1;
            committedRawTrial=null;committedRawRevision=-1;rawGenericCallbackTrial=null;rawGenericCallbackFrame=-1;
            if(participant!=null&&participant.LegacyWozRecording!=null)participant.LegacyWozRecording.InvalidateAppliedPresentation();
            if(rig!=null)rig.BlockTaskPresentation();
            if(view!=null){view.SetPresentationAvailable(false);view.SuppressViveRawStimulus();view.SuppressViveRawPanel();}
            return true;
        }

        // Requires this frame's real physical head/eye sample to have passed
        // StageRigFrame. Focus metadata never substitutes a valid device sample.
        // The runtime records focus changes and holds/interrupts the task on XR
        // loss without revoking the recorder; restoration never resumes it.
        internal bool ProcessRawFocusFrame(bool xrFocused)
        {
            var pump=namedRawPump;if(pump==null||NamedRawCompletionActive||participant==null||participant.Completed||participant.Phase==Phase.NeedsProtocolDecision)return true;
            if(desktopFocusTransitionOverflow)
            {message="Focus metadata queue overflowed; the observation sequence is incomplete.";FailNamedRawAfterTask();return false;}
            if(rawFocusBaselinePending)
            {
                if(!RecordRawFocusObservation(rawBaselineDesktopFocus,rawBaselineXrFocus))
                {FailNamedRawAfterTask();return false;}
                rawFocusBaselinePending=false;
            }
            // A cull/render may have observed loss and masked the task even
            // if OpenXR is focused again by this Update. Apply that negative
            // observation with today's valid task/head epoch before any
            // focused input; never pretend it was never interrupted.
            if(rawXrFocusLossUnrecorded&&xrFocused)
            {
                if(!RecordRawFocusObservation(rawFocusMetadataKnown?lastRecordedDesktopFocus:DesktopFocusObserved,false))
                {FailNamedRawAfterTask();return false;}
                focusPausedTask=participant.Phase==Phase.Paused;
                if(!MaintainLegacyWozDuringFocusGap()){FailNamedRawBeforeFrame();return false;}
                if(!MaintainPendingRawPlanReceiptDuringFocus())return false;
            }
            // Preserve callback transitions even when desktop focus leaves and
            // returns between Updates. Their recording time is the shared
            // software processing clock, not an OS/native event timestamp.
            while(desktopFocusTransitions.Count!=0)
                if(!RecordRawFocusObservation(desktopFocusTransitions.Dequeue(),xrFocused))
                {FailNamedRawAfterTask();return false;}
            if(!RecordRawFocusObservation(DesktopFocusObserved,xrFocused))
            {FailNamedRawAfterTask();return false;}
            rawXrFocusLossUnrecorded=false;
            if(xrFocused)
            {
                if(focusPausedTask&&participant.Phase!=Phase.Paused)focusPausedTask=false;
                return true;
            }
            focusPausedTask=participant!=null&&participant.Phase==Phase.Paused;
            suppressInputUntilReleased=true;
            if(questionnaireGate!=null)questionnaireGate.Reset();
            if(!MaintainLegacyWozDuringFocusGap()){FailNamedRawBeforeFrame();return false;}
            if(!MaintainPendingRawPlanReceiptDuringFocus())return false;
            Render();
            if(!pump.DrainTaskEvents()){FailNamedRawAfterTask();return false;}
            message=FocusHeldLiveWoz?LiveWozFocusHoldNotice:
                "XR focus unavailable. Recording continues with focus metadata; task is held and presentation is unconfirmed. Restore XR focus, release controls, then use Trigger to resume.";
            return true;
        }

        bool AwaitFocusActionActivation(ViveStroopDeviceFrame frame)
        {
            if(!focusRecoveryPending||frame==null||!frame.ActionActivationPending)return false;
            suppressInputUntilReleased=true;
            if(questionnaireGate!=null)questionnaireGate.Reset();
            if(!MaintainLegacyWozDuringFocusGap()){FailNamedRawBeforeFrame();return true;}
            if(!MaintainPendingRawPlanReceiptDuringFocus())return true;
            message="XR focus restored; waiting for current controller actions. "+(namedRawPump!=null?"Recording continues. ":"Recording has not started. ")+"Release all controls; the task will not resume automatically.";
            Render();return true;
        }
        void ObserveFocusControllerRecovery(ViveStroopDeviceFrame frame)
        {
            if(focusRecoveryPending&&frame!=null&&frame.InputReady&&!frame.TriggerHeld&&!frame.MenuHeld&&!frame.Trackpad.Pressed)
                focusRecoveryPending=false;
        }
        bool FocusHeldWozPanel {get{return focusPausedTask&&participant!=null&&participant.Phase==Phase.Paused;}}
        bool FocusHeldLiveWoz {get{return participant!=null&&participant.LockedControl==ControlSelection.LiveWoz&&
            ((FocusHeldWozPanel&&participant.IsFocusHoldPaused)||participant.IsRawTrackingPaused||participant.RawTrackingInteractionHeld||legacyWozProcess!=null&&legacyWozProcess.FocusSuspendStarted);}}
        internal string RawFocusHoldStatus {get{return FocusHeldLiveWoz?LiveWozFocusHoldNotice:
            "XR focus unavailable. Recording continues with real tracked samples and focus metadata; task is held. Restore focus, release controls and use Trigger to resume.";}}
        string LiveWozFocusHoldNotice {get{return namedRawPump!=null?
            "LiveWoZ task remains held; this build has no verified source-resume path. Recording continues; use explicit stop when finished.":
            "LiveWoZ task remains held; this build has no verified source-resume path. This preview is not recording; use explicit stop when finished.";}}

        void HoldUnrecordedInputForXrFocus()
        {
            NoteConsentAutoStartInterruption();
            recordingSelectionFreshFrame=-1;focusRecoveryPending=true;suppressInputUntilReleased=true;
            if(device!=null)device.ResetTaskInputForFocusGap();
            if(questionnaireGate!=null)questionnaireGate.Reset();
            if(view!=null)view.SetPresentationAvailable(false);
            if(!MaintainLegacyWozDuringFocusGap()&&namedRawPump!=null)FailNamedRawBeforeFrame();
            // A pre-recording configuration is retained without accepting any
            // response. Real tracking/identity failures still invalidate it.
        }
    }
}
#endif
