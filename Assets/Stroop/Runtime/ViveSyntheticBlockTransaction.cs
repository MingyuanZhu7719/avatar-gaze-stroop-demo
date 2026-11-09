using System;
using System.Collections.Generic;
using System.Threading;
using AvatarStroop.Core;

namespace AvatarStroop.Runtime
{
    // An isolated model for deterministic tests only. It has no Experiment,
    // renderer, recorder, eye provider, clock, file, consent or product gate.
    // Its synthetic acknowledgements cannot authorize a real block or trial.
    internal sealed class ViveSyntheticBlockTransaction
    {
        internal enum StartResult { Waiting, Started, Stopped }
        internal enum EventKind { RenderProxy, Response, Interrupted }
        internal sealed class Item
        {
            internal readonly Colour Word, Ink;
            internal Item(Colour word,Colour ink){Word=word;Ink=ink;}
        }
        internal sealed class ModelEvent
        {
            internal readonly long Sequence; internal readonly int Index,Segment;
            internal readonly EventKind Kind;
            internal ModelEvent(long sequence,int index,int segment,EventKind kind)
            {Sequence=sequence;Index=index;Segment=segment;Kind=kind;}
        }
        internal sealed class SimulatedPlanAck
        {
            readonly ViveSyntheticBlockTransaction owner;internal int Consumed;
            internal SimulatedPlanAck(ViveSyntheticBlockTransaction value){owner=value;}
            internal bool BelongsTo(ViveSyntheticBlockTransaction value){return Object.ReferenceEquals(owner,value);}
        }
        readonly Thread owner=Thread.CurrentThread;
        readonly string session;readonly int block,attempt,capacity;readonly Item[] plan;
        readonly double deadline;readonly Queue<ModelEvent> queue=new Queue<ModelEvent>();
        readonly SimulatedPlanAck planAck;long nextSequence,confirmedThrough=-1;
        int state,index,segment;bool rendered;double lastStartCheck=-1; // 0 waiting, 1 active, 2 all enqueued, 3 stopped, 4 paused
        int revoke;internal Action AfterEnqueueForTest {get;set;}
        internal ViveSyntheticBlockTransaction(string session,int block,int attempt,Item[] items,int capacity,double deadline)
        {
            if(String.IsNullOrWhiteSpace(session)||items==null||items.Length==0||items.Length>24||capacity<1||capacity>256||
                Double.IsNaN(deadline)||Double.IsInfinity(deadline)||deadline<0||
                (block==-1?(attempt<1||attempt>2||items.Length!=8):(block<0||block>3||attempt!=0||items.Length!=24)))
                throw new ArgumentException("Invalid synthetic plan.");
            this.session=session;this.block=block;this.attempt=attempt;this.capacity=capacity;this.deadline=deadline;
            plan=new Item[items.Length];for(int i=0;i<items.Length;i++)
            {if(items[i]==null||!Enum.IsDefined(typeof(Colour),items[i].Word)||!Enum.IsDefined(typeof(Colour),items[i].Ink))throw new ArgumentException("Invalid synthetic item.");plan[i]=new Item(items[i].Word,items[i].Ink);}
            planAck=new SimulatedPlanAck(this);
        }
        internal bool ProductAuthority {get{return false;}}
        internal bool Durable {get{return false;}}
        internal string ActualValidity {get{return "UNKNOWN";}}
        internal string ActualVisibility {get{return "UNKNOWN";}}
        internal string EyeCoverage {get{return "UNKNOWN";}}
        internal bool IsStopped {get{return Volatile.Read(ref state)==3||Volatile.Read(ref revoke)!=0;}}
        // Only synthetic trial events have been enqueued. They may not be
        // acknowledged; this never means a session or recording is complete.
        internal bool AllTrialEventsEnqueued {get{return Volatile.Read(ref state)==2&&!IsStopped;}}
        internal int Index {get{return index;}} internal int Segment {get{return segment;}}
        internal int PendingCount {get{return queue.Count;}} internal long ConfirmedThrough {get{return confirmedThrough;}}
        internal SimulatedPlanAck AckForTest {get{return planAck;}}
        bool Owner(){return Object.ReferenceEquals(Thread.CurrentThread,owner);}
        bool Live(){return Volatile.Read(ref revoke)==0&&Volatile.Read(ref state)!=3;}
        internal void Withdraw(){Interlocked.Exchange(ref revoke,1);Interlocked.Exchange(ref state,3);}
        void Stop(){Interlocked.Exchange(ref state,3);}
        internal StartResult TryStart(SimulatedPlanAck ack,double now,bool deviceReady,bool anyHeld)
        {
            if(!Owner()||!Live()||state!=0||!Object.ReferenceEquals(ack,planAck)||!ack.BelongsTo(this)||
                Double.IsNaN(now)||Double.IsInfinity(now)||now<0||now<lastStartCheck||now>=deadline||!deviceReady)
            {Stop();return StartResult.Stopped;}
            lastStartCheck=now;
            if(anyHeld)return StartResult.Waiting;
            if(Interlocked.CompareExchange(ref ack.Consumed,1,0)!=0){Stop();return StartResult.Stopped;}
            if(!Live()||Interlocked.CompareExchange(ref state,1,0)!=0||!Live()){Stop();return StartResult.Stopped;}
            return StartResult.Started;
        }
        bool Enqueue(EventKind kind,int wantedIndex,int wantedSegment)
        {
            if(!Owner()||!Live()||state!=1||index>=plan.Length||wantedIndex!=index||wantedSegment!=segment||queue.Count>=capacity)
            {Stop();return false;}
            queue.Enqueue(new ModelEvent(nextSequence++,index,segment,kind));
            var hook=AfterEnqueueForTest;
            try{if(hook!=null)hook();}
            catch(Exception error) when(!(error is OutOfMemoryException||error is StackOverflowException||error is AccessViolationException))
            {Stop();return false;}
            if(!Live()){Stop();return false;}return true;
        }
        internal bool RenderProxy(int wantedIndex,int wantedSegment,Colour word,Colour ink)
        {
            if(!Owner()||!Live()||state!=1||rendered||index>=plan.Length||plan[index].Word!=word||plan[index].Ink!=ink)
            {Stop();return false;}
            if(!Enqueue(EventKind.RenderProxy,wantedIndex,wantedSegment))return false;
            rendered=true;return true;
        }
        internal bool Respond(int wantedIndex,int wantedSegment)
        {
            if(!rendered||!Enqueue(EventKind.Response,wantedIndex,wantedSegment)){Stop();return false;}
            rendered=false;index++;if(index==plan.Length&&Interlocked.CompareExchange(ref state,2,1)!=1){Stop();return false;}return Live();
        }
        internal bool Interrupt(int wantedIndex,int wantedSegment)
        {
            if(!Enqueue(EventKind.Interrupted,wantedIndex,wantedSegment))return false;
            rendered=false;index++;segment++;
            if(Interlocked.CompareExchange(ref state,index==plan.Length?2:4,1)!=1){Stop();return false;}return Live();
        }
        // A synthetic caller declares that a resume/countdown transition has
        // occurred. No numeric duration or real countdown is inferred here.
        internal bool TryResumeForTest(bool deviceReady,bool countdownObserved)
        {
            if(!Owner()||!Live()||state!=4||!deviceReady){Stop();return false;}
            if(!countdownObserved)return false;
            if(Interlocked.CompareExchange(ref state,1,4)!=4||!Live()){Stop();return false;}return true;
        }
        // Explicitly simulated FIFO acknowledgement. It never writes/flushed
        // bytes or proves that a renderer, participant, eye device or disk acted.
        internal bool AcknowledgeNextForTest(long expectedSequence)
        {
            if(!Owner()||!Live()||queue.Count==0||queue.Peek().Sequence!=expectedSequence)
            {Stop();return false;}
            queue.Dequeue();confirmedThrough=expectedSequence;return Live();
        }
        internal void DeviceLost(){Stop();}
        internal string Scope {get{return session+":"+block+":"+attempt;}}
    }
}
