#if STROOP_XR_PREVIEW
using System;
using System.IO;
using System.Threading;
using AvatarStroop.Core;
using AvatarStroop.Runtime;
using UnityEngine;

namespace AvatarStroop.Unity
{
    public sealed partial class ViveStroopDevelopmentBootstrap
    {
        ViveLocalSessionDirectory sessionDirectoryOffer;
        bool sessionDirectoryOffered,sessionDirectoryAbandoned;
        string sessionDirectoryNotice;
        bool sessionDirectoryValidationRejected;
        SessionDirectoryPreparation sessionDirectoryPreparation;
        internal bool SessionDirectoryPreparationPending { get { return sessionDirectoryPreparation!=null; } }

        // Pure setup diagnostics, not storage probes or authority. Unselected
        // values remain explicit; choosing a block never invents its option/list.
        internal string NamedBlockSelectionValidationReason(int index)
        {
            if(index<0||index>=namedPlanOptions.Length||index>=namedPlanLists.Length)
                return "Select a supported block.";
            bool option=namedPlanOptions[index]>=0&&namedPlanOptions[index]<=1;
            bool list=namedPlanLists[index]>=0&&namedPlanLists[index]<=3;
            string prefix="Block "+(index+1)+": ";
            if(!option&&!list)return prefix+"select Automatic or No Avatar and a stimulus list (0-3).";
            if(!option)return prefix+"select Automatic or No Avatar.";
            if(!list)return prefix+"select a stimulus list (0-3).";
            return null;
        }
        internal string RecordingSetupValidationReason()
        {
            if(ParticipantBuild&&EntryAssignment==null)return "Enter the assigned anonymous participant ID (P001 to P999).";
            if(selectedDesign!=0&&selectedDesign!=3)return "Select Legacy A/B/C/D or Named multi-block for recording.";
            if(selectedDesign==3)
            {
                if(namedPlanCount<0||namedPlanCount>3)return "Select the number of blocks (1-4).";
                for(int i=0;i<=namedPlanCount;i++)
                {var reason=NamedBlockSelectionValidationReason(i);if(reason!=null)return reason;}
            }
            long mib;
            if(!Int64.TryParse(recordingBudgetMiB,System.Globalization.NumberStyles.None,System.Globalization.CultureInfo.InvariantCulture,out mib)||
                mib<1||mib>8192)return "Enter a raw maximum size from 1 to 8192 MiB under Advanced storage limits.";
            long bytes;int timeout;
            if(!TryParseCsvLimits(csvBudgetMiB,csvCleanupSeconds,out bytes,out timeout))
                return "Enter a CSV maximum size from 1 to 8192 MiB and cleanup timeout from 1 to 3600 seconds under Advanced storage limits.";
            if(!String.IsNullOrEmpty(recordingDirectory)&&!ViveCsvPathPolicy.FitsSessionDirectory(recordingDirectory))
                return ViveCsvPathPolicy.PreparationNotice;
            if(observationOptIn)return "Disable preview observation before recording.";
            return null;
        }
        internal string SessionDirectoryStatusNotice
        {
            get
            {
                if(!sessionDirectoryValidationRejected)return sessionDirectoryNotice;
                var reason=RecordingSetupValidationReason();
                return reason==null?"Setup selections are complete. Prepare recording explicitly; no folder was requested by the rejected setup.":
                    "Setup incomplete: "+reason+" No folder was requested; recording is off.";
            }
        }

        // Offering a name performs no filesystem operation. Every new scene
        // instance gets its own proposal, never the previous session's folder.
        string DefaultSessionDirectoryParent
        {get{return Path.GetFullPath(Path.Combine(Application.dataPath,"..","..",ParticipantBuild?"LocalParticipantData":"LocalSelfTestData"));}}
        void EnsureDefaultSessionDirectoryOffer()
        {
            if(sessionDirectoryOffered||!String.IsNullOrEmpty(recordingDirectory)||configured)return;
            OfferFreshSessionDirectory(DefaultSessionDirectoryParent);
        }
        internal bool OfferFreshSessionDirectory(string existingParent)
        {
            if(configured||sessionDirectoryPreparation!=null||consentAutoStart!=null||namedRawPump!=null)return false;
            sessionDirectoryOffered=true;
            try
            {
                var candidate=ViveLocalSessionDirectory.Propose(existingParent);
                sessionDirectoryOffer=candidate;sessionDirectoryAbandoned=false;sessionDirectoryValidationRejected=false;recordingDirectory=candidate.DirectoryPath;
                sessionDirectoryNotice="New session folder proposed. The explicit Prepare action will create only an empty folder; no task or gaze data is saved before both choices.";
                return true;
            }
            catch(Exception error) when(!(error is OutOfMemoryException||error is StackOverflowException||error is AccessViolationException))
            {sessionDirectoryNotice="New session folder could not be proposed; the current selection is unchanged: "+error.Message;return false;}
        }

