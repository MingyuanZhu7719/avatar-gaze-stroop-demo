using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace AvatarStroop.Runtime
{
    // A read-only directory-identity candidate, not an authorization grant or
    // a raw-file creator. The file/ACL/writer entry is intentionally closed.
    // It pins every existing ancestor against subsequent delete/rename and
    // requires CreateFile3 to reject redirects throughout each opened path.
    // It does not protect against privileged actors, cloud synchronization,
    // remote filesystem implementations masquerading as local or all races.
    internal sealed class ViveRawDirectoryLease : IDisposable
    {
        const uint ReadAttributes=0x80,ShareRead=1,ShareWrite=2,OpenExisting=3;
        const uint BackupSemantics=0x02000000,DisallowPathRedirects=0x00010000;
        const uint DirectoryAttribute=0x10,ReparseAttribute=0x400,FixedDrive=3,FilePersistentAcls=0x8,FileTypeDisk=1;
        internal const int MaxAncestorHandles=120;
        [StructLayout(LayoutKind.Sequential)]
        struct CreateFile3Parameters
        {internal uint Size,FileAttributes,FileFlags,SecurityQosFlags;internal IntPtr SecurityAttributes,TemplateFile;}
        [StructLayout(LayoutKind.Sequential)]
        struct FileInformation
        {internal uint Attributes,CreationLow,CreationHigh,AccessLow,AccessHigh,WriteLow,WriteHigh,VolumeSerial,SizeHigh,SizeLow,Links,IndexHigh,IndexLow;}
        [StructLayout(LayoutKind.Sequential)]
        struct FileIdInformation
        {internal ulong VolumeSerial;[MarshalAs(UnmanagedType.ByValArray,SizeConst=16)]internal byte[] FileId;}
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode,ExactSpelling=true,SetLastError=true)]
        static extern LeaseHandle CreateFile3(string path,uint access,uint share,uint disposition,ref CreateFile3Parameters parameters);
        [DllImport("kernel32.dll",EntryPoint="CreateFile3",CharSet=CharSet.Unicode,ExactSpelling=true,SetLastError=true)]
        static extern IntPtr CreateFile3Raw(string path,uint access,uint share,uint disposition,ref CreateFile3Parameters parameters);
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode,ExactSpelling=true,SetLastError=true)]
        static extern uint GetDriveTypeW(string root);
        [DllImport("kernel32.dll",ExactSpelling=true,SetLastError=true)]
        static extern uint GetFileType(SafeHandle handle);
        [DllImport("kernel32.dll",ExactSpelling=true,SetLastError=true)]
        [return:MarshalAs(UnmanagedType.Bool)]
        static extern bool GetFileInformationByHandle(SafeHandle handle,out FileInformation info);
        [DllImport("kernel32.dll",ExactSpelling=true,SetLastError=true)]
        [return:MarshalAs(UnmanagedType.Bool)]
        static extern bool GetFileInformationByHandleEx(SafeHandle handle,int informationClass,out FileIdInformation info,uint size);
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode,ExactSpelling=true,SetLastError=true)]
        [return:MarshalAs(UnmanagedType.Bool)]
        static extern bool GetVolumeInformationByHandleW(SafeHandle handle,IntPtr volumeName,uint volumeNameLength,
            out uint serial,out uint maximumComponent,out uint flags,IntPtr filesystemName,uint filesystemNameLength);
        [DllImport("kernel32.dll",ExactSpelling=true,SetLastError=true)]
        [return:MarshalAs(UnmanagedType.Bool)]
        static extern bool CloseHandle(IntPtr handle);
        internal sealed class LeaseHandle:SafeHandleZeroOrMinusOneIsInvalid
        {
            int closeOutcome;
            public LeaseHandle():base(true){}
            internal LeaseHandle(IntPtr value,bool owns):base(owns){SetHandle(value);}
            internal int CloseOutcome {get{return Volatile.Read(ref closeOutcome);}}
            protected override bool ReleaseHandle(){bool ok=CloseHandle(handle);Volatile.Write(ref closeOutcome,ok?1:-1);return ok;}
        }
        readonly List<LeaseHandle> handles;
        readonly List<LeaseOpenObservation> openObservations=new List<LeaseOpenObservation>(MaxAncestorHandles);
        static readonly object FencedRetentionGate=new object();
        static readonly List<ViveRawDirectoryLease> FencedRetained=new List<ViveRawDirectoryLease>();
        internal static int FencedRetainedCount {get{lock(FencedRetentionGate)return FencedRetained.Count;}}
        // Numeric diagnostics only, with no path or account identifiers.
        // ERROR_ACCESS_DENIED is an observed OS result, not a root-cause or
        // permission to retry, and other failures may have unknown causes.
        internal static int? LastFencedNativeError
        {get{lock(FencedRetentionGate){if(FencedRetained.Count==0)return null;
            int value=Volatile.Read(ref FencedRetained[0].lastNativeError);return value==0?(int?)null:value;}}}
        ViveNamedSessionFrameAdmission fencedAdmission;ViveRawIoFence fencedIo;
        readonly object lifecycle=new object();
        // 0: caller owns lease, 1: exclusively transferred, 2: disposal
        // requested before transfer. The CAS linearizes transfer vs Dispose.
        int disposed,ownershipState,disposePending,externalDisposeRequested,closeIndex=-2,cleanupBlocked,lastNativeError;
        ExclusiveOwner exclusiveOwner;
        internal sealed class ExclusiveOwner
        {
            readonly ViveRawDirectoryLease lease;
            internal ExclusiveOwner(ViveRawDirectoryLease lease){this.lease=lease;}
            internal bool BelongsTo(ViveRawDirectoryLease value){return Object.ReferenceEquals(lease,value);}
        }
        // A process-rooted slot is allocated before the native open. If a raw
        // value returns, record it before creating a managed SafeHandle. An
        // exception before that assignment remains unobservable/unknown; a
        // later finalizer may still close an abandoned wrapper.
        internal sealed class LeaseOpenObservation
        {
            internal IntPtr Raw {get;private set;}internal bool ApiReturned {get;private set;}
            internal int? NativeError {get;private set;}
            internal LeaseHandle Managed {get;private set;}
            internal void RecordReturned(IntPtr value,int nativeError)
            {
                if(ApiReturned||nativeError<0||(value!=new IntPtr(-1)&&nativeError!=0))
                    throw new InvalidOperationException("Open result unavailable or already recorded.");
                Raw=value;NativeError=nativeError==0?(int?)null:nativeError;ApiReturned=true;
            }
            internal void BindManaged(LeaseHandle value)
            {if(!ApiReturned||Managed!=null||value==null)throw new InvalidOperationException("Open ownership unavailable.");Managed=value;}
        }
        internal sealed class DirectoryIdentity
        {
            readonly ulong volume;readonly byte[] id;
            internal DirectoryIdentity(ulong volume,byte[] id){this.volume=volume;this.id=(byte[])id.Clone();}
            internal bool Matches(ulong otherVolume,byte[] otherId)
            {return ViveRawFileId.Matches(volume,id,otherVolume,otherId);}
        }
        ViveRawDirectoryLease(List<LeaseHandle> handles){this.handles=handles;}
        internal bool FileCreationAuthorized {get{return false;}}
        internal bool AccessPolicyVerified {get{return false;}}
        internal bool ConsentVerified {get{return false;}}
        internal bool DisposalPending {get{return Volatile.Read(ref disposePending)!=0;}}
        internal bool ExternalDisposeRequested {get{return Volatile.Read(ref externalDisposeRequested)!=0;}}
        internal bool IsFencedFor(ViveNamedSessionFrameAdmission admission,ViveRawIoFence fence)
        {return Object.ReferenceEquals(fencedAdmission,admission)&&Object.ReferenceEquals(fencedIo,fence)&&admission!=null&&admission.IsIoFenceAuthority(fence);}

        // Selected directory is opened on the bound writer only after the
        // matching sink issues its one-use authority. Each native call has a
        // separate permit. On failure/withdrawal, handles successfully
        // assigned to the lease are retained: no application close, retry or
        // reopen. A native handle lost during P/Invoke marshalling has unknown
        // ownership and may still be finalized by the runtime.
        internal static bool TryOpenExistingFenced(ViveNamedSessionFrameAdmission admission,ViveRawIoFence fence,string selected,
            ViveRawSessionJournalSink.NativeAuthority authority,out ViveRawDirectoryLease result)
        {
            result=null;if(!ViveRawRelativeFileCapability.ProductionGateOpen&&(authority==null||authority.Owner==null||!authority.Owner.IsNativeFixture))return false;
            if(admission==null||fence==null||authority==null||authority.Owner==null||!admission.IsIoFenceAuthority(fence)||
                !authority.Owner.HasExactSelectedDirectory(admission,fence,selected)||
                !authority.TryConsume(ViveRawSessionJournalSink.NativeStep.LeasePrepare,null,null,null))return false;
            return TryOpenExistingCore(admission,fence,selected,out result);
        }
