using System;
using AvatarStroop.Core;

namespace AvatarStroop.Runtime
{
    public sealed partial class ViveDevelopmentParticipant
    {
        long trackingPauseRevision=-1;
        internal bool RawTrackingInteractionHeld {get;private set;}
        internal bool IsRawTrackingPaused {get{return trackingPauseRevision>=0&&trackingPauseRevision==PauseDiagnosticRevision&&
            experiment.Phase==Phase.Paused&&!NamedRecordingAuthorityFaulted&&!NamedRecordingWithdrawalRequested&&!NamedViewWithdrawalRequested;}}
        internal void RecordRawTrackingGapStart(RawTrackingLoss cause,bool planPending)
        {
            RunNamedRecordingMutation(()=>{
                var before=experiment.Phase;
                Release(trackpad.Reset());inputReady=false;ResetAuxiliary();
                experiment.RecordRawTrackingGapStart(cause,planPending);RawTrackingInteractionHeld=true;
                if(before!=Phase.Paused&&experiment.Phase==Phase.Paused)
                {
                    if(PauseDiagnosticRevision==Int64.MaxValue)throw new InvalidOperationException("Pause revision exhausted.");
                    LastPauseDiagnostic="Tracking temporarily unavailable. Recording remains open; missing measurements are unknown.";
                    trackingPauseRevision=++PauseDiagnosticRevision;
                }
            });
        }
        internal void RecordRawTrackingGapEnd(RawTrackingLoss cause)
        {
            RunNamedRecordingMutation(()=>{
                bool resume=IsRawTrackingPaused&&experiment.LockedControl!=ControlSelection.LiveWoz;
                experiment.RecordRawTrackingGapEnd(cause,resume);RawTrackingInteractionHeld=false;
                // The host ended the gap only after current fresh neutral input.
                // Mark readiness within this single journalled mutation; an
                // extra task mutation would stale a pending block-plan receipt.
                inputReady=true;
                if(resume)trackingPauseRevision=-1;
            });
        }
    }
}
