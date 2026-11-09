using System;
using System.Threading;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    public static class ViveProEyeClockGuardTests
    {
        sealed class Clock:IClock
        {
            public double Time=10;
            public string Id="clock-a";
            public bool ThrowTime,ThrowId,FlipIdOnSecondRead;
            public int TimeReads,IdReads;
            public double Now {get{TimeReads++;if(ThrowTime)throw new InvalidOperationException("private time");return Time;}}
            public string ClockId {get{IdReads++;if(ThrowId)throw new InvalidOperationException("private id");return FlipIdOnSecondRead&&IdReads==2?"other-clock":Id;}}
        }
        sealed class Provider:IViveProEyeProvider
        {
            public int Calls;
            public ViveProEyePoll Next;
            public bool Throw;
            public Action DuringPoll;
            public ManualResetEvent Entered,Release;
            public ViveProEyePoll Poll()
            {
                Calls++;
                if(Entered!=null){Entered.Set();if(!Release.WaitOne(3000))throw new TimeoutException();}
                if(DuringPoll!=null)DuringPoll();
                if(Throw)throw new InvalidOperationException("private provider data");
                return Next;
            }
        }
        static ViveProEyePoll Sample(bool eye=true)
        {
            var head=new ViveHeadFrame(1,10,true,true,Rotation.Identity);
            var ray=new ViveEyeRay(2,false,true,new Vec3(0,0,0),new Vec3(0,0,1));
            var frame=eye?new ViveEyeFrame(1,1,10,false,null,null,ray):null;
            return new ViveProEyePoll(head,frame,ViveEyeRuntimeState.Working,ViveEyeCalibrationState.ReportedReady,true,0);
        }
        static ViveProEyeHeadEyeSource Source(Clock clock,Provider provider)
        {return new ViveProEyeHeadEyeSource("session",0,0,"vive-pro-eye/operator/clock-test",clock,provider,new ViveProEyeRehearsalCoordinates(),.2);}
        static void ReadOnce(ViveProEyeHeadEyeSource source)
        {HeadEyeSample sample;Check.True(source.TryRead(out sample)&&sample!=null);}
        public static void Register(TestSuite suite)
        {
            suite.Run("VIVE availability latches clock rollback across getter observations",()=>{
                var clock=new Clock();var provider=new Provider{Next=Sample()};var source=Source(clock,provider);ReadOnce(source);
                clock.Time=10.1;Check.True(source.Available);clock.Time=10.05;Check.True(!source.Available);
                clock.Time=10.15;Check.True(!source.Available);Check.True(source.UnavailableReason.Contains("clock failed"));
                HeadEyeSample sample;Check.True(!source.TryRead(out sample)&&sample==null);Check.Equal(1,provider.Calls);
            });
            suite.Run("VIVE unavailable status still observes and permanently latches clock rollback",()=>{
                var clock=new Clock();var provider=new Provider{Next=Sample()};var source=Source(clock,provider);
                clock.Time=11;Check.True(!source.Available);clock.Time=10.5;Check.True(!source.Available);
                clock.Time=11.1;HeadEyeSample sample;Check.True(!source.TryRead(out sample));Check.Equal(0,provider.Calls);
            });
            suite.Run("VIVE clock identity change remains failed after identity is restored",()=>{
                var clock=new Clock();var provider=new Provider{Next=Sample()};var source=Source(clock,provider);ReadOnce(source);
                clock.Id="clock-b";Check.True(!source.Available);clock.Id="clock-a";Check.True(!source.Available);
                HeadEyeSample sample;Check.True(!source.TryRead(out sample));Check.Equal(1,provider.Calls);
            });
            suite.Run("VIVE clock getter exceptions latch without exposing messages or polling again",()=>{
                foreach(bool failId in new[]{false,true})
                {
                    var clock=new Clock();var provider=new Provider{Next=Sample()};var source=Source(clock,provider);ReadOnce(source);
                    if(failId)clock.ThrowId=true;else clock.ThrowTime=true;
                    Check.True(!source.Available);clock.ThrowId=false;clock.ThrowTime=false;
                    int reads=clock.TimeReads+clock.IdReads;HeadEyeSample sample;
                    Check.True(!source.Available&&!source.TryRead(out sample));Check.Equal(reads,clock.TimeReads+clock.IdReads);
                    Check.Equal(1,provider.Calls);Check.True(!source.UnavailableReason.Contains("private"));
                }
            });
            suite.Run("VIVE clock failure at read start or completion latches before another poll",()=>{
                foreach(bool atCompletion in new[]{false,true})foreach(bool failId in new[]{false,true})
                {
                    var clock=new Clock();var provider=new Provider{Next=Sample()};var source=Source(clock,provider);
                    Action fail=()=>{if(failId)clock.ThrowId=true;else clock.ThrowTime=true;};
                    if(atCompletion)provider.DuringPoll=fail;else fail();
                    HeadEyeSample sample;Check.True(!source.TryRead(out sample)&&sample==null);
                    int calls=provider.Calls;Check.Equal(atCompletion?1:0,calls);
                    clock.ThrowId=false;clock.ThrowTime=false;provider.DuringPoll=null;
                    Check.True(!source.Available&&!source.TryRead(out sample));Check.Equal(calls,provider.Calls);
                }
            });
            suite.Run("VIVE clock failure after provider exception latches without leaking either error",()=>{
                var clock=new Clock();var provider=new Provider{Next=Sample(),Throw=true};var source=Source(clock,provider);
                provider.DuringPoll=()=>clock.ThrowTime=true;HeadEyeSample sample;
                Check.True(!source.TryRead(out sample)&&sample==null);clock.ThrowTime=false;provider.Throw=false;provider.DuringPoll=null;
                Check.True(!source.Available&&!source.TryRead(out sample));Check.Equal(1,provider.Calls);
                Check.True(!source.UnavailableReason.Contains("private"));
            });
            suite.Run("VIVE nonfinite and negative clock values permanently reject the epoch",()=>{
                foreach(double value in new[]{Double.NaN,Double.PositiveInfinity,Double.NegativeInfinity,-1.0})
                {
                    var clock=new Clock();var provider=new Provider{Next=Sample()};var source=Source(clock,provider);
                    clock.Time=value;Check.True(!source.Available);clock.Time=10.1;
                    HeadEyeSample sample;Check.True(!source.Available&&!source.TryRead(out sample));Check.Equal(0,provider.Calls);
                }
            });
            suite.Run("VIVE initialization rejects a changing clock identity",()=>{
                var clock=new Clock{FlipIdOnSecondRead=true};var provider=new Provider{Next=Sample()};
                Check.Throws<ArgumentException>(()=>Source(clock,provider));Check.Equal(0,provider.Calls);
            });
            suite.Run("VIVE timestamp guard preserves independent single-axis availability",()=>{
                var clock=new Clock();var provider=new Provider{Next=Sample(false)};var source=Source(clock,provider);ReadOnce(source);
                Check.True(source.LastObservation.HeadUsable&&!source.LastObservation.EyeUsable&&source.Available);
            });
            suite.Run("VIVE clock guard does not hold its lock across provider polling",()=>{
                var clock=new Clock();using(var entered=new ManualResetEvent(false))using(var release=new ManualResetEvent(false))
                {
                    var provider=new Provider{Next=Sample(),Entered=entered,Release=release};var source=Source(clock,provider);
                    Exception workerError=null;bool workerResult=false;
                    var thread=new Thread(()=>{try{HeadEyeSample sample;workerResult=source.TryRead(out sample);}catch(Exception e){workerError=e;}});
                    thread.IsBackground=true;thread.Start();
                    try{Check.True(entered.WaitOne(2000));Check.True(!source.Available);}
                    finally{release.Set();}
                    Check.True(thread.Join(3000));Check.True(workerError==null&&workerResult);
                }
            });
        }
    }
}
