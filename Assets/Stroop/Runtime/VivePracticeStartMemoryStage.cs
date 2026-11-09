using System;
using System.Threading;
using AvatarStroop.Core;

namespace AvatarStroop.Runtime
{
    // Internal carrier, never a Stream or callback. Publication is a single
    // reference assignment under the participant and choice state locks.
    internal sealed class VivePracticeMemoryPublication
    {internal VivePracticeStartEnvelope Pending,Committed;}
    // This produces only a private in-memory start envelope from an actual
    // observed Simulation participant. It has no caller-provided Stream or
    // destination and does not write files, eye data, v3 or a session journal.
    // A returned envelope does not authenticate identity, consent or storage.
    internal sealed class VivePracticeStartMemoryStage
    {
        sealed class PrivateSink:IVivePracticeStartFakeSink
        {
            readonly VivePracticeContextV3 context;readonly int maximumBytes;readonly VivePracticeMemoryPublication publication;
            internal PrivateSink(VivePracticeContextV3 context,int maximumBytes,VivePracticeMemoryPublication publication)
            {this.context=context;this.maximumBytes=maximumBytes;this.publication=publication;}
            public bool AcceptSynthetic(VivePracticeStartBatch batch)
            {publication.Pending=VivePracticeStartEnvelopeCodec.Encode(context,batch,maximumBytes);return publication.Pending!=null;}
        }
        readonly VivePracticeStartReservation reservation;
        readonly VivePracticeContextV3 context;readonly int maximumBytes,owner;readonly object stateGate=new object();
        readonly VivePracticeMemoryPublication publication=new VivePracticeMemoryPublication();
        readonly ViveDevelopmentParticipant participant;readonly VivePracticeConsentAuthority choices;readonly IClock clock;
        long publishedGeneration=-1;bool attempted,busy,faulted;
        internal bool CanWrite {get{return false;}}
        internal bool IncludesEyeData {get{return false;}}
        internal bool ResearchGateOpen {get{return false;}}
        internal VivePracticeStartReservationOutcome Outcome
        {get{lock(stateGate)return faulted?VivePracticeStartReservationOutcome.Uncertain:reservation.Outcome;}}
        internal VivePracticeStartMemoryStage(ViveDevelopmentParticipant participant,IClock clock,VivePracticeConsentAuthority choices,
            VivePracticeStartBinding binding,VivePracticeContextV3 context,int maximumBytes)
        {
            if(participant==null||clock==null||choices==null||binding==null||context==null)throw new ArgumentNullException("context/participant");
            if(context.SessionId!=participant.SessionId||context.ClockId!=participant.EventClockId||!participant.UsesEventClock(clock)||
                context.PracticeAttempt!=1||context.EpochReason!=VivePracticeEpochReasonV3.Initial||context.RecoveryOrdinal!=0||context.PreviousEpochId!=null||
                maximumBytes<256||maximumBytes>VivePracticeStartEnvelopeCodec.MaximumEnvelopeBytes)throw new ArgumentException("Matching initial development context required");
            this.context=context;this.maximumBytes=maximumBytes;this.participant=participant;this.choices=choices;this.clock=clock;owner=Thread.CurrentThread.ManagedThreadId;
            reservation=new VivePracticeStartReservation(participant,clock,choices,binding);
        }
        internal bool TryStage(VivePracticeStartBinding.Ticket ticket)
        {
            lock(stateGate)
            {
                if(Thread.CurrentThread.ManagedThreadId!=owner||busy){faulted=true;reservation.Invalidate();return false;}
                if(attempted||faulted)return false;attempted=true;busy=true;
            }
            try
            {
                var sink=new PrivateSink(context,maximumBytes,publication);
                bool accepted=reservation.TryAcceptMemory(ticket,sink,publication);
                lock(stateGate)
                {
                    if(faulted||!accepted||publication.Committed==null)return false;
                    publishedGeneration=ticket.ChoiceGeneration;return true;
                }
            }
            finally{lock(stateGate)busy=false;}
        }
        // Testing/inspection only. This never targets a file or shared stream.
        internal byte[] CopyEnvelopeForTest()
        {
            lock(stateGate)
            {
                if(faulted||publication.Committed==null||!choices.IsCurrent(publishedGeneration,participant,clock))return null;
                var copy=publication.Committed.ToArray();
                if(!choices.IsCurrent(publishedGeneration,participant,clock)){Array.Clear(copy,0,copy.Length);return null;}
                return copy;
            }
        }
    }
}
