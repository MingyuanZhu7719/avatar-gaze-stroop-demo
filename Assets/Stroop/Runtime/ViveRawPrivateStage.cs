using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;

namespace AvatarStroop.Runtime
{
    // Optional, caller-owned private staging. This is neither an export nor
    // consent to publish: bytes may already have reached the stream when a
    // withdrawal is observed. The caller owns the private stream and must not
    // close or expose it while the background worker can still be using it.
    // Never start this on an experiment's stimulus/input thread.
    internal sealed class ViveRawPrivateStage
    {
        internal enum Outcome { Pending, PrivateCandidate, Unavailable, RevokedOrIoUnknown }
        // Bound both payload and per-frame object overhead. 256 entries would
        // cap typical eye samples at only tens of KiB, long before 4 MiB.
        const int MaximumQueuedBytes=4*1024*1024, MaximumQueuedFrames=16384;
        Stream target;readonly long maxBytes;readonly CancellationToken revoked;
        readonly ViveRawRevocation authority;readonly object authorityKey=new object();
        readonly ConcurrentQueue<byte[]> queue=new ConcurrentQueue<byte[]>();
        readonly AutoResetEvent wake=new AutoResetEvent(false);readonly Thread worker;
        readonly ViveRawExclusiveDestination installedOwner;
        readonly ViveNamedSessionFrameAdmission installedAdmission;
        Stream installedCsvTarget;
        Thread installedCsvCloseWorker;
        int installedCsvState,installedCsvCloseStarted,installedCsvCloseDone;
        // 0 deferred, 1 starting, 2 started, -1 failed. An unstarted worker
        // must never be mistaken for an exited, verified worker.
        int installedStartState;
        // The stage-local stop and an output operation claim compete in this
        // single word. A claim that wins first is already in flight even if
        // its OS call has not yet started. The shared authority also orders
        // each claim against participant withdrawal and technical revocation.
        const int IoStopped=1,IoClaimed=2;
        int queuedBytes,queuedFrames,lifecycle,finished,ioState,ioThread,withdrawalRequested,completed,outputStarted;long offeredBytes;
        ViveRawSessionReadResult source;bool sinkMatched;
#if VIVE_RAW_SYNTHETIC_TEST && VIVE_SYNTHETIC_BATCH_FIXTURE && !UNITY_5_3_OR_NEWER
        Func<ViveRawPrivateStage,Stream> syntheticAsyncTarget;
        Func<ViveRawPrivateStage,Stream> syntheticCsvTargetFactory;
        Func<bool> syntheticSourceClosed;
        Stream syntheticCsvTarget;
        long syntheticCsvMaximumBytes;
        int syntheticCsvState,syntheticCsvCloseStarted,syntheticCsvCloseDone,syntheticCsvCloseReturned;
        Thread syntheticCsvCloseWorker;
        internal Func<ThreadStart,Thread> SyntheticCsvCloseThreadFactoryForTest;
        // Synthetic-only: 0 pending, 1 converted (not closed), 2 closed,
        // -1 unknown. It conveys no authorization or persistence guarantee.
        internal int SyntheticCsvStateForTest {get{return authority.IsRevoked?-1:Volatile.Read(ref syntheticCsvState);}}
        internal bool SyntheticCsvCloseDoneForTest {get{return Volatile.Read(ref syntheticCsvCloseDone)!=0;}}
        internal bool SyntheticCsvCloseSucceededForTest {get{return SyntheticCsvCloseDoneForTest&&Volatile.Read(ref syntheticCsvCloseReturned)!=0&&!authority.IsRevoked;}}
        internal static ViveRawPrivateStage CreateSyntheticAsyncForTest(Func<ViveRawPrivateStage,Stream> factory,long maximumBytes,
            CancellationToken withdrawal,ViveRawRevocation authority)
        {return new ViveRawPrivateStage(factory,maximumBytes,withdrawal,authority,true);}
        internal static ViveRawPrivateStage CreateSyntheticCsvForTest(Func<ViveRawPrivateStage,Stream> rawFactory,
            Func<ViveRawPrivateStage,Stream> csvFactory,Func<bool> sourceClosed,long rawMaximumBytes,long csvMaximumBytes,
            CancellationToken withdrawal,ViveRawRevocation authority)
        {return new ViveRawPrivateStage(rawFactory,rawMaximumBytes,withdrawal,authority,true,csvFactory,sourceClosed,csvMaximumBytes);}
#endif
#if VIVE_RAW_SYNTHETIC_TEST && !UNITY_5_3_OR_NEWER
        internal Func<int,byte[]> AllocatorForTest;
        internal Action BeforeEnqueueForTest;
        internal Action BeforeIoClaimForTest,AfterIoClaimForTest;
        internal int QueuedBytesForTest {get{return Volatile.Read(ref queuedBytes);}}
        internal int QueuedFramesForTest {get{return Volatile.Read(ref queuedFrames);}}
        internal int PeakQueuedBytesForTest;
#endif

#if VIVE_RAW_SYNTHETIC_TEST && !UNITY_5_3_OR_NEWER
        // Standalone tests use a private authority; production callers must
        // supply the participant's existing shared authority explicitly.
        internal ViveRawPrivateStage(Stream privateTarget,long maximumBytes,CancellationToken withdrawal)
            :this(privateTarget,maximumBytes,withdrawal,new ViveRawRevocation()){}
#endif
        internal ViveRawPrivateStage(Stream privateTarget,long maximumBytes,CancellationToken withdrawal,ViveRawRevocation sharedAuthority)
            :this(()=>privateTarget,maximumBytes,withdrawal,sharedAuthority){}
        // A target factory permits the actual CreateNew operation to be
        // claimed by the same authority before any target is opened. The
        // caller is responsible for retaining any handle if the factory's
        // outcome becomes uncertain; no retry or automatic deletion occurs.
        internal ViveRawPrivateStage(Func<Stream> openTarget,long maximumBytes,CancellationToken withdrawal,ViveRawRevocation sharedAuthority)
            :this(openTarget==null?null:new Func<ViveRawPrivateStage,Stream>(_=>openTarget()),maximumBytes,withdrawal,sharedAuthority){}
        internal ViveRawPrivateStage(Func<ViveRawPrivateStage,Stream> openTarget,long maximumBytes,CancellationToken withdrawal,ViveRawRevocation sharedAuthority)
            :this(openTarget,maximumBytes,withdrawal,sharedAuthority,false){}
        // The destination must first take ownership of this exact stage, then
        // call StartInstalled. No target factory or worker is touched while
        // the production gate is closed, and binding is not consent to export.
        internal static ViveRawPrivateStage CreateInstalledDeferred(ViveRawExclusiveDestination owner,
            ViveNamedSessionFrameAdmission admission,long maximumBytes,CancellationToken withdrawal,ViveRawRevocation sharedAuthority)
        {
            if(owner==null||!owner.LifecycleAvailable||admission==null||
                sharedAuthority==null||!admission.IoFence.MatchesAuthority(admission,sharedAuthority)||
                maximumBytes<8||maximumBytes>ViveRawByteBudget.MaximumSupportedBytes||withdrawal.IsCancellationRequested||
                !owner.CanPrepareStage(admission,sharedAuthority))return null;
            return new ViveRawPrivateStage(owner,admission,maximumBytes,withdrawal,sharedAuthority);
        }
        ViveRawPrivateStage(ViveRawExclusiveDestination owner,ViveNamedSessionFrameAdmission admission,long maximumBytes,
            CancellationToken withdrawal,ViveRawRevocation sharedAuthority)
        {
            installedOwner=owner;installedAdmission=admission;maxBytes=maximumBytes;revoked=withdrawal;authority=sharedAuthority;
            if(!authority.TryBindPrivateStage(authorityKey))throw new InvalidOperationException("Private stage authority unavailable.");
            worker=new Thread(Consume){IsBackground=true,Name="VIVE private raw staging"};
        }
        internal bool StartInstalled(ViveRawExclusiveDestination owner)
        {
            if(owner==null||!owner.LifecycleAvailable||!Object.ReferenceEquals(installedOwner,owner)||
                !owner.CanStartStage(this,installedAdmission,authority)||authority.PrivateStageStopped(authorityKey)||
                revoked.IsCancellationRequested||Interlocked.CompareExchange(ref installedStartState,1,0)!=0)return false;
            try
            {
                if(authority.PrivateStageStopped(authorityKey)||revoked.IsCancellationRequested){Fail();Volatile.Write(ref installedStartState,-1);return false;}
                worker.Start();Volatile.Write(ref installedStartState,2);return true;
            }
            catch(OutOfMemoryException){Fail();Volatile.Write(ref installedStartState,-1);return false;}
            catch(Exception e) when(!Fatal(e)){Fail();Volatile.Write(ref installedStartState,-1);return false;}
        }
        ViveRawPrivateStage(Func<ViveRawPrivateStage,Stream> openTarget,long maximumBytes,CancellationToken withdrawal,ViveRawRevocation sharedAuthority,bool syntheticAsync
#if VIVE_RAW_SYNTHETIC_TEST && VIVE_SYNTHETIC_BATCH_FIXTURE && !UNITY_5_3_OR_NEWER
            ,Func<ViveRawPrivateStage,Stream> csvFactory=null,Func<bool> sourceClosed=null,long csvMaximumBytes=0
#endif
            )
        {
            // Bind the shared revocation authority before inspecting the
            // target. An already-open target is still the caller's exclusive
            // private responsibility; this does not authorize its creation.
            withdrawal.ThrowIfCancellationRequested();
            if(openTarget==null||sharedAuthority==null||maximumBytes<8||maximumBytes>ViveRawByteBudget.MaximumSupportedBytes)
                throw new ArgumentException("An empty, caller-owned seekable private staging stream and explicit bound are required.");
            maxBytes=maximumBytes;revoked=withdrawal;authority=sharedAuthority;
#if VIVE_RAW_SYNTHETIC_TEST && VIVE_SYNTHETIC_BATCH_FIXTURE && !UNITY_5_3_OR_NEWER
            if(csvFactory!=null&&(sourceClosed==null||csvMaximumBytes<1024))throw new ArgumentException("Synthetic CSV settings incomplete.");
            syntheticCsvTargetFactory=csvFactory;syntheticSourceClosed=sourceClosed;syntheticCsvMaximumBytes=csvMaximumBytes;
#endif
            if(!authority.TryBindPrivateStage(authorityKey))throw new InvalidOperationException("Private stage authority unavailable.");
            if(syntheticAsync)
            {
#if VIVE_RAW_SYNTHETIC_TEST && VIVE_SYNTHETIC_BATCH_FIXTURE && !UNITY_5_3_OR_NEWER
                syntheticAsyncTarget=openTarget;
                worker=new Thread(Consume){IsBackground=true,Name="VIVE private raw staging"};worker.Start();return;
#else
                throw new InvalidOperationException("Asynchronous target preparation is unavailable.");
#endif
            }
            InitializeTarget(openTarget);
            worker=new Thread(Consume){IsBackground=true,Name="VIVE private raw staging"};worker.Start();
        }
        void InitializeTarget(Func<ViveRawPrivateStage,Stream> openTarget)
        {
            Stream opened=null;
            TargetOperation(()=>opened=openTarget(this));
            target=opened;
            if(target==null)throw new InvalidOperationException("Private staging target unavailable.");
            var verifiedTarget=target as IViveRawStageTargetVerification;
            if(verifiedTarget!=null)
            {
                bool type=false,id=false,info=false;
                TargetOperation(()=>type=verifiedTarget.VerifyFileType());
                if(!type)throw new InvalidOperationException("Private staging file type unconfirmed.");
                TargetOperation(()=>id=verifiedTarget.VerifyFileIdentity());
                if(!id)throw new InvalidOperationException("Private staging file identity unconfirmed.");
                TargetOperation(()=>info=verifiedTarget.VerifyFileInformation());
                if(!info)throw new InvalidOperationException("Private staging file information unconfirmed.");
            }
            bool canRead=false,canWrite=false,canSeek=false;long initialLength=-1,initialPosition=-1;
            TargetOperation(()=>canRead=target.CanRead);
            TargetOperation(()=>canWrite=target.CanWrite);
            TargetOperation(()=>canSeek=target.CanSeek);
            if(!canRead||!canWrite||!canSeek)
                throw new ArgumentException("An empty, caller-owned seekable private staging stream and explicit bound are required.");
            TargetOperation(()=>initialLength=target.Length);
            TargetOperation(()=>initialPosition=target.Position);
            revoked.ThrowIfCancellationRequested();
            if(initialLength!=0||initialPosition!=0)
                throw new ArgumentException("An empty, caller-owned seekable private staging stream and explicit bound are required.");
        }
        static bool Fatal(Exception e){return e is OutOfMemoryException||e is StackOverflowException||e is AccessViolationException;}
        // lifecycle: 0 accepting, 1 abandoned, 2 verified private candidate.
        // Abandon after readiness also invalidates future State observations.
        void StopIo()
        {
            while(true){int old=Volatile.Read(ref ioState);if((old&IoStopped)!=0)return;
                if(Interlocked.CompareExchange(ref ioState,old|IoStopped,old)==old)return;}
        }
        void Fail(){StopIo();Interlocked.Exchange(ref lifecycle,1);wake.Set();}
        // This is an additional nonblocking stage-local stop. Participant
        // withdrawal and technical revoke also stop claims through authority.
        // Previously claimed I/O can finish and its result remains UNKNOWN.
        internal void RequestWithdrawal()
        {
            // The stop-bit CAS is the stage-local linearization point. The
            // reporting flag must not announce that boundary beforehand.
            StopIo();Interlocked.Exchange(ref withdrawalRequested,1);
            Interlocked.Exchange(ref lifecycle,1);wake.Set();
        }
        bool TryBeginIo()
        {
            if(revoked.IsCancellationRequested){RequestWithdrawal();return false;}
#if VIVE_RAW_SYNTHETIC_TEST && !UNITY_5_3_OR_NEWER
            var before=BeforeIoClaimForTest;if(before!=null)before();
#endif
            while(true)
            {
                int old=Volatile.Read(ref ioState);
                if((old&(IoStopped|IoClaimed))!=0)return false;
                if(Interlocked.CompareExchange(ref ioState,old|IoClaimed,old)!=old)continue;
                if(!authority.TryBeginPrivateStageIo(authorityKey)){EndLocalIo();return false;}
#if VIVE_RAW_SYNTHETIC_TEST && !UNITY_5_3_OR_NEWER
                var after=AfterIoClaimForTest;if(after!=null)after();
#endif
                // This additional check may avoid a call after cancellation,
                // but it does not turn a token into an atomic authority.
                if(revoked.IsCancellationRequested||authority.PrivateStageStopped(authorityKey)||(Volatile.Read(ref ioState)&IoStopped)!=0)
                {if(revoked.IsCancellationRequested)RequestWithdrawal();EndIo();return false;}
                Volatile.Write(ref ioThread,Thread.CurrentThread.ManagedThreadId);
                return true;
            }
        }
        void EndIo()
        {
            Volatile.Write(ref ioThread,0);
            authority.EndPrivateStageIo(authorityKey);
            EndLocalIo();
        }
        void EndLocalIo()
        {
            while(true){int old=Volatile.Read(ref ioState);
                if(Interlocked.CompareExchange(ref ioState,old&~IoClaimed,old)==old)return;}
        }
        // One producer: called only after a complete frame passes chain and
        // semantic validation. No stream I/O or waits occur on the scan thread.
        internal void OfferMagic(byte[] value){Offer(value,null,null);}
        internal void OfferFrame(byte[] prefix,byte[] payload,byte[] digest){Offer(prefix,payload,digest);}
        void Offer(byte[] first,byte[] second,byte[] third)
        {
            if(Volatile.Read(ref lifecycle)!=0||Volatile.Read(ref finished)!=0)return;
            if(revoked.IsCancellationRequested||authority.PrivateStageStopped(authorityKey)){Fail();return;}
            int size=0;byte[] copy=null;bool reserved=false,enqueued=false;
            try
            {
                if(first==null||(second==null)!=(third==null)){Fail();return;}
                size=checked(first.Length+(second==null?0:second.Length)+(third==null?0:third.Length));
                if(size<=0||size>MaximumQueuedBytes||offeredBytes>maxBytes-size){Fail();return;}
                int frames=Interlocked.Increment(ref queuedFrames);
                if(frames>MaximumQueuedFrames){Interlocked.Decrement(ref queuedFrames);Fail();return;}
                int bytes=Interlocked.Add(ref queuedBytes,size);
                if(bytes>MaximumQueuedBytes){Interlocked.Add(ref queuedBytes,-size);Interlocked.Decrement(ref queuedFrames);Fail();return;}
#if VIVE_RAW_SYNTHETIC_TEST && !UNITY_5_3_OR_NEWER
                if(bytes>PeakQueuedBytesForTest)PeakQueuedBytesForTest=bytes;
#endif
                reserved=true;
#if VIVE_RAW_SYNTHETIC_TEST && !UNITY_5_3_OR_NEWER
                copy=AllocatorForTest==null?new byte[size]:AllocatorForTest(size);
#else
                copy=new byte[size];
#endif
                int at=0;Buffer.BlockCopy(first,0,copy,at,first.Length);at+=first.Length;
                if(second!=null){Buffer.BlockCopy(second,0,copy,at,second.Length);at+=second.Length;Buffer.BlockCopy(third,0,copy,at,third.Length);}
#if VIVE_RAW_SYNTHETIC_TEST && !UNITY_5_3_OR_NEWER
                if(BeforeEnqueueForTest!=null)BeforeEnqueueForTest();
#endif
                queue.Enqueue(copy);enqueued=true;offeredBytes+=size;
                if(revoked.IsCancellationRequested||authority.PrivateStageStopped(authorityKey)||Volatile.Read(ref lifecycle)!=0)
                {Fail();DrainQueued();}else wake.Set();
            }
            catch(OutOfMemoryException){Fail();}
            catch(Exception e) when(!Fatal(e)){Fail();}
            finally
            {
                if(reserved&&!enqueued){Interlocked.Add(ref queuedBytes,-size);Interlocked.Decrement(ref queuedFrames);if(copy!=null)Array.Clear(copy,0,copy.Length);}
            }
        }
        void DrainQueued()
        {
            byte[] pending;while(queue.TryDequeue(out pending))
            {Interlocked.Add(ref queuedBytes,-pending.Length);Interlocked.Decrement(ref queuedFrames);Array.Clear(pending,0,pending.Length);}
        }
        // The sink owner calls this after its independent comparison of the
        // source scan against the writer's counts, terminal and chain digest.
        // matched is not identity, consent, a withdrawal fence or durability.
        internal void FinishScan(ViveRawSessionReadResult result,bool matched)
        {
            if(Interlocked.CompareExchange(ref finished,1,0)!=0){Fail();return;}
            try
            {
                source=result==null?null:new ViveRawSessionReadResult{Status=result.Status,TrailingFrameTruncated=result.TrailingFrameTruncated,
                    VerifiedBytes=result.VerifiedBytes,VerifiedRecordCount=result.VerifiedRecordCount,VerifiedTaskRecords=result.VerifiedTaskRecords,
                    VerifiedObservationRecords=result.VerifiedObservationRecords,VerifiedStatusRecords=result.VerifiedStatusRecords,
                    CaptureTerminalObserved=result.CaptureTerminalObserved,VerifiedPrefixChainDigestHex=result.VerifiedPrefixChainDigestHex};
                sinkMatched=matched;
            }
            catch(OutOfMemoryException){Fail();}
            // Publication of source/matched precedes the release store.
            Volatile.Write(ref completed,1);wake.Set();
        }
        internal Outcome State
        {
            get
            {
                if(revoked.IsCancellationRequested||Volatile.Read(ref withdrawalRequested)!=0||authority.PrivateStageStopped(authorityKey))return Outcome.RevokedOrIoUnknown;
                int life=Volatile.Read(ref lifecycle);
                if(installedOwner!=null&&Volatile.Read(ref installedStartState)>=0&&Volatile.Read(ref installedStartState)<2)
                    return life==1?Outcome.Unavailable:Outcome.Pending;
                if(worker.IsAlive)return life==1||(Volatile.Read(ref ioState)&IoStopped)!=0?Outcome.RevokedOrIoUnknown:Outcome.Pending;
                if(life==2&&(Volatile.Read(ref ioState)&IoStopped)==0&&!revoked.IsCancellationRequested)return Outcome.PrivateCandidate;
                return Volatile.Read(ref outputStarted)!=0?Outcome.RevokedOrIoUnknown:Outcome.Unavailable;
            }
        }
        // Only indicates the worker has returned; the caller still owns the
        // target. Until then an OS call can remain in flight indefinitely.
        internal bool WorkerExited {get{return (installedOwner==null||Volatile.Read(ref installedStartState)<0||Volatile.Read(ref installedStartState)==2)&&!worker.IsAlive;}}
        internal bool IsTargetWorkerThread {get{return Object.ReferenceEquals(Thread.CurrentThread,worker);}}
        internal bool StageIoInFlight {get{return (Volatile.Read(ref ioState)&IoClaimed)!=0;}}
        internal bool HasCurrentTargetClaim(ViveNamedSessionFrameAdmission expected,ViveRawIoFence fence)
        {return expected!=null&&fence!=null&&fence.MatchesAuthority(expected,authority)&&
            Volatile.Read(ref ioThread)==Thread.CurrentThread.ManagedThreadId&&(Volatile.Read(ref ioState)&(IoClaimed|IoStopped))==IoClaimed&&
            !authority.PrivateStageStopped(authorityKey);}
        int targetCloseAttempted,targetCloseReturned;
        // Only a dedicated destination owner may invoke this after verifying
        // its exact sink and completed drain. Closing can expose the file and
        // can perform I/O, so it competes with the same withdrawal authority.
        // A failed or raced close is UNKNOWN and is never retried here.
        internal bool TryCloseTargetAfterCandidate()
        {
            // Installed targets can be closed only through the exact owner's
            // nonblocking path, never this caller-owned stream helper.
            if(installedOwner!=null||!WorkerExited||State!=Outcome.PrivateCandidate||Interlocked.CompareExchange(ref targetCloseAttempted,1,0)!=0)return false;
            try{TargetOperation(()=>target.Dispose());Volatile.Write(ref targetCloseReturned,1);}
            catch(Exception e) when(!Fatal(e)){return false;}
            return !revoked.IsCancellationRequested&&!authority.PrivateStageStopped(authorityKey)&&(Volatile.Read(ref ioState)&IoStopped)==0;
        }
        internal bool TargetCloseReturned {get{return Volatile.Read(ref targetCloseReturned)!=0;}}
        Thread installedCloseWorker;
        internal bool IsIdentityCloseWorker(bool csv)
        {return Object.ReferenceEquals(Thread.CurrentThread,csv?installedCsvCloseWorker:installedCloseWorker);}
        void InstalledIdentitySteps(bool csv,int first,int last)
        {
            for(int index=first;index<=last;index++)
            {
                var step=(ViveRawRelativeFileCapability.IdentityStep)index;bool success=false;
                TargetOperation(()=>success=installedOwner.TryIdentityStep(this,csv,step));
                if(!success||revoked.IsCancellationRequested||authority.PrivateStageStopped(authorityKey)||
                    (Volatile.Read(ref ioState)&IoStopped)!=0)throw new IOException("Output identity verification unconfirmed.");
            }
        }
        int installedCloseDone,installedCloseSuccess;
        // Only the exact destination owner may request one background close.
        // A pending or raced call is not a publication or durability receipt.
        internal bool StartInstalledTargetClose(ViveRawExclusiveDestination owner)
        {
            if(owner==null||!owner.LifecycleAvailable||!Object.ReferenceEquals(owner,installedOwner)||
                !owner.CanCloseStage(this,installedAdmission,authority)||!WorkerExited||State!=Outcome.PrivateCandidate||
                Interlocked.CompareExchange(ref targetCloseAttempted,1,0)!=0)return false;
            try
            {
                installedCloseWorker=new Thread(InstalledClose){IsBackground=true,Name="VIVE private target close"};
                installedCloseWorker.Start();return true;
            }
            catch(OutOfMemoryException){Fail();Volatile.Write(ref installedCloseDone,1);return false;}
            catch(Exception e) when(!Fatal(e)){Fail();Volatile.Write(ref installedCloseDone,1);return false;}
        }
        void InstalledClose()
        {
            try
            {
                if(!Object.ReferenceEquals(Thread.CurrentThread,installedCloseWorker))return;
                InstalledIdentitySteps(false,0,4);
                TargetOperation(()=>target.Dispose());Volatile.Write(ref targetCloseReturned,1);
                InstalledIdentitySteps(false,5,11);
                if(!revoked.IsCancellationRequested&&!authority.PrivateStageStopped(authorityKey)&&
                    (Volatile.Read(ref ioState)&IoStopped)==0)Volatile.Write(ref installedCloseSuccess,1);
            }
            catch(OutOfMemoryException){Fail();}
            catch(Exception e) when(!Fatal(e)){Fail();}
            finally{Volatile.Write(ref installedCloseDone,1);}
        }
        internal bool InstalledCloseDone {get{return Volatile.Read(ref installedCloseDone)!=0;}}
        internal bool InstalledCloseSucceeded
        {get{return InstalledCloseDone&&Volatile.Read(ref installedCloseSuccess)!=0&&!revoked.IsCancellationRequested&&
            !authority.PrivateStageStopped(authorityKey)&&(Volatile.Read(ref ioState)&IoStopped)==0;}}
        // A report claim is a one-at-a-time ordering point against the same
        // withdrawal authority as target I/O. It does not re-open the path or
        // recall a candidate already handed to its caller.
        internal bool TryBeginInstalledReport(ViveRawExclusiveDestination owner)
        {return owner!=null&&owner.LifecycleAvailable&&Object.ReferenceEquals(owner,installedOwner)&&
            InstalledCloseSucceeded&&TryBeginIo();}
        internal bool InstalledReportClaimLive(ViveRawExclusiveDestination owner)
        {return Object.ReferenceEquals(owner,installedOwner)&&Volatile.Read(ref ioThread)==Thread.CurrentThread.ManagedThreadId&&
            (Volatile.Read(ref ioState)&(IoStopped|IoClaimed))==IoClaimed&&!authority.PrivateStageStopped(authorityKey)&&
            !revoked.IsCancellationRequested;}
        internal bool TryGetInstalledVerifiedSummary(ViveRawExclusiveDestination owner,out long bytes,out string chainDigest)
        {
            bytes=0;chainDigest=null;var value=source;
            if(!InstalledReportClaimLive(owner)||!InstalledCloseSucceeded||value==null||
                value.Status!=ViveRawSessionReadStatus.VerifiedPrefix||value.TrailingFrameTruncated||
                !value.CaptureTerminalObserved||value.VerifiedBytes<8||String.IsNullOrEmpty(value.VerifiedPrefixChainDigestHex))return false;
            bytes=value.VerifiedBytes;chainDigest=value.VerifiedPrefixChainDigestHex;return true;
        }
        internal void EndInstalledReport(ViveRawExclusiveDestination owner)
        {if(Object.ReferenceEquals(owner,installedOwner)&&Volatile.Read(ref ioThread)==Thread.CurrentThread.ManagedThreadId)EndIo();}
#if VIVE_RAW_SYNTHETIC_TEST && VIVE_SYNTHETIC_BATCH_FIXTURE && !UNITY_5_3_OR_NEWER
        // The fixture's directory owner must not wait inside Dispose. A
        // claimed close may still be in flight after withdrawal or timeout;
        // neither the target nor directory can then be claimed closed.
        Thread syntheticCloseWorker;
        int syntheticCloseDone,syntheticCloseSuccess;
        internal bool StartSyntheticTargetClose(ViveRawSessionJournalSink owner)
        {
            if(owner==null||!owner.MayStartSyntheticStageClose(this))return false;
            return StartSyntheticTargetCloseCore();
        }
        // In-memory fault injection exercises the identical close worker;
        // it does not exercise the sink's separate owner-binding check.
        internal bool StartSyntheticTargetCloseForTest()
        {return StartSyntheticTargetCloseCore();}
        bool StartSyntheticTargetCloseCore()
        {
            if(!WorkerExited||State!=Outcome.PrivateCandidate||Interlocked.CompareExchange(ref targetCloseAttempted,1,0)!=0)return false;
            try
            {
                syntheticCloseWorker=new Thread(SyntheticClose){IsBackground=true,Name="VIVE synthetic target close"};
                syntheticCloseWorker.Start();return true;
            }
            catch(OutOfMemoryException){Fail();Volatile.Write(ref syntheticCloseDone,1);return false;}
            catch(Exception e) when(!Fatal(e)){Fail();Volatile.Write(ref syntheticCloseDone,1);return false;}
        }
        void SyntheticClose()
        {
            try
            {
                if(!Object.ReferenceEquals(Thread.CurrentThread,syntheticCloseWorker))return;
                TargetOperation(()=>target.Dispose());
                Volatile.Write(ref targetCloseReturned,1);
                if(!revoked.IsCancellationRequested&&!authority.PrivateStageStopped(authorityKey)&&
                    (Volatile.Read(ref ioState)&IoStopped)==0)Volatile.Write(ref syntheticCloseSuccess,1);
            }
            catch(OutOfMemoryException){Fail();}
            catch(Exception e) when(!Fatal(e)){Fail();}
            finally{Volatile.Write(ref syntheticCloseDone,1);}
        }
        internal bool SyntheticCloseDone {get{return Volatile.Read(ref syntheticCloseDone)!=0;}}
        internal bool SyntheticCloseSucceeded
        {get{return SyntheticCloseDone&&Volatile.Read(ref syntheticCloseSuccess)!=0&&!revoked.IsCancellationRequested&&
            !authority.PrivateStageStopped(authorityKey)&&(Volatile.Read(ref ioState)&IoStopped)==0;}}
        internal bool StartSyntheticCsvCloseForTest(ViveRawSessionJournalSink owner)
        {
            if(owner==null||!owner.MayStartSyntheticStageClose(this))return false;
            return StartSyntheticCsvCloseCore();
        }
        internal bool StartSyntheticCsvCloseForTest(){return StartSyntheticCsvCloseCore();}
        bool StartSyntheticCsvCloseCore()
        {
            if(syntheticCsvTarget==null||!WorkerExited||Volatile.Read(ref syntheticCsvState)!=1||
                Interlocked.CompareExchange(ref syntheticCsvCloseStarted,1,0)!=0)return false;
            try{var factory=SyntheticCsvCloseThreadFactoryForTest;
                syntheticCsvCloseWorker=factory==null?new Thread(SyntheticCsvClose):factory(SyntheticCsvClose);
                if(syntheticCsvCloseWorker==null)throw new InvalidOperationException("Synthetic close worker unavailable.");
                syntheticCsvCloseWorker.IsBackground=true;syntheticCsvCloseWorker.Name="VIVE synthetic CSV close";syntheticCsvCloseWorker.Start();return true;}
            catch(OutOfMemoryException){Volatile.Write(ref syntheticCsvState,-1);Volatile.Write(ref syntheticCsvCloseDone,1);return false;}
            catch(Exception error) when(!Fatal(error)){Volatile.Write(ref syntheticCsvState,-1);Volatile.Write(ref syntheticCsvCloseDone,1);return false;}
        }
        void SyntheticCsvClose()
        {
            try
            {
                if(!Object.ReferenceEquals(Thread.CurrentThread,syntheticCsvCloseWorker))return;
                TargetOperation(()=>syntheticCsvTarget.Dispose());
                if(!revoked.IsCancellationRequested&&!authority.PrivateStageStopped(authorityKey)&&(Volatile.Read(ref ioState)&IoStopped)==0)
                {Volatile.Write(ref syntheticCsvCloseReturned,1);Volatile.Write(ref syntheticCsvState,2);}
                else Volatile.Write(ref syntheticCsvState,-1);
            }
            catch(OutOfMemoryException){Volatile.Write(ref syntheticCsvState,-1);}
            catch(Exception error) when(!Fatal(error)){Volatile.Write(ref syntheticCsvState,-1);}
            finally{Volatile.Write(ref syntheticCsvCloseDone,1);}
        }
#endif
        // These stream adapters issue every read and write under the same
        // participant-bound claim as the raw stage, including cancellation.
        sealed class SyntheticCsvOutput:Stream
        {
            readonly ViveRawPrivateStage owner;readonly Stream inner;
            internal SyntheticCsvOutput(ViveRawPrivateStage stage,Stream output){owner=stage;inner=output;}
            T Invoke<T>(Func<T> operation){T result=default(T);owner.TargetOperation(()=>result=operation());return result;}
            public override bool CanRead {get{return Invoke(()=>inner.CanRead);}}public override bool CanWrite {get{return Invoke(()=>inner.CanWrite);}}
            public override bool CanSeek {get{return Invoke(()=>inner.CanSeek);}}public override long Length {get{return Invoke(()=>inner.Length);}}
            public override long Position {get{return Invoke(()=>inner.Position);}set{Invoke(()=>{inner.Position=value;return true;});}}
            public override void Write(byte[] buffer,int offset,int count){Invoke(()=>{inner.Write(buffer,offset,count);return true;});}
            public override void Flush(){Invoke(()=>{inner.Flush();return true;});}
            public override int Read(byte[] b,int o,int n){throw new NotSupportedException();}
            public override long Seek(long o,SeekOrigin s){throw new NotSupportedException();}public override void SetLength(long value){throw new NotSupportedException();}
        }
        sealed class SyntheticCsvSource:Stream
        {
            readonly Stream inner;readonly byte[] tail=new byte[32];int tailCount;internal long ReadBytes;
            internal SyntheticCsvSource(ViveRawPrivateStage stage,Stream source){inner=new ReadOnly(stage,source);}
            internal string Digest {get{return tailCount==32?BitConverter.ToString(tail).Replace("-",String.Empty).ToLowerInvariant():null;}}
            public override int Read(byte[] buffer,int offset,int count)
            {
                int n=inner.Read(buffer,offset,count);ReadBytes+=n;if(n<=0)return n;
                if(n>=32){Buffer.BlockCopy(buffer,offset+n-32,tail,0,32);tailCount=32;}
                else {int keep=Math.Min(tailCount,32-n);if(keep>0)Buffer.BlockCopy(tail,tailCount-keep,tail,0,keep);
                    Buffer.BlockCopy(buffer,offset,tail,keep,n);tailCount=keep+n;}
                return n;
            }
            public override bool CanRead {get{return true;}}public override bool CanWrite {get{return false;}}public override bool CanSeek {get{return false;}}
            public override long Length {get{throw new NotSupportedException();}}public override long Position {get{throw new NotSupportedException();}set{throw new NotSupportedException();}}
            public override void Flush(){throw new NotSupportedException();}public override long Seek(long o,SeekOrigin s){throw new NotSupportedException();}
            public override void SetLength(long n){throw new NotSupportedException();}public override void Write(byte[] b,int o,int n){throw new NotSupportedException();}
        }
#if VIVE_RAW_SYNTHETIC_TEST && VIVE_SYNTHETIC_BATCH_FIXTURE && !UNITY_5_3_OR_NEWER
        bool ConvertSyntheticCsv(ViveRawSessionReadResult expected)
        {
            bool ok=ConvertCsvCore(expected,true);
            Volatile.Write(ref syntheticCsvState,ok?1:-1);return ok;
        }
#endif
        bool ConvertInstalledCsv(ViveRawSessionReadResult expected)
        {
            bool ok=ConvertCsvCore(expected,false);
            Volatile.Write(ref installedCsvState,ok?1:-1);return ok;
        }
        // Both variants run exactly the same identity checks, bounded stream
        // adapters and source digest comparison. Only the closed installed
        // owner may supply production authority; test delegates stay compiled
        // out of a normal Unity player.
        bool ConvertCsvCore(ViveRawSessionReadResult expected,bool synthetic)
        {
            try
            {
                Func<bool> sourceClosed;
                Func<ViveRawPrivateStage,Stream> open;
                long csvLimit;
#if VIVE_RAW_SYNTHETIC_TEST && VIVE_SYNTHETIC_BATCH_FIXTURE && !UNITY_5_3_OR_NEWER
                if(synthetic){sourceClosed=syntheticSourceClosed;open=syntheticCsvTargetFactory;csvLimit=syntheticCsvMaximumBytes;}
                else
#endif
                {if(synthetic||installedOwner==null||!installedOwner.CsvRequested)return false;
                    sourceClosed=()=>installedOwner.SourceCloseValidated(this);open=stage=>installedOwner.OpenCsvTargetForStage(stage);
                    csvLimit=installedOwner.CsvMaximumBytes;}
                if(sourceClosed==null||open==null)return false;
                while(!sourceClosed())
                {if(revoked.IsCancellationRequested||authority.PrivateStageStopped(authorityKey)||Volatile.Read(ref lifecycle)!=0)return false;wake.WaitOne(20);}
                if(revoked.IsCancellationRequested||authority.PrivateStageStopped(authorityKey)||Volatile.Read(ref lifecycle)!=0)return false;
                Stream outputTarget=null;
                TargetOperation(()=>{
                    outputTarget=open(this);
#if VIVE_RAW_SYNTHETIC_TEST && VIVE_SYNTHETIC_BATCH_FIXTURE && !UNITY_5_3_OR_NEWER
                    if(synthetic)syntheticCsvTarget=outputTarget;else
#endif
                    installedCsvTarget=outputTarget;
                });
                var verified=outputTarget as IViveRawStageTargetVerification;if(verified==null)return false;
                bool type=false,id=false,info=false;
                TargetOperation(()=>type=verified.VerifyFileType());if(!type)return false;
                TargetOperation(()=>id=verified.VerifyFileIdentity());if(!id)return false;
                TargetOperation(()=>info=verified.VerifyFileInformation());if(!info)return false;
                using(var input=new SyntheticCsvSource(this,target))using(var output=new SyntheticCsvOutput(this,outputTarget))
                {
                    ViveRawReadableAnalysisCsv.Outcome result;
#if VIVE_RAW_SYNTHETIC_TEST && VIVE_SYNTHETIC_BATCH_FIXTURE && !UNITY_5_3_OR_NEWER
                    if(synthetic||(installedOwner!=null&&installedOwner.IsIntegrationFixture))
                        result=ViveRawReadableAnalysisCsv.WriteSyntheticForTest(input,output,maxBytes,25000000,csvLimit,revoked);
                    else
#endif
                    result=ViveRawReadableAnalysisCsv.Write(input,output,maxBytes,25000000,csvLimit,revoked);
                    if(result!=ViveRawReadableAnalysisCsv.Outcome.WrittenAndFlushed||input.ReadBytes!=expected.VerifiedBytes||
                        !String.Equals(input.Digest,expected.VerifiedPrefixChainDigestHex,StringComparison.Ordinal)||
                        authority.PrivateStageStopped(authorityKey)||revoked.IsCancellationRequested)return false;
                }
                return true;
            }
            catch(OutOfMemoryException){return false;}
            catch(Exception error) when(!Fatal(error)){return false;}
        }
        internal bool StartInstalledCsvClose(ViveRawExclusiveDestination owner)
        {
            if(owner==null||!owner.LifecycleAvailable||!Object.ReferenceEquals(owner,installedOwner)||
                !owner.CanCloseStage(this,installedAdmission,authority)||installedCsvTarget==null||!WorkerExited||
                Volatile.Read(ref installedCsvState)!=1||Interlocked.CompareExchange(ref installedCsvCloseStarted,1,0)!=0)return false;
            try{installedCsvCloseWorker=new Thread(InstalledCsvClose){IsBackground=true,Name="VIVE private CSV close"};
                installedCsvCloseWorker.Start();return true;}
            catch(OutOfMemoryException){Volatile.Write(ref installedCsvState,-1);Volatile.Write(ref installedCsvCloseDone,1);return false;}
            catch(Exception error) when(!Fatal(error)){Volatile.Write(ref installedCsvState,-1);Volatile.Write(ref installedCsvCloseDone,1);return false;}
        }
        void InstalledCsvClose()
        {
            try
            {
                if(!Object.ReferenceEquals(Thread.CurrentThread,installedCsvCloseWorker))return;
                InstalledIdentitySteps(true,0,4);
                TargetOperation(()=>installedCsvTarget.Dispose());
                InstalledIdentitySteps(true,5,11);
                Volatile.Write(ref installedCsvState,!revoked.IsCancellationRequested&&!authority.PrivateStageStopped(authorityKey)&&
                    (Volatile.Read(ref ioState)&IoStopped)==0?2:-1);
            }
            catch(OutOfMemoryException){Volatile.Write(ref installedCsvState,-1);}
            catch(Exception error) when(!Fatal(error)){Volatile.Write(ref installedCsvState,-1);}
            finally{Volatile.Write(ref installedCsvCloseDone,1);}
        }
        internal bool InstalledCsvCloseDone {get{return Volatile.Read(ref installedCsvCloseDone)!=0;}}
        internal bool InstalledCsvCloseSucceeded {get{return InstalledCsvCloseDone&&Volatile.Read(ref installedCsvState)==2&&
            !revoked.IsCancellationRequested&&!authority.PrivateStageStopped(authorityKey)&&(Volatile.Read(ref ioState)&IoStopped)==0;}}
        // Abandoning never waits for a target call already in flight.
        internal void Abandon(){Fail();}
        sealed class ReadOnly:Stream
        {
            readonly ViveRawPrivateStage owner;readonly Stream inner;
            internal ReadOnly(ViveRawPrivateStage stage,Stream value){owner=stage;inner=value;}
            public override bool CanRead {get{return true;}} public override bool CanSeek {get{return false;}} public override bool CanWrite {get{return false;}}
            public override long Length {get{throw new NotSupportedException();}} public override long Position {get{throw new NotSupportedException();}set{throw new NotSupportedException();}}
            public override int Read(byte[] b,int o,int n)
            {if(!owner.TryBeginIo())throw new IOException("Private staging I/O stopped.");
                try{return inner.Read(b,o,n);}finally{owner.EndIo();}}
            public override void Flush(){throw new NotSupportedException();}
            public override long Seek(long o,SeekOrigin s){throw new NotSupportedException();} public override void SetLength(long v){throw new NotSupportedException();}
            public override void Write(byte[] b,int o,int n){throw new NotSupportedException();}
        }
        static bool Same(ViveRawSessionReadResult a,ViveRawSessionReadResult b)
        {return b!=null&&b.Status==ViveRawSessionReadStatus.VerifiedPrefix&&!b.TrailingFrameTruncated&&
            a.VerifiedBytes==b.VerifiedBytes&&a.VerifiedRecordCount==b.VerifiedRecordCount&&a.VerifiedTaskRecords==b.VerifiedTaskRecords&&
            a.VerifiedObservationRecords==b.VerifiedObservationRecords&&a.VerifiedStatusRecords==b.VerifiedStatusRecords&&
            a.CaptureTerminalObserved==b.CaptureTerminalObserved&&String.Equals(a.VerifiedPrefixChainDigestHex,b.VerifiedPrefixChainDigestHex,StringComparison.Ordinal);}
        void TargetOperation(Action action)
        {
            if(!TryBeginIo())throw new IOException("Private staging I/O stopped.");
            try{action();}finally{EndIo();}
        }
        void Consume()
        {
            try
            {
                if(installedOwner!=null)InitializeTarget(stage=>installedOwner.OpenTargetForStage(stage));
#if VIVE_RAW_SYNTHETIC_TEST && VIVE_SYNTHETIC_BATCH_FIXTURE && !UNITY_5_3_OR_NEWER
                if(syntheticAsyncTarget!=null)InitializeTarget(syntheticAsyncTarget);
#endif
                while(true)
                {
                    if(revoked.IsCancellationRequested||authority.PrivateStageStopped(authorityKey)||Volatile.Read(ref lifecycle)!=0)break;
                    byte[] bytes;
                    if(queue.TryDequeue(out bytes))
                    {
                        Interlocked.Add(ref queuedBytes,-bytes.Length);Interlocked.Decrement(ref queuedFrames);
                        if(revoked.IsCancellationRequested||authority.PrivateStageStopped(authorityKey)||Volatile.Read(ref lifecycle)!=0){Array.Clear(bytes,0,bytes.Length);break;}
                        if(!TryBeginIo()){Array.Clear(bytes,0,bytes.Length);break;}
                        try{
                            Interlocked.Exchange(ref outputStarted,1);target.Write(bytes,0,bytes.Length);
                        }finally{EndIo();Array.Clear(bytes,0,bytes.Length);}
                        if(revoked.IsCancellationRequested||authority.PrivateStageStopped(authorityKey)||Volatile.Read(ref lifecycle)!=0)break;
                        continue;
                    }
                    if(Volatile.Read(ref completed)==1)break;
                    wake.WaitOne(50);
                }
                var scan=source;
                if(revoked.IsCancellationRequested||authority.PrivateStageStopped(authorityKey)||Volatile.Read(ref lifecycle)!=0||Volatile.Read(ref completed)!=1||!sinkMatched||
                    scan==null||scan.Status!=ViveRawSessionReadStatus.VerifiedPrefix||scan.TrailingFrameTruncated||
                    !scan.CaptureTerminalObserved||scan.VerifiedBytes!=offeredBytes) {Volatile.Write(ref completed,3);return;}
                TargetOperation(()=>target.Flush());
                long targetLength=0;TargetOperation(()=>targetLength=target.Length);
                if(revoked.IsCancellationRequested||authority.PrivateStageStopped(authorityKey)||Volatile.Read(ref lifecycle)!=0||targetLength!=scan.VerifiedBytes){Volatile.Write(ref completed,3);return;}
                TargetOperation(()=>target.Position=0);
                var checkedCopy=ViveRawSessionJournalReader.Scan(new ReadOnly(this,target),true,true,maxBytes,25000000,revoked);
                if(revoked.IsCancellationRequested||authority.PrivateStageStopped(authorityKey)||Volatile.Read(ref lifecycle)!=0||!Same(scan,checkedCopy)){Volatile.Write(ref completed,3);return;}
                TargetOperation(()=>target.Position=0);
#if VIVE_RAW_SYNTHETIC_TEST && VIVE_SYNTHETIC_BATCH_FIXTURE && !UNITY_5_3_OR_NEWER
                // CSV takes another single pass of the same raw handle only
                // after confirmed source close and its seal-boundary check.
                if(syntheticCsvTargetFactory!=null&&!ConvertSyntheticCsv(scan))
                {Volatile.Write(ref syntheticCsvState,-1);Volatile.Write(ref completed,3);return;}
#endif
                if(installedOwner!=null&&installedOwner.CsvRequested&&!ConvertInstalledCsv(scan))
                {Volatile.Write(ref installedCsvState,-1);Volatile.Write(ref completed,3);return;}
                if(revoked.IsCancellationRequested||authority.PrivateStageStopped(authorityKey)||(Volatile.Read(ref ioState)&IoStopped)!=0||Interlocked.CompareExchange(ref lifecycle,2,0)!=0)Volatile.Write(ref completed,3);
                else Volatile.Write(ref completed,2);
            }
            catch(OutOfMemoryException){Interlocked.Exchange(ref lifecycle,1);Volatile.Write(ref completed,3);}
            catch(Exception e) when(!Fatal(e)){Interlocked.Exchange(ref lifecycle,1);Volatile.Write(ref completed,3);}
            finally
            {
                DrainQueued();
                // The wake handle remains owned by the object until collection;
                // disposing it here could race the producer's last Offer.
            }
        }
    }
}
