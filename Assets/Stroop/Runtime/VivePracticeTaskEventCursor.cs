using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using AvatarStroop.Core;

namespace AvatarStroop.Runtime
{
    // Internal, in-memory origin for one owner-controlled simulation cursor.
    // Only the owning start host is allowed to hand this object to its cursor;
    // matching values or a caller-constructed copy are not proof of origin.
    // This is never a file, device, human-consent or research-admission proof.
    internal sealed class VivePracticeCursorAnchor
    {
        internal readonly VivePracticeOwnedMemoryStart Issuer;
        internal readonly ViveDevelopmentParticipant Participant;
        internal readonly IClock Clock;
        internal readonly VivePracticeConsentAuthority Choices;
        internal readonly long ChoiceGeneration,StartSequence,StartRevision;
        internal readonly double StartTime;
        internal readonly ExperimentIncrementalSnapshot StartSnapshot;
        internal readonly IReadOnlyList<ViveSyntheticStepTrace> StartTrace;
        internal readonly VivePracticeContextV3 Context;
        internal VivePracticeCursorAnchor(VivePracticeOwnedMemoryStart issuer,ViveDevelopmentParticipant participant,IClock clock,
            VivePracticeConsentAuthority choices,long choiceGeneration,long startSequence,long startRevision,double startTime,
            ExperimentIncrementalSnapshot startSnapshot,IReadOnlyList<ViveSyntheticStepTrace> startTrace,VivePracticeContextV3 context)
        {
            Issuer=issuer;Participant=participant;Clock=clock;Choices=choices;ChoiceGeneration=choiceGeneration;
            StartSequence=startSequence;StartRevision=startRevision;StartTime=startTime;StartSnapshot=startSnapshot;
            StartTrace=startTrace;Context=context;
        }
    }

    internal enum VivePracticeTaskCursorResult { NoEvents,Staged,Pending,Faulted }

    // A historical, immutable event copy only. It cannot authorize a write or
    // establish that these records were delivered or persisted.
    internal sealed class VivePracticeTaskEventTransaction
    {
        internal readonly long Revision;
        internal readonly IReadOnlyList<JournalEvent> Events;
        internal bool CanWrite {get{return false;}}
        internal VivePracticeTaskEventTransaction(long revision,IReadOnlyList<JournalEvent> events)
        {Revision=revision;Events=new ReadOnlyCollection<JournalEvent>(new List<JournalEvent>(events));}
    }

