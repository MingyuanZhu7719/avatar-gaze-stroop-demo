using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;

namespace AvatarStroop.Core
{
    // Opt-in development evidence. These immutable copies use no IClock and do
    // not authenticate a device, establish source consent or admit a study.
    public sealed class ExperimentMutationSnapshot
    {
        readonly object[] values;
        public TaskSnapshot Task {get;private set;}
        public ControlSelection? Selection {get;private set;}
        public string SourceOwner {get;private set;}
        public long SourceConfirmationSequence {get;private set;}
        public Phase? PausedFrom {get;private set;}
        public int TrialCount {get;private set;} public int CurrentTrialIndex {get;private set;}
        public ExperimentMutationTrialSnapshot? CurrentTrial {get;private set;}
        public IReadOnlyList<ExperimentMutationTrialSnapshot> Trials {get;private set;}
        internal ExperimentMutationSnapshot(TaskSnapshot task,ControlSelection? selection,string owner,long confirmation,Phase? pausedFrom,object[] values)
            :this(task,selection,owner,confirmation,pausedFrom,values,-1,-1,null,new ExperimentMutationTrialSnapshot[0]){}
        internal ExperimentMutationSnapshot(TaskSnapshot task,ControlSelection? selection,string owner,long confirmation,Phase? pausedFrom,object[] values,
            int trialCount,int currentTrialIndex,ExperimentMutationTrialSnapshot? currentTrial,ExperimentMutationTrialSnapshot[] trials)
        {Task=task;Selection=selection;SourceOwner=owner;SourceConfirmationSequence=confirmation;PausedFrom=pausedFrom;this.values=values;
            TrialCount=trialCount;CurrentTrialIndex=currentTrialIndex;CurrentTrial=currentTrial;Trials=Array.AsReadOnly(trials);}
        internal int ValueCount {get{return values.Length;}}
        internal object ValueAt(int index){return values[index];}
        internal bool Same(ExperimentMutationSnapshot other)
        {
            if(other==null||values.Length!=other.values.Length)return false;
            for(int i=0;i<values.Length;i++)
            {
                if(values[i] is double&&other.values[i] is double)
                {if(BitConverter.DoubleToInt64Bits((double)values[i])!=BitConverter.DoubleToInt64Bits((double)other.values[i]))return false;}
                else if(!Object.Equals(values[i],other.values[i]))return false;
            }
            return true;
        }
    }
    public sealed class ExperimentMutationStep
    {
        public int Index {get;private set;} public ExperimentMutationSnapshot Before {get;private set;} public ExperimentMutationSnapshot After {get;private set;}
        internal ExperimentMutationStep(int index,ExperimentMutationSnapshot before,ExperimentMutationSnapshot after){Index=index;Before=before;After=after;}
    }
    public sealed class ExperimentMutationCommit
    {
        public long Revision {get;private set;} public string Operation {get;private set;}
        public IReadOnlyList<ExperimentMutationStep> Steps {get;private set;} public ExperimentMutationSnapshot FinalState {get;private set;}
        internal long EstimatedBytes {get;private set;}
        internal ExperimentMutationCommit(long revision,string operation,IReadOnlyList<ExperimentMutationStep> steps,ExperimentMutationSnapshot finalState,long estimatedBytes)
        {Revision=revision;Operation=operation;Steps=steps;FinalState=finalState;EstimatedBytes=estimatedBytes;}
    }
    // The producer has one destructive owner-thread reader. If it falls behind,
    // cannot commit, reenters unexpectedly, or is touched across threads, the
    // entire stream becomes terminal instead of inventing complete evidence.
    internal sealed class ExperimentMutationJournal
    {
        // Conservative estimate bounds this producer's retained snapshot
        // payload; callers retaining returned commits own their memory. It is
        // not a guarantee about total managed heap or process memory.
        internal const long SnapshotBudgetBytes=16L*1024*1024;
        readonly Experiment experiment;readonly int ownerThread,capacity;readonly object sync=new object();
        readonly Queue<ExperimentMutationCommit> pending=new Queue<ExperimentMutationCommit>();
        List<ExperimentMutationStep> steps;ExperimentMutationSnapshot committed,lastStep;string operation;
        long revision,consumed,pendingBytes,activeBytes,initialBytes;int terminal;bool active;
        internal string Issue {get;private set;}
        internal bool Terminal {get{return Interlocked.CompareExchange(ref terminal,0,0)!=0;}}
        internal long Revision {get{return revision;}}
        internal ExperimentMutationSnapshot Initial {get;private set;}
        internal ExperimentMutationJournal(Experiment experiment,int capacity)
        {
            if(experiment==null||capacity<1||capacity>4096)throw new ArgumentException("Explicit bounded mutation journal capacity required");
            this.experiment=experiment;this.capacity=capacity;ownerThread=Thread.CurrentThread.ManagedThreadId;
            initialBytes=experiment.EstimateMutationSnapshotBytes();
            if(initialBytes>SnapshotBudgetBytes/2)throw new InvalidOperationException("Initial mutation snapshot exceeds development budget");
            Initial=committed=experiment.CaptureMutationSnapshot();
        }
        void Fail(string issue){lock(sync){Interlocked.Exchange(ref terminal,1);Issue=issue;active=false;steps=null;}}
        internal void Invalidate(){Fail("observation_failure");}
        bool Owner(){if(Thread.CurrentThread.ManagedThreadId==ownerThread)return true;Fail("owner_thread_violation");return false;}
        internal void Begin(string name)
        {lock(sync)BeginCore(name);}
        void BeginCore(string name)
        {
            if(!Owner()||Terminal)return;
            if(active){Fail("unexpected_reentrancy");return;}
            try
            {
                if(!experiment.MatchesMutationSnapshot(committed)){Fail("unobserved_state_change");return;}
                active=true;operation=name;steps=new List<ExperimentMutationStep>();lastStep=committed;activeBytes=0;
            }
            catch(Exception){Fail("snapshot_failure");}
        }
        internal void Step()
        {lock(sync)StepCore();}
        void StepCore()
        {
            if(!Owner()||Terminal||!active)return;
            try
            {
                if(experiment.MatchesMutationSnapshot(lastStep))return;
                if(steps.Count>=1024){Fail("transaction_step_limit");return;}
                long estimate=experiment.EstimateMutationSnapshotBytes();
                long reserve=2*Math.Max(initialBytes,estimate);
                if(reserve>SnapshotBudgetBytes||pendingBytes>SnapshotBudgetBytes-reserve||activeBytes>SnapshotBudgetBytes-reserve-pendingBytes||estimate>SnapshotBudgetBytes-reserve-pendingBytes-activeBytes)
                {Fail("snapshot_budget_exhausted");return;}
                var now=experiment.CaptureMutationSnapshot();
                steps.Add(new ExperimentMutationStep(steps.Count,lastStep,now));lastStep=now;activeBytes+=estimate;
            }
            catch(Exception){Fail("snapshot_failure");}
        }
        internal void End(bool succeeded)
        {lock(sync)EndCore(succeeded);}
        void EndCore(bool succeeded)
        {
            if(!Owner()||Terminal)return;
            if(!active){Fail("transaction_missing");return;}
            if(!succeeded){Fail("operation_failed_or_incomplete");return;}
            Step();if(Terminal)return;
            if(steps.Count==0){active=false;return;}
            if(revision==Int64.MaxValue-1||pending.Count>=capacity){Fail("revision_exhausted_or_backlog_full");return;}
            revision++;
            var immutable=new ReadOnlyCollection<ExperimentMutationStep>(new List<ExperimentMutationStep>(steps));
            committed=lastStep;pending.Enqueue(new ExperimentMutationCommit(revision,operation,immutable,committed,activeBytes));pendingBytes+=activeBytes;
            active=false;steps=null;
        }
        internal bool ReadNext(long after,out ExperimentMutationCommit next,out long head,out ExperimentMutationSnapshot current)
        {lock(sync)return ReadNextCore(after,out next,out head,out current);}
        bool ReadNextCore(long after,out ExperimentMutationCommit next,out long head,out ExperimentMutationSnapshot current)
        {
            next=null;head=revision;current=null;
            if(!Owner()||Terminal)return false;
            if(active){Fail("read_during_transaction");return false;}
            try{if(!experiment.MatchesMutationSnapshot(committed)){Fail("unobserved_state_change");return false;}}
            catch(Exception){Fail("snapshot_failure");return false;}
            if(after!=consumed){Fail("reader_cursor_mismatch");return false;}
            if(pending.Count>0)
            {
                next=pending.Dequeue();pendingBytes-=next.EstimatedBytes;if(next.Revision!=consumed+1||pendingBytes<0){Fail("revision_gap");next=null;return false;}consumed=next.Revision;
            }
            else if(consumed!=revision){Fail("revision_missing");return false;}
            head=revision;current=committed;return true;
        }
        internal bool IsCurrent(long requested)
        {lock(sync)return IsCurrentCore(requested);}
        bool IsCurrentCore(long requested)
        {
            if(!Owner()||Terminal||active||requested!=revision||consumed!=revision||pending.Count!=0)return false;
            try{if(!experiment.MatchesMutationSnapshot(committed)){Fail("unobserved_state_change");return false;}}
            catch(Exception){Fail("snapshot_failure");return false;}
            return true;
        }
    }
}
