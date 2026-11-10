using System;
using System.Threading;
using AvatarStroop.Core;

namespace AvatarStroop.Runtime
{
    public sealed partial class ViveDevelopmentParticipant
    {
        ViveLegacyWozTaskOwner legacyWozOwner;
        bool legacyWozCleanupUncertain;
        // Independent of recording intent and the optional diagnostic journal.
        // Existing task actions retain their behavior; a bad/reentrant action
        // invalidates bridge authority rather than acquiring recording authority.
        void RunLegacyWozMutation(Action action)
        {
            var owner=legacyWozOwner;
            if(owner==null){action();return;}
            owner.BeginMutation();bool completed=false;
            try{action();completed=true;}finally{owner.EndMutation(completed);}
        }
        public ViveLegacyWozTaskOwner CreateLegacyWozTaskOwner(MonotonicClock sharedClock)
        {
            if(Thread.CurrentThread.ManagedThreadId!=namedViewOwnerThread||sharedClock==null||
                !Object.ReferenceEquals(eventClock,sharedClock)||experiment.Configuration.IsNamedDesign||
                experiment.Configuration.RunKind!=RunKind.Simulation||NamedRecordingWithdrawalRequested||NamedViewWithdrawalRequested||legacyWozCleanupUncertain)
                throw new InvalidOperationException("Actual Legacy simulation owner and exact sealed task clock required.");
            if(legacyWozOwner!=null&&!legacyWozOwner.Terminal)
                throw new InvalidOperationException("The participant already owns a Legacy bridge binding.");
            legacyWozOwner=new ViveLegacyWozTaskOwner(this,sharedClock);
            return legacyWozOwner;
        }
        internal bool OwnsLegacyWoz(ViveLegacyWozTaskOwner owner)
        {return Object.ReferenceEquals(legacyWozOwner,owner)&&!NamedRecordingWithdrawalRequested&&!NamedViewWithdrawalRequested&&
            !ObservationBlocked&&!NamedRecordingAuthorityFaulted;}
        internal TaskSnapshot ReadLegacyWozTask()
        {
            var view=experiment.View(Actor.Operator);var stimulus=view.Stimulus;
            return new TaskSnapshot(SessionId,experiment.BlockIndex,experiment.Segment,view.TrialId,
                stimulus==null?(Colour?)null:stimulus.Word,stimulus==null?(Colour?)null:stimulus.Ink,experiment.Phase,experiment.CurrentCondition);
        }
        internal string LegacyWozSourceOwner {get{return experiment.LegacyWozSourceOwner;}}
        internal long LegacyWozSourceGeneration {get{return experiment.LegacyWozSourceGeneration;}}
        // Count only, never journal contents; all task/source semantic mutations
        // append an event. Harmless polling does not advance this cursor.
        internal int LegacyWozEventCursor {get{return experiment.Events.Count;}}
        internal void MarkLegacyWozCleanupUncertain(){legacyWozCleanupUncertain=true;}
    }

