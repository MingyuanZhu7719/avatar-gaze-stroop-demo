using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using AvatarStroop.Core;

namespace AvatarStroop.Runtime
{
    public enum ViveSyntheticChoiceState { AwaitingOwner, ActiveCandidate, Revoked, Ended }
    // A synthetic observation trace, not an Experiment JournalEvent.
    public sealed class ViveSyntheticStepTrace
    {
        public long Revision {get;private set;} public int Index {get;private set;}
        public ExperimentIncrementalSnapshot Before {get;private set;} public ExperimentIncrementalSnapshot After {get;private set;}
        internal ViveSyntheticStepTrace(long revision,int index,ExperimentIncrementalSnapshot before,ExperimentIncrementalSnapshot after)
        {Revision=revision;Index=index;Before=before;After=after;}
    }
    // An immutable observation of a simulation; it is not a revocable permit.
    // Callers can retain this object, so it must never be used as current
    // authority, hardware proof, a real journal or permission to start a study.
    // Snapshot trial reads retain the producer's owner-thread restriction;
    // immutable here does not mean cross-thread trial access is harmless.
    public sealed class ViveSyntheticAggregateHead
    {
        public long Revision {get;private set;}
        public long Publication {get;private set;}
        public ExperimentIncrementalSnapshot Snapshot {get;private set;}
        public long MutationStepsObserved {get;private set;}
        public TaskSnapshot Task {get{return Snapshot.Task;}}
        public ViveSyntheticChoiceState ChoiceState {get;private set;}
        public long ContextEpoch {get;private set;}
        public IReadOnlyList<ViveSyntheticStepTrace> StepTrace {get;private set;}
        internal ViveSyntheticAggregateHead(long revision,long publication,ExperimentIncrementalSnapshot snapshot,long steps,ViveSyntheticChoiceState choice,long context,IReadOnlyList<ViveSyntheticStepTrace> trace)
        {Revision=revision;Publication=publication;Snapshot=snapshot;MutationStepsObserved=steps;ChoiceState=choice;ContextEpoch=context;StepTrace=trace;}
        public bool IsRealAuthority {get{return false;}}
        public bool JournalDurable {get{return false;}}
        public bool ContextProofAvailable {get{return false;}}
    }

