#if UNITY_EDITOR && STROOP_XR_PREVIEW
using System;
using System.Reflection;
using AvatarStroop.Core;
using AvatarStroop.Runtime;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AvatarStroop.Unity.Editor
{
    public static class ViveEyeAoiFixture
    {
        sealed class Clock:IClock
        {double now=10;public double Now {get{return now+=.001;}}public string ClockId {get{return "synthetic-aoi";}}public void Advance(double seconds){now+=seconds;}}
        const BindingFlags Private=BindingFlags.Instance|BindingFlags.NonPublic;
        static int checks;
        static void Require(bool ok,string message)
        {checks++;if(!ok)throw new InvalidOperationException("VIVE AOI fixture: "+message);}
        static void Set(object target,string field,object value){target.GetType().GetField(field,Private).SetValue(target,value);}
        static TaskView Task(Phase phase,bool avatar,string trial="synthetic-aoi-trial")
        {
            var result=new TaskView();
            typeof(TaskView).GetProperty("Phase").SetValue(result,phase,null);
            typeof(TaskView).GetProperty("BlockIndex").SetValue(result,2,null);
            typeof(TaskView).GetProperty("TrialId").SetValue(result,trial,null);
            typeof(TaskView).GetProperty("OperatorAvatarVisible").SetValue(result,avatar,null);
            return result;
        }
        static ViveEyeFrame Eye(Camera camera,Vector3 target,bool validOrigin=true)
        {
            Vector3 local=Quaternion.Inverse(camera.transform.rotation)*(target-camera.transform.position).normalized;
            var ray=new ViveEyeRay(validOrigin?3UL:2UL,validOrigin,true,new Vec3(0,0,0),new Vec3(-local.x,local.y,local.z));
            return new ViveEyeFrame(41,937,9,false,ray,ray,ray);
        }
        static GameObject Root(Scene scene,string name)
        {var root=new GameObject(name);root.hideFlags=HideFlags.HideAndDontSave;SceneManager.MoveGameObjectToScene(root,scene);return root;}
        static void CameraPass(WorldSpaceExperimentView view,Camera camera)
        {
            // Exercise both real camera callback handlers, including pre-cull
            // scene identity and the completed render promotion, not just a cache
            // helper that would miss the new-frame readiness regression.
            typeof(WorldSpaceExperimentView).GetMethod("BeforeCameraCull",Private).Invoke(view,new object[]{camera});
            typeof(WorldSpaceExperimentView).GetMethod("AfterCameraRender",Private).Invoke(view,new object[]{camera});
        }
        public static void Validate()
        {
            checks=0;ViveSyntheticFixtureIsolation.OpenKnownSavedAnchor();
            var scene=EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Additive);
            GameObject cameraObject=null,viewObject=null;
            try
            {
                cameraObject=Root(scene,"AoiCamera");var camera=cameraObject.AddComponent<Camera>();
                viewObject=Root(scene,"AoiView");var view=viewObject.AddComponent<WorldSpaceExperimentView>();view.Initialize(camera,Actor.Participant);
                camera.transform.position=new Vector3(0,1.6f,0);camera.transform.rotation=Quaternion.identity;camera.cullingMask=~0;
                Set(view,"currentView",Task(Phase.MainTask,false));var clock=new Clock();
                bool headCurrent=false;view.SetPresentationAvailabilityCheck(()=>headCurrent);
                view.BindViveEyeAoiRenderClock(clock);
                var board=view.transform.Find("WorldSpaceContent/ClassroomEnvironment/Whiteboard/WritingArea");
                Require(board!=null,"actual writing area not found");
                var taskEye=Eye(camera,board.position);
                var absent=view.CaptureViveEyeAoi(taskEye,clock);
                Require(absent.Category=="invalid"&&absent.Reason=="no_completed_render_scene","missing snapshot silently classified");
                Require(absent.LastRenderFrame==-1&&!absent.LastRenderUnitySeconds.HasValue,"unobserved render was invented");
                CameraPass(view,camera);
                Require(view.CaptureViveEyeAoi(taskEye,clock).Reason=="no_completed_render_scene","failed readiness callback manufactured a scene");
                headCurrent=true;CameraPass(view,camera);
                // Reproduce the actual failure: the new frame has not read head
                // tracking yet. Binding must not overwrite last render evidence.
                headCurrent=false;view.BindViveEyeAoiRenderClock(clock);
                var task=view.CaptureViveEyeAoi(taskEye,clock);
                Require(task.Category=="task"&&task.ActiveTask&&!task.AvatarAoiApplicable,"D task surface or structural social N/A incorrect");
                Require(task.Geometry.Count==6&&task.Geometry[0].Name=="task"&&task.Geometry[0].Enabled&&!task.Geometry[1].Enabled,"fixed geometry identities/visibility missing");
                Require(Math.Abs(task.Geometry[0].ScaleWorld.X-1.38)<.0001&&Math.Abs(task.Geometry[0].ScaleWorld.Y-1.08)<.0001,"AOI not actual fixed writing area");
                Require(task.NativeFrame==41&&task.NativeTimestamp==937&&task.EyeObservedLocalSeconds==9,"native sample linkage changed");
                Require(task.CaptureCompletedLocalSeconds>=task.CaptureLocalSeconds&&task.SceneCaptureCompletedLocalSeconds>=task.SceneCaptureLocalSeconds&&
                    task.SceneCaptureCompletedLocalSeconds<=task.CaptureLocalSeconds,"software capture brackets not ordered");
                Require(task.LastRenderFrame==Time.frameCount&&task.LastRenderUnitySeconds.HasValue,"completed camera proxy not retained");
                Require(task.SceneUnityFrame==task.LastRenderFrame&&task.TaskRevision==task.LastRenderTaskRevision&&
                    task.AvatarRevision==task.LastRenderAvatarRevision,"geometry does not identify the completed camera pass");
                headCurrent=true;

                Set(view,"currentView",Task(Phase.MainTask,true));view.AvatarRoot.gameObject.SetActive(true);
                view.AvatarRoot.position=new Vector3(.65f,.9f,3);view.AvatarRoot.rotation=Quaternion.Euler(0,180,0);view.AvatarHead.localRotation=Quaternion.identity;
                var left=view.AvatarHead.Find("LeftEye/White");var head=view.AvatarHead.Find("HeadShape");
                CameraPass(view,camera);var eye=view.CaptureViveEyeAoi(Eye(camera,left.position),clock);
                Require(eye.Category=="eyes"&&eye.AvatarAoiApplicable,"eye surface not exclusive ahead of face");
                var cheek=view.CaptureViveEyeAoi(Eye(camera,view.AvatarHead.TransformPoint(new Vector3(0,-.10f,.15f))),clock);
                Require(cheek.Category=="remaining_face","front face outside eyes not classified");
                var other=view.CaptureViveEyeAoi(Eye(camera,camera.transform.position+Vector3.up*3),clock);
                Require(other.Category=="other","valid off-target ray is not other");
                var invalid=view.CaptureViveEyeAoi(Eye(camera,left.position,false),clock);
                Require(invalid.Category=="invalid"&&invalid.Reason=="invalid_combined_ray","invalid origin silently assigned other or camera-origin fallback");

                // Reproduce the new-frame readiness gate and real guard after
                // a render: neither a failed post-render nor later mutation may
                // replace completed geometry with disabled/new-frame geometry.
                var cachedEye=Eye(camera,left.position);camera.cullingMask=0;view.AvatarRoot.gameObject.SetActive(false);
                view.AvatarRoot.position+=Vector3.right*4;
                headCurrent=false;view.BindViveEyeAoiRenderClock(clock);CameraPass(view,camera);
                var afterMask=view.CaptureViveEyeAoi(cachedEye,clock);
                Require(afterMask.Category=="eyes"&&afterMask.Geometry[2].Enabled,"new-frame readiness or mask destroyed completed-render geometry");
                Require(afterMask.Geometry[2].PositionWorld.X==eye.Geometry[2].PositionWorld.X,"cached geometry mutated after capture");
                Set(view,"presentationRevision",afterMask.TaskRevision+1);
                Require(view.CaptureViveEyeAoi(cachedEye,clock).Reason=="scene_task_context_changed","changed task revision reused old scene");
                Set(view,"presentationRevision",afterMask.TaskRevision);
                Set(view,"currentView",Task(Phase.MainTask,true,"changed-trial"));
                Require(view.CaptureViveEyeAoi(cachedEye,clock).Reason=="scene_task_context_changed","changed task reused old scene");
                Set(view,"currentView",Task(Phase.Questionnaire,false));
                Require(view.CaptureViveEyeAoi(cachedEye,clock).Category=="outside_task","questionnaire entered active AOI denominator");

                camera.cullingMask=~0;headCurrent=true;view.AvatarRoot.gameObject.SetActive(true);view.AvatarRoot.position=new Vector3(.65f,.9f,3);
                Set(view,"currentView",Task(Phase.MainTask,true));
                camera.transform.position=new Vector3(0,1.6f,5);camera.transform.rotation=Quaternion.Euler(0,180,0);
                CameraPass(view,camera);
                Require(view.CaptureViveEyeAoi(Eye(camera,head.position),clock).Category=="other","back of head counted as face/eyes");
                camera.transform.position=new Vector3(-.5f,1.55f,0);camera.transform.rotation=Quaternion.identity;
                view.AvatarRoot.position=new Vector3(-.5f,.85f,3);
                CameraPass(view,camera);
                Require(view.CaptureViveEyeAoi(Eye(camera,board.position),clock).Category=="task","nearer board did not occlude head proxy");
                camera.cullingMask=0;CameraPass(view,camera);clock.Advance(.101);
                Require(view.CaptureViveEyeAoi(taskEye,clock).Reason=="stale_render_scene","stale scene survived missing render interval");
                var recent=typeof(WorldSpaceExperimentView).GetMethod("IsRecentViveEyeAoiScene",BindingFlags.Static|BindingFlags.NonPublic);
                Require((bool)recent.Invoke(null,new object[]{12,13,1.0,1.01}),"previous completed frame not eligible");
                Require(!(bool)recent.Invoke(null,new object[]{12,14,1.0,1.01}),"skipped Unity frame not rejected");
                Require(!(bool)recent.Invoke(null,new object[]{14,13,1.0,1.01}),"future scene frame accepted");
                Require(!(bool)recent.Invoke(null,new object[]{12,13,1.0,1.101}),"long single-frame stall not rejected");
                view.BindViveEyeAoiRenderClock(new Clock());
                Require(view.CaptureViveEyeAoi(taskEye,12).Reason=="no_completed_render_scene","clock change retained another session's geometry");

                // Distances remain world metres for rotated, non-uniform shapes.
                head.position=new Vector3(2,3,4);head.rotation=Quaternion.Euler(12,31,7);head.localScale=new Vector3(.4f,.8f,.6f);
                var axis=head.TransformDirection(Vector3.forward).normalized;
                var method=typeof(WorldSpaceExperimentView).GetMethod("TryIntersectAoiSurface",BindingFlags.Static|BindingFlags.NonPublic);
                object[] hit={head,false,head.position-axis*2,axis,0f,Vector3.zero};
                Require((bool)method.Invoke(null,hit)&&Math.Abs((float)hit[4]-1.7)<.001,"ellipsoid hit distance ignored transformed scale");
                object[] behind={head,false,head.position-axis*2,-axis,0f,Vector3.zero};
                Require(!(bool)method.Invoke(null,behind),"ray intersected behind its origin");
                Debug.Log("VIVE_EYE_AOI_SYNTHETIC_FIXTURE_PASSED: "+checks+" checks; no physical accuracy or synchronization claim.");
            }
            finally
            {
                if(viewObject!=null)UnityEngine.Object.DestroyImmediate(viewObject);
                if(cameraObject!=null)UnityEngine.Object.DestroyImmediate(cameraObject);
                if(scene.IsValid())EditorSceneManager.CloseScene(scene,true);
            }
        }
    }
}
#endif
