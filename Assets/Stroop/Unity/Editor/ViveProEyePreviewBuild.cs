#if STROOP_XR_PREVIEW && UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using AvatarStroop.Core;
using AvatarStroop.Runtime;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using UnityEngine.XR.Management;
using UnityEditor.XR.Management;

namespace AvatarStroop.Unity.Editor
{
    // Explicit author/build commands for the new isolated diagnostic project.
    public static class ViveProEyePreviewBuild
    {
        const string ScenePath="Assets/Stroop/Generated/ViveProEyeDiagnostic.unity";
        [Serializable] sealed class Evidence
        {
            public string scene=ScenePath,editor,executable;
            public bool explicit_start_only=true,has_study_bootstrap=false,records_samples=false,sends_network=false;
            public bool headset_verified=false,eye_accuracy_verified=false,vendor_binaries_bundled=false;
            public int scene_checks;
        }
        static int checks;
        static void Require(bool condition,string message)
        {checks++;if(!condition)throw new InvalidOperationException("VIVE diagnostic scene: "+message);}
        static string Project()
        {
            string project=Path.GetFullPath(Path.Combine(Application.dataPath,".."));
            Require(Application.unityVersion=="6000.3.25f1"&&File.Exists(Path.Combine(project,"quest-preview-preparation.json")),
                "requires the marked isolated Unity 6000.3.25f1 project");
            Require(Application.isBatchMode&&!Application.isPlaying,"requires an explicit batch action outside Play Mode");
            return project;
        }
        static string Output(string project)
        {
            string value=Environment.GetEnvironmentVariable("STROOP_VIVE_PREVIEW_OUTPUT");
            if(String.IsNullOrWhiteSpace(value)||!Path.IsPathRooted(value))throw new ArgumentException("Set an absolute STROOP_VIVE_PREVIEW_OUTPUT.");
            string output=Path.GetFullPath(value),workspace=Path.GetDirectoryName(project)+Path.DirectorySeparatorChar;
            Require(output.StartsWith(workspace,StringComparison.OrdinalIgnoreCase)&&
                !output.Equals(project,StringComparison.OrdinalIgnoreCase)&&!output.StartsWith(project+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase),
                "output must be separate from the isolated project in its workspace");
            Directory.CreateDirectory(output);return output;
        }
        public static void Configure()
        {
            checks=0;Project();
            // This only configures project assets from cached official packages.
            // It neither starts OpenXR nor changes the system's selected runtime.
            QuestPreviewBuild.Configure();
            foreach(var index in Enumerable.Range(0,SceneManager.sceneCount))
                Require(!SceneManager.GetSceneAt(index).isDirty,"save open scene changes before authoring");
            var scene=EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
            var root=new GameObject("ViveProEyeDiagnostic");
            var origin=new GameObject("ApplicationTrackingOrigin").transform;
            var offset=new GameObject("ReferenceHeightOffset").transform;offset.SetParent(origin,false);offset.localPosition=new Vector3(0,1.6f,0);
            var cameraObject=new GameObject("ViveDiagnosticCamera");cameraObject.tag="MainCamera";
            var camera=cameraObject.AddComponent<Camera>();camera.transform.SetParent(offset,false);
            camera.backgroundColor=new Color(.025f,.035f,.055f);camera.clearFlags=CameraClearFlags.SolidColor;
            camera.nearClipPlane=.05f;camera.farClipPlane=20;camera.fieldOfView=70;camera.stereoTargetEye=StereoTargetEyeMask.Both;
            camera.allowDynamicResolution=false;
            Box("Floor",root.transform,new Vector3(0,-.04f,2),new Vector3(7,.08f,8),new Color(.14f,.17f,.21f));
            Box("BackWall",root.transform,new Vector3(0,1.5f,3.8f),new Vector3(7,3,.1f),new Color(.18f,.22f,.28f));
            Box("BoardFrame",root.transform,new Vector3(0,1.46f,2.55f),new Vector3(2.64f,2.32f,.12f),new Color(.12f,.16f,.2f));
            Box("Board",root.transform,new Vector3(0,1.46f,2.48f),new Vector3(2.5f,2.18f,.02f),new Color(.93f,.95f,.97f));
            Text("Title",root.transform,new Vector3(0,2.39f,2.46f),"VIVE PRO EYE",.035f,new Color(.02f,.1f,.17f),2.25f,.15f);
            Text("Subtitle",root.transform,new Vector3(0,2.23f,2.46f),"LIVE HARDWARE REHEARSAL",.012f,new Color(.1f,.15f,.22f),2.3f,.1f);
            Text("Scope",root.transform,new Vector3(0,2.09f,2.46f),"Not an experimental block. No samples are saved.",.009f,new Color(.1f,.15f,.22f),2.3f,.09f);
            foreach(var position in new[]{new Vector2(-.7f,1.81f),new Vector2(0,1.81f),new Vector2(.7f,1.81f),new Vector2(-.35f,1.5f),new Vector2(.35f,1.5f)})
            {
                Box("GazeTarget",root.transform,new Vector3(position.x,position.y,2.455f),new Vector3(.095f,.095f,.006f),new Color(.08f,.23f,.36f));
                Box("TargetCentre",root.transform,new Vector3(position.x,position.y,2.448f),new Vector3(.025f,.025f,.006f),Color.white);
            }
            Text("MarkerMeaning",root.transform,new Vector3(0,1.31f,2.455f),"Cyan: new gaze. Amber: previous sample, up to 100 ms.",.009f,new Color(.08f,.18f,.25f),2.3f,.09f);
            Box("StatusPanel",root.transform,new Vector3(0,.87f,2.45f),new Vector3(2.35f,.68f,.016f),new Color(.04f,.1f,.16f));
            var status=Text("Status",root.transform,new Vector3(-1.1f,1.14f,2.43f),
                "STOPPED - no eye capture\nUse Begin live preview on the desktop.\nNo study session or data file is created.",.012f,Color.white,2.2f,.54f);
            status.anchor=TextAnchor.UpperLeft;status.alignment=TextAlignment.Left;
            Text("Limits",root.transform,new Vector3(0,.43f,2.43f),"Unvalidated mapping. HMD-centre ray; no accuracy claim.",.009f,new Color(.1f,.15f,.22f),2.3f,.09f);
            var marker=Primitive("CurrentValidGazeDirection",PrimitiveType.Sphere,root.transform,new Vector3(0,1.8f,2.42f),new Vector3(.045f,.045f,.012f),Color.cyan).transform;
            var avatar=new GameObject("UnvalidatedObserverRehearsal").transform;avatar.SetParent(root.transform,false);avatar.localPosition=new Vector3(1.75f,0,2.5f);
            // Face the viewer at neutral pose. Captured rotations/directions stay
            // in this avatar's local engineering frame, not room/world axes.
            avatar.localRotation=Quaternion.Euler(0,180,0);
            Box("Body",avatar,new Vector3(0,.95f,0),new Vector3(.45f,.75f,.24f),new Color(.15f,.42f,.55f));
            Box("LeftLeg",avatar,new Vector3(-.13f,.3f,0),new Vector3(.16f,.6f,.18f),new Color(.12f,.2f,.3f));
            Box("RightLeg",avatar,new Vector3(.13f,.3f,0),new Vector3(.16f,.6f,.18f),new Color(.12f,.2f,.3f));
            var head=new GameObject("CurrentTrackedHead").transform;head.SetParent(avatar,false);head.localPosition=new Vector3(0,1.55f,0);
            Primitive("HeadShape",PrimitiveType.Sphere,head,Vector3.zero,new Vector3(.34f,.4f,.32f),new Color(.72f,.75f,.77f));
            var left=Eye("LeftEye",head,new Vector3(-.075f,.045f,.14f));
            var right=Eye("RightEye",head,new Vector3(.075f,.045f,.14f));
            Text("AvatarCaption",root.transform,new Vector3(1.75f,2.05f,2.43f),"RAW HEAD / EYE\nREHEARSAL",.012f,Color.white,.85f,.2f);
            var preview=root.AddComponent<ViveProEyePreview>();preview.Configure(camera,origin,offset,marker,head,left,right,status);
            PersistMaterials(scene);Validate(scene);
            Require(EditorSceneManager.SaveScene(scene,ScenePath),"could not save scene");
            EditorBuildSettings.scenes=new[]{new EditorBuildSettingsScene(ScenePath,true)};
            AssetDatabase.SaveAssets();
            Debug.Log("VIVE_PREVIEW_CONFIGURED: explicit start only; no runtime or capture initialized.");
        }
        static Transform Eye(string name,Transform head,Vector3 position)
        {
            var eye=new GameObject(name).transform;eye.SetParent(head,false);eye.localPosition=position;
            Primitive("Sclera",PrimitiveType.Sphere,eye,Vector3.zero,new Vector3(.075f,.065f,.045f),Color.white);
            Primitive("Pupil",PrimitiveType.Sphere,eye,new Vector3(0,0,.025f),new Vector3(.027f,.027f,.015f),Color.black);
            return eye;
        }
        static GameObject Primitive(string name,PrimitiveType type,Transform parent,Vector3 position,Vector3 size,Color colour)
        {
            var result=GameObject.CreatePrimitive(type);result.name=name;result.transform.SetParent(parent,false);
            result.transform.localPosition=position;result.transform.localScale=size;
            UnityEngine.Object.DestroyImmediate(result.GetComponent<Collider>());
            var shader=Shader.Find("Unlit/Color");if(shader==null)throw new InvalidOperationException("Built-in Unlit/Color shader is unavailable.");
            var material=new Material(shader);material.color=colour;result.GetComponent<Renderer>().sharedMaterial=material;return result;
        }
        static void Box(string name,Transform parent,Vector3 position,Vector3 size,Color colour)
        {Primitive(name,PrimitiveType.Cube,parent,position,size,colour);}
        static TextMesh Text(string name,Transform parent,Vector3 position,string value,float size,Color colour,float width,float height)
        {
            var go=new GameObject(name);go.transform.SetParent(parent,false);go.transform.localPosition=position;
            var mesh=go.AddComponent<TextMesh>();mesh.text=value;mesh.characterSize=size;mesh.fontSize=70;
            mesh.font=Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");mesh.anchor=TextAnchor.MiddleCenter;mesh.alignment=TextAlignment.Center;mesh.color=colour;
            go.GetComponent<Renderer>().sharedMaterial=mesh.font.material;
            ViveProEyePreview.FitText(mesh,width,height,size);return mesh;
        }
        static void PersistMaterials(Scene scene)
        {
            foreach(var renderer in scene.GetRootGameObjects().SelectMany(root=>root.GetComponentsInChildren<Renderer>(true)))
            foreach(var material in renderer.sharedMaterials)
            {
                if(material==null||!String.IsNullOrEmpty(AssetDatabase.GetAssetPath(material)))continue;
                AssetDatabase.CreateAsset(material,AssetDatabase.GenerateUniqueAssetPath("Assets/Stroop/Generated/Materials/ViveDiagnostic.mat"));
            }
        }
        static ViveProEyePreview Validate(Scene scene)
        {
            var roots=scene.GetRootGameObjects();var previews=roots.SelectMany(root=>root.GetComponentsInChildren<ViveProEyePreview>(true)).ToArray();
            var cameras=roots.SelectMany(root=>root.GetComponentsInChildren<Camera>(true)).ToArray();
            Require(previews.Length==1&&cameras.Length==1,"expected one preview and camera");
            var preview=previews[0];
            Require(!preview.CaptureStarted,"authoring must not start capture");
            Require(preview.PreviewCamera==cameras[0]&&cameras[0].stereoTargetEye==StereoTargetEyeMask.Both,"linked stereo camera required");
            Require(preview.GazeMarker!=null&&!preview.GazeMarker.gameObject.activeSelf,"no gaze marker without live valid data");
            Require(preview.StatusText!=null&&preview.StatusText.text.Contains("STOPPED"),"stopped status required");
            Require(!roots.Any(root=>root.GetComponentInChildren<DevelopmentApplicationBootstrap>(true)!=null),"must not run study bootstrap");
            Require(!roots.Any(root=>root.GetComponentInChildren<QuestLinkPreviewRig>(true)!=null),"must not run Quest input/auto XR rig");
            Require(!Directory.GetFiles(Application.dataPath,"SRanipal.dll",SearchOption.AllDirectories).Any(),"proprietary runtime DLL must not be bundled");
            var settings=XRGeneralSettingsPerBuildTarget.XRGeneralSettingsForBuildTarget(BuildTargetGroup.Standalone);
            Require(settings!=null&&!settings.InitManagerOnStart&&settings.Manager!=null&&!settings.Manager.automaticLoading&&!settings.Manager.automaticRunning,"OpenXR auto start must be disabled");
            ValidateTextBounds(preview,false);
            ValidateDiagnosticProjection(preview);
            ValidateSdkRayConversion();
            return preview;
        }
        static void ValidateSdkRayConversion()
        {
            // Compare our derived conversion with the actual installed Unity Ray
            // convention, outside Play Mode and without constructing an eye client.
            // This is synthetic contract evidence, not native tracking validation.
            foreach(float scale in new[]{.125f,.9998477f,1f,2f})
            {
                Vector3 raw=new Vector3(.2f,-.1f,.95f)*scale;
                Vec3 derived;
                Require(ViveProEyeSdkDirection.TryGetUnitDirection(new Vec3(raw.x,raw.y,raw.z),out derived),
                    "finite nondegenerate SDK direction must produce a derived unit ray");
                var official=new Ray(Vector3.zero,raw);
                var actual=new Vector3((float)derived.X,(float)derived.Y,(float)derived.Z);
                Require(Vector3.Distance(official.direction,actual)<.000001f,
                    "derived SDK direction must match Unity Ray normalization");
                Require(derived.IsUnit,"derived ray must still satisfy unchanged core unit checks");
            }
            Vec3 rejected;
            Require(!ViveProEyeSdkDirection.TryGetUnitDirection(new Vec3(0,0,0),out rejected),"zero direction must remain invalid");
            Require(!ViveProEyeSdkDirection.TryGetUnitDirection(new Vec3(0,0,.000001f),out rejected),"sub-Ray-threshold direction must remain invalid");
            Require(!ViveProEyeSdkDirection.TryGetUnitDirection(new Vec3(Double.NaN,0,1),out rejected),"nonfinite direction must remain invalid");
            foreach(float oversized in new[]{1e30f,Single.MaxValue})
            {
                // A separate conservative malformed-input guard: Unity's JIT
                // may retain wide intermediates, so Ray itself need not reject it.
                Require(ViveProEyeSdkDirection.ConvertForRay(new Vec3(oversized,0,0),out rejected)==ViveSdkDirectionStatus.MagnitudeOverflow,
                    "malformed finite directions that overflow stored float squared magnitude must remain rejected");
            }
        }
        static void ValidateDiagnosticProjection(ViveProEyePreview preview)
        {
            var children=preview.GetComponentsInChildren<Transform>(true);
            var targets=children.Where(child=>child.name=="GazeTarget").ToArray();
            var centres=children.Where(child=>child.name=="TargetCentre").ToArray();
            Require(targets.Length==5&&centres.Length==5,"expected five static diagnostic targets and centres");
            var faces=children.Where(child=>child.name=="GazeTarget"||child.name=="TargetCentre"||child.name=="StatusPanel")
                .Select(child=>child.GetComponent<Renderer>()).ToArray();
            var marker=preview.GazeMarker;var renderer=marker.GetComponent<Renderer>();
            Vector3 originalPosition=marker.position;bool originalActive=marker.gameObject.activeSelf;
            Vector3 origin=preview.PreviewCamera.transform.position;
            try
            {
                marker.gameObject.SetActive(true);
                foreach(var target in targets)
                {
                    Vector3 projected;
                    Require(ViveProEyePreview.TryProjectDiagnosticRay(new Ray(origin,(target.position-origin).normalized),out projected),
                        "ray through each diagnostic target must project onto the board");
                    marker.position=projected;
                    var targetBounds=target.GetComponent<Renderer>().bounds;
                    Require(projected.x>targetBounds.min.x&&projected.x<targetBounds.max.x&&
                        projected.y>targetBounds.min.y&&projected.y<targetBounds.max.y,"projection must stay within its target");
                    Require(faces.All(face=>renderer.bounds.max.z<face.bounds.min.z),"projected marker must be fully in front of opaque board faces");
                    var viewport=preview.PreviewCamera.WorldToViewportPoint(projected);
                    Require(viewport.z>0&&viewport.x>.01f&&viewport.x<.99f&&viewport.y>.01f&&viewport.y<.99f,
                        "each projected target marker must be visible in the fixture camera");
                    marker.position=new Vector3(projected.x,projected.y,2.46f);
                    Require(renderer.bounds.min.z>targetBounds.min.z,"board-plane marker must expose the original target occlusion");
                }
                Vector3 rejected;
                foreach(var point in new[]{new Vector3(-1.3f,1.6f,2.46f),new Vector3(1.3f,1.6f,2.46f),
                    new Vector3(0,.3f,2.46f),new Vector3(0,2.6f,2.46f)})
                    Require(!ViveProEyePreview.TryProjectDiagnosticRay(new Ray(origin,(point-origin).normalized),out rejected),
                        "rays outside the diagnostic board must remain hidden");
                Require(!ViveProEyePreview.TryProjectDiagnosticRay(new Ray(origin,Vector3.back),out rejected),"backward ray must remain hidden");
                Require(!ViveProEyePreview.TryProjectDiagnosticRay(new Ray(origin,Vector3.right),out rejected),"parallel ray must remain hidden");
            }
            finally {marker.position=originalPosition;marker.gameObject.SetActive(originalActive);}
        }
        static void TextWithin(ViveProEyePreview preview,string name,Rect allowed,bool fullView)
        {
            var text=preview.transform.Find(name).GetComponent<TextMesh>();
            var bounds=text.GetComponent<Renderer>().bounds;
            Require(bounds.size.x>0&&bounds.size.y>0,"empty text geometry: "+name);
            Require(bounds.min.x>=allowed.xMin-.002f&&bounds.max.x<=allowed.xMax+.002f&&
                bounds.min.y>=allowed.yMin-.002f&&bounds.max.y<=allowed.yMax+.002f,"text exceeds its fixed panel: "+name+" "+bounds);
            if(fullView)
            foreach(var corner in new[]{new Vector3(bounds.min.x,bounds.min.y,bounds.center.z),new Vector3(bounds.min.x,bounds.max.y,bounds.center.z),
                new Vector3(bounds.max.x,bounds.min.y,bounds.center.z),new Vector3(bounds.max.x,bounds.max.y,bounds.center.z)})
            {
                var viewport=preview.PreviewCamera.WorldToViewportPoint(corner);
                Require(viewport.z>0&&viewport.x>.01f&&viewport.x<.99f&&viewport.y>.01f&&viewport.y<.99f,"text cropped by fixture camera: "+name);
            }
        }
        static void ValidateTextBounds(ViveProEyePreview preview,bool fullView)
        {
            TextWithin(preview,"Title",new Rect(-1.2f,2.31f,2.4f,.17f),fullView);
            TextWithin(preview,"Subtitle",new Rect(-1.2f,2.17f,2.4f,.12f),fullView);
            TextWithin(preview,"Scope",new Rect(-1.2f,2.04f,2.4f,.1f),fullView);
            TextWithin(preview,"MarkerMeaning",new Rect(-1.2f,1.25f,2.4f,.12f),fullView);
            TextWithin(preview,"Status",new Rect(-1.12f,.58f,2.24f,.57f),fullView);
            TextWithin(preview,"Limits",new Rect(-1.2f,.37f,2.4f,.12f),fullView);
            TextWithin(preview,"AvatarCaption",new Rect(1.3f,1.93f,.9f,.24f),fullView);
        }
        public static void BuildWindowsPreview()
        {
            checks=0;string project=Project(),output=Output(project);
            var scene=EditorSceneManager.OpenScene(ScenePath,OpenSceneMode.Single);Validate(scene);
            PlayerSettings.SetScriptingBackend(NamedBuildTarget.Standalone,ScriptingImplementation.Mono2x);
            PlayerSettings.productName="Avatar Stroop - VIVE PRO EYE HARDWARE REHEARSAL";
            PlayerSettings.defaultScreenWidth=1280;PlayerSettings.defaultScreenHeight=900;PlayerSettings.fullScreenMode=FullScreenMode.Windowed;
            PlayerSettings.runInBackground=true;
            string executable=Path.Combine(output,"UnityPlayer","AvatarStroopViveEyePlayer.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(executable));
            var result=BuildPipeline.BuildPlayer(new BuildPlayerOptions {scenes=new[]{ScenePath},target=BuildTarget.StandaloneWindows64,locationPathName=executable,options=BuildOptions.None});
            Require(result.summary.result==BuildResult.Succeeded&&File.Exists(executable),"Windows player build failed");
            File.WriteAllText(Path.Combine(output,"vive-eye-scene-validation.json"),JsonUtility.ToJson(new Evidence {
                editor=Application.unityVersion,executable=Path.GetFileName(executable),scene_checks=checks},true)+"\n");
            Debug.Log("VIVE_PREVIEW_BUILD_COMPLETE: build only; no player, XR or eye capture launched.");
        }
        public static void RenderFixture()
        {
            checks=0;string project=Project(),output=Output(project);
            Require(SystemInfo.graphicsDeviceType!=GraphicsDeviceType.Null,"fixture rendering requires graphics; omit -nographics");
            var scene=EditorSceneManager.OpenScene(ScenePath,OpenSceneMode.Single);var preview=Validate(scene);
            // Render outside Play Mode: no native provider or HMD is constructed.
            var camera=preview.PreviewCamera;camera.transform.rotation=Quaternion.Euler(2,8,0);
            var head=preview.transform.Find("UnvalidatedObserverRehearsal/CurrentTrackedHead");
            var left=head.Find("LeftEye");var right=head.Find("RightEye");
            var fixtureTarget=preview.GetComponentsInChildren<Transform>(true).Last(child=>child.name=="TargetCentre");
            Vector3 fixtureMarkerPosition;
            Require(ViveProEyePreview.TryProjectDiagnosticRay(new Ray(camera.transform.position,
                (fixtureTarget.position-camera.transform.position).normalized),out fixtureMarkerPosition),"live fixture must use runtime gaze projection");
            string[] states={"stopped","live-valid","invalid","duplicate","no-eye-data","invalid-direction","retained"};
            string[] messages={
                "SYNTHETIC EDITOR FIXTURE - NO LIVE CAPTURE\nSTOPPED - Begin is required on the desktop.\nNo study session or data file is created.",
                "SYNTHETIC EDITOR FIXTURE - NO LIVE CAPTURE\nHead: usable | Eye: usable\nRuntime: Working | Calibration: ReportedReady | Code: 0\nFrame: Accepted | Usable eye observations: 99\nMarker: new valid ray (cyan)\nUnvalidated tracking-to-avatar mapping; no accuracy claim.",
                "SYNTHETIC EDITOR FIXTURE - NO LIVE CAPTURE\nHead: usable | Eye: unavailable\nRuntime: Working | Calibration: Required | Code: 0\nFrame: Accepted | Usable eye observations: 0\nCalibration required by HTC. Stop capture and use\nHTC's calibration interface.",
                "SYNTHETIC EDITOR FIXTURE - NO LIVE CAPTURE\nHead: usable | Eye: unavailable\nRuntime: Working | Calibration: ReportedReady | Code: 0\nFrame: Duplicate | Usable eye observations: 0\nNo new eye frame. Check native-read and counter-change\ncounts on the desktop; repeated data is not usable.",
                "SYNTHETIC EDITOR FIXTURE - NO LIVE CAPTURE\nHead: usable | Eye: unavailable\nRuntime: Working | Calibration: ReportedReady | Code: 0\nFrame: Accepted | Usable eye observations: 0\nHTC reports no available eye data.",
                "SYNTHETIC EDITOR FIXTURE - NO LIVE CAPTURE\nHead: usable | Eye: unavailable\nRuntime: Working | Calibration: ReportedReady | Code: 0\nFrame: Accepted | Usable eye observations: 0\nCombined gaze direction is not reported valid.\nCalibration-ready does not establish valid gaze.",
                "SYNTHETIC EDITOR FIXTURE - NO LIVE CAPTURE\nHead: usable | Eye: unavailable\nRuntime: Working | Calibration: ReportedReady | Code: 0\nFrame: Duplicate | Usable eye observations: 99\nMarker: previous ray (amber), 20 ms old\nPrevious WORLD point only; no new eye observation."};
            for(int index=0;index<states.Length;index++)
            {
                preview.StatusText.text=messages[index];
                ViveProEyePreview.FitText(preview.StatusText,2.2f,.54f,.012f);
                head.gameObject.SetActive(index>0);left.gameObject.SetActive(index==1);right.gameObject.SetActive(index==1);
                head.localRotation=Quaternion.Euler(0,-10,0);
                bool visible=index==1||states[index]=="retained";
                var markerState=index==1?VivePreviewMarkerState.Fresh:visible?VivePreviewMarkerState.Retained:VivePreviewMarkerState.Hidden;
                preview.ApplyMarkerPresentation(markerState,new Vec3(fixtureMarkerPosition.x,fixtureMarkerPosition.y,fixtureMarkerPosition.z));
                if(visible)
                {
                    var properties=new MaterialPropertyBlock();preview.GazeMarker.GetComponent<Renderer>().GetPropertyBlock(properties);
                    var expected=index==1?Color.cyan:new Color(1f,.55f,.05f);
                    Require(properties.GetColor("_Color")==expected,"fresh and retained samples require distinct colours");
                }
                Render(camera,Path.Combine(output,"synthetic-vive-eye-diagnostic-"+states[index]+".png"));
                ValidateTextBounds(preview,true);
                Require(preview.GazeMarker.gameObject.activeSelf==visible,"marker must be hidden for stopped/invalid/expired duplicate fixtures");
            }
            File.WriteAllText(Path.Combine(output,"vive-eye-render-validation.json"),"{\"synthetic_editor_only\":true,\"live_capture\":false,\"checks\":"+checks+",\"views\":[\""+String.Join("\",\"",states)+"\"]}\n");
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
            Debug.Log("VIVE_PREVIEW_FIXTURE_COMPLETE: synthetic Editor renders; no wearer or live data.");
        }
        static void Render(Camera camera,string path)
        {
            var target=new RenderTexture(1600,1000,24);var previous=RenderTexture.active;
            var image=new Texture2D(1600,1000,TextureFormat.RGB24,false);
            try
            {
                camera.targetTexture=target;camera.Render();RenderTexture.active=target;
                image.ReadPixels(new Rect(0,0,1600,1000),0,0);image.Apply();
                File.WriteAllBytes(path,image.EncodeToPNG());
            }
            finally {camera.targetTexture=null;RenderTexture.active=previous;UnityEngine.Object.DestroyImmediate(image);UnityEngine.Object.DestroyImmediate(target);}
        }
    }
}
#endif
