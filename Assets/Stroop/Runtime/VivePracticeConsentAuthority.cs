using System;
using System.Threading;
using AvatarStroop.Core;

namespace AvatarStroop.Runtime
{
    // Application choice state for ONE observed simulation participant. The
    // two methods are intended to be called by separate, explicit UI actions;
    // no caller, person, vendor consent or protocol approval is authenticated.
    // This class is not wired to UI, device, files or a writer and grants no
    // permission to record. Revocation may be signalled from any thread.
    public sealed class VivePracticeConsentAuthority
    {
        readonly ViveDevelopmentParticipant participant;
        readonly IClock clock;
        readonly string clockId;
        readonly int owner;
        readonly object stateGate=new object();
        int busy;bool revoked,faulted,eye,task;
        bool startReservationIssued,retryReservationIssued;
        StartReservation startReservation;
        long generation;
        double enabledAt=-1,lastNow=-1;
        public bool CanWrite {get{return false;}}
        public bool ConsentAuthenticated {get{return false;}}
        public bool Active {get{lock(stateGate)return !revoked&&!faulted&&eye&&task&&!participant.PracticeConsentWithdrawalRequested;}}
        public bool Revoked {get{lock(stateGate)return revoked||participant.PracticeConsentWithdrawalRequested;}}
        public bool Faulted {get{lock(stateGate)return faulted;}}
        internal long Generation {get{lock(stateGate)return generation;}}
        internal double EnabledAt {get{lock(stateGate)return enabledAt;}}
        internal bool StateLockHeld {get{return Monitor.IsEntered(stateGate);}}
        // Only for an in-memory start-boundary rehearsal. This reservation is
        // not a writer, eye-sample permit, or authorization for real I/O.
        internal sealed class StartReservation
        {
            internal readonly VivePracticeConsentAuthority Issuer;internal readonly long Generation;
            internal readonly VivePracticeRetryContinuation RetryContinuation;
            internal readonly VivePracticeStartBinding.Ticket RetryTicket;
            internal readonly VivePracticeContextV3 RetryContext;
            internal bool InFlight,Finished,Uncertain;
            internal StartReservation(VivePracticeConsentAuthority issuer,long generation){Issuer=issuer;Generation=generation;}
            internal StartReservation(VivePracticeConsentAuthority issuer,long generation,VivePracticeRetryContinuation continuation,
                VivePracticeStartBinding.Ticket ticket,VivePracticeContextV3 context)
            {Issuer=issuer;Generation=generation;RetryContinuation=continuation;RetryTicket=ticket;RetryContext=context;}
        }
        internal bool TryReserveStart(long expected,ViveDevelopmentParticipant candidate,IClock candidateClock,out StartReservation reservation)
        {
            reservation=null;
            if(Thread.CurrentThread.ManagedThreadId!=owner||!Object.ReferenceEquals(participant,candidate)||!Object.ReferenceEquals(clock,candidateClock))return false;
            lock(stateGate)
            {
                if(startReservationIssued||revoked||faulted||!eye||!task||generation!=expected||participant.PracticeConsentWithdrawalRequested)return false;
                startReservationIssued=true;reservation=new StartReservation(this,expected);startReservation=reservation;return true;
            }
        }
        internal bool TryFinishRetryStart(VivePracticeStartReservation newReservation,VivePracticeOwnedMemoryStart newHost,
            VivePracticeRetryContinuation continuation,StartReservation start,ViveDevelopmentParticipant.PracticeObservationFence fence)
        {
            if(Thread.CurrentThread.ManagedThreadId!=owner||newReservation==null||newHost==null||continuation==null||start==null||fence==null)return false;
            lock(stateGate)
            {
                if(!Object.ReferenceEquals(start,startReservation)||!Object.ReferenceEquals(start.RetryContinuation,continuation)||
                    !start.InFlight||start.Finished||start.Uncertain||revoked||faulted||!eye||!task||generation!=start.Generation||
                    participant.PracticeConsentWithdrawalRequested)return false;
                return participant.TryFinishRetryStart(newReservation,newHost,continuation,start,fence);
            }
        }
        internal bool CanCommitRetryStartUnderLocks(StartReservation start,VivePracticeStartReservation newReservation,
            VivePracticeOwnedMemoryStart newHost,VivePracticeRetryContinuation continuation)
        {
            return Monitor.IsEntered(stateGate)&&start!=null&&continuation!=null&&continuation.Issuer!=null&&
                Object.ReferenceEquals(start,startReservation)&&Object.ReferenceEquals(start.RetryContinuation,continuation)&&
                start.InFlight&&!start.Finished&&!start.Uncertain&&!revoked&&!faulted&&eye&&task&&generation==start.Generation&&
                !participant.PracticeConsentWithdrawalRequested&&continuation.Issuer.RetryCommitLocksHeld(newReservation,newHost,continuation,start);
        }
        internal void CommitRetryStartUnderLocks(StartReservation start,VivePracticeStartReservation newReservation,
            VivePracticeOwnedMemoryStart newHost,VivePracticeRetryContinuation continuation)
        {
            if(!CanCommitRetryStartUnderLocks(start,newReservation,newHost,continuation))throw new InvalidOperationException("Retry commit locks or state unavailable");
            start.Finished=true;
        }
        internal bool IsCurrentRetryReservation(StartReservation reservation,VivePracticeStartBinding binding,VivePracticeContextV3 finalContext)
        {
            if(Thread.CurrentThread.ManagedThreadId!=owner||reservation==null||binding==null||finalContext==null)return false;
            lock(stateGate)
            {
                var declared=reservation.RetryContext;var ticket=reservation.RetryTicket;
                return Object.ReferenceEquals(startReservation,reservation)&&Object.ReferenceEquals(reservation.Issuer,this)&&retryReservationIssued&&
                    reservation.RetryContinuation!=null&&ticket!=null&&Object.ReferenceEquals(ticket.Issuer,binding)&&ticket.Batch!=null&&
                    ticket.Batch.Events!=null&&ticket.Batch.Events.Count==3&&declared!=null&&!declared.ObservedPracticeStartLocalSeconds.HasValue&&
                    finalContext.ObservedPracticeStartLocalSeconds.HasValue&&
                    BitConverter.DoubleToInt64Bits(finalContext.ObservedPracticeStartLocalSeconds.Value)==BitConverter.DoubleToInt64Bits(ticket.Batch.Events[0].Time)&&
                    declared.SessionId==finalContext.SessionId&&declared.ParticipantId==finalContext.ParticipantId&&declared.StreamId==finalContext.StreamId&&
                    declared.DeviceId==finalContext.DeviceId&&declared.ClockId==finalContext.ClockId&&declared.MappingVersion==finalContext.MappingVersion&&
                    declared.PracticeAttempt==finalContext.PracticeAttempt&&declared.RecordingEpochId==finalContext.RecordingEpochId&&
                    declared.RecoveryOrdinal==finalContext.RecoveryOrdinal&&declared.PreviousEpochId==finalContext.PreviousEpochId&&declared.EpochReason==finalContext.EpochReason&&
                    !reservation.InFlight&&!reservation.Finished&&!reservation.Uncertain&&!revoked&&!faulted&&eye&&task&&
                    generation==reservation.Generation&&!participant.PracticeConsentWithdrawalRequested;
            }
        }
        internal bool IsCurrentRetryStart(StartReservation reservation,VivePracticeStartBinding.Ticket ticket)
        {
            if(Thread.CurrentThread.ManagedThreadId!=owner||reservation==null||ticket==null)return false;
            lock(stateGate)return Object.ReferenceEquals(startReservation,reservation)&&Object.ReferenceEquals(reservation.Issuer,this)&&
                Object.ReferenceEquals(reservation.RetryTicket,ticket)&&reservation.RetryContinuation!=null&&reservation.RetryContext!=null&&
                !reservation.InFlight&&!reservation.Finished&&!reservation.Uncertain&&!revoked&&!faulted&&eye&&task&&
                generation==reservation.Generation&&!participant.PracticeConsentWithdrawalRequested;
        }
        internal bool TryBeginStart(StartReservation reservation,ViveDevelopmentParticipant.PracticeObservationFence fence)
        {
            if(Thread.CurrentThread.ManagedThreadId!=owner)return false;
            lock(stateGate)
            {
                if(reservation==null||!Object.ReferenceEquals(reservation,startReservation)||!Object.ReferenceEquals(reservation.Issuer,this)||
                    reservation.Finished||reservation.InFlight||revoked||faulted||!eye||!task||generation!=reservation.Generation||
                    participant.PracticeConsentWithdrawalRequested)return false;
                return participant.TryMarkPracticeStartInFlight(fence,reservation);
            }
        }
        internal bool FinishStart(StartReservation reservation,bool completedAndContextCurrent,ViveDevelopmentParticipant.PracticeObservationFence fence,
            VivePracticeMemoryPublication publication=null,VivePracticeOwnedStartReceipt ownedReceipt=null)
        {
            if(Thread.CurrentThread.ManagedThreadId!=owner)return false;
            lock(stateGate)
            {
                if(reservation==null||!Object.ReferenceEquals(reservation,startReservation)||!reservation.InFlight||reservation.Finished)return false;
                reservation.Finished=true;
                if(!completedAndContextCurrent||revoked||faulted||!eye||!task||generation!=reservation.Generation||
                    participant.PracticeConsentWithdrawalRequested||!participant.PracticeGenerationStillCurrent(fence)||
                    (publication!=null&&!participant.TryPublishPracticeMemoryStage(fence,publication))||
                    (ownedReceipt!=null&&(!Object.ReferenceEquals(ownedReceipt.Choices,this)||ownedReceipt.ChoiceGeneration!=reservation.Generation||
                        !participant.TryPublishOwnedPracticeStart(fence,ownedReceipt))))
                    reservation.Uncertain=true;
                return !reservation.Uncertain;
            }
        }
        internal bool CanContinueStart(StartReservation reservation,ViveDevelopmentParticipant.PracticeObservationFence fence)
        {
            if(Thread.CurrentThread.ManagedThreadId!=owner)return false;
            lock(stateGate)return reservation!=null&&Object.ReferenceEquals(reservation,startReservation)&&reservation.InFlight&&!reservation.Finished&&!reservation.Uncertain&&
                !revoked&&!faulted&&eye&&task&&generation==reservation.Generation&&participant.PracticeFenceStillOutstanding(fence);
        }
        internal bool TryUseOwnedStart(VivePracticeOwnedStartReceipt receipt,bool consume)
        {
            if(Thread.CurrentThread.ManagedThreadId!=owner||receipt==null||!Object.ReferenceEquals(receipt.Choices,this)||
                !Object.ReferenceEquals(receipt.Clock,clock))return false;
            lock(stateGate)
            {
                if(revoked||faulted||!eye||!task||generation!=receipt.ChoiceGeneration)return false;
                return participant.TryUseOwnedPracticeStart(receipt,consume);
            }
        }
        internal bool IsOwnedCursorCurrent(VivePracticeCursorAnchor anchor,ViveDevelopmentParticipant.PracticeObservationFence pendingFence=null)
        {
            if(Thread.CurrentThread.ManagedThreadId!=owner||anchor==null||!Object.ReferenceEquals(anchor.Choices,this)||!Object.ReferenceEquals(anchor.Clock,clock))return false;
            lock(stateGate)
                return !revoked&&!faulted&&eye&&task&&generation==anchor.ChoiceGeneration&&participant.IsOwnedPracticeCursorCurrent(anchor,pendingFence);
        }
        internal bool TryPublishSyntheticEye(VivePracticeCursorAnchor anchor,VivePracticeTaskEventCursor cursor,VivePracticeSyntheticEyeJoin join,
            ViveDevelopmentParticipant.PracticeObservationFence fence,VivePracticeSyntheticTicket ticket)
        {
            if(Thread.CurrentThread.ManagedThreadId!=owner||anchor==null||cursor==null||join==null||fence==null||ticket==null)return false;
            lock(stateGate)
                return !revoked&&!faulted&&eye&&task&&generation==anchor.ChoiceGeneration&&generation==ticket.Generation&&
                    Object.ReferenceEquals(anchor.Choices,this)&&Object.ReferenceEquals(anchor.Clock,clock)&&
                    participant.TryPublishSyntheticEye(anchor,cursor,join,fence,ticket);
        }
        internal bool TryIssueRetryContinuation(VivePracticeCursorAnchor anchor,VivePracticeTaskEventCursor cursor,
            ViveDevelopmentParticipant.PracticeTaskDrainFence fence,out VivePracticeRetryContinuation continuation)
        {
            continuation=null;if(Thread.CurrentThread.ManagedThreadId!=owner||anchor==null||!Object.ReferenceEquals(anchor.Choices,this)||!Object.ReferenceEquals(anchor.Clock,clock))return false;
            lock(stateGate)return !retryReservationIssued&&startReservationIssued&&startReservation!=null&&startReservation.Finished&&!startReservation.Uncertain&&
                !revoked&&!faulted&&eye&&task&&generation==anchor.ChoiceGeneration&&participant.TryIssueRetryContinuation(anchor,cursor,fence,out continuation);
        }
        internal bool TryReserveRetryBegin(VivePracticeCursorAnchor anchor,VivePracticeTaskEventCursor cursor,
            VivePracticeRetryContinuation continuation,ViveDevelopmentParticipant.PracticeTaskDrainFence fence)
        {
            if(Thread.CurrentThread.ManagedThreadId!=owner||anchor==null||!Object.ReferenceEquals(anchor.Choices,this)||!Object.ReferenceEquals(anchor.Clock,clock))return false;
            lock(stateGate)return !retryReservationIssued&&startReservationIssued&&startReservation!=null&&startReservation.Finished&&!startReservation.Uncertain&&
                !revoked&&!faulted&&eye&&task&&generation==anchor.ChoiceGeneration&&participant.TryReserveRetryBegin(anchor,cursor,continuation,fence);
        }
        internal bool TryClaimRetryContinuation(VivePracticeCursorAnchor anchor,VivePracticeTaskEventCursor cursor,
            VivePracticeRetryContinuation continuation,VivePracticeStartBinding.Ticket ticket,VivePracticeContextV3 context,out StartReservation nextReservation)
        {
            nextReservation=null;if(Thread.CurrentThread.ManagedThreadId!=owner||anchor==null||!Object.ReferenceEquals(anchor.Choices,this)||!Object.ReferenceEquals(anchor.Clock,clock))return false;
            lock(stateGate)
            {
                if(retryReservationIssued||!startReservationIssued||startReservation==null||!startReservation.Finished||startReservation.Uncertain||
                    revoked||faulted||!eye||!task||generation!=anchor.ChoiceGeneration||participant.PracticeConsentWithdrawalRequested)return false;
                var candidate=new StartReservation(this,generation,continuation,ticket,context);
                if(!participant.TryClaimRetryContinuation(anchor,cursor,continuation,ticket,context))return false;
                retryReservationIssued=true;startReservation=candidate;
                if(!participant.TryFinalizeRetryClaim(anchor,continuation,ticket)){candidate.Uncertain=true;faulted=true;return false;}
                nextReservation=candidate;return true;
            }
        }
        internal bool IsOwnedCursorDrainCurrent(VivePracticeCursorAnchor anchor,ViveDevelopmentParticipant.PracticeTaskDrainFence fence,bool requireOutstanding)
        {
            if(Thread.CurrentThread.ManagedThreadId!=owner||anchor==null||fence==null||!Object.ReferenceEquals(anchor.Choices,this)||!Object.ReferenceEquals(anchor.Clock,clock))return false;
            lock(stateGate)return !revoked&&!faulted&&eye&&task&&generation==anchor.ChoiceGeneration&&participant.IsOwnedPracticeCursorDrainCurrent(anchor,fence,requireOutstanding);
        }
        internal bool TryBeginOwnedCursorAppend(VivePracticeCursorAnchor anchor,VivePracticeTaskEventCursor cursor,
            VivePracticeTaskEventCursor.CursorAppendLease lease,VivePracticeMultiWriter.Prepared prepared,byte[] digest,int length,
            out ViveExclusiveMemoryTarget.Receipt oldReceipt)
        {
            oldReceipt=null;if(Thread.CurrentThread.ManagedThreadId!=owner||anchor==null||!Object.ReferenceEquals(anchor.Choices,this)||!Object.ReferenceEquals(anchor.Clock,clock))return false;
            lock(stateGate)
            {
                if(revoked||faulted||!eye||!task||generation!=anchor.ChoiceGeneration)return false;
                return participant.TryBeginOwnedCursorAppend(anchor,cursor,lease,prepared,digest,length,out oldReceipt);
            }
        }
        internal bool CanContinueOwnedCursorAppend(VivePracticeCursorAnchor anchor,VivePracticeTaskEventCursor cursor,VivePracticeTaskEventCursor.CursorAppendLease lease)
        {
            if(Thread.CurrentThread.ManagedThreadId!=owner||anchor==null||!Object.ReferenceEquals(anchor.Choices,this)||!Object.ReferenceEquals(anchor.Clock,clock))return false;
            lock(stateGate)return !revoked&&!faulted&&eye&&task&&generation==anchor.ChoiceGeneration&&participant.CanContinueOwnedCursorAppend(anchor,cursor,lease);
        }
        internal bool TryFinalizeOwnedCursorAppend(VivePracticeCursorAnchor anchor,VivePracticeTaskEventCursor cursor,
            VivePracticeTaskEventCursor.CursorAppendLease lease,ViveExclusiveMemoryTarget.Receipt receipt,ViveDevelopmentParticipant.PracticeObservationFence fence)
        {
            if(Thread.CurrentThread.ManagedThreadId!=owner||anchor==null||!Object.ReferenceEquals(anchor.Choices,this)||!Object.ReferenceEquals(anchor.Clock,clock))return false;
            lock(stateGate)return !revoked&&!faulted&&eye&&task&&generation==anchor.ChoiceGeneration&&participant.TryFinalizeOwnedCursorAppend(anchor,cursor,lease,receipt,fence);
        }

