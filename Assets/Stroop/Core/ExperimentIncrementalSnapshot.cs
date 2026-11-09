using System;
using System.Collections.Generic;

namespace AvatarStroop.Core
{
    // Immutable scalar state for an in-memory development transaction. It is
    // not a hardware timestamp or a complete internal-clock representation.
    internal struct ExperimentIncrementalScalars : IEquatable<ExperimentIncrementalScalars>
    {
        internal Phase Phase,BeforePause;internal int Block,Segment,PracticeAttempt,PracticeCorrect,QuestionIndex,TrialIndex,HeldMask,CurrentIndex,EventCount;
        internal bool ResumeCountdown,InstructionPending;internal double CountdownAt,RestAt,RatingAt,PausedAt,ActiveAccum;internal double? ActiveSince;
        internal EndReason? EndReason;internal ControlSelection? Selection;internal string Owner;internal long Confirmation;
        internal ExperimentMutationTrialSnapshot? Current;
        static bool Bits(double a,double b){return BitConverter.DoubleToInt64Bits(a)==BitConverter.DoubleToInt64Bits(b);}
        static bool Bits(double? a,double? b){return a.HasValue==b.HasValue&&(!a.HasValue||Bits(a.Value,b.Value));}
        public bool Equals(ExperimentIncrementalScalars x)
        {
            return Phase==x.Phase&&BeforePause==x.BeforePause&&Block==x.Block&&Segment==x.Segment&&PracticeAttempt==x.PracticeAttempt&&
                PracticeCorrect==x.PracticeCorrect&&QuestionIndex==x.QuestionIndex&&TrialIndex==x.TrialIndex&&HeldMask==x.HeldMask&&CurrentIndex==x.CurrentIndex&&
                EventCount==x.EventCount&&ResumeCountdown==x.ResumeCountdown&&InstructionPending==x.InstructionPending&&
                Bits(CountdownAt,x.CountdownAt)&&Bits(RestAt,x.RestAt)&&Bits(RatingAt,x.RatingAt)&&Bits(PausedAt,x.PausedAt)&&Bits(ActiveAccum,x.ActiveAccum)&&
                Bits(ActiveSince,x.ActiveSince)&&EndReason==x.EndReason&&Selection==x.Selection&&Owner==x.Owner&&Confirmation==x.Confirmation&&
                Current.HasValue==x.Current.HasValue&&(!Current.HasValue||Current.Value.Equals(x.Current.Value));
        }
    }
    internal sealed class ExperimentIncrementalPractice
    {
        readonly Stimulus[] items;
        internal int Count {get{return items==null?-1:items.Length;}}
        internal ExperimentIncrementalPractice(Stimulus[] source){items=source==null?null:(Stimulus[])source.Clone();}
        internal bool Matches(Stimulus[] source)
        {
            if(items==null||source==null)return items==null&&source==null;if(items.Length!=source.Length)return false;
            for(int i=0;i<items.Length;i++)if(items[i].Word!=source[i].Word||items[i].Ink!=source[i].Ink)return false;return true;
        }
        internal void AddReferenceValues(List<object> values)
        {values.Add(Count);if(items!=null)foreach(var stimulus in items){values.Add((int)stimulus.Word);values.Add((int)stimulus.Ink);}}
    }
    internal sealed class ExperimentIncrementalRatings
    {
        readonly RatingRecord[] items;
        internal int Count {get{return items.Length;}}
        internal ExperimentIncrementalRatings(List<RatingRecord> source){items=source.ToArray();}
        internal bool Matches(List<RatingRecord> source)
        {
            if(items.Length!=source.Count)return false;
            for(int i=0;i<items.Length;i++)if(!Object.ReferenceEquals(items[i],source[i]))return false;return true;
        }
        internal bool IsPrefixOf(List<RatingRecord> source)
        {if(items.Length>source.Count)return false;for(int i=0;i<items.Length;i++)if(!Object.ReferenceEquals(items[i],source[i]))return false;return true;}
        internal void AddReferenceValues(List<object> values)
        {values.Add(items.Length);foreach(var r in items){values.Add(r.BlockIndex);values.Add(r.ItemId);values.Add(r.Value);values.Add(r.Time);values.Add(r.Language);values.Add(r.Version);}}
    }
    public sealed class ExperimentIncrementalSnapshot
    {
        readonly object[] baseline;readonly ExperimentIncrementalScalars scalars;
        readonly ExperimentIncrementalPractice practice;readonly ExperimentIncrementalRatings ratings;readonly ExperimentPersistentTrials trials;
        public TaskSnapshot Task {get;private set;} public ControlSelection? Selection {get{return scalars.Selection;}}
        public string SourceOwner {get{return scalars.Owner;}} public long SourceConfirmationSequence {get{return scalars.Confirmation;}}
        public Phase? PausedFrom {get{return scalars.Phase==Phase.Paused?(Phase?)scalars.BeforePause:null;}}
        public int TrialCount {get{return trials.Count;}} public int CurrentTrialIndex {get{return scalars.CurrentIndex;}}
        public ExperimentMutationTrialSnapshot? CurrentTrial {get{return scalars.Current;}}
        internal ExperimentIncrementalScalars Scalars {get{return scalars;}}
        internal ExperimentIncrementalPractice Practice {get{return practice;}}
        internal ExperimentIncrementalRatings Ratings {get{return ratings;}}
        internal ExperimentPersistentTrials TrialVector {get{return trials;}}
        internal ExperimentIncrementalSnapshot(object[] baseline,ExperimentIncrementalScalars scalars,ExperimentIncrementalPractice practice,
            ExperimentIncrementalRatings ratings,ExperimentPersistentTrials trials)
        {
            this.baseline=baseline;this.scalars=scalars;this.practice=practice;this.ratings=ratings;this.trials=trials;
            bool display=scalars.Phase==Phase.MainTask&&scalars.Current.HasValue;
            Condition? condition=scalars.Block>=0&&scalars.Block<4?(Condition?)((Condition)baseline[13+scalars.Block]):null;
            Task=new TaskSnapshot((string)baseline[0],scalars.Block,scalars.Segment,display?scalars.Current.Value.TrialId:null,
                display?(Colour?)scalars.Current.Value.Word:null,display?(Colour?)scalars.Current.Value.Ink:null,scalars.Phase,condition);
        }
        internal ExperimentIncrementalSnapshot Next(ExperimentIncrementalScalars next,ExperimentIncrementalPractice nextPractice,
            ExperimentIncrementalRatings nextRatings,ExperimentPersistentTrials nextTrials)
        {return new ExperimentIncrementalSnapshot(baseline,next,nextPractice,nextRatings,nextTrials);}
        public bool TryGetTrial(int index,out ExperimentMutationTrialSnapshot trial){return trials.TryGet(index,out trial);}
        static void AddTrial(List<object> values,ExperimentMutationTrialSnapshot? value)
        {
            values.Add(value.HasValue);if(!value.HasValue)return;var t=value.Value;
            values.Add(t.TrialId);values.Add(t.SessionId);values.Add(t.ListId);values.Add(t.IsPractice);values.Add(t.PracticeAttempt);
            values.Add(t.BlockIndex);values.Add(t.PlannedIndex);values.Add(t.Segment);values.Add(t.Condition.HasValue?(int?)(int)t.Condition.Value:null);
            values.Add((int?)((int)t.Word));values.Add((int?)((int)t.Ink));values.Add(t.Onset);values.Add(t.ResponseAt);
            values.Add(t.Response.HasValue?(int?)(int)t.Response.Value:null);values.Add(t.Correct);values.Add((int)t.Outcome);values.Add(t.TechnicalIssue);
        }
        // Test-only expansion; production snapshots never materialize a full
        // boxed historical array per step.
        internal object[] ExportReferenceValuesForTest()
        {
            var v=new List<object>(baseline);var s=scalars;
            v.Add((int)s.Phase);v.Add(s.Block);v.Add(s.Segment);v.Add(s.PracticeAttempt);v.Add(s.PracticeCorrect);
            v.Add(s.QuestionIndex);v.Add(s.TrialIndex);v.Add((int)s.BeforePause);v.Add(s.ResumeCountdown);v.Add(s.InstructionPending);
            v.Add(s.CountdownAt);v.Add(s.RestAt);v.Add(s.RatingAt);v.Add(s.PausedAt);v.Add(s.ActiveAccum);v.Add(s.ActiveSince);
            v.Add(s.EndReason.HasValue?(int?)(int)s.EndReason.Value:null);v.Add(s.Selection.HasValue?(int?)(int)s.Selection.Value:null);v.Add(s.Owner);v.Add(s.Confirmation);
            for(int i=0;i<4;i++)v.Add((s.HeldMask&(1<<i))!=0);practice.AddReferenceValues(v);
            v.Add(trials.Count);for(int i=0;i<trials.Count;i++){ExperimentMutationTrialSnapshot t;if(!trials.TryGet(i,out t))throw new InvalidOperationException("Trial vector unavailable");AddTrial(v,t);}
            AddTrial(v,s.Current);v.Add(s.CurrentIndex);ratings.AddReferenceValues(v);v.Add(s.EventCount);return v.ToArray();
        }
    }
}