#if VIVE_RAW_SYNTHETIC_TEST
        // Standalone fixture build only. The runner must constrain the path to
        // its newly created workspace folder. No raw-data API is exposed.
        internal static bool TryOpenSyntheticForTest(ViveNamedSessionFrameAdmission admission,ViveRawIoFence fence,string selected,out ViveRawDirectoryLease result)
        {
            result=null;
            if(ViveRawRelativeFileCapability.ProductionGateOpen||admission==null||admission.IsLegacy||!admission.SyntheticOnly||
                !admission.IsIoFenceAuthority(fence))return false;
            return TryOpenExistingCore(admission,fence,selected,out result);
        }
#endif
        static bool TryOpenExistingCore(ViveNamedSessionFrameAdmission admission,ViveRawIoFence fence,string selected,out ViveRawDirectoryLease result)
        {
            result=null;
            string path;
            try{path=NormalizeCandidate(selected);}catch(Exception e) when(!Fatal(e)){fence.Abort();return false;}
            if(Environment.OSVersion.Platform!=PlatformID.Win32NT){fence.Abort();return false;}
            var lease=new ViveRawDirectoryLease(new List<LeaseHandle>(MaxAncestorHandles)){fencedAdmission=admission,fencedIo=fence};
            lock(FencedRetentionGate){if(FencedRetained.Count!=0){fence.Abort();return false;}FencedRetained.Add(lease);}
            ViveRawIoFence.Permit permit;bool returned=false,success=false;uint drive=0;
            if(!fence.TryReserve(ViveRawIoFence.IoOperation.LeaseDriveType,out permit)){fence.Abort();return false;}
            try{
#if VIVE_RAW_SYNTHETIC_TEST && VIVE_SYNTHETIC_BATCH_FIXTURE && !UNITY_5_3_OR_NEWER
                if(admission!=null&&admission.SyntheticOnly)ViveSyntheticNativeTrace.Called(ViveRawIoFence.IoOperation.LeaseDriveType);
#endif
                drive=GetDriveTypeW(path.Substring(0,3));returned=true;success=drive==FixedDrive;}catch(Exception e) when(!Fatal(e)){}
            if(fence.Finish(permit,returned,success)!=ViveRawIoFence.IoOutcome.ApiReturned)return false;
            uint volume=0;bool first=true;string current=path.Substring(0,3);
            if(!lease.TryOpenOneFenced(current,fence,ref volume,ref first))return false;
            foreach(string part in path.Substring(3).Split('\\'))
            {current=Path.Combine(current,part);if(!lease.TryOpenOneFenced(current,fence,ref volume,ref first))return false;}
            result=lease;return true;
        }
        static bool Fatal(Exception e){return e is OutOfMemoryException||e is StackOverflowException||e is AccessViolationException;}
        bool TryOpenOneFenced(string path,ViveRawIoFence fence,ref uint volume,ref bool first)
        {
            if(handles.Count>=MaxAncestorHandles){fence.Abort();return false;}
            // The list is preallocated and the slot is rooted before entering
            // the OS. A marshalling exception may leave a native handle whose
            // ownership/lifetime is unknown; no fallback, application cleanup
            // or repeat is attempted.
            int slot=handles.Count;handles.Add(null);LeaseHandle handle=null;
            var observation=new LeaseOpenObservation();openObservations.Add(observation);
            var parameters=new CreateFile3Parameters{Size=(uint)Marshal.SizeOf(typeof(CreateFile3Parameters)),FileFlags=BackupSemantics|DisallowPathRedirects};
            ViveRawIoFence.Permit permit;bool returned=false,success=false;
            if(!fence.TryReserve(ViveRawIoFence.IoOperation.LeaseOpen,out permit)){fence.Abort();return false;}
            try
            {
                // An IntPtr return avoids SafeHandle return-value marshalling;
                // the exact native value is recorded before wrapper creation.
                // An exception before this store still has unknown ownership.
#if VIVE_RAW_SYNTHETIC_TEST && VIVE_SYNTHETIC_BATCH_FIXTURE && !UNITY_5_3_OR_NEWER
                if(fencedAdmission!=null&&fencedAdmission.SyntheticOnly)ViveSyntheticNativeTrace.Called(ViveRawIoFence.IoOperation.LeaseOpen);
#endif
                IntPtr raw=CreateFile3Raw(path,ReadAttributes,ShareRead|ShareWrite,OpenExisting,ref parameters);
                int error=raw==new IntPtr(-1)?Marshal.GetLastWin32Error():0;
#if VIVE_RAW_SYNTHETIC_TEST && VIVE_SYNTHETIC_BATCH_FIXTURE && !UNITY_5_3_OR_NEWER
                if(fencedAdmission!=null&&fencedAdmission.SyntheticOnly&&error!=0)ViveSyntheticNativeTrace.NativeError(ViveRawIoFence.IoOperation.LeaseOpen,error);
#endif
                observation.RecordReturned(raw,error);if(error!=0)Volatile.Write(ref lastNativeError,error);returned=true;
                if(raw!=IntPtr.Zero&&raw!=new IntPtr(-1))
                {handle=new LeaseHandle(raw,true);observation.BindManaged(handle);handles[slot]=handle;success=true;}
            }
            catch(Exception e) when(!Fatal(e)){}
            if(fence.Finish(permit,returned,success)!=ViveRawIoFence.IoOutcome.ApiReturned)return false;
            returned=false;success=false;uint type=0;
            if(!fence.TryReserve(ViveRawIoFence.IoOperation.LeaseFileType,out permit)){fence.Abort();return false;}
            try{
#if VIVE_RAW_SYNTHETIC_TEST && VIVE_SYNTHETIC_BATCH_FIXTURE && !UNITY_5_3_OR_NEWER
                if(fencedAdmission!=null&&fencedAdmission.SyntheticOnly)ViveSyntheticNativeTrace.Called(ViveRawIoFence.IoOperation.LeaseFileType);
#endif
                type=GetFileType(handle);returned=true;success=type==FileTypeDisk;}catch(Exception e) when(!Fatal(e)){}
            if(fence.Finish(permit,returned,success)!=ViveRawIoFence.IoOutcome.ApiReturned)return false;
            returned=false;success=false;FileInformation info=default(FileInformation);
            if(!fence.TryReserve(ViveRawIoFence.IoOperation.LeaseFileInformation,out permit)){fence.Abort();return false;}
            try{
#if VIVE_RAW_SYNTHETIC_TEST && VIVE_SYNTHETIC_BATCH_FIXTURE && !UNITY_5_3_OR_NEWER
                if(fencedAdmission!=null&&fencedAdmission.SyntheticOnly)ViveSyntheticNativeTrace.Called(ViveRawIoFence.IoOperation.LeaseFileInformation);
#endif
                success=GetFileInformationByHandle(handle,out info);
#if VIVE_RAW_SYNTHETIC_TEST && VIVE_SYNTHETIC_BATCH_FIXTURE && !UNITY_5_3_OR_NEWER
                if(!success&&fencedAdmission!=null&&fencedAdmission.SyntheticOnly)ViveSyntheticNativeTrace.NativeError(ViveRawIoFence.IoOperation.LeaseFileInformation,Marshal.GetLastWin32Error());
#endif
                returned=true;success=success&&(info.Attributes&DirectoryAttribute)!=0&&
                (info.Attributes&ReparseAttribute)==0&&(first||info.VolumeSerial==volume);}catch(Exception e) when(!Fatal(e)){}
            if(fence.Finish(permit,returned,success)!=ViveRawIoFence.IoOutcome.ApiReturned)return false;
            if(first){volume=info.VolumeSerial;first=false;}
            returned=false;success=false;uint serial=0,component=0,flags=0;
            if(!fence.TryReserve(ViveRawIoFence.IoOperation.LeaseVolumeInformation,out permit)){fence.Abort();return false;}
            try{
#if VIVE_RAW_SYNTHETIC_TEST && VIVE_SYNTHETIC_BATCH_FIXTURE && !UNITY_5_3_OR_NEWER
                if(fencedAdmission!=null&&fencedAdmission.SyntheticOnly)ViveSyntheticNativeTrace.Called(ViveRawIoFence.IoOperation.LeaseVolumeInformation);
#endif
                success=GetVolumeInformationByHandleW(handle,IntPtr.Zero,0,out serial,out component,out flags,IntPtr.Zero,0);
#if VIVE_RAW_SYNTHETIC_TEST && VIVE_SYNTHETIC_BATCH_FIXTURE && !UNITY_5_3_OR_NEWER
                if(!success&&fencedAdmission!=null&&fencedAdmission.SyntheticOnly)ViveSyntheticNativeTrace.NativeError(ViveRawIoFence.IoOperation.LeaseVolumeInformation,Marshal.GetLastWin32Error());
#endif
                returned=true;
                success=success&&serial==volume&&(flags&FilePersistentAcls)!=0;}catch(Exception e) when(!Fatal(e)){}
            return fence.Finish(permit,returned,success)==ViveRawIoFence.IoOutcome.ApiReturned;
        }
        internal bool ExclusiveCleanupComplete {get{return Volatile.Read(ref closeIndex)==-1&&Volatile.Read(ref cleanupBlocked)==0;}}

        // One-shot exclusive transfer: once this succeeds, the former holder
        // cannot close pinned handles by calling Dispose. No OS call or user
        // callback is made, and disposal racing before this CAS wins instead.
        internal bool TryTakeExclusive(out ExclusiveOwner owner)
        {
            owner=null;if(!Monitor.TryEnter(lifecycle))return false;
            try
            {
                if(Volatile.Read(ref disposed)!=0||handles.Count==0)return false;
                var candidate=new ExclusiveOwner(this);
                if(Interlocked.CompareExchange(ref ownershipState,1,0)!=0)return false;
                exclusiveOwner=candidate;owner=candidate;return true;
            }
            finally{Monitor.Exit(lifecycle);}
        }

        // Compare two currently-open handles, not user authorization, a
        // permanent identity, filesystem trust, or a child/parent relation.
        // May block on filesystem I/O; never invoke on the Unity UI thread.
        internal bool HasSameOpenDirectoryIdentity(SafeHandle candidate)
        {return HasSameOpenDirectoryIdentity(candidate,null);}
        internal bool HasSameOpenDirectoryIdentity(SafeHandle candidate,ExclusiveOwner owner)
        {
            if(candidate==null||candidate.IsInvalid||candidate.IsClosed)return false;
            lock(lifecycle)
            {
                int state=Volatile.Read(ref ownershipState);
                if(Volatile.Read(ref disposed)!=0||handles.Count==0||state==2||
                    (state==0&&owner!=null)||(state==1&&(!Object.ReferenceEquals(owner,exclusiveOwner)||!owner.BelongsTo(this))))return false;
                FileIdInformation a,b;uint size=(uint)Marshal.SizeOf(typeof(FileIdInformation));
                // FileIdInfo is 18 in FILE_INFO_BY_HANDLE_CLASS. Fail closed if
                // the OS or filesystem cannot supply 128-bit identifiers.
                if(!GetFileInformationByHandleEx(handles[handles.Count-1],18,out a,size)||
                    !GetFileInformationByHandleEx(candidate,18,out b,size))return false;
                return ViveRawFileId.Matches(a.VolumeSerial,a.FileId,b.VolumeSerial,b.FileId);
            }
        }

        // Exactly one OS identity query. The exclusive token prevents a former
        // owner from closing the selected handle after it is snapshotted; no
        // lock is held during the potentially blocking call. Caller must hold
        // its own per-operation I/O permit and handle the uncertain result.
        internal bool TryReadExclusiveDirectoryIdentity(ExclusiveOwner owner,out DirectoryIdentity identity)
        {
            identity=null;LeaseHandle selected;
            if(!Monitor.TryEnter(lifecycle))return false;
            try
            {
                if(Volatile.Read(ref disposed)!=0||Volatile.Read(ref ownershipState)!=1||handles.Count==0||
                    !Object.ReferenceEquals(owner,exclusiveOwner)||owner==null||!owner.BelongsTo(this))return false;
                selected=handles[handles.Count-1];
            }
            finally{Monitor.Exit(lifecycle);}
            FileIdInformation info;uint size=(uint)Marshal.SizeOf(typeof(FileIdInformation));
            if(!GetFileInformationByHandleEx(selected,18,out info,size)||!ViveRawFileId.IsValid(info.FileId))return false;
            identity=new DirectoryIdentity(info.VolumeSerial,info.FileId);return true;
        }

        // Explicitly called by the sole background owner after the data file
        // and relative directory handle close. Exactly one CloseHandle per
        // permit; any failed, revoked or uncertain close is never retried.
        // SafeHandle.Dispose returning is not enough: ReleaseHandle must have
        // observed a successful CloseHandle. No lock is held across that call.
        internal bool TryCloseNextExclusive(ExclusiveOwner owner,ViveRawIoFence fence,out bool complete)
        {
            complete=false;
            if(!ViveRawRelativeFileCapability.ProductionGateOpen||fence==null||owner==null)return false;
            return TryCloseNextCore(owner,fence,out complete);
        }
