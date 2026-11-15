using System.Threading;
using AvatarStroop.Core;

namespace AvatarStroop.Runtime
{
    internal sealed partial class ViveNamedRawRecordingPump
    {
        RawTrackingLoss? trackingGap;
        internal bool TrackingGapOpen {get{return trackingGap.HasValue;}}
        internal bool ObserveTrackingHeadLoss(ViveProEyePoll exactInvalidHead,bool planPending)
        {
            double now;
            if(statusRun==null||!worker.IsCurrentHeadTrackingLoss(exactInvalidHead,statusRun)||
                !clock.TryReadRecordingAdmissionTime(out now)||now<exactInvalidHead.Head.ObservedLocalSeconds||
                now-exactInvalidHead.Head.ObservedLocalSeconds>1.0)
            {Fail();return false;}
            return OpenTrackingGap(RawTrackingLoss.HeadPose,planPending);
        }
        // Negative pose-only observation from the rig's same-device render
        // guard. No eye record or presentation witness is manufactured.
        internal bool ObserveTrackingRenderHeadLoss(bool planPending)
        {return OpenTrackingGap(RawTrackingLoss.HeadPose,planPending)&&DrainTaskEvents();}
        internal bool ObserveTrackingControllerLoss(bool planPending)
        {
            // Controller loss does not invalidate the current genuine head/eye
            // frame. The host separately classifies same-device pose loss.
            if(stagedEpoch==null||!VerifyStagedEpoch()){Fail();return false;}
            return OpenTrackingGap(RawTrackingLoss.ControllerPose,planPending);
        }
        bool OpenTrackingGap(RawTrackingLoss cause,bool planPending)
        {
            if(Thread.CurrentThread.ManagedThreadId!=owner||Volatile.Read(ref terminal)!=0||
                Volatile.Read(ref finishRequested)!=0||admission.State!=0)
            {Fail();return false;}
            // No stale positive epoch may be used for input, onset, or resume.
            stagedEpoch=null;
            if(trackingGap.HasValue)return true;
            if(!DrainTaskEvents()||
                !admission.TryRunReservedOrdinaryMutation(()=>participant.RecordRawTrackingGapStart(cause,planPending)))
            {Fail();return false;}
            trackingGap=cause;return true;
        }
        internal bool EndTrackingGap()
        {
            if(!trackingGap.HasValue){Fail();return false;}
            var cause=trackingGap.Value;
            if(!TryRunReservedOrdinaryMutation(()=>participant.RecordRawTrackingGapEnd(cause)))return false;
            trackingGap=null;return true;
        }
    }
}
