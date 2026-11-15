using System;

namespace AvatarStroop.Core
{
    internal enum RawTrackingLoss { HeadPose, ControllerPose, EyeQuality, RecordingAuthority }

    public sealed partial class Experiment
    {
        // Negative-only, no acquisition or presentation authorization.
        internal void RecordRawTrackingGapStart(RawTrackingLoss cause,bool planPending)
        {
            if(!Configuration.HasRawRecordingIntent||!Enum.IsDefined(typeof(RawTrackingLoss),cause)||
                Phase==Phase.Ended||Phase==Phase.NeedsProtocolDecision)
                throw new InvalidOperationException("Raw tracking hold has no active context.");
            if(planPending&&Phase!=Phase.Preparation&&Phase!=Phase.Familiarization&&Phase!=Phase.PracticeRetry&&Phase!=Phase.BlockReady)
                throw new InvalidOperationException("Raw pending plan is outside its preparation phase.");
            Log("tracking_gap_begin","tracking_gap_schema","1","actor","Owner","code","tracking_gap",
                "reason",cause.ToString(),"detail","task_input=blocked;missing_measurement=UNKNOWN");
            if(!planPending&&FocusHoldPhase(Phase))PauseCore(Actor.Owner,null,true);
        }
        internal void RecordRawTrackingGapEnd(RawTrackingLoss cause,bool resume)
        {
            if(!Configuration.HasRawRecordingIntent||!Enum.IsDefined(typeof(RawTrackingLoss),cause)||
                Phase==Phase.Ended||Phase==Phase.NeedsProtocolDecision)
                throw new InvalidOperationException("Raw tracking recovery has no active context.");
            Log("tracking_gap_end","tracking_gap_schema","1","actor","Owner","code","tracking_gap",
                "reason",cause.ToString(),"detail","task_input=revalidated;missing_measurement=UNKNOWN");
            if(resume)
            {
                if(LockedControl==ControlSelection.LiveWoz)throw new InvalidOperationException("Live WoZ requires separate source acquisition.");
                ResumeCore(true,true);
            }
        }
    }
}
