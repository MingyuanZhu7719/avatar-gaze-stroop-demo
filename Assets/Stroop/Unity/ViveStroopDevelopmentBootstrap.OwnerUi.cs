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
        // Kept outside the scrollable setup/status body by the desktop rig.
        // These call the same authoritative safety actions as before.
        public void DrawOwnerSafetyControls()
        {
            if(!configured||participant==null)return;
            bool enabled=GUI.enabled;
            GUILayout.BeginHorizontal();
            GUI.enabled=enabled&&participant.Phase!=Phase.Ended;
            if(GUILayout.Button("Pause task",GUILayout.Height(42)))PauseTask();
            GUI.enabled=enabled&&(participant.Phase!=Phase.Ended||rawCompletionRequested);
            if(GUILayout.Button("Withdraw task",GUILayout.Height(42)))WithdrawTask();
            GUI.enabled=enabled;GUILayout.EndHorizontal();
        }
        void OwnerHeading(string text)
        {
            if(ownerHeading==null)ownerHeading=new GUIStyle(GUI.skin.label){fontStyle=FontStyle.Bold,fontSize=18,wordWrap=true};
            GUILayout.Space(8);GUILayout.Label(text,ownerHeading);
        }
        void OwnerNotice(string text)
        {
            if(ownerNotice==null)ownerNotice=new GUIStyle(GUI.skin.box){alignment=TextAnchor.MiddleLeft,wordWrap=true,padding=new RectOffset(12,12,10,10)};
            GUILayout.Label(text,ownerNotice);
        }
        string NextOwnerAction()
        {
            if(participant.Phase==Phase.Ended)return participant.RawRecordingRequested?
                "Task ended. Review recording status and output paths below; faults or incomplete output remain unconfirmed.":
                "Preview ended. No files were saved. Reopen the application to start again.";
            if(RecordingBlocked||ObservationBlocked)return "Task blocked. Review the fault below or withdraw the task.";
            if(participant.RawRecordingRequested&&namedRawPump==null)return "Next: complete source checks, then start local recording below.";
            if(!rig.ViewRunning)return "Next: start the classroom view.";
            if(!participant.InputReady)return "Next: keep one wand connected, release the trackpad and trigger, and restore headset tracking.";
            switch(participant.Phase)
            {
                case Phase.Preparation:case Phase.PracticeRetry:return "Next: select Begin practice.";
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
            int nextMode=GUILayout.SelectionGrid(ownerRunMode,new[]{"Preview (no files)","Record locally"},1);
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
                option=GUILayout.SelectionGrid(option,new[]{"Legacy A/B/C/D","Named multi-block"},1);
                selectedDesign=option==0?0:option==1?3:-1;
            }
            else selectedDesign=GUILayout.SelectionGrid(selectedDesign,new[]{"Legacy A/B/C/D","Named Automatic (one block)","Named No Avatar (one block)","Named multi-block"},1);
            if(selectedDesign!=previousDesign){controlledRecordingNoticeAccepted=false;legacyWozRecordingSelected=false;ownerPreviewWoz=false;}
            if(view!=null)view.SetViveLegacyPreviewNotice(LegacyPreviewSelected);
            if(selectedDesign<0)return;
            if(selectedDesign==1||selectedDesign==2)
            {
                GUILayout.Label("Stimulus list");
                namedListIndex=GUILayout.SelectionGrid(namedListIndex,new[]{"0","1","2","3"},4);
            }
            if(selectedDesign==3)
            {
                GUILayout.Label("Number of blocks");
                namedPlanCount=GUILayout.SelectionGrid(namedPlanCount,new[]{"1","2","3","4"},4);
                for(int i=0;i<=namedPlanCount;i++)
                {
                    GUILayout.Label("Block "+(i+1));
                    namedPlanOptions[i]=GUILayout.SelectionGrid(namedPlanOptions[i],new[]{"Automatic","No Avatar"},2);
                    GUILayout.Label("Stimulus list");
                    namedPlanLists[i]=GUILayout.SelectionGrid(namedPlanLists[i],new[]{"0","1","2","3"},4);
                }
            }
            if(selectedDesign==0)
            {
                if(local&&ViveLegacyWozRecordingBuildPolicy.Available)
                {
                    bool next=GUILayout.Toggle(legacyWozRecordingSelected,"Use a second-PC WoZ operator");
                    if(next!=legacyWozRecordingSelected){legacyWozRecordingSelected=next;controlledRecordingNoticeAccepted=false;}
                }
                else if(!local)ownerPreviewWoz=GUILayout.Toggle(ownerPreviewWoz,"Use a second-PC WoZ operator (unrecorded)");
            }
            GUILayout.Space(6);GUILayout.Label("Controller: automatic. Keep exactly one VIVE wand connected.");
            GUILayout.Label(ViveFixedTrackpadMapping.Label);
            if(local)
            {
                OwnerHeading("3  Local output");
                GUILayout.Label("Existing folder");
                recordingDirectory=GUILayout.TextField(recordingDirectory);
                if(GUILayout.Button("Use this package's LocalSelfTestData folder"))
                    recordingDirectory=Path.GetFullPath(Path.Combine(Application.dataPath,"..","..","LocalSelfTestData"));
                ownerStorageLimits=GUILayout.Toggle(ownerStorageLimits,"Advanced storage limits");
                if(ownerStorageLimits)
                {
                    GUILayout.Label("Raw maximum size (MiB, 1-8192)");
                    recordingBudgetMiB=GUILayout.TextField(recordingBudgetMiB);
                    GUILayout.Label("CSV maximum size (MiB, 1-8192)");
                    csvBudgetMiB=GUILayout.TextField(csvBudgetMiB);
                    GUILayout.Label("CSV cleanup timeout (seconds, 1-3600)");
                    csvCleanupSeconds=GUILayout.TextField(csvCleanupSeconds);
                }
                GUILayout.Label("Folder access is checked when recording starts, not now.",recordingWrap);
                GUILayout.Label("Task responses, ratings and real eye/head data stay in this folder. No upload. Eye data may identify you.",recordingWrap);
                if(legacyWozRecordingSelected)GUILayout.Label("WoZ also saves mapped operator head/eye poses locally. Participant eyes, answers and ratings never go to the operator.",recordingWrap);
                GUILayout.Label("Withdrawal stops further work, but does not delete existing files. Failures leave output unconfirmed. Not validated for study use.",recordingWrap);
                controlledRecordingNoticeAccepted=GUILayout.Toggle(controlledRecordingNoticeAccepted,"I request a local development self-test (not a study participant session).");
                long bytes;int timeout;
                bool csvValid=TryParseCsvLimits(csvBudgetMiB,csvCleanupSeconds,out bytes,out timeout);
                bool available=SelectedRecordingAvailable&&ViveRawRelativeFileCapability.ProductionGateOpen;
                GUI.enabled=!rig.ViewRunning&&ControlledRecordingNoticeCurrent&&available&&csvValid;
                if(GUILayout.Button("Continue to headset recording choices"))
                {if(selectedDesign==0)BeginLegacyRecordingSetup();else BeginNamedRecordingSetup();}
                GUI.enabled=true;
                if(!available)GUILayout.Label("Recording is unavailable in this build. No preview will start from this path.");
                else if(!ControlledRecordingNoticeCurrent)GUILayout.Label("Next: review the local-data notice and select the checkbox.");
                else if(!csvValid)GUILayout.Label("Enter valid CSV size and timeout limits.");
                OwnerNotice("Next: open the classroom view, make the two headset recording choices, then start recording here. Continuing setup creates no files.");
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
                if(GUILayout.Button("Configure preview (no recording)"))TryConfigure();
                GUI.enabled=true;
                if(rig.ViewRunning)GUILayout.Label("Stop the headset view before configuring.");
            }
        }

        void DrawRecordingStartControls(bool canStartRecording)
        {
            OwnerHeading("4  Start local recording");
            GUILayout.Label("Raw limit: "+recordingBudgetMiB+" MiB | CSV: "+csvBudgetMiB+" MiB | cleanup: "+csvCleanupSeconds+" s",recordingWrap);
            long csvBytes;int csvTimeout;
            bool csvParameters=TryParseCsvLimits(csvBudgetMiB,csvCleanupSeconds,out csvBytes,out csvTimeout);
            GUI.enabled=CanStartWithOwnerOutput(canStartRecording,ViveRawExclusiveDestination.ProductionGateOpen,true)&&
                ViveRawReadableAnalysisCsv.ProductionGateOpen&&csvParameters;
            if(GUILayout.Button("Start recording + raw and CSV output"))
            {if(participant.LegacyRecordingRequested)StartSelectedLegacyRecordingWithCsv(csvBytes,csvTimeout);else StartSelectedNamedRecordingWithCsv(csvBytes,csvTimeout);}
            GUI.enabled=true;
            if(!canStartRecording)
                GUILayout.Label(!rig.ViewRunning?"Next: start the headset view.":!rig.HeadReady?"Next: restore headset tracking and focus.":
                    !rig.RecordingWorkerReady?"Next: enable eye tracking and wait for a fresh valid sample.":
                    "Waiting for current recording choices, controller, eye tracking and focus. See the current status.",recordingWrap);
            GUILayout.Label("This explicitly requests local raw and CSV files. Completion must be checked separately; it is not proof of coverage or durable storage.",recordingWrap);
            ownerOutputOptions=GUILayout.Toggle(ownerOutputOptions,"Advanced output options");
            if(ownerOutputOptions)
            {
                GUILayout.Label("Raw-only alternatives (no analysis CSV)",recordingWrap);
                GUI.enabled=CanStartWithOwnerOutput(canStartRecording,ViveRawExclusiveDestination.ProductionGateOpen,false);
                if(GUILayout.Button("Start recording without a local output request"))
                {if(participant.LegacyRecordingRequested)StartSelectedLegacyRecording(false);else StartSelectedNamedRecording(false);}
                GUI.enabled=CanStartWithOwnerOutput(canStartRecording,ViveRawExclusiveDestination.ProductionGateOpen,true);
                if(GUILayout.Button("Start recording + raw output only"))
                {if(participant.LegacyRecordingRequested)StartSelectedLegacyRecording(true);else StartSelectedNamedRecording(true);}
                GUI.enabled=true;
            }
        }
    }
}
#endif
