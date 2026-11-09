using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Threading;

namespace AvatarStroop.Runtime
{
    // Isolated simulation only. No real participant, consent, device, socket,
    // Stream, durable storage, or experiment admission is represented here.
    public enum ViveSyntheticWorkerPlan { Complete=0, PartialThenFail=1, CompleteThenFail=2 }
    public enum ViveSyntheticWorkerAdmission { Queued=0, Busy=1, Rejected=2 }
    public enum ViveSyntheticWorkerAckRead { Available=0, Empty=1, Busy=2, Rejected=3 }
    public enum ViveSyntheticWorkerOutcome { Accepted=0, NotWritten=1, Uncertain=2 }

    // Historical result only, not current readiness or a persistence receipt.
    public sealed class ViveSyntheticWorkerAck
    {
        public long Sequence {get;private set;}
        public object Identity {get;private set;}
        public ViveSyntheticWorkerOutcome Outcome {get;private set;}
        public string Reason {get;private set;}
        readonly byte[] digest;
        public byte[] CopyDigest(){return digest==null?null:(byte[])digest.Clone();}
        internal ViveSyntheticWorkerAck(long sequence,object identity,ViveSyntheticWorkerOutcome outcome,string reason,byte[] hash)
        {Sequence=sequence;Identity=identity;Outcome=outcome;Reason=reason;digest=hash==null?null:(byte[])hash.Clone();}
    }

    // Sealed fake target; no externally supplied callback or writable handle.
    // The exact submitted array is retained only after an accepted fake write.
    internal sealed class ViveSyntheticWorkerMemorySink
    {
        readonly List<byte[]> pieces=new List<byte[]>();
        readonly int maximumBytes;int bytes;
        internal int Length {get{return bytes;}}
        internal ViveSyntheticWorkerMemorySink(int limit){maximumBytes=limit;}
        internal bool HasCapacity(int count){return count>=0&&count<=maximumBytes-bytes;}
        internal void AppendWhole(byte[] payload){pieces.Add(payload);bytes+=payload.Length;}
        internal void AppendPrefix(byte[] payload)
        {
            int count=Math.Max(1,payload.Length/2);var prefix=new byte[count];
            Buffer.BlockCopy(payload,0,prefix,0,count);pieces.Add(prefix);bytes+=count;
        }
        internal byte[] Copy()
        {
            var result=new byte[bytes];int offset=0;
            foreach(var piece in pieces){Buffer.BlockCopy(piece,0,result,offset,piece.Length);offset+=piece.Length;}
            return result;
        }
    }

