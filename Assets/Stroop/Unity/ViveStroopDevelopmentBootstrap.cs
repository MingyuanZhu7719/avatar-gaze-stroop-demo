#if STROOP_XR_PREVIEW
using System;
using System.Collections.Generic;
using AvatarStroop.Core;
using AvatarStroop.Runtime;
using UnityEngine;

namespace AvatarStroop.Unity
{
    // Local, owner-operated development task. Recording is separately opt-in,
    // single-segment development evidence only; no study or live WoZ admission.
    public sealed partial class ViveStroopDevelopmentBootstrap : MonoBehaviour
    {
        [SerializeField] ViveStroopPreviewRig rig;
        [SerializeField] WorldSpaceExperimentView view;
        int ownerRunMode=-1;
        bool ownerAdvanced,ownerDiagnostics,ownerStorageLimits,ownerOutputOptions;
        bool ownerPreviewWoz;
        int selectedDesign=-1,namedListIndex=-1;
        int namedPlanCount=-1;
        readonly int[] namedPlanOptions={-1,-1,-1,-1},namedPlanLists={-1,-1,-1,-1};
        bool namedIntentV3,namedTaskRecordingChoice,namedEyeRecordingChoice;
        bool controlledRecordingNoticeAccepted;
        bool legacyWozRecordingSelected;
        ViveNamedSessionFrameAdmission legacyWozInstalledAdmission;
        ViveProEyeWorker legacyWozInstalledWorker;
        internal static bool ControlledRecordingRequestAllowed(bool controlledBuild,bool noticeAccepted)
        {return !controlledBuild||noticeAccepted;}
        bool ControlledRecordingNoticeCurrent {get{return ControlledRecordingRequestAllowed(ViveControlledRecordingBuildPolicy.Available,controlledRecordingNoticeAccepted);}}
        VivePreSessionRecordingChoice preSessionChoice;
        ViveNamedRecordingSetup recordingSetup;
        int recordingSetupDesign=-1;
        string recordingDirectory="",recordingBudgetMiB="256";
        string csvBudgetMiB="64",csvCleanupSeconds="300";
        int recordingSelectionFreshFrame=-1;
        bool rawSelectionProvenancePending;
        double rawTaskChoicePollSeconds,rawEyeChoicePollSeconds;
        int preSessionShownRevision=-1,preSessionShownFrame=-1;
        ViveNamedTaskMemoryView namedTaskView;
        string namedTaskViewIssue;
        string namedTaskPage;
        int namedTaskPageOffset,namedTaskTable;
        const int NamedTaskPageSize=8192;
        string message="Choose Preview or Record locally.";
        string avatarOutputNotice;
        string inputDiagnostic="Controller not checked in this update.";
        int inputDiagnosticFrame=-1;
        bool inputDiagnosticHeadReady,inputDiagnosticReady;
        VivePreviewControllerHand hand=VivePreviewControllerHand.Automatic;
        ViveStroopDeviceInput device;
        ViveQuestionnaireTrackpadGate questionnaireGate;
        ViveDevelopmentParticipant participant;
        ViveLegacyWozTaskOwner legacyWozTaskOwner;
        ViveDevelopmentParticipant pauseDiagnosticParticipant;
        long shownPauseRevision;
        string pauseDiagnostic;
        // This is a local software diagnostic, not a durable trial/eye record.
        void CapturePauseDiagnostic(string detail=null,long causedAfterRevision=-1,string expectedSource=null)
        {
            if(participant==null)return;
            if(!System.Object.ReferenceEquals(pauseDiagnosticParticipant,participant))
            {pauseDiagnosticParticipant=participant;shownPauseRevision=0;pauseDiagnostic=null;}
            if(participant.PauseDiagnosticRevision==shownPauseRevision)return;
            shownPauseRevision=participant.PauseDiagnosticRevision;
            // Per-operation detail is valid only if that operation caused this
            // revision. Never attribute an older unseen pause to a later call.
            bool causedHere=causedAfterRevision>=0&&participant.PauseDiagnosticRevision>causedAfterRevision&&
                String.Equals(participant.LastPauseDiagnostic,expectedSource,StringComparison.Ordinal);
            pauseDiagnostic=!causedHere||String.IsNullOrEmpty(detail)?participant.LastPauseDiagnostic:detail;
            if(String.IsNullOrEmpty(pauseDiagnostic))pauseDiagnostic="Cause unavailable; review before resuming.";
            Debug.Log("VIVE_PAUSE_CONFIRMED t="+Time.realtimeSinceStartupAsDouble.ToString("F3",System.Globalization.CultureInfo.InvariantCulture)+
                " source="+participant.LastPauseDiagnostic+" detail="+pauseDiagnostic);
        }
        void ApplyInputUnavailable(string reason)
        {
            long before=participant.PauseDiagnosticRevision;
            try{participant.UpdateTrackpad(false,new VivePreviewTrackpadSample(false,false,0,0));}
            finally{CapturePauseDiagnostic(reason,before,"Automatic safety: head/input readiness lost.");}
        }
        void ApplyInputFrame(bool headReady,ViveStroopDeviceFrame frame)
        {
            long before=participant.PauseDiagnosticRevision;
            try
            {
                if(frame.NeutralPending)participant.UpdateNeutralPending(headReady);
                else participant.UpdateTrackpad(headReady,frame.Trackpad);
            }
            finally
            {
                if(!headReady)CapturePauseDiagnostic("Automatic safety: "+(rig==null?"headset readiness unavailable.":rig.CurrentHeadDiagnostic),
                    before,"Automatic safety: head/input readiness lost.");
                else if(!frame.Trackpad.Usable)CapturePauseDiagnostic("Automatic safety: "+frame.Diagnostic,
                    before,"Automatic safety: controller sample unavailable.");
                else CapturePauseDiagnostic();
            }
        }
        MonotonicClock sharedClock;
        bool configured,faultLatched,recordingTerminal;
        bool namedRecordingSafetyReported,namedRecordingEyeStopAttempted;
        bool observationOptIn,observationFaultLatched,observationPauseRequested,suppressInputUntilReleased;
        ViveStroopRecordingController recording=null;
        // No installed pump may attach while storage/consent gates are closed.
        // Editor fixtures may inject only a synthetic pump by reflection.
        ViveNamedRawRecordingPump namedRawPump=null;
        // Present only after the owner explicitly requested a local output
        // for this recording. A reference or returned close is not a
        // publication receipt, research consent, or proof of durability.
        ViveRawExclusiveDestination namedRawDestination;
        // The path is a one-time, owner-requested local candidate only. Keep
        // the receipt (rather than a copied string) so withdrawal remains
        // observable while it is displayed. An attempted claim is never
        // silently replayed after focus loss or failure.
        ViveRawExclusiveDestination.LocalCandidateReceipt namedRawLocalCandidate;
        ViveRawExclusiveDestination.LocalCsvCandidateReceipt namedCsvLocalCandidate;
        bool namedCsvLocalCandidateAttempted,namedCsvCandidateHidden;
        string namedCsvCandidateNotice;
        ViveDevelopmentParticipant candidateParticipant;
        ViveRawRevocation candidateAuthority;
        ViveNamedRawRecordingPump candidatePump;
        ViveRawExclusiveDestination candidateDestination;
        MonotonicClock candidateClock;
        bool namedRawCandidateHidden;
        bool namedRawLocalCandidateAttempted;
        string namedRawLocalCandidateNotice;
        // This is a background cleanup watchdog, not an experiment timer or
        // a promise that a slow or in-flight system call can be cancelled.
        const int RawOutputCleanupTimeoutMilliseconds=300000;
        int rawSceneOwnerThread;
        WorldSpaceExperimentView rawCallbackView;
        bool rawInstallFailed;
        // OnGUI has no current eye-frame authority. A raw owner command is
        // therefore held until the next healthy Rig/Poll frame, never executed
        // against a cached eye epoch. Only one owner command may be pending.
        Action pendingRawOwnerAction;
        ViveDevelopmentParticipant pendingRawOwnerParticipant;
        ViveNamedSimulationPlan pendingRawOwnerPlan;
        ViveRawSessionContext pendingRawOwnerContext;
        ViveDevelopmentParticipant.NamedRecordingTaskStamp pendingRawOwnerStamp;
        ViveDevelopmentParticipant.NamedRawSafetyCause rawSafetyCause=ViveDevelopmentParticipant.NamedRawSafetyCause.TechnicalFault;
        Phase pendingRawOwnerPhase;
        ViveNamedSessionFrameAdmission.PresentationReservation pendingRawPresentation;
        int pendingRawPanelFrame=-1;long pendingRawPanelRevision=-1;
        bool rawPanelExposed;
        string committedRawTrial,rawGenericCallbackTrial;
        long committedRawRevision=-1;
        int rawGenericCallbackFrame=-1;
        // 0 idle, 1 practice, 2 main block. Only the owner thread touches
        // these fields; waiting never blocks on the background writer.
        int rawPlanStartRequested;
        ViveBlockPlanRequest rawPendingBlockPlan;
        // Safety watchdog in preparation only, not a trial or stimulus timer.
        const double RawPlanWaitLimitSeconds=30.0;
        double rawPlanDeadline=-1;
        bool rawSafetyLatched,rawWithdrawalRequested,rawPumpStopRequested,rawStatusStopAttempted,rawEyeStopRequested,rawPauseAttempted,rawWithdrawalAttempted,rawMutationIntervalUnknown;
        bool rawMaskUncertain,rawEyeStopUncertain,rawStatusStopUncertain;
        bool rawCompletionRequested;
        VivePracticePreviewCoordinator practicePreview;
        bool practiceEyeChoice,practiceTaskChoice;
        ViveSessionEvidencePanel evidencePanel;
        bool showEvidenceSummary;
        bool recordingStopConfirmed;
        GUIStyle recordingWrap;
        public bool Configured {get{return configured;}}
        internal bool LegacyDesignSelected
        {get{return ViveStroopRecordingScopePresentation.IsLegacy(selectedDesign,participant!=null,participant!=null&&participant.HasNamedOption);}}
        internal bool LegacyPreviewSelected {get{return LegacyDesignSelected&&(participant==null||!participant.LegacyRecordingRequested);}}
        internal bool LegacyRawRecordingRequested {get{return participant!=null&&participant.LegacyRecordingRequested;}}
        internal static bool LegacyRecordingAvailable
        {get{return ViveControlledRecordingBuildPolicy.Available&&ViveLegacyRawRecordingBuildPolicy.Available;}}
        bool SelectedRecordingAvailable
        {get{return ViveStroopRecordingScopePresentation.DesignAvailable(selectedDesign,ViveNamedRecordingUiAdmission.RecordingAvailable,LegacyRecordingAvailable);}}
        bool RecordingSetupDesignMatches
        {get{return recordingSetup!=null&&ViveStroopRecordingScopePresentation.SetupMatches(selectedDesign,recordingSetupDesign);}}
        bool RecordingSetupEligible
        {get{return recordingSetup!=null&&ViveStroopRecordingScopePresentation.SetupEligible(selectedDesign,recordingSetupDesign,
            SelectedRecordingAvailable,ControlledRecordingNoticeCurrent,
            recordingSetup.Current==ViveNamedRecordingSetup.Step.Eye||recordingSetup.BothSelected,recordingSetup.BothSelected);}}
        internal bool RawRecordingDesignAvailable
        {get{return participant==null?SelectedRecordingAvailable:
            participant.RawRecordingRequested&&RecordingKindAvailable(participant.LegacyRecordingRequested)&&
            (!participant.LegacyWozRecordingDeclared||ViveLegacyWozRecordingBuildPolicy.Available)&&
            MatchesRawRecordingKind(participant.LegacyRecordingRequested);}}
        static bool RecordingKindAvailable(bool legacy)
        {return legacy?LegacyRecordingAvailable:ViveNamedRecordingUiAdmission.RecordingAvailable;}
        internal bool MatchesRawRecordingKind(bool legacy)
        {return participant!=null&&ViveStroopRecordingScopePresentation.KindMatches(legacy,participant.NamedRecordingRequested,participant.LegacyRecordingRequested);}
        internal bool PreSessionChoiceActive {get{return preSessionChoice!=null||(recordingSetup!=null&&!configured);}}
        public bool RecordingEnabled {get{return recording!=null;}}
        public bool RecordingBlocked
        {
            get
            {
                if(rawInstallFailed)return true;
                var pump=namedRawPump;
                if(pump!=null)
                {
                    if(rawSafetyLatched||pump.WithdrawalRequested||pump.State!=ViveNamedRawRecordingPump.Outcome.Running)return true;
                    if(participant==null||!System.Threading.Monitor.TryEnter(participant.NamedRecordingGate))return true;
                    try{return participant.NamedRecordingAuthorityFaulted;}
                    finally{System.Threading.Monitor.Exit(participant.NamedRecordingGate);}
                }
                return recordingTerminal||(recording!=null&&recording.Faulted)||
                    (participant!=null&&participant.RawRecordingRequested&&participant.NamedRecordingAuthorityFaulted);
            }
        }
        internal bool NamedRawPumpAttached {get{return namedRawPump!=null;}}
        internal bool NamedRawInstallFailed {get{return rawInstallFailed;}}
        internal bool NamedRawTrialActive {get{return namedRawPump!=null&&participant!=null&&participant.CurrentTrial!=null&&
            (participant.Phase==Phase.Practice||participant.Phase==Phase.MainTask);}}
        internal bool NamedRawPanelPending {get{return namedRawPump!=null&&pendingRawPanelFrame>=0;}}
        // Remains true after post-render until the panel is globally hidden;
        // a second camera later in the same frame must still fail closed.
        internal bool NamedRawPanelExposed {get{return namedRawPump!=null&&rawPanelExposed;}}
        internal void MarkRawPanelHidden(){rawPanelExposed=false;}
        internal bool NamedRawInputBoundaryPending {get{return namedRawPump!=null&&suppressInputUntilReleased;}}
        internal ViveNamedRawRecordingPump.Preparation PrepareNamedRawStatus()
        {
            var pump=namedRawPump;
            if(pump==null||ServiceNamedRawSafety())return ViveNamedRawRecordingPump.Preparation.Faulted;
            var result=pump.PrepareStatus();
            if(result==ViveNamedRawRecordingPump.Preparation.Faulted||RecordingBlocked)
            {rawSafetyLatched=true;ServiceNamedRawSafety();return ViveNamedRawRecordingPump.Preparation.Faulted;}
            return result;
        }
        internal bool CanWaitForNamedRawBaseline
        {get{return namedRawPump!=null&&participant!=null&&participant.Phase==Phase.Preparation;}}
        public bool ObservationBlocked {get{return observationFaultLatched||(participant!=null&&participant.ObservationBlocked);}}
        public bool PresentationBlocked {get{return faultLatched||RecordingBlocked||ObservationBlocked||legacyWozMaskLatched;}}
        public bool RecordingUsable {get{return recording!=null&&recording.LastEyeUsable;}}
        public string RecordingStatus
        {get{var pump=namedRawPump;return pump!=null?ViveRawRecordingPresentation.Describe(rawInstallFailed,pump.WithdrawalRequested,rawSafetyLatched,pump.State):
            rawInstallFailed?ViveRawRecordingPresentation.Describe(true,rawWithdrawalRequested||
                participant!=null&&participant.NamedRawRevocation.ParticipantWithdrawalRequested,rawSafetyLatched,ViveNamedRawRecordingPump.Outcome.Faulted):
            recording==null?"Recording is off.":recording.Status;}}
        public static bool CanEnableRecording(bool eyeChoice,bool taskChoice,bool workerReady,bool terminal)
        {return false;}
        public static bool CanAcknowledgeInputFault(bool inputFault,bool recordingBlocked,Phase phase)
        {return inputFault&&!recordingBlocked&&(phase==Phase.Paused||phase==Phase.Ended);}
        // Shared instance, not merely the same label: task and eye observations
        // must use the same monotonic time base before they can be correlated.
        public MonotonicClock SharedClock {get{return sharedClock;}}
        // Explicit local task-binding only. Does not start a source, socket,
        // countdown, presentation or recorder. Named LiveWoz stays unavailable.
        public ViveLegacyWozTaskOwner CreateLegacyWozTaskBinding()
        {
            if(!isActiveAndEnabled||!configured||PresentationBlocked||participant==null||sharedClock==null||participant.HasNamedOption)
                throw new InvalidOperationException("Current active Legacy participant required.");
            if(legacyWozTaskOwner!=null&&!legacyWozTaskOwner.Terminal)
                throw new InvalidOperationException("An explicit Legacy binding is already owned.");
            legacyWozTaskOwner=participant.CreateLegacyWozTaskOwner(sharedClock);return legacyWozTaskOwner;
        }
        void CloseLegacyWozTaskBinding()
        {
            CloseLegacyWozScene();
            if(legacyWozTaskOwner==null)return;
            if(!legacyWozTaskOwner.PauseAuthoritativeTask())legacyWozTaskOwner.MarkCleanupUncertain();legacyWozTaskOwner.Dispose();legacyWozTaskOwner=null;
        }
        public string ControlsHint
        {
            get
            {
                if(participant==null)return "VIVE input unavailable.";
                if(RecordingBlocked)return "Use the desktop Withdraw task button; ask the operator to pause or stop.";
                if(participant.Phase==Phase.Questionnaire)
                    return "Trackpad UP: lower | DOWN: higher | Trigger: confirm\nSelected: "+participant.CandidateRating+" (not submitted)";
                return "Trackpad: colour | Trigger: continue / resume\nEscape on desktop: withdraw development task";
            }
        }
        public void Configure(ViveStroopPreviewRig preview,WorldSpaceExperimentView presentation)
        {
            if(Application.isPlaying)throw new InvalidOperationException("Author development wiring outside Play Mode.");
            if(preview==null||presentation==null||presentation.Role!=Actor.Participant)throw new ArgumentException("Participant rig and view required.");
            rig=preview;view=presentation;
        }
        void Awake()
        {
            rawSceneOwnerThread=System.Threading.Thread.CurrentThread.ManagedThreadId;
            if(view!=null){view.StimulusRendered+=OnStimulusRendered;view.ViveRawStimulusRendered+=OnViveRawStimulusRendered;view.ViveRawPanelRendered+=OnViveRawPanelRendered;view.ViveRawObservationRequired=IsViveRawObservationRequired;rawCallbackView=view;}
        }
        // Candidate wiring only: the caller must supply explicit software
        // selections, a directory and a budget. These selections do not
        // authenticate a participant or establish informed consent. The
        // production gates remain closed. Never reuse
        // the pre-session input rehearsal as these selections.
        internal bool TryCreateAndAttachNamedRawInstalled(string selectedDirectory,ViveRawByteBudget budget,
            bool taskSoftwareSelection,bool eyeSoftwareSelection)
        {return TryCreateAndAttachNamedRawInstalledWithOwnerOutput(selectedDirectory,budget,taskSoftwareSelection,eyeSoftwareSelection,false);}
        // A separate owner button is the only scene caller that passes true.
        // Participant task/eye choices never imply an owner output request.
        internal bool TryCreateAndAttachNamedRawInstalledWithOwnerOutput(string selectedDirectory,ViveRawByteBudget budget,
            bool taskSoftwareSelection,bool eyeSoftwareSelection,bool explicitOwnerOutputRequest)
        {return TryCreateAndAttachNamedRawInstalledWithCsv(selectedDirectory,budget,taskSoftwareSelection,eyeSoftwareSelection,
            explicitOwnerOutputRequest,false,0,0);}
        internal bool TryCreateAndAttachNamedRawInstalledWithCsv(string selectedDirectory,ViveRawByteBudget budget,
            bool taskSoftwareSelection,bool eyeSoftwareSelection,bool explicitOwnerOutputRequest,bool requestCsv,
            long csvMaximumBytes,int csvTimeoutMilliseconds)
        {return TryCreateAndAttachRawInstalled(false,selectedDirectory,budget,taskSoftwareSelection,eyeSoftwareSelection,
            explicitOwnerOutputRequest,requestCsv,csvMaximumBytes,csvTimeoutMilliseconds);}
        internal bool TryCreateAndAttachLegacyRawInstalled(string selectedDirectory,ViveRawByteBudget budget,
            bool taskSoftwareSelection,bool eyeSoftwareSelection)
        {return TryCreateAndAttachLegacyRawInstalledWithOwnerOutput(selectedDirectory,budget,taskSoftwareSelection,eyeSoftwareSelection,false);}
        internal bool TryCreateAndAttachLegacyRawInstalledWithOwnerOutput(string selectedDirectory,ViveRawByteBudget budget,
            bool taskSoftwareSelection,bool eyeSoftwareSelection,bool explicitOwnerOutputRequest)
        {return TryCreateAndAttachLegacyRawInstalledWithCsv(selectedDirectory,budget,taskSoftwareSelection,eyeSoftwareSelection,
            explicitOwnerOutputRequest,false,0,0);}
        internal bool TryCreateAndAttachLegacyRawInstalledWithCsv(string selectedDirectory,ViveRawByteBudget budget,
            bool taskSoftwareSelection,bool eyeSoftwareSelection,bool explicitOwnerOutputRequest,bool requestCsv,
            long csvMaximumBytes,int csvTimeoutMilliseconds)
        {return TryCreateAndAttachRawInstalled(true,selectedDirectory,budget,taskSoftwareSelection,eyeSoftwareSelection,
            explicitOwnerOutputRequest,requestCsv,csvMaximumBytes,csvTimeoutMilliseconds);}
        bool TryCreateAndAttachRawInstalled(bool legacy,string selectedDirectory,ViveRawByteBudget budget,
            bool taskSoftwareSelection,bool eyeSoftwareSelection,bool explicitOwnerOutputRequest,bool requestCsv,
            long csvMaximumBytes,int csvTimeoutMilliseconds)
        {
            // Check the closed gates before constructing an admission: even
            // attaching an admission changes the task's authority state.
            if(!ControlledRecordingNoticeCurrent||!RecordingKindAvailable(legacy)||!MatchesRawRecordingKind(legacy)||
                !RecordingSetupDesignMatches||recordingSetupDesign!=(legacy?0:3)||!ViveRawRelativeFileCapability.ProductionGateOpen||
                (explicitOwnerOutputRequest&&!ViveRawExclusiveDestination.ProductionGateOpen)||
                (requestCsv&&(!explicitOwnerOutputRequest||!ViveRawReadableAnalysisCsv.ProductionGateOpen||
                    csvMaximumBytes<1024||csvMaximumBytes>ViveRawByteBudget.MaximumSupportedBytes||
                    csvTimeoutMilliseconds<1||csvTimeoutMilliseconds>3600000))||
                !taskSoftwareSelection||!eyeSoftwareSelection||String.IsNullOrWhiteSpace(selectedDirectory)||budget==null||
                rawSceneOwnerThread==0||System.Threading.Thread.CurrentThread.ManagedThreadId!=rawSceneOwnerThread||
                namedRawPump!=null||recording!=null||recordingTerminal||rawInstallFailed||rawSafetyLatched||rawCompletionRequested||
                !isActiveAndEnabled||!configured||participant==null||sharedClock==null||view==null||rig==null||
                !rig.isActiveAndEnabled||!view.isActiveAndEnabled||!object.ReferenceEquals(rawCallbackView,view)||
                view.ViveRawObservationRequired!=IsViveRawObservationRequired||view.ViveRawPanelRendered!=OnViveRawPanelRendered||
                participant.Phase!=Phase.Preparation||participant.Completed||!participant.RawRecordingRequested||
                preSessionChoice!=null||practicePreview!=null||observationOptIn)return false;
            string normalizedDirectory,selectedChoiceDirectory;
            try{normalizedDirectory=ViveRawDirectoryLease.NormalizeCandidate(selectedDirectory);
                selectedChoiceDirectory=ViveRawDirectoryLease.NormalizeCandidate(recordingSetup.DirectoryPath);}
            catch(Exception error) when(!(error is OutOfMemoryException||error is StackOverflowException||error is AccessViolationException)){return false;}
            if(!String.Equals(normalizedDirectory,selectedChoiceDirectory,StringComparison.OrdinalIgnoreCase)||
                !System.Object.ReferenceEquals(budget,recordingSetup.Budget))return false;
            double taskPoll,eyePoll;
            if(recordingSetup==null||!recordingSetup.TryGetObservedTimes(sharedClock,out taskPoll,out eyePoll))return false;
            ViveProEyeWorker installed;
            if(!(legacy?rig.TryGetLegacyRawInstalledWorker(this,out installed):rig.TryGetNamedRawInstalledWorker(this,out installed))||installed==null)return false;
            ViveNamedSessionFrameAdmission admission=null;bool constructionAttempted=false;
            try
            {
                constructionAttempted=true;
                admission=legacy?ViveNamedSessionFrameAdmission.CreateLegacyInstalledCandidate(participant,installed,sharedClock,256,recordingSetup):
                    ViveNamedSessionFrameAdmission.CreateInstalledCandidate(participant,installed,sharedClock,256);
                if(admission.ConfirmTask()&&admission.ConfirmEye())
                {
                    // Pure configuration: the bound writer later opens the
                    // explicitly selected directory under its I/O fence.
                    var sink=new ViveRawSessionJournalSink(admission,normalizedDirectory,budget,true);
                    ViveRawExclusiveDestination output=null;
                    if(explicitOwnerOutputRequest)
                    {
                        bool armed=requestCsv?
                            ViveRawExclusiveDestination.TryCreateInstalledWithCsv(admission,sink,participant.NamedRawRevocation,
                                normalizedDirectory,budget.MaximumBytes,RawOutputCleanupTimeoutMilliseconds,
                                csvMaximumBytes,csvTimeoutMilliseconds,true,out output):
                            ViveRawExclusiveDestination.TryCreateInstalled(admission,sink,participant.NamedRawRevocation,
                                normalizedDirectory,budget.MaximumBytes,RawOutputCleanupTimeoutMilliseconds,true,out output);
                        if(!armed)throw new InvalidOperationException("The requested local output could not be armed.");
                    }
                    // Publish the reference before starting the writer; the
                    // destination never opens a path during this owner call.
                    namedRawDestination=output;
                    if(legacy?TryAttachLegacyRawInstalled(admission,sink):TryAttachNamedRawInstalled(admission,sink))
                    {
                        if(output!=null)
                        {
                            candidateParticipant=participant;candidateAuthority=participant.NamedRawRevocation;
                            candidatePump=namedRawPump;candidateDestination=output;candidateClock=sharedClock;
                        }
                        return true;
                    }
                }
            }
            catch(Exception error) when(!(error is OutOfMemoryException||error is StackOverflowException||error is AccessViolationException)){}
            if(constructionAttempted)
            {
                // Admission attachment is one-way: failed or interrupted
                // startup cannot be retried as if no choice had occurred.
                rawInstallFailed=true;faultLatched=true;
                rawWithdrawalRequested=participant.NamedRawRevocation.ParticipantWithdrawalRequested;
                try{if(admission!=null){if(rawWithdrawalRequested)admission.Withdraw();else admission.AbortForTechnicalFailure();}}catch(Exception){}
                try{rig.LatchRawInstallFailure();}catch(Exception){}
                try{rig.BlockTaskPresentation();}catch(Exception){}
                try{view.SetPresentationAvailable(false);view.SuppressViveRawStimulus();view.SuppressViveRawPanel();}catch(Exception){}
                message="Recording start failed; task remains blocked. Eye cleanup and any in-flight work are unconfirmed.";
            }
            return false;
        }
        // Scene-owned installation endpoint. The caller must supply an already
        // selected admission and sink; this method never selects a directory,
        // creates consent, opens a file, or turns on any gate. A failure is not
        // permission to reuse an admission whose startup was attempted.
        internal bool TryAttachNamedRawInstalled(ViveNamedSessionFrameAdmission admission,ViveRawSessionJournalSink sink)
        {return TryAttachRawInstalled(false,admission,sink);}
        internal bool TryAttachLegacyRawInstalled(ViveNamedSessionFrameAdmission admission,ViveRawSessionJournalSink sink)
        {return TryAttachRawInstalled(true,admission,sink);}
        bool TryAttachRawInstalled(bool legacy,ViveNamedSessionFrameAdmission admission,ViveRawSessionJournalSink sink)
        {
            if(!ControlledRecordingNoticeCurrent||!RecordingKindAvailable(legacy)||!MatchesRawRecordingKind(legacy)||
                !RecordingSetupDesignMatches||recordingSetupDesign!=(legacy?0:3)||!ViveRawRelativeFileCapability.ProductionGateOpen||
                admission==null||admission.IsLegacy!=legacy||sink==null||admission.SyntheticOnly||!sink.ProductionGateOpen||!sink.CompletionGateOpen||
                !sink.BelongsTo(admission)||rawSceneOwnerThread==0||
                System.Threading.Thread.CurrentThread.ManagedThreadId!=rawSceneOwnerThread||
                namedRawPump!=null||recording!=null||recordingTerminal||rawInstallFailed||rawSafetyLatched||rawCompletionRequested||
                !isActiveAndEnabled||!configured||participant==null||sharedClock==null||view==null||rig==null||
                !rig.isActiveAndEnabled||!view.isActiveAndEnabled||!object.ReferenceEquals(rawCallbackView,view)||
                view.ViveRawObservationRequired!=IsViveRawObservationRequired||view.ViveRawPanelRendered!=OnViveRawPanelRendered||
                participant.Phase!=Phase.Preparation||participant.Completed||!participant.RawRecordingRequested||
                preSessionChoice!=null||practicePreview!=null||observationOptIn)return false;
            double taskPoll,eyePoll;
            if(recordingSetup==null||!recordingSetup.TryGetObservedTimes(sharedClock,out taskPoll,out eyePoll))return false;
            if(legacy&&!admission.MatchesLegacyObservedChoices(sharedClock,taskPoll,eyePoll))return false;
            ViveProEyeWorker installed;
            if(!(legacy?rig.TryGetLegacyRawInstalledWorker(this,out installed):rig.TryGetNamedRawInstalledWorker(this,out installed))||installed==null||
                !admission.UsesExactSources(participant,installed,sharedClock))return false;
            ViveNamedRawRecordingPump pump=null;bool started=false;
            try{started=(legacy?ViveNamedRawRecordingPump.TryStartLegacyInstalled(participant,installed,sharedClock,admission,sink,out pump):
                ViveNamedRawRecordingPump.TryStartInstalled(participant,installed,sharedClock,admission,sink,out pump))&&pump!=null;}
            catch(Exception error) when(!(error is OutOfMemoryException||error is StackOverflowException||error is AccessViolationException)){}
            // This is a point-in-time check, not an atomic promise that a
            // later concurrent withdrawal cannot revoke the attached pump.
            if(started&&!ViveNamedRawRecordingPump.IsAvailableForSceneInstall(pump))started=false;
            if(!started)
            {
                // Startup may have bound queue/status authority before failing.
                // Revoke it, forbid retries and keep presentation masked; an
                // already reserved operation or worker cleanup may be unknown.
                rawInstallFailed=true;faultLatched=true;rig.LatchRawInstallFailure();
                rawWithdrawalRequested=participant.NamedRawRevocation.ParticipantWithdrawalRequested||pump!=null&&pump.WithdrawalRequested;
                try{if(rawWithdrawalRequested)admission.Withdraw();else admission.AbortForTechnicalFailure();}catch(Exception){}
                try{if(pump!=null)pump.Stop();}catch(Exception){}
                try{rig.BlockTaskPresentation();}catch(Exception){}
                try{view.SetPresentationAvailable(false);}catch(Exception){}
                try{view.SuppressViveRawStimulus();}catch(Exception){}
                try{view.SuppressViveRawPanel();}catch(Exception){}
                message="Recording start failed; task remains blocked. Eye cleanup and any in-flight work are unconfirmed.";
                return false;
            }
            rawTaskChoicePollSeconds=taskPoll;rawEyeChoicePollSeconds=eyePoll;
            rawSelectionProvenancePending=true;namedRawPump=pump;
            if(participant.LegacyWozRecordingDeclared){legacyWozInstalledAdmission=admission;legacyWozInstalledWorker=installed;}
            return true;
        }
        bool IsViveRawObservationRequired(){return namedRawPump!=null;}
        public void DrawOwnerControls()
        {
            GUILayout.Space(8);
            if(recordingWrap==null)recordingWrap=new GUIStyle(GUI.skin.label){wordWrap=true};
            GUILayout.Label(participant!=null&&participant.RawRecordingRequested?"PARTICIPANT  |  Local recording":
                configured?"PARTICIPANT  |  Preview - no files":"PARTICIPANT  |  Setup");
            if(configured)GUILayout.Label(RecordingStatus,recordingWrap);
            if(configured||preSessionChoice!=null||recordingSetup!=null)rig.DrawSessionViewControls();
            if(preSessionChoice!=null)
            {
                GUILayout.Label("Input rehearsal: "+preSessionChoice.Current+". No session or recording has started.");
                GUILayout.Label("This rehearsal is not consent and does not authorize recording.");
                GUI.enabled=!rig.ViewRunning;
                if(GUILayout.Button("Clear pre-session choice")){preSessionChoice.Cancel();preSessionChoice=null;preSessionShownRevision=-1;message="Recording choice cleared.";}
                GUI.enabled=true;return;
            }
            if(recordingSetup!=null&&!configured)
            {
                GUILayout.Label((recordingSetupDesign==0?"Legacy A/B/C/D":"Named multi-block")+" recording selection: "+recordingSetup.Current+". Recording has not started.");
                GUILayout.Label("The participant must respond in the headset. Study consent is separate.");
                if(GUILayout.Button("Cancel recording setup")){recordingSetup.Cancel();recordingSetup=null;recordingSetupDesign=-1;preSessionShownRevision=-1;message="Recording setup cancelled; no recording was started.";if(view!=null){view.SetViveLegacyPreviewNotice(LegacyPreviewSelected);view.SetViveUnconfiguredPreview(message);}}
                return;
            }
            if(!configured)
            {
                DrawSetupControls();
            }
            else
            {
                string design=participant.NamedPlan!=null?participant.NamedPlan.Blocks.Count+" planned blocks":participant.HasNamedOption?
                    (participant.NamedOption.Option==ViveNamedOption.NoAvatar?"No Avatar | 1 block":"Automatic | 1 block"):
                    "Legacy A/B/C/D | 4 blocks";
                OwnerHeading("Task status");
                GUILayout.Label(design+"  |  "+participant.Phase,recordingWrap);
                GUILayout.Label("Controller: "+ControllerUiState,recordingWrap);
                OwnerNotice(NextOwnerAction());
                CapturePauseDiagnostic();
                if(pauseDiagnostic!=null)
                {
                    if(recordingWrap==null)recordingWrap=new GUIStyle(GUI.skin.label){wordWrap=true};
                    if(participant.Phase==Phase.Paused||ownerDiagnostics)GUILayout.Label("Pause: "+pauseDiagnostic,recordingWrap);
                }
                if(participant.Phase==Phase.Ended)
                {
                    GUILayout.Label("Ended: "+(participant.EndReason.HasValue?participant.EndReason.Value.ToString():"reason unavailable")+". Reopen the application for a new task.",recordingWrap);
                }
                if(avatarOutputNotice!=null)
                {
                    if(recordingWrap==null)recordingWrap=new GUIStyle(GUI.skin.label){wordWrap=true};
                    GUILayout.Label(avatarOutputNotice,recordingWrap);
                }
                if(!ControllerUiReady)
                {
                    if(recordingWrap==null)recordingWrap=new GUIStyle(GUI.skin.label){wordWrap=true};
                    GUILayout.Label(ControllerUiDiagnostic,recordingWrap);
                }
                if(ownerAdvanced&&participant.NamedPlan!=null)
                    foreach(var block in participant.NamedPlan.Blocks)
                        GUILayout.Label("Planned block "+(block.Index+1)+": "+block.Selection.Option+", list "+block.Selection.ListIndex);
                DrawRecordingControls();
                if(recordingSetup!=null&&participant.RawRecordingRequested&&namedRawPump==null)
                {
                    GUILayout.Label("Recording is off. Review the folder, then start recording explicitly.",recordingWrap);
                    GUILayout.Label("Selected local folder: "+recordingSetup.DirectoryPath,recordingWrap);
                    bool canStartRecording=CanStartSelectedRawRecording(participant.LegacyRecordingRequested);
                    DrawRecordingStartControls(canStartRecording);
                }
                if(ownerAdvanced)DrawPracticePreviewControls();
                GUILayout.Space(6);
                GUI.enabled=(!participant.RawRecordingRequested||namedRawPump!=null)&&!RecordingBlocked&&!ObservationBlocked&&participant.InputReady&&(participant.Phase==Phase.Preparation||participant.Phase==Phase.PracticeRetry);
                if((participant.Phase==Phase.Preparation||participant.Phase==Phase.PracticeRetry)&&GUILayout.Button("Begin practice"))
                {if(namedRawPump!=null)RequestNamedBlockPlanStart(true);else Run(()=>{participant.OwnerBeginPractice(Actor.Owner);if(practicePreview!=null)practicePreview.OnPracticeStarted();});}
                GUI.enabled=!participant.LegacyWozRecordingDeclared&&!RecordingBlocked&&!ObservationBlocked&&participant.InputReady&&participant.Phase==Phase.BlockPreparation&&pendingRawOwnerAction==null;
                bool noAvatar=participant.HasNamedOption&&participant.NamedPlan==null&&participant.NamedOption.Option==ViveNamedOption.NoAvatar;
                if(participant.Phase==Phase.BlockPreparation&&!participant.LegacyWozRecordingDeclared&&!ownerPreviewWoz&&GUILayout.Button(participant.NamedPlan!=null?"Confirm complete plan":noAvatar?"Confirm No Avatar":"Confirm Automatic"))
                    Run(()=>{if(participant.NamedPlan!=null)participant.OwnerConfirmNamedPlan(Actor.Owner,"local-development-owner");else if(noAvatar)participant.OwnerConfirmNamedNoAvatar(Actor.Owner,"local-development-owner");else participant.OwnerLockSource(Actor.Owner,ControlSelection.Automatic,"local-development-owner");});
                GUI.enabled=!RecordingBlocked&&!ObservationBlocked&&participant.InputReady&&participant.Phase==Phase.BlockReady&&participant.BlockIndex==0&&participant.LockedControl!=ControlSelection.LiveWoz;
                if(participant.Phase==Phase.BlockReady&&participant.BlockIndex==0&&participant.LockedControl!=ControlSelection.LiveWoz&&GUILayout.Button("Begin first block"))
                {if(namedRawPump!=null)RequestNamedBlockPlanStart(false);else Run(()=>participant.OwnerBeginBlock(Actor.Owner));}
                GUI.enabled=true;
                if(participant.LegacyWozRecordingDeclared||ownerPreviewWoz)DrawLegacyWozControls();
                if(participant.ObservationEnabled)
                    GUILayout.Label(observationFaultLatched?"Development observation failed. Presentation is blocked; review or withdraw the task.":
                        participant.ObservationPending?"Development observation is catching up. Presentation is paused until current.":
                        "Bounded in-memory development observation is current; it is not a durable log or study authorization.");
                if(CanAcknowledgeInputFault(faultLatched,RecordingBlocked,participant.Phase)&&
                    GUILayout.Button("Owner: acknowledge input fault after review"))
                {faultLatched=false;view.SetFaults(new string[0]);message="Fault acknowledged. A healthy input and an explicit resume are still required.";}
                bool advanced=GUILayout.Toggle(ownerAdvanced,"Advanced task tools");
                if(ownerAdvanced&&!advanced)ClearNamedTaskView();
                ownerAdvanced=advanced;
                if(ownerAdvanced&&participant.HasNamedOption)
                {
                    if(namedRawPump==null&&participant.Phase==Phase.Ended&&!rig.ViewRunning&&!participant.NamedViewWithdrawalRequested)
                    {
                        GUILayout.Label("Named task view contains session/clock IDs, trial events and questionnaire responses. It is a one-time in-memory snapshot, not an export file.",recordingWrap);
                        GUILayout.Label("This Simulation task view does not prove full-session coverage, eye tracking or actual avatar presentation. Planned blocks do not prove they started. UTF-8 fields use b64: encoding.",recordingWrap);
                        if(namedTaskView==null&&GUILayout.Button("Create named task event table in memory"))
                            ViveNamedTaskMemoryView.TryCreate(participant,true,rig.ViewRunning,out namedTaskView,out namedTaskViewIssue);
                        if(namedTaskView!=null)
                        {
                            int table=GUILayout.SelectionGrid(namedTaskTable,new[]{"Task events","Trial and rating records"},2);
                            if(table!=namedTaskTable){namedTaskTable=table;namedTaskPage=null;namedTaskPageOffset=0;}
                            string csv=namedTaskTable==0?namedTaskView.Csv:namedTaskView.RecordsCsv;
                            if(csv==null)ClearNamedTaskView();
                            else
                            {
                                GUILayout.Label("Events: "+namedTaskView.EventCount+" | trial-related events: "+namedTaskView.TrialEventCount+" | rating events: "+namedTaskView.RatingEventCount,recordingWrap);
                                GUILayout.Label("Actual task records in snapshot: "+namedTaskView.TrialRecordCount+" trials and "+namedTaskView.RatingRecordCount+" ratings. Ratings carry block index but no independently recorded condition.",recordingWrap);
                                if(namedTaskPage==null)SetNamedTaskPage(csv,0);
                                GUILayout.Label("Showing characters "+(namedTaskPageOffset+1)+"-"+(namedTaskPageOffset+namedTaskPage.Length)+" of "+csv.Length+"; rows may cross page boundaries.",recordingWrap);
                                GUILayout.TextArea(namedTaskPage,GUILayout.Height(180));
                                GUILayout.BeginHorizontal();GUI.enabled=namedTaskPageOffset>0;
                                if(GUILayout.Button("Previous page"))SetNamedTaskPage(csv,Math.Max(0,namedTaskPageOffset-NamedTaskPageSize));
                                GUI.enabled=namedTaskPageOffset+namedTaskPage.Length<csv.Length;
                                if(GUILayout.Button("Next page"))SetNamedTaskPage(csv,namedTaskPageOffset+namedTaskPage.Length);
                                GUI.enabled=true;GUILayout.EndHorizontal();
                                if(GUILayout.Button("Hide and clear named task view"))ClearNamedTaskView();
                            }
                        }
                        if(namedTaskViewIssue!=null)GUILayout.Label("Task view unavailable: "+namedTaskViewIssue,recordingWrap);
                        GUILayout.Label("Hiding or leaving clears this view reference; copied content cannot be recalled and managed memory cannot be guaranteed erased.",recordingWrap);
                    }
                    else {ClearNamedTaskView();GUILayout.Label(namedRawPump!=null?
                        "The named task table is unavailable while a recording session is attached.":
                        "The named task view requires an ended, non-withdrawn task and a stopped classroom view.");}
                }
            }
            // Synchronous, bounded journal reading must never block an active
            // participant or a recorder that is still being closed.
            bool canReview=namedRawPump==null&&ViveSessionEvidenceController.CanReview(rig.ViewRunning,participant!=null,
                participant!=null&&participant.Phase==Phase.Ended,recording!=null,recordingStopConfirmed,
                recording!=null&&recording.SegmentOpen);
            GUILayout.Space(8);
            ownerDiagnostics=GUILayout.Toggle(ownerDiagnostics,"Diagnostics and existing journal review");
            if(ownerDiagnostics)
            {
                GUILayout.Label(rig.Status,recordingWrap);
                GUILayout.Label(rig.CurrentHeadDiagnostic,recordingWrap);
                if(device!=null&&device.DiagnosticSnapshotFrame>=0)
                {
                    GUILayout.Label("Controller routing check: frame "+device.DiagnosticSnapshotFrame+
                        (device.DiagnosticSnapshotFrame==Time.frameCount?" (current).":" (previous; not a live connection check)."),recordingWrap);
                    GUILayout.Label(device.LogicalDeviceDiagnostic,recordingWrap);
                }
            }
            if(canReview&&ownerDiagnostics)
            {
                if(LegacyPreviewSelected)GUILayout.Label("This legacy preview created no session files. Journal review opens separately selected existing files, not these unsaved task records.",recordingWrap);
                showEvidenceSummary=GUILayout.Toggle(showEvidenceSummary,"Review selected development journals (read-only)");
                if(showEvidenceSummary)
                {
                    if(evidencePanel==null)evidencePanel=new ViveSessionEvidencePanel();
                    evidencePanel.Draw();
                }
                else ClearJournalEvidenceSummary();
            }
            else
            {
                ClearJournalEvidenceSummary();showEvidenceSummary=false;
                if(ownerDiagnostics)GUILayout.Label(LegacyPreviewSelected?"Legacy results are memory-only until close; recording and file export are unavailable.":
                    namedRawPump!=null?"Recorded-session review is not available in this build.":
                    "Journal review is available after the classroom view and task have ended and recording has stopped.");
            }
            if(!String.IsNullOrEmpty(message))GUILayout.Label(message,recordingWrap);
        }
        void DrawPracticePreviewControls()
        {
            if(participant!=null&&participant.RawRecordingRequested)return;
            if(recordingWrap==null)recordingWrap=new GUIStyle(GUI.skin.label){wordWrap=true};
            GUILayout.Label("Optional practice observation rehearsal: local status only, no gaze or task data is saved. These choices do not establish research consent or validate the device.",recordingWrap);
            if(!participant.ObservationEnabled)
            {GUILayout.Label(participant.HasNamedOption?"Practice observation rehearsal is unavailable for this named preview.":
                "Enable bounded in-memory development observation before configuration to use this rehearsal.",recordingWrap);return;}
            bool beforeStart=(participant.Phase==Phase.Preparation||participant.Phase==Phase.Familiarization)&&!ObservationBlocked;
            bool canChoose=beforeStart&&(practicePreview==null||practicePreview.CanChoose);
            GUI.enabled=canChoose&&!practiceEyeChoice;
            if(GUILayout.Button("Choose practice eye observation (no saving)"))ChoosePracticeObservation(true);
            GUI.enabled=canChoose&&!practiceTaskChoice;
            if(GUILayout.Button("Choose practice task observation (no saving)"))ChoosePracticeObservation(false);
            GUI.enabled=true;
            if(practicePreview!=null)
            {
                GUILayout.Label(practicePreview.Status,recordingWrap);
                GUILayout.Label("Fresh eye/head candidates observed: "+practicePreview.AcceptedFrameCount+" / "+practicePreview.HeadValidFrameCount+". These counts do not establish tracking accuracy or complete coverage.",recordingWrap);
                if(practicePreview.UnknownIntervals>0)GUILayout.Label("Interrupted intervals with unknown coverage: "+practicePreview.UnknownIntervals+".",recordingWrap);
                if(practicePreview.CanResumeObservation)
                {
                    GUILayout.Label("When the task is in practice and the eye source is ready, explicitly start a new observation epoch. The intervening interval remains unknown; nothing is saved.",recordingWrap);
                    GUI.enabled=rig.RecordingWorkerReady&&!ObservationBlocked&&
                        (participant.Phase==Phase.Practice||participant.Phase==Phase.PracticeCorrection);
                    if(GUILayout.Button("Resume practice observation (no saving)"))practicePreview.ResumeObservation();
                    GUI.enabled=true;
                }
                if(GUILayout.Button("Withdraw practice observation choices"))practicePreview.Withdraw();
                GUILayout.Label("Withdrawing these choices does not stop separately enabled eye diagnostics; use Stop eye preview to stop them.",recordingWrap);
            }
        }
        void ChoosePracticeObservation(bool eye)
        {
            try
            {
                if(practicePreview==null)practicePreview=new VivePracticePreviewCoordinator(participant,sharedClock);
                bool accepted=eye?practicePreview.ConfirmEye():practicePreview.ConfirmTask();
                if(accepted){if(eye)practiceEyeChoice=true;else practiceTaskChoice=true;}
            }
            catch(Exception){message="Practice observation choices unavailable; no practice data is saved.";}
        }
        internal void ObservePracticeFrame(ViveProEyePoll frame)
        {if(practicePreview!=null&&practicePreview.CanObserve)practicePreview.ObserveFrame(frame);}
        internal void StopPracticeObservation()
        {if(practicePreview!=null)practicePreview.Withdraw();}
        internal void SuspendPracticeObservation()
        {if(practicePreview!=null)practicePreview.SuspendUnavailable();}
        void SetNamedTaskPage(string csv,int offset)
        {namedTaskPageOffset=offset;namedTaskPage=ViveNamedTaskMemoryView.SliceForDisplay(csv,offset,NamedTaskPageSize);}
        void ClearNamedTaskView(){namedTaskView=null;namedTaskViewIssue=null;namedTaskPage=null;namedTaskPageOffset=0;namedTaskTable=0;}
        // Journal review and the explicitly requested task-memory table are
        // independent owner actions. Only privacy/safety clears hide both.
        void ClearJournalEvidenceSummary(){if(evidencePanel!=null)evidencePanel.ClearSensitive();}
        void ClearSensitiveEvidence(){ClearJournalEvidenceSummary();ClearNamedTaskView();}
        internal void BeforeViewStart(){ClearSensitiveEvidence();ClearNamedRawLocalCandidate();showEvidenceSummary=false;}
        void OnApplicationFocus(bool focused)
        {
            if(focused)return;
            if(legacyWozStarted)CloseLegacyWozScene();else legacyWozOffer="";
            // Only a safely closed output session survives focus loss. Review
            // still requires an explicit one-time owner request after focus returns.
            bool retain=NamedRawCandidateContextIsSafe();
            if(namedRawPump!=null&&!retain)AbortRawScenePresentation();
            ClearSensitiveEvidence();
            if(!retain)ClearNamedRawLocalCandidate();
            if(device!=null)device.ResetSafetyMenu();
            if(preSessionChoice!=null)preSessionChoice.Cancel();
            if(recordingSetup!=null)recordingSetup.Cancel();
        }
        void OnDisable(){CloseLegacyWozTaskBinding();if(namedRawPump!=null)AbortRawScenePresentation();ClearSensitiveEvidence();ClearNamedRawLocalCandidate();StopPracticeObservation();if(preSessionChoice!=null)preSessionChoice.Cancel();if(recordingSetup!=null)recordingSetup.Cancel();}
        void DrawRecordingControls()
        {
            if(recordingWrap==null)recordingWrap=new GUIStyle(GUI.skin.label){wordWrap=true};
            if(namedRawPump!=null||rawInstallFailed)
            {
                OwnerHeading("Local output");
                if(namedRawDestination!=null)
                {
                    GUILayout.Label("File writer: "+namedRawDestination.State,recordingWrap);
                    if(recordingSetup!=null)GUILayout.Label("Folder: "+recordingSetup.DirectoryPath,recordingWrap);
                    GUILayout.Label("After completion, review raw and CSV file paths below. Unconfirmed output is not a successful save.",recordingWrap);
                    if(ownerDiagnostics)GUILayout.Label("Software status does not establish consent, tracking accuracy, coverage or durable storage. Withdrawal cannot recall existing bytes.",recordingWrap);
                    DrawNamedRawLocalCandidate();
                    DrawNamedCsvLocalCandidate();
                }
                return;
            }
            if(participant!=null&&participant.RawRecordingRequested)
            {GUILayout.Label("Recording is off. Complete source checks, then explicitly start recording. Practice is blocked until then.",recordingWrap);return;}
            if(participant!=null&&participant.HasNamedOption)
            {GUILayout.Label("Preview only: no task, eye or CSV files are saved.",recordingWrap);return;}
            if(LegacyRawRecordingRequested)
            {GUILayout.Label("Legacy self-test selected. Recording has not started; practice remains blocked until the explicit recording start.",recordingWrap);return;}
            GUILayout.Label("Preview only: no task, eye or CSV files are saved.",recordingWrap);
            if(recording!=null)
            {
                GUILayout.Label(recording.Status,recordingWrap);
                if(recording.RecoveryPending&&recording.LastEyeUsable&&!recording.Faulted&&GUILayout.Button("Acknowledge recovered eye source"))
                    recording.AcknowledgeRecoveredSource();
            }
            if(RecordingBlocked)GUILayout.Label("Recording stopped or faulted. Presentation and restart are blocked in this window; incomplete records remain in the selected folder.",recordingWrap);
        }
        // Eligibility is deliberately separate from the one-shot claim;
        // the destination performs the authoritative same-session checks.
        internal static bool CanRequestNamedRawLocalCandidate(bool outputGate,bool explicitOwnerReview,bool attempted,
            bool withdrawn,bool sourceClosed,bool destinationClosed)
        {return outputGate&&explicitOwnerReview&&!attempted&&!withdrawn&&sourceClosed&&destinationClosed;}
        bool TryRequestNamedRawLocalCandidate(bool explicitOwnerReview)
        {
            var destination=namedRawDestination;var pump=namedRawPump;var task=participant;var clock=sharedClock;
            var authority=task==null?null:task.NamedRawRevocation;
            bool withdrawn=rawWithdrawalRequested||rawSafetyLatched||rawInstallFailed||
                participant==null||participant.NamedRawRevocation.ParticipantWithdrawalRequested||
                pump==null||pump.WithdrawalRequested;
            bool sourceClosed=pump!=null&&pump.State==ViveNamedRawRecordingPump.Outcome.CloseCallsReturned;
            bool targetClosed=destination!=null&&destination.State==ViveRawExclusiveDestination.Outcome.CloseReturned;
            if(!Application.isFocused||!CanRequestNamedRawLocalCandidate(ViveRawExclusiveDestination.ProductionGateOpen,
                explicitOwnerReview,namedRawLocalCandidateAttempted,withdrawn,sourceClosed,targetClosed)||
                !NamedRawCandidateContextIsSafe())return false;
            namedRawLocalCandidateAttempted=true;
            ViveRawExclusiveDestination.LocalCandidateReceipt receipt;
            if(!destination.TryGetInstalledLocalCandidate(true,out receipt)||receipt==null||!receipt.WithdrawalNotObserved||
                String.IsNullOrEmpty(receipt.UnverifiedPath)||rawWithdrawalRequested||rawSafetyLatched||pump.WithdrawalRequested)
            {
                namedRawLocalCandidate=null;
                namedRawLocalCandidateNotice="Local candidate unavailable or unconfirmed. No path was accepted; this one-time request cannot be replayed.";
                return false;
            }
            namedRawLocalCandidate=receipt;
            candidateParticipant=task;candidateAuthority=authority;candidatePump=pump;
            candidateDestination=destination;candidateClock=clock;
            if(!NamedRawCandidateContextIsSafe())
            {
                ClearNamedRawLocalCandidate();
                namedRawLocalCandidateNotice="Local candidate context changed; this one-time request cannot be replayed.";
                return false;
            }
            namedRawLocalCandidateNotice=null;
            return true;
        }
        void DrawNamedRawLocalCandidate()
        {
            var receipt=namedRawLocalCandidate;
            if(receipt!=null&&!NamedRawCandidateContextIsSafe())
            {ClearNamedRawLocalCandidate();receipt=null;}
            // Hide while unfocused; recheck the original receipt and session
            // on every draw without replaying the one-shot claim.
            if(!Application.isFocused)return;
            if(receipt!=null&&namedRawCandidateHidden)
            {
                if(GUILayout.Button("Show raw candidate path"))namedRawCandidateHidden=false;
                return;
            }
            if(receipt!=null)
            {
                GUILayout.Label(receipt.ReopenedIdentityVerified?
                    "Raw candidate: identity matched at the post-close check; a future path lookup remains unverified:":
                    "Raw candidate: post-close identity is unconfirmed:",recordingWrap);
                GUILayout.TextArea(receipt.UnverifiedPath,GUILayout.Height(45));
                GUILayout.Label("This path is not proof of current authorization, complete coverage, publication or durable storage. Withdrawal cannot recall a path or bytes already shared.",recordingWrap);
                if(GUILayout.Button("Hide raw candidate path"))namedRawCandidateHidden=true;
                return;
            }
            if(!String.IsNullOrEmpty(namedRawLocalCandidateNotice))GUILayout.Label(namedRawLocalCandidateNotice,recordingWrap);
            bool withdrawn=rawWithdrawalRequested||rawSafetyLatched||rawInstallFailed||participant==null||
                participant.NamedRawRevocation.ParticipantWithdrawalRequested||namedRawPump==null||namedRawPump.WithdrawalRequested;
            bool sourceClosed=namedRawPump!=null&&namedRawPump.State==ViveNamedRawRecordingPump.Outcome.CloseCallsReturned;
            bool targetClosed=namedRawDestination!=null&&namedRawDestination.State==ViveRawExclusiveDestination.Outcome.CloseReturned;
            GUI.enabled=Application.isFocused&&CanRequestNamedRawLocalCandidate(ViveRawExclusiveDestination.ProductionGateOpen,
                true,namedRawLocalCandidateAttempted,withdrawn,sourceClosed,targetClosed)&&NamedRawCandidateContextIsSafe();
            if(GUILayout.Button("Review raw candidate path (one attempt)"))TryRequestNamedRawLocalCandidate(true);
            GUI.enabled=true;
            if(!ViveRawExclusiveDestination.ProductionGateOpen)
                GUILayout.Label("Local candidate review is unavailable until storage and withdrawal checks are complete.",recordingWrap);
        }
        internal static string CsvCandidateState(bool requested,bool attempted,bool hasReceipt,bool unknown)
        {
            if(!requested)return "CSV not requested.";
            if(unknown)return "CSV outcome UNKNOWN; no candidate path is available.";
            if(hasReceipt)return "CSV candidate path obtained; future path identity and durability remain unverified.";
            if(attempted)return "CSV outcome UNKNOWN; no candidate path is available.";
            return "CSV requested; completion remains unconfirmed until independently reviewed.";
        }
        bool TryRequestNamedCsvLocalCandidate(bool explicitOwnerReview)
        {
            var destination=namedRawDestination;var pump=namedRawPump;
            bool ready=destination!=null&&destination.CsvRequested&&pump!=null&&NamedRawCandidateContextIsSafe();
            if(!Application.isFocused||!CanRequestNamedRawLocalCandidate(ViveRawExclusiveDestination.ProductionGateOpen&&
                ViveRawReadableAnalysisCsv.ProductionGateOpen,explicitOwnerReview,namedCsvLocalCandidateAttempted,!ready,ready,ready))return false;
            namedCsvLocalCandidateAttempted=true;
            ViveRawExclusiveDestination.LocalCsvCandidateReceipt receipt;
            if(!destination.TryGetInstalledLocalCsvCandidate(true,out receipt)||receipt==null||!receipt.WithdrawalNotObserved||
                String.IsNullOrEmpty(receipt.UnverifiedPath)||!NamedRawCandidateContextIsSafe())
            {
                namedCsvLocalCandidate=null;
                namedCsvCandidateNotice="CSV candidate unavailable or unconfirmed; this one-time request cannot be replayed.";
                return false;
            }
            namedCsvLocalCandidate=receipt;
            if(!NamedRawCandidateContextIsSafe())
            {
                ClearNamedRawLocalCandidate();
                namedCsvCandidateNotice="CSV candidate context changed; this one-time request cannot be replayed.";
                return false;
            }
            namedCsvCandidateNotice=null;return true;
        }
        void DrawNamedCsvLocalCandidate()
        {
            var destination=namedRawDestination;var receipt=namedCsvLocalCandidate;
            if(receipt!=null&&!NamedRawCandidateContextIsSafe())
            {ClearNamedRawLocalCandidate();receipt=null;}
            if(!Application.isFocused)return;
            bool requested=destination!=null&&destination.CsvRequested;
            bool unknown=destination!=null&&destination.State==ViveRawExclusiveDestination.Outcome.Unknown;
            GUILayout.Label(CsvCandidateState(requested,namedCsvLocalCandidateAttempted,receipt!=null,unknown),recordingWrap);
            if(!requested)return;
            if(receipt!=null&&namedCsvCandidateHidden)
            {if(GUILayout.Button("Show CSV candidate path"))namedCsvCandidateHidden=false;return;}
            if(receipt!=null)
            {
                GUILayout.Label(receipt.ReopenedIdentityVerified?
                    "CSV candidate: identity matched at the post-close check; a future path lookup remains unverified:":
                    "CSV candidate: post-close identity is unconfirmed:",recordingWrap);
                GUILayout.TextArea(receipt.UnverifiedPath,GUILayout.Height(45));
                GUILayout.Label("The CSV path does not prove consent, complete coverage or durable storage. Withdrawal cannot recall a path or bytes already shared.",recordingWrap);
                if(GUILayout.Button("Hide CSV candidate path"))namedCsvCandidateHidden=true;
                return;
            }
            if(!String.IsNullOrEmpty(namedCsvCandidateNotice))GUILayout.Label(namedCsvCandidateNotice,recordingWrap);
            bool ready=NamedRawCandidateContextIsSafe();
            GUI.enabled=CanRequestNamedRawLocalCandidate(ViveRawExclusiveDestination.ProductionGateOpen&&
                ViveRawReadableAnalysisCsv.ProductionGateOpen,true,namedCsvLocalCandidateAttempted,!ready,ready,ready);
            if(GUILayout.Button("Review CSV candidate path (one attempt)"))TryRequestNamedCsvLocalCandidate(true);
            GUI.enabled=true;
        }
        internal static bool SameCandidateContext(object savedTask,object task,object savedAuthority,object authority,
            object savedPump,object pump,object savedDestination,object destination,object savedClock,object clock,
            bool completedAndClosed,bool presentationIdle,bool safe)
        {
            return savedTask!=null&&savedAuthority!=null&&savedPump!=null&&savedDestination!=null&&savedClock!=null&&
                object.ReferenceEquals(savedTask,task)&&object.ReferenceEquals(savedAuthority,authority)&&
                object.ReferenceEquals(savedPump,pump)&&object.ReferenceEquals(savedDestination,destination)&&
                object.ReferenceEquals(savedClock,clock)&&completedAndClosed&&presentationIdle&&safe;
        }
        bool NamedRawCandidateContextIsSafe()
        {
            var receipt=namedRawLocalCandidate;var task=participant;var pump=namedRawPump;var destination=namedRawDestination;
            if(task==null||pump==null||destination==null)return false;
            var authority=task.NamedRawRevocation;
            return SameCandidateContext(candidateParticipant,task,candidateAuthority,authority,candidatePump,pump,
                candidateDestination,destination,candidateClock,sharedClock,
                task.Completed&&rawCompletionRequested&&pump.State==ViveNamedRawRecordingPump.Outcome.CloseCallsReturned&&
                    destination.State==ViveRawExclusiveDestination.Outcome.CloseReturned,
                pendingRawPresentation==null&&pendingRawPanelFrame<0&&!rawPanelExposed&&pendingRawOwnerAction==null&&
                    rawGenericCallbackFrame<0,
                !rawWithdrawalRequested&&!rawSafetyLatched&&!rawInstallFailed&&!pump.WithdrawalRequested&&
                    !authority.ParticipantWithdrawalRequested&&!authority.IsRevoked&&
                    (receipt==null||receipt.WithdrawalNotObserved)&&
                    (namedCsvLocalCandidate==null||namedCsvLocalCandidate.WithdrawalNotObserved));
        }
        void ClearNamedRawLocalCandidate()
        {
            namedRawLocalCandidate=null;namedRawLocalCandidateNotice=null;
            namedCsvLocalCandidate=null;namedCsvCandidateNotice=null;namedCsvCandidateHidden=false;
            namedRawCandidateHidden=false;
            candidateParticipant=null;candidateAuthority=null;candidatePump=null;candidateDestination=null;candidateClock=null;
        }
        // Preview, recording setup and the controller legend use one fixed map.
        Dictionary<ViveTrackpadDirection,Colour> ConfiguredTrackpadMapping()
        {
            return ViveFixedTrackpadMapping.Create();
        }
        void BeginNamedRecordingSetup()
        {BeginRawRecordingSetup(false);}
        void BeginLegacyRecordingSetup()
        {BeginRawRecordingSetup(true);}
        void BeginRawRecordingSetup(bool legacy)
        {
            if(!ControlledRecordingNoticeCurrent){message="Read and accept the local development recording notice first.";return;}
            if(configured||rig==null||rig.ViewRunning||selectedDesign!=(legacy?0:3)||!RecordingKindAvailable(legacy))return;
            string reason;
            var choice=ViveNamedRecordingSetup.TryPrepare(RecordingKindAvailable(legacy)&&ViveRawRelativeFileCapability.ProductionGateOpen,
                recordingDirectory,recordingBudgetMiB,System.IO.Directory.Exists,out reason);
            if(choice==null){message=reason;return;}
            // Validate every task/input setting before asking the participant;
            // no task, eye client or file is created during this validation.
            try
            {
                if(!legacy)
                {
                    if(namedPlanCount<0||namedPlanCount>3)throw new InvalidOperationException("Select the number of blocks explicitly.");
                    for(int i=0;i<=namedPlanCount;i++)if(namedPlanOptions[i]<0||namedPlanOptions[i]>1||namedPlanLists[i]<0||namedPlanLists[i]>3)
                        throw new InvalidOperationException("Select an available option and stimulus list for every planned block.");
                }
                if(observationOptIn)throw new InvalidOperationException("Disable preview observation before recording.");
                double entered=ViveFixedTrackpadMapping.SectorThreshold;
                var map=ConfiguredTrackpadMapping();
                new VivePreviewTrackpadInput(map,entered);
                hand=VivePreviewControllerHand.Automatic;
                recordingSetup=choice;recordingSetupDesign=selectedDesign;preSessionShownRevision=-1;preSessionShownFrame=-1;
                if(view!=null)view.SetViveLegacyPreviewNotice(false);
                message="Start the classroom view so the participant can make the recording choices. No recording has started.";
                RenderPreSessionChoice();
            }
            catch(Exception error){choice.Cancel();message="Recording setup rejected: "+error.Message;}
        }
        void StartSelectedNamedRecording(bool explicitOwnerOutputRequest)
        {StartSelectedRawRecording(false,explicitOwnerOutputRequest);}
        void StartSelectedLegacyRecording(bool explicitOwnerOutputRequest)
        {StartSelectedRawRecording(true,explicitOwnerOutputRequest);}
        void StartSelectedRawRecording(bool legacy,bool explicitOwnerOutputRequest)
        {
            bool ready=CanStartSelectedRawRecording(legacy);
            if(!CanStartWithOwnerOutput(ready,ViveRawExclusiveDestination.ProductionGateOpen,explicitOwnerOutputRequest))
            {if(ready&&explicitOwnerOutputRequest)message="Local output remains unavailable; no recording was started.";return;}
            if(!(legacy?TryCreateAndAttachLegacyRawInstalledWithOwnerOutput(recordingSetup.DirectoryPath,recordingSetup.Budget,true,true,explicitOwnerOutputRequest):
                TryCreateAndAttachNamedRawInstalledWithOwnerOutput(recordingSetup.DirectoryPath,recordingSetup.Budget,true,true,explicitOwnerOutputRequest)))
                message=rawInstallFailed?"Recording start failed; task is blocked and cleanup may be unconfirmed.":"Recording remains unavailable; no recording was started.";
            else {suppressInputUntilReleased=true;message=explicitOwnerOutputRequest?
                "Waiting to record participant software-choice observations. A local output was requested; storage and study authorization remain unverified.":
                "Waiting to record participant software-choice observations; storage and study authorization remain unverified.";}
        }
        internal static bool TryParseCsvLimits(string sizeMiB,string seconds,out long bytes,out int timeoutMilliseconds)
        {
            bytes=0;timeoutMilliseconds=0;long size;int timeout;
            if(!long.TryParse(sizeMiB,System.Globalization.NumberStyles.None,System.Globalization.CultureInfo.InvariantCulture,out size)||
                !int.TryParse(seconds,System.Globalization.NumberStyles.None,System.Globalization.CultureInfo.InvariantCulture,out timeout)||
                size<1||size>8192||timeout<1||timeout>3600)return false;
            bytes=size*1048576L;timeoutMilliseconds=timeout*1000;return true;
        }
        void StartSelectedNamedRecordingWithCsv(long csvBytes,int csvTimeoutMilliseconds)
        {StartSelectedRawRecordingWithCsv(false,csvBytes,csvTimeoutMilliseconds);}
        void StartSelectedLegacyRecordingWithCsv(long csvBytes,int csvTimeoutMilliseconds)
        {StartSelectedRawRecordingWithCsv(true,csvBytes,csvTimeoutMilliseconds);}
        void StartSelectedRawRecordingWithCsv(bool legacy,long csvBytes,int csvTimeoutMilliseconds)
        {
            if(!CanStartWithOwnerOutput(CanStartSelectedRawRecording(legacy),ViveRawExclusiveDestination.ProductionGateOpen,true)||
                !ViveRawReadableAnalysisCsv.ProductionGateOpen)return;
            if(!(legacy?TryCreateAndAttachLegacyRawInstalledWithCsv(recordingSetup.DirectoryPath,recordingSetup.Budget,true,true,true,true,
                csvBytes,csvTimeoutMilliseconds):TryCreateAndAttachNamedRawInstalledWithCsv(recordingSetup.DirectoryPath,recordingSetup.Budget,true,true,true,true,
                csvBytes,csvTimeoutMilliseconds)))
                message=rawInstallFailed?"Recording start failed; task is blocked and cleanup may be unconfirmed.":"Recording and CSV request remain unavailable; no recording was started.";
            else {suppressInputUntilReleased=true;message="Recording and separate analysis CSV requested. Storage and study authorization remain unverified.";}
        }
        // UI action eligibility only. Neither button creates a permission,
        // and a participant's recording choices do not request an output.
        internal static bool CanStartWithOwnerOutput(bool recordingReady,bool outputGateOpen,bool explicitOwnerOutputRequest)
        {return recordingReady&&(!explicitOwnerOutputRequest||outputGateOpen);}
        bool CanStartSelectedNamedRecording()
        {return CanStartSelectedRawRecording(false);}
        bool CanStartSelectedRawRecording(bool legacy)
        {
            if(!RecordingSetupEligible||recordingSetupDesign!=(legacy?0:3)||
                participant==null||!MatchesRawRecordingKind(legacy)||participant.Phase!=Phase.Preparation||participant.Completed||namedRawPump!=null||rig==null||
                !RecordingKindAvailable(legacy)||!ViveRawRelativeFileCapability.ProductionGateOpen||
                !Application.isFocused||recordingSelectionFreshFrame!=Time.frameCount)return false;
            try{return recordingSetup.CanStart(true,true,rig.RawSceneTrackingCurrent(),rig.RecordingWorkerReady,recordingSelectionFreshFrame,Time.frameCount);}
            catch(Exception){return false;}
        }
        void TryConfigure(){TryConfigure(false);}
        void TryConfigure(bool fromParticipantChoice)
        {
            try
            {
                bool selected=fromParticipantChoice&&RecordingSetupEligible&&rig.HeadReady&&
                    SelectedRecordingAvailable&&ViveRawRelativeFileCapability.ProductionGateOpen;
                if(configured||(rig.ViewRunning&&!selected))throw new InvalidOperationException("Configure once before starting XR.");
                if(!fromParticipantChoice&&recordingSetup!=null)throw new InvalidOperationException("Cancel recording setup before choosing a non-recording preview.");
                if(fromParticipantChoice&&!selected)throw new InvalidOperationException("Participant recording choices are unavailable or no longer valid.");
                if(selectedDesign<0||selectedDesign>3)throw new InvalidOperationException("Select a development design explicitly.");
                if((selectedDesign==1||selectedDesign==2)&&(namedListIndex<0||namedListIndex>3))throw new InvalidOperationException("Select the stimulus list explicitly.");
                if(selectedDesign==3)
                {if(namedPlanCount<0||namedPlanCount>3)throw new InvalidOperationException("Select the number of blocks explicitly.");
                    for(int i=0;i<=namedPlanCount;i++)if(namedPlanOptions[i]<0||namedPlanOptions[i]>1||namedPlanLists[i]<0||namedPlanLists[i]>3)
                        throw new InvalidOperationException("Select an available option and stimulus list for every planned block.");
                }
                if(selectedDesign>0&&observationOptIn)throw new InvalidOperationException("Disable the legacy observation option for the named preview.");
                if(selected&&observationOptIn)throw new InvalidOperationException("Disable preview observation before recording setup.");
                double entered=ViveFixedTrackpadMapping.SectorThreshold;
                var map=ConfiguredTrackpadMapping();
                // Validate the full mapping before registering the wand. This
                // branch creates neither an Experiment nor an eye client.
                if(!selected&&selectedDesign==3&&namedIntentV3&&(namedTaskRecordingChoice||namedEyeRecordingChoice))
                {
                    new VivePreviewTrackpadInput(map,entered);
                    hand=VivePreviewControllerHand.Automatic;
                    preSessionChoice=new VivePreSessionRecordingChoice(namedTaskRecordingChoice,namedEyeRecordingChoice);
                    preSessionShownRevision=-1;preSessionShownFrame=-1;
                    message="Start classroom view for the input rehearsal. Recording and the session remain unavailable.";
                    RenderPreSessionChoice();return;
                }
                string session="vive-development-"+Guid.NewGuid().ToString("N");
                var root=view.AvatarRoot;var from=view.AvatarHead.position;
                Vector3 participantDirection=root.InverseTransformDirection(view.ParticipantReferencePosition-from);
                Vector3 taskDirection=root.InverseTransformDirection(view.TaskWorldPosition-from);
                var geometry=DevelopmentGeometry.FromLocalDirections(ToVec(participantDirection),ToVec(taskDirection),WorldSpaceExperimentView.GeometryVersion);
                SessionConfiguration config;
                if(selectedDesign==1)
                    config=SessionConfiguration.NamedAutomaticDevelopment(session,20261028,
                        ViveNamedOptionProvenance.Select(Actor.Owner,ViveNamedOption.Automatic,namedListIndex,geometry.Version));
                else if(selectedDesign==2)
                    config=SessionConfiguration.NamedNoAvatarDevelopment(session,20261028,
                        ViveNamedOptionProvenance.Select(Actor.Owner,ViveNamedOption.NoAvatar,namedListIndex,null));
                else if(selectedDesign==3)
                {
                    var chosen=new ViveNamedOptionProvenance[namedPlanCount+1];
                    for(int i=0;i<chosen.Length;i++)
                    {var option=namedPlanOptions[i]==0?ViveNamedOption.Automatic:ViveNamedOption.NoAvatar;
                        chosen[i]=ViveNamedOptionProvenance.Select(Actor.Owner,option,namedPlanLists[i],option==ViveNamedOption.Automatic?geometry.Version:null);}
                    var plan=ViveNamedSimulationPlan.Select(Actor.Owner,"plan-"+Guid.NewGuid().ToString("N"),chosen);
                    config=(selected||namedIntentV3)?SessionConfiguration.NamedRecordingDevelopment(session,20261028,plan,
                        ViveNamedRecordingUiAdmission.Create(Actor.Owner,"intent-"+Guid.NewGuid().ToString("N"),selected||namedTaskRecordingChoice,selected||namedEyeRecordingChoice)):
                        SessionConfiguration.NamedPlanDevelopment(session,20261028,plan);
                }
                else
                {
                    config=ViveLegacySceneConfiguration.CreatePreview(session);
                    if(selected)
                        config=ViveLegacySceneConfiguration.SelectRecording(config,geometry.Version,ViveFixedTrackpadMapping.Version,
                            "legacy-design-"+Guid.NewGuid().ToString("N"),"legacy-intent-"+Guid.NewGuid().ToString("N"),legacyWozRecordingSelected);
                }
                var taskClock=selected?rig.ActiveViewClock:new MonotonicClock("vive-development-task-"+session);
                if(taskClock==null)throw new InvalidOperationException("The active headset clock is unavailable.");
                var options=observationOptIn?new ExperimentIncrementalOptions(4096,128,16L*1024*1024):null;
                var candidate=new ViveDevelopmentParticipant(config,taskClock,geometry,map,entered,true,options,4096);
                view.SetViveLegacyPreviewNotice(selectedDesign==0&&!selected);
                view.ConfigureViveDevelopmentPreview(map,entered);
                CloseLegacyWozTaskBinding();participant=candidate;sharedClock=taskClock;hand=VivePreviewControllerHand.Automatic;
                questionnaireGate=new ViveQuestionnaireTrackpadGate(entered);
                view.SetQuestionnaireLanguage("en");view.SetPresentationAvailable(false);
                view.SetPresentationAvailabilityCheck(()=>rig.HeadReady&&participant!=null&&participant.InputReady&&!PresentationBlocked&&
                    (legacyWozScene==null||legacyWozScene.MayShowTask)&&
                    (!participant.RawRecordingRequested||namedRawPump!=null)&&
                    (namedRawPump==null||rig.RawSceneTrackingCurrent())&&
                    (recording==null||(!recording.WaitingForFreshEye&&!recording.RecoveryPending)));
                configured=true;if(selected)suppressInputUntilReleased=true;
                message=selected?"Participant choices recorded in software. Enable eye preview, then start recording; task remains blocked until recording starts.":"Configured. Start classroom view; release all controls before an owner starts practice.";
                CheckObservation(true);
                Render();
            }
            catch(Exception error){message="Configuration rejected: "+error.Message;}
        }
        public void PrepareInputBeforeXr()
        {
            if(device!=null)throw new InvalidOperationException("Existing input adapter must be released first.");
            if(!configured&&preSessionChoice==null&&recordingSetup==null)
            {
                hand=VivePreviewControllerHand.Automatic;
            }
            device=new ViveStroopDeviceInput(hand);
        }
        // This raw-level read cannot configure, consent to, or advance a task.
        // The rig's separate startup gate consumes the confirmation. Ordinary
        // Poll(false) still disarms task input until placement is complete.
        internal ViveStroopDeviceFrame ReadStartupAlignmentInput(bool tracked)
        {return device==null?null:device.ReadStartupAlignment(tracked);}
        internal bool StartupControllerConfigured {get{return device!=null;}}

