using System;
using System.Collections.Generic;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    // Contract tests for a future issuer-owned retry capability. Simulation and
    // sealed fake targets only: neither consent authentication nor durable I/O.
    public static class VivePracticeRetryFlowTests
    {
        sealed class Clock:IClock
        {
            internal double Value=10;
            public string ClockId {get{return "retry-clock";}}
            public double Now {get{return Value;}}
        }
        sealed class Fixture
        {
            internal readonly Clock Clock=new Clock();
            internal readonly ViveDevelopmentParticipant Task;
            internal readonly VivePracticeConsentAuthority Choices;
            internal readonly ViveExclusiveMemoryStore Store=new ViveExclusiveMemoryStore(8);
            internal VivePracticeOwnedMemoryStart First,Second;
            internal VivePracticeTaskEventCursor FirstCursor,SecondCursor;
            internal VivePracticeStartBinding SecondBinding;
            internal VivePracticeStartBinding.Ticket SecondTicket;
            internal VivePracticeRetryContinuation Retry;
            internal VivePracticeConsentAuthority.StartReservation SecondReservation;
            internal readonly long Generation;
            internal Fixture()
            {
                var config=new SessionConfiguration("retry-session",42,
                    new Allocation("retry-allocation",new[]{Condition.B,Condition.C,Condition.A,Condition.D},GazeTarget.Participant,new[]{0,1,2,3}),
                    RunKind.Simulation,"en",3,30,true,0,true,true,true);
                var geometry=DevelopmentGeometry.FromLocalDirections(new Vec3(0,0,-1),new Vec3(0,0,1),"retry-geometry");
                var map=new Dictionary<ViveTrackpadDirection,Colour>{{ViveTrackpadDirection.Up,Colour.Red},{ViveTrackpadDirection.Right,Colour.Green},
                    {ViveTrackpadDirection.Down,Colour.Blue},{ViveTrackpadDirection.Left,Colour.Yellow}};
                Task=new ViveDevelopmentParticipant(config,Clock,geometry,map,.5,true,new ExperimentIncrementalOptions(4096,128,16L*1024*1024),4096);
                Choices=new VivePracticeConsentAuthority(Task,Clock,true);Check.True(Choices.ConfirmEye()&&Choices.ConfirmTask());
                Generation=Choices.Generation;
                var binding=new VivePracticeStartBinding(Task,Clock,Choices,true);
                Task.UpdateTrackpad(true,Neutral());Clock.Value=11;Task.OwnerBeginPractice(Actor.Owner);
                VivePracticeStartBinding.Ticket ticket;Check.True(binding.TryAcquire(1,4096,out ticket));
                First=new VivePracticeOwnedMemoryStart(Task,Clock,Choices,binding,Context(1),Store,"first",262144,512);
                Check.True(First.TryStart(ticket));VivePracticeOwnedStartReceipt receipt;Check.True(First.TryTakeReceipt(out receipt));
                FirstCursor=new VivePracticeTaskEventCursor(First,receipt,4096,64);
            }
            internal VivePracticeContextV3 Context(int attempt)
            {
                var events=Task.ReadEventsAfter(-1,4096);double start=-1;
                foreach(var item in events)
                    if(item.Kind=="practice_started"&&item.Fields["attempt"]==attempt.ToString())start=item.Time;
                Check.True(start>=0,"actual practice_started is required");
                if(attempt==2){Check.True(SecondTicket!=null);Check.Equal(start,SecondTicket.Batch.Events[0].Time);start=SecondTicket.Batch.Events[0].Time;}
                return new VivePracticeContextV3("retry-session","participant","stream","device",Clock.ClockId,"mapping",attempt,
                    attempt==1?"epoch-1":"epoch-2",0,attempt==1?null:"epoch-1",
                    attempt==1?VivePracticeEpochReasonV3.Initial:VivePracticeEpochReasonV3.PracticeRetry,start);
            }
            internal void Drain(VivePracticeOwnedMemoryStart host,VivePracticeTaskEventCursor cursor,params string[] expected)
            {
                bool observed=false;
                for(int i=0;i<32;i++)
                {
                    VivePracticeTaskEventTransaction tx;var result=cursor.TryStage(out tx);
                    if(result==VivePracticeTaskCursorResult.NoEvents)
                    {Check.True(expected.Length==0||observed);return;}
                    Check.Equal(VivePracticeTaskCursorResult.Staged,result,"phase "+Task.Phase);
                    Check.True(tx!=null);
                    if(expected.Length>0)
                    {
                        Check.True(!observed);Check.Equal(expected.Length,tx.Events.Count);
                        for(int j=0;j<expected.Length;j++)Check.Equal(expected[j],tx.Events[j].Kind);
                        observed=true;
                    }
                    Check.True(host.TryAppendPending(cursor,tx));
                }
                throw new InvalidOperationException("drain did not terminate");
            }
            internal void Answer(VivePracticeOwnedMemoryStart host,VivePracticeTaskEventCursor cursor,bool correct,params string[] terminal)
            {
                Task.UpdateTrackpad(true,Neutral());Drain(host,cursor);Clock.Value+=1;
                Task.MarkPresented(Task.CurrentTrial.TrialId);Drain(host,cursor);
                var ink=Task.CurrentTrial.Stimulus.Ink;Task.UpdateTrackpad(true,Press(correct?ink:Other(ink)));
                if(correct)Drain(host,cursor,terminal);else Drain(host,cursor);
                if(!correct)
                {
                    Check.Equal(Phase.PracticeCorrection,Task.Phase);
                    Task.UpdateAuxiliary(false,false,false,0,true);Drain(host,cursor);
                    Task.UpdateAuxiliary(true,false,false,0,true);Drain(host,cursor,terminal);
                }
            }
            internal void CompleteFirst()
            {
                for(int i=0;i<7;i++)Answer(First,FirstCursor,i>=3);
                Answer(First,FirstCursor,true,"first_response","practice_completed","phase_changed");
                Check.Equal(Phase.PracticeRetry,Task.Phase);Check.True(FirstCursor.BoundaryCommitted);
            }
            internal void IssueRetry()
            {Check.True(FirstCursor.TryIssueRetryContinuation(out Retry));Check.True(Retry!=null);}
            internal void ObserveRetryNeutral()
            {Task.UpdateTrackpad(true,Neutral());Task.UpdateAuxiliary(false,false,false,0,true);}
            internal void BeginSecond(Func<int,ViveFakeWritePlan> plan=null)
            {ObserveRetryNeutral();Check.True(TryBeginSecond(plan));}
            internal bool TryBeginSecond(Func<int,ViveFakeWritePlan> plan=null)
            {
                try
                {
                    SecondBinding=new VivePracticeStartBinding(Task,Clock,Choices,Retry,true);
                    Clock.Value+=1;
                    if(!SecondBinding.TryBeginRetryAndAcquire(Actor.Owner,4096,out SecondTicket))return false;
                    var declared=new VivePracticeContextV3("retry-session","participant","stream","device",Clock.ClockId,"mapping",2,
                        "epoch-2",0,"epoch-1",VivePracticeEpochReasonV3.PracticeRetry,null);
                    if(!FirstCursor.TryClaimRetryContinuation(Retry,SecondTicket,declared,out SecondReservation))return false;
                    Second=new VivePracticeOwnedMemoryStart(Task,Clock,Choices,SecondBinding,Context(2),Store,"second",262144,512,SecondReservation,plan);
                    return true;
                }
                catch(InvalidOperationException){return false;}
                catch(ArgumentException){return false;}
            }
            internal void StartSecond()
            {
                Check.True(Second.TryStart(SecondTicket));VivePracticeOwnedStartReceipt receipt;
                Check.True(Second.TryTakeReceipt(out receipt));SecondCursor=new VivePracticeTaskEventCursor(Second,receipt,4096,64);
            }
            internal VivePracticeMultiReadResult Read(string target)
            {return VivePracticeMultiReader.Read(Store.CopyForTest(target),true,262144,512);}
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
        static void Bytes(byte[] a,byte[] b)
        {Check.True(a!=null&&b!=null);Check.Equal(a.Length,b.Length);for(int i=0;i<a.Length;i++)Check.Equal(a[i],b[i]);}
        static void Tail(IReadOnlyList<JournalEvent> events,params string[] kinds)
        {Check.True(events.Count>=kinds.Length);for(int i=0;i<kinds.Length;i++)Check.Equal(kinds[i],events[events.Count-kinds.Length+i].Kind);}
        static void Same(JournalEvent a,JournalEvent b)
        {
            Check.Equal(a.Sequence,b.Sequence);Check.Equal(a.Time,b.Time);Check.Equal(a.ClockId,b.ClockId);Check.Equal(a.SessionId,b.SessionId);
            Check.Equal(a.BlockIndex,b.BlockIndex);Check.Equal(a.Segment,b.Segment);Check.Equal(a.TrialId,b.TrialId);Check.Equal(a.Kind,b.Kind);
            Check.Equal(a.Fields.Count,b.Fields.Count);foreach(var field in a.Fields){Check.True(b.Fields.ContainsKey(field.Key));Check.Equal(field.Value,b.Fields[field.Key]);}
        }
        static void AssertSecond(Fixture f)
        {
            var read=f.Read("second");Check.Equal(VivePracticeMultiStatus.Unclean,read.Status);Check.True(!read.DurableStorageProven);
            Check.Equal(2,read.Context.PracticeAttempt);Check.Equal("epoch-2",read.Context.RecordingEpochId);
            Check.Equal("epoch-1",read.Context.PreviousEpochId);Check.Equal(VivePracticeEpochReasonV3.PracticeRetry,read.Context.EpochReason);
            var all=f.Task.ReadEventsAfter(-1,4096);int start=-1;
            for(int i=0;i<all.Count;i++)if(all[i].Kind=="practice_started"&&all[i].Fields["attempt"]=="2"){start=i;break;}
            Check.True(start>=0);Check.Equal(all.Count-start,read.Events.Count);
            for(int i=0;i<read.Events.Count;i++)Same(all[start+i],read.Events[i]);
        }
        public static void Register(TestSuite suite)
        {
            suite.Run("retry capability links same consent and preserves first target after second passes",()=>{
                var f=new Fixture();f.CompleteFirst();var first=f.Store.CopyForTest("first");f.IssueRetry();
                f.BeginSecond();Check.Equal(f.Generation,f.Choices.Generation);f.StartSecond();
                VivePracticeTaskEventTransaction old;Check.Equal(VivePracticeTaskCursorResult.NoEvents,f.FirstCursor.TryStage(out old));Check.True(old==null);
                for(int i=0;i<7;i++)f.Answer(f.Second,f.SecondCursor,true);
                f.Answer(f.Second,f.SecondCursor,true,"first_response","practice_completed","phase_changed");
                Check.Equal(Phase.BlockPreparation,f.Task.Phase);Check.True(f.SecondCursor.BoundaryCommitted);
                AssertSecond(f);Tail(f.Read("second").Events,"first_response","practice_completed","phase_changed");
                Check.Equal(f.Generation,f.Choices.Generation);Bytes(first,f.Store.CopyForTest("first"));
            });
            suite.Run("retry capability records second failure as ended rather than third attempt",()=>{
                var f=new Fixture();f.CompleteFirst();var first=f.Store.CopyForTest("first");f.IssueRetry();f.BeginSecond();f.StartSecond();
                for(int i=0;i<7;i++)f.Answer(f.Second,f.SecondCursor,i>=3);
                f.Answer(f.Second,f.SecondCursor,true,"first_response","practice_completed","session_ended","phase_changed");
                Check.Equal(Phase.Ended,f.Task.Phase);Check.True(f.SecondCursor.BoundaryCommitted);AssertSecond(f);
                var events=f.Read("second").Events;
                Tail(events,"first_response","practice_completed","session_ended","phase_changed");
                var end=events[events.Count-2];Check.Equal("PracticeFailed",end.Fields["reason"]);
                Check.Equal("two_practice_attempts_below_6_of_8",end.Fields["detail"]);
                bool rejected=false;try{f.Task.OwnerBeginPractice(Actor.Owner);}catch(InvalidOperationException){rejected=true;}
                Check.True(rejected);Check.Equal(Phase.Ended,f.Task.Phase);
                Bytes(first,f.Store.CopyForTest("first"));
            });
            suite.Run("retry cannot issue before committed first boundary or twice",()=>{
                var f=new Fixture();VivePracticeRetryContinuation denied;
                Check.True(!f.FirstCursor.TryIssueRetryContinuation(out denied));Check.True(denied==null);
                var valid=new Fixture();valid.CompleteFirst();valid.IssueRetry();
                Check.True(!valid.FirstCursor.TryIssueRetryContinuation(out denied));Check.True(denied==null);
            });
            suite.Run("retry withdrawal after capability prevents second target",()=>{
                var f=new Fixture();f.CompleteFirst();var first=f.Store.CopyForTest("first");f.IssueRetry();f.Choices.Withdraw();
                Check.True(!f.TryBeginSecond());Check.True(f.Store.CopyForTest("second")==null);Bytes(first,f.Store.CopyForTest("first"));
            });
            suite.Run("retry withdrawal after start ticket prevents fake append",()=>{
                var f=new Fixture();f.CompleteFirst();var first=f.Store.CopyForTest("first");f.IssueRetry();f.BeginSecond();
                f.Task.ParticipantWithdraw();Check.True(!f.Second.TryStart(f.SecondTicket));
                Check.True(f.Store.CopyForTest("second")==null);Bytes(first,f.Store.CopyForTest("first"));
            });
            suite.Run("retry pause and resume ABA cannot reuse pre-pause capability",()=>{
                var f=new Fixture();f.CompleteFirst();var first=f.Store.CopyForTest("first");f.IssueRetry();
                f.Task.ParticipantPause();Check.Equal(Phase.Paused,f.Task.Phase);
                f.Task.UpdateTrackpad(true,Neutral());f.Task.UpdateAuxiliary(false,false,false,0,true);
                f.Task.UpdateAuxiliary(true,false,false,0,true);Check.Equal(Phase.PracticeRetry,f.Task.Phase);
                bool prepared=f.TryBeginSecond();Check.True(!prepared||!f.Second.TryStart(f.SecondTicket));
                Check.True(f.Store.CopyForTest("second")==null);Bytes(first,f.Store.CopyForTest("first"));
            });
            suite.Run("retry participant withdrawal cannot append after second start",()=>{
                var f=new Fixture();f.CompleteFirst();var first=f.Store.CopyForTest("first");f.IssueRetry();f.BeginSecond();f.StartSecond();
                f.Task.UpdateTrackpad(true,Neutral());f.Drain(f.Second,f.SecondCursor);f.Clock.Value+=1;
                f.Task.MarkPresented(f.Task.CurrentTrial.TrialId);f.Drain(f.Second,f.SecondCursor);
                var second=f.Store.CopyForTest("second");f.Task.ParticipantWithdraw();Check.Equal(Phase.Ended,f.Task.Phase);
                VivePracticeTaskEventTransaction tx;Check.True(f.SecondCursor.TryStage(out tx)!=VivePracticeTaskCursorResult.Staged);
                Bytes(first,f.Store.CopyForTest("first"));Bytes(second,f.Store.CopyForTest("second"));
            });
            suite.Run("retry capability replay cannot write another target",()=>{
                var f=new Fixture();f.CompleteFirst();var first=f.Store.CopyForTest("first");f.IssueRetry();f.BeginSecond();f.StartSecond();
                bool rejected=false;
                try{
                    var duplicate=new VivePracticeOwnedMemoryStart(f.Task,f.Clock,f.Choices,f.SecondBinding,f.Context(2),f.Store,"duplicate",262144,512,f.SecondReservation,null);
                    rejected=!duplicate.TryStart(f.SecondTicket);
                }catch(ArgumentException){rejected=true;}catch(InvalidOperationException){rejected=true;}
                Check.True(rejected);Check.True(f.Store.CopyForTest("duplicate")==null);Bytes(first,f.Store.CopyForTest("first"));
            });
            suite.Run("retry mismatched previous epoch cannot start target",()=>{
                var f=new Fixture();f.CompleteFirst();var first=f.Store.CopyForTest("first");f.IssueRetry();
                f.ObserveRetryNeutral();
                f.SecondBinding=new VivePracticeStartBinding(f.Task,f.Clock,f.Choices,f.Retry,true);
                f.Clock.Value+=1;Check.True(f.SecondBinding.TryBeginRetryAndAcquire(Actor.Owner,4096,out f.SecondTicket));
                var wrong=new VivePracticeContextV3("retry-session","participant","stream","device",f.Clock.ClockId,"mapping",2,
                    "epoch-2",0,"wrong-epoch",VivePracticeEpochReasonV3.PracticeRetry,null);
                VivePracticeConsentAuthority.StartReservation reservation;
                Check.True(!f.FirstCursor.TryClaimRetryContinuation(f.Retry,f.SecondTicket,wrong,out reservation));
                Check.True(reservation==null);Check.True(f.Store.CopyForTest("second")==null);Bytes(first,f.Store.CopyForTest("first"));
            });
            suite.Run("retry withdrawal in fake start marks second outcome uncertain and cannot retry",()=>{
                var f=new Fixture();f.CompleteFirst();var first=f.Store.CopyForTest("first");f.IssueRetry();
                f.BeginSecond(length=>new ViveFakeWritePlan(length,false,f.Choices.Withdraw));
                Check.True(!f.Second.TryStart(f.SecondTicket));
                Check.Equal((ViveFakeTargetState?)ViveFakeTargetState.Uncertain,f.Store.StateForTest("second"));
                var second=f.Store.CopyForTest("second");Check.True(second!=null&&second.Length>0);
                Check.True(!f.Second.TryStart(f.SecondTicket));Bytes(second,f.Store.CopyForTest("second"));Bytes(first,f.Store.CopyForTest("first"));
            });
            suite.Run("retry partial fake start is uncertain and cannot retry",()=>{
                var f=new Fixture();f.CompleteFirst();var first=f.Store.CopyForTest("first");f.IssueRetry();
                f.BeginSecond(length=>new ViveFakeWritePlan(20,true,null));Check.True(!f.Second.TryStart(f.SecondTicket));
                Check.Equal((ViveFakeTargetState?)ViveFakeTargetState.Uncertain,f.Store.StateForTest("second"));
                var second=f.Store.CopyForTest("second");Check.True(second!=null);Check.Equal(20,second.Length);
                Check.True(!f.Second.TryStart(f.SecondTicket));Bytes(second,f.Store.CopyForTest("second"));Bytes(first,f.Store.CopyForTest("first"));
            });
            suite.Run("retry fake start exception preserves uncertain prefix without retry",()=>{
                var f=new Fixture();f.CompleteFirst();var first=f.Store.CopyForTest("first");f.IssueRetry();
                f.BeginSecond(length=>new ViveFakeWritePlan(20,false,()=>{throw new InvalidOperationException();}));
                Check.True(!f.Second.TryStart(f.SecondTicket));
                Check.Equal((ViveFakeTargetState?)ViveFakeTargetState.Uncertain,f.Store.StateForTest("second"));
                var second=f.Store.CopyForTest("second");Check.True(second!=null);Check.Equal(20,second.Length);
                Check.True(!f.Second.TryStart(f.SecondTicket));Bytes(second,f.Store.CopyForTest("second"));Bytes(first,f.Store.CopyForTest("first"));
            });
            suite.Run("retry accepts release and empty input frames before continuation is issued",()=>{
                var f=new Fixture();f.CompleteFirst();var first=f.Store.CopyForTest("first");
                f.Task.UpdateTrackpad(true,Neutral());f.Task.UpdateTrackpad(true,Neutral());
                f.Task.UpdateAuxiliary(false,false,false,0,true);f.Task.Tick();f.IssueRetry();f.BeginSecond();f.StartSecond();
                Check.Equal(f.Generation,f.Choices.Generation);Check.Equal(3,f.Read("second").Events.Count);
                Bytes(first,f.Store.CopyForTest("first"));
            });
            suite.Run("retry accepts release and empty input frames after continuation is issued",()=>{
                var f=new Fixture();f.CompleteFirst();var first=f.Store.CopyForTest("first");f.IssueRetry();
                f.Task.UpdateTrackpad(true,Neutral());f.Task.UpdateTrackpad(true,Neutral());
                f.Task.UpdateAuxiliary(false,false,false,0,true);f.Task.Tick();f.BeginSecond();f.StartSecond();
                Check.Equal(f.Generation,f.Choices.Generation);Check.Equal(3,f.Read("second").Events.Count);
                Bytes(first,f.Store.CopyForTest("first"));
            });
            suite.Run("retry rejects non-release trackpad input before second practice",()=>{
                var f=new Fixture();f.CompleteFirst();var first=f.Store.CopyForTest("first");f.IssueRetry();
                f.Task.UpdateTrackpad(true,Press(Colour.Red));
                bool prepared=f.TryBeginSecond();Check.True(!prepared||!f.Second.TryStart(f.SecondTicket),"non-release accepted and committed");
                Check.True(f.Store.CopyForTest("second")==null,"non-release created target");Bytes(first,f.Store.CopyForTest("first"));
            });
            suite.Run("retry rejects phase and journal changes before second practice",()=>{
                var f=new Fixture();f.CompleteFirst();var first=f.Store.CopyForTest("first");f.IssueRetry();
                f.Task.EndTechnical(Actor.Owner,"synthetic interruption");Check.Equal(Phase.Ended,f.Task.Phase);
                bool prepared=f.TryBeginSecond();Check.True(!prepared||!f.Second.TryStart(f.SecondTicket));
                Check.True(f.Store.CopyForTest("second")==null);Bytes(first,f.Store.CopyForTest("first"));
            });
            suite.Run("retry rejects device loss even after neutral reconnection",()=>{
                var f=new Fixture();f.CompleteFirst();var first=f.Store.CopyForTest("first");f.IssueRetry();
                f.ObserveRetryNeutral();f.Task.UpdateTrackpad(false,Neutral());f.ObserveRetryNeutral();
                bool prepared=f.TryBeginSecond();Check.True(!prepared||!f.Second.TryStart(f.SecondTicket),"device loss accepted and committed");
                Check.True(f.Store.CopyForTest("second")==null,"device loss created target");Bytes(first,f.Store.CopyForTest("first"));
            });
            suite.Run("retry rejects unrelated failed task action without trusting unchanged phase",()=>{
                var f=new Fixture();f.CompleteFirst();var first=f.Store.CopyForTest("first");f.IssueRetry();
                bool rejected=false;try{f.Task.OwnerBeginBlock(Actor.Owner);}catch(InvalidOperationException){rejected=true;}
                Check.True(rejected);Check.Equal(Phase.PracticeRetry,f.Task.Phase);
                bool prepared=f.TryBeginSecond();Check.True(!prepared||!f.Second.TryStart(f.SecondTicket));
                Check.True(f.Store.CopyForTest("second")==null);Bytes(first,f.Store.CopyForTest("first"));
            });
            suite.Run("retry rejects begin practice outside reserved retry entry",()=>{
                var f=new Fixture();f.CompleteFirst();var first=f.Store.CopyForTest("first");f.IssueRetry();
                f.ObserveRetryNeutral();
                var binding=new VivePracticeStartBinding(f.Task,f.Clock,f.Choices,f.Retry,true);
                f.Clock.Value+=1;f.Task.OwnerBeginPractice(Actor.Owner);
                VivePracticeStartBinding.Ticket ticket;bool acquired=binding.TryAcquire(2,4096,out ticket);
                if(acquired)
                {
                    var declared=new VivePracticeContextV3("retry-session","participant","stream","device",f.Clock.ClockId,"mapping",2,
                        "epoch-2",0,"epoch-1",VivePracticeEpochReasonV3.PracticeRetry,null);
                    VivePracticeConsentAuthority.StartReservation reservation;
                    Check.True(!f.FirstCursor.TryClaimRetryContinuation(f.Retry,ticket,declared,out reservation));
                    Check.True(reservation==null);
                }
                Check.True(f.Store.CopyForTest("second")==null);Bytes(first,f.Store.CopyForTest("first"));
            });
            suite.Run("retry rejects when trackpad neutral is unobserved",()=>{
                var f=new Fixture();f.CompleteFirst();var first=f.Store.CopyForTest("first");f.IssueRetry();
                f.Task.UpdateAuxiliary(false,false,false,0,true);
                bool prepared=f.TryBeginSecond();Check.True(!prepared||!f.Second.TryStart(f.SecondTicket));
                Check.True(f.Store.CopyForTest("second")==null);Bytes(first,f.Store.CopyForTest("first"));
            });
            suite.Run("retry rejects when auxiliary neutral is unobserved",()=>{
                var f=new Fixture();f.CompleteFirst();var first=f.Store.CopyForTest("first");f.IssueRetry();
                f.Task.UpdateTrackpad(true,Neutral());
                bool prepared=f.TryBeginSecond();Check.True(!prepared||!f.Second.TryStart(f.SecondTicket));
                Check.True(f.Store.CopyForTest("second")==null);Bytes(first,f.Store.CopyForTest("first"));
            });
            suite.Run("retry rejects a continuously held trackpad",()=>{
                var f=new Fixture();f.CompleteFirst();var first=f.Store.CopyForTest("first");f.IssueRetry();
                f.Task.UpdateTrackpad(true,Press(Colour.Red));f.Task.UpdateAuxiliary(false,false,false,0,true);
                bool prepared=f.TryBeginSecond();Check.True(!prepared||!f.Second.TryStart(f.SecondTicket));
                Check.True(f.Store.CopyForTest("second")==null);Bytes(first,f.Store.CopyForTest("first"));
            });
            suite.Run("retry rejects a new press after both controls were neutral",()=>{
                var f=new Fixture();f.CompleteFirst();var first=f.Store.CopyForTest("first");f.IssueRetry();
                f.ObserveRetryNeutral();f.Task.UpdateTrackpad(true,Press(Colour.Red));f.ObserveRetryNeutral();
                bool prepared=f.TryBeginSecond();Check.True(!prepared||!f.Second.TryStart(f.SecondTicket));
                Check.True(f.Store.CopyForTest("second")==null);Bytes(first,f.Store.CopyForTest("first"));
            });
            suite.Run("retry neutral pending clears auxiliary observation until resampled",()=>{
                var f=new Fixture();f.CompleteFirst();var first=f.Store.CopyForTest("first");f.IssueRetry();
                f.ObserveRetryNeutral();f.Task.UpdateNeutralPending(true);f.Task.UpdateTrackpad(true,Neutral());
                bool prepared=f.TryBeginSecond();Check.True(!prepared||!f.Second.TryStart(f.SecondTicket));
                Check.True(f.Store.CopyForTest("second")==null);Bytes(first,f.Store.CopyForTest("first"));
            });
        }
    }
}
