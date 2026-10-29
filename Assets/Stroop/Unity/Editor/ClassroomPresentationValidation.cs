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
            public bool active_participant_view_has_only_world_content_and_real_faults=true;
            public bool quest_menu_world_fixed=true;
            public Vector3 menu_world=WorldSpaceExperimentView.FixedMenuPosition;
            public Vector2 menu_size=new Vector2(WorldSpaceExperimentView.FixedMenuWidth,WorldSpaceExperimentView.FixedMenuHeight);
            public int[] questionnaire_rendered_candidates;
            public string[] ink_rgba;
            public WordBounds[] words;
        }
        [Serializable] sealed class WordBounds {public string word;public Vector3 world_size;}
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
                phase==Phase.PracticeCorrection||phase==Phase.Rest?"Right trigger: Continue / Ready when available\nLeft menu: pause":
                "X red | Y green | A blue | B yellow\nRelease before the next answer | Left menu: pause";
            view.SetHeadsetPreview("Head pose and Touch input available | avatar gaze: SIMULATED",hint);
        }

        public static void ValidateView(WorldSpaceExperimentView view)
        {
            Require(view!=null&&view.PresentationCamera!=null,"presentation/camera missing");
            var content=Content(view);var word=Stimulus(view);
            Require(content!=null&&word!=null,"world stimulus missing");
            Require(Near(view.TaskWorldPosition,new Vector3(-.5f,1.65f,2)),"task anchor changed");
            Require(Near(view.AvatarRoot.position,new Vector3(.65f,.9f,1.9f)),"standing avatar anchor changed");
            Require(Quaternion.Angle(view.AvatarRoot.localRotation,Quaternion.Euler(0,225,0))<.001f,"standing body orientation changed");
            Require(Near(view.AvatarHead.localPosition,new Vector3(0,.7f,0)),"head offset changed");
            Require(Near(view.AvatarRoot.localScale,Vector3.one),"avatar scale changed");
            Require(Mathf.Approximately(word.characterSize,WorldSpaceExperimentView.StimulusCharacterSize)&&word.fontSize==100&&Near(word.transform.localScale,Vector3.one),"fixed stimulus typography changed");
            Require(word.GetComponent<StimulusRenderWitness>()!=null,"actual stimulus witness missing");
            Require(word.transform.parent==content,"stimulus must remain world space, not HUD");
            var board=content.Find("ClassroomEnvironment/Whiteboard");
            Require(board!=null&&Near(board.position,new Vector3(-.5f,1.65f,2.04f)),"board position mismatch");
            var writing=board.Find("WritingArea").GetComponent<Renderer>();
            Require(writing.bounds.min.z>view.TaskWorldPosition.z+.01f,"writing surface occludes word plane");
            Require(writing.bounds.center.x==view.TaskWorldPosition.x&&Mathf.Approximately(writing.bounds.center.y,view.TaskWorldPosition.y),"writing area not centred on word");
            Require(board.Find("OffWhiteFace")!=null,"whiteboard face missing");
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
                var backing=menu.Find("FixedWorldMenuBacking");
                Require(backing!=null&&backing.GetComponent<Renderer>().sharedMaterial.color.a==1,"opaque fixed menu backing missing");
                var legend=content.Find("QuestControllerLegend");
                Require(legend!=null&&legend.parent==content&&Near(legend.position,new Vector3(-1.60f,1.55f,2.04f)),"controller legend is not fixed world space left of board");
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
            if(phase==Phase.PracticeCorrection)Set(result,"PracticeCorrection","Respond to the ink colour, not the word. Use the button guide on the left.");
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
            return DevelopmentGeometry.FromLocalDirections(Vec(root.InverseTransformDirection(view.PresentationCamera.transform.position-head)),
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
                Vector3 target=condition==Condition.A?view.PresentationCamera.transform.position:view.TaskWorldPosition;
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
        static WorldSpaceExperimentView NewFixture(Actor role)
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
            var camera=new GameObject("SyntheticFixtureCamera").AddComponent<Camera>();
            camera.transform.position=new Vector3(0,1.6f,0);camera.fieldOfView=60;
            camera.nearClipPlane=.05f;camera.farClipPlane=20;camera.aspect=1100f/720;
            camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=new Color(.035f,.045f,.06f);
            camera.stereoTargetEye=StereoTargetEyeMask.None;
            var view=new GameObject("SyntheticFixturePresentation").AddComponent<WorldSpaceExperimentView>();
            view.Initialize(camera,role);view.SetPresentationAvailable(false);
            if(role==Actor.Participant)
                view.SetHeadsetPreview("Head pose and Touch input available | avatar gaze: SIMULATED", "X red | Y green | A blue | B yellow\nLeft menu: pause; while paused, B: withdraw");
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
                foreach(var label in Menu(view).GetComponentsInChildren<TextMesh>(true))
                    Require(!label.GetComponent<Renderer>().enabled||String.IsNullOrEmpty(label.text),"healthy active participant contains HUD text: "+label.name);
            }
            foreach(var phase in new[]{Phase.Practice,Phase.PracticeCorrection,Phase.Questionnaire,Phase.Paused,Phase.Rest})
            {
                Apply(view,phase,Condition.A,Colour.Green);view.Initialize(view.PresentationCamera,view.Role);
                Require(legend.gameObject.activeInHierarchy==(phase==Phase.Practice||phase==Phase.PracticeCorrection),"legend misleading in "+phase);
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
        static void Render(WorldSpaceExperimentView view,string output,string name)
        {
            // Edit Mode does not run this behaviour's LateUpdate. Reinitialize
            // its idempotent presentation to apply the current HUD fit/guards
            // and dynamic font bindings before the explicit camera render.
            view.Initialize(view.PresentationCamera,view.Role);
            var camera=view.PresentationCamera;var previous=RenderTexture.active;
            var target=new RenderTexture(1100,720,24,RenderTextureFormat.ARGB32);
            var image=new Texture2D(1100,720,TextureFormat.RGB24,false);
            try
            {
                camera.targetTexture=target;camera.Render();RenderTexture.active=target;
                image.ReadPixels(new Rect(0,0,1100,720),0,0);image.Apply();
                File.WriteAllBytes(Path.Combine(output,name),image.EncodeToPNG());
            }
            finally
            {
                camera.targetTexture=null;RenderTexture.active=previous;
                UnityEngine.Object.DestroyImmediate(image);UnityEngine.Object.DestroyImmediate(target);
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
            var view=NewFixture(Actor.Participant);CheckMigration(view);CheckGeometry(view);CheckActivePresentation(view);CheckFixedMenuAndTransitions(view);CheckFaultOcclusionGate(view);
            Apply(view,Phase.MainTask,Condition.A,Colour.Green);
            CheckUnobstructed(view.PresentationCamera.transform.position,view.TaskWorldPosition,
                Content(view).Find("ClassroomEnvironment").GetComponentsInChildren<Renderer>(true)
                    .Concat(view.AvatarRoot.GetComponentsInChildren<Renderer>(true)),"participant word line is obstructed");
            var evidence=new Evidence{task_world=view.TaskWorldPosition,avatar_world=view.AvatarRoot.position,stimulus_character_size=Stimulus(view).characterSize};
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
            view.SetFaults(new string[0]);view.SetHeadsetPreview("Head pose and Touch input available | avatar gaze: SIMULATED","X red | Y green | A blue | B yellow");
            Apply(view,Phase.Preparation,Condition.A,Colour.Green);Render(view,output,"synthetic-preparation.png");files.Add("synthetic-preparation.png");
            CheckMenuReadable(view);
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
            view=NewFixture(Actor.Operator);ValidateView(view);Apply(view,Phase.MainTask,Condition.A,Colour.Green);CheckFraming(view);
            Render(view,output,"synthetic-operator.png");files.Add("synthetic-operator.png");
            evidence.checks=checks;evidence.images=files.ToArray();evidence.ink_rgba=colours.ToArray();evidence.words=words.ToArray();
            File.WriteAllText(Path.Combine(output,"classroom-fixture-evidence.json"),JsonUtility.ToJson(evidence,true)+"\n");
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
            Debug.Log("CLASSROOM_FIXTURES_COMPLETE: "+checks+" checks; synthetic Editor images only; no XR/player/protocol run.");
        }
    }
}
