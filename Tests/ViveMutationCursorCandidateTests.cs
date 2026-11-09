using System;
using System.Threading;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    public static class ViveMutationCursorCandidateTests
    {
        static ViveMutationStateCandidate State(Phase phase,string trial=null,ControlSelection? selection=ControlSelection.LiveWoz,bool active=true,int block=0,int segment=0,Phase? pausedFrom=null)
        {return new ViveMutationStateCandidate(new TaskSnapshot("mutation-session",block,segment,trial,trial==null?(Colour?)null:Colour.Red,trial==null?(Colour?)null:Colour.Blue,phase,Condition.A),selection,selection.HasValue?"owner":null,active,pausedFrom);}
        static ViveMutationCommitCandidate Commit(long revision,ViveMutationStateCandidate before,ViveMutationStateCandidate after,bool complete=true)
        {return new ViveMutationCommitCandidate(revision,complete,new[]{new ViveMutationStepCandidate(0,before,after)},after);}
        public static void Register(TestSuite suite)
        {
            suite.Run("VIVE mutation candidate preserves ordered multi-step transaction without opening a gate",()=>{
                var a=State(Phase.BlockReady);var b=State(Phase.Countdown);var c=State(Phase.MainTask);var d=State(Phase.MainTask,"trial-1");
                var cursor=new ViveMutationCursorCandidate("mutation-session",a);
                var frame=new ViveMutationCommitCandidate(1,true,new[]{new ViveMutationStepCandidate(0,a,b),new ViveMutationStepCandidate(1,b,c),new ViveMutationStepCandidate(2,c,d)},d);
                Check.True(cursor.Advance(_=>new ViveMutationReadCandidate(1,frame,d)));
                Check.Equal(1L,cursor.Revision);Check.Equal("trial-1",cursor.Current.Task.TrialId);
                Check.True(!cursor.RealExperimentSourceBound&&!cursor.CanEnterCountdown&&!cursor.CanResume&&!cursor.Terminal);
            });
            suite.Run("VIVE mutation candidate drains every committed revision before exposing current state",()=>{
                var a=State(Phase.BlockReady);var b=State(Phase.Countdown);var c=State(Phase.MainTask,"trial-1");var cursor=new ViveMutationCursorCandidate("mutation-session",a);
                Check.True(!cursor.Advance(_=>new ViveMutationReadCandidate(2,Commit(1,a,b),c))&&!cursor.Terminal&&!cursor.CaughtUp&&cursor.Current==null);
                Check.True(cursor.Advance(_=>new ViveMutationReadCandidate(2,Commit(2,b,c),c))&&cursor.Revision==2);
                Check.True(cursor.Advance(_=>new ViveMutationReadCandidate(2,null,c)));
            });
            suite.Run("VIVE mutation candidate latches missing skipped replayed and rolled-back records",()=>{
                var a=State(Phase.BlockReady);var b=State(Phase.Countdown);
                var missing=new ViveMutationCursorCandidate("mutation-session",a);Check.True(!missing.Advance(_=>new ViveMutationReadCandidate(1,null,b))&&missing.Terminal);
                var skipped=new ViveMutationCursorCandidate("mutation-session",a);Check.True(!skipped.Advance(_=>new ViveMutationReadCandidate(2,Commit(2,a,b),b))&&skipped.Terminal);
                var replay=new ViveMutationCursorCandidate("mutation-session",a);Check.True(replay.Advance(_=>new ViveMutationReadCandidate(1,Commit(1,a,b),b)));
                Check.True(!replay.Advance(_=>new ViveMutationReadCandidate(1,Commit(1,a,b),b))&&replay.Terminal);
                var rollback=new ViveMutationCursorCandidate("mutation-session",a);Check.True(rollback.Advance(_=>new ViveMutationReadCandidate(1,Commit(1,a,b),b)));
                Check.True(!rollback.Advance(_=>new ViveMutationReadCandidate(0,null,a))&&rollback.Terminal);
            });
            suite.Run("VIVE mutation candidate detects hidden or incomplete intermediate state",()=>{
                var a=State(Phase.BlockReady);var b=State(Phase.Countdown);var c=State(Phase.MainTask,"trial-1");
                var omitted=new ViveMutationCursorCandidate("mutation-session",a);
                var frame=new ViveMutationCommitCandidate(1,true,new[]{new ViveMutationStepCandidate(0,a,b),new ViveMutationStepCandidate(2,b,c)},c);
                Check.True(!omitted.Advance(_=>new ViveMutationReadCandidate(1,frame,c))&&omitted.Terminal);
                var mismatch=new ViveMutationCursorCandidate("mutation-session",a);
                var bad=new ViveMutationCommitCandidate(1,true,new[]{new ViveMutationStepCandidate(0,b,c)},c);
                Check.True(!mismatch.Advance(_=>new ViveMutationReadCandidate(1,bad,c))&&mismatch.Terminal);
                var unversioned=new ViveMutationCursorCandidate("mutation-session",a);
                Check.True(!unversioned.Advance(_=>new ViveMutationReadCandidate(0,null,b))&&unversioned.Terminal);
            });
            suite.Run("VIVE mutation candidate latches incomplete transaction and source exception",()=>{
                var a=State(Phase.BlockReady);var b=State(Phase.Countdown);var failed=new ViveMutationCursorCandidate("mutation-session",a);
                Check.True(!failed.Advance(_=>new ViveMutationReadCandidate(1,Commit(1,a,b,false),b))&&failed.Terminal&&failed.Current==null);
                Check.True(!failed.Advance(_=>new ViveMutationReadCandidate(0,null,a)));
                var exception=new ViveMutationCursorCandidate("mutation-session",a);Check.True(!exception.Advance(_=>{throw new InvalidOperationException("sensitive detail");})&&exception.LastIssue=="mutation_feed_exception");
            });
            suite.Run("VIVE mutation candidate preserves source changes and pause recovery segment",()=>{
                var a=State(Phase.MainTask,"trial-1");var b=State(Phase.Paused,pausedFrom:Phase.MainTask);var c=State(Phase.Countdown,segment:1);
                var d=State(Phase.Countdown,selection:ControlSelection.Automatic,active:true,segment:1);var cursor=new ViveMutationCursorCandidate("mutation-session",a);
                var frame=new ViveMutationCommitCandidate(1,true,new[]{new ViveMutationStepCandidate(0,a,b),new ViveMutationStepCandidate(1,b,c),new ViveMutationStepCandidate(2,c,d)},d);
                Check.True(cursor.Advance(_=>new ViveMutationReadCandidate(1,frame,d)));
                Check.Equal(1,cursor.Current.Task.Segment);Check.Equal(ControlSelection.Automatic,cursor.Current.Selection.Value);
            });
            suite.Run("VIVE mutation candidate rejects non-owner thread access",()=>{
                var a=State(Phase.BlockReady);var cursor=new ViveMutationCursorCandidate("mutation-session",a);var thread=new Thread(()=>cursor.Advance(_=>new ViveMutationReadCandidate(0,null,a)));
                thread.Start();thread.Join();Check.True(cursor.Terminal&&cursor.Current==null&&cursor.LastIssue=="owner_thread_violation");
            });
        }
    }
}
