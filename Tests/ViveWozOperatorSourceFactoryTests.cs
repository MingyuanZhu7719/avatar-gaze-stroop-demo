using System;
using System.Threading;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    public static class ViveWozOperatorSourceFactoryTests
    {
        sealed class Provider : IViveProEyeProvider
        {public int Calls;public ViveProEyePoll Poll(){Calls++;return null;}}
        static TaskSnapshot Task(int block=0,int segment=0,Condition condition=Condition.A,Phase phase=Phase.MainTask,string session="woz-session")
        {return new TaskSnapshot(session,block,segment,"trial",Colour.Red,Colour.Blue,phase,condition);}
        sealed class WorkerClient : IViveProEyeClient
        {
            readonly MonotonicClock clock;
            int sequence;
            internal int Calibration=(int)ViveEyeCalibrationState.ReportedReady;
            internal WorkerClient(MonotonicClock clock){this.clock=clock;}
            public bool Start(){return true;}
            public string Diagnostic {get{return "Synthetic operator fixture";}}
            public ViveProEyePoll Poll()
            {
                int next=Interlocked.Increment(ref sequence);
                var ray=new ViveEyeRay(2,true,true,new Vec3(0,0,0),new Vec3(0,0,1));
                return new ViveProEyePoll(null,new ViveEyeFrame(next,next,clock.Now,false,ray,ray,ray),
                    ViveEyeRuntimeState.Working,(ViveEyeCalibrationState)Volatile.Read(ref Calibration),true,0,0);
            }
            public void Dispose(){}
        }
        sealed class WorkerFixture : IDisposable
        {
            internal readonly MonotonicClock Clock=new MonotonicClock("operator-worker-fixture");
            internal readonly WorkerClient Client;
            internal readonly ViveProEyeWorker Worker;
            internal ViveLegacyWozObservationOwner Source;
            internal long Heads;
            internal WorkerFixture(bool bind=true)
            {
                Client=new WorkerClient(Clock);
                Worker=new ViveProEyeWorker(Clock,()=>new ViveHeadFrame(Heads++,Clock.Now,true,true,Rotation.Identity),()=>Client);
                Check.True(Worker.Start());Check.True(SpinWait.SpinUntil(()=>Worker.Started,3000));
                if(bind)Bind();
            }
            internal void Bind(){Source=ViveLegacyWozObservationOwner.BindSyntheticWorker("woz-session","epoch",Clock,Worker,1);}
            internal ViveLegacyWozObservationOwner.Candidate Fresh()
            {
                double received=Clock.Now;ViveLegacyWozObservationOwner.Candidate result=null;
                Check.True(SpinWait.SpinUntil(()=>Source.TryRead(new WozLanTaskContext(Task(),0,0,0),received,out result),3000),"Fresh source candidate: "+Source.LastIssue);
                return result;
            }
            public void Dispose()
            {
                if(Source!=null)Source.Dispose();Worker.Stop();
                Check.True(SpinWait.SpinUntil(()=>Worker.HasExited,3000));Worker.Dispose();
            }
        }
        sealed class HeldWorkerGate : IDisposable
        {
            readonly ManualResetEvent held=new ManualResetEvent(false),release=new ManualResetEvent(false);
            readonly Thread thread;
            internal HeldWorkerGate(ViveProEyeWorker worker)
            {
                thread=new Thread(()=>{lock(worker.RecordingTicketGate){held.Set();release.WaitOne(5000);}});
                thread.IsBackground=true;thread.Start();Check.True(held.WaitOne(3000));
            }
            public void Dispose(){release.Set();Check.True(thread.Join(3000));held.Dispose();release.Dispose();}
        }
        public static void Register(TestSuite suite)
        {
            suite.Run("VIVE WoZ operator source factory is explicitly configured and dormant",()=>{
                var clock=new ManualClock();var provider=new Provider();var mapper=new ViveProEyeRehearsalCoordinates();
                Check.Throws<InvalidOperationException>(()=>new ViveWozOperatorSourceFactory("woz-session","epoch",clock,provider,mapper,.1,false));
                var factory=new ViveWozOperatorSourceFactory("woz-session","epoch",clock,provider,mapper,.1,true);
                Check.Equal(ViveDevelopmentTransport.OperatorStream(mapper.Version,"epoch"),factory.StreamId);
                Check.Equal("vive-pro-eye-no-serial",factory.DeviceId);
                Check.True(!factory.CanRecordOrExport&&!factory.HardwareCaptureTimeVerified&&!factory.MappingValidatedForApparatus);
                var source=factory.Create(Task());Check.Equal(0,provider.Calls);Check.Equal(factory.StreamId,source.StreamId);
                Check.Equal(ControlSource.LiveOperator,source.Source);Check.Equal(StreamRole.OperatorControl,source.Role);
                Check.True(Object.ReferenceEquals(source,factory.Create(Task())));Check.Equal(0,provider.Calls);
            });
            suite.Run("VIVE WoZ operator source factory fails closed for invalid or regressed task contexts",()=>{
                var clock=new ManualClock();var provider=new Provider();var factory=new ViveWozOperatorSourceFactory("woz-session","epoch",clock,provider,new ViveProEyeRehearsalCoordinates(),.1,true);
                Check.Throws<InvalidOperationException>(()=>factory.Create(null));
                Check.Throws<InvalidOperationException>(()=>factory.Create(Task(session:"wrong-session")));
                Check.Throws<InvalidOperationException>(()=>factory.Create(Task(phase:Phase.BlockReady)));
                Check.Throws<InvalidOperationException>(()=>factory.Create(Task(condition:Condition.D)));
                var first=factory.Create(Task(0,0));var resumed=factory.Create(Task(0,1));Check.True(!Object.ReferenceEquals(first,resumed));
                Check.True(Object.ReferenceEquals(resumed,factory.Create(Task(0,1))));
                Check.Throws<InvalidOperationException>(()=>factory.Create(Task(0,1,condition:Condition.B)));
                Check.Throws<InvalidOperationException>(()=>factory.Create(Task(0,2,condition:Condition.B)));
                var later=factory.Create(Task(2,0));Check.True(!Object.ReferenceEquals(later,resumed));
                Check.Throws<InvalidOperationException>(()=>factory.Create(Task(1,0)));
                Check.Throws<InvalidOperationException>(()=>factory.Create(Task(2,0,condition:Condition.D)));
                Check.Equal(0,provider.Calls);
            });
            suite.Run("VIVE WoZ operator source factory enforces its owning thread",()=>{
                var factory=new ViveWozOperatorSourceFactory("woz-session","epoch",new ManualClock(),new Provider(),new ViveProEyeRehearsalCoordinates(),.1,true);
                Exception failure=null;var thread=new Thread(()=>{try{factory.Create(Task());}catch(Exception error){failure=error;}});
                thread.Start();Check.True(thread.Join(3000));Check.True(failure is InvalidOperationException);
                Check.True(factory.Create(Task())!=null);
            });
            suite.Run("VIVE WoZ operator source factory latches local clock rollback",()=>{
                var clock=new ManualClock {Now=1};var factory=new ViveWozOperatorSourceFactory("woz-session","epoch",clock,new Provider(),new ViveProEyeRehearsalCoordinates(),.1,true);
                Check.True(factory.Create(Task())!=null);clock.Now=.5;
                Check.Throws<InvalidOperationException>(()=>factory.Create(Task()));clock.Now=2;
                Check.Throws<InvalidOperationException>(()=>factory.Create(Task(1,0)));
            });
            suite.Run("VIVE WoZ worker lock contention preserves binding without emitting a pose or proof",()=>{
                using(var f=new WorkerFixture())
                {
                    var first=f.Fresh();long heads=f.Heads;
                    using(var held=new HeldWorkerGate(f.Worker))
                    {
                        Check.True(f.Source.BindingCurrent&&!f.Source.Terminal);
                        ViveLegacyWozObservationOwner.Candidate candidate;
                        Check.True(!f.Source.TryRead(new WozLanTaskContext(Task(),0,0,0),0,out candidate)&&candidate==null);
                        Check.True(!f.Source.TryReadPreflight(Task(phase:Phase.BlockReady),0,out candidate)&&candidate==null);
                        Check.Equal(heads,f.Heads);Check.True(f.Source.BindingCurrent&&!f.Source.Terminal&&f.Source.LastIssue==null);
                    }
                    var next=f.Fresh();Check.Equal(first.Sample.Sequence+1,next.Sample.Sequence);
                    Check.True(next.NativeEyeSequence>first.NativeEyeSequence&&next.EyeObserved>first.EyeObserved);
                }
            });
            suite.Run("VIVE WoZ startup waits for the same worker generation when the gate is busy",()=>{
                using(var f=new WorkerFixture(false))
                {
                    using(var held=new HeldWorkerGate(f.Worker))
                    {
                        f.Bind();Check.True(f.Source.BindingCurrent&&!f.Source.Terminal);
                        ViveLegacyWozObservationOwner.Candidate candidate;
                        Check.True(!f.Source.TryReadPreflight(Task(phase:Phase.BlockReady),0,out candidate)&&candidate==null);
                        Check.Equal(0L,f.Heads);
                    }
                    Check.Equal(0L,f.Fresh().Sample.Sequence);
                }
            });
            suite.Run("VIVE WoZ contention after a poll discards the candidate without advancing emissions",()=>{
                using(var f=new WorkerFixture())
                {
                    var first=f.Fresh();HeldWorkerGate held=null;
                    f.Source.BeforeCandidateIdentityCheckForTest=()=>{held=new HeldWorkerGate(f.Worker);};
                    try
                    {
                        ViveLegacyWozObservationOwner.Candidate candidate=null;
                        Check.True(SpinWait.SpinUntil(()=>{
                            bool read=f.Source.TryRead(new WozLanTaskContext(Task(),0,0,0),0,out candidate);
                            Check.True(!read&&candidate==null);return held!=null;
                        },3000));
                        Check.True(f.Source.BindingCurrent&&!f.Source.Terminal&&f.Source.LastIssue==null);
                    }
                    finally{f.Source.BeforeCandidateIdentityCheckForTest=null;if(held!=null)held.Dispose();}
                    var next=f.Fresh();Check.Equal(first.Sample.Sequence+1,next.Sample.Sequence);
                    Check.True(next.NativeEyeSequence>first.NativeEyeSequence);
                }
            });
            suite.Run("VIVE WoZ stop generation remains terminal while the worker gate is busy",()=>{
                using(var f=new WorkerFixture())
                {
                    f.Fresh();
                    using(var held=new HeldWorkerGate(f.Worker))
                    {
                        Check.True(!f.Worker.TryRequestStop());
                        Check.True(!f.Source.BindingCurrent&&f.Source.Terminal);
                        Check.Equal("provider_or_worker_run_lost",f.Source.LastIssue);
                        ViveLegacyWozObservationOwner.Candidate candidate;
                        Check.True(!f.Source.TryReadPreflight(Task(phase:Phase.BlockReady),0,out candidate)&&candidate==null);
                    }
                }
            });
            suite.Run("VIVE WoZ pending startup cannot inherit a stopped or restarted worker",()=>{
                using(var f=new WorkerFixture(false))
                {
                    using(var held=new HeldWorkerGate(f.Worker)){f.Bind();Check.True(!f.Worker.TryRequestStop());}
                    Check.True(SpinWait.SpinUntil(()=>f.Worker.HasExited,3000));
                    Check.True(f.Worker.Start());Check.True(SpinWait.SpinUntil(()=>f.Worker.Started,3000));
                    Check.True(!f.Source.BindingCurrent&&f.Source.Terminal);
                    Check.Equal("provider_or_worker_run_lost",f.Source.LastIssue);
                }
            });
            suite.Run("VIVE WoZ calibration loss blocks candidates without masquerading as worker loss",()=>{
                using(var f=new WorkerFixture())
                {
                    f.Fresh();Volatile.Write(ref f.Client.Calibration,(int)ViveEyeCalibrationState.Required);
                    Check.True(SpinWait.SpinUntil(()=>f.Worker.Poll().Calibration==ViveEyeCalibrationState.Required,3000));
                    ViveLegacyWozObservationOwner.Candidate candidate;
                    Check.True(!f.Source.TryReadPreflight(Task(phase:Phase.BlockReady),0,out candidate)&&candidate==null);
                    Check.True(f.Source.BindingCurrent&&!f.Source.Terminal&&f.Source.LastIssue==null);
                    Volatile.Write(ref f.Client.Calibration,(int)ViveEyeCalibrationState.ReportedReady);
                    Check.True(f.Fresh()!=null);
                }
            });
            suite.Run("VIVE WoZ actual observation lease loss is still terminal",()=>{
                using(var f=new WorkerFixture())
                {
                    f.Source.LoseLeaseForTest();Check.True(!f.Source.BindingCurrent&&f.Source.Terminal);
                    Check.Equal("provider_or_worker_run_lost",f.Source.LastIssue);
                }
            });
        }
    }
}
