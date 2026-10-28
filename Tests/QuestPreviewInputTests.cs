using System;
using System.Linq;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    public static class QuestPreviewInputTests
    {
        static QuestPreviewControls Controls(int buttons=0,double stick=0,bool ready=true)
        {return new QuestPreviewControls(ready,(buttons&1)!=0,(buttons&2)!=0,(buttons&4)!=0,(buttons&8)!=0,(buttons&16)!=0,(buttons&32)!=0,stick);}
        static QuestPreviewInputFrame Update(QuestPreviewInputRouter router,Experiment experiment,int buttons=0,double stick=0,bool ready=true)
        {return router.Update(experiment.Phase,experiment.QuestionIndex,Controls(buttons,stick,ready));}
        static void Apply(Experiment experiment,QuestPreviewInputFrame frame,bool sourceReady=true)
        {
            foreach(Colour colour in frame.ReleasedColours)experiment.ButtonChanged(colour,false);
            switch(frame.Action)
            {
                case QuestPreviewAction.Respond:experiment.ButtonChanged(frame.Response.Value,true);break;
                case QuestPreviewAction.SubmitRating:experiment.Rate(frame.Rating.Value);break;
                case QuestPreviewAction.ContinuePractice:experiment.ContinuePractice();break;
                case QuestPreviewAction.Ready:experiment.Ready();break;
                case QuestPreviewAction.Resume:experiment.Resume(sourceReady);break;
                case QuestPreviewAction.Pause:experiment.Pause(Actor.Participant);break;
                case QuestPreviewAction.Withdraw:experiment.Withdraw();break;
            }
        }
        static Experiment Main(ManualClock clock)
        {var experiment=Fixtures.New(clock);Fixtures.Practice(experiment,clock,8);Fixtures.Start(experiment,clock);return experiment;}
        static void ToQuestionnaire(Experiment experiment,ManualClock clock)
        {for(int i=0;i<24;i++)Fixtures.Answer(experiment,clock);Check.Equal(Phase.Questionnaire,experiment.Phase);}
        public static void Register(TestSuite suite)
        {
            suite.Run("Quest preview maps X Y A B to one new Red Green Blue Yellow edge",()=>{
                for(int colour=0;colour<4;colour++)
                {
                    var router=new QuestPreviewInputRouter();router.Update(Phase.MainTask,0,Controls());
                    var frame=router.Update(Phase.MainTask,0,Controls(1<<colour));
                    Check.Equal(QuestPreviewAction.Respond,frame.Action);Check.Equal((Colour?)colour,frame.Response);Check.Equal<int?>(null,frame.Rating);
                    Check.Equal(QuestPreviewAction.None,router.Update(Phase.MainTask,0,Controls(1<<colour)).Action);
                    frame=router.Update(Phase.MainTask,0,Controls());Check.Equal(1,frame.ReleasedColours.Count);Check.Equal((Colour)colour,frame.ReleasedColours[0]);
                    Check.Equal(QuestPreviewAction.Respond,router.Update(Phase.MainTask,0,Controls(1<<colour)).Action);
                }
            });
            suite.Run("Quest preview consumes simultaneous face edges without delayed extra trial responses",()=>{
                var router=new QuestPreviewInputRouter();router.Update(Phase.MainTask,0,Controls());
                var frame=router.Update(Phase.MainTask,0,Controls(15));Check.Equal((Colour?)Colour.Red,frame.Response);
                frame=router.Update(Phase.MainTask,0,Controls(14));Check.Equal(QuestPreviewAction.None,frame.Action);Check.Equal(1,frame.ReleasedColours.Count);Check.Equal(Colour.Red,frame.ReleasedColours[0]);
                Check.Equal(QuestPreviewAction.None,router.Update(Phase.MainTask,0,Controls(4)).Action);
                Check.Equal(0,router.Update(Phase.MainTask,0,Controls()).ReleasedColours.Count);
                Check.Equal((Colour?)Colour.Blue,router.Update(Phase.MainTask,0,Controls(4)).Response);
            });
            suite.Run("Quest preview early held response remains blocked until presented and pressed again",()=>{
                var clock=new ManualClock();var experiment=Main(clock);var trial=experiment.CurrentTrial;var router=new QuestPreviewInputRouter();
                Apply(experiment,Update(router,experiment));Apply(experiment,Update(router,experiment,1));Check.Equal(TrialOutcome.Pending,trial.Outcome);Check.Equal<Colour?>(null,trial.Response);
                experiment.MarkPresented(trial.TrialId);clock.Advance(.2);Apply(experiment,Update(router,experiment,1));Check.Equal(TrialOutcome.Pending,trial.Outcome);
                Apply(experiment,Update(router,experiment));Apply(experiment,Update(router,experiment,1));Check.Equal(TrialOutcome.Responded,trial.Outcome);Check.Equal((Colour?)Colour.Red,trial.Response);Check.Equal(1,experiment.CurrentTrial.PlannedIndex);
            });
            suite.Run("Quest preview phase entry suppresses held buttons and deflected stick until released",()=>{
                var router=new QuestPreviewInputRouter();var frame=router.Update(Phase.MainTask,0,Controls(63,1));Check.Equal(QuestPreviewAction.None,frame.Action);
                Check.Equal(QuestPreviewAction.None,router.Update(Phase.MainTask,0,Controls(63,1)).Action);
                frame=router.Update(Phase.Questionnaire,0,Controls(63,1));Check.Equal(QuestPreviewAction.None,frame.Action);Check.Equal((int?)3,frame.CandidateRating);
                Check.Equal(QuestPreviewAction.None,router.Update(Phase.Questionnaire,0,Controls(63,-1)).Action);
                router.Update(Phase.Questionnaire,0,Controls());frame=router.Update(Phase.Questionnaire,0,Controls(0,1));
                Check.Equal(QuestPreviewAction.AdjustRating,frame.Action);Check.Equal((int?)4,frame.CandidateRating);Check.Equal<int?>(null,frame.Rating);
            });
            suite.Run("Quest preview practice response cannot also dismiss correction in the same frame",()=>{
                var clock=new ManualClock();var experiment=Fixtures.New(clock);experiment.BeginFamiliarization(Actor.Owner);experiment.BeginPractice(Actor.Owner);experiment.MarkPresented(experiment.CurrentTrial.TrialId);
                var router=new QuestPreviewInputRouter();Apply(experiment,Update(router,experiment));
                int wrong=((int)experiment.CurrentTrial.Stimulus.Ink+1)%4;var frame=Update(router,experiment,(1<<wrong)|16);
                Check.Equal(Phase.Practice,frame.StartPhase);Check.Equal(QuestPreviewAction.Respond,frame.Action);Apply(experiment,frame);Check.Equal(Phase.PracticeCorrection,experiment.Phase);
                Apply(experiment,Update(router,experiment,(1<<wrong)|16));Check.Equal(Phase.PracticeCorrection,experiment.Phase);
                Apply(experiment,Update(router,experiment));frame=Update(router,experiment,16);Check.Equal(QuestPreviewAction.ContinuePractice,frame.Action);Apply(experiment,frame);
                Check.Equal(Phase.Practice,experiment.Phase);Check.Equal(1,experiment.CurrentTrial.PlannedIndex);
                Check.Equal(QuestPreviewAction.None,Update(router,experiment,16).Action);
            });
            suite.Run("Quest preview final Blue answer cannot become an automatic questionnaire rating",()=>{
                var clock=new ManualClock();var experiment=Main(clock);for(int i=0;i<23;i++)Fixtures.Answer(experiment,clock);
                experiment.MarkPresented(experiment.CurrentTrial.TrialId);var router=new QuestPreviewInputRouter();Apply(experiment,Update(router,experiment));
                var frame=Update(router,experiment,4);Check.Equal(QuestPreviewAction.Respond,frame.Action);Apply(experiment,frame);
                Check.Equal(Phase.Questionnaire,experiment.Phase);Check.Equal(0,experiment.Ratings.Count);
                frame=Update(router,experiment,4);Check.Equal(QuestPreviewAction.None,frame.Action);Check.Equal((int?)3,frame.CandidateRating);Apply(experiment,frame);
                Check.Equal(0,experiment.Ratings.Count);Apply(experiment,Update(router,experiment));frame=Update(router,experiment,4);Apply(experiment,frame);
                Check.Equal(QuestPreviewAction.SubmitRating,frame.Action);Check.Equal(1,experiment.Ratings.Count);Check.Equal(3,experiment.Ratings[0].Value);
                frame=Update(router,experiment,4);Check.Equal(QuestPreviewAction.None,frame.Action);Check.Equal((int?)3,frame.CandidateRating);Apply(experiment,frame);Check.Equal(1,experiment.Ratings.Count);
            });
            suite.Run("Quest preview questionnaire candidates need neutral stick edges and explicit separate confirmation",()=>{
                var router=new QuestPreviewInputRouter();router.Update(Phase.Questionnaire,0,Controls());
                var frame=router.Update(Phase.Questionnaire,0,Controls(4,.65));Check.Equal(QuestPreviewAction.AdjustRating,frame.Action);Check.Equal((int?)4,frame.CandidateRating);Check.Equal<int?>(null,frame.Rating);
                Check.Equal(QuestPreviewAction.None,router.Update(Phase.Questionnaire,0,Controls(4,1)).Action);
                Check.Equal(QuestPreviewAction.None,router.Update(Phase.Questionnaire,0,Controls(4,-1)).Action);
                Check.Equal(QuestPreviewAction.None,router.Update(Phase.Questionnaire,0,Controls(4,.25)).Action);
                frame=router.Update(Phase.Questionnaire,0,Controls(4,-.65));Check.Equal((int?)3,frame.CandidateRating);Check.Equal(QuestPreviewAction.AdjustRating,frame.Action);
                router.Update(Phase.Questionnaire,0,Controls());frame=router.Update(Phase.Questionnaire,0,Controls(4));Check.Equal(QuestPreviewAction.SubmitRating,frame.Action);Check.Equal((int?)3,frame.Rating);
                frame=router.Update(Phase.Questionnaire,1,Controls(4));Check.Equal(QuestPreviewAction.None,frame.Action);Check.Equal((int?)3,frame.CandidateRating);
                for(int i=0;i<8;i++){router.Update(Phase.Questionnaire,1,Controls());frame=router.Update(Phase.Questionnaire,1,Controls(0,1));}Check.Equal((int?)5,frame.CandidateRating);
                for(int i=0;i<8;i++){router.Update(Phase.Questionnaire,1,Controls());frame=router.Update(Phase.Questionnaire,1,Controls(0,-1));}Check.Equal((int?)1,frame.CandidateRating);
            });
            suite.Run("Quest preview unsubmitted candidate resets on each of five questions without recording a default",()=>{
                var clock=new ManualClock();var experiment=Main(clock);ToQuestionnaire(experiment,clock);var router=new QuestPreviewInputRouter();
                for(int item=0;item<Questionnaire.Count;item++)
                {
                    var initial=Update(router,experiment);Apply(experiment,initial);Check.Equal((int?)3,initial.CandidateRating);Check.Equal(item,experiment.Ratings.Count);
                    Apply(experiment,Update(router,experiment,0,1));Check.Equal(item,experiment.Ratings.Count);
                    Apply(experiment,Update(router,experiment));var confirm=Update(router,experiment,4);Check.Equal((int?)4,confirm.Rating);Apply(experiment,confirm);
                    Check.Equal(item+1,experiment.Ratings.Count);
                }
                Check.Equal(Phase.Rest,experiment.Phase);Check.True(experiment.Ratings.All(x=>x.Value==4));
            });
            suite.Run("Quest preview held early Ready trigger cannot skip the existing thirty-second rest",()=>{
                var clock=new ManualClock();var experiment=Main(clock);ToQuestionnaire(experiment,clock);Fixtures.Ratings(experiment,clock);var router=new QuestPreviewInputRouter();
                Apply(experiment,Update(router,experiment));var early=Update(router,experiment,16);Check.Equal(QuestPreviewAction.Ready,early.Action);Apply(experiment,early);Check.Equal(Phase.Rest,experiment.Phase);
                clock.Advance(31);var held=Update(router,experiment,16);Check.Equal(QuestPreviewAction.None,held.Action);Apply(experiment,held);Check.Equal(Phase.Rest,experiment.Phase);
                Apply(experiment,Update(router,experiment));Apply(experiment,Update(router,experiment,16));Check.Equal(Phase.BlockPreparation,experiment.Phase);Check.Equal(1,experiment.BlockIndex);
                Check.Equal(QuestPreviewAction.None,Update(router,experiment,16).Action);Check.Equal<ControlSelection?>(null,experiment.LockedControl);
            });
            suite.Run("Quest preview pause wins over response and paused B withdraw wins over resume",()=>{
                var clock=new ManualClock();var experiment=Main(clock);experiment.MarkPresented(experiment.CurrentTrial.TrialId);var router=new QuestPreviewInputRouter();Apply(experiment,Update(router,experiment));
                var frame=Update(router,experiment,63);Check.Equal(QuestPreviewAction.Pause,frame.Action);Check.Equal<Colour?>(null,frame.Response);Apply(experiment,frame);
                Check.Equal(Phase.Paused,experiment.Phase);Check.Equal(0,experiment.Trials.Count(x=>!x.IsPractice&&x.Outcome==TrialOutcome.Responded));
                frame=Update(router,experiment,63);Check.Equal(QuestPreviewAction.None,frame.Action);Apply(experiment,frame);Check.Equal(Phase.Paused,experiment.Phase);
                Apply(experiment,Update(router,experiment));frame=Update(router,experiment,8|16);Check.Equal(QuestPreviewAction.Withdraw,frame.Action);Apply(experiment,frame);
                Check.Equal(Phase.Ended,experiment.Phase);Check.Equal((EndReason?)EndReason.Withdrawn,experiment.EndReason);
            });
            suite.Run("Quest preview trigger and pause mappings expose no owner start or source-selection actions",()=>{
                foreach(Phase phase in Enum.GetValues(typeof(Phase)))
                {
                    var router=new QuestPreviewInputRouter();router.Update(phase,0,Controls());var frame=router.Update(phase,0,Controls(16));
                    var expected=phase==Phase.PracticeCorrection?QuestPreviewAction.ContinuePractice:phase==Phase.Rest?QuestPreviewAction.Ready:phase==Phase.Paused?QuestPreviewAction.Resume:QuestPreviewAction.None;
                    Check.Equal(expected,frame.Action,phase.ToString());
                    router=new QuestPreviewInputRouter();router.Update(phase,0,Controls());frame=router.Update(phase,0,Controls(32));
                    Check.Equal(phase==Phase.Paused||phase==Phase.Ended||phase==Phase.NeedsProtocolDecision?QuestPreviewAction.None:QuestPreviewAction.Pause,frame.Action);
                }
            });
            suite.Run("Quest preview device loss releases only emitted colours and reconnection never invents an edge",()=>{
                var router=new QuestPreviewInputRouter();router.Update(Phase.MainTask,0,Controls());router.Update(Phase.MainTask,0,Controls(15));
                var frame=router.Update(Phase.MainTask,0,Controls(63,1,false));Check.True(!frame.DeviceReady);Check.Equal(QuestPreviewAction.None,frame.Action);
                Check.Equal(1,frame.ReleasedColours.Count);Check.Equal(Colour.Red,frame.ReleasedColours[0]);Check.Equal<int?>(null,frame.CandidateRating);
                Check.Equal(0,router.Update(Phase.MainTask,0,Controls(63,1,false)).ReleasedColours.Count);
                frame=router.Update(Phase.MainTask,0,Controls(63,1));Check.True(frame.DeviceReady);Check.Equal(QuestPreviewAction.None,frame.Action);
                Check.Equal(QuestPreviewAction.None,router.Update(Phase.MainTask,0,Controls(63,1)).Action);router.Update(Phase.MainTask,0,Controls());
                Check.Equal((Colour?)Colour.Blue,router.Update(Phase.MainTask,0,Controls(4)).Response);
            });
            suite.Run("Quest preview invalid stick data fails closed and Reset returns releases exactly once",()=>{
                foreach(double invalid in new[]{Double.NaN,Double.PositiveInfinity,Double.NegativeInfinity,1.01,-1.01})
                {
                    var router=new QuestPreviewInputRouter();router.Update(Phase.MainTask,0,Controls());router.Update(Phase.MainTask,0,Controls(2));
                    var frame=router.Update(Phase.MainTask,0,Controls(63,invalid));Check.True(!frame.DeviceReady);Check.Equal(QuestPreviewAction.None,frame.Action);Check.Equal(Colour.Green,frame.ReleasedColours.Single());
                }
                var reset=new QuestPreviewInputRouter();reset.Update(Phase.MainTask,0,Controls());reset.Update(Phase.MainTask,0,Controls(1));reset.Update(Phase.MainTask,0,Controls(5));
                Check.Equal("Red,Blue",String.Join(",",reset.Reset()));Check.Equal(0,reset.Reset().Count);Check.Equal(QuestPreviewAction.None,reset.Update(Phase.MainTask,0,Controls(5)).Action);
            });
            suite.Run("Quest preview source reacquisition and interrupted-practice decisions remain enforced by core",()=>{
                var clock=new ManualClock();var experiment=Main(clock);experiment.MarkPresented(experiment.CurrentTrial.TrialId);experiment.Pause(Actor.Participant);var router=new QuestPreviewInputRouter();Apply(experiment,Update(router,experiment));
                var frame=Update(router,experiment,16);Check.Equal(QuestPreviewAction.Resume,frame.Action);Check.Throws<InvalidOperationException>(()=>Apply(experiment,frame,false));Check.Equal(Phase.Paused,experiment.Phase);
                Check.Equal(QuestPreviewAction.None,Update(router,experiment,16).Action);Apply(experiment,Update(router,experiment));Apply(experiment,Update(router,experiment,16));Check.Equal(Phase.Countdown,experiment.Phase);
                var practice=Fixtures.New(new ManualClock());practice.BeginFamiliarization(Actor.Owner);practice.BeginPractice(Actor.Owner);practice.MarkPresented(practice.CurrentTrial.TrialId);router=new QuestPreviewInputRouter();Apply(practice,Update(router,practice));Apply(practice,Update(router,practice,32));
                Apply(practice,Update(router,practice));Apply(practice,Update(router,practice,16));Check.Equal(Phase.NeedsProtocolDecision,practice.Phase);
                Check.Equal(QuestPreviewAction.None,Update(router,practice,16).Action);Check.Throws<InvalidOperationException>(()=>Fixtures.New(new ManualClock(),Condition.A,RunKind.Apparatus));
            });
            suite.Run("Quest preview forwards release across phases and rejects invalid routing contexts",()=>{
                var router=new QuestPreviewInputRouter();router.Update(Phase.MainTask,0,Controls());router.Update(Phase.MainTask,0,Controls(2));
                var frame=router.Update(Phase.Questionnaire,0,Controls());Check.Equal(QuestPreviewAction.None,frame.Action);Check.Equal(Colour.Green,frame.ReleasedColours.Single());
                Check.Throws<ArgumentOutOfRangeException>(()=>router.Update((Phase)99,0,Controls()));
                Check.Throws<ArgumentOutOfRangeException>(()=>router.Update(Phase.Questionnaire,-1,Controls()));Check.Throws<ArgumentOutOfRangeException>(()=>router.Update(Phase.Questionnaire,Questionnaire.Count,Controls()));
                Check.Throws<ArgumentNullException>(()=>router.Update(Phase.Preparation,0,null));
            });
            suite.Run("Quest preview pending protocol decision permits deliberate withdrawal but no resume or continuation",()=>{
                var clock=new ManualClock();var experiment=Fixtures.New(clock);experiment.BeginFamiliarization(Actor.Owner);experiment.BeginPractice(Actor.Owner);experiment.MarkPresented(experiment.CurrentTrial.TrialId);
                experiment.Pause(Actor.Participant);experiment.Resume(true);Check.Equal(Phase.NeedsProtocolDecision,experiment.Phase);
                var router=new QuestPreviewInputRouter();var held=Update(router,experiment,8|16);Check.Equal(QuestPreviewAction.None,held.Action);Apply(experiment,held);
                Check.Equal(QuestPreviewAction.None,Update(router,experiment,8|16).Action);Apply(experiment,Update(router,experiment));
                var blocked=Update(router,experiment,1|2|4|16|32);Check.Equal(QuestPreviewAction.None,blocked.Action);Apply(experiment,blocked);Check.Equal(Phase.NeedsProtocolDecision,experiment.Phase);
                Apply(experiment,Update(router,experiment));var exit=Update(router,experiment,8|16);Check.Equal(QuestPreviewAction.Withdraw,exit.Action);Apply(experiment,exit);
                Check.Equal(Phase.Ended,experiment.Phase);Check.Equal((EndReason?)EndReason.Withdrawn,experiment.EndReason);
                Check.Equal(TrialOutcome.Interrupted,experiment.Trials[0].Outcome);Check.Equal<bool?>(null,experiment.Trials[0].Correct);
            });
        }
    }
}
