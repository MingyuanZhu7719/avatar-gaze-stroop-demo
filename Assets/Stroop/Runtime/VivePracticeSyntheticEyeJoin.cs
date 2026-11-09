using System;
using System.Threading;
using AvatarStroop.Core;

namespace AvatarStroop.Runtime
{
    // Synthetic markers only: this sealed host has no Stream, file, device,
    // network or caller-supplied provider. Its memory proof is not consent,
    // durable storage, real gaze, session completeness or research admission.
    internal sealed class VivePracticeSyntheticEyeJoin
    {
        internal sealed class Node
        {
            internal readonly byte Marker;internal readonly long TaskSequence,Revision;internal readonly int TraceCount;
            internal double CommitCheckedAt;
            internal Node Next;
            internal Node(byte marker,long taskSequence,long revision,int traceCount)
            {Marker=marker;TaskSequence=taskSequence;Revision=revision;TraceCount=traceCount;}
        }
        readonly VivePracticeOwnedMemoryStart issuer;
        readonly VivePracticeTaskEventCursor cursor;
        readonly ViveDevelopmentParticipant participant;
        readonly VivePracticeConsentAuthority choices;
        readonly VivePracticeContextV3 context;
        readonly VivePracticeEyeFencedSource source;
        readonly IClock clock;
        readonly MonotonicClock syntheticCommitClock;
        readonly int owner,maximum;
        readonly object stateGate=new object();
        Node first,last,pending;
        VivePracticeSyntheticTicket pendingTicket;
        ViveDevelopmentParticipant.PracticeObservationFence pendingFence;
        double pendingPrecheck=-1;
        bool busy,faulted;
        int count;
        // Synthetic-only test hook runs BEFORE the locks and never while a
        // real destination exists. It is not a production authorization.
        internal Action BeforeCommitForTest {get;set;}
        internal Action BeforeLockedCommitForTest {get;set;}
        internal int Count {get{lock(stateGate)return count;}}
        internal bool Faulted {get{lock(stateGate)return faulted;}}
        internal bool CanWrite {get{return false;}}
        internal bool ResearchGateOpen {get{return false;}}
        internal VivePracticeSyntheticEyeJoin(VivePracticeOwnedMemoryStart issuer,VivePracticeTaskEventCursor cursor,
            ViveDevelopmentParticipant participant,IClock clock,VivePracticeConsentAuthority choices,
            VivePracticeContextV3 context,VivePracticeEyeFencedSource source,int maximum)
        {
            if(issuer==null||cursor==null||participant==null||clock==null||choices==null||context==null||source==null)
                throw new ArgumentNullException("synthetic dependencies");
            if(maximum<1||maximum>4096)throw new ArgumentOutOfRangeException("maximum");
            var monotonic=clock as MonotonicClock;
            if(monotonic==null||!participant.UsesEventClock(monotonic)||context.ClockId!=monotonic.ClockId)
                throw new ArgumentException("The same sealed monotonic task clock is required for synthetic commit.");
            this.issuer=issuer;this.cursor=cursor;this.participant=participant;this.clock=clock;this.choices=choices;
            syntheticCommitClock=monotonic;this.context=context;this.source=source;this.maximum=maximum;owner=Thread.CurrentThread.ManagedThreadId;
        }
        internal bool TryJoin(VivePracticeSyntheticTicket ticket)
        {
            lock(stateGate)
            {
                if(Thread.CurrentThread.ManagedThreadId!=owner||busy||faulted||ticket==null||count>=maximum)
                {faulted=true;return false;}
                busy=true;
            }
            ViveDevelopmentParticipant.PracticeObservationFence fence=null;
            try
            {
                byte marker;
                if(!Object.ReferenceEquals(ticket.Issuer,source)||!Object.ReferenceEquals(ticket.Participant,participant)||
                    !Object.ReferenceEquals(ticket.Clock,clock)||!Object.ReferenceEquals(ticket.Choices,choices)||
                    !Object.ReferenceEquals(ticket.Context,context)||
                    !source.TryConsumeSyntheticTicket(ticket,ticket.Snapshot,ticket.Revision,ticket.TraceCount,out marker))
                {Fail();return false;}
                if(!participant.TryAcquirePracticeObservationFence(clock,out fence)||fence==null||
                    !cursor.MatchesSyntheticEyeCheckpoint(issuer,participant,clock,choices,context,fence,ticket.Snapshot,ticket.Revision,ticket.TraceCount))
                {Fail();return false;}
                if(ticket.Snapshot==null||ticket.Snapshot.Scalars.EventCount<1){Fail();return false;}
                var completed=fence;bool current=participant.TryConsumePracticeObservationFence(fence);fence=null;
                if(!current){Fail();return false;}
                var node=new Node(marker,ticket.Snapshot.Scalars.EventCount-1,ticket.Revision,ticket.TraceCount);
                lock(stateGate){if(faulted)return false;pending=node;pendingTicket=ticket;pendingFence=completed;}
                if(BeforeCommitForTest!=null)BeforeCommitForTest();
                // Use the same sealed stopwatch and conversion for both reads.
                // Waiting before the locked check can add age or reveal rollback.
                double now;
                if(clock.ClockId!=context.ClockId||!syntheticCommitClock.TryReadSyntheticCommitTime(out now)||now<ticket.Observed||
                    now-ticket.Observed>ticket.MaximumAgeSeconds){Fail();return false;}
                lock(stateGate){if(faulted||!Object.ReferenceEquals(pending,node)){faulted=true;return false;}pendingPrecheck=now;}
                if(BeforeLockedCommitForTest!=null)BeforeLockedCommitForTest();
                if(!cursor.TryPublishSyntheticEye(this,issuer,participant,clock,choices,context,completed,ticket))
                {Fail();return false;}
                return true;
            }
            catch(Exception){Fail();return false;}
            finally
            {
                if(fence!=null)participant.AbandonPracticeObservationFence(fence);
                lock(stateGate){pending=null;pendingTicket=null;pendingFence=null;pendingPrecheck=-1;busy=false;}
            }
        }
        void Fail(){lock(stateGate)faulted=true;}
        // Called only through cursor -> reservation -> choices -> participant
        // -> fake target -> synthetic source -> fake join.
        // This performs controlled internal identity/lock checks, one sealed
        // stopwatch read and an already-allocated reference assignment. Lock
        // acquisition and scheduling are not bounded real-time guarantees.
        internal bool PublishUnderTargetLock(VivePracticeTaskEventCursor candidateCursor,VivePracticeOwnedMemoryStart candidateIssuer,
            ViveDevelopmentParticipant.PracticeObservationFence fence,VivePracticeSyntheticTicket ticket)
        {
            lock(source.SyntheticTicketGate)
            {
                if(!source.ConsumedTicketStillCurrent(ticket))return false;
                lock(stateGate)
                {
                    if(Thread.CurrentThread.ManagedThreadId!=owner||!busy||faulted||pending==null||count>=maximum||
                        !Object.ReferenceEquals(cursor,candidateCursor)||!Object.ReferenceEquals(issuer,candidateIssuer)||
                        !Object.ReferenceEquals(pendingFence,fence)||!Object.ReferenceEquals(pendingTicket,ticket)||ticket.Consumed!=1||
                        !cursor.CompletionLockHeld||!issuer.SyntheticReservationLockHeld||!issuer.CursorCommitLocksHeld||
                        !choices.StateLockHeld||!participant.ObservationFenceLockHeld)return false;
                    double commitTime;
                    if(!Object.ReferenceEquals(clock,syntheticCommitClock)||!Object.ReferenceEquals(ticket.Clock,syntheticCommitClock)||
                        !syntheticCommitClock.TryReadSyntheticCommitTime(out commitTime)||pendingPrecheck<ticket.Observed||
                        Double.IsNaN(pendingPrecheck)||Double.IsInfinity(pendingPrecheck)||commitTime<pendingPrecheck||commitTime<ticket.Observed||
                        commitTime-ticket.Observed>ticket.MaximumAgeSeconds)return false;
                    pending.CommitCheckedAt=commitTime;
                    if(last==null)first=pending;else last.Next=pending;last=pending;count++;return true;
                }
            }
        }
        internal byte[] CopyMarkersForTest()
        {lock(stateGate){var values=new byte[count];int i=0;for(var item=first;item!=null;item=item.Next)values[i++]=item.Marker;return values;}}
        internal long[] CopyTaskWatermarksForTest()
        {lock(stateGate){var values=new long[count];int i=0;for(var item=first;item!=null;item=item.Next)values[i++]=item.TaskSequence;return values;}}
        internal double[] CopyCommitTimesForTest()
        {lock(stateGate){var values=new double[count];int i=0;for(var item=first;item!=null;item=item.Next)values[i++]=item.CommitCheckedAt;return values;}}
    }
}
