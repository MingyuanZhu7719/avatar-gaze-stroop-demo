using System;
using System.Reflection;
using System.Runtime.InteropServices;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    public static class SrAnipalInteropTests
    {
        delegate int StatusProbe(int selector,out int status);
        static void BindManagedDelegate(object client,string fieldName,Delegate implementation)
        {
            var field=client.GetType().GetField(fieldName,BindingFlags.Instance|BindingFlags.NonPublic);
            field.SetValue(client,Delegate.CreateDelegate(field.FieldType,implementation.Target,implementation.Method));
        }
        static void Float(IntPtr buffer,int offset,float value)
        {Marshal.Copy(BitConverter.GetBytes(value),0,IntPtr.Add(buffer,offset),4);}
        sealed class NativeFake : ISrAnipalNativeClient
        {
            public string ThrowAt;
            public int InitResult,StatusResult,CalibrationResult,ReadResult,ReleaseResult;
            public int State=2,Sequence;
            public bool Connected=true,Required;
            public byte NativeDataPresent=1;
            public ulong CombinedMask=2;
            public int Opens,Initializes,Statuses,Devices,Calibrations,Reads,Releases,Closes;
            void Fail(string stage){if(ThrowAt==stage)throw new InvalidOperationException("Synthetic "+stage+" failure");}
            public void Open(){Opens++;Fail("open");}
            public int Initialize(){Initializes++;Fail("initialize");return InitResult;}
            public int GetStatus(out int status){Statuses++;Fail("status");status=State;return StatusResult;}
            public bool IsViveProEye(){Devices++;Fail("device");return Connected;}
            public int NeedsCalibration(ref bool needed){Calibrations++;Fail("calibration");needed=Required;return CalibrationResult;}
            public int Read(IntPtr buffer)
            {
                Reads++;Fail("read");Sequence++;
                Marshal.WriteByte(buffer,0,NativeDataPresent);
                Marshal.WriteInt32(buffer,4,Sequence);Marshal.WriteInt32(buffer,8,Sequence*8);
                Marshal.WriteInt64(buffer,112,unchecked((long)CombinedMask));Float(buffer,140,1);
                return ReadResult;
            }
            public int Release(){Releases++;Fail("release");return ReleaseResult;}
            public void Close(){Closes++;Fail("close");}
        }
        static SrAnipalViveProEyeProvider Provider(NativeFake native)
        {return new SrAnipalViveProEyeProvider(new ManualClock(),()=>null,native);}
        public static void Register(TestSuite t)
        {
            t.Run("SRanipal installed client separates eye status selector from v2 lifecycle selectors",()=>{
                // Construct only the managed dispatch object. Never call Open or load
                // a vendor DLL; each export delegate is replaced by managed test code.
                var type=typeof(SrAnipalViveProEyeProvider).GetNestedType("InstalledClient",BindingFlags.NonPublic);
                var client=(ISrAnipalNativeClient)Activator.CreateInstance(type,true);
                int initialSelector=-1,statusSelector=-1,releaseSelector=-1;
                BindManagedDelegate(client,"initial",new Func<int,IntPtr,int>((selector,configuration)=>{
                    initialSelector=selector;Check.Equal(IntPtr.Zero,configuration);return 1035;}));
                BindManagedDelegate(client,"status",new StatusProbe(delegate(int selector,out int status){
                    statusSelector=selector;status=17;return 1036;}));
                BindManagedDelegate(client,"release",new Func<int,int>(selector=>{releaseSelector=selector;return 1023;}));
                Check.Equal(1035,client.Initialize());Check.Equal(2,initialSelector);
                int state;Check.Equal(1036,client.GetStatus(out state));Check.Equal(17,state);
                Check.Equal(1023,client.Release());Check.Equal(2,releaseSelector);
                Check.Equal(0,statusSelector,"Status must resolve the initialized eye module, not fixed module zero");
            });
            t.Run("SRanipal x64 buffer decodes independent rays and opaque native time at verified offsets",()=>{
                SrAnipalViveProEyeProvider.ValidateAbi();
                var memory=Marshal.AllocHGlobal(240);
                try
                {
                    for(int i=0;i<240;i++)Marshal.WriteByte(memory,i,0);
                    Marshal.WriteByte(memory,0,1);Marshal.WriteInt32(memory,4,73);Marshal.WriteInt32(memory,8,Int32.MinValue);
                    Marshal.WriteInt64(memory,16,1);Marshal.WriteInt64(memory,64,2);Marshal.WriteInt64(memory,112,3);
                    Float(memory,24,31);Float(memory,84,-1);Float(memory,132,.6f);Float(memory,136,0);Float(memory,140,.8f);
                    var frame=SrAnipalViveProEyeProvider.DecodeEyeBuffer(memory,2.25);
                    Check.Equal(73,frame.FrameSequence);Check.Equal(Int32.MinValue,frame.Timestamp);Check.Near(2.25,frame.ObservedLocalSeconds);
                    Check.True(!frame.NoUser);Check.True(frame.Left.OriginReportedValid&&!frame.Left.DirectionReportedValid);
                    Check.True(!frame.Right.OriginReportedValid&&frame.Right.DirectionReportedValid);
                    Check.True(frame.Combined.OriginReportedValid&&frame.Combined.DirectionReportedValid);
                    Check.Near(31,frame.Left.OriginMillimeters.X);Check.Near(-1,frame.Right.Direction.X);
                    Check.Near(.6,frame.Combined.Direction.X,.000001);Check.Near(.8,frame.Combined.Direction.Z,.000001);
                }
                finally{Marshal.FreeHGlobal(memory);}
            });
            t.Run("SRanipal native positive data flag permits valid gaze and zero flag rejects without bypassing other gates",()=>{
                var clock=new ManualClock();var native=new NativeFake();long headSequence=0;
                using(var provider=new SrAnipalViveProEyeProvider(clock,
                    ()=>new ViveHeadFrame(++headSequence,clock.Now,true,true,Rotation.Identity),native))
                {
                    Check.True(provider.Start());
                    var source=new ViveProEyeHeadEyeSource("interop-presence",0,0,"vive-pro-eye/operator/interop-presence",
                        clock,provider,new ViveProEyeRehearsalCoordinates());
                    HeadEyeSample sample;
                    Check.True(source.TryRead(out sample));Check.True(sample.HeadValid&&sample.EyeValid);
                    Check.True(source.LastObservation.EyeUsable);Check.True(!source.LastObservation.RawPoll.Eye.NoUser);
                    native.NativeDataPresent=0;
                    Check.True(source.TryRead(out sample));Check.True(sample.HeadValid&&!sample.EyeValid);
                    Check.True(!source.LastObservation.EyeUsable);Check.True(source.LastObservation.RawPoll.Eye.NoUser);
                    Check.Equal("HTC reports no available eye data.",provider.Diagnostic);
                    native.NativeDataPresent=1;native.CombinedMask=0;
                    Check.True(source.TryRead(out sample));Check.True(sample.HeadValid&&!sample.EyeValid);
                    Check.True(!source.LastObservation.EyeUsable);Check.True(!source.LastObservation.RawPoll.Eye.NoUser);
                    Check.Equal("Combined eye direction is not reported valid",source.LastObservation.EyeFailureReason);
                    native.CombinedMask=2;native.Required=true;
                    Check.True(source.TryRead(out sample));Check.True(sample.HeadValid&&!sample.EyeValid);
                    Check.True(!source.LastObservation.EyeUsable);
                    Check.Equal("Eye calibration: Required",source.LastObservation.EyeFailureReason);
                    native.Required=false;
                    Check.True(source.TryRead(out sample));Check.True(source.LastObservation.EyeUsable&&sample.EyeValid);
                }
            });
            t.Run("SRanipal construction and stopped polling never initialize native eye capture",()=>{
                var clock=new MonotonicClock("interop-test-clock");
                using(var provider=new SrAnipalViveProEyeProvider(clock,()=>null))
                {
                    Check.True(!provider.Started);var poll=provider.Poll();
                    Check.Equal(ViveEyeRuntimeState.Unavailable,poll.Runtime);Check.Equal(null,poll.Eye);
                    Check.True(!poll.EyeDeviceConnected);provider.Stop();Check.True(!provider.Started);
                }
            });
            t.Run("SRanipal consent and device failures remain explicit and never trigger calibration",()=>{
                Check.True(SrAnipalViveProEyeProvider.NativeMessage("Init",1035).Contains("user must decide"));
                Check.True(SrAnipalViveProEyeProvider.NativeMessage("Init",1034).Contains("disabled by the user"));
                Check.True(SrAnipalViveProEyeProvider.NativeMessage("Read",1023).Contains("device not found"));
                Check.True(SrAnipalViveProEyeProvider.NativeMessage("Read",1036).Contains("did not respond"));
            });
            t.Run("SRanipal explicit init rejection closes client and permits an explicit retry",()=>{
                var native=new NativeFake {InitResult=1035};
                using(var provider=Provider(native))
                {
                    Check.True(!provider.Start());Check.Equal(1,native.Closes);Check.Equal(0,native.Releases);
                    Check.Equal((int?)1035,provider.LastNativeResult);Check.True(!provider.Quarantined);
                    Check.Equal(null,provider.Poll().Eye);native.InitResult=0;
                    Check.True(provider.Start());Check.True(provider.Start());Check.Equal(2,native.Initializes);
                    provider.Stop();provider.Stop();Check.Equal(1,native.Releases);Check.Equal(2,native.Closes);
                }
            });
            t.Run("SRanipal initialization exception quarantines resources and forbids retry",()=>{
                var native=new NativeFake {ThrowAt="initialize"};var provider=Provider(native);
                Check.True(!provider.Start());Check.True(provider.Quarantined);Check.True(!provider.Started);
                Check.True(provider.Diagnostic.Contains("until process exit"));Check.True(!provider.Start());
                Check.Equal(1,native.Initializes);Check.Equal(null,provider.Poll().Eye);
                provider.Stop();provider.Dispose();provider.Dispose();
                Check.Equal(0,native.Releases);Check.Equal(0,native.Closes);
                Check.Throws<ObjectDisposedException>(()=>provider.Start());
                // An injected fake must never poison the real-process native-start gate.
                using(var independent=Provider(new NativeFake()))Check.True(independent.Start());
            });
            t.Run("SRanipal pre-init open failure cleans partial load without invoking native initialization",()=>{
                var native=new NativeFake {ThrowAt="open"};
                using(var provider=Provider(native))
                {
                    Check.True(!provider.Start());Check.Equal(0,native.Initializes);Check.Equal(1,native.Closes);
                    Check.True(!provider.Quarantined);native.ThrowAt=null;Check.True(provider.Start());
                }
            });
            t.Run("SRanipal status calibration and read error codes expose no frame and recover only on a subsequent good poll",()=>{
                foreach(string stage in new[]{"status","calibration","read"})
                {
                    var native=new NativeFake();
                    using(var provider=Provider(native))
                    {
                        Check.True(provider.Start());Check.True(provider.Poll().Eye!=null);
                        int reads=native.Reads;
                        if(stage=="status")native.StatusResult=1036;
                        if(stage=="calibration")native.CalibrationResult=1035;
                        if(stage=="read")native.ReadResult=1023;
                        var poll=provider.Poll();Check.Equal(null,poll.Eye);Check.Equal(ViveEyeRuntimeState.Error,poll.Runtime);
                        Check.Equal((int?)(stage=="status"?1036:stage=="calibration"?1035:1023),poll.NativeResultCode);
                        Check.Equal(reads+(stage=="read"?1:0),native.Reads);
                        native.StatusResult=native.CalibrationResult=native.ReadResult=0;
                        Check.True(provider.Poll().Eye!=null);Check.True(!provider.Quarantined);
                    }
                }
            });
            t.Run("SRanipal poll exceptions latch failure with no cached frame or implicit native retry",()=>{
                foreach(string stage in new[]{"status","device","calibration","read"})
                {
                    var native=new NativeFake();
                    using(var provider=Provider(native))
                    {
                        Check.True(provider.Start());Check.True(provider.Poll().Eye!=null);native.ThrowAt=stage;
                        var failed=provider.Poll();Check.Equal(null,failed.Eye);Check.Equal(ViveEyeRuntimeState.Error,failed.Runtime);
                        Check.Equal(null,failed.NativeResultCode);int statuses=native.Statuses,reads=native.Reads;
                        native.ThrowAt=null;Check.Equal(null,provider.Poll().Eye);
                        Check.Equal(statuses,native.Statuses);Check.Equal(reads,native.Reads);
                        provider.Stop();Check.Equal(1,native.Releases);Check.Equal(1,native.Closes);
                        Check.True(provider.Start());Check.True(provider.Poll().Eye!=null);
                    }
                }
            });
            t.Run("SRanipal device idle and calibration-required states cannot become calibrated output",()=>{
                var native=new NativeFake();
                using(var provider=Provider(native))
                {
                    Check.True(provider.Start());native.State=1;Check.Equal(null,provider.Poll().Eye);Check.Equal(0,native.Reads);
                    native.State=2;native.Connected=false;Check.Equal(null,provider.Poll().Eye);Check.Equal(0,native.Reads);
                    native.Connected=true;native.Required=true;
                    var required=provider.Poll();Check.Equal(ViveEyeCalibrationState.Required,required.Calibration);Check.True(required.Eye!=null);
                    native.Required=false;Check.Equal(ViveEyeCalibrationState.ReportedReady,provider.Poll().Calibration);
                }
            });
            t.Run("SRanipal release error or exception never escapes disposal and retains uncertain native resources",()=>{
                foreach(bool throws in new[]{false,true})
                {
                    var native=new NativeFake();var provider=Provider(native);Check.True(provider.Start());
                    if(throws)native.ThrowAt="release";else native.ReleaseResult=1036;
                    provider.Stop();Check.True(provider.Quarantined);Check.True(!provider.Started);Check.True(!provider.Start());
                    Check.Equal(null,provider.Poll().Eye);Check.True(provider.Diagnostic.Contains("until process exit"));
                    provider.Dispose();provider.Dispose();provider.Stop();
                    Check.Equal(1,native.Releases);Check.Equal(0,native.Closes);
                }
            });
            t.Run("SRanipal unload failure retains resources and cannot escape Stop or Dispose",()=>{
                var native=new NativeFake();var provider=Provider(native);Check.True(provider.Start());
                native.ThrowAt="close";provider.Dispose();provider.Dispose();provider.Stop();
                Check.True(provider.Quarantined);Check.True(!provider.Started);
                Check.Equal(1,native.Releases);Check.Equal(1,native.Closes);
                Check.True(provider.Diagnostic.Contains("unload failed"));
            });
        }
    }
}
