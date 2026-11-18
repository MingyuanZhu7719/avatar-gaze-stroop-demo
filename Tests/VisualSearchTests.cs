using System;
using System.Linq;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    public static class VisualSearchTests
    {
        static SessionConfiguration Configuration()
        {
            var allocation=new Allocation("search-pilot",new[]{Condition.B,Condition.C,Condition.A,Condition.D},GazeTarget.Participant,new[]{0,1,2,3});
            return SessionConfiguration.WithTaskKind(new SessionConfiguration("search-session",42,allocation,RunKind.Simulation,"en",1,0,true),TaskKind.VisualSearch);
        }
        static Experiment New(ManualClock clock){return new Experiment(Configuration(),clock);}
        static void Begin(Experiment e){e.BeginFamiliarization(Actor.Owner);e.BeginPractice(Actor.Owner);}
        static void Answer(Experiment e,ManualClock clock,bool correct=true)
        {
            var trial=e.CurrentTrial;e.MarkPresented(trial.TrialId);clock.Advance(.3);
            string item=correct?trial.VisualSearch.TargetId:trial.VisualSearch.Items.First(x=>x.Id!=trial.VisualSearch.TargetId).Id;
            Check.True(e.SelectVisualSearchItem(trial.TrialId,item));
        }
        public static void Register(TestSuite suite)
        {
            suite.Run("Visual search plans balance both sizes and target positions, round-trip exact layouts and avoid duplicate targets",()=>{
                foreach(uint seed in new uint[]{0,1,42,20261028,UInt32.MaxValue})
                {
                    var identities=new System.Collections.Generic.HashSet<string>();
                    for(int list=0;list<4;list++)
                    {
                        var plans=VisualSearchPlans.MainList(seed,list);Check.Equal(24,plans.Length);
                        Check.Equal(12,plans.Count(x=>x.VisualSearch.SetSize==12));Check.Equal(12,plans.Count(x=>x.VisualSearch.SetSize==24));
                        Check.Equal(24,plans.Select(x=>x.VisualSearch.FindItem(x.VisualSearch.TargetId).Slot).Distinct().Count());
                        Check.True(plans.Select(x=>x.Key).SequenceEqual(VisualSearchPlans.MainList(seed,list).Select(x=>x.Key)));
                        foreach(int size in new[]{12,24})
                        {
                            var group=plans.Select(x=>x.VisualSearch).Where(x=>x.SetSize==size).ToArray();
                            foreach(Colour colour in Enum.GetValues(typeof(Colour)))Check.Equal(3,group.Count(x=>x.TargetColour==colour));
                            foreach(VisualSearchPattern pattern in Enum.GetValues(typeof(VisualSearchPattern)))Check.Equal(3,group.Count(x=>x.TargetPattern==pattern));
                            Check.Equal(6,group.Count(x=>x.FindItem(x.TargetId).Slot%6<3));
                        }
                        foreach(var stimulus in plans)
                        {
                            var p=stimulus.VisualSearch;Check.True(identities.Add(p.LayoutId));Check.Equal(p.SetSize,p.Items.Select(x=>x.Slot).Distinct().Count());
                            Check.Equal(1,p.Items.Count(x=>x.Colour==p.TargetColour&&x.Pattern==p.TargetPattern));
                            Check.True(p.Items.Count(x=>x.Colour==p.TargetColour)>1&&p.Items.Count(x=>x.Pattern==p.TargetPattern)>1);
                            var copy=VisualSearchTrial.Parse(p.Serialize());Check.Equal(p.PlanIdentity,copy.PlanIdentity);Check.Equal(p.Serialize(),copy.Serialize());
                            Check.True(p.Serialize().Length<2048);Check.Throws<InvalidOperationException>(()=>{var ignored=stimulus.Word;});
                        }
                    }
                    for(int attempt=1;attempt<=2;attempt++)
                    {var p=VisualSearchPlans.Practice(seed,attempt);Check.Equal(8,p.Length);Check.Equal(4,p.Count(x=>x.VisualSearch.SetSize==12));}
                    Check.True(!VisualSearchPlans.Practice(seed,1).Select(x=>x.Key).SequenceEqual(VisualSearchPlans.Practice(seed,2).Select(x=>x.Key)));
                }
            });
            suite.Run("Visual search layout parser refuses duplicate slots, ambiguous target and noncanonical content",()=>{
                var p=VisualSearchPlans.MainList(42,0)[0].VisualSearch;var items=p.Items.ToArray();
                items[1]=items[0];Check.Throws<ArgumentException>(()=>new VisualSearchTrial("bad",1,items[0].Slot,items));
                items=p.Items.Select(x=>x.Id==p.TargetId?x:new VisualSearchItem(x.Slot,p.TargetColour,p.TargetPattern)).ToArray();
                Check.Throws<ArgumentException>(()=>new VisualSearchTrial("bad",1,p.FindItem(p.TargetId).Slot,items));
                Check.Throws<ArgumentException>(()=>VisualSearchTrial.Parse(p.Serialize()+";"));
                Check.Throws<ArgumentException>(()=>VisualSearchTrial.Parse(new String('x',2049)));
            });
            suite.Run("Visual search full four-block flow records 104 item responses, 20 task-specific ratings and no fake Stroop values",()=>{
                var clock=new ManualClock();var e=New(clock);Begin(e);
                while(e.Phase==Phase.Practice)Answer(e,clock);
                Check.Equal(8,e.PracticeCorrect);
                for(int block=0;block<4;block++)
                {
                    Fixtures.Start(e,clock);Check.Equal(TaskKind.VisualSearch,e.View(Actor.Participant).TaskKind);
                    while(e.Phase==Phase.MainTask)Answer(e,clock);
                    Check.True(e.View(Actor.Participant).QuestionnaireItem.Contains("target object"));Fixtures.Ratings(e,clock);
                    if(block<3)Check.True(e.Ready());
                }
                Check.Equal(EndReason.Completed,e.EndReason);Check.Equal(104,e.Trials.Count);Check.Equal(20,e.Ratings.Count);
                Check.True(e.Trials.All(x=>x.Correct==true&&x.Response==null&&x.SelectedItemId==x.VisualSearch.TargetId));
                Check.True(e.Ratings.All(x=>x.Version==Questionnaire.VisualSearchVersion));
                Check.True(e.Events.All(x=>x.Fields["task_kind"]=="VisualSearch"&&!x.Fields.ContainsKey("response_rule")));
                foreach(var item in e.Events.Where(x=>x.Kind=="trial_planned"||x.Kind=="stimulus_presented"||x.Kind=="first_response"))
                {Check.True(!item.Fields.ContainsKey("word")&&!item.Fields.ContainsKey("ink")&&!item.Fields.ContainsKey("planned_word")&&!item.Fields.ContainsKey("expected_response"));Check.True(item.Fields.ContainsKey("target_item"));}
                foreach(var item in e.Events.Where(x=>x.Kind=="trial_planned"))Check.True(VisualSearchTrial.Parse(item.Fields["visual_search_layout"])!=null);
            });
            suite.Run("Visual search rejects premature, stale, colour and off-board selections; wrong practice choice is recorded once",()=>{
                var clock=new ManualClock();var e=New(clock);Begin(e);var trial=e.CurrentTrial;
                Check.True(!e.SelectVisualSearchItem(trial.TrialId,trial.VisualSearch.TargetId));e.MarkPresented(trial.TrialId);
                Check.True(!e.SelectVisualSearchItem("stale",trial.VisualSearch.TargetId));Check.True(!e.SelectVisualSearchItem(trial.TrialId,"missing"));
                Check.True(!e.ButtonChanged(Colour.Red,true));e.ButtonChanged(Colour.Red,false);
                Check.True(e.RecordVisualSearchPointer(trial.TrialId,null,"click_miss"));Check.True(e.RecordVisualSearchPointer(trial.TrialId,trial.VisualSearch.TargetId,"hover"));
                Answer(e,clock,false);Check.Equal(Phase.PracticeCorrection,e.Phase);Check.Equal(false,trial.Correct);Check.Equal(0,e.PracticeCorrect);
                Check.True(e.View(Actor.Participant).VisualSearch!=null);Check.True(!e.SelectVisualSearchItem(trial.TrialId,trial.VisualSearch.TargetId));
                e.ContinuePractice();Check.True(e.CurrentTrial.TrialId!=trial.TrialId);Check.Equal(1,e.Events.Count(x=>x.Kind=="first_response"));
                Check.True(!e.RecordVisualSearchPointer(trial.TrialId,trial.VisualSearch.TargetId,"hover"));
            });
            suite.Run("Visual search timeout has no response and practice continues; a late click cannot score",()=>{
                var clock=new ManualClock();var e=New(clock);Begin(e);var trial=e.CurrentTrial;e.MarkPresented(trial.TrialId);
                clock.Advance(29.999);e.Tick();Check.Equal(TrialOutcome.Pending,trial.Outcome);
                clock.Advance(.001);Check.True(!e.SelectVisualSearchItem(trial.TrialId,trial.VisualSearch.TargetId));
                Check.Equal(TrialOutcome.TimedOut,trial.Outcome);Check.Equal(Phase.PracticeCorrection,e.Phase);
                Check.True(trial.ResponseAt==null&&trial.Correct==null&&trial.ReactionSeconds==null&&trial.SelectedItemId==null);
                Check.Equal(1,e.Events.Count(x=>x.Kind=="trial_timed_out"));e.Tick();Check.Equal(1,e.Events.Count(x=>x.Kind=="trial_timed_out"));
                e.ContinuePractice();while(e.Phase==Phase.Practice)Answer(e,clock);Check.Equal(7,e.PracticeCorrect);
                Fixtures.Start(e,clock);for(int i=0;i<23;i++)Answer(e,clock);
                trial=e.CurrentTrial;e.MarkPresented(trial.TrialId);clock.Advance(30);e.Tick();
                Check.Equal(Phase.Questionnaire,e.Phase);Check.Equal(TrialOutcome.TimedOut,trial.Outcome);
            });
            suite.Run("Visual search cue evidence requires a presented current trial and finite completed preview timing",()=>{
                var clock=new ManualClock();var e=New(clock);Begin(e);var trial=e.CurrentTrial;
                Check.True(!e.RecordVisualSearchCueEvidence(trial.TrialId,10,1.5));e.MarkPresented(trial.TrialId);
                Check.True(!e.RecordVisualSearchCueEvidence("stale",10,1.5));
                Check.True(!e.RecordVisualSearchCueEvidence(trial.TrialId,-1,1.5));
                Check.True(!e.RecordVisualSearchCueEvidence(trial.TrialId,Double.NaN,1.5));
                Check.True(!e.RecordVisualSearchCueEvidence(trial.TrialId,10,Double.PositiveInfinity));
                Check.True(!e.RecordVisualSearchCueEvidence(trial.TrialId,10,1.499));
                Check.True(e.RecordVisualSearchCueEvidence(trial.TrialId,10,1.51));
                var cue=e.Events.Single(x=>x.Kind=="visual_search_cue");
                Check.Equal("10",cue.Fields["cue_first_render_unity_seconds"]);Check.Equal("1.51",cue.Fields["cue_rendered_elapsed_seconds"]);
                Check.Equal("software_camera_callbacks_not_photon",cue.Fields["basis"]);Check.Equal(trial.VisualSearch.LayoutId,cue.Fields["layout_id"]);
                Answer(e,clock);Check.True(!e.RecordVisualSearchCueEvidence(trial.TrialId,10,1.51));
            });
            suite.Run("Visual search immutable snapshots and persistent trial values preserve object responses",()=>{
                var clock=new ManualClock();var e=new Experiment(Configuration(),clock,new ExperimentIncrementalOptions(4096,128,16L*1024*1024));Begin(e);
                var trial=e.CurrentTrial;Answer(e,clock);
                Check.True(!e.IncrementalObservationFaulted,e.IncrementalObservationIssue);
                var value=new VivePersistentTrialValue(trial);Check.Equal(TaskKind.VisualSearch,value.TaskKind);Check.Equal(trial.SelectedItemId,value.SelectedItemId);
                Check.True(value.ValidSearchValue);Check.Throws<InvalidOperationException>(()=>{var ignored=value.Word;});
                ExperimentIncrementalCommit next;ExperimentIncrementalSnapshot state;long head,after=0;
                while(e.TryReadIncrementalAfter(after,out next,out head,out state)&&next!=null)after=next.Revision;
                Check.True(e.IsIncrementalRevisionCurrent(after));
                Check.True(state!=null);ExperimentMutationTrialSnapshot copy;Check.True(state.TryGetTrial(0,out copy));
                Check.Equal(trial.VisualSearch.Serialize(),copy.SearchLayout);Check.Equal(trial.SelectedItemId,copy.SelectedItemId);
            });
            suite.Run("Visual search source remains Automatic and configuration copies leave Stroop unchanged",()=>{
                var clock=new ManualClock();var original=Fixtures.New(clock).Configuration;var config=SessionConfiguration.WithTaskKind(original,TaskKind.VisualSearch);
                Check.Equal(TaskKind.Stroop,original.TaskKind);Check.Equal(TaskKind.VisualSearch,config.TaskKind);
                Check.Throws<ArgumentOutOfRangeException>(()=>SessionConfiguration.WithTaskKind(original,(TaskKind)99));
                var e=new Experiment(config,clock);Begin(e);while(e.Phase==Phase.Practice)Answer(e,clock);
                Check.Throws<InvalidOperationException>(()=>e.LockBlock(Actor.Owner,ControlSelection.LiveWoz,"owner"));
                Check.Equal(Phase.BlockPreparation,e.Phase);e.LockBlock(Actor.Owner,ControlSelection.Automatic,"owner");Check.Equal(Phase.BlockReady,e.Phase);
            });
            suite.Run("Visual search selection trigger stays consumed across correction and questionnaire until physical release",()=>{
                var clock=new ManualClock();var config=Configuration();
                config=SessionConfiguration.WithTaskKind(new SessionConfiguration(config.SessionId,config.Seed,config.Allocation,RunKind.Simulation,"en",5,30,true,0,true,true,true),TaskKind.VisualSearch);
                var p=new ViveDevelopmentParticipant(config,clock,DevelopmentFixtures.Geometry(),ViveFixedTrackpadMapping.Create(),.5,true);
                var gate=new ViveTaskTriggerGate();bool wasHeld=false;
                Action<bool,bool> auxiliary=(held,consumed)=>{bool down=held&&!wasHeld;wasHeld=held;p.UpdateAuxiliary(gate.TakeConfirmationPress(p.Phase,true,held,down,consumed),false,false,0,true);};
                p.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,false,0,0));p.OwnerBeginPractice(Actor.Owner);auxiliary(false,false);
                var trial=p.CurrentTrial;p.MarkPresented(trial.TrialId);clock.Advance(.2);
                p.SelectVisualSearchItem(trial.TrialId,trial.VisualSearch.Items.First(x=>x.Id!=trial.VisualSearch.TargetId).Id);
                auxiliary(true,true);auxiliary(true,false);auxiliary(true,false);Check.Equal(Phase.PracticeCorrection,p.Phase);
                auxiliary(false,false);auxiliary(true,false);Check.Equal(Phase.Practice,p.Phase);auxiliary(false,false);
                while(p.Phase==Phase.Practice)
                {trial=p.CurrentTrial;p.MarkPresented(trial.TrialId);clock.Advance(.2);p.SelectVisualSearchItem(trial.TrialId,trial.VisualSearch.TargetId);}
                p.OwnerLockSource(Actor.Owner,ControlSelection.Automatic,"search-owner");p.OwnerBeginBlock(Actor.Owner);clock.Advance(5);p.Tick();
                auxiliary(false,false);
                while(p.Phase==Phase.MainTask)
                {trial=p.CurrentTrial;p.MarkPresented(trial.TrialId);clock.Advance(.2);p.SelectVisualSearchItem(trial.TrialId,trial.VisualSearch.TargetId);}
                Check.Equal(Phase.Questionnaire,p.Phase);auxiliary(true,true);auxiliary(true,false);auxiliary(true,false);
                Check.Equal(0,p.RatingCount);auxiliary(false,false);auxiliary(true,false);Check.True(!p.QuestionnaireInstructionPending);
                auxiliary(false,false);auxiliary(true,false);Check.Equal(1,p.RatingCount);
            });
        }
    }
}
