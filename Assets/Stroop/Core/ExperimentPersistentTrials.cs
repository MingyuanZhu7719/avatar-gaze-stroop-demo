using System;
using System.Threading;

namespace AvatarStroop.Core
{
    // An explicit, owner-thread, cumulative development allocation estimate.
    // Reservation is never refunded within this epoch, including on failure:
    // immutable nodes can remain reachable from later or externally held heads.
    // This is not a process/GC memory limit or an apparatus readiness signal.
    public sealed class ExperimentPersistentMutationBudget
    {
        public const long MaximumLimitBytes=64L*1024*1024;
        readonly object sync=new object();readonly int ownerThread;readonly long limit;
        internal object Sync {get{return sync;}}
        long used;int terminal;string issue;
        public ExperimentPersistentMutationBudget(long estimatedBytesLimit)
        {
            if(estimatedBytesLimit<256||estimatedBytesLimit>MaximumLimitBytes)throw new ArgumentOutOfRangeException("estimatedBytesLimit");
            limit=estimatedBytesLimit;ownerThread=Thread.CurrentThread.ManagedThreadId;
        }
        public long EstimatedBytesUsed {get{lock(sync)return used;}}
        public long EstimatedBytesLimit {get{return limit;}}
        public bool Terminal {get{return Interlocked.CompareExchange(ref terminal,0,0)!=0;}}
        public string LastIssue {get{lock(sync)return issue;}}
        internal bool Fail(string reason)
        {lock(sync){if(!Terminal){issue=reason;Interlocked.Exchange(ref terminal,1);}return false;}}
        internal bool CheckOwner()
        {
            lock(sync)
            {if(Thread.CurrentThread.ManagedThreadId!=ownerThread)return Fail("owner_thread_violation");return !Terminal;}
        }
        internal bool TryReserve(long bytes)
        {
            lock(sync)
            {
                if(!CheckOwner())return false;
                if(bytes<0||bytes>limit-used)return Fail("estimated_budget_exhausted");
                used+=bytes;return true;
            }
        }
    }

