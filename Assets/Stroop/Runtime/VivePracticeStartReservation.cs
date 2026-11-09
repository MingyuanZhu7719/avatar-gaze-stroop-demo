using System;
using System.Threading;
using AvatarStroop.Core;

namespace AvatarStroop.Runtime
{
    // A fake callback used ONLY by isolated simulation tests. This contract is
    // not a Stream, file, v3 writer, UI, hardware or permission boundary.
    internal interface IVivePracticeStartFakeSink
    {bool AcceptSynthetic(VivePracticeStartBatch batch);}
    internal enum VivePracticeStartReservationOutcome { NotAttempted,Rejected,AcceptedByFake,Uncertain }

    // Owner-thread structural rehearsal with real participant and choice state
    // objects. The batch is task events only, not an eye sample. A successful
    // fake callback proves neither consent identity nor durable persistence.
    // In-flight is a logical reservation: scheduling can delay the physical
    // call after withdrawal. Therefore this must never be connected to Stream.
    internal sealed class VivePracticeStartReservation
    {
        readonly ViveDevelopmentParticipant participant;readonly IClock clock;
        readonly VivePracticeConsentAuthority choices;readonly VivePracticeStartBinding binding;
        readonly VivePracticeConsentAuthority.StartReservation retryStartReservation;
        readonly int owner;readonly object completionGate=new object();int busy,terminal;bool attempted;
        ViveDevelopmentParticipant.PracticeObservationFence retryPublicationFence;
        VivePracticeOwnedMemoryStart retryPublicationHost;
        internal VivePracticeStartReservationOutcome Outcome {get;private set;}
        internal bool CanWrite {get{return false;}}
        internal sealed class OperationGuard
        {
            readonly VivePracticeStartReservation issuer;readonly VivePracticeConsentAuthority.StartReservation reservation;
            readonly ViveDevelopmentParticipant.PracticeObservationFence fence;bool active=true;
            internal OperationGuard(VivePracticeStartReservation issuer,VivePracticeConsentAuthority.StartReservation reservation,
                ViveDevelopmentParticipant.PracticeObservationFence fence){this.issuer=issuer;this.reservation=reservation;this.fence=fence;}
            internal bool BelongsTo(VivePracticeStartReservation candidate){return Object.ReferenceEquals(issuer,candidate);}
            internal bool CanContinue()
            {
                lock(issuer.completionGate)
                {
                    if(Thread.CurrentThread.ManagedThreadId!=issuer.owner){Interlocked.Exchange(ref issuer.terminal,1);return false;}
                    return active&&!issuer.Faulted&&issuer.choices.CanContinueStart(reservation,fence);
                }
            }
            internal void Close(){lock(issuer.completionGate)active=false;}
        }
        internal VivePracticeStartReservation(ViveDevelopmentParticipant participant,IClock clock,
            VivePracticeConsentAuthority choices,VivePracticeStartBinding binding)
        {
            if(participant==null||clock==null||choices==null||binding==null)throw new ArgumentNullException("participant/clock/choices/binding");
            if(!binding.BelongsTo(participant,clock,choices))throw new ArgumentException("The same actual objects are required");
            this.participant=participant;this.clock=clock;this.choices=choices;this.binding=binding;owner=Thread.CurrentThread.ManagedThreadId;
        }
        internal VivePracticeStartReservation(ViveDevelopmentParticipant participant,IClock clock,
            VivePracticeConsentAuthority choices,VivePracticeStartBinding binding,VivePracticeConsentAuthority.StartReservation retryReservation)
            :this(participant,clock,choices,binding)
        {if(retryReservation==null)throw new ArgumentNullException("retryReservation");retryStartReservation=retryReservation;}
        bool Enter()
        {
            lock(completionGate)
            {
                if(Thread.CurrentThread.ManagedThreadId!=owner||busy!=0){Interlocked.Exchange(ref terminal,1);return false;}
                busy=1;return true;
            }
        }
        bool Faulted {get{return Interlocked.CompareExchange(ref terminal,0,0)!=0;}}
        internal bool IsFaulted {get{return Faulted;}}
        internal bool CompletionLockHeld {get{return Monitor.IsEntered(completionGate);}}
        // No I/O, used to latch a surrounding owner/reentrancy fault before
        // its private memory result is made available.
        internal void Invalidate(){lock(completionGate)Interlocked.Exchange(ref terminal,1);}
        internal bool TryUseOwnedStart(VivePracticeOwnedStartReceipt receipt,bool consume)
        {lock(completionGate)return Thread.CurrentThread.ManagedThreadId==owner&&!Faulted&&busy==0&&choices.TryUseOwnedStart(receipt,consume);}
        internal bool IsOwnedCursorCurrent(VivePracticeCursorAnchor anchor,ViveDevelopmentParticipant.PracticeObservationFence pendingFence=null)
        {lock(completionGate)return Thread.CurrentThread.ManagedThreadId==owner&&!Faulted&&busy==0&&choices.IsOwnedCursorCurrent(anchor,pendingFence);}
        internal bool TryPublishSyntheticEye(VivePracticeCursorAnchor anchor,VivePracticeTaskEventCursor cursor,VivePracticeSyntheticEyeJoin join,
            ViveDevelopmentParticipant.PracticeObservationFence fence,VivePracticeSyntheticTicket ticket)
        {lock(completionGate)return Thread.CurrentThread.ManagedThreadId==owner&&!Faulted&&busy==0&&cursor!=null&&cursor.CompletionLockHeld&&
            choices.TryPublishSyntheticEye(anchor,cursor,join,fence,ticket);}
        internal bool TryIssueRetryContinuation(VivePracticeCursorAnchor anchor,VivePracticeTaskEventCursor cursor,
            ViveDevelopmentParticipant.PracticeTaskDrainFence fence,out VivePracticeRetryContinuation continuation)
        {lock(completionGate){continuation=null;return Thread.CurrentThread.ManagedThreadId==owner&&!Faulted&&busy==0&&choices.TryIssueRetryContinuation(anchor,cursor,fence,out continuation);}}
        internal bool TryReserveRetryBegin(VivePracticeCursorAnchor anchor,VivePracticeTaskEventCursor cursor,
            VivePracticeRetryContinuation continuation,ViveDevelopmentParticipant.PracticeTaskDrainFence fence)
        {lock(completionGate)return Thread.CurrentThread.ManagedThreadId==owner&&!Faulted&&busy==0&&choices.TryReserveRetryBegin(anchor,cursor,continuation,fence);}
        internal bool TryClaimRetryContinuation(VivePracticeCursorAnchor anchor,VivePracticeTaskEventCursor cursor,
            VivePracticeRetryContinuation continuation,VivePracticeStartBinding.Ticket ticket,VivePracticeContextV3 context,
            out VivePracticeConsentAuthority.StartReservation nextReservation)
        {lock(completionGate){nextReservation=null;return Thread.CurrentThread.ManagedThreadId==owner&&!Faulted&&busy==0&&choices.TryClaimRetryContinuation(anchor,cursor,continuation,ticket,context,out nextReservation);}}
        internal bool TryPassRetryPublication(VivePracticeCursorAnchor anchor,VivePracticeTaskEventCursor cursor,
            VivePracticeRetryContinuation continuation,VivePracticeStartReservation newReservation,VivePracticeOwnedMemoryStart newHost,
            VivePracticeConsentAuthority.StartReservation start,ViveDevelopmentParticipant.PracticeObservationFence fence)
        {
            lock(completionGate)
            {
                if(Thread.CurrentThread.ManagedThreadId!=owner||Faulted||busy!=0||cursor==null||!cursor.CompletionLockHeld||
                    newReservation==null||Object.ReferenceEquals(newReservation,this))return false;
                return newReservation.TryFinishRetryUnderOldReservation(anchor,cursor,continuation,newHost,start,fence);
            }
        }
        bool TryFinishRetryUnderOldReservation(VivePracticeCursorAnchor anchor,VivePracticeTaskEventCursor cursor,
            VivePracticeRetryContinuation continuation,VivePracticeOwnedMemoryStart newHost,
            VivePracticeConsentAuthority.StartReservation start,ViveDevelopmentParticipant.PracticeObservationFence fence)
        {
            lock(completionGate)
            {
                if(Thread.CurrentThread.ManagedThreadId!=owner||Faulted||busy!=1||Outcome!=VivePracticeStartReservationOutcome.Uncertain||
                    continuation==null||continuation.Issuer==null||!continuation.Issuer.RetryOldReservationLockHeld||
                    !Object.ReferenceEquals(retryStartReservation,start)||newHost==null||
                    !Object.ReferenceEquals(retryPublicationHost,newHost)||!Object.ReferenceEquals(retryPublicationFence,fence))return false;
                bool result=choices.TryFinishRetryStart(this,newHost,continuation,start,fence);
                retryPublicationHost=null;retryPublicationFence=null;return result;
            }
        }
        internal bool IsOwnedCursorDrainCurrent(VivePracticeCursorAnchor anchor,ViveDevelopmentParticipant.PracticeTaskDrainFence fence,bool requireOutstanding)
        {lock(completionGate)return Thread.CurrentThread.ManagedThreadId==owner&&!Faulted&&busy==0&&choices.IsOwnedCursorDrainCurrent(anchor,fence,requireOutstanding);}
        internal bool TryBeginCursorAppend(VivePracticeCursorAnchor anchor,VivePracticeTaskEventCursor cursor,VivePracticeTaskEventCursor.CursorAppendLease lease,
            VivePracticeMultiWriter.Prepared prepared,byte[] digest,int length,out ViveExclusiveMemoryTarget.Receipt oldReceipt)
        {lock(completionGate){oldReceipt=null;return Thread.CurrentThread.ManagedThreadId==owner&&!Faulted&&busy==0&&choices.TryBeginOwnedCursorAppend(anchor,cursor,lease,prepared,digest,length,out oldReceipt);}}
        internal bool CanContinueCursorAppend(VivePracticeCursorAnchor anchor,VivePracticeTaskEventCursor cursor,VivePracticeTaskEventCursor.CursorAppendLease lease)
        {lock(completionGate)return Thread.CurrentThread.ManagedThreadId==owner&&!Faulted&&busy==0&&choices.CanContinueOwnedCursorAppend(anchor,cursor,lease);}
        internal bool TryFinalizeCursorAppend(VivePracticeCursorAnchor anchor,VivePracticeTaskEventCursor cursor,VivePracticeTaskEventCursor.CursorAppendLease lease,
            ViveExclusiveMemoryTarget.Receipt receipt,ViveDevelopmentParticipant.PracticeObservationFence fence)
        {lock(completionGate)return Thread.CurrentThread.ManagedThreadId==owner&&!Faulted&&busy==0&&choices.TryFinalizeOwnedCursorAppend(anchor,cursor,lease,receipt,fence);}
        internal bool TryFinalizeCursorDrain(VivePracticeCursorAnchor anchor,VivePracticeTaskEventCursor cursor,VivePracticeTaskEventCursor.CursorAppendLease lease,
            ViveExclusiveMemoryTarget.Receipt receipt,ViveDevelopmentParticipant.PracticeTaskDrainFence fence)
        {lock(completionGate)return Thread.CurrentThread.ManagedThreadId==owner&&!Faulted&&busy==0&&choices.TryFinalizeOwnedCursorDrain(anchor,cursor,lease,receipt,fence);}
        bool TakeFence(long revision,out ViveDevelopmentParticipant.PracticeObservationFence fence)
        {
            fence=null;
            if(!participant.TryAcquirePracticeObservationFence(clock,out fence)||fence==null)return false;
            var trace=fence.StepTrace;var snapshot=fence.Snapshot;
            return trace!=null&&trace.Count>0&&snapshot!=null&&snapshot.Task.Phase==Phase.Practice&&
                trace[trace.Count-1].Revision==revision&&Object.ReferenceEquals(trace[trace.Count-1].After,snapshot);
        }
        internal bool TryAccept(VivePracticeStartBinding.Ticket ticket,IVivePracticeStartFakeSink fake)
        {return TryAcceptCore(ticket,fake,null,null);}
        internal bool TryAcceptMemory(VivePracticeStartBinding.Ticket ticket,IVivePracticeStartFakeSink fake,VivePracticeMemoryPublication publication)
        {if(publication==null)return false;return TryAcceptCore(ticket,fake,publication,null);}
        internal bool TryAcceptOwned(VivePracticeStartBinding.Ticket ticket,VivePracticeOwnedMemoryStart owned)
        {if(owned==null)return false;return TryAcceptCore(ticket,null,null,owned);}
        bool TryAcceptCore(VivePracticeStartBinding.Ticket ticket,IVivePracticeStartFakeSink fake,VivePracticeMemoryPublication publication,VivePracticeOwnedMemoryStart owned)
        {
            if(!Enter())return false;
            ViveDevelopmentParticipant.PracticeObservationFence fence=null;
            VivePracticeConsentAuthority.StartReservation reservation=null;
            OperationGuard guard=null;bool inFlight=false;
            try
            {
                if(attempted||Faulted||ticket==null||(fake==null&&owned==null)||!Object.ReferenceEquals(ticket.Issuer,binding))return false;
                attempted=true;Outcome=VivePracticeStartReservationOutcome.Rejected;
                VivePracticeStartBatch batch;
                if(retryStartReservation!=null&&(owned==null||!choices.IsCurrentRetryStart(retryStartReservation,ticket)))return false;
                if(!binding.TryTake(ticket,out batch)||batch==null||!TakeFence(batch.Revision,out fence))return false;
                if(retryStartReservation!=null)reservation=retryStartReservation;
                else if(!choices.TryReserveStart(ticket.ChoiceGeneration,participant,clock,out reservation))return false;
                bool before=participant.TryConsumePracticeObservationFence(fence);fence=null;
                if(!before||Faulted||!TakeFence(batch.Revision,out fence))return false;
                lock(completionGate)
                {
                    if(Faulted||!choices.TryBeginStart(reservation,fence))return false;
                    // From this point an invocation may have occurred. A
                    // concurrent withdrawal is uncertain even if no bytes are observable.
                    inFlight=true;Outcome=VivePracticeStartReservationOutcome.Uncertain;
                }
                bool accepted;
                if(owned!=null){guard=new OperationGuard(this,reservation,fence);accepted=owned.AcceptSynthetic(batch,guard);guard.Close();guard=null;}
                else accepted=fake.AcceptSynthetic(batch);
                var completedFence=fence;bool current=participant.TryConsumePracticeObservationFence(fence);fence=null;
                if(retryStartReservation!=null)
                {
                    var cap=retryStartReservation.RetryContinuation;
                    if(!accepted||!current||Faulted||cap==null||cap.Cursor==null||owned==null)return false;
                    lock(completionGate)
                    {
                        if(Faulted||retryPublicationHost!=null||retryPublicationFence!=null)return false;
                        retryPublicationHost=owned;retryPublicationFence=completedFence;
                    }
                    if(!cap.Cursor.TryPublishRetryStart(cap,this,owned,reservation,completedFence))return false;
                    Outcome=VivePracticeStartReservationOutcome.AcceptedByFake;return true;
                }
                lock(completionGate)
                {
                    VivePracticeOwnedStartReceipt receipt=owned==null?null:owned.PendingReceipt;
                    bool ownedValid=owned==null||(receipt!=null&&Object.ReferenceEquals(receipt.Issuer,owned));
                    bool committed=choices.FinishStart(reservation,accepted&&current&&!Faulted&&ownedValid,completedFence,publication,receipt);
                    if(!committed)return false;
                    Outcome=VivePracticeStartReservationOutcome.AcceptedByFake;return true;
                }
            }
            catch(Exception){Interlocked.Exchange(ref terminal,1);return false;}
            finally
            {
                if(guard!=null)guard.Close();
                if(fence!=null)participant.AbandonPracticeObservationFence(fence);
                if(inFlight&&Outcome==VivePracticeStartReservationOutcome.Uncertain&&reservation!=null)
                    choices.FinishStart(reservation,false,null);
                lock(completionGate){retryPublicationHost=null;retryPublicationFence=null;busy=0;}
            }
        }
    }
}
