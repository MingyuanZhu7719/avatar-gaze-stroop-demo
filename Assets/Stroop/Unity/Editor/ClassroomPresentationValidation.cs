using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using AvatarStroop.Core;
using AvatarStroop.Runtime;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace AvatarStroop.Unity.Editor
{
    // Explicit isolated-project checks and synthetic visual fixtures. No bootstrap,
    // owner commands, protocol clock, player, input or XR loader is started here.
    public static class ClassroomPresentationValidation
    {
        [Serializable] sealed class Evidence
        {
            public string kind="synthetic-editor-presentation-fixtures-not-trials";
            public string geometry_version=WorldSpaceExperimentView.GeometryVersion;
            public bool actual_experiment=false, xr_started=false, headset_acceptance=false;
            public int checks;
            public string[] images;
            public Vector3 task_world,avatar_world;
            public float stimulus_character_size;
            public float avatar_yaw_degrees=225,desktop_pitch_degrees=15.5f;
            public float desktop_yaw_degrees=-4;
            public float desktop_vertical_fov_degrees=WorldSpaceExperimentView.DesktopVerticalFieldOfView;
            public float synthetic_mono_vertical_fov_degrees=WorldSpaceExperimentView.DesktopVerticalFieldOfView;
            public Vector3 nominal_participant_reference=WorldSpaceExperimentView.CenteredParticipantReference;
            public float participant_fixture_pitch_degrees=15,participant_fixture_yaw_degrees=4;
            public bool start_alignment_once_xz_yaw=true,tracked_height_pitch_roll_preserved=true;
            public bool active_participant_view_has_only_world_content_and_real_faults=true;
            public bool quest_menu_world_fixed=true;
            public Vector3 menu_world=WorldSpaceExperimentView.FixedMenuPosition;
            public Vector2 menu_size=new Vector2(WorldSpaceExperimentView.FixedMenuWidth,WorldSpaceExperimentView.FixedMenuHeight);
            public int[] questionnaire_rendered_candidates;
            public string ui_language="en";
            public int[] questionnaire_rendered_items;
            public int[] countdown_rendered_seconds;
            public int[] rest_rendered_seconds;
            public string[] questionnaire_guide_steps;
            public bool completion_only_gentle_fireworks,phase_animation_world_fixed;
            public bool progress_fill_rendered_above_track,questionnaire_intro_waits_for_acknowledgement;
            public bool rest_reference_persists_and_allows_immediate_continue;
            public Vector3 board_world=WorldSpaceExperimentView.BoardPosition;
            public float writing_front_z=1.915f,menu_backing_z=1.910f,task_text_z=1.900f;
            public string[] ink_rgba;
            public WordBounds[] words;
            public StartupHeightEvidence startup_reference_height;
        }
        [Serializable] sealed class WordBounds {public string word;public Vector3 world_size;}
        [Serializable] sealed class StartupHeightEvidence
        {
            public string kind="synthetic-floor-origin-reproduction-not-headset-capture";
            public bool xr_focus_or_tracking_events_injected=false;
            public float authored_device_offset,raw_floor_height,before_world_height,prepared_world_height;
            public Vector3 raw_head_euler;
            public bool raw_pose_unchanged,waiting_render_uniform,render_settings_restored;
        }
        static int checks;
        static void Require(bool value,string message)
        {if(!value)throw new InvalidOperationException("Classroom validation: "+message);checks++;}
        static bool Near(Vector3 a,Vector3 b) {return Vector3.Distance(a,b)<.0001f;}
        static Transform Content(WorldSpaceExperimentView view) {return view.transform.Find("WorldSpaceContent");}
        static TextMesh Stimulus(WorldSpaceExperimentView view) {return Content(view).Find("Stimulus").GetComponent<TextMesh>();}
        static Transform Menu(WorldSpaceExperimentView view)
        {return Content(view).Find("HeadsetVisibleDevelopmentWarning");}
        static void Layout(WorldSpaceExperimentView view)
        {typeof(WorldSpaceExperimentView).GetMethod("LayoutHud",BindingFlags.NonPublic|BindingFlags.Instance).Invoke(view,null);}
        static QuestPreviewControls Stick(double y)
        {return new QuestPreviewControls(true,false,false,false,false,false,false,y);}
        static void FixtureControls(WorldSpaceExperimentView view,Phase phase,int? candidate=null)
        {
            // Match QuestLinkPreviewRig.ReadInput's phase-specific presentation.
            // Candidates below come from the real pure router, not a live device
            // or an Experiment. The Editor never creates/enables an XR rig.
            string hint=phase==Phase.Questionnaire?
                "Left stick UP: lower number | DOWN: higher number (1-5)\nCentre between steps | A: confirm\nSelected: "+(candidate.HasValue?candidate.Value.ToString():"--")+" (not submitted)":
                phase==Phase.Paused?"PAUSED | Right trigger: actively resume\nB: withdraw and end preview":
                phase==Phase.NeedsProtocolDecision?"A protocol decision is pending\nNo automatic retry or resume | B: withdraw":
                phase==Phase.Rest?"A: continue whenever ready. No minimum rest.\nLeft menu: pause":
                phase==Phase.PracticeCorrection?"Right trigger: continue when ready\nLeft menu: pause":
                phase==Phase.Ended?"Session ended. No participant response is required.":
                "X red | Y green | A blue | B yellow\nRelease before the next answer | Left menu: pause";
            view.SetHeadsetPreview("Head pose and Touch input available | avatar gaze: SIMULATED",hint);
        }

        public static void ValidateView(WorldSpaceExperimentView view)
        {
            Require(view!=null&&view.PresentationCamera!=null,"presentation/camera missing");
            var content=Content(view);var word=Stimulus(view);
            Require(content!=null&&word!=null,"world stimulus missing");
            Require(Near(view.TaskWorldPosition,WorldSpaceExperimentView.TaskDisplayPosition),"task anchor changed");
            Require(Near(view.AvatarRoot.position,new Vector3(.65f,.9f,1.9f)),"standing avatar anchor changed");
            Require(Quaternion.Angle(view.AvatarRoot.localRotation,Quaternion.Euler(0,225,0))<.001f,"standing body orientation changed");
            Require(Near(view.AvatarHead.localPosition,new Vector3(0,.7f,0)),"head offset changed");
            Require(Near(view.AvatarRoot.localScale,Vector3.one),"avatar scale changed");
            Require(Mathf.Approximately(word.characterSize,WorldSpaceExperimentView.StimulusCharacterSize)&&word.fontSize==100&&Near(word.transform.localScale,Vector3.one),"fixed stimulus typography changed");
            Require(word.GetComponent<StimulusRenderWitness>()!=null,"actual stimulus witness missing");
            Require(word.transform.parent==content,"stimulus must remain world space, not HUD");
            var board=content.Find("ClassroomEnvironment/Whiteboard");
            Require(board!=null&&Near(board.position,WorldSpaceExperimentView.BoardPosition),"board position mismatch");
            var writing=board.Find("WritingArea").GetComponent<Renderer>();
            Require(writing.bounds.min.z>view.TaskWorldPosition.z+.01f,"writing surface occludes word plane");
            Require(writing.bounds.center.x==view.TaskWorldPosition.x&&Mathf.Approximately(writing.bounds.center.y,view.TaskWorldPosition.y),"writing area not centred on word");
            Require(board.Find("OffWhiteFace")!=null,"whiteboard face missing");
            Require(Mathf.Abs(writing.bounds.min.z-1.915f)<.0001f,"physical writing surface depth changed");
            Require(writing.bounds.size.x>=WorldSpaceExperimentView.FixedMenuWidth&&writing.bounds.size.y>=WorldSpaceExperimentView.FixedMenuHeight,
                "physical writing inset does not contain the entire fixed display region");
            Require(Near(view.TaskWorldPosition,WorldSpaceExperimentView.FixedMenuPosition),"word and menu do not share the physical display anchor");
            Require(board.position.z<2.04f,"board was not moved toward the participant");
            Require(content.GetComponentsInChildren<Collider>(true).Length==0,"scenery unexpectedly adds colliders/AOIs");
            var body=view.AvatarRoot.Find("StandingBody");
            Require(body!=null,"standing body missing");
            foreach(string part in new[]{"Neck","LeftArm","RightArm","LeftHand","RightHand","LeftLeg","RightLeg","LeftShoe","RightShoe"})
                Require(body.Find(part)!=null,"missing fixed body part "+part);
            foreach(var renderer in view.AvatarRoot.GetComponentsInChildren<MeshRenderer>(true))
                Require(renderer.GetComponent<AvatarRenderWitness>()!=null,"avatar renderer lacks witness");
            Require(view.AvatarRoot.GetComponentsInChildren<Animator>(true).Length==0,"unexpected independent animation driver");
            if(new SerializedObject(view).FindProperty("headsetPreview").boolValue&&view.Role==Actor.Participant)
            {
                var menu=Menu(view);
                Require(menu!=null&&menu.parent==content&&Near(menu.position,WorldSpaceExperimentView.FixedMenuPosition),"Quest menu is not world fixed");
                Require(Quaternion.Angle(menu.rotation,Quaternion.identity)<.001f&&Near(menu.localScale,Vector3.one),"world menu rotation/scale changed");
                Require(!menu.IsChildOf(view.PresentationCamera.transform),"Quest menu follows camera");
                Require(Near(view.ParticipantReferencePosition,WorldSpaceExperimentView.CenteredParticipantReference),"nominal centred participant reference changed");
                var backing=menu.Find("FixedWorldMenuBacking");
                Require(backing!=null&&backing.GetComponent<Renderer>().sharedMaterial.color.a==1,"opaque fixed menu backing missing");
                var legend=content.Find("QuestControllerLegend");
                Require(legend!=null&&legend.parent==content&&Near(legend.position,new Vector3(-1.60f,1.55f,WorldSpaceExperimentView.BoardPosition.z)),"controller legend is not fixed world space left of board");
                Require(legend.GetComponentsInChildren<AvatarRenderWitness>(true).Length==0,"legend incorrectly belongs to avatar visibility");
                foreach(string hand in new[]{"LeftController","RightController"})
                    Require(legend.Find(hand+"/ControllerFace")!=null&&legend.Find(hand+"/Grip")!=null,"controller silhouette missing");
                var names=new[]{"X","Y","A","B"};var colours=new[]{Color.red,Color.green,Color.blue,Color.yellow};
                for(int i=0;i<names.Length;i++)
                {
                    var hand=legend.Find(i<2?"LeftController":"RightController");
                    Require(hand.Find("Button"+names[i]).GetComponent<Renderer>().sharedMaterial.color==colours[i],"legend colour mapping changed");
                    Require(hand.Find("Button"+names[i]+"Label").GetComponent<TextMesh>().text==names[i],"controller button label missing");
                }
                Require(legend.Find("LeftController/ButtonY").localPosition.y>legend.Find("LeftController/ButtonX").localPosition.y&&
                    legend.Find("RightController/ButtonB").localPosition.y>legend.Find("RightController/ButtonA").localPosition.y,"controller face layout changed");
                Require(legend.Find("LeftController/ButtonY").localPosition.x>legend.Find("LeftController/ButtonX").localPosition.x&&
                    legend.Find("RightController/ButtonB").localPosition.x<legend.Find("RightController/ButtonA").localPosition.x,"controller diagonal button relation changed");
                Require(!legend.GetComponentsInChildren<TextMesh>(true).Any(text=>new[]{"RED","GREEN","BLUE","YELLOW"}.Contains(text.text)),"legend adds competing Stroop colour words");
            }
        }

        // Internal-set protocol DTOs are populated only by this Editor fixture.
        // This is not an Experiment, simulation host, live trial or state override.
        static TaskView FixtureTask(Phase phase, bool visible, Colour ink, bool operatorRole=false,Colour word=Colour.Yellow)
        {
            var result=new TaskView();
            Set(result,"Phase",phase);Set(result,"OperatorAvatarVisible",visible);
            Set(result,"ParticipantAvatarVisible",visible);Set(result,"OperatorViewProvisional",operatorRole);
            if(phase==Phase.PracticeCorrection)Set(result,"PracticeCorrection","Select the font colour: "+ink);
            if(phase==Phase.Questionnaire)Set(result,"QuestionnaireItem",Questionnaire.Item(4,"en"));
            if(phase==Phase.MainTask||phase==Phase.Practice)
            {Set(result,"TrialId","editor-visual-fixture");Set(result,"Stimulus",new Stimulus(word,ink));}
            return result;
        }
        static void Set(TaskView target,string property,object value)
        {typeof(TaskView).GetProperty(property).SetValue(target,value,null);}
        static Vec3 Vec(Vector3 value) {return new Vec3(value.x,value.y,value.z);}
        static AutomaticGazeGeometry Geometry(WorldSpaceExperimentView view)
        {
            var root=view.AvatarRoot;var head=view.AvatarHead.position;
            return DevelopmentGeometry.FromLocalDirections(Vec(root.InverseTransformDirection(view.ParticipantReferencePosition-head)),
                Vec(root.InverseTransformDirection(view.TaskWorldPosition-head)),WorldSpaceExperimentView.GeometryVersion);
        }
        static AvatarOutput Output(WorldSpaceExperimentView view,Condition condition,Phase phase)
        {
            return new AvatarController("editor-visual-fixture",RunKind.Simulation,0,0,ControlSelection.Automatic,
                condition,GazeTarget.Participant,Geometry(view)).Render(phase,0);
        }
        static void Apply(WorldSpaceExperimentView view,Phase phase,Condition condition,Colour ink)
        {
            bool visible=phase==Phase.MainTask&&condition!=Condition.D;
            view.ApplyTaskView(FixtureTask(phase,visible,ink,view.Role==Actor.Operator));
            view.ApplyAvatarOutput(Output(view,condition,phase));
        }
        static void CheckDirection(Vector3 expected,Vector3 actual,string message)
        {Require(Vector3.Dot(expected.normalized,actual.normalized)>.99999f,message);}
        static void CheckGeometry(WorldSpaceExperimentView view)
        {
            var body=view.AvatarRoot.Find("StandingBody");
            var before=body.GetComponentsInChildren<Transform>(true).ToDictionary(t=>t,t=>t.localToWorldMatrix);
            foreach(var condition in new[]{Condition.A,Condition.C})
            {
                Apply(view,Phase.MainTask,condition,Colour.Green);
                Vector3 target=condition==Condition.A?view.ParticipantReferencePosition:view.TaskWorldPosition;
                Vector3 expected=target-view.AvatarHead.position;
                CheckDirection(expected,view.AvatarHead.forward,"head misses actual "+condition+" target");
                // The shared gaze model defines directions from the head origin;
                // it does not add independently calibrated per-eye vergence.
                foreach(string eye in new[]{"LeftEye","RightEye"})
                    CheckDirection(expected,view.AvatarHead.Find(eye).forward,"eye misses actual "+condition+" target");
                foreach(var pair in before)Require(pair.Key.localToWorldMatrix==pair.Value,"body moved with gaze");
                if(condition==Condition.C)
                    CheckUnobstructed(view.AvatarHead.position,view.TaskWorldPosition,
                        Content(view).Find("ClassroomEnvironment").GetComponentsInChildren<Renderer>(true),"task gaze crosses room/board");
            }
            foreach(var phase in new[]{Phase.Preparation,Phase.Practice,Phase.Rest,Phase.Questionnaire,Phase.Paused})
            {
                if(phase==Phase.Questionnaire) view.SetQuestionnaireLanguage("en");
                Apply(view,phase,Condition.A,Colour.Green);
                Require(!view.AvatarRoot.gameObject.activeInHierarchy,"avatar visible in "+phase);
                Require(!view.AvatarRoot.GetComponentsInChildren<Renderer>(true).Any(r=>r.gameObject.activeInHierarchy),"body part leaks in "+phase);
                if(phase!=Phase.Practice)Require(!Stimulus(view).gameObject.activeInHierarchy,"stimulus leaks outside task/practice");
            }
            Apply(view,Phase.MainTask,Condition.D,Colour.Green);
            Require(!view.AvatarRoot.gameObject.activeInHierarchy,"avatar visible in no-avatar D");
        }
        static void CheckUnobstructed(Vector3 from,Vector3 to,IEnumerable<Renderer> renderers,string message)
        {
            var delta=to-from;var ray=new Ray(from,delta.normalized);
            foreach(var renderer in renderers)
            {
                float hit;
                Require(!renderer.bounds.IntersectRay(ray,out hit)||hit>=delta.magnitude-.001f,message+": "+renderer.name);
            }
        }
        static WorldSpaceExperimentView NewFixture(Actor role,bool headset=true)
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
            var camera=new GameObject("SyntheticFixtureCamera").AddComponent<Camera>();
            camera.transform.position=role==Actor.Participant?WorldSpaceExperimentView.CenteredParticipantReference:new Vector3(0,1.6f,0);
            camera.fieldOfView=WorldSpaceExperimentView.DesktopVerticalFieldOfView;
            camera.nearClipPlane=.05f;camera.farClipPlane=20;camera.aspect=1100f/720;
            camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=new Color(.035f,.045f,.06f);
            camera.stereoTargetEye=StereoTargetEyeMask.None;
            var view=new GameObject("SyntheticFixturePresentation").AddComponent<WorldSpaceExperimentView>();
            view.Initialize(camera,role);view.SetPresentationAvailable(false);
            if(role==Actor.Participant&&headset)
            {
                view.SetHeadsetPreview("Head pose and Touch input available | avatar gaze: SIMULATED", "X red | Y green | A blue | B yellow\nLeft menu: pause; while paused, B: withdraw");
                // Synthetic mono composition only. Runtime initial yaw is +Z;
                // actual tracked pitch/roll are never authored or flattened.
                camera.transform.rotation=Quaternion.Euler(15,4,0);
                Require(Mathf.Approximately(camera.fieldOfView,WorldSpaceExperimentView.DesktopVerticalFieldOfView),"synthetic mono fixture field of view changed");
            }
            // Fixture provenance belongs to filenames/JSON, not a fake fault
            // painted into the participant's otherwise clean active view.
            view.SetFaults(new string[0]);
            return view;
        }
        static void CheckSavedScenes()
        {
            foreach(string path in new[]{"Assets/Stroop/Generated/ParticipantQuestPreview.unity","Assets/Stroop/Generated/OperatorDevelopment.unity"})
            {
                var scene=EditorSceneManager.OpenScene(path,OpenSceneMode.Single);
                var views=scene.GetRootGameObjects().SelectMany(root=>root.GetComponentsInChildren<WorldSpaceExperimentView>(true)).ToArray();
                Require(views.Length==1,"saved scene has incorrect presentation count");
                // Validate serialized state before Initialize can migrate it.
                ValidateView(views[0]);
                if(views[0].Role==Actor.Participant)
                {
                    var warning=Menu(views[0]);
                    Require(warning!=null&&Near(warning.position,WorldSpaceExperimentView.FixedMenuPosition),"saved fixed world menu changed");
                    Require(Quaternion.Angle(views[0].PresentationCamera.transform.localRotation,Quaternion.identity)<.001f,"Quest camera retained desktop pitch");
                    Require(Near(views[0].PresentationCamera.transform.position,WorldSpaceExperimentView.CenteredParticipantReference),"saved authored participant station is not board centred");
#if STROOP_XR_PREVIEW
                    var rig=new SerializedObject(views[0].GetComponent<QuestLinkPreviewRig>());
                    var origin=rig.FindProperty("origin").objectReferenceValue as Behaviour;
                    Require(origin!=null&&!origin.enabled,"saved XROrigin can automatically initialize/recenter runtime");
#endif
                }
                foreach(var renderer in scene.GetRootGameObjects().SelectMany(root=>root.GetComponentsInChildren<Renderer>(true)))
                    Require(renderer.sharedMaterial!=null&&!String.IsNullOrEmpty(AssetDatabase.GetAssetPath(renderer.sharedMaterial)),"saved material is transient/missing");
            }
        }
        static void CheckMigration(WorldSpaceExperimentView view)
        {
            var content=Content(view);
            // Reconstruct the relevant old generated-scene state, retaining the
            // actual head/eye/TextMesh instances and their serialized references.
            var word=Stimulus(view);var head=view.AvatarHead;var oldEyes=head.GetComponentsInChildren<Transform>(true);
            UnityEngine.Object.DestroyImmediate(content.Find("ClassroomEnvironment").gameObject);
            UnityEngine.Object.DestroyImmediate(view.AvatarRoot.Find("StandingBody").gameObject);
            var oldLegend=content.Find("QuestControllerLegend");if(oldLegend!=null)UnityEngine.Object.DestroyImmediate(oldLegend.gameObject);
            word.transform.localPosition=new Vector3(.5f,1.65f,2);
            word.characterSize=.13f;word.transform.localScale=Vector3.one;
            // Recreate the previous camera-attached menu before the migration.
            var menu=Menu(view);menu.SetParent(view.PresentationCamera.transform,false);menu.localPosition=new Vector3(0,0,2);
            view.AvatarRoot.localPosition=new Vector3(-.65f,.9f,2.5f);
            view.AvatarRoot.localRotation=Quaternion.Euler(0,180,0);
            view.Initialize(view.PresentationCamera,view.Role);ValidateView(view);
            Require(Mathf.Approximately(word.characterSize,WorldSpaceExperimentView.StimulusCharacterSize),"legacy oversized glyph setting was not migrated");
            int objects=content.GetComponentsInChildren<Transform>(true).Length;
            int materials=content.GetComponentsInChildren<Renderer>(true).Select(r=>r.sharedMaterial).Distinct().Count();
            view.Initialize(view.PresentationCamera,view.Role);view.Initialize(view.PresentationCamera,view.Role);
            Require(content.GetComponentsInChildren<Transform>(true).Length==objects,"migration duplicated objects");
            Require(content.GetComponentsInChildren<Renderer>(true).Select(r=>r.sharedMaterial).Distinct().Count()==materials,"migration duplicated materials");
            Require(Stimulus(view)==word&&view.AvatarHead==head&&oldEyes.All(t=>t!=null),"migration replaced gaze/stimulus identity");
        }
        static void CheckInitialAlignment(WorldSpaceExperimentView view)
        {
            var reference=view.ParticipantReferencePosition;var camera=view.PresentationCamera;
            var savedPosition=camera.transform.position;camera.transform.position+=new Vector3(.4f,.3f,-.2f);
            Require(Near(view.ParticipantReferencePosition,reference),"automatic gaze reference follows tracked head movement");camera.transform.position=savedPosition;
            foreach(float heightOffset in new[]{0f,1.6f})foreach(float yaw in new[]{0f,90f,-125f,180f})
            {
                var root=new GameObject("SyntheticTrackingOrigin");var parent=new GameObject("SyntheticHeightOffset");var head=new GameObject("SyntheticTrackedHead");
                parent.transform.SetParent(root.transform,false);parent.transform.localPosition=new Vector3(0,heightOffset,0);head.transform.SetParent(parent.transform,false);
                root.transform.SetPositionAndRotation(new Vector3(.3f,0,-.7f),Quaternion.Euler(0,35,0));
                Vector3 rawPosition=new Vector3(2,1.2f,-3);Quaternion rawRotation=Quaternion.Euler(0,yaw,0)*Quaternion.Euler(20,0,10);
                head.transform.SetLocalPositionAndRotation(rawPosition,rawRotation);
                float initialY=head.transform.position.y;Vector3 initialForward=head.transform.forward,initialUp=head.transform.up;
                var placement=new QuestPreviewStartAlignment();var oldRoot=root.transform.localToWorldMatrix;
                Require(!placement.TryApply(false,root.transform,parent.transform,rawPosition,rawRotation,reference)&&!placement.Applied&&oldRoot==root.transform.localToWorldMatrix,"unready input moves initial origin");
                Require(!placement.TryApply(true,root.transform,parent.transform,rawPosition,Quaternion.Euler(90,0,0),reference)&&!placement.Applied,"near vertical initial pose has undefined yaw but aligns");
                Require(!placement.TryApply(true,root.transform,parent.transform,new Vector3(Single.NaN,0,0),rawRotation,reference)&&!placement.Applied,"nonfinite pose aligns");
                Require(placement.TryApply(true,root.transform,parent.transform,rawPosition,rawRotation,reference)&&placement.Applied,"valid initial alignment failed");
                Require(Mathf.Abs(head.transform.position.x-reference.x)<.0001f&&Mathf.Abs(head.transform.position.z-reference.z)<.0001f,"initial head is not horizontally centred on board");
                Require(Mathf.Abs(head.transform.position.y-initialY)<.0001f,"alignment changed tracked height");
                Require(Vector3.Dot(Vector3.ProjectOnPlane(head.transform.forward,Vector3.up).normalized,Vector3.forward)>.99999f,"initial yaw does not face board");
                Require(Mathf.Abs(head.transform.forward.y-initialForward.y)<.0001f&&Mathf.Abs(head.transform.up.y-initialUp.y)<.0001f,"alignment flattened pitch/roll");
                var alignedRoot=root.transform.localToWorldMatrix;Vector3 alignedHead=head.transform.position,delta=new Vector3(.3f,.2f,-.4f);
                head.transform.localPosition=rawPosition+delta;head.transform.localRotation=rawRotation*Quaternion.Euler(0,15,0);
                Vector3 actualDelta=head.transform.position-alignedHead,expectedDelta=parent.transform.TransformVector(delta);
                Require(Near(actualDelta,expectedDelta)&&Mathf.Abs(actualDelta.magnitude-delta.magnitude)<.0001f&&Mathf.Abs(actualDelta.y-.2f)<.0001f,"later physical movement is cancelled or rescaled");
                Require(!placement.TryApply(false,root.transform,parent.transform,head.transform.localPosition,head.transform.localRotation,reference)&&
                    !placement.TryApply(true,root.transform,parent.transform,head.transform.localPosition,head.transform.localRotation,reference)&&
                    root.transform.localToWorldMatrix==alignedRoot,"tracking recovery re-aligns or locks physical movement");
                UnityEngine.Object.DestroyImmediate(root);
            }
        }
        static void CheckStartupReferenceHeightAndView()
        {
            // Exercise the actual placement/reveal helpers with delayed caller
            // readiness, without constructing an XR loader or claiming to test
            // native focus events, TrackedPoseDriver timing, or headset output.
            foreach(bool floorMode in new[]{true,false})
            foreach(float rawHeight in floorMode?new[]{1.25f,1.6f,1.92f}:new[]{-.2f,.25f})
            {
                var root=new GameObject("SyntheticStartupOrigin");
                var parent=new GameObject("SyntheticStartupHeightOffset");
                var camera=new GameObject("SyntheticStartupTrackedCamera").AddComponent<Camera>();
                try
                {
                    parent.transform.SetParent(root.transform,false);camera.transform.SetParent(parent.transform,false);
                    root.transform.SetPositionAndRotation(new Vector3(.4f,0,-.3f),Quaternion.Euler(0,27,0));
                    var authoredOffset=new Vector3(.17f,1.6f,-.22f);
                    parent.transform.localPosition=authoredOffset;
                    var rawPosition=new Vector3(.3f,rawHeight,-.5f);
                    var rawRotation=Quaternion.Euler(0,37,0)*Quaternion.Euler(14,0,-9);
                    camera.transform.SetLocalPositionAndRotation(rawPosition,rawRotation);
                    camera.cullingMask=0x13579;camera.clearFlags=floorMode?CameraClearFlags.Depth:CameraClearFlags.Skybox;
                    camera.backgroundColor=new Color(.2f,.4f,.6f,.8f);
                    int savedMask=camera.cullingMask;var savedFlags=camera.clearFlags;var savedBackground=camera.backgroundColor;
                    var placement=new QuestPreviewStartAlignment();var startup=new QuestPreviewStartupView(camera);
                    var originalRoot=root.transform.localToWorldMatrix;
                    Require(camera.enabled&&camera.cullingMask==0&&camera.clearFlags==CameraClearFlags.SolidColor&&!startup.Revealed,
                        "startup room is visible or tracking camera was disabled before reference readiness");
                    Require(Mathf.Abs(camera.transform.position.y-(rawHeight+1.6f))<.0001f,"synthetic authored height no longer reproduces additive offset");
                    for(int delayedUpdate=0;delayedUpdate<3;delayedUpdate++)
                    {
                        Require(!placement.PrepareReferenceHeight(false,floorMode,parent.transform,1.6f)&&
                            !placement.TryApply(false,root.transform,parent.transform,rawPosition,rawRotation,WorldSpaceExperimentView.CenteredParticipantReference)&&
                            !startup.TryReveal(false),"delayed reference/pose/focus readiness admitted startup");
                        Require(root.transform.localToWorldMatrix==originalRoot&&Near(parent.transform.localPosition,authoredOffset)&&
                            Near(camera.transform.localPosition,rawPosition)&&Quaternion.Angle(camera.transform.localRotation,rawRotation)<.001f&&camera.cullingMask==0,
                            "delayed startup changed origin, raw pose, or visibility");
                    }
                    foreach(float invalidHeight in new[]{Single.NaN,Single.PositiveInfinity,Single.NegativeInfinity,-.01f})
                        Require(!placement.PrepareReferenceHeight(true,floorMode,parent.transform,invalidHeight)&&Near(parent.transform.localPosition,authoredOffset),
                            "invalid configured device height changed the reference offset");
                    // Origin convention may resolve before either pose or focus.
                    // It may also change before the one-shot placement commits.
                    foreach(bool preparingFloor in new[]{true,false,floorMode})
                    {
                        Require(placement.PrepareReferenceHeight(true,preparingFloor,parent.transform,1.6f),"supported startup origin height was not prepared");
                        Require(Near(parent.transform.localPosition,new Vector3(authoredOffset.x,preparingFloor?0:1.6f,authoredOffset.z))&&
                            parent.transform.localRotation==Quaternion.identity&&Near(parent.transform.localScale,Vector3.one)&&
                            root.transform.localToWorldMatrix==originalRoot&&Near(camera.transform.localPosition,rawPosition)&&
                            Quaternion.Angle(camera.transform.localRotation,rawRotation)<.001f&&!placement.Applied&&!startup.Revealed,
                            "height preparation changed XZ, raw pose, alignment, or visibility");
                    }
                    float expectedHeight=rawHeight+(floorMode?0:1.6f);
                    Require(Mathf.Abs(camera.transform.position.y-expectedHeight)<.0001f,"startup convention flattened real height or retained the Floor lift");
                    Require(!placement.TryApply(true,root.transform,parent.transform,rawPosition,new Quaternion(0,0,0,0),WorldSpaceExperimentView.CenteredParticipantReference)&&
                        !placement.TryApply(false,root.transform,parent.transform,rawPosition,rawRotation,WorldSpaceExperimentView.CenteredParticipantReference)&&
                        !startup.TryReveal(false)&&root.transform.localToWorldMatrix==originalRoot&&camera.cullingMask==0,
                        "invalid pose or unavailable focus aligned/revealed the prepared room");
                    Vector3 beforeForward=camera.transform.forward,beforeUp=camera.transform.up;
                    Require(placement.TryApply(true,root.transform,parent.transform,rawPosition,rawRotation,WorldSpaceExperimentView.CenteredParticipantReference),
                        "prepared startup did not align when caller became ready");
                    Require(Mathf.Abs(camera.transform.position.y-expectedHeight)<.0001f&&
                        Mathf.Abs(camera.transform.forward.y-beforeForward.y)<.0001f&&Mathf.Abs(camera.transform.up.y-beforeUp.y)<.0001f,
                        "startup alignment changed tracked height, pitch, or roll");
                    Require(!startup.TryReveal(false)&&!startup.Revealed&&camera.cullingMask==0,"alignment alone bypassed the later-frame readiness gate");
                    Require(startup.TryReveal(true)&&startup.Revealed&&camera.cullingMask==savedMask&&camera.clearFlags==savedFlags&&camera.backgroundColor.Equals(savedBackground),
                        "first ready view did not exactly restore camera render settings");
                    var alignedRoot=root.transform.localToWorldMatrix;var preparedOffset=parent.transform.localPosition;
                    Vector3 alignedHead=camera.transform.position,delta=new Vector3(.28f,-.13f,.34f);
                    var movedRotation=rawRotation*Quaternion.Euler(-7,17,5);
                    camera.transform.SetLocalPositionAndRotation(rawPosition+delta,movedRotation);
                    Require(Near(camera.transform.position-alignedHead,parent.transform.TransformVector(delta))&&
                        Quaternion.Angle(camera.transform.localRotation,movedRotation)<.001f,"physical translation/pitch/roll were cancelled after startup");
                    // A later owner of render settings must not be overwritten
                    // by a second reveal attempt after remove/re-wear.
                    camera.cullingMask=0x2468;camera.clearFlags=CameraClearFlags.Nothing;
                    var laterBackground=new Color(.6f,.3f,.1f,.9f);camera.backgroundColor=laterBackground;
                    foreach(bool readyAfterLoss in new[]{false,true,false,true})
                    {
                        Require(!placement.PrepareReferenceHeight(readyAfterLoss,!floorMode,parent.transform,4f)&&
                            !placement.TryApply(readyAfterLoss,root.transform,parent.transform,camera.transform.localPosition,camera.transform.localRotation,WorldSpaceExperimentView.CenteredParticipantReference)&&
                            !startup.TryReveal(readyAfterLoss),"tracking/focus recovery reapplied startup placement or reveal");
                        Require(root.transform.localToWorldMatrix==alignedRoot&&Near(parent.transform.localPosition,preparedOffset)&&
                            Near(camera.transform.position-alignedHead,parent.transform.TransformVector(delta))&&
                            Quaternion.Angle(camera.transform.localRotation,movedRotation)<.001f&&
                            camera.cullingMask==0x2468&&camera.clearFlags==CameraClearFlags.Nothing&&camera.backgroundColor.Equals(laterBackground)&&startup.Revealed,
                            "remove/re-wear changed reference height, physical movement, or later render settings");
                    }
                }
                finally {UnityEngine.Object.DestroyImmediate(root);}
            }
        }
        static void CheckFraming(WorldSpaceExperimentView view)
        {
            var camera=view.PresentationCamera;
            var renderers=Content(view).Find("ClassroomEnvironment/Whiteboard").GetComponentsInChildren<Renderer>(true)
                .Concat(view.AvatarRoot.GetComponentsInChildren<Renderer>(true));
            var legend=Content(view).Find("QuestControllerLegend");
            if(legend!=null&&legend.gameObject.activeInHierarchy)renderers=renderers.Concat(legend.GetComponentsInChildren<Renderer>(true));
            foreach(var renderer in renderers)
            {
                // Test the transformed local bounds. World AABB combinations can
                // invent a nearest/rightmost shoe corner that is not on a rotated
                // box and incorrectly reject an otherwise fully visible shoe.
                var bounds=renderer.localBounds;
                for(int x=-1;x<=1;x+=2)for(int y=-1;y<=1;y+=2)for(int z=-1;z<=1;z+=2)
                {
                    var p=camera.WorldToViewportPoint(renderer.transform.TransformPoint(bounds.center+Vector3.Scale(bounds.extents,new Vector3(x,y,z))));
                    Require(p.z>camera.nearClipPlane&&p.x>=0&&p.x<=1&&p.y>=0&&p.y<=1,"board/body/legend outside mono fixture view: "+renderer.name+" at "+p.ToString("F4"));
                }
            }
        }
        static void CheckActivePresentation(WorldSpaceExperimentView view)
        {
            var legend=Content(view).Find("QuestControllerLegend");var matrix=legend.localToWorldMatrix;
            foreach(var condition in new[]{Condition.A,Condition.B,Condition.C,Condition.D})
            {
                Apply(view,Phase.MainTask,condition,Colour.Green);view.Initialize(view.PresentationCamera,view.Role);
                Require(legend.gameObject.activeInHierarchy&&legend.localToWorldMatrix==matrix,"legend changed with A-D condition");
                Require(!Menu(view).Find("PhaseProgressAnimation").gameObject.activeInHierarchy,"phase animation is visible during main answering");
                foreach(var label in Menu(view).GetComponentsInChildren<TextMesh>(true))
                    Require(!label.gameObject.activeInHierarchy||!label.GetComponent<Renderer>().enabled||String.IsNullOrEmpty(label.text),"healthy active participant contains HUD text: "+label.name);
            }
            foreach(var phase in new[]{Phase.Practice,Phase.PracticeCorrection,Phase.Questionnaire,Phase.Paused,Phase.Rest})
            {
                Apply(view,phase,Condition.A,Colour.Green);view.Initialize(view.PresentationCamera,view.Role);
                Require(legend.gameObject.activeInHierarchy==(phase==Phase.Practice||phase==Phase.PracticeCorrection),"legend misleading in "+phase);
                if(phase==Phase.Practice)
                {
                    Require(!Menu(view).Find("PhaseProgressAnimation").gameObject.activeInHierarchy,"phase animation is visible during practice answering");
                    foreach(var label in Menu(view).GetComponentsInChildren<TextMesh>(true))
                        Require(!label.gameObject.activeInHierarchy||!label.GetComponent<Renderer>().enabled||String.IsNullOrEmpty(label.text),"healthy practice contains HUD text: "+label.name);
                }
                if(phase==Phase.PracticeCorrection||phase==Phase.Questionnaire||phase==Phase.Paused)
                {
                    var message=Menu(view).Find("ParticipantInstructions").GetComponent<TextMesh>();
                    Require(!String.IsNullOrWhiteSpace(message.text)&&message.GetComponent<Renderer>().enabled,"necessary instructions missing in "+phase);
                }
            }
        }
        static void CheckFixedMenuAndTransitions(WorldSpaceExperimentView view)
        {
            Require(view.CanShowWorldDiagnostics,"fixed menu cannot show diagnostics");
            view.PresentationCamera.enabled=false;Require(!view.CanShowWorldDiagnostics,"disabled camera suppresses error fallback");view.PresentationCamera.enabled=true;
            view.enabled=false;Require(!view.CanShowWorldDiagnostics,"disabled presentation suppresses error fallback");view.enabled=true;
            var menu=Menu(view);var legend=Content(view).Find("QuestControllerLegend");
            var menuMatrix=menu.localToWorldMatrix;var legendMatrix=legend.localToWorldMatrix;
            // Consecutive updates, without Initialize, model the real phase path
            // rather than relying on scene regeneration to restore visibility.
            foreach(var phase in new[]{Phase.Preparation,Phase.Practice,Phase.PracticeCorrection,Phase.Practice,
                Phase.MainTask,Phase.Paused,Phase.MainTask,Phase.Questionnaire,Phase.Rest,Phase.MainTask})
            {
                Apply(view,phase,Condition.A,Colour.Green);Layout(view);
                bool mapped=phase==Phase.Practice||phase==Phase.PracticeCorrection||phase==Phase.MainTask;
                Require(legend.gameObject.activeInHierarchy==mapped,"legend lost/misleading across transition to "+phase);
                Require(menu.localToWorldMatrix==menuMatrix&&legend.localToWorldMatrix==legendMatrix,"world panels moved across transition");
                bool clean=phase==Phase.Practice||phase==Phase.MainTask;
                Require(menu.Find("FixedWorldMenuBacking").gameObject.activeSelf==!clean,"normal menu backing leaks into task or disappears outside task");
            }
            Apply(view,Phase.Preparation,Condition.A,Colour.Green);Layout(view);
            var labels=menu.GetComponentsInChildren<TextMesh>(true).ToDictionary(label=>label,label=>label.transform.localToWorldMatrix);
            var camera=view.PresentationCamera;var position=camera.transform.position;var rotation=camera.transform.rotation;
            float fov=camera.fieldOfView,aspect=camera.aspect;
            camera.transform.position+=new Vector3(.25f,.12f,-.15f);camera.transform.rotation=Quaternion.Euler(-8,23,2);
            camera.fieldOfView=75;camera.aspect=1;
            view.SetHeadsetPreview("Head pose and Touch input available | avatar gaze: SIMULATED", "X red | Y green | A blue | B yellow\nLeft menu: pause; while paused, B: withdraw");
            Layout(view);
            Require(menu.localToWorldMatrix==menuMatrix&&legend.localToWorldMatrix==legendMatrix,"menu/legend follows camera movement");
            foreach(var label in labels)Require(label.Key.transform.localToWorldMatrix==label.Value,"menu label refits to camera: "+label.Key.name);
            camera.transform.SetPositionAndRotation(position,rotation);camera.fieldOfView=fov;camera.aspect=aspect;Layout(view);
        }
        static void CheckMenuReadable(WorldSpaceExperimentView view)
        {
            var menu=Menu(view);var backing=menu.Find("FixedWorldMenuBacking");
            Require(backing.gameObject.activeInHierarchy,"required menu has no contrast backing");
            var background=backing.GetComponent<Renderer>().sharedMaterial.color;
            Require(background.a==1&&background.maxColorComponent<.05f,"required menu background is not opaque dark");
            foreach(var label in menu.GetComponentsInChildren<TextMesh>(true))
            {
                var renderer=label.GetComponent<Renderer>();
                if(!renderer.enabled||!label.gameObject.activeInHierarchy||String.IsNullOrEmpty(label.text))continue;
                var bounds=renderer.localBounds;
                for(int x=-1;x<=1;x+=2)for(int y=-1;y<=1;y+=2)
                {
                    var world=label.transform.TransformPoint(bounds.center+Vector3.Scale(bounds.extents,new Vector3(x,y,0)));
                    var local=menu.InverseTransformPoint(world);
                    Require(Mathf.Abs(local.x)<WorldSpaceExperimentView.FixedMenuWidth*.5f&&Mathf.Abs(local.y)<WorldSpaceExperimentView.FixedMenuHeight*.5f,"menu text exceeds its own dark background: "+label.name);
                    var screen=view.PresentationCamera.WorldToViewportPoint(world);
                    Require(screen.z>0&&screen.x>=0&&screen.x<=1&&screen.y>=0&&screen.y<=1,"menu label outside mono fixture: "+label.name);
                }
            }
        }
        static string NormalizedText(string value)
        {return String.Join(" ",(value??"").Split(new[]{' ','\r','\n','\t'},StringSplitOptions.RemoveEmptyEntries));}
        static void CheckVisibleEnglishText(WorldSpaceExperimentView view)
        {
            // Check actual rendered strings, including the desktop camera-owned
            // warning surface. Source scans alone miss dynamically supplied text.
            var labels=view.GetComponentsInChildren<TextMesh>(true)
                .Concat(view.PresentationCamera.GetComponentsInChildren<TextMesh>(true)).Distinct();
            foreach(var label in labels)
            {
                if(!label.gameObject.activeInHierarchy||!label.GetComponent<Renderer>().enabled||String.IsNullOrEmpty(label.text))continue;
                Require(!label.text.Any(c=>(c>=0x2e80&&c<=0x9fff)||(c>=0xf900&&c<=0xfaff)||c==0xfffd),
                    "non-English localization or undecodable text is visible: "+label.name);
            }
        }
        static void CheckEnglishInstructionStates(WorldSpaceExperimentView view)
        {
            view.SetQuestionnaireLanguage("en");
            bool rejected=false;
            try{view.SetQuestionnaireLanguage("zh");}catch(ArgumentException){rejected=true;}
            Require(rejected,"legacy questionnaire language silently reinterpreted as English presentation");
            var compose=typeof(WorldSpaceExperimentView).GetMethod("ParticipantMessage",BindingFlags.NonPublic|BindingFlags.Instance);
            foreach(var phase in new[]{Phase.Preparation,Phase.Familiarization,Phase.PracticeCorrection,Phase.PracticeRetry,
                Phase.BlockPreparation,Phase.BlockReady,Phase.Countdown,Phase.Questionnaire,Phase.Rest,Phase.Paused,Phase.NeedsProtocolDecision,Phase.Ended})
            {
                var task=FixtureTask(phase,false,Colour.Green);
                view.ApplyTaskView(task);view.ApplyAvatarOutput(Output(view,Condition.A,phase));FixtureControls(view,phase,phase==Phase.Questionnaire?(int?)3:null);Layout(view);
                string expected=(string)compose.Invoke(view,new object[]{task});
                var shown=Menu(view).Find("ParticipantInstructions").GetComponent<TextMesh>();
                Require(NormalizedText(shown.text)==NormalizedText(expected),"English instructions clipped in "+phase);
                if(!String.IsNullOrEmpty(expected))Require(shown.GetComponent<Renderer>().enabled,"English instructions hidden in "+phase);
                CheckVisibleEnglishText(view);CheckMenuReadable(view);
            }
            var ready=FixtureTask(Phase.Rest,false,Colour.Green);Set(ready,"ReadyEnabled",true);
            view.ApplyTaskView(ready);FixtureControls(view,Phase.Rest);Layout(view);
            var readyMessage=Menu(view).Find("ParticipantInstructions").GetComponent<TextMesh>();
            Require(NormalizedText(readyMessage.text).Contains("Rest as long as you need."),"English ready/continue instruction clipped");
            Require(NormalizedText(Menu(view).Find("QuestPreviewInputStatus").GetComponent<TextMesh>().text).Contains("A: continue whenever you are ready. No minimum wait."),"rest does not offer immediate A continuation");
            CheckVisibleEnglishText(view);CheckMenuReadable(view);
            var retained=FixtureTask(Phase.BlockReady,false,Colour.Green);Set(retained,"SourceContinuationPending",true);
            view.ApplyTaskView(retained);Layout(view);
            string waiting=NormalizedText(Menu(view).Find("ParticipantInstructions").GetComponent<TextMesh>().text);
            Require(waiting.Contains("same confirmed source")&&waiting.Contains("No additional owner confirmation")&&waiting.Contains("owner recovery guidance"),
                "later block readiness asks for a new owner confirmation or implies automatic recovery");
        }
        static void CheckCompleteEnglishQuestionnaire(WorldSpaceExperimentView view,int item)
        {
            var label=Menu(view).Find("ParticipantInstructions").GetComponent<TextMesh>();
            string text=NormalizedText(label.text);
            Require(label.GetComponent<Renderer>().enabled,"English questionnaire is hidden");
            Require(view.QuestionnaireResponseVisible,"visible ready questionnaire is not available to the input presentation gate");
            Require(text.Contains(Questionnaire.Instruction("en")),"English questionnaire instruction truncated");
            Require(text.Contains(Questionnaire.Item(item,"en")),"English questionnaire item truncated: "+item);
            int previous=-1;
            for(int score=1;score<=5;score++)
            {
                int at=text.IndexOf(score+" "+Questionnaire.Anchor(score,"en"),StringComparison.Ordinal);
                Require(at>previous,"English questionnaire anchor missing, truncated or reordered: "+score);previous=at;
            }
            int lines=label.text.Split('\n').Length;
            Require(label.GetComponent<Renderer>().bounds.size.y/lines>=.040f,"questionnaire type is no longer enlarged/readable: "+item);
            CheckVisibleEnglishText(view);CheckMenuReadable(view);
        }
        static void CheckPhaseFlowFixtures(WorldSpaceExperimentView view,string output,List<string> files,Evidence evidence)
        {
            view.SetQuestionnaireLanguage("en");view.SetFaults(new string[0]);
            var menu=Menu(view);var animation=menu.Find("PhaseProgressAnimation");
            Require(animation!=null&&animation.parent==menu&&menu.parent==Content(view),"phase animation is not fixed to the classroom menu");
            var number=animation.Find("PhaseRemainingSeconds").GetComponent<TextMesh>();
            var fill=animation.Find("ProgressFill");var track=animation.Find("ProgressTrack");
            var guide=animation.Find("QuestionnaireControllerGuide");
            var sparks=animation.Find("GentleCompletionFireworks");
            var instructions=menu.Find("ParticipantInstructions").GetComponent<TextMesh>();
            Vector3 menuPosition=menu.position;Quaternion menuRotation=menu.rotation;
            foreach(int remaining in new[]{3,2,1})
            {
                view.SetPhaseProgress(remaining,3,0,30,0,3,false);Apply(view,Phase.Countdown,Condition.A,Colour.Green);Layout(view);
                Require(number.gameObject.activeInHierarchy&&number.text==remaining.ToString(),"countdown does not show actual remaining seconds");
                Require(Mathf.Abs(fill.localScale.x/track.localScale.x-remaining/3f)<.0001f,"countdown bar is not derived from remaining protocol time");
                Require(!guide.gameObject.activeInHierarchy&&!sparks.gameObject.activeInHierarchy&&!Stimulus(view).gameObject.activeInHierarchy,"countdown leaks rating guide, fireworks or stimulus");
                string name="synthetic-countdown-"+remaining+".png";Render(view,output,name);files.Add(name);CheckMenuReadable(view);
            }
            evidence.countdown_rendered_seconds=new[]{3,2,1};
            view.SetPhaseProgress(45,120,0,30,0,3,false);Apply(view,Phase.Countdown,Condition.A,Colour.Green);Layout(view);
            for(int repeat=0;repeat<5;repeat++)
            {
                Apply(view,Phase.Paused,Condition.A,Colour.Green);Layout(view);
                Require(!animation.gameObject.activeInHierarchy,"countdown progress leaks into pause");
                Apply(view,Phase.Countdown,Condition.A,Colour.Green);Layout(view);
                Require(number.text=="45"&&fill.gameObject.activeInHierarchy&&Mathf.Abs(fill.localScale.x/track.localScale.x-.375f)<.0001f,
                    "long countdown changed without protocol time or disappeared after pause");
            }
            string longCountdown="synthetic-countdown-long-paused-resumed.png";Render(view,output,longCountdown);files.Add(longCountdown);
            var captions=new[]{"LEFT STICK UP: 3 to 2","LEFT STICK DOWN: 2 to 3","PRESS A TO CONFIRM"};
            var slugs=new[]{"up","down","confirm-a"};
            var stick=guide.Find("AnimatedLeftStick");
            var guideStarted=typeof(WorldSpaceExperimentView).GetField("instructionAnimationStartedAt",BindingFlags.NonPublic|BindingFlags.Instance);
            var animateGuide=typeof(WorldSpaceExperimentView).GetMethod("LayoutInstructionDemo",BindingFlags.NonPublic|BindingFlags.Instance);
            var panel=new Rect(-WorldSpaceExperimentView.FixedMenuWidth*.5f,-WorldSpaceExperimentView.FixedMenuHeight*.5f,
                WorldSpaceExperimentView.FixedMenuWidth,WorldSpaceExperimentView.FixedMenuHeight);
            for(int step=0;step<3;step++)
            {
                view.SetPhaseProgress(0,3,0,30,0,3,false,true);
                guideStarted.SetValue(view,Time.realtimeSinceStartupAsDouble-(1+step*2));
                Apply(view,Phase.Questionnaire,Condition.A,Colour.Green);FixtureControls(view,Phase.Questionnaire,3);Layout(view);
                Require(guide.gameObject.activeInHierarchy&&!number.gameObject.activeInHierarchy&&!sparks.gameObject.activeInHierarchy,"questionnaire introduction presentation missing or leaked another phase");
                Require(view.QuestionnaireInstructionVisible&&!view.QuestionnaireResponseVisible,"introduction acknowledgement/rating visibility gates disagree");
                var caption=animation.Find("ControllerGuideStep").GetComponent<TextMesh>();
                Require(NormalizedText(caption.text).Contains(captions[step]),"guide timeline does not show UP, DOWN, A in order");
                Require(!instructions.text.Contains(Questionnaire.Item(4,"en"))&&!instructions.text.Contains(Questionnaire.Anchor(1,"en")),"rating choices shown before acknowledgement");
                string hint=menu.Find("QuestPreviewInputStatus").GetComponent<TextMesh>().text;
                Require(hint.Contains("Press A when ready")&&!hint.Contains("Selected:")&&!hint.Contains("opens in"),"introduction must allow A immediately without a timer or rating candidate");
                Require(instructions.text.Contains("UP lowers")&&instructions.text.Contains("DOWN raises")&&instructions.text.Contains("A confirms"),"guide changed or omitted rating input meaning");
                Require(step==0?stick.localPosition.y>.065f:step==1?stick.localPosition.y<.065f:Mathf.Abs(stick.localPosition.y-.065f)<.0001f,"animated stick direction disagrees with UP/decrement or DOWN/increment");
                animateGuide.Invoke(view,new object[]{panel,1.0+step*2});var matrix=stick.localToWorldMatrix;
                animateGuide.Invoke(view,new object[]{panel,121.0+step*2});Require(stick.localToWorldMatrix==matrix,"instruction demonstration does not loop after a long wait");
                Require(!view.AvatarRoot.gameObject.activeInHierarchy,"avatar visible in questionnaire introduction");
                string name="synthetic-questionnaire-guide-"+slugs[step]+".png";Render(view,output,name);files.Add(name);CheckMenuReadable(view);
            }
            evidence.questionnaire_guide_steps=new[]{"UP decrements","DOWN increments","A confirms"};
            // Zero instruction time never dismisses the pending introduction.
            guideStarted.SetValue(view,Time.realtimeSinceStartupAsDouble-301);
            for(int repeat=0;repeat<5;repeat++)
            {
                Apply(view,Phase.Paused,Condition.A,Colour.Green);Layout(view);
                Require(!animation.gameObject.activeInHierarchy&&!view.QuestionnaireInstructionVisible&&!view.QuestionnaireResponseVisible,"paused introduction still accepts input");
                Apply(view,Phase.Questionnaire,Condition.A,Colour.Green);Layout(view);
                Require(view.QuestionnaireInstructionVisible&&track.gameObject.activeInHierarchy&&fill.gameObject.activeInHierarchy,"long introduction expired or lost its bar after pause/resume");
            }
            string longGuide="synthetic-questionnaire-guide-after-300s.png";Render(view,output,longGuide);files.Add(longGuide);
            view.SetPhaseProgress(0,3,0,30,0,3,false,false);Apply(view,Phase.Questionnaire,Condition.A,Colour.Green);FixtureControls(view,Phase.Questionnaire,3);Layout(view);
            Require(!guide.gameObject.activeInHierarchy&&!view.QuestionnaireInstructionVisible,"instruction guide did not disappear after acknowledgement");CheckCompleteEnglishQuestionnaire(view,4);
            for(int answered=0;answered<Questionnaire.Count;answered++)
            {
                view.SetPhaseProgress(0,3,0,30,0,3,false,false,answered);
                var question=FixtureTask(Phase.Questionnaire,false,Colour.Green);Set(question,"QuestionnaireItem",Questionnaire.Item(answered,"en"));
                view.ApplyTaskView(question);Layout(view);CheckCompleteEnglishQuestionnaire(view,answered);
                Require(track.gameObject.activeInHierarchy&&fill.gameObject.activeInHierarchy&&Mathf.Abs(fill.localScale.x/track.localScale.x-answered/5f)<.0001f,
                    "questionnaire bar does not persist and reflect submitted answers");
                Require(animation.Find("ControllerGuideStep").GetComponent<TextMesh>().text==answered+" of 5 answers submitted","questionnaire progress description is inaccurate");
                string name="synthetic-questionnaire-progress-"+answered+".png";Render(view,output,name);files.Add(name);
                Apply(view,Phase.Paused,Condition.A,Colour.Green);Layout(view);
                view.ApplyTaskView(question);Layout(view);
                Require(view.QuestionnaireResponseVisible&&Mathf.Abs(fill.localScale.x/track.localScale.x-answered/5f)<.0001f,"questionnaire progress resets after pause");
            }
            foreach(int remaining in new[]{30,27,15,1,0})
            {
                view.SetPhaseProgress(0,3,remaining,30,0,3,false);
                var rest=FixtureTask(Phase.Rest,false,Colour.Green);Set(rest,"ReadyEnabled",true);
                view.ApplyTaskView(rest);view.ApplyAvatarOutput(Output(view,Condition.A,Phase.Rest));FixtureControls(view,Phase.Rest);Layout(view);
                Require(number.text==(30-remaining).ToString()+(remaining==0?"+":""),"rest does not show elapsed seconds within its optional reference");
                Require(view.RestResponseVisible&&!view.QuestionnaireResponseVisible,"rest instructions are unavailable to the input presentation gate");
                Require(!view.AvatarRoot.gameObject.activeInHierarchy&&!guide.gameObject.activeInHierarchy&&!sparks.gameObject.activeInHierarchy,"rest leaks avatar, controller guide or celebration");
                Require(track.gameObject.activeInHierarchy&&fill.gameObject.activeInHierarchy&&Mathf.Abs(fill.localScale.x/track.localScale.x-(1-remaining/30f))<.0001f,"rest progress does not show elapsed reference or disappears at completion");
                Require(instructions.text.Contains("Rest as long as you need.")&&NormalizedText(instructions.text).Contains("optional reference"),"rest wording implies a required wait");
                Require(NormalizedText(menu.Find("QuestPreviewInputStatus").GetComponent<TextMesh>().text).Contains("A: continue whenever you are ready. No minimum wait."),"rest A is not immediately available");
                string name="synthetic-rest-"+remaining+".png";Render(view,output,name);files.Add(name);CheckMenuReadable(view);
            }
            evidence.rest_rendered_seconds=new[]{30,27,15,1,0};
            for(int repeat=0;repeat<10;repeat++)
            {
                Apply(view,Phase.Paused,Condition.A,Colour.Green);Layout(view);
                Require(!animation.gameObject.activeInHierarchy&&!view.RestResponseVisible,"paused rest still accepts readiness");
                var rest=FixtureTask(Phase.Rest,false,Colour.Green);Set(rest,"ReadyEnabled",true);view.ApplyTaskView(rest);Layout(view);
                Require(fill.gameObject.activeInHierarchy&&fill.localScale.x==track.localScale.x&&number.text=="30+","rest loses its completed reference after repeated long/pause layouts");
            }
            // Exercise transparent ordering off axis; Render checks actual pixels.
            var cameraPosition=view.PresentationCamera.transform.position;var cameraRotation=view.PresentationCamera.transform.rotation;
            view.PresentationCamera.transform.position+=new Vector3(.3f,.1f,-.1f);
            view.PresentationCamera.transform.LookAt(menu.position);
            view.SetPhaseProgress(0,3,27,30,0,3,false);Layout(view);
            string oblique="synthetic-rest-short-fill-oblique.png";Render(view,output,oblique);files.Add(oblique);
            view.PresentationCamera.transform.SetPositionAndRotation(cameraPosition,cameraRotation);
            evidence.progress_fill_rendered_above_track=true;
            evidence.questionnaire_intro_waits_for_acknowledgement=true;
            evidence.rest_reference_persists_and_allows_immediate_continue=true;
            const string matchingDiagnostic="Head pose available; waiting for both Touch controllers";
            foreach(var phase in new[]{Phase.Countdown,Phase.Questionnaire,Phase.Rest})
            {
                view.SetPhaseProgress(2,3,15,30,0,3,false,true);Apply(view,phase,Condition.A,Colour.Green);
                view.SetHeadsetPreview(matchingDiagnostic,"");view.SetFaults(new[]{matchingDiagnostic,PreviewRecovery.Guidance});Layout(view);
                var fault=menu.Find("WorldSpaceFaultMessages").GetComponent<TextMesh>();
                string message=NormalizedText(fault.text);
                Require(fault.GetComponent<Renderer>().enabled&&message.Contains(matchingDiagnostic)&&message.Contains("NEW session")&&message.Contains("will not resume"),"phase panel hides duplicate tracking diagnostic or recovery guidance: "+phase);
                Require(!animation.gameObject.activeInHierarchy,"phase decoration remains visible over a required fault");
                Require(!view.QuestionnaireResponseVisible&&!view.QuestionnaireInstructionVisible,"hidden questionnaire accepts ratings or acknowledgement behind the fault-priority panel");
                Require(!view.RestResponseVisible,"hidden rest instructions accept Ready behind the fault-priority panel");
                CheckMenuReadable(view);
            }
            string faultName="synthetic-rest-tracking-recovery-fault.png";Render(view,output,faultName);files.Add(faultName);
            view.SetFaults(new string[0]);FixtureControls(view,Phase.Rest);
            Layout(view);Require(view.RestResponseVisible,"rest presentation gate does not recover after the fault is cleared");
            view.SetPhaseProgress(0,3,0,30,0,3,false);Apply(view,Phase.Questionnaire,Condition.A,Colour.Green);Layout(view);
            Require(view.QuestionnaireResponseVisible,"questionnaire presentation gate does not recover after the fault is cleared");
            view.SetPhaseProgress(0,3,0,30,0,3,true);Apply(view,Phase.Ended,Condition.A,Colour.Green);FixtureControls(view,Phase.Ended);Layout(view);
            Require(sparks.gameObject.activeInHierarchy&&instructions.text.Contains("Thank you for taking part."),"completed preview has no thank-you or gentle fireworks");
            Require(!track.gameObject.activeInHierarchy&&!number.gameObject.activeInHierarchy&&!view.AvatarRoot.gameObject.activeInHierarchy,"completion leaks a rest countdown or avatar");
            var animate=typeof(WorldSpaceExperimentView).GetMethod("LayoutCompletionFireworks",BindingFlags.NonPublic|BindingFlags.Instance);
            animate.Invoke(view,new object[]{true,1.0});var first=sparks.GetChild(0).localPosition;
            animate.Invoke(view,new object[]{true,1.5});Require(first!=sparks.GetChild(0).localPosition,"completion rays are static");
            foreach(var renderer in sparks.GetComponentsInChildren<Renderer>())
                Require(renderer.sharedMaterial.color.maxColorComponent<=.84f,"completion effect exceeds restrained brightness");
            animate.Invoke(view,new object[]{true,6.0});Require(!sparks.gameObject.activeInHierarchy,"completion fireworks do not stop after six seconds");
            string completedName="synthetic-completed-thank-you-fireworks.png";Render(view,output,completedName);files.Add(completedName);CheckMenuReadable(view);
            view.SetPhaseProgress(0,3,0,30,0,3,false);Apply(view,Phase.Ended,Condition.A,Colour.Green);FixtureControls(view,Phase.Ended);Layout(view);
            Require(!sparks.gameObject.activeInHierarchy&&!instructions.text.Contains("preview is complete"),"withdrawal/fault ending receives completion fireworks or completion wording");
            string withdrawnName="synthetic-withdrawn-no-celebration.png";Render(view,output,withdrawnName);files.Add(withdrawnName);CheckMenuReadable(view);
            // A stale caller completion flag is also insufficient outside Ended.
            view.SetPhaseProgress(0,3,0,30,0,3,true);Apply(view,Phase.NeedsProtocolDecision,Condition.A,Colour.Green);Layout(view);
            Require(!sparks.gameObject.activeInHierarchy,"fault/protocol-decision phase celebrates despite not being Ended");
            view.SetPhaseProgress(0,3,0,30,0,3,false);Apply(view,Phase.MainTask,Condition.D,Colour.Green);Layout(view);
            Require(!animation.gameObject.activeInHierarchy&&!view.AvatarRoot.gameObject.activeInHierarchy,"phase animation or avatar leaks into no-avatar D answering");
            Require(Near(menu.position,menuPosition)&&Quaternion.Angle(menu.rotation,menuRotation)<.0001f,"phase transition moved the fixed menu");
            evidence.completion_only_gentle_fireworks=true;evidence.phase_animation_world_fixed=true;
        }
        static void CheckFaultOcclusionGate(WorldSpaceExperimentView view)
        {
            Apply(view,Phase.MainTask,Condition.A,Colour.Green);
            var word=Stimulus(view);var fault=Menu(view).Find("WorldSpaceFaultMessages").GetComponent<TextMesh>();
            var gate=typeof(WorldSpaceExperimentView).GetMethod("CanAcknowledge",BindingFlags.NonPublic|BindingFlags.Instance);
            var covered=typeof(WorldSpaceExperimentView).GetField("faultCoversStimulus",BindingFlags.NonPublic|BindingFlags.Instance);
            var layout=typeof(WorldSpaceExperimentView).GetMethod("LayoutHud",BindingFlags.NonPublic|BindingFlags.Instance);
            view.SetFaults(new string[0]);view.SetPresentationAvailable(true);layout.Invoke(view,null);
            Require((bool)gate.Invoke(view,null),"fixture baseline presentation gate unexpectedly closed");
            // Deliberately fill the view in this synthetic fixture only, forcing
            // both safety slots to overlap. No render/ack callback is triggered.
            word.transform.localScale=Vector3.one*100;
            view.SetFaults(new[]{"ConnectionFault: participant link unavailable"});layout.Invoke(view,null);
            Require((bool)covered.GetValue(view)&&!(bool)gate.Invoke(view,null)&&fault.GetComponent<Renderer>().enabled,"obscuring fault is hidden or permits acknowledgement");
            view.SetFaults(new string[0]);word.transform.localScale=Vector3.one;layout.Invoke(view,null);
            Require(!(bool)covered.GetValue(view)&&(bool)gate.Invoke(view,null),"cleared fault leaves a stale presentation block");
            view.SetPresentationAvailable(false);
        }
        static void Render(WorldSpaceExperimentView view,string output,string name,bool requireUniform=false)
        {
            // Edit Mode does not run this behaviour's LateUpdate. Reinitialize
            // its idempotent presentation to apply the current HUD fit/guards
            // and dynamic font bindings before the explicit camera render.
            view.Initialize(view.PresentationCamera,view.Role);
            CheckVisibleEnglishText(view);
            var camera=view.PresentationCamera;var previous=RenderTexture.active;
            var target=new RenderTexture(1100,720,24,RenderTextureFormat.ARGB32);
            var image=new Texture2D(1100,720,TextureFormat.RGB24,false);
            try
            {
                camera.targetTexture=target;camera.Render();RenderTexture.active=target;
                image.ReadPixels(new Rect(0,0,1100,720),0,0);image.Apply();
                if(!requireUniform)CheckRenderedProgress(view,image);
                if(requireUniform)
                {
                    var pixels=image.GetPixels32();var first=pixels[0];
                    Require(pixels.All(pixel=>pixel.r==first.r&&pixel.g==first.g&&pixel.b==first.b),
                        "startup waiting fixture rendered room geometry instead of a uniform neutral view");
                }
                File.WriteAllBytes(Path.Combine(output,name),image.EncodeToPNG());
            }
            finally
            {
                camera.targetTexture=null;RenderTexture.active=previous;
                UnityEngine.Object.DestroyImmediate(image);UnityEngine.Object.DestroyImmediate(target);
            }
        }
        static float ColourDistance(Color a,Color b)
        {return Vector3.Distance(new Vector3(a.r,a.g,a.b),new Vector3(b.r,b.g,b.b));}
        static void CheckRenderedProgress(WorldSpaceExperimentView view,Texture2D image)
        {
            var menu=Menu(view);if(menu==null)return;
            var animation=menu.Find("PhaseProgressAnimation");
            if(animation==null||!animation.gameObject.activeInHierarchy)return;
            var fill=animation.Find("ProgressFill");var track=animation.Find("ProgressTrack");
            if(!fill.gameObject.activeInHierarchy||fill.localScale.x<.01f)return;
            var renderer=fill.GetComponent<Renderer>();var trackRenderer=track.GetComponent<Renderer>();
            Require(renderer.enabled&&renderer.sharedMaterial.renderQueue>trackRenderer.sharedMaterial.renderQueue,
                "progress fill can be hidden by transparent distance sorting");
            var viewport=view.PresentationCamera.WorldToViewportPoint(renderer.bounds.center);
            Require(viewport.z>view.PresentationCamera.nearClipPlane&&viewport.z<view.PresentationCamera.farClipPlane&&
                viewport.x>.01f&&viewport.x<.99f&&viewport.y>.01f&&viewport.y<.99f,"progress fill lies outside the camera clipping/viewport bounds");
            var pixel=image.GetPixel(Mathf.Clamp((int)(viewport.x*image.width),0,image.width-1),Mathf.Clamp((int)(viewport.y*image.height),0,image.height-1));
            var expected=renderer.sharedMaterial.color;var covered=trackRenderer.sharedMaterial.color;
            float fillDistance=Mathf.Min(ColourDistance(pixel,expected),ColourDistance(pixel,expected.gamma));
            float trackDistance=Mathf.Min(ColourDistance(pixel,covered),ColourDistance(pixel,covered.gamma));
            Require(fillDistance<.10f&&fillDistance<trackDistance,"progress renderer is enabled but its actual pixels are missing or covered: "+pixel);
        }
        static void CheckDesktopQuestionnaire(string output,List<string> files)
        {
            var view=NewFixture(Actor.Participant,false);view.SetQuestionnaireLanguage("en");
            view.SetPhaseProgress(0,3,0,30,0,3,false,true);
            Apply(view,Phase.Questionnaire,Condition.A,Colour.Green);Layout(view);
            var instructions=view.PresentationCamera.GetComponentsInChildren<TextMesh>(true).Single(label=>label.name=="ParticipantInstructions");
            Require(view.QuestionnaireInstructionVisible&&!view.QuestionnaireResponseVisible,"desktop introduction cannot be acknowledged or admits hidden ratings");
            string text=NormalizedText(instructions.text);
            Require(text.Contains("keys 1 to 5")&&text.Contains("Press Space when ready")&&!text.Contains("Selection opens after"),"desktop introduction omits its real input or requires a timed wait");
            for(int repeat=0;repeat<5;repeat++){Layout(view);Require(view.QuestionnaireInstructionVisible,"desktop introduction expires on repeated layouts");}
            string intro="synthetic-desktop-questionnaire-introduction.png";Render(view,output,intro);files.Add(intro);
            view.SetFaults(new[]{"Synthetic readiness fault"});Layout(view);
            Require(!view.QuestionnaireInstructionVisible&&!view.QuestionnaireResponseVisible,"desktop fault permits introduction/rating input");
            view.SetFaults(new string[0]);Apply(view,Phase.Paused,Condition.A,Colour.Green);Layout(view);
            Require(!view.QuestionnaireInstructionVisible&&!view.QuestionnaireResponseVisible,"desktop pause permits acknowledgement");
            Apply(view,Phase.Questionnaire,Condition.A,Colour.Green);Layout(view);Require(view.QuestionnaireInstructionVisible,"desktop intro does not recover after explicit resume");
            view.SetPhaseProgress(0,3,0,30,0,3,false,false);Layout(view);
            Require(!view.QuestionnaireInstructionVisible&&view.QuestionnaireResponseVisible,"desktop acknowledgement does not reveal rating input");
            Require(NormalizedText(instructions.text).Contains(Questionnaire.Item(4,"en")),"desktop acknowledgement fails to reveal the question");
            string ready="synthetic-desktop-questionnaire-ready.png";Render(view,output,ready);files.Add(ready);
        }
        static StartupHeightEvidence RenderStartupHeightFixtures(WorldSpaceExperimentView view,string output,List<string> files)
        {
            var camera=view.PresentationCamera;var savedParent=camera.transform.parent;
            var savedPosition=camera.transform.localPosition;var savedRotation=camera.transform.localRotation;
            int savedMask=camera.cullingMask;var savedFlags=camera.clearFlags;var savedBackground=camera.backgroundColor;
            var root=new GameObject("SyntheticRenderedStartupOrigin");var parent=new GameObject("SyntheticRenderedStartupOffset");
            var evidence=new StartupHeightEvidence{authored_device_offset=1.6f,raw_floor_height=1.6f,raw_head_euler=Vector3.zero};
            try
            {
                Apply(view,Phase.MainTask,Condition.A,Colour.Green);
                root.transform.position=new Vector3(WorldSpaceExperimentView.CenteredParticipantReference.x,0,WorldSpaceExperimentView.CenteredParticipantReference.z);
                parent.transform.SetParent(root.transform,false);parent.transform.localPosition=new Vector3(0,1.6f,0);
                camera.transform.SetParent(parent.transform,false);
                var rawPosition=new Vector3(0,1.6f,0);var rawRotation=Quaternion.identity;
                camera.transform.SetLocalPositionAndRotation(rawPosition,rawRotation);
                evidence.before_world_height=camera.transform.position.y;
                Require(Mathf.Abs(evidence.before_world_height-3.2f)<.0001f,"rendered Floor fixture does not reproduce double height");
                string before="synthetic-startup-floor-before-height-preparation.png";
                Render(view,output,before);files.Add(before);
                var placement=new QuestPreviewStartAlignment();var startup=new QuestPreviewStartupView(camera);
                Require(!placement.PrepareReferenceHeight(false,true,parent.transform,1.6f)&&!startup.TryReveal(false),"unknown reference mode revealed the fixture room");
                string waiting="synthetic-startup-waiting-reference-and-pose.png";
                Render(view,output,waiting,true);files.Add(waiting);evidence.waiting_render_uniform=true;
                Require(camera.cullingMask==0&&camera.transform.parent==parent.transform,"fixture initialization removed startup masking or tracking hierarchy");
                Require(placement.PrepareReferenceHeight(true,true,parent.transform,1.6f),"rendered Floor reference was not prepared before alignment");
                evidence.prepared_world_height=camera.transform.position.y;
                Require(Mathf.Abs(evidence.prepared_world_height-1.6f)<.0001f&&!placement.Applied&&!startup.Revealed,
                    "pre-focus height preparation changed startup readiness or retained double height");
                Require(placement.TryApply(true,root.transform,parent.transform,rawPosition,rawRotation,WorldSpaceExperimentView.CenteredParticipantReference)&&
                    !startup.TryReveal(false)&&startup.TryReveal(true),"rendered fixture failed one-shot placement and later reveal");
                evidence.render_settings_restored=camera.cullingMask==savedMask&&camera.clearFlags==savedFlags&&camera.backgroundColor.Equals(savedBackground);
                Require(evidence.render_settings_restored,"rendered startup fixture failed to restore its camera settings");
                string after="synthetic-startup-floor-after-height-preparation.png";
                Render(view,output,after);files.Add(after);
                evidence.raw_pose_unchanged=Near(camera.transform.localPosition,rawPosition)&&Quaternion.Angle(camera.transform.localRotation,rawRotation)<.001f;
                Require(evidence.raw_pose_unchanged&&camera.transform.parent==parent.transform,"before/after fixture changed raw head pose or hierarchy");
                var centre=camera.WorldToViewportPoint(view.TaskWorldPosition);
                Require(Mathf.Abs(centre.x-.5f)<.0001f&&centre.y>.45f&&centre.y<.55f,"prepared level Floor fixture does not face the board at measured height");
                return evidence;
            }
            finally
            {
                camera.transform.SetParent(savedParent,false);camera.transform.SetLocalPositionAndRotation(savedPosition,savedRotation);
                camera.cullingMask=savedMask;camera.clearFlags=savedFlags;camera.backgroundColor=savedBackground;
                UnityEngine.Object.DestroyImmediate(root);
            }
        }
        public static void RenderFixtures()
        {
            string project=Path.GetFullPath(Path.Combine(Application.dataPath,".."));
            string output=Environment.GetEnvironmentVariable("STROOP_CLASSROOM_EVIDENCE");
            if(!Application.isBatchMode||Application.isPlaying||!File.Exists(Path.Combine(project,"quest-preview-preparation.json")))
                throw new InvalidOperationException("Classroom fixtures require an explicit batch action in the marked isolated project, outside Play Mode.");
            if(String.IsNullOrWhiteSpace(output)||!Path.IsPathRooted(output))throw new ArgumentException("Explicit absolute fixture evidence path required.");
            output=Path.GetFullPath(output);string workspace=Path.GetDirectoryName(project)+Path.DirectorySeparatorChar;
            if(!output.StartsWith(workspace,StringComparison.OrdinalIgnoreCase)||output==project||output.StartsWith(project+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Fixture evidence must be a separate directory in this workspace.");
            if(SystemInfo.graphicsDeviceType==GraphicsDeviceType.Null||GraphicsSettings.currentRenderPipeline!=null)
                throw new InvalidOperationException("Actual fixture rendering requires graphics and the built-in pipeline; do not use -nographics.");
            Directory.CreateDirectory(output);checks=0;var files=new List<string>();
            CheckSavedScenes();
            var view=NewFixture(Actor.Participant);CheckMigration(view);CheckInitialAlignment(view);CheckStartupReferenceHeightAndView();CheckGeometry(view);CheckActivePresentation(view);CheckFixedMenuAndTransitions(view);CheckFaultOcclusionGate(view);CheckEnglishInstructionStates(view);
            Apply(view,Phase.MainTask,Condition.A,Colour.Green);
            CheckUnobstructed(view.PresentationCamera.transform.position,view.TaskWorldPosition,
                Content(view).Find("ClassroomEnvironment").GetComponentsInChildren<Renderer>(true)
                    .Concat(view.AvatarRoot.GetComponentsInChildren<Renderer>(true)),"participant word line is obstructed");
            var evidence=new Evidence{task_world=view.TaskWorldPosition,avatar_world=view.AvatarRoot.position,stimulus_character_size=Stimulus(view).characterSize};
            evidence.startup_reference_height=RenderStartupHeightFixtures(view,output,files);
            var colours=new List<string>();
            foreach(Colour ink in Enum.GetValues(typeof(Colour)))
            {
                Apply(view,Phase.MainTask,Condition.A,ink);
                var name="synthetic-participant-"+ink.ToString().ToLowerInvariant()+".png";
                Render(view,output,name);files.Add(name);colours.Add(Stimulus(view).color.ToString("F3"));
                CheckFraming(view);
                var word=Stimulus(view).GetComponent<Renderer>().bounds;
                var writing=Content(view).Find("ClassroomEnvironment/Whiteboard/WritingArea").GetComponent<Renderer>().bounds;
                Debug.Log("CLASSROOM_WORD_BOUNDS YELLOW size="+word.size.ToString("F4")+" characterSize="+Stimulus(view).characterSize.ToString("F6"));
                Require(word.min.x>=writing.min.x&&word.max.x<=writing.max.x&&word.min.y>=writing.min.y&&word.max.y<=writing.max.y,"word exceeds writing inset: "+word.size.ToString("F4"));
            }
            var words=new List<WordBounds>();
            foreach(Colour word in Enum.GetValues(typeof(Colour)))
            {
                view.ApplyTaskView(FixtureTask(Phase.MainTask,true,Colour.Green,false,word));
                string name="synthetic-word-"+word.ToString().ToLowerInvariant()+".png";
                Render(view,output,name);files.Add(name);
                var bounds=Stimulus(view).GetComponent<Renderer>().bounds;
                var writing=Content(view).Find("ClassroomEnvironment/Whiteboard/WritingArea").GetComponent<Renderer>().bounds;
                words.Add(new WordBounds{word=word.ToString(),world_size=bounds.size});
                Require(bounds.min.x>=writing.min.x&&bounds.max.x<=writing.max.x&&bounds.min.y>=writing.min.y&&bounds.max.y<=writing.max.y,"word exceeds writing inset: "+word);
                Require(Mathf.Approximately(Stimulus(view).characterSize,WorldSpaceExperimentView.StimulusCharacterSize)&&Near(Stimulus(view).transform.localScale,Vector3.one),"word-specific size changed");
            }
            Require(words.Max(word=>word.world_size.x)>=.90f&&words.Max(word=>word.world_size.x)<=.98f,"longest preview word must retain its measured writing-area margin");
            Apply(view,Phase.MainTask,Condition.C,Colour.Blue);Render(view,output,"synthetic-task-gaze.png");files.Add("synthetic-task-gaze.png");
            Apply(view,Phase.MainTask,Condition.D,Colour.Yellow);Render(view,output,"synthetic-no-avatar-d.png");files.Add("synthetic-no-avatar-d.png");
            Apply(view,Phase.Practice,Condition.A,Colour.Green);Render(view,output,"synthetic-practice.png");files.Add("synthetic-practice.png");
            Apply(view,Phase.MainTask,Condition.A,Colour.Green);
            string trackingFault="Head pose available; waiting for both Touch controllers";
            view.SetHeadsetPreview(trackingFault,"X red | Y green | A blue | B yellow");
            view.SetFaults(new[]{trackingFault,trackingFault,"ConnectionFault: participant link unavailable"});
            Render(view,output,"synthetic-active-fault.png");files.Add("synthetic-active-fault.png");
            var faultText=Menu(view).Find("WorldSpaceFaultMessages").GetComponent<TextMesh>();
            string visibleFault=String.Join(" ",faultText.text.Split(new[]{' ','\r','\n','\t'},StringSplitOptions.RemoveEmptyEntries));
            Require(faultText.GetComponent<Renderer>().enabled&&visibleFault.Contains("Touch controllers")&&visibleFault.Contains("ConnectionFault"),"active tracking/network faults hidden");
            Require(visibleFault.IndexOf("Touch controllers",StringComparison.Ordinal)==visibleFault.LastIndexOf("Touch controllers",StringComparison.Ordinal),"tracking fault duplicated");
            view.SetHeadsetPreview("Head pose and Touch input available | avatar gaze: SIMULATED","X red | Y green | A blue | B yellow");
            view.SetFaults(new[]{PreviewRecovery.Guidance});
            Render(view,output,"synthetic-connection-restart-guidance.png");files.Add("synthetic-connection-restart-guidance.png");
            string restartText=String.Join(" ",faultText.text.Split(new[]{' ','\r','\n','\t'},StringSplitOptions.RemoveEmptyEntries));
            Require(faultText.GetComponent<Renderer>().enabled&&restartText.Contains("NEW session")&&restartText.Contains("will not resume"),"explicit new-session restart guidance is hidden or incomplete");
            view.SetFaults(new string[0]);view.SetHeadsetPreview("Head pose and Touch input available | avatar gaze: SIMULATED","X red | Y green | A blue | B yellow");
            Apply(view,Phase.Preparation,Condition.A,Colour.Green);Render(view,output,"synthetic-preparation.png");files.Add("synthetic-preparation.png");
            CheckMenuReadable(view);
            var fixtureRotation=view.PresentationCamera.transform.rotation;
            view.PresentationCamera.transform.rotation=Quaternion.identity;
            var centred=view.PresentationCamera.WorldToViewportPoint(view.TaskWorldPosition);
            Require(Mathf.Abs(centred.x-.5f)<.0001f,"level centred start does not face whiteboard centre");
            Render(view,output,"synthetic-centred-forward-preparation.png");files.Add("synthetic-centred-forward-preparation.png");
            Apply(view,Phase.MainTask,Condition.A,Colour.Green);
            Render(view,output,"synthetic-centred-forward-main.png");files.Add("synthetic-centred-forward-main.png");
            view.PresentationCamera.transform.rotation=fixtureRotation;
            var router=new QuestPreviewInputRouter();var frame=router.Update(Phase.Questionnaire,4,Stick(0));
            var candidates=new List<int>{frame.CandidateRating.Value};
            foreach(var phase in new[]{Phase.PracticeCorrection,Phase.Questionnaire,Phase.Paused})
            {
                Apply(view,phase,Condition.A,Colour.Green);
                FixtureControls(view,phase,phase==Phase.Questionnaire?frame.CandidateRating:null);
                var name="synthetic-"+phase.ToString().ToLowerInvariant()+".png";
                Render(view,output,name);files.Add(name);CheckMenuReadable(view);
            }
            Apply(view,Phase.Questionnaire,Condition.A,Colour.Green);
            frame=router.Update(Phase.Questionnaire,4,Stick(1));
            Require(frame.CandidateRating==2,"UP must move toward visually higher option 2 from 3");
            FixtureControls(view,Phase.Questionnaire,frame.CandidateRating);Render(view,output,"synthetic-questionnaire-up-2.png");files.Add("synthetic-questionnaire-up-2.png");candidates.Add(frame.CandidateRating.Value);
            router.Update(Phase.Questionnaire,4,Stick(0));router.Update(Phase.Questionnaire,4,Stick(-1));
            router.Update(Phase.Questionnaire,4,Stick(0));frame=router.Update(Phase.Questionnaire,4,Stick(-1));
            Require(frame.CandidateRating==4,"DOWN must move toward visually lower option 4 from 3");
            FixtureControls(view,Phase.Questionnaire,frame.CandidateRating);Render(view,output,"synthetic-questionnaire-down-4.png");files.Add("synthetic-questionnaire-down-4.png");candidates.Add(frame.CandidateRating.Value);
            var instruction=Menu(view).Find("ParticipantInstructions").GetComponent<TextMesh>().text;
            int previous=-1;for(int score=1;score<=5;score++)
            {int at=instruction.IndexOf("\n"+score+" ",StringComparison.Ordinal);Require(at>previous,"rating anchors are not ordered top-to-bottom 1 through 5");previous=at;}
            var control=Menu(view).Find("QuestPreviewInputStatus").GetComponent<TextMesh>();
            string shown=String.Join(" ",control.text.Split(new[]{' ','\r','\n','\t'},StringSplitOptions.RemoveEmptyEntries));
            Require(shown.Contains("Selected: 4")&&shown.Contains("A: confirm")&&shown.Contains("UP: lower number")&&shown.Contains("DOWN: higher number"),"questionnaire candidate/control hint missing");
            evidence.questionnaire_rendered_candidates=candidates.ToArray();
            var renderedItems=new List<int>();
            for(int item=0;item<Questionnaire.Count;item++)
            {
                var task=FixtureTask(Phase.Questionnaire,false,Colour.Green);
                Set(task,"QuestionnaireItem",Questionnaire.Item(item,"en"));
                view.ApplyTaskView(task);FixtureControls(view,Phase.Questionnaire,3);
                string name="synthetic-english-questionnaire-item-"+(item+1)+".png";
                Render(view,output,name);files.Add(name);CheckCompleteEnglishQuestionnaire(view,item);renderedItems.Add(item);
            }
            evidence.questionnaire_rendered_items=renderedItems.ToArray();
            CheckPhaseFlowFixtures(view,output,files,evidence);
            CheckDesktopQuestionnaire(output,files);
            view=NewFixture(Actor.Operator);ValidateView(view);Apply(view,Phase.MainTask,Condition.A,Colour.Green);CheckFraming(view);
            Render(view,output,"synthetic-operator.png");files.Add("synthetic-operator.png");
            evidence.checks=checks;evidence.images=files.ToArray();evidence.ink_rgba=colours.ToArray();evidence.words=words.ToArray();
            File.WriteAllText(Path.Combine(output,"classroom-fixture-evidence.json"),JsonUtility.ToJson(evidence,true)+"\n");
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
            Debug.Log("CLASSROOM_FIXTURES_COMPLETE: "+checks+" checks; synthetic Editor images only; no XR/player/protocol run.");
        }
    }
}
