using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using AvatarStroop.Core;

namespace AvatarStroop.Runtime
{
    // Injected tests implement this boundary without loading a library or opening a device.
    internal interface ISrAnipalNativeClient
    {
        void Open();
        int Initialize();
        int GetStatus(out int status);
        bool IsViveProEye();
        int NeedsCalibration(ref bool needed);
        int Read(IntPtr buffer);
        int Release();
        void Close();
    }

    // Thin, independently authored interop for the already installed HTC runtime.
    // No HTC binary is shipped, downloaded, installed or searched in the working directory.
    // Native ABI verified against the installed HTC 1.3.5.1 client (runtime 1.3.6.12).
    // Explicit Start is required. This class never opens calibration or accepts consent.
    public sealed class SrAnipalViveProEyeProvider : IViveProEyeClient, IViveProEyeCleanupStatus
    {
        // Installed 1.3.5.1 uses the unversioned eye selector for GetStatus;
        // selector 2 there queries fixed module 0 instead of its initialized eye ID.
        const int EyeStatusType=0, EyeV2=2, Work=0, Working=2, DataSize=240;
        readonly IClock clock;
        readonly Func<ViveHeadFrame> headReader;
        readonly ISrAnipalNativeClient client;
        readonly bool actualNative;
        IntPtr buffer;
        bool initialized,disposed,opened,pollFault,quarantined,ownsNativeLease;
        static readonly object nativeGate=new object();
        static bool nativeOwned,nativeQuarantined;
        // Intentionally retained until process exit when native ownership is uncertain.
        // A delegate or buffer must not disappear while vendor work might still use it.
        static readonly List<QuarantinedResources> quarantine=new List<QuarantinedResources>();
        sealed class QuarantinedResources
        {
            internal readonly ISrAnipalNativeClient Client;
            internal readonly IntPtr Buffer;
            internal QuarantinedResources(ISrAnipalNativeClient client,IntPtr buffer){Client=client;Buffer=buffer;}
        }
        public string Diagnostic {get;private set;}="Stopped; explicit Start is required.";
        public bool Started {get{return initialized;}}
        public bool Quarantined {get{return quarantined;}}
        public bool RestartSafe {get{return !quarantined;}}
        public int? LastNativeResult {get;private set;}
        public static string PluginDirectory {get{return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "VIVE","Updater","App","SRanipal","tools","eye_calibration_dashboard","EyeCalibrationDashboard_Data","Plugins");}}

        public SrAnipalViveProEyeProvider(IClock clock,Func<ViveHeadFrame> headReader)
            :this(clock,headReader,new InstalledClient()){}
        internal SrAnipalViveProEyeProvider(IClock clock,Func<ViveHeadFrame> headReader,ISrAnipalNativeClient client)
        {
            this.clock=clock??throw new ArgumentNullException("clock");
            this.headReader=headReader??throw new ArgumentNullException("headReader");
            this.client=client??throw new ArgumentNullException("client");
            actualNative=client is InstalledClient;
        }

        public bool Start()
        {
            if(disposed)throw new ObjectDisposedException("SrAnipalViveProEyeProvider");
            if(quarantined)return false;
            if(initialized)return true;
            if(actualNative)
            {
                lock(nativeGate)
                {
                    if(nativeQuarantined||nativeOwned)
                    {
                        Diagnostic=nativeQuarantined?"A native client has uncertain ownership; restart the process before another capture.":
                            "Another native client in this process is active; stop it before starting another capture.";
                        return false;
                    }
                    nativeOwned=ownsNativeLease=true;
                }
            }
            LastNativeResult=null;pollFault=false;
            bool initializationEntered=false;
            string stage="open installed client";
            try
            {
                ValidateAbi();
                opened=true; // Open can load a DLL before failing to bind one of its exports.
                client.Open();
                buffer=Marshal.AllocHGlobal(DataSize);
                stage="initialize eye client";
                initializationEntered=true;
                int result=client.Initialize();LastNativeResult=result;
                if(result!=Work){Diagnostic=NativeMessage("Initialization",result);CloseKnownInactiveClient();return false;}
                initialized=true;Diagnostic="HTC client initialized; waiting for fresh device data.";return true;
            }
            catch(Exception error)
            {
                Diagnostic=ExceptionMessage("HTC client unavailable during "+stage,error);
                if(initializationEntered)Quarantine();else CloseKnownInactiveClient();
                return false;
            }
        }

