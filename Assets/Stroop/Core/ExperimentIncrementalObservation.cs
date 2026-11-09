using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;

namespace AvatarStroop.Core
{
    // Explicit isolated development option. It is not apparatus admission.
    public sealed class ExperimentIncrementalOptions
    {
        public int TransactionCapacity {get;private set;} public int MaximumTrials {get;private set;} public long EstimatedBytesLimit {get;private set;}
        public ExperimentIncrementalOptions(int transactionCapacity,int maxItems,long estimatedBytesLimit)
        {
            if(transactionCapacity<1||transactionCapacity>4096||maxItems<1||maxItems>4096||estimatedBytesLimit<256||estimatedBytesLimit>16L*1024*1024)
                throw new ArgumentException("Explicit bounded development observation options required");
            TransactionCapacity=transactionCapacity;MaximumTrials=maxItems;EstimatedBytesLimit=estimatedBytesLimit;
        }
    }
    public sealed class ExperimentIncrementalStep
    {
        public int Index {get;private set;} public ExperimentIncrementalSnapshot Before {get;private set;} public ExperimentIncrementalSnapshot After {get;private set;}
        internal ExperimentIncrementalStep(int index,ExperimentIncrementalSnapshot before,ExperimentIncrementalSnapshot after){Index=index;Before=before;After=after;}
    }
    public sealed class ExperimentIncrementalCommit
    {
        public long Revision {get;private set;} public string Operation {get;private set;}
        public IReadOnlyList<ExperimentIncrementalStep> Steps {get;private set;} public ExperimentIncrementalSnapshot FinalState {get;private set;}
        internal ExperimentIncrementalCommit(long revision,string operation,IReadOnlyList<ExperimentIncrementalStep> steps,ExperimentIncrementalSnapshot final)
        {Revision=revision;Operation=operation;Steps=steps;FinalState=final;}
    }
    internal sealed class ExperimentIncrementalJournal
    {
        readonly Experiment experiment;readonly ExperimentPersistentMutationBudget budget;readonly int owner,capacity;
        readonly Queue<ExperimentIncrementalCommit> pending;List<ExperimentIncrementalStep> steps;
        ExperimentIncrementalSnapshot committed,last;string operation;long revision,consumed;int terminal;bool active;
        internal ExperimentIncrementalSnapshot Initial {get;private set;}
        string issue;
        internal string Issue {get{lock(budget.Sync)return issue??budget.LastIssue;}}
        internal long EstimatedBytesUsed {get{return budget.EstimatedBytesUsed;}}
        internal bool Terminal {get{return Interlocked.CompareExchange(ref terminal,0,0)!=0||budget.Terminal;}}
        internal ExperimentIncrementalJournal(Experiment experiment,ExperimentIncrementalOptions options)
        {
            this.experiment=experiment;owner=Thread.CurrentThread.ManagedThreadId;capacity=options.TransactionCapacity;
            budget=new ExperimentPersistentMutationBudget(options.EstimatedBytesLimit);
            if(!budget.TryReserve(65536L+capacity*16L))throw new InvalidOperationException("Initial incremental budget unavailable");
            pending=new Queue<ExperimentIncrementalCommit>(capacity);
            var full=experiment.CaptureMutationSnapshot();int staticCount=experiment.IncrementalBaselineValueCount();var baseline=new object[staticCount];
            for(int i=0;i<staticCount;i++)baseline[i]=full.ValueAt(i);
            var vector=ExperimentPersistentTrials.Create(experiment.Configuration.SessionId,options.MaximumTrials,budget);
            Initial=committed=experiment.CaptureIncrementalSnapshot(null,baseline,vector,budget);
            if(committed==null)throw new InvalidOperationException("Initial incremental state unavailable");
        }
        void Fail(string reason){lock(budget.Sync){if(issue==null)issue=budget.LastIssue??reason;Interlocked.Exchange(ref terminal,1);active=false;steps=null;}}
        internal void Invalidate(){Fail("incremental_observation_failure");}
        bool Owner(){if(Thread.CurrentThread.ManagedThreadId==owner)return true;Fail("owner_thread_violation");return false;}
        internal void Begin(string name)
        {lock(budget.Sync)BeginCore(name);}
        void BeginCore(string name)
        {
            if(!Owner()||Terminal)return;if(active){Fail("unexpected_reentrancy");return;}
            try{if(!experiment.MatchesIncrementalSnapshot(committed)){Fail(budget.LastIssue??"unobserved_state_change");return;}active=true;operation=name;last=committed;steps=null;}
            catch(Exception){Fail("incremental_snapshot_failure");}
        }
        internal void Step()
        {lock(budget.Sync)StepCore();}
        void StepCore()
        {
            if(!Owner()||Terminal||!active)return;
            try
            {
                if(experiment.MatchesIncrementalSnapshot(last))return;
                if(budget.Terminal){Fail(budget.LastIssue??"incremental_capture_failed");return;}
                if(steps!=null&&steps.Count>=1024){Fail("transaction_step_limit");return;}
                if(!budget.TryReserve(2048)){Fail("estimated_budget_exhausted");return;}
                var next=experiment.CaptureIncrementalSnapshot(last,null,null,budget);
                if(next==null||budget.Terminal){Fail(budget.LastIssue??"incremental_capture_failed");return;}
                if(steps==null)steps=new List<ExperimentIncrementalStep>();
                steps.Add(new ExperimentIncrementalStep(steps.Count,last,next));last=next;
            }
            catch(Exception){Fail("incremental_snapshot_failure");}
        }
        internal void End(bool success)
        {lock(budget.Sync)EndCore(success);}
        void EndCore(bool success)
        {
            if(!Owner()||Terminal)return;if(!active){Fail("transaction_missing");return;}if(!success){Fail("operation_failed_or_incomplete");return;}
            Step();if(Terminal)return;if(steps==null||steps.Count==0){active=false;return;}
            if(revision==Int64.MaxValue-1||pending.Count>=capacity){Fail("revision_exhausted_or_backlog_full");return;}
            if(!budget.TryReserve(512L+steps.Count*16L)){Fail("estimated_budget_exhausted");return;}
            revision++;committed=last;pending.Enqueue(new ExperimentIncrementalCommit(revision,operation,new ReadOnlyCollection<ExperimentIncrementalStep>(new List<ExperimentIncrementalStep>(steps)),committed));
            active=false;steps=null;
        }
        internal bool ReadNext(long after,out ExperimentIncrementalCommit next,out long head,out ExperimentIncrementalSnapshot current)
        {lock(budget.Sync)return ReadNextCore(after,out next,out head,out current);}
        bool ReadNextCore(long after,out ExperimentIncrementalCommit next,out long head,out ExperimentIncrementalSnapshot current)
        {
            next=null;head=revision;current=null;if(!Owner()||Terminal)return false;if(active){Fail("read_during_transaction");return false;}
            try{if(!experiment.MatchesIncrementalSnapshot(committed)){Fail(budget.LastIssue??"unobserved_state_change");return false;}}
            catch(Exception){Fail("incremental_snapshot_failure");return false;}
            if(after!=consumed){Fail("reader_cursor_mismatch");return false;}
            if(pending.Count>0){next=pending.Dequeue();if(next.Revision!=consumed+1){Fail("revision_gap");next=null;return false;}consumed=next.Revision;}
            else if(consumed!=revision){Fail("revision_missing");return false;}
            head=revision;current=committed;return true;
        }
        internal bool IsCurrent(long requested)
        {lock(budget.Sync)return IsCurrentCore(requested);}
        bool IsCurrentCore(long requested)
        {
            if(!Owner()||Terminal||active||requested!=revision||consumed!=revision||pending.Count!=0)return false;
            try{if(!experiment.MatchesIncrementalSnapshot(committed)){Fail(budget.LastIssue??"unobserved_state_change");return false;}}
            catch(Exception){Fail("incremental_snapshot_failure");return false;}
            return true;
        }
    }
}
