using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    public static class ViveNativeAcquisitionTests
    {
        sealed class Client : IViveProEyeClient
        {
            readonly Queue<ViveProEyePoll> values=new Queue<ViveProEyePoll>();
            internal readonly AutoResetEvent Waiting=new AutoResetEvent(false),Permit=new AutoResetEvent(false);
            public string Diagnostic {get{return "synthetic acquisition";}}
            public bool Start(){return true;}
            public ViveProEyePoll Poll()
            {Waiting.Set();Check.True(Permit.WaitOne(3000));lock(values)return values.Dequeue();}
            internal void Send(ViveProEyePoll value)
            {lock(values)values.Enqueue(value);Permit.Set();Check.True(Waiting.WaitOne(3000));}
            internal void Unblock(){lock(values)values.Enqueue(null);Permit.Set();}
            public void Dispose(){}
        }
        static ViveProEyePoll Eye(double now,int id,bool invalid=false)
        {
            var ray=new ViveEyeRay(invalid?0UL:3UL,!invalid,!invalid,new Vec3(1,2,3),new Vec3(0,0,1));
            return new ViveProEyePoll(null,new ViveEyeFrame(id,id*8,now,false,ray,ray,ray),ViveEyeRuntimeState.Working,
                invalid?ViveEyeCalibrationState.Required:ViveEyeCalibrationState.ReportedReady,true,0,2);
        }
        static void Stop(ViveProEyeWorker worker,Client client)
        {worker.TryRequestStop();client.Unblock();Check.True(worker.Stop(1000));worker.Dispose();}
        static void AckStatuses(ViveProEyeWorker worker,object run)
        {
            ViveProEyeWorker.StatusTicket ticket;
            while(worker.TryReserveStatusEvidence(run,out ticket)==ViveProEyeWorker.StatusDisposition.Event)
                Check.True(worker.TryAcknowledgeStatusEvidence(ticket));
        }
        public static void Register(TestSuite suite)
        {
            suite.Run("native acquisition FIFO preserves intermediate invalid samples and excludes duplicate resurrection",()=>{
                var clock=new MonotonicClock("acquisition-fifo");var client=new Client();
                var worker=new ViveProEyeWorker(clock,()=>null,()=>client);var authority=new ViveRawRevocation();object run;
                try
                {
                    Check.True(worker.Start());Check.True(client.Waiting.WaitOne(3000));
                    Check.True(worker.TryEnableStatusEvidence(16,authority,out run,true));
                    client.Send(Eye(clock.Now,99)); // call entered before admission
                    Check.Equal(ViveProEyeWorker.StatusDisposition.Empty,worker.ReadAcquisitionBacklog(run));
                    client.Send(Eye(clock.Now,1));client.Send(Eye(clock.Now,3,true));client.Send(Eye(clock.Now,1));client.Send(Eye(clock.Now,4));
                    // No owner Poll or render tick was needed to retain all new data.
                    ViveRawEyeAcquisition a,b,c;
                    Check.Equal(ViveProEyeWorker.StatusDisposition.Event,worker.TryDequeueAcquisition(run,out a));
                    Check.Equal(ViveProEyeWorker.StatusDisposition.Event,worker.TryDequeueAcquisition(run,out b));
                    Check.Equal(ViveProEyeWorker.StatusDisposition.Event,worker.TryPeekAcquisition(run,out c));
                    Check.Equal(0L,a.Sequence);Check.Equal(1,a.Poll.Eye.FrameSequence);Check.Equal(null,a.Poll.Head);
                    Check.Equal(1L,b.Sequence);Check.Equal(3,b.Poll.Eye.FrameSequence);
                    Check.Equal(ViveEyeCalibrationState.Required,b.Poll.Calibration);Check.Equal(0UL,b.Poll.Eye.Combined.ValidityMask);
                    Check.Equal(2L,c.Sequence);Check.Equal(4,c.Poll.Eye.FrameSequence);Check.Equal(4L,c.EligiblePolls);
                    Check.Equal(1L,c.DuplicatePolls);Check.Equal(1L,c.NativeSequenceGapCount);
                    Check.Equal(ViveProEyeWorker.StatusDisposition.Unavailable,worker.ReadAcquisitionBacklog(new object()));
                    AckStatuses(worker,run);ViveProEyeWorker.StatusSeal seal;Check.True(worker.TrySealStatusEvidence(run,out seal));
                    Check.True(!seal.TryConsume(run),"Completion must wait for acquisition backlog");
                    client.Send(Eye(clock.Now,5)); // sealed late return is not added
                    ViveRawEyeAcquisition last;Check.Equal(ViveProEyeWorker.StatusDisposition.Event,worker.TryDequeueAcquisition(run,out last));
                    Check.True(Object.ReferenceEquals(c,last));Check.Equal(ViveProEyeWorker.StatusDisposition.Empty,worker.ReadAcquisitionBacklog(run));
                    Check.True(seal.TryConsume(run));
                    authority.Revoke();Check.Equal(ViveProEyeWorker.StatusDisposition.Unavailable,worker.ReadAcquisitionBacklog(run));
                }
                finally{Stop(worker,client);}
            });
            suite.Run("native acquisition FIFO overrun stops admission instead of silently dropping",()=>{
                var clock=new MonotonicClock("acquisition-overflow");var client=new Client();
                var worker=new ViveProEyeWorker(clock,()=>null,()=>client);object run;
                try
                {
                    Check.True(worker.Start());Check.True(client.Waiting.WaitOne(3000));
                    Check.True(worker.TryEnableStatusEvidence(16,new ViveRawRevocation(),out run,true));client.Send(null);
                    run.GetType().GetField("AcquisitionQueue",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(run,new ViveRawEyeAcquisition[2]);
                    client.Send(Eye(clock.Now,1));client.Send(Eye(clock.Now,2));client.Send(Eye(clock.Now,3));
                    Check.Equal(ViveProEyeWorker.StatusDisposition.Faulted,worker.ReadAcquisitionBacklog(run));
                    ViveProEyePoll frame;Check.True(!worker.TryPollForRecording(out frame));
                }
                finally{Stop(worker,client);}
            });
            suite.Run("native polling wait is interruptible without changing global timer policy",()=>{
                using(var stop=new ManualResetEvent(true))using(var delay=new ViveNativePollDelay(stop))
                    Check.True(delay.Wait(5));
            });
        }
    }
}
