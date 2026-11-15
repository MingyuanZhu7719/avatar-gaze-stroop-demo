using System;

namespace AvatarStroop.Runtime
{
    // A bounded owner/shared-clock observation of gaze absence. It is not an
    // SDK frame, a hardware sampling audit, or an estimate of lost frames.
    internal sealed class ViveRawEyeObservation
    {
        internal enum Disposition { NoNewSample=0, Missing=1 }
        internal enum Reason { Duplicate=0, FrozenOrStale=1, InvalidQuality=2, NoEye=3, OutOfOrder=4, SnapshotBusy=5 }
        internal readonly double ObservedLocalSeconds;
        internal readonly Disposition State;
        internal readonly Reason Cause;
        internal readonly long TaskCursor;
        internal ViveRawEyeObservation(double observedLocalSeconds,Reason cause,long taskCursor)
        {
            State=cause==Reason.Duplicate?Disposition.NoNewSample:Disposition.Missing;
            if(!Valid(State,cause,observedLocalSeconds,taskCursor))throw new ArgumentOutOfRangeException("eye observation");
            ObservedLocalSeconds=observedLocalSeconds;Cause=cause;TaskCursor=taskCursor;
        }
        internal static bool Valid(Disposition state,Reason cause,double seconds,long cursor)
        {
            return !Double.IsNaN(seconds)&&!Double.IsInfinity(seconds)&&seconds>=0&&cursor>=-1&&
                (int)cause>=(int)Reason.Duplicate&&(int)cause<=(int)Reason.SnapshotBusy&&
                state==(cause==Reason.Duplicate?Disposition.NoNewSample:Disposition.Missing);
        }
    }
}
