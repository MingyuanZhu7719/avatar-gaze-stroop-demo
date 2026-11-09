using System;
using System.Threading;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    // Only an explicitly instrumented isolated source copy invokes this hook.
    internal static class ViveIsolatedCaptureSafetyHook
    {
        internal static Action<string> Callback;
        internal static void Invoke(string stage){var action=Callback;if(action!=null)action(stage);}
    }
    public static class ViveIsolatedCommitCaptureAdmissionInjectionTests
    {
        sealed class Clock:IClock
        {public string ClockId {get{return "admission-clock";}} public double Now {get{return 10;}}}
        sealed class Fixture:IDisposable
        {
            internal readonly ViveIsolatedCommitCapture Capture;
            internal Fixture()
            {
                var config=new SessionConfiguration("admission-session",42,
                    new Allocation("admission-allocation",new[]{Condition.B,Condition.C,Condition.A,Condition.D},GazeTarget.Participant,new[]{0,1,2,3}),
                    RunKind.Simulation,"en",3,30,true,0,true,true,true);
                Capture=new ViveIsolatedCommitCapture(config,new Clock(),new ExperimentIncrementalOptions(4096,128,16L*1024*1024),8,262144,8,64,16777216);
                for(int i=0;i<5000;i++)
                {var result=Capture.TrySubmitNext();if(result==ViveSyntheticWorkerAdmission.Queued)break;
                    Check.Equal(ViveSyntheticWorkerAdmission.Busy,result);if(i==4999)throw new InvalidOperationException("baseline submit timeout");Thread.Sleep(1);}
                for(int i=0;i<5000;i++)
                {var result=Capture.TryConsumeAck();if(result==ViveSyntheticWorkerAckRead.Available)break;
                    Check.True(result==ViveSyntheticWorkerAckRead.Empty||result==ViveSyntheticWorkerAckRead.Busy);
                    if(i==4999)throw new InvalidOperationException("baseline ack timeout");Thread.Sleep(1);}
                Check.True(Capture.BaselineAcknowledged);
            }
            internal void Foreign(ViveIsolatedAction action)
            {
                bool accepted=false;var caller=new Thread(()=>accepted=Capture.TryPerform(action)){IsBackground=true};
                caller.Start();Check.True(caller.Join(5000));Check.True(accepted);
            }
            public void Dispose(){ViveIsolatedCaptureSafetyHook.Callback=null;Capture.Dispose();}
        }
        // Register only when Capture is compiled from the instrumented copy.
        public static void RegisterInstrumented(TestSuite suite)
        {
            suite.Run("INSTRUMENTED isolated withdraw before admission takes priority",()=>{
                using(var f=new Fixture())
                {
                    bool injected=false,ordinaryExecuted=false;
                    ViveIsolatedCaptureSafetyHook.Callback=stage=>{
                        if(stage=="before_admission"&&!injected){injected=true;f.Foreign(ViveIsolatedAction.Withdraw);}
                        if(stage=="after_ordinary_execute")ordinaryExecuted=true;
                    };
                    f.Capture.TryPerform(ViveIsolatedAction.BeginFamiliarization);
                    Check.True(injected&&!ordinaryExecuted);Phase phase;Check.True(f.Capture.TryGetPhase(out phase));
                    Check.Equal(Phase.Ended,phase);Check.True(f.Capture.Terminal&&f.Capture.LossDetected);
                }
            });
            suite.Run("INSTRUMENTED isolated admitted action may finish after withdraw and marks unknown",()=>{
                using(var f=new Fixture())
                {
                    bool injected=false,ordinaryExecuted=false;
                    ViveIsolatedCaptureSafetyHook.Callback=stage=>{
                        if(stage=="after_admission"&&!injected){injected=true;f.Foreign(ViveIsolatedAction.Withdraw);}
                        if(stage=="after_ordinary_execute")ordinaryExecuted=true;
                    };
                    Check.True(f.Capture.TryPerform(ViveIsolatedAction.BeginFamiliarization));
                    Check.True(injected&&ordinaryExecuted);Phase phase;Check.True(f.Capture.TryGetPhase(out phase));
                    Check.Equal(Phase.Ended,phase);Check.True(f.Capture.Terminal&&f.Capture.LossDetected);
                }
            });
            suite.Run("INSTRUMENTED isolated simultaneous pause survives failing deferred withdraw",()=>{
                using(var f=new Fixture())
                {
                    bool injected=false,withdrawFailed=false;
                    ViveIsolatedCaptureSafetyHook.Callback=stage=>{
                        if(stage=="after_admission"&&!injected){injected=true;f.Foreign(ViveIsolatedAction.Pause);f.Foreign(ViveIsolatedAction.Withdraw);}
                        if(stage=="before_withdraw"){withdrawFailed=true;throw new InvalidOperationException("synthetic withdraw failure");}
                    };
                    f.Capture.TryPerform(ViveIsolatedAction.BeginFamiliarization);
                    Check.True(injected&&withdrawFailed);Phase phase;Check.True(f.Capture.TryGetPhase(out phase));
                    Check.Equal(Phase.Paused,phase);Check.True(f.Capture.Terminal&&f.Capture.LossDetected);
                }
            });
            suite.Run("INSTRUMENTED isolated failed reentrant same safety is never retried",()=>{
                using(var f=new Fixture())
                {
                    bool injected=false,nestedAccepted=false,ordinaryAfterFailure=false;int pauseCalls=0;
                    ViveIsolatedCaptureSafetyHook.Callback=stage=>{
                        if(stage=="after_admission"&&!injected){injected=true;f.Foreign(ViveIsolatedAction.Pause);}
                        if(stage=="before_pause")
                        {
                            pauseCalls++;nestedAccepted=f.Capture.TryPerform(ViveIsolatedAction.Pause);
                            throw new InvalidOperationException("synthetic pause failure");
                        }
                        if(stage=="after_ordinary_execute"&&pauseCalls>0)ordinaryAfterFailure=true;
                    };
                    f.Capture.TryPerform(ViveIsolatedAction.BeginFamiliarization);
                    Check.True(injected&&nestedAccepted);Check.Equal(1,pauseCalls);
                    Check.True(f.Capture.Terminal&&f.Capture.LossDetected&&f.Capture.SafetyOutcomeUncertain);
                    Check.True(f.Capture.PumpDeferredSafety());Check.True(f.Capture.PumpDeferredSafety());
                    Check.Equal(1,pauseCalls);Check.True(!f.Capture.SafetyPending);
                    f.Capture.TryPerform(ViveIsolatedAction.Pause);Check.Equal(1,pauseCalls);
                    long revision=f.Capture.CapturedRevision;Phase prior;Check.True(f.Capture.TryGetPhase(out prior));
                    Check.True(!f.Capture.TryPerform(ViveIsolatedAction.BeginPractice));
                    Check.True(!f.Capture.TryPerform(ViveIsolatedAction.Tick));
                    Phase unchanged;Check.True(f.Capture.TryGetPhase(out unchanged));Check.Equal(prior,unchanged);
                    Check.Equal(revision,f.Capture.CapturedRevision);Check.True(!ordinaryAfterFailure);
                    Check.True(f.Capture.TryPerform(ViveIsolatedAction.Withdraw));Phase phase;
                    Check.True(f.Capture.TryGetPhase(out phase));Check.Equal(Phase.Ended,phase);Check.Equal(1,pauseCalls);
                }
            });
        }
    }
}
