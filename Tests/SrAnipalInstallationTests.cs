using System;
using System.Collections.Generic;
using System.IO;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    // Synthetic locations only: no native DLL load, registry access, directories,
    // device acquisition, calibration or process-wide environment changes.
    public static class SrAnipalInstallationTests
    {
        sealed class Files
        {
            internal readonly string Root=Path.GetFullPath(Path.Combine(Path.GetTempPath(),"stroop-eye-install-"+Guid.NewGuid().ToString("N")));
            internal readonly HashSet<string> Present=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            internal readonly Dictionary<string,string> Versions=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
            internal readonly HashSet<string> X86=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            internal readonly List<string> Queries=new List<string>();
            internal int VersionReads,ArchitectureReads;
            internal string Directory(string name){return Path.Combine(Root,name);}
            internal static string Dashboard(string root)
            {return Path.Combine(root,"tools","eye_calibration_dashboard","EyeCalibrationDashboard_Data","Plugins","SRanipal.dll");}
            internal static string Calibration(string root)
            {return Path.Combine(root,"tools","eye_calibration","EyeCalibration_Data","Plugins","SRanipal.dll");}
            internal bool Exists(string path){Queries.Add(path);return Present.Contains(path);}
            internal string Version(string path){VersionReads++;string version;return Versions.TryGetValue(path,out version)?version:"1.3.5.1";}
            internal bool IsX64(string path){ArchitectureReads++;return !X86.Contains(path);}
            internal SrAnipalInstalledClientLocation Resolve(string explicitDll,params SrAnipalInstallRoot[] roots)
            {return SrAnipalInstalledClientLocation.Resolve(explicitDll,roots,Exists,Version,IsX64);}
            internal bool Queried(string path)
            {return Queries.Exists(item=>String.Equals(item,path,StringComparison.OrdinalIgnoreCase));}
        }
        static Exception Rejected(Action action)
        {
            try{action();}catch(Exception error){return error;}
            throw new Exception("Expected installation selection to reject this candidate.");
        }
        public static void Register(TestSuite suite)
        {
            suite.Run("SRanipal resolves a registered custom install with Unicode spaces and associated runtime",()=>{
                var f=new Files();string custom=f.Directory("E-drive HTC 安装 with spaces");
                string dll=Files.Dashboard(custom),runtime=Path.Combine(custom,"sr_runtime.exe");
                f.Present.Add(dll);f.Present.Add(runtime);
                var result=f.Resolve(null,new SrAnipalInstallRoot(custom,"registered custom install"));
                Check.Equal(dll,result.LibraryPath);Check.Equal(runtime,result.RuntimePath);
                Check.Equal("registered custom install",result.DiscoverySource);Check.Equal("1.3.5.1",result.FileVersion);
                Check.Equal(1,f.VersionReads);Check.Equal(1,f.ArchitectureReads);
            });
            suite.Run("SRanipal preferred registered installation wins over an existing default copy",()=>{
                var f=new Files();string custom=f.Directory("custom E drive"),old=f.Directory("default C drive");
                f.Present.Add(Files.Dashboard(custom));f.Present.Add(Files.Dashboard(old));
                var result=f.Resolve(null,new SrAnipalInstallRoot(custom,"registered"),new SrAnipalInstallRoot(old,"default"));
                Check.Equal(Files.Dashboard(custom),result.LibraryPath);
                Check.True(!f.Queried(Files.Dashboard(old)),"Old default copy was examined after the registered match.");
            });
            suite.Run("SRanipal supports the installed non-dashboard calibration client layout",()=>{
                var f=new Files();string custom=f.Directory("HTC moved installation");f.Present.Add(Files.Calibration(custom));
                var result=f.Resolve(null,new SrAnipalInstallRoot(custom,"registered"));
                Check.Equal(Files.Calibration(custom),result.LibraryPath);
                Check.True(f.Queried(Files.Dashboard(custom)),"The preferred dashboard layout was not examined.");
            });
            suite.Run("SRanipal skips missing installed roots and retains the selected source diagnostic",()=>{
                var f=new Files();string removed=f.Directory("removed installation"),current=f.Directory("current installation");
                f.Present.Add(Files.Dashboard(current));
                var result=f.Resolve(null,new SrAnipalInstallRoot(removed,"stale registration"),new SrAnipalInstallRoot(current,"current registration"));
                Check.Equal(Files.Dashboard(current),result.LibraryPath);Check.Equal("current registration",result.DiscoverySource);
                Check.True(!String.IsNullOrWhiteSpace(result.SearchDiagnostic));
            });
            suite.Run("SRanipal client resolution does not require a separately discoverable runtime executable",()=>{
                var f=new Files();string custom=f.Directory("installed SDK client");f.Present.Add(Files.Dashboard(custom));
                var result=f.Resolve(null,new SrAnipalInstallRoot(custom,"registered"));
                Check.Equal(Files.Dashboard(custom),result.LibraryPath);Check.Equal(String.Empty,result.RuntimePath);
            });
            suite.Run("SRanipal explicit known-layout DLL overrides all automatic candidates",()=>{
                var f=new Files();string chosen=f.Directory("explicit installation"),other=f.Directory("other installation");
                string dll=Files.Calibration(chosen),runtime=Path.Combine(chosen,"sr_runtime.exe");
                f.Present.Add(dll);f.Present.Add(runtime);f.Present.Add(Files.Dashboard(other));
                var result=f.Resolve(dll,new SrAnipalInstallRoot(other,"automatic"));
                Check.Equal(dll,result.LibraryPath);Check.Equal(runtime,result.RuntimePath);
                Check.True(result.DiscoverySource.Contains("explicit"));Check.True(!f.Queried(Files.Dashboard(other)));
            });
            suite.Run("SRanipal explicit DLL can use an uncommon vendor layout without inventing a runtime executable",()=>{
                var f=new Files();string dll=Path.Combine(f.Directory("manual vendor install"),"SRanipal.dll");f.Present.Add(dll);
                var result=f.Resolve(dll);
                Check.Equal(dll,result.LibraryPath);Check.Equal(String.Empty,result.RuntimePath);
            });
            suite.Run("SRanipal missing explicit DLL does not silently fall back to another installed client",()=>{
                var f=new Files();string missing=Path.Combine(f.Directory("missing override"),"SRanipal.dll"),fallback=f.Directory("default");
                f.Present.Add(Files.Dashboard(fallback));
                Check.Throws<FileNotFoundException>(()=>f.Resolve(missing,new SrAnipalInstallRoot(fallback,"default")));
                Check.True(!f.Queried(Files.Dashboard(fallback)));Check.Equal(0,f.VersionReads);Check.Equal(0,f.ArchitectureReads);
            });
            suite.Run("SRanipal explicit override rejects relative paths and unrelated DLL filenames",()=>{
                var f=new Files();
                foreach(string path in new[]{"SRanipal.dll",Path.Combine("relative","SRanipal.dll"),Path.Combine(f.Root,"OtherClient.dll")})
                    Rejected(()=>f.Resolve(path));
                Check.Equal(0,f.VersionReads);Check.Equal(0,f.ArchitectureReads);
            });
            suite.Run("SRanipal incompatible first installed client fails before any automatic fallback",()=>{
                foreach(string incompatible in new[]{"1.3.5.0","1.3.6.1","2.0.0.0","",null})
                {
                    var f=new Files();string current=f.Directory("registered"),fallback=f.Directory("old default");
                    string dll=Files.Dashboard(current);f.Present.Add(dll);f.Present.Add(Files.Dashboard(fallback));f.Versions[dll]=incompatible;
                    Check.Throws<InvalidOperationException>(()=>f.Resolve(null,new SrAnipalInstallRoot(current,"registered"),new SrAnipalInstallRoot(fallback,"default")));
                    Check.True(!f.Queried(Files.Dashboard(fallback)),"Unsupported ABI silently selected a different install.");
                }
            });
            suite.Run("SRanipal x86 client is rejected rather than replaced by a different install",()=>{
                var f=new Files();string current=f.Directory("registered"),fallback=f.Directory("default");string dll=Files.Dashboard(current);
                f.Present.Add(dll);f.Present.Add(Files.Dashboard(fallback));f.X86.Add(dll);
                Check.Throws<InvalidOperationException>(()=>f.Resolve(null,new SrAnipalInstallRoot(current,"registered"),new SrAnipalInstallRoot(fallback,"default")));
                Check.True(!f.Queried(Files.Dashboard(fallback)));Check.Equal(1,f.ArchitectureReads);
            });
            suite.Run("SRanipal explicit client retains exact version and x64 checks",()=>{
                var f=new Files();string dll=Path.Combine(f.Root,"SRanipal.dll");f.Present.Add(dll);f.Versions[dll]="1.3.6.12";
                Check.Throws<InvalidOperationException>(()=>f.Resolve(dll));
                f.Versions[dll]="1.3.5.1";f.X86.Add(dll);
                Check.Throws<InvalidOperationException>(()=>f.Resolve(dll));
            });
            suite.Run("SRanipal discovery deduplicates Windows-style case variants of missing roots",()=>{
                var f=new Files();string missing=f.Directory("missing HTC"),current=f.Directory("current HTC");f.Present.Add(Files.Dashboard(current));
                var result=f.Resolve(null,new SrAnipalInstallRoot(missing,"first"),new SrAnipalInstallRoot(missing.ToUpperInvariant()+Path.DirectorySeparatorChar,"duplicate"),new SrAnipalInstallRoot(current,"current"));
                Check.Equal(Files.Dashboard(current),result.LibraryPath);
                var counts=new Dictionary<string,int>(StringComparer.OrdinalIgnoreCase);
                foreach(string query in f.Queries)
                {
                    if(!String.Equals(Path.GetFileName(query),"SRanipal.dll",StringComparison.OrdinalIgnoreCase))continue;
                    int count;counts.TryGetValue(query,out count);counts[query]=count+1;
                }
                foreach(var count in counts)Check.Equal(1,count.Value,"Repeated candidate: "+count.Key);
            });
            suite.Run("SRanipal missing-install diagnostics identify the client without searching the working directory",()=>{
                var f=new Files();string root=f.Directory("absent registered installation");
                string unrelated=Path.Combine(Environment.CurrentDirectory,"SRanipal.dll");f.Present.Add(unrelated);
                var error=Rejected(()=>f.Resolve(null,new SrAnipalInstallRoot(root,"registered missing")));
                Check.True(error is FileNotFoundException);Check.True(error.Message.Contains("SRanipal"));
                Check.True(!f.Queried(unrelated));Check.Equal(0,f.VersionReads);Check.Equal(0,f.ArchitectureReads);
                Check.True(!Directory.Exists(f.Root),"Synthetic location tests wrote files or directories.");
            });
        }
    }
}
