using System;
using System.Collections.Generic;
using System.Reflection;
using AvatarStroop.Core;

namespace AvatarStroop.Tests
{
    public static class NamedRecordingPlanPreparationTests
    {
        sealed class Clock:IClock {public double Now {get;set;} public string ClockId {get{return "synthetic-plan-clock";}}}
        static Experiment New(Clock clock)
        {
            var a=ViveNamedOptionProvenance.Select(Actor.Owner,ViveNamedOption.Automatic,2,"synthetic-geometry");
            var b=ViveNamedOptionProvenance.Select(Actor.Owner,ViveNamedOption.NoAvatar,1,null);
            var plan=ViveNamedSimulationPlan.Select(Actor.Owner,"synthetic-plan",a,b);
            var intent=ViveNamedRecordingIntent.Select(Actor.Owner,"synthetic-intent",true,true);
            return new Experiment(SessionConfiguration.NamedRecordingDevelopment("synthetic-plan-session",37,plan,intent),clock);
        }
        static Stimulus[] PrivateArray(Experiment e,string field)
        {var f=typeof(Experiment).GetField(field,BindingFlags.Instance|BindingFlags.NonPublic);Check.True(f!=null);return (Stimulus[])f.GetValue(e);}
        static void Answer(Experiment e,bool correct)
        {
            var trial=e.CurrentTrial;e.MarkPresented(trial.TrialId);
            var value=correct?trial.Stimulus.Ink:(Colour)(((int)trial.Stimulus.Ink+1)%4);
            Check.True(e.ButtonChanged(value,true));e.ButtonChanged(value,false);
            if(e.Phase==Phase.PracticeCorrection)e.ContinuePractice();
        }
        static JournalEvent Copy(JournalEvent e)
        {
            var fields=new Dictionary<string,string>();foreach(var pair in e.Fields)fields.Add(pair.Key,pair.Value);
            return new JournalEvent(e.Sequence,e.Time,e.ClockId,e.SessionId,e.BlockIndex,e.Segment,e.TrialId,e.Kind,fields,e.NamedOption,e.NamedPlan,e.NamedBlock,e.NamedRecordingIntent);
        }
        public static void Register(TestSuite suite)
        {
            suite.Run("named recording prepares actual practice array without starting practice",()=>{
                var c=new Clock();var e=New(c);Check.Throws<UnauthorizedAccessException>(()=>e.PrepareNamedRecordingBlockPlan(Actor.Participant,true));
                var p=e.PrepareNamedRecordingBlockPlan(Actor.Owner,true);var original=PrivateArray(e,"practice");
                Check.Equal(Phase.Preparation,e.Phase);Check.Equal(0,e.PracticeAttempt);Check.Equal("PRACTICE",p.Fields["plan_scope"]);
                Check.Equal("1",p.Fields["attempt"]);Check.Equal("8",p.Fields["item_count"]);Check.True(e.MatchesNamedRecordingBlockPlan(p));
                Check.True(Object.ReferenceEquals(p,e.PrepareNamedRecordingBlockPlan(Actor.Owner,true)));
                Check.True(!e.MatchesNamedRecordingBlockPlan(Copy(p)));
                var saved=original[0];original[0]=new Stimulus((Colour)(((int)saved.Word+1)%4),saved.Ink);
                Check.True(!e.MatchesNamedRecordingBlockPlan(p));Check.Throws<InvalidOperationException>(()=>e.PrepareNamedRecordingBlockPlan(Actor.Owner,true));
                e.BeginFamiliarization(Actor.Owner);Check.Throws<InvalidOperationException>(()=>e.BeginPractice(Actor.Owner));Check.Equal(0,e.PracticeAttempt);
                original[0]=saved;Check.True(e.MatchesNamedRecordingBlockPlan(p));e.BeginPractice(Actor.Owner);
                Check.True(Object.ReferenceEquals(original,PrivateArray(e,"practice")));Check.True(Object.ReferenceEquals(original[0],e.CurrentTrial.Stimulus));
                Check.True(!e.MatchesNamedRecordingBlockPlan(p));
            });
            suite.Run("named recording practice retry binds new attempt and pause retains plan",()=>{
                var c=new Clock();var e=New(c);var first=e.PrepareNamedRecordingBlockPlan(Actor.Owner,true);
                e.BeginFamiliarization(Actor.Owner);e.BeginPractice(Actor.Owner);var interrupted=e.CurrentTrial;
                e.MarkPresented(interrupted.TrialId);e.Pause(Actor.Participant);e.Resume(true);
                Check.Equal(TrialOutcome.Interrupted,interrupted.Outcome);Check.Equal(1,e.CurrentTrial.PlannedIndex);
                while(e.Phase==Phase.Practice)Answer(e,false);
                Check.Equal(Phase.PracticeRetry,e.Phase);var second=e.PrepareNamedRecordingBlockPlan(Actor.Owner,true);
                Check.Equal("2",second.Fields["attempt"]);Check.True(!Object.ReferenceEquals(first,second)&&e.MatchesNamedRecordingBlockPlan(second));
                var array=PrivateArray(e,"practice");e.BeginPractice(Actor.Owner);
                Check.True(Object.ReferenceEquals(array,PrivateArray(e,"practice"))&&Object.ReferenceEquals(array[0],e.CurrentTrial.Stimulus));
            });
            suite.Run("named recording main plan binds actual list and interruption preserves index",()=>{
                var c=new Clock();var e=New(c);e.PrepareNamedRecordingBlockPlan(Actor.Owner,true);e.BeginFamiliarization(Actor.Owner);e.BeginPractice(Actor.Owner);
                while(e.Phase==Phase.Practice)Answer(e,true);
                e.ConfirmNamedPlan(Actor.Owner,"synthetic-owner");Check.Equal(Phase.BlockReady,e.Phase);
                var plan=e.PrepareNamedRecordingBlockPlan(Actor.Owner,false);
                Check.Equal("MAIN",plan.Fields["plan_scope"]);Check.Equal("0",plan.Fields["attempt"]);Check.Equal("24",plan.Fields["item_count"]);
                Check.True(e.MatchesNamedRecordingBlockPlan(plan)&&Object.ReferenceEquals(plan,e.PrepareNamedRecordingBlockPlan(Actor.Owner,false)));
                var listsField=typeof(Experiment).GetField("lists",BindingFlags.Instance|BindingFlags.NonPublic);Check.True(listsField!=null);
                var items=((Stimulus[][])listsField.GetValue(e))[2];
                for(int i=0;i<items.Length;i++)
                {
                    string prefix="item_"+i.ToString("D2",System.Globalization.CultureInfo.InvariantCulture);
                    Check.Equal(items[i].Word.ToString(),plan.Fields[prefix+"_word"]);Check.Equal(items[i].Ink.ToString(),plan.Fields[prefix+"_ink"]);
                }
                var saved=items[23];items[23]=new Stimulus(saved.Word,(Colour)(((int)saved.Ink+1)%4));
                Check.True(!e.MatchesNamedRecordingBlockPlan(plan));Check.Throws<InvalidOperationException>(()=>e.PrepareNamedRecordingBlockPlan(Actor.Owner,false));items[23]=saved;
                Check.True(!e.MatchesNamedRecordingBlockPlan(Copy(plan)));e.BeginCountdown(Actor.Owner,true);c.Now+=3;e.Tick();
                Check.Equal(plan.Fields["item_00_word"],e.CurrentTrial.Stimulus.Word.ToString());Check.Equal(plan.Fields["item_00_ink"],e.CurrentTrial.Stimulus.Ink.ToString());
                var interrupted=e.CurrentTrial;e.MarkPresented(interrupted.TrialId);e.Pause(Actor.Participant);e.Resume(true);c.Now+=3;e.Tick();
                Check.Equal(TrialOutcome.Interrupted,interrupted.Outcome);Check.Equal(1,e.CurrentTrial.PlannedIndex);
                Check.Equal(plan.Fields["item_01_word"],e.CurrentTrial.Stimulus.Word.ToString());Check.Equal(plan.Fields["item_01_ink"],e.CurrentTrial.Stimulus.Ink.ToString());
                Check.True(!e.MatchesNamedRecordingBlockPlan(plan));
            });
            suite.Run("legacy named simulation cannot create recording plan events",()=>{
                var c=new Clock();var option=ViveNamedOptionProvenance.Select(Actor.Owner,ViveNamedOption.Automatic,0,"synthetic-geometry");
                var plan=ViveNamedSimulationPlan.Select(Actor.Owner,"legacy-plan",option);
                var e=new Experiment(SessionConfiguration.NamedPlanDevelopment("legacy-session",37,plan),c);int before=e.Events.Count;
                Check.Throws<InvalidOperationException>(()=>e.PrepareNamedRecordingBlockPlan(Actor.Owner,true));Check.Equal(before,e.Events.Count);
            });
        }
    }
}
