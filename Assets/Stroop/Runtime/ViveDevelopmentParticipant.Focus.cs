using System;
using AvatarStroop.Core;

namespace AvatarStroop.Runtime
{
    public sealed partial class ViveDevelopmentParticipant
    {
        bool focusHoldPaused;
        long focusHoldPauseRevision=-1;
        bool xrFocusObservationKnown,lastObservedXrFocus;
        // Negative-only provenance. It cannot restore the revoked WoZ choice,
        // authorize input/presentation or override a recording/data failure.
        internal bool IsFocusHoldPaused
        {get{return focusHoldPaused&&experiment.Phase==Phase.Paused&&
            focusHoldPauseRevision==PauseDiagnosticRevision&&!NamedRecordingAuthorityFaulted&&
            !NamedRecordingWithdrawalRequested&&!NamedViewWithdrawalRequested;}}
        internal bool IsXrFocusInteractionHeld
        {get{return xrFocusObservationKnown&&!lastObservedXrFocus&&experiment.Phase!=Phase.Ended&&
            experiment.Phase!=Phase.NeedsProtocolDecision&&!NamedRecordingAuthorityFaulted&&
            !NamedRecordingWithdrawalRequested&&!NamedViewWithdrawalRequested;}}
        // Raw callers must execute this single task mutation through their
        // current pump reservation. It neither revokes capture nor resumes a
        // task when focus returns. Ordinary preview uses its existing wrapper.
        internal void RecordFocusState(bool desktopFocused,bool xrFocused,bool baseline=false)
        {
            Action action=()=>
            {
                Phase before=experiment.Phase;long revision=PauseDiagnosticRevision;
                if(!xrFocused){Release(trackpad.Reset());ResetAuxiliary();}
                try
                {
                    experiment.RecordFocusState(desktopFocused,xrFocused,baseline);
                    xrFocusObservationKnown=true;lastObservedXrFocus=xrFocused;
                }
                finally
                {
                    if(before!=Phase.Paused&&experiment.Phase==Phase.Paused&&revision==PauseDiagnosticRevision)
                    {
                        LastPauseDiagnostic="XR focus was lost. Task held; recording continues subject to actual data and storage readiness.";
                        if(PauseDiagnosticRevision<Int64.MaxValue)PauseDiagnosticRevision++;
                        focusHoldPaused=!xrFocused;focusHoldPauseRevision=PauseDiagnosticRevision;
                    }
                    else if(experiment.Phase!=Phase.Paused){focusHoldPaused=false;focusHoldPauseRevision=-1;}
                }
            };
            if(observation==null)RunNamedRecordingMutation(action);
            else Observed(action,!xrFocused);
        }
        internal void RecordFocusForwardingSuspension(bool acknowledged,bool cleanupUnknown,bool noPositiveGrant)
        {
            Action action=()=>experiment.RecordFocusForwardingSuspension(acknowledged,cleanupUnknown,noPositiveGrant);
            if(observation==null)RunNamedRecordingMutation(action);else Observed(action,false);
        }
    }
}
