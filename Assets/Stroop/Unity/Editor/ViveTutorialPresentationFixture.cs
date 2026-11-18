#if UNITY_EDITOR && STROOP_XR_PREVIEW
using System;
using System.IO;
using System.Reflection;
using AvatarStroop.Core;
using AvatarStroop.Runtime;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AvatarStroop.Unity.Editor
{
    // Actual text geometry, participant-camera panel witnesses and tutorial
    // task visibility. No physical headset, eye tracker or file writer runs.
    public static class ViveTutorialPresentationFixture
    {
        const BindingFlags Private=BindingFlags.Instance|BindingFlags.NonPublic;
        static int checks;
        static void Require(bool value,string detail){checks++;if(!value)throw new InvalidOperationException("Tutorial presentation: "+detail);}
        static object Get(object target,string field){return target.GetType().GetField(field,Private).GetValue(target);}
        static object Call(object target,string method,params object[] args){return target.GetType().GetMethod(method,Private|BindingFlags.Public).Invoke(target,args);}
        static void Set(object target,string property,object value){target.GetType().GetProperty(property).SetValue(target,value,null);}
        static bool AcceptPanel(object sample){return sample!=null;}
        static object Static(Type type,string name,params object[] args){return type.GetMethod(name,BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,args);}
        static string Words(string value){return String.Join(" ",(value??"").Split((char[])null,StringSplitOptions.RemoveEmptyEntries));}
        static TaskView Task(Phase phase,TaskKind kind,ResponseRule rule)
        {
            var value=new TaskView();Set(value,"Phase",phase);Set(value,"UsesTutorial",true);Set(value,"TrainingSchema","tutorial-exam-v1");
            Set(value,"TaskKind",kind);Set(value,"ResponseRule",rule);Set(value,"BlockIndex",-1);Set(value,"TrialId","tutorial-fixture-"+kind+"-"+rule+"-"+phase);
            Set(value,"PracticeTrialCount",10);Set(value,"PracticeCorrect",8);Set(value,"PracticeAttempt",1);Set(value,"PracticePassed",true);
            Set(value,"Stimulus",new Stimulus(Colour.Red,Colour.Green));
            if(kind==TaskKind.VisualSearch)Set(value,"VisualSearch",VisualSearchPlans.Practice(19,1)[0].VisualSearch);
            Set(value,"TutorialExpectedAnswer",kind==TaskKind.VisualSearch?value.VisualSearch.TargetLabel:rule==ResponseRule.WordMeaning?"Red":"Green");
            SetFeedback(value,false);
            return value;
        }
        static void SetFeedback(TaskView task,bool correct)
        {
            Set(task,"TutorialCorrect",correct);
            Set(task,"TutorialFeedback",(correct?"Correct! ":"Incorrect. ")+(task.TaskKind==TaskKind.VisualSearch?"Match both colour and pattern. The target is the ":
                task.ResponseRule==ResponseRule.WordMeaning?"Choose the colour named by the word: ":"Choose the font colour: ")+task.TutorialExpectedAnswer+".");
        }
        static Rect Bounds(TextMesh text,Transform parent)
        {
            var box=text.GetComponent<Renderer>().localBounds;var min=new Vector2(float.PositiveInfinity,float.PositiveInfinity);
            var max=new Vector2(float.NegativeInfinity,float.NegativeInfinity);
            foreach(float x in new[]{box.min.x,box.max.x})foreach(float y in new[]{box.min.y,box.max.y})
            {var p=parent.InverseTransformPoint(text.transform.TransformPoint(new Vector3(x,y,0)));min=Vector2.Min(min,new Vector2(p.x,p.y));max=Vector2.Max(max,new Vector2(p.x,p.y));}
            return Rect.MinMaxRect(min.x,min.y,max.x,max.y);
        }
        static void CheckPanel(WorldSpaceExperimentView view,Camera camera,TaskView task,int selection)
        {
            view.SetViveTutorialMenuSelection(selection);view.ApplyTaskView(task);Call(view,"LayoutHud");
            var panel=(Transform)Get(view,"warningRoot");
            var body=(TextMesh)Get(view,"messageText");var options=(TextMesh)Get(view,"previewInputText");
            Require(Words(body.text)==Words((string)Call(view,"ParticipantMessage",task)),"feedback/result text was truncated");
            foreach(var text in new[]{body,options})
            {
                var bounds=Bounds(text,panel);
                Require(text.GetComponent<Renderer>().enabled&&bounds.width>0,"panel text hidden");
                Require(bounds.xMin>=-.631f&&bounds.xMax<=.631f&&bounds.yMin>=-.491f&&bounds.yMax<=.491f,"panel text outside headset safe area");
                Require(!text.text.Contains("...")&&!text.text.Contains("…"),"panel replaced instructions with ellipsis");
            }
            Require(!Bounds(body,panel).Overlaps(Bounds(options,panel)),"feedback and choices overlap");
            var mark=(Transform)Get(view,"tutorialAnswerMark");
            string outcome="";
            if(task.Phase==Phase.TutorialFeedback)
            {
                bool correct=task.TutorialCorrect.Value;
                outcome=correct?"Correct":"Wrong";
                var heading=(TextMesh)Get(view,"phaseText");
                Require(heading.text==(correct?"Correct!":"Incorrect")&&Words(body.text).Contains(task.TutorialExpectedAnswer),"answer or correctness missing");
                Require(Words(options.text)==Words((string)Static(typeof(WorldSpaceExperimentView),"TutorialMenuText",selection)),"selected tutorial option missing");
                Require(mark!=null&&mark.parent==panel&&mark.gameObject.activeInHierarchy,"answer mark outside visible participant panel");
                var first=mark.GetChild(0);var second=mark.GetChild(1);
                Require(first.GetComponent<Renderer>().enabled&&second.GetComponent<Renderer>().enabled&&
                    (camera.cullingMask&(1<<first.gameObject.layer))!=0&&(camera.cullingMask&(1<<second.gameObject.layer))!=0,"answer strokes hidden from camera");
                float strokeSpacing=Vector3.Distance(first.localPosition,second.localPosition);
                Require(correct?strokeSpacing>.25f:strokeSpacing<.01f,"wrong check or X geometry");
                var start=typeof(WorldSpaceExperimentView).GetField("tutorialMarkStarted",Private);
                var since=Time.realtimeSinceStartupAsDouble;
                start.SetValue(view,since);Call(view,"LayoutHud");float initial=mark.localScale.x;
                start.SetValue(view,since-1);view.ApplyTaskView(task);Call(view,"LayoutHud");
                Require((double)start.GetValue(view)==since-1&&mark.localScale.x>initial*1.25f,"animation did not grow and settle across repeated task updates");
            }
            else
            {
                if(mark!=null)Require(!mark.gameObject.activeInHierarchy,"tutorial animation leaked into practice exam");
                if(task.Phase==Phase.PracticeExamResult)outcome=task.PracticePassed==true?"Passed":"Retry";
            }
            Capture(camera,task,task.Phase+(outcome==""?"":"-"+outcome)+"-Selection"+selection);
        }
        static void Capture(Camera camera,TaskView task,string stage)
        {
            var directory=Environment.GetEnvironmentVariable("STROOP_TUTORIAL_RENDER_OUTPUT");
            if(!String.IsNullOrWhiteSpace(directory))Screenshot(camera,Path.Combine(directory,task.TaskKind+"-"+task.ResponseRule+"-"+stage+".png"));
        }
        static void Screenshot(Camera camera,string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));var target=new RenderTexture(1200,1000,24);
            var old=camera.targetTexture;var active=RenderTexture.active;var pixels=new Texture2D(1200,1000,TextureFormat.RGB24,false);
            try{target.Create();camera.targetTexture=target;camera.Render();RenderTexture.active=target;pixels.ReadPixels(new Rect(0,0,1200,1000),0,0);pixels.Apply();File.WriteAllBytes(path,pixels.EncodeToPNG());}
            finally{camera.targetTexture=old;RenderTexture.active=active;UnityEngine.Object.DestroyImmediate(pixels);target.Release();UnityEngine.Object.DestroyImmediate(target);}
        }
        public static void Validate()
        {
            checks=0;ViveSyntheticFixtureIsolation.OpenKnownSavedAnchor();
            var scene=EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Additive);
            var cameraRoot=new GameObject("TutorialFixtureCamera");var viewRoot=new GameObject("TutorialFixtureView");
            SceneManager.MoveGameObjectToScene(cameraRoot,scene);SceneManager.MoveGameObjectToScene(viewRoot,scene);
            try
            {
                var camera=cameraRoot.AddComponent<Camera>();var view=viewRoot.AddComponent<WorldSpaceExperimentView>();
                view.Initialize(camera,Actor.Participant);Call(view,"OnDisable");Call(view,"OnEnable");
                view.ConfigureViveDevelopmentPreview(ViveFixedTrackpadMapping.Create(),ViveFixedTrackpadMapping.SectorThreshold);
                view.SetViveDevelopmentStatus("","");view.SetFaults(new string[0]);view.SetPresentationAvailable(true);
                camera.transform.position=WorldSpaceExperimentView.CenteredParticipantReference;camera.transform.LookAt(WorldSpaceExperimentView.FixedMenuPosition);
                camera.fieldOfView=64;camera.aspect=1.2f;camera.cullingMask=1<<28;camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=Color.black;
                foreach(var item in cameraRoot.GetComponentsInChildren<Transform>(true))item.gameObject.layer=28;
                foreach(var kind in new[]{TaskKind.Stroop,TaskKind.VisualSearch})foreach(var rule in new[]{ResponseRule.InkColor,ResponseRule.WordMeaning})
                {
                    if(kind==TaskKind.VisualSearch&&rule==ResponseRule.WordMeaning)continue;
                    var trial=Task(Phase.Tutorial,kind,rule);view.ApplyTaskView(trial);Call(view,"LayoutHud");
                    string acknowledged=null;Action<string,double> onPresented=(id,at)=>acknowledged=id;view.StimulusRendered+=onPresented;
                    foreach(var item in viewRoot.GetComponentsInChildren<Transform>(true))item.gameObject.layer=28;
                    Require(view.CleanTaskPresentation,"tutorial is not a task view");
                    if(kind==TaskKind.Stroop)
                    {
                        var stimulus=(TextMesh)Get(view,"stimulusText");var instructions=(TextMesh)Get(view,"tutorialBoardInstructions");
                        Require(stimulus.gameObject.activeInHierarchy&&instructions.gameObject.activeInHierarchy,"tutorial word/instruction hidden");
                        Require(!stimulus.GetComponent<Renderer>().bounds.Intersects(instructions.GetComponent<Renderer>().bounds),"tutorial instructions overlap the stimulus");
                        Require(instructions.text.Contains(rule==ResponseRule.WordMeaning?"Read the word":"ink colour"),"tutorial rule mismatch");
                        Capture(camera,trial,"Tutorial-Question");
                    }
                    else
                    {
                        var searchRoot=(Transform)Get(view,"visualSearchRoot");
                        var header=(TextMesh)Get(view,"visualSearchInstruction");var target=(TextMesh)Get(view,"visualSearchTargetLabel");
                        Require(!Bounds(header,searchRoot).Overlaps(Bounds(target,searchRoot))&&Bounds(header,searchRoot).xMax<.39f&&
                            Bounds(target,searchRoot).xMax<.39f,"larger search instruction overlaps the target example or name");
                        Require(Bounds((TextMesh)Get(view,"visualSearchControls"),searchRoot).yMin>=-.483f,"search cue footer touches board edge");
                        Capture(camera,trial,"Tutorial-Cue");
                    }
                    foreach(var witness in viewRoot.GetComponentsInChildren<StimulusRenderWitness>(true))witness.runInEditMode=true;
                    var timer=System.Diagnostics.Stopwatch.StartNew();
                    do{camera.Render();if(acknowledged==trial.TrialId)break;System.Threading.Thread.Sleep(30);}while(timer.Elapsed.TotalSeconds<4);
                    Require(acknowledged==trial.TrialId,"tutorial stimulus did not receive its camera onset witness: "+kind+"/"+rule);
                    if(kind==TaskKind.VisualSearch)
                    {
                        Require(Bounds((TextMesh)Get(view,"visualSearchControls"),(Transform)Get(view,"visualSearchRoot")).yMin>=-.483f,"search array footer touches board edge");
                        Capture(camera,trial,"Tutorial-Array");
                    }
                    Require((bool)Call(view,"PrepareRawAutomaticAvatar",trial,null),"tutorial failed hidden-avatar admission");
                    view.StimulusRendered-=onPresented;
                    var feedback=Task(Phase.TutorialFeedback,kind,rule);
                    foreach(bool correct in new[]{false,true})
                    {
                        SetFeedback(feedback,correct);
                        foreach(int selected in new[]{0,1})CheckPanel(view,camera,feedback,selected);
                    }
                    var result=Task(Phase.PracticeExamResult,kind,rule);CheckPanel(view,camera,result,0);
                    Set(result,"PracticeCorrect",7);Set(result,"PracticePassed",false);CheckPanel(view,camera,result,0);
                    CheckPanel(view,camera,Task(Phase.PracticeExamReady,kind,rule),0);
                }
                var rawFeedback=Task(Phase.TutorialFeedback,TaskKind.Stroop,ResponseRule.InkColor);
                view.SetViveTutorialMenuSelection(0);view.ApplyTaskView(rawFeedback);
                foreach(var item in viewRoot.GetComponentsInChildren<Transform>(true))item.gameObject.layer=28;
                Call(view,"BeforeCameraCull",camera);
                foreach(var witness in viewRoot.GetComponentsInChildren<ViveRawPanelRenderWitness>(true))witness.runInEditMode=true;
                camera.Render();
                Require((bool)Call(view,"HasPriorTutorialPanelWitness",rawFeedback,0,Time.frameCount+1),"preview tutorial choice has no camera witness");
                view.SetViveTutorialMenuSelection(1);
                Require(!(bool)Call(view,"HasPriorTutorialPanelWitness",rawFeedback,1,Time.frameCount+1),"preview changed choice accepted stale witness");
                typeof(WorldSpaceExperimentView).GetField("ViveRawObservationRequired",Private).SetValue(view,(Func<bool>)(()=>true));
                var callback=typeof(WorldSpaceExperimentView).GetField("ViveRawPanelRendered",Private);
                callback.SetValue(view,Delegate.CreateDelegate(callback.FieldType,typeof(ViveTutorialPresentationFixture).GetMethod("AcceptPanel",BindingFlags.Static|BindingFlags.NonPublic)));
                view.SetViveTutorialMenuSelection(0);view.ApplyTaskView(rawFeedback);
                foreach(var item in viewRoot.GetComponentsInChildren<Transform>(true))item.gameObject.layer=28;
                var arguments=new object[]{Time.frameCount,(long)-1};Require((bool)Call(view,"TryPrepareViveRawPanel",arguments)&&(bool)Call(view,"TryRevealViveRawPanel"),"tutorial panel could not prepare witness");
                camera.Render();
                Require((bool)Call(view,"HasPriorViveRawPanelWitness",rawFeedback,false,0,0,Time.frameCount+1),"tutorial choice has no completed camera witness");
                view.SetViveTutorialMenuSelection(1);
                Require(!(bool)Call(view,"HasPriorViveRawPanelWitness",rawFeedback,false,1,0,Time.frameCount+1),"unrendered choice accepted old panel witness");
                Require(!(bool)Static(typeof(ViveStroopDevelopmentBootstrap),"ControllerColourMappingPermitted",TaskKind.Stroop,Phase.TutorialFeedback),"response colours conflict with tutorial menu selection");
                Require((bool)Static(typeof(ViveStroopDevelopmentBootstrap),"ControllerColourMappingPermitted",TaskKind.Stroop,Phase.Tutorial),"tutorial response mapping hidden");
                Debug.Log("VIVE_TUTORIAL_PRESENTATION_FIXTURE_PASSED: "+checks+" checks; actual text geometry and camera witnesses, no physical headset acceptance.");
            }
            finally{UnityEngine.Object.DestroyImmediate(viewRoot);UnityEngine.Object.DestroyImmediate(cameraRoot);EditorSceneManager.CloseScene(scene,true);}
        }
    }
}
#endif
