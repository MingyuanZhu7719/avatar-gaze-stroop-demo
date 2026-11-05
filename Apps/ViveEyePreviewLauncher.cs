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
                if(args.Length!=0)throw new ArgumentException("Open this launcher without command-line options; it never begins capture automatically.");
                using(var single=new Mutex(false,"Local\\AvatarStroopViveEyePreviewLauncher"))
                {
                    try{owns=single.WaitOne(0);}
                    catch(AbandonedMutexException){owns=true;}
                    if(!owns)throw new InvalidOperationException("A VIVE preview launcher is already running. Return to its player; no second player was opened.");
                    try
                    {
                        int current=Process.GetCurrentProcess().Id;
                        foreach(string name in new[]{VivePreviewLaunchPlan.LauncherProcessName,VivePreviewLaunchPlan.PlayerProcessName})
                        foreach(var process in Process.GetProcessesByName(name))
                        using(process)
                            if(process.Id!=current&&!process.HasExited)
                                throw new InvalidOperationException("Another VIVE preview is still running. Close it normally before opening this build. No process was stopped.");
                        var plan=VivePreviewLaunchPlan.Create(AppDomain.CurrentDomain.BaseDirectory,VivePreviewLaunchPlan.InstalledSteamVrManifest);
                        using(var player=Process.Start(plan.CreateProcessStartInfo()))
                        {
                            if(player==null)throw new InvalidOperationException("The VIVE player launch was not confirmed. Check existing windows before trying again.");
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
                MessageBox.Show("VIVE preview could not be opened.\n\n"+error.Message+
                    "\n\nThe system OpenXR runtime and eye-data consent were not changed.","Avatar Stroop - VIVE preview launcher",MessageBoxButtons.OK,MessageBoxIcon.Warning);
                return 1;
            }
        }
    }
}
