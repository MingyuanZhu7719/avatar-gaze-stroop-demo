#if STROOP_XR_PREVIEW
using System;
using AvatarStroop.Core;
using AvatarStroop.Runtime;
using UnityEngine;

namespace AvatarStroop.Unity
{
    public sealed partial class ViveStroopDevelopmentBootstrap
    {
        const double ConsentAutoArmTimeoutSeconds=900;
        internal static bool ConsentAutoArmTimeCurrent(double armed,double now)
        {return !Double.IsNaN(armed)&&!Double.IsInfinity(armed)&&!Double.IsNaN(now)&&!Double.IsInfinity(now)&&
            armed>=0&&now>=armed&&now-armed<ConsentAutoArmTimeoutSeconds;}
        // A positive owner action, separate from choosing a directory and from
        // both participant choices. It is never persisted across a view/session.
        sealed class ConsentAutoStartArm
        {
            internal ViveNamedRecordingSetup Choices;
            internal ViveRawByteBudget Budget;
            internal string Folder,DirectorySelection,RawLimit,CsvLimit,CsvTimeout;
            internal int Design,InitialRevision,PlanCount;
            internal int[] Options,Lists;
            internal bool Woz,RequestsCaptured,Interrupted;
            internal long CsvBytes,WorkerGeneration;
            internal int CsvMilliseconds;
            internal MonotonicClock DeadlineClock,ViewClock;
            internal double ArmedAt,ConfirmedAt;
            internal ViveProEyeWorker Worker;
            internal ViveDevelopmentParticipant Task;
        }
        ConsentAutoStartArm consentAutoStart;
        string consentAutoStartNotice;
        bool rawPracticeFromConsent,rawConsentInterrupted;
        ViveDevelopmentParticipant rawConsentPracticeTask;
        MonotonicClock rawConsentPracticeClock;

