using System;
using AvatarStroop.Core;

namespace AvatarStroop.Runtime
{
    // Assembly-private scene boundary, created by the actual bootstrap only.
    // Readiness is read from its owned scene, never a public bool/provider.
    internal interface IViveLegacyWozSceneSink
    {
        bool LocallyReady {get;}
        void Clear();
        void Apply(AvatarOutput output);
    }
    internal sealed class ViveLegacyWozControlledScene : IDisposable
    {
        internal readonly ViveLegacyWozTaskOwner Owner;
        readonly ViveLegacyWozParticipantProcess process;
        readonly IViveLegacyWozSceneSink sink;
        readonly bool fixture;
        bool closed,clearUncertain;
        string presentedTrial;
        string preparedTrial;
        bool countdownProven;
        internal bool Closed {get{return closed||process.Terminal;}}
        internal bool RecordingAllowed {get{return false;}}
        internal bool HardwareValidated {get{return false;}}
        internal ViveLegacyWozControlledScene(ViveLegacyWozParticipantProcess process,IViveLegacyWozSceneSink sink)
            :this(process,sink,false){}
        ViveLegacyWozControlledScene(ViveLegacyWozParticipantProcess process,IViveLegacyWozSceneSink sink,bool fixture)
        {
            if(process==null||sink==null||(!fixture&&!process.IsInstalledChannel))throw new InvalidOperationException("Owned installed-channel development scene required");
            this.process=process;this.sink=sink;this.fixture=fixture;Owner=process.ActualOwner;
            Owner.RegisterControlledScene(this);process.AttachScene(this);
            if(!ClearForSafety()){Owner.MarkCleanupUncertain();process.Dispose();throw new InvalidOperationException("Scene clear uncertain");}
        }
        bool Local()
        {return !closed&&!clearUncertain&&!process.Terminal&&Owner.ControlledSceneCurrent(this)&&sink.LocallyReady;}
        bool Guard(Func<bool> action)
        {try{return action();}catch{ClearForSafety();process.Dispose();closed=true;return false;}}
        bool Proof()
        {return fixture?process.ControlledEvidenceFresh(process.CurrentPreflight):process.ControlledPhaseProofCurrent;}
        bool DisplayProof()
        {var value=process.DisplayPreflight;return value!=null&&process.ControlledEvidenceFresh(value)&&
            (fixture?value is ViveLegacyWozParticipantProcess.MemoryEvidence:value is ViveLegacyWozParticipantProcess.SocketEvidence&&!value.SyntheticObservationsOnly);}
        internal bool CanBeginCountdown
        {get{return Guard(()=>Local()&&Owner.SceneTask.Phase==Phase.BlockReady&&process.CurrentTaskAcknowledged&&
            (!ViveLegacyWozParticipantProcess.NeedsSource(Owner.SceneTask)||Proof()));}}
        internal bool BeginCountdown()
        {
            if(!CanBeginCountdown)return false;
            try{Owner.BeginControlledCountdown(this);ClearForSafety();process.TickMaintainedPreflight();return !Closed;}
            catch{Dispose();return false;}
        }
        // Called before ordinary task input/Tick. While acquiring a new exact
        // task proof, keep stimulus and both avatar axes masked, and consume no
        // task response. Original challenge timeout is never extended by this.
        internal bool PumpBeforeTask()
        {return Guard(PumpCore);}
        bool PumpCore()
        {
            if(closed)return false;
            if(Owner.RecordingBinding!=null&&Owner.RecordingBinding.CompletedTaskObserved){ClearForSafety();return false;}
            if(!Local()){Dispose();return false;}
            process.TickMaintainedPreflight();
            if(!Local()){ClearForSafety();return false;}
            var task=Owner.SceneTask;
            bool ready=process.DisplayTaskAcknowledged&&(!ViveLegacyWozParticipantProcess.NeedsSource(task)||
                (DisplayProof()&&(task.Phase!=Phase.MainTask||ValidPose(process.DisplayPose))));
            if(task.Phase!=Phase.Countdown)countdownProven=false;
            if(ready&&task.Phase==Phase.Countdown)countdownProven=true;
            if(!ready)
            {
                ClearForSafety();
                // Once actually presented, loss cannot create an invisible RT
                // interval. Interrupt the real trial instead of later resuming.
                if((task.Phase==Phase.MainTask&&task.TrialId==presentedTrial)||(task.Phase==Phase.Countdown&&countdownProven))Dispose();
            }
            return ready;
        }
        bool ValidPose(ViveLegacyWozParticipantProcess.Evidence evidence)
        {return evidence!=null&&process.ControlledEvidenceFresh(evidence)&&evidence.Pose!=null&&evidence.Pose.HeadValid&&evidence.Pose.EyeValid&&
            (fixture?evidence is ViveLegacyWozParticipantProcess.MemoryEvidence:
                evidence is ViveLegacyWozParticipantProcess.SocketEvidence&&!evidence.SyntheticObservationsOnly)&&
            evidence.Pose.RawHeadRotation.IsUnit&&evidence.Pose.RawEyeDirection.IsUnit;}
        internal bool MayShowTask
        {get{return Guard(()=>{if(!Local()||!process.DisplayTaskAcknowledged)return false;var task=Owner.SceneTask;
            return !ViveLegacyWozParticipantProcess.NeedsSource(task)||(DisplayProof()&&(task.Phase!=Phase.MainTask||ValidPose(process.DisplayPose)));});}}
        internal void Present()
        {
            try
            {
                if(!MayShowTask){ClearForSafety();return;}
                var task=Owner.SceneTask;
                if(task.Phase==Phase.MainTask)preparedTrial=task.TrialId;
                if(task.Phase!=Phase.MainTask||task.Condition==Condition.D){ClearAvatarOnly();
                    if(task.Phase==Phase.MainTask&&Owner.RecordingBinding!=null)Owner.RecordingBinding.NoteSceneApplied(this,null);return;}
                var evidence=process.DisplayPose;if(!ValidPose(evidence)){ClearForSafety();return;}
                var sample=evidence.Pose;
                // Source already applied the fixed SRanipal/tracking mapping.
                // Do not reflect or normalize those coordinates a second time.
                var output=new AvatarOutput{AvatarVisible=true,SelectedSource=sample.Source,ActualSource=sample.Source,
                    ActualPose=new AvatarPose(sample.RawHeadRotation,sample.RawEyeDirection),HeadValid=true,EyeValid=true,
                    InputSample=sample,MappingVersion=process.Mapping,Status=GazeOutputStatus.SampleAvailable};
                sink.Apply(output);
                if(!Local()||!Object.ReferenceEquals(evidence,process.DisplayPose)||!MayShowTask)ClearForSafety();
                else if(Owner.RecordingBinding!=null)Owner.RecordingBinding.NoteSceneApplied(this,evidence);
            }
            catch{ClearForSafety();process.Dispose();closed=true;}
        }
        // A hidden output still uses the same owned sink; no Automatic fallback.
        void ClearAvatarOnly()
        {sink.Apply(new AvatarOutput{AvatarVisible=false,SelectedSource=fixture?ControlSource.Simulator:ControlSource.LiveOperator,
            ActualSource=null,HeadValid=false,EyeValid=false,MappingVersion=process.Mapping,Status=GazeOutputStatus.SampleMissing});}
        internal void AfterTaskMutation()
        {
            if(closed)return;
            if(Owner.RecordingBinding!=null&&Owner.RecordingBinding.CompletedTaskObserved){ClearForSafety();return;}
            // Includes the real MarkPresented mutation and response/phase changes.
            if(!process.CurrentTaskContext)ClearForSafety();
            if(process.Terminal)closed=true;
        }
        internal void NotePresented(string trialId)
        {if(!closed&&MayShowTask&&Owner.SceneTask.Phase==Phase.MainTask&&Owner.SceneTask.TrialId==trialId)presentedTrial=trialId;}
        internal void RecheckPresentation()
        {
            Guard(()=>{
            if(closed)return false;
            if(Owner.RecordingBinding!=null&&Owner.RecordingBinding.CompletedTaskObserved){ClearForSafety();return false;}
            if(MayShowTask)return true;
            var task=Owner.SceneTask;ClearForSafety();
            if(!Local()||(task.Phase==Phase.MainTask&&(task.TrialId==presentedTrial||task.TrialId==preparedTrial))||
                (task.Phase==Phase.Countdown&&countdownProven))Dispose();
            return false;});
        }
        internal bool ClearForSafety()
        {
            if(Owner.RecordingBinding!=null)Owner.RecordingBinding.InvalidateAppliedPresentation();
            if(clearUncertain)return false;
            try{sink.Clear();return true;}catch{clearUncertain=true;Owner.MarkCleanupUncertain();return false;}
        }
        public void Dispose()
        {if(closed)return;closed=true;ClearForSafety();process.Dispose();}
#if STROOP_TEST_BUILD
        internal sealed class MemorySink:IViveLegacyWozSceneSink
        {
            internal bool Ready=true;internal bool FailReady {get;set;}internal bool FailClear {get;set;}internal Action DuringApply {get;set;}internal AvatarOutput Output;internal int Clears;
            public bool LocallyReady {get{if(FailReady)throw new InvalidOperationException("Sealed fixture readiness failure");return Ready;}}
            public void Clear(){Output=null;Clears++;if(FailClear)throw new InvalidOperationException("Sealed fixture clear failure");}
            public void Apply(AvatarOutput output){Output=output;var action=DuringApply;DuringApply=null;if(action!=null)action();}
        }
        internal static ViveLegacyWozControlledScene CreateMemoryFixture(ViveLegacyWozParticipantProcess process,MemorySink sink)
        {return new ViveLegacyWozControlledScene(process,sink,true);}
#endif
    }
    public sealed partial class ViveLegacyWozTaskOwner
    {
        ViveLegacyWozControlledScene controlledScene;
        internal TaskSnapshot SceneTask {get{return participant.ReadLegacyWozTask();}}
        internal void RegisterControlledScene(ViveLegacyWozControlledScene scene)
        {if(!OnOwnerThread||terminal||controlledScene!=null||scene==null||!Object.ReferenceEquals(scene.Owner,this)||
                (participant.RawRecordingRequested&&!RecordingAdmissionCurrent))
            throw new InvalidOperationException("One actual authorized Legacy scene required");controlledScene=scene;}
        internal bool ControlledSceneCurrent(ViveLegacyWozControlledScene scene)
        {return OnOwnerThread&&!terminal&&depth==0&&Object.ReferenceEquals(controlledScene,scene)&&participant.OwnsLegacyWoz(this)&&
            !participant.HasNamedOption&&(!participant.RawRecordingRequested||(RecordingBinding!=null&&RecordingBinding.SceneAuthorityCurrent))&&participant.LockedControl==ControlSelection.LiveWoz;}
        internal void BeginControlledCountdown(ViveLegacyWozControlledScene scene)
        {
            if(!ControlledSceneCurrent(scene)||!scene.CanBeginCountdown)throw new InvalidOperationException("Current controlled scene proof required");
            participant.BeginLegacyWozControlledCountdown(this);
        }
    }
    public sealed partial class ViveDevelopmentParticipant
    {
        internal void BeginLegacyWozControlledCountdown(ViveLegacyWozTaskOwner owner)
        {
            if(!OwnsLegacyWoz(owner)||!owner.OnOwnerThread||RawRecordingRequested||HasNamedOption||!InputReady||experiment.Phase!=Phase.BlockReady)
                throw new InvalidOperationException("Actual unrecorded Legacy countdown required");
            Action begin=()=>{if(experiment.BlockIndex==0)experiment.BeginCountdown(Actor.Owner,true);
                else if(!experiment.TryBeginRetainedBlockCountdown(true))throw new InvalidOperationException("Retained source continuation unavailable");};
            if(observation==null)RunNamedRecordingMutation(begin);else Observed(begin,false);
        }
    }
}
