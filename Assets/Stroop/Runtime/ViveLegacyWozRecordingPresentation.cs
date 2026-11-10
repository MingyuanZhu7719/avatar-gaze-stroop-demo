using System;
using System.Collections.Generic;
using AvatarStroop.Core;

namespace AvatarStroop.Runtime
{
    internal sealed partial class ViveLegacyWozRecordingBinding
    {
        internal sealed class PresentationEvidence
        {
            internal readonly ViveLegacyWozRecordingBinding Owner;
            internal readonly ViveLegacyWozParticipantProcess.Evidence Pose;
            internal readonly string Trial;
            internal readonly long WireRevision;
            internal readonly IReadOnlyDictionary<string,string> Fields;
            internal bool Applied;
            internal PresentationEvidence(ViveLegacyWozRecordingBinding owner,ViveLegacyWozParticipantProcess.Evidence pose,
                string trial,long revision,IReadOnlyDictionary<string,string> fields)
            {Owner=owner;Pose=pose;Trial=trial;WireRevision=revision;Fields=fields;}
        }
        PresentationEvidence pendingPresentation;
        internal string LastPresentationIssue {get;private set;}
        internal bool SceneAuthorityCurrent {get{return SourcesCurrent&&(admission.State==0||admission.State==1)&&!CompletedTaskObserved;}}
        internal bool TryCapturePresentation(out PresentationEvidence result)
        {
            result=null;LastPresentationIssue="recording_or_task";if(!SceneAuthorityCurrent||admission.State!=1||process==null||!process.DisplayTaskAcknowledged||participant.Phase!=Phase.MainTask)return false;
            var pose=process.DisplayPose;bool absent=owner.SceneTask.Condition==Condition.D;
            LastPresentationIssue="fresh_owned_pose";
            if(!absent&&(pose==null||!process.ControlledEvidenceFresh(pose)||pose.Pose==null||!pose.Pose.HeadValid||!pose.Pose.EyeValid||
                (synthetic?!(pose is ViveLegacyWozParticipantProcess.MemoryEvidence):!(pose is ViveLegacyWozParticipantProcess.SocketEvidence)||pose.SyntheticObservationsOnly)))return false;
            LastPresentationIssue="owner_capture";ViveLegacyWozTaskOwner.Binding task;if(!owner.TryCapture(out task))return false;
            LastPresentationIssue="pose_lineage";var fields=absent?null:process.CapturePresentationFields(pose);if(!absent&&fields==null)return false;
            result=new PresentationEvidence(this,absent?null:pose,task.Task.TrialId,task.WireRevision,fields);pendingPresentation=result;LastPresentationIssue=null;return true;
        }
        internal bool PresentationCurrent(PresentationEvidence value,bool requireApplied)
        {
            if(!SceneAuthorityCurrent||admission.State!=1||value==null||!Object.ReferenceEquals(value,pendingPresentation)||
                !Object.ReferenceEquals(value.Owner,this)||(requireApplied&&!value.Applied)||!owner.MutationIdle||!process.DisplayTaskAcknowledged)return false;
            ViveLegacyWozTaskOwner.Binding current;if(!owner.TryCapture(out current)||current.Task.Phase!=Phase.MainTask||
                current.Task.TrialId!=value.Trial||current.WireRevision!=value.WireRevision)return false;
            return value.Pose==null?current.Task.Condition==Condition.D:
                Object.ReferenceEquals(value.Pose,process.DisplayPose)&&process.ControlledEvidenceFresh(value.Pose);
        }
        // Only the registered owned scene calls this after its exact sink apply
        // and after rechecking the same immutable evidence. It proves software
        // use, not physical HMD visibility or optical onset.
        internal void NoteSceneApplied(ViveLegacyWozControlledScene scene,ViveLegacyWozParticipantProcess.Evidence value)
        {
            var pending=pendingPresentation;
            if(!owner.ControlledSceneCurrent(scene)||pending==null||!Object.ReferenceEquals(pending.Pose,value)||!PresentationCurrent(pending,false))return;
            pending.Applied=true;
        }
        internal void PresentationFinished(PresentationEvidence value)
        {if(Object.ReferenceEquals(pendingPresentation,value))pendingPresentation=null;}
        internal void InvalidateAppliedPresentation()
        {if(pendingPresentation!=null)pendingPresentation.Applied=false;}
    }
    public sealed partial class ViveDevelopmentParticipant
    {
        internal void MarkWozPresentedWithSoftwareProxy(string trialId,double unitySeconds,int frame,long revision,ViveLegacyWozRecordingBinding.PresentationEvidence evidence)
        {
            var recording=LegacyWozRecording;
            if(recording==null||evidence==null||!recording.PresentationCurrent(evidence,true)||observation!=null)
                throw new InvalidOperationException("Exact applied WoZ presentation reservation required");
            RunNamedRecordingMutation(()=>{
                RequireReady();experiment.MarkPresentedWithSoftwareProxy(trialId,unitySeconds,frame,revision);
                if(evidence.Fields!=null)experiment.RecordLegacyWozEvidence(evidence.Fields);
            });
        }
    }
}
