#if WOZ_OPERATOR_LAUNCH_SMOKE
using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using AvatarStroop.Apps;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    // Packaging-only Windows smoke. The child is a console fixture, never Unity
    // or SteamVR. Production Main (which opens UI/folders) is not invoked.
    public static class ViveWozOperatorLauncherSmoke
    {
#if WOZ_OPERATOR_LAUNCH_CHILD
        public static int Main(string[] args)
        {
            Console.OutputEncoding=new UTF8Encoding(false);
            Console.WriteLine(Environment.GetEnvironmentVariable(VivePreviewLaunchPlan.RuntimeVariable));
            Console.WriteLine(Environment.GetEnvironmentVariable(VivePreviewLaunchPlan.OpenVrRuntimeVariable));
            Console.WriteLine(Environment.CurrentDirectory);
            foreach(string value in args)Console.WriteLine(value);
            return 0;
        }
#else
        static void Require(bool value,string message){if(!value)throw new Exception(message);}
        public static int Main(string[] args)
        {
            string root=Path.Combine(Path.GetTempPath(),"woz-launch-"+Guid.NewGuid().ToString("N"));
            string before=Environment.GetEnvironmentVariable(VivePreviewLaunchPlan.RuntimeVariable);
            string beforeOpenVr=Environment.GetEnvironmentVariable(VivePreviewLaunchPlan.OpenVrRuntimeVariable);
            try
            {
                Require(args.Length==1,"Expected one harmless console child path");
                string package=Path.Combine(root,"package with spaces 中文 & percent%"),runtime=Path.Combine(root,"registered SteamVR 中文");
                string logFolder=Path.Combine(root,"logs with spaces 中文 & percent%");
                Directory.CreateDirectory(Path.Combine(runtime,"bin","win64"));Directory.CreateDirectory(logFolder);
                Directory.CreateDirectory(Path.Combine(package,"AvatarStroopViveWozOperatorDevelopment_Data"));
                string manifest=Path.Combine(runtime,"steamxr_win64.json");
                File.WriteAllText(manifest,"{\"file_format_version\":\"1.0.0\",\"runtime\":{\"name\":\"SteamVR\",\"VALVE_runtime_is_steamvr\":true,\"library_path\":\"bin/vrclient_x64.dll\"}}");
                File.WriteAllText(Path.Combine(runtime,"bin","vrclient_x64.dll"),"synthetic path fixture only");
                string openVr=Path.Combine(runtime,"bin","win64","openvr_api.dll");File.WriteAllText(openVr,"synthetic path fixture only");
                File.Copy(args[0],Path.Combine(package,ViveWozOperatorLauncher.PlayerName));
                var start=ViveWozOperatorLauncher.CreateStartInfo(package,manifest,logFolder);
                Require(!start.UseShellExecute,"Child must use explicit environment");
                Require(start.EnvironmentVariables[VivePreviewLaunchPlan.RuntimeVariable]==manifest,"Selected manifest was not inherited");
                Require(start.EnvironmentVariables[VivePreviewLaunchPlan.OpenVrRuntimeVariable]==runtime,"Selected OpenVR runtime root was not inherited");
                Require(Environment.GetEnvironmentVariable(VivePreviewLaunchPlan.RuntimeVariable)==before,"Parent environment was changed");
                Require(Environment.GetEnvironmentVariable(VivePreviewLaunchPlan.OpenVrRuntimeVariable)==beforeOpenVr,"Parent OpenVR environment was changed");
                start.RedirectStandardOutput=true;start.RedirectStandardError=true;start.CreateNoWindow=true;
                start.StandardOutputEncoding=new UTF8Encoding(false);
                using(var child=Process.Start(start))
                {
                    Require(child!=null,"Synthetic child did not start");
                    Require(child.WaitForExit(10000),"Synthetic child did not exit");
                    Require(child.ExitCode==0,child.StandardError.ReadToEnd());
                    Require(child.StandardOutput.ReadToEnd().Replace("\r","")==manifest+"\n"+runtime+"\n"+package+"\n-logFile\n"+Path.Combine(logFolder,"Player.log")+"\n",
                        "Unicode paths or log argument changed in the real child");
                }
                File.Delete(openVr);
                Require(ViveWozOperatorLauncher.CreateStartInfo(package,manifest,logFolder)!=null,"Optional model absence blocked launch");
                File.Delete(Path.Combine(runtime,"bin","vrclient_x64.dll"));bool rejected=false;
                try{ViveWozOperatorLauncher.CreateStartInfo(package,manifest,logFolder);}catch(FileNotFoundException){rejected=true;}
                Require(rejected,"Missing selected OpenXR runtime must not fall back to another installation");
                Console.WriteLine("WOZ OPERATOR LAUNCH SMOKE: child environment, Unicode paths, log argument and missing-library rejection passed");
                return 0;
            }
            catch(Exception error){Console.Error.WriteLine(error);return 1;}
            finally
            {
                string temp=Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
                if(!Path.GetFullPath(root).StartsWith(temp,StringComparison.Ordinal))throw new Exception("Temporary cleanup escaped root");
                if(Directory.Exists(root))Directory.Delete(root,true);
            }
        }
#endif
    }
}
#endif
