using System;
using AvatarStroop.Core;

namespace AvatarStroop.Tests
{
    public static class TrialRecordOwnershipTests
    {
        public static void Register(TestSuite suite)
        {
            suite.Run("trial owner capability rejects unrelated or null writer without modifying the live record",()=>{
                var clock=new ManualClock();var experiment=Fixtures.New(clock);experiment.BeginFamiliarization(Actor.Owner);experiment.BeginPractice(Actor.Owner);
                var retained=experiment.CurrentTrial;var otherKey=new object();
                var other=new TrialRecord(otherKey,"other","other-session","list",false,0,0,0,0,Condition.A,new Stimulus(Colour.Red,Colour.Blue));
                Check.Throws<UnauthorizedAccessException>(()=>retained.SetOnset(null,9));
                Check.Throws<UnauthorizedAccessException>(()=>retained.SetOnset(otherKey,9));
                Check.Throws<UnauthorizedAccessException>(()=>retained.SetResponseAt(otherKey,10));
                Check.Throws<UnauthorizedAccessException>(()=>retained.SetResponse(otherKey,Colour.Red));
                Check.Throws<UnauthorizedAccessException>(()=>retained.SetCorrect(otherKey,true));
                Check.Throws<UnauthorizedAccessException>(()=>retained.SetResponded(otherKey));
                Check.Throws<UnauthorizedAccessException>(()=>retained.SetInterrupted(otherKey));
                Check.Throws<UnauthorizedAccessException>(()=>retained.SetTechnicalIssue(otherKey));
                Check.True(!retained.Onset.HasValue&&!retained.ResponseAt.HasValue&&!retained.Response.HasValue&&!retained.Correct.HasValue&&!retained.TechnicalIssue);
                Check.Equal(TrialOutcome.Pending,retained.Outcome);Check.Equal(TrialOutcome.Pending,other.Outcome);
                experiment.MarkPresented(retained.TrialId);clock.Advance(.25);
                Check.True(experiment.ButtonChanged(retained.Stimulus.Ink,true));
                Check.True(Object.ReferenceEquals(retained,experiment.Trials[0]));Check.Equal(TrialOutcome.Responded,retained.Outcome);
                Check.Equal(true,retained.Correct.Value);Check.Near(.25,retained.ReactionSeconds.Value);
            });
            suite.Run("trial public properties cannot write record state",()=>{
                foreach(var property in typeof(TrialRecord).GetProperties())
                {
                    var setter=property.GetSetMethod(true);
                    Check.True(setter==null||setter.IsPrivate,"Unexpected writable setter: "+property.Name);
                }
                Check.True(typeof(TrialRecord).GetConstructor(Type.EmptyTypes)!=null,"Public parameterless constructor must remain available");
                var detached=new TrialRecord();Check.Equal(TrialOutcome.Pending,detached.Outcome);
            });
        }
    }
}
