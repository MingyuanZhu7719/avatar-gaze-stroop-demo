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
    // Real TextMesh geometry and optional camera PNGs, without starting XR,
    // native eye input, a controller, recording or an experiment clock.
    public static class ViveHeadsetNoticeLayoutFixture
    {
        const BindingFlags Private=BindingFlags.Instance|BindingFlags.NonPublic;
        static int checks;
        static string output;
        static void Require(bool condition,string detail)
        {checks++;if(!condition)throw new InvalidOperationException("Headset notice layout: "+detail);}
        static object Get(object value,string field){return value.GetType().GetField(field,Private).GetValue(value);}
        static object Call(object value,string method,params object[] args)
        {return value.GetType().GetMethod(method,Private|BindingFlags.Public).Invoke(value,args);}
        static void Set(TaskView value,string property,object data){value.GetType().GetProperty(property).SetValue(value,data,null);}
        static string Words(string value){return String.Join(" ",(value??"").Split((char[])null,StringSplitOptions.RemoveEmptyEntries));}
        static GameObject Root(Scene scene,string name)
        {var value=new GameObject(name);SceneManager.MoveGameObjectToScene(value,scene);value.hideFlags=HideFlags.HideAndDontSave;return value;}
        static Rect TextBounds(TextMesh text,Transform space)
        {
            var bounds=text.GetComponent<MeshRenderer>().localBounds;
            float left=Single.PositiveInfinity,bottom=left,right=Single.NegativeInfinity,top=right;
            foreach(float x in new[]{bounds.min.x,bounds.max.x})foreach(float y in new[]{bounds.min.y,bounds.max.y})
            {
                var point=space.InverseTransformPoint(text.transform.TransformPoint(new Vector3(x,y,0)));
                left=Mathf.Min(left,point.x);right=Mathf.Max(right,point.x);bottom=Mathf.Min(bottom,point.y);top=Mathf.Max(top,point.y);
            }
            return Rect.MinMaxRect(left,bottom,right,top);
        }
        static void Within(Rect actual,Rect safe,string name)
        {
            Require(actual.width>0&&actual.height>0,name+" has no text geometry");
            Require(actual.xMin>=safe.xMin-.001f&&actual.xMax<=safe.xMax+.001f&&
                actual.yMin>=safe.yMin-.001f&&actual.yMax<=safe.yMax+.001f,
                name+" exceeds its safe text area: "+actual+" versus "+safe);
        }
        static void Notice(object notice,Camera camera,string expected,float size,string name)
        {
            var text=(TextMesh)Get(notice,"text");var layout=Get(notice,"noticeLayout");
            var safe=(Rect)layout.GetType().GetProperty("SafeBounds",Private).GetValue(layout,null);
            Require(Words(text.text)==Words(expected),name+" lost or changed instruction content");
            Require(Mathf.Approximately(text.characterSize,size)&&text.transform.localScale==Vector3.one,
                name+" shrank the glyphs to fit");
            Within(TextBounds(text,camera.transform),safe,name);
            Require(safe.width<=1.19f&&safe.height<=.97f,name+" escaped the central angular panel");
        }
        static void Screenshot(Camera camera,string name)
        {
            if(output==null)return;
            int height=1000,width=Mathf.RoundToInt(height*camera.aspect);
            var texture=new RenderTexture(width,height,24,RenderTextureFormat.ARGB32){antiAliasing=4};
            var pixels=new Texture2D(width,height,TextureFormat.RGB24,false);
            var old=camera.targetTexture;var active=RenderTexture.active;
            try
            {
                texture.Create();camera.targetTexture=texture;camera.Render();RenderTexture.active=texture;
                pixels.ReadPixels(new Rect(0,0,width,height),0,0);pixels.Apply();
                string path=Path.Combine(output,name+".png");File.WriteAllBytes(path,pixels.EncodeToPNG());
                Debug.Log("VIVE_NOTICE_SCREENSHOT: "+path);
            }
            finally
            {
                camera.targetTexture=old;RenderTexture.active=active;
                UnityEngine.Object.DestroyImmediate(pixels);texture.Release();UnityEngine.Object.DestroyImmediate(texture);
            }
        }
        static void WorldNotice(WorldSpaceExperimentView view,Camera camera,TaskView task,string name)
        {
            view.ApplyTaskView(task);Call(view,"LayoutHud");
            var label=(TextMesh)Get(view,"messageText");var panel=(Transform)Get(view,"warningRoot");
            string expected=(string)Call(view,"ParticipantMessage",task);
            Require(Words(label.text)==Words(expected),name+" truncated the participant message");
            Require(label.GetComponent<Renderer>().enabled,name+" body is hidden");
            Within(TextBounds(label,panel),new Rect(-.63f,-.49f,1.26f,.98f),name);
            // Existing world-panel fitting must stay readable as well as bounded.
            int lines=label.text.Split('\n').Length;
            Require(label.GetComponent<Renderer>().bounds.size.y/lines>=.030f,name+" body text became too small");
            Screenshot(camera,name);
        }
        static void StartupProjectionChanges(ViveStartupView startup,Camera camera,string prompt)
        {
            // Use a frustum-limited width: the central angular cap must not
            // hide the small projection changes that this regression exercises.
            camera.fieldOfView=60;camera.aspect=.65f;startup.Show(true,prompt);camera.Render();
            int nextFrame=Time.frameCount+1;
            Require(startup.WasRenderedBefore(nextFrame),"startup did not obtain a completed-camera witness");
            var text=(TextMesh)Get(startup,"text");var layout=Get(startup,"noticeLayout");
            var safeProperty=layout.GetType().GetProperty("SafeBounds",Private);
            var before=(Rect)safeProperty.GetValue(layout,null);string fitted=text.text;var position=text.transform.localPosition;
            foreach(float fieldOfView in new[]{60.02f,59.98f,60.01f,59.99f,60f})
            {
                camera.fieldOfView=fieldOfView;
                var bounds=(Rect)safeProperty.GetValue(layout,null);
                if(fieldOfView!=60)Require(bounds!=before,"synthetic projection jitter did not change the actual safe area");
                startup.Show(true,prompt);
                Require(startup.WasRenderedBefore(nextFrame),"small projection jitter discarded the completed startup witness");
                Require(text.text==fitted&&text.transform.localPosition==position,"small projection jitter unnecessarily reflowed the notice");
                Notice(startup,camera,prompt,.012f,"startup projection jitter");
            }
            camera.aspect=.45f;startup.Show(true,prompt);
            Require(!startup.WasRenderedBefore(nextFrame),"substantial narrowing retained an old layout witness");
            Require(text.text!=fitted,"substantial narrowing did not rewrap the long startup notice");
            Notice(startup,camera,prompt,.012f,"startup narrowed projection");
            camera.Render();Require(startup.WasRenderedBefore(Time.frameCount+1),"reflowed startup could not obtain a new camera witness");
            startup.Show(true,"Look naturally forward.\nHold still briefly.");
            Require(!startup.WasRenderedBefore(Time.frameCount+1),"changed startup content retained the earlier witness");
            startup.Hide();camera.cullingMask=0;camera.fieldOfView=70;
        }
        public static void Validate()
        {
            checks=0;output=null;
            var args=Environment.GetCommandLineArgs();
            for(int i=0;i<args.Length-1;i++)if(args[i]=="-stroopNoticePreviewOutput")output=Path.GetFullPath(args[i+1]);
            if(output!=null)Directory.CreateDirectory(output);
            ViveSyntheticFixtureIsolation.OpenKnownSavedAnchor();
            var scene=EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Additive);
            GameObject cameraObject=null,viewObject=null;IDisposable hold=null;ViveStartupView startup=null;
            try
            {
                var camera=(cameraObject=Root(scene,"HeadsetNoticeFixtureCamera")).AddComponent<Camera>();
                camera.nearClipPlane=.05f;camera.fieldOfView=70;camera.cullingMask=0;
                var type=typeof(ViveStartupView).Assembly.GetType("AvatarStroop.Unity.ViveRawHoldView",true);
                hold=(IDisposable)Activator.CreateInstance(type,Private,null,new object[]{camera},null);
                startup=new ViveStartupView(camera);
                string[] prompts={"Put on the headset.","View preparation paused.\nCheck the desktop status.",
                    "Look naturally forward.\nHold still briefly.\nPreparing your view...",
                    "Configure the session on the desktop,\nthen restart the view.",
                    "Controller unavailable.\nConnect exactly one VIVE controller.\nCheck controller status on the desktop.",
                    "Wearing the headset?\nRelease all controls, then press TRIGGER.\nLook naturally forward and hold still."};
                StartupProjectionChanges(startup,camera,prompts[prompts.Length-1]);
                foreach(float aspect in new[]{.8f,1f,1.6f})
                {
                    camera.aspect=aspect;
                    for(int i=0;i<prompts.Length;i++)
                    {
                        startup.Show(true,prompts[i]);Notice(startup,camera,prompts[i],.012f,"startup "+i+" aspect "+aspect);
                        if(aspect==1&&i>=4)Screenshot(camera,"startup-"+i);
                    }
                    startup.Hide();camera.cullingMask=0;
                    Call(hold,"SetTransition",false,0.0,Double.NaN);
                    Require((bool)Call(hold,"BeginRender",camera,true,false,false),"normal hold refused");
                    Notice(hold,camera,"Task on hold.\nCheck the desktop\nfor the next step.",.012f,"hold "+aspect);
                    if(aspect==1)Screenshot(camera,"hold");Call(hold,"EndRender");
                    Require((bool)Call(hold,"BeginRender",camera,true,true,false),"blocked hold refused");
                    Notice(hold,camera,"Task blocked.\nCheck recording status\non the desktop.",.012f,"blocked "+aspect);Call(hold,"EndRender");
                    Require((bool)Call(hold,"BeginRender",camera,true,false,true),"controller hold refused");
                    Notice(hold,camera,"Controller view unavailable.\nCheck tracking on the desktop.",.012f,"controller "+aspect);
                    if(aspect==1)Screenshot(camera,"controller-unavailable");Call(hold,"EndRender");
                    foreach(string instruction in new[]{"Choose the colour named by the word.","Choose the ink colour, not the word.",
                        "Find the matching backpack.\nPoint at it and pull the trigger."})
                    {
                        Call(hold,"SetTransition",false,0.0,5.0);Call(hold,"SetPracticeInstruction",instruction);
                        Require((bool)Call(hold,"BeginRender",camera,true,false,false),"practice hold refused");
                        Notice(hold,camera,"Practice starts in\n5\n"+instruction,.015f,"practice "+aspect);
                        if(aspect==1&&instruction.StartsWith("Find",StringComparison.Ordinal))Screenshot(camera,"practice-visual-search");
                        Call(hold,"EndRender");
                    }
                    Call(hold,"SetBlockCompletion",1.4);
                    Require((bool)Call(hold,"BeginRender",camera,true,false,false),"block completion refused");
                    Notice(hold,camera,"Block complete!\nA few questions are next.",.012f,"block completion "+aspect);
                    if(aspect==1)Screenshot(camera,"block-complete");Call(hold,"EndRender");
                    foreach(double elapsed in new[]{1.4,6.0})
                    {
                        Call(hold,"SetTransition",true,elapsed,Double.NaN);
                        Require((bool)Call(hold,"BeginRender",camera,true,false,false),"final completion refused");
                        Notice(hold,camera,elapsed<6?"All blocks complete!\nWell done!":"Thank you for participating!\nYou may remove the headset.",.012f,"final "+elapsed+" aspect "+aspect);
                        if(aspect==1)Screenshot(camera,elapsed<6?"final-fireworks":"thank-you");Call(hold,"EndRender");
                    }
                }
                hold.Dispose();hold=null;startup.Dispose();startup=null;
                var view=(viewObject=Root(scene,"WorldNoticeFixtureView")).AddComponent<WorldSpaceExperimentView>();view.Initialize(camera,Actor.Participant);
                camera.transform.position=WorldSpaceExperimentView.CenteredParticipantReference;
                camera.transform.LookAt(WorldSpaceExperimentView.FixedMenuPosition);camera.fieldOfView=64;camera.aspect=1;
                camera.cullingMask=1<<28;camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=Color.black;
                Call(view,"SetViveParticipantRecordingChoice","Allow task recording?\nTask responses and timing will be saved locally.\nRelease all controls before choosing.",3);
                foreach(var child in viewObject.GetComponentsInChildren<Transform>(true))child.gameObject.layer=28;
                Call(view,"LayoutHud");
                foreach(string field in new[]{"previewStatusText","previewInputText"})
                    Within(TextBounds((TextMesh)Get(view,field),(Transform)Get(view,"warningRoot")),new Rect(-.63f,-.49f,1.26f,.98f),"recording choice "+field);
                Screenshot(camera,"recording-choice");
                view.ConfigureViveDevelopmentPreview(ViveFixedTrackpadMapping.Create(),ViveFixedTrackpadMapping.SectorThreshold);
                view.SetViveDevelopmentStatus("Follow the instructions above.","Release the trigger, then pull it to continue.");view.SetFaults(new string[0]);
                foreach(var child in viewObject.GetComponentsInChildren<Transform>(true))child.gameObject.layer=28;
                foreach(Phase phase in new[]{Phase.Preparation,Phase.PracticeCorrection,Phase.BlockPreparation,Phase.BlockReady,Phase.Paused,Phase.Ended})
                {
                    var task=new TaskView();Set(task,"Phase",phase);Set(task,"TaskKind",TaskKind.VisualSearch);
                    Set(task,"PracticeCorrection","The matching target was the yellow striped backpack. Match both its colour and pattern.");
                    if(phase==Phase.Paused)view.SetVivePauseDiagnostic("Controller tracking was interrupted. Check that the controller is visible to the base stations.");
                    if(phase==Phase.Ended)view.SetPhaseProgress(0,0,0,0,0,0,true);
                    WorldNotice(view,camera,task,"world-"+phase.ToString().ToLowerInvariant());
                }
                Require(camera.GetComponent<ViveStroopPreviewRig>()==null,"fixture started a rig");
                Debug.Log("VIVE_HEADSET_NOTICE_LAYOUT_FIXTURE_PASSED: "+checks+" checks; fixed readable notice glyphs, measured wrapping, full text and safe bounds at three aspect ratios, stable startup witness under small projection changes, plus world-panel confirmation/correction/completion. Synthetic camera evidence only; no XR or headset acceptance.");
            }
            finally
            {
                if(hold!=null)hold.Dispose();if(startup!=null)startup.Dispose();
                if(viewObject!=null)UnityEngine.Object.DestroyImmediate(viewObject);
                if(cameraObject!=null)UnityEngine.Object.DestroyImmediate(cameraObject);
                if(scene.IsValid()&&scene.isLoaded)EditorSceneManager.CloseScene(scene,true);
            }
        }
    }
}
#endif
