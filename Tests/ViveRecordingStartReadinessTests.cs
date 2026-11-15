using System;
using System.Collections.Generic;
using System.Threading;
using AvatarStroop.Core;
using AvatarStroop.Runtime;
using Reason=AvatarStroop.Runtime.ViveProEyeWorker.RecordingStartReadinessReason;
using State=AvatarStroop.Runtime.ViveProEyeWorker.RecordingStartReadinessState;

namespace AvatarStroop.Tests
{
    public static class ViveRecordingStartReadinessTests
    {
        sealed class Client : IViveProEyeClient
        {
            readonly Queue<ViveProEyePoll> values=new Queue<ViveProEyePoll>();
            internal readonly AutoResetEvent Waiting=new AutoResetEvent(false),Permit=new AutoResetEvent(false);
            public string Diagnostic {get{return "synthetic startup eye client";}}
            public bool Start(){return true;}
            public ViveProEyePoll Poll()
            {Waiting.Set();Wait(Permit);lock(values)return values.Dequeue();}
            internal void Queue(ViveProEyePoll value){lock(values)values.Enqueue(value);Permit.Set();}
            internal void Send(ViveProEyePoll value){Queue(value);Wait(Waiting);}
            public void Dispose(){}
        }
        static void Wait(WaitHandle value)
        {Check.True(value.WaitOne(3000),"synthetic startup eye poll timed out");}
        static ViveProEyePoll Poll(double now,int frame,ViveEyeCalibrationState calibration,bool direction,int? result=0)
        {
            var ray=new ViveEyeRay(direction?2UL:0UL,false,direction,new Vec3(0,0,0),new Vec3(0,0,1));
            return new ViveProEyePoll(null,new ViveEyeFrame(frame,frame*8,now,false,null,null,ray),
                ViveEyeRuntimeState.Working,calibration,true,result,2);
        }
        static void Good(RecordingStartStreak streak,double time,int number)
        {streak.Observe(true,time,Reason.None,number,number*8,time);}
        static void InitialReady(RecordingStartStreak streak)
        {Good(streak,0,1);Good(streak,.25,2);Good(streak,.5,3);Check.Equal(Reason.None,streak.Read(.5));}
        static void RunClientChecks()
        {
            var clock=new MonotonicClock("synthetic-start-readiness");
            var first=new Client();var next=new Client();
            int factories=0;
            var worker=new ViveProEyeWorker(clock,()=>null,()=>++factories==1?first:next);
            Client active=first;
            try
            {
                Check.Equal(State.Unavailable,worker.ReadRecordingStartReadiness().State);
                Check.True(worker.Start());Wait(first.Waiting);
                long firstGeneration=worker.RecordingStartGeneration;
                Check.Equal(Reason.AwaitingObservation,worker.ReadRecordingStartReadiness().Reason);
                first.Send(Poll(clock.Now,1,ViveEyeCalibrationState.Required,false));
                Check.Equal(Reason.CalibrationRequired,worker.ReadRecordingStartReadiness().Reason);
                first.Send(Poll(clock.Now,2,ViveEyeCalibrationState.ReportedReady,false));
                Check.Equal(Reason.DirectionUnavailable,worker.ReadRecordingStartReadiness().Reason);
                first.Send(Poll(clock.Now,3,ViveEyeCalibrationState.ReportedReady,true,null));
                Check.Equal(Reason.NativeStatusInvalid,worker.ReadRecordingStartReadiness().Reason);
                first.Send(Poll(clock.Now,4,ViveEyeCalibrationState.ReportedReady,true));
                lock(worker.RecordingTicketGate)
                {
                    var health=worker.ReadRecordingStartReadiness();
                    Check.Equal(State.Waiting,health.State);
                    Check.Equal(Reason.Stabilizing,health.Reason);Check.True(health.RunIdentity==null);
                }
                // Publish distinct exact Poll objects through the real fake
                // provider loop. Core threshold boundaries separately use
                // deterministic timestamps; this only checks integration.
                double deadline=clock.Now+4;
                int frame=5;var ready=worker.ReadRecordingStartReadiness();
                while(!ready.Ready&&clock.Now<deadline)
                {
                    Thread.Sleep(20);
                    first.Send(Poll(clock.Now,frame++,ViveEyeCalibrationState.ReportedReady,true));
                    ready=worker.ReadRecordingStartReadiness();
                }
                object run;Check.True(ready.Ready&&worker.TryGetRecordingRunIdentity(out run)&&
                    Object.ReferenceEquals(ready.RunIdentity,run),"real fake worker did not reach healthy exact-run readiness");
                first.Send(Poll(clock.Now,frame++,ViveEyeCalibrationState.Required,false));
                var invalid=worker.ReadRecordingStartReadiness();
                Check.True(!invalid.Ready&&invalid.RunIdentity==null&&invalid.Reason==Reason.CalibrationRequired,
                    "later invalid provider return retained a ready worker snapshot");
                using(var held=new ManualResetEvent(false))using(var release=new ManualResetEvent(false))
                {
                    var contender=new Thread(()=>{lock(worker.RecordingTicketGate){held.Set();release.WaitOne(3000);}}){IsBackground=true};
                    contender.Start();
                    try{Wait(held);Check.Equal(State.Busy,worker.ReadRecordingStartReadiness().State);}
                    finally{release.Set();Check.True(contender.Join(3000));}
                }
                Check.True(worker.TryRequestStop());first.Queue(null);Check.True(worker.Stop(1000));active=null;
                Check.Equal(State.Unavailable,worker.ReadRecordingStartReadiness().State);
                Check.True(worker.RecordingStartGeneration!=firstGeneration);
                long stoppedGeneration=worker.RecordingStartGeneration;
                Check.True(worker.Start());active=next;Wait(next.Waiting);
                Check.True(worker.RecordingStartGeneration!=stoppedGeneration);
                // An explicit stop revokes future status recording from this
                // instance; a reinitialized preview is not fresh authority.
                Check.Equal(State.Unavailable,worker.ReadRecordingStartReadiness().State);
            }
            finally
            {
                if(active!=null){worker.TryRequestStop();active.Queue(null);worker.Stop(1000);}
                worker.Dispose();first.Waiting.Dispose();first.Permit.Dispose();next.Waiting.Dispose();next.Permit.Dispose();
            }
        }

