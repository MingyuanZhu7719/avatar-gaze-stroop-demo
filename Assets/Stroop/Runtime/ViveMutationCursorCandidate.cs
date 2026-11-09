using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using AvatarStroop.Core;

namespace AvatarStroop.Runtime
{
    // These values are supplied by a future producer, not evidence that the
    // present Experiment publishes mutations. This candidate has no hardware,
    // network, live-source or experiment admission capability.
    public sealed class ViveMutationStateCandidate
    {
        public TaskSnapshot Task {get;private set;}
        public ControlSelection? Selection {get;private set;}
        public string OwnerId {get;private set;}
        public bool ChoiceActive {get;private set;}
        public Phase? PausedFrom {get;private set;}
        public ViveMutationStateCandidate(TaskSnapshot task,ControlSelection? selection,string ownerId,bool choiceActive,Phase? pausedFrom)
        {
            if(task==null||(selection.HasValue&&!Enum.IsDefined(typeof(ControlSelection),selection.Value))||
                (pausedFrom.HasValue&&!Enum.IsDefined(typeof(Phase),pausedFrom.Value))||
                (choiceActive&&(!selection.HasValue||String.IsNullOrWhiteSpace(ownerId))))
                throw new ArgumentException("Explicit valid candidate state required");
            if(ownerId!=null)WireCodec.RequireIdentity(ownerId);
            Task=task;Selection=selection;OwnerId=ownerId;ChoiceActive=choiceActive;PausedFrom=pausedFrom;
        }
        internal bool Same(ViveMutationStateCandidate b)
        {
            if(b==null)return false;var a=Task;var t=b.Task;
            return Selection==b.Selection&&OwnerId==b.OwnerId&&ChoiceActive==b.ChoiceActive&&PausedFrom==b.PausedFrom&&
                a.SessionId==t.SessionId&&a.BlockIndex==t.BlockIndex&&a.Segment==t.Segment&&a.TrialId==t.TrialId&&
                a.Word==t.Word&&a.Ink==t.Ink&&a.Phase==t.Phase&&a.Condition==t.Condition;
        }
    }
    public sealed class ViveMutationStepCandidate
    {
        public int Index {get;private set;} public ViveMutationStateCandidate Before {get;private set;} public ViveMutationStateCandidate After {get;private set;}
        public ViveMutationStepCandidate(int index,ViveMutationStateCandidate before,ViveMutationStateCandidate after)
        {if(index<0||before==null||after==null)throw new ArgumentException("Explicit ordered state step required");Index=index;Before=before;After=after;}
    }
    public sealed class ViveMutationCommitCandidate
    {
        public long Revision {get;private set;} public bool CompleteAndSuccessful {get;private set;}
        public IReadOnlyList<ViveMutationStepCandidate> Steps {get;private set;}
        public ViveMutationStateCandidate FinalState {get;private set;}
        public ViveMutationCommitCandidate(long revision,bool completeAndSuccessful,IEnumerable<ViveMutationStepCandidate> steps,ViveMutationStateCandidate finalState)
        {
            if(revision<=0||revision==Int64.MaxValue||steps==null||finalState==null)throw new ArgumentException("Valid bounded mutation commit required");
            var items=new List<ViveMutationStepCandidate>();foreach(var item in steps){if(item==null||items.Count>=1024)throw new ArgumentException("Bounded non-null steps required");items.Add(item);}
            Revision=revision;CompleteAndSuccessful=completeAndSuccessful;Steps=new ReadOnlyCollection<ViveMutationStepCandidate>(items);FinalState=finalState;
        }
    }
    // The producer must return these three fields atomically. The current code
    // has NO Experiment implementation of this contract; test doubles only.
    public sealed class ViveMutationReadCandidate
    {
        public long HeadRevision {get;private set;} public ViveMutationCommitCandidate Next {get;private set;} public ViveMutationStateCandidate Current {get;private set;}
        public ViveMutationReadCandidate(long headRevision,ViveMutationCommitCandidate next,ViveMutationStateCandidate current)
        {if(headRevision<0||headRevision==Int64.MaxValue||current==null)throw new ArgumentException("Atomic head and state required");HeadRevision=headRevision;Next=next;Current=current;}
    }
    public sealed class ViveMutationCursorCandidate
    {
        readonly string sessionId;readonly int ownerThread;ViveMutationStateCandidate state;long revision;int terminal;bool caughtUp=true;
        public string LastIssue {get;private set;} public bool Terminal {get{return Interlocked.CompareExchange(ref terminal,0,0)!=0;}}
        public bool RealExperimentSourceBound {get{return false;}} public bool CanEnterCountdown {get{return false;}} public bool CanResume {get{return false;}}
        public long Revision {get{return revision;}} public bool CaughtUp {get{return caughtUp&&!Terminal;}}
        // Not an IViveWozSourceStateReader: a synthetic candidate cannot be
        // accidentally plugged into the live source confirmation mechanism.
        public ViveMutationStateCandidate Current {get{return CaughtUp?state:null;}}
        public ViveMutationCursorCandidate(string session,ViveMutationStateCandidate initial)
        {
            WireCodec.RequireIdentity(session);
            if(initial==null||initial.Task.SessionId!=session)throw new ArgumentException("Matching revision-zero initial state required");
            sessionId=session;state=initial;ownerThread=Thread.CurrentThread.ManagedThreadId;
        }
        bool Fail(string issue){Interlocked.Exchange(ref terminal,1);caughtUp=false;LastIssue=issue;return false;}
        public bool Advance(Func<long,ViveMutationReadCandidate> readAtomicAfter)
        {
            if(Thread.CurrentThread.ManagedThreadId!=ownerThread)return Fail("owner_thread_violation");
            if(Terminal)return false;if(readAtomicAfter==null)return Fail("mutation_feed_missing");
            ViveMutationReadCandidate read;try{read=readAtomicAfter(revision);}catch(Exception){return Fail("mutation_feed_exception");}
            if(read==null||read.Current==null||read.Current.Task.SessionId!=sessionId||read.HeadRevision<revision)return Fail("invalid_or_rolled_back_head");
            var next=read.Next;
            if(next==null)
            {
                if(read.HeadRevision!=revision||!state.Same(read.Current))return Fail("missing_mutation_or_unversioned_state");
                caughtUp=true;return true;
            }
            if(revision==Int64.MaxValue-1||next.Revision!=revision+1||next.Revision>read.HeadRevision||!next.CompleteAndSuccessful)
                return Fail("mutation_gap_replay_or_failed_commit");
            var last=state;int expected=0;
            foreach(var step in next.Steps)
            {
                if(step.Index!=expected++||!last.Same(step.Before)||step.After.Task.SessionId!=sessionId)
                    return Fail("missing_or_inconsistent_mutation_step");
                last=step.After;
            }
            if(!last.Same(next.FinalState))return Fail("incomplete_mutation_commit");
            state=next.FinalState;revision=next.Revision;caughtUp=revision==read.HeadRevision;
            if(caughtUp&&!state.Same(read.Current))return Fail("commit_head_state_mismatch");
            LastIssue=null;return caughtUp;
        }
    }
}
