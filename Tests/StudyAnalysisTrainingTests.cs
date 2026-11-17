#if !UNITY_5_3_OR_NEWER
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    public static class StudyAnalysisTrainingTests
    {
        internal static void Trial(StudyAnalysis analysis,string id,string stage,int attempt,int round,bool correct=true,string rule="InkColor",bool search=false,bool interrupted=false,string eyePolicy=null,double onset=2)
        {
            string phase=stage=="tutorial"?"Tutorial":stage=="formal"?"MainTask":"Practice";
            int block=stage=="formal"?0:-1;
            var common=new List<string>{"legacy_phase",phase,"training_protocol","tutorial-exam-v1","learning_stage",stage,
                "practice_attempt",attempt.ToString(CultureInfo.InvariantCulture),"tutorial_round",round.ToString(CultureInfo.InvariantCulture),
                "task_kind",search?"VisualSearch":"Stroop","response_rule",rule};
            if(eyePolicy!=null)common.AddRange(new[]{"eye_recording_policy",eyePolicy});
            var plan=new List<string>(common){"list","training-test"};
            var response=new List<string>(common){"correct",correct.ToString(),"onset",onset.ToString("R",CultureInfo.InvariantCulture),"rt_seconds",".5"};
            if(search)
            {
                var layout=VisualSearchPlans.MainList(71,0)[0].VisualSearch;
                plan.AddRange(new[]{"visual_search_layout",layout.Serialize(),"search_schema",VisualSearchTrial.Schema,"set_size",layout.SetSize.ToString(CultureInfo.InvariantCulture),
                    "target_item",layout.TargetId,"target_colour",layout.TargetColour.ToString(),"target_pattern",layout.TargetPattern.ToString(),"layout_seed",layout.LayoutSeed.ToString(CultureInfo.InvariantCulture)});
                response.AddRange(new[]{"selected_item",correct?layout.TargetId:layout.Items.First(x=>x.Id!=layout.TargetId).Id});
            }
            else
            {
                plan.AddRange(new[]{"planned_word","Red","planned_ink","Green"});
                string expected=rule=="WordMeaning"?"Red":"Green";
                response.AddRange(new[]{"response",correct?expected:"Blue"});
            }
            StudyAnalysisTests.Event(analysis,"trial_planned",id,block,onset-1,plan.ToArray());
            var presentation=new List<string>(common){"task_onset_seconds",onset.ToString("R",CultureInfo.InvariantCulture),"task_onset_basis","synthetic","task_onset_clock_id","synthetic-clock"};
            StudyAnalysisTests.Event(analysis,"stimulus_presented",id,block,onset,presentation.ToArray());
            StudyAnalysisTests.Event(analysis,interrupted?"trial_interrupted":"first_response",id,block,onset+.52,response.ToArray());
        }
        internal static void Completion(StudyAnalysis a,int attempt,int correct,string rule="InkColor",bool search=false)
        {
            StudyAnalysisTests.Event(a,"practice_completed",null,-1,4,"legacy_phase","Practice","training_protocol","tutorial-exam-v1","learning_stage","practice_exam",
                "practice_attempt",attempt.ToString(CultureInfo.InvariantCulture),"attempt",attempt.ToString(CultureInfo.InvariantCulture),"tutorial_round",attempt.ToString(CultureInfo.InvariantCulture),
                "task_kind",search?"VisualSearch":"Stroop","response_rule",rule,"planned","10","correct_first",correct.ToString(CultureInfo.InvariantCulture),"threshold","8","passed",(correct>=8).ToString());
        }
        public static void Register(TestSuite suite)
        {
            suite.Run("study training keeps tutorial exam attempts and formal trials separate for all task modes",()=>{
                foreach(string rule in new[]{"InkColor","WordMeaning","VisualSearch"})
                {
                    bool search=rule=="VisualSearch";var a=StudyAnalysisTests.Analysis();
                    Trial(a,"tutorial1","tutorial",0,1,false,rule,search);
                    for(int i=0;i<10;i++)Trial(a,"failed"+i,"practice_exam",1,1,i<7,rule,search);
                    Completion(a,1,7,rule,search);
                    Trial(a,"tutorial2","tutorial",0,2,true,rule,search);
                    Trial(a,"interrupted","practice_exam",2,2,true,rule,search,true);
                    for(int i=0;i<10;i++)Trial(a,"passed"+i,"practice_exam",2,2,i<8,rule,search);
                    Completion(a,2,8,rule,search);Trial(a,"formal","formal",2,2,true,rule,search);a.Finish();
                    Check.Equal(2,a.Trials.Count(x=>x.Scope=="Tutorial"));Check.Equal(21,a.Trials.Count(x=>x.Scope=="PracticeExam"));Check.Equal(1,a.Trials.Count(x=>x.Scope=="Formal"));
                    Check.Equal(1,a.Summaries().Where(x=>x.Scope=="Formal").Sum(x=>x.Trials));
                    Check.Equal(2,a.Summaries().Where(x=>x.Scope=="PracticeExam").Select(x=>x.PracticeAttempt).Distinct().Count());
                    var failed=a.TrainingAttempts.Single(x=>x.Attempt==1);var passed=a.TrainingAttempts.Single(x=>x.Attempt==2);
                    Check.Equal(false,failed.Passed);Check.Equal(true,passed.Passed);Check.Equal(7,failed.ReconstructedCorrect);Check.Equal(8,passed.ReconstructedCorrect);
                    Check.Equal(0,failed.Flags.Count);Check.Equal(0,passed.Flags.Count);Check.Equal(11,passed.ReconstructedTrialCount);Check.Equal(10,passed.ReconstructedEligible);
                    Check.True(!a.Trials.Single(x=>x.Id=="interrupted").AccuracyIncluded);
                }
            });
            suite.Run("study training retains legacy six of eight without imposing the new exam protocol",()=>{
                var a=StudyAnalysisTests.Analysis();
                StudyAnalysisTests.Event(a,"trial_planned","legacy-practice",-1,1,"legacy_phase","Practice","planned_word","Red","planned_ink","Green");
                StudyAnalysisTests.Event(a,"practice_completed",null,-1,3,"legacy_phase","Practice","attempt","1","planned","8","correct_first","6");a.Finish();
                var p=a.TrainingAttempts.Single();Check.Equal("Practice",a.Trials.Single().Scope);Check.Equal("Practice",p.Scope);Check.Equal(6,p.Threshold);Check.Equal(true,p.Passed);
                Check.Equal("legacy_8_item_rule_inferred",p.ThresholdProvenance);Check.Equal(0,p.Flags.Count);Check.True(!a.Summaries().Any(x=>x.Scope=="Formal"));
            });
            suite.Run("study training flags missing completions duplicate score summaries and inconsistent totals",()=>{
                var a=StudyAnalysisTests.Analysis();Trial(a,"incomplete","practice_exam",3,3);
                Trial(a,"mismatch","practice_exam",4,4);Completion(a,4,8);Completion(a,4,8);a.Finish();
                var p=a.TrainingAttempts.Single(x=>x.Attempt==3);Check.True(!p.CompletionObserved&&!p.Passed.HasValue&&p.Flags.Contains("completion_not_recorded"));
                p=a.TrainingAttempts.Single(x=>x.Attempt==4);Check.True(p.Flags.Contains("duplicate_completion_summary")&&p.Flags.Contains("exam_trial_coverage_mismatch")&&p.Flags.Contains("exam_correct_count_mismatch"));
            });
            suite.Run("study unknown and conflicting learning stages never enter formal aggregates",()=>{
                var a=StudyAnalysisTests.Analysis();Trial(a,"unknown","setup",0,0);
                Trial(a,"conflict","tutorial",0,1);
                StudyAnalysisTests.Event(a,"first_response","conflict",-1,3,"legacy_phase","Tutorial","training_protocol","tutorial-exam-v1","learning_stage","formal","practice_attempt","0","tutorial_round","1","response","Green","correct","True","onset","2","rt_seconds","1");
                a.Finish();Check.True(!a.Summaries().Any(x=>x.Scope=="Formal"));Check.True(a.Trials.All(x=>!x.AccuracyIncluded));
                Check.True(a.Trials.Single(x=>x.Id=="conflict").Flags.Contains("learning_stage_conflict"));
            });
        }
    }
}
#endif
