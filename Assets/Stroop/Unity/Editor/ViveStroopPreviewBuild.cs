#if STROOP_XR_PREVIEW && UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using AvatarStroop.Core;
using AvatarStroop.Runtime;
using Unity.XR.CoreUtils;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEditor.XR.Management;
using UnityEngine;
using UnityEngine.InputSystem.XR;
using UnityEngine.SceneManagement;
using UnityEngine.XR;
using UnityEngine.XR.Management;
using UnityEngine.XR.OpenXR;
using UnityEngine.XR.OpenXR.Features.Interactions;

namespace AvatarStroop.Unity.Editor
{
    // Isolated development preview. The separate bootstrap is dormant until an
    // owner supplies a complete mapping; optional local recording requires two
    // choices and a directory; no network, research admission or automatic XR.
    public static class ViveStroopPreviewBuild
    {
        const string ScenePath="Assets/Stroop/Generated/ViveClassroomPreview.unity";
        static void Require(bool value,string message){if(!value)throw new InvalidOperationException("VIVE classroom: "+message);}
        static string Project()
        {
            string project=Path.GetFullPath(Path.Combine(Application.dataPath,".."));
            Require(Application.isBatchMode&&!Application.isPlaying&&Application.unityVersion=="6000.3.25f1"&&
                File.Exists(Path.Combine(project,"quest-preview-preparation.json"))&&
                File.Exists(Path.Combine(project,"vive-classroom-isolated-intent.json")),"requires a specifically marked isolated Unity 6000.3.25f1 classroom project");
            return project;
        }
        static string Output(string project)
        {
            string value=Environment.GetEnvironmentVariable("STROOP_VIVE_CLASSROOM_OUTPUT");
            Require(!String.IsNullOrWhiteSpace(value)&&Path.IsPathRooted(value),"absolute output is required");
            string output=Path.GetFullPath(value),workspace=Path.GetDirectoryName(project)+Path.DirectorySeparatorChar;
            Require(output.StartsWith(workspace,StringComparison.OrdinalIgnoreCase)&&!output.Equals(project,StringComparison.OrdinalIgnoreCase)&&
                !output.StartsWith(project+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase),"output must be separate from the project");
            Directory.CreateDirectory(output);return output;
        }
        public static void Configure()
        {
            ReadRecordingBuildDefines(Project());
            // Configure only this isolated project's official cached packages and
            // generated scene; the installed system runtime is never changed.
            QuestPreviewBuild.Configure();
            var openxr=OpenXRSettings.GetSettingsForBuildTargetGroup(BuildTargetGroup.Standalone);
            Require(openxr!=null,"missing isolated OpenXR settings");
            var touch=openxr.GetFeature<OculusTouchControllerProfile>();
            var vive=openxr.GetFeature<HTCViveControllerProfile>();
            Require(vive!=null,"installed HTC Vive Controller profile missing");
            if(touch!=null){touch.enabled=false;EditorUtility.SetDirty(touch);}
            vive.enabled=true;EditorUtility.SetDirty(vive);
            var presence=openxr.GetFeature<ViveVerifiedPresenceFeature>();
            Require(presence!=null,"optional presence capability feature missing");
            presence.enabled=true;EditorUtility.SetDirty(presence);EditorUtility.SetDirty(openxr);
            var scene=EditorSceneManager.OpenScene("Assets/Stroop/Generated/ParticipantQuestPreview.unity",OpenSceneMode.Single);
            var views=scene.GetRootGameObjects().SelectMany(x=>x.GetComponentsInChildren<WorldSpaceExperimentView>(true)).ToArray();
            Require(views.Length==1&&views[0].Role==Actor.Participant,"expected one participant view");
            var view=views[0];var quest=view.GetComponent<QuestLinkPreviewRig>();
            Require(quest!=null,"missing authored source rig");
            var serialized=new SerializedObject(quest);
            var origin=serialized.FindProperty("origin").objectReferenceValue as XROrigin;
            var driver=serialized.FindProperty("poseDriver").objectReferenceValue as TrackedPoseDriver;
            Require(origin!=null&&origin.CameraFloorOffsetObject!=null&&driver!=null&&origin.Camera==view.PresentationCamera,"invalid source camera hierarchy");
            var originTransform=origin.Origin.transform;var offset=origin.CameraFloorOffsetObject.transform;
            UnityEngine.Object.DestroyImmediate(driver);
            UnityEngine.Object.DestroyImmediate(origin);
            UnityEngine.Object.DestroyImmediate(quest);
            var bootstrap=view.GetComponent<DevelopmentApplicationBootstrap>();
            Require(bootstrap!=null,"missing source bootstrap");UnityEngine.Object.DestroyImmediate(bootstrap);
            var rig=view.gameObject.AddComponent<ViveStroopPreviewRig>();
            rig.Configure(view.PresentationCamera,originTransform,offset,view);
            // A collider-free, explicitly unlit marker exists only in the VIVE
            // preview. It remains hidden until a fresh valid observation.
            var marker=GameObject.CreatePrimitive(PrimitiveType.Sphere);
            marker.name="ViveFreshGazeDirection";marker.transform.SetParent(view.transform,false);
            marker.transform.localScale=Vector3.one*.018f;
            UnityEngine.Object.DestroyImmediate(marker.GetComponent<Collider>());
            var gazeShader=Shader.Find("Unlit/Color");Require(gazeShader!=null,"built-in unlit marker shader missing");
            var gazeMaterial=new Material(gazeShader){color=Color.cyan};
            Directory.CreateDirectory(Path.Combine(Application.dataPath,"Stroop","Generated","Materials"));AssetDatabase.Refresh();
            AssetDatabase.CreateAsset(gazeMaterial,AssetDatabase.GenerateUniqueAssetPath("Assets/Stroop/Generated/Materials/ViveFreshGaze.mat"));
            marker.GetComponent<Renderer>().sharedMaterial=gazeMaterial;rig.ConfigureGazeMarker(marker.transform);
            var development=view.gameObject.AddComponent<ViveStroopDevelopmentBootstrap>();
            development.Configure(rig,view);rig.ConfigureDevelopment(development);
            EditorUtility.SetDirty(view);EditorUtility.SetDirty(rig);EditorUtility.SetDirty(development);
            Require(EditorSceneManager.SaveScene(scene,ScenePath),"could not save isolated scene");
            EditorBuildSettings.scenes=new[]{new EditorBuildSettingsScene(ScenePath,true)};
            AssetDatabase.SaveAssets();
            File.WriteAllText(Path.Combine(Project(),"vive-classroom-configured.json"),"{\"explicit_xr_start\":true,\"explicit_eye_start\":true,\"explicit_task_configuration\":true,\"study\":false,\"input_configured\":false,\"hardware_validated\":false}\n");
            Debug.Log("VIVE_CLASSROOM_CONFIGURED: development task waits for explicit setup; no runtime, eye capture or task started.");
        }
        static void Validate(Scene scene)
        {
            ValidateInputGate();
            ValidateGazeDirection();
            ValidateStartAlignment();
            Require(ViveVerifiedPresenceFeature.HasExpectedWindowsX64Layout(),"presence interop layout mismatch");
            Require(!scene.GetRootGameObjects().SelectMany(x=>x.GetComponentsInChildren<Transform>(true)).Any(x=>x.gameObject.layer==ViveStartupView.Layer),"startup instruction layer must be reserved");
            var roots=scene.GetRootGameObjects();
            var views=roots.SelectMany(x=>x.GetComponentsInChildren<WorldSpaceExperimentView>(true)).ToArray();
            var rigs=roots.SelectMany(x=>x.GetComponentsInChildren<ViveStroopPreviewRig>(true)).ToArray();
            Require(views.Length==1&&views[0].Role==Actor.Participant&&rigs.Length==1&&rigs[0].gameObject==views[0].gameObject,"view/rig mismatch");
            var development=roots.SelectMany(x=>x.GetComponentsInChildren<ViveStroopDevelopmentBootstrap>(true)).ToArray();
            Require(development.Length==1&&development[0].gameObject==views[0].gameObject&&!development[0].Configured&&
                !development[0].RecordingEnabled&&!development[0].RecordingBlocked,"unconfigured local development bootstrap with recording off required");
            for(int mask=0;mask<16;mask++)
            {
                bool eye=(mask&1)!=0,taskChoice=(mask&2)!=0,worker=(mask&4)!=0,terminal=(mask&8)!=0;
                Require(!ViveStroopDevelopmentBootstrap.CanEnableRecording(eye,taskChoice,worker,terminal),
                    "local VIVE recording must remain disabled for every owner choice and worker state");
            }
            Require(!ViveStroopDevelopmentBootstrap.CanAcknowledgeInputFault(true,true,Phase.Paused)&&
                !ViveStroopDevelopmentBootstrap.CanAcknowledgeInputFault(true,false,Phase.MainTask)&&
                ViveStroopDevelopmentBootstrap.CanAcknowledgeInputFault(true,false,Phase.Paused),
                "fault acknowledgement cannot bypass recording block or an unconfirmed task stop");
            foreach(var dimensions in new[]{new Vector2(640,480),new Vector2(1024,768),new Vector2(1920,1080)})
            {
                var bounds=ViveStroopPreviewRig.OwnerPanelBounds(dimensions.x,dimensions.y);
                Require(bounds.x>=0&&bounds.y>=0&&bounds.xMax<=dimensions.x&&bounds.yMax<=dimensions.y&&bounds.width>0&&bounds.height>0,
                    "scrollable owner panel must remain inside common window sizes");
            }
            // Editor policy is deliberately unavailable even when the player
            // request opts in. Validate the actual Editor getter, not player flags.
            string scope=ViveStroopPreviewRig.ScopeNotice;
            Require(scope.Contains("Simulated development task")&&scope.Contains("Local VIVE recording is unavailable")&&
                scope.Contains("No two-PC network, formal study")&&scope.Contains("Legacy A/B/C/D has no recording or export"),
                "desktop scope notice must describe recording and study limitations accurately");
            Require(new SerializedObject(rigs[0]).FindProperty("development").objectReferenceValue==development[0],"VIVE rig and development bootstrap must be linked");
            Require(roots.SelectMany(x=>x.GetComponentsInChildren<DevelopmentApplicationBootstrap>(true)).Count()==0,"task bootstrap is not permitted");
            Require(roots.SelectMany(x=>x.GetComponentsInChildren<QuestLinkPreviewRig>(true)).Count()==0,"Quest rig is not permitted");
            Require(roots.SelectMany(x=>x.GetComponentsInChildren<TrackedPoseDriver>(true)).Count()==0,"conflicting camera driver is not permitted");
            Require(views[0].PresentationCamera!=null&&views[0].PresentationCamera.stereoTargetEye==StereoTargetEyeMask.Both,"stereo camera required");
            Require(rigs[0].GazeMarker!=null&&!rigs[0].GazeMarker.gameObject.activeSelf&&
                rigs[0].GazeMarker.GetComponent<Collider>()==null&&rigs[0].GazeMarker.GetComponent<Renderer>()!=null,
                "fresh gaze direction marker must be initially hidden and collider-free");
            Require(views[0].AvatarRoot!=null&&views[0].AvatarRoot.gameObject.activeSelf,"static classroom avatar must be visible before tasks are configured");
            var viewState=new SerializedObject(views[0]);
            Require(viewState.FindProperty("viveUnconfiguredPreview").boolValue,"explicit VIVE unconfigured profile required");
            var legend=views[0].transform.Find("WorldSpaceContent/QuestControllerLegend");
            Require(legend==null||!legend.gameObject.activeSelf,"Quest response legend must be hidden");
            bool rejected=false;
            try{views[0].ApplyTaskView(new TaskView());}catch(InvalidOperationException){rejected=true;}
            Require(rejected,"unconfigured VIVE preview must refuse task presentation");
            Require(!Directory.GetFiles(Application.dataPath,"SRanipal.dll",SearchOption.AllDirectories).Any(),"vendor DLL must not be bundled");
            ClassroomPresentationValidation.ValidateView(views[0]);
        }
        static void ValidateStartAlignment()
        {
            // Editor-only synthetic transforms; no XR loader or headset.
            foreach(var mode in new[]{TrackingOriginModeFlags.Device,TrackingOriginModeFlags.Floor})
            {
                var root=new GameObject("ViveAlignmentValidation");var offset=new GameObject("Offset");
                try
                {
                    offset.transform.SetParent(root.transform,false);
                    var alignment=new VivePreviewStartAlignment();
                    var local=new Vector3(.25f,mode==TrackingOriginModeFlags.Floor?.8f:.02f,-.1f);
                    var rotation=Quaternion.Euler(12,28,0);
                    var target=new Vector3(WorldSpaceExperimentView.BoardPosition.x,WorldSpaceExperimentView.TaskDisplayPosition.y,WorldSpaceExperimentView.CenteredParticipantReference.z);
                    Require(!alignment.TryApply(false,mode,0,0,root.transform,offset.transform,local,rotation,target),"unconfirmed VIVE pose must stay masked");
                    Require(!alignment.TryApply(true,mode,1,1,root.transform,offset.transform,local,rotation,target),"first pose must not align");
                    for(int frame=2;frame<=8;frame++)
                        Require(!alignment.TryApply(true,mode,frame,1+(frame-1)*.1,root.transform,offset.transform,local,rotation,target),"unstable duration must not align");
                    Require(alignment.TryApply(true,mode,9,1.81,root.transform,offset.transform,local,rotation,target),"stable pose must align once");
                    Vector3 head=offset.transform.TransformPoint(local);
                    Require(Vector3.Distance(head,target)<.0001f,"first VIVE head position must align with the board target");
                    float pitchBefore=(rotation*Vector3.forward).y;
                    float pitchAfter=(offset.transform.rotation*rotation*Vector3.forward).y;
                    Require(Mathf.Abs(pitchBefore-pitchAfter)<.0001f,"VIVE head pitch must be preserved");
                    Vector3 moved=offset.transform.TransformPoint(local+new Vector3(0,.15f,0));
                    Require(Mathf.Abs((moved.y-head.y)-.15f)<.0001f,"later physical height movement must be preserved");
                    Require(!alignment.TryApply(true,mode,4,2,root.transform,offset.transform,local+Vector3.up,rotation,target),"VIVE view must not continuously realign");
                }
                finally{UnityEngine.Object.DestroyImmediate(root);}
            }
            var guardedRoot=new GameObject("ViveAlignmentGuard");var guardedOffset=new GameObject("Offset");
            try
            {
                guardedOffset.transform.SetParent(guardedRoot.transform,false);
                // This input must pass the pose range gate so the separate
                // virtual-height correction guard is actually exercised.
                var guarded=new VivePreviewStartAlignment();var implausible=new Vector3(0,3.9f,0);
                var target=new Vector3(0,WorldSpaceExperimentView.TaskDisplayPosition.y,0);
                guarded.TryApply(true,TrackingOriginModeFlags.Floor,1,1,guardedRoot.transform,guardedOffset.transform,implausible,Quaternion.identity,target);
                for(int frame=2;frame<=8;frame++)guarded.TryApply(true,TrackingOriginModeFlags.Floor,frame,1+(frame-1)*.1,guardedRoot.transform,guardedOffset.transform,implausible,Quaternion.identity,target);
                Require(!guarded.TryApply(true,TrackingOriginModeFlags.Floor,9,1.81,guardedRoot.transform,guardedOffset.transform,implausible,Quaternion.identity,target)&&
                    guarded.HeightRejected&&!guarded.Applied,"excessive VIVE virtual height shift must stay masked");
            }
            finally{UnityEngine.Object.DestroyImmediate(guardedRoot);}
            var invalidRoot=new GameObject("ViveAlignmentInvalidPose");var invalidOffset=new GameObject("Offset");
            try
            {
                invalidOffset.transform.SetParent(invalidRoot.transform,false);
                var invalid=new VivePreviewStartAlignment();var outsideRange=new Vector3(0,10,0);
                var target=new Vector3(0,WorldSpaceExperimentView.TaskDisplayPosition.y,0);
                invalid.TryApply(true,TrackingOriginModeFlags.Floor,1,1,invalidRoot.transform,invalidOffset.transform,outsideRange,Quaternion.identity,target);
                invalid.TryApply(true,TrackingOriginModeFlags.Floor,2,1.1,invalidRoot.transform,invalidOffset.transform,outsideRange,Quaternion.identity,target);
                Require(!invalid.TryApply(true,TrackingOriginModeFlags.Floor,3,1.81,invalidRoot.transform,invalidOffset.transform,outsideRange,Quaternion.identity,target)&&
                    !invalid.Applied,"out-of-range VIVE tracking pose must stay masked");
            }
            finally{UnityEngine.Object.DestroyImmediate(invalidRoot);}
        }
        static void ValidateGazeDirection()
        {
            Vector3 point;
            Require(ViveStroopPreviewRig.TryProjectGazeDirection(new Vector3(.2f,1.6f,.1f),Quaternion.identity,
                new Vec3(0,0,1),Vector3.forward,out point)&&Vector3.Distance(point,new Vector3(.2f,1.6f,1.45f))<.0001f,
                "gaze direction must use HMD centre and the fixed preview distance");
            Require(ViveStroopPreviewRig.TryProjectGazeDirection(Vector3.zero,Quaternion.Euler(0,90,0),
                new Vec3(0,0,1),Vector3.right,out point)&&Vector3.Distance(point,new Vector3(1.35f,0,0))<.0001f,
                "gaze direction must apply room rotation once");
            Require(!ViveStroopPreviewRig.TryProjectGazeDirection(Vector3.zero,Quaternion.identity,
                new Vec3(0,0,-1),Vector3.forward,out point),"backward gaze marker must be hidden");
            Require(!ViveStroopPreviewRig.TryProjectGazeDirection(Vector3.zero,Quaternion.identity,
                new Vec3(Double.NaN,0,1),Vector3.forward,out point),"invalid gaze direction must be hidden");
        }
        static void ValidateInputGate()
        {
            // Managed-only fixture: never opens an XR loader or input device.
            var gate=new ViveStroopNeutralGate();
            var held=gate.Observe(true,true,false,true,true,false);
            Require(held.PhysicalReady&&held.NeutralPending&&!held.LostReady&&!held.TriggerDown,"held startup must wait without declaring loss");
            var neutral=gate.Observe(true,false,false,false,false,false);
            Require(neutral.PhysicalReady&&!neutral.NeutralPending,"neutral input should arm");
            var edge=gate.Observe(true,false,false,false,true,false);
            Require(edge.TriggerDown&&!edge.MenuDown,"trigger should produce exactly one edge");
            var phase=gate.Observe(true,true,false,false,true,false);
            Require(phase.PhysicalReady&&phase.NeutralPending&&!phase.LostReady&&!phase.TriggerDown,"held phase transition is not disconnection");
            var lost=gate.Observe(false,false,false,false,false,false);
            Require(!lost.PhysicalReady&&lost.LostReady,"physical loss must be reported");
            var repeated=gate.Observe(false,false,false,false,false,false);
            Require(!repeated.LostReady,"physical loss must not repeat");
            var reconnect=gate.Observe(true,false,true,true,false,false);
            Require(reconnect.PhysicalReady&&reconnect.NeutralPending&&!reconnect.TriggerDown,"held reconnect must await neutral");
        }
        [Serializable] sealed class ControlledBuildIntent
        { public bool controlled_recording_development=false; public bool legacy_raw_recording_development=false; }
        static string[] ReadRecordingBuildDefines(string project)
        {
            var intent=JsonUtility.FromJson<ControlledBuildIntent>(File.ReadAllText(Path.Combine(project,"vive-classroom-isolated-intent.json")));
            Require(intent!=null,"isolated build intent is required");
            return ViveRecordingBuildRequest.Defines(
                Environment.GetEnvironmentVariable("STROOP_VIVE_CONTROLLED_RECORDING_DEVELOPMENT"),
                Environment.GetEnvironmentVariable("STROOP_VIVE_LEGACY_RAW_RECORDING_DEVELOPMENT"),
                intent.controlled_recording_development,intent.legacy_raw_recording_development,
                PlayerSettings.GetScriptingDefineSymbols(UnityEditor.Build.NamedBuildTarget.Standalone));
        }
        public static void BuildWindowsPreview()
        {
            string project=Project();
            var recordingDefines=ReadRecordingBuildDefines(project);
            bool controlled=recordingDefines.Contains("STROOP_CONTROLLED_RECORDING_DEVELOPMENT");
            bool legacy=recordingDefines.Contains("STROOP_LEGACY_RAW_RECORDING_DEVELOPMENT");
            string output=Output(project);
            Require(File.Exists(Path.Combine(project,"vive-classroom-configured.json")),"configure in a separate batch invocation first");
            var general=XRGeneralSettingsPerBuildTarget.XRGeneralSettingsForBuildTarget(BuildTargetGroup.Standalone);
            var settings=OpenXRSettings.GetSettingsForBuildTargetGroup(BuildTargetGroup.Standalone);
            Require(general!=null&&!general.InitManagerOnStart&&general.Manager!=null&&!general.Manager.automaticLoading&&!general.Manager.automaticRunning&&
                general.Manager.activeLoaders.Count==1&&general.Manager.activeLoaders[0] is OpenXRLoader,"XR must require explicit start");
            Require(settings!=null&&settings.GetFeature<HTCViveControllerProfile>()!=null&&settings.GetFeature<HTCViveControllerProfile>().enabled&&
                (settings.GetFeature<OculusTouchControllerProfile>()==null||!settings.GetFeature<OculusTouchControllerProfile>().enabled),"wrong isolated controller profile");
            var scene=EditorSceneManager.OpenScene(ScenePath,OpenSceneMode.Single);Validate(scene);
            PlayerSettings.productName=legacy?"Avatar Stroop - LEGACY + NAMED LOCAL RECORDING TEST":controlled?"Avatar Stroop - CONTROLLED LOCAL RECORDING TEST":"Avatar Stroop - VIVE CLASSROOM VIEW ONLY";
            string executable=Path.Combine(output,"UnityPlayer/AvatarStroopViveEyePlayer.exe");Directory.CreateDirectory(Path.GetDirectoryName(executable));
            var result=BuildPipeline.BuildPlayer(new BuildPlayerOptions{scenes=new[]{ScenePath},locationPathName=executable,
                target=BuildTarget.StandaloneWindows64,options=controlled?BuildOptions.Development:BuildOptions.None,
                extraScriptingDefines=recordingDefines});
            Require(result.summary.result==BuildResult.Succeeded&&File.Exists(executable),"player build failed");
            File.WriteAllText(Path.Combine(output,"vive-classroom-scene-validation.json"),"{\"development_bootstrap\":true,\"automatic_task_start\":false,\"recording_default\":false,\"optional_local_recording\":"+(controlled?"true":"false")+",\"legacy_raw_recording_development\":"+(legacy?"true":"false")+",\"named_explicit_recording_requested\":"+(controlled?"true":"false")+",\"preview_configuration_records\":false,\"separate_recording_choices_required\":true,\"network\":false,\"input_configured\":false,\"hardware_validated\":false}\n");
            Debug.Log("VIVE_CLASSROOM_BUILD_COMPLETE: "+(legacy?"legacy and named explicit opt-in local recording self-tests":controlled?"named explicit opt-in local recording self-test; legacy preview is memory-only":"unconfigured memory-only development preview")+"; no automatic capture; not headset validated.");
        }
        public static void RenderFixture()
        {
            string project=Project();ReadRecordingBuildDefines(project);string output=Output(project);
            var scene=EditorSceneManager.OpenScene(ScenePath,OpenSceneMode.Single);Validate(scene);
            var view=scene.GetRootGameObjects().SelectMany(x=>x.GetComponentsInChildren<WorldSpaceExperimentView>(true)).Single();
            view.SetViveUnconfiguredPreview("Synthetic Editor render. No XR or eye capture started.");
            var camera=view.PresentationCamera;
            camera.transform.SetPositionAndRotation(WorldSpaceExperimentView.CenteredParticipantReference,Quaternion.identity);
            camera.fieldOfView=WorldSpaceExperimentView.DesktopVerticalFieldOfView;
            Render(camera,Path.Combine(output,"synthetic-vive-classroom.png"));
            // Render the same fixed VIVE mapping used by the runtime; the saved
            // scene remains unconfigured and no participant session is started.
            var mapping=ViveFixedTrackpadMapping.Create();
            view.ConfigureViveDevelopmentPreview(mapping,.5);
            var task=new TaskView();
            typeof(TaskView).GetProperty("Phase").SetValue(task,Phase.Practice,null);
            typeof(TaskView).GetProperty("TrialId").SetValue(task,"synthetic-editor-vive-practice",null);
            typeof(TaskView).GetProperty("Stimulus").SetValue(task,new Stimulus(Colour.Red,Colour.Green),null);
            view.ApplyTaskView(task);
            // Edit-mode fixtures do not receive MonoBehaviour.LateUpdate.
            // Explicitly run the same layout pass before inspecting/rasterizing.
            typeof(WorldSpaceExperimentView).GetMethod("LayoutHud",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic).Invoke(view,null);
            var legend=view.transform.Find("WorldSpaceContent/ViveControllerLegend");
            Require(legend!=null&&legend.gameObject.activeInHierarchy,"configured practice must show VIVE mapping");
            var oldLegend=view.transform.Find("WorldSpaceContent/QuestControllerLegend");
            Require(oldLegend==null||!oldLegend.gameObject.activeInHierarchy,"configured VIVE practice must not show Quest mapping");
            Require(legend.Find("ButtonU")!=null&&legend.Find("ButtonR")!=null&&legend.Find("ButtonD")!=null&&legend.Find("ButtonL")!=null,
                "four VIVE direction sectors required");
            var directions=new[]{"U","R","D","L"};var expectedColours=new[]{Color.red,Color.green,Color.blue,Color.yellow};
            for(int i=0;i<directions.Length;i++)
            {
                var button=legend.Find("Button"+directions[i]);
                var label=legend.Find("Button"+directions[i]+"Label");
                Require(button.GetComponent<Renderer>().sharedMaterial.color==expectedColours[i]&&
                    label!=null&&label.GetComponent<TextMesh>().text==directions[i],"fixed VIVE mapping or label changed");
            }
            Render(camera,Path.Combine(output,"synthetic-vive-practice.png"),true,false);
            var root=view.AvatarRoot;var head=view.AvatarHead.position;
            var towardParticipant=root.InverseTransformDirection(view.ParticipantReferencePosition-head);
            var towardTask=root.InverseTransformDirection(view.TaskWorldPosition-head);
            var geometry=DevelopmentGeometry.FromLocalDirections(ToVec(towardParticipant),ToVec(towardTask),WorldSpaceExperimentView.GeometryVersion);
            foreach(var condition in new[]{Condition.A,Condition.B,Condition.C,Condition.D})
            {
                var main=new TaskView();
                typeof(TaskView).GetProperty("Phase").SetValue(main,Phase.MainTask,null);
                typeof(TaskView).GetProperty("TrialId").SetValue(main,"synthetic-editor-vive-main-"+condition,null);
                typeof(TaskView).GetProperty("Stimulus").SetValue(main,new Stimulus(Colour.Red,Colour.Green),null);
                typeof(TaskView).GetProperty("OperatorAvatarVisible").SetValue(main,condition!=Condition.D,null);
                view.ApplyTaskView(main);
                var controller=new AvatarController("synthetic-editor-vive",RunKind.Simulation,0,0,ControlSelection.Automatic,condition,GazeTarget.Participant,geometry);
                view.ApplyAvatarOutput(controller.Render(Phase.MainTask,0));
                typeof(WorldSpaceExperimentView).GetMethod("LayoutHud",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic).Invoke(view,null);
                Require(view.AvatarRoot.gameObject.activeInHierarchy==(condition!=Condition.D),"main-task avatar visibility must follow A/B/C versus D");
                Render(camera,Path.Combine(output,"synthetic-vive-main-"+condition.ToString().ToLowerInvariant()+".png"),true,condition!=Condition.D);
            }
            // Match the projection used by the 1280x720 render textures while
            // checking text bounds; restore the camera setting afterwards.
            float priorAspect=camera.aspect;
            try{camera.aspect=1280f/720f;RenderQuestionnaireFixtures(view,camera,output);}
            finally{camera.aspect=priorAspect;}
            int oldMask=camera.cullingMask;
            using(var startup=new ViveStartupView(camera))
            {
                startup.Show(true,"Wearing the headset?\nRelease all controls, then press TRIGGER.\nLook naturally forward and hold still.");
                Require(camera.cullingMask==ViveStartupView.Mask,"startup must hide all classroom content");
                Render(camera,Path.Combine(output,"synthetic-vive-startup-confirm.png"));
                Require(startup.WasRenderedBefore(Time.frameCount+1),"startup prompt must have an actual camera render witness");
                var prompt=camera.transform.Find("ViveStartupInstructions");
                Require(prompt!=null,"startup prompt missing");ViewportBounds(camera,prompt.GetComponent<Renderer>());
                startup.Show(false,"Waiting for tracking");
                Require(camera.cullingMask==0&&!startup.WasRenderedBefore(Time.frameCount+1),"tracking loss must invalidate prompt and mask scene");
                startup.Show(true,"Look naturally forward.\nHold still briefly.\nPreparing your view...");
                Render(camera,Path.Combine(output,"synthetic-vive-startup-preparing.png"));
                startup.Hide();Require(!prompt.gameObject.activeSelf,"completed startup must not follow the participant");
            }
            camera.cullingMask=oldMask;
            Debug.Log("VIVE_CLASSROOM_FIXTURE_COMPLETE: synthetic Editor renders, not headset captures.");
        }
        static void RenderQuestionnaireFixtures(WorldSpaceExperimentView view,Camera camera,string output)
        {
            // Only synthetic TaskView values and a demonstrative rating are used.
            // These images do not start a participant session, XR or eye capture.
            view.SetQuestionnaireLanguage("en");view.SetFaults(new string[0]);view.SetPresentationAvailable(true);
            var questionnaire=new TaskView();
            typeof(TaskView).GetProperty("Phase").SetValue(questionnaire,Phase.Questionnaire,null);
            typeof(TaskView).GetProperty("QuestionnaireItem").SetValue(questionnaire,Questionnaire.Item(4,"en"),null);
            view.SetPhaseProgress(0,0,0,0,0,0,false,true,0);
            view.SetViveQuestionnaireRating(3);view.ApplyTaskView(questionnaire);
            var flags=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic;
            var layout=typeof(WorldSpaceExperimentView).GetMethod("LayoutHud",flags);
            var started=typeof(WorldSpaceExperimentView).GetField("instructionAnimationStartedAt",flags);
            Require(layout!=null&&started!=null,"questionnaire fixture requires the actual view layout");
            var names=new[]{"top","bottom","trigger"};
            for(int step=0;step<names.Length;step++)
            {
                double elapsed=step*2+.5;
                Action freeze=()=>started.SetValue(view,Time.realtimeSinceStartupAsDouble-elapsed);
                freeze();layout.Invoke(view,null);
                var guide=FindNamed<Transform>(view,"ViveQuestionnaireControllerGuide");
                var caption=FindNamed<TextMesh>(view,"ControllerGuideStep");
                Require(guide.gameObject.activeInHierarchy&&view.QuestionnaireInstructionVisible&&
                    Normalize(caption.text).Contains(Normalize(ViveQuestionnairePresentation.GuideCaption(step))),
                    "VIVE questionnaire guide and step must be visible during the untimed introduction");
                Render(camera,Path.Combine(output,"synthetic-vive-questionnaire-guide-"+names[step]+".png"),beforeCameraRender:freeze);
            }
            // Three actual layouts of the same rear-side illustration. These
            // images show a demonstration, never a physical button response.
            float firstX=0,pressedX=0;
            var triggerTimes=new[]{4.0,5.0,5.98};
            var triggerNames=new[]{"rest","pull","release"};
            for(int i=0;i<triggerTimes.Length;i++)
            {
                double elapsed=triggerTimes[i];
                Action freeze=()=>started.SetValue(view,Time.realtimeSinceStartupAsDouble-elapsed);
                freeze();layout.Invoke(view,null);
                var rear=FindNamed<Transform>(view,"ViveRearWandGrip");
                var trigger=FindNamed<Transform>(view,"ViveAnimatedTrigger");
                var callout=FindNamed<Transform>(view,"ViveRearTriggerCallout");
                var label=FindNamed<TextMesh>(view,"ViveTriggerLabel");
                Require(view.QuestionnaireInstructionVisible&&rear.gameObject.activeInHierarchy&&trigger.gameObject.activeInHierarchy&&
                    callout.gameObject.activeInHierarchy&&label.text.Contains("BACK TRIGGER")&&
                    Mathf.Abs(trigger.localPosition.x-rear.localPosition.x)<.085f,
                    "rear trigger must remain visibly associated with the wand");
                ViewportBounds(camera,rear.GetComponent<Renderer>());ViewportBounds(camera,trigger.GetComponent<Renderer>());
                ViewportBounds(camera,label.GetComponent<Renderer>());
                if(i==0)firstX=trigger.localPosition.x;
                else if(i==1){pressedX=trigger.localPosition.x;Require(pressedX<firstX-.015f,"trigger pull must move into the handle");}
                else Require(trigger.localPosition.x>pressedX+.015f,"trigger release must return toward its resting position");
                Render(camera,Path.Combine(output,"synthetic-vive-questionnaire-trigger-"+triggerNames[i]+".png"),beforeCameraRender:freeze);
            }
            view.SetPhaseProgress(0,0,0,0,0,0,false,false,0);
            for(int selected=1;selected<=5;selected++)
            {
                view.SetViveQuestionnaireRating(selected);view.ApplyTaskView(questionnaire);layout.Invoke(view,null);
                var selection=FindNamed<TextMesh>(view,"ViveQuestionnaireSelection");
                var body=FindNamed<TextMesh>(view,"ParticipantInstructions");
                Require(view.QuestionnaireResponseVisible&&selection.gameObject.activeInHierarchy&&selection.GetComponent<MeshRenderer>().enabled&&
                    selection.text.Contains("SELECTED: "+selected)&&selection.text.Contains(Questionnaire.Anchor(selected,"en")),
                    "current VIVE candidate and anchor must be visible");
                Require(body.text.Contains("> "+selected)&&Normalize(body.text).Contains(Normalize(Questionnaire.Item(4,"en"))),
                    "long questionnaire item and selected option must both remain in the displayed text");
                var bodyBox=ViewportBounds(camera,body.GetComponent<Renderer>());
                var selectedBox=ViewportBounds(camera,selection.GetComponent<Renderer>());
                Require(!bodyBox.Overlaps(selectedBox),"questionnaire text and current selection must not overlap");
                Render(camera,Path.Combine(output,"synthetic-vive-questionnaire-option-"+selected+".png"));
            }
        }
        static T FindNamed<T>(WorldSpaceExperimentView view,string name) where T:Component
        {
            var matches=view.GetComponentsInChildren<T>(true).Where(x=>x.name==name).ToArray();
            Require(matches.Length==1,"expected exactly one synthetic questionnaire element: "+name);
            return matches[0];
        }
        static string Normalize(string text)
        {return String.Join(" ",(text??"").Split((char[])null,StringSplitOptions.RemoveEmptyEntries));}
        static Rect ViewportBounds(Camera camera,Renderer renderer)
        {
            Require(renderer!=null&&renderer.enabled&&renderer.gameObject.activeInHierarchy,"fixture element must have a visible renderer");
            var bounds=renderer.bounds;float left=Single.PositiveInfinity,right=Single.NegativeInfinity,bottom=Single.PositiveInfinity,top=Single.NegativeInfinity;
            for(int x=-1;x<=1;x+=2)for(int y=-1;y<=1;y+=2)for(int z=-1;z<=1;z+=2)
            {
                var p=camera.WorldToViewportPoint(bounds.center+Vector3.Scale(bounds.extents,new Vector3(x,y,z)));
                Require(p.z>0&&p.x>=0&&p.x<=1&&p.y>=0&&p.y<=1,"questionnaire element must fit inside the Editor camera viewport");
                left=Mathf.Min(left,p.x);right=Mathf.Max(right,p.x);bottom=Mathf.Min(bottom,p.y);top=Mathf.Max(top,p.y);
            }
            return Rect.MinMaxRect(left,bottom,right,top);
        }
        static Vec3 ToVec(Vector3 value){return new Vec3(value.x,value.y,value.z);}
        static void Render(Camera camera,string path,bool verifyLegend=false,bool? expectAvatar=null,Action beforeCameraRender=null)
        {
            var render=new RenderTexture(1280,720,24,RenderTextureFormat.ARGB32);
            var previous=RenderTexture.active;var priorTarget=camera.targetTexture;
            try
            {
                // A warm-up render settles objects created/enabled in this same
                // edit-mode fixture before the image used as visual evidence.
                camera.targetTexture=render;
                if(beforeCameraRender!=null)beforeCameraRender();camera.Render();
                if(beforeCameraRender!=null)beforeCameraRender();camera.Render();RenderTexture.active=render;
                var pixels=new Texture2D(1280,720,TextureFormat.RGB24,false);
                try
                {
                    pixels.ReadPixels(new Rect(0,0,1280,720),0,0);pixels.Apply();
                    if(verifyLegend)
                    {
                        int red=0,green=0,blue=0,yellow=0;
                        // Only the left portion contains the mapping; the green
                        // centre word cannot make this check pass accidentally.
                        var values=pixels.GetPixels32();
                        for(int y=0;y<720;y++)for(int x=0;x<480;x++)
                        {
                            var c=values[y*1280+x];
                            if(c.r>180&&c.g<80&&c.b<80)red++;
                            if(c.g>180&&c.r<80&&c.b<80)green++;
                            if(c.b>180&&c.r<80&&c.g<80)blue++;
                            if(c.r>180&&c.g>180&&c.b<80)yellow++;
                        }
                        Require(red>=50&&green>=50&&blue>=50&&yellow>=50,"synthetic VIVE legend colours must actually render on the left");
                    }
                    if(expectAvatar.HasValue)
                    {
                        int shirt=0;var values=pixels.GetPixels32();
                        // Current neutral room is grey. The standing placeholder's
                        // blue shirt must appear on the right only when requested.
                        for(int y=0;y<720;y++)for(int x=880;x<1280;x++)
                        {
                            var c=values[y*1280+x];
                            if(c.r>=45&&c.r<=90&&c.g>=85&&c.g<=140&&c.b>=110&&c.b<=170&&c.g>c.r+20&&c.b>c.g+8)shirt++;
                        }
                        Require(expectAvatar.Value?shirt>=100:shirt==0,"synthetic avatar pixels must match requested phase/condition visibility");
                    }
                    File.WriteAllBytes(path,pixels.EncodeToPNG());
                }
                finally{UnityEngine.Object.DestroyImmediate(pixels);}
            }
            finally{camera.targetTexture=priorTarget;RenderTexture.active=previous;render.Release();UnityEngine.Object.DestroyImmediate(render);}
        }
    }

