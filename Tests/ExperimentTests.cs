using System;
using System.Linq;
using AvatarStroop.Core;

namespace AvatarStroop.Tests
{
    public static class ExperimentTests
    {
        public static void Register(TestSuite t)
        {
            t.Run("AC-03 practice correction waits and first answer stays wrong",()=>{
                var c=new ManualClock();var e=Fixtures.New(c);e.BeginFamiliarization(Actor.Owner);e.BeginPractice(Actor.Owner);
                var first=e.CurrentTrial;e.MarkPresented(first.TrialId);c.Advance(.8);
                var wrong=(Colour)(((int)first.Stimulus.Ink+1)%4);e.ButtonChanged(wrong,true);e.ButtonChanged(wrong,false);
                Check.Equal(Phase.PracticeCorrection,e.Phase);Check.Equal(0,e.PracticeCorrect);
                e.ButtonChanged(first.Stimulus.Ink,true);e.ButtonChanged(first.Stimulus.Ink,false);c.Advance(90);e.Tick();
                Check.Equal(first.TrialId,e.CurrentTrial.TrialId);Check.Equal(false,first.Correct);Check.Equal(0,e.PracticeCorrect);
                Check.True(e.View(Actor.Participant).PracticeCorrection.Contains(first.Stimulus.Ink.ToString()));
                e.ContinuePractice();Check.Equal(Phase.Practice,e.Phase);Check.Equal(1,e.CurrentTrial.PlannedIndex);
            });
            t.Run("AC-03 practice 6 of 8 passes without extra criteria",()=>{
                var c=new ManualClock();var e=Fixtures.New(c);Fixtures.Practice(e,c,6);
                Check.Equal(Phase.BlockPreparation,e.Phase);Check.Equal(1,e.PracticeAttempt);Check.Equal(8,e.Trials.Count);
            });
            t.Run("AC-03 one retry may pass and second failure ends",()=>{
                var c=new ManualClock();var e=Fixtures.New(c);Fixtures.Practice(e,c,5);Check.Equal(Phase.PracticeRetry,e.Phase);
                Fixtures.Practice(e,c,6);Check.Equal(Phase.BlockPreparation,e.Phase);Check.Equal(2,e.PracticeAttempt);Check.Equal(16,e.Trials.Count);
                var c2=new ManualClock();var failed=Fixtures.New(c2);Fixtures.Practice(failed,c2,5);Fixtures.Practice(failed,c2,5);
                Check.Equal(EndReason.PracticeFailed,failed.EndReason);Check.Throws<InvalidOperationException>(()=>failed.BeginPractice(Actor.Owner));
            });
            t.Run("AC-02 owner-only lock keeps allocated factors immutable",()=>{
                var c=new ManualClock();var e=Fixtures.New(c);Fixtures.Practice(e,c,8);
                Check.Throws<UnauthorizedAccessException>(()=>e.LockBlock(Actor.Participant,ControlSelection.Automatic,"p"));
                Check.Throws<UnauthorizedAccessException>(()=>e.LockBlock(Actor.Operator,ControlSelection.Automatic,"o"));
                var copy=e.Configuration.Allocation.Order;copy[0]=Condition.D;Check.Equal(Condition.A,e.CurrentCondition);
                e.LockBlock(Actor.Owner,ControlSelection.Automatic,"owner");
                Check.Throws<InvalidOperationException>(()=>e.LockBlock(Actor.Owner,ControlSelection.LiveWoz,"owner"));
                Check.Throws<InvalidOperationException>(()=>e.BeginCountdown(Actor.Owner,false));
                Check.Equal(Phase.BlockReady,e.Phase);Check.True(e.Events.Any(x=>x.Kind=="block_configuration_locked"));
            });
            t.Run("AC-05 apparatus cannot start with unresolved adapters and fault policy",()=>{
                var c=new ManualClock();Check.Throws<InvalidOperationException>(()=>Fixtures.New(c,Condition.A,RunKind.Apparatus));
            });
            t.Run("AC-04 actual onset required, one press edge advances immediately",()=>{
                var c=new ManualClock();var e=Fixtures.New(c);Fixtures.Practice(e,c,8);Fixtures.Start(e,c);
                var first=e.CurrentTrial;var answer=first.Stimulus.Ink;
                Check.True(!e.ButtonChanged(answer,true));e.ButtonChanged(answer,false);Check.Equal(first,e.CurrentTrial);
                e.MarkPresented(first.TrialId);c.Advance(.125);Check.True(e.ButtonChanged(answer,true));
                Check.Equal(1,e.CurrentTrial.PlannedIndex);Check.Near(.125,first.ReactionSeconds.Value);
                e.MarkPresented(e.CurrentTrial.TrialId);c.Advance(.1);Check.True(!e.ButtonChanged(answer,true));
                Check.Equal(1,e.CurrentTrial.PlannedIndex);e.ButtonChanged(answer,false);Check.True(e.ButtonChanged(answer,true));
                Check.Equal(2,e.CurrentTrial.PlannedIndex);Check.Equal(Phase.MainTask,e.Phase);
            });
            t.Run("AC-04 no response deadline or main correctness feedback",()=>{
                var c=new ManualClock();var e=Fixtures.New(c);Fixtures.Practice(e,c,8);Fixtures.Start(e,c);e.MarkPresented(e.CurrentTrial.TrialId);
                var id=e.CurrentTrial.TrialId;c.Advance(3600);e.Tick();Check.Equal(id,e.CurrentTrial.TrialId);
                Fixtures.Answer(e,c,false);Check.Equal(Phase.MainTask,e.Phase);Check.Equal(null,e.View(Actor.Participant).PracticeCorrection);
                Check.Equal(null,e.View(Actor.Operator).PracticeCorrection);
            });
            t.Run("AC-01 both task views agree; practice D ratings rest hide operator avatar",()=>{
                var c=new ManualClock();var e=Fixtures.New(c,Condition.D);e.BeginFamiliarization(Actor.Owner);e.BeginPractice(Actor.Owner);
                Check.True(!e.View(Actor.Participant).OperatorAvatarVisible);
                for(int i=0;i<8;i++)Fixtures.Answer(e,c);Fixtures.Start(e,c);
                Check.Equal(e.View(Actor.Participant).TrialId,e.View(Actor.Operator).TrialId);
                Check.Equal(e.View(Actor.Participant).Stimulus.Key,e.View(Actor.Operator).Stimulus.Key);
                Check.True(!e.View(Actor.Participant).OperatorAvatarVisible);Check.True(e.View(Actor.Operator).OperatorViewProvisional);
                for(int i=0;i<24;i++)Fixtures.Answer(e,c);
                Check.True(!e.View(Actor.Participant).OperatorAvatarVisible);Check.Equal(null,e.View(Actor.Participant).Stimulus);
                Fixtures.Ratings(e,c);Check.True(!e.View(Actor.Participant).OperatorAvatarVisible);
            });
            t.Run("AC-03 ratings fixed order 1-5 separate and rest excludes rating duration",()=>{
                var c=new ManualClock();var e=Fixtures.New(c);Fixtures.Practice(e,c,8);Fixtures.Start(e,c);
                for(int i=0;i<24;i++)Fixtures.Answer(e,c);
                Check.Throws<ArgumentOutOfRangeException>(()=>e.Rate(0));Check.Throws<ArgumentOutOfRangeException>(()=>e.Rate(6));
                for(int i=0;i<5;i++){Check.Equal(Questionnaire.Item(i,"en"),e.View(Actor.Participant).QuestionnaireItem);c.Advance(50);e.Rate(i+1);}
                Check.Equal(5,e.Ratings.Count);Check.True(!e.Ready());double restStart=c.Now;c.Advance(29.999);Check.True(!e.Ready());c.Now=restStart+30;Check.True(e.Ready());
                Check.Equal(Phase.BlockPreparation,e.Phase);Check.Equal(1,e.BlockIndex);
                Check.Equal("focus,distraction,observed,evaluated,negative_evaluation_worry",String.Join(",",e.Ratings.Select(x=>x.ItemId)));
            });
            t.Run("AC-03 complete 96 main trials and fourth ratings plus extended rest",()=>{
                var c=new ManualClock();var e=Fixtures.New(c);Fixtures.Practice(e,c,8);
                for(int block=0;block<4;block++)
                {
                    Fixtures.Start(e,c);for(int trial=0;trial<24;trial++)Fixtures.Answer(e,c,trial%3!=0);
                    Check.Equal(Phase.Questionnaire,e.Phase);Fixtures.Ratings(e,c);Check.Equal(Phase.Rest,e.Phase);
                    c.Advance(29);Check.True(!e.Ready());c.Advance(91);Check.True(e.Ready());
                }
                Check.Equal(EndReason.Completed,e.EndReason);Check.Equal(96,e.Trials.Count(x=>!x.IsPractice));Check.Equal(20,e.Ratings.Count);
                Check.Equal(4,e.Events.Count(x=>x.Kind=="rest_ready"));Check.True(e.Trials.All(x=>x.Outcome==TrialOutcome.Responded));
            });
            t.Run("AC-03 explicit pause freezes clock then next trial in new segment",()=>{
                var c=new ManualClock();var e=Fixtures.New(c,Condition.B);Fixtures.Practice(e,c,8);Fixtures.Start(e,c);e.MarkPresented(e.CurrentTrial.TrialId);
                c.Advance(2.25);var interrupted=e.CurrentTrial;var button=interrupted.Stimulus.Ink;e.Pause(Actor.Operator);
                Check.Equal(TrialOutcome.Interrupted,interrupted.Outcome);Check.Equal(null,interrupted.Correct);Check.Equal(null,interrupted.ReactionSeconds);
                Check.Equal(null,e.View(Actor.Participant).Stimulus);Check.True(!e.View(Actor.Participant).OperatorAvatarVisible);
                e.ButtonChanged(button,true);e.ButtonChanged(button,false);c.Advance(200);Check.Near(2.25,e.ActiveSeconds);
                Check.Throws<InvalidOperationException>(()=>e.Resume(false));Check.Equal(Phase.Paused,e.Phase);
                e.Resume(true);Check.Equal(Phase.Countdown,e.Phase);c.Advance(1);e.Tick();Check.Near(2.25,e.ActiveSeconds);
                Check.Equal(1,e.CurrentTrial.PlannedIndex);Check.Equal(1,e.CurrentTrial.Segment);e.MarkPresented(e.CurrentTrial.TrialId);c.Advance(.5);Check.Near(2.75,e.ActiveSeconds);
                Check.Equal(1,e.Trials.Count(x=>x.Outcome==TrialOutcome.Interrupted));
            });
            t.Run("AC-03 final interrupted trial resumes into ratings without replay",()=>{
                var c=new ManualClock();var e=Fixtures.New(c);Fixtures.Practice(e,c,8);Fixtures.Start(e,c);
                for(int i=0;i<23;i++)Fixtures.Answer(e,c);e.MarkPresented(e.CurrentTrial.TrialId);e.Pause(Actor.Participant);c.Advance(3);e.Resume(true);
                Check.Equal(Phase.Questionnaire,e.Phase);Check.Equal(24,e.Trials.Count(x=>!x.IsPractice));
                Check.Equal(23,e.Trials.Count(x=>!x.IsPractice&&x.Outcome==TrialOutcome.Responded));Check.Equal(1,e.Segment);
            });
            t.Run("AC-05 initial and resumed countdown pauses require reacquisition",()=>{
                var c=new ManualClock();var e=Fixtures.New(c);Fixtures.Practice(e,c,8);
                e.LockBlock(Actor.Owner,ControlSelection.LiveWoz,"owner");e.BeginCountdown(Actor.Owner,true);
                e.Pause(Actor.Operator);Check.Throws<InvalidOperationException>(()=>e.Resume(false));
                c.Advance(10);e.Tick();Check.Equal(Phase.Paused,e.Phase);e.Resume(true);c.Advance(1);e.Tick();
                e.MarkPresented(e.CurrentTrial.TrialId);e.Pause(Actor.Participant);e.Resume(true);
                Check.Equal(Phase.Countdown,e.Phase);e.Pause(Actor.Operator);
                Check.Throws<InvalidOperationException>(()=>e.Resume(false));Check.Equal(Phase.Paused,e.Phase);
                e.Resume(true);c.Advance(1);e.Tick();Check.Equal(1,e.CurrentTrial.PlannedIndex);Check.Equal(1,e.Segment);
            });
            t.Run("AC-03 rest pause excludes paused time, withdrawal needs no reason",()=>{
                var c=new ManualClock();var e=Fixtures.New(c);Fixtures.Practice(e,c,8);Fixtures.Start(e,c);
                for(int i=0;i<24;i++)Fixtures.Answer(e,c);Fixtures.Ratings(e,c);c.Advance(15);e.Pause(Actor.Participant);c.Advance(100);e.Resume(true);
                Check.Near(15,e.RestSeconds);Check.True(!e.Ready());e.Withdraw();Check.Equal(EndReason.Withdrawn,e.EndReason);
                Check.Equal(24,e.Trials.Count(x=>!x.IsPractice));Check.Equal(5,e.Ratings.Count);
            });
            t.Run("AC-03 practice interruption exposes unresolved scoring policy",()=>{
                var c=new ManualClock();var e=Fixtures.New(c);e.BeginFamiliarization(Actor.Owner);e.BeginPractice(Actor.Owner);e.MarkPresented(e.CurrentTrial.TrialId);
                e.Pause(Actor.Participant);e.Resume(true);Check.Equal(Phase.NeedsProtocolDecision,e.Phase);
                Check.Equal(null,e.CurrentTrial.Correct);Check.Equal(null,e.CurrentTrial.ReactionSeconds);
                Check.Throws<InvalidOperationException>(()=>e.BeginPractice(Actor.Owner));e.EndTechnical(Actor.Owner,"practice_interruption_policy_unresolved");
                Check.Equal(EndReason.TechnicalEnd,e.EndReason);
            });
            t.Run("AC-04 held state persists across pause; release during pause permits fresh press",()=>{
                var c=new ManualClock();var e=Fixtures.New(c);Fixtures.Practice(e,c,8);Fixtures.Start(e,c);
                var held=e.CurrentTrial.Stimulus.Ink;e.MarkPresented(e.CurrentTrial.TrialId);c.Advance(.2);Check.True(e.ButtonChanged(held,true));
                e.MarkPresented(e.CurrentTrial.TrialId);e.Pause(Actor.Participant);e.Resume(true);c.Advance(1);e.Tick();e.MarkPresented(e.CurrentTrial.TrialId);
                Check.True(!e.ButtonChanged(held,true));Check.Equal(2,e.CurrentTrial.PlannedIndex);
                e.Pause(Actor.Participant);e.ButtonChanged(held,false);e.Resume(true);c.Advance(1);e.Tick();e.MarkPresented(e.CurrentTrial.TrialId);c.Advance(.2);
                Check.True(e.ButtonChanged(held,true));Check.Equal(4,e.CurrentTrial.PlannedIndex);
            });
            t.Run("AC-06 fault flag logs without inventing automatic pause",()=>{
                var c=new ManualClock();var e=Fixtures.New(c);Fixtures.Practice(e,c,8);Fixtures.Start(e,c);e.FlagTechnicalIssue("operator_eye_invalid");
                Check.Equal(Phase.MainTask,e.Phase);Check.True(e.CurrentTrial.TechnicalIssue);Check.Equal(ControlSelection.Automatic,e.LockedControl);
            });
            t.Run("AC-07 event linkage and timestamp reconstruction",()=>{
                var c=new ManualClock();var e=Fixtures.New(c);Fixtures.Practice(e,c,8);Fixtures.Start(e,c);Fixtures.Answer(e,c);
                var trial=e.Trials.Last(x=>!x.IsPractice&&x.Outcome==TrialOutcome.Responded);
                var onset=e.Events.Single(x=>x.Kind=="stimulus_presented"&&x.TrialId==trial.TrialId);
                var response=e.Events.Single(x=>x.Kind=="first_response"&&x.TrialId==trial.TrialId);
                Check.Near(trial.ReactionSeconds.Value,response.Time-onset.Time);Check.True(e.Events.All(x=>x.SessionId=="test-session"&&x.ClockId=="test-monotonic"));
                for(int i=0;i<e.Events.Count;i++)Check.Equal((long)i,e.Events[i].Sequence);
                Check.Throws<InvalidOperationException>(()=>e.MarkPresented("wrong-id"));
                c.Now=-1;Check.Throws<InvalidOperationException>(()=>e.Tick());
            });
            t.Run("AC-07 interrupted termination has no manufactured response",()=>{
                var c=new ManualClock();var e=Fixtures.New(c);Fixtures.Practice(e,c,8);Fixtures.Start(e,c);e.MarkPresented(e.CurrentTrial.TrialId);
                var trial=e.CurrentTrial;e.Withdraw();Check.Equal(TrialOutcome.Interrupted,trial.Outcome);Check.Equal(null,trial.ResponseAt);Check.Equal(null,trial.Correct);
                Check.Equal(1,e.Trials.Count(x=>!x.IsPractice));e.Withdraw();Check.Equal(1,e.Events.Count(x=>x.Kind=="session_ended"));
            });
            t.Run("AC-03 explicit config and exact questionnaire anchors",()=>{
                Check.Equal("Strongly disagree",Questionnaire.Anchor(1,"en"));Check.Equal("非常同意",Questionnaire.Anchor(5,"zh"));
                Check.Equal("在刚才这一组中，我做任务时有被注视的感觉。",Questionnaire.Item(2,"zh"));
                Check.Throws<ArgumentException>(()=>Questionnaire.Item(0,"unconfirmed"));Check.Throws<ArgumentOutOfRangeException>(()=>Questionnaire.Anchor(6,"en"));
                var allocation=TrialPlans.Allocate(1,42)[0];
                Check.Throws<ArgumentOutOfRangeException>(()=>new SessionConfiguration("x",42,allocation,RunKind.Simulation,"en",0));
            });
        }
    }
}
