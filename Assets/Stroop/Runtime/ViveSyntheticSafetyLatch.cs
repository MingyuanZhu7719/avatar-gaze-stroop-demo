#if VIVE_SYNTHETIC_BATCH_FIXTURE
using System;
using System.Threading;
using AvatarStroop.Core;

namespace AvatarStroop.Runtime
{
    // Test-build-only bridge. It is not attached to Unity, a writer, or a
    // research session. An accepted signal closes the in-memory authority;
    // it does not prove that an in-flight native operation has stopped.
    internal sealed class ViveSyntheticSafetyLatch
    {
        internal enum Result { Idle, Pending, Paused, AlreadyEnded, Withdrawn, NotApplied, Uncertain, WrongOwner }
        const int Fault=1, Withdrawal=2, Closed=4;
        readonly ViveDevelopmentParticipant task;
        readonly ViveNamedSessionFrameAdmission admission;
        readonly object run;
        readonly Thread owner;
        int state;
        bool pauseAttempted,withdrawAttempted;
        Result pauseResult,withdrawResult;

        internal ViveSyntheticSafetyLatch(ViveDevelopmentParticipant participant,ViveNamedSessionFrameAdmission source,
            ViveProEyeWorker eye,MonotonicClock clock)
        {
            object identity;
            if(participant==null||source==null||eye==null||clock==null||!source.SyntheticOnly||eye.UsesInstalledSdkPath||
                !source.UsesExactSources(participant,eye,clock)||!eye.TryGetRecordingRunIdentity(out identity)||identity==null)
                throw new InvalidOperationException("Synthetic safety source unavailable.");
            task=participant;admission=source;run=identity;owner=Thread.CurrentThread;
        }
        // Identity is an in-memory test capability, not authentication.
        internal object RunForTest {get{return run;}}
        internal bool SignalFault(object identity){return Signal(identity,Fault);}
        internal bool SignalWithdrawal(object identity){return Signal(identity,Withdrawal);}
        bool Signal(object identity,int kind)
        {
            if(!Object.ReferenceEquals(run,identity))return false;
            while(true)
            {
                int old=Volatile.Read(ref state);if((old&Closed)!=0)return false;
                if(Interlocked.CompareExchange(ref state,old|kind,old)==old)break;
            }
            // Abort uses the same shared revoke fence as the admission. This
            // technical stop does not set a participant-withdrawal end reason.
            admission.IoFence.Abort();
            return true;
        }
        internal void Close()
        {while(true){int old=Volatile.Read(ref state);if((old&Closed)!=0)return;if(Interlocked.CompareExchange(ref state,old|Closed,old)==old)return;}}
        // Only this test's creating thread may inspect/mutate the participant.
        // TryApply uses TryEnter for two locks, but its subsequent task/clock
        // calls have no hard realtime or bounded-duration guarantee.
        internal Result PollOnOwner()
        {
            if(!Object.ReferenceEquals(Thread.CurrentThread,owner))return Result.WrongOwner;
            int current=Volatile.Read(ref state);
            // Closing rejects new messages, but cannot discard an accepted
            // safety request; its owner must still poll the retained latch.
            if((current&(Fault|Withdrawal))==0)return (current&Closed)!=0?Result.NotApplied:Result.Idle;
            admission.IoFence.Abort();
            bool withdraw=(current&Withdrawal)!=0;
            if(withdraw&&withdrawAttempted)return withdrawResult;
            if(!withdraw&&pauseAttempted)return pauseResult;
            var applied=task.TryApplyNamedRawSafety(withdraw);
            if(applied==ViveDevelopmentParticipant.NamedRawSafetyResult.Pending)return Result.Pending;
            Result result;
            if(applied==ViveDevelopmentParticipant.NamedRawSafetyResult.Uncertain)result=Result.Uncertain;
            else if(applied==ViveDevelopmentParticipant.NamedRawSafetyResult.NotApplied)result=Result.NotApplied;
            else if(task.Phase==Phase.Ended)
                result=task.EndReason==EndReason.Withdrawn?Result.Withdrawn:Result.AlreadyEnded;
            else result=!withdraw&&task.Phase==Phase.Paused?Result.Paused:Result.Uncertain;
            if(withdraw){withdrawAttempted=true;withdrawResult=result;}else{pauseAttempted=true;pauseResult=result;}
            // An in-flight pause cannot be undone. If a newer explicit
            // withdrawal arrived meanwhile it still needs an owner action.
            if(!withdraw&&(Volatile.Read(ref state)&Withdrawal)!=0)return Result.Pending;
            return result;
        }
    }
}
#endif
