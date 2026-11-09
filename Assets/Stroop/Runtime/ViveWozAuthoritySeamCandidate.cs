using System;
using System.Net;
using System.Threading;
using AvatarStroop.Core;

namespace AvatarStroop.Runtime
{
    // Synthetic/development seam only. Neither type implements the production
    // source-state reader; declared peer identities are not authentication.
    public enum ViveLocalChoiceState { AwaitingOwner, ActiveCandidate, Revoked, Ended, Faulted }

    // Receives ordered immutable Experiment commits from the *existing single*
    // mutation consumer. Constructing this object neither opens another cursor
    // nor proves that the caller is a real UI or the authoritative host.
    public sealed class ViveWozLocalChoiceCandidate
    {
        readonly string session,owner;readonly int ownerThread;
        ExperimentMutationSnapshot current;long revision,head,confirmation=-1;int threadFault;
        ViveLocalChoiceState state;
        public ViveLocalChoiceState State {get{return Interlocked.CompareExchange(ref threadFault,0,0)!=0?ViveLocalChoiceState.Faulted:state;}private set{state=value;}}
        public string LastIssue {get;private set;}
        public long Revision {get{return revision;}}
        public long? ConfirmationSequence {get{return confirmation<0?(long?)null:confirmation;}}
        public bool CaughtUp {get{Owner();return State!=ViveLocalChoiceState.Faulted&&revision==head;}}
        public bool LocalChoiceCandidate {get{Owner();return State==ViveLocalChoiceState.ActiveCandidate&&CaughtUp;}}
        public bool CanEnterCountdown {get{return false;}}
        public bool CanResume {get{return false;}}
        public bool RealUiBound {get{return false;}}
        public bool SyntheticFixtureOnly {get{return true;}}
        public bool CryptographicallyAuthenticated {get{return false;}}
        internal ViveWozLocalChoiceCandidate(string session,string owner,ExperimentMutationSnapshot initial,bool syntheticFixture)
        {
            if(!syntheticFixture)throw new InvalidOperationException("Synthetic fixture scope is required.");
            WireCodec.RequireIdentity(session);WireCodec.RequireIdentity(owner);
            if(initial==null||initial.Task==null||initial.Task.SessionId!=session)throw new ArgumentException("Matching initial mutation snapshot required.");
            this.session=session;this.owner=owner;current=initial;ownerThread=Thread.CurrentThread.ManagedThreadId;
            State=ViveLocalChoiceState.AwaitingOwner;
        }
        void Owner(){if(Thread.CurrentThread.ManagedThreadId!=ownerThread){Interlocked.Exchange(ref threadFault,1);throw new InvalidOperationException("Owner thread required.");}}
        bool Fail(string issue){State=ViveLocalChoiceState.Faulted;LastIssue=issue;return false;}
        bool Retains(ExperimentMutationSnapshot state)
        {return state!=null&&state.Task!=null&&state.Task.SessionId==session&&state.Selection==ControlSelection.LiveWoz&&
            state.SourceOwner==owner&&state.SourceConfirmationSequence==confirmation;}
        static bool SameSnapshot(ExperimentMutationSnapshot a,ExperimentMutationSnapshot b)
        {
            if(a==null||b==null||a.Task==null||b.Task==null||!a.Same(b)||a.Selection!=b.Selection||a.SourceOwner!=b.SourceOwner||
                a.SourceConfirmationSequence!=b.SourceConfirmationSequence||a.PausedFrom!=b.PausedFrom)return false;
            var x=a.Task;var y=b.Task;
            return x.SessionId==y.SessionId&&x.BlockIndex==y.BlockIndex&&x.Segment==y.Segment&&x.Phase==y.Phase&&
                x.Condition==y.Condition&&x.TrialId==y.TrialId&&x.Word==y.Word&&x.Ink==y.Ink;
        }
        // Advancing head without its commits makes choice evidence unavailable.
        // Backlog is not itself confirmation, consent, or a fresh task state.
        public bool ObserveHead(long observedHead)
        {
            Owner();if(State==ViveLocalChoiceState.Faulted||State==ViveLocalChoiceState.Revoked||State==ViveLocalChoiceState.Ended)return false;
            if(observedHead<head||observedHead<revision||observedHead==Int64.MaxValue)return Fail("mutation_head_regressed_or_invalid");
            head=observedHead;LastIssue=CaughtUp?null:"pending_backlog";return CaughtUp;
        }
        public bool ObserveCommit(ExperimentMutationCommit commit,long observedHead)
        {
            Owner();if(State==ViveLocalChoiceState.Faulted||State==ViveLocalChoiceState.Revoked||State==ViveLocalChoiceState.Ended)return false;
            try{return ObserveCommitCore(commit,observedHead);}catch(Exception){return Fail("mutation_feed_exception");}
        }
        bool ObserveCommitCore(ExperimentMutationCommit commit,long observedHead)
        {
            if(commit==null||revision==Int64.MaxValue-1||commit.Revision!=revision+1||observedHead<head||observedHead<commit.Revision||observedHead==Int64.MaxValue)
                return Fail("mutation_gap_replay_or_invalid_head");
            if(commit.Steps==null||commit.Steps.Count==0||commit.FinalState==null)return Fail("incomplete_commit");
            var last=current;int index=0;bool ended=false;
            foreach(var step in commit.Steps)
            {
                if(step==null||step.Index!=index++||step.Before==null||step.After==null||!SameSnapshot(last,step.Before)||
                    step.After.Task==null||step.After.Task.SessionId!=session)return Fail("inconsistent_mutation_step");
                // An active choice invalidated at any point cannot be restored by
                // a later state in the same transaction.
                if(State==ViveLocalChoiceState.ActiveCandidate&&!Retains(step.After))return Fail("intermediate_revocation_or_context_change");
                if(step.After.Task.Phase==Phase.NeedsProtocolDecision)return Fail("protocol_decision_required");
                if(ended&&step.After.Task.Phase!=Phase.Ended)return Fail("terminal_phase_reentered");
                if(step.After.Task.Phase==Phase.Ended)ended=true;
                last=step.After;
            }
            if(!SameSnapshot(last,commit.FinalState))return Fail("incomplete_mutation_commit");
            current=commit.FinalState;revision=commit.Revision;head=observedHead;
            if(ended){State=ViveLocalChoiceState.Ended;LastIssue="session_ended";return false;}
            LastIssue=CaughtUp?null:"pending_backlog";return CaughtUp;
        }
        // This is an explicit method action, never an external boolean. A real UI
        // must invoke it only after its first actual owner choice and after the
        // underlying single cursor establishes that the head is still current.
        public void ConfirmOwnerChoice(Actor actor)
        {
            Owner();if(State!=ViveLocalChoiceState.AwaitingOwner||actor!=Actor.Owner||!CaughtUp||
                current.Task.Phase!=Phase.BlockReady||current.Task.BlockIndex!=0||current.Selection!=ControlSelection.LiveWoz||
                current.SourceOwner!=owner||current.SourceConfirmationSequence<0)
                throw new InvalidOperationException("Caught-up first-block owner choice and matching committed source are required.");
            confirmation=current.SourceConfirmationSequence;State=ViveLocalChoiceState.ActiveCandidate;LastIssue=null;
        }
        // Terminal before any clock, message encoding, network, or async action.
        // The actual host is separately responsible for clearing and pausing.
        public void RevokeOwnerChoice(Actor actor)
        {
            Owner();if(actor!=Actor.Owner||(State!=ViveLocalChoiceState.ActiveCandidate&&State!=ViveLocalChoiceState.AwaitingOwner))throw new InvalidOperationException("Available owner choice required.");
            State=ViveLocalChoiceState.Revoked;LastIssue="ui_revoked";
        }
        public void SignalJournalLoss(string reason)
        {Owner();if(String.IsNullOrWhiteSpace(reason)){if(State!=ViveLocalChoiceState.Revoked)Fail("journal_loss_reason_invalid");throw new ArgumentException("Explicit reason required.");}
            if(State!=ViveLocalChoiceState.Revoked)Fail("journal_loss:"+reason);}
    }