        internal void RenderPreSessionChoice()
        {
            if(view==null)return;
            if(recordingSetup!=null&&!configured)
            {
                if(!RecordingSetupDesignMatches||!SelectedRecordingAvailable||!ControlledRecordingNoticeCurrent)recordingSetup.Cancel();
                view.SetViveLegacyPreviewNotice(false);
                if(preSessionShownRevision!=recordingSetup.Revision){preSessionShownRevision=recordingSetup.Revision;preSessionShownFrame=Time.frameCount;}
                string recordingText;
                switch(recordingSetup.Current)
                {
                    case ViveNamedRecordingSetup.Step.Task:
                        recordingText="Local development self-test only. Allow this PC to store task responses, questionnaire ratings and timing? No study participation is authorized.";break;
                    case ViveNamedRecordingSetup.Step.Eye:
                        recordingText="Allow this PC to store real eye and head observations for this self-test? Eye data may identify you. No upload. Withdrawal does not erase existing files.";break;
                    case ViveNamedRecordingSetup.Step.Confirmed:
                        recordingText="Both software choices were selected. No recording has started.";break;
                    case ViveNamedRecordingSetup.Step.Declined:
                        recordingText="Recording declined. No recording has started.";break;
                    default:recordingText="Recording choices cancelled. No recording has started.";break;
                }
                recordingText=(recordingSetupDesign==0?"Legacy A/B/C/D self-test. ":"Named multi-block self-test. ")+recordingText;
                if(recordingSetupDesign==0&&legacyWozRecordingSelected)
                    recordingText="LiveWoZ local recording: files also store mapped operator head/eye poses and source times. "+recordingText;
                view.SetViveParticipantRecordingChoice(recordingText,recordingSetup.Revision);return;
            }
            if(preSessionChoice==null)return;
            if(preSessionShownRevision!=preSessionChoice.Revision)
            {preSessionShownRevision=preSessionChoice.Revision;preSessionShownFrame=Time.frameCount;}
            string text;
            switch(preSessionChoice.Current)
            {
                case VivePreSessionRecordingChoice.Step.Task:
                    text="Input rehearsal only; not consent or permission. No recording or session will start. Practice choosing task-event recording, including responses.";break;
                case VivePreSessionRecordingChoice.Step.Eye:
                    text="Input rehearsal only; not consent or permission. No recording or session will start. Practice choosing eye recording. Eye data can identify you.";break;
                case VivePreSessionRecordingChoice.Step.Confirmed:
                    text="Rehearsal complete. This grants no recording permission. Recording is unavailable and no session was created. This is not research consent.";break;
                case VivePreSessionRecordingChoice.Step.Declined:
                    text="Rehearsal choice declined. No recording or experiment session was started.";break;
                default:text="Input rehearsal cancelled. No recording or experiment session was started.";break;
            }
            view.SetVivePreSessionRecordingChoice(text);
        }
        void PollPreSessionChoice(bool headReady)
        {
            RenderPreSessionChoice();
            if(preSessionChoice==null||preSessionChoice.Current==VivePreSessionRecordingChoice.Step.Declined||preSessionChoice.Current==VivePreSessionRecordingChoice.Step.Cancelled)return;
            if(Input.GetKeyDown(KeyCode.Escape)){preSessionChoice.Cancel();RenderPreSessionChoice();return;}
            if(device==null||!headReady)
            {if(preSessionChoice.IsTerminal)preSessionChoice.Cancel();else preSessionChoice.Observe(false,false,false,false,false,false);RenderPreSessionChoice();return;}
            var frame=device.Read(Phase.Preparation,headReady);
            RecordControllerUiPoll(headReady,frame.InputReady,frame.Diagnostic);
            if(preSessionChoice.IsTerminal&&(!frame.DeviceReady||frame.DeviceChanged)){preSessionChoice.Cancel();RenderPreSessionChoice();return;}
            preSessionChoice.Observe(frame.DeviceReady,Time.frameCount>preSessionShownFrame,
                frame.TriggerHeld,frame.MenuHeld,frame.Trackpad.Pressed,frame.DeviceChanged);
            RenderPreSessionChoice();
        }
        void PollNamedRecordingSetup(bool headReady)
        {
            RenderPreSessionChoice();
            if(recordingSetup==null||recordingSetup.Current==ViveNamedRecordingSetup.Step.Declined||recordingSetup.Current==ViveNamedRecordingSetup.Step.Cancelled)return;
            bool wasConfirmed=recordingSetup.BothSelected;
            if(Input.GetKeyDown(KeyCode.Escape)){recordingSetup.Cancel();RenderPreSessionChoice();return;}
            if(device==null||!headReady)
            {recordingSetup.Observe(false,false,false,false,false,false,null);RenderPreSessionChoice();return;}
            var frame=device.Read(Phase.Preparation,headReady);
            RecordControllerUiPoll(headReady,frame.InputReady,frame.Diagnostic);
            recordingSetup.Observe(frame.DeviceReady,Time.frameCount>preSessionShownFrame&&view.WasViveRecordingChoiceRendered(recordingSetup.Revision,Time.frameCount),
                frame.TriggerHeld,frame.MenuHeld,frame.Trackpad.Pressed,frame.DeviceChanged,rig.ActiveViewClock);
            RenderPreSessionChoice();
            if(!wasConfirmed&&recordingSetup.BothSelected)TryConfigure(true);
        }
        static bool ObservationPausable(Phase phase)
        {return phase==Phase.Practice||phase==Phase.PracticeCorrection||phase==Phase.Countdown||phase==Phase.MainTask||phase==Phase.Questionnaire||phase==Phase.Rest;}
        // Never report an observation failure by throwing through the recorder's
        // action callback: the action may already have happened and the recorder
        // must first preserve its existing event/segment handling. Safety actions
        // remain available after the observation has become terminal.
        bool CheckObservation(bool synchronize)
        {
            if(participant==null||!participant.ObservationEnabled)return true;
            bool current=false,failed=observationFaultLatched;
            try
            {
                if(!failed&&!participant.ObservationFaulted)
                    current=synchronize?participant.SynchronizeObservation():!participant.ObservationBlocked;
                failed=failed||participant.ObservationFaulted;
            }
            catch(Exception){failed=true;}
            if(!failed&&current&&!participant.ObservationPending)
            {observationPauseRequested=false;return true;}
            // A backlog can first be detected by the recording or render
            // callbacks, before Poll gets a chance to see it. Quarantine any
            // held controls even if the observer catches up by the next Poll.
            suppressInputUntilReleased=true;
            if(failed)observationFaultLatched=true;
            if(view!=null)view.SetPresentationAvailable(false);
            // PresentationAvailable only controls render witnessing; it does not
            // hide an already-rendered word. Cull the actual participant camera
            // immediately for both backlog and terminal failure. The rig will
            // restore it on a later head update only when PresentationBlocked is
            // false, after the task has been verified paused or ended.
            if(rig!=null)rig.BlockTaskPresentation();
            // A hidden active trial must not continue running while a bounded
            // backlog is drained or after observation is lost. Attempt pause
            // once, through the recording lifecycle when it is still present.
            if(!observationPauseRequested&&ObservationPausable(participant.Phase))
            {
                observationPauseRequested=true;
                long beforePause=participant.PauseDiagnosticRevision;
                try{if(recording!=null&&!recordingTerminal)recording.Pause();else participant.OwnerPause(Actor.Owner);}catch(Exception){}
                CapturePauseDiagnostic(failed?"Automatic safety: task observation failed.":"Automatic safety: task observation is catching up.",beforePause,"Owner or software safety request.");
                if(participant.Phase!=Phase.Paused&&participant.Phase!=Phase.Ended)
                {failed=true;observationFaultLatched=true;}
            }
            // Pausing may itself reveal a terminal producer failure.
            if(!failed&&participant.ObservationFaulted)
            {failed=true;observationFaultLatched=true;if(rig!=null)rig.BlockTaskPresentation();}
            bool stopped=participant.Phase==Phase.Paused||participant.Phase==Phase.Ended;
            message=failed?
                (stopped?"Development observation failed; task paused or ended. Review or withdraw.":
                    "Development observation failed; presentation is blocked and a safe stop is not confirmed."):
                (stopped?"Development observation is catching up; task is paused.":
                    "Development observation is catching up; presentation is blocked.");
            return false;
        }
        public void Poll(bool headReady)
        {
            BeginControllerUiPoll(headReady);
            if(preSessionChoice!=null){PollPreSessionChoice(headReady);return;}
            if(recordingSetup!=null&&!configured){PollNamedRecordingSetup(headReady);return;}
            if(!configured)return;
            if(recordingSetup!=null&&namedRawPump==null)recordingSelectionFreshFrame=-1;
            if(recordingSetup!=null&&namedRawPump==null&&(!headReady||!recordingSetup.BothSelected||!RecordingSetupDesignMatches||!RawRecordingDesignAvailable))
            {
                recordingSetup.Cancel();if(view!=null)view.SetPresentationAvailable(false);
                message="Recording choices are no longer current; no recording was started. Close and reconfigure before trying again.";
                return;
            }
            try
            {
                if(Input.GetKeyDown(KeyCode.Escape)){if(recordingSetup!=null&&namedRawPump==null)recordingSetup.Cancel();WithdrawTask();return;}
                ViveStroopDeviceFrame recordedWozFrame=null;
                if(RecordedLegacyWoz&&namedRawPump!=null)
                {
                    // Pairing/acquisition waits still poll the exact controller
                    // for loss/replacement before network or owner actions.
                    // This safety check takes no ordinary mutation capacity.
                    if(faultLatched||!headReady||device==null){FailNamedRawBeforeFrame();return;}
                    recordedWozFrame=device.Read(participant.Phase,headReady);
                    RecordControllerUiPoll(headReady,recordedWozFrame.InputReady,recordedWozFrame.Diagnostic);
                    if(!recordedWozFrame.DeviceReady||!recordedWozFrame.Trackpad.Usable||recordedWozFrame.DeviceChanged)
                    {questionnaireGate.Reset();FailNamedRawBeforeFrame();return;}
                }
                if(pendingLegacyWozOffer!=null){var create=pendingLegacyWozOffer;pendingLegacyWozOffer=null;create();suppressInputUntilReleased=true;Render();return;}
                if(RecordedLegacyWoz&&legacyWozScene!=null&&!legacyWozStarted)
                {suppressInputUntilReleased=true;Render();return;}
                if(legacyWozStarted&&!PollLegacyWozBeforeTask()){questionnaireGate.Reset();suppressInputUntilReleased=true;Render();return;}
                if(!CheckObservation(true))
                {
                    questionnaireGate.Reset();
                    suppressInputUntilReleased=true;
                    if(device!=null&&!RecordingBlocked)
                    {
                        if(device.ReadSafetyMenu(headReady))HandleParticipantMenu();
                    }
                    return;
                }
                if(RecordingBlocked){questionnaireGate.Reset();view.SetPresentationAvailable(false);return;}
                // Menu can cancel unstarted recording choices only. During the
                // task it never pauses, consumes a response, or skips input-loss checks.
                if(device!=null&&device.ReadSafetyMenu(headReady)&&HandleParticipantMenu())
                {questionnaireGate.Reset();return;}
                if(namedRawPump!=null&&(rawPlanStartRequested!=0||rawPendingBlockPlan!=null))
                {ProcessNamedBlockPlanStart(headReady,recordedWozFrame);return;}
                if(recording!=null&&(recording.WaitingForFreshEye||recording.RecoveryPending))
                {
                    questionnaireGate.Reset();
                    Render();return;
                }
                // A raw session cannot let loss-of-input processing enter an
                // ordinary capacity reservation: releasing a held response or
                // pausing for lost tracking is safety work. Revoke the raw
                // pump first and let the existing safety path attempt pause;
                // the tail is not claimed to have been recorded.
                if(namedRawPump!=null&&(faultLatched||!headReady||device==null))
                {questionnaireGate.Reset();FailNamedRawBeforeFrame();message+=" Recording stopped; its unconfirmed tail is unknown and this recording cannot resume.";return;}
                if(faultLatched){questionnaireGate.Reset();Mutate(()=>ApplyInputUnavailable("Automatic safety: development input fault remains latched."));Render();return;}
                if(device==null){questionnaireGate.Reset();Mutate(()=>ApplyInputUnavailable("Automatic safety: selected controller input adapter unavailable."));Render();return;}
                var frame=recordedWozFrame??device.Read(participant.Phase,headReady);
                // Display this existing poll only. Never obtain an extra input
                // sample for UI, which could consume a neutral/confirmation edge.
                RecordControllerUiPoll(headReady,frame.InputReady,frame.Diagnostic);
                if(recordingSetup!=null&&namedRawPump==null&&(!frame.DeviceReady||frame.DeviceChanged))
                {
                    recordingSetup.Cancel();view.SetPresentationAvailable(false);
                    message="Controller identity or tracking changed; recording choices were cancelled. Close and reconfigure before trying again.";return;
                }
                if(recordingSetup!=null&&namedRawPump==null&&frame.InputReady&&!frame.TriggerHeld&&!frame.MenuHeld&&!frame.Trackpad.Pressed&&
                    Application.isFocused&&rig.RawSceneTrackingCurrent())recordingSelectionFreshFrame=Time.frameCount;
                if(namedRawPump!=null&&(!frame.DeviceReady||!frame.Trackpad.Usable||frame.DeviceChanged))
                {questionnaireGate.Reset();FailNamedRawBeforeFrame();message+=" Controller input was lost or changed. Recording stopped; its unconfirmed tail is unknown and this recording cannot resume.";return;}
                if(namedRawPump!=null&&rawSelectionProvenancePending)
                {
                    // This event enters the same bounded FIFO as task data.
                    // If stopping/crashing happens before durable consumption,
                    // the earlier software decisions remain UNKNOWN on disk.
                    if(!Mutate(()=>{if(participant.LegacyRecordingRequested)participant.RecordLegacySoftwareChoices(sharedClock,rawTaskChoicePollSeconds,rawEyeChoicePollSeconds);
                        else participant.RecordNamedSoftwareChoices(sharedClock,rawTaskChoicePollSeconds,rawEyeChoicePollSeconds);}))
                    {message+=" Participant choice provenance is unconfirmed; task start is blocked.";return;}
                    rawSelectionProvenancePending=false;
                    message="Participant software-choice observations were queued; storage remains unconfirmed.";Render();return;
                }
                if(suppressInputUntilReleased)
                {
                    // Do not replay held confirm, colour or menu input when the
                    // observer catches up. Wait until all controls return neutral;
                    // Menu does not request a task pause.
                    if(!frame.InputReady||MustWaitForRawPlanNeutral(frame.NeutralPending,frame.TriggerHeld,frame.MenuHeld,frame.Trackpad.Pressed))
                    {questionnaireGate.Reset();Render();return;}
                    suppressInputUntilReleased=false;
                }
                if(namedRawPump!=null&&pendingRawOwnerAction!=null)
                {
                    // Validate the queued command before *any* task mutation;
                    // a held control must not leak into its next phase.
                    if(!PendingRawOwnerStillCurrent()||!System.Object.ReferenceEquals(participant,pendingRawOwnerParticipant)||
                        participant.Phase!=pendingRawOwnerPhase||!System.Object.ReferenceEquals(participant.NamedPlan,pendingRawOwnerPlan)||
                        !ViveStroopRecordingScopePresentation.ExactContextMatches(pendingRawOwnerContext,participant.RawContext))
                    {FailNamedRawBeforeFrame();message+=" The pending owner action no longer matches the task; it was not run.";return;}
                    if(!frame.InputReady||MustWaitForRawPlanNeutral(frame.NeutralPending,frame.TriggerHeld,frame.MenuHeld,frame.Trackpad.Pressed))
                    {questionnaireGate.Reset();message="Release controller controls before the pending owner action.";Render();return;}
                }
                if(!Mutate(()=>ApplyInputFrame(headReady,frame))){questionnaireGate.Reset();Render();return;}
                if(namedRawPump!=null&&pendingRawOwnerAction!=null)
                {
                    // Do not carry held inputs into the phase created by an
                    // owner confirmation. Safety menu was already read above.
                    if(!frame.InputReady||frame.TriggerHeld||frame.MenuHeld||frame.Trackpad.Pressed)
                    {questionnaireGate.Reset();message="Release controller controls before the pending owner action.";Render();return;}
                    var requested=pendingRawOwnerAction;var expectedTask=pendingRawOwnerParticipant;
                    var expectedPlan=pendingRawOwnerPlan;var expectedPhase=pendingRawOwnerPhase;var expectedContext=pendingRawOwnerContext;
                    ClearPendingRawOwnerAction();
                    if(!System.Object.ReferenceEquals(participant,expectedTask)||participant.Phase!=expectedPhase||
                        !System.Object.ReferenceEquals(participant.NamedPlan,expectedPlan)||
                        !ViveStroopRecordingScopePresentation.ExactContextMatches(expectedContext,participant.RawContext)||!participant.InputReady)
                    {FailNamedRawAfterTask();message+=" The pending owner action no longer matches the task; it was not run.";return;}
                    if(!Mutate(requested)){message+=" The pending owner action did not complete safely; do not retry without review.";return;}
                    message="Owner action accepted; the recording result remains unconfirmed.";Render();return;
                }
                bool displayReady=participant.Phase==Phase.Questionnaire?
                    (participant.QuestionnaireInstructionPending?view.QuestionnaireInstructionVisible:participant.QuestionnaireInputReady&&view.QuestionnaireResponseVisible):
                    participant.Phase!=Phase.Rest||view.RestResponseVisible;
                // An enabled text object is not a render witness. For an
                // attached raw session, ordinary non-trial input is released
                // only after the exact phase/item/rating was rendered by the
                // participant camera in the preceding frame. Safety controls
                // have their own path and do not depend on this witness.
                if(namedRawPump!=null&&WorldSpaceExperimentView.ViveRawPanelPhase(participant.Phase))
                {
                    int answered=Math.Min(Questionnaire.Count,Math.Max(0,participant.RatingCount-Math.Max(0,participant.BlockIndex)*Questionnaire.Count));
                    bool witnessed=view.HasPriorViveRawPanelWitness(participant.ParticipantView,participant.QuestionnaireInstructionPending,
                        participant.CandidateRating,answered,Time.frameCount);
                    // The panel is globally masked during this Update. Its
                    // prior exact-frame witness is stronger than checking
                    // activeInHierarchy after that deliberate mask.
                    displayReady=witnessed;
                }
                int ratingDirection=questionnaireGate.Observe(frame.InputReady,participant.Phase==Phase.Questionnaire,displayReady,frame.Trackpad);
                if(!Mutate(()=>participant.UpdateAuxiliary(frame.InputReady&&frame.TriggerHeld,false,false,ratingDirection,displayReady))){Render();return;}
                // Rest confirmation can enter BlockReady in this same Poll.
                // Prepare before Tick's retained-source auto-countdown.
                if(namedRawPump!=null&&participant.Phase==Phase.BlockReady&&participant.BlockIndex>0)
                {RequestNamedBlockPlanStart(false);ProcessNamedBlockPlanStart(headReady,recordedWozFrame);return;}
                if(!Mutate(()=>participant.Tick())){Render();return;}message=frame.Diagnostic;Render();
            }
            catch(Exception error)
            {
                if(questionnaireGate!=null)questionnaireGate.Reset();
                message="Development task error: "+error.GetType().Name;
                faultLatched=true;
                if(namedRawPump!=null){FailNamedRawAfterTask();return;}
                long beforePause=participant.PauseDiagnosticRevision;
                try{if(recording!=null)recording.Pause();else participant.UpdateTrackpad(false,new VivePreviewTrackpadSample(false,false,0,0));}catch(Exception){}
                CapturePauseDiagnostic("Automatic safety: task processing error "+error.GetType().Name+".",beforePause,recording==null?"Automatic safety: head/input readiness lost.":"Owner or software safety request.");
                CheckObservation(true);
                view.SetPresentationAvailable(false);
                rig.BlockTaskPresentation();
                view.SetFaults(new[]{participant.Phase==Phase.Paused||participant.Phase==Phase.Ended?
                    "Development input failed. Task paused or ended; owner review required.":
                    "Development input failed. A safe task stop could not be confirmed; keep presentation closed."});
            }
        }
        void RequestNamedBlockPlanStart(bool practice)
        {
            if(namedRawPump==null||participant==null||RecordingBlocked||rawSelectionProvenancePending||rawPlanStartRequested!=0||rawPendingBlockPlan!=null)return;
            rawPlanStartRequested=practice?1:2;
            message="Preparing the actual block plan; task start waits for recording confirmation.";
        }
        static bool IsWithinRawPlanDeadline(double deadline,double now)
        {return !Double.IsNaN(deadline)&&!Double.IsInfinity(deadline)&&deadline>=0&&
            !Double.IsNaN(now)&&!Double.IsInfinity(now)&&now>=0&&now<deadline;}
        static bool MustWaitForRawPlanNeutral(bool neutralPending,bool triggerHeld,bool menuHeld,bool trackpadHeld)
        {return neutralPending||triggerHeld||menuHeld||trackpadHeld;}
        bool RawPlanDeadlineStillValid()
        {double now;return sharedClock!=null&&sharedClock.TryReadRecordingAdmissionTime(out now)&&IsWithinRawPlanDeadline(rawPlanDeadline,now);}
        // Owner-thread polling only: writer/flush remains on the writer thread.
        // A late receipt is unusable after phase, generation, device or revoke
        // changes. Any failure latches the existing stop path and stays UNKNOWN.
        void ProcessNamedBlockPlanStart(bool headReady,ViveStroopDeviceFrame recordedWozFrame=null)
        {
            if(namedRawPump==null||participant==null||sharedClock==null||rawPlanStartRequested==0||RecordingBlocked)
            {FailNamedRawBeforeFrame();return;}
            if(rawPendingBlockPlan!=null)
            {
                if(!RawPlanDeadlineStillValid())
                {FailNamedRawBeforeFrame();message+=" Block plan confirmation timed out or its clock failed; in-flight I/O is unknown.";return;}
            }
            if(!headReady||device==null){FailNamedRawBeforeFrame();return;}
            var input=recordedWozFrame??device.Read(participant.Phase,headReady);
            RecordControllerUiPoll(headReady,input.InputReady,input.Diagnostic);
            if(!input.DeviceReady||!input.Trackpad.Usable||input.DeviceChanged)
            {FailNamedRawBeforeFrame();message+=" Input device readiness or identity changed while preparing the block plan.";return;}
            if(rawPendingBlockPlan==null)
            {
                JournalEvent plan=null;
                if(!namedRawPump.TryRunReservedOrdinaryMutation(()=>plan=participant.LegacyRecordingRequested?
                    participant.PrepareLegacyRecordingBlockPlan(Actor.Owner,rawPlanStartRequested==1):participant.PrepareNamedRecordingBlockPlan(Actor.Owner,rawPlanStartRequested==1)))
                {FailNamedRawAfterTask();message+=" Block-plan preparation was not safely recorded; do not retry.";return;}
                if(!ViveBlockPlanRequest.TryCapture(participant,sharedClock,plan,out rawPendingBlockPlan))
                {FailNamedRawBeforeFrame();message+=" Block plan context unavailable; no task was started.";return;}
                double now;
                if(!sharedClock.TryReadRecordingAdmissionTime(out now)||Double.IsNaN(now)||Double.IsInfinity(now)||now<0||now>Double.MaxValue-RawPlanWaitLimitSeconds)
                {FailNamedRawBeforeFrame();message+=" Block plan wait clock unavailable; in-flight I/O is unknown.";return;}
                rawPlanDeadline=now+RawPlanWaitLimitSeconds;
                message="Waiting for block-plan write and flush API confirmation; no task has started.";
                return;
            }
            if(!rawPendingBlockPlan.TryValidateCurrentForReceipt(participant,sharedClock))
            {FailNamedRawBeforeFrame();message+=" Block plan changed while waiting; its outcome is unknown.";return;}
            ViveRawSessionJournalSink.BlockPlanWriteReceipt receipt;
            var state=namedRawPump.TryGetBlockPlanReceipt(rawPendingBlockPlan,out receipt);
            if(state==ViveRawSessionJournalSink.BlockPlanReceiptPoll.Pending)
            {message="Waiting for block-plan write and flush API confirmation; no task has started.";return;}
            // A held trigger across Rest -> BlockReady is not a device fault.
            // Keep the receipt unconsumed until every control is neutral.
            if(state==ViveRawSessionJournalSink.BlockPlanReceiptPoll.Ready&&
                MustWaitForRawPlanNeutral(input.NeutralPending,input.TriggerHeld,input.MenuHeld,input.Trackpad.Pressed))
            {message="Release the controller controls before starting the prepared block.";return;}
            bool practice=rawPlanStartRequested==1;
            // The owner might have been descheduled while polling the receipt.
            // This is still not a physical hard-real-time guarantee.
            if(!RawPlanDeadlineStillValid())
            {FailNamedRawBeforeFrame();message+=" Block plan confirmation exceeded its deadline; in-flight I/O is unknown.";return;}
            bool started=false;
            if(state!=ViveRawSessionJournalSink.BlockPlanReceiptPoll.Ready||receipt==null||
                !namedRawPump.TryRunReservedOrdinaryMutation(()=>started=participant.LegacyRecordingRequested?
                    participant.TryBeginLegacyRecordingWithPlan(Actor.Owner,rawPendingBlockPlan,receipt,practice):
                    participant.TryBeginNamedRecordingWithPlan(Actor.Owner,rawPendingBlockPlan,receipt,practice))||!started)
            {FailNamedRawAfterTask();message+=" Block start or recording confirmation is unavailable or unknown.";return;}
            rawPendingBlockPlan=null;rawPlanStartRequested=0;rawPlanDeadline=-1;suppressInputUntilReleased=true;
            message="Block plan write and flush APIs returned; input must be released before continuing. Recording remains development-only.";
            // Refresh the hidden model after the phase mutation. The rig keeps
            // it masked until a later neutral input sample clears the boundary.
            Render();
        }
        void ClearPendingRawOwnerAction()
        {
            pendingRawOwnerAction=null;pendingRawOwnerParticipant=null;pendingRawOwnerPlan=null;pendingRawOwnerContext=null;pendingRawOwnerStamp=null;
        }
        bool PendingRawOwnerStillCurrent()
        {
            ViveDevelopmentParticipant.NamedRecordingTaskStamp current;
            return pendingRawOwnerStamp!=null&&participant!=null&&sharedClock!=null&&
                participant.TryCaptureRawRecordingTaskStamp(sharedClock,out current)&&current.Context!=null&&
                ViveStroopRecordingScopePresentation.ExactContextMatches(pendingRawOwnerStamp.Context,current.Context)&&
                System.Object.ReferenceEquals(current.Clock,pendingRawOwnerStamp.Clock)&&
                System.Object.ReferenceEquals(current.Configuration,pendingRawOwnerStamp.Configuration)&&
                current.Generation==pendingRawOwnerStamp.Generation&&current.EventCursor==pendingRawOwnerStamp.EventCursor&&
                System.Object.ReferenceEquals(current.Owner,pendingRawOwnerStamp.Owner)&&
                System.Object.ReferenceEquals(current.Plan,pendingRawOwnerStamp.Plan)&&
                System.Object.ReferenceEquals(current.Intent,pendingRawOwnerStamp.Intent)&&current.Session==pendingRawOwnerStamp.Session&&
                current.Phase==pendingRawOwnerStamp.Phase&&current.Block==pendingRawOwnerStamp.Block&&
                current.Segment==pendingRawOwnerStamp.Segment&&current.TrialId==pendingRawOwnerStamp.TrialId;
        }
        // On the main thread, immediately mask output and revoke producer
        // admission before attempting a task mutation. TryApply never waits
        // for named task/view locks; Pending means no action was attempted.
        // No operation here waits for the background writer or flushes a file.
        internal bool ServiceNamedRawSafety()
        {
            var pump=namedRawPump;if(pump==null)return false;
            if(!rawSafetyLatched&&!RecordingBlocked)return false;
            rawSafetyLatched=true;
            ClearPendingRawOwnerAction();
            // Only an explicit pump.Withdraw() sets this flag; internal
            // stop/fault queue detachment is not a participant withdrawal.
            if(pump.WithdrawalRequested)rawWithdrawalRequested=true;
            if(!rawPumpStopRequested)
            {rawPumpStopRequested=true;if(rawWithdrawalRequested)pump.Withdraw();else pump.Stop();}
            if(rawWithdrawalRequested&&!pump.WithdrawalRequested)pump.Withdraw();
            // A broken Unity view must not prevent the pump's shared revoke.
            // A reserved I/O call remains uncertain even after this request.
            try{if(rig!=null)rig.BlockTaskPresentation();}catch(Exception){rawMaskUncertain=true;}
            try{if(view!=null)view.SetPresentationAvailable(false);}catch(Exception){rawMaskUncertain=true;}
            if(legacyWozScene!=null)try
            {
                // Raw authority is already revoked. Tick can only finish a
                // pending negative acknowledgment, or fail closed on lost
                // recording authority. It never renews a source/task here.
                legacyWozProcess.Tick();
                if(legacyWozProcess.Terminal)CloseLegacyWozScene();
            }
            catch(Exception){rawMaskUncertain=true;try{CloseLegacyWozScene();}catch(Exception){}}
            if(!rawStatusStopAttempted)
            {rawStatusStopAttempted=true;try{pump.TryStopStatusOnOwner();}catch(Exception){rawStatusStopUncertain=true;}}
            if(!rawEyeStopRequested&&rig!=null)
            {try{rawEyeStopRequested=rig.TryStopEyeForRawSafety();}catch(Exception){rawEyeStopRequested=true;rawEyeStopUncertain=true;}}
            if(participant==null){message="Recording stopped; task safety is unavailable. Eye stop, I/O and cleanup may be pending."+RawSafetyWarnings();return true;}
            bool participantWithdrawal;
            if(!TryReadNamedParticipantWithdrawal(out participantWithdrawal))
            {message="Recording stop requested; participant safety status is pending. Eye stop, I/O or cleanup may still be in progress."+RawSafetyWarnings();return true;}
            if(participantWithdrawal)
            {rawWithdrawalRequested=true;if(!pump.WithdrawalRequested)pump.Withdraw();}
            bool withdrawing=rawWithdrawalRequested;
            if(withdrawing?rawWithdrawalAttempted:rawPauseAttempted)
            {
                if(rawMaskUncertain||rawEyeStopUncertain||rawStatusStopUncertain)
                    message="Task safety was already attempted; review its result. Eye stop, I/O and cleanup may be pending."+RawSafetyWarnings();
                return true;
            }
            var result=participant.TryApplyNamedRawSafetyForCause(withdrawing?ViveDevelopmentParticipant.NamedRawSafetyCause.Withdrawal:rawSafetyCause);
            if(result==ViveDevelopmentParticipant.NamedRawSafetyResult.Pending)
            {message="Recording stopped; task safety request is pending. Eye stop, I/O or cleanup may still be in progress."+RawSafetyWarnings();return true;}
            if(withdrawing)rawWithdrawalAttempted=true;else rawPauseAttempted=true;
            message=result==ViveDevelopmentParticipant.NamedRawSafetyResult.Applied?
                (withdrawing?"Task withdrawal confirmed; eye stop, recording I/O or cleanup may still be in progress.":"Task paused or ended; eye stop, recording I/O or cleanup may still be in progress."):
                "Task stop or withdrawal is unconfirmed. Keep presentation blocked; eye stop, recording I/O or cleanup may still be in progress.";
            if(rawMutationIntervalUnknown)message+=" Task input during the last status interval is unknown; do not treat it as validated.";
            if(pump.StatusCleanupPending)message+=" Status evidence cleanup is unconfirmed.";
            message+=RawSafetyWarnings();
            return true;
        }
        string RawSafetyWarnings()
        {return (rawMaskUncertain?" Presentation masking is unconfirmed.":"")+
            (rawEyeStopUncertain?" Eye stop is unconfirmed.":"")+
            (rawStatusStopUncertain?" Status cleanup is unconfirmed.":"");}
        bool TryReadNamedParticipantWithdrawal(out bool requested)
        {
            requested=false;var task=participant;if(task==null)return true;
            if(!System.Threading.Monitor.TryEnter(task.NamedRecordingGate))return false;
            try{requested=task.NamedRecordingWithdrawalRequested;return true;}
            finally{System.Threading.Monitor.Exit(task.NamedRecordingGate);}
        }
        internal bool StageNamedRawFrame(ViveProEyePoll frame)
        {
            var pump=namedRawPump;if(pump==null)return true;
            if(ServiceNamedRawSafety())return false;
            bool staged=pump.StageRigFrame(frame,1.0);
            if(!staged||RecordingBlocked){rawSafetyLatched=true;ServiceNamedRawSafety();return false;}
            return true;
        }
        internal void FailNamedRawBeforeFrame()
        {
            if(namedRawPump==null)return;
            ClearPendingRawOwnerAction();
            rawSafetyLatched=true;ServiceNamedRawSafety();
        }
        internal bool VerifyNamedRawEpoch()
        {
            var pump=namedRawPump;if(pump==null)return true;
            if(ServiceNamedRawSafety())return false;
            if(!pump.VerifyStagedEpoch()||RecordingBlocked){rawSafetyLatched=true;ServiceNamedRawSafety();return false;}
            return true;
        }
        internal void FailNamedRawAfterTask()
        {
            if(namedRawPump==null)return;
            ClearPendingRawOwnerAction();
            rawMutationIntervalUnknown=true;rawSafetyLatched=true;ServiceNamedRawSafety();
            if(message.IndexOf("last status interval is unknown",StringComparison.Ordinal)<0)
                message+=" Task input during the last status interval is unknown; do not treat it as validated.";
        }
        internal bool DrainNamedRawTaskEvents()
        {
            var pump=namedRawPump;if(pump==null)return true;
            if(ServiceNamedRawSafety())return false;
            bool drained=pump.DrainTaskEvents();
            if(!drained||RecordingBlocked){FailNamedRawAfterTask();return false;}
            return true;
        }
        // Runs before ordinary input on the next owner frame. Missing or late
        // callbacks are never retried and cannot leave the FIFO producer bit held.
        internal bool ExpirePendingRawPresentation()
        {
            if(pendingRawPresentation==null&&pendingRawPanelFrame<0)return false;
            AbortRawScenePresentation();return true;
        }
        void AbortRawScenePresentation()
        {
            var token=pendingRawPresentation;pendingRawPresentation=null;
            if(participant!=null&&participant.LegacyWozRecording!=null)participant.LegacyWozRecording.InvalidateAppliedPresentation();
            pendingRawPanelFrame=-1;pendingRawPanelRevision=-1;
            committedRawTrial=null;committedRawRevision=-1;rawGenericCallbackTrial=null;rawGenericCallbackFrame=-1;
            try{if(rig!=null)rig.BlockTaskPresentation();}catch(Exception){rawMaskUncertain=true;}
            try{if(view!=null)view.SetPresentationAvailable(false);}catch(Exception){rawMaskUncertain=true;}
            try{if(token!=null&&namedRawPump!=null)namedRawPump.CancelPresentation(token);}
            finally{if(namedRawPump!=null)FailNamedRawAfterTask();}
        }
        internal bool NamedRawTaskCompleted {get{return namedRawPump!=null&&participant!=null&&participant.Completed;}}
        // Non-trial content is prepared while the real participant camera is
        // masked. Until input is neutral or a pending block plan is confirmed,
        // stay masked but keep draining the actual task FIFO. This never
        // interprets an enabled object as a displayed panel.
        internal bool PrepareRawNonTrialScene()
        {
            if(!RawRecordingDesignAvailable||namedRawPump==null||participant==null||view==null||rig==null||
                pendingRawPresentation!=null||pendingRawPanelFrame>=0||PresentationBlocked||participant.Completed||!rig.RawSceneTrackingCurrent())return false;
            if(!WorldSpaceExperimentView.ViveRawPanelPhase(participant.Phase))return false;
            if(legacyWozScene!=null&&!legacyWozScene.MayShowTask)return false;
            if(!participant.InputReady||rawPlanStartRequested!=0||rawPendingBlockPlan!=null)
                return namedRawPump.DrainTaskEvents();
            if(!namedRawPump.PrepareNonTrialPanelRender())return false;
            long revision;
            if(!view.TryPrepareViveRawPanel(Time.frameCount,out revision)||revision<0)return false;
            pendingRawPanelFrame=Time.frameCount;pendingRawPanelRevision=revision;
            rawPanelExposed=true;
            if(!view.TryRevealViveRawPanel()||!rig.TryRevealRawScene()){AbortRawScenePresentation();return false;}
            return true;
        }
        bool OnViveRawPanelRendered(WorldSpaceExperimentView.ViveRawPanelObservation rendered)
        {
            if(namedRawPump==null)return false;
            try
            {
                int answered=participant==null?0:Math.Min(Questionnaire.Count,Math.Max(0,participant.RatingCount-Math.Max(0,participant.BlockIndex)*Questionnaire.Count));
                if(!RawRecordingDesignAvailable||participant==null||view==null||rig==null||rendered==null||
                    pendingRawPanelFrame!=Time.frameCount||rendered.Frame!=pendingRawPanelFrame||rendered.Revision!=pendingRawPanelRevision||
                    !participant.InputReady||PresentationBlocked||!rig.RawSceneTrackingCurrent()||!namedRawPump.VerifyStagedEpoch()||
                    (legacyWozScene!=null&&!legacyWozScene.MayShowTask)||
                    !view.MatchesViveRawPanel(rendered,participant.ParticipantView,participant.QuestionnaireInstructionPending,participant.CandidateRating,answered))
                {AbortRawScenePresentation();return false;}
                // Start the existing 3-second countdown only after its first
                // accepted software render. This is not an optical timestamp.
                if(participant.RawCountdownAwaitingSoftwareWitness&&
                    !(participant.LegacyRecordingRequested?participant.TryStartLegacyRawCountdownFromSoftwareWitness(rendered.Frame,rendered.Revision,rendered.UnityRenderSeconds):
                        participant.TryStartNamedRawCountdownFromSoftwareWitness(rendered.Frame,rendered.Revision,rendered.UnityRenderSeconds)))
                {AbortRawScenePresentation();return false;}
                if(!namedRawPump.FinishNonTrialPanelRender()||PresentationBlocked||!participant.InputReady||!rig.RawSceneTrackingCurrent()||
                    !view.MatchesViveRawPanel(rendered,participant.ParticipantView,participant.QuestionnaireInstructionPending,participant.CandidateRating,
                        Math.Min(Questionnaire.Count,Math.Max(0,participant.RatingCount-Math.Max(0,participant.BlockIndex)*Questionnaire.Count))))
                {AbortRawScenePresentation();return false;}
                pendingRawPanelFrame=-1;pendingRawPanelRevision=-1;return true;
            }
            catch(Exception){AbortRawScenePresentation();return false;}
        }
        // Scene-side hook is deliberately unreachable while the existing
        // recording admission gate is closed. A queue reservation or render
        // callback is not research consent, file durability or HMD visibility.
        internal bool PrepareRawScenePresentation()
        {
            if(!RawRecordingDesignAvailable||namedRawPump==null||participant==null||view==null||rig==null||
                pendingRawPresentation!=null||PresentationBlocked||!participant.InputReady||!rig.RawSceneTrackingCurrent())return false;
            var trial=participant.CurrentTrial;
            if(trial==null||trial.Stimulus==null||(participant.Phase!=Phase.Practice&&participant.Phase!=Phase.MainTask))return false;
            if(participant.LegacyWozRecordingDeclared&&participant.Phase==Phase.MainTask&&
                (legacyWozScene==null||!legacyWozScene.MayShowTask))return false;
            long revision;
            if(!view.TryGetViveRawTrialIdentity(trial.TrialId,trial.Stimulus.Word,trial.Stimulus.Ink,out revision))return false;
            if(trial.Onset.HasValue)
            {
                if(!String.Equals(committedRawTrial,trial.TrialId,StringComparison.Ordinal)||committedRawRevision!=revision||
                    !namedRawPump.DrainTaskEvents()||!rig.RawSceneTrackingCurrent())return false;
            }
            else
            {
                ViveNamedSessionFrameAdmission.PresentationReservation token;
                if(!namedRawPump.TryPreparePresentation(Time.frameCount,revision,out token)||token==null)return false;
                pendingRawPresentation=token;
            }
            if(legacyWozScene!=null&&participant.Phase==Phase.MainTask)
            {
                legacyWozScene.Present();
                if(!legacyWozScene.MayShowTask||PresentationBlocked||
                    (pendingRawPresentation!=null&&(pendingRawPresentation.WozEvidence==null||!pendingRawPresentation.WozEvidence.Applied)))
                {AbortRawScenePresentation();return false;}
            }
            if(!view.TryRevealViveRawTrial(trial.TrialId,trial.Stimulus.Word,trial.Stimulus.Ink,revision))
            {AbortRawScenePresentation();return false;}
            view.SetPresentationAvailable(true);
            if(!rig.TryRevealRawScene()){AbortRawScenePresentation();return false;}
            return true;
        }
        // Only after the final task mutation has been drained. This merely
        // starts the asynchronous candidate drain; no file or coverage claim
        // follows from the request. The raw camera remains masked throughout.
        internal bool RequestNamedRawCompletionIfEnded()
        {
            var pump=namedRawPump;if(pump==null||participant==null)return false;
            if(!participant.Completed)return true;
            if(rawCompletionRequested)return !rawSafetyLatched&&!pump.WithdrawalRequested;
            bool requested=!rawSafetyLatched&&!pump.WithdrawalRequested&&!RecordingBlocked&&
                (participant.LegacyWozRecordingDeclared?participant.LegacyWozRecording!=null&&participant.LegacyWozRecording.RequestCompletedDrain():pump.RequestCompletedDrain());
            if(!requested)
            {FailNamedRawAfterTask();return false;}
            rawCompletionRequested=true;
            message="Task ended; recording drain requested. API completion, coverage and durability are not yet established.";
            return true;
        }
        internal bool NamedRawCompletionActive {get{return rawCompletionRequested;}}
        // Owner-thread polling only; never wait for the writer or retry I/O.
        // Explicit withdrawal and stop retain their existing safety path.
        internal bool PollNamedRawCompletion()
        {
            var pump=namedRawPump;if(!rawCompletionRequested||pump==null)return false;
            if(Input.GetKeyDown(KeyCode.Escape))
            {try{RequestNamedRawWithdrawal();}finally{ClearNamedTaskView();}return false;}
            bool participantWithdrawal;
            if(!TryReadNamedParticipantWithdrawal(out participantWithdrawal))
            {message="Checking participant safety status; recording and cleanup may be pending.";return true;}
            if(participantWithdrawal)rawWithdrawalRequested=true;
            if(rawSafetyLatched||rawWithdrawalRequested||pump.WithdrawalRequested)
            {ServiceNamedRawSafety();return false;}
            var state=pump.State;
            if(state==ViveNamedRawRecordingPump.Outcome.Finishing||state==ViveNamedRawRecordingPump.Outcome.StopPending)
            {message="Task ended; recording drain or cleanup is pending. Coverage and durability are unknown.";return true;}
            if(state==ViveNamedRawRecordingPump.Outcome.CloseCallsReturned)
            {message="Recording close calls returned. Sampling coverage and power-loss durability remain unknown.";return true;}
            rawSafetyLatched=true;ServiceNamedRawSafety();
            message+=" Recording completion is unconfirmed; the remaining tail is unknown.";
            return false;
        }
        void RequestNamedRawWithdrawal()
        {
            if(namedRawPump==null)return;
            ClearNamedRawLocalCandidate();
            rawSafetyLatched=true;
            // Withdrawal detaches uncommitted queue payloads; a previously
            // reserved OS call and eventual cleanup remain uncertain.
            namedRawPump.Withdraw();rawWithdrawalRequested=true;rawPumpStopRequested=true;
            ServiceNamedRawSafety();
        }
        internal void RequestNamedRawStopForViewClose()
        {
            if(namedRawPump==null)return;
            rawSafetyLatched=true;
            ServiceNamedRawSafety();
        }
        // Drain already requested safety work while presentation is blocked.
        // No participant controller action can request a manual pause here.
        public void PollRecordingBlockedSafety()
        {
            if(namedRawPump!=null)
            {
                ServiceNamedRawSafety();
                return;
            }
            if(participant!=null&&participant.RawRecordingRequested)
            {
                bool handled=participant.PumpNamedRecordingSafety();
                if(participant.NamedRecordingWithdrawalRequested&&!namedRecordingEyeStopAttempted)
                {
                    namedRecordingEyeStopAttempted=true;ClearNamedTaskView();StopPracticeObservation();
                    try{if(rig!=null)rig.StopEyeAfterWithdrawal();}
                    catch(Exception){message="Eye stop after withdrawal is unconfirmed; do not retry without review.";}
                }
                if(!namedRecordingSafetyReported)
                {
                    namedRecordingSafetyReported=true;
                    message=handled&&(participant.Phase==Phase.Paused||participant.Phase==Phase.Ended)?
                        "Recording authority stopped; task pause or end is confirmed.":
                        "Recording authority stopped; task pause or end is not confirmed. Presentation remains blocked.";
                }
            }
        }
        bool HandleParticipantMenu()
        {
            // Preserve cancellation of choices before recording starts. Menu
            // has no in-task pause action; tracking faults, owner controls,
            // withdrawal and explicit Trigger resume retain their own paths.
            if(recordingSetup==null||namedRawPump!=null)return false;
            recordingSetup.Cancel();if(view!=null)view.SetPresentationAvailable(false);
            message="Participant cancelled recording choices; no recording was started.";
            return true;
        }
        void Render()
        {
            try{RenderCore();}
            catch
            {
                // Clear a previously rendered avatar and mask the camera on
                // any presentation failure, then preserve the original fault.
                try{if(view!=null)view.ClearAvatarOutput();}catch(Exception){}
                try{if(rig!=null)rig.BlockTaskPresentation();}catch(Exception){}
                throw;
            }
        }
        void RenderCore()
        {
            avatarOutputNotice=null;
            if(participant==null){if(view!=null)view.ClearAvatarOutput();return;}
            CapturePauseDiagnostic();
            if(view!=null)view.SetVivePauseDiagnostic(pauseDiagnostic!=null&&pauseDiagnostic.Length>180?
                participant.LastPauseDiagnostic+" See desktop for details.":pauseDiagnostic);
            if(namedRawPump!=null&&participant.CurrentTrial!=null&&
                (participant.Phase==Phase.Practice||participant.Phase==Phase.MainTask))
            {
                if(RawRecordingDesignAvailable)
                {
                    // Build the expected mesh while physically masked; only a
                    // later same-frame reservation may make it renderable.
                    if(rig!=null)rig.BlockTaskPresentation();
                    if(view!=null){view.SetPresentationAvailable(false);view.PrepareViveRawTaskView(participant.ParticipantView);}
                    return;
                }
                // SetPresentationAvailable(false) alone does not hide the task
                // mesh. Mask the actual camera BEFORE ApplyTaskView can enable
                // it; no trusted per-trial write/flush receipt exists today.
                if(rig!=null)rig.BlockTaskPresentation();
                if(view!=null){view.SetPresentationAvailable(false);view.SuppressViveRawStimulus();}
                ViveTrialOpenRequest request;
                bool captured=ViveTrialOpenRequest.TryCapture(participant,sharedClock,out request)&&request!=null&&
                    !request.HasWriteReceipt&&!request.HasFlushReceipt&&!request.MayShowStimulus&&request.Validity=="UNKNOWN";
                FailNamedRawBeforeFrame();
                message+=captured?
                    " Trial-open context observed in memory only; no write or flush receipt exists. Stimulus remains blocked and validity is unknown.":
                    " Trial-open context or receipt unavailable. Stimulus remains blocked and validity is unknown.";
                return;
            }
            if(legacyWozScene!=null&&!legacyWozScene.MayShowTask)
            {legacyWozScene.Present();view.SetPresentationAvailable(false);view.SuppressViveRawStimulus();return;}
            view.SetPresentationAvailable(participant.InputReady&&rig.HeadReady&&!PresentationBlocked&&
                (!participant.RawRecordingRequested||namedRawPump!=null)&&
                (recording==null||(!recording.WaitingForFreshEye&&!recording.RecoveryPending)));
            int answered=Math.Min(Questionnaire.Count,Math.Max(0,participant.RatingCount-Math.Max(0,participant.BlockIndex)*Questionnaire.Count));
            view.SetPhaseProgress(participant.CountdownRemainingSeconds,participant.CountdownDurationSeconds,
                participant.RestRemainingSeconds,participant.RestDurationSeconds,0,0,participant.Completed,participant.QuestionnaireInstructionPending,answered);
            view.SetViveQuestionnaireRating(participant.CandidateRating);
            view.ApplyTaskView(participant.ParticipantView);
            if(legacyWozScene!=null)
            {
                legacyWozScene.Present();
                if(!legacyWozScene.MayShowTask){view.SetPresentationAvailable(false);view.SuppressViveRawStimulus();}
                view.SetViveDevelopmentStatus((RecordedLegacyWoz?"CONTROLLED LEGACY WOZ: local recording requested; coverage/durability unverified. ":
                    "CONTROLLED LEGACY WOZ: no recording; mapping/hardware unvalidated. ")+legacyWozStatus,ControlsHint);
                return;
            }
            AvatarOutput avatar=participant.CurrentAvatar;
            // Outside the separately owned controlled scene, no external pose
            // is admitted. Preserve the ordinary Automatic-only path.
            if(!ViveWozPresentationPolicy.IsAutomaticOnly(avatar))
            {
                view.ClearAvatarOutput();
                avatarOutputNotice=ViveWozPresentationPolicy.BlockedFeedback(avatar);
            }
            else view.ApplyAvatarOutput(avatar);
            view.SetViveDevelopmentStatus(namedRawPump!=null?"Local self-test. "+RecordingStatus:LegacyRawRecordingRequested?"Legacy self-test selected; recording has not started.":participant.NamedPlan!=null||participant.HasNamedOption?"Preview: no durable recording.":recording==null?"LEGACY PREVIEW: recording/export unavailable.":
                "VIVE development task; local recording enabled. "+recording.Status,ControlsHint);
        }
        void OnStimulusRendered(string trialId,double renderProxySeconds)
        {
            // Raw callback handling is separate so the same render never marks
            // a recorded trial through this unreserved preview-only route.
            if(namedRawPump!=null)
            {
                bool committed=rawGenericCallbackFrame==Time.frameCount&&String.Equals(rawGenericCallbackTrial,trialId,StringComparison.Ordinal);
                rawGenericCallbackTrial=null;rawGenericCallbackFrame=-1;
                if(!committed)FailNamedRawAfterTask();return;
            }
            if(participant==null||participant.CurrentTrial==null||participant.CurrentTrial.TrialId!=trialId||!participant.InputReady||!rig.HeadReady)return;
            if(!CheckObservation(true)||RecordingBlocked||(recording!=null&&(recording.WaitingForFreshEye||recording.RecoveryPending)))return;
            if(participant.Phase==Phase.Practice||participant.Phase==Phase.MainTask)
            {if(Mutate(()=>participant.MarkPresented(trialId))&&legacyWozScene!=null)legacyWozScene.NotePresented(trialId);}
        }
        void OnViveRawStimulusRendered(WorldSpaceExperimentView.StimulusRenderObservation render)
        {
            if(namedRawPump==null)return;
            var token=pendingRawPresentation;
            if(token!=null)
            {
                pendingRawPresentation=null;
                try
                {
                    if(!RawRecordingDesignAvailable||render==null||rig==null||!rig.RawSceneTrackingCurrent()||
                        render.Frame!=Time.frameCount||render.Frame!=token.Frame||render.Revision!=token.Revision||
                        !String.Equals(render.TrialId,token.Stamp.TrialId,StringComparison.Ordinal)||!render.MatchesTask(token.Word,token.Ink)||
                        !namedRawPump.CommitPresentation(token,render.Frame,render.Revision,render.TrialId,token.Word,token.Ink,render.UnityRenderSeconds))
                    {AbortRawScenePresentation();return;}
                    committedRawTrial=render.TrialId;committedRawRevision=render.Revision;
                    if(legacyWozScene!=null)legacyWozScene.NotePresented(render.TrialId);
                    rawGenericCallbackTrial=render.TrialId;rawGenericCallbackFrame=render.Frame;
                    return;
                }
                catch(Exception){AbortRawScenePresentation();return;}
            }
            // The callback is outside the guarded task Poll. Identity and
            // TextMesh content are diagnostic only until an installed FIFO
            // reservation and an exact epoch/trial transaction are available.
            bool proxyObserved=false;
            try
            {
                ViveRenderProxyObservation observed;
                proxyObserved=render!=null&&ViveRenderProxyObservation.TryObserve(participant,sharedClock,render.TrialId,
                    render.UnityRenderSeconds,out observed)&&render.MatchesTask(observed.TaskModelWord,observed.TaskModelInk);
            }
            catch(Exception){proxyObserved=false;}
            if(proxyObserved)message+=" Unity render-proxy callback and task content matched only; no trial-open proof, durable record, headset visibility or onset is established.";
        }
        public void BeforeXrStop()
        {
            CloseLegacyWozScene();
            if(namedRawPump!=null){RequestNamedRawStopForViewClose();return;}
            if(questionnaireGate!=null)questionnaireGate.Reset();
            long before=participant==null?0:participant.PauseDiagnosticRevision;
            try{if(recording!=null)StopRecording();else if(participant!=null)participant.UpdateTrackpad(false,new VivePreviewTrackpadSample(false,false,0,0));}
            finally{CapturePauseDiagnostic("Automatic safety: classroom view is stopping.",before,"Automatic safety: head/input readiness lost.");CheckObservation(true);}
        }
        public bool PollRecordingEye()
        {
            if(recording==null||RecordingBlocked)return false;
            if(observationFaultLatched||(participant!=null&&participant.ObservationFaulted)){CheckObservation(true);return false;}
            long before=participant==null?0:participant.PauseDiagnosticRevision;
            try
            {
                // Continue the recorder's own loss detection and safety handling
                // while an observation backlog is being drained.
                if(recording.WaitingForFreshEye){recording.Refresh();return recording.LastEyeUsable;}
                return recording.PollEye()&&recording.LastEyeUsable;
            }
            finally{CapturePauseDiagnostic("Automatic safety: eye/recording readiness lost.",before,"Owner or software safety request.");CheckObservation(true);}
        }
        public void StopRecording()
        {
            if(recording==null||recordingTerminal)return;
            long before=participant==null?0:participant.PauseDiagnosticRevision;
            try{recording.Dispose();recordingStopConfirmed=true;message="Recording stopped. "+recording.Status;}
            catch(Exception e){message="Recording stop outcome is uncertain: "+e.GetType().Name;}
            finally{CapturePauseDiagnostic("Recording stopped; task safety pause.",before,"Owner or software safety request.");recordingTerminal=true;if(view!=null)view.SetPresentationAvailable(false);CheckObservation(true);}
        }
        public void AfterXrStop()
        {BeginControllerUiPoll(false);if(preSessionChoice!=null)preSessionChoice.Cancel();if(recordingSetup!=null)recordingSetup.Cancel();if(device!=null){device.Dispose();device=null;}}
        void OnDestroy()
        {
            CloseLegacyWozTaskBinding();
            if(namedRawPump!=null)AbortRawScenePresentation();
            ClearSensitiveEvidence();ClearNamedRawLocalCandidate();StopPracticeObservation();
            if(view!=null){view.StimulusRendered-=OnStimulusRendered;view.ViveRawStimulusRendered-=OnViveRawStimulusRendered;view.ViveRawPanelRendered-=OnViveRawPanelRendered;view.ViveRawObservationRequired=null;}
            rawCallbackView=null;
            if(recording!=null&&!recordingTerminal)StopRecording();
            // Rig owns loader teardown and calls AfterXrStop after it is closed.
        }
        bool Mutate(Action action)
        {
            if(RecordingBlocked||participant!=null&&participant.RawRecordingRequested&&namedRawPump==null||!CheckObservation(true))return false;
            bool ok=false,current=false;
            try
            {
                if(namedRawPump!=null)
                {
                    ok=namedRawPump.TryRunReservedOrdinaryMutation(action);
                    if(!ok){FailNamedRawAfterTask();message+=" Raw task action was blocked or may be partially applied; do not retry.";}
                }
                else if(recording==null){action();ok=true;}
                else
                {
                    ok=recording.RunTaskMutation(action);
                    if(!ok)message="Task action blocked or already applied; not retried. "+recording.Status;
                }
            }
            finally{current=CheckObservation(true);if(legacyWozScene!=null)legacyWozScene.AfterTaskMutation();}
            return ok&&current;
        }
        void PauseTask()
        {
            if(legacyWozProcess!=null&&!legacyWozProcess.Terminal){RevokeLegacyWoz();return;}
            long beforePause=participant==null?0:participant.PauseDiagnosticRevision;
            if(namedRawPump!=null&&(rawPlanStartRequested!=0||rawPendingBlockPlan!=null))
            {RequestExplicitRawPause(ViveDevelopmentParticipant.NamedRawSafetyCause.OwnerPause);
                CapturePauseDiagnostic("Operator requested pause.",beforePause,"Owner or software safety request.");
                message+=" Plan confirmation was interrupted; no late receipt can start the task. Recording stopped; its unconfirmed tail is unknown and this recording cannot resume.";return;}
            if(namedRawPump!=null)
            {RequestExplicitRawPause(ViveDevelopmentParticipant.NamedRawSafetyCause.OwnerPause);
                CapturePauseDiagnostic("Operator requested pause.",beforePause,"Owner or software safety request.");
                message+=" Recording stopped for owner safety; its unconfirmed tail is unknown and this recording cannot resume.";return;}
            try
            {
                if(recording!=null&&!recordingTerminal)recording.Pause();else participant.OwnerPause(Actor.Owner);
                bool stopped=participant.Phase==Phase.Paused||participant.Phase==Phase.Ended;
                if(!stopped){faultLatched=true;view.SetPresentationAvailable(false);rig.BlockTaskPresentation();}
                message=stopped?
                    "Task paused or ended.":"Pause was requested but a safe stop is not confirmed.";
            }
            catch(Exception e)
            {
                faultLatched=true;view.SetPresentationAvailable(false);rig.BlockTaskPresentation();
                message=(participant.Phase==Phase.Paused||participant.Phase==Phase.Ended?
                    "Pause encountered an error; task is paused or ended. ":
                    "Pause outcome is not confirmed; do not retry without review. ")+e.GetType().Name;
            }
            finally{CapturePauseDiagnostic("Operator requested pause.",beforePause,"Owner or software safety request.");FinishSafetyAction();}
        }
        void RequestExplicitRawPause(ViveDevelopmentParticipant.NamedRawSafetyCause cause)
        {
            if(namedRawPump==null)return;
            // A later explicit pause may upgrade a technical hold. It cannot
            // override withdrawal or repeat an already attempted explicit pause.
            if(!rawWithdrawalRequested&&!namedRawPump.WithdrawalRequested&&
                (!rawSafetyLatched||rawSafetyCause==ViveDevelopmentParticipant.NamedRawSafetyCause.TechnicalFault))
            {rawSafetyCause=cause;rawPauseAttempted=false;}
            FailNamedRawBeforeFrame();
        }
        void WithdrawTask()
        {
            if(namedRawPump!=null){try{RequestNamedRawWithdrawal();}finally{ClearNamedTaskView();}return;}
            ClearNamedTaskView();
            StopPracticeObservation();
            try
            {
                if(recording!=null&&!recordingTerminal)recording.Withdraw();else participant.ParticipantWithdraw();
                bool withdrawn=participant.Phase==Phase.Ended&&participant.EndReason==EndReason.Withdrawn;
                if(!withdrawn){faultLatched=true;view.SetPresentationAvailable(false);rig.BlockTaskPresentation();}
                message=withdrawn?
                    "Task withdrawn.":"Withdrawal was requested but the task end is not confirmed.";
            }
            catch(Exception e)
            {
                faultLatched=true;view.SetPresentationAvailable(false);rig.BlockTaskPresentation();
                message=(participant.Phase==Phase.Ended&&participant.EndReason==EndReason.Withdrawn?
                    "Withdrawal encountered an error; task is withdrawn. ":
                    "Withdrawal outcome is not confirmed; do not retry without review. ")+e.GetType().Name;
            }
            finally
            {
                // Withdrawal also ends this preview's eye capture. A stop
                // request prevents new publications, while native cleanup may
                // still be pending and must not be reported as complete.
                try{if(rig!=null)rig.StopEyeAfterWithdrawal();}
                catch(Exception e){faultLatched=true;view.SetPresentationAvailable(false);rig.BlockTaskPresentation();message="Eye stop after withdrawal is unconfirmed: "+e.GetType().Name;}
                FinishSafetyAction();
                CloseLegacyWozScene();
            }
        }
        void FinishSafetyAction()
        {
            CheckObservation(true);
            try{Render();}
            catch(Exception e)
            {
                faultLatched=true;view.SetPresentationAvailable(false);rig.BlockTaskPresentation();
                message="Safety action may already have been applied; display failed. Review task state before retrying: "+e.GetType().Name;
            }
        }
        void Run(Action action)
        {
            if(namedRawPump!=null)
            {
                if(action==null||rawSelectionProvenancePending||participant==null||!participant.RawRecordingRequested||participant.RawContext==null||participant.Phase!=Phase.BlockPreparation||
                    !participant.InputReady||RecordingBlocked||pendingRawOwnerAction!=null)
                {message="Owner action was not queued; review current task and input readiness.";return;}
                ViveDevelopmentParticipant.NamedRecordingTaskStamp stamp;
                if(sharedClock==null||!participant.TryCaptureRawRecordingTaskStamp(sharedClock,out stamp)||stamp.Context==null)
                {message="Owner action was not queued; current task authority is unavailable.";return;}
                pendingRawOwnerAction=action;pendingRawOwnerParticipant=participant;pendingRawOwnerPlan=participant.NamedPlan;
                pendingRawOwnerContext=stamp.Context;
                pendingRawOwnerStamp=stamp;
                pendingRawOwnerPhase=participant.Phase;
                message="Owner action queued for the next healthy, neutral input frame.";
                return;
            }
            bool applied=false,started=false,completed=false;
            Action tracked=()=>{started=true;action();completed=true;};
            try{applied=Mutate(tracked);if(applied)message="Owner action accepted.";}
            catch(Exception error)
            {
                message=(!started?"Owner action did not start. Review required: ":completed?
                    "Owner action completed, but later processing failed; do not retry. ":
                    "Owner action may have been partially applied; do not retry. ")+error.GetType().Name;
                faultLatched=true;view.SetPresentationAvailable(false);rig.BlockTaskPresentation();
                long beforePause=participant.PauseDiagnosticRevision;
                try{if(recording!=null&&!recordingTerminal)recording.Pause();else if(ObservationPausable(participant.Phase))participant.OwnerPause(Actor.Owner);}catch(Exception){}
                CapturePauseDiagnostic("Automatic safety: owner-action or display error "+error.GetType().Name+".",beforePause,"Owner or software safety request.");
                CheckObservation(true);return;
            }
            try{Render();}
            catch(Exception error)
            {
                message=(completed?"Owner action completed, but display failed; do not retry. ":started?
                    "Action may have been partially applied; display failed; do not retry. ":
                    "Owner action did not start; display failed. ")+error.GetType().Name;
                faultLatched=true;view.SetPresentationAvailable(false);rig.BlockTaskPresentation();
                long beforePause=participant.PauseDiagnosticRevision;
                try{if(recording!=null&&!recordingTerminal)recording.Pause();else if(ObservationPausable(participant.Phase))participant.OwnerPause(Actor.Owner);}catch(Exception){}
                CapturePauseDiagnostic("Automatic safety: owner-action or display error "+error.GetType().Name+".",beforePause,"Owner or software safety request.");
                CheckObservation(true);
            }
        }
        static Vec3 ToVec(Vector3 value){return new Vec3(value.x,value.y,value.z);}
    }
}
#endif

