using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace AvatarStroop.Unity.Editor
{
    // Explicit build action for a disposable development project; never runs on import.
    public static class DevelopmentPlayerBuild
    {
        public static void BuildWindowsPreview()
        {
            string output=Environment.GetEnvironmentVariable("STROOP_PREVIEW_OUTPUT");
            if(String.IsNullOrWhiteSpace(output)||!Path.IsPathRooted(output))
                throw new ArgumentException("Set STROOP_PREVIEW_OUTPUT to an absolute development output directory.");
            DevelopmentSceneBuilder.CreateScenes();
            PlayerSettings.SetScriptingBackend(NamedBuildTarget.Standalone,ScriptingImplementation.Mono2x);
            PlayerSettings.fullScreenMode=FullScreenMode.Windowed;
            PlayerSettings.defaultScreenWidth=1100;PlayerSettings.defaultScreenHeight=720;
            PlayerSettings.runInBackground=true;
            foreach(string role in new[]{"Participant","Operator"})
            {
                PlayerSettings.productName="Avatar Stroop "+role+" - SIMULATION";
                string target=Path.Combine(output,role,"AvatarStroop"+role+".exe");
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                var report=BuildPipeline.BuildPlayer(new BuildPlayerOptions {
                    scenes=new[]{"Assets/Stroop/Generated/"+role+"Development.unity"},
                    locationPathName=target,target=BuildTarget.StandaloneWindows64,options=BuildOptions.None
                });
                Debug.Log("DESKTOP_PREVIEW_BUILD "+role+" result="+report.summary.result+" errors="+report.summary.totalErrors+" warnings="+report.summary.totalWarnings);
                if(report.summary.result!=BuildResult.Succeeded||!File.Exists(target))
                    throw new InvalidOperationException("Desktop preview player build failed: "+role);
            }
        }
    }
}
