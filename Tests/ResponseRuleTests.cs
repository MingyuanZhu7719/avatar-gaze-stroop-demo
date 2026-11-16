using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Threading;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    public static class ResponseRuleTests
    {
        static Experiment New(ManualClock clock,ResponseRule rule)
        {
            var allocation=new Allocation("response-rule-fixture",new[]{Condition.A,Condition.B,Condition.D,Condition.C},GazeTarget.Participant,new[]{0,1,2,3});
            return new Experiment(new SessionConfiguration("response-rule-session",42,allocation,RunKind.Simulation,"en",1,0,true,responseRule:rule),clock);
        }
        static void Answer(Experiment e,ManualClock clock,Colour answer)
        {e.MarkPresented(e.CurrentTrial.TrialId);clock.Advance(.25);Check.True(e.ButtonChanged(answer,true));e.ButtonChanged(answer,false);}
        public static void Register(TestSuite suite)
        {
            suite.Run("VIVE controller input follows word meaning with observed participant state",()=>{
                var clock=new ManualClock();var baseline=Fixtures.New(clock).Configuration;
                var config=new SessionConfiguration("word-vive",42,baseline.Allocation,RunKind.Simulation,"en",5,30,true,0,true,true,true,responseRule:ResponseRule.WordMeaning);
                var p=new ViveDevelopmentParticipant(config,clock,DevelopmentFixtures.Geometry(),ViveFixedTrackpadMapping.Create(),.5,true,
                    new ExperimentIncrementalOptions(4096,128,16L*1024*1024),4096);
                Action neutral=()=>p.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,false,0,0));
                neutral();p.OwnerBeginPractice(Actor.Owner);
                for(int i=0;i<8;i++)
                {
                    neutral();var trial=p.CurrentTrial;p.MarkPresented(trial.TrialId);clock.Advance(.25);var answer=trial.Stimulus.Word;
                    double x=answer==Colour.Green?1:answer==Colour.Yellow?-1:0,y=answer==Colour.Red?1:answer==Colour.Blue?-1:0;
                    p.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,true,x,y));neutral();
                    Check.Equal(true,trial.Correct);Check.True(!p.ObservationFaulted);
                }
                Check.Equal(Phase.BlockPreparation,p.Phase);Check.Equal(ResponseRule.WordMeaning,p.ParticipantView.ResponseRule);
                p.OwnerLockSource(Actor.Owner,ControlSelection.Automatic,"word-vive-owner");p.OwnerBeginBlock(Actor.Owner);
                clock.Advance(5);p.Tick();Check.Equal(Phase.MainTask,p.Phase);Check.Equal(ResponseRule.WordMeaning,p.CurrentTrial.ResponseRule);
            });
            suite.Run("Word-meaning VRAW round-trip preserves actual stimulus, response rule, expected answer and questionnaire version",()=>{
                using(var input=new MemoryStream(ViveRawTrialProjectionTests.FourBlocks(responseRule:ResponseRule.WordMeaning),false))
                {
                    var read=ViveRawSessionJournalReader.ProjectTrials(input,true,true,4194304,5000,CancellationToken.None);
                    Check.True(read.TrialProjection!=null);var projection=read.TrialProjection;
                    Check.True(projection.Trials.All(x=>x.ResponseRule=="WordMeaning"&&x.ExpectedResponse==x.PlannedWord));
                    Check.True(projection.Trials.Any(x=>x.PlannedWord!=x.PlannedInk));
                    Check.True(projection.Trials.Where(x=>x.ResponseEvents==1).All(x=>x.Response==x.Word&&x.DeclaredCorrect=="True"));
                    Check.True(projection.Ratings.All(x=>x.Version==Questionnaire.WordMeaningVersion));
                    ViveRawTrialReport report;Check.True(ViveRawTrialReport.TryBuild(read,true,out report));
                    Check.True(report.TrialsCsv.Contains(",response_rule,expected_response\n"));
                }
            });
            suite.Run("Word-meaning rule scores actual word and keeps all four blocks and practice in one mode",()=>{
                var clock=new ManualClock();var e=New(clock,ResponseRule.WordMeaning);
                e.BeginFamiliarization(Actor.Owner);e.BeginPractice(Actor.Owner);
                while(e.Phase==Phase.Practice)Answer(e,clock,e.CurrentTrial.Stimulus.Word);
                Check.Equal(8,e.PracticeCorrect);Check.Equal(Phase.BlockPreparation,e.Phase);
                for(int block=0;block<4;block++)
                {
                    Fixtures.Start(e,clock);
                    while(e.Phase==Phase.MainTask)Answer(e,clock,e.CurrentTrial.Stimulus.Word);
                    Check.Equal(Phase.Questionnaire,e.Phase);
                    Check.True(e.View(Actor.Participant).QuestionnaireItem.Contains("colour named by the word"));
                    Fixtures.Ratings(e,clock);
                    if(block<3)Check.True(e.Ready());
                }
                Check.Equal(Phase.Ended,e.Phase);Check.Equal(104,e.Trials.Count);
                Check.True(e.Trials.Any(x=>!x.Stimulus.Congruent));
                Check.True(e.Trials.All(x=>x.ResponseRule==ResponseRule.WordMeaning&&x.ExpectedResponse==x.Stimulus.Word&&x.Correct==true));
                Check.True(e.Ratings.All(x=>x.Version==Questionnaire.WordMeaningVersion));
                Check.True(e.Events.All(x=>x.Fields["response_rule"]=="WordMeaning"));
                foreach(var item in e.Events.Where(x=>x.Kind=="trial_planned"||x.Kind=="stimulus_presented"||x.Kind=="first_response"))
                    Check.Equal(e.Trials.Single(x=>x.TrialId==item.TrialId).Stimulus.Word.ToString(),item.Fields["expected_response"]);
            });
            suite.Run("Word-meaning practice rejects ink answer for incongruent stimulus and gives the word answer",()=>{
                var clock=new ManualClock();var e=New(clock,ResponseRule.WordMeaning);
                e.BeginFamiliarization(Actor.Owner);e.BeginPractice(Actor.Owner);
                while(e.CurrentTrial.Stimulus.Congruent)Answer(e,clock,e.CurrentTrial.Stimulus.Word);
                var trial=e.CurrentTrial;Answer(e,clock,trial.Stimulus.Ink);
                Check.Equal(false,trial.Correct);Check.Equal(Phase.PracticeCorrection,e.Phase);
                Check.Equal("Select the colour named by the word: "+trial.Stimulus.Word,e.View(Actor.Participant).PracticeCorrection);
                Check.Equal(ResponseRule.WordMeaning,e.View(Actor.Participant).ResponseRule);
            });
            suite.Run("Response-rule configuration copies do not change a running session and default stays ink",()=>{
                var clock=new ManualClock();var original=Fixtures.New(clock);var copy=SessionConfiguration.WithResponseRule(original.Configuration,ResponseRule.WordMeaning);
                Check.Equal(ResponseRule.InkColor,original.Configuration.ResponseRule);Check.Equal(ResponseRule.WordMeaning,copy.ResponseRule);
                Check.True(Object.ReferenceEquals(copy.Allocation,original.Configuration.Allocation));
                Check.Throws<ArgumentOutOfRangeException>(()=>SessionConfiguration.WithResponseRule(copy,(ResponseRule)99));
                Check.Equal(Questionnaire.Version,Questionnaire.VersionFor(ResponseRule.InkColor));
                Check.Equal(Questionnaire.Item(0,"en"),Questionnaire.Item(0,"en",ResponseRule.InkColor));
                for(int i=2;i<5;i++)Check.Equal(Questionnaire.Item(i,"en"),Questionnaire.Item(i,"en",ResponseRule.WordMeaning));
                Fixtures.Practice(original,clock,8);Fixtures.Start(original,clock);
                while(original.CurrentTrial.Stimulus.Congruent)Fixtures.Answer(original,clock);
                var trial=original.CurrentTrial;Answer(original,clock,trial.Stimulus.Word);Check.Equal(false,trial.Correct);Check.Equal(trial.Stimulus.Ink,trial.ExpectedResponse);
            });
            suite.Run("Trial projection retains declared rule, detects conflicts and leaves old records unspecified",()=>{
                var accumulator=new ViveRawTrialAccumulator();
                accumulator.Observe("trial_planned","new",0,0,1,1,new Dictionary<string,string>{{"response_rule","WordMeaning"},{"expected_response","Red"}});
                accumulator.Observe("first_response","new",0,0,2,2,new Dictionary<string,string>{{"response_rule","InkColor"},{"expected_response","Green"}});
                accumulator.Observe("trial_planned","old",0,0,3,3,new Dictionary<string,string>());
                var trials=accumulator.Finish().Trials;
                Check.Equal("WordMeaning",trials[0].ResponseRule);Check.Equal("Red",trials[0].ExpectedResponse);Check.True(trials[0].ContextConflict);
                Check.Equal(null,trials[1].ResponseRule);Check.Equal(null,trials[1].ExpectedResponse);
            });
        }
    }
}
