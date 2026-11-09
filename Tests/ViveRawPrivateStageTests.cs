#if VIVE_RAW_SYNTHETIC_TEST && !UNITY_5_3_OR_NEWER
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    public static class ViveRawPrivateStageTests
    {
        sealed class BlockedWrite:MemoryStream
        {
            internal readonly ManualResetEvent Entered=new ManualResetEvent(false),Release=new ManualResetEvent(false);
            public override void Write(byte[] bytes,int offset,int count){Entered.Set();Release.WaitOne();base.Write(bytes,offset,count);}
        }
        sealed class BlockedFlush:MemoryStream
        {
            internal readonly ManualResetEvent Entered=new ManualResetEvent(false),Release=new ManualResetEvent(false);
            internal int ReadCalls;
            public override void Flush(){Entered.Set();Release.WaitOne();base.Flush();}
            public override int Read(byte[] bytes,int offset,int count){Interlocked.Increment(ref ReadCalls);return base.Read(bytes,offset,count);}
        }
        sealed class BlockedRead:MemoryStream
        {
            internal readonly ManualResetEvent Entered=new ManualResetEvent(false),Release=new ManualResetEvent(false);
            internal int ReadCalls;
            public override int Read(byte[] bytes,int offset,int count)
            {int call=Interlocked.Increment(ref ReadCalls);if(call==1){Entered.Set();Release.WaitOne();}return base.Read(bytes,offset,count);}
        }
        sealed class CountedBlockedWrite:MemoryStream
        {
            internal readonly ManualResetEvent Entered=new ManualResetEvent(false),Release=new ManualResetEvent(false);
            internal int Calls;
            public override void Write(byte[] bytes,int offset,int count)
            {int call=Interlocked.Increment(ref Calls);if(call==1){Entered.Set();Release.WaitOne();}base.Write(bytes,offset,count);}
        }
        sealed class PartialFailure:MemoryStream
        {public override void Write(byte[] bytes,int offset,int count){base.Write(bytes,offset,Math.Min(7,count));throw new IOException("synthetic output failure");}}
        sealed class MemoryFailure:MemoryStream
        {public override void Write(byte[] bytes,int offset,int count){throw new OutOfMemoryException("synthetic staging allocation failure");}}
        sealed class ValidationProbe:MemoryStream
        {
            internal int Touches;
            public override bool CanRead {get{Touches++;return base.CanRead;}}
            public override bool CanWrite {get{Touches++;return base.CanWrite;}}
            public override bool CanSeek {get{Touches++;return base.CanSeek;}}
            public override long Length {get{Touches++;return base.Length;}}
            public override long Position {get{Touches++;return base.Position;}set{Touches++;base.Position=value;}}
        }
        sealed class BlockedValidationLength:MemoryStream
        {
            internal readonly ManualResetEvent Entered=new ManualResetEvent(false),Release=new ManualResetEvent(false);
            internal int PositionReads;
            public override long Length {get{Entered.Set();Release.WaitOne();return base.Length;}}
            public override long Position {get{Interlocked.Increment(ref PositionReads);return base.Position;}set{base.Position=value;}}
        }
        static ViveRawSessionReadResult Scan(byte[] bytes,ViveRawPrivateStage stage)
        {using(var input=new MemoryStream(bytes,false))return ViveRawSessionJournalReader.ScanWithPrivateStage(input,Math.Max(8,bytes.Length+1),5000,CancellationToken.None,stage);}
        static void Await(ViveRawPrivateStage stage)
        {Check.True(SpinWait.SpinUntil(()=>stage.WorkerExited,10000),"synthetic stage worker did not exit");}
        sealed class WaitingClient:IViveProEyeClient
        {
            internal readonly ManualResetEvent Entered=new ManualResetEvent(false),Release=new ManualResetEvent(false);
            public string Diagnostic {get{return "synthetic shared withdrawal";}}
            public bool Start(){return true;}
            public ViveProEyePoll Poll(){Entered.Set();Release.WaitOne();return null;}
            public void Dispose(){}
        }
        static ViveDevelopmentParticipant NamedTask(MonotonicClock clock)
        {
            var option=ViveNamedOptionProvenance.Select(Actor.Owner,ViveNamedOption.Automatic,1,"shared-stage-geometry");
            var plan=ViveNamedSimulationPlan.Select(Actor.Owner,"shared-stage-plan",option);
            var intent=ViveNamedRecordingIntent.Select(Actor.Owner,"shared-stage-intent",true,true);
            var config=SessionConfiguration.NamedRecordingDevelopment("shared-stage-session",37,plan,intent);
            var geometry=new AutomaticGazeGeometry(Rotation.Identity,new Vec3(0,0,1),new Rotation(0,Math.Sqrt(.5),0,Math.Sqrt(.5)),new Vec3(1,0,0),"shared-stage-geometry");
            var map=new Dictionary<ViveTrackpadDirection,Colour>{{ViveTrackpadDirection.Up,Colour.Red},{ViveTrackpadDirection.Right,Colour.Green},
                {ViveTrackpadDirection.Down,Colour.Blue},{ViveTrackpadDirection.Left,Colour.Yellow}};
            return new ViveDevelopmentParticipant(config,clock,geometry,map,.5,true);
        }
        public static void Register(TestSuite suite)
        {
            suite.Run("installed private stage remains closed before owner or authority access",()=>{
                var authority=new ViveRawRevocation();
                Check.True(!ViveRawExclusiveDestination.ProductionGateOpen);
                Check.True(ViveRawPrivateStage.CreateInstalledDeferred(null,null,1024,CancellationToken.None,authority)==null);
                Check.True(authority.MayReserve&&!authority.ParticipantWithdrawalRequested);
            });
            suite.Run("shared participant withdrawal before binding leaves target untouched",()=>{
                var shared=new ViveRawRevocation();shared.RevokeForParticipant();using(var target=new ValidationProbe())
                {Check.Throws<InvalidOperationException>(()=>new ViveRawPrivateStage(target,1024,CancellationToken.None,shared));
                    Check.True(shared.ParticipantWithdrawalRequested);Check.Equal(0,target.Touches);}
            });
            suite.Run("shared withdrawal during binding prevents target inspection",()=>{
                var shared=new ViveRawRevocation();using(var target=new ValidationProbe())using(var entered=new ManualResetEvent(false))using(var release=new ManualResetEvent(false))
                {shared.AfterPrivateStageBindForTest=()=>{entered.Set();release.WaitOne();};Exception error=null;
                    var thread=new Thread(()=>{try{new ViveRawPrivateStage(target,1024,CancellationToken.None,shared);}catch(Exception e){error=e;}}){IsBackground=true};thread.Start();
                    try{Check.True(entered.WaitOne(5000));shared.RevokeForParticipant();}
                    finally{release.Set();Check.True(thread.Join(5000));}
                    Check.True(error is InvalidOperationException);Check.Equal(0,target.Touches);Check.True(shared.ParticipantWithdrawalRequested);}
            });
            suite.Run("technical revoke blocks native and stage before state exchange",()=>{
                var shared=new ViveRawRevocation();using(var target=new MemoryStream())using(var entered=new ManualResetEvent(false))using(var release=new ManualResetEvent(false))
                {var stage=new ViveRawPrivateStage(target,1024,CancellationToken.None,shared);
                    shared.AfterPrivateStageStopForTest=()=>{entered.Set();release.WaitOne();};
                    var thread=new Thread(()=>shared.Revoke()){IsBackground=true};thread.Start();
                    try{Check.True(entered.WaitOne(5000));Check.True(shared.IsRevoked&&!shared.MayReserve&&!shared.ParticipantWithdrawalRequested);
                        stage.OfferMagic(new byte[]{1,2,3,4,5,6,7,8});Await(stage);Check.Equal(0L,target.Length);
                        Check.Equal(ViveRawPrivateStage.Outcome.RevokedOrIoUnknown,stage.State);}
                    finally{release.Set();Check.True(thread.Join(5000));}}
            });
            suite.Run("participant withdrawal entry defeats pending shared stage claim",()=>{
                var clock=new MonotonicClock("participant-shared-stage");var task=NamedTask(clock);
                using(var target=new MemoryStream())using(var entered=new ManualResetEvent(false))using(var release=new ManualResetEvent(false))
                {var stage=new ViveRawPrivateStage(target,1024,CancellationToken.None,task.NamedRawRevocation);
                    stage.BeforeIoClaimForTest=()=>{entered.Set();release.WaitOne();};stage.OfferMagic(new byte[]{1,2,3,4,5,6,7,8});
                    try{Check.True(entered.WaitOne(5000));task.ParticipantWithdraw();Check.True(task.NamedRawRevocation.ParticipantWithdrawalRequested);}
                    finally{release.Set();Await(stage);}Check.Equal(0L,target.Length);Check.Equal(ViveRawPrivateStage.Outcome.RevokedOrIoUnknown,stage.State);}
            });
            suite.Run("pump withdrawal entry defeats pending shared stage claim",()=>{
                var clock=new MonotonicClock("pump-shared-stage");var task=NamedTask(clock);var client=new WaitingClient();
                var worker=new ViveProEyeWorker(clock,()=>new ViveHeadFrame(1,clock.Now,true,true,Rotation.Identity),()=>client);ViveNamedRawRecordingPump pump=null;
                using(var target=new MemoryStream())using(var entered=new ManualResetEvent(false))using(var release=new ManualResetEvent(false))
                {ViveRawPrivateStage stage=null;
                    try{Check.True(worker.Start()&&client.Entered.WaitOne(3000));var admission=ViveNamedSessionFrameAdmission.CreateSyntheticForTest(task,worker,clock,32);
                        Check.True(admission.ConfirmTask()&&admission.ConfirmEye());var writer=new ViveNamedRawRecordingPump.SyntheticWriter(admission);
                        pump=ViveNamedRawRecordingPump.StartSyntheticForTest(task,worker,clock,admission,writer);
                        stage=new ViveRawPrivateStage(target,1024,CancellationToken.None,task.NamedRawRevocation);
                        stage.BeforeIoClaimForTest=()=>{entered.Set();release.WaitOne();};stage.OfferMagic(new byte[]{1,2,3,4,5,6,7,8});
                        Check.True(entered.WaitOne(5000));pump.Withdraw();Check.True(pump.WithdrawalRequested&&!task.NamedRawRevocation.MayReserve);}
                    finally{release.Set();if(stage!=null)Await(stage);if(pump!=null)pump.Stop();client.Release.Set();worker.Stop(1000);worker.Dispose();}
                    Check.Equal(0L,target.Length);Check.Equal(ViveRawPrivateStage.Outcome.RevokedOrIoUnknown,stage.State);}
            });
            suite.Run("shared withdrawal preserves in-flight uncertainty and stops queued writes",()=>{
                var shared=new ViveRawRevocation();using(var target=new CountedBlockedWrite())
                {var stage=new ViveRawPrivateStage(target,1024,CancellationToken.None,shared);stage.OfferMagic(new byte[]{1,2,3,4,5,6,7,8});
                    try{Check.True(target.Entered.WaitOne(5000));stage.OfferMagic(new byte[]{9,10,11,12,13,14,15,16});shared.RevokeForParticipant();
                        Check.True(stage.StageIoInFlight);Check.Equal(ViveRawPrivateStage.Outcome.RevokedOrIoUnknown,stage.State);}
                    finally{target.Release.Set();Await(stage);}Check.Equal(1,target.Calls);Check.Equal(8L,target.Length);}
            });
            suite.Run("shared withdrawal drains a producer that enqueues after worker exit",()=>{
                var shared=new ViveRawRevocation();using(var target=new MemoryStream())using(var entered=new ManualResetEvent(false))using(var release=new ManualResetEvent(false))
                {var stage=new ViveRawPrivateStage(target,1024,CancellationToken.None,shared);stage.BeforeEnqueueForTest=()=>{entered.Set();release.WaitOne();};
                    var thread=new Thread(()=>stage.OfferMagic(new byte[]{1,2,3,4,5,6,7,8})){IsBackground=true};thread.Start();
                    try{Check.True(entered.WaitOne(5000));shared.RevokeForParticipant();Await(stage);}
                    finally{release.Set();Check.True(thread.Join(5000));}Check.Equal(0,stage.QueuedBytesForTest);Check.Equal(0,stage.QueuedFramesForTest);Check.Equal(0L,target.Length);}
            });
            suite.Run("cancelled token prevents stage constructor target inspection",()=>{
                using(var cancel=new CancellationTokenSource())using(var target=new ValidationProbe())
                {cancel.Cancel();bool rejected=false;
                    try{new ViveRawPrivateStage(target,1024,cancel.Token);}catch(OperationCanceledException){rejected=true;}
                    Check.True(rejected);Check.Equal(0,target.Touches);}
            });
            suite.Run("cancellation during target validation stops later inspection",()=>{
                using(var cancel=new CancellationTokenSource())using(var target=new BlockedValidationLength())
                {ViveRawPrivateStage stage=null;Exception error=null;
                    var thread=new Thread(()=>{try{stage=new ViveRawPrivateStage(target,1024,cancel.Token);}catch(Exception e){error=e;}}){IsBackground=true};thread.Start();
                    try{Check.True(target.Entered.WaitOne(5000),"length inspection did not begin");cancel.Cancel();}
                    finally{target.Release.Set();Check.True(thread.Join(5000),"constructor remained blocked after target returned");}
                    Check.True(stage==null&&error!=null);Check.Equal(0,target.PositionReads);}
            });
            suite.Run("private stage copies one verified four-block scan without granting authorization",()=>{
                byte[] bytes=ViveRawTrialProjectionTests.FourBlocks();
                using(var target=new MemoryStream())
                {var stage=new ViveRawPrivateStage(target,bytes.Length,CancellationToken.None);var scan=Scan(bytes,stage);stage.FinishScan(scan,true);Await(stage);
                    Check.Equal(ViveRawSessionReadStatus.VerifiedPrefix,scan.Status);Check.True(scan.CaptureTerminalObserved);
                    Check.Equal(ViveRawPrivateStage.Outcome.PrivateCandidate,stage.State);Check.Equal((long)bytes.Length,target.Length);
                    var copy=target.ToArray();for(int i=0;i<bytes.Length;i++)Check.Equal(bytes[i],copy[i]);
                    Check.True(!scan.SourceAuthenticated&&!scan.CurrentAccessAuthorized&&!scan.DurableStorageProven);stage.Abandon();
                    Check.Equal(ViveRawPrivateStage.Outcome.RevokedOrIoUnknown,stage.State);}
            });
            suite.Run("slow private output cannot block source scan and withdrawal is uncertain",()=>{
                byte[] bytes=ViveRawTrialProjectionTests.FourBlocks();using(var cancel=new CancellationTokenSource())using(var target=new BlockedWrite())
                {var stage=new ViveRawPrivateStage(target,bytes.Length,cancel.Token);ViveRawSessionReadResult scan=null;
                    var thread=new Thread(()=>scan=Scan(bytes,stage)){IsBackground=true};thread.Start();
                    try{Check.True(target.Entered.WaitOne(5000),"synthetic output did not enter write");
                        Check.True(thread.Join(5000),"source scan waited for blocked output");Check.Equal(ViveRawSessionReadStatus.VerifiedPrefix,scan.Status);
                        cancel.Cancel();Check.True(stage.StageIoInFlight);Check.Equal(ViveRawPrivateStage.Outcome.RevokedOrIoUnknown,stage.State);stage.FinishScan(scan,true);}
                    finally{target.Release.Set();thread.Join(5000);Await(stage);}
                    Check.Equal(ViveRawPrivateStage.Outcome.RevokedOrIoUnknown,stage.State);}
            });
            suite.Run("partial output failure is unknown and source scan remains valid",()=>{
                byte[] bytes=ViveRawSessionTerminalTests.Fixture();using(var target=new PartialFailure())
                {var stage=new ViveRawPrivateStage(target,bytes.Length,CancellationToken.None);var scan=Scan(bytes,stage);stage.FinishScan(scan,true);Await(stage);
                    Check.Equal(ViveRawSessionReadStatus.VerifiedPrefix,scan.Status);Check.Equal(ViveRawPrivateStage.Outcome.RevokedOrIoUnknown,stage.State);Check.True(target.Length>0);}
            });
            suite.Run("staging allocation and worker OOM do not change source scan",()=>{
                byte[] bytes=ViveRawSessionTerminalTests.Fixture();using(var target=new MemoryStream())
                {var stage=new ViveRawPrivateStage(target,bytes.Length,CancellationToken.None);stage.AllocatorForTest=n=>{throw new OutOfMemoryException("synthetic allocation");};
                    var scan=Scan(bytes,stage);stage.FinishScan(scan,true);Await(stage);Check.Equal(ViveRawSessionReadStatus.VerifiedPrefix,scan.Status);
                    Check.Equal(ViveRawPrivateStage.Outcome.Unavailable,stage.State);Check.Equal(0L,target.Length);}
                using(var target=new MemoryFailure())
                {var stage=new ViveRawPrivateStage(target,bytes.Length,CancellationToken.None);var scan=Scan(bytes,stage);stage.FinishScan(scan,true);Await(stage);
                    Check.Equal(ViveRawSessionReadStatus.VerifiedPrefix,scan.Status);Check.Equal(ViveRawPrivateStage.Outcome.RevokedOrIoUnknown,stage.State);}
            });
            suite.Run("abandon during flush invalidates private candidate",()=>{
                byte[] bytes=ViveRawSessionTerminalTests.Fixture();using(var target=new BlockedFlush())
                {var stage=new ViveRawPrivateStage(target,bytes.Length,CancellationToken.None);var scan=Scan(bytes,stage);stage.FinishScan(scan,true);
                    try{Check.True(target.Entered.WaitOne(5000),"synthetic flush did not begin");stage.Abandon();Check.Equal(ViveRawPrivateStage.Outcome.RevokedOrIoUnknown,stage.State);}
                    finally{target.Release.Set();Await(stage);}Check.Equal(ViveRawPrivateStage.Outcome.RevokedOrIoUnknown,stage.State);
                    Check.Equal(0,target.ReadCalls);}
            });
            suite.Run("late enqueue after worker abort is drained and source scan survives",()=>{
                byte[] bytes=ViveRawSessionTerminalTests.Fixture();using(var target=new MemoryStream())using(var entered=new ManualResetEvent(false))using(var release=new ManualResetEvent(false))
                {var stage=new ViveRawPrivateStage(target,bytes.Length,CancellationToken.None);stage.BeforeEnqueueForTest=()=>{entered.Set();release.WaitOne();};
                    ViveRawSessionReadResult scan=null;var thread=new Thread(()=>scan=Scan(bytes,stage)){IsBackground=true};thread.Start();
                    try{Check.True(entered.WaitOne(5000),"producer barrier not reached");stage.Abandon();Await(stage);}
                    finally{release.Set();Check.True(thread.Join(5000),"source scan did not return");}
                    Check.Equal(ViveRawSessionReadStatus.VerifiedPrefix,scan.Status);Check.Equal(0,stage.QueuedBytesForTest);Check.Equal(0,stage.QueuedFramesForTest);
                    Check.True(stage.State!=ViveRawPrivateStage.Outcome.PrivateCandidate);}
            });
            suite.Run("corrupt truncated and unsealed sources never yield a candidate",()=>{
                var good=ViveRawSessionTerminalTests.Fixture();var corrupt=(byte[])good.Clone();corrupt[corrupt.Length-1]^=1;
                foreach(var bytes in new[]{corrupt,ViveRawSessionTerminalTests.Fixture(truncateTerminal:true),ViveRawSessionTerminalTests.Fixture(omitTerminal:true)})
                using(var target=new MemoryStream())
                {var stage=new ViveRawPrivateStage(target,bytes.Length,CancellationToken.None);var scan=Scan(bytes,stage);stage.FinishScan(scan,true);Await(stage);
                    Check.True(stage.State!=ViveRawPrivateStage.Outcome.PrivateCandidate);}
            });
            suite.Run("stage withdrawal wins before output claim and issues no I/O",()=>{
                using(var target=new MemoryStream())using(var entered=new ManualResetEvent(false))using(var release=new ManualResetEvent(false))
                {var stage=new ViveRawPrivateStage(target,1024,CancellationToken.None);
                    stage.BeforeIoClaimForTest=()=>{entered.Set();release.WaitOne();};
                    stage.OfferMagic(new byte[]{1,2,3,4,5,6,7,8});
                    try{Check.True(entered.WaitOne(5000),"claim barrier not reached");stage.RequestWithdrawal();
                        Check.Equal(ViveRawPrivateStage.Outcome.RevokedOrIoUnknown,stage.State);}
                    finally{release.Set();Await(stage);}
                    Check.Equal(0L,target.Length);Check.True(!stage.StageIoInFlight);
                    Check.Equal(ViveRawPrivateStage.Outcome.RevokedOrIoUnknown,stage.State);}
            });
            suite.Run("stage withdrawal after a claim remains unknown and avoids optional output",()=>{
                using(var target=new MemoryStream())using(var entered=new ManualResetEvent(false))using(var release=new ManualResetEvent(false))
                {var stage=new ViveRawPrivateStage(target,1024,CancellationToken.None);
                    stage.AfterIoClaimForTest=()=>{entered.Set();release.WaitOne();};
                    stage.OfferMagic(new byte[]{1,2,3,4,5,6,7,8});
                    try{Check.True(entered.WaitOne(5000),"claimed barrier not reached");Check.True(stage.StageIoInFlight);
                        stage.RequestWithdrawal();Check.True(stage.StageIoInFlight);
                        Check.Equal(ViveRawPrivateStage.Outcome.RevokedOrIoUnknown,stage.State);}
                    finally{release.Set();Await(stage);}
                    Check.Equal(0L,target.Length);Check.True(!stage.StageIoInFlight);
                    Check.Equal(ViveRawPrivateStage.Outcome.RevokedOrIoUnknown,stage.State);}
            });
            suite.Run("stage withdrawal does not wait for claimed output or claim queued writes",()=>{
                using(var target=new CountedBlockedWrite())
                {var stage=new ViveRawPrivateStage(target,1024,CancellationToken.None);
                    stage.OfferMagic(new byte[]{1,2,3,4,5,6,7,8});
                    try{Check.True(target.Entered.WaitOne(5000),"claimed output did not start");
                        stage.OfferMagic(new byte[]{9,10,11,12,13,14,15,16});
                        stage.RequestWithdrawal();Check.True(stage.StageIoInFlight);
                        Check.Equal(ViveRawPrivateStage.Outcome.RevokedOrIoUnknown,stage.State);}
                    finally{target.Release.Set();Await(stage);}
                    Check.Equal(1,target.Calls);Check.Equal(8L,target.Length);Check.True(!stage.StageIoInFlight);
                    Check.Equal(ViveRawPrivateStage.Outcome.RevokedOrIoUnknown,stage.State);}
            });
            suite.Run("stage withdrawal during readback prevents a subsequent target read",()=>{
                byte[] bytes=ViveRawSessionTerminalTests.Fixture();using(var target=new BlockedRead())
                {var stage=new ViveRawPrivateStage(target,bytes.Length,CancellationToken.None);var scan=Scan(bytes,stage);stage.FinishScan(scan,true);
                    try{Check.True(target.Entered.WaitOne(5000),"readback did not enter first read");Check.True(stage.StageIoInFlight);
                        stage.RequestWithdrawal();Check.True(stage.StageIoInFlight);
                        Check.Equal(ViveRawPrivateStage.Outcome.RevokedOrIoUnknown,stage.State);}
                    finally{target.Release.Set();Await(stage);}
                    Check.Equal(1,target.ReadCalls);Check.True(!stage.StageIoInFlight);
                    Check.Equal(ViveRawPrivateStage.Outcome.RevokedOrIoUnknown,stage.State);}
            });
        }
    }
}
#endif
