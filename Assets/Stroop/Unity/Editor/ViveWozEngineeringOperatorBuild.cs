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
    // Generates a separate dormant engineering product in a marked isolated
    // project. Never changes the experiment or Quest scenes.
    public static class ViveWozEngineeringOperatorBuild
    {
        const string ScenePath="Assets/Stroop/Generated/ViveWozEngineeringOperator.unity";
        static void Require(bool ok,string message){if(!ok)throw new InvalidOperationException("VIVE engineering operator: "+message);}
        public static void BuildWindowsCandidate()
        {
            string project=Path.GetFullPath(Path.Combine(Application.dataPath,".."));
            Require(Application.isBatchMode&&!Application.isPlaying&&Application.unityVersion=="6000.3.25f1"&&
                File.Exists(Path.Combine(project,"quest-preview-preparation.json"))&&File.Exists(Path.Combine(project,"vive-engineering-operator-isolated-intent.json")),
                "a separately marked isolated project and Unity 6000.3.25f1 are required");
            var general=XRGeneralSettingsPerBuildTarget.XRGeneralSettingsForBuildTarget(BuildTargetGroup.Standalone);
            Require(general!=null&&!general.InitManagerOnStart&&general.Manager!=null&&!general.Manager.automaticLoading&&!general.Manager.automaticRunning,
                "automatic XR initialization/loading/running must be disabled");
            for(int i=0;i<SceneManager.sceneCount;i++)Require(!SceneManager.GetSceneAt(i).isDirty,"save open scenes before generating the scene");
            string target=Environment.GetEnvironmentVariable("STROOP_VIVE_ENGINEERING_OPERATOR_OUTPUT");
            Require(!String.IsNullOrWhiteSpace(target)&&Path.IsPathRooted(target),"explicit absolute output directory required");
            string output=Path.GetFullPath(target),parent=Path.GetDirectoryName(project)+Path.DirectorySeparatorChar;
            Require(output.StartsWith(parent,StringComparison.OrdinalIgnoreCase)&&!output.Equals(project,StringComparison.OrdinalIgnoreCase)&&
                !output.StartsWith(project+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase),"output must be a separate workspace directory");
            Require(!Directory.Exists(output),"output must be a new directory");
            if(!AssetDatabase.IsValidFolder("Assets/Stroop/Generated"))AssetDatabase.CreateFolder("Assets/Stroop","Generated");
            Require(AssetDatabase.LoadAssetAtPath<SceneAsset>(ScenePath)==null,"refusing to overwrite an existing scene");
            var scene=EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
            var root=new GameObject("ViveWozEngineeringOperator");
            var bootstrap=root.AddComponent<ViveWozEngineeringOperatorBootstrap>();
            Require(!bootstrap.NetworkOpen&&!bootstrap.CanEnterLiveCountdown&&!bootstrap.CanRecordOrExport,"fresh application must be dormant");
            Require(EditorSceneManager.SaveScene(scene,ScenePath),"scene save failed");
            Directory.CreateDirectory(output);
            string executable=Path.Combine(output,"AvatarStroopViveWozEngineeringOperator.exe");
            var result=BuildPipeline.BuildPlayer(new BuildPlayerOptions{scenes=new[]{ScenePath},locationPathName=executable,
                target=BuildTarget.StandaloneWindows64,options=BuildOptions.None});
            Require(result.summary.result==BuildResult.Succeeded&&File.Exists(executable),"candidate build failed");
            Debug.Log("VIVE_WOZ_ENGINEERING_OPERATOR_CANDIDATE: dormant pose-only sender; uncalibrated; no task, recording or headset validation.");
        }
    }
}
#endif