    // Simulation-only, in-memory proof of a single destructive reader and one
    // aggregate publication point. This class owns neither the Experiment nor
    // its mutable API. Its fixture must keep *all* Experiment mutation on this
    // owner thread and prevent other readers/writers; the producer's journal
    // lock does not protect Experiment mutations made by external callers.
    // No output from this class can admit, resume, or record a real task.
    public sealed class ViveSyntheticAggregatePump
    {
        readonly Experiment experiment;readonly string session;readonly int ownerThread;readonly object sync=new object();
        ExperimentIncrementalSnapshot staged;ViveSyntheticAggregateHead published;
        readonly int maximumTraceSteps;readonly List<ViveSyntheticStepTrace> trace=new List<ViveSyntheticStepTrace>();
        ViveSyntheticChoiceState choice=ViveSyntheticChoiceState.AwaitingOwner;string selectedOwner;long selectedConfirmation=-1,contextEpoch;
        long revision,steps,highestHead,publication;int terminal;bool busy,available;string issue,failureContext;
        public bool ActualSimulationExperimentBound {get{return true;}}
        public bool RealExperimentSourceBound {get{return false;}}
        public bool JournalDurable {get{return false;}}
        public bool CanEnterCountdown {get{return false;}}
        public bool CanResume {get{return false;}}
        public bool Terminal {get{return Interlocked.CompareExchange(ref terminal,0,0)!=0||experiment.IncrementalObservationFaulted;}}
        public string LastIssue {get{lock(sync)return issue??(experiment.IncrementalObservationFaulted?experiment.IncrementalObservationIssue:null);}}
        public string FailureContext {get{lock(sync)return failureContext;}}
        public ViveSyntheticAggregatePump(Experiment observedSimulation,int maximumTraceSteps=4096)
        {
            if(observedSimulation==null||observedSimulation.Configuration.RunKind!=RunKind.Simulation||
                !observedSimulation.IncrementalObservationEnabled||observedSimulation.IncrementalObservationFaulted||
                observedSimulation.InitialIncrementalSnapshot==null||maximumTraceSteps<1||maximumTraceSteps>4096)
                throw new ArgumentException("An incrementally observed simulation Experiment is required");
            experiment=observedSimulation;session=experiment.Configuration.SessionId;staged=experiment.InitialIncrementalSnapshot;this.maximumTraceSteps=maximumTraceSteps;
            if(staged.Task==null||staged.Task.SessionId!=session)throw new ArgumentException("Matching initial session required");
            ownerThread=Thread.CurrentThread.ManagedThreadId;
        }
        bool Fail(string reason)
        {
            Interlocked.Exchange(ref terminal,1);available=false;published=null;
            if(failureContext==null)failureContext=reason;
            if(issue==null)issue=experiment.IncrementalObservationFaulted?(experiment.IncrementalObservationIssue??reason):reason;return false;
        }
        bool Enter()
        {
            if(Thread.CurrentThread.ManagedThreadId!=ownerThread)return Fail("owner_thread_violation");
            if(experiment.IncrementalObservationFaulted)return Fail("source_faulted");
            if(Terminal)return false;if(busy)return Fail("reentrant_access");busy=true;return true;
        }
        // The source dequeues destructively; any malformed or failed commit is
        // terminal. A bounded unfinished backlog stays private and unavailable.
        public bool Advance(int maxCommits)
        {
            lock(sync)
            {
                if(!Enter())return false;
                var prior=available?published:null;available=false;published=null;
                try
                {
                    if(maxCommits<1||maxCommits>256)return Fail("pump_bound_invalid");
                    for(int count=0;count<maxCommits;count++)
                    {
                        ExperimentIncrementalCommit next;ExperimentIncrementalSnapshot current;long head;
                        if(!experiment.TryReadIncrementalAfter(revision,out next,out head,out current)||experiment.IncrementalObservationFaulted)
                            return Fail("source_read_failed");
                        if(Terminal)return false;
                        if(current==null||current.Task==null||current.Task.SessionId!=session||head<revision||head<highestHead||head==Int64.MaxValue)
                            return Fail("source_head_invalid_or_regressed");
                        highestHead=head;
                        if(next==null)
                        {
                            if(head!=revision||!Object.ReferenceEquals(staged,current))return Fail("source_missing_or_unversioned_state");
                            return PublishIfCurrent(prior);
                        }
                        if(revision>=Int64.MaxValue-1||next.Revision!=revision+1||next.Revision>head||next.Steps==null||
                            next.Steps.Count==0||next.Steps.Count>1024||next.FinalState==null)
                            return Fail("commit_gap_or_incomplete");
                        if(next.Steps.Count>maximumTraceSteps-trace.Count)return Fail("synthetic_trace_capacity_exceeded");
                        var candidate=staged;int index=0;
                        foreach(var step in next.Steps)
                        {
                            if(step==null||step.Index!=index++||step.Before==null||step.After==null||
                                !Object.ReferenceEquals(candidate,step.Before)||Object.ReferenceEquals(step.Before,step.After)||
                                step.After.Task==null||step.After.Task.SessionId!=session)return Fail("commit_step_invalid");
                            if(!StageStep(next.Revision,step))return false;
                            candidate=step.After;
                        }
                        if(!Object.ReferenceEquals(candidate,next.FinalState)||steps>Int64.MaxValue-next.Steps.Count)
                            return Fail("commit_final_invalid");
                        if(next.Revision==head&&!Object.ReferenceEquals(candidate,current))return Fail("head_state_mismatch");
                        staged=candidate;revision=next.Revision;steps+=next.Steps.Count;
                        if(revision==head)return PublishIfCurrent(prior);
                    }
                    return false;
                }
                catch(Exception){return Fail("pump_exception");}
                finally{busy=false;}
            }
        }
        bool StageStep(long tx,ExperimentIncrementalStep step)
        {
            var before=step.Before;var after=step.After;
            if(before.Task.Phase==Phase.Ended&&after.Task.Phase!=Phase.Ended)return Fail("synthetic_terminal_phase_reentered");
            if(choice==ViveSyntheticChoiceState.ActiveCandidate&&
                (after.Selection!=ControlSelection.LiveWoz||after.SourceOwner!=selectedOwner||after.SourceConfirmationSequence!=selectedConfirmation||
                 after.Task.Phase==Phase.NeedsProtocolDecision))choice=ViveSyntheticChoiceState.Revoked;
            if(after.Task.Phase==Phase.NeedsProtocolDecision&&choice==ViveSyntheticChoiceState.AwaitingOwner)choice=ViveSyntheticChoiceState.Revoked;
            if(after.Task.Phase==Phase.Ended&&choice!=ViveSyntheticChoiceState.Revoked)choice=ViveSyntheticChoiceState.Ended;
            bool invalidateContext=before.Task.Phase!=after.Task.Phase||before.Task.Condition!=after.Task.Condition||
                before.Task.BlockIndex!=after.Task.BlockIndex||before.Task.Segment!=after.Task.Segment||
                before.Selection!=after.Selection||before.SourceOwner!=after.SourceOwner||
                before.SourceConfirmationSequence!=after.SourceConfirmationSequence;
            if(invalidateContext){if(contextEpoch==Int64.MaxValue)return Fail("synthetic_context_exhausted");contextEpoch++;}
            trace.Add(new ViveSyntheticStepTrace(tx,step.Index,before,after));return true;
        }
        bool CommitHead()
        {
            if(publication==Int64.MaxValue)return Fail("synthetic_publication_exhausted");
            long next=publication+1;
            var head=new ViveSyntheticAggregateHead(revision,next,staged,steps,choice,contextEpoch,
                new ReadOnlyCollection<ViveSyntheticStepTrace>(new List<ViveSyntheticStepTrace>(trace)));
            publication=next;published=head;available=true;return true;
        }
        bool PublishIfCurrent(ViveSyntheticAggregateHead prior)
        {
            bool current=experiment.IsIncrementalRevisionCurrent(revision);
            if(Terminal||experiment.IncrementalObservationFaulted)return Fail("source_faulted");
            if(!current){available=false;return false;}
            // No external callback occurs between this freshness check and the
            // single immutable publication. Exclusive fixture ownership is
            // still required; this cannot fence external Experiment writers.
            if(prior!=null&&prior.Revision==revision&&Object.ReferenceEquals(prior.Snapshot,staged)&&
                prior.MutationStepsObserved==steps&&prior.ChoiceState==choice&&prior.ContextEpoch==contextEpoch&&prior.StepTrace.Count==trace.Count)
            {published=prior;available=true;return true;}
            return CommitHead();
        }
        // The Actor value and simulation source are fixture inputs, not proof
        // of an actual UI action or human identity.
        public bool ConfirmSyntheticOwnerChoice(Actor actor)
        {
            lock(sync)
            {
                if(!Enter())return false;
                try
                {
                    if(!available||published==null||choice!=ViveSyntheticChoiceState.AwaitingOwner||actor!=Actor.Owner)return false;
                    bool current=experiment.IsIncrementalRevisionCurrent(revision);
                    if(Terminal||experiment.IncrementalObservationFaulted)return Fail("source_faulted");
                    if(!current){available=false;published=null;return false;}
                    if(staged.Task.Phase!=Phase.BlockReady||staged.Task.BlockIndex!=0||staged.Selection!=ControlSelection.LiveWoz||
                        String.IsNullOrWhiteSpace(staged.SourceOwner)||staged.SourceConfirmationSequence<0)return false;
                    if(contextEpoch==Int64.MaxValue)return Fail("synthetic_context_exhausted");
                    selectedOwner=staged.SourceOwner;selectedConfirmation=staged.SourceConfirmationSequence;
                    contextEpoch++;choice=ViveSyntheticChoiceState.ActiveCandidate;return CommitHead();
                }
                catch(Exception){return Fail("synthetic_confirmation_failed");}
                finally{busy=false;}
            }
        }
        public bool TryGet(out ViveSyntheticAggregateHead head)
        {
            head=null;
            lock(sync)
            {
                if(!Enter())return false;
                try
                {
                    if(experiment.IncrementalObservationFaulted)return Fail("source_faulted");
                    if(!available||published==null)return false;
                    bool current=experiment.IsIncrementalRevisionCurrent(published.Revision);
                    if(Terminal||experiment.IncrementalObservationFaulted)return Fail("source_faulted");
                    if(!current){available=false;published=null;return false;}
                    head=published;return true;
                }
                catch(Exception){return Fail("source_freshness_failed");}
                finally{busy=false;}
            }
        }
    }
}
