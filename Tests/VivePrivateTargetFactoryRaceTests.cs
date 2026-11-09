#if VIVE_RAW_SYNTHETIC_TEST && !UNITY_5_3_OR_NEWER
using System;
using System.IO;
using System.Threading;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    // In-memory checks of the shared authority at the target lifecycle boundary.
    // These do not model filesystem creation, publication or durability.
    public static class VivePrivateTargetFactoryRaceTests
    {
        sealed class PropertyProbe:MemoryStream
        {
            internal int Inspections;
            public override bool CanRead {get{Interlocked.Increment(ref Inspections);return base.CanRead;}}
            public override bool CanWrite {get{Interlocked.Increment(ref Inspections);return base.CanWrite;}}
            public override bool CanSeek {get{Interlocked.Increment(ref Inspections);return base.CanSeek;}}
            public override long Length {get{Interlocked.Increment(ref Inspections);return base.Length;}}
            public override long Position {get{Interlocked.Increment(ref Inspections);return base.Position;}set{base.Position=value;}}
        }
        sealed class BlockedClose:MemoryStream
        {
            internal readonly ManualResetEvent Entered=new ManualResetEvent(false),Release=new ManualResetEvent(false);
            internal int Calls;
            protected override void Dispose(bool disposing)
            {
                if(disposing){Interlocked.Increment(ref Calls);Entered.Set();Release.WaitOne();}
                base.Dispose(disposing);
            }
        }
        public static void Register(TestSuite suite)
        {
            suite.Run("installed local destination gate refuses a request without touching authority",()=>{
                var shared=new ViveRawRevocation();ViveRawExclusiveDestination destination;
                Check.True(!ViveRawExclusiveDestination.ProductionGateOpen);
                Check.True(!ViveRawExclusiveDestination.TryCreateInstalled(null,null,shared,"C:\\synthetic",1024,1000,true,out destination));
                Check.True(destination==null&&shared.MayReserve&&!shared.ParticipantWithdrawalRequested);
            });
            suite.Run("installed CSV gate refuses explicit request without consuming authority",()=>{
                var shared=new ViveRawRevocation();ViveRawExclusiveDestination destination;
                Check.True(!ViveRawExclusiveDestination.ProductionGateOpen);
                Check.True(!ViveRawRelativeFileCapability.ProductionGateOpen);
                Check.True(!ViveRawReadableAnalysisCsv.ProductionGateOpen);
                Check.True(!ViveRawExclusiveDestination.TryCreateInstalledWithCsv(null,null,shared,"C:\\synthetic",1024,1000,4096,1000,true,out destination));
                Check.True(destination==null&&shared.MayReserve&&!shared.ParticipantWithdrawalRequested);
            });
            suite.Run("local candidate never claims reopened identity or durability",()=>{
                var shared=new ViveRawRevocation();
                var candidate=new ViveRawExclusiveDestination.LocalCandidateReceipt(shared,"unverified",1,"synthetic-id",8,"synthetic-chain");
                Check.True(candidate.WithdrawalNotObserved&&!candidate.ReopenedIdentityVerified&&!candidate.Durable);
                shared.RevokeForParticipant();
                Check.True(!candidate.WithdrawalNotObserved&&!candidate.ReopenedIdentityVerified&&!candidate.Durable);
            });
            suite.Run("participant withdrawal before target factory prevents factory call",()=>{
                var shared=new ViveRawRevocation();shared.RevokeForParticipant();int calls=0;
                Check.Throws<InvalidOperationException>(()=>new ViveRawPrivateStage(()=>{Interlocked.Increment(ref calls);return new MemoryStream();},1024,CancellationToken.None,shared));
                Check.Equal(0,calls);Check.True(shared.ParticipantWithdrawalRequested);
            });
            suite.Run("withdrawal during target factory stops later target inspection",()=>{
                var shared=new ViveRawRevocation();var target=new PropertyProbe();int calls=0;Exception failure=null;ViveRawPrivateStage stage=null;
                using(var entered=new ManualResetEvent(false))using(var release=new ManualResetEvent(false))
                {
                    var thread=new Thread(()=>{try{stage=new ViveRawPrivateStage(()=>{Interlocked.Increment(ref calls);entered.Set();release.WaitOne();return target;},1024,CancellationToken.None,shared);}catch(Exception e){failure=e;}}){IsBackground=true};
                    thread.Start();try{Check.True(entered.WaitOne(5000),"factory did not start");shared.RevokeForParticipant();}
                    finally{release.Set();Check.True(thread.Join(5000),"factory constructor did not stop");}
                    Check.Equal(1,calls);Check.True(stage==null&&failure!=null);Check.Equal(0,target.Inspections);Check.True(shared.ParticipantWithdrawalRequested);
                }
                target.Dispose();
            });
            suite.Run("withdrawal during target close is unknown and close is not replayed",()=>{
                byte[] bytes=ViveRawTrialProjectionTests.FourBlocks();var shared=new ViveRawRevocation();var target=new BlockedClose();
                var stage=new ViveRawPrivateStage(()=>target,bytes.Length,CancellationToken.None,shared);
                ViveRawSessionReadResult scan;using(var input=new MemoryStream(bytes,false))scan=ViveRawSessionJournalReader.ScanWithPrivateStage(input,bytes.Length+1,5000,CancellationToken.None,stage);
                stage.FinishScan(scan,true);Check.True(SpinWait.SpinUntil(()=>stage.WorkerExited,10000),"stage worker did not exit");
                Check.Equal(ViveRawPrivateStage.Outcome.PrivateCandidate,stage.State);
                bool result=true;Exception failure=null;var thread=new Thread(()=>{try{result=stage.TryCloseTargetAfterCandidate();}catch(Exception e){failure=e;}}){IsBackground=true};
                thread.Start();try{Check.True(target.Entered.WaitOne(5000),"close was not entered");shared.RevokeForParticipant();Check.True(stage.StageIoInFlight);}
                finally{target.Release.Set();Check.True(thread.Join(5000),"close did not return");}
                Check.True(failure==null&&!result);Check.True(stage.TargetCloseReturned);Check.Equal(ViveRawPrivateStage.Outcome.RevokedOrIoUnknown,stage.State);
                Check.True(!stage.TryCloseTargetAfterCandidate());Check.Equal(1,target.Calls);
            });
#if VIVE_SYNTHETIC_BATCH_FIXTURE
            suite.Run("abandon during asynchronous close keeps late return unknown",()=>CheckAsyncCloseStop(false));
            suite.Run("withdrawal during asynchronous close keeps late return unknown",()=>CheckAsyncCloseStop(true));
#endif
        }
#if VIVE_SYNTHETIC_BATCH_FIXTURE
        static void CheckAsyncCloseStop(bool withdraw)
        {
            byte[] bytes=ViveRawTrialProjectionTests.FourBlocks();var shared=new ViveRawRevocation();var target=new BlockedClose();
            var stage=new ViveRawPrivateStage(()=>target,bytes.Length,CancellationToken.None,shared);
            ViveRawSessionReadResult scan;using(var input=new MemoryStream(bytes,false))scan=ViveRawSessionJournalReader.ScanWithPrivateStage(input,bytes.Length+1,5000,CancellationToken.None,stage);
            stage.FinishScan(scan,true);Check.True(SpinWait.SpinUntil(()=>stage.WorkerExited,10000),"stage worker did not exit");
            Check.True(stage.StartSyntheticTargetCloseForTest(),"close worker did not start");
            try
            {
                Check.True(target.Entered.WaitOne(5000),"close did not enter");
                if(withdraw)shared.RevokeForParticipant();else stage.Abandon();
                Check.True(stage.StageIoInFlight&&!stage.SyntheticCloseSucceeded&&!stage.TargetCloseReturned);
                Check.Equal(ViveRawPrivateStage.Outcome.RevokedOrIoUnknown,stage.State);
            }
            finally{target.Release.Set();}
            Check.True(SpinWait.SpinUntil(()=>stage.SyntheticCloseDone,5000),"close finally signal missing");
            Check.True(stage.TargetCloseReturned&&!stage.SyntheticCloseSucceeded);
            Check.Equal(ViveRawPrivateStage.Outcome.RevokedOrIoUnknown,stage.State);
            Check.True(!stage.StartSyntheticTargetCloseForTest());Check.Equal(1,target.Calls);
        }
#endif
    }
}
#endif