    // BEGIN PURE RECORDING BUILD REQUEST
    // This only chooses per-build defines. It grants no runtime permission,
    // changes no persistent symbols and never starts a source or recording.
    internal static class ViveRecordingBuildRequest
    {
        internal static string[] Defines(string control,string legacy,bool intentControlled,bool intentLegacy,string persistedDefines)
        {
            if(!(String.IsNullOrEmpty(control)||control=="1")||!(String.IsNullOrEmpty(legacy)||legacy=="1"))
                throw new InvalidOperationException("VIVE classroom: invalid recording development build request");
            bool controlled=control=="1",legacyRequested=legacy=="1";
            if(legacyRequested&&!controlled)
                throw new InvalidOperationException("VIVE classroom: legacy raw recording requires controlled Development");
            if(controlled!=intentControlled||legacyRequested!=intentLegacy)
                throw new InvalidOperationException("VIVE classroom: recording build requests must exactly match isolated intent");
            foreach(string define in (persistedDefines??"").Split(';'))
                if(define.Trim()=="STROOP_CONTROLLED_RECORDING_DEVELOPMENT"||define.Trim()=="STROOP_LEGACY_RAW_RECORDING_DEVELOPMENT")
                    throw new InvalidOperationException("VIVE classroom: recording defines must be explicit per-build options, not persisted project settings");
            return legacyRequested?new[]{"STROOP_CONTROLLED_RECORDING_DEVELOPMENT","STROOP_LEGACY_RAW_RECORDING_DEVELOPMENT"}:
                controlled?new[]{"STROOP_CONTROLLED_RECORDING_DEVELOPMENT"}:new string[0];
        }
    }
    // END PURE RECORDING BUILD REQUEST
}
#endif
