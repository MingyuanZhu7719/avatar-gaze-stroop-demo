using System;
using System.Linq;
using AvatarStroop.Core;

namespace AvatarStroop.Tests
{
    public static class TutorialTrainingTests
    {
        static SessionConfiguration Configuration(int mode)
        {
            var allocation=new Allocation("training",new[]{Condition.B,Condition.C,Condition.A,Condition.D},GazeTarget.Participant,new[]{0,1,2,3});
            var config=new SessionConfiguration("training-session",42,allocation,RunKind.Simulation,"en",5,
                responseRule:mode==1?ResponseRule.WordMeaning:ResponseRule.InkColor);
            if(mode==2)config=SessionConfiguration.WithTaskKind(config,TaskKind.VisualSearch);
            return SessionConfiguration.WithTrainingProtocol(config,TrainingProtocol.TutorialExamV1);
        }
        static Experiment New(ManualClock clock,int mode=0)
        {return new Experiment(Configuration(mode),clock);}
        static void StartTutorial(Experiment e)
        {e.BeginFamiliarization(Actor.Owner);e.BeginTutorial(Actor.Owner);}
        static void Answer(Experiment e,ManualClock clock,bool correct=true)
        {
            var trial=e.CurrentTrial;e.MarkPresented(trial.TrialId);clock.Advance(.2);
            if(trial.VisualSearch!=null)
            {
                string item=correct?trial.VisualSearch.TargetId:trial.VisualSearch.Items.First(x=>x.Id!=trial.VisualSearch.TargetId).Id;
                Check.True(e.SelectVisualSearchItem(trial.TrialId,item));
            }
            else
            {
                Colour colour=correct?trial.ExpectedResponse:(Colour)(((int)trial.ExpectedResponse+1)%4);
                Check.True(e.ButtonChanged(colour,true));e.ButtonChanged(colour,false);
            }
        }
        static void RequestExam(Experiment e,ManualClock clock)
        {Answer(e,clock);e.RequestPracticeExam();Check.Equal(Phase.PracticeExamReady,e.Phase);e.BeginPractice(Actor.Owner);}
        static void Exam(Experiment e,ManualClock clock,int correct)
        {
            for(int i=0;i<10;i++)
            {
                Check.Equal(Phase.Practice,e.Phase);Answer(e,clock,i<correct);
                Check.Equal<string>(null,e.View(Actor.Participant).PracticeCorrection);
                Check.Equal<string>(null,e.View(Actor.Participant).TutorialFeedback);
                if(i<9)Check.Equal(Phase.Practice,e.Phase);
            }
            Check.Equal(Phase.PracticeExamResult,e.Phase);Check.Equal(correct,e.PracticeCorrect);
        }
        public static void Register(TestSuite suite)
        {
            suite.Run("Formal eye recording declaration stays closed through training and opens once at first formal countdown",()=>{
                for(int mode=0;mode<3;mode++)
                {
                    var original=Configuration(mode);var configured=SessionConfiguration.WithFormalEyeRecordingOnly(original);
                    Check.True(!original.FormalEyeRecordingOnly&&configured.FormalEyeRecordingOnly);
                    Check.Equal("formal-session-v1",configured.EyeRecordingPolicy);
                    var clock=new ManualClock();var e=new Experiment(configured,clock);StartTutorial(e);
                    Check.True(!e.FormalEyeRecordingWindowStart.HasValue);
                    RequestExam(e,clock);Exam(e,clock,7);e.ContinuePracticeExamResult();
                    Check.True(!e.FormalEyeRecordingWindowStart.HasValue);
                    RequestExam(e,clock);Exam(e,clock,8);e.ContinuePracticeExamResult();
                    Check.Equal(Phase.BlockPreparation,e.Phase);Check.True(!e.FormalEyeRecordingWindowStart.HasValue);
                    e.LockBlock(Actor.Owner,ControlSelection.Automatic,"owner");
                    Check.Equal(Phase.BlockReady,e.Phase);Check.True(!e.FormalEyeRecordingWindowStart.HasValue);
                    Check.Throws<InvalidOperationException>(()=>e.BeginCountdown(Actor.Owner,false));
                    Check.True(!e.FormalEyeRecordingWindowStart.HasValue);
                    e.BeginCountdown(Actor.Owner,true);double boundary=e.FormalEyeRecordingWindowStart.Value;
                    Check.Equal(clock.Now,boundary);Check.Equal(1,e.Events.Count(x=>x.Kind=="formal_eye_recording_window_open"));
                    e.Pause(Actor.Owner);clock.Advance(1);e.Resume(true);
                    Check.Equal(boundary,e.FormalEyeRecordingWindowStart.Value);
                    Check.Equal(1,e.Events.Count(x=>x.Kind=="formal_eye_recording_window_open"));
                    Check.True(e.Events.All(x=>x.Fields["eye_recording_policy"]=="formal-session-v1"));
                    Check.True(!New(new ManualClock(),mode).Events[0].Fields.ContainsKey("eye_recording_policy"));
                }
            });
            suite.Run("Tutorial protocol preserves historical eight-six practice and explicitly opts in to ten-eight",()=>{
                var legacy=Fixtures.New(new ManualClock()).Configuration;
                Check.True(!legacy.UsesTutorial);Check.Equal(8,legacy.PracticeTrialCount);Check.Equal(6,legacy.PracticePassCorrect);
                var config=SessionConfiguration.WithTrainingProtocol(legacy,TrainingProtocol.TutorialExamV1);
                Check.True(config.UsesTutorial&&!legacy.UsesTutorial);Check.Equal(10,config.PracticeTrialCount);Check.Equal(8,config.PracticePassCorrect);
                Check.Equal("tutorial-exam-v1",config.TrainingSchema);
                Check.True(SessionConfiguration.WithResponseRule(config,ResponseRule.WordMeaning).UsesTutorial);
                Check.True(SessionConfiguration.WithTaskKind(config,TaskKind.VisualSearch).UsesTutorial);
                var e=new Experiment(config,new ManualClock());e.BeginFamiliarization(Actor.Owner);
                Check.Throws<InvalidOperationException>(()=>e.BeginPractice(Actor.Owner));
                Check.Throws<UnauthorizedAccessException>(()=>e.BeginTutorial(Actor.Participant));
                e.BeginTutorial(Actor.Owner);Check.Throws<InvalidOperationException>(()=>e.RequestPracticeExam());
                Check.True(e.Events[0].Fields["training_protocol"]=="tutorial-exam-v1");
                Check.True(!Fixtures.New(new ManualClock()).Events[0].Fields.ContainsKey("training_protocol"));
            });
            suite.Run("All three modes provide unlimited tutorial feedback and explicit participant exam choice",()=>{
                for(int mode=0;mode<3;mode++)
                {
                    var clock=new ManualClock();var e=New(clock,mode);StartTutorial(e);
                    for(int i=0;i<31;i++)
                    {
                        var trial=e.CurrentTrial;Check.True(trial.IsTutorial&&trial.IsPractice);Check.Equal(0,trial.PracticeAttempt);
                        Check.Equal(-1,trial.BlockIndex);Check.Equal(i,trial.PlannedIndex);Check.True(!e.View(Actor.Participant).OperatorAvatarVisible);
                        Answer(e,clock,i%2==0);Check.Equal(Phase.TutorialFeedback,e.Phase);
                        var view=e.View(Actor.Participant);Check.Equal(trial.TrialId,view.TrialId);Check.Equal(i%2==0,view.TutorialCorrect);
                        Check.True(view.TutorialFeedback.Contains(i%2==0?"Correct!":"Incorrect."));
                        Check.True(view.TutorialFeedback.Contains(view.TutorialExpectedAnswer));
                        Check.Equal(0,e.PracticeCorrect);Check.Equal(0,e.PracticeAttempt);
                        if(i<30)e.ContinueTutorial();
                    }
                    Check.Equal(31,e.Trials.Count);e.RequestPracticeExam();Check.Equal(Phase.PracticeExamReady,e.Phase);
                    Check.Equal<TrialRecord>(null,e.CurrentTrial);Check.Equal(0,e.PracticeAttempt);
                    Check.True(e.Events.Where(x=>x.Kind=="first_response").All(x=>x.Fields["learning_stage"]=="tutorial"));
                    Check.True(e.Events.Where(x=>x.Kind=="trial_planned").All(x=>x.Fields["tutorial_round"]=="1"));
                }
            });
            suite.Run("All three modes fail at seven of ten retry without cap and pass at exactly eight with result acknowledgement",()=>{
                for(int mode=0;mode<3;mode++)
                {
                    var clock=new ManualClock();var e=New(clock,mode);StartTutorial(e);
                    for(int attempt=1;attempt<=4;attempt++)
                    {
                        RequestExam(e,clock);Check.Equal(attempt,e.PracticeAttempt);Exam(e,clock,7);
                        Check.Equal(false,e.PracticePassed);Check.Equal(-1,e.BlockIndex);Check.Equal<EndReason?>(null,e.EndReason);
                        var completed=e.Events.Last(x=>x.Kind=="practice_completed");Check.Equal("10",completed.Fields["planned"]);
                        Check.Equal("8",completed.Fields["threshold"]);Check.Equal("False",completed.Fields["passed"]);
                        Check.Equal("practice_exam",completed.Fields["learning_stage"]);
                        e.ContinuePracticeExamResult();Check.Equal(Phase.Tutorial,e.Phase);Check.Equal(attempt+1,e.TutorialRound);
                    }
                    RequestExam(e,clock);Exam(e,clock,8);Check.Equal(true,e.PracticePassed);
                    Check.Equal(-1,e.BlockIndex);Check.Throws<InvalidOperationException>(()=>e.BeginPractice(Actor.Owner));
                    e.ContinuePracticeExamResult();Check.Equal(Phase.BlockPreparation,e.Phase);Check.Equal(0,e.BlockIndex);
                    Check.Equal(5,e.PracticeAttempt);Check.True(e.Trials.All(x=>x.IsPractice));
                    Check.Equal(e.Trials.Count,e.Trials.Select(x=>x.TrialId).Distinct().Count());
                }
            });
            suite.Run("New exam plans are deterministic five-five and cover four response colours with near balance",()=>{
                for(uint seed=0;seed<24;seed++)for(int attempt=1;attempt<=4;attempt++)
                {
                    var plans=TrialPlans.PracticeExam(seed,attempt);Check.Equal(10,plans.Length);
                    Check.Equal(5,plans.Count(x=>x.Word==x.Ink));Check.Equal(5,plans.Count(x=>x.Word!=x.Ink));
                    foreach(Colour colour in Enum.GetValues(typeof(Colour)))
                    {Check.True(plans.Count(x=>x.Word==colour)>=2&&plans.Count(x=>x.Word==colour)<=3);Check.True(plans.Count(x=>x.Ink==colour)>=2&&plans.Count(x=>x.Ink==colour)<=3);}
                    Check.True(plans.Select(x=>x.Key).SequenceEqual(TrialPlans.PracticeExam(seed,attempt).Select(x=>x.Key)));
                    var search=VisualSearchPlans.PracticeExam(seed,attempt);Check.Equal(10,search.Length);
                    Check.Equal(5,search.Count(x=>x.VisualSearch.SetSize==12));Check.Equal(5,search.Count(x=>x.VisualSearch.SetSize==24));
                    Check.True(search.Select(x=>x.Key).SequenceEqual(VisualSearchPlans.PracticeExam(seed,attempt).Select(x=>x.Key)));
                }
            });
            suite.Run("Tutorial search has no deadline while exam timeout remains one incorrect first outcome without feedback",()=>{
                var clock=new ManualClock();var e=New(clock,2);StartTutorial(e);var tutor=e.CurrentTrial;
                e.MarkPresented(tutor.TrialId);clock.Advance(120);e.Tick();Check.Equal(TrialOutcome.Pending,tutor.Outcome);
                Check.Equal(Phase.Tutorial,e.Phase);Check.True(e.SelectVisualSearchItem(tutor.TrialId,tutor.VisualSearch.TargetId));
                e.RequestPracticeExam();e.BeginPractice(Actor.Owner);var first=e.CurrentTrial;e.MarkPresented(first.TrialId);
                clock.Advance(30);e.Tick();Check.Equal(TrialOutcome.TimedOut,first.Outcome);Check.Equal(Phase.Practice,e.Phase);
                Check.Equal(1,e.CurrentTrial.PlannedIndex);Check.Equal(0,e.PracticeCorrect);
                for(int i=1;i<10;i++)Answer(e,clock);Check.Equal(9,e.PracticeCorrect);Check.Equal(true,e.PracticePassed);
                Check.Equal(1,e.Events.Count(x=>x.Kind=="trial_timed_out"));
            });
            suite.Run("Tutorial and exam tracking interruptions repeat the same planned item without reducing score",()=>{
                for(int mode=0;mode<3;mode++)
                {
                    var clock=new ManualClock();var e=New(clock,mode);StartTutorial(e);var first=e.CurrentTrial;
                    e.MarkPresented(first.TrialId);e.Pause(Actor.Owner);e.Resume(true);
                    Check.Equal(TrialOutcome.Interrupted,first.Outcome);Check.Equal(0,e.CurrentTrial.PlannedIndex);
                    Check.Equal(first.Stimulus.Key,e.CurrentTrial.Stimulus.Key);Check.True(first.TrialId!=e.CurrentTrial.TrialId);
                    Check.Equal(first.TrialId,e.Events.Last(x=>x.Kind=="trial_planned").Fields["retry_of"]);
                    RequestExam(e,clock);first=e.CurrentTrial;e.MarkPresented(first.TrialId);e.Pause(Actor.Owner);e.Resume(true);
                    Check.Equal(0,e.CurrentTrial.PlannedIndex);Check.Equal(first.Stimulus.Key,e.CurrentTrial.Stimulus.Key);
                    Check.Equal(TrialOutcome.Interrupted,first.Outcome);Exam(e,clock,10);Check.Equal(true,e.PracticePassed);
                    Check.Equal(10,e.Trials.Count(x=>!x.IsTutorial&&x.Outcome==TrialOutcome.Responded));
                }
            });
            suite.Run("Tutorial and assessment score only fresh first response and allow withdrawal",()=>{
                var clock=new ManualClock();var e=New(clock);StartTutorial(e);var trial=e.CurrentTrial;e.MarkPresented(trial.TrialId);
                var wrong=(Colour)(((int)trial.ExpectedResponse+1)%4);Check.True(e.ButtonChanged(wrong,true));
                Check.True(!e.ButtonChanged(trial.ExpectedResponse,true));Check.Equal(false,trial.Correct);
                e.ButtonChanged(wrong,false);e.ButtonChanged(trial.ExpectedResponse,false);e.RequestPracticeExam();e.BeginPractice(Actor.Owner);
                trial=e.CurrentTrial;e.MarkPresented(trial.TrialId);wrong=(Colour)(((int)trial.ExpectedResponse+1)%4);
                Check.True(e.ButtonChanged(wrong,true));Check.Equal(false,trial.Correct);var next=e.CurrentTrial;e.MarkPresented(next.TrialId);
                Check.True(!e.ButtonChanged(wrong,true));Check.Equal(TrialOutcome.Pending,next.Outcome);e.ButtonChanged(wrong,false);
                e.Withdraw();Check.Equal(EndReason.Withdrawn,e.EndReason);Check.Equal(Phase.Ended,e.Phase);
                var tutorial=New(new ManualClock());StartTutorial(tutorial);tutorial.Withdraw();Check.Equal(EndReason.Withdrawn,tutorial.EndReason);
            });
        }
    }
}
