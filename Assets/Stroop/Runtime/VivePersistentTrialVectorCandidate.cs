using System;
using System.Threading;
using AvatarStroop.Core;

namespace AvatarStroop.Runtime
{
    // A copied value, never a retained mutable TrialRecord. Existing engine and
    // recorder references are untouched. Strings and Stimulus enum values are
    // immutable; this object is development data, not an acquisition record.
    public readonly struct VivePersistentTrialValue : IEquatable<VivePersistentTrialValue>
    {
        public string TrialId {get;} public string SessionId {get;} public string ListId {get;}
        public bool IsPractice {get;} public int PracticeAttempt {get;} public int BlockIndex {get;} public int PlannedIndex {get;} public int Segment {get;}
        public Condition? Condition {get;} public Colour Word {get;} public Colour Ink {get;}
        public double? Onset {get;} public double? ResponseAt {get;} public Colour? Response {get;} public bool? Correct {get;}
        public TrialOutcome Outcome {get;} public bool TechnicalIssue {get;}
        internal VivePersistentTrialValue(TrialRecord source)
        {
            if(source==null||source.Stimulus==null)throw new ArgumentException("A trial with stimulus is required");
            TrialId=source.TrialId;SessionId=source.SessionId;ListId=source.ListId;IsPractice=source.IsPractice;PracticeAttempt=source.PracticeAttempt;
            BlockIndex=source.BlockIndex;PlannedIndex=source.PlannedIndex;Segment=source.Segment;Condition=source.Condition;Word=source.Stimulus.Word;Ink=source.Stimulus.Ink;
            Onset=source.Onset;ResponseAt=source.ResponseAt;Response=source.Response;Correct=source.Correct;Outcome=source.Outcome;TechnicalIssue=source.TechnicalIssue;
        }
        public bool Equals(VivePersistentTrialValue value)
        {return TrialId==value.TrialId&&SessionId==value.SessionId&&ListId==value.ListId&&IsPractice==value.IsPractice&&PracticeAttempt==value.PracticeAttempt&&
            BlockIndex==value.BlockIndex&&PlannedIndex==value.PlannedIndex&&Segment==value.Segment&&Condition==value.Condition&&Word==value.Word&&Ink==value.Ink&&
            SameTime(Onset,value.Onset)&&SameTime(ResponseAt,value.ResponseAt)&&Response==value.Response&&Correct==value.Correct&&Outcome==value.Outcome&&TechnicalIssue==value.TechnicalIssue;}
        // Preserve even unusual synthetic bits for differential tests. This is
        // not a declaration that non-finite source timestamps are valid.
        static bool SameTime(double? a,double? b)
        {return a.HasValue==b.HasValue&&(!a.HasValue||BitConverter.DoubleToInt64Bits(a.Value)==BitConverter.DoubleToInt64Bits(b.Value));}
        public override bool Equals(object value){return value is VivePersistentTrialValue&&Equals((VivePersistentTrialValue)value);}
        public override int GetHashCode(){return TrialId==null?0:TrialId.GetHashCode();}
    }

