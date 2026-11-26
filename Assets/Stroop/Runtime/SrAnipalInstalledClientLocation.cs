using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security;
using System.Runtime.InteropServices;
using System.Text;

namespace AvatarStroop.Runtime
{
    internal sealed class SrAnipalInstallRoot
    {
        internal SrAnipalInstallRoot(string path,string source){Path=path;Source=source;}
        internal string Path {get;private set;}
        internal string Source {get;private set;}
    }

    // Locate an installed client without initializing HTC, starting a process,
    // changing registration, or searching arbitrary application/drive folders.
    internal sealed class SrAnipalInstalledClientLocation
    {
        internal const string OverrideVariable="STROOP_SRANIPAL_DLL";
        internal const string SidecarName="sranipal-client-path.txt";
        const string VerifiedVersion="1.3.5.1";
        const string RegistryPath=@"SOFTWARE\VIVE\SRWorks\SRanipal";
        const int MaximumRoots=32,MaximumDiagnostic=8192,MaximumSidecarBytes=4096;
        static readonly string[] RelativeLibraries={
            Path.Combine("tools","eye_calibration_dashboard","EyeCalibrationDashboard_Data","Plugins","SRanipal.dll"),
            Path.Combine("tools","eye_calibration","EyeCalibration_Data","Plugins","SRanipal.dll")};
        internal string LibraryPath {get;private set;}
        internal string RuntimePath {get;private set;}
        internal string DiscoverySource {get;private set;}
        internal string FileVersion {get;private set;}
        internal string SearchDiagnostic {get;private set;}

        internal static SrAnipalInstalledClientLocation Resolve()
        {
            if(Environment.OSVersion.Platform!=PlatformID.Win32NT)
                throw new PlatformNotSupportedException("Installed SRanipal discovery requires Windows.");
            string explicitDll=Environment.GetEnvironmentVariable(OverrideVariable);
            string source="environment "+OverrideVariable;
            if(String.IsNullOrEmpty(explicitDll))explicitDll=ReadSidecar(out source);
            var notes=new StringBuilder();
            // An explicit choice never falls back to a different installation.
            var roots=String.IsNullOrEmpty(explicitDll)?InstalledRoots(notes):new List<SrAnipalInstallRoot>();
            return ResolveCore(explicitDll,roots,File.Exists,ReadVersion,IsX64Library,source,notes);
        }

        internal static SrAnipalInstalledClientLocation Resolve(string explicitDll,IEnumerable<SrAnipalInstallRoot> roots,
            Func<string,bool> exists,Func<string,string> version,Func<string,bool> x64)
        {return ResolveCore(explicitDll,roots,exists,version,x64,"explicit DLL override",new StringBuilder());}

