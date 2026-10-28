#if STROOP_XR_PREVIEW && UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AvatarStroop.Core;
using Unity.XR.CoreUtils;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEditor.XR.Management;
using UnityEditor.XR.Management.Metadata;
using UnityEditor.XR.OpenXR;
using UnityEditor.XR.OpenXR.Features;
using UnityEngine;
using UnityEngine.InputSystem.XR;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using UnityEngine.XR.Management;
using UnityEngine.XR.OpenXR;
using UnityEngine.XR.OpenXR.Features;
using UnityEngine.XR.OpenXR.Features.Interactions;

namespace AvatarStroop.Unity.Editor
{
    // Explicit batch actions for the marked isolated preview; never execute on import.
    public static class QuestPreviewBuild
    {
        const string ExpectedEditor="6000.3.25f1";
        const string SettingsPath="Assets/Stroop/QuestPreviewSettings";
        const string ScenePath="Assets/Stroop/Generated/ParticipantQuestPreview.unity";
        const string OperatorScene="Assets/Stroop/Generated/OperatorDevelopment.unity";
        [Serializable] sealed class Preparation {public int format;public string source;public string editor_version;}
        [Serializable] sealed class SceneEvidence
        {
            public string role,scene;public int camera_count,rig_count;
            public bool role_matches,camera_linked,rig_references_valid,stereo_both;
        }
        [Serializable] sealed class SceneEvidenceFile {public bool hardware_validated=false;public SceneEvidence[] scenes;}

