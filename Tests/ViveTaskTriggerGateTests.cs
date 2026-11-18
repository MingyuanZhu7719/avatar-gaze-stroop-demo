using System;
using System.Linq;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    public static class ViveTaskTriggerGateTests
    {
        sealed class Fixture
        {
            internal readonly ManualClock Clock=new ManualClock();
            internal readonly ViveDevelopmentParticipant Task;
            readonly ViveTaskTriggerGate gate=new ViveTaskTriggerGate();
            bool previousHeld;
            internal Fixture()
            {
                var allocation=new Allocation("trigger-gate-allocation",new[]{Condition.B,Condition.C,Condition.A,Condition.D},GazeTarget.Participant,new[]{0,1,2,3});
                var configuration=new SessionConfiguration("trigger-gate-session",71,allocation,RunKind.Simulation,"en",3,30,true,0,true,true,true);
                Task=new ViveDevelopmentParticipant(configuration,Clock,DevelopmentFixtures.Geometry(),ViveFixedTrackpadMapping.Create(),.5,true);
                Ready();Task.OwnerBeginPractice(Actor.Owner);Ready();Trigger(false);
            }
            internal void Ready(){Task.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,false,0,0));}
            internal void Trigger(bool held,bool consumed=false,bool displayed=true)
            {
                bool down=held&&!previousHeld;previousHeld=held;
                Task.UpdateAuxiliary(gate.TakeConfirmationPress(Task.Phase,Task.InputReady,held,down,consumed),false,false,0,displayed);
            }
            internal void Answer(bool correct=true)
            {
                Ready();var trial=Task.CurrentTrial;Task.MarkPresented(trial.TrialId);
                var colour=correct?trial.Stimulus.Ink:(Colour)(((int)trial.Stimulus.Ink+1)%4);
                double x=colour==Colour.Green?1:colour==Colour.Yellow?-1:0,y=colour==Colour.Red?1:colour==Colour.Blue?-1:0;
                Task.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,true,x,y));Ready();
            }
            internal void ToQuestionnaire()
            {
                while(Task.Phase==Phase.Practice)Answer();
                Task.OwnerLockSource(Actor.Owner,ControlSelection.Automatic,"trigger-owner");Task.OwnerBeginBlock(Actor.Owner);
                Clock.Advance(3);Task.Tick();Ready();
                for(int i=0;i<23;i++)Answer();
                Trigger(true);Answer();Check.Equal(Phase.Questionnaire,Task.Phase);
            }
        }
        public static void Register(TestSuite suite)
        {
            suite.Run("VIVE task trigger requires a fresh physical press and consumes unavailable or already-used holds",()=>{
                var gate=new ViveTaskTriggerGate();
                Check.True(!gate.TakeConfirmationPress(Phase.Rest,true,false,false,false));
                Check.True(!gate.TakeConfirmationPress(Phase.Rest,true,true,false,false));
                Check.True(!gate.TakeConfirmationPress(Phase.Rest,true,true,true,false),"a late down bit must not resurrect a held press");
                Check.True(!gate.TakeConfirmationPress(Phase.Rest,true,false,false,false));
                Check.True(gate.TakeConfirmationPress(Phase.Rest,true,true,true,false));
                Check.True(!gate.TakeConfirmationPress(Phase.Rest,true,true,true,false));
                Check.True(!gate.TakeConfirmationPress(Phase.Rest,true,true,false,false));
                Check.True(!gate.TakeConfirmationPress(Phase.Rest,true,false,false,false));
                Check.True(!gate.TakeConfirmationPress(Phase.Rest,false,true,true,false));
                Check.True(!gate.TakeConfirmationPress(Phase.Rest,true,true,true,false));
                Check.True(!gate.TakeConfirmationPress(Phase.Rest,true,false,false,false));
                Check.True(!gate.TakeConfirmationPress(Phase.Rest,true,true,true,true));
                Check.True(!gate.TakeConfirmationPress(Phase.Rest,true,true,true,false));
                Check.True(!gate.TakeConfirmationPress(Phase.Rest,true,false,false,false));
                Check.True(gate.TakeConfirmationPress(Phase.Rest,true,true,true,false));
            });
            suite.Run("VIVE trigger on a trial or countdown cannot act on the next panel",()=>{
                foreach(var phase in new[]{Phase.Tutorial,Phase.Practice,Phase.Countdown,Phase.MainTask})
                {
                    var gate=new ViveTaskTriggerGate();
                    Check.True(!gate.TakeConfirmationPress(phase,true,true,true,false));
                    Check.True(!gate.TakeConfirmationPress(Phase.Questionnaire,true,true,true,false));
                    Check.True(!gate.TakeConfirmationPress(Phase.Questionnaire,true,false,false,false));
                    Check.True(gate.TakeConfirmationPress(Phase.Questionnaire,true,true,true,false));
                }
            });
            suite.Run("VIVE trigger held across correction cannot skip it and release never confirms or pauses",()=>{
                var f=new Fixture();f.Trigger(true);f.Answer(false);var incorrect=f.Task.CurrentTrial;
                f.Trigger(true);f.Trigger(true);Check.Equal(Phase.PracticeCorrection,f.Task.Phase);
                f.Trigger(false);Check.Equal(Phase.PracticeCorrection,f.Task.Phase);
                f.Trigger(true);Check.Equal(Phase.Practice,f.Task.Phase);Check.True(f.Task.CurrentTrial.TrialId!=incorrect.TrialId);
                f.Trigger(true);f.Trigger(false);Check.Equal(TrialOutcome.Pending,f.Task.CurrentTrial.Outcome);
                Check.True(f.Task.ReadEventsAfter(-1,4096).All(x=>x.Kind!="explicit_pause"));
            });
            suite.Run("VIVE fresh confirmation preserves automatic safety hold and cannot replay a recovery press",()=>{
                var f=new Fixture();var interrupted=f.Task.CurrentTrial;f.Task.MarkPresented(interrupted.TrialId);f.Trigger(true);
                f.Task.UpdateTrackpad(false,new VivePreviewTrackpadSample(true,false,0,0));f.Trigger(true);
                Check.Equal(Phase.Paused,f.Task.Phase);Check.Equal(TrialOutcome.Interrupted,interrupted.Outcome);
                f.Ready();f.Trigger(true);f.Trigger(true);Check.Equal(Phase.Paused,f.Task.Phase);
                f.Trigger(false);Check.Equal(Phase.Paused,f.Task.Phase);f.Trigger(true);Check.Equal(Phase.Practice,f.Task.Phase);
                var resumed=f.Task.CurrentTrial;Check.True(resumed.TrialId!=interrupted.TrialId);
                f.Trigger(true);f.Trigger(true);f.Trigger(false);Check.Equal(TrialOutcome.Pending,resumed.Outcome);
                Check.Equal(1,f.Task.ReadEventsAfter(-1,4096).Count(x=>x.Kind=="explicit_pause"));
            });
            suite.Run("VIVE questionnaire introduction ratings and rest each require a distinct released trigger press",()=>{
                var f=new Fixture();f.ToQuestionnaire();
                f.Trigger(true);f.Trigger(true);Check.True(f.Task.QuestionnaireInstructionPending);Check.Equal(0,f.Task.RatingCount);
                f.Trigger(false);f.Trigger(true);Check.True(!f.Task.QuestionnaireInstructionPending);
                f.Trigger(true);f.Trigger(true);f.Trigger(false);Check.Equal(0,f.Task.RatingCount);
                for(int i=0;i<5;i++)
                {
                    f.Trigger(true);Check.Equal(i+1,f.Task.RatingCount);
                    f.Trigger(true);f.Trigger(true);Check.Equal(i+1,f.Task.RatingCount);
                    f.Trigger(false);Check.Equal(i+1,f.Task.RatingCount);
                }
                Check.Equal(Phase.Rest,f.Task.Phase);f.Trigger(true);Check.Equal(Phase.BlockReady,f.Task.Phase);
                f.Task.Tick();Check.Equal(Phase.Countdown,f.Task.Phase);f.Trigger(true);f.Trigger(false);
                Check.Equal(Phase.Countdown,f.Task.Phase);
                Check.True(f.Task.ReadEventsAfter(-1,4096).All(x=>x.Kind!="explicit_pause"));
            });
        }
    }
}
