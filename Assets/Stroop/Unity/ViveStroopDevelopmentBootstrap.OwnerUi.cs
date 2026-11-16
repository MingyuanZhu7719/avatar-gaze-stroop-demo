#if STROOP_XR_PREVIEW
using System;
using System.IO;
using AvatarStroop.Core;
using AvatarStroop.Runtime;
using UnityEngine;

namespace AvatarStroop.Unity
{
    // Presentation only. Existing task, choice, source and output gates remain authoritative.
    public sealed partial class ViveStroopDevelopmentBootstrap
    {
        GUIStyle ownerHeading,ownerNotice;
        const string ControllerHeadUnavailable="Controller not checked for task input while headset tracking or focus is unavailable.";
        const string ControllerNotChecked="Controller not checked in this update.";
        // Display bookkeeping only. Do not poll physical input, mutate task
        // readiness, grant recording authority or change confirmation edges.
        void BeginControllerUiPoll(bool headReady)
        {
            inputDiagnosticFrame=-1;inputDiagnosticReady=false;inputDiagnosticHeadReady=headReady;
            inputDiagnostic=headReady?ControllerNotChecked:ControllerHeadUnavailable;
        }
        void RecordControllerUiPoll(bool headReady,bool ready,string diagnostic)
        {
            inputDiagnosticFrame=Time.frameCount;inputDiagnosticHeadReady=headReady;inputDiagnosticReady=ready;
            inputDiagnostic=String.IsNullOrEmpty(diagnostic)?"Controller input result is unavailable.":diagnostic;
        }
        bool ControllerUiChecked
        {get{return rig!=null&&rig.HeadReady&&inputDiagnosticHeadReady&&inputDiagnosticFrame==Time.frameCount;}}
        bool ControllerUiReady {get{return ControllerUiChecked&&inputDiagnosticReady;}}
        string ControllerUiState {get{return !ControllerUiChecked?"not checked":inputDiagnosticReady?"ready":"not ready";}}
        string ControllerUiDiagnostic
        {get{return rig==null||!rig.HeadReady?ControllerHeadUnavailable:
            !inputDiagnosticHeadReady||inputDiagnosticFrame!=Time.frameCount?ControllerNotChecked:inputDiagnostic;}}
        // These are the existing task actions and gates. Render them before
        // status/output diagnostics so live updates cannot move a held button.
        void DrawPrimaryTaskControls()
        {
            GUILayout.Space(6);
            bool practicePhase=participant.Phase==Phase.Preparation||participant.Phase==Phase.PracticeRetry;
            if(practicePhase&&!ConsentAutoStartActive)
            {
                var practiceGate=BeginPracticeBlocker();
                GUI.enabled=practiceGate==null;
                if(GUILayout.Button("Begin practice"))TryRequestBeginPractice();
                GUI.enabled=true;
                if(practiceGate!=null)GUILayout.Label(practiceGate,recordingWrap);
                if(rawPracticeRequested&&GUILayout.Button("Cancel pending practice start"))
                {ClearRawPracticeRequest();message="Pending practice start canceled. No trial started.";}
            }
            DrawFormalConfirmationControls();
            GUI.enabled=!RecordingBlocked&&!ObservationBlocked&&participant.InputReady&&participant.Phase==Phase.BlockReady&&participant.BlockIndex==0&&participant.LockedControl!=ControlSelection.LiveWoz;
            if(participant.Phase==Phase.BlockReady&&participant.BlockIndex==0&&participant.LockedControl!=ControlSelection.LiveWoz&&GUILayout.Button("Begin first block"))
            {if(namedRawPump!=null)RequestNamedBlockPlanStart(false);else Run(()=>participant.OwnerBeginBlock(Actor.Owner));}
            GUI.enabled=true;
        }
        // Kept outside the scrollable setup/status body by the desktop rig.
        // These call the same authoritative safety actions as before.
        public void DrawOwnerSafetyControls()
        {
            if(!configured||participant==null)return;
            bool enabled=GUI.enabled;
            bool stacked=ViveOwnerDesktopLayout.StackActions;
            if(stacked)GUILayout.BeginVertical();else GUILayout.BeginHorizontal();
            GUI.enabled=enabled&&participant.Phase!=Phase.Ended;
            if(GUILayout.Button("Pause task",GUILayout.Height(42)))PauseTask();
            GUI.enabled=enabled&&(participant.Phase!=Phase.Ended||rawCompletionRequested);
            if(GUILayout.Button("Withdraw task",GUILayout.Height(42)))WithdrawTask();
            GUI.enabled=enabled;if(stacked)GUILayout.EndVertical();else GUILayout.EndHorizontal();
        }
        void OwnerHeading(string text)
        {
            if(ownerHeading==null)ownerHeading=new GUIStyle(GUI.skin.label){fontStyle=FontStyle.Bold,fontSize=18,wordWrap=true};
            GUILayout.Space(8);GUILayout.Label(text,ownerHeading);
        }
        void OwnerNotice(string text)
        {
            if(ownerNotice==null)ownerNotice=new GUIStyle(GUI.skin.box){alignment=TextAnchor.MiddleLeft,wordWrap=true,padding=new RectOffset(12,12,10,10)};
            GUILayout.Label(text,ownerNotice,GUILayout.Width(ViveOwnerDesktopLayout.ContentWidth));
        }
        string NextOwnerAction()
        {
            if(participant.Phase==Phase.Ended)return participant.RawRecordingRequested?
                "Task ended. Review recording status and output paths below; faults or incomplete output remain unconfirmed.":
                "Preview ended. No files were saved. Reopen the application to start again.";
            if(RecordingBlocked||ObservationBlocked)return "Task blocked. Review the fault below or withdraw the task.";
            if(formalConfirmationRequest!=null)return FormalConfirmationStatus;
            if(focusPlanConfirmationRequired||focusPlanTriggerAccepted)return FocusPlanAwaitNotice;
            if(participant.Phase==Phase.Preparation&&ConsentAutoStartActive)
            {var automatic=ConsentAutoStartStatus;if(!String.IsNullOrEmpty(automatic))return automatic;}
            if(FocusHeldLiveWoz)return LiveWozFocusHoldNotice;
            if(namedRawPump!=null&&namedRawPump.TrackingGapOpen)return RawTrackingHoldNotice;
            if(recordingSetup!=null&&namedRawPump==null&&recordingSetup.TrackingSuspended)
                return "Tracking is temporarily unavailable. Choices and folder are retained. Restore tracking, release controls, then start recording explicitly.";
            if(recordingSetup!=null&&namedRawPump==null&&recordingSetup.Current==ViveNamedRecordingSetup.Step.Cancelled)
                return "Recording setup invalidated: "+recordingSetup.CancellationMessage+" No recording was started. Close this preview and configure a new session.";
            if(focusPlanConfirmationRequired||focusPlanTriggerAccepted)return FocusPlanAwaitNotice;
            if(rig!=null&&rig.ViewRunning&&!rig.XrFocused)return namedRawPump!=null?
                "XR focus unavailable. Local recording continues while real samples remain valid; the task is held. Restore focus, release controls, then use Trigger to resume.":
                "XR focus unavailable. Recording choices are retained. Restore focus and release controls before continuing.";
            if(focusOwnerActionWasCancelled)return "The queued owner action was cancelled before execution because focus metadata changed. Review the task and click the action again. Recording continues.";
            if(participant.Phase==Phase.Preparation)
            {var automatic=ConsentAutoStartStatus;if(!String.IsNullOrEmpty(automatic))return automatic;}
            if(participant.RawRecordingRequested&&namedRawPump==null)return "Next: complete source checks, then start local recording below.";
            if(!rig.ViewRunning)return "Next: start the classroom view.";
            if(!ControllerUiReady||!participant.InputReady)return "Next: keep one wand connected, release the trackpad and trigger, and restore headset tracking.";
            switch(participant.Phase)
            {
                case Phase.Preparation:case Phase.PracticeRetry:return BeginPracticeBlocker()??"Next: select Begin practice.";
                case Phase.Familiarization:return "In headset: follow the controller instructions and confirm with the trigger.";
                case Phase.Practice:case Phase.PracticeCorrection:return "Practice in progress. Respond using the trackpad; trigger confirms instructions.";
                case Phase.BlockPreparation:return "Next: review and confirm the experiment plan below.";
                case Phase.BlockReady:return "Next: begin the first block below.";
                case Phase.Countdown:case Phase.MainTask:return "Block running. The participant responds in the headset.";
                case Phase.Questionnaire:return "In headset: trackpad up selects a lower rating; down selects a higher rating. Trigger confirms.";
                case Phase.Rest:return "Rest. The participant presses the trigger when ready to continue.";
                case Phase.Paused:return "Paused. Review and acknowledge any input fault below, then release the controls and press the trigger to resume.";
                default:return "Follow the current headset instruction.";
            }
        }
        void SetOwnerRunMode(int mode)
        {
            if(mode<0||mode>1)throw new ArgumentOutOfRangeException("mode");
            ownerRunMode=mode;selectedDesign=-1;controlledRecordingNoticeAccepted=false;
            observationOptIn=false;legacyWozRecordingSelected=false;ownerPreviewWoz=false;
            namedIntentV3=false;namedTaskRecordingChoice=false;namedEyeRecordingChoice=false;
            ownerAdvanced=false;ownerStorageLimits=false;ownerOutputOptions=false;
            ClearNamedTaskView();
            message=ownerRunMode==1?"Choose a design and local output below. Recording is still off.":"Preview never saves task or eye files.";
        }
        void DrawSetupControls()
        {
            OwnerHeading("1  Run mode");
            int nextMode=ViveOwnerDesktopLayout.Selection("run-mode",ownerRunMode,new[]{"Preview (no files)","Record locally"},1);
            if(nextMode!=ownerRunMode)
            {
                SetOwnerRunMode(nextMode);
            }
            if(ownerRunMode<0){GUILayout.Label("Choose a mode to continue.");return;}
            bool local=ownerRunMode==1;
            GUILayout.Label(local?"Local development self-test. Recording is off.":"Preview only. No task, eye or CSV files will be saved.");
            OwnerHeading("2  Experiment design");
            int previousDesign=selectedDesign;
            if(local)
            {
                int option=selectedDesign==0?0:selectedDesign==3?1:-1;
                option=ViveOwnerDesktopLayout.Selection("record-design",option,new[]{"Legacy A/B/C/D","Named multi-block"},1);
                selectedDesign=option==0?0:option==1?3:-1;
            }
            else selectedDesign=ViveOwnerDesktopLayout.Selection("preview-design",selectedDesign,new[]{"Legacy A/B/C/D","Named Automatic (one block)","Named No Avatar (one block)","Named multi-block"},1);
            if(selectedDesign!=previousDesign){controlledRecordingNoticeAccepted=false;legacyWozRecordingSelected=false;ownerPreviewWoz=false;}
            if(view!=null)view.SetViveLegacyPreviewNotice(LegacyPreviewSelected);
            if(selectedDesign<0)return;
            DrawResponseRuleChoice();
            if(selectedDesign==1||selectedDesign==2)
            {
                GUILayout.Label("Stimulus list");
                namedListIndex=ViveOwnerDesktopLayout.Selection("single-list",namedListIndex,new[]{"0","1","2","3"},4,38);
            }
            if(selectedDesign==3)
            {
                GUILayout.Label("Number of blocks");
                namedPlanCount=ViveOwnerDesktopLayout.Selection("block-count",namedPlanCount,new[]{"1","2","3","4"},4,38);
                for(int i=0;i<=namedPlanCount;i++)
                {
                    GUILayout.Label("Block "+(i+1));
                    namedPlanOptions[i]=ViveOwnerDesktopLayout.Selection("block-"+i+"-option",namedPlanOptions[i],new[]{"Automatic","No Avatar"},2);
                    GUILayout.Label("Stimulus list");
                    namedPlanLists[i]=ViveOwnerDesktopLayout.Selection("block-"+i+"-list",namedPlanLists[i],new[]{"0","1","2","3"},4,38);
                    string missing=NamedBlockSelectionValidationReason(i);
                    if(!String.IsNullOrEmpty(missing))
                    {GUILayout.Label(missing,recordingWrap,GUILayout.Width(ViveOwnerDesktopLayout.ContentWidth));ViveOwnerDesktopLayout.Observe("block-"+i+"-validation",GUILayoutUtility.GetLastRect());}
                }
            }
            if(selectedDesign==0&&selectedTaskKind!=TaskKind.VisualSearch)
            {
                if(local&&ViveLegacyWozRecordingBuildPolicy.Available)
                {
                    bool next=GUILayout.Toggle(legacyWozRecordingSelected,"Use a second-PC WoZ operator");
                    if(next!=legacyWozRecordingSelected){legacyWozRecordingSelected=next;controlledRecordingNoticeAccepted=false;}
                }
                else if(!local)ownerPreviewWoz=GUILayout.Toggle(ownerPreviewWoz,"Use a second-PC WoZ operator (unrecorded)");
            }
            GUILayout.Space(6);GUILayout.Label("Controller: automatic. Keep exactly one VIVE wand connected.");
            GUILayout.Label(selectedTaskKind==TaskKind.VisualSearch?"Point at a backpack and pull the trigger. Ratings: trackpad up/down, trigger to confirm.":ViveFixedTrackpadMapping.Label);
            if(local)
            {
                OwnerHeading("3  Local output");
                EnsureDefaultSessionDirectoryOffer();
                GUILayout.Label("Session output folder (a new folder is offered for each launch)");
                recordingDirectory=GUILayout.TextField(recordingDirectory,GUILayout.Width(ViveOwnerDesktopLayout.ContentWidth));
                ViveOwnerDesktopLayout.Observe("folder-path",GUILayoutUtility.GetLastRect());
                GUI.enabled=!SessionDirectoryPreparationPending;
                if(GUILayout.Button("Choose a new session folder",GUILayout.Width(ViveOwnerDesktopLayout.ContentWidth)))
                    OfferFreshSessionDirectory(Path.GetFullPath(Path.Combine(Application.dataPath,"..","..","LocalSelfTestData")));
                GUI.enabled=true;
                if(SessionDirectoryPreparationPending&&GUILayout.Button("Cancel folder preparation"))
                    CancelSessionDirectoryPreparation("Folder preparation canceled. No recording was armed; an empty folder may remain.");
                if(!String.IsNullOrEmpty(SessionDirectoryStatusNotice))GUILayout.Label(SessionDirectoryStatusNotice,recordingWrap,GUILayout.Width(ViveOwnerDesktopLayout.ContentWidth));
                ownerStorageLimits=GUILayout.Toggle(ownerStorageLimits,"Advanced storage limits");
                if(ownerStorageLimits)
                {
                    GUILayout.Label("Raw maximum size (MiB, 1-8192)");
                    recordingBudgetMiB=GUILayout.TextField(recordingBudgetMiB,GUILayout.Width(ViveOwnerDesktopLayout.ContentWidth));
                    GUILayout.Label("CSV maximum size (MiB, 1-8192)");
                    csvBudgetMiB=GUILayout.TextField(csvBudgetMiB,GUILayout.Width(ViveOwnerDesktopLayout.ContentWidth));
                    GUILayout.Label("CSV cleanup timeout (seconds, 1-3600)");
                    csvCleanupSeconds=GUILayout.TextField(csvCleanupSeconds,GUILayout.Width(ViveOwnerDesktopLayout.ContentWidth));
                }
                GUILayout.Label("The offered folder is created only when you explicitly prepare. Custom paths must already exist. The writer checks access again after both choices.",recordingWrap);
                GUILayout.Label("Task responses, ratings and real eye/head data stay in this folder. No upload. Eye data may identify you.",recordingWrap);
                if(legacyWozRecordingSelected)GUILayout.Label("WoZ also saves mapped operator head/eye poses locally. Participant eyes, answers and ratings never go to the operator.",recordingWrap);
                GUILayout.Label("Withdrawal stops further work, but does not delete existing files. Failures leave output unconfirmed. Not validated for study use.",recordingWrap);
                controlledRecordingNoticeAccepted=GUILayout.Toggle(controlledRecordingNoticeAccepted,"I request a local development self-test (not a study participant session).",GUILayout.Width(ViveOwnerDesktopLayout.ContentWidth));
                long bytes;int timeout;
                bool csvValid=TryParseCsvLimits(csvBudgetMiB,csvCleanupSeconds,out bytes,out timeout);
                bool available=SelectedRecordingAvailable&&ViveRawRelativeFileCapability.ProductionGateOpen;
                string setupIssue=RecordingSetupValidationReason();
                if(!String.IsNullOrEmpty(setupIssue))
                {
                    GUILayout.Label("Before preparing: "+setupIssue,recordingWrap,GUILayout.Width(ViveOwnerDesktopLayout.ContentWidth));
                    if(selectedDesign==3)GUILayout.Label("Scroll up to the indicated block and select both its option and stimulus list. No selection is filled automatically.",recordingWrap,GUILayout.Width(ViveOwnerDesktopLayout.ContentWidth));
                }
                GUI.enabled=!rig.ViewRunning&&!SessionDirectoryPreparationPending&&ControlledRecordingNoticeCurrent&&available&&csvValid&&setupIssue==null;
                if(GUILayout.Button("Prepare raw + CSV and arm recording + Practice",GUILayout.Width(ViveOwnerDesktopLayout.ContentWidth)))ArmConsentAutoStart();
                ViveOwnerDesktopLayout.Observe("setup-submit",GUILayoutUtility.GetLastRect());
                GUI.enabled=true;
                if(!available)GUILayout.Label("Recording is unavailable in this build. No preview will start from this path.");
                else if(!ControlledRecordingNoticeCurrent)GUILayout.Label("Next: review the local-data notice and select the checkbox.");
                else if(!csvValid)GUILayout.Label("Enter valid CSV size and timeout limits.");
                OwnerNotice("This button explicitly requests local raw + CSV output and first Practice after both headset choices. Then start the classroom view once. Eye preview starts without saving data; accepting both choices and releasing controls starts recording and Practice automatically. Formal blocks still need the operator. Preparing may create one empty session folder, but no task, gaze or CSV files.");
                GUILayout.Label("The setup arm expires after 15 minutes; after both choices, the automatic start expires after 3 minutes. No automatic retry.",recordingWrap);
            }
            else
            {
                ownerAdvanced=GUILayout.Toggle(ownerAdvanced,"Advanced preview options");
                if(ownerAdvanced)
                {
                    observationOptIn=GUILayout.Toggle(observationOptIn,"Bounded in-memory observation (Legacy only; no files)");
                    if(selectedDesign>0&&observationOptIn)GUILayout.Label("Clear Legacy observation for Named previews.");
                    if(selectedDesign==3&&!ViveControlledRecordingBuildPolicy.Available)
                    {
                        namedIntentV3=GUILayout.Toggle(namedIntentV3,"Recording-choice rehearsal (does not record)");
                        if(namedIntentV3)
                        {
                            namedTaskRecordingChoice=GUILayout.Toggle(namedTaskRecordingChoice,"Rehearse task-event choice");
                            namedEyeRecordingChoice=GUILayout.Toggle(namedEyeRecordingChoice,"Rehearse eye-data choice");
                        }
                    }
                }
                GUI.enabled=!rig.ViewRunning;
                if(GUILayout.Button("Configure preview (no recording)",GUILayout.Width(ViveOwnerDesktopLayout.ContentWidth)))TryConfigure();
                ViveOwnerDesktopLayout.Observe("setup-submit",GUILayoutUtility.GetLastRect());
                GUI.enabled=true;
                if(rig.ViewRunning)GUILayout.Label("Stop the headset view before configuring.");
            }
        }