        public static void Register(TestSuite suite)
        {
            suite.Run("raw preflight refuses native calibration and invalid direction before any status queue",RunClientChecks);
            suite.Run("raw preflight healthy 0.5s window must span at least 3 advancing provider frames",()=>{
                var s=new RecordingStartStreak();Check.Equal(Reason.AwaitingObservation,s.Read(0));
                Good(s,0,1);Good(s,.25,2);Good(s,.375,3);
                Check.Equal(Reason.Stabilizing,s.Read(.5));
                Good(s,.5,4);Check.Equal(Reason.None,s.Read(.5));
                Check.Equal(Reason.None,s.Read(.75));Check.Equal(Reason.Stale,s.Read(.751));
            });
            suite.Run("raw preflight duplicates do not count or extend actual distinct frame freshness",()=>{
                var s=new RecordingStartStreak();InitialReady(s);
                Good(s,.625,3);Good(s,.75,3);
                Check.Equal(Reason.None,s.Read(.75));Check.Equal(Reason.Stale,s.Read(.76));
                Good(s,.8,3);Check.Equal(Reason.Stale,s.Read(.8));
                Good(s,.9,4);Check.Equal(Reason.Stabilizing,s.Read(.9));
                Good(s,1d,5);Good(s,1.125,6);Good(s,1.25,7);
                Check.Equal(Reason.Stabilizing,s.Read(1.25));
            });
            suite.Run("raw preflight bad status and time break the streak, require a new verified window",()=>{
                var s=new RecordingStartStreak();InitialReady(s);
                s.Observe(true,.55,Reason.CalibrationRequired,4,32,.55);
                Check.Equal(Reason.CalibrationRequired,s.Read(.55));
                Good(s,.625,3);Check.Equal(Reason.Stabilizing,s.Read(.625));
                Good(s,.75,4);Good(s,1,5);Good(s,1.25,6);Check.Equal(Reason.None,s.Read(1.25));
                s.Observe(false,Double.NaN,Reason.None,7,56,1.3);Check.Equal(Reason.InvalidTime,s.Read(1.3));
                s.Observe(true,1.36,Reason.None,7,56,1.4);Check.Equal(Reason.InvalidTime,s.Read(1.36));
                s.Observe(true,1.37,Reason.None,7,56,1.2);Check.Equal(Reason.InvalidTime,s.Read(1.37));
                Good(s,1.5,7);Check.Equal(Reason.Stabilizing,s.Read(1.5));
            });
            suite.Run("raw preflight source counter discontinuity and poll gap force a fresh window",()=>{
                var s=new RecordingStartStreak();InitialReady(s);
                s.Observe(true,.625,Reason.None,4,24,.625);
                Check.Equal(Reason.FrameDiscontinuity,s.Read(.625));
                Good(s,.75,4);Check.Equal(Reason.FrameDiscontinuity,s.Read(.75));
                Good(s,.8,5);Check.Equal(Reason.Stabilizing,s.Read(.8));
                Good(s,1,6);Good(s,1.5,7);Check.Equal(Reason.Stabilizing,s.Read(1.5));
                Check.Equal(Reason.InvalidTime,s.Read(1.49));
            });
        }
    }
}
