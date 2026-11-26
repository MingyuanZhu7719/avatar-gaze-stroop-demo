using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace AvatarStroop.Runtime
{
    // Visual-only OpenVR client. ABI: Valve openvr v1.16.8, IVRSystem_022 and
    // IVRRenderModels_006. No compositor, actions, haptics, model files or writes.
    // Models/textures remain supplied by the user's installed SteamVR runtime.
    internal sealed class ViveSteamVrRenderModelSource : IDisposable
    {
        internal sealed class Model
        {
            internal string Name;
            internal float[] Vertices;
            internal int[] Indices;
            internal byte[] Rgba;
            internal int Width,Height;
        }
        [StructLayout(LayoutKind.Sequential)] struct NativeModel
        {internal IntPtr Vertices;internal uint VertexCount;internal IntPtr Indices;internal uint TriangleCount;internal int Texture;}
        [StructLayout(LayoutKind.Sequential)] struct NativeTexture
        {internal ushort Width,Height;internal IntPtr Pixels;internal int Format;}
        [StructLayout(LayoutKind.Sequential)] struct Matrix34
        {
            internal float A,B,C,D,E,F,G,H,I,J,K,L;
            internal float[] Values {get{return new[]{A,B,C,D,E,F,G,H,I,J,K,L};}}
        }
        [StructLayout(LayoutKind.Sequential)] struct Component
        {internal Matrix34 Render,Local;internal uint Properties;}
        [StructLayout(LayoutKind.Sequential)] struct ControllerState
        {internal uint Packet;internal ulong Pressed,Touched;internal float A0,A1,A2,A3,A4,A5,A6,A7,A8,A9;}
        [StructLayout(LayoutKind.Sequential)] struct Mode {[MarshalAs(UnmanagedType.I1)]internal bool Scroll;}
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate uint Init(ref int error,int kind,IntPtr startup);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr Interface([MarshalAs(UnmanagedType.LPStr)]string version,ref int error);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void Shutdown();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate uint Token();
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate uint Role(int hand);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int DeviceClass(uint device);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] [return:MarshalAs(UnmanagedType.I1)] delegate bool Connected(uint device);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate uint Property(uint device,int property,StringBuilder value,uint capacity,ref int error);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int LoadModel([MarshalAs(UnmanagedType.LPStr)]string name,ref IntPtr model);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void Free(IntPtr value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int LoadTexture(int texture,ref IntPtr value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] [return:MarshalAs(UnmanagedType.I1)] delegate bool ComponentState(
            [MarshalAs(UnmanagedType.LPStr)]string model,[MarshalAs(UnmanagedType.LPStr)]string component,
            ref ControllerState state,ref Mode mode,ref Component result);
        [DllImport("kernel32",CharSet=CharSet.Unicode,SetLastError=true)] static extern IntPtr LoadLibraryEx(string path,IntPtr file,uint flags);
        [DllImport("kernel32",CharSet=CharSet.Ansi)] static extern IntPtr GetProcAddress(IntPtr library,string name);
        [DllImport("kernel32",EntryPoint="GetModuleFileNameW",CharSet=CharSet.Unicode,SetLastError=true)]
        static extern uint GetModuleFileName(IntPtr module,StringBuilder path,int capacity);
        [DllImport("kernel32")] [return:MarshalAs(UnmanagedType.Bool)] static extern bool FreeLibrary(IntPtr library);
        [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] struct ProcessEntry
        {
            internal uint Size,Usage,Id;internal UIntPtr Heap;internal uint Module,Threads,Parent;
            internal int Priority;internal uint Flags;[MarshalAs(UnmanagedType.ByValTStr,SizeConst=260)]internal string File;
        }
        [DllImport("kernel32")] static extern IntPtr CreateToolhelp32Snapshot(uint flags,uint process);
        [DllImport("kernel32",CharSet=CharSet.Unicode)] [return:MarshalAs(UnmanagedType.Bool)] static extern bool Process32First(IntPtr snapshot,ref ProcessEntry entry);
        [DllImport("kernel32",CharSet=CharSet.Unicode)] [return:MarshalAs(UnmanagedType.Bool)] static extern bool Process32Next(IntPtr snapshot,ref ProcessEntry entry);
        [DllImport("kernel32")] [return:MarshalAs(UnmanagedType.Bool)] static extern bool CloseHandle(IntPtr handle);
        IntPtr library;
        Role role;DeviceClass deviceClass;Connected connected;Property property;
        LoadModel loadModel;Free freeModel,freeTexture;LoadTexture loadTexture;ComponentState componentState;
        Shutdown shutdown;Token token;uint ownedToken,contextToken;bool ownsSession,disposed;
        Model pending,loaded;int pendingTexture;
        internal string LastIssue {get;private set;}
        internal string RuntimeLibraryPath {get;private set;}
        internal string LoadedRuntimeLibraryPath {get;private set;}
        internal string ContextInitializationMode {get;private set;}="not_initialized";
        internal uint ContextToken {get{return contextToken;}}
        internal bool Ready {get{return !disposed&&role!=null&&token!=null&&token()==contextToken;}}
        static bool RuntimeRunning()
        {
            // Mono's Process.GetProcessesByName can omit native Windows processes.
            IntPtr snapshot=CreateToolhelp32Snapshot(2,0);if(snapshot==new IntPtr(-1))return false;
            try
            {
                var entry=new ProcessEntry{Size=(uint)Marshal.SizeOf(typeof(ProcessEntry))};
                if(!Process32First(snapshot,ref entry))return false;
                do{if(String.Equals(entry.File,"vrserver.exe",StringComparison.OrdinalIgnoreCase))return true;}while(Process32Next(snapshot,ref entry));
                return false;
            }
            finally{CloseHandle(snapshot);}
        }
        static T Function<T>(IntPtr address) where T:class
        {if(address==IntPtr.Zero)throw new InvalidOperationException("OpenVR interface function unavailable");return Marshal.GetDelegateForFunctionPointer(address,typeof(T)) as T;}
        static T Slot<T>(IntPtr table,int index) where T:class
        {return Function<T>(Marshal.ReadIntPtr(table,index*IntPtr.Size));}
        static string RuntimeLibrary()
        {
            // The launcher has selected the OpenXR runtime for this process.
            // Resolve the optional OpenVR model client from that exact install,
            // never from a second registry lookup or a standard-path fallback.
            string manifest=Environment.GetEnvironmentVariable(VivePreviewLaunchPlan.RuntimeVariable);
            if(String.IsNullOrWhiteSpace(manifest))throw new InvalidOperationException("selected_runtime_manifest_missing");
            return VivePreviewLaunchPlan.GetOpenVrLibraryPath(manifest);
        }
        internal bool TryOpen()
        {
            if(Ready)return true;if(disposed)return false;
            try
            {
                if(Environment.OSVersion.Platform!=PlatformID.Win32NT||IntPtr.Size!=8){LastIssue="unsupported_platform";return false;}
                // Do not launch SteamVR/claim scene focus just to draw a hand.
                if(role!=null)CloseNative();
                RuntimeLibraryPath=null;LoadedRuntimeLibraryPath=null;ContextInitializationMode="not_initialized";
                RuntimeLibraryPath=RuntimeLibrary();
                if(!RuntimeRunning()){LastIssue="steamvr_not_running";return false;}
                library=LoadLibraryEx(RuntimeLibraryPath,IntPtr.Zero,0x1100);if(library==IntPtr.Zero){LastIssue="runtime_library_load_failed";return false;}
                var loadedPath=new StringBuilder(32768);
                uint loadedLength=GetModuleFileName(library,loadedPath,loadedPath.Capacity);
                if(loadedLength==0||loadedLength>=loadedPath.Capacity)
                    throw new InvalidOperationException("runtime_library_loaded_path_unavailable");
                LoadedRuntimeLibraryPath=Path.GetFullPath(loadedPath.ToString());
                if(!String.Equals(Path.GetFullPath(RuntimeLibraryPath),LoadedRuntimeLibraryPath,StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("runtime_library_loaded_path_mismatch");
                var get=Function<Interface>(GetProcAddress(library,"VR_GetGenericInterface"));
                shutdown=Function<Shutdown>(GetProcAddress(library,"VR_ShutdownInternal"));token=Function<Token>(GetProcAddress(library,"VR_GetInitToken"));
                int error=0;IntPtr system=get("FnTable:IVRSystem_022",ref error);
                if(system==IntPtr.Zero||error!=0)
                {
                    error=0;ownedToken=Function<Init>(GetProcAddress(library,"VR_InitInternal2"))(ref error,3,IntPtr.Zero);
                    if(error!=0)throw new InvalidOperationException("background_init_"+error);
                    ownsSession=true;error=0;system=get("FnTable:IVRSystem_022",ref error);
                }
                if(system==IntPtr.Zero||error!=0)throw new InvalidOperationException("system_interface_unavailable");
                error=0;IntPtr models=get("FnTable:IVRRenderModels_006",ref error);
                if(models==IntPtr.Zero||error!=0)throw new InvalidOperationException("render_models_interface_unavailable");
                role=Slot<Role>(system,17);deviceClass=Slot<DeviceClass>(system,19);connected=Slot<Connected>(system,20);property=Slot<Property>(system,27);
                loadModel=Slot<LoadModel>(models,0);freeModel=Slot<Free>(models,1);loadTexture=Slot<LoadTexture>(models,2);freeTexture=Slot<Free>(models,3);componentState=Slot<ComponentState>(models,14);
                contextToken=token();
                ContextInitializationMode=ownsSession?"background":"borrowed";
                LastIssue=null;return true;
            }
            catch(Exception e) when(!(e is OutOfMemoryException||e is StackOverflowException||e is AccessViolationException))
            {LastIssue=e.Message;CloseNative();return false;}
        }
        string StringProperty(uint device,int key)
        {int error=0;var b=new StringBuilder(1024);uint n=property(device,key,b,1024,ref error);return error==0&&n>1&&n<=1024?b.ToString():null;}
        internal bool TryResolve(int hand,string serial,out string model,out float[] trackingToGrip)
        {
            model=null;trackingToGrip=null;if(!Ready||hand<1||hand>2)return false;
            uint index=role(hand);
            if(index>=64||!connected(index)||deviceClass(index)!=2)return false;
            // Unity OpenXR and OpenVR must refer to the same physical role/device.
            if(!String.IsNullOrEmpty(serial)&&!String.Equals(serial,StringProperty(index,1002),StringComparison.Ordinal))return false;
            model=StringProperty(index,1003);return !String.IsNullOrEmpty(model)&&TryGrip(model,out trackingToGrip);
        }
        internal bool TryGrip(string model,out float[] matrix)
        {
            matrix=null;if(!Ready||String.IsNullOrEmpty(model))return false;
            var state=new ControllerState();var mode=new Mode();var component=new Component();
            // Valve's Render Model Reference maps pose/grip to OpenXR grip/pose.
            // New drivers may provide the explicit OpenXR override first.
            if(!componentState(model,"openxr_grip",ref state,ref mode,ref component)&&
                !componentState(model,"grip",ref state,ref mode,ref component))return false;
            matrix=component.Local.Values;return true;
        }
        internal Model PollModel(string name)
        {
            if(!Ready||String.IsNullOrEmpty(name))return null;
            if(loaded!=null&&loaded.Name==name)return loaded;
            if(pending==null||pending.Name!=name)
            {
                pending=null;IntPtr p=IntPtr.Zero;
                try
                {
                    int status=loadModel(name,ref p);if(status==100)return null;
                    if(status!=0||p==IntPtr.Zero){LastIssue="model_load_"+status;return null;}
                    var native=(NativeModel)Marshal.PtrToStructure(p,typeof(NativeModel));
                    if(native.VertexCount<3||native.VertexCount>65535||native.TriangleCount<1||native.TriangleCount>200000||native.Vertices==IntPtr.Zero||native.Indices==IntPtr.Zero)throw new InvalidDataException("model_limits");
                    var vertices=new float[checked((int)native.VertexCount*8)];Marshal.Copy(native.Vertices,vertices,0,vertices.Length);
                    var packed=new short[checked((int)native.TriangleCount*3)];Marshal.Copy(native.Indices,packed,0,packed.Length);
                    var indices=new int[packed.Length];for(int i=0;i<indices.Length;i++){indices[i]=(ushort)packed[i];if(indices[i]>=native.VertexCount)throw new InvalidDataException("model_index");}
                    pending=new Model{Name=name,Vertices=vertices,Indices=indices};pendingTexture=native.Texture;
                }
                finally{if(p!=IntPtr.Zero)freeModel(p);}
            }
            IntPtr texture=IntPtr.Zero;
            try
            {
                int status=loadTexture(pendingTexture,ref texture);if(status==100)return null;
                if(status!=0||texture==IntPtr.Zero){LastIssue="texture_load_"+status;return null;}
                var native=(NativeTexture)Marshal.PtrToStructure(texture,typeof(NativeTexture));
                if(native.Format!=0||native.Width<1||native.Height<1||native.Width>4096||native.Height>4096||native.Pixels==IntPtr.Zero)throw new InvalidDataException("texture_format_or_limits");
                pending.Rgba=new byte[checked(native.Width*native.Height*4)];Marshal.Copy(native.Pixels,pending.Rgba,0,pending.Rgba.Length);
                pending.Width=native.Width;pending.Height=native.Height;loaded=pending;pending=null;LastIssue=null;return loaded;
            }
            finally{if(texture!=IntPtr.Zero)freeTexture(texture);}
        }
        void CloseNative()
        {
            role=null;loaded=pending=null;
            // Never tear down a context borrowed from another OpenVR client,
            // or one replaced since this optional visual initialized its own.
            if(ownsSession&&shutdown!=null&&token!=null&&token()==ownedToken)shutdown();
            ownsSession=false;if(library!=IntPtr.Zero){FreeLibrary(library);library=IntPtr.Zero;}
            ContextInitializationMode="not_initialized";
        }
        public void Dispose(){if(disposed)return;disposed=true;CloseNative();}
    }
}