        static SrAnipalInstalledClientLocation ResolveCore(string explicitDll,IEnumerable<SrAnipalInstallRoot> roots,
            Func<string,bool> exists,Func<string,string> version,Func<string,bool> x64,string explicitSource,StringBuilder notes)
        {
            if(exists==null)throw new ArgumentNullException("exists");
            if(version==null)throw new ArgumentNullException("version");
            if(x64==null)throw new ArgumentNullException("x64");
            if(!String.IsNullOrEmpty(explicitDll))
            {
                string selected=Absolute(explicitDll);
                if(!String.Equals(Path.GetFileName(selected),"SRanipal.dll",StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("The explicit HTC client must name the installed SRanipal.dll file. No fallback was used.");
                Note(notes,explicitSource+": "+selected);
                if(!Exists(exists,selected))throw Missing("The explicit HTC client does not exist. Correct "+OverrideVariable+" or "+SidecarName+"; no fallback was used.",notes);
                return Validate(selected,RuntimeRootForLibrary(selected),explicitSource,exists,version,x64,notes);
            }
            if(roots==null)throw new ArgumentNullException("roots");
            var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);int count=0;
            foreach(var candidate in roots)
            {
                if(count++>=MaximumRoots){Note(notes,"Additional installation roots were not inspected (bounded search).");break;}
                if(candidate==null)continue;
                string root;
                try{root=Absolute(candidate.Path);}
                catch(Exception error)
                {
                    if(!PathError(error))throw;
                    Note(notes,Clean(candidate.Source)+": ignored invalid installation root ("+error.GetType().Name+").");continue;
                }
                foreach(string relative in RelativeLibraries)
                {
                    string path=Path.GetFullPath(Path.Combine(root,relative));
                    if(!seen.Add(path))continue;
                    Note(notes,Clean(candidate.Source)+": "+path);
                    if(Exists(exists,path))return Validate(path,root,Clean(candidate.Source),exists,version,x64,notes);
                }
            }
            throw Missing("The installed HTC SRanipal client was not found. Start the installed SR_Runtime once to refresh its registration, or put the full installed SRanipal.dll path in "+SidecarName+" beside the program. No download or whole-drive scan was attempted.",notes);
        }

        static SrAnipalInstalledClientLocation Validate(string path,string root,string source,Func<string,bool> exists,
            Func<string,string> version,Func<string,bool> x64,StringBuilder notes)
        {
            string actual;
            try{actual=version(path);}
            catch(Exception error){throw new InvalidOperationException("Cannot inspect the installed HTC client version at "+path+". No fallback was used.",error);}
            if(actual!=VerifiedVersion)throw new InvalidOperationException("The installed HTC client at "+path+" has DLL version "+Clean(actual)+"; this adapter supports SRanipal.dll "+VerifiedVersion+" only. The SR_Runtime product version is separate. No fallback was used.");
            bool architecture;
            try{architecture=x64(path);}
            catch(Exception error){throw new InvalidOperationException("Cannot inspect the installed HTC client architecture at "+path+". No fallback was used.",error);}
            if(!architecture)throw new InvalidOperationException("The installed HTC client is not a Windows x64 PE library: "+path+". No fallback was used.");
            string runtime="";
            if(!String.IsNullOrEmpty(root))
            {
                string possible=Path.Combine(root,"sr_runtime.exe");
                try{if(exists(possible))runtime=possible;}
                catch(Exception error){Note(notes,"SR_Runtime executable lookup unavailable: "+error.GetType().Name+".");}
            }
            var report=new StringBuilder();
            Note(report,"HTC client source: "+source+" | DLL version: "+actual+" | Architecture: Windows x64");
            Note(report,"Selected client: "+path);
            if(runtime.Length>0)Note(report,"Installed SR_Runtime: "+runtime);
            Note(report,"Search details:\n"+notes);
            return new SrAnipalInstalledClientLocation {LibraryPath=path,RuntimePath=runtime,DiscoverySource=source,
                FileVersion=actual,SearchDiagnostic=report.ToString()};
        }

        static bool Exists(Func<string,bool> exists,string path)
        {
            try{return exists(path);}
            catch(Exception error){throw new InvalidOperationException("Cannot inspect the registered HTC client path "+path+". Check access to that installation; no fallback was used.",error);}
        }
        static FileNotFoundException Missing(string message,StringBuilder notes)
        {return new FileNotFoundException(message+"\nInspected locations:\n"+notes);}
        static bool PathError(Exception error)
        {return error is ArgumentException||error is NotSupportedException||error is PathTooLongException||error is SecurityException;}
        static string Absolute(string path)
        {
            if(String.IsNullOrWhiteSpace(path)||path.Length>MaximumSidecarBytes||!Path.IsPathRooted(path))
                throw new ArgumentException("A fully qualified installed HTC path is required.");
            string value=path.Trim();
            if(value.IndexOf('\r')>=0||value.IndexOf('\n')>=0||value.IndexOf('\0')>=0)
                throw new ArgumentException("One installed HTC path is required.");
            if(Path.DirectorySeparatorChar=='\\'&&(value.StartsWith(@"\\",StringComparison.Ordinal)||Path.GetPathRoot(value).Length<3))
                throw new ArgumentException("A fully qualified local drive path is required for the installed HTC client.");
            return Path.GetFullPath(value);
        }
        static string Clean(string value)
        {
            string clean=(value??"unknown").Replace('\r',' ').Replace('\n',' ').Replace('\0',' ');
            return clean.Length>200?clean.Substring(0,200):clean;
        }
        static void Note(StringBuilder notes,string text)
        {
            int remaining=MaximumDiagnostic-notes.Length;if(remaining<=0)return;
            string value=text+"\n";notes.Append(value.Length>remaining?value.Substring(0,remaining):value);
        }
        static string RuntimeRootForLibrary(string path)
        {
            foreach(string relative in RelativeLibraries)
            {
                string suffix=Path.DirectorySeparatorChar+relative;
                if(path.EndsWith(suffix,StringComparison.OrdinalIgnoreCase))
                {
                    string root=path.Substring(0,path.Length-suffix.Length);
                    return root.Length==2&&root[1]==':'?root+Path.DirectorySeparatorChar:root;
                }
            }
            return "";
        }
        static string ReadVersion(string path)
        {
            var value=FileVersionInfo.GetVersionInfo(path);
            return value.FileMajorPart+"."+value.FileMinorPart+"."+value.FileBuildPart+"."+value.FilePrivatePart;
        }
        internal static bool IsX64Library(string path)
        {
            using(var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete))
            using(var reader=new BinaryReader(file))
            {
                if(file.Length<64||reader.ReadUInt16()!=0x5a4d)return false;
                file.Position=0x3c;int pe=reader.ReadInt32();
                if(pe<64||pe>1048576||pe>file.Length-26)return false;
                file.Position=pe;
                if(reader.ReadUInt32()!=0x00004550||reader.ReadUInt16()!=0x8664)return false;
                file.Position=pe+24;return reader.ReadUInt16()==0x20b;
            }
        }