        public ViveProEyePoll Poll()
        {
            if(disposed)throw new ObjectDisposedException("SrAnipalViveProEyeProvider");
            // Head tracking has its own validity and remains independent of eye-device errors.
            ViveHeadFrame head=null;
            try{head=headReader();}catch(Exception){Diagnostic="Head observation failed; eye data cannot supply a head pose.";}
            if(!initialized)return new ViveProEyePoll(head,null,ViveEyeRuntimeState.Unavailable,ViveEyeCalibrationState.Unknown,false,LastNativeResult);
            if(pollFault)return Failure(head,null,null);
            string stage="status";
            try
            {
                int nativeStatus;
                int result=client.GetStatus(out nativeStatus);LastNativeResult=result;
                if(result!=Work){Diagnostic=NativeMessage("Status",result);return Failure(head,result,nativeStatus);}
                stage="device presence";
                bool connected=client.IsViveProEye();
                if(!connected||nativeStatus!=Working)
                {
                    Diagnostic=!connected?"VIVE Pro Eye is not reported connected.":"HTC eye runtime is not working (status "+nativeStatus+").";
                    return new ViveProEyePoll(head,null,ViveEyeRuntimeState.Unavailable,ViveEyeCalibrationState.Unknown,connected,result,nativeStatus);
                }
                stage="calibration status";
                bool needsCalibration=false;
                int calibrationResult=client.NeedsCalibration(ref needsCalibration);LastNativeResult=calibrationResult;
                if(calibrationResult!=Work)
                {
                    Diagnostic=NativeMessage("Calibration status",calibrationResult);
                    return Failure(head,calibrationResult,nativeStatus);
                }
                var calibrationState=needsCalibration?ViveEyeCalibrationState.Required:ViveEyeCalibrationState.ReportedReady;
                // Zero unused/padding bytes; never inspect pupil, expression, eye images or serials.
                for(int offset=0;offset<DataSize;offset+=8)Marshal.WriteInt64(buffer,offset,0);
                stage="eye read";
                result=client.Read(buffer);LastNativeResult=result;
                double observed=clock.Now; // Local API-return observation, not device exposure time.
                if(result!=Work){Diagnostic=NativeMessage("Eye read",result);return Failure(head,result,nativeStatus);}
                var frame=DecodeEyeBuffer(buffer,observed);
                Diagnostic=needsCalibration?"Calibration is required; complete it in the official HTC interface.":
                    frame.NoUser?"HTC reports no available eye data.":"HTC read succeeded; freshness and validity are checked separately.";
                return new ViveProEyePoll(head,frame,ViveEyeRuntimeState.Working,calibrationState,true,result,nativeStatus);
            }
            catch(Exception error)
            {
                // Do not repeatedly enter a native API which just threw. The owner must
                // explicitly stop it; only an acknowledged Release permits later restart.
                pollFault=true;LastNativeResult=null;
                Diagnostic=ExceptionMessage("HTC "+stage+" failed; polling stopped until explicit Stop",error);
                return Failure(head,null,null);
            }
        }

