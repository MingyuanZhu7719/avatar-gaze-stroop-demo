using System;
using System.Threading;
using AvatarStroop.Core;

namespace AvatarStroop.Runtime
{
    internal sealed partial class ViveNamedRawRecordingPump
    {
        internal string FrameQualityHoldReason {get;private set;}
        internal void RequestEyeQualityHold(string reason)
        {
            if(reason==null||!reason.StartsWith("eye_authority_busy",StringComparison.Ordinal))
                throw new ArgumentException("Expected recording-authority contention.", "reason");
            stagedEpoch=null;
            FrameQualityHoldReason=reason;
        }
        internal void ClearEyeQualityHoldRequest(){FrameQualityHoldReason=null;}
        internal bool ObserveRecordingAuthorityLoss(bool planPending)
        {
            if(statusRun==null||FrameQualityHoldReason==null||Thread.CurrentThread.ManagedThreadId!=owner)
            {Fail();return false;}
            return OpenTrackingGap(RawTrackingLoss.RecordingAuthority,planPending)&&DrainTaskEvents();
        }
    }
}
