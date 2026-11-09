using System;

namespace AvatarStroop.Core
{
    // Immutable producer-time copy. It never retains the mutable TrialRecord;
    // intermediate states are diagnostic and cannot authorize presentation.
    public readonly struct ExperimentMutationTrialSnapshot : IEquatable<ExperimentMutationTrialSnapshot>
    {
        public string TrialId {get;} public string SessionId {get;} public string ListId {get;}
        public bool IsPractice {get;} public int PracticeAttempt {get;} public int BlockIndex {get;} public int PlannedIndex {get;} public int Segment {get;}
        public Condition? Condition {get;} public Colour Word {get;} public Colour Ink {get;}
        public double? Onset {get;} public double? ResponseAt {get;} public Colour? Response {get;} public bool? Correct {get;}
        public TrialOutcome Outcome {get;} public bool TechnicalIssue {get;}
        internal ExperimentMutationTrialSnapshot(TrialRecord source)
        {
            if(source==null||source.Stimulus==null)throw new ArgumentException("Trial and immutable stimulus required");
            TrialId=source.TrialId;SessionId=source.SessionId;ListId=source.ListId;IsPractice=source.IsPractice;PracticeAttempt=source.PracticeAttempt;
            BlockIndex=source.BlockIndex;PlannedIndex=source.PlannedIndex;Segment=source.Segment;Condition=source.Condition;Word=source.Stimulus.Word;Ink=source.Stimulus.Ink;
            Onset=source.Onset;ResponseAt=source.ResponseAt;Response=source.Response;Correct=source.Correct;Outcome=source.Outcome;TechnicalIssue=source.TechnicalIssue;
        }
        static bool SameTime(double? a,double? b)
        {return a.HasValue==b.HasValue&&(!a.HasValue||BitConverter.DoubleToInt64Bits(a.Value)==BitConverter.DoubleToInt64Bits(b.Value));}
        public bool Equals(ExperimentMutationTrialSnapshot value)
        {return TrialId==value.TrialId&&SessionId==value.SessionId&&ListId==value.ListId&&IsPractice==value.IsPractice&&PracticeAttempt==value.PracticeAttempt&&
            BlockIndex==value.BlockIndex&&PlannedIndex==value.PlannedIndex&&Segment==value.Segment&&Condition==value.Condition&&Word==value.Word&&Ink==value.Ink&&
            SameTime(Onset,value.Onset)&&SameTime(ResponseAt,value.ResponseAt)&&Response==value.Response&&Correct==value.Correct&&Outcome==value.Outcome&&TechnicalIssue==value.TechnicalIssue;}
        public override bool Equals(object value){return value is ExperimentMutationTrialSnapshot&&Equals((ExperimentMutationTrialSnapshot)value);}
        public override int GetHashCode(){return TrialId==null?0:TrialId.GetHashCode();}
    }
}
