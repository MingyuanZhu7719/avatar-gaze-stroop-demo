using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace AvatarStroop.Runtime
{
    // A provider can archive older callback packets while presenting a separate
    // latest sample to task gates. Historical Ready must not override a newer
    // calibration-required/device-error report.
    internal interface IViveProEyeLiveSnapshot
    {
        bool TryGetLiveSnapshot(out ViveProEyePoll value);
        bool AcquisitionFaulted {get;}
    }
    internal interface IViveProEyeAcquisitionSeal
    {
        // Worker-thread only: may invoke native unregister, never the owner UI.
        bool RequestAcquisitionSeal();
        bool AcquisitionSealDrained {get;}
        bool HasBufferedAcquisition {get;}
    }

    public sealed partial class SrAnipalViveProEyeProvider : IViveProEyeAcquisitionSeal
    {
        const int CallbackCapacity=2048;
        const double HealthQueryInterval=.100,MaximumHealthAge=ViveNativePollDiagnostics.MaximumCachedHealthAgeSeconds;
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate void NativeEyeCallback(IntPtr data);
        sealed class HealthSnapshot
        {
            internal double Started,Completed;
            internal double? StatusAt,DeviceAt,CalibrationAt;
            internal int? StatusResult,NativeStatus,CalibrationResult;
            internal bool Connected;internal bool? CalibrationNeeded;
            internal ViveEyeRuntimeState Runtime;
            internal ViveEyeCalibrationState Calibration;
        }
        sealed class CallbackPacket
        {
            internal readonly ViveEyeFrame Eye;internal readonly HealthSnapshot Health;
            internal CallbackPacket(ViveEyeFrame eye,HealthSnapshot health){Eye=eye;Health=health;}
        }
        readonly object callbackGate=new object();
        CallbackPacket[] callbackQueue;
        CallbackPacket latestCallback;
        int callbackHead,callbackCount,callbackAccepting,callbackInFlight,callbackFault;
        NativeEyeCallback nativeCallback;
        IntPtr nativeCallbackPointer;
        bool callbackRegistered,callbackCaptureMode,callbackSealed;
        int? callbackRegistrationResult;
        HealthSnapshot latestHealth;
        double nextHealthQuery;
        ViveProEyePoll latestLivePoll;
        public bool AcquisitionFaulted {get{return Volatile.Read(ref callbackFault)!=0;}}
        bool IViveProEyeLiveSnapshot.TryGetLiveSnapshot(out ViveProEyePoll value)
        {value=latestLivePoll;return callbackCaptureMode;}
        bool IViveProEyeAcquisitionSeal.HasBufferedAcquisition
        {get{lock(callbackGate){return callbackCaptureMode&&callbackCount>0;}}}
        bool IViveProEyeAcquisitionSeal.AcquisitionSealDrained
        {get{lock(callbackGate){return !callbackCaptureMode||callbackSealed&&callbackCount==0&&Volatile.Read(ref callbackInFlight)==0;}}}
        bool IViveProEyeAcquisitionSeal.RequestAcquisitionSeal()
        {
            if(!callbackCaptureMode)return true;
            if(callbackSealed)return !AcquisitionFaulted;
            return UnregisterCallbackCapture(true);
        }

        bool StartCallbackCapture()
        {
            callbackQueue=new CallbackPacket[CallbackCapacity];callbackHead=callbackCount=0;latestCallback=null;
            callbackCaptureMode=true;callbackSealed=false;
            Volatile.Write(ref callbackFault,0);latestLivePoll=null;
            RefreshCallbackHealth();
            nativeCallback=ReceiveNativeEye;nativeCallbackPointer=Marshal.GetFunctionPointerForDelegate(nativeCallback);
            Volatile.Write(ref callbackAccepting,1);
            try
            {
                int result=((ISrAnipalCallbackNativeClient)client).RegisterEyeCallback(nativeCallbackPointer);
                LastNativeResult=result;callbackRegistrationResult=result;
                if(result!=Work)
                {
                    Volatile.Write(ref callbackAccepting,0);
                    Diagnostic=NativeMessage("Callback registration",result);Quarantine();return false;
                }
                callbackRegistered=true;return true;
            }
            catch(Exception error)
            {
                Volatile.Write(ref callbackAccepting,0);Diagnostic=ExceptionMessage("Callback registration failed",error);
                Quarantine();return false;
            }
        }

        // This vendor callback never calls Unity, any native query, disk, task
        // admission or user code. It copies allowed fields while the supplied
        // pointer is valid. The small O(1) managed queue lock is never held over
        // an IPC call, unregister, wait, or file operation.
        void ReceiveNativeEye(IntPtr data)
        {
            Interlocked.Increment(ref callbackInFlight);
            try
            {
                if(Volatile.Read(ref callbackAccepting)==0)return;
                var health=Volatile.Read(ref latestHealth);
                // Select the completed health publication before reading time;
                // a concurrent refresh cannot become a future report attached
                // to this callback merely by winning an intervening race.
                double observed=clock.Now;
                if(data==IntPtr.Zero||health==null||!FiniteTime(observed)||health.Completed>observed)
                {Interlocked.Exchange(ref callbackFault,1);return;}
                var packet=new CallbackPacket(DecodeEyeBuffer(data,observed),health);
                lock(callbackGate)
                {
                    if(Volatile.Read(ref callbackAccepting)==0)return;
                    if(callbackQueue==null||callbackCount==callbackQueue.Length)
                    {Interlocked.Exchange(ref callbackFault,1);Volatile.Write(ref callbackAccepting,0);return;}
                    callbackQueue[(callbackHead+callbackCount)%callbackQueue.Length]=packet;callbackCount++;latestCallback=packet;
                }
            }
            catch(Exception)
            {
                // No managed exception may cross the unmanaged callback ABI.
                // The worker treats this latched loss as a recording fault.
                Interlocked.Exchange(ref callbackFault,1);Volatile.Write(ref callbackAccepting,0);
            }
            finally{Interlocked.Decrement(ref callbackInFlight);}
        }

        void RefreshCallbackHealth()
        {
            var health=new HealthSnapshot {Started=clock.Now,Runtime=ViveEyeRuntimeState.Error,Calibration=ViveEyeCalibrationState.Unknown};
            try
            {
                int status;health.StatusResult=client.GetStatus(out status);health.NativeStatus=status;health.StatusAt=clock.Now;
                if(health.StatusResult==Work)
                {
                    health.Connected=client.IsViveProEye();health.DeviceAt=clock.Now;
                    if(health.Connected&&status==Working)
                    {
                        bool needed=false;health.CalibrationResult=client.NeedsCalibration(ref needed);health.CalibrationAt=clock.Now;
                        if(health.CalibrationResult==Work)
                        {
                            health.CalibrationNeeded=needed;health.Runtime=ViveEyeRuntimeState.Working;
                            health.Calibration=needed?ViveEyeCalibrationState.Required:ViveEyeCalibrationState.ReportedReady;
                        }
                    }
                    else health.Runtime=ViveEyeRuntimeState.Unavailable;
                }
            }
            finally
            {
                health.Completed=clock.Now;
                if(!FiniteTime(health.Started)||!FiniteTime(health.Completed)||health.Completed<health.Started)
                    throw new InvalidOperationException("Callback health clock is invalid.");
                Volatile.Write(ref latestHealth,health);nextHealthQuery=health.Completed+HealthQueryInterval;
            }
        }

        ViveProEyePoll PollCallbackCapture(ViveHeadFrame head)
        {
            long sequence=pollSequence++;double started=clock.Now;
            try
            {
                if(AcquisitionFaulted)throw new InvalidOperationException("Native callback queue overflow or callback copy failure.");
                if(!callbackSealed&&started>=nextHealthQuery)RefreshCallbackHealth();
                CallbackPacket archived=null,latest;
                lock(callbackGate)
                {
                    if(callbackCount>0)
                    {
                        archived=callbackQueue[callbackHead];callbackQueue[callbackHead]=null;
                        callbackHead=(callbackHead+1)%callbackQueue.Length;callbackCount--;
                    }
                    latest=latestCallback;
                }
                double returned=clock.Now;var currentHealth=Volatile.Read(ref latestHealth);
                // Equivalent repeated health queries do not invent eye loss.
                // A changed report immediately overrides older packet health;
                // e.g. an old Ready packet cannot conceal new Required.
                latestLivePoll=latest!=null&&HealthEquivalent(latest.Health,currentHealth)&&
                    HealthFresh(latest.Health,returned)&&HealthFresh(currentHealth,returned)?
                    CallbackPoll(head,latest,sequence,started,returned):HealthOnlyPoll(head,currentHealth,returned);
                Diagnostic=currentHealth.Calibration==ViveEyeCalibrationState.Required?
                    "HTC calibration report requires calibration; callback samples retain the report and its age.":
                    "HTC callback acquisition active; health queried separately with explicit observation times.";
                return archived==null?HealthOnlyPoll(head,currentHealth,returned):CallbackPoll(head,archived,sequence,started,returned);
            }
            catch(Exception error)
            {
                pollFault=true;Interlocked.Exchange(ref callbackFault,1);
                Diagnostic=ExceptionMessage("HTC callback capture failed; explicit Stop required",error);
                latestLivePoll=Failure(head,null,null);return latestLivePoll;
            }
        }
        ViveProEyePoll CallbackPoll(ViveHeadFrame head,CallbackPacket packet,long sequence,double started,double returned)
        {
            var h=packet.Health;var eye=packet.Eye;
            bool freshAtArrival=HealthFresh(h,eye.ObservedLocalSeconds);
            var diagnostics=new ViveNativePollDiagnostics(sequence,started,returned,h.StatusAt,h.DeviceAt,h.CalibrationAt,
                null,null,h.StatusResult,h.CalibrationResult,h.CalibrationNeeded,null,true,eye.ObservedLocalSeconds,h.Started,h.Completed,callbackRegistrationResult);
            // This generic code is the actual registration result, not a made-up
            // GetEyeData result; both output record kinds carry its provenance.
            return new ViveProEyePoll(head,eye,freshAtArrival?h.Runtime:ViveEyeRuntimeState.Unavailable,
                freshAtArrival?h.Calibration:ViveEyeCalibrationState.Unknown,h.Connected,callbackRegistrationResult,h.NativeStatus,diagnostics);
        }
        ViveProEyePoll HealthOnlyPoll(ViveHeadFrame head,HealthSnapshot health,double now)
        {
            bool fresh=HealthFresh(health,now);
            return new ViveProEyePoll(head,null,fresh?health.Runtime:ViveEyeRuntimeState.Unavailable,
                fresh?health.Calibration:ViveEyeCalibrationState.Unknown,health!=null&&health.Connected,callbackRegistrationResult,
                health==null?null:health.NativeStatus);
        }
        static bool HealthFresh(HealthSnapshot health,double now)
        {return health!=null&&FiniteTime(now)&&now>=health.Completed&&now-health.Completed<=MaximumHealthAge;}
        static bool HealthEquivalent(HealthSnapshot a,HealthSnapshot b)
        {return a!=null&&b!=null&&a.Runtime==b.Runtime&&a.Calibration==b.Calibration&&a.Connected==b.Connected&&
            a.StatusResult==b.StatusResult&&a.NativeStatus==b.NativeStatus&&a.CalibrationResult==b.CalibrationResult&&a.CalibrationNeeded==b.CalibrationNeeded;}
        static bool FiniteTime(double value){return !Double.IsNaN(value)&&!Double.IsInfinity(value)&&value>=0;}

        bool StopCallbackCapture()
        {return UnregisterCallbackCapture(false);}
        bool UnregisterCallbackCapture(bool preserveQueued)
        {
            // Normal completion admits callbacks through the native unregister
            // barrier, then drains them. Stop/withdraw blocks acceptance first.
            if(!preserveQueued)Volatile.Write(ref callbackAccepting,0);
            try
            {
                // In pinned SRanipal 1.3.5.1, invocation (RVA 0x8883..0x88e7)
                // and unregister (RVA 0x8dda..0x8ea7) hold the same v2 list
                // mutex (RVA 0x16a60). Successful unregister waits for callbacks
                // already invoking our delegate and removes future invocation.
                int result=((ISrAnipalCallbackNativeClient)client).UnregisterEyeCallback(nativeCallbackPointer);
                LastNativeResult=result;
                if(result!=Work||!SpinWait.SpinUntil(()=>Volatile.Read(ref callbackInFlight)==0,250))
                {Interlocked.Exchange(ref callbackFault,1);Diagnostic=NativeMessage("Callback unregister/quiescence",result);Quarantine();return false;}
                Volatile.Write(ref callbackAccepting,0);callbackRegistered=false;callbackSealed=true;
                if(!preserveQueued)CloseCallbackCaptureState();
                return true;
            }
            catch(Exception error)
            {Interlocked.Exchange(ref callbackFault,1);Diagnostic=ExceptionMessage("Callback unregister failed",error);Quarantine();return false;}
        }
        void CloseCallbackCaptureState()
        {
            lock(callbackGate){callbackQueue=null;latestCallback=null;callbackCount=callbackHead=0;}
            latestLivePoll=null;latestHealth=null;nativeCallbackPointer=IntPtr.Zero;callbackCaptureMode=false;
            GC.KeepAlive(nativeCallback);nativeCallback=null;
        }
    }
}
