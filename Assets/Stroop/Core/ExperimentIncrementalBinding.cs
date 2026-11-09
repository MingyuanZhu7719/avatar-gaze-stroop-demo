using System;

namespace AvatarStroop.Core
{
    public sealed partial class Experiment
    {
        ExperimentIncrementalJournal incrementalMutationJournal;
        // Separate constructor keeps the full snapshot implementation as an
        // independent reference and leaves the ordinary Quest path unchanged.
        public Experiment(SessionConfiguration configuration,IClock clock,ExperimentIncrementalOptions options)
            :this(configuration,clock)
        {
            if(options==null)throw new ArgumentNullException("options");
            if(configuration.IsNamedDesign)throw new InvalidOperationException("Legacy incremental observation does not support named-option provenance");
            incrementalMutationJournal=new ExperimentIncrementalJournal(this,options);
        }
        public bool IncrementalObservationEnabled {get{return incrementalMutationJournal!=null;}}
        public bool IncrementalObservationFaulted {get{return incrementalMutationJournal!=null&&incrementalMutationJournal.Terminal;}}
        public string IncrementalObservationIssue {get{return incrementalMutationJournal==null?"not_enabled":incrementalMutationJournal.Issue;}}
        public long IncrementalEstimatedBytesUsed {get{return incrementalMutationJournal==null?0:incrementalMutationJournal.EstimatedBytesUsed;}}
        public ExperimentIncrementalSnapshot InitialIncrementalSnapshot {get{return incrementalMutationJournal==null?null:incrementalMutationJournal.Initial;}}
        public bool TryReadIncrementalAfter(long after,out ExperimentIncrementalCommit next,out long head,out ExperimentIncrementalSnapshot current)
        {next=null;head=0;current=null;return incrementalMutationJournal!=null&&incrementalMutationJournal.ReadNext(after,out next,out head,out current);}
        public bool IsIncrementalRevisionCurrent(long revision)
        {return incrementalMutationJournal!=null&&incrementalMutationJournal.IsCurrent(revision);}

        internal int IncrementalBaselineValueCount()
        {int result=13+4+4;foreach(var list in lists)result+=2*list.Length;return result;}
        ExperimentIncrementalScalars CaptureIncrementalScalars()
        {
            int mask=0;for(int i=0;i<MutationColours.Length;i++)if(held.Contains(MutationColours[i]))mask|=1<<i;
            return new ExperimentIncrementalScalars{
                Phase=Phase,BeforePause=beforePause,Block=BlockIndex,Segment=Segment,PracticeAttempt=PracticeAttempt,PracticeCorrect=practiceCorrect,
                QuestionIndex=questionIndex,TrialIndex=trialIndex,HeldMask=mask,CurrentIndex=current==null?-1:trials.IndexOf(current),EventCount=events.Count,
                ResumeCountdown=resumeCountdown,InstructionPending=questionnaireInstructionPending,CountdownAt=countdownAt,RestAt=restAt,RatingAt=ratingAt,
                PausedAt=pausedAt,ActiveAccum=activeAccum,ActiveSince=activeSince,EndReason=EndReason,Selection=LockedControl,Owner=sourceConfirmationOwner,
                Confirmation=sourceConfirmationSequence,Current=current==null?(ExperimentMutationTrialSnapshot?)null:new ExperimentMutationTrialSnapshot(current)};
        }
        internal bool MatchesIncrementalSnapshot(ExperimentIncrementalSnapshot snapshot)
        {
            if(snapshot==null||!snapshot.Scalars.Equals(CaptureIncrementalScalars())||!snapshot.Practice.Matches(practice)||!snapshot.Ratings.Matches(ratings)||
                snapshot.TrialCount!=trials.Count)return false;
            for(int i=0;i<trials.Count;i++)
            {ExperimentMutationTrialSnapshot prior;if(!snapshot.TrialVector.TryGet(i,out prior)||!prior.Equals(new ExperimentMutationTrialSnapshot(trials[i])))return false;}
            return true;
        }
        // Called only on the producer's owner thread after a semantic write.
        // No clock, live renderer or external callback is read here.
        internal ExperimentIncrementalSnapshot CaptureIncrementalSnapshot(ExperimentIncrementalSnapshot previous,object[] baseline,
            ExperimentPersistentTrials initialTrials,ExperimentPersistentMutationBudget budget)
        {
            var scalars=CaptureIncrementalScalars();var vector=previous==null?initialTrials:previous.TrialVector;
            if(vector==null||trials.Count<vector.Count||trials.Count>vector.Count+1)throw new InvalidOperationException("Trial history unavailable");
            int changed=-1;
            for(int i=0;i<vector.Count;i++)
            {
                ExperimentMutationTrialSnapshot prior;if(!vector.TryGet(i,out prior))return null;
                var actual=new ExperimentMutationTrialSnapshot(trials[i]);if(!prior.Equals(actual))
                {if(changed>=0||i!=scalars.CurrentIndex)throw new InvalidOperationException("Unexpected non-current trial mutation");changed=i;}
            }
            if(changed>=0)
            {
                if(trials.Count!=vector.Count)throw new InvalidOperationException("Concurrent append and trial mutation");
                ExperimentPersistentTrials updated;if(!vector.TryReplace(changed,new ExperimentMutationTrialSnapshot(trials[changed]),out updated))return null;vector=updated;
            }
            if(trials.Count==vector.Count+1)
            {
                if(scalars.CurrentIndex!=vector.Count)throw new InvalidOperationException("Unexpected trial append");
                ExperimentPersistentTrials appended;if(!vector.TryAppend(new ExperimentMutationTrialSnapshot(trials[vector.Count]),out appended))return null;vector=appended;
            }
            var practiceSnapshot=previous==null?null:previous.Practice;
            if(practiceSnapshot==null||!practiceSnapshot.Matches(practice))
            {if(!budget.TryReserve(512L+(practice==null?0:practice.Length*16L)))return null;practiceSnapshot=new ExperimentIncrementalPractice(practice);}
            var ratingSnapshot=previous==null?null:previous.Ratings;
            if(ratingSnapshot==null||!ratingSnapshot.Matches(ratings))
            {
                if(ratingSnapshot!=null&&!ratingSnapshot.IsPrefixOf(ratings))throw new InvalidOperationException("Unexpected rating mutation");
                if(!budget.TryReserve(512L+ratings.Count*16L))return null;ratingSnapshot=new ExperimentIncrementalRatings(ratings);
            }
            return previous==null?new ExperimentIncrementalSnapshot(baseline,scalars,practiceSnapshot,ratingSnapshot,vector):
                previous.Next(scalars,practiceSnapshot,ratingSnapshot,vector);
        }
    }
}
