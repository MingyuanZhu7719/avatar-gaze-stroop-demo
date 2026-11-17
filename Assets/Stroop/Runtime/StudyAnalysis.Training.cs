#if !UNITY_5_3_OR_NEWER
using System;
using System.Collections.Generic;
using System.Linq;

namespace AvatarStroop.Runtime
{
    public sealed class StudyTrainingAttempt
    {
        public string Session, TrainingProtocol, Scope, TaskKind, Rule, ThresholdProvenance;
        public int Attempt, SummaryCount;
        public int? TutorialRound, Planned, CorrectFirst, Threshold, ReconstructedTrialCount, ReconstructedEligible, ReconstructedCorrect;
        public bool? Passed;
        public double? CompletionTime;
        public readonly List<string> Flags=new List<string>();
        public bool CompletionObserved {get{return SummaryCount>0;}}
        internal void Flag(string value){if(!Flags.Contains(value))Flags.Add(value);}
    }

    public sealed partial class StudyAnalysis
    {
        public readonly List<StudyTrainingAttempt> TrainingAttempts=new List<StudyTrainingAttempt>();
        public string TrainingProtocol {get;private set;}

        internal static string TrialScope(string protocol,string stage,string phase)
        {
            // An unknown stage never becomes a formal observation by exclusion.
            if(stage!=null)
                return stage=="tutorial"&&phase!=null&&phase.StartsWith("Tutorial",StringComparison.Ordinal)?"Tutorial":
                    stage=="practice_exam"&&phase!=null&&phase.StartsWith("Practice",StringComparison.Ordinal)?"PracticeExam":
                    stage=="formal"&&phase=="MainTask"?"Formal":"Unknown";
            if(phase!=null&&phase.StartsWith("Tutorial",StringComparison.Ordinal))return "Tutorial";
            if(phase!=null&&phase.StartsWith("Practice",StringComparison.Ordinal))return protocol=="tutorial-exam-v1"?"PracticeExam":"Practice";
            return phase=="MainTask"?"Formal":"Unknown";
        }
        static int? OptionalInt(IDictionary<string,string> fields,string key)
        {int value;return Int32.TryParse(Get(fields,key),out value)?value:(int?)null;}

        void ApplyTrainingTrial(StudyTrial trial,IDictionary<string,string> fields)
        {
            string protocol=Get(fields,"training_protocol"),stage=Get(fields,"learning_stage");
            int? attempt=OptionalInt(fields,"practice_attempt"),round=OptionalInt(fields,"tutorial_round");
            if(protocol!=null){if(trial.TrainingProtocol!=null&&trial.TrainingProtocol!=protocol)trial.Flag("training_protocol_conflict");else trial.TrainingProtocol=protocol;}
            if(stage!=null){if(trial.LearningStage!=null&&trial.LearningStage!=stage)trial.Flag("learning_stage_conflict");else trial.LearningStage=stage;}
            if(attempt.HasValue){if(trial.PracticeAttempt.HasValue&&trial.PracticeAttempt!=attempt)trial.Flag("practice_attempt_conflict");else trial.PracticeAttempt=attempt;}
            if(round.HasValue){if(trial.TutorialRound.HasValue&&trial.TutorialRound!=round)trial.Flag("tutorial_round_conflict");else trial.TutorialRound=round;}
            if(trial.TrainingProtocol=="tutorial-exam-v1"&&
                ((trial.Scope=="PracticeExam"&&(!trial.PracticeAttempt.HasValue||trial.PracticeAttempt<1))||
                 (trial.Scope=="Tutorial"&&(!trial.TutorialRound.HasValue||trial.TutorialRound<1))))trial.Flag("missing_or_invalid_training_identity");
        }