// Pure presentation only: this class cannot admit recording, open a file or
// change task state. Kept outside the XR guard for headless text checks and
// the shared view; inactive non-VIVE callers retain their original wording.
namespace AvatarStroop.Unity
{
    internal static class ViveStroopRecordingScopePresentation
    {
        internal const string LegacyBanner="LEGACY PREVIEW | RECORDING/EXPORT UNAVAILABLE";
        internal const string LegacyMemoryNotice="No eye/task files. Task records: memory only until close.";
        internal const string LegacyNotice="LEGACY PREVIEW: recording/export unavailable. No eye or task files are created. Task records are memory-only until this preview closes; they are not saved.";
        internal static bool IsLegacy(int selectedDesign,bool hasParticipant,bool hasNamedOption)
        {return hasParticipant?!hasNamedOption:selectedDesign==0;}
        // These are UI eligibility checks only. Storage, observed choices,
        // exact sources and runtime authority are checked by their owners.
        internal static bool DesignAvailable(int selectedDesign,bool controlled,bool legacy)
        {return controlled&&(selectedDesign==3||selectedDesign==0&&legacy);}
        internal static bool SetupMatches(int selectedDesign,int setupDesign)
        {return (selectedDesign==0||selectedDesign==3)&&selectedDesign==setupDesign;}
        internal static bool SetupEligible(int selectedDesign,int setupDesign,bool available,bool notice,bool taskChoice,bool eyeChoice)
        {return SetupMatches(selectedDesign,setupDesign)&&available&&notice&&taskChoice&&eyeChoice;}
        internal static bool ExactContextMatches(object expected,object current)
        {return expected!=null&&object.ReferenceEquals(expected,current);}
        internal static bool KindMatches(bool legacy,bool namedRequested,bool legacyRequested)
        {return legacy?legacyRequested&&!namedRequested:namedRequested&&!legacyRequested;}
        internal static string LegacySelectionNotice(bool available)
        {return available?"Legacy A/B/C/D selected. Configure a memory-only preview, or request the optional controlled recording self-test below. No recording has started.":LegacyNotice;}
        internal static string BuildDesignScope(bool controlled,bool legacy)
        {return controlled&&legacy?
            "Controlled recording support is available for explicit Legacy A/B/C/D or Named multi-block self-tests, after separate task/eye choices and an explicit start. Preview configuration never records. No two-PC task or study validation.":BuildScope(controlled);}
        internal static string OwnerDesignHeading(bool legacySelected,bool recordingRequested,bool configured,bool controlled,bool legacyAvailable)
        {return legacySelected&&recordingRequested?"VIVE - LEGACY CONTROLLED RECORDING SELF-TEST":
            legacySelected&&!configured&&controlled&&legacyAvailable?"VIVE - LEGACY PREVIEW / RECORDING OPTIONS":OwnerHeading(legacySelected,controlled);}
        internal static string BuildScope(bool controlled)
        {return controlled?
            "Controlled recording support is limited to Named explicit multi-block simulations, after separate task/eye choices and an explicit start. Legacy A/B/C/D has no recording or export. No two-PC task or study validation.":
            "Simulated development task. Local VIVE recording is unavailable. Unrecorded Legacy WoZ requires explicit pairing. Hardware, LAN and study validation pending. Legacy A/B/C/D has no recording or export.";}
        internal static string OwnerHeading(bool legacy,bool controlled)
        {return legacy?"VIVE - LEGACY PREVIEW - NO RECORDING / EXPORT":controlled?
            "VIVE - NAMED RECORDING / PREVIEW OPTIONS":"VIVE CLASSROOM - DEVELOPMENT VIEW ONLY";}
        internal static string Banner(bool legacy,string otherwise)
        {return legacy?LegacyBanner:otherwise;}
        internal static string Controls(bool legacy,string controls)
        {return legacy?(string.IsNullOrEmpty(controls)?LegacyMemoryNotice:controls+"\n"+LegacyMemoryNotice):controls;}
        internal static string LegacyEndMessage(bool completed)
        {return (completed?"Legacy preview complete.":"Legacy preview ended.")+"\nNo eye or task files were saved.\nTask records remain in memory only until close.";}
    }
}

