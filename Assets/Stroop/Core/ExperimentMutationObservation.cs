using System;
using System.Collections.Generic;

namespace AvatarStroop.Core
{
    public sealed partial class Experiment
    {
        ExperimentMutationJournal mutationJournal;
        static readonly Colour[] MutationColours={Colour.Red,Colour.Green,Colour.Blue,Colour.Yellow};
        // The ordinary constructor and Quest behavior remain unchanged. The
        // explicit option is available only at construction, before a trial can
        // escape to a caller. Revision zero means completed construction, not a
        // claim to have observed the inside of the constructor.
        public Experiment(SessionConfiguration configuration,IClock clock,bool explicitDevelopmentObservation,int capacity)
            :this(configuration,clock)
        {
            if(!explicitDevelopmentObservation)throw new ArgumentException("Explicit development observation required");
            if(configuration.IsNamedDesign)throw new InvalidOperationException("Legacy mutation observation does not support named-option provenance");
            mutationJournal=new ExperimentMutationJournal(this,capacity);
        }
        public bool MutationObservationEnabled {get{return mutationJournal!=null;}}
        public bool MutationObservationFaulted {get{return mutationJournal!=null&&mutationJournal.Terminal;}}
        public string MutationObservationIssue {get{return mutationJournal==null?"not_enabled":mutationJournal.Issue;}}
        public ExperimentMutationSnapshot InitialMutationSnapshot {get{return mutationJournal==null?null:mutationJournal.Initial;}}
        // One owner-thread cursor; an out-of-order read or truncated stream is
        // terminal. No journal read creates a clock sample or invokes a callback.
        public bool TryReadMutationAfter(long after,out ExperimentMutationCommit next,out long head,out ExperimentMutationSnapshot current)
        {
            next=null;head=0;current=null;
            return mutationJournal!=null&&mutationJournal.ReadNext(after,out next,out head,out current);
        }
        public bool IsMutationRevisionCurrent(long revision)
        {return mutationJournal!=null&&mutationJournal.IsCurrent(revision);}
        void BeginMutation(string operation)
        {try{if(mutationJournal!=null)mutationJournal.Begin(operation);}catch(Exception){if(mutationJournal!=null)mutationJournal.Invalidate();}
            try{if(incrementalMutationJournal!=null)incrementalMutationJournal.Begin(operation);}catch(Exception){if(incrementalMutationJournal!=null)incrementalMutationJournal.Invalidate();}}
        void TraceMutation()
        {try{if(mutationJournal!=null)mutationJournal.Step();}catch(Exception){if(mutationJournal!=null)mutationJournal.Invalidate();}
            try{if(incrementalMutationJournal!=null)incrementalMutationJournal.Step();}catch(Exception){if(incrementalMutationJournal!=null)incrementalMutationJournal.Invalidate();}}
        void EndMutation(bool success)
        {try{if(mutationJournal!=null)mutationJournal.End(success);}catch(Exception){if(mutationJournal!=null)mutationJournal.Invalidate();}
            try{if(incrementalMutationJournal!=null)incrementalMutationJournal.End(success);}catch(Exception){if(incrementalMutationJournal!=null)incrementalMutationJournal.Invalidate();}}

        // Pre-allocation estimate deliberately overcounts boxed values, List
        // backing arrays and transient copies. Immutable strings/stimuli are
        // shared with the Experiment; no user content is duplicated here.
        internal long EstimateMutationSnapshotBytes()
        {
            long values=512;foreach(var list in lists)values+=2L*list.Length;
            values+=practice==null?0:2L*practice.Length;
            values+=18L*(trials.Count+1)+6L*ratings.Count;
            return checked(8192+160*values);
        }

