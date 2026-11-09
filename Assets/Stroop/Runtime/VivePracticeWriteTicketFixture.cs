using System;
using System.Threading;

namespace AvatarStroop.Runtime
{
    // Isolated state-machine fixture. No real participant, consent, device,
    // journal, Stream, network, storage, or production admission is used.
    internal sealed class PracticeFixtureClock
    {
        long bits;
        internal PracticeFixtureClock(double time){Set(time);}
        internal double Now {get{return BitConverter.Int64BitsToDouble(Interlocked.Read(ref bits));}}
        internal void Set(double value){Interlocked.Exchange(ref bits,BitConverter.DoubleToInt64Bits(value));}
    }
    internal sealed class PracticeFixtureContext
    {
        internal readonly string Session,Participant,Stream,Device,Clock,Mapping,Epoch;
        internal readonly int Attempt;internal readonly long StartSequence;
        internal PracticeFixtureContext(string session,string participant,string stream,string device,string clock,string mapping,string epoch,int attempt,long startSequence)
        {
            if(String.IsNullOrWhiteSpace(session)||String.IsNullOrWhiteSpace(participant)||String.IsNullOrWhiteSpace(stream)||String.IsNullOrWhiteSpace(device)||
                String.IsNullOrWhiteSpace(clock)||String.IsNullOrWhiteSpace(mapping)||String.IsNullOrWhiteSpace(epoch)||attempt<1||startSequence<0)
                throw new ArgumentException("Synthetic context is incomplete.");
            Session=session;Participant=participant;Stream=stream;Device=device;Clock=clock;Mapping=mapping;Epoch=epoch;Attempt=attempt;StartSequence=startSequence;
        }
    }
    // This fake sink records only synthetic call counts; it never writes bytes.
    internal sealed class PracticeFixtureSink
    {
        int writes,flushes;
        internal int WriteStarts {get{return Volatile.Read(ref writes);}}
        internal int FlushStarts {get{return Volatile.Read(ref flushes);}}
        internal void StartWrite(){Interlocked.Increment(ref writes);}
        internal void StartFlush(){Interlocked.Increment(ref flushes);}
    }
    internal sealed class PracticeFixtureFrame
    {
        internal readonly PracticeFixtureContext Context;internal readonly long Sequence;internal readonly double Observed;
        internal readonly bool RawUsable;internal readonly byte SyntheticMarker;
        internal PracticeFixtureFrame(PracticeFixtureContext context,long sequence,double observed,bool usable,byte syntheticMarker)
        {Context=context;Sequence=sequence;Observed=observed;RawUsable=usable;SyntheticMarker=syntheticMarker;}
    }
    internal enum PracticeFixtureState { Idle,MemoryCommitted,IoInFlight,WrittenUnflushed,FlushInFlight,SyntheticComplete,Invalidated,Revoked,Uncertain,Faulted }
    internal enum PracticeFixtureCommit { RejectedBeforeCommit,MemoryCommitted }
    internal enum PracticeFixtureIoResult { Complete,Partial,Failed }
    internal sealed class PracticeFixtureTicket
    {
        internal readonly PracticeFixtureCoordinator Issuer;internal readonly PracticeFixtureFrame Frame;
        internal readonly object Head,Lease,Writer;internal readonly long TaskGeneration,ConsentGeneration,SourceGeneration;
        internal int Claimed;
        internal PracticeFixtureTicket(PracticeFixtureCoordinator issuer,PracticeFixtureFrame frame,object head,object lease,object writer,long task,long consent,long source)
        {Issuer=issuer;Frame=frame;Head=head;Lease=lease;Writer=writer;TaskGeneration=task;ConsentGeneration=consent;SourceGeneration=source;}
    }
    // The lock covers only bounded local state operations and a byte assignment.
    // Fake sink start records no bytes and invokes no callback; the real I/O
    // handoff problem is intentionally not implemented or proven by this model.
    internal sealed class PracticeFixtureCoordinator
    {
        readonly object gate=new object(),lease=new object(),writer=new object();readonly int owner;
        readonly PracticeFixtureClock clock;readonly PracticeFixtureContext context;readonly PracticeFixtureSink sink;readonly double maxAge;
        object head=new object();long taskGeneration,consentGeneration,sourceGeneration,lastSequence=-1;double enabledAt=-1,lastNow=-1,lastEye=-1;
        bool eyeChoice,taskChoice,revoked,activeTask=true,sourceActive=true,leaseActive=true;PracticeFixtureTicket outstanding;
        PracticeFixtureState state=PracticeFixtureState.Idle;byte memoryMarker;bool hasMemory;
        internal PracticeFixtureCoordinator(PracticeFixtureContext context,PracticeFixtureClock clock,PracticeFixtureSink sink,double maxAge)
        {
            if(context==null||clock==null||sink==null||!Finite(maxAge)||maxAge<=0||maxAge>1)throw new ArgumentException("Bounded synthetic fixture required.");
            this.context=context;this.clock=clock;this.sink=sink;this.maxAge=maxAge;owner=Thread.CurrentThread.ManagedThreadId;
        }
        internal bool SyntheticFixtureOnly {get{return true;}}internal bool ProductionGateOpen {get{return false;}}
        internal bool ConsentAuthenticated {get{return false;}}internal bool DeviceAuthenticated {get{return false;}}internal bool DurabilityProven {get{return false;}}
        internal PracticeFixtureState State {get{lock(gate)return state;}}
        internal bool HasSyntheticMemory {get{lock(gate)return hasMemory;}}
        internal byte SyntheticMemoryMarker {get{lock(gate){if(!hasMemory)throw new InvalidOperationException();return memoryMarker;}}}
        static bool Finite(double n){return !Double.IsNaN(n)&&!Double.IsInfinity(n)&&n>=0;}
        bool OnOwner(){return Thread.CurrentThread.ManagedThreadId==owner;}
        bool ObserveNow(out double now)
        {now=clock.Now;if(!Finite(now)||now<lastNow){state=PracticeFixtureState.Faulted;return false;}lastNow=now;return true;}
        bool Active(){return !revoked&&eyeChoice&&taskChoice&&activeTask&&sourceActive&&leaseActive&&state==PracticeFixtureState.Idle;}
        bool Bump(ref long value){if(value==Int64.MaxValue){state=state==PracticeFixtureState.IoInFlight||state==PracticeFixtureState.WrittenUnflushed||state==PracticeFixtureState.FlushInFlight?PracticeFixtureState.Uncertain:PracticeFixtureState.Faulted;return false;}value++;return true;}
        // These are fixture actions, not evidence of a real person's consent.
        internal bool ChooseEye(){lock(gate){if(!OnOwner()||revoked||eyeChoice)return false;eyeChoice=true;return EnableIfComplete();}}
        internal bool ChooseTask(){lock(gate){if(!OnOwner()||revoked||taskChoice)return false;taskChoice=true;return EnableIfComplete();}}
        bool EnableIfComplete()
        {if(!eyeChoice||!taskChoice)return true;double now;if(!ObserveNow(out now)||!Bump(ref consentGeneration)){state=PracticeFixtureState.Faulted;return false;}enabledAt=now;return true;}
        // Safety actions have no dependence on the owner thread and acquire no
        // lock held across clocks, callbacks, files, streams or external I/O.
        internal void Revoke()
        {
            lock(gate){if(revoked)return;bool io=state==PracticeFixtureState.IoInFlight||state==PracticeFixtureState.WrittenUnflushed||state==PracticeFixtureState.FlushInFlight;
                revoked=true;outstanding=null;Bump(ref consentGeneration);
                if(io)state=PracticeFixtureState.Uncertain;
                else if(state!=PracticeFixtureState.SyntheticComplete&&state!=PracticeFixtureState.Uncertain&&state!=PracticeFixtureState.Faulted)state=PracticeFixtureState.Revoked;}
        }
        internal bool ChangeTask(bool active)
        {lock(gate){if(!OnOwner())return false;activeTask=active;head=new object();outstanding=null;if(!Bump(ref taskGeneration))return false;Invalidate();return true;}}
        internal bool ChangeSource(bool active,bool leaseHeld)
        {lock(gate){if(!OnOwner())return false;sourceActive=active;leaseActive=leaseHeld;outstanding=null;if(!Bump(ref sourceGeneration))return false;Invalidate();return true;}}
        void Invalidate()
        {if(state==PracticeFixtureState.IoInFlight||state==PracticeFixtureState.WrittenUnflushed||state==PracticeFixtureState.FlushInFlight)state=PracticeFixtureState.Uncertain;
            else if(state==PracticeFixtureState.MemoryCommitted)state=PracticeFixtureState.Invalidated;}
        internal PracticeFixtureTicket Issue(PracticeFixtureFrame frame)
        {
            lock(gate)
            {
                if(outstanding!=null&&Volatile.Read(ref outstanding.Claimed)!=0)outstanding=null;
                double now;if(!OnOwner()||!Active()||outstanding!=null||frame==null||!Object.ReferenceEquals(frame.Context,context)||!frame.RawUsable||
                    frame.Sequence<=lastSequence||!Finite(frame.Observed)||!ObserveNow(out now)||frame.Observed<=enabledAt||frame.Observed>now||now-frame.Observed>maxAge)return null;
                outstanding=new PracticeFixtureTicket(this,frame,head,lease,writer,taskGeneration,consentGeneration,sourceGeneration);return outstanding;
            }
        }
        internal PracticeFixtureCommit Commit(PracticeFixtureTicket ticket)
        {
            if(ticket==null)return PracticeFixtureCommit.RejectedBeforeCommit;
            if(Interlocked.CompareExchange(ref ticket.Claimed,1,0)!=0)
            {lock(gate){if(Object.ReferenceEquals(outstanding,ticket))outstanding=null;}return PracticeFixtureCommit.RejectedBeforeCommit;}
            lock(gate)
            {
                if(!Object.ReferenceEquals(outstanding,ticket))return PracticeFixtureCommit.RejectedBeforeCommit;
                outstanding=null;
                if(!OnOwner()){state=state==PracticeFixtureState.IoInFlight||state==PracticeFixtureState.WrittenUnflushed||state==PracticeFixtureState.FlushInFlight?PracticeFixtureState.Uncertain:PracticeFixtureState.Faulted;return PracticeFixtureCommit.RejectedBeforeCommit;}
                double now;var frame=ticket.Frame;
                if(!Object.ReferenceEquals(ticket.Issuer,this)||!Object.ReferenceEquals(ticket.Head,head)||!Object.ReferenceEquals(ticket.Lease,lease)||
                    !Object.ReferenceEquals(ticket.Writer,writer)||ticket.TaskGeneration!=taskGeneration||ticket.ConsentGeneration!=consentGeneration||ticket.SourceGeneration!=sourceGeneration||
                    !Active()||frame==null||!Object.ReferenceEquals(frame.Context,context)||!frame.RawUsable||frame.Sequence<=lastSequence||
                    !Finite(frame.Observed)||!ObserveNow(out now)||frame.Observed<=enabledAt||frame.Observed<=lastEye||frame.Observed>now||now-frame.Observed>maxAge)
                    return PracticeFixtureCommit.RejectedBeforeCommit;
                // Synthetic byte only. This is the model's linearization point;
                // it does not invoke a client, stream, worker, file or clock.
                memoryMarker=frame.SyntheticMarker;hasMemory=true;lastSequence=frame.Sequence;lastEye=frame.Observed;state=PracticeFixtureState.MemoryCommitted;
                return PracticeFixtureCommit.MemoryCommitted;
            }
        }
        internal bool BeginSyntheticWrite()
        {lock(gate){if(!OnOwner()||revoked||state!=PracticeFixtureState.MemoryCommitted||!hasMemory)return false;state=PracticeFixtureState.IoInFlight;sink.StartWrite();return true;}}
        internal void CompleteSyntheticWrite(PracticeFixtureIoResult result)
        {lock(gate){if(!OnOwner()||state!=PracticeFixtureState.IoInFlight)return;state=result==PracticeFixtureIoResult.Complete&&!revoked?PracticeFixtureState.WrittenUnflushed:PracticeFixtureState.Uncertain;}}
        internal bool BeginSyntheticFlush()
        {lock(gate){if(!OnOwner()||revoked||state!=PracticeFixtureState.WrittenUnflushed)return false;state=PracticeFixtureState.FlushInFlight;sink.StartFlush();return true;}}
        internal void CompleteSyntheticFlush(PracticeFixtureIoResult result)
        {lock(gate){if(!OnOwner()||state!=PracticeFixtureState.FlushInFlight)return;state=result==PracticeFixtureIoResult.Complete&&!revoked?PracticeFixtureState.SyntheticComplete:PracticeFixtureState.Uncertain;}}
    }
}