        void ReadTrainingEvent(string kind,IDictionary<string,string> fields,string rule,string taskKind,double? time)
        {
            string protocol=Get(fields,"training_protocol");
            if(protocol!=null)
            {
                if(TrainingProtocol!=null&&TrainingProtocol!=protocol)Warn("Training protocol changed within the session; recorded trial scopes remain separate.");
                else TrainingProtocol=protocol;
            }
            if(kind!="practice_completed")return;
            int attempt=Int(fields,"attempt",Int(fields,"practice_attempt",-1));
            string scope=protocol=="tutorial-exam-v1"?"PracticeExam":"Practice";
            var item=TrainingAttempts.FirstOrDefault(x=>x.TrainingProtocol==protocol&&x.Attempt==attempt);
            if(item==null){item=new StudyTrainingAttempt{Session=SessionId,TrainingProtocol=protocol,Scope=scope,TaskKind=taskKind??"Stroop",Rule=rule??"InkColor",Attempt=attempt};TrainingAttempts.Add(item);}
            item.SummaryCount++;
            if(item.SummaryCount>1){item.Flag("duplicate_completion_summary");return;}
            item.TutorialRound=OptionalInt(fields,"tutorial_round");item.Planned=OptionalInt(fields,"planned");
            item.CorrectFirst=OptionalInt(fields,"correct_first");item.Threshold=OptionalInt(fields,"threshold");
            item.Passed=Truth(fields,"passed");item.CompletionTime=time;
            item.ThresholdProvenance=item.Threshold.HasValue?"explicit":"not_recorded";
            // Historical practice was 6/8. Do not impose the new 8/10 rule on it.
            if(protocol==null&&item.Planned==8&&!item.Threshold.HasValue)
            {item.Threshold=6;item.ThresholdProvenance="legacy_8_item_rule_inferred";}
            if(protocol==null&&!item.Passed.HasValue&&item.CorrectFirst.HasValue&&item.Threshold.HasValue)
                item.Passed=item.CorrectFirst>=item.Threshold;
            if(attempt<1)item.Flag("invalid_attempt");
            if(!item.Planned.HasValue||item.Planned<1||!item.CorrectFirst.HasValue||item.CorrectFirst<0||item.CorrectFirst>item.Planned)item.Flag("invalid_score");
            if(!item.Threshold.HasValue||item.Threshold<1||item.Threshold>item.Planned)item.Flag("invalid_threshold");
            if(item.Passed.HasValue&&item.CorrectFirst.HasValue&&item.Threshold.HasValue&&item.Passed!=(item.CorrectFirst>=item.Threshold))item.Flag("pass_declaration_mismatch");
            if(protocol=="tutorial-exam-v1"&&(item.Planned!=10||item.Threshold!=8||!item.Passed.HasValue))item.Flag("exam_protocol_mismatch");
        }

        void FinishTraining()
        {
            foreach(var group in Trials.Where(x=>x.Scope=="PracticeExam"&&x.PracticeAttempt>0)
                .GroupBy(x=>new{x.TrainingProtocol,x.PracticeAttempt}))
            {
                var item=TrainingAttempts.FirstOrDefault(x=>x.TrainingProtocol==group.Key.TrainingProtocol&&x.Attempt==group.Key.PracticeAttempt);
                if(item==null)
                {
                    var first=group.First();item=new StudyTrainingAttempt{Session=SessionId,TrainingProtocol=first.TrainingProtocol,Scope="PracticeExam",TaskKind=first.TaskKind,
                        Rule=first.Rule,Attempt=group.Key.PracticeAttempt.Value,TutorialRound=first.TutorialRound,ThresholdProvenance="not_recorded"};
                    item.Flag("completion_not_recorded");TrainingAttempts.Add(item);
                }
                item.ReconstructedTrialCount=group.Count();item.ReconstructedEligible=group.Count(x=>x.AccuracyIncluded);
                item.ReconstructedCorrect=group.Count(x=>x.AccuracyIncluded&&x.Correct==true);
                if(item.CompletionObserved)
                {
                    if(item.ReconstructedEligible!=item.Planned)item.Flag("exam_trial_coverage_mismatch");
                    if(item.ReconstructedCorrect!=item.CorrectFirst)item.Flag("exam_correct_count_mismatch");
                }
            }
            foreach(var item in TrainingAttempts.Where(x=>x.Scope=="PracticeExam"&&x.CompletionObserved&&!x.ReconstructedTrialCount.HasValue))item.Flag("exam_trials_missing");
            if(TrainingAttempts.Any(x=>x.Flags.Count>0))Warn("Some training attempts have incomplete or conflicting evidence; inspect training_attempts.csv before using the declared score or pass result.");
        }
    }
}
#endif