        static ViveProEyePoll Failure(ViveHeadFrame head,int? result,int? nativeStatus)
        {return new ViveProEyePoll(head,null,ViveEyeRuntimeState.Error,ViveEyeCalibrationState.Unknown,false,result,nativeStatus);}
        static ViveEyeRay Ray(SingleEye data)
        {return new ViveEyeRay(data.Mask,(data.Mask&1UL)!=0,(data.Mask&2UL)!=0,
            new Vec3(data.OriginX,data.OriginY,data.OriginZ),new Vec3(data.DirectionX,data.DirectionY,data.DirectionZ));}
        internal static ViveEyeFrame DecodeEyeBuffer(IntPtr pointer,double observed)
        {
            if(pointer==IntPtr.Zero)throw new ArgumentNullException("pointer");
            var data=(EyeData)Marshal.PtrToStructure(pointer,typeof(EyeData));
            // Installed 1.3.5.1 calls byte 0 "no_user", but its native v2 callback
            // sets it when either eye has a valid origin, and its managed sample
            // processes gaze when it is true. It is a positive eye-data presence
            // signal, not quantitative wearer detection. Preserve the logical
            // NoUser/data-absence contract by negating this misnamed native flag.
            return new ViveEyeFrame(data.Sequence,data.Timestamp,observed,data.NoUser==0,Ray(data.Left),Ray(data.Right),Ray(data.Combined));
        }
        public static string NativeMessage(string action,int result)
        {
            string reason=result==1035?"Eye-data agreement has not been accepted. The user must decide in HTC's interface":
                result==1034?"Eye tracking is disabled by the user":result==1025?"Not authorized":
                result==1023?"Eye device not found":result==1036?"Eye runtime did not respond":
                result==-3?"Eye runtime not found":"HTC native error";
            return action+": "+reason+" ("+result+"). No consent or runtime settings were changed.";
        }

        public void Stop()
        {
            if(disposed||quarantined)return;
            if(initialized)
            {
                try
                {
                    int result=client.Release();LastNativeResult=result;
                    if(result!=Work){Diagnostic=NativeMessage("Release",result);Quarantine();return;}
                }
                catch(Exception error)
                {
                    LastNativeResult=null;Diagnostic=ExceptionMessage("HTC release failed",error);Quarantine();return;
                }
            }
            initialized=false;pollFault=false;
            if(CloseKnownInactiveClient())Diagnostic="Stopped; this process released its eye client.";
        }
        public void Dispose(){if(disposed)return;try{Stop();}finally{disposed=true;}}
        bool CloseKnownInactiveClient()
        {
            try
            {
                if(opened)client.Close();
                opened=false;
                if(buffer!=IntPtr.Zero){Marshal.FreeHGlobal(buffer);buffer=IntPtr.Zero;}
                if(ownsNativeLease)lock(nativeGate){nativeOwned=false;ownsNativeLease=false;}
                return true;
            }
            catch(Exception error)
            {
                Diagnostic+=" "+ExceptionMessage("HTC unload failed",error);Quarantine();return false;
            }
        }
        void Quarantine()
        {
            if(quarantined)return;
            quarantined=true;initialized=false;pollFault=true;
            lock(nativeGate)
            {
                quarantine.Add(new QuarantinedResources(client,buffer));
                if(actualNative){nativeQuarantined=true;nativeOwned=false;ownsNativeLease=false;}
            }
            Diagnostic+=" Native ownership is uncertain; resources are retained until process exit. Restart this process before another capture.";
        }
        static string ExceptionMessage(string action,Exception error)
        {return action+": "+error.GetType().Name+" (0x"+error.HResult.ToString("X8")+"). "+error.Message;}