    // Opt-in simulation only. The sole constructor consumes an issuer-owned
    // start receipt: an event list, copied bytes, or caller-created anchor is
    // never accepted as authority. This first version stages at most one task
    // transaction and intentionally has no commit/advance API until a later
    // issuer-owned target-commit receipt is available.
    internal sealed class VivePracticeTaskEventCursor
    {
        // A single in-flight append attempt, not an I/O or human-consent
        // permission. The issuer verifies object identity, never field values
        // supplied by the caller. Its fence remains outstanding until finalize.
        internal sealed class CursorAppendLease
        {
            internal readonly VivePracticeTaskEventCursor Cursor;
            internal readonly VivePracticeCursorAnchor Anchor;
            internal readonly VivePracticeTaskEventTransaction Transaction;
            internal readonly ViveDevelopmentParticipant.PracticeObservationFence Fence;
            internal readonly ViveDevelopmentParticipant.PracticeTaskDrainFence DrainFence;
            internal CursorAppendLease(VivePracticeTaskEventCursor cursor,VivePracticeCursorAnchor anchor,
                VivePracticeTaskEventTransaction transaction,ViveDevelopmentParticipant.PracticeObservationFence fence)
            {Cursor=cursor;Anchor=anchor;Transaction=transaction;Fence=fence;}
            internal CursorAppendLease(VivePracticeTaskEventCursor cursor,VivePracticeCursorAnchor anchor,
                VivePracticeTaskEventTransaction transaction,ViveDevelopmentParticipant.PracticeTaskDrainFence fence)
            {Cursor=cursor;Anchor=anchor;Transaction=transaction;DrainFence=fence;}
        }
        readonly VivePracticeOwnedMemoryStart issuer;
        readonly VivePracticeCursorAnchor anchor;
        readonly int owner,maximumTraceSteps,maximumTransactionEvents;
        readonly object completionGate=new object();
        int busy,terminal,traceCount,lastStepIndex;
        bool boundaryCommitted;
        internal bool BoundaryCommitted {get{return boundaryCommitted;}}
        long lastRevision,lastSequence;
        double lastEventTime,lastNow;
        ExperimentIncrementalSnapshot checkpoint;
        ViveSyntheticStepTrace lastStep;
        ViveDevelopmentParticipant.PracticeObservationFence pendingFence;
        ViveDevelopmentParticipant.PracticeTaskDrainFence pendingDrainFence;
        ViveDevelopmentParticipant.PracticeTaskDrainFence committedTerminalFence;
        ViveDevelopmentParticipant.PracticeTaskDrainFence retryIssueFence;
        VivePracticeTaskEventTransaction pending;
        CursorAppendLease appendLease;bool appendAuthorized;
        int pendingEndCount;long pendingEndRevision,pendingLastSequence;double pendingLastEventTime;
        ViveSyntheticStepTrace pendingEndStep;ExperimentIncrementalSnapshot pendingEndSnapshot;
        internal bool CanWrite {get{return false;}}
        internal bool CanClaimDurability {get{return false;}}
        internal bool Faulted {get{return Interlocked.CompareExchange(ref terminal,0,0)!=0;}}
        internal bool CompletionLockHeld {get{return Monitor.IsEntered(completionGate);}}
        internal string LastIssue {get;private set;}
        // Synthetic-only comparison against the last *committed* task head. It
        // cannot issue a recording permit or publish/write a frame. The caller
        // still owns and must consume/abandon the exact participant fence.
        internal bool MatchesSyntheticEyeCheckpoint(VivePracticeOwnedMemoryStart expectedIssuer,
            ViveDevelopmentParticipant expectedParticipant,IClock expectedClock,VivePracticeConsentAuthority expectedChoices,
            VivePracticeContextV3 expectedContext,ViveDevelopmentParticipant.PracticeObservationFence fence,
            ExperimentIncrementalSnapshot expectedSnapshot,long expectedRevision,int expectedTraceCount)
        {
            lock(completionGate)
            {
                if(Thread.CurrentThread.ManagedThreadId!=owner||Faulted||busy!=0||pending!=null||appendLease!=null||boundaryCommitted||
                    fence==null||expectedTraceCount<1||traceCount!=expectedTraceCount||lastRevision!=expectedRevision||
                    !Object.ReferenceEquals(issuer,expectedIssuer)||!Object.ReferenceEquals(anchor.Participant,expectedParticipant)||
                    !Object.ReferenceEquals(anchor.Clock,expectedClock)||!Object.ReferenceEquals(anchor.Choices,expectedChoices)||
                    !Object.ReferenceEquals(anchor.Context,expectedContext)||!Object.ReferenceEquals(checkpoint,expectedSnapshot)||
                    checkpoint==null||(long)checkpoint.Scalars.EventCount!=lastSequence+1||
                    !Object.ReferenceEquals(fence.Snapshot,checkpoint)||fence.Revision!=lastRevision||
                    fence.StepTrace==null||fence.StepTrace.Count!=traceCount||fence.MutationStepsObserved!=traceCount||
                    !Object.ReferenceEquals(fence.StepTrace[traceCount-1],lastStep))return false;
                return issuer.IsCurrentForCursor(anchor,fence);
            }
        }
        internal bool TryPublishSyntheticEye(VivePracticeSyntheticEyeJoin join,VivePracticeOwnedMemoryStart expectedIssuer,
            ViveDevelopmentParticipant expectedParticipant,IClock expectedClock,VivePracticeConsentAuthority expectedChoices,
            VivePracticeContextV3 expectedContext,ViveDevelopmentParticipant.PracticeObservationFence fence,VivePracticeSyntheticTicket ticket)
        {
            lock(completionGate)
            {
                if(join==null||ticket==null||ticket.Consumed!=1||
                    !MatchesSyntheticEyeCheckpoint(expectedIssuer,expectedParticipant,expectedClock,expectedChoices,expectedContext,
                        fence,ticket.Snapshot,ticket.Revision,ticket.TraceCount))return false;
                return issuer.TryPublishSyntheticEye(anchor,this,join,fence,ticket);
            }
        }
        internal VivePracticeTaskEventCursor(VivePracticeOwnedMemoryStart issuer,VivePracticeOwnedStartReceipt receipt,
            int maximumTraceSteps,int maximumTransactionEvents)
        {
            if(issuer==null||receipt==null)throw new ArgumentNullException("issuer/receipt");
            if(maximumTraceSteps<1||maximumTraceSteps>4096||maximumTransactionEvents<1||maximumTransactionEvents>256)
                throw new ArgumentOutOfRangeException("bounded limits required");
            VivePracticeCursorAnchor issued;
            if(!issuer.TryConsumeForCursor(receipt,out issued)||issued==null||!Object.ReferenceEquals(issued.Issuer,issuer))
                throw new InvalidOperationException("An unconsumed, current issuer-owned start receipt is required");
            // Any failure after consumption is terminal for that receipt.
            if(issued.Participant==null||issued.Clock==null||issued.Choices==null||issued.Context==null||issued.StartSnapshot==null||
                issued.StartTrace==null||issued.StartTrace.Count<1||issued.StartTrace.Count>maximumTraceSteps||
                issued.StartSequence<0||issued.StartSequence>=Int32.MaxValue||issued.StartRevision<1||
                !Finite(issued.StartTime)||issued.StartSnapshot.Scalars.EventCount!=issued.StartSequence+1||
                issued.StartTrace[issued.StartTrace.Count-1]==null||issued.StartTrace[issued.StartTrace.Count-1].Revision!=issued.StartRevision||
                !Object.ReferenceEquals(issued.StartTrace[issued.StartTrace.Count-1].After,issued.StartSnapshot)||!ValidState(issued.StartSnapshot,issued))
                throw new InvalidOperationException("The issued start anchor is incomplete");
            this.issuer=issuer;anchor=issued;owner=Thread.CurrentThread.ManagedThreadId;
            this.maximumTraceSteps=maximumTraceSteps;this.maximumTransactionEvents=maximumTransactionEvents;
            traceCount=issued.StartTrace.Count;lastStep=issued.StartTrace[traceCount-1];lastStepIndex=lastStep.Index;
            lastRevision=issued.StartRevision;lastSequence=issued.StartSequence;lastEventTime=lastNow=issued.StartTime;checkpoint=issued.StartSnapshot;
        }
        static bool Finite(double v){return !Double.IsNaN(v)&&!Double.IsInfinity(v)&&v>=0;}
        static bool ValidState(ExperimentIncrementalSnapshot state,VivePracticeCursorAnchor anchor)
        {
            if(state==null||state.Task==null||state.Task.SessionId!=anchor.Context.SessionId||state.Task.BlockIndex!=-1||
                state.Task.Condition.HasValue||state.Selection.HasValue||state.Scalars.PracticeAttempt!=anchor.Context.PracticeAttempt||
                (state.Task.Phase!=Phase.Practice&&state.Task.Phase!=Phase.PracticeCorrection))return false;
            if(!state.CurrentTrial.HasValue)return true;
            var trial=state.CurrentTrial.Value;
            return trial.IsPractice&&trial.SessionId==anchor.Context.SessionId&&trial.BlockIndex==-1&&!trial.Condition.HasValue&&
                trial.PracticeAttempt==anchor.Context.PracticeAttempt;
        }
        VivePracticeTaskCursorResult Fail(string reason)
        {lock(completionGate){Interlocked.Exchange(ref terminal,1);if(LastIssue==null)LastIssue=reason;return VivePracticeTaskCursorResult.Faulted;}}
        bool Enter()
        {
            lock(completionGate)
            {
                if(Thread.CurrentThread.ManagedThreadId!=owner){Fail("owner_thread_violation");return false;}
                if(busy!=0||appendLease!=null){Fail("reentrant_or_append_in_flight");return false;}
                if(Faulted)return false;busy=1;return true;
            }
        }
        // No file, sink or callback is accepted here. The caller must not
        // interpret Staged as a successful target append. While a transaction
        // is pending this cursor cannot advance or issue a second transaction.
        internal VivePracticeTaskCursorResult TryStage(out VivePracticeTaskEventTransaction transaction)
        {
            transaction=null;if(!Enter())return VivePracticeTaskCursorResult.Faulted;
            ViveDevelopmentParticipant.PracticeObservationFence fence=null;
            try
            {
                if(boundaryCommitted)return VivePracticeTaskCursorResult.NoEvents;
                if(!issuer.IsCurrentForCursor(anchor))return Fail("issuer_or_target_unavailable");
                if(pending!=null)
                {
                    lock(completionGate)
                    {
                        if(Faulted||!(pendingDrainFence!=null?issuer.IsCurrentForCursorDrain(anchor,pendingDrainFence,false):issuer.IsCurrentForCursor(anchor,pendingFence)))
                            return Fail("pending_context_changed");
                        return VivePracticeTaskCursorResult.Pending;
                    }
                }
                if(!anchor.Participant.TryAcquirePracticeObservationFence(anchor.Clock,out fence)||fence==null)
                    return TryStageTerminal(out transaction);
                // PracticeRetry is a valid pre-practice eye-fence phase, but
                // for this already-started cursor it is an epoch boundary.
                // Release the ordinary fence before requesting task-only proof.
                if(fence.Snapshot!=null&&fence.Snapshot.Task!=null&&fence.Snapshot.Task.Phase==Phase.PracticeRetry)
                {
                    anchor.Participant.AbandonPracticeObservationFence(fence);fence=null;
                    return TryStageTerminal(out transaction);
                }
                var trace=fence.StepTrace;var current=fence.Snapshot;
                if(trace==null||trace.Count<traceCount||trace.Count>maximumTraceSteps||fence.MutationStepsObserved!=trace.Count||
                    traceCount<1||!Object.ReferenceEquals(trace[traceCount-1],lastStep)||!ValidState(current,anchor))
                    return Fail("trace_missing_or_context_closed");
                int index=traceCount,selectedEnd=-1,selectedCount=0;long selectedRevision=-1;
                var previous=checkpoint;long revision=lastRevision;int stepIndex=lastStepIndex;
                while(index<trace.Count)
                {
                    int groupStart=index,groupEventCount=0;long groupRevision=trace[index].Revision;
                    while(index<trace.Count&&trace[index].Revision==groupRevision)
                    {
                        var step=trace[index];
                        if(step==null||step.Before==null||step.After==null||!Object.ReferenceEquals(previous,step.Before)||
                            !((step.Revision==revision&&step.Index==stepIndex+1)||
                              (revision<Int64.MaxValue&&step.Revision==revision+1&&step.Index==0))||
                            !ValidState(step.Before,anchor)||!ValidState(step.After,anchor))return Fail("trace_gap_or_practice_interrupted");
                        int before=step.Before.Scalars.EventCount,after=step.After.Scalars.EventCount;
                        if(before<0||after<before||after-before>1)return Fail("event_position_invalid");
                        groupEventCount+=after-before;previous=step.After;revision=step.Revision;stepIndex=step.Index;index++;
                    }
                    if(index==groupStart)return Fail("empty_revision");
                    if(groupEventCount>maximumTransactionEvents)return Fail("transaction_event_limit");
                    if(selectedEnd<0&&groupEventCount>0){selectedEnd=index;selectedCount=groupEventCount;selectedRevision=groupRevision;}
                }
                if(!Object.ReferenceEquals(previous,current))return Fail("trace_head_mismatch");
                IReadOnlyList<JournalEvent> events=null;double now=lastNow;
                if(selectedEnd>=0)
                {
                    events=anchor.Participant.ReadEventsAfter(lastSequence,selectedCount);
                    if(events==null||events.Count!=selectedCount)return Fail("event_prefix_missing");
                    string clockId=anchor.Clock.ClockId;now=anchor.Clock.Now;
                    if(clockId!=anchor.Context.ClockId||!Finite(now)||now<lastNow)return Fail("clock_invalid_or_rollback");
                    double prior=lastEventTime;long sequence=lastSequence;
                    foreach(var item in events)
                    {
                        if(item==null||sequence==Int64.MaxValue||item.Sequence!=sequence+1||item.SessionId!=anchor.Context.SessionId||
                            item.ClockId!=anchor.Context.ClockId||item.BlockIndex!=-1||item.Segment!=anchor.StartSnapshot.Task.Segment||
                            !Finite(item.Time)||item.Time<prior||item.Time>now||item.Kind=="practice_started")return Fail("event_context_or_order_invalid");
                        sequence=item.Sequence;prior=item.Time;
                    }
                    if(trace[selectedEnd-1].After.Scalars.EventCount!=sequence+1)return Fail("event_end_position_mismatch");
                }
                var completedFence=fence;bool valid=anchor.Participant.TryConsumePracticeObservationFence(fence);fence=null;
                lock(completionGate)
                {
                    if(!valid||Faulted||!issuer.IsCurrentForCursor(anchor,completedFence))return Fail("context_changed_during_read");
                    if(selectedEnd<0)
                    {
                        // State-only transactions need no target commit; no event
                        // position advances, and the verified head becomes the anchor.
                        if(trace.Count>traceCount){lastStep=trace[trace.Count-1];traceCount=trace.Count;checkpoint=current;lastRevision=lastStep.Revision;lastStepIndex=lastStep.Index;}
                        return VivePracticeTaskCursorResult.NoEvents;
                    }
                    lastNow=now;
                    pendingFence=completedFence;pendingEndCount=selectedEnd;pendingEndStep=trace[selectedEnd-1];
                    pendingEndSnapshot=pendingEndStep.After;pendingEndRevision=selectedRevision;
                    pendingLastSequence=events[events.Count-1].Sequence;pendingLastEventTime=events[events.Count-1].Time;
                    pending=new VivePracticeTaskEventTransaction(selectedRevision,events);transaction=pending;
                    return VivePracticeTaskCursorResult.Staged;
                }
            }
            catch(Exception){return Fail("task_cursor_exception");}
            finally{if(fence!=null)anchor.Participant.AbandonPracticeObservationFence(fence);lock(completionGate)busy=0;}
        }
        // Pins a new current fence to this exact pending object. The caller may
        // only be the owning issuer; no caller-provided success value is used.
        internal bool TryBeginPendingAppend(VivePracticeTaskEventTransaction transaction,out CursorAppendLease lease)
        {
            lease=null;
            lock(completionGate)
            {
                if(Thread.CurrentThread.ManagedThreadId!=owner||busy!=0||appendLease!=null||Faulted||
                    pending==null||!Object.ReferenceEquals(transaction,pending)||(pendingFence==null&&pendingDrainFence==null))
                {Fail("append_begin_invalid");return false;}
                if(pendingDrainFence!=null)return TryBeginPendingDrain(transaction,out lease);
                if(!issuer.IsCurrentForCursor(anchor,pendingFence)){Fail("pending_context_changed");return false;}
                ViveDevelopmentParticipant.PracticeObservationFence fence=null;
                try
                {
                    if(!anchor.Participant.TryAcquirePracticeObservationFence(anchor.Clock,out fence)||fence==null||
                        !Object.ReferenceEquals(fence.Snapshot,pendingFence.Snapshot)||!issuer.IsCurrentForCursor(anchor,fence))
                    {Fail("append_fence_unavailable");return false;}
                    var issued=new CursorAppendLease(this,anchor,pending,fence);appendLease=issued;lease=issued;fence=null;return true;
                }
                catch(Exception){Fail("append_begin_exception");return false;}
                finally{if(fence!=null)anchor.Participant.AbandonPracticeObservationFence(fence);}
            }
        }
        // Pure pre-append validation. The issuer must call without holding its
        // own completion lock, preserving cursor→issuer lock order.
        VivePracticeTaskCursorResult TryStageTerminal(out VivePracticeTaskEventTransaction transaction)
        {
            transaction=null;ViveDevelopmentParticipant.PracticeTaskDrainFence fence=null;
            try
            {
                if(!anchor.Participant.TryAcquirePracticeTaskDrainFence(anchor,anchor.Clock,out fence)||fence==null)
                    return Fail("current_task_terminal_fence_unavailable");
                var trace=fence.StepTrace;var now=anchor.Clock.Now;
                IReadOnlyList<JournalEvent> events;
                if(anchor.Clock.ClockId!=anchor.Context.ClockId||!Finite(now)||now<lastNow||trace==null||
                    trace.Count>maximumTraceSteps||!VivePracticeTerminalValidator.TryValidate(anchor,fence,checkpoint,lastStep,
                        traceCount,lastRevision,lastSequence,lastEventTime,now,out events)||events==null||
                    events.Count<1||events.Count>maximumTransactionEvents)
                    return Fail("terminal_transaction_invalid_or_incomplete");
                var completedFence=fence;bool valid=anchor.Participant.TryConsumePracticeTaskDrainFence(anchor,fence);fence=null;
                lock(completionGate)
                {
                    if(!valid||Faulted||!issuer.IsCurrentForCursorDrain(anchor,completedFence,false))return Fail("terminal_context_changed_during_read");
                    lastNow=now;pendingDrainFence=completedFence;pendingEndCount=trace.Count;pendingEndStep=trace[trace.Count-1];
                    pendingEndSnapshot=pendingEndStep.After;pendingEndRevision=pendingEndStep.Revision;
                    pendingLastSequence=events[events.Count-1].Sequence;pendingLastEventTime=events[events.Count-1].Time;
                    pending=new VivePracticeTaskEventTransaction(pendingEndRevision,events);transaction=pending;
                    return VivePracticeTaskCursorResult.Staged;
                }
            }
            catch(Exception){return Fail("terminal_cursor_exception");}
            finally{if(fence!=null)anchor.Participant.AbandonPracticeTaskDrainFence(fence);}
        }
        bool TryBeginPendingDrain(VivePracticeTaskEventTransaction transaction,out CursorAppendLease lease)
        {
            lease=null;
            if(!issuer.IsCurrentForCursorDrain(anchor,pendingDrainFence,false)){Fail("pending_terminal_context_changed");return false;}
            ViveDevelopmentParticipant.PracticeTaskDrainFence fence=null;
            try
            {
                if(!anchor.Participant.TryAcquirePracticeTaskDrainFence(anchor,anchor.Clock,out fence)||fence==null||
                    !Object.ReferenceEquals(fence.Snapshot,pendingDrainFence.Snapshot)||fence.Revision!=pendingDrainFence.Revision||
                    !issuer.IsCurrentForCursorDrain(anchor,fence,true))
                {Fail("append_terminal_fence_unavailable");return false;}
                var issued=new CursorAppendLease(this,anchor,transaction,fence);appendLease=issued;lease=issued;fence=null;return true;
            }
            catch(Exception){Fail("append_terminal_fence_exception");return false;}
            finally{if(fence!=null)anchor.Participant.AbandonPracticeTaskDrainFence(fence);}
        }
        internal bool IsPendingLeaseForIssuer(VivePracticeOwnedMemoryStart candidate,CursorAppendLease lease)
        {
            lock(completionGate)
            {
                if(Thread.CurrentThread.ManagedThreadId!=owner||Faulted||!Object.ReferenceEquals(candidate,issuer)||
                    lease==null||!Object.ReferenceEquals(lease,appendLease)||!Object.ReferenceEquals(lease.Cursor,this)||
                    !Object.ReferenceEquals(lease.Anchor,anchor)||!Object.ReferenceEquals(lease.Transaction,pending)||
                    !(lease.DrainFence!=null?issuer.IsCurrentForCursorDrain(anchor,lease.DrainFence,true):issuer.IsCurrentForCursor(anchor,lease.Fence)))
                {Fail("pending_lease_invalid");return false;}
                return true;
            }
        }
        // Establish the issuer's single in-flight target reservation while
        // preserving cursor→issuer lock order. This never invokes storage.
        internal bool TryAuthorizePendingAppend(CursorAppendLease lease,VivePracticeMultiWriter.Prepared prepared,
            byte[] digest,int deltaLength,out ViveExclusiveMemoryTarget.Receipt oldReceipt)
        {
            oldReceipt=null;
            lock(completionGate)
            {
                if(Thread.CurrentThread.ManagedThreadId!=owner||Faulted||appendAuthorized||lease==null||
                    !Object.ReferenceEquals(lease,appendLease)||!Object.ReferenceEquals(lease.Cursor,this)||
                    !Object.ReferenceEquals(lease.Anchor,anchor)||!Object.ReferenceEquals(lease.Transaction,pending)||
                    prepared==null||digest==null||deltaLength<1)
                {Fail("append_authorization_invalid");return false;}
                try
                {
                    if(!issuer.TryBeginCursorAppend(anchor,this,lease,prepared,digest,deltaLength,out oldReceipt)||oldReceipt==null)
                    {oldReceipt=null;Fail("issuer_append_reservation_failed");return false;}
                    appendAuthorized=true;return true;
                }
                catch(Exception){oldReceipt=null;Fail("append_authorization_exception");return false;}
            }
        }
        // Called before the target append, never afterwards: the old target
        // receipt becomes invalid as soon as a target append is attempted.
        internal bool TryContinuePendingAppend(CursorAppendLease lease)
        {
            lock(completionGate)
            {
                if(Thread.CurrentThread.ManagedThreadId!=owner||Faulted||!appendAuthorized||lease==null||
                    !Object.ReferenceEquals(lease,appendLease)||!Object.ReferenceEquals(lease.Cursor,this)||
                    !Object.ReferenceEquals(lease.Anchor,anchor)||!Object.ReferenceEquals(lease.Transaction,pending))
                {Fail("append_continuation_invalid");return false;}
                try
                {
                    if(!issuer.CanContinueCursorAppend(anchor,this,lease))
                    {Fail("append_context_changed");return false;}
                    return true;
                }
                catch(Exception){Fail("append_continuation_exception");return false;}
            }
        }
        // The target has already attempted one append. Any failure below is
        // terminal: the issuer must mark the target uncertain and never retry.
        internal bool TryCompletePendingAppend(CursorAppendLease lease,ViveExclusiveMemoryTarget.Receipt newTargetReceipt)
        {
            lock(completionGate)
            {
                if(Thread.CurrentThread.ManagedThreadId!=owner||Faulted||!appendAuthorized||lease==null||!Object.ReferenceEquals(lease,appendLease)||
                    !Object.ReferenceEquals(lease.Cursor,this)||!Object.ReferenceEquals(lease.Anchor,anchor)||
                    !Object.ReferenceEquals(lease.Transaction,pending)||newTargetReceipt==null)
                {Fail("append_finalize_invalid");return false;}
                bool current;
                try{current=lease.DrainFence!=null?anchor.Participant.TryConsumePracticeTaskDrainFence(anchor,lease.DrainFence):anchor.Participant.TryConsumePracticeObservationFence(lease.Fence);}
                catch(Exception){Fail("append_fence_consume_failed");return false;}
                if(!current||Faulted){Fail("append_context_changed");return false;}
                try
                {
                    if(!(lease.DrainFence!=null?issuer.TryFinalizeCursorDrain(anchor,this,lease,newTargetReceipt,lease.DrainFence):issuer.TryFinalizeCursorAppend(anchor,this,lease,newTargetReceipt,lease.Fence))||
                        Object.ReferenceEquals(appendLease,lease)||pending!=null)
                    {Fail("append_receipt_or_context_invalid");return false;}
                    return true;
                }
                catch(Exception){Fail("append_finalize_exception");return false;}
            }
        }
        // Called by this exact sealed issuer while cursor→issuer→choices→
        // participant→target locks are held. Only primitive/reference updates;
        // no caller callbacks, I/O or observation clock is accessed.
        internal bool CommitUnderIssuerLocks(VivePracticeOwnedMemoryStart candidate,CursorAppendLease lease)
        {
            if(!CanCommitUnderIssuerLocks(candidate,lease))return false;
            traceCount=pendingEndCount;lastStep=pendingEndStep;lastStepIndex=pendingEndStep.Index;
            lastRevision=pendingEndRevision;checkpoint=pendingEndSnapshot;lastSequence=pendingLastSequence;lastEventTime=pendingLastEventTime;
            if(pendingDrainFence!=null){boundaryCommitted=true;committedTerminalFence=lease.DrainFence;}
            pending=null;pendingFence=null;pendingDrainFence=null;pendingEndStep=null;pendingEndSnapshot=null;appendLease=null;appendAuthorized=false;return true;
        }
        internal bool CanCommitUnderIssuerLocks(VivePracticeOwnedMemoryStart candidate,CursorAppendLease lease)
        {
            return Monitor.IsEntered(completionGate)&&Object.ReferenceEquals(candidate,issuer)&&issuer.CursorCommitLocksHeld&&!Faulted&&
                appendAuthorized&&lease!=null&&Object.ReferenceEquals(lease,appendLease)&&Object.ReferenceEquals(lease.Cursor,this)&&
                Object.ReferenceEquals(lease.Anchor,anchor)&&Object.ReferenceEquals(lease.Transaction,pending)&&
                pendingEndStep!=null&&pendingEndSnapshot!=null&&pendingEndCount>=1;
        }
        // Only a committed Retry boundary may offer a historical continuation.
        // The host must revalidate its own target receipt and participant/choice
        // state before ever treating the exact issued object as current.
        internal bool CanIssueRetryUnderIssuerLocks(VivePracticeOwnedMemoryStart candidate,VivePracticeCursorAnchor candidateAnchor,
            ViveDevelopmentParticipant.PracticeTaskDrainFence terminalFence)
        {
            return Monitor.IsEntered(completionGate)&&Object.ReferenceEquals(candidate,issuer)&&
                Object.ReferenceEquals(candidateAnchor,anchor)&&issuer.CursorPreTargetLocksHeld&&!Faulted&&boundaryCommitted&&
                pending==null&&appendLease==null&&committedTerminalFence!=null&&retryIssueFence!=null&&Object.ReferenceEquals(terminalFence,retryIssueFence)&&
                checkpoint!=null&&checkpoint.Task!=null&&checkpoint.Task.Phase==Phase.PracticeRetry&&checkpoint.Task.BlockIndex==-1&&
                checkpoint.Scalars.PracticeAttempt==1&&lastStep!=null&&Object.ReferenceEquals(lastStep.After,checkpoint)&&
                lastStep.Revision==lastRevision&&
                lastSequence>=0&&checkpoint.Scalars.EventCount==lastSequence+1;
        }
        internal bool TryIssueRetryContinuation(out VivePracticeRetryContinuation continuation)
        {
            continuation=null;
            lock(completionGate)
            {
                if(Thread.CurrentThread.ManagedThreadId!=owner){Fail("owner_thread_violation");return false;}
                if(busy!=0||Faulted||!boundaryCommitted||committedTerminalFence==null||retryIssueFence!=null)return false;
                ViveDevelopmentParticipant.PracticeTaskDrainFence fence=null;
                try
                {
                    if(!anchor.Participant.TryAcquirePracticeTaskDrainFence(anchor,anchor.Clock,out fence)||fence==null||
                        fence.RetryInputRevision!=committedTerminalFence.RetryInputRevision||
                        fence.StepTrace==null||fence.StepTrace.Count<1||fence.Revision!=fence.StepTrace[fence.StepTrace.Count-1].Revision||
                        !VivePracticeRetryReleaseValidator.IsReleaseOnly(fence.StepTrace,fence.MutationStepsObserved,traceCount,lastStep,checkpoint,
                            fence.StepTrace.Count,fence.Snapshot))
                    {Fail("retry_issue_trace_invalid");return false;}
                    var consumed=fence;bool valid=anchor.Participant.TryConsumePracticeTaskDrainFence(anchor,fence);fence=null;
                    if(!valid||!issuer.IsCurrentForCursorDrain(anchor,consumed,false)){Fail("retry_issue_context_changed");return false;}
                    retryIssueFence=consumed;
                    if(!issuer.TryIssueRetryContinuation(anchor,this,consumed,out continuation)||continuation==null)
                    {Fail("retry_issue_failed");return false;}
                    return true;
                }
                catch(Exception){Fail("retry_issue_exception");return false;}
                finally{if(fence!=null)anchor.Participant.AbandonPracticeTaskDrainFence(fence);}
            }
        }
        internal bool TryReserveRetryBegin(VivePracticeRetryContinuation continuation)
        {
            lock(completionGate)
            {
                if(Thread.CurrentThread.ManagedThreadId!=owner){Fail("owner_thread_violation");return false;}
                if(busy!=0||Faulted||!boundaryCommitted||continuation==null||!Object.ReferenceEquals(continuation.Cursor,this)||
                    !Object.ReferenceEquals(continuation.Anchor,anchor)||!Object.ReferenceEquals(continuation.IssueFence,retryIssueFence))return false;
                ViveDevelopmentParticipant.PracticeTaskDrainFence fence=null;
                try
                {
                    if(!anchor.Participant.TryAcquirePracticeTaskDrainFence(anchor,anchor.Clock,out fence)||fence==null||
                        fence.RetryInputRevision!=continuation.IssueFence.RetryInputRevision||
                        fence.StepTrace==null||fence.StepTrace.Count<1||fence.Revision!=fence.StepTrace[fence.StepTrace.Count-1].Revision||
                        !VivePracticeRetryReleaseValidator.IsReleaseOnly(fence.StepTrace,fence.MutationStepsObserved,continuation.IssueTraceCount,
                            continuation.IssueStep,continuation.IssueSnapshot,fence.StepTrace.Count,fence.Snapshot))
                    {Fail("retry_begin_trace_invalid");return false;}
                    var consumed=fence;bool valid=anchor.Participant.TryConsumePracticeTaskDrainFence(anchor,fence);fence=null;
                    if(!valid||!issuer.IsCurrentForCursorDrain(anchor,consumed,false)||
                        !issuer.TryReserveRetryBegin(anchor,this,continuation,consumed))
                    {Fail("retry_begin_reservation_failed");return false;}
                    return true;
                }
                catch(Exception){Fail("retry_begin_reservation_exception");return false;}
                finally{if(fence!=null)anchor.Participant.AbandonPracticeTaskDrainFence(fence);}
            }
        }
        internal bool TryClaimRetryContinuation(VivePracticeRetryContinuation continuation,VivePracticeStartBinding.Ticket ticket,
            VivePracticeContextV3 nextContext,out VivePracticeConsentAuthority.StartReservation nextReservation)
        {
            nextReservation=null;
            lock(completionGate)
            {
                if(Thread.CurrentThread.ManagedThreadId!=owner){Fail("owner_thread_violation");return false;}
                if(busy!=0||Faulted||!boundaryCommitted||continuation==null||ticket==null||nextContext==null||
                    !Object.ReferenceEquals(continuation.Cursor,this)||!Object.ReferenceEquals(continuation.Anchor,anchor))return false;
                return issuer.TryClaimRetryContinuation(anchor,this,continuation,ticket,nextContext,out nextReservation);
            }
        }
        internal bool CanClaimRetryUnderIssuerLocks(VivePracticeOwnedMemoryStart candidate,VivePracticeRetryContinuation continuation)
        {
            return Monitor.IsEntered(completionGate)&&Object.ReferenceEquals(candidate,issuer)&&issuer.CursorPreTargetLocksHeld&&
                continuation!=null&&Object.ReferenceEquals(continuation.Issuer,issuer)&&Object.ReferenceEquals(continuation.Cursor,this)&&
                Object.ReferenceEquals(continuation.Anchor,anchor)&&!Faulted&&boundaryCommitted&&pending==null&&appendLease==null&&
                Object.ReferenceEquals(continuation.TerminalFence,committedTerminalFence)&&Object.ReferenceEquals(continuation.IssueFence,retryIssueFence)&&
                Object.ReferenceEquals(continuation.TerminalSnapshot,checkpoint)&&
                Object.ReferenceEquals(continuation.TerminalStep,lastStep)&&continuation.TerminalRevision==lastRevision&&
                continuation.TerminalSequence==lastSequence&&continuation.TerminalTime==lastEventTime&&
                checkpoint!=null&&checkpoint.Task!=null&&checkpoint.Task.Phase==Phase.PracticeRetry;
        }
        internal bool TryPublishRetryStart(VivePracticeRetryContinuation continuation,VivePracticeStartReservation newReservation,
            VivePracticeOwnedMemoryStart newHost,VivePracticeConsentAuthority.StartReservation start,
            ViveDevelopmentParticipant.PracticeObservationFence completedFence)
        {
            lock(completionGate)
            {
                if(Thread.CurrentThread.ManagedThreadId!=owner){Fail("owner_thread_violation");return false;}
                if(Faulted||!boundaryCommitted||continuation==null||newReservation==null||newHost==null||start==null||completedFence==null||
                    !Object.ReferenceEquals(continuation.Cursor,this)||!Object.ReferenceEquals(continuation.Anchor,anchor))return false;
                return issuer.TryPublishRetryStart(anchor,this,continuation,newReservation,newHost,start,completedFence);
            }
        }
        internal ExperimentIncrementalSnapshot RetryTerminalSnapshot {get{return checkpoint;}}
        internal ViveDevelopmentParticipant.PracticeTaskDrainFence RetryTerminalFence {get{return committedTerminalFence;}}
        internal ViveSyntheticStepTrace RetryTerminalStep {get{return lastStep;}}
        internal long RetryTerminalRevision {get{return lastRevision;}}
        internal long RetryTerminalSequence {get{return lastSequence;}}
        internal double RetryTerminalTime {get{return lastEventTime;}}
        internal void FaultPendingAppend(CursorAppendLease lease)
        {
            lock(completionGate)
            {
                Fail("append_uncertain_or_failed");
                if(lease!=null&&Object.ReferenceEquals(lease,appendLease)&&Thread.CurrentThread.ManagedThreadId==owner)
                {if(lease.DrainFence!=null)anchor.Participant.AbandonPracticeTaskDrainFence(lease.DrainFence);else anchor.Participant.AbandonPracticeObservationFence(lease.Fence);appendLease=null;}
            }
        }
        internal void Stop(){lock(completionGate)Interlocked.Exchange(ref terminal,1);}
    }
}
