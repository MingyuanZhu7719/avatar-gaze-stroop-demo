#if UNITY_EDITOR && STROOP_XR_PREVIEW
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using AvatarStroop.Core;
using AvatarStroop.Runtime;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace AvatarStroop.Unity.Editor
{
    // Isolated Editor-only test using one actual development participant and
    // renderer. It does not start XR, an eye worker, a socket, or a recorder.
    // All input, clock, geometry, and optional images are synthetic.
    public static class ViveNamedAutomaticPresentationFixture
    {
        [Serializable] sealed class Preparation {public string source=null;}
        sealed class FixtureClock:IClock
        {public double Value;public double Now {get{return Value;}}public string ClockId {get{return "synthetic-named-editor-clock";}}}
        static void Require(bool value,string issue)
        {if(!value)throw new InvalidOperationException("Named Automatic synthetic fixture: "+issue);}
        static Dictionary<ViveTrackpadDirection,Colour> Mapping()
        {return new Dictionary<ViveTrackpadDirection,Colour>{{ViveTrackpadDirection.Up,Colour.Red},{ViveTrackpadDirection.Right,Colour.Green},
            {ViveTrackpadDirection.Down,Colour.Blue},{ViveTrackpadDirection.Left,Colour.Yellow}};}
        static VivePreviewTrackpadSample Input(bool down,Colour colour)
        {
            double x=colour==Colour.Green?1:colour==Colour.Yellow?-1:0;
            double y=colour==Colour.Red?1:colour==Colour.Blue?-1:0;
            return new VivePreviewTrackpadSample(true,down,x,y);
        }
        static void Neutral(ViveDevelopmentParticipant task)
        {task.UpdateTrackpad(true,Input(false,Colour.Red));}
        static void Layout(WorldSpaceExperimentView view)
        {
            var method=typeof(WorldSpaceExperimentView).GetMethod("LayoutHud",BindingFlags.Instance|BindingFlags.NonPublic);
            Require(method!=null,"HUD layout method is unavailable");method.Invoke(view,null);
        }
        static Quaternion UnityRotation(Rotation value)
        {return new Quaternion((float)value.X,(float)value.Y,(float)value.Z,(float)value.W);}
        static Vector3 UnityVector(Vec3 value){return new Vector3((float)value.X,(float)value.Y,(float)value.Z);}
        static void ComparePose(AvatarPose actual,Quaternion expected,string label)
        {
            Quaternion applied=UnityRotation(actual.HeadRotation).normalized;
            Require(Mathf.Abs(Quaternion.Dot(applied,expected.normalized))>0.99999f,label+" head differs from expected orientation");
            var forward=(applied*Vector3.forward).normalized;var eye=UnityVector(actual.EyeDirection).normalized;
            Require(Vector3.Dot(forward,eye)>0.99999f,label+" head and eye point in different root-local directions");
        }
        static void CompareBothEyes(WorldSpaceExperimentView view,string label)
        {
            var head=view.AvatarHead;var left=head.Find("LeftEye");var right=head.Find("RightEye");
            Require(left!=null&&right!=null,label+" eye transforms unavailable");
            var forward=(head.localRotation*Vector3.forward).normalized;
            var leftDirection=(head.localRotation*(left.localRotation*Vector3.forward)).normalized;
            var rightDirection=(head.localRotation*(right.localRotation*Vector3.forward)).normalized;
            Require(Vector3.Dot(forward,leftDirection)>0.99999f&&Vector3.Dot(forward,rightDirection)>0.99999f,
                label+" rendered eye transforms do not follow head forward");
        }
        static Color Ink(Colour value)
        {
            switch(value){case Colour.Red:return Color.red;case Colour.Green:return Color.green;
                case Colour.Blue:return Color.blue;case Colour.Yellow:return Color.yellow;default:throw new ArgumentOutOfRangeException("value");}
        }
        static TextMesh CheckStimulus(WorldSpaceExperimentView view,TrialRecord trial,string label)
        {
            Require(trial!=null&&trial.Stimulus!=null,label+" current synthetic trial is missing");
            var target=view.transform.Find("WorldSpaceContent/Stimulus");
            var text=target==null?null:target.GetComponent<TextMesh>();var renderer=target==null?null:target.GetComponent<Renderer>();
            Require(text!=null&&renderer!=null&&target.gameObject.activeInHierarchy&&renderer.enabled,label+" stimulus is not visible");
            Require(text.text==trial.Stimulus.Word.ToString().ToUpperInvariant(),label+" displayed word differs from the authoritative trial");
            Require(text.color==Ink(trial.Stimulus.Ink),label+" displayed colour differs from the authoritative trial");
            return text;
        }
        static bool PixelMatches(Color32 pixel,Colour ink)
        {
            if(ink==Colour.Red)return pixel.r>160&&pixel.g<100&&pixel.b<100;
            if(ink==Colour.Green)return pixel.g>160&&pixel.r<100&&pixel.b<100;
            if(ink==Colour.Blue)return pixel.b>160&&pixel.r<100&&pixel.g<100;
            return pixel.r>160&&pixel.g>160&&pixel.b<100;
        }
        static void CheckEvent(ViveDevelopmentParticipant task,string kind,double active,ViveNamedOptionProvenance expected)
        {
            JournalEvent found=null;foreach(var item in task.ReadEventsAfter(-1,4096))if(item.Kind==kind)found=item;
            Require(found!=null&&object.ReferenceEquals(found.NamedOption,expected),kind+" has no matching typed provenance");
            string field;Require(found.Fields.TryGetValue("named_schema",out field)&&field==ViveNamedOptionProvenance.SchemaVersion,kind+" schema differs");
            Require(found.Fields.TryGetValue("named_option",out field)&&field==expected.Option.ToString(),kind+" option differs");
            Require(found.Fields.TryGetValue("named_schedule",out field)&&field==expected.ScheduleVersion,kind+" schedule differs");
            Require(found.Fields.TryGetValue("named_geometry",out field)&&field==expected.GeometryVersion,kind+" geometry differs");
            double observed;Require(found.Fields.TryGetValue("named_active_seconds",out field)&&
                Double.TryParse(field,NumberStyles.Float,CultureInfo.InvariantCulture,out observed)&&Math.Abs(observed-active)<1e-9,
                kind+" was not recorded at the same active time");
            Require(found.ClockId=="synthetic-named-editor-clock",kind+" clock differs");
        }
        // The optional output must be a new local folder under the explicit
        // workspace and outside the source and isolated Unity project. The
        // fixture never overwrites an existing image or accepts a linked path.
        static string NewOutput()
        {
            string value=Environment.GetEnvironmentVariable("STROOP_VIVE_NAMED_FIXTURE_OUTPUT");
            string workspace=Environment.GetEnvironmentVariable("STROOP_VIVE_FIXTURE_WORKSPACE");
            string project=Environment.GetEnvironmentVariable("STROOP_VIVE_FIXTURE_PROJECT");
            Require(!String.IsNullOrWhiteSpace(value)&&Path.IsPathRooted(value),"an explicit output path is required for images");
            string output=Path.GetFullPath(value).TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar);
            string root=Path.GetFullPath(workspace).TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar);
            string isolated=Path.GetFullPath(Path.Combine(Application.dataPath,".."));
            var preparation=JsonUtility.FromJson<Preparation>(File.ReadAllText(Path.Combine(isolated,"quest-preview-preparation.json")));
            Require(preparation!=null&&!String.IsNullOrWhiteSpace(preparation.source)&&Path.IsPathRooted(preparation.source),"source marker unavailable");
            string source=Path.GetFullPath(preparation.source).TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar);
            string projectPath=Path.GetFullPath(project).TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar);
            Require(output.StartsWith(root+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)&&
                !output.StartsWith(source+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)&&
                !source.StartsWith(output+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)&&
                !String.Equals(output,source,StringComparison.OrdinalIgnoreCase)&&
                !output.StartsWith(projectPath+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)&&
                !projectPath.StartsWith(output+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)&&
                !String.Equals(output,projectPath,StringComparison.OrdinalIgnoreCase),"output must be separate under workspace");
            Require(!Directory.Exists(output)&&!File.Exists(output),"output already exists");
            var parent=new DirectoryInfo(Path.GetDirectoryName(output));
            while(parent!=null){Require(parent.Exists&&(parent.Attributes&FileAttributes.ReparsePoint)==0,"output path contains a link or missing parent");parent=parent.Parent;}
            Directory.CreateDirectory(output);return output;
        }
        static void Render(Camera camera,string directory,string design,string label,bool avatarExpected,TextMesh stimulus,Colour? ink)
        {
            var render=new RenderTexture(1280,720,24,RenderTextureFormat.ARGB32);
            var prior=RenderTexture.active;var target=camera.targetTexture;
            try
            {
                camera.targetTexture=render;camera.Render();camera.Render();RenderTexture.active=render;
                var image=new Texture2D(1280,720,TextureFormat.RGB24,false);
                try
                {
                    image.ReadPixels(new Rect(0,0,1280,720),0,0);image.Apply();
                    int shirt=0;var pixels=image.GetPixels32();
                    for(int y=0;y<720;y++)for(int x=880;x<1280;x++)
                    {var p=pixels[y*1280+x];if(p.r>=45&&p.r<=90&&p.g>=85&&p.g<=140&&p.b>=110&&p.b<=170&&p.g>p.r+20&&p.b>p.g+8)shirt++;}
                    Require(avatarExpected?shirt>=100:shirt==0,label+" synthetic avatar pixels do not match visibility");
                    if(stimulus!=null&&ink.HasValue)
                    {
                        var bounds=stimulus.GetComponent<Renderer>().bounds;float minX=1,minY=1,maxX=0,maxY=0;
                        for(int x=-1;x<=1;x+=2)for(int y=-1;y<=1;y+=2)for(int z=-1;z<=1;z+=2)
                        {
                            Vector3 point=bounds.center+Vector3.Scale(bounds.extents,new Vector3(x,y,z));
                            Vector3 projected=camera.WorldToViewportPoint(point);Require(projected.z>0,label+" stimulus behind camera");
                            minX=Mathf.Min(minX,projected.x);maxX=Mathf.Max(maxX,projected.x);
                            minY=Mathf.Min(minY,projected.y);maxY=Mathf.Max(maxY,projected.y);
                        }
                        Require(minX>=0&&maxX<=1&&minY>=0&&maxY<=1&&maxX>minX&&maxY>minY,label+" stimulus outside synthetic frame");
                        int coloured=0;
                        for(int y=Mathf.Max(0,Mathf.FloorToInt(minY*720));y<Mathf.Min(720,Mathf.CeilToInt(maxY*720));y++)
                            for(int x=Mathf.Max(0,Mathf.FloorToInt(minX*1280));x<Mathf.Min(1280,Mathf.CeilToInt(maxX*1280));x++)
                                if(PixelMatches(pixels[y*1280+x],ink.Value))coloured++;
                        Require(coloured>=10,label+" authoritative ink did not render inside the stimulus bounds");
                    }
                    string path=Path.Combine(directory,"synthetic-named-"+design+"-"+label+".png");
                    using(var output=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None))
                    {var bytes=image.EncodeToPNG();output.Write(bytes,0,bytes.Length);}
                }
                finally{UnityEngine.Object.DestroyImmediate(image);}
            }
            finally{camera.targetTexture=target;RenderTexture.active=prior;render.Release();UnityEngine.Object.DestroyImmediate(render);}
        }
        public static void Validate(){Run(false);}
        public static void RenderSynthetic(){Run(true);}
        // Synthetic plan order is exclusively a test fixture, not a proposed
        // study allocation. No XR, network, eye data or file recording starts.
        public static void ValidateMultiBlock()
        {
            ViveSyntheticFixtureIsolation.OpenKnownSavedAnchor();
            Scene previous=SceneManager.GetActiveScene(),fixture=default(Scene);bool opened=false;
            GameObject cameraObject=null,viewObject=null;var anchorRenderers=new List<KeyValuePair<Renderer,bool>>();
            try
            {
                foreach(var root in previous.GetRootGameObjects())foreach(var renderer in root.GetComponentsInChildren<Renderer>(true))
                {anchorRenderers.Add(new KeyValuePair<Renderer,bool>(renderer,renderer.enabled));renderer.enabled=false;}
                fixture=EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Additive);
                opened=fixture.IsValid()&&fixture.isLoaded;Require(opened,"could not create isolated multi-block scene");
                cameraObject=new GameObject("SyntheticNamedPlanCamera");SceneManager.MoveGameObjectToScene(cameraObject,fixture);
                Require(cameraObject.scene==fixture,"multi-block camera not in fixture scene");cameraObject.hideFlags=HideFlags.HideAndDontSave;
                var camera=cameraObject.AddComponent<Camera>();camera.transform.SetPositionAndRotation(WorldSpaceExperimentView.CenteredParticipantReference,Quaternion.identity);
                camera.fieldOfView=WorldSpaceExperimentView.DesktopVerticalFieldOfView;
                viewObject=new GameObject("SyntheticNamedPlanView");SceneManager.MoveGameObjectToScene(viewObject,fixture);
                Require(viewObject.scene==fixture,"multi-block view not in fixture scene");viewObject.hideFlags=HideFlags.HideAndDontSave;
                var view=viewObject.AddComponent<WorldSpaceExperimentView>();view.Initialize(camera,Actor.Participant);
                view.SetQuestionnaireLanguage("en");
                var mapping=Mapping();view.ConfigureViveDevelopmentPreview(mapping,.5);view.SetPresentationAvailable(true);
                view.SetViveDevelopmentStatus("Synthetic named plan test; no research data or durable recording.","Synthetic controls only");
                var avatarRoot=view.AvatarRoot;var head=view.AvatarHead.position;
                Vector3 participant=avatarRoot.InverseTransformDirection(view.ParticipantReferencePosition-head);
                Vector3 board=avatarRoot.InverseTransformDirection(view.TaskWorldPosition-head);
                var geometry=DevelopmentGeometry.FromLocalDirections(new Vec3(participant.x,participant.y,participant.z),
                    new Vec3(board.x,board.y,board.z),"synthetic-named-editor-geometry");
                foreach(int count in new[]{1,4})
                    ValidateSyntheticPlan(view,geometry,mapping,count);
                Debug.Log("VIVE_NAMED_MULTIBLOCK_SYNTHETIC_FIXTURE_PASSED: one- and four-block plans, actual simulation participant and renderer, rest/readiness, task clock and typed provenance; no XR, hardware or real data.");
            }
            finally
            {
                foreach(var entry in anchorRenderers)if(entry.Key!=null)entry.Key.enabled=entry.Value;
                if(viewObject!=null)UnityEngine.Object.DestroyImmediate(viewObject);if(cameraObject!=null)UnityEngine.Object.DestroyImmediate(cameraObject);
                if(previous.IsValid()&&previous.isLoaded&&SceneManager.GetActiveScene()!=previous)SceneManager.SetActiveScene(previous);
                if(opened&&fixture.IsValid()&&fixture.isLoaded)Require(EditorSceneManager.CloseScene(fixture,true),"could not close synthetic multi-block scene");
            }
        }
        static void ValidateSyntheticPlan(WorldSpaceExperimentView view,AutomaticGazeGeometry geometry,Dictionary<ViveTrackpadDirection,Colour> mapping,int count)
        {
            var selections=new ViveNamedOptionProvenance[count];
            for(int i=0;i<count;i++)
            {var option=i%2==0?ViveNamedOption.Automatic:ViveNamedOption.NoAvatar;
                selections[i]=ViveNamedOptionProvenance.Select(Actor.Owner,option,i,option==ViveNamedOption.Automatic?geometry.Version:null);}
            var plan=ViveNamedSimulationPlan.Select(Actor.Owner,"synthetic-editor-plan-"+count,selections);
            var clock=new FixtureClock();var task=new ViveDevelopmentParticipant(
                SessionConfiguration.NamedPlanDevelopment("synthetic-plan-session-"+count,42,plan),clock,geometry,mapping,.5,true);
            Neutral(task);task.OwnerBeginPractice(Actor.Owner);
            for(int i=0;i<8;i++)
            {Neutral(task);var trial=task.CurrentTrial;Require(trial!=null&&object.ReferenceEquals(trial.NamedPlan,plan)&&trial.NamedBlock==null,"practice gained a block option");
                task.MarkPresented(trial.TrialId);clock.Value+=.4;task.UpdateTrackpad(true,Input(true,trial.Stimulus.Ink));Neutral(task);}
            Require(task.Phase==Phase.BlockPreparation,"multi-block practice did not pass");
            task.OwnerConfirmNamedPlan(Actor.Owner,"synthetic-owner");task.OwnerBeginBlock(Actor.Owner);
            for(int b=0;b<count;b++)
            {
                clock.Value+=3;task.Tick();Neutral(task);Require(task.Phase==Phase.MainTask,"planned block did not start");
                var block=plan.Blocks[b];var trial=task.CurrentTrial;
                Require(object.ReferenceEquals(task.CurrentNamedPlanBlock,block)&&object.ReferenceEquals(trial.NamedBlock,block)&&object.ReferenceEquals(trial.NamedPlan,plan),"actual participant/trial selected wrong block");
                Require(trial.ListId==TrialPlans.MainListId(42,block.Selection.ListIndex),"block stimulus list differs from plan");
                task.MarkPresented(trial.TrialId);view.ApplyTaskView(task.ParticipantView);
                if(block.Selection.Option==ViveNamedOption.NoAvatar)
                {CheckNoAvatar(view,task,"multi-block no avatar "+b);CheckStimulus(view,trial,"multi-block no avatar "+b);}
                else
                {
                    var output=task.CurrentAvatar;Require(output!=null&&object.ReferenceEquals(output.NamedBlock,block)&&output.AvatarVisible&&Math.Abs(output.Reference.ActiveSeconds)<1e-9,"automatic block did not start from its own active time");
                    view.ApplyAvatarOutput(output);Layout(view);Require(view.AvatarRoot.gameObject.activeInHierarchy,"automatic avatar hidden");CheckStimulus(view,trial,"multi-block automatic "+b);
                    ComparePose(view.AppliedPose,UnityRotation(geometry.TaskPose.HeadRotation),"multi-block automatic start "+b);CompareBothEyes(view,"multi-block automatic start "+b);
                }
                clock.Value+=3.25;
                if(block.Selection.Option==ViveNamedOption.Automatic)
                {var output=task.CurrentAvatar;Require(Math.Abs(output.Reference.ActiveSeconds-3.25)<1e-9,"automatic block clock differs");
                    view.ApplyTaskView(task.ParticipantView);view.ApplyAvatarOutput(output);Layout(view);
                    ComparePose(view.AppliedPose,Quaternion.Slerp(UnityRotation(geometry.TaskPose.HeadRotation),UnityRotation(geometry.ParticipantPose.HeadRotation),.5f),"multi-block transition "+b);CompareBothEyes(view,"multi-block transition "+b);}
                if(b==0)
                {
                    task.ParticipantPause();Require(task.Phase==Phase.Paused,"multi-block pause missing");
                    view.ApplyTaskView(task.ParticipantView);var output=task.CurrentAvatar;if(output==null)view.ClearAvatarOutput();else view.ApplyAvatarOutput(output);
                    Layout(view);Require(!view.AvatarRoot.gameObject.activeInHierarchy,"avatar visible in pause");
                    clock.Value+=5;Require(task.CurrentAvatar==null||Math.Abs(task.CurrentAvatar.Reference.ActiveSeconds-3.25)<1e-9,"pause did not freeze clock");
                    Neutral(task);task.UpdateAuxiliary(false,false,false,0,true);task.UpdateAuxiliary(true,false,false,0,true);
                    Require(task.Phase==Phase.Countdown,"explicit resume missing");clock.Value+=3;task.Tick();Neutral(task);
                }
                while(task.Phase==Phase.MainTask)
                {trial=task.CurrentTrial;Require(object.ReferenceEquals(trial.NamedBlock,block),"trial block provenance changed");
                    if(!trial.Onset.HasValue)task.MarkPresented(trial.TrialId);clock.Value+=.2;task.UpdateTrackpad(true,Input(true,trial.Stimulus.Ink));Neutral(task);}
                Require(task.Phase==Phase.Questionnaire,"questionnaire missing");
                task.UpdateAuxiliary(false,false,false,0,true);task.UpdateAuxiliary(true,false,false,0,true);task.UpdateAuxiliary(false,false,false,0,true);
                for(int q=0;q<Questionnaire.Count;q++){task.UpdateAuxiliary(true,false,false,0,true);task.UpdateAuxiliary(false,false,false,0,true);}
                if(b+1<count)
                {
                    Require(task.Phase==Phase.Rest,"rest missing");task.UpdateAuxiliary(false,false,false,0,true);task.UpdateAuxiliary(true,false,false,0,true);
                    Require(task.Phase==Phase.BlockReady&&object.ReferenceEquals(task.CurrentNamedPlanBlock,plan.Blocks[b+1]),"rest did not select next planned block");
                    task.UpdateTrackpad(false,Input(false,Colour.Red));task.Tick();Require(task.Phase==Phase.BlockReady,"unready block advanced");
                    Neutral(task);task.Tick();Require(task.Phase==Phase.Countdown,"ready block did not continue without owner confirmation");
                }
            }
            Require(task.Phase==Phase.Ended&&task.EndReason==EndReason.Completed&&task.RatingCount==count*Questionnaire.Count,"synthetic plan did not complete");
            int confirmations=0,started=0;var seen=new HashSet<int>();
            foreach(var item in task.ReadEventsAfter(-1,4096))
            {
                Require(object.ReferenceEquals(item.NamedPlan,plan)&&item.NamedOption==null,"event lost plan identity");
                if(item.Kind=="named_plan_confirmed"){confirmations++;Require(item.NamedBlock==null&&item.BlockIndex==-1,"plan confirmation was assigned a block");}
                if(item.Kind=="stimulus_presented"&&item.NamedBlock!=null)
                {Require(item.BlockIndex==item.NamedBlock.Index&&item.Fields["named_block_started"]=="True", "presented event has inconsistent block identity");seen.Add(item.BlockIndex);started++;}
            }
            Require(confirmations==1&&seen.Count==count&&started==count*24,"plan confirmation or presented trial coverage differs");
            Require(!task.IsResearchSession&&!task.UsesNetwork&&!task.RecordsTaskFiles&&!task.RecordsEyeSamples,"synthetic plan used a real data path");
        }
        public static void ValidateNoAvatar(){RunNoAvatar(false);}
        public static void RenderNoAvatarSynthetic(){RunNoAvatar(true);}
        static void CheckNoAvatar(WorldSpaceExperimentView view,ViveDevelopmentParticipant task,string label)
        {
            Require(task.CurrentAvatar==null&&!task.ParticipantView.OperatorAvatarVisible,label+" produced an avatar or pose");
            view.ApplyTaskView(task.ParticipantView);view.ClearAvatarOutput();Layout(view);
            Require(!view.AvatarRoot.gameObject.activeInHierarchy,label+" avatar root remains visible");
            foreach(var renderer in view.AvatarRoot.GetComponentsInChildren<Renderer>(true))
                Require(!renderer.gameObject.activeInHierarchy,label+" contains a visible avatar part");
        }
        static void RunNoAvatar(bool images)
        {
            ViveSyntheticFixtureIsolation.OpenKnownSavedAnchor();
            if(images)Require(SystemInfo.graphicsDeviceType!=GraphicsDeviceType.Null&&GraphicsSettings.currentRenderPipeline==null,
                "synthetic images require a graphics device and built-in pipeline");
            string output=images?NewOutput():null;GameObject cameraObject=null,viewObject=null;
            Scene previous=SceneManager.GetActiveScene(),fixture=default(Scene);bool opened=false;
            var anchorRenderers=new List<KeyValuePair<Renderer,bool>>();
            try
            {
                foreach(var root in previous.GetRootGameObjects())foreach(var renderer in root.GetComponentsInChildren<Renderer>(true))
                {anchorRenderers.Add(new KeyValuePair<Renderer,bool>(renderer,renderer.enabled));renderer.enabled=false;}
                fixture=EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Additive);
                opened=fixture.IsValid()&&fixture.isLoaded;Require(opened,"could not create isolated No Avatar scene");
                cameraObject=new GameObject("SyntheticNamedNoAvatarCamera");SceneManager.MoveGameObjectToScene(cameraObject,fixture);
                Require(cameraObject.scene==fixture,"camera not in synthetic scene");cameraObject.hideFlags=HideFlags.HideAndDontSave;
                var camera=cameraObject.AddComponent<Camera>();camera.transform.SetPositionAndRotation(WorldSpaceExperimentView.CenteredParticipantReference,Quaternion.identity);
                camera.fieldOfView=WorldSpaceExperimentView.DesktopVerticalFieldOfView;
                viewObject=new GameObject("SyntheticNamedNoAvatarView");SceneManager.MoveGameObjectToScene(viewObject,fixture);
                Require(viewObject.scene==fixture,"view not in synthetic scene");viewObject.hideFlags=HideFlags.HideAndDontSave;
                var view=viewObject.AddComponent<WorldSpaceExperimentView>();view.Initialize(camera,Actor.Participant);
                view.SetQuestionnaireLanguage("en");
                var mapping=Mapping();view.ConfigureViveDevelopmentPreview(mapping,.5);view.SetPresentationAvailable(true);
                view.SetViveDevelopmentStatus("Named No Avatar: one-block synthetic simulation; avatar source not applicable; no durable recording.","Synthetic controls only");
                var avatarRoot=view.AvatarRoot;var head=view.AvatarHead.position;
                Vector3 participant=avatarRoot.InverseTransformDirection(view.ParticipantReferencePosition-head);
                Vector3 board=avatarRoot.InverseTransformDirection(view.TaskWorldPosition-head);
                var geometry=DevelopmentGeometry.FromLocalDirections(new Vec3(participant.x,participant.y,participant.z),
                    new Vec3(board.x,board.y,board.z),"synthetic-named-editor-geometry");
                var selected=ViveNamedOptionProvenance.Select(Actor.Owner,ViveNamedOption.NoAvatar,2,null);
                var config=SessionConfiguration.NamedNoAvatarDevelopment("synthetic-named-no-avatar",42,selected);
                var clock=new FixtureClock();var task=new ViveDevelopmentParticipant(config,clock,geometry,mapping,.5,true);
                CheckNoAvatar(view,task,"preparation");Neutral(task);task.OwnerBeginPractice(Actor.Owner);
                for(int i=0;i<8;i++)
                {
                    CheckNoAvatar(view,task,"practice "+i);Neutral(task);var trial=task.CurrentTrial;
                    CheckStimulus(view,trial,"practice "+i);task.MarkPresented(trial.TrialId);clock.Value+=.4;
                    task.UpdateTrackpad(true,Input(true,trial.Stimulus.Ink));Neutral(task);
                }
                Require(task.Phase==Phase.BlockPreparation,"synthetic No Avatar practice did not pass");
                task.OwnerConfirmNamedNoAvatar(Actor.Owner,"synthetic-owner");CheckNoAvatar(view,task,"block ready");
                task.OwnerBeginBlock(Actor.Owner);clock.Value+=3;task.Tick();Neutral(task);
                Require(task.Phase==Phase.MainTask,"synthetic No Avatar block did not start");
                var first=task.CurrentTrial;var expected=TrialPlans.MainList(42,2)[0];
                Require(first.Stimulus.Word==expected.Word&&first.Stimulus.Ink==expected.Ink,"No Avatar changed the selected trial list");
                CheckNoAvatar(view,task,"main");var text=CheckStimulus(view,first,"main");task.MarkPresented(first.TrialId);
                if(images)Render(camera,output,"no-avatar","main",false,text,first.Stimulus.Ink);
                clock.Value+=1;task.ParticipantPause();Require(task.Phase==Phase.Paused&&first.Outcome==TrialOutcome.Interrupted,"No Avatar pause did not interrupt the trial");
                CheckNoAvatar(view,task,"paused");CheckEvent(task,"explicit_pause",1,selected);
                if(images)Render(camera,output,"no-avatar","paused",false,null,null);
                clock.Value+=10;Neutral(task);task.UpdateAuxiliary(false,false,false,0,true);task.UpdateAuxiliary(true,false,false,0,true);
                Require(task.Phase==Phase.Countdown,"No Avatar explicit resume did not start countdown");clock.Value+=3;task.Tick();Neutral(task);
                int answered=0;
                while(task.Phase==Phase.MainTask)
                {
                    CheckNoAvatar(view,task,"resumed trial "+answered);var trial=task.CurrentTrial;
                    CheckStimulus(view,trial,"resumed trial "+answered);task.MarkPresented(trial.TrialId);clock.Value+=.2;
                    task.UpdateTrackpad(true,Input(true,trial.Stimulus.Ink));Neutral(task);answered++;
                    Require(answered<=23,"No Avatar trial count exceeded plan");
                }
                Require(answered==23&&task.Phase==Phase.Questionnaire,"No Avatar task or questionnaire transition is incorrect");
                CheckNoAvatar(view,task,"questionnaire");task.UpdateAuxiliary(false,false,false,0,true);task.UpdateAuxiliary(true,false,false,0,true);
                task.UpdateAuxiliary(false,false,false,0,true);
                for(int i=0;i<Questionnaire.Count;i++)
                {task.UpdateAuxiliary(true,false,false,0,true);task.UpdateAuxiliary(false,false,false,0,true);}
                Require(task.Phase==Phase.Ended&&task.EndReason==EndReason.Completed,"No Avatar did not finish after five ratings");
                CheckNoAvatar(view,task,"completed");bool confirmation=false;
                foreach(var item in task.ReadEventsAfter(-1,4096))
                {
                    string value;Require(object.ReferenceEquals(item.NamedOption,selected)&&item.Fields.TryGetValue("named_option",out value)&&value=="NoAvatar","No Avatar event lost named provenance");
                    Require(item.Fields.TryGetValue("named_source",out value)&&value==ViveNamedOptionProvenance.NoAvatarNotApplicable,"No Avatar event misstates source");
                    Require(item.Fields.TryGetValue("named_avatar_aoi",out value)&&value==ViveNamedOptionProvenance.NoAvatarNotApplicable,"No Avatar event misstates avatar AOI");
                    Require(item.Fields.TryGetValue("named_participant_eye",out value)&&value=="NOT_RECORDED","participant eye data incorrectly marked invalid");
                    Require(!item.Fields.ContainsKey("condition"),"No Avatar event was assigned a legacy condition");
                    if(item.Kind=="named_option_confirmed")confirmation=true;
                }
                Require(confirmation&&!task.IsResearchSession&&!task.UsesNetwork&&!task.RecordsTaskFiles&&!task.RecordsEyeSamples,
                    "No Avatar confirmation missing or fixture entered a study/network/recording path");
                Debug.Log("VIVE_NAMED_NO_AVATAR_SYNTHETIC_FIXTURE_PASSED: actual simulation participant, no avatar in practice/task/pause/questionnaire/end, trial and typed not-applicable provenance; images="+images+"; no XR, hardware or real data.");
            }
            finally
            {
                foreach(var entry in anchorRenderers)if(entry.Key!=null)entry.Key.enabled=entry.Value;
                if(viewObject!=null)UnityEngine.Object.DestroyImmediate(viewObject);if(cameraObject!=null)UnityEngine.Object.DestroyImmediate(cameraObject);
                if(previous.IsValid()&&previous.isLoaded&&SceneManager.GetActiveScene()!=previous)SceneManager.SetActiveScene(previous);
                if(opened&&fixture.IsValid()&&fixture.isLoaded)Require(EditorSceneManager.CloseScene(fixture,true),"could not close synthetic No Avatar scene");
            }
        }
        static void Run(bool images)
        {
            ViveSyntheticFixtureIsolation.OpenKnownSavedAnchor();
            if(images)Require(SystemInfo.graphicsDeviceType!=GraphicsDeviceType.Null&&GraphicsSettings.currentRenderPipeline==null,
                "synthetic images require a graphics device and built-in pipeline");
            string output=images?NewOutput():null;
            GameObject cameraObject=null,viewObject=null;
            Scene previous=SceneManager.GetActiveScene(),fixture=default(Scene);bool opened=false;
            var anchorRenderers=new List<KeyValuePair<Renderer,bool>>();
            try
            {
                // The isolation anchor is intentionally left loaded. Temporarily
                // suppress its renderers so its Quest scene cannot paint over
                // the separate VIVE fixture camera; never save that scene.
                foreach(var root in previous.GetRootGameObjects())
                    foreach(var renderer in root.GetComponentsInChildren<Renderer>(true))
                    {anchorRenderers.Add(new KeyValuePair<Renderer,bool>(renderer,renderer.enabled));renderer.enabled=false;}
                fixture=EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Additive);
                opened=fixture.IsValid()&&fixture.isLoaded;Require(opened,"could not create isolated additive scene");
                cameraObject=new GameObject("SyntheticNamedAutomaticCamera");SceneManager.MoveGameObjectToScene(cameraObject,fixture);
                Require(cameraObject.scene==fixture,"camera not in synthetic scene");cameraObject.hideFlags=HideFlags.HideAndDontSave;
                var camera=cameraObject.AddComponent<Camera>();camera.transform.SetPositionAndRotation(WorldSpaceExperimentView.CenteredParticipantReference,Quaternion.identity);
                camera.fieldOfView=WorldSpaceExperimentView.DesktopVerticalFieldOfView;
                viewObject=new GameObject("SyntheticNamedAutomaticView");SceneManager.MoveGameObjectToScene(viewObject,fixture);
                Require(viewObject.scene==fixture,"view not in synthetic scene");viewObject.hideFlags=HideFlags.HideAndDontSave;
                var view=viewObject.AddComponent<WorldSpaceExperimentView>();view.Initialize(camera,Actor.Participant);
                view.SetQuestionnaireLanguage("en");
                var mapping=Mapping();view.ConfigureViveDevelopmentPreview(mapping,.5);view.SetPresentationAvailable(true);
                view.SetViveDevelopmentStatus("Named Automatic: one-block synthetic simulation; geometry unvalidated; no durable recording.","Synthetic controls only");
                var avatarRoot=view.AvatarRoot;var head=view.AvatarHead.position;
                Vector3 participant=avatarRoot.InverseTransformDirection(view.ParticipantReferencePosition-head);
                Vector3 board=avatarRoot.InverseTransformDirection(view.TaskWorldPosition-head);
                var geometry=DevelopmentGeometry.FromLocalDirections(new Vec3(participant.x,participant.y,participant.z),
                    new Vec3(board.x,board.y,board.z),"synthetic-named-editor-geometry");
                var selected=ViveNamedOptionProvenance.Select(Actor.Owner,ViveNamedOption.Automatic,2,geometry.Version);
                var config=SessionConfiguration.NamedAutomaticDevelopment("synthetic-named-editor",42,selected);
                var clock=new FixtureClock();var task=new ViveDevelopmentParticipant(config,clock,geometry,mapping,.5,true);
                Neutral(task);task.OwnerBeginPractice(Actor.Owner);
                for(int i=0;i<8;i++)
                {
                    Neutral(task);var trial=task.CurrentTrial;Require(trial!=null,"synthetic practice trial missing");
                    task.MarkPresented(trial.TrialId);clock.Value+=.4;task.UpdateTrackpad(true,Input(true,trial.Stimulus.Ink));Neutral(task);
                }
                Require(task.Phase==Phase.BlockPreparation,"synthetic practice did not pass");
                task.OwnerLockSource(Actor.Owner,ControlSelection.Automatic,"synthetic-owner");task.OwnerBeginBlock(Actor.Owner);
                clock.Value+=3;task.Tick();Require(task.Phase==Phase.MainTask,"synthetic block did not start");
                Neutral(task);task.MarkPresented(task.CurrentTrial.TrialId);
                var taskHead=UnityRotation(geometry.TaskPose.HeadRotation);var participantHead=UnityRotation(geometry.ParticipantPose.HeadRotation);
                double previousTime=0;
                foreach(var sample in new[]{Tuple.Create(0d,"0",ReferencePhase.TaskDwell,0f),Tuple.Create(3.25,"3-25",ReferencePhase.TurnToParticipant,.5f),
                    Tuple.Create(3.5,"3-5",ReferencePhase.ParticipantDwell,1f),Tuple.Create(6.75,"6-75",ReferencePhase.TurnToTask,.5f)})
                {
                    clock.Value+=sample.Item1-previousTime;previousTime=sample.Item1;
                    view.ApplyTaskView(task.ParticipantView);var actual=task.CurrentAvatar;
                    Require(actual!=null&&actual.NamedOption==selected&&actual.AvatarVisible&&actual.Reference.Phase==sample.Item3,
                        sample.Item2+" did not render the selected named option");
                    Require(Math.Abs(actual.Reference.ActiveSeconds-sample.Item1)<1e-9,sample.Item2+" used another active clock");
                    view.ApplyAvatarOutput(actual);Layout(view);Require(view.AvatarRoot.gameObject.activeInHierarchy,sample.Item2+" avatar hidden");
                    var stimulus=CheckStimulus(view,task.CurrentTrial,sample.Item2);
                    ComparePose(view.AppliedPose,Quaternion.Slerp(taskHead,participantHead,sample.Item4),sample.Item2);
                    CompareBothEyes(view,sample.Item2);
                    if(images)Render(camera,output,"automatic",sample.Item2,true,stimulus,task.CurrentTrial.Stimulus.Ink);
                    if(sample.Item1==3.25)
                    {
                        var trial=task.CurrentTrial;task.UpdateTrackpad(true,Input(true,trial.Stimulus.Ink));Neutral(task);
                        Require(trial.Outcome==TrialOutcome.Responded,"synthetic response was not accepted");
                        CheckEvent(task,"first_response",3.25,selected);
                        Require(Math.Abs(task.CurrentAvatar.Reference.ActiveSeconds-3.25)<1e-9,"response reset the trajectory");
                        task.MarkPresented(task.CurrentTrial.TrialId);
                    }
                }
                task.ParticipantPause();Require(task.Phase==Phase.Paused,"synthetic pause was not applied");
                CheckEvent(task,"explicit_pause",6.75,selected);view.ApplyTaskView(task.ParticipantView);view.ApplyAvatarOutput(task.CurrentAvatar);Layout(view);
                Require(!view.AvatarRoot.gameObject.activeInHierarchy,"avatar visible during explicit pause");
                clock.Value+=50;Require(Math.Abs(task.CurrentAvatar.Reference.ActiveSeconds-6.75)<1e-9,"pause did not freeze active time");
                var pausedStimulus=view.transform.Find("WorldSpaceContent/Stimulus");
                Require(pausedStimulus!=null&&!pausedStimulus.gameObject.activeInHierarchy,"stimulus visible during explicit pause");
                if(images)Render(camera,output,"automatic","paused",false,null,null);
                Require(!task.IsResearchSession&&!task.UsesNetwork&&!task.RecordsTaskFiles&&!task.RecordsEyeSamples,
                    "synthetic fixture entered a study, network or recording path");
                Debug.Log("VIVE_NAMED_AUTOMATIC_SYNTHETIC_FIXTURE_PASSED: actual simulation participant, renderer transforms, active clock, response, pause and provenance; images="+images+"; no XR, hardware, real data or research validation.");
            }
            finally
            {
                foreach(var entry in anchorRenderers)if(entry.Key!=null)entry.Key.enabled=entry.Value;
                if(viewObject!=null)UnityEngine.Object.DestroyImmediate(viewObject);
                if(cameraObject!=null)UnityEngine.Object.DestroyImmediate(cameraObject);
                if(previous.IsValid()&&previous.isLoaded&&SceneManager.GetActiveScene()!=previous)SceneManager.SetActiveScene(previous);
                if(opened&&fixture.IsValid()&&fixture.isLoaded)Require(EditorSceneManager.CloseScene(fixture,true),"could not close synthetic scene");
            }
        }
    }
}
#endif
