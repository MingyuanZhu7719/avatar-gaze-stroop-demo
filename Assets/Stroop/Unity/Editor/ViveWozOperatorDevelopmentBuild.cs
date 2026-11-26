#if STROOP_XR_PREVIEW && UNITY_EDITOR
using System;
using System.IO;
using AvatarStroop.Core;
using AvatarStroop.Runtime;
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
        static void CheckParticipantAvatarView()
        {
            var camera=new GameObject("FixtureOperatorCamera").AddComponent<Camera>();
            var view=new GameObject("FixtureOperatorView").AddComponent<WorldSpaceExperimentView>();
            try
            {
                view.Initialize(camera,Actor.Operator);
                var task=new TaskSnapshot("display-fixture",0,1,"trial-1",Colour.Red,Colour.Blue,Phase.MainTask,Condition.A);
                view.ApplyTaskView(ViveWozOperatorTaskView.FromFreshTask(task));
                Require(view.AvatarRoot.gameObject.activeSelf,"one-way main task still has its placeholder");
                var operatorPosition=new Vector3(.32f,1.61f,-.13f);
                view.BeginWozParticipantAvatarView(operatorPosition);
                Require(!view.AvatarRoot.gameObject.activeSelf,"two-way view starts without a pose");
                var left=view.AvatarHead.Find("LeftEye");
                var right=view.AvatarHead.Find("RightEye");
                var origin=(left.position+right.position)*.5f;
                var board=(view.TaskWorldPosition-origin).normalized;
                var direct=(operatorPosition-origin).normalized;
                Require(Vector3.Angle(board,direct)>20,"the board and evaluator must be distinct targets");
                var room=view.AvatarRoot.parent;
                var boardLocal=room.InverseTransformDirection(board);
                view.ShowWozParticipantPose(Rotation.Identity,new Vec3(boardLocal.x,boardLocal.y,boardLocal.z));
                Require(view.AvatarRoot.gameObject.activeSelf && Vector3.Angle(left.forward,board)<.1f,"neutral head looks at board");
                var head=Quaternion.Euler(0,14,0);
                var eyes=Quaternion.Inverse(head)*room.InverseTransformDirection(direct);
                view.ShowWozParticipantPose(new Rotation(head.x,head.y,head.z,head.w),new Vec3(eyes.x,eyes.y,eyes.z));
                Require(Quaternion.Angle(view.AvatarHead.localRotation,head)<.1f && Vector3.Angle(left.forward,direct)<.1f &&
                    Vector3.Angle(right.forward,direct)<.1f,"head and both eyes turn toward the evaluator without mirroring");
                view.ClearWozParticipantPose();
                Require(!view.AvatarRoot.gameObject.activeSelf,"tracking loss clears the avatar");
                view.ApplyTaskView(ViveWozOperatorTaskView.FromFreshTask(null));
                view.ShowWozParticipantPose(Rotation.Identity,new Vec3(0,0,1));
                Require(!view.AvatarRoot.gameObject.activeSelf,"outside formal trials the avatar stays hidden");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(view.gameObject);
                UnityEngine.Object.DestroyImmediate(camera.gameObject);
            }
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
            // This scene builds the entire classroom at runtime. Include the
            // materials explicitly so player stripping cannot remove its shaders.
            const string resourceFolder="Assets/Stroop/Generated/Resources";
            if(!AssetDatabase.IsValidFolder(resourceFolder))AssetDatabase.CreateFolder("Assets/Stroop/Generated","Resources");
            foreach(string shaderName in new[]{"Unlit/Color","AvatarStroop/WorldSpaceOverlay"})
            {
                var shader=Shader.Find(shaderName);
                Require(shader!=null,"operator classroom shader missing: "+shaderName);
                AssetDatabase.CreateAsset(new Material(shader),resourceFolder+"/"+shaderName.Replace('/','_')+".mat");
            }
            Require(AssetDatabase.LoadAssetAtPath<SceneAsset>(ScenePath)==null,"refusing to overwrite an existing operator scene");
            var scene=EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
            ViveWozOperatorDiagnosticsFixture.Validate();
            CheckParticipantAvatarView();
            var root=new GameObject("ViveWozOperatorDevelopment");
            var bootstrap=root.AddComponent<ViveWozOperatorDevelopmentBootstrap>();
            Require(!bootstrap.Configured&&!bootstrap.NetworkOpen&&!bootstrap.PreflightReady&&!bootstrap.CanEnterLiveCountdown&&!bootstrap.CanRecordOrExport,
                "fresh application must be dormant and fail closed");
            Require(EditorSceneManager.SaveScene(scene,ScenePath),"operator scene save failed");
            Directory.CreateDirectory(output);
            PlayerSettings.productName="Avatar Stroop - WoZ Operator";
            PlayerSettings.bundleVersion="2026.11.26.2";
            PlayerSettings.usePlayerLog=true;
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
