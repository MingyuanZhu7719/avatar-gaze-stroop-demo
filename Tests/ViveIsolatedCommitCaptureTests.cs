using System;
using System.Collections.Generic;
using System.Threading;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    // Synthetic private Experiment and fake memory worker only. This is not a
    // participant recorder, durable storage, consent, device, or research gate.
    public static class ViveIsolatedCommitCaptureTests
    {
        sealed class Clock:IClock
        {
            internal double Value=10;internal Action OnNow;
            public string ClockId {get{return "isolated-capture-clock";}}
            public double Now {get{var action=OnNow;OnNow=null;if(action!=null)action();return Value;}}
        }
        sealed class Fixture:IDisposable
        {
            internal readonly Clock Clock=new Clock();internal readonly Experiment Reference;
            internal readonly ViveIsolatedCommitCapture Capture;
            internal Fixture(int pending=8,int workerCapacity=8,int frames=64,int bytes=16777216)
            {
                var config=new SessionConfiguration("isolated-capture-session",42,
                    new Allocation("isolated-capture-allocation",new[]{Condition.B,Condition.C,Condition.A,Condition.D},GazeTarget.Participant,new[]{0,1,2,3}),
                    RunKind.Simulation,"en",3,30,true,0,true,true,true);
                Reference=new Experiment(config,Clock);
                Capture=new ViveIsolatedCommitCapture(config,Clock,new ExperimentIncrementalOptions(4096,128,16L*1024*1024),
                    pending,262144,workerCapacity,frames,bytes);
            }
            internal void Submit(ViveSyntheticWorkerPlan plan=ViveSyntheticWorkerPlan.Complete)
            {
                for(int i=0;i<5000;i++)
                {
                    var result=Capture.TrySubmitNext(plan);if(result==ViveSyntheticWorkerAdmission.Queued)return;
                    Check.Equal(ViveSyntheticWorkerAdmission.Busy,result);Thread.Sleep(1);
                }
                throw new InvalidOperationException("bounded synthetic submit wait exceeded");
            }
            internal ViveSyntheticWorkerAckRead Ack()
            {
                for(int i=0;i<5000;i++)
                {
                    var result=Capture.TryConsumeAck();
                    if(result==ViveSyntheticWorkerAckRead.Available||result==ViveSyntheticWorkerAckRead.Rejected)return result;
                    Check.True(result==ViveSyntheticWorkerAckRead.Empty||result==ViveSyntheticWorkerAckRead.Busy);Thread.Sleep(1);
                }
                throw new InvalidOperationException("bounded synthetic ack wait exceeded");
            }
            internal byte[] Bytes()
            {
                for(int i=0;i<5000;i++){byte[] bytes;if(Capture.TryCopyFakeBytes(out bytes))return bytes;Thread.Sleep(1);}
                throw new InvalidOperationException("bounded fake snapshot wait exceeded");
            }
            internal void Baseline()
            {
                Check.True(!Capture.BaselineAcknowledged);
                Check.Equal(1,Capture.PendingCount);Submit();Check.Equal(ViveSyntheticWorkerAckRead.Available,Ack());
                Check.True(Capture.BaselineAcknowledged);Check.Equal(0L,Capture.AcknowledgedRevision);Check.Equal(0,Capture.PendingCount);
            }
            internal void BeginFamiliarization()
            {Clock.Value+=1;Check.True(Capture.TryPerform(ViveIsolatedAction.BeginFamiliarization));Reference.BeginFamiliarization(Actor.Owner);}
            internal void BeginPractice()
            {Clock.Value+=1;Check.True(Capture.TryPerform(ViveIsolatedAction.BeginPractice));Reference.BeginPractice(Actor.Owner);}
            public void Dispose(){Capture.Dispose();}
        }
        static void Same(JournalEvent expected,JournalEvent actual)
        {
            Check.Equal(expected.Sequence,actual.Sequence);Check.Equal(expected.Time,actual.Time);Check.Equal(expected.ClockId,actual.ClockId);
            Check.Equal(expected.SessionId,actual.SessionId);Check.Equal(expected.BlockIndex,actual.BlockIndex);Check.Equal(expected.Segment,actual.Segment);
            Check.Equal(expected.TrialId,actual.TrialId);Check.Equal(expected.Kind,actual.Kind);Check.Equal(expected.Fields.Count,actual.Fields.Count);
            foreach(var pair in expected.Fields){Check.True(actual.Fields.ContainsKey(pair.Key));Check.Equal(pair.Value,actual.Fields[pair.Key]);}
        }
        static void SameBytes(byte[] before,byte[] after)
        {Check.True(before!=null&&after!=null);Check.Equal(before.Length,after.Length);for(int i=0;i<before.Length;i++)Check.Equal(before[i],after[i]);}
        public static void Register(TestSuite suite)
        {
            suite.Run("isolated capture baseline ack then queued real commits preserve raw event order",()=>{
                using(var f=new Fixture())
                {
                    f.Baseline();f.BeginFamiliarization();f.BeginPractice();
                    string trial;Colour ink;Check.True(f.Capture.TryGetCurrentTrial(out trial,out ink));
                    Check.Equal(f.Reference.CurrentTrial.TrialId,trial);Check.Equal(f.Reference.CurrentTrial.Stimulus.Ink,ink);
                    f.Clock.Value+=1;Check.True(f.Capture.TryPerform(ViveIsolatedAction.MarkCurrentPresented));f.Reference.MarkPresented(trial);
                    f.Clock.Value+=1;Check.True(f.Capture.TryPerform(ViveIsolatedAction.Press,ink));f.Reference.ButtonChanged(ink,true);
                    f.Clock.Value+=1;Check.True(f.Capture.TryPerform(ViveIsolatedAction.Release,ink));f.Reference.ButtonChanged(ink,false);
                    Check.Equal(5,f.Capture.PendingCount);Check.Equal(0L,f.Capture.AcknowledgedRevision);
                    long head=f.Capture.CapturedRevision;Check.True(head>0);
                    for(int i=0;i<5;i++)f.Submit();
                    for(int i=0;i<5;i++)Check.Equal(ViveSyntheticWorkerAckRead.Available,f.Ack());
                    Check.Equal(0,f.Capture.PendingCount);Check.Equal(head,f.Capture.AcknowledgedRevision);
                    var frames=ViveIsolatedCaptureDecodeHelper.Decode(f.Bytes());Check.Equal(6,frames.Count);
                    var events=new List<JournalEvent>();
                    for(int i=0;i<frames.Count;i++)
                    {
                        Check.Equal((long)i,frames[i].Ordinal);Check.Equal((long)i,frames[i].Revision);
                        foreach(var e in frames[i].Events)events.Add(e);
                    }
                    Check.Equal(f.Reference.Events.Count,events.Count);
                    for(int i=0;i<events.Count;i++)Same(f.Reference.Events[i],events[i]);
                    Check.True(!f.Capture.CanWriteReal&&!f.Capture.ResearchAdmission&&!f.Capture.Terminal&&!f.Capture.LossDetected);
                }
            });
            suite.Run("isolated capture full queue executes ordinary task action and marks unknown",()=>{
                using(var f=new Fixture(1,1))
                {
                    f.Baseline();f.BeginFamiliarization();Check.Equal(1,f.Capture.PendingCount);
                    f.Clock.Value+=1;Check.True(f.Capture.TryPerform(ViveIsolatedAction.BeginPractice));
                    Phase phase;Check.True(f.Capture.TryGetPhase(out phase));Check.Equal(Phase.Practice,phase);
                    Check.True(f.Capture.Terminal&&f.Capture.LossDetected);Check.Equal(1,f.Capture.PendingCount);
                    Check.Equal(ViveSyntheticWorkerAdmission.Rejected,f.Capture.TrySubmitNext());
                }
            });
            suite.Run("isolated capture pause under full queue is reachable and marks unknown",()=>{
                using(var f=new Fixture(1,1))
                {
                    f.Baseline();var prefix=f.Bytes();f.BeginFamiliarization();Check.Equal(1,f.Capture.PendingCount);
                    Check.True(f.Capture.TryPerform(ViveIsolatedAction.Pause));Phase phase;
                    Check.True(f.Capture.TryGetPhase(out phase));Check.Equal(Phase.Paused,phase);
                    Check.True(f.Capture.Terminal&&f.Capture.LossDetected);SameBytes(prefix,f.Bytes());
                    Check.Equal(ViveSyntheticWorkerAdmission.Rejected,f.Capture.TrySubmitNext());
                }
            });
            suite.Run("isolated capture withdrawal under full queue is reachable and retains prefix",()=>{
                using(var f=new Fixture(1,1))
                {
                    f.Baseline();var prefix=f.Bytes();f.BeginFamiliarization();Check.Equal(1,f.Capture.PendingCount);
                    Check.True(f.Capture.TryPerform(ViveIsolatedAction.Withdraw));Phase phase;
                    Check.True(f.Capture.TryGetPhase(out phase));Check.Equal(Phase.Ended,phase);
                    Check.True(f.Capture.Terminal&&f.Capture.LossDetected);SameBytes(prefix,f.Bytes());
                    Check.Equal(ViveSyntheticWorkerAdmission.Rejected,f.Capture.TrySubmitNext());
                }
            });
            suite.Run("isolated capture partial worker acknowledgement stops and preserves prior fake prefix",()=>{
                using(var f=new Fixture())
                {
                    f.Baseline();var prefix=f.Bytes();f.BeginFamiliarization();f.Submit(ViveSyntheticWorkerPlan.PartialThenFail);
                    Check.Equal(ViveSyntheticWorkerAckRead.Rejected,f.Ack());Check.True(f.Capture.Terminal&&f.Capture.LossDetected);
                    Check.Equal(0L,f.Capture.AcknowledgedRevision);var bytes=f.Bytes();Check.True(bytes.Length>prefix.Length);
                    for(int i=0;i<prefix.Length;i++)Check.Equal(prefix[i],bytes[i]);
                    Check.Equal(ViveSyntheticWorkerAdmission.Rejected,f.Capture.TrySubmitNext());
                    Check.Equal(ViveSyntheticWorkerAckRead.Rejected,f.Capture.TryConsumeAck());SameBytes(bytes,f.Bytes());
                }
            });
            suite.Run("isolated capture revocation blocks future admission and preserves committed prefix",()=>{
                using(var f=new Fixture())
                {
                    f.Baseline();var prefix=f.Bytes();f.Capture.Revoke();
                    Check.True(f.Capture.Terminal&&f.Capture.LossDetected);
                    Check.True(f.Capture.TryPerform(ViveIsolatedAction.BeginFamiliarization));Phase phase;
                    Check.True(f.Capture.TryGetPhase(out phase));Check.Equal(Phase.Familiarization,phase);
                    Check.Equal(ViveSyntheticWorkerAdmission.Rejected,f.Capture.TrySubmitNext());SameBytes(prefix,f.Bytes());
                }
            });
            suite.Run("isolated capture pre-ack action executes but recording is unknown",()=>{
                using(var f=new Fixture())
                {
                    Check.True(!f.Capture.BaselineAcknowledged);Check.Equal(1,f.Capture.PendingCount);
                    Check.True(f.Capture.TryPerform(ViveIsolatedAction.BeginFamiliarization));Phase phase;
                    Check.True(f.Capture.TryGetPhase(out phase));Check.Equal(Phase.Familiarization,phase);
                    Check.True(f.Capture.Terminal&&f.Capture.LossDetected&&!f.Capture.BaselineAcknowledged);
                    Check.Equal(ViveSyntheticWorkerAdmission.Rejected,f.Capture.TrySubmitNext());
                }
            });
            suite.Run("isolated capture foreign-thread pause defers to owner and marks unknown",()=>{
                using(var f=new Fixture())
                {
                    f.Baseline();bool accepted=false;
                    var caller=new Thread(()=>accepted=f.Capture.TryPerform(ViveIsolatedAction.Pause)){IsBackground=true};
                    caller.Start();Check.True(caller.Join(5000));Check.True(accepted);
                    Check.True(f.Capture.Terminal&&f.Capture.LossDetected);Check.True(f.Capture.PumpDeferredSafety());
                    Phase phase;Check.True(f.Capture.TryGetPhase(out phase));Check.Equal(Phase.Paused,phase);
                }
            });
            suite.Run("isolated capture reentrant pause is deferred until action returns",()=>{
                using(var f=new Fixture())
                {
                    f.Baseline();bool accepted=false;f.Clock.OnNow=()=>accepted=f.Capture.TryPerform(ViveIsolatedAction.Pause);
                    Check.True(f.Capture.TryPerform(ViveIsolatedAction.BeginFamiliarization));Check.True(accepted);
                    Check.True(f.Capture.Terminal&&f.Capture.LossDetected);
                    Phase phase;Check.True(f.Capture.TryGetPhase(out phase));Check.Equal(Phase.Paused,phase);
                }
            });
        }
    }
}
