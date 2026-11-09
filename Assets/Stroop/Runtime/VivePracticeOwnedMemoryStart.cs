using System;
using System.Threading;
using AvatarStroop.Core;

namespace AvatarStroop.Runtime
{
    // Bridges a live, single-use start ticket to a sealed fake memory target.
    // It never opens a path, a caller Stream, a device, or a v3/eye writer.
    // The target/writer stay private; CopyForTest on the fake store is not an
    // authorization or durability receipt. Do not use for research data.
    internal sealed class VivePracticeOwnedMemoryStart
    {
        readonly VivePracticeStartReservation reservation;readonly ViveExclusiveMemoryStore store;
        readonly ViveDevelopmentParticipant participant;readonly VivePracticeConsentAuthority choices;readonly IClock clock;
        readonly VivePracticeContextV3 context;readonly string targetId;readonly int maximumBytes,maximumBatches,owner;
        readonly VivePracticeConsentAuthority.StartReservation retryReservation;
        readonly Func<int,ViveFakeWritePlan> testPlan;
        ViveExclusiveMemoryTarget target;VivePracticeMultiWriter writer;VivePracticeOwnedStartReceipt pendingReceipt;
        ViveExclusiveMemoryTarget.Receipt currentTargetReceipt,appendOldReceipt;
        VivePracticeTaskEventCursor appendCursor;VivePracticeTaskEventCursor.CursorAppendLease appendLease;
        VivePracticeMultiWriter.Prepared appendPrepared;byte[] appendDigest;int appendLength;bool appendInFlight,cursorFailed;
        long choiceGeneration;bool attempted,active,sinkBusy,appendAttempted,receiptPublished,receiptClaimed,receiptConsumed;
        ViveDevelopmentParticipant.PracticeObservationFence receiptFence;
        VivePracticeCursorAnchor activeCursorAnchor;
        VivePracticeRetryContinuation retryContinuation;
        bool retryContinuationIssued,retryContinuationConsumed,retryBeginReserved;
        internal VivePracticeStartReservationOutcome Outcome {get{return reservation.Outcome;}}
        internal bool ResearchGateOpen {get{return false;}}
        internal bool RealFileOwnershipProven {get{return false;}}
        internal bool EyeDataIncluded {get{return false;}}
        internal VivePracticeOwnedMemoryStart(ViveDevelopmentParticipant participant,IClock clock,VivePracticeConsentAuthority choices,
            VivePracticeStartBinding binding,VivePracticeContextV3 context,ViveExclusiveMemoryStore store,string targetId,
            int maximumBytes,int maximumBatches,Func<int,ViveFakeWritePlan> testPlan=null)
            :this(participant,clock,choices,binding,context,store,targetId,maximumBytes,maximumBatches,null,testPlan)
        {}
        internal VivePracticeOwnedMemoryStart(ViveDevelopmentParticipant participant,IClock clock,VivePracticeConsentAuthority choices,
            VivePracticeStartBinding binding,VivePracticeContextV3 context,ViveExclusiveMemoryStore store,string targetId,
            int maximumBytes,int maximumBatches,VivePracticeConsentAuthority.StartReservation retryReservation,Func<int,ViveFakeWritePlan> testPlan)
        {
            if(participant==null||clock==null||choices==null||binding==null||context==null||store==null)throw new ArgumentNullException("development dependencies");
            if(!binding.BelongsTo(participant,clock,choices)||context.SessionId!=participant.SessionId||context.ClockId!=participant.EventClockId||
                !participant.UsesEventClock(clock)||maximumBytes<256||maximumBytes>1048576||maximumBatches<1||maximumBatches>100000)
                throw new ArgumentException("Matching development context and bounds required");
            if(retryReservation==null)
            {
                if(context.PracticeAttempt!=1||context.EpochReason!=VivePracticeEpochReasonV3.Initial||context.RecoveryOrdinal!=0||context.PreviousEpochId!=null)
                    throw new ArgumentException("Matching initial development context required");
                reservation=new VivePracticeStartReservation(participant,clock,choices,binding);
            }
            else
            {
                if(retryReservation.RetryContinuation==null||retryReservation.RetryContinuation.Issuer==null||
                    !retryReservation.RetryContinuation.Issuer.IsDistinctRetryTarget(retryReservation.RetryContinuation,store,targetId)||
                    !choices.IsCurrentRetryReservation(retryReservation,binding,context))
                    throw new ArgumentException("An exact current retry reservation and event-derived context are required");
                reservation=new VivePracticeStartReservation(participant,clock,choices,binding,retryReservation);
            }
            this.store=store;this.context=context;this.retryReservation=retryReservation;
            this.participant=participant;this.choices=choices;this.clock=clock;
            this.targetId=targetId;this.maximumBytes=maximumBytes;this.maximumBatches=maximumBatches;this.testPlan=testPlan;
            owner=Thread.CurrentThread.ManagedThreadId;
        }
        internal bool TryStart(VivePracticeStartBinding.Ticket ticket)
        {
            if(Thread.CurrentThread.ManagedThreadId!=owner||active){reservation.Invalidate();return false;}
            if(attempted)return false;attempted=true;
            if(!RetryTargetStillCurrent())return false;
            active=true;if(ticket!=null)choiceGeneration=ticket.ChoiceGeneration;
            try
            {
                bool result=reservation.TryAcceptOwned(ticket,this);
                if(!result&&target!=null){if(appendAttempted)target.MarkOutcomeUncertain();else target.Withdraw();}
                return result;
            }
            finally{active=false;}
        }
        internal VivePracticeOwnedStartReceipt PendingReceipt {get{return pendingReceipt;}}
        // Called only under completion→choice→participant locks. No external
        // callbacks or I/O occur; the target is the sealed in-memory fake.
        internal bool TryPublishReceiptUnderParticipantLock(VivePracticeOwnedStartReceipt receipt,ViveDevelopmentParticipant.PracticeObservationFence fence)
        {
            if(!reservation.CompletionLockHeld||!choices.StateLockHeld||!participant.ObservationFenceLockHeld||
                !Object.ReferenceEquals(pendingReceipt,receipt)||receiptPublished||fence==null||target==null||!target.IsCurrentReceipt(receipt.TargetReceipt))return false;
            receiptFence=fence;currentTargetReceipt=receipt.TargetReceipt;receiptPublished=true;return true;
        }
        internal bool TryUseReceiptUnderParticipantLock(VivePracticeOwnedStartReceipt receipt,ViveDevelopmentParticipant candidate,long generation,bool consume)
        {
            if(!reservation.CompletionLockHeld||!choices.StateLockHeld||!participant.ObservationFenceLockHeld||
                !Object.ReferenceEquals(pendingReceipt,receipt)||!receiptPublished||!Object.ReferenceEquals(candidate,participant)||
                receiptFence==null||!receiptFence.BelongsTo(participant)||!receiptFence.GenerationMatches(generation)||target==null||
                !target.IsCurrentReceipt(receipt.TargetReceipt)||receiptConsumed)return false;
            if(consume)
            {
                if(!receiptClaimed||receipt.Batch==null||receipt.Batch.Events==null||receipt.Batch.Events.Count!=3)return false;
                receiptConsumed=true; // consumed even if constructing the anchor fails
                var last=receipt.Batch.Events[2];
                activeCursorAnchor=new VivePracticeCursorAnchor(this,participant,clock,choices,choiceGeneration,last.Sequence,
                    receipt.Batch.Revision,last.Time,receiptFence.Snapshot,receiptFence.StepTrace,context);
            }
            else{if(receiptClaimed)return false;receiptClaimed=true;}
            return true;
        }
        // A receipt is only a historical internal candidate. Its eventual
        // consumer must revalidate all identities, revocation and task state.
        internal bool TryTakeReceipt(out VivePracticeOwnedStartReceipt receipt)
        {
            receipt=null;if(Thread.CurrentThread.ManagedThreadId!=owner||active||pendingReceipt==null||
                !reservation.TryUseOwnedStart(pendingReceipt,false))return false;
            receipt=pendingReceipt;return true;
        }
        // The cursor must consume through this issuer; receipt fields or a
        // cloned receipt cannot advance private state or expose target handles.
        internal bool TryConsumeForCursor(VivePracticeOwnedStartReceipt receipt,out VivePracticeCursorAnchor anchor)
        {
            anchor=null;if(Thread.CurrentThread.ManagedThreadId!=owner||active||!Object.ReferenceEquals(pendingReceipt,receipt))return false;
            if(!reservation.TryUseOwnedStart(receipt,true)||activeCursorAnchor==null)return false;
            anchor=activeCursorAnchor;return true;
        }
        // Until later target append is implemented, this validates only the
        // original fake target receipt; it is not a generic ongoing I/O grant.
        internal bool IsCurrentForCursor(VivePracticeCursorAnchor anchor)
        {return Thread.CurrentThread.ManagedThreadId==owner&&!active&&anchor!=null&&reservation.IsOwnedCursorCurrent(anchor);}
        internal bool IsCurrentForCursor(VivePracticeCursorAnchor anchor,ViveDevelopmentParticipant.PracticeObservationFence pendingFence)
        {return Thread.CurrentThread.ManagedThreadId==owner&&!active&&anchor!=null&&pendingFence!=null&&reservation.IsOwnedCursorCurrent(anchor,pendingFence);}
        internal bool SyntheticReservationLockHeld {get{return reservation.CompletionLockHeld;}}
        internal bool TryPublishSyntheticEye(VivePracticeCursorAnchor anchor,VivePracticeTaskEventCursor cursor,VivePracticeSyntheticEyeJoin join,
            ViveDevelopmentParticipant.PracticeObservationFence fence,VivePracticeSyntheticTicket ticket)
        {return Thread.CurrentThread.ManagedThreadId==owner&&!active&&anchor!=null&&cursor!=null&&cursor.CompletionLockHeld&&
            reservation.TryPublishSyntheticEye(anchor,cursor,join,fence,ticket);}
        internal bool TryPublishSyntheticEyeUnderParticipantLock(VivePracticeCursorAnchor anchor,VivePracticeTaskEventCursor cursor,
            VivePracticeSyntheticEyeJoin join,ViveDevelopmentParticipant.PracticeObservationFence fence,VivePracticeSyntheticTicket ticket)
        {
            return CursorPreTargetLocksHeld&&cursor!=null&&cursor.CompletionLockHeld&&Object.ReferenceEquals(anchor,activeCursorAnchor)&&
                target!=null&&currentTargetReceipt!=null&&receiptConsumed&&!cursorFailed&&!appendInFlight&&
                target.TryPublishSyntheticEyeUnderTargetLock(this,cursor,join,fence,ticket,currentTargetReceipt);
        }
        internal bool IsCurrentForCursorDrain(VivePracticeCursorAnchor anchor,ViveDevelopmentParticipant.PracticeTaskDrainFence fence,bool requireOutstanding)
        {return Thread.CurrentThread.ManagedThreadId==owner&&!active&&anchor!=null&&fence!=null&&reservation.IsOwnedCursorDrainCurrent(anchor,fence,requireOutstanding);}
        internal bool TryIssueRetryContinuation(VivePracticeCursorAnchor anchor,VivePracticeTaskEventCursor cursor,
            ViveDevelopmentParticipant.PracticeTaskDrainFence fence,out VivePracticeRetryContinuation continuation)
        {return reservation.TryIssueRetryContinuation(anchor,cursor,fence,out continuation);}
        internal bool TryClaimRetryContinuation(VivePracticeCursorAnchor anchor,VivePracticeTaskEventCursor cursor,
            VivePracticeRetryContinuation continuation,VivePracticeStartBinding.Ticket ticket,VivePracticeContextV3 nextContext,
            out VivePracticeConsentAuthority.StartReservation nextReservation)
        {return reservation.TryClaimRetryContinuation(anchor,cursor,continuation,ticket,nextContext,out nextReservation);}
        internal bool TryReserveRetryBegin(VivePracticeCursorAnchor anchor,VivePracticeTaskEventCursor cursor,
            VivePracticeRetryContinuation continuation,ViveDevelopmentParticipant.PracticeTaskDrainFence freshFence)
        {return reservation.TryReserveRetryBegin(anchor,cursor,continuation,freshFence);}
        // Called only within cursor -> reservation -> choices -> participant.
        // The cap is a historical candidate; revalidation at claim is required.
        internal bool IssueRetryUnderParticipantLock(VivePracticeCursorAnchor anchor,VivePracticeTaskEventCursor cursor,
            ViveDevelopmentParticipant.PracticeTaskDrainFence fence,out VivePracticeRetryContinuation continuation)
        {
            continuation=null;
            if(Thread.CurrentThread.ManagedThreadId!=owner||!CursorPreTargetLocksHeld||cursor==null||!cursor.CompletionLockHeld||
                retryContinuationIssued||retryContinuationConsumed||cursorFailed||appendInFlight||!receiptConsumed||
                !Object.ReferenceEquals(anchor,activeCursorAnchor)||!cursor.CanIssueRetryUnderIssuerLocks(this,anchor,fence)||
                target==null||currentTargetReceipt==null||!target.IsCurrentReceipt(currentTargetReceipt))return false;
            var issued=new VivePracticeRetryContinuation(this,cursor,anchor,cursor.RetryTerminalSnapshot,cursor.RetryTerminalStep,
                cursor.RetryTerminalFence,cursor.RetryTerminalRevision,cursor.RetryTerminalSequence,cursor.RetryTerminalTime,fence);
            retryContinuation=issued;retryContinuationIssued=true;continuation=issued;return true;
        }
        internal bool ReserveRetryBeginUnderParticipantLock(VivePracticeCursorAnchor anchor,VivePracticeTaskEventCursor cursor,
            VivePracticeRetryContinuation continuation,ViveDevelopmentParticipant.PracticeTaskDrainFence freshFence)
        {
            if(!VivePracticeRetryContinuation.RetryHandoffEnabled||Thread.CurrentThread.ManagedThreadId!=owner||!CursorPreTargetLocksHeld||cursor==null||!cursor.CompletionLockHeld||freshFence==null||
                !retryContinuationIssued||retryContinuationConsumed||retryBeginReserved||cursorFailed||appendInFlight||
                !Object.ReferenceEquals(retryContinuation,continuation)||!Object.ReferenceEquals(anchor,activeCursorAnchor)||
                !cursor.CanClaimRetryUnderIssuerLocks(this,continuation)||target==null||currentTargetReceipt==null||
                !target.IsCurrentReceipt(currentTargetReceipt))return false;
            retryBeginReserved=true;return true;
        }
        static bool ValidRetryContext(VivePracticeContextV3 previous,VivePracticeContextV3 next)
        {
            return previous!=null&&next!=null&&previous.PracticeAttempt==1&&next.PracticeAttempt==2&&
                next.EpochReason==VivePracticeEpochReasonV3.PracticeRetry&&next.RecoveryOrdinal==0&&
                next.PreviousEpochId==previous.RecordingEpochId&&next.RecordingEpochId!=previous.RecordingEpochId&&
                !next.ObservedPracticeStartLocalSeconds.HasValue&&next.SessionId==previous.SessionId&&
                next.ParticipantId==previous.ParticipantId&&next.StreamId==previous.StreamId&&next.DeviceId==previous.DeviceId&&
                next.ClockId==previous.ClockId&&next.MappingVersion==previous.MappingVersion;
        }
        // Participant and choice have already validated the exact outstanding
        // ticket, actual BeginPractice trace, and prepared the next reservation.
        // This marks only the issuer's local cap consumed, not any I/O permit.
        internal bool ConsumeRetryUnderParticipantLock(VivePracticeCursorAnchor anchor,VivePracticeTaskEventCursor cursor,
            VivePracticeRetryContinuation continuation,VivePracticeStartBinding.Ticket ticket,VivePracticeContextV3 nextContext)
        {
            if(!VivePracticeRetryContinuation.RetryHandoffEnabled||Thread.CurrentThread.ManagedThreadId!=owner||!CursorPreTargetLocksHeld||cursor==null||!cursor.CompletionLockHeld||
                !retryContinuationIssued||!retryBeginReserved||retryContinuationConsumed||cursorFailed||appendInFlight||!receiptConsumed||
                !Object.ReferenceEquals(retryContinuation,continuation)||!Object.ReferenceEquals(anchor,activeCursorAnchor)||
                !cursor.CanClaimRetryUnderIssuerLocks(this,continuation)||target==null||currentTargetReceipt==null||
                !target.IsCurrentReceipt(currentTargetReceipt)||!ValidRetryContext(context,nextContext)||ticket==null||ticket.Issuer==null||
                !ticket.Issuer.BelongsTo(participant,clock,choices)||ticket.ChoiceGeneration!=choiceGeneration||ticket.Fence==null||
                ticket.Batch==null||ticket.Batch.Attempt!=2||ticket.Batch.SessionId!=context.SessionId||ticket.Batch.ClockId!=context.ClockId||
                ticket.Batch.Revision<=continuation.IssueRevision||
                continuation.TerminalSequence==Int64.MaxValue||ticket.Batch.Events==null||ticket.Batch.Events.Count!=3||
                ticket.Batch.Events[0]==null||ticket.Batch.Events[0].Sequence!=continuation.TerminalSequence+1||
                ticket.Batch.Events[0].Time<continuation.TerminalTime)return false;
            retryContinuationConsumed=true;return true;
        }
        internal bool IsRetryConsumedUnderParticipantLock(VivePracticeRetryContinuation continuation)
        {
            return Thread.CurrentThread.ManagedThreadId==owner&&CursorPreTargetLocksHeld&&continuation!=null&&
                continuation.Cursor!=null&&continuation.Cursor.CompletionLockHeld&&retryContinuationIssued&&retryContinuationConsumed&&
                Object.ReferenceEquals(retryContinuation,continuation)&&Object.ReferenceEquals(continuation.Issuer,this)&&
                !cursorFailed&&!appendInFlight&&target!=null&&currentTargetReceipt!=null&&target.IsCurrentReceipt(currentTargetReceipt);
        }
        // This is only an in-memory identity check, not filesystem exclusivity.
        internal bool IsDistinctRetryTarget(VivePracticeRetryContinuation continuation,ViveExclusiveMemoryStore candidateStore,string candidateTargetId)
        {
            return VivePracticeRetryContinuation.RetryHandoffEnabled&&Thread.CurrentThread.ManagedThreadId==owner&&retryContinuationIssued&&retryContinuationConsumed&&
                Object.ReferenceEquals(retryContinuation,continuation)&&Object.ReferenceEquals(store,candidateStore)&&
                !String.IsNullOrWhiteSpace(candidateTargetId)&&!String.Equals(targetId,candidateTargetId,StringComparison.Ordinal)&&
                continuation.Cursor!=null&&!continuation.Cursor.Faulted&&!reservation.IsFaulted&&
                !cursorFailed&&!appendInFlight&&target!=null&&currentTargetReceipt!=null&&target.IsCurrentReceipt(currentTargetReceipt);
        }
        bool RetryTargetStillCurrent()
        {
            if(retryReservation==null)return true;
            var continuation=retryReservation.RetryContinuation;
            return continuation!=null&&continuation.Issuer!=null&&continuation.Issuer.IsDistinctRetryTarget(continuation,store,targetId);
        }
        internal bool IsCursorAnchorUnderParticipantLock(VivePracticeCursorAnchor anchor)
        {
            return reservation.CompletionLockHeld&&choices.StateLockHeld&&participant.ObservationFenceLockHeld&&receiptConsumed&&!cursorFailed&&!appendInFlight&&
                Object.ReferenceEquals(anchor,activeCursorAnchor)&&target!=null&&currentTargetReceipt!=null&&target.IsCurrentReceipt(currentTargetReceipt);
        }
        internal bool OwnsWriter(VivePracticeMultiWriter candidate){return Object.ReferenceEquals(writer,candidate);}
        internal bool CursorPreTargetLocksHeld {get{return reservation.CompletionLockHeld&&choices.StateLockHeld&&participant.ObservationFenceLockHeld;}}
        internal bool CursorCommitLocksHeld {get{return CursorPreTargetLocksHeld&&target!=null&&target.TargetLockHeld;}}
        internal bool RetryOldReservationLockHeld {get{return reservation.CompletionLockHeld;}}
        internal bool TryPublishRetryStart(VivePracticeCursorAnchor anchor,VivePracticeTaskEventCursor cursor,
            VivePracticeRetryContinuation continuation,VivePracticeStartReservation newReservation,VivePracticeOwnedMemoryStart newHost,
            VivePracticeConsentAuthority.StartReservation start,ViveDevelopmentParticipant.PracticeObservationFence fence)
        {return reservation.TryPassRetryPublication(anchor,cursor,continuation,newReservation,newHost,start,fence);}
        internal bool TryPublishRetryUnderParticipantLock(VivePracticeStartReservation newReservation,VivePracticeOwnedMemoryStart newHost,
            VivePracticeRetryContinuation continuation,VivePracticeConsentAuthority.StartReservation start,
            ViveDevelopmentParticipant.PracticeObservationFence fence)
        {
            if(!CursorPreTargetLocksHeld||continuation==null||continuation.Cursor==null||!continuation.Cursor.CompletionLockHeld||
                newReservation==null||!newReservation.CompletionLockHeld||newHost==null||target==null||currentTargetReceipt==null)return false;
            return target.TryPublishRetryUnderOldTargetLock(this,newReservation,newHost,continuation,start,fence,currentTargetReceipt);
        }
        internal bool RetryCommitLocksHeld(VivePracticeStartReservation newReservation,VivePracticeOwnedMemoryStart newHost,
            VivePracticeRetryContinuation continuation,VivePracticeConsentAuthority.StartReservation start)
        {
            return Thread.CurrentThread.ManagedThreadId==owner&&continuation!=null&&continuation.Cursor!=null&&continuation.Cursor.CompletionLockHeld&&
                !continuation.Cursor.Faulted&&CursorPreTargetLocksHeld&&!reservation.IsFaulted&&newReservation!=null&&newReservation.CompletionLockHeld&&
                !newReservation.IsFaulted&&newHost!=null&&start!=null&&Object.ReferenceEquals(newHost.reservation,newReservation)&&
                Object.ReferenceEquals(newHost.retryReservation,start)&&Object.ReferenceEquals(start.RetryContinuation,continuation)&&
                Object.ReferenceEquals(newHost.choices,choices)&&Object.ReferenceEquals(newHost.participant,participant)&&Object.ReferenceEquals(newHost.clock,clock)&&
                retryContinuationIssued&&retryContinuationConsumed&&Object.ReferenceEquals(retryContinuation,continuation)&&
                Object.ReferenceEquals(continuation.Issuer,this)&&!cursorFailed&&!appendInFlight&&target!=null&&target.TargetLockHeld&&
                currentTargetReceipt!=null&&target.IsCurrentReceipt(currentTargetReceipt)&&newHost.target!=null&&newHost.target.TargetLockHeld&&
                !Object.ReferenceEquals(target,newHost.target)&&Object.ReferenceEquals(store,newHost.store)&&
                !String.Equals(targetId,newHost.targetId,StringComparison.Ordinal)&&newHost.pendingReceipt!=null&&
                Object.ReferenceEquals(newHost.pendingReceipt.Participant,participant)&&Object.ReferenceEquals(newHost.pendingReceipt.Choices,choices)&&
                Object.ReferenceEquals(newHost.pendingReceipt.Clock,clock)&&newHost.pendingReceipt.ChoiceGeneration==choiceGeneration&&
                newHost.target.IsCurrentReceipt(newHost.pendingReceipt.TargetReceipt)&&newHost.active&&!newHost.receiptPublished;
        }
        internal bool TryPublishRetryWithOldTargetLock(VivePracticeStartReservation newReservation,VivePracticeOwnedMemoryStart newHost,
            VivePracticeRetryContinuation continuation,VivePracticeConsentAuthority.StartReservation start,
            ViveDevelopmentParticipant.PracticeObservationFence fence)
        {
            if(target==null||!target.TargetLockHeld||newHost==null||newHost.target==null)return false;
            return newHost.target.TryPublishRetryUnderNewTargetLock(this,newReservation,newHost,continuation,start,fence);
        }
        internal bool CommitRetryPublicationUnderTargetLocks(VivePracticeOwnedMemoryStart oldHost,VivePracticeStartReservation newReservation,
            VivePracticeRetryContinuation continuation,VivePracticeConsentAuthority.StartReservation start,
            ViveDevelopmentParticipant.PracticeObservationFence fence)
        {
            if(oldHost==null||!oldHost.RetryCommitLocksHeld(newReservation,this,continuation,start)||fence==null||
                !Object.ReferenceEquals(pendingReceipt.Issuer,this)||!choices.CanCommitRetryStartUnderLocks(start,newReservation,this,continuation))return false;
            choices.CommitRetryStartUnderLocks(start,newReservation,this,continuation);
            receiptFence=fence;currentTargetReceipt=pendingReceipt.TargetReceipt;receiptPublished=true;return true;
        }
        // Do not hold issuer locks before asking cursor for its lock.
        internal bool IsPendingLease(VivePracticeTaskEventCursor.CursorAppendLease lease)
        {return lease!=null&&lease.Cursor!=null&&lease.Cursor.IsPendingLeaseForIssuer(this,lease);}
        internal bool TryBeginCursorAppend(VivePracticeCursorAnchor anchor,VivePracticeTaskEventCursor cursor,
            VivePracticeTaskEventCursor.CursorAppendLease lease,VivePracticeMultiWriter.Prepared prepared,byte[] digest,int length,
            out ViveExclusiveMemoryTarget.Receipt oldReceipt)
        {return reservation.TryBeginCursorAppend(anchor,cursor,lease,prepared,digest,length,out oldReceipt);}
        internal bool CanContinueCursorAppend(VivePracticeCursorAnchor anchor,VivePracticeTaskEventCursor cursor,VivePracticeTaskEventCursor.CursorAppendLease lease)
        {return reservation.CanContinueCursorAppend(anchor,cursor,lease);}
        internal bool TryFinalizeCursorAppend(VivePracticeCursorAnchor anchor,VivePracticeTaskEventCursor cursor,
            VivePracticeTaskEventCursor.CursorAppendLease lease,ViveExclusiveMemoryTarget.Receipt receipt,ViveDevelopmentParticipant.PracticeObservationFence fence)
        {return reservation.TryFinalizeCursorAppend(anchor,cursor,lease,receipt,fence);}
        internal bool TryFinalizeCursorDrain(VivePracticeCursorAnchor anchor,VivePracticeTaskEventCursor cursor,
            VivePracticeTaskEventCursor.CursorAppendLease lease,ViveExclusiveMemoryTarget.Receipt receipt,ViveDevelopmentParticipant.PracticeTaskDrainFence fence)
        {return reservation.TryFinalizeCursorDrain(anchor,cursor,lease,receipt,fence);}
        internal bool BeginCursorUnderParticipantLock(VivePracticeCursorAnchor anchor,VivePracticeTaskEventCursor cursor,
            VivePracticeTaskEventCursor.CursorAppendLease lease,VivePracticeMultiWriter.Prepared prepared,byte[] digest,int length,
            out ViveExclusiveMemoryTarget.Receipt oldReceipt)
        {
            oldReceipt=null;
            if(!CursorPreTargetLocksHeld||cursor==null||!cursor.CompletionLockHeld||cursorFailed||appendInFlight||!Object.ReferenceEquals(anchor,activeCursorAnchor)||
                !Object.ReferenceEquals(cursor,appendCursor)||!Object.ReferenceEquals(lease,appendLease)||!Object.ReferenceEquals(prepared,appendPrepared)||
                !Object.ReferenceEquals(digest,appendDigest)||length!=appendLength||target==null||currentTargetReceipt==null||writer==null||
                !writer.IsOutstandingPrepared(this,lease,prepared)||!prepared.MatchesDigest(digest)||prepared.DeltaLength!=length||
                prepared.ExpectedPreviousLength!=currentTargetReceipt.TotalLength||!target.IsCurrentReceipt(currentTargetReceipt))return false;
            appendOldReceipt=currentTargetReceipt;oldReceipt=currentTargetReceipt;appendInFlight=true;return true;
        }
        internal bool ContinueCursorUnderParticipantLock(VivePracticeCursorAnchor anchor,VivePracticeTaskEventCursor cursor,VivePracticeTaskEventCursor.CursorAppendLease lease)
        {
            return CursorPreTargetLocksHeld&&cursor!=null&&cursor.CompletionLockHeld&&!cursorFailed&&appendInFlight&&
                Object.ReferenceEquals(anchor,activeCursorAnchor)&&Object.ReferenceEquals(cursor,appendCursor)&&Object.ReferenceEquals(lease,appendLease)&&
                target!=null&&appendOldReceipt!=null&&target.IsCurrentReceipt(appendOldReceipt);
        }
        internal bool FinalizeCursorUnderParticipantLock(VivePracticeCursorAnchor anchor,VivePracticeTaskEventCursor cursor,
            VivePracticeTaskEventCursor.CursorAppendLease lease,ViveExclusiveMemoryTarget.Receipt receipt)
        {
            return CursorPreTargetLocksHeld&&cursor!=null&&cursor.CompletionLockHeld&&!cursorFailed&&appendInFlight&&
                Object.ReferenceEquals(anchor,activeCursorAnchor)&&Object.ReferenceEquals(cursor,appendCursor)&&Object.ReferenceEquals(lease,appendLease)&&
                target!=null&&target.TryFinalizeCursorReceipt(receipt,this,cursor,lease);
        }
        internal bool MayCommitPrepared(VivePracticeMultiWriter candidate,VivePracticeTaskEventCursor.CursorAppendLease lease,
            VivePracticeMultiWriter.Prepared prepared,ViveExclusiveMemoryTarget.Receipt receipt)
        {
            return CursorCommitLocksHeld&&appendCursor!=null&&appendCursor.CompletionLockHeld&&!cursorFailed&&appendInFlight&&Object.ReferenceEquals(candidate,writer)&&Object.ReferenceEquals(lease,appendLease)&&
                Object.ReferenceEquals(prepared,appendPrepared)&&receipt!=null&&target.IsCurrentReceipt(receipt)&&appendOldReceipt!=null&&appendOldReceipt.Ordinal<Int64.MaxValue&&
                receipt.Ordinal==appendOldReceipt.Ordinal+1&&prepared.ExpectedPreviousLength==appendOldReceipt.TotalLength&&
                prepared.NewTotalLength==receipt.TotalLength&&receipt.TotalLength==appendOldReceipt.TotalLength+appendLength&&receipt.MatchesPayloadDigest(appendDigest);
        }
        internal bool CommitCursorUnderTargetLock(ViveExclusiveMemoryTarget.Receipt receipt,VivePracticeTaskEventCursor cursor,
            VivePracticeTaskEventCursor.CursorAppendLease lease)
        {
            if(!MayCommitPrepared(writer,lease,appendPrepared,receipt)||!cursor.CanCommitUnderIssuerLocks(this,lease)||
                !writer.CanCommitPrepared(this,lease,appendPrepared,receipt))return false;
            if(!writer.CommitPreparedAndCursor(this,lease,appendPrepared,receipt,cursor))return false;
            currentTargetReceipt=receipt;appendInFlight=false;appendCursor=null;appendLease=null;appendPrepared=null;appendDigest=null;appendOldReceipt=null;appendLength=0;
            return true;
        }
        // Complete a previously staged genuine task transaction against this
        // same sealed fake target. A failure never rewinds/retries either side.
        internal bool TryAppendPending(VivePracticeTaskEventCursor cursor,VivePracticeTaskEventTransaction transaction,Func<int,ViveFakeWritePlan> planFactory=null)
        {
            if(Thread.CurrentThread.ManagedThreadId!=owner){reservation.Invalidate();return false;}
            if(active||cursorFailed||cursor==null||transaction==null||writer==null||target==null)return false;
            VivePracticeTaskEventCursor.CursorAppendLease lease=null;VivePracticeMultiWriter.Prepared prepared=null;bool appended=false,success=false;
            try
            {
                if(!cursor.TryBeginPendingAppend(transaction,out lease)||lease==null)return false;
                if(!writer.TryPrepareTaskTransaction(this,lease,out prepared)||prepared==null)return false;
                byte[] bytes=prepared.CopyDelta(),digest=prepared.CopyDeltaDigest();
                var plan=planFactory==null?new ViveFakeWritePlan(bytes.Length,false,null):planFactory(bytes.Length);
                if(plan==null)return false;
                appendCursor=cursor;appendLease=lease;appendPrepared=prepared;appendDigest=digest;appendLength=bytes.Length;
                ViveExclusiveMemoryTarget.Receipt oldReceipt;
                if(!cursor.TryAuthorizePendingAppend(lease,prepared,digest,bytes.Length,out oldReceipt)||oldReceipt==null||!cursor.TryContinuePendingAppend(lease))return false;
                ViveExclusiveMemoryTarget.Receipt next;appended=true;
                if(target.AppendIfCurrent(oldReceipt,bytes,plan,out next)!=ViveFakeWriteResult.Accepted||next==null)return false;
                if(!cursor.TryCompletePendingAppend(lease,next))return false;
                success=true;return true;
            }
            catch(Exception error) when(!(error is OutOfMemoryException||error is StackOverflowException||error is AccessViolationException)){return false;}
            finally
            {
                if(!success)
                {
                    cursorFailed=true;if(appended)target.MarkOutcomeUncertain();else if(target!=null)target.Withdraw();
                    if(prepared!=null)writer.AbandonPrepared(this,lease,prepared);
                    if(lease!=null)cursor.FaultPendingAppend(lease);
                }
            }
        }
        internal bool AcceptSynthetic(VivePracticeStartBatch batch,VivePracticeStartReservation.OperationGuard guard)
        {
            if(!active||Thread.CurrentThread.ManagedThreadId!=owner||sinkBusy||guard==null||!guard.BelongsTo(reservation))
            {reservation.Invalidate();return false;}
            sinkBusy=true;
            try
            {
            if(!guard.CanContinue()||!RetryTargetStillCurrent())return false;
            // Construct and validate the complete first transaction entirely in
            // private memory before reserving the fake target identifier.
            var envelope=VivePracticeStartEnvelopeCodec.Encode(context,batch,VivePracticeStartEnvelopeCodec.MaximumEnvelopeBytes);
            var candidate=new VivePracticeMultiWriter(envelope,maximumBytes,maximumBatches);writer=candidate;
            if(!candidate.BindIssuer(this))return false;byte[] bytes=candidate.Snapshot();
            if(!guard.CanContinue()||!RetryTargetStillCurrent())return false;
            var plan=testPlan==null?new ViveFakeWritePlan(bytes.Length,false,null):testPlan(bytes.Length);
            if(plan==null||!guard.CanContinue()||!RetryTargetStillCurrent())return false;
            ViveExclusiveMemoryTarget created;
            if(store.TryCreateNew(targetId,maximumBytes,out created)!=ViveFakeCreateResult.Created)return false;
            target=created;
            if(!guard.CanContinue()||!RetryTargetStillCurrent()){target.Withdraw();return false;}
            appendAttempted=true;
            ViveExclusiveMemoryTarget.Receipt issued;
            if(target.AppendWithReceipt(bytes,plan,out issued)!=ViveFakeWriteResult.Accepted||issued==null)return false;
            if(!guard.CanContinue()||!RetryTargetStillCurrent()){target.MarkOutcomeUncertain();return false;}
            writer=candidate;pendingReceipt=new VivePracticeOwnedStartReceipt(this,participant,choices,clock,issued,batch,choiceGeneration);return true;
            }
            finally{sinkBusy=false;}
        }
    }
}