        static string RequireIsolated()
        {
            string project=Path.GetFullPath(Path.Combine(Application.dataPath,".."));
            string marker=Path.Combine(project,"quest-preview-preparation.json");
            if(Application.unityVersion!=ExpectedEditor||!File.Exists(marker))
                throw new InvalidOperationException("Quest preview requires a marked isolated project and Unity "+ExpectedEditor);
            var preparation=JsonUtility.FromJson<Preparation>(File.ReadAllText(marker));
            if(preparation==null||preparation.format!=1||preparation.editor_version!=ExpectedEditor||
                String.IsNullOrEmpty(preparation.source)||String.Equals(Path.GetFullPath(preparation.source),project,StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Refusing to configure the desktop source or an unmarked project.");
            return project;
        }

        public static void Configure()
        {
            string project=RequireIsolated();
            foreach(var sceneIndex in Enumerable.Range(0,SceneManager.sceneCount))
                if(SceneManager.GetSceneAt(sceneIndex).isDirty)throw new InvalidOperationException("Save open scenes before configuring the isolated preview.");
            if(!AssetDatabase.IsValidFolder(SettingsPath))AssetDatabase.CreateFolder("Assets/Stroop","QuestPreviewSettings");
            XRGeneralSettingsPerBuildTarget perBuild;
            if(!EditorBuildSettings.TryGetConfigObject(XRGeneralSettings.settingsKey,out perBuild)||perBuild==null)
            {
                perBuild=ScriptableObject.CreateInstance<XRGeneralSettingsPerBuildTarget>();
                AssetDatabase.CreateAsset(perBuild,SettingsPath+"/XRGeneralSettings.asset");
                EditorBuildSettings.AddConfigObject(XRGeneralSettings.settingsKey,perBuild,true);
            }
            if(!perBuild.HasSettingsForBuildTarget(BuildTargetGroup.Standalone))perBuild.CreateDefaultSettingsForBuildTarget(BuildTargetGroup.Standalone);
            if(!perBuild.HasManagerSettingsForBuildTarget(BuildTargetGroup.Standalone))perBuild.CreateDefaultManagerSettingsForBuildTarget(BuildTargetGroup.Standalone);
            var general=perBuild.SettingsForBuildTarget(BuildTargetGroup.Standalone);
            var manager=general.Manager;
            general.InitManagerOnStart=false;
            manager.automaticLoading=false;manager.automaticRunning=false;
            if(!manager.TrySetLoaders(new List<XRLoader>()))throw new InvalidOperationException("Could not clear isolated preview XR loaders.");
            if(!XRPackageMetadataStore.AssignLoader(manager,"UnityEngine.XR.OpenXR.OpenXRLoader",BuildTargetGroup.Standalone))
                throw new InvalidOperationException("Could not assign the installed official OpenXR loader.");
            var openxr=OpenXRSettings.GetSettingsForBuildTargetGroup(BuildTargetGroup.Standalone);
            if(openxr==null)
            {
                // OpenXR's concrete settings class is internal. Use its public
                // package metadata and Unity's ScriptableObject factory instead.
                var packageDefinition=new OpenXRManagementSettings();
                var settingsType=TypeCache.GetTypesDerivedFrom<ScriptableObject>().Single(type=>type.FullName==packageDefinition.metadata.settingsType);
                var packageSettings=ScriptableObject.CreateInstance(settingsType);
                AssetDatabase.CreateAsset(packageSettings,SettingsPath+"/OpenXRPackageSettings.asset");
                if(!packageDefinition.PopulateNewSettingsInstance(packageSettings))throw new InvalidOperationException("Could not register official OpenXR settings.");
                openxr=OpenXRSettings.GetSettingsForBuildTargetGroup(BuildTargetGroup.Standalone);
            }
            if(openxr==null)throw new InvalidOperationException("Could not create Standalone OpenXR settings.");
            FeatureHelpers.RefreshFeatures(BuildTargetGroup.Standalone);
            foreach(var feature in openxr.GetFeatures<OpenXRFeature>())feature.enabled=feature is OculusTouchControllerProfile;
            var touch=openxr.GetFeature<OculusTouchControllerProfile>();
            if(touch==null)throw new InvalidOperationException("Official Oculus Touch controller profile is unavailable.");
            touch.enabled=true;openxr.renderMode=OpenXRSettings.RenderMode.MultiPass;
            EditorUtility.SetDirty(touch);EditorUtility.SetDirty(openxr);EditorUtility.SetDirty(general);EditorUtility.SetDirty(manager);EditorUtility.SetDirty(perBuild);

            PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneWindows64,false);
            PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneWindows64,new[]{GraphicsDeviceType.Direct3D11});
            PlayerSettings.SetScriptingBackend(NamedBuildTarget.Standalone,ScriptingImplementation.Mono2x);
            PlayerSettings.fullScreenMode=FullScreenMode.Windowed;
            PlayerSettings.defaultScreenWidth=1100;PlayerSettings.defaultScreenHeight=720;
            PlayerSettings.runInBackground=true;

            DevelopmentSceneBuilder.CreateScenes();
            var scene=EditorSceneManager.OpenScene("Assets/Stroop/Generated/ParticipantDevelopment.unity",OpenSceneMode.Single);
            var views=scene.GetRootGameObjects().SelectMany(root=>root.GetComponentsInChildren<WorldSpaceExperimentView>(true)).ToArray();
            if(views.Length!=1||views[0].Role!=Actor.Participant)throw new InvalidOperationException("Expected exactly one participant presentation.");
            var view=views[0];
            var rig=view.GetComponent<QuestLinkPreviewRig>();
            if(rig==null)rig=view.gameObject.AddComponent<QuestLinkPreviewRig>();
            rig.Configure(view.PresentationCamera);
            view.PresentationCamera.stereoTargetEye=StereoTargetEyeMask.Both;
            view.PresentationCamera.allowDynamicResolution=false;
            PersistMaterials(scene);
            EditorUtility.SetDirty(rig);
            if(!EditorSceneManager.SaveScene(scene,ScenePath))throw new InvalidOperationException("Could not save participant Quest preview scene.");
            EditorBuildSettings.scenes=new[]{new EditorBuildSettingsScene(ScenePath,true),new EditorBuildSettingsScene(OperatorScene,true)};

            // The next batch invocation reopens the Editor with both input backends.
            var playerAssets=AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/ProjectSettings.asset");
            if(playerAssets.Length==0)throw new InvalidOperationException("Unity did not expose its PlayerSettings asset.");
            var serialized=new SerializedObject(playerAssets[0]);
            var input=serialized.FindProperty("activeInputHandler");
            if(input==null)throw new InvalidOperationException("Cannot configure the installed Editor's input backend.");
            input.intValue=2;serialized.ApplyModifiedPropertiesWithoutUndo();
            AssetDatabase.SaveAssets();
            File.WriteAllText(Path.Combine(project,"quest-preview-configured.json"),
                "{\"editor\":\""+Application.unityVersion+"\",\"graphics\":\"Direct3D11\",\"stereo\":\"MultiPass\",\"input\":\"Both\",\"automatic_xr_start\":false,\"participant_scene\":\""+ScenePath+"\",\"hardware_validated\":false}\n");
            Debug.Log("QUEST_PREVIEW_CONFIGURED: participant-only manual OpenXR; D3D11; MultiPass; Input Both. No headset/runtime was started.");
        }

        static void PersistMaterials(Scene scene)
        {
            var saved=new HashSet<Material>();int index=0;
            foreach(var root in scene.GetRootGameObjects())
            foreach(var renderer in root.GetComponentsInChildren<Renderer>(true))
            foreach(var material in renderer.sharedMaterials)
            {
                if(material==null||!String.IsNullOrEmpty(AssetDatabase.GetAssetPath(material))||!saved.Add(material))continue;
                string path=AssetDatabase.GenerateUniqueAssetPath("Assets/Stroop/Generated/Materials/QuestPreview-"+(index++)+".mat");
                AssetDatabase.CreateAsset(material,path);
            }
        }

