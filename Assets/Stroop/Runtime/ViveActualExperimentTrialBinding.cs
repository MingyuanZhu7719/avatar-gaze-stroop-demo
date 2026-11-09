using System;
using System.Threading;
using AvatarStroop.Core;

namespace AvatarStroop.Runtime
{
    // Development-only binding to one actual observed simulation Experiment.
    // It consumes its immutable committed producer steps; it never re-reads a
    // live TrialRecord. It does not prove source consent, physical devices,
    // hardware timing, data completeness, or permit presentation/admission.
    public sealed class ViveActualExperimentTrialBinding
    {
        readonly Experiment experiment;readonly VivePersistentTrialTransactionCandidate staging;
        readonly object token=new object(),sync=new object();readonly int ownerThread,maximumItems;
        ExperimentMutationTrialSnapshot[] trials;ExperimentMutationSnapshot last;int count,terminal;long revision;bool caughtUp;string issue;
        // Diagnostics only; either value may lag the producer. TryGet performs
        // the current-head/owner check and never returns a partially staged row.
        public long Revision {get{lock(sync)return revision;}} public int Count {get{lock(sync)return count;}}
        public bool Terminal {get{lock(sync)return Interlocked.CompareExchange(ref terminal,0,0)!=0||experiment.MutationObservationFaulted||staging.Terminal;}}
        public string LastIssue {get{lock(sync)return issue??(experiment.MutationObservationFaulted?experiment.MutationObservationIssue:staging.LastIssue);}}
        public bool RealExperimentSourceBound {get{return true;}} public bool CanEnterCountdown {get{return false;}} public bool CanResume {get{return false;}}
        public ViveActualExperimentTrialBinding(Experiment source,int maximumItems,long budgetBytes)
        {
            if(source==null||!source.MutationObservationEnabled||source.MutationObservationFaulted||source.InitialMutationSnapshot==null||
                source.Configuration.RunKind!=RunKind.Simulation)throw new ArgumentException("An observed development Experiment is required");
            var initial=source.InitialMutationSnapshot;
            if(initial.Task.SessionId!=source.Configuration.SessionId||initial.TrialCount!=0||initial.Trials.Count!=0||
                initial.CurrentTrial.HasValue||initial.CurrentTrialIndex!=-1)throw new ArgumentException("A complete empty revision-zero baseline is required");
            staging=VivePersistentTrialTransactionCandidate.Create(source.Configuration.SessionId,maximumItems,budgetBytes,token);
            experiment=source;this.maximumItems=maximumItems;ownerThread=Thread.CurrentThread.ManagedThreadId;
            trials=new ExperimentMutationTrialSnapshot[maximumItems];last=initial;
        }
        bool Fail(string reason){issue=reason;Interlocked.Exchange(ref terminal,1);caughtUp=false;return false;}
        bool Owner(){if(Thread.CurrentThread.ManagedThreadId==ownerThread)return !Terminal;return Fail("owner_thread_violation");}
        static TrialRecord Detached(ExperimentMutationTrialSnapshot value)
        {
            // Reconstruct only from the immutable producer copy, never from
            // Experiment.CurrentTrial or other live mutable references.
            object key=new object();var record=new TrialRecord(key,value.TrialId,value.SessionId,value.ListId,value.IsPractice,value.PracticeAttempt,
                value.BlockIndex,value.PlannedIndex,value.Segment,value.Condition,new Stimulus(value.Word,value.Ink));
            if(value.Onset.HasValue)record.SetOnset(key,value.Onset.Value);
            if(value.ResponseAt.HasValue)record.SetResponseAt(key,value.ResponseAt.Value);
            if(value.Response.HasValue)record.SetResponse(key,value.Response.Value);
            if(value.Correct.HasValue)record.SetCorrect(key,value.Correct.Value);
            if(value.Outcome==TrialOutcome.Responded)record.SetResponded(key);
            else if(value.Outcome==TrialOutcome.Interrupted)record.SetInterrupted(key);
            else if(value.Outcome!=TrialOutcome.Pending)throw new InvalidOperationException("Unknown trial outcome");
            if(value.TechnicalIssue)record.SetTechnicalIssue(key);return record;
        }
        static bool ValidSnapshot(ExperimentMutationSnapshot state)
        {
            if(state==null||state.TrialCount<0||state.Trials==null||state.Trials.Count!=state.TrialCount||state.CurrentTrialIndex< -1||
                state.CurrentTrialIndex>=state.TrialCount)return false;
            if(!state.CurrentTrial.HasValue)return state.CurrentTrialIndex==-1;
            return state.CurrentTrialIndex==-1||state.CurrentTrial.Value.Equals(state.Trials[state.CurrentTrialIndex]);
        }
        bool StageStep(ExperimentMutationStep step,ExperimentMutationSnapshot prior,ExperimentMutationTrialSnapshot[] nextTrials,ref int nextCount,long tx)
        {
            if(step==null||!ValidSnapshot(step.Before)||!ValidSnapshot(step.After)||!step.Before.Same(prior))return false;
            var state=step.After;if(state.Task.SessionId!=experiment.Configuration.SessionId||state.TrialCount>maximumItems||
                state.TrialCount<nextCount||state.TrialCount>nextCount+1)return false;
            int changed=-1;
            for(int i=0;i<nextCount;i++)if(!nextTrials[i].Equals(state.Trials[i])){if(changed>=0)return false;changed=i;}
            if(state.TrialCount==nextCount+1)
            {
                if(changed>=0||state.CurrentTrialIndex!=nextCount||!state.CurrentTrial.HasValue||!state.CurrentTrial.Value.Equals(state.Trials[nextCount])||
                    !prior.CurrentTrial.HasValue||prior.CurrentTrialIndex!=-1||!prior.CurrentTrial.Value.Equals(state.CurrentTrial.Value))return false;
                var added=state.Trials[nextCount];if(!staging.TryAppend(tx,step.Index,nextCount,Detached(added),token))return false;
                nextTrials[nextCount++]=added;return true;
            }
            if(changed>=0)
            {
                if(state.CurrentTrialIndex!=changed||!state.CurrentTrial.HasValue||!state.CurrentTrial.Value.Equals(state.Trials[changed]))return false;
                var updated=state.Trials[changed];if(!staging.TryReplace(tx,step.Index,changed,Detached(updated),token))return false;
                nextTrials[changed]=updated;return true;
            }
            return staging.TryNoTrialChange(tx,step.Index,token);
        }
        public bool Advance()
        {
            lock(sync)
            {
                if(!Owner())return false;caughtUp=false;
                try
                {
                    ExperimentMutationCommit commit;ExperimentMutationSnapshot current;long head;
                    if(!experiment.TryReadMutationAfter(revision,out commit,out head,out current)||!ValidSnapshot(current)||head<revision)
                        return Fail("producer_unavailable");
                    if(commit==null)
                    {
                        if(head!=revision||!last.Same(current))return Fail("producer_head_mismatch");
                        caughtUp=experiment.IsMutationRevisionCurrent(revision);return caughtUp;
                    }
                    if(revision==Int64.MaxValue-1||commit.Revision!=revision+1||commit.Revision>head||commit.Steps==null||
                        !ValidSnapshot(commit.FinalState)||!staging.TryBegin(commit.Revision,commit.Steps.Count,token))return Fail("transaction_invalid");
                    var nextTrials=(ExperimentMutationTrialSnapshot[])trials.Clone();int nextCount=count,index=0;var prior=last;
                    foreach(var step in commit.Steps)
                    {
                        if(step==null||step.Index!=index++||!StageStep(step,prior,nextTrials,ref nextCount,commit.Revision))
                        {staging.TryAbort(token);return Fail("transaction_step_invalid");}
                        prior=step.After;
                    }
                    if(!prior.Same(commit.FinalState)||nextCount!=commit.FinalState.TrialCount||
                        (commit.Revision==head&&!commit.FinalState.Same(current)))
                    {staging.TryAbort(token);return Fail("transaction_final_mismatch");}
                    if(!staging.TryCommit(commit.Revision,token))return Fail("transaction_commit_failed");
                    trials=nextTrials;count=nextCount;last=commit.FinalState;revision=commit.Revision;
                    caughtUp=revision==head&&experiment.IsMutationRevisionCurrent(revision);return caughtUp;
                }
                catch(Exception){return Fail("binding_exception");}
            }
        }
        public bool TryGet(int index,out VivePersistentTrialValue value)
        {
            lock(sync)
            {
                value=default(VivePersistentTrialValue);if(!Owner()||!caughtUp||index<0||index>=count)return false;
                try
                {
                    if(!experiment.IsMutationRevisionCurrent(revision)){caughtUp=false;return false;}
                    long committed;int size;
                    if(!staging.TryGetStatus(out committed,out size)||committed!=revision||size!=count||!staging.TryGet(index,out value))
                        return Fail("committed_head_unavailable");
                    return true;
                }
                catch(Exception){return Fail("binding_read_failed");}
            }
        }
    }
}
