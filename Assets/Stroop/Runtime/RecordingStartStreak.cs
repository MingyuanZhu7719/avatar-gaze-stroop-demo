using System;
using Reason=AvatarStroop.Runtime.ViveProEyeWorker.RecordingStartReadinessReason;

namespace AvatarStroop.Runtime
{
    // Serialized by the owning worker's gate. Holds only constant-size
    // preflight scalar metadata, never eye/head payloads or evidence records.
    internal sealed class RecordingStartStreak
    {
        internal const double MinimumValidSeconds=.5,MaximumAgeSeconds=.25;
        internal const int MinimumDistinctFrames=3;
        double lastPoll=-1,firstDistinct=-1,lastDistinct=-1,lastEye=-1,lastDistinctEye=-1;
        int sequence,timestamp,count;
        bool hasIdentity;
        Reason reason=Reason.AwaitingObservation;
        static bool FiniteTime(double time){return !Double.IsNaN(time)&&!Double.IsInfinity(time)&&time>=0;}
        void Reset(Reason failure){count=0;firstDistinct=-1;reason=failure;}

        internal void Observe(bool timeValid,double returnedAt,Reason status,int frame,int nativeTimestamp,double eyeAt)
        {
            if(!timeValid||!FiniteTime(returnedAt)||returnedAt<lastPoll){Reset(Reason.InvalidTime);return;}
            bool pollGap=lastPoll>=0&&returnedAt-lastPoll>MaximumAgeSeconds;lastPoll=returnedAt;
            if(status!=Reason.None){Reset(status);return;}
            if(!FiniteTime(eyeAt)||eyeAt>returnedAt||(lastEye>=0&&eyeAt<lastEye))
            {Reset(Reason.InvalidTime);return;}
            bool duplicate=hasIdentity&&frame==sequence&&nativeTimestamp==timestamp;
            if(hasIdentity&&!duplicate&&(frame<=sequence||nativeTimestamp<=timestamp))
            {
                // Start a new scalar identity epoch, not a ready streak, in
                // case this is a legitimate SDK counter reset. A later pair
                // still must advance both counters and meet the full window.
                sequence=frame;timestamp=nativeTimestamp;lastEye=eyeAt;
                lastDistinct=lastDistinctEye=-1;Reset(Reason.FrameDiscontinuity);return;
            }
            if(!duplicate&&lastEye>=0&&eyeAt<=lastEye){Reset(Reason.InvalidTime);return;}
            lastEye=eyeAt;
            if(returnedAt-eyeAt>MaximumAgeSeconds){Reset(Reason.Stale);return;}
            if(pollGap||lastDistinct>=0&&returnedAt-lastDistinct>MaximumAgeSeconds)Reset(Reason.Stale);
            if(duplicate)
            {
                // A valid repeat after an invalid return cannot establish a
                // new window, but no longer reports the old status as current.
                if(reason!=Reason.None&&reason!=Reason.Stale)reason=Reason.Stabilizing;
                return; // age and distinct count never advance
            }
            sequence=frame;timestamp=nativeTimestamp;hasIdentity=true;
            lastDistinct=returnedAt;lastDistinctEye=eyeAt;
            if(count==0)firstDistinct=returnedAt;
            if(count<MinimumDistinctFrames)count++;
            reason=Reason.None;
        }
        internal Reason Read(double now)
        {
            if(!FiniteTime(now)||now<lastPoll)return Reason.InvalidTime;
            if(reason!=Reason.None)return reason;
            if(now-lastPoll>MaximumAgeSeconds||now-lastDistinct>MaximumAgeSeconds||now-lastDistinctEye>MaximumAgeSeconds)
                return Reason.Stale;
            return count>=MinimumDistinctFrames&&lastDistinct-firstDistinct>=MinimumValidSeconds?Reason.None:Reason.Stabilizing;
        }
    }
}