        internal bool ConsentAutoStartActive {get{return consentAutoStart!=null;}}
        // Keep every material disclosure in the rendered choice itself. The
        // consent-only HUD layout measures this full text; never accept a
        // clipped notice as the participant's rendered-choice witness.
        internal static string ConsentAutoChoiceText(bool eye,bool woz)
        {
            string text=eye?
                "Self-test, not study consent. Store real eye/head data on this PC? Eye data may identify you. No upload. Withdrawal does not erase files.":
                "Self-test, not study consent. Store task responses, questionnaire ratings and timing on this PC?";
            if(woz)text+=" LiveWoZ also stores mapped operator head/eye poses and source times.";
            return text+" Both choices + release start local raw/CSV recording and Practice. Formal blocks require the operator.";
        }
        internal void ArmConsentAutoStart()
        {
            if(consentAutoStart!=null||configured||rig==null||rig.ViewRunning||ownerRunMode!=1||
                !ControlledRecordingNoticeCurrent||!SelectedRecordingAvailable||
                !ViveRawExclusiveDestination.ProductionGateOpen||!ViveRawReadableAnalysisCsv.ProductionGateOpen)return;
            long bytes;int milliseconds;
            if(!TryParseCsvLimits(csvBudgetMiB,csvCleanupSeconds,out bytes,out milliseconds))return;
            if(TryQueueSessionDirectoryPreparation())return;
            var previous=recordingSetup;
            BeginRawRecordingSetup(selectedDesign==0);
            if(recordingSetup==null||object.ReferenceEquals(previous,recordingSetup)||recordingSetup.Current!=ViveNamedRecordingSetup.Step.Task)return;
            var deadlineClock=new MonotonicClock("local-recording-owner-arm");double now;
            if(!deadlineClock.TryReadRecordingAdmissionTime(out now)){CancelRecordingChoices();return;}
            consentAutoStart=new ConsentAutoStartArm{Choices=recordingSetup,Budget=recordingSetup.Budget,Folder=recordingSetup.DirectoryPath,
                DirectorySelection=recordingDirectory,RawLimit=recordingBudgetMiB,CsvLimit=csvBudgetMiB,CsvTimeout=csvCleanupSeconds,Design=selectedDesign,
                InitialRevision=recordingSetup.Revision,PlanCount=namedPlanCount,Options=(int[])namedPlanOptions.Clone(),Lists=(int[])namedPlanLists.Clone(),
                Woz=legacyWozRecordingSelected,CsvBytes=bytes,CsvMilliseconds=milliseconds,DeadlineClock=deadlineClock,ArmedAt=now};
            consentAutoStartNotice=null;
            message="Armed for local raw + CSV recording and first Practice after both headset choices and release of all controls. Start the classroom view. No files yet.";
            Debug.Log("VIVE_CONSENT_AUTO_START_ARMED: explicit owner raw/CSV + first-Practice request; both headset choices still required.");
            RenderPreSessionChoice();
        }
        static bool SameAutoPlan(int[] saved,int[] current)
        {
            if(saved==null||current==null||saved.Length!=current.Length)return false;
            for(int i=0;i<saved.Length;i++)if(saved[i]!=current[i])return false;
            return true;
        }
        bool ConsentAutoStartContextCurrent(ConsentAutoStartArm arm)
        {return ControlledRecordingNoticeCurrent&&ConsentAutoStartSelectionsMatch(arm);}
        bool ConsentAutoStartDeadlineCurrent(ConsentAutoStartArm arm)
        {
            double now;
            return arm!=null&&arm.DeadlineClock!=null&&arm.DeadlineClock.TryReadRecordingAdmissionTime(out now)&&
                ConsentAutoArmTimeCurrent(arm.ArmedAt,now)&&(arm.Task==null||RawIntentTimeCurrent(arm.ConfirmedAt,now));
        }
        // Pure identity/configuration matching, kept separate from the actual
        // build/owner gate so Edit Mode can verify it without opening that gate.
        bool ConsentAutoStartSelectionsMatch(ConsentAutoStartArm arm)
        {
            if(arm==null||recordingSetup==null||!object.ReferenceEquals(arm.Choices,recordingSetup)||
                !object.ReferenceEquals(arm.Budget,recordingSetup.Budget)||arm.Folder!=recordingSetup.DirectoryPath||
                arm.Design!=selectedDesign||arm.Design!=recordingSetupDesign||ownerRunMode!=1||
                arm.DirectorySelection!=recordingDirectory||arm.RawLimit!=recordingBudgetMiB||arm.CsvLimit!=csvBudgetMiB||arm.CsvTimeout!=csvCleanupSeconds||
                arm.Woz!=legacyWozRecordingSelected||arm.PlanCount!=namedPlanCount||!SameAutoPlan(arm.Options,namedPlanOptions)||!SameAutoPlan(arm.Lists,namedPlanLists))return false;
            int accepted=recordingSetup.Current==ViveNamedRecordingSetup.Step.Task?0:
                recordingSetup.Current==ViveNamedRecordingSetup.Step.Eye?1:recordingSetup.BothSelected?2:-1;
            if(accepted<0||recordingSetup.Revision!=arm.InitialRevision+accepted)return false;
            if(arm.ViewClock!=null&&(rig==null||!object.ReferenceEquals(arm.ViewClock,rig.ActiveViewClock)))return false;
            if(arm.Worker!=null&&(rig==null||!object.ReferenceEquals(arm.Worker,rig.CurrentRawStartWorker)||arm.WorkerGeneration!=arm.Worker.RecordingStartGeneration))return false;
            if(arm.Task!=null&&(!object.ReferenceEquals(arm.Task,participant)||!object.ReferenceEquals(arm.ViewClock,sharedClock)||participant.Phase!=Phase.Preparation))return false;
            return true;
        }
        void ObserveConsentAutoStartFrame(bool headReady,bool xrFocused)
        {
            var arm=consentAutoStart;
            if(rawPracticeFromConsent&&(!headReady||!xrFocused))rawConsentInterrupted=true;
            if(arm==null)return;
            // Startup before the final choice is not a recovery. After the
            // final explicit choice, loss before install is sticky and cannot
            // be erased by the recording's initial focus baseline.
            if(recordingSetup!=null&&recordingSetup.BothSelected&&(!headReady||!xrFocused))arm.Interrupted=true;
            if(!ConsentAutoStartContextCurrent(arm)||!ConsentAutoStartDeadlineCurrent(arm)||RecordingBlocked)
            {
                CancelConsentAutoStart("Automatic start expired, was canceled, or its setup changed. No automatic retry; review on the desktop.");
                return;
            }
            if(arm.ViewClock==null&&rig!=null&&rig.ActiveViewClock!=null)arm.ViewClock=rig.ActiveViewClock;
            if(arm.Worker==null&&rig!=null&&rig.CurrentRawStartWorker!=null)
            {arm.Worker=rig.CurrentRawStartWorker;arm.WorkerGeneration=arm.Worker.RecordingStartGeneration;}
        }
        void ConsentAutoStartConfigured()
        {
            var arm=consentAutoStart;if(arm==null)return;
            if(!ConsentAutoStartContextCurrent(arm)||!recordingSetup.BothSelected||participant==null||participant.Phase!=Phase.Preparation||
                rig==null||sharedClock==null||!object.ReferenceEquals(sharedClock,rig.ActiveViewClock))
            {CancelConsentAutoStart("Automatic start setup changed. Review on the desktop; no recording or Practice was started.");return;}
            double confirmed;
            if(!arm.DeadlineClock.TryReadRecordingAdmissionTime(out confirmed))
            {CancelConsentAutoStart("Automatic start clock unavailable. Review on the desktop; no recording or Practice was started.");return;}
            arm.Task=participant;arm.ViewClock=sharedClock;arm.ConfirmedAt=confirmed;
            message="Both choices accepted. Release Trigger, Menu and trackpad. Local recording and Practice will start when tracking is ready.";
        }
        void ServiceConsentAutoStart()
        {
            var arm=consentAutoStart;
            if(arm==null||arm.RequestsCaptured||!ConsentAutoStartContextCurrent(arm)||arm.Task==null||!configured||
                !recordingSetup.BothSelected||recordingSetup.TrackingSuspended||rig==null||!rig.HeadReady||!rig.XrFocused||
                !rig.RawSceneTrackingCurrent()||!ControllerUiReady||recordingSelectionFreshFrame!=Time.frameCount||
                arm.Worker==null||rawStartIntent!=null||namedRawPump!=null)return;
            if(!ConsentAutoStartDeadlineCurrent(arm))
            {CancelConsentAutoStart("Automatic start expired. No automatic retry; review on the desktop.");return;}
            // Consume BEFORE either request. A refusal/exception cannot leave
            // an auto-practice request to piggyback a later manual recording.
            arm.RequestsCaptured=true;
            try
            {
                RequestRawRecordingStart(arm.Design==0,true,true,arm.CsvBytes,arm.CsvMilliseconds);
                if(rawStartIntent==null){CancelConsentAutoStart("Automatic recording start was rejected. No automatic retry; review on the desktop.");return;}
                CaptureRawPracticeRequest();
                if(!rawPracticeRequested){CancelRequestedRawRecordingStart();return;}
                rawPracticeFromConsent=true;rawConsentInterrupted=arm.Interrupted;
                rawConsentPracticeTask=participant;rawConsentPracticeClock=sharedClock;
                message="Both choices accepted and controls released. Starting local recording, then Practice automatically; formal blocks still need the operator.";
                Debug.Log("VIVE_CONSENT_AUTO_START_REQUESTED: existing one-use recording and first-Practice paths captured once after neutral release.");
            }
            catch(Exception)
            {
                CancelRequestedRawRecordingStart();ClearRawPracticeRequest();
                CancelConsentAutoStart("Automatic start could not be completed safely. No automatic retry; review recording status on the desktop.");
                throw;
            }
        }
        void NoteConsentAutoStartInterruption()
        {
            if(consentAutoStart!=null&&consentAutoStart.Choices.BothSelected)consentAutoStart.Interrupted=true;
            if(rawPracticeFromConsent)rawConsentInterrupted=true;
        }
        bool AutomaticFirstPracticeMayProceed()
        {return rawPracticeFromConsent&&!rawConsentInterrupted&&participant!=null&&participant.Phase==Phase.Preparation&&
            object.ReferenceEquals(rawConsentPracticeTask,participant)&&object.ReferenceEquals(rawConsentPracticeClock,sharedClock)&&
            rig!=null&&rig.HeadReady&&rig.XrFocused;}
        void ClearConsentPracticeOrigin()
        {rawPracticeFromConsent=false;rawConsentInterrupted=false;rawConsentPracticeTask=null;rawConsentPracticeClock=null;}
        void CancelConsentAutoStart(string reason)
        {
            bool had=consentAutoStart!=null||rawPracticeFromConsent;
            if(!had)return;
            consentAutoStart=null;ClearConsentPracticeOrigin();
            if(rawStartIntent!=null)CancelRequestedRawRecordingStart();
            if(rawPracticeRequested)ClearRawPracticeRequest();
            if(had){consentAutoStartNotice=reason;message=reason;Debug.Log("VIVE_CONSENT_AUTO_START_CANCELED: pending automatic actions cleared; no automatic retry.");}
        }
        string ConsentAutoStartStatus
        {
            get
            {
                if(consentAutoStart==null&&!rawPracticeFromConsent)
                    return rawStartIntent==null&&namedRawPump==null&&!rawPracticeRequested&&rawPlanStartRequested==0&&rawPendingBlockPlan==null&&
                        !RecordingBlocked&&!ObservationBlocked&&!focusPlanConfirmationRequired&&!focusPlanTriggerAccepted?consentAutoStartNotice:null;
                if(recordingSetup==null||!recordingSetup.BothSelected)return "Automatic recording + Practice armed. Both separate headset choices are required. No files have been requested.";
                if(namedRawPump==null)return "Automatic recording + Practice: "+RecordingStartBlocker();
                if(focusPlanConfirmationRequired||focusPlanTriggerAccepted)return FocusPlanAwaitNotice;
                if(rawPlanStartRequested!=0||rawPendingBlockPlan!=null)return "Preparing Practice. Waiting for the block-plan write and flush confirmation; release all controls.";
                return PracticeExecutionBlocker()??"Local recording started. Preparing first Practice automatically. Formal blocks still require the operator.";
            }
        }
    }
}
#endif