    // A separate remote type deliberately cannot be substituted for local choice.
    // It wraps the existing bounded, sequenced receiver and checks the supplied
    // datagram endpoint; neither endpoint matching nor a candidate proves identity.
    public sealed class ViveWozRemoteDeclarationCandidate
    {
        readonly ViveWozSourceReceiver receiver;readonly IPEndPoint expected;readonly int ownerThread;int faulted;
        internal ViveWozRemoteDeclarationCandidate(ViveWozSourceReceiver receiver,IPEndPoint expectedEndpoint,bool syntheticFixture)
        {
            if(!syntheticFixture)throw new InvalidOperationException("Synthetic fixture scope is required.");
            if(receiver==null||expectedEndpoint==null||expectedEndpoint.Port==0)throw new ArgumentException("Receiver and explicit peer endpoint required.");
            this.receiver=receiver;expected=new IPEndPoint(expectedEndpoint.Address,expectedEndpoint.Port);ownerThread=Thread.CurrentThread.ManagedThreadId;
        }
        void Owner(){if(Thread.CurrentThread.ManagedThreadId!=ownerThread){Interlocked.Exchange(ref faulted,1);receiver.InvalidateFromAnyThread();throw new InvalidOperationException("Owner thread required.");}}
        public ViveWozSourceEvidenceState State {get{Owner();return Interlocked.CompareExchange(ref faulted,0,0)!=0?ViveWozSourceEvidenceState.Faulted:receiver.State;}}
        public bool RemoteDeclarationAvailable
        {get{Owner();if(Interlocked.CompareExchange(ref faulted,0,0)!=0)return false;try{bool available=receiver.Candidate();
            return available&&Interlocked.CompareExchange(ref faulted,0,0)==0;}catch(Exception){Interlocked.Exchange(ref faulted,1);receiver.InvalidateFromAnyThread();return false;}}}
        public bool CanEnterCountdown {get{return false;}}
        public bool CanResume {get{return false;}}
        public bool SyntheticFixtureOnly {get{return true;}}
        public bool CryptographicallyAuthenticated {get{return false;}}
        public bool Observe(byte[] packet,IPEndPoint actualSender)
        {
            Owner();if(Interlocked.CompareExchange(ref faulted,0,0)!=0)return false;
            bool matches=actualSender!=null&&actualSender.Equals(expected);
            try{bool accepted=receiver.Observe(packet,matches);return accepted&&Interlocked.CompareExchange(ref faulted,0,0)==0;}
            catch(Exception){Interlocked.Exchange(ref faulted,1);receiver.InvalidateFromAnyThread();return false;}
        }
        public void SignalLoss(string reason)
        {Owner();Interlocked.Exchange(ref faulted,1);bool first=receiver.InvalidateFromAnyThread();
            if(String.IsNullOrWhiteSpace(reason))throw new ArgumentException("Explicit reason required.");
            if(first)receiver.RecordInitialExternalLoss(reason);receiver.SignalLoss(reason);}
    }
}
