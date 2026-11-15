using System;
using System.Threading;
using AvatarStroop.Core;

namespace AvatarStroop.Runtime
{
    public sealed partial class ViveProEyeWorker
    {
        // The owner needs exact attached run/participant revocation authority,
        // not access to mutable eye data or the polling gate.
        sealed class TaskRunCapability
        {
            internal readonly Run Run;
            internal readonly ViveRawRevocation Authority;
            internal TaskRunCapability(Run run,ViveRawRevocation authority){Run=run;Authority=authority;}
        }
        sealed class OwnerTaskPoll
        {
            internal readonly TaskRunCapability Capability;
            internal readonly ViveProEyePoll Frame;
            internal readonly bool Measured;
            internal readonly long StatusRevision,Loss;
            internal OwnerTaskPoll(TaskRunCapability capability,ViveProEyePoll frame,bool measured,long statusRevision,long loss)
            {Capability=capability;Frame=frame;Measured=measured;StatusRevision=statusRevision;Loss=loss;}
        }
        // These are owner-thread-only, from the actual injected head reader.
        OwnerTaskPoll issuedTaskPoll;
        RecordingFrameTicket issuedTaskTicket,lastTaskTicket;
        long taskTicketRevision,lastTaskHeadSequence=-1;
        double lastTaskHeadObserved=-1;

        TaskRunCapability TaskCapability(object expectedRun=null)
        {
            var run=Volatile.Read(ref current);
            if(run==null||(expectedRun!=null&&!Object.ReferenceEquals(expectedRun,run))||
                Volatile.Read(ref statusRevoked)!=0||!GenerationLive(run))return null;
            var capability=Volatile.Read(ref run.TaskCapability);
            return capability!=null&&Object.ReferenceEquals(capability.Run,run)&&capability.Authority.MayReserve&&
                Object.ReferenceEquals(Volatile.Read(ref current),run)&&
                Object.ReferenceEquals(Volatile.Read(ref run.TaskCapability),capability)&&
                Volatile.Read(ref statusRevoked)==0&&GenerationLive(run)?capability:null;
        }
        internal bool IsAttachedTaskRunCurrent(object expectedRun)
        {return expectedRun!=null&&TaskCapability(expectedRun)!=null;}

        internal bool IsIndependentTaskPoll(ViveProEyePoll frame)
        {return frame!=null&&issuedTaskPoll!=null&&Object.ReferenceEquals(issuedTaskPoll.Frame,frame);}

        internal bool TryPollTaskForRecording(out ViveProEyePoll frame)
        {
            frame=null;
            if(!Object.ReferenceEquals(Thread.CurrentThread,headThread))return false;
            // A new owner observation expires any previous frame's authority.
            issuedTaskPoll=null;issuedTaskTicket=null;lastTaskTicket=null;
            var capability=TaskCapability();
            if(capability==null)return false;
            ViveHeadFrame head;
            try{head=headReader();}catch(Exception){return false;}
            if(head==null||!Object.ReferenceEquals(capability,TaskCapability(capability.Run)))return false;
            ViveProEyePoll latest=null;bool measured=false;long statusRevision=0,loss=0;
            if(Monitor.TryEnter(gate))
            {
                try
                {
                    if(!Object.ReferenceEquals(capability,TaskCapability(capability.Run)))return false;
                    var run=capability.Run;latest=run.Latest;measured=true;
                    statusRevision=run.StatusRevision;loss=run.LossGeneration;
                }
                finally{Monitor.Exit(gate);}
            }
            var snapshot=latest==null?new ViveProEyePoll(head,null,ViveEyeRuntimeState.Unavailable,ViveEyeCalibrationState.Unknown,false):
                new ViveProEyePoll(head,latest.Eye,latest.Runtime,latest.Calibration,latest.EyeDeviceConnected,latest.NativeResultCode,latest.NativeRuntimeStatus);
            if(!Object.ReferenceEquals(capability,TaskCapability(capability.Run)))return false;
            issuedTaskPoll=new OwnerTaskPoll(capability,snapshot,measured,statusRevision,loss);
            frame=snapshot;return true;
        }

