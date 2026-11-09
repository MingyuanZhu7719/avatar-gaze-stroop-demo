using System;
using System.Threading;
using AvatarStroop.Core;

namespace AvatarStroop.Runtime
{
    // Binds the actual observed simulation participant's current start revision
    // to the application's two explicit, revocable choice actions. It is not a
    // UI identity, vendor consent, worker lease, sample, writer or research gate.
    // The returned batch is historical data, not authorization to write it.
    public sealed class VivePracticeStartBinding
    {
        public sealed class Ticket
        {
            internal readonly VivePracticeStartBinding Issuer;
            internal readonly VivePracticeStartBatch Batch;
            internal readonly ViveDevelopmentParticipant.PracticeObservationFence Fence;
            internal readonly long ChoiceGeneration;
            internal Ticket(VivePracticeStartBinding issuer,VivePracticeStartBatch batch,
                ViveDevelopmentParticipant.PracticeObservationFence fence,long generation)
            {Issuer=issuer;Batch=batch;Fence=fence;ChoiceGeneration=generation;}
        }
        readonly ViveDevelopmentParticipant participant;
        readonly IClock clock;
        readonly VivePracticeConsentAuthority choices;
        readonly string clockId;
        readonly int owner;
        readonly VivePracticeRetryContinuation retryContinuation;
        bool retryClaimed;
        int busy,faulted,closed;
        bool spent;
        Ticket outstanding;
        double lastNow=-1;
        public bool CanWrite {get{return false;}}
        public bool ConsentAuthenticated {get{return false;}}
        public bool Faulted {get{return Interlocked.CompareExchange(ref faulted,0,0)!=0;}}
        internal bool BelongsTo(ViveDevelopmentParticipant candidate,IClock candidateClock,VivePracticeConsentAuthority candidateChoices)
        {return Object.ReferenceEquals(participant,candidate)&&Object.ReferenceEquals(clock,candidateClock)&&Object.ReferenceEquals(choices,candidateChoices);}
        public VivePracticeStartBinding(ViveDevelopmentParticipant participant,IClock clock,VivePracticeConsentAuthority choices,bool explicitDevelopmentOnly)
        {
            if(!explicitDevelopmentOnly)throw new InvalidOperationException("Explicit development scope required");
            if(participant==null||clock==null||choices==null)throw new ArgumentNullException("participant/clock/choices");
            if(participant.IsResearchSession||!participant.UsesEventClock(clock)||!choices.IsCurrent(choices.Generation,participant,clock))
                throw new InvalidOperationException("Active choices and same observed simulation participant/clock required");
            string id=clock.ClockId;if(String.IsNullOrWhiteSpace(id)||id!=participant.EventClockId)throw new ArgumentException("Matching clock identity required");
            this.participant=participant;this.clock=clock;this.choices=choices;clockId=id;owner=Thread.CurrentThread.ManagedThreadId;
        }
        // Retry is only a candidate until the old owned issuer validates its
        // exact continuation and the outstanding start ticket under its locks.
        internal VivePracticeStartBinding(ViveDevelopmentParticipant participant,IClock clock,VivePracticeConsentAuthority choices,
            VivePracticeRetryContinuation continuation,bool explicitDevelopmentOnly)
            :this(participant,clock,choices,explicitDevelopmentOnly)
        {if(continuation==null)throw new ArgumentNullException("continuation");retryContinuation=continuation;}
        internal bool OwnsOutstandingRetryTicket(VivePracticeRetryContinuation continuation,Ticket ticket)
        {
            return Thread.CurrentThread.ManagedThreadId==owner&&!Faulted&&Interlocked.CompareExchange(ref closed,0,0)==0&&
                !spent&&!retryClaimed&&continuation!=null&&Object.ReferenceEquals(retryContinuation,continuation)&&ticket!=null&&
                Object.ReferenceEquals(outstanding,ticket)&&Object.ReferenceEquals(ticket.Issuer,this)&&ticket.Batch!=null&&ticket.Batch.Attempt==2;
        }
        internal bool MarkRetryClaimed(VivePracticeRetryContinuation continuation,Ticket ticket)
        {
            if(!OwnsOutstandingRetryTicket(continuation,ticket)||continuation.Anchor==null||continuation.Issuer==null||
                !choices.StateLockHeld||!participant.ObservationFenceLockHeld||
                !continuation.Issuer.IsRetryConsumedUnderParticipantLock(continuation))return false;
            retryClaimed=true;return true;
        }
        bool Enter()
        {
            if(Thread.CurrentThread.ManagedThreadId!=owner){Interlocked.Exchange(ref faulted,1);return false;}
            if(Interlocked.CompareExchange(ref busy,1,0)!=0){Interlocked.Exchange(ref faulted,1);return false;}
            return true;
        }
        bool Current(long generation)
        {
            if(Faulted||Interlocked.CompareExchange(ref closed,0,0)!=0||!choices.IsCurrent(generation,participant,clock))return false;
            return !Faulted&&Interlocked.CompareExchange(ref closed,0,0)==0;
        }
        bool Now(out double value)
        {
            value=0;
            try
            {
                string id=clock.ClockId;value=clock.Now;
                if(id!=clockId||Double.IsNaN(value)||Double.IsInfinity(value)||value<0||value<lastNow)return false;
                lastNow=value;return true;
            }
            catch(Exception){return false;}
        }
        // One owner-thread action followed immediately by extraction. A failed
        // start or extraction is never replayed: the participant action may
        // already have executed. No device, file, or research gate is implied.
        internal bool TryBeginRetryAndAcquire(Actor actor,int maximumTraceSteps,out Ticket ticket)
        {
            ticket=null;
            if(actor!=Actor.Owner||maximumTraceSteps<1||maximumTraceSteps>4096||retryContinuation==null||retryContinuation.Cursor==null||Thread.CurrentThread.ManagedThreadId!=owner||
                Faulted||spent||outstanding!=null||Interlocked.CompareExchange(ref closed,0,0)!=0||
                !retryContinuation.Cursor.TryReserveRetryBegin(retryContinuation))return false;
            try
            {
                participant.OwnerBeginPractice(actor);
                if(TryAcquire(2,maximumTraceSteps,out ticket))return true;
            }
            catch(Exception){ }
            Interlocked.Exchange(ref faulted,1);return false;
        }
        public bool TryAcquire(int attempt,int maximumTraceSteps,out Ticket ticket)
        {
            ticket=null;if(!Enter())return false;
            ViveDevelopmentParticipant.PracticeObservationFence fence=null;
            try
            {
                if(spent||outstanding!=null||Faulted||Interlocked.CompareExchange(ref closed,0,0)!=0)return false;
                if(retryContinuation==null?attempt!=1:attempt!=2)return false;
                long generation=choices.Generation;double enabled=choices.EnabledAt;
                if(!Current(generation)||enabled<0)return false;
                VivePracticeStartBatch batch;
                if(!VivePracticeStartBatchExtractor.TryExtract(participant,clock,attempt,maximumTraceSteps,out batch)||batch==null||
                    batch.Events==null||batch.Events.Count!=3||batch.Events[0].Time<=enabled||!Current(generation))return false;
                if(!participant.TryAcquirePracticeObservationFence(clock,out fence)||fence==null)return false;
                var trace=fence.StepTrace;var snapshot=fence.Snapshot;
                if(trace==null||trace.Count==0||snapshot==null||snapshot.Task.Phase!=Phase.Practice||
                    trace[trace.Count-1].Revision!=batch.Revision||!Object.ReferenceEquals(trace[trace.Count-1].After,snapshot)||!Current(generation))return false;
                if((retryContinuation==null&&!participant.TryReservePracticeStartRevision(fence,batch.Revision))||!Current(generation))return false;
                var issued=new Ticket(this,batch,fence,generation);outstanding=issued;ticket=issued;fence=null;return true;
            }
            catch(Exception){Interlocked.Exchange(ref faulted,1);return false;}
            finally{if(fence!=null)participant.AbandonPracticeObservationFence(fence);Interlocked.Exchange(ref busy,0);}
        }
        // One attempt only, including rejection. The single-use participant
        // fence is consumed after clock callbacks, so task actions/reentry and
        // choice withdrawal invalidate the historical batch handoff.
        public bool TryTake(Ticket ticket,out VivePracticeStartBatch batch)
        {
            batch=null;if(!Enter())return false;
            ViveDevelopmentParticipant.PracticeObservationFence fence=null;
            try
            {
                if(ticket==null||!Object.ReferenceEquals(ticket.Issuer,this)||!Object.ReferenceEquals(ticket,outstanding)||
                    (retryContinuation!=null&&!retryClaimed))return false;
                outstanding=null;spent=true;fence=ticket.Fence;
                if(!Current(ticket.ChoiceGeneration))return false;
                double now;if(!Now(out now)){Interlocked.Exchange(ref faulted,1);return false;}
                if(now<ticket.Batch.Events[2].Time){Interlocked.Exchange(ref faulted,1);return false;}
                if(!Current(ticket.ChoiceGeneration))return false;
                bool valid=participant.TryConsumePracticeObservationFence(fence);fence=null;
                if(!valid||!Current(ticket.ChoiceGeneration))return false;
                batch=ticket.Batch;return true;
            }
            catch(Exception){Interlocked.Exchange(ref faulted,1);return false;}
            finally{if(fence!=null)participant.AbandonPracticeObservationFence(fence);Interlocked.Exchange(ref busy,0);}
        }
        // Do not call participant/UI/stream APIs from a foreign thread. If the
        // owner does not return, the host must separately enforce safety stop.
        public void Close()
        {
            if(!Enter())return;
            try{Interlocked.Exchange(ref closed,1);if(outstanding!=null){participant.AbandonPracticeObservationFence(outstanding.Fence);outstanding=null;}}
            finally{Interlocked.Exchange(ref busy,0);}
        }
    }
}
