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
            Console.WriteLine(Environment.GetEnvironmentVariable(VivePreviewLaunchPlan.RuntimeVariable));
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
            internal readonly string Root,Package,ManifestPath,Library,Player;
            internal Fixture()
            {
                Root=Path.Combine(Path.GetTempPath(),"stroop-vive-launch-"+Guid.NewGuid().ToString("N"));
                Package=Path.Combine(Root,"package with spaces");
                string runtime=Path.Combine(Root,"Steam VR with spaces");
                Directory.CreateDirectory(Path.Combine(runtime,"bin"));
                ManifestPath=Path.Combine(runtime,"steamxr_win64.json");Library=Path.Combine(runtime,"bin","vrclient_x64.dll");
                Player=Path.Combine(Package,"UnityPlayer",VivePreviewLaunchPlan.PlayerName);
                Directory.CreateDirectory(Path.Combine(Package,"UnityPlayer","AvatarStroopViveEyePlayer_Data"));
                File.WriteAllText(ManifestPath,Manifest);File.WriteAllText(Library,"not an executable library; path-validation fixture only");
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
        public static void Register(TestSuite suite)
        {
            suite.Run("VIVE launch selects SteamVR in child environment only, including space paths",()=>{
                string before=Environment.GetEnvironmentVariable(VivePreviewLaunchPlan.RuntimeVariable),marker=Environment.GetEnvironmentVariable(VivePreviewLaunchPlan.MarkerVariable);
                using(var f=new Fixture())
                {
                    var plan=f.Plan();var info=plan.CreateProcessStartInfo();
                    Check.Equal(Path.GetFullPath(f.Player),info.FileName);Check.Equal(Path.GetDirectoryName(f.Player),info.WorkingDirectory);
                    Check.True(!info.UseShellExecute);Check.Equal("",info.Arguments);
                    Check.Equal(Path.GetFullPath(f.ManifestPath),info.EnvironmentVariables[VivePreviewLaunchPlan.RuntimeVariable]);
                    Check.Equal(VivePreviewLaunchPlan.MarkerValue,info.EnvironmentVariables[VivePreviewLaunchPlan.MarkerVariable]);
                    Check.Equal(Path.GetFullPath(f.Library),plan.RuntimeLibraryPath);
                    Check.True(VivePreviewLaunchPlan.IsExpectedChildEnvironment(VivePreviewLaunchPlan.MarkerValue,plan.RuntimeManifestPath));
                    info.EnvironmentVariables[VivePreviewLaunchPlan.RuntimeVariable]="child-only mutation";
                }
                Check.Equal(before,Environment.GetEnvironmentVariable(VivePreviewLaunchPlan.RuntimeVariable));
                Check.Equal(marker,Environment.GetEnvironmentVariable(VivePreviewLaunchPlan.MarkerVariable));
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
        }
        public static void VerifyActualChild(string childExecutable)
        {
            Check.True(Environment.OSVersion.Platform==PlatformID.Win32NT,"The actual child test requires Windows Framework");
            Check.True(File.Exists(childExecutable));
            string before=Environment.GetEnvironmentVariable(VivePreviewLaunchPlan.RuntimeVariable),marker=Environment.GetEnvironmentVariable(VivePreviewLaunchPlan.MarkerVariable);
            using(var f=new Fixture())
            {
                File.Copy(childExecutable,f.Player,true);var info=f.Plan().CreateProcessStartInfo();
                info.CreateNoWindow=true;info.RedirectStandardOutput=true;info.RedirectStandardError=true;
                using(var child=System.Diagnostics.Process.Start(info))
                {
                    Check.True(child!=null);
                    if(!child.WaitForExit(10000))throw new TimeoutException("Harmless environment child did not exit; no XR process was launched");
                    string output=child.StandardOutput.ReadToEnd().Replace("\r","");
                    Check.Equal(0,child.ExitCode,child.StandardError.ReadToEnd());
                    Check.Equal(f.ManifestPath+"\n"+VivePreviewLaunchPlan.MarkerValue+"\n"+Path.GetDirectoryName(f.Player)+"\n0\n",output);
                }
            }
            Check.Equal(before,Environment.GetEnvironmentVariable(VivePreviewLaunchPlan.RuntimeVariable));
            Check.Equal(marker,Environment.GetEnvironmentVariable(VivePreviewLaunchPlan.MarkerVariable));
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