        // Explicit offsets match x64 installed managed/native interop; unused payload is retained
        // only in the transient native buffer, never exported. Native timestamp unit is unverified.
        [StructLayout(LayoutKind.Explicit,Size=48)] struct SingleEye
        {
            [FieldOffset(0)] public ulong Mask;
            [FieldOffset(8)] public float OriginX;[FieldOffset(12)] public float OriginY;[FieldOffset(16)] public float OriginZ;
            [FieldOffset(20)] public float DirectionX;[FieldOffset(24)] public float DirectionY;[FieldOffset(28)] public float DirectionZ;
        }
        [StructLayout(LayoutKind.Explicit,Size=DataSize)] struct EyeData
        {
            [FieldOffset(0)] public byte NoUser;
            [FieldOffset(4)] public int Sequence;[FieldOffset(8)] public int Timestamp;
            [FieldOffset(16)] public SingleEye Left;[FieldOffset(64)] public SingleEye Right;[FieldOffset(112)] public SingleEye Combined;
        }
        public static void ValidateAbi()
        {
            if(Marshal.SizeOf(typeof(SingleEye))!=48||Marshal.SizeOf(typeof(EyeData))!=DataSize||
                Marshal.OffsetOf(typeof(EyeData),"Combined").ToInt32()!=112)
                throw new InvalidOperationException("SRanipal native layout does not match the verified x64 ABI.");
        }
        sealed class InstalledClient : ISrAnipalNativeClient
        {
            IntPtr library;
            InitialDelegate initial;
            ReleaseDelegate release;
            StatusDelegate status;
            EyeDelegate read;
            CalibrationDelegate calibration;
            DeviceDelegate device;
            public void Open()
            {
                if(Environment.OSVersion.Platform!=PlatformID.Win32NT||IntPtr.Size!=8)
                    throw new PlatformNotSupportedException("The installed SRanipal client requires 64-bit Windows.");
                string path=Path.Combine(PluginDirectory,"SRanipal.dll");
                if(!File.Exists(path))throw new FileNotFoundException("The installed HTC SRanipal client was not found. No download is attempted.");
                var version=FileVersionInfo.GetVersionInfo(path);
                if(version.FileMajorPart!=1||version.FileMinorPart!=3||version.FileBuildPart!=5||version.FilePrivatePart!=1)
                    throw new InvalidOperationException("This adapter has verified native layout only for SRanipal.dll 1.3.5.1. Review an updated client before use.");
                // LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_DEFAULT_DIRS.
                library=LoadLibraryEx(path,IntPtr.Zero,0x1100);
                if(library==IntPtr.Zero)throw new InvalidOperationException("Cannot load installed HTC client (Windows error "+Marshal.GetLastWin32Error()+").");
                initial=Bind<InitialDelegate>("Initial");release=Bind<ReleaseDelegate>("Release");
                status=Bind<StatusDelegate>("GetStatus");read=Bind<EyeDelegate>("GetEyeData_v2");
                calibration=Bind<CalibrationDelegate>("IsUserNeedCalibration");device=Bind<DeviceDelegate>("IsViveProEye");
            }
            public int Initialize(){return initial(EyeV2,IntPtr.Zero);}
            public int GetStatus(out int value){return status(EyeStatusType,out value);}
            public bool IsViveProEye(){return device();}
            public int NeedsCalibration(ref bool needed){return calibration(ref needed);}
            public int Read(IntPtr data){return read(data);}
            public int Release(){return release(EyeV2);}
            public void Close()
            {
                if(library!=IntPtr.Zero)
                {
                    if(!FreeLibrary(library))throw new InvalidOperationException("Cannot unload HTC client (Windows error "+Marshal.GetLastWin32Error()+").");
                    library=IntPtr.Zero;
                }
                initial=null;release=null;status=null;read=null;calibration=null;device=null;
            }
            T Bind<T>(string name) where T:class
            {
                var pointer=GetProcAddress(library,name);
                if(pointer==IntPtr.Zero)throw new EntryPointNotFoundException("Installed HTC client is missing "+name+".");
                return (T)(object)Marshal.GetDelegateForFunctionPointer(pointer,typeof(T));
            }
        }
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate int InitialDelegate(int feature,IntPtr config);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate int ReleaseDelegate(int feature);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate int StatusDelegate(int feature,out int state);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate int EyeDelegate(IntPtr data);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate int CalibrationDelegate([MarshalAs(UnmanagedType.Bool)] ref bool needed);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] [return:MarshalAs(UnmanagedType.Bool)] delegate bool DeviceDelegate();
        [DllImport("kernel32.dll",EntryPoint="LoadLibraryExW",CharSet=CharSet.Unicode,SetLastError=true)] static extern IntPtr LoadLibraryEx(string path,IntPtr reserved,uint flags);
        [DllImport("kernel32.dll",CharSet=CharSet.Ansi,ExactSpelling=true)] static extern IntPtr GetProcAddress(IntPtr module,string name);
        [DllImport("kernel32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] static extern bool FreeLibrary(IntPtr module);
    }
}