// Pure configuration construction used by the scene. An intent is not an
// admission, recording start, participant authorization or file operation.
namespace AvatarStroop.Unity
{
    using AvatarStroop.Core;
    internal static class ViveLegacySceneConfiguration
    {
        internal static SessionConfiguration CreatePreview(string session)
        {
            var allocation=new Allocation("development-fixture-BCAD",new[]{Condition.B,Condition.C,Condition.A,Condition.D},GazeTarget.Participant,new[]{0,1,2,3});
            return new SessionConfiguration(session,20261028,allocation,RunKind.Simulation,"en",3,30,true,0,true,true,true);
        }
        internal static SessionConfiguration SelectRecording(SessionConfiguration preview,string geometryVersion,string mappingVersion,string designId,string intentId,bool operatorWoz=false)
        {
            if(preview==null)throw new System.ArgumentNullException("preview");
            if(operatorWoz&&!ViveLegacyWozRecordingBuildPolicy.Available)throw new InvalidOperationException("Controlled WoZ recording build required.");
            var design=operatorWoz?ViveLegacyRecordingDesign.SelectOperatorWoz(Actor.Owner,designId,preview.Allocation,geometryVersion,mappingVersion):
                ViveLegacyRecordingDesign.Select(Actor.Owner,designId,preview.Allocation,geometryVersion,mappingVersion);
            var intent=ViveLegacyRecordingIntent.Select(Actor.Owner,intentId,true,true);
            return SessionConfiguration.LegacyRecordingDevelopment(preview,design,intent);
        }
    }
}
