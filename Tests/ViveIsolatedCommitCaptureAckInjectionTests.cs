using System;
using System.Threading;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    // Called only from an explicitly instrumented isolated source copy; never
    // added to production capture. The worker and all output remain fake.
    internal static class ViveIsolatedCaptureAckHook
    {
        internal static Func<ViveSyntheticWorkerAck,ViveSyntheticWorkerAck> Rewrite;
        internal static ViveSyntheticWorkerAck Apply(ViveSyntheticWorkerAck ack)
        {var callback=Rewrite;return callback==null?ack:callback(ack);}
    }
    public static class ViveIsolatedCommitCaptureAckInjectionTests
    {
        sealed class Clock:IClock
        {internal double Value=10;public string ClockId {get{return "isolated-ack-clock";}} public double Now {get{return Value;}}}
        sealed class Fixture:IDisposable
        {
            internal readonly Clock Clock=new Clock();internal readonly ViveIsolatedCommitCapture Capture;
            internal Fixture()
            {
                var config=new SessionConfiguration("isolated-ack-session",42,
                    new Allocation("isolated-ack-allocation",new[]{Condition.B,Condition.C,Condition.A,Condition.D},GazeTarget.Participant,new[]{0,1,2,3}),
                    RunKind.Simulation,"en",3,30,true,0,true,true,true);
                Capture=new ViveIsolatedCommitCapture(config,Clock,new ExperimentIncrementalOptions(4096,128,16L*1024*1024),8,262144,8,64,16777216);
                Submit();Check.Equal(ViveSyntheticWorkerAckRead.Available,Ack());Check.True(Capture.BaselineAcknowledged);
            }
            internal void Submit()
            {
                for(int i=0;i<5000;i++)
                {var result=Capture.TrySubmitNext();if(result==ViveSyntheticWorkerAdmission.Queued)return;
                    Check.Equal(ViveSyntheticWorkerAdmission.Busy,result);Thread.Sleep(1);}
                throw new InvalidOperationException("bounded synthetic submit wait exceeded");
            }
            internal ViveSyntheticWorkerAckRead Ack()
            {
                for(int i=0;i<5000;i++)
                {var result=Capture.TryConsumeAck();if(result==ViveSyntheticWorkerAckRead.Available||result==ViveSyntheticWorkerAckRead.Rejected)return result;
                    Check.True(result==ViveSyntheticWorkerAckRead.Empty||result==ViveSyntheticWorkerAckRead.Busy);Thread.Sleep(1);}
                throw new InvalidOperationException("bounded synthetic ack wait exceeded");
            }
            internal void Familiarize()
            {Clock.Value+=1;Check.True(Capture.TryPerform(ViveIsolatedAction.BeginFamiliarization));}
            internal void Practice()
            {Clock.Value+=1;Check.True(Capture.TryPerform(ViveIsolatedAction.BeginPractice));}
            public void Dispose(){ViveIsolatedCaptureAckHook.Rewrite=null;Capture.Dispose();}
        }
        static ViveSyntheticWorkerAck Alter(ViveSyntheticWorkerAck ack,long sequence,object identity,ViveSyntheticWorkerOutcome outcome,byte[] digest)
        {return new ViveSyntheticWorkerAck(sequence,identity,outcome,"test-only injected ack",digest);}
        static void MustStop(Fixture f,long expectedRevision,int expectedPending)
        {
            Check.True(f.Capture.Terminal&&f.Capture.LossDetected);Check.Equal(expectedRevision,f.Capture.AcknowledgedRevision);
            Check.Equal(expectedPending,f.Capture.PendingCount);Check.Equal(ViveSyntheticWorkerAdmission.Rejected,f.Capture.TrySubmitNext());
            Check.Equal(ViveSyntheticWorkerAckRead.Rejected,f.Capture.TryConsumeAck());
        }
        // Register only when Capture was compiled from the instrumented copy.
        public static void RegisterInstrumented(TestSuite suite)
        {
            suite.Run("INSTRUMENTED isolated capture rejects replayed previous ack",()=>{
                using(var f=new Fixture())
                {
                    ViveSyntheticWorkerAck previous=null;
                    ViveIsolatedCaptureAckHook.Rewrite=ack=>{if(ack.Sequence==1){previous=ack;return ack;}return previous;};
                    f.Familiarize();f.Submit();Check.Equal(ViveSyntheticWorkerAckRead.Available,f.Ack());Check.True(previous!=null);
                    f.Practice();f.Submit();Check.Equal(ViveSyntheticWorkerAckRead.Rejected,f.Ack());MustStop(f,1,1);
                }
            });
            suite.Run("INSTRUMENTED isolated capture rejects future ordinal ack",()=>{
                using(var f=new Fixture())
                {
                    f.Familiarize();f.Practice();f.Submit();f.Submit();
                    ViveIsolatedCaptureAckHook.Rewrite=ack=>Alter(ack,ack.Sequence+1,ack.Identity,ack.Outcome,ack.CopyDigest());
                    Check.Equal(ViveSyntheticWorkerAckRead.Rejected,f.Ack());MustStop(f,0,2);
                }
            });
            suite.Run("INSTRUMENTED isolated capture rejects wrong identity ack",()=>{
                using(var f=new Fixture())
                {
                    f.Familiarize();f.Submit();
                    ViveIsolatedCaptureAckHook.Rewrite=ack=>Alter(ack,ack.Sequence,new object(),ack.Outcome,ack.CopyDigest());
                    Check.Equal(ViveSyntheticWorkerAckRead.Rejected,f.Ack());MustStop(f,0,1);
                }
            });
            suite.Run("INSTRUMENTED isolated capture rejects wrong digest ack",()=>{
                using(var f=new Fixture())
                {
                    f.Familiarize();f.Submit();
                    ViveIsolatedCaptureAckHook.Rewrite=ack=>{var digest=ack.CopyDigest();digest[0]^=255;return Alter(ack,ack.Sequence,ack.Identity,ack.Outcome,digest);};
                    Check.Equal(ViveSyntheticWorkerAckRead.Rejected,f.Ack());MustStop(f,0,1);
                }
            });
            suite.Run("INSTRUMENTED isolated capture rejects uncertain outcome ack",()=>{
                using(var f=new Fixture())
                {
                    f.Familiarize();f.Submit();
                    ViveIsolatedCaptureAckHook.Rewrite=ack=>Alter(ack,ack.Sequence,ack.Identity,ViveSyntheticWorkerOutcome.Uncertain,ack.CopyDigest());
                    Check.Equal(ViveSyntheticWorkerAckRead.Rejected,f.Ack());MustStop(f,0,1);
                }
            });
        }
    }
}
