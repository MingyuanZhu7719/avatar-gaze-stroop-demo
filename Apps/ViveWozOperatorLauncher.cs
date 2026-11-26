using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using AvatarStroop.Runtime;

namespace AvatarStroop.Apps
{
    // Same resolver as the participant launcher. Paths remain managed strings,
    // avoiding the code-page conversion of a batch FOR/F resolver.
    public static class ViveWozOperatorLauncher
    {
        public const string PlayerName="AvatarStroopViveWozOperatorDevelopment.exe";
        public static ProcessStartInfo CreateStartInfo(string package,string manifest,string logFolder)
        {
            if(!Path.IsPathRooted(package)||!Path.IsPathRooted(logFolder))
                throw new ArgumentException("Full package and log paths are required.");
            package=Path.GetFullPath(package);logFolder=Path.GetFullPath(logFolder);
            string player=Path.Combine(package,PlayerName);
            if(!File.Exists(player)||!Directory.Exists(Path.Combine(package,Path.GetFileNameWithoutExtension(PlayerName)+"_Data")))
                throw new FileNotFoundException("Keep the launcher beside the complete WoZ Operator executable and Data folder.");
            VivePreviewLaunchPlan.ValidateRuntimeManifest(manifest);
            var start=new ProcessStartInfo {FileName=player,WorkingDirectory=package,UseShellExecute=false,
                Arguments="-logFile \""+Path.Combine(logFolder,"Player.log")+"\""};
            start.EnvironmentVariables[VivePreviewLaunchPlan.RuntimeVariable]=Path.GetFullPath(manifest);
            start.EnvironmentVariables[VivePreviewLaunchPlan.OpenVrRuntimeVariable]=Path.GetDirectoryName(Path.GetFullPath(manifest));
            return start;
        }
        static void Note(string folder,string message)
        {File.AppendAllText(Path.Combine(folder,"Launcher.txt"),message+Environment.NewLine,new UTF8Encoding(false));}

        [STAThread]
        public static int Main(string[] args)
        {
            string runFolder=null;bool owns=false;
            try
            {
                if(args.Length>1)throw new ArgumentException("Double-click the launcher, or supply one installed steamxr_win64.json file.");
                using(var single=new Mutex(false,"Local\\AvatarStroopWozOperatorLauncher"))
                {
                    try{owns=single.WaitOne(0);}catch(AbandonedMutexException){owns=true;}
                    if(!owns)throw new InvalidOperationException("Close the existing WoZ Operator program before opening another.");
                    try
                    {
                        foreach(var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(PlayerName)))
                        using(process)if(!process.HasExited)
                            throw new InvalidOperationException("Close the existing WoZ Operator program before opening another.");
                        string package=Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory);
                        runFolder=Path.Combine(package,"WoZLogs","run-"+DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ")+"-"+Guid.NewGuid().ToString("N"));
                        Directory.CreateDirectory(runFolder);
                        Note(runFolder,"WoZ Operator diagnostic launch\nStarted UTC: "+DateTime.UtcNow.ToString("O"));
                        Note(runFolder,"Runtime selection: "+(args.Length==0?"registered SteamVR installation":"explicit manifest override"));
                        string manifest=args.Length==0?VivePreviewLaunchPlan.InstalledSteamVrManifest:Path.GetFullPath(args[0]);
                        Note(runFolder,"SteamVR manifest: "+manifest);
                        Note(runFolder,"OpenVR runtime root (child-only VR_OVERRIDE): "+Path.GetDirectoryName(manifest));
                        Note(runFolder,"OpenXR runtime library: "+VivePreviewLaunchPlan.ValidateRuntimeManifest(manifest));
                        try{Note(runFolder,"OpenVR model library: "+VivePreviewLaunchPlan.GetOpenVrLibraryPath(manifest));}
                        catch(Exception error){Note(runFolder,"Optional OpenVR model library unavailable: "+error.Message);}
                        var start=CreateStartInfo(package,manifest,runFolder);
                        using(var player=Process.Start(start))
                        {
                            if(player==null)throw new InvalidOperationException("Could not confirm the operator process started.");
                            player.WaitForExit();Note(runFolder,"Process exit code: "+player.ExitCode);
                            if(!File.Exists(Path.Combine(runFolder,"Player.log")))Note(runFolder,"The player did not create Player.log during this launch.");
                            return player.ExitCode;
                        }
                    }
                    finally{if(owns)single.ReleaseMutex();}
                }
            }
            catch(Exception error)
            {
                if(runFolder!=null&&Directory.Exists(runFolder))
                    try{Note(runFolder,"Launch failed: "+error.GetType().Name+": "+error.Message);}catch(IOException){}catch(UnauthorizedAccessException){}
                MessageBox.Show("WoZ Operator could not be opened.\n\n"+error.Message+
                    "\n\nKeep the complete package together. Launch SteamVR once to register its installation, or drag its steamxr_win64.json onto Start-WoZ-Operator.cmd.",
                    "Avatar Stroop - WoZ Operator launcher",MessageBoxButtons.OK,MessageBoxIcon.Warning);
                return 1;
            }
            finally
            {
                if(runFolder!=null&&Directory.Exists(runFolder))
                    try{Process.Start(new ProcessStartInfo(runFolder){UseShellExecute=true});}catch(Exception){ /* The error dialog or in-app export remains available. */ }
            }
        }
    }
}
