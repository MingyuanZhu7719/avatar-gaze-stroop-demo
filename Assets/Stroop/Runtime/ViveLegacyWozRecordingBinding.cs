using System;
using System.Collections.Generic;
using AvatarStroop.Core;

namespace AvatarStroop.Runtime
{
    // One local raw admission joins the real task and network owner. It owns no
    // second recorder, stream or remote worker. No caller timestamp or provider
    // can create it, and no network message grants participant consent.
    internal sealed partial class ViveLegacyWozRecordingBinding
    {
        readonly ViveLegacyWozTaskOwner owner;
        readonly ViveDevelopmentParticipant participant;
        readonly ViveNamedSessionFrameAdmission admission;
        readonly ViveProEyeWorker worker;
        readonly ViveNamedRawRecordingPump pump;
        readonly bool synthetic;
        ViveLegacyWozParticipantProcess process;
        bool stopped,insideReservedMutation;
        internal bool SyntheticOnly {get{return synthetic;}}
        internal ViveLegacyWozTaskOwner TaskOwner {get{return owner;}}
        bool SourcesCurrent {get{return !stopped&&owner.OnOwnerThread&&!owner.Terminal&&
            participant.LegacyWozRawAdmissionCurrent(admission,owner.Clock)&&
            admission.UsesExactSources(participant,worker,owner.Clock);}}
        internal bool Current {get{return SourcesCurrent&&!insideReservedMutation&&admission.State==0;}}
        internal bool MayAppendInsideReservation {get{return SourcesCurrent&&insideReservedMutation&&admission.State==1;}}
        internal bool CompletedTaskObserved {get;private set;}
        internal bool BlockProofCurrent
        {get{if(!SourcesCurrent||process==null||participant.Phase!=Phase.BlockReady||!process.CurrentTaskAcknowledged)return false;
            if(!ViveLegacyWozParticipantProcess.NeedsSource(owner.SceneTask))return true;
            var proof=process.CurrentPreflight;return proof!=null&&process.ControlledEvidenceFresh(proof)&&
                (synthetic?proof is ViveLegacyWozParticipantProcess.MemoryEvidence:proof is ViveLegacyWozParticipantProcess.SocketEvidence&&!proof.SyntheticObservationsOnly);}}
        internal bool MayBeginInsidePlanMutation {get{return SourcesCurrent&&admission.State==1&&participant.LockedControl==ControlSelection.LiveWoz;}}
        internal void ObserveActualTaskCompletion()
        {if(!CompletedTaskObserved&&SourcesCurrent&&process!=null&&participant.Completed)CompletedTaskObserved=true;}
        internal bool RequestCompletedDrain()
        {
            if(!CompletedTaskObserved||!SourcesCurrent||!participant.Completed||process==null||
                !process.CloseForRecordedCompletedDrain(this)||!pump.RequestCompletedDrain()){Stop();return false;}return true;
        }
        ViveLegacyWozRecordingBinding(ViveLegacyWozTaskOwner owner,ViveNamedSessionFrameAdmission admission,
            ViveProEyeWorker worker,ViveNamedRawRecordingPump pump,bool synthetic)
        {
            if(owner==null||admission==null||worker==null||!owner.OnOwnerThread||owner.Terminal||
                !admission.SessionContext.IsLegacyWoz||admission.SyntheticOnly!=synthetic||
                !admission.UsesExactSources(owner.Participant,worker,owner.Clock)||
                pump==null||!pump.UsesRecordingBinding(owner.Participant,worker,owner.Clock,admission)||
                (!synthetic&&(!ViveLegacyWozRecordingBuildPolicy.Available||!worker.UsesInstalledSdkPath)))
                throw new InvalidOperationException("Exact local WoZ recording admission and source required.");
            this.owner=owner;participant=owner.Participant;this.admission=admission;this.worker=worker;this.pump=pump;this.synthetic=synthetic;
            if(!Current)throw new InvalidOperationException("Recording admission is not current.");
            owner.AttachRecordingBinding(this);
        }
        internal static ViveLegacyWozRecordingBinding CreateInstalled(ViveLegacyWozTaskOwner owner,ViveNamedSessionFrameAdmission admission,
            ViveProEyeWorker worker,ViveNamedRawRecordingPump pump)
        {return new ViveLegacyWozRecordingBinding(owner,admission,worker,pump,false);}
#if VIVE_RAW_SYNTHETIC_TEST && VIVE_SYNTHETIC_BATCH_FIXTURE && !UNITY_5_3_OR_NEWER
        internal static ViveLegacyWozRecordingBinding CreateMemoryFixture(ViveLegacyWozTaskOwner owner,ViveNamedSessionFrameAdmission admission,ViveProEyeWorker worker,ViveNamedRawRecordingPump pump)
        {return new ViveLegacyWozRecordingBinding(owner,admission,worker,pump,true);}
#endif
        internal void AttachProcess(ViveLegacyWozParticipantProcess candidate)
        {
            if(!Current||process!=null||candidate==null||!Object.ReferenceEquals(candidate.ActualOwner,owner)||
                (synthetic?(candidate.IsInstalledChannel||candidate.UsesOwnedSystemSocket):
                    (!candidate.IsInstalledChannel||!candidate.UsesOwnedSystemSocket)))
                throw new InvalidOperationException("One matching recording process required.");
            process=candidate;
        }
        internal bool RunMutation(Action action)
        {
            if(!Current||action==null)return false;
            bool result=pump.TryRunReservedOrdinaryMutation(()=>{
                insideReservedMutation=true;try{action();}finally{insideReservedMutation=false;}});
            if(!result)Stop();return result;
        }
        internal bool Record(ViveLegacyWozParticipantProcess sender,IReadOnlyDictionary<string,string> fields)
        {
            if(!Current||!Object.ReferenceEquals(sender,process))return false;
            return RunMutation(()=>participant.RecordLegacyWozEvidence(this,fields));
        }
        // Immediate irrevocable stop. It does not certify SDK/writer close,
        // durable bytes, a complete trial/session, or permission to export.
        internal void Stop()
        {if(stopped)return;stopped=true;admission.AbortForTechnicalFailure();if(pump!=null)pump.Stop();}
    }
    internal static class ViveLegacyWozRecordingBuildPolicy
    {
        internal static bool Available
        {get{
#if STROOP_LEGACY_WOZ_RECORDING_DEVELOPMENT
            return ViveLegacyRawRecordingBuildPolicy.Available;
#else
            return false;
#endif
        }}
    }
    public sealed partial class ViveLegacyWozTaskOwner
    {
        internal ViveDevelopmentParticipant Participant {get{return participant;}}
        internal ViveLegacyWozRecordingBinding RecordingBinding {get;private set;}
        internal bool RecordingAdmissionCurrent {get{return RecordingBinding!=null&&RecordingBinding.Current;}}
        internal void AttachRecordingBinding(ViveLegacyWozRecordingBinding value)
        {if(!OnOwnerThread||terminal||RecordingBinding!=null||value==null)throw new InvalidOperationException("One local recording binding required.");RecordingBinding=value;}
        internal void RunSourceChoiceMutation(Action action)
        {if(RecordingBinding==null){action();return;}if(!RecordingBinding.RunMutation(action))throw new InvalidOperationException("Source-choice recording reservation failed.");}
    }
    public sealed partial class ViveDevelopmentParticipant
    {
        internal bool LegacyWozRawAdmissionCurrent(ViveNamedSessionFrameAdmission admission,MonotonicClock clock)
        {return rawContext!=null&&rawContext.IsLegacyWoz&&Object.ReferenceEquals(eventClock,clock)&&
            Object.ReferenceEquals(System.Threading.Volatile.Read(ref namedFrameAdmission),admission)&&
            !namedRawRevocation.IsRevoked&&!NamedRecordingWithdrawalRequested&&!NamedRecordingAuthorityFaulted;}
        internal ViveLegacyWozRecordingBinding LegacyWozRecording {get{return legacyWozOwner==null?null:legacyWozOwner.RecordingBinding;}}
        internal bool LegacyWozRecordingDeclared {get{return rawContext!=null&&rawContext.IsLegacyWoz;}}
        bool RecordedWozBlockProofCurrent {get{return rawContext==null||!rawContext.IsLegacyWoz||
            (LegacyWozRecording!=null&&LegacyWozRecording.BlockProofCurrent);}}
        internal void RecordLegacyWozEvidence(ViveLegacyWozRecordingBinding binding,IReadOnlyDictionary<string,string> fields)
        {
            if(legacyWozOwner==null||!Object.ReferenceEquals(legacyWozOwner.RecordingBinding,binding)||!binding.MayAppendInsideReservation)
                throw new InvalidOperationException("Actual participant recording binding required.");
            RunNamedRecordingMutation(()=>experiment.RecordLegacyWozEvidence(fields));
        }
    }
}
