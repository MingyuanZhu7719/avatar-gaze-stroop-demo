using System;
using System.Threading;
using AvatarStroop.Core;

namespace AvatarStroop.Runtime
{
    // Isolated synthetic candidate only: the session and TrialRecord are caller
    // claims, not evidence of the owning Experiment or atomic capture. This is
    // deliberately not an IViveWozSourceStateReader and cannot admit a task.
    public sealed class VivePersistentTrialMirrorCandidate
    {
        const long MaximumEstimatedBytes=16L*1024*1024;
        readonly string sessionId;readonly int ownerThread;readonly object sync=new object();VivePersistentTrialVectorCandidate head;
        long revision;int terminal;string issue;
        // This is one increment per synthetic append/replace, NOT the
        // Experiment journal's transaction revision (which may have steps).
        public long Revision {get{return revision;}} public int Count {get{return head.Count;}}
        public bool Terminal {get{return Interlocked.CompareExchange(ref terminal,0,0)!=0||head.Terminal;}}
        public string LastIssue {get{return issue??head.LastIssue;}}
        public bool RealExperimentSourceBound {get{return false;}} public bool CanEnterCountdown {get{return false;}} public bool CanResume {get{return false;}}
        VivePersistentTrialMirrorCandidate(string session,int maximumItems,long budgetBytes)
        {sessionId=session;ownerThread=Thread.CurrentThread.ManagedThreadId;head=VivePersistentTrialVectorCandidate.Create(maximumItems,budgetBytes);}
        public static VivePersistentTrialMirrorCandidate Create(string session,int maximumItems,long budgetBytes)
        {
            WireCodec.RequireIdentity(session);
            if(budgetBytes<256||budgetBytes>MaximumEstimatedBytes)throw new ArgumentOutOfRangeException("budgetBytes");
            return new VivePersistentTrialMirrorCandidate(session,maximumItems,budgetBytes);
        }
        bool Fail(string reason){issue=reason;Interlocked.Exchange(ref terminal,1);return false;}
        bool Owner()
        {if(Thread.CurrentThread.ManagedThreadId==ownerThread)return !Terminal;return Fail("owner_thread_violation");}
        bool Next(long proposed)
        {return revision<Int64.MaxValue-1&&proposed==revision+1;}
        static bool Finite(double? value)
        {return !value.HasValue||(!Double.IsNaN(value.Value)&&!Double.IsInfinity(value.Value)&&value.Value>=0);}
        bool Valid(VivePersistentTrialValue value)
        {
            try{WireCodec.RequireIdentity(value.TrialId);WireCodec.RequireIdentity(value.ListId);}catch(ArgumentException){return false;}
            return value.SessionId==sessionId&&(int)value.Word>=(int)Colour.Red&&(int)value.Word<=(int)Colour.Yellow&&
            (int)value.Ink>=(int)Colour.Red&&(int)value.Ink<=(int)Colour.Yellow&&(!value.Response.HasValue||Enum.IsDefined(typeof(Colour),value.Response.Value))&&
            (!value.Condition.HasValue||Enum.IsDefined(typeof(Condition),value.Condition.Value))&&Enum.IsDefined(typeof(TrialOutcome),value.Outcome)&&Finite(value.Onset)&&Finite(value.ResponseAt)&&
            (!value.Onset.HasValue||!value.ResponseAt.HasValue||value.ResponseAt.Value>=value.Onset.Value)&&
            (value.Outcome!=TrialOutcome.Responded||(value.Onset.HasValue&&value.ResponseAt.HasValue&&value.Response.HasValue&&value.Correct.HasValue&&
                value.Correct.Value==(value.Response.Value==value.Ink)));}
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
            if(before.Outcome!=TrialOutcome.Pending&&before.Outcome!=after.Outcome)return false;
            return true;
        }
        public bool TryAppend(long proposedRevision,int index,TrialRecord record)
        {lock(sync)return TryAppendCore(proposedRevision,index,record);}
        bool TryAppendCore(long proposedRevision,int index,TrialRecord record)
        {
            if(!Owner())return false;
            if(!Next(proposedRevision)||index!=head.Count||record==null)return Fail("append_revision_index_or_source_invalid");
            try
            {
                VivePersistentTrialVectorCandidate candidate;
                if(!head.TryAppend(record,out candidate))return Fail("vector_unavailable");
                var value=candidate.Get(index);if(!Valid(value))return Fail("trial_value_invalid");
                for(int i=0;i<head.Count;i++)if(head.Get(i).TrialId==value.TrialId)return Fail("duplicate_trial_identity");
                head=candidate;revision=proposedRevision;return true;
            }
            catch(Exception){return Fail("append_failed");}
        }
        public bool TryReplace(long proposedRevision,int index,TrialRecord record)
        {lock(sync)return TryReplaceCore(proposedRevision,index,record);}
        bool TryReplaceCore(long proposedRevision,int index,TrialRecord record)
        {
            if(!Owner())return false;
            if(!Next(proposedRevision)||index<0||index>=head.Count||record==null)return Fail("replace_revision_index_or_source_invalid");
            try
            {
                var before=head.Get(index);VivePersistentTrialVectorCandidate candidate;
                if(!head.TryReplace(index,record,out candidate))return Fail("vector_unavailable");
                var after=candidate.Get(index);
                if(!Valid(after)||!SameIdentity(before,after)||!Advances(before,after))return Fail("trial_identity_or_state_regressed");
                head=candidate;revision=proposedRevision;return true;
            }
            catch(Exception){return Fail("replace_failed");}
        }
        // No stale/old version is exposed after failure or on a foreign thread.
        public bool TryGet(int index,out VivePersistentTrialValue value)
        {lock(sync)return TryGetCore(index,out value);}
        bool TryGetCore(int index,out VivePersistentTrialValue value)
        {
            value=default(VivePersistentTrialValue);if(!Owner())return false;
            if(index<0||index>=head.Count)return Fail("read_index_invalid");
            try{value=head.Get(index);return true;}catch(Exception){return Fail("read_failed");}
        }
    }
}
