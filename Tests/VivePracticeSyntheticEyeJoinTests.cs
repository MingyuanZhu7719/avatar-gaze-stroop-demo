using System;
using System.Collections.Generic;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    // Real Simulation participant, sealed synthetic frames and fake memory
    // target only. No headset, user gaze, file, network or v3 writer is used.
    public static class VivePracticeSyntheticEyeJoinTests
    {
        sealed class UntrustedClock:IClock
        {public string ClockId {get{return "synthetic-join-clock";}}public double Now {get{return 0;}}}
        sealed class Fixture:IDisposable
        {
            internal readonly MonotonicClock Clock=new MonotonicClock("synthetic-join-clock");internal readonly ViveDevelopmentParticipant Task;
            internal readonly VivePracticeConsentAuthority Choices;internal readonly VivePracticeContextV3 Context;
            internal readonly VivePracticeEyeFencedSource Source;internal readonly VivePracticeSyntheticFrameFeed Feed;
            internal readonly VivePracticeOwnedMemoryStart Host;internal readonly VivePracticeTaskEventCursor Cursor;
            internal readonly VivePracticeSyntheticEyeJoin Join;internal readonly ViveExclusiveMemoryStore Store=new ViveExclusiveMemoryStore(8);
            int sequence;
            internal Fixture()
            {
                var allocation=new Allocation("join-allocation",new[]{Condition.B,Condition.C,Condition.A,Condition.D},GazeTarget.Participant,new[]{0,1,2,3});
                var config=new SessionConfiguration("join-session",42,allocation,RunKind.Simulation,"en",3,30,true,0,true,true,true);
                var geometry=DevelopmentGeometry.FromLocalDirections(new Vec3(0,0,-1),new Vec3(0,0,1),"join-geometry");
                var map=new Dictionary<ViveTrackpadDirection,Colour>{{ViveTrackpadDirection.Up,Colour.Red},{ViveTrackpadDirection.Right,Colour.Green},
                    {ViveTrackpadDirection.Down,Colour.Blue},{ViveTrackpadDirection.Left,Colour.Yellow}};
                Task=new ViveDevelopmentParticipant(config,Clock,geometry,map,.5,true,new ExperimentIncrementalOptions(4096,128,16L*1024*1024),4096);
                Task.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,false,0,0));
                Choices=new VivePracticeConsentAuthority(Task,Clock,true);Check.True(Choices.ConfirmEye()&&Choices.ConfirmTask());
                VivePracticeSyntheticFrameFeed feed;var mapper=new ViveProEyeRehearsalCoordinates();
                Source=VivePracticeEyeFencedSource.CreateSealedSyntheticTicketSource(Task,Clock,Choices,mapper,.2,out feed);Feed=feed;
                var binding=new VivePracticeStartBinding(Task,Clock,Choices,true);
                Check.True(System.Threading.SpinWait.SpinUntil(()=>Clock.Now>Choices.EnabledAt,1000));Task.OwnerBeginPractice(Actor.Owner);
                VivePracticeStartBinding.Ticket start;Check.True(binding.TryAcquire(1,4096,out start));
                Context=new VivePracticeContextV3(Task.SessionId,"synthetic","synthetic-stream","synthetic-device",Clock.ClockId,mapper.Version,1,
                    "synthetic-epoch",0,null,VivePracticeEpochReasonV3.Initial,start.Batch.Events[0].Time);
                Host=new VivePracticeOwnedMemoryStart(Task,Clock,Choices,binding,Context,Store,"target",65536,32);
                Check.True(Host.TryStart(start));VivePracticeOwnedStartReceipt receipt;Check.True(Host.TryTakeReceipt(out receipt));
                Cursor=new VivePracticeTaskEventCursor(Host,receipt,4096,64);Check.True(Source.BindPractice(Context));
                Join=new VivePracticeSyntheticEyeJoin(Host,Cursor,Task,Clock,Choices,Context,Source,32);
            }
            internal VivePracticeSyntheticTicket Issue(byte marker)
            {
                sequence++;System.Threading.Thread.Sleep(2);double observed=Clock.Now;
                Feed.SetFrame(sequence,sequence,observed,sequence,observed,marker);
                VivePracticeSyntheticTicket ticket;Check.True(Source.TryIssueSyntheticTicket(out ticket));return ticket;
            }
            internal void Drain()
            {
                for(int i=0;i<128;i++)
                {
                    VivePracticeTaskEventTransaction transaction;var state=Cursor.TryStage(out transaction);
                    if(state==VivePracticeTaskCursorResult.NoEvents)return;
                    Check.Equal(VivePracticeTaskCursorResult.Staged,state);Check.True(Host.TryAppendPending(Cursor,transaction));
                }
                throw new Exception("Synthetic drain exceeded its bound");
            }
            public void Dispose(){Source.Dispose();}
        }
        public static void Register(TestSuite suite)
        {
            suite.Run("synthetic eye join uses actual participant and committed event cursor",()=>{
                using(var f=new Fixture())
                {
                    f.Drain();var first=f.Issue(41);Check.True(f.Join.TryJoin(first));Check.Equal(1,f.Join.Count);
                    f.Task.MarkPresented(f.Task.CurrentTrial.TrialId);f.Drain();var second=f.Issue(42);Check.True(f.Join.TryJoin(second));
                    var markers=f.Join.CopyMarkersForTest();Check.Equal(2,markers.Length);Check.Equal((byte)41,markers[0]);Check.Equal((byte)42,markers[1]);
                    var checks=f.Join.CopyCommitTimesForTest();Check.Equal(2,checks.Length);
                    Check.True(checks[0]>=first.Observed&&checks[0]-first.Observed<=first.MaximumAgeSeconds);
                    Check.True(checks[1]>=second.Observed&&checks[1]-second.Observed<=second.MaximumAgeSeconds);
                    var watermarks=f.Join.CopyTaskWatermarksForTest();Check.Equal(2,watermarks.Length);Check.True(watermarks[1]>watermarks[0]);
                    Check.True(!f.Join.CanWrite&&!f.Join.ResearchGateOpen&&!f.Host.EyeDataIncluded);
                    Check.True(!f.Join.TryJoin(second)&&f.Join.Faulted);Check.Equal(2,f.Join.Count);
                }
            });
            suite.Run("synthetic eye join rejects uncommitted task event",()=>{
                using(var f=new Fixture())
                {f.Task.MarkPresented(f.Task.CurrentTrial.TrialId);var ticket=f.Issue(3);Check.True(!f.Join.TryJoin(ticket)&&f.Join.Faulted);Check.Equal(0,f.Join.Count);}
            });
            suite.Run("synthetic eye join rejects intervening task mutation and choice withdrawal",()=>{
                foreach(int mode in new[]{0,1,2,3,4,5,6,7,8})using(var f=new Fixture())
                {
                    var ticket=f.Issue(4);f.Join.BeforeCommitForTest=()=>{
                        if(mode==0)f.Task.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,false,0,0));
                        else if(mode==1)f.Choices.Withdraw();else if(mode==2)f.Task.ParticipantWithdraw();
                        else if(mode==3)f.Source.Dispose();else if(mode==4)System.Threading.Thread.Sleep(250);
                        else if(mode==5)Check.True(!f.Join.TryJoin(ticket));
                        else if(mode==6){var thread=new System.Threading.Thread(()=>f.Join.TryJoin(ticket));thread.Start();Check.True(thread.Join(5000));}
                        else if(mode==7){System.Threading.Thread.Sleep(2);double now=f.Clock.Now;f.Feed.SetFrame(2,2,now,2,now,10);VivePracticeEyeCandidate ignored;f.Source.TryRead(out ignored);}
                        else {var field=typeof(VivePracticeOwnedMemoryStart).GetField("target",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic);
                            var target=(ViveExclusiveMemoryTarget)field.GetValue(f.Host);var thread=new System.Threading.Thread(target.Withdraw);thread.Start();Check.True(thread.Join(5000));}};
                    Check.True(!f.Join.TryJoin(ticket)&&f.Join.Faulted);Check.Equal(0,f.Join.Count);
                    Check.True(!f.Join.TryJoin(ticket));
                }
            });
            suite.Run("synthetic eye join rejects foreign source and leaves prior evidence intact",()=>{
                using(var a=new Fixture())using(var b=new Fixture())
                {var first=a.Issue(5);Check.True(a.Join.TryJoin(first));var foreign=b.Issue(6);Check.True(!a.Join.TryJoin(foreign));
                    Check.Equal(1,a.Join.Count);Check.Equal((byte)5,a.Join.CopyMarkersForTest()[0]);}
            });
            suite.Run("synthetic eye join cannot pass pause as continuous evidence",()=>{
                using(var f=new Fixture())
                {var ticket=f.Issue(7);f.Task.ParticipantPause();Check.True(!f.Join.TryJoin(ticket));Check.Equal(0,f.Join.Count);Check.Equal(Phase.Paused,f.Task.Phase);}
            });
            suite.Run("synthetic eye join refuses an uncertain fake task append",()=>{
                using(var f=new Fixture())
                {
                    var ticket=f.Issue(8);f.Task.MarkPresented(f.Task.CurrentTrial.TrialId);
                    VivePracticeTaskEventTransaction transaction;Check.Equal(VivePracticeTaskCursorResult.Staged,f.Cursor.TryStage(out transaction));
                    Check.True(!f.Host.TryAppendPending(f.Cursor,transaction,length=>new ViveFakeWritePlan(20,true,null)));
                    Check.True(!f.Join.TryJoin(ticket));Check.Equal(0,f.Join.Count);
                    Check.Equal((ViveFakeTargetState?)ViveFakeTargetState.Uncertain,f.Store.StateForTest("target"));
                }
            });
            suite.Run("synthetic eye join rejects a first-attempt ticket after practice retry",()=>{
                using(var f=new Fixture())
                {
                    var ticket=f.Issue(9);
                    for(int i=0;i<8;i++)
                    {
                        f.Task.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,false,0,0));
                        f.Task.MarkPresented(f.Task.CurrentTrial.TrialId);var ink=f.Task.CurrentTrial.Stimulus.Ink;
                        var press=ink==Colour.Red?new VivePreviewTrackpadSample(true,true,1,0):new VivePreviewTrackpadSample(true,true,0,1);
                        f.Task.UpdateTrackpad(true,press);Check.Equal(Phase.PracticeCorrection,f.Task.Phase);
                        f.Task.UpdateAuxiliary(false,false,false,0,true);f.Task.UpdateAuxiliary(true,false,false,0,true);
                    }
                    Check.Equal(Phase.PracticeRetry,f.Task.Phase);Check.True(!f.Join.TryJoin(ticket));Check.Equal(0,f.Join.Count);
                }
            });
            suite.Run("synthetic eye join rejects a user supplied clock",()=>{
                using(var f=new Fixture())
                {
                    Check.Throws<ArgumentException>(()=>new VivePracticeSyntheticEyeJoin(f.Host,f.Cursor,f.Task,new UntrustedClock(),f.Choices,f.Context,f.Source,32));
                    Check.Throws<ArgumentException>(()=>new VivePracticeSyntheticEyeJoin(f.Host,f.Cursor,f.Task,new MonotonicClock(f.Clock.ClockId),f.Choices,f.Context,f.Source,32));
                }
            });
            suite.Run("synthetic eye join checks age after waiting for its final source lock",()=>{
                using(var f=new Fixture())
                {
                    var ticket=f.Issue(10);var acquired=new System.Threading.ManualResetEvent(false);
                    System.Threading.Thread blocker=null;
                    f.Join.BeforeLockedCommitForTest=()=>{
                        blocker=new System.Threading.Thread(()=>{lock(f.Source.SyntheticTicketGate){acquired.Set();System.Threading.Thread.Sleep(300);}});
                        blocker.Start();Check.True(acquired.WaitOne(5000));
                    };
                    Check.True(!f.Join.TryJoin(ticket)&&f.Join.Faulted);Check.Equal(0,f.Join.Count);
                    Check.True(blocker!=null&&blocker.Join(5000));
                }
            });
            suite.Run("synthetic eye join rejects rollback between precheck and final read",()=>{
                using(var f=new Fixture())
                {
                    var ticket=f.Issue(11);double prior=-1,simulatedPrecheck=-1;
                    var field=typeof(VivePracticeSyntheticEyeJoin).GetField("pendingPrecheck",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic);
                    Check.True(field!=null);
                    f.Join.BeforeLockedCommitForTest=()=>{
                        prior=(double)field.GetValue(f.Join);Check.True(prior>=ticket.Observed);
                        // Simulate an earlier read ahead of the later sealed
                        // clock without injecting callbacks into commit locks.
                        simulatedPrecheck=f.Clock.Now+10;field.SetValue(f.Join,simulatedPrecheck);
                    };
                    Check.True(!f.Join.TryJoin(ticket)&&f.Join.Faulted);Check.Equal(0,f.Join.Count);
                    Check.True(prior>=ticket.Observed&&simulatedPrecheck>f.Clock.Now);
                    Check.True(!f.Join.CanWrite&&!f.Join.ResearchGateOpen);
                }
            });
        }
    }
}