        sealed class SessionDirectoryPreparation
        {
            internal readonly ViveLocalSessionDirectory Candidate;
            internal readonly string Directory,RawLimit,CsvLimit,CsvTimeout;
            internal readonly int Design,Mode,PlanCount;
            internal readonly ParticipantAssignment Assignment;
            internal readonly int[] Options,Lists;
            internal readonly bool Woz;
            internal readonly System.Diagnostics.Stopwatch Elapsed=System.Diagnostics.Stopwatch.StartNew();
            internal int Done;
            internal bool Success;
            internal string Reason;
            internal SessionDirectoryPreparation(ViveStroopDevelopmentBootstrap owner,ViveLocalSessionDirectory candidate)
            {
                Candidate=candidate;Directory=owner.recordingDirectory;RawLimit=owner.recordingBudgetMiB;
                CsvLimit=owner.csvBudgetMiB;CsvTimeout=owner.csvCleanupSeconds;Design=owner.selectedDesign;
                Mode=owner.ownerRunMode;PlanCount=owner.namedPlanCount;Woz=owner.legacyWozRecordingSelected;
                Assignment=ParticipantBuild?owner.EntryAssignment:null;
                Options=(int[])owner.namedPlanOptions.Clone();Lists=(int[])owner.namedPlanLists.Clone();
            }
            internal bool Matches(ViveStroopDevelopmentBootstrap owner)
            {
                return !owner.configured&&owner.rig!=null&&!owner.rig.ViewRunning&&owner.ControlledRecordingNoticeCurrent&&
                    owner.SelectedRecordingAvailable&&object.ReferenceEquals(Candidate,owner.sessionDirectoryOffer)&&
                    Directory==owner.recordingDirectory&&RawLimit==owner.recordingBudgetMiB&&
                    CsvLimit==owner.csvBudgetMiB&&CsvTimeout==owner.csvCleanupSeconds&&Design==owner.selectedDesign&&
                    Mode==owner.ownerRunMode&&PlanCount==owner.namedPlanCount&&Woz==owner.legacyWozRecordingSelected&&
                    (!ParticipantBuild||object.ReferenceEquals(Assignment,owner.EntryAssignment))&&
                    SameAutoPlan(Options,owner.namedPlanOptions)&&SameAutoPlan(Lists,owner.namedPlanLists);
            }
            internal void Run()
            {
                try{Success=Candidate.TryCreate(out Reason);}
                catch(Exception error) when(!(error is OutOfMemoryException||error is StackOverflowException||error is AccessViolationException))
                {Reason="Folder preparation failed; no recording was armed. An empty folder may remain.";}
                finally{Volatile.Write(ref Done,1);}
            }
        }

        // Called only after the original explicit owner arm/build/notice/limit
        // checks. Filesystem work stays off Unity's input and rendering thread.
        bool TryQueueSessionDirectoryPreparation()
        {
            if(sessionDirectoryPreparation!=null)
            {message="Preparing the new session folder. The interface remains available; recording has not started.";return true;}
            var setupReason=RecordingSetupValidationReason();
            if(setupReason!=null)
            {
                sessionDirectoryValidationRejected=true;
                message=sessionDirectoryNotice="Setup incomplete: "+setupReason+" No folder was requested; recording is off.";
                Debug.Log("VIVE_SESSION_SETUP_REJECTED: "+setupReason+"; folder_operation=not_requested");
                return true;
            }
            sessionDirectoryValidationRejected=false;
            var candidate=sessionDirectoryOffer;
            if(candidate==null||recordingDirectory!=candidate.DirectoryPath)return false; // explicit existing/custom folder
            if(sessionDirectoryAbandoned)
            {message=sessionDirectoryNotice="This folder preparation was canceled. Choose a new session folder; the old request cannot be resumed.";return true;}
            if(candidate.Created)return false;
            if(candidate.Attempted)
            {message=sessionDirectoryNotice="This folder request already failed or was canceled. Choose a new session folder; no automatic retry.";return true;}
            var request=new SessionDirectoryPreparation(this,candidate);
            sessionDirectoryPreparation=request;
            message=sessionDirectoryNotice="Preparing an empty session folder in the background. No recording has started.";
            Debug.Log("VIVE_SESSION_FOLDER_PREPARING: explicit empty-folder request; recording=off");
            try
            {
                var thread=new Thread(request.Run){IsBackground=true,Name="ViveSessionDirectoryPreparation"};
                thread.Start();
            }
            catch(Exception error) when(!(error is OutOfMemoryException||error is StackOverflowException||error is AccessViolationException))
            {
                sessionDirectoryPreparation=null;sessionDirectoryAbandoned=true;
                message=sessionDirectoryNotice="Folder preparation could not start. Choose a new session folder; no recording was armed.";
            }
            return true;
        }
        internal void ServiceSessionDirectoryPreparation()
        {
            var request=sessionDirectoryPreparation;if(request==null)return;
            if(!request.Matches(this))
            {CancelSessionDirectoryPreparation("Setup changed while the folder was being prepared. No recording was armed; an empty folder may remain. Choose a new session folder.");return;}
            if(request.Elapsed.Elapsed.TotalSeconds>30)
            {CancelSessionDirectoryPreparation("Folder preparation timed out. No recording was armed. The empty folder operation may finish later; choose a new folder to retry explicitly.");return;}
            if(Volatile.Read(ref request.Done)==0)return;
            sessionDirectoryPreparation=null;
            if(!request.Success)
            {message=sessionDirectoryNotice=request.Reason??"Folder preparation failed; no recording was armed. Choose a new session folder.";
                Debug.LogWarning("VIVE_SESSION_FOLDER_FAILED: "+sessionDirectoryNotice);return;}
            if(!request.Matches(this))
            {message=sessionDirectoryNotice="Setup changed while the folder was being prepared. No recording was armed. The empty folder was preserved; review settings and prepare again.";return;}
            sessionDirectoryNotice="New empty session folder prepared. Raw and CSV recording still require both participant choices.";
            Debug.Log("VIVE_SESSION_FOLDER_PREPARED: empty folder verified; participant choices still required");
            ArmConsentAutoStart();
        }
        void CancelSessionDirectoryPreparation(string notice)
        {
            if(sessionDirectoryPreparation==null)return;
            sessionDirectoryPreparation=null;
            // Never stop a native filesystem call or delete its output. Its
            // result can no longer arm recording after cancellation/disable.
            sessionDirectoryAbandoned=true;
            message=sessionDirectoryNotice=notice;
        }
    }
}
#endif
