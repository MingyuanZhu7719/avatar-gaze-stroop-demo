using System;
using AvatarStroop.Core;

namespace AvatarStroop.Runtime
{
    public sealed partial class ViveLegacyWozParticipantProcess
    {
        bool focusForwardingSuspending;
        bool focusSuspensionStarted,focusSuspensionMetadataPending;
        public bool FocusForwardingSuspending {get{return focusForwardingSuspending;}}
        public bool FocusSuspendStarted {get{return focusSuspensionStarted;}}
        public bool FocusForwardingSuspended {get;private set;}
        public bool FocusForwardingCleanupUnknown {get;private set;}
        public bool FocusForwardingRevokeNotApplicable {get;private set;}

        // The actual focus pause has already revoked the positive choice.
        // This closes forwarding with the old negative-only revoke channel;
        // it does not stop, restart or grant local raw capture. No pose can be
        // presented and no source can resume from this state.
        public bool TickFocusHoldSuspension()
        {
            OwnerThread();
            if(FocusForwardingSuspended)
                return focusSuspensionStarted&&owner.IsRevokedSourceChoiceForFocusSuspension(choice);
            if(terminal)return false;
            if(!(focusSuspensionStarted?owner.IsRevokedSourceChoiceForFocusSuspension(choice):owner.IsSourceChoiceForFocusSuspension(choice))||
                (owner.RecordingBinding!=null&&!owner.RecordingBinding.SceneAuthorityCurrent))
            {End("focus_suspend_context_unavailable");return false;}
            try
            {
                if(!focusForwardingSuspending)
                {
                    if(transport.Terminal){End(transport.LastIssue);return false;}
                    if(scene!=null&&!scene.ClearForSafety()){End("focus_suspend_clear_uncertain");return false;}
                    focusSuspensionStarted=true;owner.RevokeSourceChoice(choice);
                    preflight=null;pose=null;retainedPreflight=null;retainedPose=null;
                    challenge=null;confirmChallenge=null;pendingCycle=false;maintained=false;locallyPaused=true;
                    binding=null;
                    if(grant==null){FocusForwardingRevokeNotApplicable=true;FinishFocusSuspension(false);return FocusForwardingSuspended;}
                    focusForwardingSuspending=true;
                    revoke=Guid.NewGuid().ToString("N");revokeIssued=transport.Now;
                    if(!transport.SendControl(WozControlKind.SourceRevoke,grant,revoke))
                    {FinishFocusSuspension(false);return FocusForwardingSuspended;}
                }
                TickCore();
                return (!terminal&&focusForwardingSuspending)||FocusForwardingSuspended;
            }
            catch(Exception error)
            {
                // A malformed protocol/local context still uses the existing
                // real-error stop path; focus cannot turn it into success.
                End("focus_suspend_failure:"+error.GetType().Name);return false;
            }
        }

        void FinishFocusSuspension(bool acknowledged)
        {
            OwnerThread();
            preflight=null;pose=null;retainedPreflight=null;retainedPose=null;
            challenge=null;confirmChallenge=null;binding=null;task=null;retiredTask=null;retiredGrant=null;grant=null;
            owner.RevokeSourceChoice(choice);
            transport.Dispose();
            FocusForwardingCleanupUnknown=!acknowledged||transport.CleanupUncertain;
            focusForwardingSuspending=false;FocusForwardingSuspended=true;terminal=true;locallyPaused=true;
            focusSuspensionMetadataPending=true;
            LastIssue=FocusForwardingCleanupUnknown?"local_positive_acceptance_revoked_remote_cleanup_unknown":
                "local_positive_acceptance_revoked_remote_software_ack_received";
            // Deliberately retain the current local task/raw binding owner.
            // Explicit Dispose/withdrawal and actual data/storage failures keep
            // their existing stop semantics. No positive epoch is revived.
        }

        // Call only from the owner's freshly staged raw frame (or ordinary
        // unrecorded preview). Binding.RunMutation enforces the exact live pump
        // reservation; an unrecorded/late call cannot pretend data was stored.
        public bool RecordFocusSuspensionMetadataForCurrentFrame()
        {
            OwnerThread();if(!focusSuspensionMetadataPending)return true;
            if(!FocusForwardingSuspended||!focusSuspensionStarted||!owner.IsRevokedSourceChoiceForFocusSuspension(choice))return false;
            var participant=owner.Participant;var recording=owner.RecordingBinding;
            bool success;
            if(recording!=null)success=recording.RunMutation(()=>participant.RecordFocusForwardingSuspension(
                SourceRevokeAcknowledged,FocusForwardingCleanupUnknown,FocusForwardingRevokeNotApplicable));
            else{participant.RecordFocusForwardingSuspension(SourceRevokeAcknowledged,FocusForwardingCleanupUnknown,FocusForwardingRevokeNotApplicable);success=true;}
            if(success)focusSuspensionMetadataPending=false;
            return success;
        }
    }
}