        static List<SrAnipalInstallRoot> InstalledRoots(StringBuilder notes)
        {
            var roots=new List<SrAnipalInstallRoot>();
            try
            {
                var processes=Process.GetProcessesByName("sr_runtime");
                Array.Sort(processes,(a,b)=>a.Id.CompareTo(b.Id));
                foreach(var process in processes)
                using(process)
                {
                    try
                    {
                        string path=process.MainModule.FileName;
                        if(String.Equals(Path.GetFileName(path),"sr_runtime.exe",StringComparison.OrdinalIgnoreCase))
                            roots.Add(new SrAnipalInstallRoot(Path.GetDirectoryName(Absolute(path)),"running SR_Runtime"));
                    }
                    catch(Exception error){Note(notes,"Running SR_Runtime location unavailable: "+error.GetType().Name+".");}
                }
            }
            catch(Exception error){Note(notes,"Running SR_Runtime discovery unavailable: "+error.GetType().Name+".");}
            // Unity's .NET Standard profile does not expose Microsoft.Win32.Registry.
            // Query only these HTC values through the Windows read-only API instead.
            foreach(var hive in new[]{unchecked((int)0x80000001),unchecked((int)0x80000002)})
            foreach(uint view in new uint[]{0x0100,0x0200})
            {
                string label=(hive==unchecked((int)0x80000001)?"HKCU":"HKLM")+
                    (view==0x0100?" Registry64 ":" Registry32 ")+RegistryPath;
                IntPtr key=IntPtr.Zero;bool opened=false;
                try
                {
                    int result=RegOpenKeyEx(new IntPtr(hive),RegistryPath,0,0x0001|view,out key);
                    if(result!=0)
                    {
                        if(result!=2)Note(notes,label+" unavailable: Windows error "+result+".");
                        continue;
                    }
                    opened=true;
                    string module=ReadRegistryString(key,"ModuleFileName",label,notes);
                    if(!String.IsNullOrWhiteSpace(module))
                    {
                        try
                        {
                            string path=Absolute(module);
                            if(String.Equals(Path.GetFileName(path),"sr_runtime.exe",StringComparison.OrdinalIgnoreCase))
                                roots.Add(new SrAnipalInstallRoot(Path.GetDirectoryName(path),label+" ModuleFileName"));
                        }
                        catch(Exception error){Note(notes,label+" ModuleFileName ignored: "+error.GetType().Name+".");}
                    }
                    string root=ReadRegistryString(key,"VIVE_INSTALL_PATH",label,notes);
                    if(!String.IsNullOrWhiteSpace(root))roots.Add(new SrAnipalInstallRoot(root,label+" VIVE_INSTALL_PATH"));
                }
                catch(Exception error){Note(notes,label+" unavailable: "+error.GetType().Name+".");}
                finally
                {
                    if(opened)
                    {
                        int closed=RegCloseKey(key);
                        if(closed!=0)Note(notes,label+" close returned Windows error "+closed+".");
                    }
                }
            }
            string defaultRoot=Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            if(!String.IsNullOrEmpty(defaultRoot))roots.Add(new SrAnipalInstallRoot(Path.Combine(defaultRoot,"VIVE","Updater","App","SRanipal"),"legacy default installation"));
            return roots;
        }