#if VIVE_RAW_SYNTHETIC_TEST
        // Standalone fixture build only. The synthetic admission must be the
        // same authority used to create this lease; no raw-data API is exposed.
        internal bool TryCloseNextSyntheticForTest(ViveNamedSessionFrameAdmission admission,ExclusiveOwner owner,ViveRawIoFence fence,out bool complete)
        {
            complete=false;
            if(ViveRawRelativeFileCapability.ProductionGateOpen||admission==null||!admission.SyntheticOnly||
                !IsFencedFor(admission,fence)||owner==null)return false;
            return TryCloseNextCore(owner,fence,out complete);
        }
#endif
        bool TryCloseNextCore(ExclusiveOwner owner,ViveRawIoFence fence,out bool complete)
        {
            complete=false;
            LeaseHandle selected;
            if(!Monitor.TryEnter(lifecycle))return false;
            try
            {
                if(Volatile.Read(ref disposed)!=0||Volatile.Read(ref ownershipState)!=1||Volatile.Read(ref cleanupBlocked)!=0||handles.Count>MaxAncestorHandles||
                    !Object.ReferenceEquals(owner,exclusiveOwner)||!owner.BelongsTo(this))return false;
                if(closeIndex==-2)closeIndex=handles.Count-1;
                if(closeIndex<0){complete=true;return true;}
                selected=handles[closeIndex];
                if(selected==null||selected.IsClosed||selected.IsInvalid){Volatile.Write(ref cleanupBlocked,1);fence.Abort();return false;}
            }
            finally{Monitor.Exit(lifecycle);}
            ViveRawIoFence.Permit permit;
            if(!fence.TryReserve(ViveRawIoFence.IoOperation.CloseAncestor,out permit))return false;
            bool returned=false,success=false;
            try{selected.Dispose();returned=true;success=selected.CloseOutcome==1;}
            catch(Exception e) when(!(e is OutOfMemoryException||e is StackOverflowException||e is AccessViolationException)){}
            if(fence.Finish(permit,returned,success)!=ViveRawIoFence.IoOutcome.ApiReturned)
            {Volatile.Write(ref cleanupBlocked,1);return false;}
            if(!Monitor.TryEnter(lifecycle)){Volatile.Write(ref cleanupBlocked,1);fence.Abort();return false;}
            try
            {
                if(closeIndex<0||!Object.ReferenceEquals(handles[closeIndex],selected)||Volatile.Read(ref ownershipState)!=1)
                {Volatile.Write(ref cleanupBlocked,1);fence.Abort();return false;}
                closeIndex--;complete=closeIndex<0;return true;
            }
            finally{Monitor.Exit(lifecycle);}
        }

        // Deliberately strict local DOS syntax. Selection of a path is not
        // permission from a participant and this parser makes no I/O.
        internal static string NormalizeCandidate(string path)
        {
            if(String.IsNullOrWhiteSpace(path)||path.Length<4||!AsciiLetter(path[0])||path[1]!=':'||path[2]!='\\'||
                path.IndexOf('/')>=0||path.IndexOf('\0')>=0||path.Length>240)throw new ArgumentException("Explicit local directory required.");
            string tail=path.Substring(3).TrimEnd('\\');
            if(tail.Length==0)throw new ArgumentException("A directory below a drive root is required.");
            foreach(string part in tail.Split('\\'))
            {
                if(part.Length==0||part=="."||part==".."||part.EndsWith(" ",StringComparison.Ordinal)||part.EndsWith(".",StringComparison.Ordinal)||
                    part.IndexOfAny(new[]{':','*','?','\"','<','>','|'})>=0)throw new ArgumentException("Ambiguous path component.");
                foreach(char character in part)if(character<' ')throw new ArgumentException("Control characters are not permitted.");
                string stem=part.Split('.')[0];
                if(IsDeviceStem(stem))throw new ArgumentException("Reserved device component.");
            }
            // Fully qualified drive syntax and every component were checked
            // above; avoid an additional OS path resolution outside a permit.
            return path.TrimEnd('\\');
        }
        static bool AsciiLetter(char c){return(c>='A'&&c<='Z')||(c>='a'&&c<='z');}
        static bool IsDeviceStem(string stem)
        {
            string s=stem.ToUpperInvariant();
            return s=="CON"||s=="PRN"||s=="AUX"||s=="NUL"||
                (s.Length==4&&(s.StartsWith("COM",StringComparison.Ordinal)||s.StartsWith("LPT",StringComparison.Ordinal))&&
                    ((s[3]>='1'&&s[3]<='9')||s[3]=='\u00b9'||s[3]=='\u00b2'||s[3]=='\u00b3'));
        }
        // Read-only: does not create a file, set an ACL, enumerate a directory,
        // contact a headset, or alter system policy. Missing CreateFile3 and any
        // failed verification fail closed; there is no CreateFileW fallback.
        internal static ViveRawDirectoryLease OpenExistingCandidate(string selectedPath)
        {
            if(Environment.OSVersion.Platform!=PlatformID.Win32NT)throw new PlatformNotSupportedException();
            string path=NormalizeCandidate(selectedPath),root=path.Substring(0,3);
            if(GetDriveTypeW(root)!=FixedDrive)throw new IOException("Selected volume is not a fixed drive.");
            var opened=new List<LeaseHandle>();
            try
            {
                string current=root;uint volume=0;bool first=true;
                OpenOne(current,opened,ref volume,ref first);
                foreach(string part in path.Substring(3).Split('\\'))
                {current=Path.Combine(current,part);OpenOne(current,opened,ref volume,ref first);}
                return new ViveRawDirectoryLease(opened);
            }
            catch{foreach(var item in opened)item.Dispose();throw;}
        }
        static void OpenOne(string path,List<LeaseHandle> opened,ref uint volume,ref bool first)
        {
            var parameters=new CreateFile3Parameters{Size=(uint)Marshal.SizeOf(typeof(CreateFile3Parameters)),
                FileFlags=BackupSemantics|DisallowPathRedirects};
            LeaseHandle h=CreateFile3(path,ReadAttributes,ShareRead|ShareWrite,OpenExisting,ref parameters);
            if(h==null||h.IsInvalid){int error=Marshal.GetLastWin32Error();if(h!=null)h.Dispose();throw new IOException("Directory open failed; Win32 error "+error);}
            opened.Add(h);FileInformation info;
            if(GetFileType(h)!=FileTypeDisk||!GetFileInformationByHandle(h,out info)||
                (info.Attributes&DirectoryAttribute)==0||(info.Attributes&ReparseAttribute)!=0)throw new IOException("Directory handle verification failed.");
            if(first){volume=info.VolumeSerial;first=false;}else if(info.VolumeSerial!=volume)throw new IOException("Volume boundary is not supported.");
            uint serial,component,flags;
            if(!GetVolumeInformationByHandleW(h,IntPtr.Zero,0,out serial,out component,out flags,IntPtr.Zero,0)||
                serial!=volume||(flags&FilePersistentAcls)==0)throw new IOException("Persistent ACL support unavailable.");
        }
        public void Dispose()
        {
            int prior=Interlocked.CompareExchange(ref ownershipState,2,0);
            if(prior==1){Interlocked.Exchange(ref externalDisposeRequested,1);return;}
            if(prior!=0||Interlocked.Exchange(ref disposed,1)!=0)return;
            if(!Monitor.TryEnter(lifecycle)){Volatile.Write(ref disposePending,1);return;}
            try
            {
                for(int i=handles.Count-1;i>=0;i--)handles[i].Dispose();
                handles.Clear();
            }
            finally{Monitor.Exit(lifecycle);}
        }
    }
}
