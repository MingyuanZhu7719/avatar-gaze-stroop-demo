using System;
using System.Collections.Generic;
using System.Reflection;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    // Simulation participant, in-memory target and declared choices only.
    public static class VivePracticeTerminalValidatorTests
    {
        sealed class Clock:IClock
        { internal double Value=10; public string ClockId {get{return "terminal-validator-clock";}} public double Now {get{return Value;}} }
        sealed class Fixture
        {
            internal readonly Clock Clock=new Clock();
            internal readonly ViveDevelopmentParticipant Task;
            internal readonly VivePracticeConsentAuthority Choices;
            internal readonly VivePracticeOwnedMemoryStart Host;
            internal readonly VivePracticeTaskEventCursor Cursor;
            internal readonly VivePracticeCursorAnchor Anchor;
            internal ExperimentIncrementalSnapshot Checkpoint;
            internal ViveSyntheticStepTrace Last;
            internal int Count; internal long Sequence; internal double EventTime;
            internal Fixture()
            {
                var config=new SessionConfiguration("validator-session",42,new Allocation("validator-allocation",
                    new[]{Condition.B,Condition.C,Condition.A,Condition.D},GazeTarget.Participant,new[]{0,1,2,3}),
                    RunKind.Simulation,"en",3,30,true,0,true,true,true);
                var geometry=DevelopmentGeometry.FromLocalDirections(new Vec3(0,0,-1),new Vec3(0,0,1),"validator-geometry");
                var map=new Dictionary<ViveTrackpadDirection,Colour>{{ViveTrackpadDirection.Up,Colour.Red},
                    {ViveTrackpadDirection.Right,Colour.Green},{ViveTrackpadDirection.Down,Colour.Blue},{ViveTrackpadDirection.Left,Colour.Yellow}};
                Task=new ViveDevelopmentParticipant(config,Clock,geometry,map,.5,true,new ExperimentIncrementalOptions(4096,128,16L*1024*1024),4096);
                Choices=new VivePracticeConsentAuthority(Task,Clock,true);Check.True(Choices.ConfirmEye()&&Choices.ConfirmTask());
                var binding=new VivePracticeStartBinding(Task,Clock,Choices,true);
                Task.UpdateTrackpad(true,Neutral());Clock.Value=11;Task.OwnerBeginPractice(Actor.Owner);
                VivePracticeStartBinding.Ticket ticket;Check.True(binding.TryAcquire(1,4096,out ticket));
                var context=new VivePracticeContextV3(config.SessionId,"participant","stream","device",Clock.ClockId,"mapping",1,"epoch",0,null,VivePracticeEpochReasonV3.Initial,11);
                Host=new VivePracticeOwnedMemoryStart(Task,Clock,Choices,binding,context,new ViveExclusiveMemoryStore(4),"target",262144,512);
                Check.True(Host.TryStart(ticket));VivePracticeOwnedStartReceipt receipt;Check.True(Host.TryTakeReceipt(out receipt));
                Cursor=new VivePracticeTaskEventCursor(Host,receipt,4096,64);
                var field=typeof(VivePracticeTaskEventCursor).GetField("anchor",BindingFlags.Instance|BindingFlags.NonPublic);
                Check.True(field!=null);Anchor=(VivePracticeCursorAnchor)field.GetValue(Cursor);Check.True(Anchor!=null);
            }
            internal void Capture()
            {
                ViveDevelopmentParticipant.PracticeObservationFence f;
                Check.True(Task.TryAcquirePracticeObservationFence(Clock,out f)&&f!=null);
                Checkpoint=f.Snapshot;Count=f.StepTrace.Count;Last=f.StepTrace[Count-1];
                Sequence=Checkpoint.Scalars.EventCount-1;
                var all=Task.ReadEventsAfter(-1,4096);Check.True(all.Count>0);EventTime=all[all.Count-1].Time;
                Check.True(Task.TryConsumePracticeObservationFence(f));
            }
            internal void PrepareTrial()
            {Task.UpdateTrackpad(true,Neutral());Clock.Value+=1;Task.MarkPresented(Task.CurrentTrial.TrialId);}
            internal void Answer(bool correct)
            {
                PrepareTrial();var ink=Task.CurrentTrial.Stimulus.Ink;
                Task.UpdateTrackpad(true,Press(correct?ink:Other(ink)));
                if(!correct){Task.UpdateAuxiliary(false,false,false,0,true);Task.UpdateAuxiliary(true,false,false,0,true);}
            }
            internal ViveDevelopmentParticipant.PracticeTaskDrainFence DrainFence()
            {ViveDevelopmentParticipant.PracticeTaskDrainFence f;Check.True(Task.TryAcquirePracticeTaskDrainFence(Anchor,Clock,out f));return f;}
            internal bool Validate(ViveDevelopmentParticipant.PracticeTaskDrainFence f,out IReadOnlyList<JournalEvent> events)
            {return VivePracticeTerminalValidator.TryValidate(Anchor,f,Checkpoint,Last,Count,Last.Revision,Sequence,EventTime,Clock.Value,out events);}
        }
        static VivePreviewTrackpadSample Neutral(){return new VivePreviewTrackpadSample(true,false,0,0);}
        static VivePreviewTrackpadSample Press(Colour c)
        {switch(c){case Colour.Red:return new VivePreviewTrackpadSample(true,true,0,1);case Colour.Green:return new VivePreviewTrackpadSample(true,true,1,0);case Colour.Blue:return new VivePreviewTrackpadSample(true,true,0,-1);default:return new VivePreviewTrackpadSample(true,true,-1,0);}}
        static Colour Other(Colour c){return c==Colour.Red?Colour.Green:Colour.Red;}
        static void Kinds(IReadOnlyList<JournalEvent> events,params string[] kinds)
        {Check.True(events!=null);Check.Equal(kinds.Length,events.Count);for(int i=0;i<kinds.Length;i++)Check.Equal(kinds[i],events[i].Kind);}
        public static void Register(TestSuite suite)
        {
            suite.Run("terminal validator accepts actual final correct response",()=>{
                var f=new Fixture();for(int i=0;i<7;i++)f.Answer(true);f.PrepareTrial();f.Capture();
                f.Task.UpdateTrackpad(true,Press(f.Task.CurrentTrial.Stimulus.Ink));var fence=f.DrainFence();IReadOnlyList<JournalEvent> events;
                Check.True(f.Validate(fence,out events));Kinds(events,"first_response","practice_completed","phase_changed");
                Check.Equal("BlockPreparation",events[2].Fields["to"]);f.Task.AbandonPracticeTaskDrainFence(fence);
            });
            suite.Run("terminal validator accepts actual correction continuation",()=>{
                var f=new Fixture();for(int i=0;i<7;i++)f.Answer(true);f.PrepareTrial();var ink=f.Task.CurrentTrial.Stimulus.Ink;
                f.Task.UpdateTrackpad(true,Press(Other(ink)));f.Task.UpdateAuxiliary(false,false,false,0,true);f.Capture();
                f.Task.UpdateAuxiliary(true,false,false,0,true);var fence=f.DrainFence();IReadOnlyList<JournalEvent> events;
                Check.True(f.Validate(fence,out events));Kinds(events,"practice_correction_continue","practice_completed","phase_changed");
                f.Task.AbandonPracticeTaskDrainFence(fence);
            });
            suite.Run("terminal validator accepts actual first attempt failure",()=>{
                var f=new Fixture();for(int i=0;i<7;i++)f.Answer(i>=3);f.PrepareTrial();f.Capture();
                f.Task.UpdateTrackpad(true,Press(f.Task.CurrentTrial.Stimulus.Ink));var fence=f.DrainFence();IReadOnlyList<JournalEvent> events;
                Check.True(f.Validate(fence,out events));Kinds(events,"first_response","practice_completed","phase_changed");
                Check.Equal("PracticeRetry",events[2].Fields["to"]);f.Task.AbandonPracticeTaskDrainFence(fence);
            });
            suite.Run("terminal validator accepts actual pause in practice and correction",()=>{
                for(int mode=0;mode<2;mode++){
                    var f=new Fixture();f.PrepareTrial();if(mode==1)f.Task.UpdateTrackpad(true,Press(Other(f.Task.CurrentTrial.Stimulus.Ink)));
                    f.Capture();f.Task.ParticipantPause();var fence=f.DrainFence();IReadOnlyList<JournalEvent> events;
                    Check.True(f.Validate(fence,out events),"pause mode "+mode+" checkpoint="+f.Checkpoint.Task.Phase+" outcome="+f.Checkpoint.CurrentTrial.Value.Outcome+" correct="+f.Checkpoint.CurrentTrial.Value.Correct+" after="+fence.Snapshot.Task.Phase+" pausedFrom="+fence.Snapshot.PausedFrom);if(mode==0)Kinds(events,"trial_interrupted","explicit_pause","phase_changed");
                    else Kinds(events,"explicit_pause","phase_changed");f.Task.AbandonPracticeTaskDrainFence(fence);
                }
            });
            suite.Run("terminal validator accepts held key release and safety pause",()=>{
                var f=new Fixture();f.Task.UpdateTrackpad(true,Neutral());f.Task.UpdateTrackpad(true,Press(Colour.Red));f.Capture();
                Check.True(f.Checkpoint.Scalars.HeldMask!=0,"held not registered");f.Task.ParticipantPause();var fence=f.DrainFence();IReadOnlyList<JournalEvent> events;
                Check.True(f.Validate(fence,out events),"held pause rejected");Kinds(events,"trial_interrupted","explicit_pause","phase_changed");
                Check.Equal(0,fence.Snapshot.Scalars.HeldMask);f.Task.AbandonPracticeTaskDrainFence(fence);
            });
            suite.Run("terminal validator rejects forged checkpoint and stale sequence",()=>{
                var f=new Fixture();f.PrepareTrial();f.Capture();f.Task.ParticipantPause();var fence=f.DrainFence();IReadOnlyList<JournalEvent> events;
                Check.True(!VivePracticeTerminalValidator.TryValidate(f.Anchor,fence,f.Checkpoint,f.Last,f.Count,f.Last.Revision,f.Sequence-1,f.EventTime,f.Clock.Value,out events));
                Check.True(events==null);Check.True(!VivePracticeTerminalValidator.TryValidate(f.Anchor,fence,f.Checkpoint,f.Last,f.Count-1,f.Last.Revision,f.Sequence,f.EventTime,f.Clock.Value,out events));
                Check.True(events==null);f.Task.AbandonPracticeTaskDrainFence(fence);
            });
            suite.Run("terminal validator rejects backlog before terminal pause",()=>{
                var f=new Fixture();f.Capture();f.PrepareTrial();f.Task.ParticipantPause();var fence=f.DrainFence();IReadOnlyList<JournalEvent> events;
                Check.True(!f.Validate(fence,out events)&&events==null);f.Task.AbandonPracticeTaskDrainFence(fence);
            });
            suite.Run("terminal validator rejects time rollback and foreign anchor",()=>{
                var f=new Fixture();f.PrepareTrial();f.Capture();f.Clock.Value+=1;f.Task.ParticipantPause();var fence=f.DrainFence();IReadOnlyList<JournalEvent> events;
                Check.True(!VivePracticeTerminalValidator.TryValidate(f.Anchor,fence,f.Checkpoint,f.Last,f.Count,f.Last.Revision,f.Sequence,f.Clock.Value+1,f.Clock.Value+2,out events));
                Check.True(events==null);
                Check.True(!VivePracticeTerminalValidator.TryValidate(f.Anchor,fence,f.Checkpoint,f.Last,f.Count,f.Last.Revision,f.Sequence,f.EventTime,f.Clock.Value-.5,out events));
                Check.True(events==null);
                var other=new Fixture();Check.True(!VivePracticeTerminalValidator.TryValidate(other.Anchor,fence,f.Checkpoint,f.Last,f.Count,f.Last.Revision,f.Sequence,f.EventTime,f.Clock.Value,out events));
                Check.True(events==null);f.Task.AbandonPracticeTaskDrainFence(fence);
            });
            suite.Run("terminal validator rejects hidden pause resume before a later pause",()=>{
                var f=new Fixture();f.PrepareTrial();f.Capture();f.Task.ParticipantPause();
                f.Task.UpdateAuxiliary(false,false,false,0,true);f.Task.UpdateAuxiliary(true,false,false,0,true);
                Check.Equal(Phase.Practice,f.Task.Phase);f.Task.ParticipantPause();var fence=f.DrainFence();IReadOnlyList<JournalEvent> events;
                Check.True(!f.Validate(fence,out events)&&events==null);f.Task.AbandonPracticeTaskDrainFence(fence);
            });
            suite.Run("terminal drain rejects resumed or withdrawn participant",()=>{
                var f=new Fixture();f.Capture();f.Task.ParticipantPause();f.Task.UpdateAuxiliary(false,false,false,0,true);f.Task.UpdateAuxiliary(true,false,false,0,true);
                ViveDevelopmentParticipant.PracticeTaskDrainFence fence;Check.True(!f.Task.TryAcquirePracticeTaskDrainFence(f.Anchor,f.Clock,out fence));
                var g=new Fixture();g.Capture();g.Task.ParticipantWithdraw();Check.True(!g.Task.TryAcquirePracticeTaskDrainFence(g.Anchor,g.Clock,out fence));
            });
        }
    }
}
