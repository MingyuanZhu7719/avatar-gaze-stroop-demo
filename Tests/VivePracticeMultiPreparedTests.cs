using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    // Real in-memory Simulation participant and sealed fake target. Reflection
    // only inspects the host's writer; it is never used to mint authority.
    public static class VivePracticeMultiPreparedTests
    {
        sealed class Clock:IClock
        {internal double Value=10;public string ClockId{get{return "prepared-clock";}}public double Now{get{return Value;}}}
        sealed class Fixture
        {
            internal readonly Clock Clock=new Clock();internal readonly ViveDevelopmentParticipant Task;
            internal readonly VivePracticeOwnedMemoryStart Host;internal readonly VivePracticeTaskEventCursor Cursor;
            internal readonly ViveExclusiveMemoryStore Store=new ViveExclusiveMemoryStore(8);internal readonly VivePracticeContextV3 Context;
            internal readonly VivePracticeMultiWriter Writer;
            internal Fixture()
            {
                var config=new SessionConfiguration("prepared-session",42,
                    new Allocation("prepared-allocation",new[]{Condition.B,Condition.C,Condition.A,Condition.D},GazeTarget.Participant,new[]{0,1,2,3}),
                    RunKind.Simulation,"en",3,30,true,0,true,true,true);
                var geometry=DevelopmentGeometry.FromLocalDirections(new Vec3(0,0,-1),new Vec3(0,0,1),"prepared-geometry");
                var map=new Dictionary<ViveTrackpadDirection,Colour>{{ViveTrackpadDirection.Up,Colour.Red},{ViveTrackpadDirection.Right,Colour.Green},
                    {ViveTrackpadDirection.Down,Colour.Blue},{ViveTrackpadDirection.Left,Colour.Yellow}};
                Task=new ViveDevelopmentParticipant(config,Clock,geometry,map,.5,true,new ExperimentIncrementalOptions(4096,128,16L*1024*1024),4096);
                var choices=new VivePracticeConsentAuthority(Task,Clock,true);Check.True(choices.ConfirmEye()&&choices.ConfirmTask());
                var binding=new VivePracticeStartBinding(Task,Clock,choices,true);
                Task.UpdateTrackpad(true,new VivePreviewTrackpadSample(true,false,0,0));Clock.Value=11;Task.OwnerBeginPractice(Actor.Owner);
                VivePracticeStartBinding.Ticket ticket;Check.True(binding.TryAcquire(1,4096,out ticket));
                Context=new VivePracticeContextV3("prepared-session","participant","stream","device",Clock.ClockId,"mapping",1,
                    "epoch",0,null,VivePracticeEpochReasonV3.Initial,11);
                Host=new VivePracticeOwnedMemoryStart(Task,Clock,choices,binding,Context,Store,"target",65536,32);
                Check.True(Host.TryStart(ticket));VivePracticeOwnedStartReceipt receipt;Check.True(Host.TryTakeReceipt(out receipt));
                Cursor=new VivePracticeTaskEventCursor(Host,receipt,4096,64);
                var field=typeof(VivePracticeOwnedMemoryStart).GetField("writer",BindingFlags.Instance|BindingFlags.NonPublic);
                Check.True(field!=null);Writer=(VivePracticeMultiWriter)field.GetValue(Host);Check.True(Writer!=null);
            }
            internal VivePracticeTaskEventTransaction Stage()
            {
                Clock.Value=12;Task.MarkPresented(Task.CurrentTrial.TrialId);VivePracticeTaskEventTransaction tx;
                Check.Equal(VivePracticeTaskCursorResult.Staged,Cursor.TryStage(out tx));Check.True(tx!=null);return tx;
            }
            internal VivePracticeTaskEventCursor.CursorAppendLease Begin(VivePracticeTaskEventTransaction tx)
            {VivePracticeTaskEventCursor.CursorAppendLease lease;Check.True(Cursor.TryBeginPendingAppend(tx,out lease));return lease;}
        }
        public static void Register(TestSuite suite)
        {
            suite.Run("prepared task is exact immutable delta without writer or target advance",()=>{
                var f=new Fixture();var tx=f.Stage();var lease=f.Begin(tx);byte[] before=f.Writer.Snapshot(),targetBefore=f.Store.CopyForTest("target");
                VivePracticeMultiWriter.Prepared p;Check.True(f.Writer.TryPrepareTaskTransaction(f.Host,lease,out p));
                Check.True(f.Writer.IsOutstandingPrepared(f.Host,lease,p));Check.Equal((long)before.Length,p.ExpectedPreviousLength);
                var delta=p.CopyDelta();byte[] hash;using(var sha=SHA256.Create())hash=sha.ComputeHash(delta);
                Check.True(p.MatchesDigest(hash));Check.Equal(p.ExpectedPreviousLength+delta.Length,p.NewTotalLength);
                delta[0]^=1;Check.True(!delta.SequenceEqual(p.CopyDelta()));hash[0]^=1;Check.True(!p.MatchesDigest(hash));
                Check.True(before.SequenceEqual(f.Writer.Snapshot())&&targetBefore.SequenceEqual(f.Store.CopyForTest("target")));
            });
            suite.Run("prepared cannot commit from outside issuer target lock chain",()=>{
                var f=new Fixture();var tx=f.Stage();var lease=f.Begin(tx);byte[] before=f.Writer.Snapshot();VivePracticeMultiWriter.Prepared p;
                Check.True(f.Writer.TryPrepareTaskTransaction(f.Host,lease,out p));
                Check.True(!f.Writer.CanCommitPrepared(f.Host,lease,p,null));Check.True(!f.Writer.CommitPrepared(f.Host,lease,p,null));
                Check.True(f.Writer.Faulted&&before.SequenceEqual(f.Writer.Snapshot()));
            });
            suite.Run("prepared duplicate or abandoned attempts are terminal and preserve prefix",()=>{
                var f=new Fixture();var tx=f.Stage();var lease=f.Begin(tx);byte[] before=f.Writer.Snapshot();VivePracticeMultiWriter.Prepared p,duplicate;
                Check.True(f.Writer.TryPrepareTaskTransaction(f.Host,lease,out p));Check.True(!f.Writer.TryPrepareTaskTransaction(f.Host,lease,out duplicate));
                Check.True(f.Writer.Faulted&&before.SequenceEqual(f.Writer.Snapshot()));
                var g=new Fixture();var lease2=g.Begin(g.Stage());byte[] before2=g.Writer.Snapshot();VivePracticeMultiWriter.Prepared p2;
                Check.True(g.Writer.TryPrepareTaskTransaction(g.Host,lease2,out p2));g.Writer.AbandonPrepared(g.Host,lease2,p2);
                Check.True(g.Writer.Faulted&&p2.CopyDelta()==null&&p2.CopyDeltaDigest()==null&&
                    !g.Writer.TryPrepareTaskTransaction(g.Host,lease2,out duplicate)&&before2.SequenceEqual(g.Writer.Snapshot()));
            });
            suite.Run("bound writer public append and close cannot bypass issuer",()=>{
                var f=new Fixture();byte[] before=f.Writer.Snapshot();Check.True(!f.Writer.Close()&&f.Writer.Faulted&&before.SequenceEqual(f.Writer.Snapshot()));
                var g=new Fixture();before=g.Writer.Snapshot();var bogus=new JournalEvent(0,12,g.Clock.ClockId,g.Task.SessionId,-1,0,"t","stimulus_presented",new Dictionary<string,string>());
                Check.True(!g.Writer.AppendTaskTransaction(g.Context,new[]{bogus})&&g.Writer.Faulted&&before.SequenceEqual(g.Writer.Snapshot()));
            });
            suite.Run("prepared cannot be transferred across issuers or writers",()=>{
                var a=new Fixture();var b=new Fixture();var lease=a.Begin(a.Stage());VivePracticeMultiWriter.Prepared p;
                Check.True(!a.Writer.TryPrepareTaskTransaction(b.Host,lease,out p)&&a.Writer.Faulted);
            });
            suite.Run("prepared wrong-thread access faults without advancing bytes",()=>{
                var f=new Fixture();var lease=f.Begin(f.Stage());VivePracticeMultiWriter.Prepared p;Check.True(f.Writer.TryPrepareTaskTransaction(f.Host,lease,out p));
                byte[] before=f.Writer.Snapshot();bool result=true;var thread=new Thread(()=>result=f.Writer.IsOutstandingPrepared(f.Host,lease,p));
                thread.Start();Check.True(thread.Join(5000));Check.True(!result&&f.Writer.Faulted&&before.SequenceEqual(f.Writer.Snapshot()));
            });
            suite.Run("prepared wrong-thread copy invalidates writer and cannot leak delta",()=>{
                var f=new Fixture();var lease=f.Begin(f.Stage());VivePracticeMultiWriter.Prepared p;Check.True(f.Writer.TryPrepareTaskTransaction(f.Host,lease,out p));
                byte[] before=f.Writer.Snapshot(),copied=new byte[]{1};var thread=new Thread(()=>copied=p.CopyDelta());
                thread.Start();Check.True(thread.Join(5000));Check.True(copied==null&&f.Writer.Faulted&&p.CopyDeltaDigest()==null&&before.SequenceEqual(f.Writer.Snapshot()));
            });
            suite.Run("issuer commits real participant transaction to sealed fake target",()=>{
                var f=new Fixture();var tx=f.Stage();Check.True(f.Host.TryAppendPending(f.Cursor,tx));
                var r=VivePracticeMultiReader.Read(f.Store.CopyForTest("target"),true,65536,32);
                Check.Equal(VivePracticeMultiStatus.Unclean,r.Status);Check.Equal(4,r.Events.Count);Check.Equal("stimulus_presented",r.Events[3].Kind);
                Check.True(!r.DurableStorageProven&&!r.ConsentAuthenticated&&!r.FullPracticeCoverageProven);
            });
        }
    }
}