        bool IndependentHeadLoss(ViveProEyePoll frame,object expectedRun)
        {
            var poll=issuedTaskPoll;
            return expectedRun!=null&&poll!=null&&Object.ReferenceEquals(poll.Frame,frame)&&issuedTaskTicket==null&&
                Object.ReferenceEquals(poll.Capability,TaskCapability(expectedRun))&&
                frame.Head.ObservationSequence>lastTaskHeadSequence&&frame.Head.ObservedLocalSeconds>lastTaskHeadObserved;
        }

        internal RecordingTicketDisposition TryIssueIndependentTaskTicket(ViveProEyePoll frame,double now,double maximumAge,bool eyeStatusDeferred,
            out RecordingFrameTicket ticket)
        {
            ticket=null;
            if(!Object.ReferenceEquals(Thread.CurrentThread,headThread)||frame==null||!WireCodec.Finite(now)||
                !WireCodec.Finite(maximumAge)||maximumAge<=0||maximumAge>1)return RecordingTicketDisposition.Unavailable;
            var poll=issuedTaskPoll;
            if(poll==null||issuedTaskTicket!=null||!Object.ReferenceEquals(poll.Frame,frame)||
                !Object.ReferenceEquals(poll.Capability,TaskCapability(poll.Capability.Run))||
                taskTicketRevision==Int64.MaxValue||!HeadUsableForTicket(frame)||frame.Head.ObservationSequence<=lastTaskHeadSequence||
                now<frame.Head.ObservedLocalSeconds||now-frame.Head.ObservedLocalSeconds>maximumAge||
                frame.Head.ObservedLocalSeconds<=lastTaskHeadObserved)return RecordingTicketDisposition.Unavailable;
            ViveRawEyeObservation.Reason? absent=ViveRawEyeObservation.Reason.SnapshotBusy;
            var run=poll.Capability.Run;
            if(poll.Measured&&!eyeStatusDeferred&&Monitor.TryEnter(gate))
            {
                try
                {
                    if(!Object.ReferenceEquals(poll.Capability,TaskCapability(run)))return RecordingTicketDisposition.Unavailable;
                    // We need coherent gaze only to retain a gaze sample, not
                    // for the head-led behavioral transaction.
                    if(run.StatusCount==0&&run.StatusReserved==null&&
                        poll.StatusRevision==run.StatusRevision&&poll.Loss==run.LossGeneration)
                        absent=ClassifyTaskEyeUnderLock(run,frame,now,maximumAge);
                }
                finally{Monitor.Exit(gate);}
            }
            if(!Object.ReferenceEquals(poll.Capability,TaskCapability(run)))return RecordingTicketDisposition.Unavailable;
            ticket=new RecordingFrameTicket(this,run,frame,++taskTicketRevision,poll.Loss,poll.StatusRevision,true,true,absent,true);
            issuedTaskTicket=ticket;return RecordingTicketDisposition.Issued;
        }
        bool TryConsumeIndependentTaskTicket(RecordingFrameTicket ticket,out ViveProEyePoll frame)
        {
            frame=null;
            if(!Object.ReferenceEquals(Thread.CurrentThread,headThread)||!Object.ReferenceEquals(ticket,issuedTaskTicket)||
                Interlocked.Exchange(ref ticket.Consumed,1)!=0)return false;
            var poll=issuedTaskPoll;var issued=Interlocked.Exchange(ref ticket.Frame,null);
            if(poll==null||issued==null||!Object.ReferenceEquals(poll.Frame,issued)||
                !Object.ReferenceEquals(ticket.RunIdentity,poll.Capability.Run)||
                !Object.ReferenceEquals(poll.Capability,TaskCapability(ticket.RunIdentity)))return false;
            lastTaskHeadSequence=issued.Head.ObservationSequence;lastTaskHeadObserved=issued.Head.ObservedLocalSeconds;
            lastTaskTicket=ticket;frame=issued;return true;
        }
        RecordingEpochDetail ReadIndependentTaskEpoch(RecordingFrameTicket ticket)
        {
            if(!Object.ReferenceEquals(lastTaskTicket,ticket)||Volatile.Read(ref ticket.Consumed)!=1)
                return RecordingEpochDetail.TicketIdentity;
            var poll=issuedTaskPoll;
            if(poll==null||!Object.ReferenceEquals(ticket.RunIdentity,poll.Capability.Run)||
                !Object.ReferenceEquals(poll.Capability,TaskCapability(ticket.RunIdentity)))
                return RecordingEpochDetail.WorkerRun;
            return RecordingEpochDetail.Current;
        }
    }
}