    // Reusable owner binding, independent of the memory transport below. No
    // synthetic authority, recorder stamp, caller timestamp or peer bool enters it.
    public sealed partial class ViveLegacyWozTaskOwner : IDisposable
    {
        readonly ViveDevelopmentParticipant participant;
        readonly Thread thread=Thread.CurrentThread;
        readonly MonotonicClock clock;
        long revision,wireRevision,actionGeneration;int depth,beforeEvents;volatile bool terminal;
        string beforeSourceOwner;long beforeSourceGeneration;ControlSelection? beforeSelection;
        TaskSnapshot beforeTask;
        Binding current;
        internal ViveLegacyWozTaskOwner(ViveDevelopmentParticipant participant,MonotonicClock clock)
        {this.participant=participant;this.clock=clock;}
        public bool Terminal {get{return terminal;}}
        public bool CanEnterLiveCountdown {get{return false;}}
        public bool CanRecordOrExport {get{return false;}}
        public bool CanPresentLivePose {get{return false;}}
        public bool UsesClock(MonotonicClock candidate){return Object.ReferenceEquals(clock,candidate);}
        internal MonotonicClock Clock {get{return clock;}}
        internal string SessionId {get{return participant.SessionId;}}
        internal void MarkCleanupUncertain(){participant.MarkLegacyWozCleanupUncertain();terminal=true;current=null;}
        internal bool OnOwnerThread {get{return Object.ReferenceEquals(Thread.CurrentThread,thread);}}
        internal bool MutationIdle {get{return OnOwnerThread&&!terminal&&depth==0;}}
        internal void BeginMutation()
        {
            if(!OnOwnerThread||depth!=0)
            {terminal=true;throw new InvalidOperationException("Legacy bridge task mutation requires its nonreentrant owner thread.");}
            if(actionGeneration>=Int64.MaxValue-1)terminal=true;
            if(actionGeneration<Int64.MaxValue)actionGeneration++;
            if(depth==0&&OnOwnerThread){beforeEvents=participant.LegacyWozEventCursor;beforeTask=participant.ReadLegacyWozTask();
                beforeSourceOwner=participant.LegacyWozSourceOwner;beforeSourceGeneration=participant.LegacyWozSourceGeneration;beforeSelection=participant.LockedControl;}
            depth++;
        }
        internal void EndMutation(bool completed)
        {
            if(depth>0)depth--;if(!completed||!OnOwnerThread)terminal=true;
            if(actionGeneration<Int64.MaxValue)actionGeneration++;else terminal=true;
            if(depth==0&&OnOwnerThread&&(beforeEvents!=participant.LegacyWozEventCursor||!SameTask(beforeTask,participant.ReadLegacyWozTask())))
            {current=null;if(revision<Int64.MaxValue)revision++;else terminal=true;}
            if(depth==0&&OnOwnerThread&&(!SameTask(beforeTask,participant.ReadLegacyWozTask())||beforeSourceOwner!=participant.LegacyWozSourceOwner||
                beforeSourceGeneration!=participant.LegacyWozSourceGeneration||beforeSelection!=participant.LockedControl))
            {if(wireRevision<Int64.MaxValue)wireRevision++;else terminal=true;}
            if(depth==0&&OnOwnerThread){if(RecordingBinding!=null)RecordingBinding.ObserveActualTaskCompletion();InvalidateSourceChoiceAfterSafety();}
        }
        static bool SameTask(TaskSnapshot a,TaskSnapshot b)
        {return a!=null&&b!=null&&a.SessionId==b.SessionId&&a.Phase==b.Phase&&a.BlockIndex==b.BlockIndex&&a.Segment==b.Segment&&
            a.TrialId==b.TrialId&&a.Word==b.Word&&a.Ink==b.Ink&&a.Condition==b.Condition;}
        public sealed class Binding
        {
            internal readonly ViveLegacyWozTaskOwner Owner;
            public TaskSnapshot Task {get;private set;}
            public long Revision {get;private set;}
            internal readonly long WireRevision;
            // Local only; never placed on the operator wire.
            internal readonly string SourceOwner;
            internal readonly long SourceGeneration;
            internal readonly ControlSelection? Selection;
            internal Binding(ViveLegacyWozTaskOwner owner,TaskSnapshot task,long revision,string sourceOwner,long generation,ControlSelection? selection)
            {Owner=owner;Task=task;Revision=revision;WireRevision=owner.wireRevision;SourceOwner=sourceOwner;SourceGeneration=generation;Selection=selection;}
        }
        public bool TryCapture(out Binding binding)
        {
            binding=null;
            if(!OnOwnerThread)return false;
            if(terminal||depth!=0||!participant.OwnsLegacyWoz(this)){terminal=true;current=null;return false;}
            current=new Binding(this,participant.ReadLegacyWozTask(),revision,participant.LegacyWozSourceOwner,
                participant.LegacyWozSourceGeneration,participant.LockedControl);
            binding=current;return true;
        }
        public bool IsCurrent(Binding binding)
        {
            return OnOwnerThread&&!terminal&&depth==0&&binding!=null&&Object.ReferenceEquals(binding,current)&&
                Object.ReferenceEquals(binding.Owner,this)&&participant.OwnsLegacyWoz(this)&&binding.Revision==revision&&
                binding.SourceOwner==participant.LegacyWozSourceOwner&&binding.SourceGeneration==participant.LegacyWozSourceGeneration&&
                binding.Selection==participant.LockedControl;
        }
        // Pause is the real participant's existing safety path. Its raw safety
        // rules remain responsible for any attached recorder. No resume exists.
        internal bool PauseAuthoritativeTask()
        {
            if(!OnOwnerThread)return false;
            try
            {
                if(participant.Phase!=Phase.Paused&&participant.Phase!=Phase.Ended)participant.OwnerPause(Actor.Owner);
                return participant.Phase==Phase.Paused||participant.Phase==Phase.Ended;
            }
            catch{return false;}
        }
        public void Dispose()
        {if(!OnOwnerThread)throw new InvalidOperationException("Task binding disposal requires its owner thread.");terminal=true;current=null;}
    }
}