        // Capture private state directly. Never call View(), Time(), derived
        // remaining-time getters, or an external sink from this method.
        internal ExperimentMutationSnapshot CaptureMutationSnapshot()
        {
            var vals=new List<object>();
            vals.Add(Configuration.SessionId);vals.Add(Configuration.Seed);vals.Add(Configuration.RunKind);vals.Add(Configuration.QuestionnaireLanguage);
            vals.Add(Configuration.CountdownSeconds);vals.Add(Configuration.MinimumRestSeconds);vals.Add(Configuration.SkipFinalRest);
            vals.Add(Configuration.QuestionnaireInstructionSeconds);vals.Add(Configuration.QuestionnaireInstructionRequiresAcknowledgement);
            vals.Add(Configuration.ParticipantPacedRest);vals.Add(Configuration.RetainControlAcrossBlocks);
            vals.Add(Configuration.Allocation.AllocationId);vals.Add(Configuration.Allocation.BInitialTarget);
            foreach(var allocationCondition in Configuration.Allocation.Order)vals.Add(allocationCondition);
            foreach(var list in Configuration.Allocation.ListByBlock)vals.Add(list);
            foreach(var list in lists)foreach(var stimulus in list){vals.Add(stimulus.Word);vals.Add(stimulus.Ink);}
            vals.Add((int)Phase);vals.Add(BlockIndex);vals.Add(Segment);vals.Add(PracticeAttempt);vals.Add(practiceCorrect);
            vals.Add(questionIndex);vals.Add(trialIndex);vals.Add((int)beforePause);vals.Add(resumeCountdown);vals.Add(questionnaireInstructionPending);
            vals.Add(countdownAt);vals.Add(restAt);vals.Add(ratingAt);vals.Add(pausedAt);vals.Add(activeAccum);vals.Add(activeSince);
            vals.Add(EndReason.HasValue?(int?)(int)EndReason.Value:null);vals.Add(LockedControl.HasValue?(int?)(int)LockedControl.Value:null);vals.Add(sourceConfirmationOwner);vals.Add(sourceConfirmationSequence);
            foreach(var colour in MutationColours)vals.Add(held.Contains(colour));
            vals.Add(practice==null?-1:practice.Length);
            if(practice!=null)foreach(var stimulus in practice){vals.Add((int)stimulus.Word);vals.Add((int)stimulus.Ink);}
            vals.Add(trials.Count);
            foreach(var trial in trials)AppendMutationTrial(vals,trial);
            AppendMutationTrial(vals,current);int currentIndex=current==null?-1:trials.IndexOf(current);vals.Add(currentIndex);vals.Add(ratings.Count);
            foreach(var rating in ratings){vals.Add(rating.BlockIndex);vals.Add(rating.ItemId);vals.Add(rating.Value);vals.Add(rating.Time);vals.Add(rating.Language);vals.Add(rating.Version);}
            vals.Add(events.Count);
            bool display=Phase==Phase.MainTask&&current!=null;var condition=CurrentCondition;
            var task=new TaskSnapshot(Configuration.SessionId,BlockIndex,Segment,display?current.TrialId:null,
                display?(Colour?)current.Stimulus.Word:null,display?(Colour?)current.Stimulus.Ink:null,Phase,condition);
            var copiedTrials=new ExperimentMutationTrialSnapshot[trials.Count];
            for(int i=0;i<trials.Count;i++)copiedTrials[i]=new ExperimentMutationTrialSnapshot(trials[i]);
            return new ExperimentMutationSnapshot(task,LockedControl,sourceConfirmationOwner,sourceConfirmationSequence,
                Phase==Phase.Paused?(Phase?)beforePause:null,vals.ToArray(),trials.Count,currentIndex,
                current==null?(ExperimentMutationTrialSnapshot?)null:new ExperimentMutationTrialSnapshot(current),copiedTrials);
        }
        static void AppendMutationTrial(List<object> values,TrialRecord trial)
        {
            values.Add(trial!=null);if(trial==null)return;
            values.Add(trial.TrialId);values.Add(trial.SessionId);values.Add(trial.ListId);values.Add(trial.IsPractice);values.Add(trial.PracticeAttempt);
            values.Add(trial.BlockIndex);values.Add(trial.PlannedIndex);values.Add(trial.Segment);values.Add(trial.Condition.HasValue?(int?)(int)trial.Condition.Value:null);
            values.Add(trial.Stimulus==null?(int?)null:(int)trial.Stimulus.Word);values.Add(trial.Stimulus==null?(int?)null:(int)trial.Stimulus.Ink);
            values.Add(trial.Onset);values.Add(trial.ResponseAt);values.Add(trial.Response.HasValue?(int?)(int)trial.Response.Value:null);values.Add(trial.Correct);values.Add((int)trial.Outcome);values.Add(trial.TechnicalIssue);
        }
        // Compare against an immutable snapshot without reboxing/copying the
        // full experiment on every no-op Tick or freshness read. Configuration
        // and planned lists are immutable after construction and are captured in
        // the snapshot baseline; the remaining values use the exact same order.
        static class MutationType<T>
        {internal static readonly bool CanBeNull=!typeof(T).IsValueType||Nullable.GetUnderlyingType(typeof(T))!=null;}
        struct MutationMatcher
        {
            readonly ExperimentMutationSnapshot snapshot;int index;bool matches;
            internal MutationMatcher(ExperimentMutationSnapshot value,int start){snapshot=value;index=start;matches=value!=null&&start<=value.ValueCount;}
            internal void Check<T>(T value)
            {
                if(!matches)return;if(index>=snapshot.ValueCount){matches=false;return;}
                object prior=snapshot.ValueAt(index++);
                if(prior==null){if(!MutationType<T>.CanBeNull||!EqualityComparer<T>.Default.Equals(default(T),value))matches=false;return;}
                if(!(prior is T)||!EqualityComparer<T>.Default.Equals((T)prior,value))matches=false;
            }
            internal void Check(double value)
            {
                if(!matches)return;if(index>=snapshot.ValueCount){matches=false;return;}object prior=snapshot.ValueAt(index++);
                if(!(prior is double)||BitConverter.DoubleToInt64Bits((double)prior)!=BitConverter.DoubleToInt64Bits(value))matches=false;
            }
            internal void Check(double? value)
            {
                if(!matches)return;if(index>=snapshot.ValueCount){matches=false;return;}object prior=snapshot.ValueAt(index++);
                if(prior==null){if(value.HasValue)matches=false;return;}
                if(!value.HasValue||!(prior is double)||BitConverter.DoubleToInt64Bits((double)prior)!=BitConverter.DoubleToInt64Bits(value.Value))matches=false;
            }
            internal bool Complete {get{return matches&&index==snapshot.ValueCount;}}
        }
        internal bool MatchesMutationSnapshot(ExperimentMutationSnapshot snapshot)
        {
            if(snapshot==null)return false;
            int baseline=13+4+4;foreach(var list in lists)baseline+=2*list.Length;
            var m=new MutationMatcher(snapshot,baseline);
            m.Check((int)Phase);m.Check(BlockIndex);m.Check(Segment);m.Check(PracticeAttempt);m.Check(practiceCorrect);
            m.Check(questionIndex);m.Check(trialIndex);m.Check((int)beforePause);m.Check(resumeCountdown);m.Check(questionnaireInstructionPending);
            m.Check(countdownAt);m.Check(restAt);m.Check(ratingAt);m.Check(pausedAt);m.Check(activeAccum);m.Check(activeSince);
            m.Check(EndReason.HasValue?(int?)(int)EndReason.Value:null);m.Check(LockedControl.HasValue?(int?)(int)LockedControl.Value:null);m.Check(sourceConfirmationOwner);m.Check(sourceConfirmationSequence);
            foreach(var colour in MutationColours)m.Check(held.Contains(colour));
            m.Check(practice==null?-1:practice.Length);
            if(practice!=null)foreach(var stimulus in practice){m.Check((int)stimulus.Word);m.Check((int)stimulus.Ink);}
            m.Check(trials.Count);foreach(var trial in trials)MatchMutationTrial(ref m,trial);
            MatchMutationTrial(ref m,current);m.Check(current==null?-1:trials.IndexOf(current));m.Check(ratings.Count);
            foreach(var rating in ratings){m.Check(rating.BlockIndex);m.Check(rating.ItemId);m.Check(rating.Value);m.Check(rating.Time);m.Check(rating.Language);m.Check(rating.Version);}
            m.Check(events.Count);return m.Complete;
        }
        static void MatchMutationTrial(ref MutationMatcher m,TrialRecord trial)
        {
            m.Check(trial!=null);if(trial==null)return;
            m.Check(trial.TrialId);m.Check(trial.SessionId);m.Check(trial.ListId);m.Check(trial.IsPractice);m.Check(trial.PracticeAttempt);
            m.Check(trial.BlockIndex);m.Check(trial.PlannedIndex);m.Check(trial.Segment);m.Check(trial.Condition.HasValue?(int?)(int)trial.Condition.Value:null);
            m.Check(trial.Stimulus==null?(int?)null:(int)trial.Stimulus.Word);m.Check(trial.Stimulus==null?(int?)null:(int)trial.Stimulus.Ink);
            m.Check(trial.Onset);m.Check(trial.ResponseAt);m.Check(trial.Response.HasValue?(int?)(int)trial.Response.Value:null);m.Check(trial.Correct);m.Check((int)trial.Outcome);m.Check(trial.TechnicalIssue);
        }
    }
}
