using System;
using System.Threading;
using AvatarStroop.Core;

namespace AvatarStroop.Runtime
{
    public sealed partial class ViveProEyeWorker
    {
        static bool TaskCarrierLive(Run run)
        {return run!=null&&!run.Completed&&(run.Ready||run.DeferredEye);}

        internal bool IsTaskOnlyRecordingRun
        {get{var run=Volatile.Read(ref current);return run!=null&&run.DeferredEye&&TaskCarrierLive(run)&&GenerationLive(run)&&!run.StopRequested;}}
        internal bool FormalEyePersistenceEnabled
        {get{var run=Volatile.Read(ref current);return run!=null&&run.DeferredEye&&Volatile.Read(ref run.EyePersistenceEnabled)&&GenerationLive(run)&&!run.StopRequested;}}

        // Warming the SDK does not open either archival queue or admit gaze.
        // The host calls this only after the practice exam has been passed.
        internal bool RequestFormalEyeStart()
        {
            if(!Object.ReferenceEquals(Thread.CurrentThread,headThread)||!Monitor.TryEnter(gate))return false;
            try
            {
                var run=current;
                if(disposed||run==null||!run.DeferredEye||!TaskCarrierLive(run)||run.StopRequested||
                    !GenerationLive(run)||Volatile.Read(ref statusRevoked)!=0)return false;
                if(!run.NativeStartRequested)
                {run.NativeStartRequested=true;run.Starting=true;run.Diagnostic="Checking eye tracking before the formal experiment.";run.NativeStartSignal.Set();}
                return true;
            }
            finally{Monitor.Exit(gate);}
        }

        // The exact task boundary, shared clock and same authorized task run
        // open the recording window even when eye tracking is unavailable.
        // Missing data remains missing; no cached training sample or
        // pre-boundary callback is eligible for persistence.
        internal RecordingStartReadinessState TryEnableFormalEyePersistence(object expectedRun,double boundary)
        {
            var expected=expectedRun as Run;
            if(!Object.ReferenceEquals(Thread.CurrentThread,headThread)||!WireCodec.Finite(boundary)||boundary<0||
                expected==null||!expected.DeferredEye||!IsAttachedTaskRunCurrent(expected))
                return RecordingStartReadinessState.Unavailable;
            // The owner opens this window once. A published boundary remains
            // readable during native sampling, while faults and revocation
            // independently invalidate the capability checked above.
            if(Volatile.Read(ref expected.EyePersistenceEnabled))
                return expected.StatusEnabledAt==boundary&&IsAttachedTaskRunCurrent(expected)?
                    RecordingStartReadinessState.Ready:RecordingStartReadinessState.Unavailable;
            if(!Monitor.TryEnter(gate))return RecordingStartReadinessState.Busy;
            try
            {
                var run=current;double now;
                var clock=sharedClock as MonotonicClock;
                if(disposed||run==null||!run.DeferredEye||!Object.ReferenceEquals(run,expectedRun)||
                    run.StopRequested||!GenerationLive(run)||!run.StatusEnabled||StatusAuthorityRevoked(run)||run.StatusFault||
                    run.StatusSealed||Volatile.Read(ref statusRevoked)!=0||clock==null||!clock.TryReadRecordingAdmissionTime(out now)||boundary>now)
                    return RecordingStartReadinessState.Unavailable;
                run.StatusEnabledAt=boundary;
                if(run.CaptureAcquisitionRequested)
                {run.AcquisitionEnabled=true;run.AcquisitionQueue=new ViveRawEyeAcquisition[AcquisitionCapacity];
                    run.AcquisitionSeen=new System.Collections.Generic.HashSet<ulong>();}
                Volatile.Write(ref run.EyePersistenceEnabled,true);
                if(run.ParticipantEyeFailure!=(int)EyeClientFailureKind.None)
                    RecordStatusEvidence(run,null,true,now);
                return RecordingStartReadinessState.Ready;
            }
            finally{Monitor.Exit(gate);}
        }
    }
}
