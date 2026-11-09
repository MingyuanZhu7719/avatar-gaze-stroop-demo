using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using AvatarStroop.Core;

namespace AvatarStroop.Runtime
{
    internal enum ViveIsolatedAction { BeginFamiliarization,BeginPractice,MarkCurrentPresented,Press,Release,ContinuePractice,Tick,Pause,Resume,Withdraw }

    // A private Simulation Experiment, its sole destructive incremental reader,
    // and a private fake worker. Never a participant, apparatus, storage,
    // authenticated consent, research gate, or production recorder. Complete
    // snapshots are deliberately expanded here; this is not a VR hot path.
    internal sealed class ViveIsolatedCommitCapture : IDisposable
    {
        sealed class Pending
        {
            internal readonly long Ordinal,Revision;internal readonly object Identity=new object();
            internal readonly byte[] Frame,Digest;internal readonly int PayloadBytes;internal bool Submitted;
            internal Pending(long ordinal,long revision,byte[] frame,int payload,byte[] digest)
            {Ordinal=ordinal;Revision=revision;Frame=frame;PayloadBytes=payload;Digest=digest;}
        }
        sealed class Bounded : IDisposable
        {
            readonly MemoryStream stream=new MemoryStream();readonly BinaryWriter writer;readonly int maximum;
            internal Bounded(int max){maximum=max;writer=new BinaryWriter(stream,new UTF8Encoding(false,true),true);}
            void Room(int n){if(n<0||stream.Length>maximum-(long)n)throw new InvalidOperationException("synthetic frame limit");}
            internal void Byte(byte x){Room(1);writer.Write(x);} internal void Int(int x){Room(4);writer.Write(x);}
            internal void Long(long x){Room(8);writer.Write(x);}
            internal void Text(string x)
            {
                if(x==null){Int(-1);return;}int n=Encoding.UTF8.GetByteCount(x);if(n>4096)throw new InvalidOperationException("synthetic string limit");
                Room(checked(4+n));writer.Write(n);writer.Write(Encoding.UTF8.GetBytes(x));
            }
            internal byte[] Bytes(){writer.Flush();return stream.ToArray();}
            public void Dispose(){writer.Dispose();stream.Dispose();}
        }
        readonly Experiment experiment;readonly IClock clock;readonly string clockId,session;
        readonly int owner,maxPending,maxFrameBytes;readonly ViveSyntheticCommitWorker worker;
        readonly object stateGate=new object();
        readonly Queue<Pending> pending=new Queue<Pending>();
        ExperimentIncrementalSnapshot captured;long revision,ackRevision=-1,lastEventSequence,nextOrdinal;
        double lastEventTime;int terminal,revoked,busy,loss,disposed,deferredSafety,safetyUncertainMask;string issue;bool baselineAcknowledged;
        internal bool CanWriteReal {get{return false;}} internal bool ResearchAdmission {get{return false;}}
        internal bool Terminal {get{return Volatile.Read(ref terminal)!=0;}} internal bool LossDetected {get{return Volatile.Read(ref loss)!=0;}}
        internal string LastIssue {get{lock(stateGate)return issue;}}
        void RequireOwner(){if(!Owner())throw new InvalidOperationException("Isolated capture owner required");}
        internal long CapturedRevision {get{RequireOwner();return revision;}}
        internal long AcknowledgedRevision {get{RequireOwner();return ackRevision;}} internal int PendingCount {get{RequireOwner();return pending.Count;}}
        internal bool BaselineAcknowledged {get{RequireOwner();return baselineAcknowledged;}}
        internal bool SafetyPending {get{lock(stateGate)return deferredSafety!=0;}}
        internal bool SafetyOutcomeUncertain {get{lock(stateGate)return safetyUncertainMask!=0;}}
        internal ViveIsolatedCommitCapture(SessionConfiguration configuration,IClock clock,ExperimentIncrementalOptions options,
            int maximumPending,int maximumFrameBytes,int workerCapacity,int maximumFrames,int maximumSinkBytes)
        {
            if(configuration==null||configuration.RunKind!=RunKind.Simulation||clock==null||options==null||
                maximumPending<1||maximumPending>256||maximumFrameBytes<177||maximumFrameBytes>262144)
                throw new ArgumentException("Explicit isolated Simulation and limits required");
            owner=Thread.CurrentThread.ManagedThreadId;this.clock=clock;clockId=clock.ClockId;session=configuration.SessionId;
            maxPending=maximumPending;maxFrameBytes=maximumFrameBytes;
            experiment=new Experiment(configuration,clock,options);captured=experiment.InitialIncrementalSnapshot;
            if(captured==null||captured.Task==null||captured.Task.SessionId!=session||captured.Scalars.EventCount!=1||
                experiment.Events.Count!=1||experiment.Events[0].Sequence!=0||experiment.Events[0].Kind!="session_created"||
                experiment.Events[0].ClockId!=clockId)throw new InvalidOperationException("Simulation baseline unavailable");
            lastEventSequence=0;lastEventTime=experiment.Events[0].Time;
            // Build the baseline before starting a worker so construction failure
            // cannot strand a background thread.
            var initial=Encode(null,captured,new[]{experiment.Events[0]},0);var first=MakePending(0,0,initial);
            worker=new ViveSyntheticCommitWorker(workerCapacity,maximumFrameBytes,maximumFrames,maximumSinkBytes);
            pending.Enqueue(first);nextOrdinal=1;
        }
        bool Owner(){if(Thread.CurrentThread.ManagedThreadId==owner)return true;Fail("owner_thread_violation",true);return false;}
        void Fail(string reason,bool unknown)
        {
            lock(stateGate){if(issue==null)issue=reason;if(unknown)Interlocked.Exchange(ref loss,1);Interlocked.Exchange(ref terminal,1);}
            if(worker!=null)worker.Revoke();
        }
        bool Enter(){if(!Owner())return false;if(Interlocked.CompareExchange(ref busy,1,0)!=0){Fail("reentrant_access",true);return false;}return true;}
        void Exit(){Interlocked.Exchange(ref busy,0);}
        static bool Safety(ViveIsolatedAction action){return action==ViveIsolatedAction.Pause||action==ViveIsolatedAction.Withdraw;}
        internal bool TryPerform(ViveIsolatedAction action,Colour colour=Colour.Red)
        {
            if(Safety(action)&&(Thread.CurrentThread.ManagedThreadId!=owner||Volatile.Read(ref busy)!=0))
            {return QueueDeferredSafety(action);}
            if(action==ViveIsolatedAction.Withdraw)Revoke();
            if(Safety(action))
            {int bit=action==ViveIsolatedAction.Withdraw?2:1;lock(stateGate)if((safetyUncertainMask&bit)!=0)return false;}
            if(Thread.CurrentThread.ManagedThreadId!=owner)
            {
                Owner();return false;
            }
            if(Interlocked.CompareExchange(ref busy,1,0)!=0)
            {
                if(Safety(action))return QueueDeferredSafety(action);
                Fail("reentrant_access",true);return false;
            }
            bool executed=false;
            try
            {
                if(!Enum.IsDefined(typeof(ViveIsolatedAction),action))return false;
                bool safety=Safety(action);
                if(action==ViveIsolatedAction.Withdraw)
                {
                    Execute(action,colour);return true;
                }
                bool mayRecord,safetyWasPending;
                lock(stateGate)
                {
                    if(!safety&&(Volatile.Read(ref disposed)!=0||safetyUncertainMask!=0))return false;
                    safetyWasPending=deferredSafety!=0;
                    // Action admission is ordered with Revoke, but an action
                    // already admitted may physically run after revocation.
                    mayRecord=!safetyWasPending&&!Terminal&&Volatile.Read(ref revoked)==0&&Volatile.Read(ref disposed)==0&&
                        baselineAcknowledged&&pending.Count<maxPending;
                }
                if(safetyWasPending){DrainDeferredSafety();if(!safety)return false;}
                Execute(action,colour);executed=true;
                if(!mayRecord||Terminal||Volatile.Read(ref revoked)!=0)
                {Fail("action_recording_unknown",true);return true;}
                CaptureOne();return true;
            }
            catch(Exception)
            {
                if(Safety(action))MarkSafetyUncertain(action==ViveIsolatedAction.Withdraw?2:1);
                Fail(executed?"capture_after_action_failed":"action_or_capture_failed",true);return false;
            }
            finally{DrainDeferredSafety();Exit();}
        }
        bool QueueDeferredSafety(ViveIsolatedAction action)
        {
            bool accepted;int bit=action==ViveIsolatedAction.Withdraw?2:1;
            lock(stateGate)
            {
                accepted=(safetyUncertainMask&bit)==0;if(accepted)deferredSafety|=bit;
                if(action==ViveIsolatedAction.Withdraw)Interlocked.Exchange(ref revoked,1);
                if(issue==null)issue="safety_request_deferred";
                Interlocked.Exchange(ref loss,1);Interlocked.Exchange(ref terminal,1);
            }
            if(worker!=null)worker.Revoke();
            return accepted;
        }
        void MarkSafetyUncertain(int bit)
        {lock(stateGate){safetyUncertainMask|=bit;deferredSafety&=~bit;}}
        // A clock/provider callback may request safety while Experiment is in
        // a mutation. Never recursively mutate it: revoke immediately, then
        // apply at most one of each deferred safety request after it returns.
        void DrainDeferredSafety()
        {
            int processed=0;
            while(true)
            {
                int next,selected;
                lock(stateGate)
                {
                    next=deferredSafety&~processed;if(next==0)return;
                    selected=(next&2)!=0?2:1;deferredSafety&=~selected;
                }
                processed|=selected;
                try
                {
                    Execute(selected==2?ViveIsolatedAction.Withdraw:ViveIsolatedAction.Pause,Colour.Red);
                    if(selected==2&&experiment.Phase==Phase.Ended){lock(stateGate)deferredSafety&=~1;}
                }
                catch(Exception){MarkSafetyUncertain(selected);Fail("deferred_safety_failed",true);}
            }
        }
        internal bool PumpDeferredSafety()
        {
            if(!Enter())return false;try{DrainDeferredSafety();return true;}finally{Exit();}
        }
        void Execute(ViveIsolatedAction action,Colour colour)
        {
            switch(action)
            {
                case ViveIsolatedAction.BeginFamiliarization:experiment.BeginFamiliarization(Actor.Owner);break;
                case ViveIsolatedAction.BeginPractice:experiment.BeginPractice(Actor.Owner);break;
                case ViveIsolatedAction.MarkCurrentPresented:
                    if(experiment.CurrentTrial==null)throw new InvalidOperationException("No current trial");
                    experiment.MarkPresented(experiment.CurrentTrial.TrialId);break;
                case ViveIsolatedAction.Press:experiment.ButtonChanged(colour,true);break;
                case ViveIsolatedAction.Release:experiment.ButtonChanged(colour,false);break;
                case ViveIsolatedAction.ContinuePractice:experiment.ContinuePractice();break;
                case ViveIsolatedAction.Tick:experiment.Tick();break;
                case ViveIsolatedAction.Pause:experiment.Pause(Actor.Participant);break;
                case ViveIsolatedAction.Resume:experiment.Resume(true);break;
                case ViveIsolatedAction.Withdraw:experiment.Withdraw();break;
                default:throw new ArgumentOutOfRangeException("action");
            }
        }
        internal bool TryGetCurrentTrial(out string trialId,out Colour ink)
        {
            trialId=null;ink=Colour.Red;if(!Owner()||Terminal||experiment.CurrentTrial==null)return false;
            trialId=experiment.CurrentTrial.TrialId;ink=experiment.CurrentTrial.Stimulus.Ink;return true;
        }
        // Owner-only synthetic diagnostics. Neither exposes the Experiment,
        // its commits/snapshots, nor the private fake worker or sink handle.
        internal bool TryGetPhase(out Phase phase)
        {phase=Phase.Preparation;if(!Owner())return false;phase=experiment.Phase;return true;}
        internal bool TryCopyFakeBytes(out byte[] bytes)
        {
            bytes=null;if(!Owner())return false;bool faulted,wasRevoked;string reason;
            return worker.TryCopyFakeBytes(out bytes,out faulted,out wasRevoked,out reason);
        }
        bool CaptureOne()
        {
            if(experiment.IncrementalObservationFaulted){Fail("source_faulted",true);return false;}
            ExperimentIncrementalCommit next;ExperimentIncrementalSnapshot current;long head;
            if(!experiment.TryReadIncrementalAfter(revision,out next,out head,out current)||experiment.IncrementalObservationFaulted||
                head<revision||current==null){Fail("source_read_failed",true);return false;}
            if(next==null)
            {
                if(head!=revision||!Object.ReferenceEquals(captured,current)||!experiment.IsIncrementalRevisionCurrent(revision))
                {Fail("source_gap",true);return false;}return true;
            }
            if(revision==Int64.MaxValue||next.Revision!=revision+1||next.Revision!=head||next.Steps==null||
                next.Steps.Count<1||next.Steps.Count>1024||next.FinalState==null){Fail("commit_gap",true);return false;}
            var before=captured;int index=0,eventCount=0;
            foreach(var step in next.Steps)
            {
                if(step==null||step.Index!=index++||step.Before==null||step.After==null||
                    !Object.ReferenceEquals(before,step.Before)||Object.ReferenceEquals(step.Before,step.After)||
                    step.After.Task==null||step.After.Task.SessionId!=session||step.Before.Scalars.EventCount<0||
                    step.After.Scalars.EventCount<step.Before.Scalars.EventCount||
                    step.After.Scalars.EventCount-step.Before.Scalars.EventCount>1){Fail("commit_step_invalid",true);return false;}
                eventCount+=step.After.Scalars.EventCount-step.Before.Scalars.EventCount;before=step.After;
            }
            if(!Object.ReferenceEquals(before,next.FinalState)||!Object.ReferenceEquals(current,next.FinalState)||
                next.FinalState.Scalars.EventCount!=lastEventSequence+1+eventCount||
                !experiment.IsIncrementalRevisionCurrent(next.Revision)){Fail("commit_head_mismatch",true);return false;}
            var source=experiment.Events;var events=new JournalEvent[eventCount];double prior=lastEventTime;
            for(int i=0;i<eventCount;i++)
            {
                long seq=lastEventSequence+1+i;if(seq<0||seq>=source.Count){Fail("event_gap",true);return false;}
                var e=source[(int)seq];if(e==null||e.Sequence!=seq||e.SessionId!=session||e.ClockId!=clockId||
                    Double.IsNaN(e.Time)||Double.IsInfinity(e.Time)||e.Time<prior){Fail("event_invalid",true);return false;}
                events[i]=e;prior=e.Time;
            }
            int eventIndex=0;
            foreach(var step in next.Steps)
            {
                if(step.After.Scalars.EventCount==step.Before.Scalars.EventCount)continue;
                var e=events[eventIndex++];string trial=step.Before.CurrentTrial.HasValue?step.Before.CurrentTrial.Value.TrialId:null;
                if(e.Sequence!=step.Before.Scalars.EventCount||e.BlockIndex!=step.Before.Task.BlockIndex||
                    e.Segment!=step.Before.Task.Segment||e.TrialId!=trial){Fail("event_step_mismatch",true);return false;}
            }
            byte[] payload=Encode(next,null,events,next.Revision);var item=MakePending(nextOrdinal,next.Revision,payload);
            lock(stateGate)
            {
                if(Terminal||Volatile.Read(ref revoked)!=0){Interlocked.Exchange(ref loss,1);return false;}
                pending.Enqueue(item);nextOrdinal++;captured=next.FinalState;revision=next.Revision;
                lastEventSequence+=eventCount;lastEventTime=prior;return true;
            }
        }
        byte[] Encode(ExperimentIncrementalCommit commit,ExperimentIncrementalSnapshot baseline,IReadOnlyList<JournalEvent> events,long txRevision)
        {
            int maxPayload=maxFrameBytes-(txRevision==0?177:169);
            using(var b=new Bounded(maxPayload))
            {
                b.Text("ISOLATED_SIMULATION_V1");b.Long(txRevision);b.Text(commit==null?"BASELINE":commit.Operation);
                if(commit==null){b.Int(1);WriteSnapshot(b,baseline);}
                else{b.Int(commit.Steps.Count);foreach(var step in commit.Steps){b.Int(step.Index);WriteSnapshot(b,step.Before);WriteSnapshot(b,step.After);}}
                b.Int(events.Count);foreach(var e in events)WriteEvent(b,e);return b.Bytes();
            }
        }
        static void WriteSnapshot(Bounded b,ExperimentIncrementalSnapshot snapshot)
        {
            var values=snapshot.ExportReferenceValuesForTest();if(values.Length>32768)throw new InvalidOperationException("snapshot limit");
            b.Int(values.Length);foreach(var v in values)
            {
                if(v==null){b.Byte(0);continue;}if(v is string){b.Byte(1);b.Text((string)v);continue;}
                if(v is bool){b.Byte((bool)v?(byte)3:(byte)2);continue;}if(v is int){b.Byte(4);b.Int((int)v);continue;}
                if(v is long){b.Byte(5);b.Long((long)v);continue;}if(v is uint){b.Byte(6);b.Long((long)(uint)v);continue;}
                if(v is double){b.Byte(7);b.Long(BitConverter.DoubleToInt64Bits((double)v));continue;}
                if(v.GetType().IsEnum){b.Byte(8);b.Text(v.GetType().FullName);b.Long(Convert.ToInt64(v));continue;}
                throw new InvalidOperationException("unsupported snapshot value");
            }
        }
        static void WriteEvent(Bounded b,JournalEvent e)
        {
            if(e==null||e.Fields==null||e.Fields.Count>64)throw new InvalidOperationException("invalid event");
            b.Long(e.Sequence);b.Long(BitConverter.DoubleToInt64Bits(e.Time));b.Text(e.ClockId);b.Text(e.SessionId);
            b.Int(e.BlockIndex);b.Int(e.Segment);b.Text(e.TrialId);b.Text(e.Kind);b.Int(e.Fields.Count);
            var keys=new List<string>(e.Fields.Keys);keys.Sort(StringComparer.Ordinal);
            foreach(var key in keys){if(String.IsNullOrWhiteSpace(key))throw new InvalidOperationException("invalid event field");b.Text(key);b.Text(e.Fields[key]);}
        }
        Pending MakePending(long ordinal,long txRevision,byte[] payload)
        {
            int overhead=ordinal==0?177:169;if(payload==null||payload.Length<1||payload.Length>maxFrameBytes-overhead)throw new InvalidOperationException("frame limit");
            var frame=new byte[checked(payload.Length+overhead)];var magic=Encoding.ASCII.GetBytes("SYNTHETIC-COMMIT-V1");
            Buffer.BlockCopy(magic,0,frame,0,magic.Length);Buffer.BlockCopy(BitConverter.GetBytes(ordinal),0,frame,24,8);
            Buffer.BlockCopy(BitConverter.GetBytes(txRevision),0,frame,32,8);Buffer.BlockCopy(BitConverter.GetBytes(payload.Length),0,frame,40,4);
            Buffer.BlockCopy(payload,0,frame,overhead,payload.Length);byte[] digest;using(var hash=SHA256.Create())digest=hash.ComputeHash(frame);
            return new Pending(ordinal,txRevision,frame,payload.Length,digest);
        }
        internal ViveSyntheticWorkerAdmission TrySubmitNext(ViveSyntheticWorkerPlan plan=ViveSyntheticWorkerPlan.Complete)
        {
            if(!Enter())return ViveSyntheticWorkerAdmission.Rejected;
            try
            {
                if(Terminal||Volatile.Read(ref revoked)!=0||Volatile.Read(ref disposed)!=0)return ViveSyntheticWorkerAdmission.Rejected;
                foreach(var item in pending)if(!item.Submitted)
                {
                    var result=worker.TrySubmit(item.Ordinal,item.Identity,item.Frame,item.PayloadBytes,plan);
                    if(result==ViveSyntheticWorkerAdmission.Queued)item.Submitted=true;
                    else if(result==ViveSyntheticWorkerAdmission.Rejected)Fail("worker_rejected",true);
                    return result;
                }
                return ViveSyntheticWorkerAdmission.Busy;
            }
            catch(Exception){Fail("worker_submit_exception",true);return ViveSyntheticWorkerAdmission.Rejected;}
            finally{Exit();}
        }
        internal ViveSyntheticWorkerAckRead TryConsumeAck()
        {
            if(!Enter())return ViveSyntheticWorkerAckRead.Rejected;
            try
            {
                if(Terminal||Volatile.Read(ref revoked)!=0||Volatile.Read(ref disposed)!=0)return ViveSyntheticWorkerAckRead.Rejected;
                ViveSyntheticWorkerAck ack;var result=worker.TryTakeAck(out ack);
                if(result==ViveSyntheticWorkerAckRead.Rejected){Fail("worker_ack_rejected",true);return result;}
                if(result!=ViveSyntheticWorkerAckRead.Available)return result;
                if(pending.Count==0){Fail("unexpected_ack",true);return ViveSyntheticWorkerAckRead.Rejected;}
                var first=pending.Peek();var digest=ack==null?null:ack.CopyDigest();
                if(ack==null||!first.Submitted||ack.Sequence!=first.Ordinal||!Object.ReferenceEquals(ack.Identity,first.Identity)||
                    ack.Outcome!=ViveSyntheticWorkerOutcome.Accepted||!SameDigest(first.Digest,digest))
                {Fail("worker_ack_invalid_or_uncertain",true);return ViveSyntheticWorkerAckRead.Rejected;}
                lock(stateGate)
                {
                    if(Terminal||Volatile.Read(ref revoked)!=0||Volatile.Read(ref disposed)!=0)
                    {Interlocked.Exchange(ref loss,1);return ViveSyntheticWorkerAckRead.Rejected;}
                    pending.Dequeue();ackRevision=first.Revision;if(first.Ordinal==0)baselineAcknowledged=true;return result;
                }
            }
            catch(Exception){Fail("worker_ack_exception",true);return ViveSyntheticWorkerAckRead.Rejected;}
            finally{Exit();}
        }
        static bool SameDigest(byte[] a,byte[] b)
        {if(a==null||b==null||a.Length!=32||b.Length!=32)return false;int d=0;for(int i=0;i<32;i++)d|=a[i]^b[i];return d==0;}
        internal void Revoke()
        {
            lock(stateGate){Interlocked.Exchange(ref revoked,1);if(issue==null)issue="synthetic_revoked";Interlocked.Exchange(ref loss,1);Interlocked.Exchange(ref terminal,1);}
            if(worker!=null)worker.Revoke();
        }
        public void Dispose()
        {if(Interlocked.Exchange(ref disposed,1)!=0)return;Fail("disposed",true);worker.Dispose();}
    }
}
