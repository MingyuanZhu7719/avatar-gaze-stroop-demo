using System;
using System.Collections.Generic;
using AvatarStroop.Core;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AvatarStroop.Unity.Editor
{
    // Scene generation is an explicit Editor action, not an editor-startup side effect.
    public static class DevelopmentSceneBuilder
    {
        const string DirectoryPath = "Assets/Stroop/Generated";

        [MenuItem("Avatar Stroop/Create development scenes (simulation)")]
        public static void CreateScenes()
        {
            for(int i = 0; i < SceneManager.sceneCount; i++)
                if(SceneManager.GetSceneAt(i).isDirty)
                    throw new InvalidOperationException("Save open scene edits before generating development scenes.");
            EnsureFolder("Assets/Stroop", "Generated");
            EnsureFolder(DirectoryPath, "Materials");
            Create(Actor.Participant);
            Create(Actor.Operator);
            AssetDatabase.SaveAssets();
            var scenes = new List<EditorBuildSettingsScene>(EditorBuildSettings.scenes);
            foreach(string role in new[] {"Participant", "Operator"})
            {
                string path = DirectoryPath + "/" + role + "Development.unity";
                if(!scenes.Exists(item => item.path == path)) scenes.Add(new EditorBuildSettingsScene(path, true));
            }
            EditorBuildSettings.scenes = scenes.ToArray();
            Debug.Log("Generated separate participant/operator SIMULATION presentation scenes. No XR runtime or headset validation.");
        }

        static void Create(Actor role)
        {
            string path = DirectoryPath + "/" + role + "Development.unity";
            if(AssetDatabase.LoadAssetAtPath<SceneAsset>(path) != null)
            {
                Debug.Log("Preserving existing development scene: " + path);
                return;
            }
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var cameraObject = new GameObject(role + "PresentationCamera");
            cameraObject.tag = "MainCamera";
            var camera = cameraObject.AddComponent<Camera>();
            camera.transform.position = new Vector3(0, 1.6f, 0);
            camera.backgroundColor = new Color(.035f, .045f, .06f);
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.nearClipPlane = .05f; camera.farClipPlane = 20; camera.fieldOfView = 60;
            var root = new GameObject(role + "WorldSpacePresentation");
            var view = root.AddComponent<WorldSpaceExperimentView>();
            view.Initialize(camera, role);
            root.AddComponent<DevelopmentApplicationBootstrap>();
            view.SetFaults(new[] {"SIMULATION application. Owner starts practice through the development Inspector."});
            PersistMaterials(scene, role.ToString());
            if(!EditorSceneManager.SaveScene(scene, path)) throw new InvalidOperationException("Could not save " + path);
        }

        static void PersistMaterials(Scene scene, string role)
        {
            var saved = new HashSet<Material>(); int index = 0;
            foreach(var root in scene.GetRootGameObjects())
            foreach(var renderer in root.GetComponentsInChildren<Renderer>(true))
            foreach(var material in renderer.sharedMaterials)
            {
                if(material == null || !String.IsNullOrEmpty(AssetDatabase.GetAssetPath(material)) || !saved.Add(material)) continue;
                string path = AssetDatabase.GenerateUniqueAssetPath(DirectoryPath + "/Materials/" + role + "-" + index++ + ".mat");
                AssetDatabase.CreateAsset(material, path);
            }
        }
        // Explicit upgrade of this tool's two generated scenes. CreateScenes still
        // preserves existing files; the isolated preview build opts into migration.
        public static void UpgradeClassroomScene(Actor role)
        {
            if(role!=Actor.Participant&&role!=Actor.Operator)throw new ArgumentException("Only presentation roles have generated scenes.");
            for(int i=0;i<SceneManager.sceneCount;i++)
                if(SceneManager.GetSceneAt(i).isDirty)throw new InvalidOperationException("Save open scene edits before upgrading generated scenes.");
            string path=DirectoryPath+"/"+role+"Development.unity";
            var scene=EditorSceneManager.OpenScene(path,OpenSceneMode.Single);
            WorldSpaceExperimentView view=null;
            foreach(var root in scene.GetRootGameObjects())
            foreach(var candidate in root.GetComponentsInChildren<WorldSpaceExperimentView>(true))
            {if(view!=null)throw new InvalidOperationException("Generated scene has more than one presentation.");view=candidate;}
            if(view==null||view.Role!=role||view.PresentationCamera==null)throw new InvalidOperationException("Generated scene role/camera is invalid.");
            view.Initialize(view.PresentationCamera,role);
            ClassroomPresentationValidation.ValidateView(view);
            PersistMaterials(scene,role.ToString());
            EditorUtility.SetDirty(view);
            if(!EditorSceneManager.SaveScene(scene,path))throw new InvalidOperationException("Could not save upgraded "+path);
        }
        static void EnsureFolder(string parent, string name)
        {
            if(!AssetDatabase.IsValidFolder(parent + "/" + name)) AssetDatabase.CreateFolder(parent, name);
        }
    }
}