        static SceneEvidence ValidateScene(Scene scene,string role)
        {
            var roots=scene.GetRootGameObjects();
            var views=roots.SelectMany(root=>root.GetComponentsInChildren<WorldSpaceExperimentView>(true)).ToArray();
            var cameras=roots.SelectMany(root=>root.GetComponentsInChildren<Camera>(true)).ToArray();
            var rigs=roots.SelectMany(root=>root.GetComponentsInChildren<QuestLinkPreviewRig>(true)).ToArray();
            bool participant=role=="Participant";
            var evidence=new SceneEvidence{role=role,scene=scene.path,camera_count=cameras.Length,rig_count=rigs.Length,
                role_matches=views.Length==1&&views[0].Role==(participant?Actor.Participant:Actor.Operator),
                camera_linked=views.Length==1&&cameras.Length==1&&views[0].PresentationCamera==cameras[0],
                rig_references_valid=!participant&&rigs.Length==0,stereo_both=cameras.Length==1&&cameras[0].stereoTargetEye==StereoTargetEyeMask.Both};
            if(participant&&rigs.Length==1&&views.Length==1)
            {
                var serialized=new SerializedObject(rigs[0]);
                var camera=serialized.FindProperty("previewCamera").objectReferenceValue as Camera;
                var origin=serialized.FindProperty("origin").objectReferenceValue as XROrigin;
                var driver=serialized.FindProperty("poseDriver").objectReferenceValue as TrackedPoseDriver;
                evidence.rig_references_valid=rigs[0].gameObject==views[0].gameObject&&camera==views[0].PresentationCamera&&
                    camera!=null&&origin!=null&&driver!=null&&origin.Camera==camera&&driver.gameObject==camera.gameObject&&
                    origin.CameraFloorOffsetObject!=null&&camera.transform.parent==origin.CameraFloorOffsetObject.transform&&
                    Mathf.Approximately(origin.CameraYOffset,1.6f)&&origin.RequestedTrackingOriginMode==XROrigin.TrackingOriginMode.Device&&
                    !camera.allowDynamicResolution;
            }
            if(!evidence.role_matches||!evidence.camera_linked||!evidence.rig_references_valid||
                evidence.rig_count!=(participant?1:0)||(participant&&!evidence.stereo_both))
                throw new InvalidOperationException("Quest preview scene structure failed validation: "+role);
            return evidence;
        }

        public static void BuildWindowsPreview()
        {
            string project=RequireIsolated();
            if(!File.Exists(Path.Combine(project,"quest-preview-configured.json")))throw new InvalidOperationException("Run Configure in a separate batch invocation first.");
            string output=Environment.GetEnvironmentVariable("STROOP_QUEST_PREVIEW_OUTPUT");
            if(String.IsNullOrWhiteSpace(output)||!Path.IsPathRooted(output))throw new ArgumentException("An absolute STROOP_QUEST_PREVIEW_OUTPUT is required.");
            output=Path.GetFullPath(output);
            if(output.Equals(project,StringComparison.OrdinalIgnoreCase)||output.StartsWith(project+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Build output must not overwrite the project or Assets.");
            var general=XRGeneralSettingsPerBuildTarget.XRGeneralSettingsForBuildTarget(BuildTargetGroup.Standalone);
            var openxr=OpenXRSettings.GetSettingsForBuildTargetGroup(BuildTargetGroup.Standalone);
            if(general==null||general.InitManagerOnStart||general.Manager==null||general.Manager.automaticLoading||general.Manager.automaticRunning||
                general.Manager.activeLoaders.Count!=1||!(general.Manager.activeLoaders[0] is OpenXRLoader)||
                openxr==null||openxr.renderMode!=OpenXRSettings.RenderMode.MultiPass||!openxr.GetFeature<OculusTouchControllerProfile>().enabled)
                throw new InvalidOperationException("Preview XR settings changed; reconfigure before building.");
            var playerSettings=new SerializedObject(AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/ProjectSettings.asset")[0]);
            if(playerSettings.FindProperty("activeInputHandler").intValue!=2)throw new InvalidOperationException("Both input backends must be enabled before building.");
            var evidence=new List<SceneEvidence>();
            foreach(string role in new[]{"Participant","Operator"})
            {
                string scenePath=role=="Participant"?ScenePath:OperatorScene;
                var scene=EditorSceneManager.OpenScene(scenePath,OpenSceneMode.Single);
                evidence.Add(ValidateScene(scene,role));
                PlayerSettings.productName="Avatar Stroop "+role+" - QUEST LINK PREVIEW";
                string executable=Path.Combine(output,role,"AvatarStroop"+role+".exe");
                Directory.CreateDirectory(Path.GetDirectoryName(executable));
                var report=BuildPipeline.BuildPlayer(new BuildPlayerOptions{scenes=new[]{scenePath},locationPathName=executable,
                    target=BuildTarget.StandaloneWindows64,options=BuildOptions.None});
                Debug.Log("QUEST_PREVIEW_BUILD "+role+" result="+report.summary.result+" errors="+report.summary.totalErrors+" warnings="+report.summary.totalWarnings);
                if(report.summary.result!=BuildResult.Succeeded||!File.Exists(executable))throw new InvalidOperationException("Quest preview build failed: "+role);
            }
            File.WriteAllText(Path.Combine(output,"quest-scene-validation.json"),JsonUtility.ToJson(new SceneEvidenceFile{scenes=evidence.ToArray()},true)+"\n");
            Debug.Log("QUEST_PREVIEW_BUILD_COMPLETE: build evidence only; no player or headset was launched.");
        }
    }
}
#endif
