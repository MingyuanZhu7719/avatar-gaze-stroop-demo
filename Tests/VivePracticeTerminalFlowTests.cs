using System;
using System.Collections.Generic;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    // Synthetic Simulation only. Terminal capture tests express the desired
    // whole-transaction behavior; the current practice-only cursor fails closed.
    public static class VivePracticeTerminalFlowTests
    {
        sealed class Clock:IClock
        {
            internal double Value=10;
            public string ClockId { get { return "terminal-clock"; } }
            public double Now { get { return Value; } }
        }
        sealed class Fixture
        {
            internal readonly Clock Clock=new Clock();
            internal readonly ViveDevelopmentParticipant Task;
            internal readonly VivePracticeConsentAuthority Choices;
            internal readonly ViveExclusiveMemoryStore Store=new ViveExclusiveMemoryStore(8);
            internal VivePracticeOwnedMemoryStart Host;
            internal VivePracticeTaskEventCursor Cursor;
            internal Fixture()
            {
                var config=new SessionConfiguration("terminal-session",42,
                    new Allocation("terminal-allocation",new[]{Condition.B,Condition.C,Condition.A,Condition.D},GazeTarget.Participant,new[]{0,1,2,3}),
                    RunKind.Simulation,"en",3,30,true,0,true,true,true);
                var geometry=DevelopmentGeometry.FromLocalDirections(new Vec3(0,0,-1),new Vec3(0,0,1),"terminal-geometry");
                var map=new Dictionary<ViveTrackpadDirection,Colour>{{ViveTrackpadDirection.Up,Colour.Red},{ViveTrackpadDirection.Right,Colour.Green},
                    {ViveTrackpadDirection.Down,Colour.Blue},{ViveTrackpadDirection.Left,Colour.Yellow}};
                Task=new ViveDevelopmentParticipant(config,Clock,geometry,map,.5,true,new ExperimentIncrementalOptions(4096,128,16L*1024*1024),4096);
                Choices=new VivePracticeConsentAuthority(Task,Clock,true);
                Check.True(Choices.ConfirmEye()&&Choices.ConfirmTask());
                Task.UpdateTrackpad(true,Neutral());
                StartAttempt(1);
            }
            internal void StartAttempt(int attempt)
            {
                var binding=new VivePracticeStartBinding(Task,Clock,Choices,true);
                Clock.Value+=1;Task.OwnerBeginPractice(Actor.Owner);
                VivePracticeStartBinding.Ticket ticket;Check.True(binding.TryAcquire(attempt,4096,out ticket));
                var context=new VivePracticeContextV3("terminal-session","participant","stream","device",Clock.ClockId,"mapping",attempt,
                    "epoch-"+attempt,0,null,VivePracticeEpochReasonV3.Initial,Clock.Value);
                Host=new VivePracticeOwnedMemoryStart(Task,Clock,Choices,binding,context,Store,"target-"+attempt,262144,512);
                Check.True(Host.TryStart(ticket));VivePracticeOwnedStartReceipt receipt;Check.True(Host.TryTakeReceipt(out receipt));
                Cursor=new VivePracticeTaskEventCursor(Host,receipt,4096,64);
            }
            internal VivePracticeMultiReadResult Read(int attempt)
            {return VivePracticeMultiReader.Read(Store.CopyForTest("target-"+attempt),true,262144,512);}
            internal void Drain(params string[] expectedTransaction)
            {
                bool observed=false;
                for(int i=0;i<32;i++)
                {
                    VivePracticeTaskEventTransaction tx;var result=Cursor.TryStage(out tx);
                    if(result==VivePracticeTaskCursorResult.NoEvents)
                    {Check.True(expectedTransaction.Length==0||observed,"missing expected transaction");return;}
                    Check.Equal(VivePracticeTaskCursorResult.Staged,result,"stage at phase "+Task.Phase);
                    Check.True(tx!=null);
                    if(expectedTransaction.Length>0)
                    {
                        Check.True(!observed,"unexpected additional transaction");
                        Check.Equal(expectedTransaction.Length,tx.Events.Count);
                        for(int j=0;j<expectedTransaction.Length;j++)Check.Equal(expectedTransaction[j],tx.Events[j].Kind);
                        observed=true;
                    }
                    Check.True(Host.TryAppendPending(Cursor,tx));
                }
                throw new InvalidOperationException("fixture drain did not terminate");
            }
            internal void Answer(bool correct,params string[] expectedTerminalTransaction)
            {
                Task.UpdateTrackpad(true,Neutral());Drain();Clock.Value+=1;
                Task.MarkPresented(Task.CurrentTrial.TrialId);Drain();
                var ink=Task.CurrentTrial.Stimulus.Ink;
                Task.UpdateTrackpad(true,Press(correct?ink:Other(ink)));
                if(correct)Drain(expectedTerminalTransaction);else Drain();
                if(!correct)
                {
                    Check.Equal(Phase.PracticeCorrection,Task.Phase);
                    Task.UpdateAuxiliary(false,false,false,0,true);Drain();
                    Task.UpdateAuxiliary(true,false,false,0,true);Drain(expectedTerminalTransaction);
                }
            }
            // Inspect authoritative task behavior even while the recording
            // cursor deliberately rejects unsupported terminal transitions.
            internal void AnswerRaw(bool correct)
            {
                Task.UpdateTrackpad(true,Neutral());Clock.Value+=1;
                Task.MarkPresented(Task.CurrentTrial.TrialId);
                var ink=Task.CurrentTrial.Stimulus.Ink;
                Task.UpdateTrackpad(true,Press(correct?ink:Other(ink)));
                if(!correct)
                {
                    Check.Equal(Phase.PracticeCorrection,Task.Phase);
                    Task.UpdateAuxiliary(false,false,false,0,true);
                    Task.UpdateAuxiliary(true,false,false,0,true);
                }
            }
        }
        static VivePreviewTrackpadSample Neutral(){return new VivePreviewTrackpadSample(true,false,0,0);}
        static VivePreviewTrackpadSample Press(Colour colour)
        {
            switch(colour)
            {
                case Colour.Red:return new VivePreviewTrackpadSample(true,true,0,1);
                case Colour.Green:return new VivePreviewTrackpadSample(true,true,1,0);
                case Colour.Blue:return new VivePreviewTrackpadSample(true,true,0,-1);
                case Colour.Yellow:return new VivePreviewTrackpadSample(true,true,-1,0);
                default:throw new ArgumentException();
            }
        }
        static Colour Other(Colour ink){return ink==Colour.Red?Colour.Green:Colour.Red;}
        static void Tail(IReadOnlyList<JournalEvent> events,params string[] kinds)
        {
            Check.True(events.Count>=kinds.Length);
            for(int i=0;i<kinds.Length;i++)Check.Equal(kinds[i],events[events.Count-kinds.Length+i].Kind);
        }
        static void Same(JournalEvent a,JournalEvent b)
        {
            Check.Equal(a.Sequence,b.Sequence);Check.Equal(a.Time,b.Time);Check.Equal(a.ClockId,b.ClockId);
            Check.Equal(a.SessionId,b.SessionId);Check.Equal(a.BlockIndex,b.BlockIndex);Check.Equal(a.Segment,b.Segment);
            Check.Equal(a.TrialId,b.TrialId);Check.Equal(a.Kind,b.Kind);Check.Equal(a.Fields.Count,b.Fields.Count);
            foreach(var field in a.Fields){Check.True(b.Fields.ContainsKey(field.Key));Check.Equal(field.Value,b.Fields[field.Key]);}
        }
        static void AssertCommitted(Fixture f,int attempt)
        {
            var read=f.Read(attempt);Check.Equal(VivePracticeMultiStatus.Unclean,read.Status);
            Check.True(!read.DurableStorageProven&&!read.FullPracticeCoverageProven);
            var all=f.Task.ReadEventsAfter(-1,4096);int start=-1;
            for(int i=0;i<all.Count;i++)if(all[i].Kind=="practice_started"&&all[i].Fields["attempt"]==attempt.ToString()){start=i;break;}
            Check.True(start>=0);Check.Equal(all.Count-start,read.Events.Count);
            for(int i=0;i<read.Events.Count;i++)Same(all[start+i],read.Events[i]);
        }
        public static void Register(TestSuite suite)
        {
            suite.Run("practice terminal correct answer commits complete block boundary transaction",()=>{
                var f=new Fixture();for(int i=0;i<7;i++)f.Answer(true);
                f.Answer(true,"first_response","practice_completed","phase_changed");
                Check.Equal(Phase.BlockPreparation,f.Task.Phase);Check.True(f.Cursor.BoundaryCommitted);AssertCommitted(f,1);
                var events=f.Read(1).Events;Tail(events,"first_response","practice_completed","phase_changed");
                Check.Equal(-1,events[events.Count-2].BlockIndex);Check.Equal(0,events[events.Count-1].BlockIndex);
            });
            suite.Run("practice terminal correction commits as separate complete transaction",()=>{
                var f=new Fixture();for(int i=0;i<7;i++)f.Answer(true);
                f.Answer(false,"practice_correction_continue","practice_completed","phase_changed");
                Check.Equal(Phase.BlockPreparation,f.Task.Phase);Check.True(f.Cursor.BoundaryCommitted);AssertCommitted(f,1);
                Tail(f.Read(1).Events,"first_response","phase_changed","practice_correction_continue","practice_completed","phase_changed");
            });
            suite.Run("practice retry records first boundary and rejects second attempt target",()=>{
                var f=new Fixture();for(int i=0;i<7;i++)f.Answer(i>=3);
                f.Answer(true,"first_response","practice_completed","phase_changed");
                Check.Equal(Phase.PracticeRetry,f.Task.Phase);Check.True(f.Cursor.BoundaryCommitted);AssertCommitted(f,1);
                Tail(f.Read(1).Events,"first_response","practice_completed","phase_changed");
                var first=f.Store.CopyForTest("target-1");
                var binding=new VivePracticeStartBinding(f.Task,f.Clock,f.Choices,true);
                f.Task.UpdateTrackpad(true,Neutral());f.Clock.Value+=1;f.Task.OwnerBeginPractice(Actor.Owner);
                var context=new VivePracticeContextV3("terminal-session","participant","stream","device",f.Clock.ClockId,"mapping",2,
                    "epoch-2",0,"epoch-1",VivePracticeEpochReasonV3.PracticeRetry,f.Clock.Value);
                bool rejected=false;
                try { new VivePracticeOwnedMemoryStart(f.Task,f.Clock,f.Choices,binding,context,f.Store,"target-2",262144,512); }
                catch(ArgumentException){rejected=true;}
                Check.True(rejected);Check.True(f.Store.CopyForTest("target-2")==null);
                var after=f.Store.CopyForTest("target-1");Check.Equal(first.Length,after.Length);
                for(int i=0;i<first.Length;i++)Check.Equal(first[i],after[i]);
            });
            suite.Run("practice pause commits whole transaction and old cursor never crosses resume",()=>{
                var f=new Fixture();f.Task.UpdateTrackpad(true,Neutral());f.Drain();f.Clock.Value+=1;
                f.Task.MarkPresented(f.Task.CurrentTrial.TrialId);f.Drain();
                f.Task.ParticipantPause();
                Check.Equal(Phase.Paused,f.Task.Phase);Tail(f.Task.ReadEventsAfter(-1,4096),"trial_interrupted","explicit_pause","phase_changed");
                f.Drain("trial_interrupted","explicit_pause","phase_changed");Check.True(f.Cursor.BoundaryCommitted);AssertCommitted(f,1);
                Tail(f.Read(1).Events,"trial_interrupted","explicit_pause","phase_changed");
                var paused=f.Store.CopyForTest("target-1");
                f.Task.UpdateTrackpad(true,Neutral());f.Task.UpdateAuxiliary(false,false,false,0,true);
                f.Task.UpdateAuxiliary(true,false,false,0,true);Check.Equal(Phase.Practice,f.Task.Phase);
                VivePracticeTaskEventTransaction tx;Check.Equal(VivePracticeTaskCursorResult.NoEvents,f.Cursor.TryStage(out tx));
                Check.True(tx==null&&f.Cursor.BoundaryCommitted);
                var after=f.Store.CopyForTest("target-1");Check.Equal(paused.Length,after.Length);
                for(int i=0;i<paused.Length;i++)Check.Equal(paused[i],after[i]);
            });
            suite.Run("practice withdrawal stops append and preserves fake committed prefix",()=>{
                var f=new Fixture();f.Task.UpdateTrackpad(true,Neutral());f.Drain();f.Clock.Value+=1;
                f.Task.MarkPresented(f.Task.CurrentTrial.TrialId);f.Drain();
                var before=f.Store.CopyForTest("target-1");f.Task.ParticipantWithdraw();Check.Equal(Phase.Ended,f.Task.Phase);
                Tail(f.Task.ReadEventsAfter(-1,4096),"trial_interrupted","session_ended","phase_changed");
                VivePracticeTaskEventTransaction tx;Check.True(f.Cursor.TryStage(out tx)!=VivePracticeTaskCursorResult.Staged);
                var after=f.Store.CopyForTest("target-1");Check.Equal(before.Length,after.Length);
                for(int i=0;i<before.Length;i++)Check.Equal(before[i],after[i]);
            });
            suite.Run("practice two attempts expose actual retry and failure order",()=>{
                var f=new Fixture();for(int i=0;i<8;i++)f.AnswerRaw(i>=3);
                Check.Equal(Phase.PracticeRetry,f.Task.Phase);
                Tail(f.Task.ReadEventsAfter(-1,4096),"first_response","practice_completed","phase_changed");
                f.Task.UpdateTrackpad(true,Neutral());f.Clock.Value+=1;f.Task.OwnerBeginPractice(Actor.Owner);
                Tail(f.Task.ReadEventsAfter(-1,4096),"practice_started","phase_changed","trial_planned");
                for(int i=0;i<8;i++)f.AnswerRaw(i>=3);
                Check.Equal(Phase.Ended,f.Task.Phase);
                Tail(f.Task.ReadEventsAfter(-1,4096),"first_response","practice_completed","session_ended","phase_changed");
            });
            suite.Run("practice final interrupted trial resumes into completion without fabricated answer",()=>{
                var f=new Fixture();for(int i=0;i<7;i++)f.AnswerRaw(true);
                f.Task.UpdateTrackpad(true,Neutral());f.Clock.Value+=1;f.Task.MarkPresented(f.Task.CurrentTrial.TrialId);
                f.Task.ParticipantPause();Check.Equal(Phase.Paused,f.Task.Phase);
                Tail(f.Task.ReadEventsAfter(-1,4096),"trial_interrupted","explicit_pause","phase_changed");
                f.Task.UpdateTrackpad(true,Neutral());f.Task.UpdateAuxiliary(false,false,false,0,true);
                f.Task.UpdateAuxiliary(true,false,false,0,true);
                Check.Equal(Phase.BlockPreparation,f.Task.Phase);
                Tail(f.Task.ReadEventsAfter(-1,4096),"explicit_resume","practice_resume_next_planned_trial","practice_completed","phase_changed");
            });
        }
    }
}
