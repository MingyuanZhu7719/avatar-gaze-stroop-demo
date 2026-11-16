using System;
using System.Collections.Generic;
using System.Threading;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    public static class ViveGazeValidationCaptureTests
    {
        sealed class Client : IViveProEyeClient,IViveProEyeCleanupStatus,IViveProEyeAcquisitionSeal
        {
            internal readonly Queue<ViveProEyePoll> Values=new Queue<ViveProEyePoll>();
            internal readonly Queue<ViveProEyePoll> Tail=new Queue<ViveProEyePoll>();
            internal Func<ViveProEyePoll> OnPoll=null;
            internal bool Safe=true,SealOk=true,Sealed;
            internal int StartThread,PollThread,SealThread,DisposeThread;
            public bool RestartSafe {get{return Safe;}}
            public string Diagnostic {get{return "synthetic validation client";}}
            public bool Start(){StartThread=Thread.CurrentThread.ManagedThreadId;return true;}
            public ViveProEyePoll Poll()
            {
                PollThread=Thread.CurrentThread.ManagedThreadId;
                if(OnPoll!=null)return OnPoll();
                return Values.Count==0?null:Values.Dequeue();
            }
            public bool RequestAcquisitionSeal()
            {
                SealThread=Thread.CurrentThread.ManagedThreadId;Sealed=true;
                while(Tail.Count>0)Values.Enqueue(Tail.Dequeue());
                return SealOk;
            }
            public bool AcquisitionSealDrained {get{return Sealed&&Values.Count==0;}}
            public bool HasBufferedAcquisition {get{return Values.Count>0;}}
            public void Dispose(){DisposeThread=Thread.CurrentThread.ManagedThreadId;}
        }
        static ViveProEyePoll Eye(IClock clock,int frame,bool invalid=false,double? observed=null)
        {
            var ray=new ViveEyeRay(invalid?0UL:3UL,!invalid,!invalid,new Vec3(0,0,0),new Vec3(0,0,1));
            return new ViveProEyePoll(null,new ViveEyeFrame(frame,frame*8,observed??clock.Now,invalid,null,null,ray),
                ViveEyeRuntimeState.Working,invalid?ViveEyeCalibrationState.Required:ViveEyeCalibrationState.ReportedReady,true,0,2);
        }
        static void Until(Func<bool> ready,string message)
        {Check.True(SpinWait.SpinUntil(ready,3000),message);}
        static void Finish(ViveGazeValidationCapture capture)
        {capture.RequestStop();Until(()=>capture.HasExited,"Validation native worker did not finish");capture.Dispose();}
        static List<ViveRawEyeAcquisition> Drain(ViveGazeValidationCapture capture)
        {
            var values=new List<ViveRawEyeAcquisition>();ViveRawEyeAcquisition value;
            while(capture.TryDequeue(out value))values.Add(value);
            return values;
        }
        public static void Register(TestSuite suite)
        {
            suite.Run("validation capture is explicit one-shot and all native operations stay on one worker",()=>{
                var clock=new ManualClock{Now=1};var client=new Client();int factories=0;
                client.Values.Enqueue(Eye(clock,1));
                var capture=new ViveGazeValidationCapture(clock,()=>{Interlocked.Increment(ref factories);return client;});
                try
                {
                    Check.Equal(0,factories);Check.True(!capture.Started&&!capture.HasExited);Check.Equal(0,capture.PendingCount);
                    Check.True(capture.Start());Check.True(!capture.Start());Until(()=>capture.AcquisitionCount==1,"No native acquisition");
                    Finish(capture);
                    Check.Equal(1,factories);Check.True(capture.HasExited&&capture.ProviderDrained&&capture.CleanupConfirmed);
                    Check.True(client.StartThread!=Thread.CurrentThread.ManagedThreadId);
                    Check.Equal(client.StartThread,client.PollThread);Check.Equal(client.StartThread,client.SealThread);Check.Equal(client.StartThread,client.DisposeThread);
                    Check.Equal(null,capture.Fault);Check.True(!capture.Start());Check.Equal(1,Drain(capture).Count);
                }
                finally{Finish(capture);}
            });
            suite.Run("validation capture retains invalid calibration-required native rows and counts dedup and native gaps",()=>{
                var clock=new ManualClock{Now=1};var client=new Client();
                client.Values.Enqueue(Eye(clock,10,true));client.Values.Enqueue(Eye(clock,10,true));
                client.Values.Enqueue(Eye(clock,13));client.Values.Enqueue(Eye(clock,12));
                var capture=new ViveGazeValidationCapture(clock,()=>client);
                try
                {
                    Check.True(capture.Start());Until(()=>capture.AcquisitionCount==3,"Expected three unique native rows");Finish(capture);
                    var values=Drain(capture);Check.Equal(3,values.Count);Check.Equal(1L,capture.DuplicatePolls);
                    Check.Equal(2L,capture.NativeSequenceGapCount);Check.Equal(1L,capture.NativeSequenceDiscontinuities);
                    Check.Equal(ViveEyeCalibrationState.Required,values[0].Poll.Calibration);Check.True(values[0].Poll.Eye.NoUser);
                    Check.Equal(0UL,values[0].Poll.Eye.Combined.ValidityMask);Check.Equal(null,values[0].Poll.Head);
                    Check.Equal(0L,values[0].Sequence);Check.Equal(2L,values[2].Sequence);Check.Equal(3L,capture.DequeuedCount);
                    Check.Equal(null,capture.Fault);
                }
                finally{Finish(capture);}
            });
            suite.Run("validation normal stop seals callback and keeps its tail readable after cleanup",()=>{
                var clock=new ManualClock{Now=1};var client=new Client();
                client.Values.Enqueue(Eye(clock,1));client.Tail.Enqueue(Eye(clock,2));client.Tail.Enqueue(Eye(clock,3,true));
                var capture=new ViveGazeValidationCapture(clock,()=>client);
                try
                {
                    Check.True(capture.Start());Until(()=>capture.AcquisitionCount==1,"Initial native sample missing");Finish(capture);
                    Check.True(capture.ProviderDrained&&capture.CleanupConfirmed);Check.Equal(null,capture.Fault);
                    var values=Drain(capture);Check.Equal(3,values.Count);Check.Equal(3,values[2].Poll.Eye.FrameSequence);
                    Check.Equal(3L,capture.AcquisitionCount);Check.Equal(0,capture.PendingCount);
                }
                finally{Finish(capture);}
            });
            suite.Run("validation stop does not block owner behind native poll and preserves its returned sample",()=>{
                var clock=new ManualClock{Now=1};var entered=new ManualResetEvent(false);var release=new ManualResetEvent(false);
                var client=new Client{OnPoll=()=>{entered.Set();Check.True(release.WaitOne(3000));return Eye(clock,1);}};
                var capture=new ViveGazeValidationCapture(clock,()=>client);
                try
                {
                    Check.True(capture.Start());Check.True(entered.WaitOne(3000));capture.RequestStop();
                    Check.True(!capture.HasExited,"Stop must return while fake native call remains blocked");
                    Check.Equal(0,capture.PendingCount);release.Set();Finish(capture);
                    Check.Equal(1,Drain(capture).Count);Check.Equal(null,capture.Fault);
                }
                finally{release.Set();Finish(capture);entered.Dispose();release.Dispose();}
            });
            suite.Run("validation overflow faults instead of silently overwriting older samples",()=>{
                var clock=new ManualClock{Now=1};var client=new Client();
                for(int i=1;i<=4;i++)client.Values.Enqueue(Eye(clock,i));
                var capture=new ViveGazeValidationCapture(clock,()=>client,2);
                try
                {
                    Check.True(capture.Start());Until(()=>capture.HasExited,"Overflow did not stop capture");
                    Check.Equal("validation_acquisition_queue_overflow",capture.Fault);Check.Equal(1L,capture.RejectedAcquisitions);
                    Check.Equal(2,capture.MaximumPendingCount);Check.True(!capture.ProviderDrained&&capture.CleanupConfirmed);
                    var values=Drain(capture);Check.Equal(2,values.Count);Check.Equal(1,values[0].Poll.Eye.FrameSequence);Check.Equal(2,values[1].Poll.Eye.FrameSequence);
                }
                finally{Finish(capture);}
            });
            suite.Run("validation unconfirmed cleanup and unsuccessful callback barrier are not successful completion",()=>{
                foreach(bool unsafeCleanup in new[]{false,true})
                {
                    var clock=new ManualClock{Now=1};var client=new Client{Safe=!unsafeCleanup,SealOk=unsafeCleanup};client.Values.Enqueue(Eye(clock,1));
                    var capture=new ViveGazeValidationCapture(clock,()=>client);
                    try
                    {
                        Check.True(capture.Start());Until(()=>capture.AcquisitionCount==1,"Native sample missing");Finish(capture);
                        Check.Equal(unsafeCleanup?"native_cleanup_unconfirmed":"native_callback_seal_failed",capture.Fault);
                        Check.Equal(unsafeCleanup,capture.ProviderDrained);Check.Equal(!unsafeCleanup,capture.CleanupConfirmed);
                        Check.Equal(1,Drain(capture).Count);
                    }
                    finally{Finish(capture);}
                }
            });
            suite.Run("validation future arrival clocks fault and never gain a refreshed observation time",()=>{
                var clock=new ManualClock{Now=1};var client=new Client();client.Values.Enqueue(Eye(clock,1,false,2));
                var capture=new ViveGazeValidationCapture(clock,()=>client);
                try
                {
                    Check.True(capture.Start());Until(()=>capture.HasExited,"Invalid clock did not stop capture");
                    Check.Equal("invalid_eye_arrival_clock",capture.Fault);Check.Equal(1L,capture.RejectedAcquisitions);Check.Equal(0,Drain(capture).Count);
                }
                finally{Finish(capture);}
            });
            suite.Run("validation capture never starts after disposal and contains factory exceptions",()=>{
                var clock=new ManualClock{Now=1};int factories=0;
                var never=new ViveGazeValidationCapture(clock,()=>{factories++;return new Client();});
                never.Dispose();Check.True(!never.Start());Check.Equal(0,factories);
                var capture=new ViveGazeValidationCapture(clock,()=>{throw new InvalidOperationException("synthetic");});
                try
                {
                    Check.True(capture.Start());Until(()=>capture.HasExited,"Factory error did not stop capture");
                    Check.Equal("capture_exception_InvalidOperationException",capture.Fault);Check.True(capture.CleanupConfirmed&&!capture.ProviderDrained);
                }
                finally{Finish(capture);}
            });
            suite.Run("validation stop during client construction does not start native capture afterward",()=>{
                var clock=new ManualClock{Now=1};var entered=new ManualResetEvent(false);var release=new ManualResetEvent(false);
                var client=new Client();
                var capture=new ViveGazeValidationCapture(clock,()=>{entered.Set();Check.True(release.WaitOne(3000));return client;});
                try
                {
                    Check.True(capture.Start());Check.True(entered.WaitOne(3000));capture.RequestStop();Check.True(!capture.HasExited);
                    release.Set();Finish(capture);Check.Equal(0,client.StartThread);Check.Equal(0,client.PollThread);
                    Check.True(client.DisposeThread!=0&&client.DisposeThread!=Thread.CurrentThread.ManagedThreadId);
                    Check.True(capture.ProviderDrained&&capture.CleanupConfirmed);Check.Equal(null,capture.Fault);
                }
                finally{release.Set();Finish(capture);entered.Dispose();release.Dispose();}
            });
        }
    }
}
