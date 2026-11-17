using System;
using System.Collections.Generic;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    public static class ViveTutorialRuntimeTests
    {
        static ViveDevelopmentParticipant New()
        {
            var config=new SessionConfiguration("tutorial-runtime",73,new Allocation("tutorial-allocation",
                new[]{Condition.B,Condition.C,Condition.A,Condition.D},GazeTarget.Participant,new[]{0,1,2,3}),
                RunKind.Simulation,"en",5,30,true,0,true,true,true);
            config=SessionConfiguration.WithTrainingProtocol(config,TrainingProtocol.TutorialExamV1);
            return new ViveDevelopmentParticipant(config,new ManualClock(),DevelopmentFixtures.Geometry(),ViveFixedTrackpadMapping.Create(),
                ViveFixedTrackpadMapping.SectorThreshold,true);
        }
        static void Neutral(ViveDevelopmentParticipant p){p.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,false,0,0));}
        static void Answer(ViveDevelopmentParticipant p,bool correct=true)
        {
            Neutral(p);p.MarkPresented(p.CurrentTrial.TrialId);
            var colour=p.CurrentTrial.ExpectedResponse;if(!correct)colour=(Colour)(((int)colour+1)%4);
            double x=colour==Colour.Green?1:colour==Colour.Yellow?-1:0;
            double y=colour==Colour.Red?1:colour==Colour.Blue?-1:0;
            p.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,true,x,y));Neutral(p);
        }
        static void Aux(ViveDevelopmentParticipant p,bool confirm=false,int direction=0,bool visible=true)
        {p.UpdateAuxiliary(confirm,false,false,direction,visible);}
        static Dictionary<string,string> Fields(params string[] pairs)
        {var result=new Dictionary<string,string>();for(int i=0;i<pairs.Length;i+=2)result.Add(pairs[i],pairs[i+1]);return result;}
        static Dictionary<string,string> ExamPlan(int attempt,bool training=true)
        {
            var f=Fields("recording_plan_schema","1","plan_scope","PRACTICE","attempt",attempt.ToString(),
                "list","practice-73-"+attempt,"item_count",training?"10":"8","legacy_phase",training?"PracticeExamReady":"PracticeRetry");
            if(training)f.Add("training_protocol","tutorial-exam-v1");
            for(int i=0;i<(training?10:8);i++){f.Add("item_"+i.ToString("D2")+"_word","Red");f.Add("item_"+i.ToString("D2")+"_ink","Green");}
            return f;
        }
        public static void Register(TestSuite suite)
        {
            suite.Run("Tutorial participant menu requires visible feedback and separate fresh confirmation",()=>{
                var p=New();Neutral(p);p.OwnerBeginTutorial(Actor.Owner);Answer(p,false);
                Check.Equal(Phase.TutorialFeedback,p.Phase);Check.True(p.ParticipantView.TutorialFeedback.Contains("Incorrect"));
                Aux(p,true);Check.Equal(Phase.TutorialFeedback,p.Phase); // same/held answer edge cannot exit
                Aux(p);Aux(p,true,1);Check.Equal(1,p.TutorialMenuSelection);Check.Equal(Phase.TutorialFeedback,p.Phase);
                Aux(p);Aux(p,true,visible:false);Check.Equal(Phase.TutorialFeedback,p.Phase);
                Aux(p,visible:true);Aux(p);Aux(p,true);Check.Equal(Phase.PracticeExamReady,p.Phase);
                p.OwnerBeginPractice(Actor.Owner);for(int i=0;i<10;i++)Answer(p,i<8);
                Check.Equal(Phase.PracticeExamResult,p.Phase);Check.Equal(8,p.PracticeCorrect);
                Aux(p,true);Check.Equal(Phase.PracticeExamResult,p.Phase);Aux(p);Aux(p,true);
                Check.Equal(Phase.BlockPreparation,p.Phase);
            });
            suite.Run("Tutorial default continue does not start examination and withdrawal is available",()=>{
                var p=New();Neutral(p);p.OwnerBeginTutorial(Actor.Owner);Answer(p);
                Aux(p);Aux(p);Aux(p,true);Check.Equal(Phase.Tutorial,p.Phase);Check.Equal(0,p.PracticeAttempt);
                p.ParticipantWithdraw();Check.Equal(Phase.Ended,p.Phase);Check.Equal(EndReason.Withdrawn,p.EndReason);
            });
            suite.Run("Raw exam plan reader accepts attempt three and tenth item only with new protocol",()=>{
                var audit=new ViveRawPreparedPlanAudit();var plan=audit.Add(ExamPlan(3),null,-1,0,1,1,true);
                Check.Equal(10,plan.Items.Count);
                var trial=Fields("training_protocol","tutorial-exam-v1","learning_stage","practice_exam","list","practice-73-3",
                    "planned_index","9","planned_stimulus_schema","1","planned_word","Red","planned_ink","Green");
                audit.ObserveTrial(trial,-1,"session","session/p3/t9");
                Check.Throws<ViveRawPreparedPlanAudit.InvalidPlan>(()=>new ViveRawPreparedPlanAudit().Add(ExamPlan(3,false),null,-1,0,1,1,true));
                var bad=ExamPlan(1);bad.Remove("training_protocol");
                Check.Throws<ViveRawPreparedPlanAudit.InvalidPlan>(()=>new ViveRawPreparedPlanAudit().Add(bad,null,-1,0,1,1,true));
            });
            suite.Run("Raw tutorial retry reader requires observed tracking loss and same stimulus",()=>{
                var audit=new ViveRawPreparedPlanAudit();var trial=Fields("training_protocol","tutorial-exam-v1","learning_stage","tutorial",
                    "tutorial_round","1","list","tutorial-73-1","planned_index","0","planned_stimulus_schema","1","planned_word","Red","planned_ink","Green");
                audit.ObserveTrial(trial,-1,"session","session/tutorial-1/t0");
                trial.Add("tracking_retry_schema","1");trial.Add("retry_of","session/tutorial-1/t0");
                Check.Throws<ViveRawPreparedPlanAudit.InvalidPlan>(()=>audit.ObserveTrial(trial,-1,"session","session/tutorial-1/t0/retry-s1",1));
                audit.ObserveOutcome("trial_interrupted",Fields("reason","tracking_gap"),-1,0,"session/tutorial-1/t0");
                audit.ObserveTrial(trial,-1,"session","session/tutorial-1/t0/retry-s1",1);
                audit.ObserveOutcome("trial_interrupted",Fields("reason","tracking_gap"),-1,1,"session/tutorial-1/t0/retry-s1");
                trial["retry_of"]="session/tutorial-1/t0/retry-s1";trial["planned_ink"]="Blue";
                Check.Throws<ViveRawPreparedPlanAudit.InvalidPlan>(()=>audit.ObserveTrial(trial,-1,"session","session/tutorial-1/t0/retry-s2",2));
            });
        }
    }
}
