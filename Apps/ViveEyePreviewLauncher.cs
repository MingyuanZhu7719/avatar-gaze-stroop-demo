using System;
using System.Diagnostics;
using System.Threading;
using System.Windows.Forms;
using AvatarStroop.Runtime;

namespace AvatarStroop.Apps
{
    // Separate executable: its child environment exists before Unity native
    // pre-init. Never changes the configured system/user OpenXR runtime.
    public static class ViveEyePreviewLauncher
    {
        [STAThread]
        public static int Main(string[] args)
        {
            Application.EnableVisualStyles();
            bool owns=false;
            try
            {
                if(args.Length!=0)throw new ArgumentException("Open this launcher without command-line options.");
                using(var single=new Mutex(false,"Local\\AvatarStroopViveEyePreviewLauncher"))
                {
                    try{owns=single.WaitOne(0);}
                    catch(AbandonedMutexException){owns=true;}
                    if(!owns)throw new InvalidOperationException("VIVE preview is already open. Return to its window.");
                    try
                    {
                        int current=Process.GetCurrentProcess().Id;
                        foreach(string name in new[]{VivePreviewLaunchPlan.LauncherProcessName,VivePreviewLaunchPlan.PlayerProcessName})
                        foreach(var process in Process.GetProcessesByName(name))
                        using(process)
                            if(process.Id!=current&&!process.HasExited)
                                throw new InvalidOperationException("Close the running VIVE preview before opening this build.");
                        var plan=VivePreviewLaunchPlan.Create(AppDomain.CurrentDomain.BaseDirectory,VivePreviewLaunchPlan.InstalledSteamVrManifest);
                        using(var player=Process.Start(plan.CreateProcessStartInfo()))
                        {
                            if(player==null)throw new InvalidOperationException("Could not confirm the VIVE player opened. Check existing windows before retrying.");
                            // Retain the single-instance lease for the child's full lifetime.
                            // This launcher has no hidden experiment or capture startup action.
                            player.WaitForExit();return player.ExitCode;
                        }
                    }
                    finally {if(owns)single.ReleaseMutex();}
                }
            }
            catch(Exception error)
            {
                MessageBox.Show("VIVE preview could not be opened.\n\n"+error.Message,
                    "Avatar Stroop - VIVE preview launcher",MessageBoxButtons.OK,MessageBoxIcon.Warning);
                return 1;
            }
        }
    }
}
