using System;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    // Uses only synthetic Experiment objects, in-memory code, and no renderer.
    public static class ViveSyntheticMarkPresentedBudgetTests
    {
        sealed class Clock:IClock {public double Now {get;set;} public string ClockId {get{return "synthetic-clock-\u96ea";}}}
        sealed class Fixture
        {
            internal readonly Clock Clock=new Clock();internal readonly Experiment Task;
            internal readonly ViveNamedSessionFrameAdmission.Manifest Manifest;
            internal Fixture(ViveNamedOption option,string session="synthetic-session-\u96ea",string planId="synthetic-plan-\u96ea",
                string intentId="synthetic-intent-\u96ea",string geometry="synthetic-geometry-\u96ea")
            {
                var selection=ViveNamedOptionProvenance.Select(Actor.Owner,option,1,option==ViveNamedOption.NoAvatar?null:geometry);
                var plan=ViveNamedSimulationPlan.Select(Actor.Owner,planId,selection);
                var intent=ViveNamedRecordingIntent.Select(Actor.Owner,intentId,true,true);
                Task=new Experiment(SessionConfiguration.NamedRecordingDevelopment(session,37,plan,intent),Clock);
                Manifest=new ViveNamedSessionFrameAdmission.Manifest(session,Clock.ClockId,plan,intent,true);Manifest.SealChoiceTime(0,-1);
                Task.BeginFamiliarization(Actor.Owner);Task.BeginPractice(Actor.Owner);
            }
            internal void Main()
            {
                while(Task.Phase==Phase.Practice)
                {Task.MarkPresented(Task.CurrentTrial.TrialId);Clock.Now+=.2;var ink=Task.CurrentTrial.Stimulus.Ink;Check.True(Task.ButtonChanged(ink,true));Task.ButtonChanged(ink,false);}
                Task.ConfirmNamedPlan(Actor.Owner,"synthetic-owner");Task.BeginCountdown(Actor.Owner,true);Clock.Now+=3;Task.Tick();
                Check.Equal(Phase.MainTask,Task.Phase);
            }
            internal bool Capture(int limit,out ViveSyntheticMarkPresentedBudget budget,string source=ViveSyntheticMarkPresentedBudget.SourceAssumption,
                string eventSchema=ViveNamedRecordingIntent.EventSchemaVersion,string planSchema=ViveNamedSimulationPlan.SchemaVersion)
            {return ViveSyntheticMarkPresentedBudget.TryCapture(Task,Manifest,source,eventSchema,planSchema,limit,out budget);}
        }
        static void One(Fixture f,double delay)
        {
            int before=f.Task.Events.Count;ViveSyntheticMarkPresentedBudget budget;
            Check.True(f.Capture(1048616,out budget)&&budget!=null);Check.Equal(before,f.Task.Events.Count);
            Check.True(!budget.AuthorizesAction&&!budget.ActualDisplayProven&&!budget.FirstManifestIncluded&&budget.Durability=="UNKNOWN");
            Check.True(budget.TaskFrameUpperBound<=1048616);
            f.Clock.Now+=delay;f.Task.MarkPresented(f.Task.CurrentTrial.TrialId);
            int exact;Check.True(budget.TryVerifySyntheticResult(out exact));Check.True(exact>0&&exact<=budget.TaskFrameUpperBound);
        }
        public static void Register(TestSuite suite)
        {
            suite.Run("synthetic presented bound covers practice main options and changing active time",()=>{
                foreach(var option in new[]{ViveNamedOption.Automatic,ViveNamedOption.NoAvatar})
                {
                    var practice=new Fixture(option);One(practice,.01);
                    var main=new Fixture(option);main.Main();One(main,.25);
                    var ink=main.Task.CurrentTrial.Stimulus.Ink;Check.True(main.Task.ButtonChanged(ink,true));main.Task.ButtonChanged(ink,false);
                    One(main,123456.789);
                }
            });
            suite.Run("synthetic bound rejects wrong source schemas and excludes first manifest",()=>{
                var f=new Fixture(ViveNamedOption.Automatic);ViveSyntheticMarkPresentedBudget value;
                Check.True(!f.Capture(1048616,out value,source:"different-source")&&value==null);
                Check.True(!f.Capture(1048616,out value,eventSchema:"vive-named-recording-intent-v4")&&value==null);
                Check.True(!f.Capture(1048616,out value,planSchema:"vive-named-simulation-plan-v3")&&value==null);
                Check.True(f.Capture(1048616,out value)&&!value.FirstManifestIncluded);
            });
            suite.Run("synthetic unicode and per-frame limit are checked before action",()=>{
                var f=new Fixture(ViveNamedOption.Automatic);ViveSyntheticMarkPresentedBudget budget;
                Check.True(f.Capture(1048616,out budget));int upper=budget.TaskFrameUpperBound;int count=f.Task.Events.Count;
                Check.True(!f.Capture(upper-1,out budget)&&budget==null);Check.Equal(count,f.Task.Events.Count);
                Check.True(f.Capture(upper,out budget)&&budget.TaskFrameUpperBound==upper);
                Check.True(!f.Capture(1048617,out budget)&&budget==null);
                var oversized=new Fixture(ViveNamedOption.Automatic,geometry:new string('\u96ea',10923));
                Check.True(!oversized.Capture(1048616,out budget)&&budget==null);
            });
            suite.Run("synthetic large valid metadata still fits capped task schema",()=>{
                string session="synthetic-"+new string('s',32740);
                var f=new Fixture(ViveNamedOption.Automatic,session,new string('p',32768),new string('i',32768),new string('g',32768));
                f.Main();ViveSyntheticMarkPresentedBudget budget;Check.True(f.Capture(1048616,out budget));
                // Static source fields and one 32 KiB dynamic field cannot use
                // all 1 MiB; a generic 1 MiB payload construction is covered
                // by ViveRawTaskFrameMeasurementTests, not this task schema.
                Check.True(budget.TaskFrameUpperBound<1048616);One(f,.01);
            });
            suite.Run("synthetic post action mismatch does not claim a valid bound",()=>{
                var f=new Fixture(ViveNamedOption.Automatic);ViveSyntheticMarkPresentedBudget budget;
                Check.True(f.Capture(1048616,out budget));
                f.Task.MarkPresented(f.Task.CurrentTrial.TrialId);f.Task.Pause(Actor.Participant);
                int bytes;Check.True(!budget.TryVerifySyntheticResult(out bytes));Check.Equal(0,bytes);
            });
        }
    }
}
