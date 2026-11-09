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
    public static class ViveWozEngineeringParticipantBuild
    {
        const string ScenePath="Assets/Stroop/Generated/ViveWozEngineeringParticipant.unity";
        static void Require(bool condition,string message){if(!condition)throw new InvalidOperationException("VIVE engineering participant: "+message);}
        public static void BuildWindowsCandidate()
        {
            string project=Path.GetFullPath(Path.Combine(Application.dataPath,".."));
            Require(Application.isBatchMode&&!Application.isPlaying&&Application.unityVersion=="6000.3.25f1"&&
                File.Exists(Path.Combine(project,"quest-preview-preparation.json"))&&File.Exists(Path.Combine(project,"vive-engineering-participant-isolated-intent.json")),"an explicitly marked isolated project and Unity 6000.3.25f1 are required");
            var general=XRGeneralSettingsPerBuildTarget.XRGeneralSettingsForBuildTarget(BuildTargetGroup.Standalone);
            Require(general!=null&&!general.InitManagerOnStart&&general.Manager!=null&&!general.Manager.automaticLoading&&!general.Manager.automaticRunning,"XR automatic start must be disabled");
            for(int i=0;i<SceneManager.sceneCount;i++)Require(!SceneManager.GetSceneAt(i).isDirty,"open scenes must not have unsaved changes");
            string target=Environment.GetEnvironmentVariable("STROOP_VIVE_ENGINEERING_PARTICIPANT_OUTPUT");
            Require(!String.IsNullOrWhiteSpace(target)&&Path.IsPathRooted(target),"an explicit absolute output directory is required");
            string output=Path.GetFullPath(target),parent=Path.GetDirectoryName(project)+Path.DirectorySeparatorChar;
            Require(output.StartsWith(parent,StringComparison.OrdinalIgnoreCase)&&!output.Equals(project,StringComparison.OrdinalIgnoreCase)&&!output.StartsWith(project+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase),"output must be a separate workspace directory");
            Require(!Directory.Exists(output),"refusing to overwrite an existing output");
            if(!AssetDatabase.IsValidFolder("Assets/Stroop/Generated"))AssetDatabase.CreateFolder("Assets/Stroop","Generated");
            Require(AssetDatabase.LoadAssetAtPath<SceneAsset>(ScenePath)==null,"refusing to overwrite an existing scene");
            var scene=EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
            var root=new GameObject("ViveWozEngineeringParticipant");
            var bootstrap=root.AddComponent<ViveWozEngineeringParticipantBootstrap>();
            Require(!bootstrap.NetworkOpen&&!bootstrap.CanEnterLiveCountdown&&!bootstrap.CanRecordOrExport,"new viewer must be dormant and have no study or recording gate");
            Require(EditorSceneManager.SaveScene(scene,ScenePath),"scene save failed");
            Directory.CreateDirectory(output);
            string executable=Path.Combine(output,"AvatarStroopViveWozEngineeringParticipant.exe");
            var result=BuildPipeline.BuildPlayer(new BuildPlayerOptions{scenes=new[]{ScenePath},locationPathName=executable,target=BuildTarget.StandaloneWindows64,options=BuildOptions.None});
            Require(result.summary.result==BuildResult.Succeeded&&File.Exists(executable),"candidate build failed");
            Debug.Log("VIVE_WOZ_ENGINEERING_PARTICIPANT: unconfigured isolated viewer; no task or recording; no headset validation.");
        }
    }
}
#endif
