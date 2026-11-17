using System;
using System.Threading;
using AvatarStroop.Core;

namespace AvatarStroop.Runtime
{
    public sealed partial class ViveProEyeWorker
    {
        internal enum AcquisitionSealDisposition { Ready, Busy, Faulted }
        // Only requests the asynchronous native barrier; the owner never calls
        // SDK unregister or waits. Worker publication acknowledges all buffered
        // callbacks before the existing status/archival FIFO seal can proceed.
        internal AcquisitionSealDisposition PrepareAcquisitionSeal(object runIdentity)
        {
            if(!Object.ReferenceEquals(Thread.CurrentThread,headThread)||runIdentity==null)return AcquisitionSealDisposition.Faulted;
            if(!Monitor.TryEnter(gate))return AcquisitionSealDisposition.Busy;
            try
            {
                if(disposed||Volatile.Read(ref statusRevoked)!=0||current==null||!Object.ReferenceEquals(current,runIdentity)||
                    !TaskCarrierLive(current)||current.StopRequested||!GenerationLive(current)||!current.StatusEnabled||
                    current.StatusFault||StatusAuthorityRevoked(current))return AcquisitionSealDisposition.Faulted;
                if(!current.AcquisitionEnabled||current.AcquisitionSource==null)return AcquisitionSealDisposition.Ready;
                Interlocked.Exchange(ref current.AcquisitionSealRequested,1);
                return current.AcquisitionProviderDrained?AcquisitionSealDisposition.Ready:AcquisitionSealDisposition.Busy;
            }
            finally{Monitor.Exit(gate);}
        }
        public bool UsesHighResolutionPollDelay
        {get{lock(gate){return current!=null&&current.HighResolutionPollDelay;}}}

        // The caller must reserve downstream space before dequeuing. Production
        // admission holds RecordingTicketGate across peek, reserve and dequeue,
        // and still checks its participant revocation fence before publication.
        internal StatusDisposition TryPeekAcquisition(object runIdentity,out ViveRawEyeAcquisition value)
        {return ReadAcquisition(runIdentity,false,out value);}
        internal StatusDisposition TryDequeueAcquisition(object runIdentity,out ViveRawEyeAcquisition value)
        {return ReadAcquisition(runIdentity,true,out value);}
        internal StatusDisposition ReadAcquisitionBacklog(object runIdentity)
        {ViveRawEyeAcquisition ignored;return ReadAcquisition(runIdentity,false,out ignored);}
        StatusDisposition ReadAcquisition(object runIdentity,bool dequeue,out ViveRawEyeAcquisition value)
        {
            value=null;
            if(!Object.ReferenceEquals(Thread.CurrentThread,headThread)||runIdentity==null)return StatusDisposition.Unavailable;
            if(!Monitor.TryEnter(gate))return StatusDisposition.Busy;
            try
            {
                if(disposed||Volatile.Read(ref statusRevoked)!=0||current==null||!Object.ReferenceEquals(current,runIdentity)||
                    !TaskCarrierLive(current)||current.StopRequested||!GenerationLive(current)||!current.StatusEnabled||StatusAuthorityRevoked(current))
                    return StatusDisposition.Unavailable;
                if(current.StatusFault)return StatusDisposition.Faulted;
                if(!current.AcquisitionEnabled||current.AcquisitionCount==0)return StatusDisposition.Empty;
                value=current.AcquisitionQueue[current.AcquisitionHead];
                if(value==null){FaultStatusEvidence(current);return StatusDisposition.Faulted;}
                if(dequeue)
                {
                    current.AcquisitionQueue[current.AcquisitionHead]=null;
                    current.AcquisitionHead=(current.AcquisitionHead+1)%current.AcquisitionQueue.Length;
                    current.AcquisitionCount--;
                }
                return StatusDisposition.Event;
            }
            finally{Monitor.Exit(gate);}
        }
        static void ClearAcquisitionEvidence(Run run)
        {
            run.AcquisitionEnabled=false;run.AcquisitionQueue=null;run.AcquisitionSeen=null;
            run.AcquisitionHead=run.AcquisitionCount=0;
        }
        static void RecordAcquisitionEvidence(Run run,ViveProEyePoll poll,bool timeValid,double time)
        {
            if(!run.AcquisitionEnabled||!run.StatusEnabled||StatusAuthorityRevoked(run)||run.StatusFault||run.StatusSealed)return;
            if(!timeValid||time<run.StatusEnabledAt){FaultStatusEvidence(run);return;}
            if(poll!=null&&poll.Eye!=null)
            {
                if(!WireCodec.Finite(poll.Eye.ObservedLocalSeconds)||poll.Eye.ObservedLocalSeconds<0)
                {FaultStatusEvidence(run);return;}
                if(poll.Eye.ObservedLocalSeconds<=run.StatusEnabledAt)return;
            }
            run.AcquisitionPolls++;
            if(poll==null||poll.Eye==null||poll.NativeResultCode!=0)return;
            var eye=poll.Eye;var timing=poll.NativeDiagnostics;
            // The loop admission also excludes polls reserved before enable. A
            // timed provider makes the API-start boundary explicit in the file.
            if(timing!=null&&!timing.CallbackBased&&timing.PollStartedLocalSeconds<run.StatusEnabledAt)return;
            if(!WireCodec.Finite(eye.ObservedLocalSeconds)||eye.ObservedLocalSeconds<run.StatusEnabledAt||eye.ObservedLocalSeconds>time)
            {FaultStatusEvidence(run);return;}
            ulong identity=((ulong)(uint)eye.FrameSequence<<32)|(uint)eye.Timestamp;
            if(run.AcquisitionSeen.Contains(identity)){run.AcquisitionDuplicates++;return;}
            if(run.AcquisitionQueue==null||run.AcquisitionCount==run.AcquisitionQueue.Length||
                run.AcquisitionSeen.Count>=MaximumAcquisitionIdentities||run.AcquisitionSequence==Int64.MaxValue)
            {
                run.Diagnostic="Eye acquisition queue/identity budget exhausted; recording admission stopped without silently dropping samples.";
                FaultStatusEvidence(run);return;
            }
            if(run.HasAcquisitionEye)
            {
                long delta=(long)eye.FrameSequence-run.LastAcquisitionFrame;
                if(delta>1)run.AcquisitionNativeGaps+=delta-1;
                else if(delta<=0)run.AcquisitionDiscontinuities++;
            }
            run.HasAcquisitionEye=true;run.LastAcquisitionFrame=eye.FrameSequence;run.AcquisitionSeen.Add(identity);
            // The provider has no simultaneous main-thread head observation.
            var eyeOnly=new ViveProEyePoll(null,eye,poll.Runtime,poll.Calibration,poll.EyeDeviceConnected,
                poll.NativeResultCode,poll.NativeRuntimeStatus,poll.NativeDiagnostics);
            var value=new ViveRawEyeAcquisition(run.AcquisitionSequence++,time,eyeOnly,run.AcquisitionPolls,
                run.AcquisitionDuplicates,run.AcquisitionNativeGaps,run.AcquisitionDiscontinuities);
            run.AcquisitionQueue[(run.AcquisitionHead+run.AcquisitionCount)%run.AcquisitionQueue.Length]=value;
            run.AcquisitionCount++;
        }
    }
}
