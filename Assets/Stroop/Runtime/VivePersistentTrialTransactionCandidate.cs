using System;
using System.Threading;
using AvatarStroop.Core;

namespace AvatarStroop.Runtime
{
    // Isolated synthetic staging only. The token/session and TrialRecord are
    // caller claims, not proof of an Experiment, device or atomic source read.
    // No staged data is exposed, no I/O occurs, and no gate can be opened.
    public sealed class VivePersistentTrialTransactionCandidate
    {
        const long MaximumEstimatedBytes=16L*1024*1024;
        readonly string sessionId;readonly object sourceToken,sync=new object();readonly int ownerThread;
        VivePersistentTrialVectorCandidate committed,staged;
        long revision,activeRevision;int expectedSteps,nextStep,terminal;string issue;bool active;
        // Diagnostic scalars may be read on any thread and are individually
        // synchronized; they are not readiness evidence. Callers needing a
        // corresponding usable pair must use owner-thread TryGetStatus.
        public long Revision {get{lock(sync)return revision;}} public int Count {get{lock(sync)return committed.Count;}}
        public bool Terminal {get{lock(sync)return Interlocked.CompareExchange(ref terminal,0,0)!=0||committed.Terminal;}}
        public string LastIssue {get{lock(sync)return issue??committed.LastIssue;}}
        public bool RealExperimentSourceBound {get{return false;}} public bool CanEnterCountdown {get{return false;}} public bool CanResume {get{return false;}}
        VivePersistentTrialTransactionCandidate(string session,int maximumItems,long budgetBytes,object token)
        {sessionId=session;sourceToken=token;ownerThread=Thread.CurrentThread.ManagedThreadId;committed=VivePersistentTrialVectorCandidate.Create(maximumItems,budgetBytes);}
        public static VivePersistentTrialTransactionCandidate Create(string session,int maximumItems,long budgetBytes,object token)
        {
            WireCodec.RequireIdentity(session);
            if(token==null||budgetBytes<256||budgetBytes>MaximumEstimatedBytes)throw new ArgumentException("Explicit bounded synthetic source and budget required");
            return new VivePersistentTrialTransactionCandidate(session,maximumItems,budgetBytes,token);
        }
        bool Fail(string reason){issue=reason;Interlocked.Exchange(ref terminal,1);active=false;staged=null;return false;}
        bool Owner(){if(Thread.CurrentThread.ManagedThreadId==ownerThread)return !Terminal;return Fail("owner_thread_violation");}
        bool Token(object value){return Object.ReferenceEquals(sourceToken,value);}
        bool Current(long tx,object token)
        {return active&&Token(token)&&tx==activeRevision;}
        bool NextStep(long tx,int step,object token)
        {return Current(tx,token)&&step==nextStep&&nextStep<expectedSteps;}
        static bool Finite(double? value)
        {return !value.HasValue||(!Double.IsNaN(value.Value)&&!Double.IsInfinity(value.Value)&&value.Value>=0);}
        bool Valid(VivePersistentTrialValue value)
        {
            try{WireCodec.RequireIdentity(value.TrialId);WireCodec.RequireIdentity(value.ListId);}catch(ArgumentException){return false;}
            return value.SessionId==sessionId&&(int)value.Word>=(int)Colour.Red&&(int)value.Word<=(int)Colour.Yellow&&
                (int)value.Ink>=(int)Colour.Red&&(int)value.Ink<=(int)Colour.Yellow&&(!value.Response.HasValue||Enum.IsDefined(typeof(Colour),value.Response.Value))&&
                (!value.Condition.HasValue||Enum.IsDefined(typeof(Condition),value.Condition.Value))&&Enum.IsDefined(typeof(TrialOutcome),value.Outcome)&&
                Finite(value.Onset)&&Finite(value.ResponseAt)&&(!value.Onset.HasValue||!value.ResponseAt.HasValue||value.ResponseAt.Value>=value.Onset.Value)&&
                (value.Outcome!=TrialOutcome.Responded||(value.Onset.HasValue&&value.ResponseAt.HasValue&&value.Response.HasValue&&value.Correct.HasValue&&
                    value.Correct.Value==(value.Response.Value==value.Ink)));
        }
        static bool SameIdentity(VivePersistentTrialValue a,VivePersistentTrialValue b)
        {return a.TrialId==b.TrialId&&a.SessionId==b.SessionId&&a.ListId==b.ListId&&a.IsPractice==b.IsPractice&&a.PracticeAttempt==b.PracticeAttempt&&
            a.BlockIndex==b.BlockIndex&&a.PlannedIndex==b.PlannedIndex&&a.Segment==b.Segment&&a.Condition==b.Condition&&a.Word==b.Word&&a.Ink==b.Ink;}
        static bool SameTime(double? a,double? b)
        {return a.HasValue==b.HasValue&&(!a.HasValue||BitConverter.DoubleToInt64Bits(a.Value)==BitConverter.DoubleToInt64Bits(b.Value));}
        static bool Advances(VivePersistentTrialValue before,VivePersistentTrialValue after)
        {
            if(before.Onset.HasValue&&!SameTime(before.Onset,after.Onset))return false;
            if(before.ResponseAt.HasValue&&!SameTime(before.ResponseAt,after.ResponseAt))return false;
            if(before.Response.HasValue&&before.Response!=after.Response)return false;
            if(before.Correct.HasValue&&before.Correct!=after.Correct)return false;
            if(before.TechnicalIssue&&!after.TechnicalIssue)return false;
            return before.Outcome==TrialOutcome.Pending||before.Outcome==after.Outcome;
        }
        public bool TryBegin(long txRevision,int stepCount,object token)
        {
            lock(sync)
            {
                if(!Owner())return false;
                if(active||!Token(token)||revision>=Int64.MaxValue-1||txRevision!=revision+1||stepCount<0||stepCount>1024)
                    return Fail("transaction_begin_invalid");
                active=true;activeRevision=txRevision;expectedSteps=stepCount;nextStep=0;staged=committed;return true;
            }
        }
        public bool TryAppend(long txRevision,int stepIndex,int index,TrialRecord record,object token)
        {
            lock(sync)
            {
                if(!Owner())return false;
                if(!NextStep(txRevision,stepIndex,token)||record==null||index!=staged.Count)return Fail("append_context_invalid");
                try
                {
                    VivePersistentTrialVectorCandidate candidate;if(!staged.TryAppend(record,out candidate))return Fail("vector_unavailable");
                    var value=candidate.Get(index);if(!Valid(value))return Fail("trial_invalid");
                    for(int i=0;i<staged.Count;i++)if(staged.Get(i).TrialId==value.TrialId)return Fail("duplicate_trial_identity");
                    staged=candidate;nextStep++;return true;
                }
                catch(Exception){return Fail("append_failed");}
            }
        }
        public bool TryReplace(long txRevision,int stepIndex,int index,TrialRecord record,object token)
        {
            lock(sync)
            {
                if(!Owner())return false;
                if(!NextStep(txRevision,stepIndex,token)||record==null||index<0||index>=staged.Count)return Fail("replace_context_invalid");
                try
                {
                    var before=staged.Get(index);VivePersistentTrialVectorCandidate candidate;if(!staged.TryReplace(index,record,out candidate))return Fail("vector_unavailable");
                    var after=candidate.Get(index);if(!Valid(after)||!SameIdentity(before,after)||!Advances(before,after))return Fail("trial_identity_or_state_regressed");
                    staged=candidate;nextStep++;return true;
                }
                catch(Exception){return Fail("replace_failed");}
            }
        }
        // A producer transaction may change phase, source or logs without
        // changing a trial. Such a step still consumes its exact ordinal.
        public bool TryNoTrialChange(long txRevision,int stepIndex,object token)
        {
            lock(sync)
            {
                if(!Owner())return false;
                if(!NextStep(txRevision,stepIndex,token))return Fail("no_trial_change_context_invalid");
                nextStep++;return true;
            }
        }
        public bool TryCommit(long txRevision,object token)
        {
            lock(sync)
            {
                if(!Owner())return false;
                if(!Current(txRevision,token)||nextStep!=expectedSteps||staged==null||staged.Terminal)return Fail("transaction_incomplete");
                committed=staged;staged=null;revision=txRevision;active=false;return true;
            }
        }
        public bool TryAbort(object token)
        {
            lock(sync)
            {
                if(!Owner())return false;
                if(!active||!Token(token))return Fail("abort_context_invalid");
                Fail("transaction_aborted");return true;
            }
        }
        // An active or failed transaction never returns a usable status pair.
        public bool TryGetStatus(out long committedRevision,out int committedCount)
        {
            lock(sync)
            {
                committedRevision=-1;committedCount=-1;if(!Owner()||active)return false;
                committedRevision=revision;committedCount=committed.Count;return true;
            }
        }
        // During staging even the old head is unavailable; this avoids a stale
        // committed value being mistaken for the active transaction's state.
        public bool TryGet(int index,out VivePersistentTrialValue value)
        {
            lock(sync)
            {
                value=default(VivePersistentTrialValue);if(!Owner()||active)return false;
                if(index<0||index>=committed.Count)return Fail("read_index_invalid");
                try{value=committed.Get(index);return true;}catch(Exception){return Fail("read_failed");}
            }
        }
    }
}
