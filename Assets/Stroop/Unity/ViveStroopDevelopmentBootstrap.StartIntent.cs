#if STROOP_XR_PREVIEW
using System;
using UnityEngine;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Unity
{
    public sealed partial class ViveStroopDevelopmentBootstrap
    {
        const double RawOwnerIntentTimeoutSeconds=180;
        sealed class RawStartIntent
        {
            internal ViveNamedRecordingSetup Choices;
            internal ViveDevelopmentParticipant Participant;
            internal MonotonicClock Clock;
            internal ViveRawByteBudget Budget;
            internal ViveProEyeWorker Worker;
            internal long WorkerGeneration;
            internal ViveRecordingStartClaim Claim;
            internal string Folder;
            internal int Revision,Design;
            internal bool Legacy,Output,Csv;
            internal long CsvBytes;
            internal int CsvTimeout;
            internal double RequestedAt;
            internal ConsentAutoStartArm AutomaticArm;
        }
        RawStartIntent rawStartIntent;
        bool rawPracticeRequested;
        ViveDevelopmentParticipant rawPracticeTask;
        MonotonicClock rawPracticeClock;
        Phase rawPracticePhase;
        double rawPracticeRequestedAt;
        internal static bool RawIntentTimeCurrent(double requested,double now)
        {return !Double.IsNaN(requested)&&!Double.IsInfinity(requested)&&!Double.IsNaN(now)&&!Double.IsInfinity(now)&&
            requested>=0&&now>=requested&&now-requested<RawOwnerIntentTimeoutSeconds;}
        // Eye acquisition is asynchronous. The exact worker validates a 0.5 s
        // continuous valid streak with advancing samples no older than 0.25 s,
        // not whether Unity consumed another distinct eye sample this Update.
        // The one-use claim rechecks that worker/run under its lock at install.
        // Head/XR and neutral controller evidence must still be current-frame;
        // this helper grants neither an owner request nor a file capability.
        internal static bool RawStartInputsReady(ViveNamedRecordingSetup choices,bool xrFocused,bool sceneTrackingCurrent,
            bool workerReady,ViveProEyeWorker.RecordingStartReadiness? eyeReadiness,int neutralFrame,int currentFrame)
        {return choices!=null&&eyeReadiness.HasValue&&eyeReadiness.Value.Ready&&
            choices.CanStart(true,xrFocused,sceneTrackingCurrent,workerReady,neutralFrame,currentFrame);}
        bool RawStartCanBeRequested(bool legacy,bool output,bool csv)
        {
            return configured&&recordingSetup!=null&&recordingSetup.BothSelected&&RecordingSetupEligible&&
                recordingSetupDesign==(legacy?0:3)&&participant!=null&&MatchesRawRecordingKind(legacy)&&
                participant.Phase==Phase.Preparation&&!participant.Completed&&namedRawPump==null&&rig!=null&&
                !RecordingBlocked&&RecordingKindAvailable(legacy)&&ViveRawRelativeFileCapability.ProductionGateOpen&&
                (!output||ViveRawExclusiveDestination.ProductionGateOpen)&&(!csv||output&&ViveRawReadableAnalysisCsv.ProductionGateOpen);
        }
        void RequestRawRecordingStart(bool legacy,bool output,bool csv,long csvBytes,int csvTimeout)
        {
            if(rawStartIntent!=null||!RawStartCanBeRequested(legacy,output,csv))return;
            if(csv&&(csvBytes<1024||csvBytes>ViveRawByteBudget.MaximumSupportedBytes||csvTimeout<1||csvTimeout>3600000))return;
            consentAutoStartNotice=null;
            double taskPoll,eyePoll,now;
            if(!recordingSetup.TryGetObservedTimes(sharedClock,out taskPoll,out eyePoll)||!sharedClock.TryReadRecordingAdmissionTime(out now))return;
            var startWorker=rig.CurrentRawStartWorker;
            if(startWorker==null){message="Enable eye tracking before requesting recording. No recording has started.";return;}
            rawStartIntent=new RawStartIntent{Choices=recordingSetup,Participant=participant,Clock=sharedClock,
                Budget=recordingSetup.Budget,Folder=recordingSetup.DirectoryPath,Revision=recordingSetup.Revision,
                Design=recordingSetupDesign,Legacy=legacy,Output=output,Csv=csv,CsvBytes=csvBytes,CsvTimeout=csvTimeout,RequestedAt=now,
                Worker=startWorker,WorkerGeneration=startWorker.RecordingStartGeneration,Claim=new ViveRecordingStartClaim(startWorker),
                AutomaticArm=consentAutoStart!=null&&consentAutoStart.RequestsCaptured?consentAutoStart:null};
            message="Start requested. Recording has not started. Keep the headset on, restore tracking, and release Trigger, Menu and trackpad.";
            Debug.Log("VIVE_RAW_START_REQUESTED: waiting for current neutral controller and head/eye validation.");
        }
        bool PendingRawStartStillMatches(RawStartIntent intent)
        {
            double now;
            return intent!=null&&intent.Clock!=null&&intent.Clock.TryReadRecordingAdmissionTime(out now)&&RawIntentTimeCurrent(intent.RequestedAt,now)&&RawStartCanBeRequested(intent.Legacy,intent.Output,intent.Csv)&&
                (intent.AutomaticArm==null||object.ReferenceEquals(intent.AutomaticArm,consentAutoStart)&&ConsentAutoStartContextCurrent(intent.AutomaticArm)&&ConsentAutoStartDeadlineCurrent(intent.AutomaticArm))&&
                object.ReferenceEquals(intent.Choices,recordingSetup)&&object.ReferenceEquals(intent.Participant,participant)&&
                object.ReferenceEquals(intent.Clock,sharedClock)&&object.ReferenceEquals(intent.Budget,recordingSetup.Budget)&&
                intent.Worker!=null&&object.ReferenceEquals(intent.Worker,rig.CurrentRawStartWorker)&&intent.WorkerGeneration==intent.Worker.RecordingStartGeneration&&
                intent.Revision==recordingSetup.Revision&&intent.Design==recordingSetupDesign&&
                String.Equals(intent.Folder,recordingSetup.DirectoryPath,StringComparison.Ordinal);
        }
        void CancelRequestedRawRecordingStart()
        {
            if(rawStartIntent==null)return;
            rawStartIntent=null;
            CancelConsentAutoStart("Automatic recording start canceled. No recording has started.");
            ClearRawPracticeRequest();
            message="Pending recording start canceled. No recording has started.";
            Debug.Log("VIVE_RAW_START_CANCELED: no recording installation attempted.");
        }
        // The claim clears a request before any install action. A rejected
        // install cannot turn into an implicit retry on a later frame.
        static bool CanClaimRawStart(bool requested,bool exactContext,bool currentFrameReady)
        {return requested&&exactContext&&currentFrameReady;}
        bool ServiceRequestedRawRecordingStart()
        {
            var intent=rawStartIntent;if(intent==null)return false;
            if(!PendingRawStartStillMatches(intent)){CancelRequestedRawRecordingStart();return false;}
            // The claim holds the exact worker lock through validation and
            // install. Its callback is attempted once; never a retry after
            // an uncertain side effect, and never a permission substitute.
            bool attached=false;
            var claim=intent.Claim.TryExecute(()=>PendingRawStartStillMatches(intent),
                ()=>CanClaimRawStart(true,true,CanStartSelectedRawRecording(intent.Legacy)),()=>{
                    rawStartIntent=null;
                    if(intent.Csv)
                        attached=intent.Legacy?TryCreateAndAttachLegacyRawInstalledWithCsv(intent.Folder,intent.Budget,true,true,true,true,intent.CsvBytes,intent.CsvTimeout):
                            TryCreateAndAttachNamedRawInstalledWithCsv(intent.Folder,intent.Budget,true,true,true,true,intent.CsvBytes,intent.CsvTimeout);
                    else
                        attached=intent.Legacy?TryCreateAndAttachLegacyRawInstalledWithOwnerOutput(intent.Folder,intent.Budget,true,true,intent.Output):
                            TryCreateAndAttachNamedRawInstalledWithOwnerOutput(intent.Folder,intent.Budget,true,true,intent.Output);
                });
            if(claim==ViveRecordingStartClaim.Result.Invalid){CancelRequestedRawRecordingStart();return false;}
            if(claim==ViveRecordingStartClaim.Result.Waiting)
            {message=RecordingStartBlocker();return false;}
            if(!attached)
            {
                bool automatic=rawPracticeFromConsent;
                ClearRawPracticeRequest();
                message=rawInstallFailed?"Recording start failed; cleanup and any output may be unconfirmed.":"Recording start was rejected; no recording was installed.";
                if(automatic)consentAutoStartNotice=message+" No automatic retry.";
                Debug.LogWarning("VIVE_RAW_START_NOT_INSTALLED: installation status reviewed; no automatic retry.");
            }
            else
            {
                suppressInputUntilReleased=true;
                message="Recording installed. Waiting for initial captured evidence; file creation and durability are not yet confirmed.";
                Debug.Log("VIVE_RAW_START_INSTALLED: local writer/admission attached; file creation not yet confirmed.");
            }
            return true;
        }
        void CaptureRawPracticeRequest()
        {
            double now;
            if(sharedClock==null||!sharedClock.TryReadRecordingAdmissionTime(out now))return;
            consentAutoStartNotice=null;
            rawPracticeRequested=true;rawPracticeTask=participant;rawPracticeClock=sharedClock;
            rawPracticePhase=participant.Phase;rawPracticeRequestedAt=now;
            message="Practice start requested. Waiting for recording, current inputs and headset confirmation.";
            Debug.Log("VIVE_RAW_PRACTICE_REQUESTED: no trial has started.");
        }
        void ClearRawPracticeRequest()
        {
            rawPracticeRequested=false;rawPracticeTask=null;rawPracticeClock=null;
            if(consentAutoStart!=null&&consentAutoStart.RequestsCaptured)consentAutoStart=null;
            ClearConsentPracticeOrigin();
        }
        void ExpireRawOwnerIntents()
        {
            if(rawStartIntent!=null&&!PendingRawStartStillMatches(rawStartIntent))CancelRequestedRawRecordingStart();
            if(!rawPracticeRequested)return;
            double now;
            if(participant==null||!object.ReferenceEquals(rawPracticeTask,participant)||!object.ReferenceEquals(rawPracticeClock,sharedClock)||
                rawPracticePhase!=participant.Phase||sharedClock==null||!sharedClock.TryReadRecordingAdmissionTime(out now)||
                !RawIntentTimeCurrent(rawPracticeRequestedAt,now))
            {
                bool automatic=rawPracticeFromConsent;
                ClearRawPracticeRequest();message="Practice request expired or task changed. No trial was started.";
                if(automatic)consentAutoStartNotice=message;
                Debug.Log("VIVE_RAW_PRACTICE_CANCELED: expired or changed task context.");
            }
        }
        string PracticeExecutionBlocker()
        {
            if(rawSelectionProvenancePending)return "Begin practice: waiting for initial head/eye evidence and software choices.";
            if(namedRawPump==null||namedRawPump.TrackingGapOpen)return "Begin practice: recording or tracking is not ready.";
            if(suppressInputUntilReleased||!ControllerUiReady||!participant.InputReady)return "Begin practice: waiting for a current neutral controller.";
            if(rig==null||!rig.HeadReady||!rig.XrFocused)return "Begin practice: waiting for current headset tracking and XR focus.";
            return null;
        }
    }
}
#endif
