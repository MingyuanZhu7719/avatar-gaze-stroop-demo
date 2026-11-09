using System;
using System.Threading;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Apps
{
    // An opt-in health probe with a soft observation deadline; native calls may overrun.
    // Reports aggregates only: no gaze vectors,
    // eye images, pupil measurements, user/device identifiers, or files.
    public static class ViveEyeProbe
    {
        public static int Main(string[] args)
        {
            if(args.Length==1&&args[0]=="--self-check")
            {SrAnipalViveProEyeProvider.ValidateAbi();Console.WriteLine("PASS native ABI layout; no device was opened.");return 0;}
            int seconds;
            if(args.Length!=3||args[0]!="--observe-live"||args[1]!="--seconds"||!Int32.TryParse(args[2],out seconds)||seconds<1||seconds>10)
            {Console.WriteLine("Opt-in: ViveEyeProbe.exe --observe-live --seconds 1..10. Seconds sets a soft observation deadline, not a native-call timeout. Requires existing HTC runtime and user consent. No calibration, session or file recording.");return 2;}
            Console.WriteLine("Requested observation seconds="+seconds+" (soft deadline; native calls may overrun).");
            var clock=new MonotonicClock("vive-eye-health-local");
            using(var provider=new SrAnipalViveProEyeProvider(clock,()=>null))
            {
                if(!provider.Start()){Console.WriteLine(provider.Diagnostic);return 3;}
                int polls=0,readFrames=0,fresh=0,duplicates=0,unusable=0,noUser=0,ready=0,directions=0;
                int? lastSequence=null,lastTimestamp=null;double start=clock.Now;
                while(clock.Now-start<seconds)
                {
                    var poll=provider.Poll();polls++;
                    if(poll.Eye!=null)
                    {
                        readFrames++;
                        var frame=poll.Eye;
                        bool isFresh=false;
                        if(!lastSequence.HasValue)
                        {
                            // The first read establishes a baseline, not evidence of live progression.
                            lastSequence=frame.FrameSequence;lastTimestamp=frame.Timestamp;
                        }
                        else if(frame.FrameSequence>lastSequence.Value&&frame.Timestamp>lastTimestamp.Value)
                        {
                            isFresh=true;fresh++;lastSequence=frame.FrameSequence;lastTimestamp=frame.Timestamp;
                        }
                        else duplicates++;
                        if(frame.NoUser)noUser++;
                        if(poll.Calibration==ViveEyeCalibrationState.ReportedReady)ready++;
                        Vec3 derived;
                        if(isFresh&&!frame.NoUser&&poll.Runtime==ViveEyeRuntimeState.Working&&poll.Calibration==ViveEyeCalibrationState.ReportedReady&&
                            poll.EyeDeviceConnected&&(!poll.NativeResultCode.HasValue||poll.NativeResultCode.Value==0)&&
                            frame.Combined!=null&&(frame.Combined.ValidityMask&2UL)!=0&&frame.Combined.DirectionReportedValid&&
                            ViveProEyeSdkDirection.TryGetUnitDirection(frame.Combined.Direction,out derived))directions++;
                    }
                    else unusable++;
                    Thread.Sleep(5);
                }
                double observedSeconds=clock.Now-start;
                string lastReadDiagnostic=provider.Diagnostic;
                provider.Stop();
                bool succeeded=directions>0&&!provider.Quarantined;
                Console.WriteLine("Observed seconds="+observedSeconds.ToString("F2",System.Globalization.CultureInfo.InvariantCulture)+
                    "; polls="+polls+"; successful eye reads="+readFrames+"; fresh counter pairs="+fresh+"; repeated/out-of-order="+duplicates+
                    "; no eye frame="+unusable+"; eye data absent="+noUser+"; calibration-ready reads="+ready+"; fresh convertible combined directions="+directions);
                Console.WriteLine("Last read: "+lastReadDiagnostic);
                Console.WriteLine("Stop: "+provider.Diagnostic);
                Console.WriteLine(succeeded?
                    "PASS: fresh, calibration-ready, ray-convertible eye directions were observed. This is not tracking accuracy validation.":
                    provider.Quarantined?
                    "NOT READY: native client cleanup is uncertain; earlier valid directions do not establish a successful probe. Restart this process before another capture.":
                    "NOT READY: no fresh, calibration-ready, valid eye direction was observed. Successful API reads alone do not establish valid eye output.");
                Console.WriteLine("No raw eye values or files saved. Poll counts are not hardware sampling rate, latency, accuracy, or Stroop/WoZ acceptance. Head pose was not sampled.");
                return succeeded?0:4;
            }
        }
    }
}
