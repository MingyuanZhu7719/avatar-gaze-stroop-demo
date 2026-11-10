using System;
using AvatarStroop.Core;

namespace AvatarStroop.Runtime
{
    public sealed partial class ViveDevelopmentParticipant
    {
        internal void ValidateLegacyWozSourceSelection(ViveLegacyWozTaskOwner owner,Actor actor,string sourceOwner)
        {
            if(!OwnsLegacyWoz(owner)||!owner.OnOwnerThread||HasNamedOption||(RawRecordingRequested&&!owner.RecordingAdmissionCurrent))
                throw new InvalidOperationException("Current unrecorded Legacy owner required.");
            RequireOwner(actor);RequireReady();WireCodec.RequireIdentity(sourceOwner);
            if(experiment.Phase!=Phase.BlockPreparation||
                (experiment.Configuration.RetainControlAcrossBlocks&&(experiment.BlockIndex!=0||experiment.LockedControl.HasValue)))
                throw new InvalidOperationException("Unconfirmed source after practice required.");
        }
        // Explicit owner software choice only. This is the actual Experiment
        // source lock, not a diagnostic reader or a remote-requested mutation.
        // Existing public Automatic path and Live countdown policy are unchanged.
        internal void SelectLegacyWozForOwnedBridge(ViveLegacyWozTaskOwner owner,Actor actor,string sourceOwner)
        {
            if(!OwnsLegacyWoz(owner)||!owner.OnOwnerThread||HasNamedOption||(RawRecordingRequested&&!owner.RecordingAdmissionCurrent))
                throw new InvalidOperationException("Current unrecorded Legacy owner required.");
            Action select=()=>{RequireOwner(actor);RequireReady();experiment.LockBlock(actor,ControlSelection.LiveWoz,sourceOwner);};
            owner.RunSourceChoiceMutation(()=>{if(observation==null)RunNamedRecordingMutation(select);else Observed(select,false);});
        }
#if STROOP_TEST_BUILD
        // Only the sealed memory fixture can exercise later actual phases while
        // production Live countdown remains closed. No caller timestamp/phase.
        internal bool BeginLegacyWozMemoryCountdown(ViveLegacyWozTaskOwner owner,ViveLegacyWozTaskOwner.SourceChoice choice)
        {
            if(!OwnsLegacyWoz(owner)||!owner.IsSourceChoiceCurrent(choice)||experiment.Phase!=Phase.BlockReady)return false;
            Action begin=()=>experiment.BeginCountdown(Actor.Owner,true);
            if(observation==null)RunNamedRecordingMutation(begin);else Observed(begin,false);
            return experiment.Phase==Phase.Countdown;
        }
#endif
    }
    public sealed partial class ViveLegacyWozTaskOwner
    {
        SourceChoice choice;
        long choiceGeneration;
        internal sealed class SourceChoice
        {
            internal readonly ViveLegacyWozTaskOwner Owner;
            internal readonly string Epoch,Stream,Mapping,SourceOwner;
            internal readonly long Generation,SourceGeneration;
            internal bool Revoked;
            internal SourceChoice(ViveLegacyWozTaskOwner owner,string epoch,string stream,string mapping,string sourceOwner,long generation,long sourceGeneration)
            {Owner=owner;Epoch=epoch;Stream=stream;Mapping=mapping;SourceOwner=sourceOwner;Generation=generation;SourceGeneration=sourceGeneration;}
        }
        internal SourceChoice SelectAndConfirmLocalSource(Actor actor,string sourceOwner,string epoch,string stream,string mapping)
        {
            ValidateLocalSourceSelection(actor,sourceOwner);
            foreach(var value in new[]{sourceOwner,epoch,stream,mapping})WireCodec.RequireIdentity(value);
            participant.SelectLegacyWozForOwnedBridge(this,actor,sourceOwner);
            if(terminal||participant.LockedControl!=ControlSelection.LiveWoz||participant.LegacyWozSourceOwner!=sourceOwner||participant.Phase!=Phase.BlockReady)
                throw new InvalidOperationException("Actual local LiveWoz selection was not confirmed.");
            choice=new SourceChoice(this,epoch,stream,mapping,sourceOwner,++choiceGeneration,participant.LegacyWozSourceGeneration);
            return choice;
        }
        internal void ValidateLocalSourceSelection(Actor actor,string sourceOwner)
        {
            if(!OnOwnerThread||terminal||depth!=0||choice!=null||actor!=Actor.Owner||choiceGeneration==Int64.MaxValue)
                throw new InvalidOperationException("One current explicit local owner choice per epoch required.");
            participant.ValidateLegacyWozSourceSelection(this,actor,sourceOwner);
        }
        internal bool IsSourceChoiceCurrent(SourceChoice candidate)
        {
            return OnOwnerThread&&!terminal&&depth==0&&candidate!=null&&Object.ReferenceEquals(candidate,choice)&&
                Object.ReferenceEquals(candidate.Owner,this)&&!candidate.Revoked&&participant.OwnsLegacyWoz(this)&&
                participant.LockedControl==ControlSelection.LiveWoz&&participant.LegacyWozSourceOwner==candidate.SourceOwner&&
                participant.LegacyWozSourceGeneration==candidate.SourceGeneration&&participant.Phase!=Phase.Paused&&participant.Phase!=Phase.Ended;
        }
        internal void RevokeSourceChoice(SourceChoice candidate)
        {
            if(!OnOwnerThread)throw new InvalidOperationException("Local source revocation requires owner thread.");
            if(candidate!=null&&Object.ReferenceEquals(candidate,choice))candidate.Revoked=true;
            current=null;
        }
        void InvalidateSourceChoiceAfterSafety()
        {
            if(choice!=null&&(terminal||participant.Phase==Phase.Paused||participant.Phase==Phase.Ended||
                participant.NamedRecordingWithdrawalRequested||participant.LockedControl!=ControlSelection.LiveWoz||
                participant.LegacyWozSourceGeneration!=choice.SourceGeneration))choice.Revoked=true;
        }
    }
}