    // Structural-sharing prototype only. A 32-element chunk and a short root
    // array are copied per append/replacement. The owner-thread budget is
    // cumulative for the epoch and is never refunded when a version is dropped,
    // so aliasing between older and newer versions cannot undercount nodes.
    // Estimates exclude the Experiment's existing records/strings, caller-held
    // memory, GC delay and the rest of the process. No I/O or device is touched.
    public sealed class VivePersistentTrialVectorCandidate
    {
        const int ChunkSize=32;
        sealed class Budget
        {
            readonly int owner=Thread.CurrentThread.ManagedThreadId;readonly long limit;long used;int terminal;
            internal string Issue;
            internal Budget(long limit){this.limit=limit;}
            internal bool Terminal {get{return Interlocked.CompareExchange(ref terminal,0,0)!=0;}}
            internal long Used {get{return used;}} internal long Limit {get{return limit;}}
            internal bool Reserve(long amount)
            {
                if(Thread.CurrentThread.ManagedThreadId!=owner){Issue="owner_thread_violation";Interlocked.Exchange(ref terminal,1);return false;}
                if(Terminal)return false;
                if(amount<0||amount>limit-used){Issue="estimated_budget_exhausted";Interlocked.Exchange(ref terminal,1);return false;}
                used+=amount;return true;
            }
            internal void AllocationFailed(){Issue="allocation_failed";Interlocked.Exchange(ref terminal,1);}
        }
        readonly Budget budget;readonly VivePersistentTrialValue[][] chunks;readonly int maximumItems;
        public int Count {get;private set;}
        public long EstimatedBytesUsed {get{return budget.Used;}} public long EstimatedBytesLimit {get{return budget.Limit;}}
        public bool Terminal {get{return budget.Terminal;}} public string LastIssue {get{return budget.Issue;}}
        public bool RealExperimentSourceBound {get{return false;}} public bool CanEnterCountdown {get{return false;}} public bool CanResume {get{return false;}}
        VivePersistentTrialVectorCandidate(Budget budget,int maximumItems,VivePersistentTrialValue[][] chunks,int count)
        {this.budget=budget;this.maximumItems=maximumItems;this.chunks=chunks;Count=count;}
        // Explicit engineering-only bounds, unrelated to experiment parameters.
        public static VivePersistentTrialVectorCandidate Create(int maximumItems,long estimatedBytesLimit)
        {
            if(maximumItems<1||maximumItems>4096||estimatedBytesLimit<256)throw new ArgumentException("Explicit bounded development vector and budget required");
            var budget=new Budget(estimatedBytesLimit);if(!budget.Reserve(256))throw new InvalidOperationException("Insufficient initial budget");
            return new VivePersistentTrialVectorCandidate(budget,maximumItems,new VivePersistentTrialValue[0][],0);
        }
        // Reserves a conservative upper bound before either array is allocated.
        // The 256-byte-per-value estimate intentionally exceeds this value's
        // known fields on current managed runtimes; it is not an OS heap limit.
        long NextEstimate {get{return checked(512L+((maximumItems+ChunkSize-1)/ChunkSize)*(long)IntPtr.Size+ChunkSize*256L);}}
        public VivePersistentTrialValue Get(int index)
        {if(index<0||index>=Count)throw new ArgumentOutOfRangeException("index");return chunks[index/ChunkSize][index%ChunkSize];}
        public bool TryAppend(TrialRecord source,out VivePersistentTrialVectorCandidate result)
        {
            result=null;if(source==null||source.Stimulus==null)throw new ArgumentException("A trial with stimulus is required");
            if(Count>=maximumItems)throw new InvalidOperationException("Vector item limit reached");
            if(!budget.Reserve(NextEstimate))return false;
            try
            {
                int chunkIndex=Count/ChunkSize,slot=Count%ChunkSize;
                var root=new VivePersistentTrialValue[slot==0?chunks.Length+1:chunks.Length][];Array.Copy(chunks,root,chunks.Length);
                root[chunkIndex]=slot==0?new VivePersistentTrialValue[ChunkSize]:(VivePersistentTrialValue[])chunks[chunkIndex].Clone();
                root[chunkIndex][slot]=new VivePersistentTrialValue(source);
                result=new VivePersistentTrialVectorCandidate(budget,maximumItems,root,Count+1);return true;
            }
            catch(Exception){budget.AllocationFailed();throw;}
        }
        public bool TryReplace(int index,TrialRecord source,out VivePersistentTrialVectorCandidate result)
        {
            result=null;if(source==null||source.Stimulus==null)throw new ArgumentException("A trial with stimulus is required");
            if(index<0||index>=Count)throw new ArgumentOutOfRangeException("index");
            if(!budget.Reserve(NextEstimate))return false;
            try
            {
                int chunkIndex=index/ChunkSize;var root=(VivePersistentTrialValue[][])chunks.Clone();
                root[chunkIndex]=(VivePersistentTrialValue[])chunks[chunkIndex].Clone();root[chunkIndex][index%ChunkSize]=new VivePersistentTrialValue(source);
                result=new VivePersistentTrialVectorCandidate(budget,maximumItems,root,Count);return true;
            }
            catch(Exception){budget.AllocationFailed();throw;}
        }
        internal bool SharesChunk(VivePersistentTrialVectorCandidate other,int chunk)
        {return other!=null&&chunk>=0&&chunk<chunks.Length&&chunk<other.chunks.Length&&Object.ReferenceEquals(chunks[chunk],other.chunks[chunk]);}
    }
}
