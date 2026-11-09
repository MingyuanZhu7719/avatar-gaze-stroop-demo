using System;
using System.Collections.Generic;
using System.Reflection;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    // Isolated simulation only; no device, file, network, or production gate.
    public static class ViveParticipantPauseSafetyTests
    {
        sealed class Clock : IClock
        {
            public Action Hook;
            public double Now {get {var action=Hook;Hook=null;if(action!=null)action();return 1;}}
            public string ClockId {get{return "pause-safety-clock";}}
        }
        static ViveDevelopmentParticipant New(Clock clock,bool observe)
        {
            var allocation=new Allocation("pause-safety-allocation",new[]{Condition.B,Condition.C,Condition.A,Condition.D},GazeTarget.Participant,new[]{0,1,2,3});
            var config=new SessionConfiguration("pause-safety-session",42,allocation,RunKind.Simulation,"en",3,30,true,0,true,true,true);
            var map=new Dictionary<ViveTrackpadDirection,Colour>{{ViveTrackpadDirection.Up,Colour.Red},{ViveTrackpadDirection.Right,Colour.Green},{ViveTrackpadDirection.Down,Colour.Blue},{ViveTrackpadDirection.Left,Colour.Yellow}};
            var geometry=DevelopmentFixtures.Geometry();
            return observe?new ViveDevelopmentParticipant(config,clock,geometry,map,.5,true,new ExperimentIncrementalOptions(4096,128,16L*1024*1024),4096):
                new ViveDevelopmentParticipant(config,clock,geometry,map,.5,true);
        }
        static Experiment Engine(ViveDevelopmentParticipant p)
        {return (Experiment)typeof(ViveDevelopmentParticipant).GetField("experiment",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(p);}
        static void BeginPractice(ViveDevelopmentParticipant p)
        {p.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,false,0,0));p.OwnerBeginPractice(Actor.Owner);Check.Equal(Phase.Practice,p.Phase);}
        static void CheckActor(Experiment engine,string expected)
        {
            int count=0;
            foreach(var item in engine.Events)if(item.Kind=="explicit_pause")
            {string actor;Check.True(item.Fields.TryGetValue("actor",out actor));Check.Equal(expected,actor);count++;}
            Check.Equal(1,count,"Exactly one pause must be recorded");
        }
        static void FailObservation(Experiment engine)
        {
            var journal=typeof(Experiment).GetField("incrementalMutationJournal",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(engine);
            var budget=(ExperimentPersistentMutationBudget)journal.GetType().GetField("budget",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(journal);
            Check.True(!budget.TryReserve(budget.EstimatedBytesLimit));
        }
        public static void Register(TestSuite suite)
        {
            suite.Run("VIVE participant pause records the actual actor in default and observed practice",()=>{
                foreach(bool observe in new[]{false,true})
                {
                    var p=New(new Clock(),observe);BeginPractice(p);var trial=p.CurrentTrial;
                    p.ParticipantPause();Check.Equal(Phase.Paused,p.Phase);Check.Equal(TrialOutcome.Interrupted,trial.Outcome);
                    CheckActor(Engine(p),"Participant");Check.Equal(0,p.RatingCount);
                    if(observe)Check.True(!p.ObservationBlocked);
                }
            });
            suite.Run("VIVE participant pause remains available after observation failure and preserves withdrawal",()=>{
                var p=New(new Clock(),true);BeginPractice(p);var trial=p.CurrentTrial;var engine=Engine(p);FailObservation(engine);
                Check.True(p.ObservationFaulted);p.ParticipantPause();Check.Equal(Phase.Paused,p.Phase);Check.Equal(TrialOutcome.Interrupted,trial.Outcome);
                CheckActor(engine,"Participant");Check.True(p.ObservationFaulted);p.ParticipantWithdraw();
                Check.Equal(Phase.Ended,p.Phase);Check.Equal(EndReason.Withdrawn,p.EndReason.Value);Check.True(p.ObservationFaulted);
            });
            suite.Run("VIVE observation hides current head during in-progress task mutation",()=>{
                var clock=new Clock();var p=New(clock,true);p.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,false,0,0));
                bool hookRan=false,blocked=false;clock.Hook=()=>{hookRan=true;blocked=p.ObservationBlocked;};
                p.OwnerBeginPractice(Actor.Owner);Check.True(hookRan);Check.True(blocked,"In-progress action must mask presentation");
                Check.Equal(Phase.Practice,p.Phase);Check.True(!p.ObservationBlocked);
            });
        }
    }
}