        string BeginPracticeBlocker()
        {
            if(participant==null||!RawPlanStartPhaseMatches(true,participant.Phase))return "Begin practice: task is not in a preparation phase.";
            if(previewPracticeCountdownTask!=null)return "Practice starts after the five-second headset countdown.";
            if(RecordingBlocked||ObservationBlocked)return "Begin practice: task is blocked; review the recording or observation status.";
            if(participant.RawRecordingRequested)
            {
                if(namedRawPump==null&&rawStartIntent==null)return "Begin practice: first request local recording explicitly.";
                if(rawPracticeRequested)return namedRawPump==null?"Practice requested. Waiting for recording to start first.":
                    PracticeExecutionBlocker()??"Practice start requested; waiting to prepare the block plan.";
                if(rawPlanStartRequested!=0||rawPendingBlockPlan!=null)return focusPlanConfirmationRequired?FocusPlanAwaitNotice:
                    "Practice start requested; waiting for current inputs and the block-plan write receipt.";
                return null; // Explicit request may wait; execution still validates the live frame.
            }
            if(rig==null||!rig.ViewRunning)return "Begin practice: start the headset view.";
            if(!rig.XrFocused||!rig.HeadReady)return "Begin practice: restore headset tracking and XR focus.";
            if(!ControllerUiReady||!participant.InputReady)return "Begin practice: waiting for one current, neutral controller.";
            return null;
        }
        void TryRequestBeginPractice()
        {
            var blocker=BeginPracticeBlocker();
            if(blocker!=null){message=blocker;return;}
            if(participant.RawRecordingRequested)
            {
                CaptureRawPracticeRequest();
            }
            else RequestPreviewPracticeCountdown();
        }
        string RecordingStartBlocker()
        {
            if(RecordingBlocked)return "Start recording: recording is blocked. Review the fault in diagnostics.";
            if(rig==null||!rig.ViewRunning)return "Start recording: start the headset view.";
            if(!rig.HeadReady)return "Start recording: restore headset tracking. "+rig.CurrentHeadDiagnostic;
            if(!rig.XrFocused)return "Start recording: close the SteamVR dashboard and restore XR focus.";
            if(!rig.RawSceneTrackingCurrent())return "Start recording: waiting for current headset tracking and scene readiness. "+rig.CurrentHeadDiagnostic;
            if(recordingSetup==null||!recordingSetup.BothSelected)return "Start recording: both headset choices are required.";
            if(recordingSetup.TrackingSuspended)return "Start recording: tracking was interrupted. Keep the headset on, restore the same controller, and release Trigger, Menu and the trackpad.";
            if(!ControllerUiChecked)return "Start recording: waiting for a current controller check. "+ControllerUiDiagnostic;
            if(!ControllerUiReady)return "Start recording: controller input is not ready. "+ControllerUiDiagnostic;
            if(recordingSelectionFreshFrame!=Time.frameCount)return "Start recording: waiting for current neutral controls. Release Trigger, Menu and the trackpad.";
            if(!rig.RecordingWorkerReady)return "Start recording: enable eye tracking and wait for the client to be ready.";
            var eyeReadiness=rig.RawStartReadiness;
            if(!eyeReadiness.HasValue||!eyeReadiness.Value.Ready)return ViveStroopPreviewRig.DescribeRawStartReadiness(eyeReadiness);
            if(participant!=null&&CanStartSelectedRawRecording(participant.LegacyRecordingRequested))
                return "Start recording: eye readiness is valid and current headset and neutral controller checks passed. Waiting to complete the requested start; recording has not started.";
            return "Start recording: current recording setup or output capability is unavailable. See diagnostics.";
        }
        void DrawRecordingStartControls(bool canStartRecording)
        {
            OwnerHeading("4  Start local recording");
            GUILayout.Label("Raw limit: "+recordingBudgetMiB+" MiB | CSV: "+csvBudgetMiB+" MiB | cleanup: "+csvCleanupSeconds+" s",recordingWrap);
            if(ConsentAutoStartActive)
            {
                GUILayout.Label(ConsentAutoStartStatus,recordingWrap);
                GUILayout.Space(12);
                if(GUILayout.Button("Cancel automatic recording + Practice start"))
                    CancelConsentAutoStart("Automatic start canceled. No automatic retry; review recording status before continuing.");
                return;
            }
            long csvBytes;int csvTimeout;
            bool csvParameters=TryParseCsvLimits(csvBudgetMiB,csvCleanupSeconds,out csvBytes,out csvTimeout);
            bool legacy=participant.LegacyRecordingRequested;
            if(rawStartIntent!=null)
            {
                GUILayout.Label("Start requested (expires after 3 minutes). Recording is still off. "+RecordingStartBlocker(),recordingWrap);
                if(GUILayout.Button("Cancel pending recording start"))CancelRequestedRawRecordingStart();
                return;
            }
            GUI.enabled=RawStartCanBeRequested(legacy,true,true)&&csvParameters;
            if(GUILayout.Button("Start recording + raw and CSV output"))
            {if(participant.LegacyRecordingRequested)StartSelectedLegacyRecordingWithCsv(csvBytes,csvTimeout);else StartSelectedNamedRecordingWithCsv(csvBytes,csvTimeout);}
            GUI.enabled=true;
            if(!canStartRecording)
                GUILayout.Label("You can request a start now. Recording waits for validation. "+RecordingStartBlocker(),recordingWrap);
            else if(!csvParameters)
                GUILayout.Label("Start with CSV is unavailable: review the CSV size and cleanup limits.",recordingWrap);
            GUILayout.Label("This explicitly requests local raw and CSV files. Completion must be checked separately; it is not proof of coverage or durable storage.",recordingWrap);
            ownerOutputOptions=GUILayout.Toggle(ownerOutputOptions,"Advanced output options");
            if(ownerOutputOptions)
            {
                GUILayout.Label("Raw-only alternatives (no analysis CSV)",recordingWrap);
                GUI.enabled=RawStartCanBeRequested(legacy,false,false);
                if(GUILayout.Button("Start recording without a local output request"))
                {if(participant.LegacyRecordingRequested)StartSelectedLegacyRecording(false);else StartSelectedNamedRecording(false);}
                GUI.enabled=RawStartCanBeRequested(legacy,true,false);
                if(GUILayout.Button("Start recording + raw output only"))
                {if(participant.LegacyRecordingRequested)StartSelectedLegacyRecording(true);else StartSelectedNamedRecording(true);}
                GUI.enabled=true;
            }
        }
    }
}
#endif
