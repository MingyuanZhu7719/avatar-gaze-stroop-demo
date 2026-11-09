#if VIVE_RAW_SYNTHETIC_TEST && !UNITY_5_3_OR_NEWER
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;

namespace AvatarStroop.Runtime
{
    // Synthetic-only experiment: producer is a nonblocking read tee over the
    // original same-handle strict scan. This is NOT connected to production.
    // A target stream here has no native identity or close guarantee.
    internal sealed class ViveSyntheticAsyncReadableCsv
    {
        internal enum Outcome { Pending, Candidate, Unknown }
        readonly ConcurrentQueue<byte[]> queue=new ConcurrentQueue<byte[]>();
        readonly AutoResetEvent wake=new AutoResetEvent(false);
        readonly ViveRawRevocation authority; readonly object claim=new object();
        readonly Stream target; readonly int capacity; readonly long maxOutput; readonly int maxRecords; readonly long maxInput;
        readonly Thread worker;
        // Single producer. Bit 0 is an in-flight Offer; bit 1 closes
        // admission. The worker waits only for an already admitted producer,
        // never the other way around, before draining and reporting exit.
        int queued,done,failed,matched,workerDone,converted,offerState,finishClaim; long inputOffered;
        string expectedDigest;
        internal Action<byte[]> AfterCopyBeforeEnqueueForTest;
        internal Action AfterFinishClaimForTest;
        internal bool AdmissionStoppedForTest {get{return (Volatile.Read(ref offerState)&2)!=0;}}
        internal int QueuedBytesForTest {get{return Volatile.Read(ref queued);}}
        internal bool WorkerExited {get{return Volatile.Read(ref workerDone)!=0;}}
        internal Outcome State {get {if(Volatile.Read(ref failed)!=0||authority.IsRevoked)return Outcome.Unknown;
            return WorkerExited&&Volatile.Read(ref converted)==1&&Volatile.Read(ref done)==1&&Volatile.Read(ref matched)==1?Outcome.Candidate:Outcome.Pending;}}
        internal ViveSyntheticAsyncReadableCsv(Stream output,ViveRawRevocation shared,int maxQueueBytes,long inputLimit,int recordLimit,long outputLimit)
        {
            if(output==null||shared==null||maxQueueBytes<=0||inputLimit<=0||recordLimit<=0||outputLimit<1024)throw new ArgumentException();
            authority=shared;target=output;capacity=maxQueueBytes;maxInput=inputLimit;maxRecords=recordLimit;maxOutput=outputLimit;
            if(!authority.TryBindPrivateStage(claim))throw new InvalidOperationException("Synthetic authority unavailable");
            worker=new Thread(Run){IsBackground=true,Name="Synthetic bounded CSV conversion"};worker.Start();
        }
        internal void Offer(byte[] buffer,int offset,int count)
        {
            if(count<=0||Volatile.Read(ref failed)!=0||Volatile.Read(ref done)!=0)return;
            int state=Interlocked.CompareExchange(ref offerState,1,0);
            if(state!=0){if((state&2)==0)Fail();return;}
            byte[] copy=null;bool reserved=false,enqueued=false;
            try
            {
                if(authority.PrivateStageStopped(claim)||Volatile.Read(ref failed)!=0){Fail();return;}
                if(!TryReserve(count)){Fail();return;}
                reserved=true;copy=new byte[count];Buffer.BlockCopy(buffer,offset,copy,0,count);
                var hook=AfterCopyBeforeEnqueueForTest;if(hook!=null)hook(copy);
                queue.Enqueue(copy);enqueued=true;inputOffered+=count;wake.Set();
            }
            catch(Exception e) when(!(e is StackOverflowException||e is AccessViolationException)){Fail();}
            finally
            {
                if(reserved&&!enqueued){Interlocked.Add(ref queued,-count);if(copy!=null)Array.Clear(copy,0,copy.Length);}
                Interlocked.Decrement(ref offerState);wake.Set();
            }
        }
        internal void Finish(ViveRawSessionReadResult scan,bool sourceMatched)
        {
            if(Interlocked.CompareExchange(ref finishClaim,1,0)!=0){Fail();return;}
            var hook=AfterFinishClaimForTest;if(hook!=null)hook();
            if((CloseOffers()&1)!=0){Fail();Volatile.Write(ref done,1);wake.Set();return;}
            if(sourceMatched&&scan!=null&&scan.Status==ViveRawSessionReadStatus.VerifiedPrefix&&!scan.TrailingFrameTruncated&&
                scan.CaptureTerminalObserved&&scan.VerifiedBytes==inputOffered&&scan.VerifiedRecordCount>0&&
                ValidDigest(scan.VerifiedPrefixChainDigestHex)&&!authority.IsRevoked&&Volatile.Read(ref failed)==0)
            {expectedDigest=scan.VerifiedPrefixChainDigestHex;Volatile.Write(ref matched,1);}
            else Fail();
            Volatile.Write(ref done,1);wake.Set();
        }
        static bool ValidDigest(string value)
        {if(value==null||value.Length!=64)return false;foreach(char c in value)if(!((c>='0'&&c<='9')||(c>='a'&&c<='f')))return false;return true;}
        internal static bool QueueFitsForTest(int current,int count,int limit)
        {return current>=0&&count>0&&limit>0&&current<=limit&&count<=limit-current;}
        bool TryReserve(int count)
        {while(true){int old=Volatile.Read(ref queued);if(!QueueFitsForTest(old,count,capacity))return false;
            if(Interlocked.CompareExchange(ref queued,old+count,old)==old)return true;}}
        int CloseOffers()
        {
            while(true){int old=Volatile.Read(ref offerState);if((old&2)!=0)return old;
                if(Interlocked.CompareExchange(ref offerState,old|2,old)==old)return old;}
        }
        void Fail(){CloseOffers();Interlocked.Exchange(ref failed,1);wake.Set();}
        void Run()
        {
            try
            {
                using(var input=new QueuedInput(this))using(var output=new ClaimedOutput(this,target))
                {
                    var result=ViveRawReadableAnalysisCsv.WriteSyntheticForTest(input,output,maxInput,maxRecords,maxOutput,CancellationToken.None);
                    // The worker's own strict scan validates this final chain
                    // digest; bind it to the independently scanned source.
                    if(result==ViveRawReadableAnalysisCsv.Outcome.WrittenAndFlushed&&
                        input.TrailerDigest==expectedDigest&&!authority.PrivateStageStopped(claim)&&Volatile.Read(ref failed)==0)
                        Volatile.Write(ref converted,1);
                    else Fail();
                }
            }
            catch(Exception e) when(!(e is StackOverflowException||e is AccessViolationException)){Fail();}
            finally
            {
                CloseOffers();
                // A producer already past admission may still enqueue after
                // failure. Do not drain or report worker exit until it leaves.
                while((Volatile.Read(ref offerState)&1)!=0)wake.WaitOne(20);
                byte[] b;while(queue.TryDequeue(out b)){Interlocked.Add(ref queued,-b.Length);Array.Clear(b,0,b.Length);}
                Volatile.Write(ref workerDone,1);
            }
        }
        sealed class QueuedInput:Stream
        {
            readonly ViveSyntheticAsyncReadableCsv owner; byte[] current;int at,tailCount;readonly byte[] tail=new byte[32];
            internal QueuedInput(ViveSyntheticAsyncReadableCsv value){owner=value;}
            internal string TrailerDigest {get{return tailCount==32?BitConverter.ToString(tail).Replace("-",String.Empty).ToLowerInvariant():null;}}
            void Remember(byte[] buffer,int offset,int count)
            {if(count>=32){Buffer.BlockCopy(buffer,offset+count-32,tail,0,32);tailCount=32;return;}
                int keep=Math.Min(tailCount,32-count);if(keep>0)Buffer.BlockCopy(tail,tailCount-keep,tail,0,keep);
                Buffer.BlockCopy(buffer,offset,tail,keep,count);tailCount=keep+count;}
            public override bool CanRead {get{return true;}} public override bool CanWrite {get{return false;}} public override bool CanSeek {get{return false;}}
            public override long Length {get{throw new NotSupportedException();}} public override long Position {get{throw new NotSupportedException();}set{throw new NotSupportedException();}}
            public override int Read(byte[] buffer,int offset,int count)
            {
                if(count==0)return 0;
                while(true)
                {
                    if(Volatile.Read(ref owner.failed)!=0||owner.authority.PrivateStageStopped(owner.claim))throw new IOException("Synthetic stage stopped");
                    if(current!=null){int n=Math.Min(count,current.Length-at);Buffer.BlockCopy(current,at,buffer,offset,n);Remember(buffer,offset,n);at+=n;
                        if(at==current.Length){Interlocked.Add(ref owner.queued,-current.Length);Array.Clear(current,0,current.Length);current=null;at=0;}return n;}
                    if(owner.queue.TryDequeue(out current))continue;
                    if(Volatile.Read(ref owner.done)!=0)return 0;
                    owner.wake.WaitOne(20);
                }
            }
            protected override void Dispose(bool disposing){if(current!=null){Interlocked.Add(ref owner.queued,-current.Length);Array.Clear(current,0,current.Length);current=null;}base.Dispose(disposing);}
            public override void Flush(){throw new NotSupportedException();}public override long Seek(long o,SeekOrigin s){throw new NotSupportedException();}
            public override void SetLength(long n){throw new NotSupportedException();} public override void Write(byte[] b,int o,int n){throw new NotSupportedException();}
        }
        sealed class ClaimedOutput:Stream
        {
            readonly ViveSyntheticAsyncReadableCsv owner;readonly Stream inner;
            internal ClaimedOutput(ViveSyntheticAsyncReadableCsv value,Stream target){owner=value;inner=target;}
            T Claim<T>(Func<T> operation){if(!owner.authority.TryBeginPrivateStageIo(owner.claim))throw new IOException("Withdrawal stopped I/O");
                try{if(owner.authority.PrivateStageStopped(owner.claim))throw new IOException("Withdrawal stopped I/O");return operation();}
                finally{owner.authority.EndPrivateStageIo(owner.claim);}}
            public override bool CanRead {get{return Claim(()=>inner.CanRead);}}public override bool CanWrite {get{return Claim(()=>inner.CanWrite);}}public override bool CanSeek {get{return Claim(()=>inner.CanSeek);}}
            public override long Length {get{return Claim(()=>inner.Length);}}public override long Position {get{return Claim(()=>inner.Position);}set{Claim(()=>{inner.Position=value;return true;});}}
            public override void Flush(){Claim(()=>{inner.Flush();return true;});}public override void Write(byte[] b,int o,int n){Claim(()=>{inner.Write(b,o,n);return true;});}
            public override int Read(byte[] b,int o,int n){throw new NotSupportedException();}public override long Seek(long o,SeekOrigin s){throw new NotSupportedException();}public override void SetLength(long n){throw new NotSupportedException();}
            // The caller retains the target handle and is responsible for close.
            protected override void Dispose(bool disposing){base.Dispose(disposing);}
        }
    }
}
#endif