        internal bool TryFinalizeOwnedCursorDrain(VivePracticeCursorAnchor anchor,VivePracticeTaskEventCursor cursor,
            VivePracticeTaskEventCursor.CursorAppendLease lease,ViveExclusiveMemoryTarget.Receipt receipt,ViveDevelopmentParticipant.PracticeTaskDrainFence fence)
        {
            if(Thread.CurrentThread.ManagedThreadId!=owner||anchor==null||!Object.ReferenceEquals(anchor.Choices,this)||!Object.ReferenceEquals(anchor.Clock,clock))return false;
            lock(stateGate)return !revoked&&!faulted&&eye&&task&&generation==anchor.ChoiceGeneration&&participant.TryFinalizeOwnedCursorDrain(anchor,cursor,lease,receipt,fence);
        }

        public VivePracticeConsentAuthority(ViveDevelopmentParticipant participant,IClock clock,bool explicitDevelopmentOnly)
        {
            if(!explicitDevelopmentOnly)throw new InvalidOperationException("Explicit development-only scope required");
            if(participant==null||clock==null)throw new ArgumentNullException("participant/clock");
            if(participant.IsResearchSession||!participant.UsesEventClock(clock))throw new ArgumentException("The same observed simulation participant and clock are required");
            string id=clock.ClockId;if(String.IsNullOrWhiteSpace(id))throw new ArgumentException("Explicit clock identity required");
            this.participant=participant;this.clock=clock;clockId=id;owner=Thread.CurrentThread.ManagedThreadId;
            if(!participant.TryAttachPracticeConsent(this,clock))throw new InvalidOperationException("A unique observed participant choice authority is required");
        }
        void Fail(){lock(stateGate)faulted=true;}
        bool Enter()
        {
            if(Thread.CurrentThread.ManagedThreadId!=owner){Fail();return false;}
            if(Interlocked.CompareExchange(ref busy,1,0)!=0){Fail();return false;}
            return true;
        }
        static bool BeforePractice(ExperimentIncrementalSnapshot snapshot)
        {return snapshot!=null&&snapshot.Task!=null&&snapshot.Task.BlockIndex==-1&&!snapshot.Selection.HasValue&&
            (snapshot.Task.Phase==Phase.Preparation||snapshot.Task.Phase==Phase.Familiarization)&&snapshot.Scalars.PracticeAttempt==0;}
        static bool Finite(double value){return !Double.IsNaN(value)&&!Double.IsInfinity(value);}
        public bool ConfirmEye(){return Confirm(true);}
        public bool ConfirmTask(){return Confirm(false);}
        bool Confirm(bool eyeChoice)
        {
            if(!Enter())return false;
            ViveDevelopmentParticipant.PracticeObservationFence fence=null;
            try
            {
                lock(stateGate){if(revoked||faulted||participant.PracticeConsentWithdrawalRequested||(eyeChoice?eye:task))return false;}
                if(!participant.TryAcquirePracticeObservationFence(clock,out fence)||fence==null||!BeforePractice(fence.Snapshot))
                {Fail();return false;}
                string observed=clock.ClockId;double now=clock.Now;
                if(observed!=clockId||!Finite(now)||now<0||now<lastNow||Revoked||Faulted){Fail();return false;}
                if(!participant.TryConsumePracticeObservationFence(fence)){fence=null;Fail();return false;}
                fence=null;
                lock(stateGate)
                {
                    if(revoked||faulted||participant.PracticeConsentWithdrawalRequested||(eyeChoice?eye:task))return false;
                    if(generation==Int64.MaxValue){faulted=true;return false;}
                    generation++;lastNow=now;
                    if(eyeChoice)eye=true;else task=true;
                    if(eye&&task)enabledAt=now;
                    return true;
                }
            }
            catch(Exception){Fail();return false;}
            finally{if(fence!=null)participant.AbandonPracticeObservationFence(fence);Interlocked.Exchange(ref busy,0);}
        }
        // This invalidates outstanding generations immediately even when an
        // external callback or write is already in progress. It cannot undo
        // bytes already passed to a Stream; the future host must preserve the
        // uncertain prefix, latch the failure and never retry automatically.
        public void Withdraw()
        {lock(stateGate){if(revoked)return;revoked=true;if(startReservation!=null&&startReservation.InFlight&&!startReservation.Finished)startReservation.Uncertain=true;
            if(generation==Int64.MaxValue)faulted=true;else generation++;}}
        // Restricted to the sealed in-memory preview status publisher; no
        // callbacks, devices or I/O are admitted by this path.
        internal bool TryCommitPracticePreview(VivePracticePreviewCoordinator preview,
            ViveDevelopmentParticipant.PracticeObservationFence fence,long expected)
        {
            if(Thread.CurrentThread.ManagedThreadId!=owner||preview==null||fence==null)return false;
            lock(stateGate)
            {
                if(revoked||faulted||!eye||!task||generation!=expected)return false;
                return participant.CommitPreviewUnderChoiceLock(this,preview,fence,expected);
            }
        }
        // A local state check only. Caller must separately prove participant
        // current-head/phase, source, lease, epoch, age and writer ownership.
        internal bool IsCurrent(long expected,ViveDevelopmentParticipant candidate,IClock candidateClock)
        {
            if(Thread.CurrentThread.ManagedThreadId!=owner||!Object.ReferenceEquals(participant,candidate)||!Object.ReferenceEquals(clock,candidateClock))return false;
            lock(stateGate)return !revoked&&!faulted&&eye&&task&&generation==expected&&!participant.PracticeConsentWithdrawalRequested;
        }
    }
}
