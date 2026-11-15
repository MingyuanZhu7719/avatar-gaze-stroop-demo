using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    public static class SrAnipalCallbackTests
    {
        sealed class Clock : IClock
        {
            internal double Value;internal Action ReadHook;
            public double Now {get{double value=Value;var hook=ReadHook;ReadHook=null;if(hook!=null)hook();return value;}}
            public string ClockId {get{return "callback-synthetic";}}
        }
        sealed class Native : ISrAnipalNativeClient,ISrAnipalCallbackNativeClient
        {
            internal readonly Clock Clock;
            internal bool Required;internal int RegisterResult,UnregisterResult;
            internal int Reads,Queries,Registered,Unregistered,Releases,Closes,Sequence;
            internal Delegate Callback;
            internal Action DuringUnregister;
            internal Action DuringStatus;
            internal Native(Clock clock){Clock=clock;}
            public void Open(){}
            public int Initialize(){return 0;}
            public int GetStatus(out int value){Clock.Value+=.001;Queries++;if(DuringStatus!=null)DuringStatus();value=2;return 0;}
            public bool IsViveProEye(){Clock.Value+=.001;return true;}
            public int NeedsCalibration(ref bool value){Clock.Value+=.001;value=Required;return 0;}
            public int Read(IntPtr data){Reads++;throw new Exception("Polling read entered in callback mode");}
            public int RegisterEyeCallback(IntPtr pointer)
            {
                Registered++;var type=typeof(SrAnipalViveProEyeProvider).GetNestedType("NativeEyeCallback",BindingFlags.NonPublic);
                Callback=Marshal.GetDelegateForFunctionPointer(pointer,type);return RegisterResult;
            }
            public int UnregisterEyeCallback(IntPtr pointer){Unregistered++;if(DuringUnregister!=null)DuringUnregister();return UnregisterResult;}
            public int Release(){Releases++;return 0;}
            public void Close(){Closes++;}
            internal void Emit(bool invalid=false)
            {
                IntPtr data=Marshal.AllocHGlobal(240);
                try
                {
                    for(int i=0;i<240;i+=8)Marshal.WriteInt64(data,i,0);
                    Marshal.WriteByte(data,0,1);Marshal.WriteInt32(data,4,++Sequence);Marshal.WriteInt32(data,8,Sequence*8);
                    foreach(int offset in new[]{16,64,112})
                    {Marshal.WriteInt64(data,offset,invalid?0:3);Marshal.Copy(BitConverter.GetBytes(1f),0,IntPtr.Add(data,offset+28),4);}
                    Callback.DynamicInvoke(data);
                }
                finally{Marshal.FreeHGlobal(data);}
            }
        }
        public static void Register(TestSuite suite)
        {
            suite.Run("SRanipal callbacks preserve every queued frame and actual registration provenance without GetEyeData",()=>{
                var clock=new Clock();var native=new Native(clock);
                using(var provider=new SrAnipalViveProEyeProvider(clock,()=>null,native))
                {
                    Check.True(provider.Start());
                    for(int i=0;i<3;i++){clock.Value+=.008;native.Emit(i==1);}
                    for(int i=0;i<3;i++)
                    {
                        var poll=provider.Poll();Check.Equal(i+1,poll.Eye.FrameSequence);
                        Check.Equal(i==1?0UL:3UL,poll.Eye.Combined.ValidityMask);var d=poll.NativeDiagnostics;
                        Check.True(d.CallbackBased);Check.Equal("native_callback",d.CaptureMode);Check.Equal("callback_registration",d.NativeResultSource);
                        Check.Equal((int?)0,d.CallbackRegistrationResult);Check.Equal((int?)0,poll.NativeResultCode);Check.Equal((int?)null,d.ReadResult);
                        Check.Equal((double?)null,d.ReadStartedLocalSeconds);Check.Near(poll.Eye.ObservedLocalSeconds,d.CallbackObservedLocalSeconds.Value);
                        Check.True(d.HealthQueryCompletedLocalSeconds<=poll.Eye.ObservedLocalSeconds);
                        Check.True(d.PollStartedLocalSeconds>=poll.Eye.ObservedLocalSeconds);
                    }
                    Check.Equal(0,native.Reads);Check.Equal(1,native.Queries);Check.Equal(null,provider.Poll().Eye);
                    provider.Stop();Check.Equal(1,native.Unregistered);Check.Equal(1,native.Releases);Check.Equal(1,native.Closes);
                    native.Emit();Check.True(!provider.AcquisitionFaulted); // late managed test callback is ignored
                }
            });
            suite.Run("new calibration requirement overrides historical Ready only for live gates, preserving archived health",()=>{
                var clock=new Clock();var native=new Native(clock);
                using(var provider=new SrAnipalViveProEyeProvider(clock,()=>null,native))
                {
                    Check.True(provider.Start());clock.Value+=.008;native.Emit();native.Required=true;clock.Value+=.11;
                    var archive=provider.Poll();Check.Equal(ViveEyeCalibrationState.ReportedReady,archive.Calibration);
                    ViveProEyePoll live;Check.True(((IViveProEyeLiveSnapshot)provider).TryGetLiveSnapshot(out live));
                    Check.Equal(ViveEyeCalibrationState.Required,live.Calibration);Check.Equal(null,live.Eye);
                    clock.Value+=.008;native.Emit();var required=provider.Poll();Check.Equal(ViveEyeCalibrationState.Required,required.Calibration);
                    Check.Equal((bool?)true,required.NativeDiagnostics.CalibrationNeeded);
                    native.Required=false;clock.Value+=.11;provider.Poll();
                    Check.True(((IViveProEyeLiveSnapshot)provider).TryGetLiveSnapshot(out live));Check.Equal(null,live.Eye);
                    clock.Value+=.008;native.Emit();provider.Poll();
                    Check.True(((IViveProEyeLiveSnapshot)provider).TryGetLiveSnapshot(out live));Check.True(live.Eye!=null);
                    Check.Equal(ViveEyeCalibrationState.ReportedReady,live.Calibration);
                }
            });
            suite.Run("equivalent repeated health queries preserve a continuous half-second recording-ready streak",()=>{
                var clock=new Clock();var native=new Native(clock);var streak=new RecordingStartStreak();
                using(var provider=new SrAnipalViveProEyeProvider(clock,()=>null,native))
                {
                    Check.True(provider.Start());
                    for(int i=0;i<70;i++)
                    {
                        clock.Value+=.01;native.Emit();provider.Poll();ViveProEyePoll live;
                        Check.True(((IViveProEyeLiveSnapshot)provider).TryGetLiveSnapshot(out live));Check.True(live.Eye!=null,"Repeated same health invented an eye gap");
                        streak.Observe(true,clock.Now,ViveProEyeWorker.RecordingStartReadinessReason.None,
                            live.Eye.FrameSequence,live.Eye.Timestamp,live.Eye.ObservedLocalSeconds);
                    }
                    Check.True(native.Queries>=6);Check.Equal(ViveProEyeWorker.RecordingStartReadinessReason.None,streak.Read(clock.Now));
                }
            });
            suite.Run("callback receipt keeps earlier health if a query completes during its clock read",()=>{
                var clock=new Clock();var native=new Native(clock);
                using(var provider=new SrAnipalViveProEyeProvider(clock,()=>null,native))
                {
                    Check.True(provider.Start());clock.Value+=.2;clock.ReadHook=()=>provider.Poll();native.Emit();
                    Check.True(!provider.AcquisitionFaulted);var poll=provider.Poll();Check.True(poll.Eye!=null);
                    Check.True(poll.NativeDiagnostics.HealthQueryCompletedLocalSeconds<poll.Eye.ObservedLocalSeconds);
                }
            });
            suite.Run("stale callback health is explicit Unknown and never refreshed by dequeue",()=>{
                var clock=new Clock();var native=new Native(clock);
                using(var provider=new SrAnipalViveProEyeProvider(clock,()=>null,native))
                {
                    Check.True(provider.Start());clock.Value+=.3;native.Emit();double observed=clock.Now;
                    var poll=provider.Poll();Check.Near(observed,poll.Eye.ObservedLocalSeconds);
                    Check.Equal(ViveEyeCalibrationState.Unknown,poll.Calibration);Check.Equal(ViveEyeRuntimeState.Unavailable,poll.Runtime);
                    Check.True(poll.NativeDiagnostics.HealthQueryCompletedLocalSeconds<observed-.25);
                }
            });
            suite.Run("callback queue overflow is latched instead of overwriting old frames",()=>{
                var clock=new Clock();var native=new Native(clock);
                using(var provider=new SrAnipalViveProEyeProvider(clock,()=>null,native))
                {
                    Check.True(provider.Start());for(int i=0;i<2049;i++)native.Emit();Check.True(provider.AcquisitionFaulted);
                    Check.Equal(ViveEyeRuntimeState.Error,provider.Poll().Runtime);
                }
            });
            suite.Run("graceful callback seal keeps the unregister tail and never falls back to polling",()=>{
                var clock=new Clock();var native=new Native(clock);
                using(var provider=new SrAnipalViveProEyeProvider(clock,()=>null,native))
                {
                    Check.True(provider.Start());native.Emit();native.Emit();
                    native.DuringUnregister=()=>{native.Emit();native.Emit();};
                    var seal=(IViveProEyeAcquisitionSeal)provider;Check.True(seal.RequestAcquisitionSeal());
                    Check.True(!seal.AcquisitionSealDrained);Check.True(seal.RequestAcquisitionSeal());Check.Equal(1,native.Unregistered);
                    for(int i=1;i<=4;i++)Check.Equal(i,provider.Poll().Eye.FrameSequence);
                    Check.True(seal.AcquisitionSealDrained);Check.Equal(null,provider.Poll().Eye);Check.Equal(0,native.Reads);
                    provider.Stop();Check.Equal(1,native.Unregistered);Check.Equal(1,native.Releases);
                }
            });
            suite.Run("worker bursts callback backlog accumulated during a slow health query",()=>{
                var clock=new MonotonicClock("slow-health-callback");var native=new Native(new Clock());
                using(var queried=new ManualResetEvent(false))
                {
                    native.DuringStatus=()=>{
                        if(native.Queries!=2)return;
                        queried.Set();for(int i=0;i<64;i++)native.Emit();Thread.Sleep(80);
                    };
                    var provider=new SrAnipalViveProEyeProvider(clock,()=>null,native);
                    var worker=new ViveProEyeWorker(clock,()=>null,()=>provider);
                    try
                    {
                        Check.True(worker.Start());Check.True(queried.WaitOne(2000));
                        Check.True(SpinWait.SpinUntil(()=>worker.CaptureDiagnostics.SuccessfulEyeReads==64,200),
                            "A fixed wait per queued callback throttled backlog drain after the slow query");
                        Check.Equal(0,native.Reads);Check.True(!provider.AcquisitionFaulted);
                    }
                    finally{Check.True(worker.Stop(1000));worker.Dispose();}
                }
            });
            suite.Run("callback registration or unregister uncertainty retains native resources and blocks restart",()=>{
                foreach(bool registration in new[]{false,true})
                {
                    var clock=new Clock();var native=new Native(clock) {RegisterResult=registration?1036:0,UnregisterResult=registration?0:1036};
                    var provider=new SrAnipalViveProEyeProvider(clock,()=>null,native);
                    Check.Equal(!registration,provider.Start());provider.Stop();Check.True(provider.Quarantined);
                    Check.Equal(0,native.Releases);Check.Equal(0,native.Closes);Check.True(!provider.Start());
                    native.Emit();provider.Dispose();
                }
            });
        }
    }
}
