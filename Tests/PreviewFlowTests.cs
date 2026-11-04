using System;
using System.Globalization;
using System.IO;
using System.Linq;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    public static class PreviewFlowTests
    {
        static Allocation Allocation()
        {return new Allocation("preview-flow-fixture",new[]{Condition.B,Condition.C,Condition.A,Condition.D},GazeTarget.Participant,new[]{0,1,2,3});}
        static SessionConfiguration Configuration(double countdown=3,double rest=30,bool skipFinal=true,double intro=3)
        {return new SessionConfiguration("preview-flow-test",42,Allocation(),RunKind.Simulation,"en",countdown,rest,skipFinal,intro);}
        static Experiment New(ManualClock clock,double countdown=3,double rest=30,bool skipFinal=true,double intro=3)
        {return new Experiment(Configuration(countdown,rest,skipFinal,intro),clock);}
        static int Events(Experiment experiment,string kind)
        {return experiment.Events.Count(item=>item.Kind==kind);}
        static double Number(JournalEvent entry,string field)
        {return Double.Parse(entry.Fields[field],CultureInfo.InvariantCulture);}
        static void Start(Experiment experiment,ManualClock clock)
        {
            experiment.LockBlock(Actor.Owner,ControlSelection.Automatic,"preview-flow-owner");
            experiment.BeginCountdown(Actor.Owner,true);
            clock.Advance(experiment.Configuration.CountdownSeconds);experiment.Tick();
            Check.Equal(Phase.MainTask,experiment.Phase);
        }
        static void FinishMain(Experiment experiment,ManualClock clock)
        {
            for(int trial=0;trial<24;trial++)Fixtures.Answer(experiment,clock);
            Check.Equal(Phase.Questionnaire,experiment.Phase);
        }
        static Experiment AtQuestionnaire(ManualClock clock,double rest=30,bool skipFinal=true,double intro=3)
        {
            var experiment=New(clock,3,rest,skipFinal,intro);
            Fixtures.Practice(experiment,clock,8);Start(experiment,clock);FinishMain(experiment,clock);
            return experiment;
        }
        static void RateAll(Experiment experiment)
        {for(int item=0;item<Questionnaire.Count;item++)experiment.Rate(item+1);}
        static Experiment Enhanced(ManualClock clock)
        {return new Experiment(new SessionConfiguration("participant-paced-preview-test",42,Allocation(),RunKind.Simulation,"en",3,30,true,0,true,true,true),clock);}
        static Experiment EnhancedQuestionnaire(ManualClock clock)
        {
            var experiment=Enhanced(clock);Fixtures.Practice(experiment,clock,8);Start(experiment,clock);FinishMain(experiment,clock);
            return experiment;
        }

        public static void Register(TestSuite suite)
        {
            RegisterEnhanced(suite);
            RetainedSourceFlowTests.Register(suite);
            suite.Run("Preview flow configuration retains legacy defaults and records explicit timing policy",()=>{
                var legacy=new SessionConfiguration("legacy-flow-test",42,Allocation(),RunKind.Simulation,"en",1);
                Check.Near(30,legacy.MinimumRestSeconds);Check.True(!legacy.SkipFinalRest);Check.Near(0,legacy.QuestionnaireInstructionSeconds);
                var configured=Configuration(3,7.5,true,1.25);
                Check.Near(3,configured.CountdownSeconds);Check.Near(7.5,configured.MinimumRestSeconds);
                Check.True(configured.SkipFinalRest);Check.Near(1.25,configured.QuestionnaireInstructionSeconds);
                foreach(var configuration in new[]{legacy,configured})
                {
                    var experiment=new Experiment(configuration,new ManualClock());
                    var created=experiment.Events.Single(item=>item.Kind=="session_created");
                    Check.Equal(SessionConfiguration.SoftwareVersion,created.Fields["software"]);
                    Check.Equal(SessionConfiguration.ProtocolVersion,created.Fields["protocol"]);
                    Check.Near(configuration.MinimumRestSeconds,Number(created,"minimum_rest_seconds"));
                    Check.Equal(configuration.SkipFinalRest,Boolean.Parse(created.Fields["skip_final_rest"]));
                    Check.Near(configuration.QuestionnaireInstructionSeconds,Number(created,"questionnaire_instruction_seconds"));
                    Check.Equal("configured-preview-flow-v1",created.Fields["presentation_policy"]);
                    Check.Equal("phase_elapsed_including_instructions_excluding_explicit_pauses",created.Fields["questionnaire_duration_policy"]);
                    Check.Near(0,experiment.CountdownRemainingSeconds);Check.Near(0,experiment.RestRemainingSeconds);
                    Check.Near(0,experiment.QuestionnaireInstructionRemainingSeconds);Check.True(!experiment.QuestionnaireInputReady);
                }
            });
            suite.Run("Preview flow rejects negative nonfinite durations while permitting zero rest and instructions",()=>{
                foreach(double invalid in new[]{-.01,Double.NaN,Double.PositiveInfinity,Double.NegativeInfinity})
                {
                    Check.Throws<ArgumentOutOfRangeException>(()=>Configuration(rest:invalid));
                    Check.Throws<ArgumentOutOfRangeException>(()=>Configuration(intro:invalid));
                    Check.Throws<ArgumentOutOfRangeException>(()=>Configuration(countdown:invalid));
                }
                Check.Throws<ArgumentOutOfRangeException>(()=>Configuration(countdown:0));
                var zero=Configuration(rest:0,intro:0);Check.Near(0,zero.MinimumRestSeconds);Check.Near(0,zero.QuestionnaireInstructionSeconds);
            });
            suite.Run("Preview three second countdown reaches its exact boundary without manufacturing stimulus onset",()=>{
                var clock=new ManualClock();var experiment=New(clock);Fixtures.Practice(experiment,clock,8);
                experiment.LockBlock(Actor.Owner,ControlSelection.Automatic,"owner");experiment.BeginCountdown(Actor.Owner,true);
                double start=clock.Now;Check.Near(3,experiment.CountdownRemainingSeconds);
                clock.Now=start+2.999;experiment.Tick();Check.Equal(Phase.Countdown,experiment.Phase);
                Check.Near(.001,experiment.CountdownRemainingSeconds);Check.Equal(0,experiment.Trials.Count(item=>!item.IsPractice));
                clock.Now=start+3;Check.Near(0,experiment.CountdownRemainingSeconds);Check.Equal(Phase.Countdown,experiment.Phase);
                experiment.Tick();Check.Equal(Phase.MainTask,experiment.Phase);Check.Equal(0,experiment.CurrentTrial.PlannedIndex);
                Check.True(!experiment.CurrentTrial.Onset.HasValue);Check.Near(0,experiment.ActiveSeconds);
                int presented=Events(experiment,"stimulus_presented");clock.Advance(100);experiment.Tick();
                Check.True(!experiment.CurrentTrial.Onset.HasValue);Check.Equal(presented,Events(experiment,"stimulus_presented"));
                Check.Equal(1,experiment.Trials.Count(item=>!item.IsPractice));Check.Near(0,experiment.CountdownRemainingSeconds);
                experiment.MarkPresented(experiment.CurrentTrial.TrialId);Check.Near(clock.Now,experiment.CurrentTrial.Onset.Value);
            });
            suite.Run("Preview delayed countdown tick creates only one pending first trial",()=>{
                var clock=new ManualClock();var experiment=New(clock);Fixtures.Practice(experiment,clock,8);
                experiment.LockBlock(Actor.Owner,ControlSelection.Automatic,"owner");experiment.BeginCountdown(Actor.Owner,true);
                clock.Advance(40);Check.Near(0,experiment.CountdownRemainingSeconds);Check.Equal(Phase.Countdown,experiment.Phase);
                experiment.Tick();var first=experiment.CurrentTrial;experiment.Tick();clock.Advance(40);experiment.Tick();
                Check.Equal(Phase.MainTask,experiment.Phase);Check.Equal(first,experiment.CurrentTrial);
                Check.Equal(1,experiment.Trials.Count(item=>!item.IsPractice));Check.True(!first.Onset.HasValue);
            });
            suite.Run("Preview countdown pause freezes its remainder and still requires explicit source reacquisition",()=>{
                var clock=new ManualClock();var experiment=New(clock);Fixtures.Practice(experiment,clock,8);
                experiment.LockBlock(Actor.Owner,ControlSelection.Automatic,"owner");experiment.BeginCountdown(Actor.Owner,true);
                clock.Advance(1.25);experiment.Pause(Actor.Participant);Check.Near(1.75,experiment.CountdownRemainingSeconds);
                clock.Advance(100);experiment.Tick();Check.Equal(Phase.Paused,experiment.Phase);
                Check.Near(1.75,experiment.CountdownRemainingSeconds);Check.Equal(0,experiment.Trials.Count(item=>!item.IsPractice));
                Check.Throws<InvalidOperationException>(()=>experiment.Resume(false));Check.Equal(Phase.Paused,experiment.Phase);
                experiment.Resume(true);double resumed=clock.Now;Check.Near(1.75,experiment.CountdownRemainingSeconds);
                clock.Now=resumed+1.749;experiment.Tick();Check.Equal(Phase.Countdown,experiment.Phase);
                clock.Now=resumed+1.75;experiment.Tick();Check.Equal(Phase.MainTask,experiment.Phase);
                Check.True(!experiment.CurrentTrial.Onset.HasValue);Check.Near(0,experiment.ActiveSeconds);
            });
            suite.Run("Preview resuming an interrupted main trial starts the configured countdown for the next trial",()=>{
                var clock=new ManualClock();var experiment=New(clock);Fixtures.Practice(experiment,clock,8);Start(experiment,clock);
                var interrupted=experiment.CurrentTrial;experiment.MarkPresented(interrupted.TrialId);clock.Advance(.5);
                experiment.Pause(Actor.Operator);clock.Advance(20);experiment.Resume(true);
                Check.Equal(Phase.Countdown,experiment.Phase);Check.Near(3,experiment.CountdownRemainingSeconds);
                Check.Equal(TrialOutcome.Interrupted,interrupted.Outcome);Check.Equal(1,experiment.Segment);
                double resumed=clock.Now;clock.Now=resumed+2.999;experiment.Tick();Check.Equal(Phase.Countdown,experiment.Phase);
                clock.Now=resumed+3;experiment.Tick();Check.Equal(1,experiment.CurrentTrial.PlannedIndex);
                Check.True(!experiment.CurrentTrial.Onset.HasValue);Check.Near(.5,experiment.ActiveSeconds);
            });
            suite.Run("Preview configured rest freezes during pause and never advances without Ready",()=>{
                var clock=new ManualClock();var experiment=AtQuestionnaire(clock,rest:4.5,intro:0);RateAll(experiment);
                Check.Equal(Phase.Rest,experiment.Phase);Check.Near(4.5,experiment.RestRemainingSeconds);Check.True(!experiment.Ready());
                clock.Advance(1.25);experiment.Pause(Actor.Participant);Check.Near(3.25,experiment.RestRemainingSeconds);
                clock.Advance(100);experiment.Tick();Check.Near(3.25,experiment.RestRemainingSeconds);
                Check.True(!experiment.ReadyEnabled);Check.True(!experiment.View(Actor.Participant).ReadyEnabled);
                Check.Throws<InvalidOperationException>(()=>experiment.Ready());experiment.Resume(true);
                double resumed=clock.Now;clock.Now=resumed+3.249;Check.True(!experiment.ReadyEnabled);Check.True(!experiment.Ready());
                clock.Now=resumed+3.25;Check.True(experiment.ReadyEnabled);Check.Near(0,experiment.RestRemainingSeconds);
                experiment.Tick();clock.Advance(60);experiment.Tick();Check.Equal(Phase.Rest,experiment.Phase);
                Check.Near(0,experiment.RestRemainingSeconds);Check.True(experiment.Ready());
                Check.Equal(Phase.BlockPreparation,experiment.Phase);Check.Equal(1,experiment.BlockIndex);Check.True(!experiment.LockedControl.HasValue);
            });
            suite.Run("Preview zero rest enables Ready immediately but still requires an explicit action",()=>{
                var clock=new ManualClock();var experiment=AtQuestionnaire(clock,rest:0,intro:0);RateAll(experiment);
                Check.Equal(Phase.Rest,experiment.Phase);Check.True(experiment.ReadyEnabled);Check.Near(0,experiment.RestRemainingSeconds);
                experiment.Tick();clock.Advance(30);experiment.Tick();Check.Equal(Phase.Rest,experiment.Phase);
                experiment.Pause(Actor.Participant);Check.True(!experiment.ReadyEnabled);Check.Near(0,experiment.RestRemainingSeconds);
                clock.Advance(20);experiment.Resume(true);Check.True(experiment.ReadyEnabled);
                Check.True(experiment.Ready());Check.Equal(Phase.BlockPreparation,experiment.Phase);
            });
            suite.Run("Preview questionnaire blocks early ratings but retains the actual item and accepts the boundary without Tick",()=>{
                var clock=new ManualClock();var experiment=AtQuestionnaire(clock);double started=clock.Now;
                Check.Near(3,experiment.QuestionnaireInstructionRemainingSeconds);Check.True(!experiment.QuestionnaireInputReady);
                Check.Equal(Questionnaire.Item(0,"en"),experiment.View(Actor.Participant).QuestionnaireItem);
                Check.Equal(1,Events(experiment,"questionnaire_instruction_started"));Check.Equal(0,Events(experiment,"questionnaire_instruction_completed"));
                Check.Throws<InvalidOperationException>(()=>experiment.Rate(3));
                clock.Now=started+2.999;experiment.Tick();Check.Throws<InvalidOperationException>(()=>experiment.Rate(3));
                Check.Equal(0,experiment.Ratings.Count);Check.Equal(0,experiment.QuestionIndex);Check.Near(.001,experiment.QuestionnaireInstructionRemainingSeconds);
                clock.Now=started+3;Check.True(experiment.QuestionnaireInputReady);Check.Near(0,experiment.QuestionnaireInstructionRemainingSeconds);
                experiment.Rate(3);Check.Equal(1,experiment.Ratings.Count);Check.Equal(1,experiment.QuestionIndex);
                Check.Equal(1,Events(experiment,"questionnaire_instruction_completed"));
                for(int item=1;item<Questionnaire.Count;item++)
                {
                    Check.True(experiment.QuestionnaireInputReady);Check.Near(0,experiment.QuestionnaireInstructionRemainingSeconds);
                    Check.Equal(Questionnaire.Item(item,"en"),experiment.View(Actor.Participant).QuestionnaireItem);experiment.Rate(item+1);
                }
                experiment.Tick();Check.Equal(Phase.Rest,experiment.Phase);Check.True(!experiment.QuestionnaireInputReady);
                Check.Equal(1,Events(experiment,"questionnaire_instruction_started"));Check.Equal(1,Events(experiment,"questionnaire_instruction_completed"));
                Check.Near(3,Number(experiment.Events.Single(item=>item.Kind=="questionnaire_completed"),"duration_seconds"));
            });
            suite.Run("Preview delayed instruction tick records completion once without selecting a rating",()=>{
                var clock=new ManualClock();var experiment=AtQuestionnaire(clock);double started=clock.Now;
                clock.Advance(10);Check.Near(0,experiment.QuestionnaireInstructionRemainingSeconds);experiment.Tick();
                Check.Equal(Phase.Questionnaire,experiment.Phase);Check.True(experiment.QuestionnaireInputReady);Check.Equal(0,experiment.Ratings.Count);
                var completed=experiment.Events.Single(item=>item.Kind=="questionnaire_instruction_completed");
                Check.Near(started+10,completed.Time);Check.Near(3,Number(completed,"duration_seconds"));Check.Near(10,Number(completed,"observed_elapsed_seconds"));
                experiment.Tick();clock.Advance(10);experiment.Tick();experiment.Rate(3);
                Check.Equal(1,Events(experiment,"questionnaire_instruction_completed"));Check.Equal(1,experiment.Ratings.Count);
            });
            suite.Run("Preview questionnaire pause freezes instructions and excludes pauses from the inclusive phase duration",()=>{
                var clock=new ManualClock();var experiment=AtQuestionnaire(clock);
                clock.Advance(1);experiment.Pause(Actor.Participant);Check.Near(2,experiment.QuestionnaireInstructionRemainingSeconds);
                clock.Advance(100);experiment.Tick();Check.Equal(Phase.Paused,experiment.Phase);
                Check.Near(2,experiment.QuestionnaireInstructionRemainingSeconds);Check.True(!experiment.QuestionnaireInputReady);
                Check.Equal(0,Events(experiment,"questionnaire_instruction_completed"));Check.Throws<InvalidOperationException>(()=>experiment.Rate(3));
                experiment.Resume(true);Check.Near(2,experiment.QuestionnaireInstructionRemainingSeconds);
                clock.Advance(2);experiment.Rate(3);clock.Advance(2);experiment.Rate(3);
                experiment.Pause(Actor.Operator);clock.Advance(50);experiment.Tick();Check.True(!experiment.QuestionnaireInputReady);
                Check.Near(0,experiment.QuestionnaireInstructionRemainingSeconds);experiment.Resume(true);Check.True(experiment.QuestionnaireInputReady);
                for(int item=2;item<Questionnaire.Count;item++){clock.Advance(2);experiment.Rate(3);}
                var completed=experiment.Events.Single(item=>item.Kind=="questionnaire_completed");
                Check.Near(11,Number(completed,"duration_seconds"));Check.Equal(1,Events(experiment,"questionnaire_instruction_completed"));
                Check.Equal(5,experiment.Ratings.Count);Check.Equal(Phase.Rest,experiment.Phase);Check.Near(30,experiment.RestRemainingSeconds);
            });
            suite.Run("Preview pause at an already due instruction boundary records completion before freezing input",()=>{
                var clock=new ManualClock();var experiment=AtQuestionnaire(clock);clock.Advance(3);
                experiment.Pause(Actor.Participant);Check.Equal(1,Events(experiment,"questionnaire_instruction_completed"));
                Check.True(!experiment.QuestionnaireInputReady);Check.Near(0,experiment.QuestionnaireInstructionRemainingSeconds);
                clock.Advance(100);experiment.Tick();experiment.Resume(true);Check.True(experiment.QuestionnaireInputReady);
                experiment.Tick();experiment.Rate(3);Check.Equal(1,Events(experiment,"questionnaire_instruction_completed"));
                var done=experiment.Events.Single(item=>item.Kind=="questionnaire_instruction_completed");
                var pause=experiment.Events.First(item=>item.Kind=="explicit_pause");Check.True(done.Sequence<pause.Sequence);
            });
            suite.Run("Preview withdrawal and technical end during instructions never complete or answer the questionnaire",()=>{
                foreach(bool technical in new[]{false,true})foreach(bool paused in new[]{false,true})
                {
                    var clock=new ManualClock();var experiment=AtQuestionnaire(clock);clock.Advance(1);
                    if(paused){experiment.Pause(Actor.Participant);clock.Advance(100);}
                    if(technical)experiment.EndTechnical(Actor.Owner,"preview_flow_fixture_end");else experiment.Withdraw();
                    clock.Advance(100);experiment.Tick();
                    Check.Equal(Phase.Ended,experiment.Phase);Check.Equal<EndReason?>(technical?EndReason.TechnicalEnd:EndReason.Withdrawn,experiment.EndReason);
                    Check.Equal(0,experiment.Ratings.Count);Check.Equal(0,Events(experiment,"questionnaire_instruction_completed"));
                    Check.Equal(0,Events(experiment,"questionnaire_completed"));Check.Equal(1,Events(experiment,"session_ended"));
                    Check.True(!experiment.QuestionnaireInputReady);Check.Near(0,experiment.QuestionnaireInstructionRemainingSeconds);
                    Check.Throws<InvalidOperationException>(()=>experiment.Rate(3));
                }
            });
            suite.Run("Preview instructions repeat once on the next block rather than once per rating item",()=>{
                var clock=new ManualClock();var experiment=AtQuestionnaire(clock,rest:0);clock.Advance(3);experiment.Tick();RateAll(experiment);
                Check.Equal(1,Events(experiment,"questionnaire_instruction_started"));Check.Equal(1,Events(experiment,"questionnaire_instruction_completed"));
                Check.True(experiment.Ready());Start(experiment,clock);FinishMain(experiment,clock);
                Check.Equal(1,experiment.BlockIndex);Check.Equal(0,experiment.QuestionIndex);Check.Near(3,experiment.QuestionnaireInstructionRemainingSeconds);
                Check.True(!experiment.QuestionnaireInputReady);Check.Equal(2,Events(experiment,"questionnaire_instruction_started"));
                Check.Equal(1,Events(experiment,"questionnaire_instruction_completed"));Check.Throws<InvalidOperationException>(()=>experiment.Rate(3));
                clock.Advance(3);experiment.Rate(3);Check.Equal(2,Events(experiment,"questionnaire_instruction_completed"));
            });
            suite.Run("Preview four blocks preserve 96 main trials and 20 ratings with only three rests",()=>{
                var clock=new ManualClock();var experiment=New(clock);Fixtures.Practice(experiment,clock,8);
                for(int block=0;block<4;block++)
                {
                    Start(experiment,clock);FinishMain(experiment,clock);clock.Advance(3);experiment.Tick();
                    if(block==3)
                    {
                        for(int item=0;item<Questionnaire.Count-1;item++)experiment.Rate(item+1);
                        Check.Equal(Phase.Questionnaire,experiment.Phase);Check.Equal(19,experiment.Ratings.Count);
                        Check.True(!experiment.EndReason.HasValue);Check.Equal(0,Events(experiment,"final_rest_skipped"));
                        experiment.Rate(5);
                    }
                    else RateAll(experiment);
                    Check.Equal((block+1)*5,experiment.Ratings.Count);
                    if(block==3)
                    {
                        Check.Equal(Phase.Ended,experiment.Phase);Check.Equal<EndReason?>(EndReason.Completed,experiment.EndReason);
                        Check.True(!experiment.ReadyEnabled);Check.Near(0,experiment.RestRemainingSeconds);
                    }
                    else
                    {
                        Check.Equal(Phase.Rest,experiment.Phase);double restStarted=clock.Now;
                        clock.Now=restStarted+29.999;Check.True(!experiment.Ready());clock.Now=restStarted+30;Check.True(experiment.Ready());
                        Check.Equal(Phase.BlockPreparation,experiment.Phase);Check.True(!experiment.LockedControl.HasValue);
                    }
                }
                Check.Equal(96,experiment.Trials.Count(item=>!item.IsPractice));Check.Equal(8,experiment.Trials.Count(item=>item.IsPractice));
                Check.Equal(20,experiment.Ratings.Count);Check.True(experiment.Trials.All(item=>item.Outcome==TrialOutcome.Responded));
                Check.Equal(3,Events(experiment,"rest_ready"));Check.Equal(3,experiment.Events.Count(item=>item.Kind=="phase_changed"&&item.Fields["to"]=="Rest"));
                Check.Equal(4,Events(experiment,"questionnaire_instruction_started"));Check.Equal(4,Events(experiment,"questionnaire_instruction_completed"));
                Check.Equal(1,Events(experiment,"final_rest_skipped"));Check.Equal(1,Events(experiment,"session_ended"));
            });
            suite.Run("Preview legacy constructor keeps immediate ratings and the final explicit 30 second rest",()=>{
                var clock=new ManualClock();var configuration=new SessionConfiguration("legacy-flow-test",42,Allocation(),RunKind.Simulation,"en",1);
                var experiment=new Experiment(configuration,clock);Fixtures.Practice(experiment,clock,8);
                for(int block=0;block<4;block++)
                {
                    Start(experiment,clock);FinishMain(experiment,clock);Check.True(experiment.QuestionnaireInputReady);
                    Check.Near(0,experiment.QuestionnaireInstructionRemainingSeconds);RateAll(experiment);
                    Check.Equal(Phase.Rest,experiment.Phase);Check.Near(30,experiment.RestRemainingSeconds);Check.True(!experiment.ReadyEnabled);
                    double restStarted=clock.Now;clock.Now=restStarted+29.999;Check.True(!experiment.Ready());
                    clock.Now=restStarted+30;experiment.Tick();Check.Equal(Phase.Rest,experiment.Phase);Check.True(experiment.Ready());
                }
                Check.Equal(Phase.Ended,experiment.Phase);Check.Equal<EndReason?>(EndReason.Completed,experiment.EndReason);
                Check.Equal(96,experiment.Trials.Count(item=>!item.IsPractice));Check.Equal(20,experiment.Ratings.Count);
                Check.Equal(4,Events(experiment,"rest_ready"));Check.Equal(0,Events(experiment,"final_rest_skipped"));
                Check.Equal(0,Events(experiment,"questionnaire_instruction_started"));Check.Equal(0,Events(experiment,"questionnaire_instruction_completed"));
            });
        }

        static void RegisterEnhanced(TestSuite suite)
        {
            suite.Run("Participant paced preview policies are explicit while legacy constructors retain their defaults",()=>{
                var legacy=Configuration();
                Check.True(!legacy.QuestionnaireInstructionRequiresAcknowledgement&&!legacy.ParticipantPacedRest&&!legacy.RetainControlAcrossBlocks);
                var experiment=Enhanced(new ManualClock());var configuration=experiment.Configuration;
                Check.True(configuration.QuestionnaireInstructionRequiresAcknowledgement&&configuration.ParticipantPacedRest&&configuration.RetainControlAcrossBlocks);
                Check.Near(3,configuration.CountdownSeconds);Check.Near(30,configuration.MinimumRestSeconds);Check.Near(0,configuration.QuestionnaireInstructionSeconds);
                var created=experiment.Events.Single(item=>item.Kind=="session_created");
                Check.Equal("participant-paced-session-preview-v2",created.Fields["presentation_policy"]);
                Check.Equal("True",created.Fields["questionnaire_instruction_requires_acknowledgement"]);
                Check.Equal("participant_continue_no_minimum_no_automatic_advance",created.Fields["rest_policy"]);
                Check.Equal("explicit_once_after_practice_retained_for_session",created.Fields["source_lock_policy"]);
                Check.True(!experiment.AcknowledgeQuestionnaireInstruction());
            });
            suite.Run("Questionnaire introduction waits indefinitely for an explicit acknowledgement and preserves pause duration",()=>{
                var clock=new ManualClock();var experiment=EnhancedQuestionnaire(clock);
                Check.True(experiment.QuestionnaireInstructionPending&&!experiment.QuestionnaireInputReady);
                clock.Advance(600);experiment.Tick();
                Check.True(experiment.QuestionnaireInstructionPending&&!experiment.QuestionnaireInputReady);
                Check.Equal(0,Events(experiment,"questionnaire_instruction_completed"));Check.Throws<InvalidOperationException>(()=>experiment.Rate(3));
                experiment.Pause(Actor.Participant);clock.Advance(100);experiment.Tick();
                Check.True(experiment.QuestionnaireInstructionPending&&!experiment.QuestionnaireInputReady);
                Check.True(!experiment.AcknowledgeQuestionnaireInstruction());Check.Throws<InvalidOperationException>(()=>experiment.Rate(3));
                experiment.Resume(true);clock.Advance(15);
                Check.True(experiment.AcknowledgeQuestionnaireInstruction());Check.True(experiment.QuestionnaireInputReady&&!experiment.QuestionnaireInstructionPending);
                Check.Equal(0,experiment.QuestionIndex);Check.Equal(0,experiment.Ratings.Count);
                Check.True(!experiment.AcknowledgeQuestionnaireInstruction());experiment.Tick();
                Check.Equal(1,Events(experiment,"questionnaire_instruction_completed"));
                var instruction=experiment.Events.Single(item=>item.Kind=="questionnaire_instruction_completed");
                Check.Equal("explicit_participant_acknowledgement",instruction.Fields["completion_policy"]);Check.Near(615,Number(instruction,"observed_elapsed_seconds"));
                for(int item=0;item<Questionnaire.Count;item++){clock.Advance(2);experiment.Rate(item+1);}
                Check.Near(625,Number(experiment.Events.Single(item=>item.Kind=="questionnaire_completed"),"duration_seconds"));
                Check.Equal(Phase.Rest,experiment.Phase);Check.True(experiment.ReadyEnabled);Check.Near(30,experiment.RestRemainingSeconds);
                Check.True(!experiment.QuestionnaireInstructionPending&&!experiment.AcknowledgeQuestionnaireInstruction());
            });
            suite.Run("Unacknowledged preview instructions cannot complete after withdrawal or technical end",()=>{
                foreach(bool technical in new[]{false,true})foreach(bool paused in new[]{false,true})
                {
                    var clock=new ManualClock();var experiment=EnhancedQuestionnaire(clock);
                    if(paused)experiment.Pause(Actor.Participant);
                    if(technical)experiment.EndTechnical(Actor.Owner,"fixture_end");else experiment.Withdraw();
                    clock.Advance(1000);experiment.Tick();
                    Check.True(!experiment.QuestionnaireInstructionPending&&!experiment.QuestionnaireInputReady&&!experiment.AcknowledgeQuestionnaireInstruction());
                    Check.Equal(0,experiment.Ratings.Count);Check.Equal(0,Events(experiment,"questionnaire_instruction_completed"));
                    Check.Equal(0,Events(experiment,"questionnaire_completed"));Check.Equal(1,Events(experiment,"session_ended"));
                }
            });
            suite.Run("Session source is confirmed once across BCAD with participant paced rests and complete export provenance",()=>{
                var clock=new ManualClock();var experiment=Enhanced(clock);Fixtures.Practice(experiment,clock,8);
                Check.Equal(Phase.BlockPreparation,experiment.Phase);Check.True(!experiment.TryBeginRetainedBlockCountdown(true));
                Check.Throws<InvalidOperationException>(()=>experiment.BeginCountdown(Actor.Owner,true));
                experiment.LockBlock(Actor.Owner,ControlSelection.SimulatorRehearsal,"session-source-owner");
                Check.True(!experiment.TryBeginRetainedBlockCountdown(true));
                Check.Throws<InvalidOperationException>(()=>experiment.BeginCountdown(Actor.Owner,false));
                experiment.BeginCountdown(Actor.Owner,true);
                var order=new[]{Condition.B,Condition.C,Condition.A,Condition.D};
                for(int block=0;block<4;block++)
                {
                    Check.Equal(block,experiment.BlockIndex);Check.Equal<Condition?>(order[block],experiment.CurrentCondition);
                    Check.Equal<ControlSelection?>(ControlSelection.SimulatorRehearsal,experiment.LockedControl);
                    clock.Advance(3);experiment.Tick();Check.Equal(Phase.MainTask,experiment.Phase);Check.True(!experiment.CurrentTrial.Onset.HasValue);
                    FinishMain(experiment,clock);clock.Advance(1000);experiment.Tick();
                    Check.True(experiment.QuestionnaireInstructionPending);Check.Equal(block*5,experiment.Ratings.Count);
                    Check.True(experiment.AcknowledgeQuestionnaireInstruction());Check.Equal(block*5,experiment.Ratings.Count);
                    for(int item=0;item<Questionnaire.Count-1;item++)experiment.Rate(item+1);
                    Check.Equal(Phase.Questionnaire,experiment.Phase);Check.True(!experiment.EndReason.HasValue);
                    experiment.Rate(5);Check.Equal((block+1)*5,experiment.Ratings.Count);
                    if(block==3)break;
                    Check.Equal(Phase.Rest,experiment.Phase);Check.True(experiment.ReadyEnabled);Check.Near(30,experiment.RestRemainingSeconds);
                    if(block==1)
                    {
                        experiment.Pause(Actor.Participant);clock.Advance(50);experiment.Tick();
                        Check.Equal(Phase.Paused,experiment.Phase);Check.True(!experiment.ReadyEnabled);
                        Check.Throws<InvalidOperationException>(()=>experiment.Ready());experiment.Resume(true);
                    }
                    if(block==2){clock.Advance(1000);experiment.Tick();Check.Equal(Phase.Rest,experiment.Phase);Check.True(experiment.ReadyEnabled);}
                    Check.True(experiment.Ready());Check.Equal(Phase.BlockReady,experiment.Phase);
                    Check.True(experiment.RetainedSourceContinuationPending&&experiment.View(Actor.Participant).SourceContinuationPending);
                    Check.Throws<InvalidOperationException>(()=>experiment.Ready());
                    Check.Throws<InvalidOperationException>(()=>experiment.LockBlock(Actor.Owner,ControlSelection.Automatic,"different-owner"));
                    clock.Advance(100);experiment.Tick();Check.True(!experiment.TryBeginRetainedBlockCountdown(false));
                    Check.Equal(Phase.BlockReady,experiment.Phase);Check.Equal((block+1)*24,experiment.Trials.Count(item=>!item.IsPractice));
                    Check.True(experiment.TryBeginRetainedBlockCountdown(true));Check.True(!experiment.TryBeginRetainedBlockCountdown(true));
                    Check.Equal(Phase.Countdown,experiment.Phase);Check.Near(3,experiment.CountdownRemainingSeconds);
                }
                Check.Equal(Phase.Ended,experiment.Phase);Check.Equal<EndReason?>(EndReason.Completed,experiment.EndReason);
                Check.Equal(96,experiment.Trials.Count(item=>!item.IsPractice));Check.Equal(8,experiment.Trials.Count(item=>item.IsPractice));Check.Equal(20,experiment.Ratings.Count);
                Check.True(experiment.Trials.All(item=>item.Outcome==TrialOutcome.Responded));
                Check.Equal(1,Events(experiment,"session_source_confirmed"));Check.Equal(4,Events(experiment,"block_configuration_locked"));
                Check.Equal(3,Events(experiment,"retained_source_countdown_started"));Check.Equal(3,Events(experiment,"rest_ready"));
                Check.Equal(4,Events(experiment,"questionnaire_instruction_started"));Check.Equal(4,Events(experiment,"questionnaire_instruction_completed"));
                Check.Equal(1,Events(experiment,"final_rest_skipped"));Check.Equal(1,Events(experiment,"session_ended"));
                string confirmation=experiment.Events.Single(item=>item.Kind=="session_source_confirmed").Sequence.ToString(CultureInfo.InvariantCulture);
                foreach(var locked in experiment.Events.Where(item=>item.Kind=="block_configuration_locked"))
                {
                    Check.Equal("session-source-owner",locked.Fields["owner"]);Check.Equal("0",locked.Fields["confirmation_block_index"]);
                    Check.Equal(confirmation,locked.Fields["confirmation_event_sequence"]);
                    Check.Equal(locked.BlockIndex==0?"explicit_owner_confirmation":"retained_session",locked.Fields["selection_origin"]);
                }
                string root=Path.Combine(Path.GetTempPath(),"sf-"+Guid.NewGuid().ToString("N").Substring(0,12));
                try
                {
                    var result=AuditExporter.Export(experiment,new AuditLog(experiment.Configuration.SessionId,RunKind.Simulation),Path.Combine(root,"exports"),
                        new ExportProvenance("preview-flow-regression","manual clock fixture presentation","manual clock fixture response"));
                    Check.True(result.Success,result.Error);Check.True(result.SessionCompleted);
                    string request=Guid.NewGuid().ToString("N");PreviewExportReceipt receipt;string reason;
                    PreviewExportReceipt.WriteCompleted(root,experiment.Configuration.SessionId,request,result);
                    Check.True(PreviewExportReceipt.TryReadVerified(root,experiment.Configuration.SessionId,request,out receipt,out reason),reason);
                    Check.Equal(result.DirectoryPath,receipt.DirectoryPath);Check.Equal(experiment.Configuration.SessionId,receipt.SessionId);
                    var allocations=CsvRows(result.DirectoryPath,"allocations.csv");Check.Equal(4,allocations.Length);
                    for(int block=0;block<4;block++)
                    {Check.Equal(order[block].ToString(),allocations[block]["condition"]);Check.Equal("SimulatorRehearsal",allocations[block]["selected_control"]);}
                    var trials=CsvRows(result.DirectoryPath,"trials.csv");Check.Equal(104,trials.Length);
                    Check.Equal(96,trials.Count(row=>row["is_practice"]=="false"&&row["selected_control"]=="SimulatorRehearsal"));
                    var ratings=CsvRows(result.DirectoryPath,"ratings.csv");Check.Equal(20,ratings.Length);
                    for(int block=0;block<4;block++)for(int item=0;item<Questionnaire.Count;item++)
                    {
                        var row=ratings[block*Questionnaire.Count+item];Check.Equal(order[block].ToString(),row["condition"]);
                        Check.Equal(Questionnaire.Id(item),row["item_id"]);Check.Equal((item+1).ToString(CultureInfo.InvariantCulture),row["value"]);
                        Check.Equal("en",row["language"]);Check.Equal(Questionnaire.Version,row["item_version"]);
                    }
                    Check.Equal("Completed",CsvRows(result.DirectoryPath,"session.csv").Single()["end_reason"]);
                }
                finally{if(Directory.Exists(root))Directory.Delete(root,true);}
            });
        }
        // These controlled fixture tables contain no quoted cells; general CSV
        // escaping and arbitrary user text are independently covered by DataTests.
        static System.Collections.Generic.Dictionary<string,string>[] CsvRows(string directory,string file)
        {
            var lines=File.ReadAllLines(Path.Combine(directory,file));Check.True(lines.All(line=>!line.Contains("\"")));
            var headers=lines[0].Split(',');
            return lines.Skip(1).Select(line=>{
                var cells=line.Split(',');Check.Equal(headers.Length,cells.Length);
                var row=new System.Collections.Generic.Dictionary<string,string>();
                for(int i=0;i<headers.Length;i++)row.Add(headers[i],cells[i]);return row;
            }).ToArray();
        }
    }
}
