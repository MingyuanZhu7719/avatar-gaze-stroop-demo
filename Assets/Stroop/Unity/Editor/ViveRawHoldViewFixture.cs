#if UNITY_EDITOR && STROOP_XR_PREVIEW
using System;
using System.Reflection;
using AvatarStroop.Core;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AvatarStroop.Unity.Editor
{
    public static class ViveRawHoldViewFixture
    {
        static int assertions;
        static void Require(bool value,string message)
        {if(!value)throw new InvalidOperationException("Neutral hold fixture: "+message);assertions++;}
        static object Call(object instance,string method,params object[] args)
        {
            var m=instance.GetType().GetMethod(method,BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic);
            if(method=="BeginRender"&&args.Length==3)args=new object[]{args[0],args[1],args[2],false};
            Require(m!=null,"missing "+method);return m.Invoke(instance,args);
        }
        static object Get(object instance,string field)
        {
            var f=instance.GetType().GetField(field,BindingFlags.Instance|BindingFlags.NonPublic);
            Require(f!=null,"missing "+field);return f.GetValue(instance);
        }
        static GameObject InScene(Scene scene,string name)
        {
            var item=new GameObject(name);SceneManager.MoveGameObjectToScene(item,scene);
            item.hideFlags=HideFlags.HideAndDontSave;return item;
        }
        public static void Validate()
        {
            assertions=0;ViveSyntheticFixtureIsolation.OpenKnownSavedAnchor();
            Scene scene=default(Scene);IDisposable hold=null;ViveStartupView startup=null;
            GameObject cameraObject=null,otherObject=null,taskObject=null,stimulus=null;
            try
            {
                scene=EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Additive);
                var camera=(cameraObject=InScene(scene,"Neutral test camera")).AddComponent<Camera>();
                var other=(otherObject=InScene(scene,"Other synthetic camera")).AddComponent<Camera>();
                var view=(taskObject=InScene(scene,"Task view fixture")).AddComponent<WorldSpaceExperimentView>();
                view.Initialize(camera,Actor.Participant);
                stimulus=InScene(scene,"Synthetic unrelated task renderable");stimulus.layer=0;
                stimulus.AddComponent<TextMesh>().text="NOT THE HOLD MESSAGE";
                var type=typeof(ViveStroopPreviewRig).Assembly.GetType("AvatarStroop.Unity.ViveRawHoldView",true);
                hold=(IDisposable)Activator.CreateInstance(type,BindingFlags.Instance|BindingFlags.NonPublic,null,new object[]{camera},null);
                var display=(GameObject)Get(hold,"root");var text=(TextMesh)Get(hold,"text");
                int holdMask=1<<display.layer;
                Require(holdMask!=0&&(holdMask&ViveStartupView.Mask)==0&&display.transform.parent==camera.transform,
                    "hold was not isolated from startup witnesses");
                Require(!display.activeSelf&&display.GetComponentsInChildren<MonoBehaviour>(true).Length==0,
                    "hold view contains an input or presentation-witness component");
                camera.cullingMask=1;other.cullingMask=1;
                Require(!(bool)Call(hold,"BeginRender",camera,true,false)&&camera.cullingMask==1,
                    "hold was drawn over a task-visible camera");
                camera.cullingMask=0;
                Require(!(bool)Call(hold,"BeginRender",other,true,false)&&other.cullingMask==1&&camera.cullingMask==0,
                    "hold was admitted to a different camera");
                Require(!(bool)Call(hold,"BeginRender",camera,false,false)&&camera.cullingMask==0&&!display.activeSelf,
                    "hold was admitted without a current tracked head");
                var oldClear=CameraClearFlags.Depth;var oldBackground=new Color(.1f,.2f,.3f,1);
                camera.clearFlags=oldClear;camera.backgroundColor=oldBackground;
                Require((bool)Call(hold,"BeginRender",camera,true,false),
                    "valid hold was not admitted to the masked camera");
                Require(camera.cullingMask==holdMask&&camera.clearFlags==CameraClearFlags.SolidColor&&
                    display.activeSelf&&text.text.Contains("Task on hold")&&(camera.cullingMask&(1<<stimulus.layer))==0,
                    "masked hold culling admitted task layers or omitted wording");
                foreach(var item in view.GetComponentsInChildren<Renderer>(true))
                    Require((camera.cullingMask&(1<<item.gameObject.layer))==0,"hold mask admits an actual task renderer");
                // Culling a head-relative notice is never a startup/choice or
                // raw task witness. Exercise the real render callback reset.
                Call(view,"SetViveParticipantRecordingChoice","Synthetic choice text must not be witnessed",1);
                Call(view,"BeforeCameraCull",camera);
                Call(view,"ObserveViveRecordingChoiceElement",camera,1);
                Call(view,"ObserveViveRecordingChoiceElement",camera,2);
                Call(view,"AfterCameraRender",camera);
                Require((int)Get(view,"recordingChoiceSeenRevision")<0&&(int)Get(view,"rawPanelCaptureFrame")<0&&
                    Get(view,"capturedAvatar")==null&&Get(view,"capturedTrial")==null,
                    "neutral hold render callback admitted a task or recording-choice witness");
                Call(hold,"EndRender");
                Require(camera.cullingMask==0&&!display.activeSelf&&camera.clearFlags==oldClear&&camera.backgroundColor==oldBackground,
                    "ending the hold re-opened the task or left its overlay/camera state active");
                // If the rig's post-render handler runs first, the final
                // camera mask is already zero before the view sees it.
                Require((bool)Call(hold,"BeginRender",camera,true,false),"could not prepare reverse callback order");
                Call(view,"BeforeCameraCull",camera);
                Call(view,"ObserveViveRecordingChoiceElement",camera,1);
                Call(view,"ObserveViveRecordingChoiceElement",camera,2);
                Call(hold,"EndRender");
                Require(camera.cullingMask==0,"rig post-render did not restore the zero task mask");
                Call(view,"AfterCameraRender",camera);
                Require((int)Get(view,"recordingChoiceSeenRevision")<0&&(int)Get(view,"rawPanelCaptureFrame")<0&&
                    Get(view,"capturedAvatar")==null&&Get(view,"capturedTrial")==null,
                    "reverse callback order admitted a task or recording-choice witness");
                Require((bool)Call(hold,"BeginRender",camera,true,false,true)&&
                    text.text.Contains("Controller view unavailable")&&camera.cullingMask==holdMask&&
                    (camera.cullingMask&(1<<stimulus.layer))==0,
                    "controller-unavailable notice altered the neutral hold boundary");
                Call(hold,"EndRender");
                Call(hold,"SetTransition",false,0.0,5.0);
                Call(hold,"SetPracticeInstruction","Choose the colour named by the word.");
                Require((bool)Call(hold,"BeginRender",camera,true,false)&&text.text.Contains("Practice starts in\n5")&&
                    text.text.Contains("named by the word")&&camera.cullingMask==holdMask,
                    "practice countdown omitted five-second warning or response rule");
                var sparks=(Transform)Get(hold,"completionSparks");
                Require(!sparks.gameObject.activeSelf,"practice warning leaked celebration");
                Call(hold,"EndRender");
                Call(hold,"SetBlockCompletion",1.0);
                Require((bool)Call(hold,"BeginRender",camera,true,false)&&text.text.Contains("Block complete!")&&sparks.gameObject.activeSelf,
                    "completed block has no fireworks before questionnaire");
                var firstSpark=sparks.GetChild(0).localPosition;Call(hold,"EndRender");
                Call(hold,"SetBlockCompletion",2.0);Call(hold,"BeginRender",camera,true,false);
                Require(sparks.GetChild(0).localPosition!=firstSpark,"fireworks are not animated");
                foreach(var mesh in sparks.GetComponentsInChildren<Renderer>(true))
                    Require(mesh.gameObject.layer==display.layer,"fireworks escaped the isolated hold layer");
                Call(hold,"EndRender");
                Call(hold,"SetTransition",true,6.0,Double.NaN);
                Require((bool)Call(hold,"BeginRender",camera,true,false)&&text.text.Contains("Thank you!")&&!sparks.gameObject.activeSelf,
                    "final thank-you repeats fireworks after D or omits completion wording");
                Call(hold,"EndRender");
                Call(hold,"SetBlockCompletion",1.0);
                Require((bool)Call(hold,"BeginRender",camera,true,true,true)&&text.text.Contains("Task blocked"),
                    "terminal stop was displayed as a routine hold");
                Require(!sparks.gameObject.activeSelf,"fault/withdrawal incorrectly received fireworks");
                UnityEngine.Object.DestroyImmediate(cameraObject);cameraObject=null;
                hold.Dispose();hold.Dispose();hold=null;
                Require(display==null,"destroyed camera retained its hold object");

                camera=(cameraObject=InScene(scene,"Destroyed startup camera")).AddComponent<Camera>();
                startup=new ViveStartupView(camera);startup.Show(true,"Synthetic startup prompt");
                UnityEngine.Object.DestroyImmediate(cameraObject);cameraObject=null;
                startup.Hide();startup.Dispose();startup.Dispose();startup=null;
                Require(true,"startup teardown without a live camera completed");
                Debug.Log("VIVE_RAW_HOLD_VIEW_FIXTURE_PASSED: "+assertions+" assertions. EditMode only; no XR, data, or writer.");
            }
            finally
            {
                if(hold!=null)hold.Dispose();if(startup!=null)startup.Dispose();
                foreach(var item in new[]{cameraObject,otherObject,taskObject,stimulus})
                    if(item!=null)UnityEngine.Object.DestroyImmediate(item);
                if(scene.IsValid()&&scene.isLoaded)EditorSceneManager.CloseScene(scene,true);
            }
        }
    }
}
#endif
