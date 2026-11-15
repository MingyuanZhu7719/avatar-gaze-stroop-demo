#if UNITY_EDITOR && STROOP_XR_PREVIEW
using System;
using System.IO;
using System.Reflection;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace AvatarStroop.Unity.Editor
{
    // Tests the generated mesh and renderer pixels. No headset or input SDK is
    // requested, and the separate rig/native fixture controls real phase policy.
    public static class ViveTrackedWandVisualFixture
    {
        const BindingFlags Private=BindingFlags.Instance|BindingFlags.NonPublic;
        static void Require(bool condition,string error)
        {if(!condition)throw new InvalidOperationException("Tracked wand fixture: "+error);}
        static object Call(object candidate,string name,params object[] args)
        {return candidate.GetType().GetMethod(name,Private).Invoke(candidate,args);}
        static bool Flag(object candidate,string name)
        {return (bool)candidate.GetType().GetProperty(name,Private).GetValue(candidate,null);}
        static MonoBehaviour Create(Transform parent,int layer)
        {
            var type=typeof(ViveTrackedWandVisualFixture).Assembly.GetType("AvatarStroop.Unity.ViveTrackedWandVisual")??
                Type.GetType("AvatarStroop.Unity.ViveTrackedWandVisual, Assembly-CSharp",true);
            return (MonoBehaviour)type.GetMethod("Create",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,new object[]{parent,layer});
        }
        static Scene NewIsolatedScene()
        {
            Require(Application.isBatchMode&&!Application.isPlaying,"run only in an isolated batch Editor");
            var active=SceneManager.GetActiveScene();
            var mode=SceneManager.sceneCount==1&&String.IsNullOrEmpty(active.path)?NewSceneMode.Single:NewSceneMode.Additive;
            return EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,mode);
        }
        static void CloseIsolatedScene(Scene scene)
        {
            if(SceneManager.sceneCount>1)EditorSceneManager.CloseScene(scene,true);
            else EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
        }
        static GameObject Root(Scene scene,string name)
        {var go=new GameObject(name);SceneManager.MoveGameObjectToScene(go,scene);return go;}
        static void RememberedPoseFixture(Scene scene)
        {
            var origin=Root(scene,"Synthetic tracking space").transform;
            origin.SetPositionAndRotation(new Vector3(2,.4f,3),Quaternion.Euler(0,27,0));
            var camera=Root(scene,"Synthetic moving eye camera").AddComponent<Camera>();
            camera.enabled=false;
            var wand=Create(origin,3);
            Call(wand,"ConfigureCamera",camera,4);
            Require(wand.transform.parent==origin&&!Flag(wand,"Visible")&&!Flag(wand,"TrackingHintVisible"),"initial pose flashed");
            var bounds=new Bounds(Vector3.zero,Vector3.zero);
            var renderers=wand.GetComponentsInChildren<Renderer>(true);
            Require(renderers.Length>=10&&wand.GetComponentsInChildren<Collider>(true).Length==0&&
                wand.GetComponentsInChildren<Rigidbody>(true).Length==0,"missing authored parts or unwanted physics");
            foreach(var part in renderers)
            {
                Require(part.gameObject.layer==3&&part.shadowCastingMode==ShadowCastingMode.Off&&
                    !part.receiveShadows&&!part.gameObject.activeInHierarchy,"layer/shadows/inactive creation contract failed");
                var mesh=part.GetComponent<MeshFilter>().sharedMesh;
                bounds.Encapsulate(mesh.bounds.min+part.transform.localPosition);
                bounds.Encapsulate(mesh.bounds.max+part.transform.localPosition);
            }
            Require(bounds.size.z>.19f&&bounds.size.z<.24f,"geometry did not use meter-scale physical size");
            foreach(var name in new[]{"Open tracking hoop","Round trackpad","Underside trigger","Grip shell"})
                Require(wand.transform.Find("Wand geometry/"+name)!=null,"missing "+name);
            var position=new Vector3(.26f,1.15f,.38f);
            var rotation=Quaternion.Euler(-12,38,19);
            int frame=Time.frameCount;
            Require((bool)Call(wand,"Apply",true,true,position,rotation,frame)&&Flag(wand,"Visible"),"valid current wand not shown");
            Require((wand.transform.position-origin.TransformPoint(position)).sqrMagnitude<1e-8f&&
                Quaternion.Angle(wand.transform.rotation,origin.rotation*rotation)<.05f,"did not use exact tracking-space pose");
            var nearUnit=new Quaternion(rotation.x*1.005f,rotation.y*1.005f,rotation.z*1.005f,rotation.w*1.005f);
            Require((bool)Call(wand,"Apply",true,true,position,nearUnit,frame)&&
                Quaternion.Angle(wand.transform.rotation,origin.rotation*rotation)<.05f,
                "adapter-valid quaternion did not normalize for visual-only pose");
            Vector3 world=wand.transform.position;
            camera.transform.position+=new Vector3(1,-2,3);
            Require((wand.transform.position-world).sqrMagnitude<1e-8f,"model followed head camera rather than tracking space");
            Require(!(bool)Call(wand,"Apply",false,true,position,rotation,frame)&&!Flag(wand,"Visible"),"disallowed phase exposed held geometry");
            Require(!(bool)Call(wand,"Apply",true,false,position,rotation,frame)&&!Flag(wand,"Visible"),"tracking loss retained pose");
            Require((bool)Call(wand,"ShowTrackingUnavailable",true,frame)&&!Flag(wand,"Visible")&&Flag(wand,"TrackingHintVisible"),
                "missing tracking was not exclusively text");
            var hint=camera.transform.Find("Vive wand tracking loss");
            Require(hint!=null&&hint.GetComponent<TextMesh>()!=null&&hint.gameObject.layer==4&&hint.GetComponent<Collider>()==null,
                "tracking hint must be distinct, camera-relative and noninteractive");
            Require((bool)Call(wand,"Apply",true,true,position*.5f,rotation,frame)&&Flag(wand,"Visible")&&!Flag(wand,"TrackingHintVisible"),
                "recovery failed to replace hint with current pose");
            Require(!(bool)Call(wand,"Apply",true,true,position,rotation,frame-1)&&!Flag(wand,"Visible"),
                "stale frame reappeared");
            Require(!(bool)Call(wand,"ShowTrackingUnavailable",true,frame-1)&&!Flag(wand,"TrackingHintVisible"),
                "stale hint persisted");
            Require(!(bool)Call(wand,"Apply",true,true,new Vector3(float.NaN,0,0),rotation,frame)&&
                !(bool)Call(wand,"Apply",true,true,position,new Quaternion(0,0,0,0),frame),"invalid native pose accepted");
            Call(wand,"Apply",true,true,position,rotation,frame);
            wand.GetType().GetField("visibleFrame",Private).SetValue(wand,frame-1);
            Call(wand,"GuardRender",camera);
            Require(!Flag(wand,"Visible")&&!Flag(wand,"TrackingHintVisible"),"pre-cull failed to expire absent publication");
            // Edit-mode DestroyImmediate does not run the lifecycle of
            // runtime-only MonoBehaviours. Invoke the real disposal callback.
            Call(wand,"OnDestroy");
            UnityEngine.Object.DestroyImmediate(wand.gameObject);
            Require(camera.transform.Find("Vive wand tracking loss")==null,"separately parented hint leaked on teardown");
        }
        public static void Validate()
        {
            var scene=NewIsolatedScene();
            try{RememberedPoseFixture(scene);}
            finally{CloseIsolatedScene(scene);}
            Debug.Log("VIVE_TRACKED_WAND_VISUAL_FIXTURE_PASSED: tracking space, pose, invalid/stale/disabled visibility, meter scale, noninteractive geometry and hint.");
        }
        static string NewOutput()
        {
            string value=Environment.GetEnvironmentVariable("STROOP_VIVE_WAND_FIXTURE_OUTPUT");
            string workspace=Environment.GetEnvironmentVariable("STROOP_VIVE_FIXTURE_WORKSPACE");
            Require(!String.IsNullOrEmpty(value)&&!String.IsNullOrEmpty(workspace)&&
                Path.IsPathRooted(value)&&Path.IsPathRooted(workspace),"explicit output and workspace paths required");
            var output=Path.GetFullPath(value).TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar);
            var root=Path.GetFullPath(workspace).TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar);
            var project=Path.GetFullPath(Path.Combine(Application.dataPath,".."));
            Require(output.StartsWith(root+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)&&
                !output.StartsWith(project+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)&&output!=project&&
                !Directory.Exists(output)&&!File.Exists(output),"output must be a new workspace child outside project");
            var parent=new DirectoryInfo(Path.GetDirectoryName(output));
            while(parent!=null){Require(parent.Exists&&(parent.Attributes&FileAttributes.ReparsePoint)==0,"invalid output ancestor");parent=parent.Parent;}
            Directory.CreateDirectory(output);return output;
        }
        static bool Similar(Color32 a,Color32 b)
        {return Math.Abs(a.r-b.r)+Math.Abs(a.g-b.g)+Math.Abs(a.b-b.b)<18;}
        static void Render(Camera camera,Transform wand,string path,bool verifyHole)
        {
            const int width=1000,height=880;
            var rt=new RenderTexture(width,height,24,RenderTextureFormat.ARGB32);
            var image=new Texture2D(width,height,TextureFormat.RGB24,false);
            var prior=RenderTexture.active;
            try
            {
                Require(rt.Create(),"could not allocate synthetic graphics output");
                camera.targetTexture=rt;camera.Render();
                RenderTexture.active=rt;image.ReadPixels(new Rect(0,0,width,height),0,0,false);image.Apply();
                var pixels=image.GetPixels32();var background=pixels[0];int silhouette=0;
                foreach(var pixel in pixels)if(!Similar(pixel,background))silhouette++;
                Require(silhouette>width*height*.04&&silhouette<width*height*.65,"clear pixels do not contain a bounded wand silhouette: "+silhouette);
                if(verifyHole)
                {
                    var center=camera.WorldToScreenPoint(wand.TransformPoint(new Vector3(0,0,.049f)));
                    var rim=camera.WorldToScreenPoint(wand.TransformPoint(new Vector3(.040f,0,.049f)));
                    Require(Similar(pixels[(int)center.y*width+(int)center.x],background)&&
                        !Similar(pixels[(int)rim.y*width+(int)rim.x],background),"ring centre was not genuinely open");
                }
                File.WriteAllBytes(path,image.EncodeToPNG());
            }
            finally
            {
                camera.targetTexture=null;RenderTexture.active=prior;rt.Release();
                UnityEngine.Object.DestroyImmediate(rt);UnityEngine.Object.DestroyImmediate(image);
            }
        }
        public static void ValidateRender()
        {
            Validate();
            Require(SystemInfo.graphicsDeviceType!=GraphicsDeviceType.Null,"graphics mode required; do not use -nographics");
            string output=NewOutput();
            var scene=NewIsolatedScene();
            MonoBehaviour wand=null;
            try
            {
                var origin=Root(scene,"Synthetic tracking space").transform;
                var camera=Root(scene,"Preview capture camera").AddComponent<Camera>();
                camera.enabled=false;camera.cullingMask=1<<3;camera.clearFlags=CameraClearFlags.SolidColor;
                camera.backgroundColor=new Color(.73f,.80f,.87f);camera.orthographic=true;
                camera.orthographicSize=.142f;camera.nearClipPlane=.01f;camera.farClipPlane=3;
                wand=Create(origin,3);
                Require((bool)Call(wand,"Apply",true,true,Vector3.zero,Quaternion.identity,Time.frameCount),"visible capture pose unavailable");
                camera.transform.SetPositionAndRotation(new Vector3(0,.32f,-.007f),Quaternion.LookRotation(Vector3.down,Vector3.forward));
                Render(camera,wand.transform,Path.Combine(output,"wand-top.png"),true);
                Vector3 eye=new Vector3(.14f,.22f,-.15f);
                camera.transform.SetPositionAndRotation(eye,Quaternion.LookRotation(new Vector3(0,0,-.01f)-eye,Vector3.forward));
                Require((bool)Call(wand,"Apply",true,true,Vector3.zero,Quaternion.identity,Time.frameCount),"second capture pose unavailable");
                Render(camera,wand.transform,Path.Combine(output,"wand-three-quarter.png"),false);
                Debug.Log("VIVE_TRACKED_WAND_RENDER_FIXTURE_PASSED: visible geometry and open hoop verified from rendered pixels.");
            }
            finally
            {
                if(wand!=null){Call(wand,"OnDestroy");UnityEngine.Object.DestroyImmediate(wand.gameObject);}
                CloseIsolatedScene(scene);
            }
        }
    }
}
#endif