        static string ReadRegistryString(IntPtr key,string name,string label,StringBuilder notes)
        {
            uint type,bytes=0;
            int result=RegQueryValueEx(key,name,IntPtr.Zero,out type,null,ref bytes);
            if(result==2)return null;
            if(result!=0&&result!=234){Note(notes,label+" "+name+" unreadable: Windows error "+result+".");return null;}
            // Two queries, one bounded allocation, no retry loop if a concurrent
            // installer changes the value. The next launch can inspect it again.
            if((type!=1&&type!=2)||bytes==0||bytes>MaximumSidecarBytes*2||(bytes&1)!=0)
            {Note(notes,label+" "+name+" ignored: unsupported or oversized registry string.");return null;}
            byte[] data=new byte[(int)bytes];
            result=RegQueryValueEx(key,name,IntPtr.Zero,out type,data,ref bytes);
            if(result==2)return null;
            if(result!=0){Note(notes,label+" "+name+" unreadable: Windows error "+result+".");return null;}
            if((type!=1&&type!=2)||bytes>data.Length||(bytes&1)!=0)
            {Note(notes,label+" "+name+" ignored: registry value changed during reading.");return null;}
            string value;
            try{value=new UnicodeEncoding(false,false,true).GetString(data,0,(int)bytes).TrimEnd('\0');}
            catch(DecoderFallbackException){Note(notes,label+" "+name+" ignored: malformed UTF-16.");return null;}
            if(value.IndexOf('\0')>=0){Note(notes,label+" "+name+" ignored: embedded null.");return null;}
            if(type==2)value=Environment.ExpandEnvironmentVariables(value);
            if(value.Length>MaximumSidecarBytes){Note(notes,label+" "+name+" ignored: expanded path is oversized.");return null;}
            return value;
        }

        static string ReadSidecar(out string source)
        {
            source="explicit DLL override";
            // Resolve the real player executable even under Unity's embedded
            // Mono, whose AppDomain base points into the managed data folder.
            var executableName=new StringBuilder(32768);
            uint executableLength=GetModuleFileName(IntPtr.Zero,executableName,executableName.Capacity);
            if(executableLength==0||executableLength>=executableName.Capacity)throw new InvalidOperationException("Cannot locate this program's HTC path file (Windows error "+Marshal.GetLastWin32Error()+").");
            string executable=executableName.ToString();
            string folder=Path.GetDirectoryName(Absolute(executable));
            var candidates=new List<string>{Path.Combine(folder,SidecarName)};
            var parent=Directory.GetParent(folder);
            if(String.Equals(Path.GetFileName(folder),"UnityPlayer",StringComparison.OrdinalIgnoreCase)&&parent!=null&&
                File.Exists(Path.Combine(parent.FullName,"AvatarStroopViveEyePreview.exe")))
                candidates.Add(Path.Combine(parent.FullName,SidecarName));
            foreach(string path in candidates)
            {
                if(!File.Exists(path))continue;
                try
                {
                    byte[] bytes=new byte[MaximumSidecarBytes+1];int length=0;
                    using(var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete))
                    {
                        while(length<bytes.Length)
                        {int read=file.Read(bytes,length,bytes.Length-length);if(read==0)break;length+=read;}
                    }
                    if(length>MaximumSidecarBytes)throw new InvalidDataException("The HTC path file exceeds 4096 bytes.");
                    string text=new UTF8Encoding(false,true).GetString(bytes,0,length).TrimStart('\ufeff').Trim();
                    if(String.IsNullOrWhiteSpace(text)||text.IndexOf('\r')>=0||text.IndexOf('\n')>=0)
                        throw new InvalidDataException("The HTC path file must contain one full SRanipal.dll path only.");
                    // Validate even an empty/malformed explicit file immediately;
                    // do not conceal it by searching another installation.
                    text=Absolute(text);source="path file "+path;return text;
                }
                catch(Exception error){throw new InvalidOperationException("Cannot use HTC client path file "+path+". It must contain one fully qualified installed SRanipal.dll path. No fallback was used.",error);}
            }
            return null;
        }
        [DllImport("kernel32.dll",EntryPoint="GetModuleFileNameW",CharSet=CharSet.Unicode,SetLastError=true)]
        static extern uint GetModuleFileName(IntPtr module,StringBuilder path,int capacity);
        [DllImport("advapi32.dll",EntryPoint="RegOpenKeyExW",CharSet=CharSet.Unicode,ExactSpelling=true)]
        static extern int RegOpenKeyEx(IntPtr key,string subkey,uint options,uint access,out IntPtr openedKey);
        [DllImport("advapi32.dll",EntryPoint="RegQueryValueExW",CharSet=CharSet.Unicode,ExactSpelling=true)]
        static extern int RegQueryValueEx(IntPtr key,string value,IntPtr reserved,out uint type,[Out] byte[] data,ref uint bytes);
        [DllImport("advapi32.dll",ExactSpelling=true)]
        static extern int RegCloseKey(IntPtr key);
    }
}
