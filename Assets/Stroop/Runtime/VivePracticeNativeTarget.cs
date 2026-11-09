using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace AvatarStroop.Runtime
{
    // Development-only: this API is not wired to a UI, eye source, task,
    // authenticated consent authority or production/research admission gate.
    internal enum VivePracticeNativeState { New,Creating,Open,Writing,Flushing,Closing,Closed,Revoked,Uncertain,Faulted,Disposed }
    internal interface IVivePracticeNativeHandle
    {int Write(byte[] bytes);void Flush();bool Close();}
    internal interface IVivePracticeNativeFactory
    {IVivePracticeNativeHandle CreateNew(string absolutePath);}
    internal interface IVivePracticeNativeErrorInfo {int? LastNativeError {get;}}
    internal sealed class VivePracticeNativeCallException : IOException
    {
        internal int? ErrorCode {get;private set;}internal int? CleanupErrorCode {get;private set;}internal bool CleanupOutcomeUncertain {get;private set;}
        internal VivePracticeNativeCallException(int code):this(code,null,false){}
        internal VivePracticeNativeCallException(int? code,int? cleanupError,bool cleanupUncertain):base("Native operation failed or is uncertain.")
        {ErrorCode=code;CleanupErrorCode=cleanupError;CleanupOutcomeUncertain=cleanupUncertain;}
    }

    // No FileStream buffer and no File.Move. SafeHandle owns the native handle
    // and P/Invoke retains it for the duration of a synchronous call. Neither the
    // Windows cache nor later OS writeback is controlled by consent withdrawal.
    internal sealed class VivePracticeWindowsNativeFactory : IVivePracticeNativeFactory
    {
        const uint GenericWrite=0x40000000,CreateNewDisposition=1,NormalAttributes=0x80,FileTypeDisk=1,DriveRemovable=2,DriveFixed=3;
        sealed class NativeHandle : SafeHandleZeroOrMinusOneIsInvalid
        {
            int closeOutcome,closeError;
            public NativeHandle():base(true){}
            internal bool CloseAcknowledged {get{return Volatile.Read(ref closeOutcome)==1;}}
            internal int? CloseError {get{return Volatile.Read(ref closeOutcome)==-1?(int?)Volatile.Read(ref closeError):null;}}
            protected override bool ReleaseHandle()
            {bool ok=CloseHandle(handle);int error=ok?0:Marshal.GetLastWin32Error();Volatile.Write(ref closeError,error);Volatile.Write(ref closeOutcome,ok?1:-1);return ok;}
        }
        sealed class Handle : IVivePracticeNativeHandle,IVivePracticeNativeErrorInfo
        {
            readonly NativeHandle native;int closeAttempted;
            internal Handle(NativeHandle native){this.native=native;}
            public int Write(byte[] bytes)
            {
                if(bytes==null||bytes.Length==0)throw new ArgumentException("Nonempty data required.");
                uint written;if(!WriteFile(native,bytes,checked((uint)bytes.Length),out written,IntPtr.Zero))throw new VivePracticeNativeCallException(Marshal.GetLastWin32Error());
                return checked((int)written);
            }
            public void Flush(){if(!FlushFileBuffers(native))throw new VivePracticeNativeCallException(Marshal.GetLastWin32Error());}
            public int? LastNativeError {get{return native.CloseError;}}
            public bool Close()
            {
                if(Interlocked.CompareExchange(ref closeAttempted,1,0)!=0)return false;
                native.Dispose();return native.CloseAcknowledged;
            }
        }
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode,ExactSpelling=true,SetLastError=true)]
        static extern NativeHandle CreateFileW(string path,uint desiredAccess,uint shareMode,IntPtr securityAttributes,uint creationDisposition,uint flagsAndAttributes,IntPtr templateFile);
        [DllImport("kernel32.dll",ExactSpelling=true,SetLastError=true)]
        [return:MarshalAs(UnmanagedType.Bool)]
        static extern bool WriteFile(NativeHandle file,[In] byte[] buffer,uint bytesToWrite,out uint bytesWritten,IntPtr overlapped);
        [DllImport("kernel32.dll",ExactSpelling=true,SetLastError=true)]
        [return:MarshalAs(UnmanagedType.Bool)]
        static extern bool FlushFileBuffers(NativeHandle file);
        [DllImport("kernel32.dll",ExactSpelling=true,SetLastError=true)]
        [return:MarshalAs(UnmanagedType.Bool)]
        static extern bool CloseHandle(IntPtr handle);
        [DllImport("kernel32.dll",ExactSpelling=true,SetLastError=true)]
        static extern uint GetFileType(NativeHandle file);
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode,ExactSpelling=true,SetLastError=true)]
        static extern uint GetDriveTypeW(string rootPath);
        public IVivePracticeNativeHandle CreateNew(string absolutePath)
        {
            if(Environment.OSVersion.Platform!=PlatformID.Win32NT)throw new PlatformNotSupportedException("Windows development target required.");
            string root=Path.GetPathRoot(absolutePath);uint drive=GetDriveTypeW(root);
            if(drive!=DriveFixed&&drive!=DriveRemovable)throw new IOException("Selected drive type is unavailable or unsupported.");
            // CREATE_NEW rejects an existing name; no preliminary target-file
            // existence check, enumeration, replacement, rename or deletion.
            NativeHandle h=null;bool transferred=false,cleanupUncertain=false;int? cleanupError=null;Exception failure=null;IVivePracticeNativeHandle result=null;
            try
            {
                h=CreateFileW(absolutePath,GenericWrite,0,IntPtr.Zero,CreateNewDisposition,NormalAttributes,IntPtr.Zero);
                if(h==null||h.IsInvalid)throw new VivePracticeNativeCallException(Marshal.GetLastWin32Error());
                uint type=GetFileType(h);int error=type==0?Marshal.GetLastWin32Error():0;
                if(type!=FileTypeDisk){if(error!=0)throw new VivePracticeNativeCallException(error);throw new IOException("Native target type is unavailable or unsupported.");}
                result=new Handle(h);transferred=true;
            }
            catch(Exception e) when(!Fatal(e)){failure=e;}
            finally
            {
                if(!transferred&&h!=null)
                {
                    bool wasValid=!h.IsInvalid;
                    try{h.Dispose();}catch(Exception e) when(!Fatal(e)){cleanupUncertain=wasValid;}
                    if(wasValid&&!h.CloseAcknowledged)cleanupUncertain=true;
                    cleanupError=h.CloseError;
                }
            }
            if(failure!=null){var native=failure as VivePracticeNativeCallException;throw new VivePracticeNativeCallException(native==null?(int?)null:native.ErrorCode,cleanupError,cleanupUncertain);}
            return result;
        }
        static bool Fatal(Exception e){return e is OutOfMemoryException||e is StackOverflowException||e is AccessViolationException;}
    }
    // All identity, opt-in, task and source assertions remain caller-declared.
    // Directory attributes and path normalization do not eliminate Windows
    // reparse-point or directory-rename races. Never use as a security boundary.
    internal sealed class VivePracticeNativeTarget : IDisposable
    {
        readonly object gate=new object(),context;readonly int owner,maximumBytes;readonly IVivePracticeNativeFactory factory;
        readonly string outputPath;IVivePracticeNativeHandle handle;VivePracticeNativeState state=VivePracticeNativeState.New;
        bool eyeChoice,taskChoice,revoked,disposed,cleanupRequested,running,closeAttempted,handleClosed,deniedObserved,nativeCleanupUncertain;int nextSequence,totalBytes;int? nativeError,nativeCleanupError;
        // Synthetic testing only; the native target is not connected to a UI.
        internal Action AfterReservation {get;set;}
        internal VivePracticeNativeTarget(string absoluteDirectory,object context,IVivePracticeNativeFactory factory,int maximumBytes)
        {
            if(context==null||factory==null||maximumBytes<1||maximumBytes>67108864||String.IsNullOrWhiteSpace(absoluteDirectory))throw new ArgumentException("Bounded selected directory and context required.");
            if(absoluteDirectory.Length<3||!AsciiLetter(absoluteDirectory[0])||absoluteDirectory[1]!=':'||
                (absoluteDirectory[2]!='\\'&&absoluteDirectory[2]!='/'))throw new ArgumentException("Fully qualified drive directory required.");
            foreach(string component in absoluteDirectory.Substring(3).Split(new[]{'\\','/'},StringSplitOptions.RemoveEmptyEntries))
                if(component=="."||component==".."||component.IndexOf(':')>=0||component.EndsWith(" ",StringComparison.Ordinal)||component.EndsWith(".",StringComparison.Ordinal))
                    throw new ArgumentException("Unambiguous selected directory required.");
            // Non-Windows builds may exercise injected fakes with Windows path
            // syntax; the physical factory itself refuses non-Windows calls.
            bool windows=Environment.OSVersion.Platform==PlatformID.Win32NT;
            string full=windows?Path.GetFullPath(absoluteDirectory):absoluteDirectory.Replace('/','\\');
            if(!windows&&full.Length>3)full=full.TrimEnd('\\');
            if(windows){string root=Path.GetPathRoot(full);if(String.IsNullOrEmpty(root)||root.Length<3||!AsciiLetter(root[0])||root[1]!=':'||(root[2]!='\\'&&root[2]!='/'))throw new ArgumentException("Drive directory required.");}
            this.context=context;this.factory=factory;this.maximumBytes=maximumBytes;owner=Thread.CurrentThread.ManagedThreadId;
            string name="practice-"+Guid.NewGuid().ToString("N")+".bin";
            outputPath=windows?Path.Combine(full,name):full+(full.EndsWith("\\",StringComparison.Ordinal)?"":"\\")+name;
        }
        internal static VivePracticeNativeTarget ForWindowsDevelopment(string selectedDirectory,object context,int maximumBytes)
        {return new VivePracticeNativeTarget(selectedDirectory,context,new VivePracticeWindowsNativeFactory(),maximumBytes);}
        internal string OutputPath {get{return outputPath;}}internal bool ProductionGateOpen {get{return false;}}
        internal bool ConsentAuthenticated {get{return false;}}internal bool HardwareAuthenticated {get{return false;}}internal bool DurableStorageProven {get{return false;}}
        internal int? LastNativeError {get{lock(gate)return nativeError;}}internal int? LastNativeCleanupError {get{lock(gate)return nativeCleanupError;}}
        internal bool NativeCleanupOutcomeUncertain {get{lock(gate)return nativeCleanupUncertain;}}internal bool AccessDeniedObserved {get{lock(gate)return deniedObserved;}}
        internal VivePracticeNativeState State {get{lock(gate)return state;}}
        internal bool HandleClosed {get{lock(gate)return handleClosed;}}
        internal bool CleanupRequired {get{lock(gate)return handle!=null&&!handleClosed&&!closeAttempted;}}
        bool Owner(){return Thread.CurrentThread.ManagedThreadId==owner;}
        static bool AsciiLetter(char c){return (c>='A'&&c<='Z')||(c>='a'&&c<='z');}
        static int? Error(Exception e){var native=e as VivePracticeNativeCallException;return native==null?(int?)null:native.ErrorCode;}
        void RecordError(int? error){if(error.HasValue){nativeError=error;if(error.Value==5)deniedObserved=true;}}
        void RecordCleanupFailure(Exception error)
        {var native=error as VivePracticeNativeCallException;if(native==null)return;if(native.CleanupErrorCode.HasValue){nativeCleanupError=native.CleanupErrorCode;if(native.CleanupErrorCode.Value==5)deniedObserved=true;}if(native.CleanupOutcomeUncertain)nativeCleanupUncertain=true;}
        internal bool ChooseEye(){lock(gate){if(!Owner()||revoked||disposed||state!=VivePracticeNativeState.New||eyeChoice)return false;eyeChoice=true;return true;}}
        internal bool ChooseTask(){lock(gate){if(!Owner()||revoked||disposed||state!=VivePracticeNativeState.New||taskChoice)return false;taskChoice=true;return true;}}
        // Revocation does not wait on synchronous I/O. An already-reserved call
        // can still begin afterward; it is uncertain and no new write/flush is reserved.
        internal void Revoke()
        {lock(gate){if(revoked)return;revoked=true;if(running)state=VivePracticeNativeState.Uncertain;
            else if(state!=VivePracticeNativeState.Closed&&state!=VivePracticeNativeState.Uncertain&&state!=VivePracticeNativeState.Faulted&&state!=VivePracticeNativeState.Disposed)state=VivePracticeNativeState.Revoked;}}
        bool Reserve(VivePracticeNativeState before,VivePracticeNativeState during)
        {
            lock(gate)
            {
                if(!Owner()||running){if(running)state=VivePracticeNativeState.Uncertain;else if(!revoked&&state!=VivePracticeNativeState.Closed)state=VivePracticeNativeState.Faulted;return false;}
                if(revoked||disposed||!eyeChoice||!taskChoice||state!=before)return false;
                running=true;state=during;if(during==VivePracticeNativeState.Closing)closeAttempted=true;return true;
            }
        }
        internal bool Create()
        {
            if(!Reserve(VivePracticeNativeState.New,VivePracticeNativeState.Creating))return false;
            IVivePracticeNativeHandle created=null;bool failed=false;int? error=null;Exception operationFailure=null;
            try{if(AfterReservation!=null)AfterReservation();created=factory.CreateNew(outputPath);if(created==null)failed=true;}
            catch(Exception e) when(!Fatal(e)){failed=true;error=Error(e);operationFailure=e;}
            bool success;lock(gate){running=false;RecordError(error);RecordCleanupFailure(operationFailure);if(created!=null)handle=created;
                success=!failed&&!revoked&&!disposed&&state==VivePracticeNativeState.Creating;state=success?VivePracticeNativeState.Open:VivePracticeNativeState.Uncertain;}
            MaybeCleanup();return success;
        }
        internal bool Append(object declaredContext,int sequence,byte[] bytes)
        {
            byte[] copy;
            lock(gate)
            {
                if(!Owner()||running){if(running)state=VivePracticeNativeState.Uncertain;else if(!revoked&&state!=VivePracticeNativeState.Closed)state=VivePracticeNativeState.Faulted;return false;}
                if(revoked||disposed||state!=VivePracticeNativeState.Open||handle==null)return false;
                if(!Object.ReferenceEquals(declaredContext,context)||bytes==null||bytes.Length==0||bytes.Length>262144||sequence!=nextSequence||
                    nextSequence==Int32.MaxValue||totalBytes>maximumBytes-(long)bytes.Length){state=VivePracticeNativeState.Faulted;return false;}
                copy=(byte[])bytes.Clone();running=true;state=VivePracticeNativeState.Writing;
            }
            bool failed=false;int count=-1;int? error=null;
            try{if(AfterReservation!=null)AfterReservation();count=handle.Write(copy);}catch(Exception e) when(!Fatal(e)){failed=true;error=Error(e);}
            bool success;lock(gate){running=false;RecordError(error);success=!failed&&count==copy.Length&&!revoked&&!disposed&&state==VivePracticeNativeState.Writing;
                if(success){totalBytes+=count;nextSequence++;state=VivePracticeNativeState.Open;}else state=VivePracticeNativeState.Uncertain;}
            MaybeCleanup();return success;
        }
        internal bool Flush()
        {
            if(!Reserve(VivePracticeNativeState.Open,VivePracticeNativeState.Flushing))return false;
            bool failed=false;int? error=null;try{if(AfterReservation!=null)AfterReservation();handle.Flush();}catch(Exception e) when(!Fatal(e)){failed=true;error=Error(e);}
            bool success;lock(gate){running=false;RecordError(error);success=!failed&&!revoked&&!disposed&&state==VivePracticeNativeState.Flushing;
                state=success?VivePracticeNativeState.Open:VivePracticeNativeState.Uncertain;}
            MaybeCleanup();return success;
        }
        internal bool Close()
        {if(!Reserve(VivePracticeNativeState.Open,VivePracticeNativeState.Closing))return false;return CloseReserved(false);}
        // Cleanup is one-shot and may cause OS work. It is not a write, a flush,
        // durability acknowledgment, or a reason to relabel an uncertain prefix.
        internal bool Cleanup()
        {
            lock(gate)
            {
                if(!Owner()||running){if(running)state=VivePracticeNativeState.Uncertain;else if(!revoked&&state!=VivePracticeNativeState.Closed)state=VivePracticeNativeState.Faulted;return false;}
                if(closeAttempted||handle==null||handleClosed||(state!=VivePracticeNativeState.Revoked&&state!=VivePracticeNativeState.Uncertain&&state!=VivePracticeNativeState.Faulted))return false;
                closeAttempted=true;running=true;
            }
            return CloseReserved(true);
        }
        bool CloseReserved(bool cleanup)
        {
            bool closed=false;int? error=null;try{if(AfterReservation!=null)AfterReservation();closed=handle.Close();if(!closed){var details=handle as IVivePracticeNativeErrorInfo;if(details!=null)error=details.LastNativeError;}}catch(Exception e) when(!Fatal(e)){error=Error(e);}
            lock(gate){running=false;RecordError(error);if(closed)handleClosed=true;
                if(cleanup||!closed||revoked||state!=VivePracticeNativeState.Closing){state=VivePracticeNativeState.Uncertain;return false;}
                state=VivePracticeNativeState.Closed;return true;}
        }
        void MaybeCleanup()
        {bool requested;lock(gate)requested=cleanupRequested&&!running&&handle!=null&&!handleClosed&&!closeAttempted;if(requested&&Owner())Cleanup();}
        // Disposal irrevocably blocks new operations. During an owner-thread
        // reentrant or concurrent dispose, cleanup is requested and performed
        // only after the in-flight call returns. No wait, cancel or retry claim.
        public void Dispose()
        {
            bool closeNow=false,cleanupNow=false;
            lock(gate)
            {
                disposed=true;
                if(running){cleanupRequested=true;state=VivePracticeNativeState.Uncertain;return;}
                if(!Owner())
                {cleanupRequested=handle!=null&&!handleClosed&&!closeAttempted;if(cleanupRequested)state=VivePracticeNativeState.Uncertain;else if(state==VivePracticeNativeState.New)state=VivePracticeNativeState.Disposed;return;}
                if(handle!=null&&!handleClosed&&!closeAttempted)
                {
                    cleanupNow=revoked||state!=VivePracticeNativeState.Open;
                    closeNow=!cleanupNow;closeAttempted=true;running=true;
                    if(closeNow)state=VivePracticeNativeState.Closing;
                }
                else if(state==VivePracticeNativeState.New)state=VivePracticeNativeState.Disposed;
            }
            if(closeNow||cleanupNow)CloseReserved(cleanupNow);
        }
        static bool Fatal(Exception e){return e is OutOfMemoryException||e is StackOverflowException||e is AccessViolationException;}
    }
}