    // Immutable structure-sharing storage only; it cannot establish source
    // provenance, authorize a task or replace an Experiment transaction.
    // No mutable TrialRecord/array is retained or exposed. Public operations
    // use the shared budget's owner thread and lock terminal on misuse.
    public sealed class ExperimentPersistentTrials
    {
        const int ChunkSize=32;
        readonly ExperimentPersistentMutationBudget budget;
        readonly string sessionId;readonly int maximumItems;
        readonly ExperimentMutationTrialSnapshot[][] chunks;
        public int Count {get;private set;}
        public bool Terminal {get{return budget.Terminal;}}
        public string LastIssue {get{return budget.LastIssue;}}
        public long EstimatedBytesUsed {get{return budget.EstimatedBytesUsed;}}
        public bool CanEnterCountdown {get{return false;}}
        public bool CanResume {get{return false;}}
        ExperimentPersistentTrials(string session,int maximumItems,ExperimentPersistentMutationBudget budget,
            ExperimentMutationTrialSnapshot[][] chunks,int count)
        {sessionId=session;this.maximumItems=maximumItems;this.budget=budget;this.chunks=chunks;Count=count;}
        public static ExperimentPersistentTrials Create(string sessionId,int maximumItems,ExperimentPersistentMutationBudget budget)
        {
            if(budget==null)throw new ArgumentNullException("budget");
            lock(budget.Sync)
            {
                if(!budget.CheckOwner())throw new InvalidOperationException("Development vector budget unavailable");
                try
                {
                    WireCodec.RequireIdentity(sessionId);
                    if(maximumItems<1||maximumItems>4096)throw new ArgumentException("Explicit bounded session and item count required");
                }
                catch(Exception){budget.Fail("vector_configuration_invalid");throw;}
                if(!budget.TryReserve(256))throw new InvalidOperationException("Development vector budget unavailable");
                try{return new ExperimentPersistentTrials(sessionId,maximumItems,budget,new ExperimentMutationTrialSnapshot[0][],0);}
                catch(Exception){budget.Fail("allocation_failed");throw;}
            }
        }
        // Reserve before constructing a root, chunk or head. 256 bytes/value
        // overestimates this snapshot's known value fields on current runtimes;
        // strings are shared immutable references, not copied source data.
        long UpdateEstimate {get{return checked(512L+((maximumItems+ChunkSize-1)/ChunkSize)*(long)IntPtr.Size+ChunkSize*256L);}}
        static bool SameIdentity(ExperimentMutationTrialSnapshot a,ExperimentMutationTrialSnapshot b)
        {return a.TrialId==b.TrialId&&a.SessionId==b.SessionId&&a.ListId==b.ListId&&a.IsPractice==b.IsPractice&&
            a.PracticeAttempt==b.PracticeAttempt&&a.BlockIndex==b.BlockIndex&&a.PlannedIndex==b.PlannedIndex&&a.Segment==b.Segment&&
            a.Condition==b.Condition&&a.Word==b.Word&&a.Ink==b.Ink;}
        bool Valid(ExperimentMutationTrialSnapshot value)
        {
            try{WireCodec.RequireIdentity(value.TrialId);WireCodec.RequireIdentity(value.ListId);}catch(ArgumentException){return false;}
            return value.SessionId==sessionId;
        }
        ExperimentMutationTrialSnapshot At(int index){return chunks[index/ChunkSize][index%ChunkSize];}
        public bool TryGet(int index,out ExperimentMutationTrialSnapshot value)
        {
            lock(budget.Sync)
            {
                value=default(ExperimentMutationTrialSnapshot);if(!budget.CheckOwner())return false;
                if(index<0||index>=Count)return budget.Fail("read_index_invalid");
                value=At(index);return true;
            }
        }
        public bool TryAppend(ExperimentMutationTrialSnapshot value,out ExperimentPersistentTrials next)
        {
            lock(budget.Sync)
            {
                next=null;if(!budget.CheckOwner())return false;
                if(Count>=maximumItems||!Valid(value))return budget.Fail("append_identity_or_capacity_invalid");
                for(int i=0;i<Count;i++)if(At(i).TrialId==value.TrialId)return budget.Fail("duplicate_trial_identity");
                if(!budget.TryReserve(UpdateEstimate))return false;
                try
                {
                    int chunk=Count/ChunkSize,slot=Count%ChunkSize;
                    var root=new ExperimentMutationTrialSnapshot[slot==0?chunks.Length+1:chunks.Length][];Array.Copy(chunks,root,chunks.Length);
                    root[chunk]=slot==0?new ExperimentMutationTrialSnapshot[ChunkSize]:(ExperimentMutationTrialSnapshot[])chunks[chunk].Clone();
                    root[chunk][slot]=value;next=new ExperimentPersistentTrials(sessionId,maximumItems,budget,root,Count+1);return true;
                }
                catch(Exception){return budget.Fail("allocation_failed");}
            }
        }
        public bool TryReplace(int index,ExperimentMutationTrialSnapshot value,out ExperimentPersistentTrials next)
        {
            lock(budget.Sync)
            {
                next=null;if(!budget.CheckOwner())return false;
                if(index<0||index>=Count||!Valid(value)||!SameIdentity(At(index),value))return budget.Fail("replace_identity_or_index_invalid");
                if(!budget.TryReserve(UpdateEstimate))return false;
                try
                {
                    int chunk=index/ChunkSize;var root=(ExperimentMutationTrialSnapshot[][])chunks.Clone();
                    root[chunk]=(ExperimentMutationTrialSnapshot[])chunks[chunk].Clone();root[chunk][index%ChunkSize]=value;
                    next=new ExperimentPersistentTrials(sessionId,maximumItems,budget,root,Count);return true;
                }
                catch(Exception){return budget.Fail("allocation_failed");}
            }
        }
        internal bool SharesChunk(ExperimentPersistentTrials other,int index)
        {return other!=null&&index>=0&&index<chunks.Length&&index<other.chunks.Length&&Object.ReferenceEquals(chunks[index],other.chunks[index]);}
    }
}
