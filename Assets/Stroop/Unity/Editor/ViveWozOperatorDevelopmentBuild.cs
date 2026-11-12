#if STROOP_XR_PREVIEW && UNITY_EDITOR
using System;
using System.IO;
using AvatarStroop.Unity;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEditor.XR.Management;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AvatarStroop.Unity.Editor
{
    // New isolated operator product; never modifies the participant/Quest scene.
    // The generated scene has no simulated operator and starts nothing automatically.
    public static class ViveWozOperatorDevelopmentBuild
    {
        const string ScenePath="Assets/Stroop/Generated/ViveWozOperatorDevelopment.unity";
        static void Require(bool condition,string message){if(!condition)throw new InvalidOperationException("VIVE operator development: "+message);}
        static string Project()
        {
            string p=Path.GetFullPath(Path.Combine(Application.dataPath,".."));
            Require(Application.isBatchMode&&!Application.isPlaying&&Application.unityVersion=="6000.3.25f1"&&
                File.Exists(Path.Combine(p,"quest-preview-preparation.json"))&&File.Exists(Path.Combine(p,"vive-operator-isolated-intent.json")),
                "a separately marked isolated project and Unity 6000.3.25f1 are required");
            return p;
        }
        public static void BuildWindowsCandidate()
        {
            string project=Project();
            var general=XRGeneralSettingsPerBuildTarget.XRGeneralSettingsForBuildTarget(BuildTargetGroup.Standalone);
            Require(general!=null&&!general.InitManagerOnStart&&general.Manager!=null&&!general.Manager.automaticLoading&&!general.Manager.automaticRunning,
                "automatic XR initialization/loading/running must be disabled in this isolated project");
            for(int i=0;i<SceneManager.sceneCount;i++)Require(!SceneManager.GetSceneAt(i).isDirty,"save open scenes before generating the operator scene");
            string target=Environment.GetEnvironmentVariable("STROOP_VIVE_OPERATOR_OUTPUT");
            Require(!String.IsNullOrWhiteSpace(target)&&Path.IsPathRooted(target),"explicit absolute output directory required");
            string output=Path.GetFullPath(target), parent=Path.GetDirectoryName(project)+Path.DirectorySeparatorChar;
            Require(output.StartsWith(parent,StringComparison.OrdinalIgnoreCase)&&!output.Equals(project,StringComparison.OrdinalIgnoreCase)&&
                !output.StartsWith(project+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase),"output must be separate from the project and inside its task workspace");
            Require(!Directory.Exists(output),"output must be a new directory; existing builds are never overwritten");
            if(!AssetDatabase.IsValidFolder("Assets/Stroop/Generated"))AssetDatabase.CreateFolder("Assets/Stroop","Generated");
            Require(AssetDatabase.LoadAssetAtPath<SceneAsset>(ScenePath)==null,"refusing to overwrite an existing operator scene");
            var scene=EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
            var root=new GameObject("ViveWozOperatorDevelopment");
            var bootstrap=root.AddComponent<ViveWozOperatorDevelopmentBootstrap>();
            Require(!bootstrap.Configured&&!bootstrap.NetworkOpen&&!bootstrap.PreflightReady&&!bootstrap.CanEnterLiveCountdown&&!bootstrap.CanRecordOrExport,
                "fresh application must be dormant and fail closed");
            Require(EditorSceneManager.SaveScene(scene,ScenePath),"operator scene save failed");
            Directory.CreateDirectory(output);
            PlayerSettings.resizableWindow=true;
            PlayerSettings.defaultScreenWidth=1600;PlayerSettings.defaultScreenHeight=1050;
            string executable=Path.Combine(output,"AvatarStroopViveWozOperatorDevelopment.exe");
            var result=BuildPipeline.BuildPlayer(new BuildPlayerOptions{scenes=new[]{ScenePath},locationPathName=executable,
                target=BuildTarget.StandaloneWindows64,options=BuildOptions.None});
            Require(result.summary.result==BuildResult.Succeeded&&File.Exists(executable),"operator candidate build failed");
            Debug.Log("VIVE_WOZ_OPERATOR_CANDIDATE: isolated dormant development process; explicit installed-source and authenticated task/preflight controls; recording off; no headset validation.");
        }
    }
}
#endif
