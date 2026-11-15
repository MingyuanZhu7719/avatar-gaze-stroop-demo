using System;
using System.Linq;
using AvatarStroop.Core;

namespace AvatarStroop.Tests
{
    public static class ViveFocusRuntimeTests
    {
        public static void Register(TestSuite suite)
        {
            suite.Run("desktop focus observation alone does not pause or complete a trial",()=>{
                var clock=new ManualClock();var task=Fixtures.New(clock);
                task.BeginFamiliarization(Actor.Owner);task.BeginPractice(Actor.Owner);
                var trial=task.CurrentTrial;task.MarkPresented(trial.TrialId);clock.Advance(.2);
                task.RecordFocusState(false,true,false);
                Check.Equal(Phase.Practice,task.Phase);Check.Equal(TrialOutcome.Pending,trial.Outcome);
                Check.True(!trial.ReactionSeconds.HasValue&&task.CurrentTrial==trial);
                var focus=task.Events.Last(x=>x.Kind=="focus_changed");
                Check.Equal("False",focus.Fields["desktop_focused"]);Check.Equal("True",focus.Fields["xr_focused"]);
                Check.Equal("focus_lost",focus.Fields["reason"]);Check.Equal("NOT_ESTABLISHED",focus.Fields["measurement_quality"]);
                Check.True(!task.Events.Any(x=>x.Kind=="focus_pause"));
            });
            suite.Run("XR focus loss interrupts a trial and recovery cannot invent a valid RT",()=>{
                var clock=new ManualClock();var task=Fixtures.New(clock);
                task.BeginFamiliarization(Actor.Owner);task.BeginPractice(Actor.Owner);
                var trial=task.CurrentTrial;task.MarkPresented(trial.TrialId);clock.Advance(.2);
                task.RecordFocusState(true,false,false);
                Check.Equal(Phase.Paused,task.Phase);Check.Equal(TrialOutcome.Interrupted,trial.Outcome);
                Check.True(!trial.ReactionSeconds.HasValue);
                Check.True(task.Events.Any(x=>x.Kind=="focus_pause"&&x.Fields["reason"]=="xr_focus_lost"));
                Check.True(!task.Events.Any(x=>x.Kind=="explicit_pause"));
                clock.Advance(30);task.RecordFocusState(true,true,false);
                Check.Equal(Phase.Paused,task.Phase);Check.Equal(TrialOutcome.Interrupted,trial.Outcome);
                Check.True(!trial.ReactionSeconds.HasValue);
                task.Resume(true);Check.Equal(Phase.Practice,task.Phase);
                Check.True(task.CurrentTrial!=trial&&task.CurrentTrial.PlannedIndex==1&&!trial.ReactionSeconds.HasValue);
            });
            suite.Run("focus observations before practice neither start recording nor task",()=>{
                var clock=new ManualClock();var task=Fixtures.New(clock);
                task.RecordFocusState(false,false,true);task.RecordFocusState(true,true,false);
                Check.Equal(Phase.Preparation,task.Phase);Check.Equal(0,task.Trials.Count);
                Check.True(!task.Events.Any(x=>x.Kind=="focus_pause"));
                Check.Equal(2,task.Events.Count(x=>x.Kind=="focus_changed"));
                Check.Equal("focus_baseline",task.Events.First(x=>x.Kind=="focus_changed").Fields["reason"]);
            });
            suite.Run("focus recovery preserves an existing explicit pause",()=>{
                var clock=new ManualClock();var task=Fixtures.New(clock);
                task.BeginFamiliarization(Actor.Owner);task.BeginPractice(Actor.Owner);
                task.Pause(Actor.Participant);task.RecordFocusState(false,false,false);task.RecordFocusState(true,true,false);
                Check.Equal(Phase.Paused,task.Phase);
                Check.Equal(1,task.Events.Count(x=>x.Kind=="explicit_pause"));
                Check.Equal(0,task.Events.Count(x=>x.Kind=="focus_pause"));
            });
        }
    }
}