    // One owner and one private background worker. Bounds apply to cumulative
    // fake bytes, frame size, frame count and outstanding queue+ack entries.
    // Submit and acknowledgment reads never wait on the worker's lock. A busy
    // return has no effect and may be retried; Queued is not a write result.
    // Revoke is nonblocking. An atomic reservation orders fake write vs revoke;
    // a revoke during a reserved write makes its result uncertain. It does not
    // cancel a reserved operation and makes no statement about real I/O.
    public sealed class ViveSyntheticCommitWorker : IDisposable
    {
        sealed class Item
        {
            internal readonly long Sequence;internal readonly object Identity;internal readonly byte[] Payload;internal readonly ViveSyntheticWorkerPlan Plan;
            internal Item(long sequence,object identity,byte[] payload,ViveSyntheticWorkerPlan plan){Sequence=sequence;Identity=identity;Payload=payload;Plan=plan;}
        }
        sealed class ReferenceComparer:IEqualityComparer<object>
        {public new bool Equals(object a,object b){return Object.ReferenceEquals(a,b);}public int GetHashCode(object a){return RuntimeHelpers.GetHashCode(a);}}
        readonly object gate=new object();readonly int owner,capacity,maximumFrameBytes,maximumFrames;
        readonly Queue<Item> pending=new Queue<Item>();readonly Queue<ViveSyntheticWorkerAck> completed=new Queue<ViveSyntheticWorkerAck>();
        readonly HashSet<object> identities=new HashSet<object>(new ReferenceComparer());
        readonly ViveSyntheticWorkerMemorySink sink;readonly AutoResetEvent wake=new AutoResetEvent(false);readonly Thread worker;
        // 0=active, 1=revoked, 2=fake write reserved, 3=admission reserved.
        // CAS 2->0 commits a fake write; CAS 3->0 commits an admission.
        int outstanding,acceptedFrames,foreignFault,lifecycle;long nextSequence;bool faulted,exited;string issue;
        public bool ResearchAdmission {get{return false;}}
        public bool RealStorage {get{return false;}}
        public bool ConsentAuthenticated {get{return false;}}
        public ViveSyntheticCommitWorker(int capacity,int maximumFrameBytes,int maximumFrames,int maximumSinkBytes)
        {
            if(capacity<1||capacity>256||maximumFrameBytes<177||maximumFrameBytes>262144||maximumFrames<1||maximumFrames>4096||
                maximumSinkBytes<1||maximumSinkBytes>16*1024*1024)throw new ArgumentOutOfRangeException("Explicit bounded synthetic limits required");
            this.capacity=capacity;this.maximumFrameBytes=maximumFrameBytes;this.maximumFrames=maximumFrames;
            sink=new ViveSyntheticWorkerMemorySink(maximumSinkBytes);owner=Thread.CurrentThread.ManagedThreadId;
            worker=new Thread(Work){IsBackground=true,Name="synthetic-commit-worker"};worker.Start();
        }
        bool Owner()
        {
            if(Thread.CurrentThread.ManagedThreadId==owner)return true;
            Interlocked.Exchange(ref foreignFault,1);
            try{wake.Set();}catch(ObjectDisposedException){}
            return false;
        }
        static bool Fatal(Exception error){return error is OutOfMemoryException||error is StackOverflowException||error is AccessViolationException;}
        void CancelPending(string reason)
        {while(pending.Count>0){var item=pending.Dequeue();completed.Enqueue(new ViveSyntheticWorkerAck(item.Sequence,item.Identity,ViveSyntheticWorkerOutcome.NotWritten,reason,null));}}
        void Fault(string reason)
        {if(!faulted){faulted=true;issue=reason;}CancelPending("worker_faulted");}
        // Caller supplies a complete synthetic frame; layout/hash authenticity
        // is not checked. The first frame includes the eight-byte magic.
        public ViveSyntheticWorkerAdmission TrySubmit(long sequence,object identity,byte[] frame,int payloadBytes,ViveSyntheticWorkerPlan plan)
        {
            if(!Owner())return ViveSyntheticWorkerAdmission.Rejected;
            if(!Monitor.TryEnter(gate))return ViveSyntheticWorkerAdmission.Busy;
            try
            {
                if(Interlocked.CompareExchange(ref lifecycle,0,0)!=0||faulted||Interlocked.CompareExchange(ref foreignFault,0,0)!=0)return ViveSyntheticWorkerAdmission.Rejected;
                int overhead=sequence==0?177:169;int payloadLimit=sequence==0?261967:261975;
                if(sequence!=nextSequence||nextSequence>=maximumFrames||identity==null||identities.Contains(identity)||frame==null||
                    payloadBytes<0||payloadBytes>payloadLimit||frame.Length!=payloadBytes+overhead||frame.Length>maximumFrameBytes||
                    !Enum.IsDefined(typeof(ViveSyntheticWorkerPlan),plan))
                {Fault("invalid_submission");if(!exited)wake.Set();return ViveSyntheticWorkerAdmission.Rejected;}
                if(outstanding>=capacity)return ViveSyntheticWorkerAdmission.Busy;
                if(Interlocked.CompareExchange(ref lifecycle,3,0)!=0)return ViveSyntheticWorkerAdmission.Rejected;
                try
                {
                    var copy=(byte[])frame.Clone();
                    if(Interlocked.CompareExchange(ref lifecycle,0,3)!=3)return ViveSyntheticWorkerAdmission.Rejected;
                    identities.Add(identity);pending.Enqueue(new Item(sequence,identity,copy,plan));outstanding++;nextSequence++;
                    wake.Set();return ViveSyntheticWorkerAdmission.Queued;
                }
                catch(Exception error) when(!Fatal(error))
                {Interlocked.CompareExchange(ref lifecycle,0,3);Fault("submit_failed");if(!exited)wake.Set();return ViveSyntheticWorkerAdmission.Rejected;}
            }
            finally{Monitor.Exit(gate);}
        }
        public ViveSyntheticWorkerAckRead TryTakeAck(out ViveSyntheticWorkerAck ack)
        {
            ack=null;if(!Owner())return ViveSyntheticWorkerAckRead.Rejected;
            if(!Monitor.TryEnter(gate))return ViveSyntheticWorkerAckRead.Busy;
            try
            {
                if(Interlocked.CompareExchange(ref foreignFault,0,0)!=0)return ViveSyntheticWorkerAckRead.Rejected;
                if(completed.Count==0)return ViveSyntheticWorkerAckRead.Empty;
                ack=completed.Dequeue();outstanding--;return ViveSyntheticWorkerAckRead.Available;
            }
            finally{Monitor.Exit(gate);}
        }
        // Revocation is permitted from any thread and does not wait on a lock.
        // It does not erase prior fake bytes or reverse a committed fake write.
        public void Revoke()
        {
            Interlocked.Exchange(ref lifecycle,1);try{wake.Set();}catch(ObjectDisposedException){}
        }
        // Snapshot is synthetic diagnostics only; no current task/readiness claim.
        public bool TryCopyFakeBytes(out byte[] bytes,out bool isFaulted,out bool isRevoked,out string lastIssue)
        {
            bytes=null;isFaulted=false;isRevoked=false;lastIssue=null;
            if(!Owner()||!Monitor.TryEnter(gate))return false;
            try
            {
                if(Interlocked.CompareExchange(ref foreignFault,0,0)!=0)return false;
                bytes=sink.Copy();isFaulted=faulted;isRevoked=Interlocked.CompareExchange(ref lifecycle,0,0)==1;lastIssue=issue;return true;
            }
            finally{Monitor.Exit(gate);}
        }
        void Work()
        {
            try
            {
                while(true)
                {
                    lock(gate)
                    {
                        if(Interlocked.CompareExchange(ref foreignFault,0,0)!=0)Fault("owner_thread_violation");
                        if(Interlocked.CompareExchange(ref lifecycle,0,0)==1)CancelPending("revoked_before_write");
                        if(faulted||Interlocked.CompareExchange(ref lifecycle,0,0)==1){exited=true;wake.Dispose();return;}
                        if(pending.Count>0)
                        {
                            var item=pending.Dequeue();bool began=false;
                            try
                            {
                                if(acceptedFrames>=maximumFrames||!sink.HasCapacity(item.Payload.Length))
                                {completed.Enqueue(new ViveSyntheticWorkerAck(item.Sequence,item.Identity,ViveSyntheticWorkerOutcome.NotWritten,"fake_capacity_exhausted",null));Fault("fake_capacity_exhausted");continue;}
                                byte[] digest;using(var sha=SHA256.Create())digest=sha.ComputeHash(item.Payload);
                                if(Interlocked.CompareExchange(ref lifecycle,2,0)!=0)
                                {completed.Enqueue(new ViveSyntheticWorkerAck(item.Sequence,item.Identity,ViveSyntheticWorkerOutcome.NotWritten,"revoked_before_write",null));continue;}
                                began=true;
                                if(item.Plan==ViveSyntheticWorkerPlan.PartialThenFail)sink.AppendPrefix(item.Payload);
                                else sink.AppendWhole(item.Payload);
                                if(item.Plan!=ViveSyntheticWorkerPlan.Complete||Interlocked.CompareExchange(ref foreignFault,0,0)!=0||
                                    Interlocked.CompareExchange(ref lifecycle,0,2)!=2)
                                {completed.Enqueue(new ViveSyntheticWorkerAck(item.Sequence,item.Identity,ViveSyntheticWorkerOutcome.Uncertain,"fake_write_uncertain",digest));Fault("fake_write_uncertain");continue;}
                                acceptedFrames++;completed.Enqueue(new ViveSyntheticWorkerAck(item.Sequence,item.Identity,ViveSyntheticWorkerOutcome.Accepted,null,digest));
                            }
                            catch(Exception error) when(!Fatal(error))
                            {completed.Enqueue(new ViveSyntheticWorkerAck(item.Sequence,item.Identity,began?ViveSyntheticWorkerOutcome.Uncertain:ViveSyntheticWorkerOutcome.NotWritten,"fake_write_exception",null));Fault("fake_write_exception");}
                            continue;
                        }
                    }
                    wake.WaitOne();
                }
            }
            catch(Exception error) when(!Fatal(error))
            {lock(gate){Fault("worker_exception");if(!exited){exited=true;wake.Dispose();}}}
        }
        // No join, external callback, filesystem access or retry is performed.
        public void Dispose(){Revoke();}
    }
}
