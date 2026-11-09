using System;
using System.Collections.Generic;
using AvatarStroop.Core;

namespace AvatarStroop.Runtime
{
    // Immutable value copies of one ended Simulation participant's own task
    // records. No worker, file, network or gaze evidence is represented.
    internal sealed class ViveNamedTaskRecordSnapshot
    {
        internal sealed class Trial
        {
            internal readonly string Id,Session,List;
            internal readonly bool Practice,TechnicalIssue;
            internal readonly int Attempt,Block,Planned,Segment;
            internal readonly Colour Word,Ink;
            internal readonly double? Onset,ResponseAt,ReactionSeconds;
            internal readonly Colour? Response;
            internal readonly bool? Correct;
            internal readonly TrialOutcome Outcome;
            internal readonly ViveNamedOptionProvenance Named;
            internal readonly ViveNamedSimulationPlan Plan;internal readonly ViveNamedSimulationBlock NamedBlock;
            internal Trial(TrialRecord r)
            {
                if(r==null||r.Stimulus==null)throw new InvalidOperationException();
                Id=r.TrialId;Session=r.SessionId;List=r.ListId;Practice=r.IsPractice;Attempt=r.PracticeAttempt;
                Block=r.BlockIndex;Planned=r.PlannedIndex;Segment=r.Segment;Word=r.Stimulus.Word;Ink=r.Stimulus.Ink;
                Onset=r.Onset;ResponseAt=r.ResponseAt;ReactionSeconds=r.ReactionSeconds;Response=r.Response;Correct=r.Correct;
                Outcome=r.Outcome;TechnicalIssue=r.TechnicalIssue;Named=r.NamedOption;Plan=r.NamedPlan;NamedBlock=r.NamedBlock;
            }
        }
        internal sealed class Rating
        {
            internal readonly int Block,Value;internal readonly string Item,Language,Version;internal readonly double Time;
            internal Rating(RatingRecord r)
            {if(r==null)throw new InvalidOperationException();Block=r.BlockIndex;Value=r.Value;Item=r.ItemId;Time=r.Time;Language=r.Language;Version=r.Version;}
        }
        internal readonly Trial[] Trials;internal readonly Rating[] Ratings;
        internal ViveNamedTaskRecordSnapshot(IReadOnlyList<TrialRecord> trials,IReadOnlyList<RatingRecord> ratings)
        {
            if(trials==null||ratings==null||trials.Count>1024||ratings.Count>128)throw new InvalidOperationException("Task record limit exceeded.");
            Trials=new Trial[trials.Count];Ratings=new Rating[ratings.Count];
            for(int i=0;i<Trials.Length;i++)Trials[i]=new Trial(trials[i]);
            for(int i=0;i<Ratings.Length;i++)Ratings[i]=new Rating(ratings[i]);
        }
    }
}
