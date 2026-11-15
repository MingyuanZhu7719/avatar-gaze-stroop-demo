using System;
using System.Threading;
using AvatarStroop.Core;

namespace AvatarStroop.Runtime
{
    internal sealed partial class ViveNamedSessionFrameAdmission
    {
        // A focus boundary may discard an uncommitted display reservation,
        // never an already attempted presentation or partially run mutation.
        // No ordinal was published/charged by TryReservePresentation, so only
        // that known empty slot and producer bit can be released here.
        internal bool TryAbandonPresentationForFocus(PresentationReservation token)
        {
            if(Thread.CurrentThread.ManagedThreadId!=owner||ioFence.IsRevoked||Sealed||
                Volatile.Read(ref ring)==null||(Volatile.Read(ref state)&(R|F))!=0)
            {Fail();return false;}
            if(token==null)
            {
                if(Volatile.Read(ref presentationSlot)!=null||(Volatile.Read(ref state)&P)!=0)
                {Fail();return false;}
                return true;
            }
            try
            {
                if(!Object.ReferenceEquals(token.Owner,this)||Volatile.Read(ref token.Consumed)!=0||
                    !Object.ReferenceEquals(Volatile.Read(ref presentationSlot),token)||!ProducerLive()||
                    !Monitor.TryEnter(task.NamedRecordingGate))
                {Fail();return false;}
                try
                {
                    var target=Volatile.Read(ref ring);
                    if(!task.NamedRecordingStampCurrentUnderLock(token.Stamp)||target==null||
                        Interlocked.Read(ref nextOrdinal)!=token.Ordinal||lastTaskSequence!=token.Stamp.EventCursor||
                        !Object.ReferenceEquals(task.CurrentTrial,token.Trial)||token.Trial.Outcome!=TrialOutcome.Pending||
                        token.Trial.Onset.HasValue||Volatile.Read(ref target[(int)(token.Ordinal%capacity)])!=null||
                        !ProducerLive()||Interlocked.CompareExchange(ref token.Consumed,1,0)!=0||
                        !Object.ReferenceEquals(Interlocked.CompareExchange(ref presentationSlot,null,token),token)||
                        !ClearBitIfLive(P))
                    {Fail();return false;}
                    return !ioFence.IsRevoked&&(Volatile.Read(ref state)&(R|F|S|P))==0;
                }
                finally{Monitor.Exit(task.NamedRecordingGate);}
            }
            catch(Exception error) when(!(error is OutOfMemoryException||error is StackOverflowException||error is AccessViolationException))
            {Fail();return false;}
        }
    }
}
