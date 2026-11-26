using System;
using System.IO;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
#if VIVE_LAUNCH_ENV_CHILD
    // A harmless console child used only by the Windows Framework build check.
    // No Unity, XR, UI, native client or device code is invoked.
    public static class VivePreviewEnvironmentChild
    {
        public static int Main(string[] args)
        {
            Console.OutputEncoding=new System.Text.UTF8Encoding(false);
            Console.WriteLine(Environment.GetEnvironmentVariable(VivePreviewLaunchPlan.RuntimeVariable));
            Console.WriteLine(Environment.GetEnvironmentVariable(VivePreviewLaunchPlan.OpenVrRuntimeVariable));
            Console.WriteLine(Environment.GetEnvironmentVariable(VivePreviewLaunchPlan.MarkerVariable));
            Console.WriteLine(Environment.CurrentDirectory);
            Console.WriteLine(args.Length);
            return 0;
        }
    }
#else
    public static class VivePreviewLaunchPlanTests
    {
        const string Manifest="{\"file_format_version\":\"1.0.0\",\"runtime\":{\"name\":\"SteamVR\",\"VALVE_runtime_is_steamvr\":true,\"library_path\":\"bin\\\\vrclient_x64.dll\"}}";
        sealed class Fixture : IDisposable
        {
            internal readonly string Root,Package,Runtime,ManifestPath,Library,OpenVrLibrary,Registration,Player;
            internal Fixture()
            {
                Root=Path.Combine(Path.GetTempPath(),"stroop-vive-launch-"+Guid.NewGuid().ToString("N"));
                Package=Path.Combine(Root,"package with spaces");
                Runtime=Path.Combine(Root,"Steam VR 安装 with spaces");
                Directory.CreateDirectory(Path.Combine(Runtime,"bin","win64"));
                ManifestPath=Path.Combine(Runtime,"steamxr_win64.json");Library=Path.Combine(Runtime,"bin","vrclient_x64.dll");
                OpenVrLibrary=Path.Combine(Runtime,"bin","win64","openvr_api.dll");
                Registration=Path.Combine(Root,"openvrpaths.vrpath");
                Player=Path.Combine(Package,"UnityPlayer",VivePreviewLaunchPlan.PlayerName);
                Directory.CreateDirectory(Path.Combine(Package,"UnityPlayer","AvatarStroopViveEyePlayer_Data"));
                File.WriteAllText(ManifestPath,Manifest);File.WriteAllText(Library,"not an executable library; path-validation fixture only");
                File.WriteAllText(OpenVrLibrary,"not an executable OpenVR library; path-validation fixture only");
                File.WriteAllText(Registration,RegistrationJson(Runtime));
                File.WriteAllText(Player,"not an executable player; path-validation fixture only");
            }
            internal VivePreviewLaunchPlan Plan(){return VivePreviewLaunchPlan.Create(Package,ManifestPath);}
            public void Dispose()
            {
                string expected=Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
                if(!Path.GetFullPath(Root).StartsWith(expected,StringComparison.Ordinal))throw new InvalidOperationException("Test cleanup escaped its temporary root");
                if(Directory.Exists(Root))Directory.Delete(Root,true);
            }
        }
        static string JsonString(string value)
        {return "\""+value.Replace("\\","\\\\").Replace("\"","\\\"")+"\"";}
        static string RegistrationJson(params string[] runtimes)
        {
            var values=new string[runtimes.Length];
            for(int i=0;i<runtimes.Length;i++)values[i]=JsonString(runtimes[i]);
            return "{\"jsonid\":\"vrpathreg\",\"version\":1,\"runtime\":["+String.Join(",",values)+"]}";
        }
        public static void Register(TestSuite suite)
        {
            suite.Run("VIVE launch selects SteamVR in child environment only, including space paths",()=>{
                string before=Environment.GetEnvironmentVariable(VivePreviewLaunchPlan.RuntimeVariable),marker=Environment.GetEnvironmentVariable(VivePreviewLaunchPlan.MarkerVariable);
                string openVrBefore=Environment.GetEnvironmentVariable(VivePreviewLaunchPlan.OpenVrRuntimeVariable);
                using(var f=new Fixture())
                {
                    var plan=f.Plan();var info=plan.CreateProcessStartInfo();
                    Check.Equal(Path.GetFullPath(f.Player),info.FileName);Check.Equal(Path.GetDirectoryName(f.Player),info.WorkingDirectory);
                    Check.True(!info.UseShellExecute);Check.Equal("",info.Arguments);
                    Check.Equal(Path.GetFullPath(f.ManifestPath),info.EnvironmentVariables[VivePreviewLaunchPlan.RuntimeVariable]);
                    Check.Equal(Path.GetFullPath(f.Runtime),info.EnvironmentVariables[VivePreviewLaunchPlan.OpenVrRuntimeVariable]);
                    Check.Equal(VivePreviewLaunchPlan.MarkerValue,info.EnvironmentVariables[VivePreviewLaunchPlan.MarkerVariable]);
                    Check.Equal(Path.GetFullPath(f.Library),plan.RuntimeLibraryPath);
                    Check.True(VivePreviewLaunchPlan.IsExpectedChildEnvironment(VivePreviewLaunchPlan.MarkerValue,plan.RuntimeManifestPath));
                    info.EnvironmentVariables[VivePreviewLaunchPlan.RuntimeVariable]="child-only mutation";
                    info.EnvironmentVariables[VivePreviewLaunchPlan.OpenVrRuntimeVariable]="child-only OpenVR mutation";
                }
                Check.Equal(before,Environment.GetEnvironmentVariable(VivePreviewLaunchPlan.RuntimeVariable));
                Check.Equal(marker,Environment.GetEnvironmentVariable(VivePreviewLaunchPlan.MarkerVariable));
                Check.Equal(openVrBefore,Environment.GetEnvironmentVariable(VivePreviewLaunchPlan.OpenVrRuntimeVariable));
            });
            suite.Run("VIVE launch replaces an inherited stale OpenVR override only in its child environment",()=>{
                string original=Environment.GetEnvironmentVariable(VivePreviewLaunchPlan.OpenVrRuntimeVariable);
                try
                {
                    using(var f=new Fixture())
                    {
                        string stale=Path.Combine(f.Root,"stale SteamVR installation");
                        Environment.SetEnvironmentVariable(VivePreviewLaunchPlan.OpenVrRuntimeVariable,stale);
                        var info=f.Plan().CreateProcessStartInfo();
                        Check.Equal(Path.GetFullPath(f.Runtime),info.EnvironmentVariables[VivePreviewLaunchPlan.OpenVrRuntimeVariable]);
                        Check.Equal(Path.GetFullPath(f.ManifestPath),info.EnvironmentVariables[VivePreviewLaunchPlan.RuntimeVariable]);
                        Check.Equal(stale,Environment.GetEnvironmentVariable(VivePreviewLaunchPlan.OpenVrRuntimeVariable));
                    }
                }
                finally{Environment.SetEnvironmentVariable(VivePreviewLaunchPlan.OpenVrRuntimeVariable,original);}
                Check.Equal(original,Environment.GetEnvironmentVariable(VivePreviewLaunchPlan.OpenVrRuntimeVariable));
            });
            suite.Run("VIVE launch refuses missing manifest, runtime library and player package",()=>{
                using(var f=new Fixture()){File.Delete(f.ManifestPath);Check.Throws<FileNotFoundException>(()=>f.Plan());}
                using(var f=new Fixture()){File.Delete(f.Library);Check.Throws<FileNotFoundException>(()=>f.Plan());}
                using(var f=new Fixture()){File.Delete(f.Player);Check.Throws<FileNotFoundException>(()=>f.Plan());}
                using(var f=new Fixture()){Directory.Delete(Path.Combine(f.Package,"UnityPlayer","AvatarStroopViveEyePlayer_Data"));Check.Throws<FileNotFoundException>(()=>f.Plan());}
            });
            suite.Run("VIVE launch refuses another runtime, x86 library, path escape and duplicate keys",()=>{
                foreach(string invalid in new[]{Manifest.Replace("SteamVR","Oculus"),Manifest.Replace("true","false"),
                    Manifest.Replace("vrclient_x64.dll","vrclient.dll"),Manifest.Replace("bin\\\\vrclient_x64.dll","..\\\\vrclient_x64.dll"),
                    Manifest.Replace("\"name\":\"SteamVR\"","\"name\":\"Oculus\",\"name\":\"SteamVR\""),
                    Manifest+" trailing",Manifest.Replace("1.0.0","2.0.0"),Manifest.Replace("true","null")})
                using(var f=new Fixture()){File.WriteAllText(f.ManifestPath,invalid);Check.Throws<InvalidDataException>(()=>f.Plan());}
            });
            suite.Run("VIVE launch marker requires an explicit absolute SteamVR manifest",()=>{
                Check.True(!VivePreviewLaunchPlan.IsExpectedChildEnvironment(null,null));
                Check.True(!VivePreviewLaunchPlan.IsExpectedChildEnvironment("other","steamxr_win64.json"));
                Check.True(!VivePreviewLaunchPlan.IsExpectedChildEnvironment(VivePreviewLaunchPlan.MarkerValue,"steamxr_win64.json"));
                using(var f=new Fixture())Check.True(!VivePreviewLaunchPlan.IsExpectedChildEnvironment(VivePreviewLaunchPlan.MarkerValue,Path.Combine(f.Root,"oculus.json")));
            });
            suite.Run("VIVE launch resolves registered custom SteamVR directory with escaped paths and normal registry metadata",()=>{
                using(var f=new Fixture())
                {
                    // Real openvrpaths files also contain non-runtime arrays and
                    // nullable metadata. Only the first registered runtime selects
                    // the installation; this must not depend on the default C: path.
                    string registration="{\"config\":[\"unrelated\\\\config\"],\"external_drivers\":null,"+
                        "\"jsonid\":\"vrpathreg\",\"log\":[\"unrelated\\\\log\"],\"version\":1,"+
                        "\"runtime\":["+JsonString(f.Runtime)+"],\"unrelated\":[null,true,false,1,{\"runtime\":null}]}";
                    File.WriteAllText(f.Registration,registration);
                    string manifest=VivePreviewLaunchPlan.ResolveRegisteredSteamVrManifest(f.Registration);
                    Check.Equal(Path.GetFullPath(f.ManifestPath),manifest);
                    var plan=VivePreviewLaunchPlan.Create(f.Package,manifest);
                    Check.Equal(Path.GetFullPath(f.Library),plan.RuntimeLibraryPath);
                    Check.Equal(Path.GetFullPath(f.ManifestPath),plan.CreateProcessStartInfo().EnvironmentVariables[VivePreviewLaunchPlan.RuntimeVariable]);
                }
            });
            suite.Run("VIVE launch rejects absent or malformed SteamVR registration without a default installation fallback",()=>{
                using(var f=new Fixture())
                {
                    File.Delete(f.Registration);
                    Check.Throws<FileNotFoundException>(()=>VivePreviewLaunchPlan.ResolveRegisteredSteamVrManifest(f.Registration));
                }
                foreach(string invalid in new[]{"{}","not JSON","[]",
                    "{\"jsonid\":\"other\",\"version\":1,\"runtime\":[]}",
                    "{\"jsonid\":\"vrpathreg\",\"version\":2,\"runtime\":[]}",
                    "{\"jsonid\":\"vrpathreg\",\"version\":\"1\",\"runtime\":[]}",
                    "{\"jsonid\":\"vrpathreg\",\"version\":1}",
                    "{\"jsonid\":\"vrpathreg\",\"version\":1,\"runtime\":null}",
                    "{\"jsonid\":\"vrpathreg\",\"version\":1,\"runtime\":\"SteamVR\"}",
                    "{\"jsonid\":\"vrpathreg\",\"version\":1,\"runtime\":[]}",
                    "{\"jsonid\":\"vrpathreg\",\"version\":1,\"runtime\":[null]}",
                    "{\"jsonid\":\"vrpathreg\",\"version\":1,\"runtime\":[1]}",
                    RegistrationJson("relative/SteamVR")})
                using(var f=new Fixture())
                {
                    File.WriteAllText(f.Registration,invalid);
                    Check.Throws<InvalidDataException>(()=>VivePreviewLaunchPlan.ResolveRegisteredSteamVrManifest(f.Registration));
                }
            });
            suite.Run("VIVE launch rejects ambiguous duplicate registration keys and trailing content",()=>{
                using(var f=new Fixture())
                {
                    string valid=RegistrationJson(f.Runtime);
                    foreach(string invalid in new[]{valid.Replace("\"jsonid\":\"vrpathreg\"","\"jsonid\":\"other\""),
                        valid.Replace("\"version\":1","\"version\":2"),valid.Replace("\"version\":1","\"version\":\"1\""),
                        valid.Replace("\"version\":1","\"version\":1,\"version\":1"),
                        valid.Replace("\"jsonid\":\"vrpathreg\"","\"jsonid\":\"other\",\"jsonid\":\"vrpathreg\""),
                        valid.Replace("\"runtime\":[","\"runtime\":[],\"runtime\":["),valid+" trailing"})
                    {
                        File.WriteAllText(f.Registration,invalid);
                        Check.Throws<InvalidDataException>(()=>VivePreviewLaunchPlan.ResolveRegisteredSteamVrManifest(f.Registration));
                    }
                }
            });
            suite.Run("VIVE launch validates the first registered runtime and never silently selects a secondary copy",()=>{
                using(var first=new Fixture())using(var second=new Fixture())
                {
                    File.WriteAllText(first.Registration,RegistrationJson(first.Runtime,second.Runtime));
                    Check.Equal(first.ManifestPath,VivePreviewLaunchPlan.ResolveRegisteredSteamVrManifest(first.Registration));
                    File.WriteAllText(first.ManifestPath,Manifest.Replace("SteamVR","Other runtime"));
                    Check.Throws<InvalidDataException>(()=>VivePreviewLaunchPlan.ResolveRegisteredSteamVrManifest(first.Registration));
                    File.WriteAllText(first.ManifestPath,Manifest);
                    File.Delete(first.Library);
                    Check.Throws<FileNotFoundException>(()=>VivePreviewLaunchPlan.ResolveRegisteredSteamVrManifest(first.Registration));
                    File.Delete(first.ManifestPath);
                    Check.Throws<FileNotFoundException>(()=>VivePreviewLaunchPlan.ResolveRegisteredSteamVrManifest(first.Registration));
                }
                using(var f=new Fixture())
                {
                    foreach(string invalidFirst in new[]{"null",JsonString("relative/SteamVR")})
                    {
                        File.WriteAllText(f.Registration,"{\"jsonid\":\"vrpathreg\",\"version\":1,\"runtime\":["+invalidFirst+","+JsonString(f.Runtime)+"]}");
                        Check.Throws<InvalidDataException>(()=>VivePreviewLaunchPlan.ResolveRegisteredSteamVrManifest(f.Registration));
                    }
                }
            });
            suite.Run("VIVE OpenVR model library stays with the launch manifest even if registration selects another installation",()=>{
                using(var first=new Fixture())using(var second=new Fixture())
                {
                    string selected=VivePreviewLaunchPlan.ResolveRegisteredSteamVrManifest(first.Registration);
                    File.WriteAllText(first.Registration,RegistrationJson(second.Runtime));
                    Check.Equal(second.ManifestPath,VivePreviewLaunchPlan.ResolveRegisteredSteamVrManifest(first.Registration));
                    Check.Equal(first.OpenVrLibrary,VivePreviewLaunchPlan.GetOpenVrLibraryPath(selected));
                    File.Delete(first.OpenVrLibrary);
                    Check.Throws<FileNotFoundException>(()=>VivePreviewLaunchPlan.GetOpenVrLibraryPath(selected));
                }
            });
            suite.Run("VIVE OpenVR model library requires a valid matching SteamVR manifest and x64 runtime",()=>{
                using(var f=new Fixture())
                {
                    File.Delete(f.Library);
                    Check.Throws<FileNotFoundException>(()=>VivePreviewLaunchPlan.GetOpenVrLibraryPath(f.ManifestPath));
                }
                using(var f=new Fixture())
                {
                    File.WriteAllText(f.ManifestPath,Manifest.Replace("vrclient_x64.dll","vrclient.dll"));
                    Check.Throws<InvalidDataException>(()=>VivePreviewLaunchPlan.GetOpenVrLibraryPath(f.ManifestPath));
                }
            });
        }
        public static void VerifyActualChild(string childExecutable)
        {
            Check.True(Environment.OSVersion.Platform==PlatformID.Win32NT,"The actual child test requires Windows Framework");
            Check.True(File.Exists(childExecutable));
            string before=Environment.GetEnvironmentVariable(VivePreviewLaunchPlan.RuntimeVariable),marker=Environment.GetEnvironmentVariable(VivePreviewLaunchPlan.MarkerVariable);
            string openVrBefore=Environment.GetEnvironmentVariable(VivePreviewLaunchPlan.OpenVrRuntimeVariable);
            using(var f=new Fixture())
            {
                File.Copy(childExecutable,f.Player,true);var info=f.Plan().CreateProcessStartInfo();
                info.CreateNoWindow=true;info.RedirectStandardOutput=true;info.RedirectStandardError=true;
                info.StandardOutputEncoding=System.Text.Encoding.UTF8;
                using(var child=System.Diagnostics.Process.Start(info))
                {
                    Check.True(child!=null);
                    if(!child.WaitForExit(10000))throw new TimeoutException("Harmless environment child did not exit; no XR process was launched");
                    string output=child.StandardOutput.ReadToEnd().Replace("\r","");
                    Check.Equal(0,child.ExitCode,child.StandardError.ReadToEnd());
                    Check.Equal(f.ManifestPath+"\n"+f.Runtime+"\n"+VivePreviewLaunchPlan.MarkerValue+"\n"+Path.GetDirectoryName(f.Player)+"\n0\n",output);
                }
            }
            Check.Equal(before,Environment.GetEnvironmentVariable(VivePreviewLaunchPlan.RuntimeVariable));
            Check.Equal(marker,Environment.GetEnvironmentVariable(VivePreviewLaunchPlan.MarkerVariable));
            Check.Equal(openVrBefore,Environment.GetEnvironmentVariable(VivePreviewLaunchPlan.OpenVrRuntimeVariable));
        }
    }
    public static class VivePreviewLaunchEnvironmentSmoke
    {
        public static int Main(string[] args)
        {
            var suite=new TestSuite();VivePreviewLaunchPlanTests.Register(suite);
            suite.Run("Actual Windows child inherits SteamVR override without changing parent environment",()=>{
                Check.Equal(1,args.Length);VivePreviewLaunchPlanTests.VerifyActualChild(args[0]);});
            Console.WriteLine("VIVE LAUNCH ENV RESULT "+suite.Passed+" passed; "+suite.Failed+" failed");
            return suite.Failed==0?0:1;
        }
    }
#endif
}
