#if UNITY_EDITOR && STROOP_XR_PREVIEW
using System;
using System.IO;
using System.Reflection;
using AvatarStroop.Runtime;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace AvatarStroop.Unity.Editor
{
    // Synthetic native payload exercises the production mesh and camera path.
    // Official installed-model PNGs use the separate native-source fixture.
    public static class ViveTrackedWandVisualFixture
    {
        const BindingFlags Private=BindingFlags.Instance|BindingFlags.NonPublic;
        static int checks;
        static readonly float[] Identity={1,0,0,0,0,1,0,0,0,0,1,0};
        static void Require(bool condition,string error)
        {checks++;if(!condition)throw new InvalidOperationException("Tracked wand fixture: "+error);}
        static object Call(object candidate,string name,params object[] args)
        {return candidate.GetType().GetMethod(name,Private|BindingFlags.Public).Invoke(candidate,args);}
        static object Get(object candidate,string name)
        {return candidate.GetType().GetField(name,Private).GetValue(candidate);}
        static bool Flag(object candidate,string name)
        {return (bool)candidate.GetType().GetProperty(name,Private).GetValue(candidate,null);}
        static Type SourceType
        {get{return typeof(ViveDevelopmentParticipant).Assembly.GetType("AvatarStroop.Runtime.ViveSteamVrRenderModelSource",true);}}
        static MonoBehaviour Create(Transform parent,int layer)
        {
            var type=typeof(ViveStroopPreviewRig).Assembly.GetType("AvatarStroop.Unity.ViveTrackedWandVisual",true);
            var visual=(MonoBehaviour)type.GetMethod("Create",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,new object[]{parent,layer});
            // Non-ExecuteAlways behaviours do not reliably receive edit-mode
            // lifecycle callbacks. Wire the real callbacks once for Camera.Render.
            Call(visual,"OnDisable");Call(visual,"OnEnable");return visual;
        }
        static object SyntheticModel()
        {
            var type=SourceType.GetNestedType("Model",BindingFlags.NonPublic);
            var value=Activator.CreateInstance(type,true);
            type.GetField("Name",Private).SetValue(value,"synthetic-native-rhs-quad");
            type.GetField("Vertices",Private).SetValue(value,new float[]{
                -.14f,-.10f,-.04f,0,0,1,0,0, .14f,-.10f,-.04f,0,0,1,1,0,
                .14f,.10f,-.04f,0,0,1,1,1, -.14f,.10f,-.04f,0,0,1,0,1});
            type.GetField("Indices",Private).SetValue(value,new[]{0,1,2,0,2,3});
            type.GetField("Rgba",Private).SetValue(value,new byte[]{64,220,180,255});
            type.GetField("Width",Private).SetValue(value,1);type.GetField("Height",Private).SetValue(value,1);
            return value;
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
        static void DestroyVisual(MonoBehaviour wand)
        {if(wand==null)return;Call(wand,"OnDestroy");UnityEngine.Object.DestroyImmediate(wand.gameObject);}
        static void RememberedPoseFixture(Scene scene)
        {
            var origin=Root(scene,"Synthetic tracking space").transform;
            origin.SetPositionAndRotation(new Vector3(2,.4f,3),Quaternion.Euler(0,27,0));
            var camera=Root(scene,"Synthetic moving eye camera").AddComponent<Camera>();camera.enabled=false;camera.cullingMask=0;
            var wand=Create(origin,3);
            try
            {
                Call(wand,"ConfigureCamera",camera,4);
                Require(wand.transform.parent==origin&&!Flag(wand,"Visible")&&!Flag(wand,"TrackingHintVisible"),"initial pose flashed");
                Require(!(bool)Call(wand,"Apply",true,true,Vector3.zero,Quaternion.identity,Time.frameCount),"pose shown without a runtime model");
                float[] grip={0,0,1,.03f,0,1,0,.04f,-1,0,0,-.05f};
                Call(wand,"InstallModel",SyntheticModel(),grip);
                var mesh=(Mesh)Get(wand,"mesh");var model=(Transform)Get(wand,"model");
                Require(wand.GetComponentsInChildren<Renderer>(true).Length==0&&wand.GetComponentsInChildren<Collider>(true).Length==0&&
                    wand.GetComponentsInChildren<Rigidbody>(true).Length==0,"model must have no scene renderer or physics");
                Require(mesh.vertexCount==4&&Mathf.Abs(mesh.bounds.size.x-.28f)<.00001f&&Mathf.Abs(mesh.vertices[0].z-.04f)<.00001f&&
                    mesh.normals[0]==Vector3.back,"native geometry conversion or meter scale changed");
                int[] triangles=mesh.triangles;
                Require(triangles.Length==6&&triangles[0]==0&&triangles[1]==2&&triangles[2]==1&&triangles[4]==3&&triangles[5]==2,
                    "native triangle handedness was not reversed");
                Matrix4x4 expected=Matrix4x4.TRS(new Vector3(.03f,.04f,.05f),Quaternion.Euler(0,-90,0),Vector3.one).inverse;
                Require((model.localPosition-(Vector3)expected.GetColumn(3)).sqrMagnitude<1e-8f&&Quaternion.Angle(model.localRotation,expected.rotation)<.05f,
                    "model origin did not use inverse converted grip component");
                Require(camera.GetCommandBuffers(CameraEvent.AfterEverything).Length==1&&camera.cullingMask==0,"camera buffer attachment changed task mask");
                var position=new Vector3(.26f,1.15f,.38f);var rotation=Quaternion.Euler(-12,38,19);int frame=Time.frameCount;
                Require((bool)Call(wand,"Apply",true,true,position,rotation,frame)&&Flag(wand,"Visible"),"valid current wand not shown");
                Require((wand.transform.position-origin.TransformPoint(position)).sqrMagnitude<1e-8f&&
                    Quaternion.Angle(wand.transform.rotation,origin.rotation*rotation)<.05f,"did not use exact tracking-space pose");
                var nearUnit=new Quaternion(rotation.x*1.005f,rotation.y*1.005f,rotation.z*1.005f,rotation.w*1.005f);
                Require((bool)Call(wand,"Apply",true,true,position,nearUnit,frame)&&Quaternion.Angle(wand.transform.rotation,origin.rotation*rotation)<.05f,
                    "adapter-valid quaternion did not normalize for visual-only pose");
                Vector3 world=wand.transform.position;camera.transform.position+=new Vector3(1,-2,3);
                Require((wand.transform.position-world).sqrMagnitude<1e-8f,"model followed head camera rather than tracking space");
                Require(!(bool)Call(wand,"Apply",false,true,position,rotation,frame)&&!Flag(wand,"Visible"),"ineligible visual retained geometry");
                Require(!(bool)Call(wand,"Apply",true,false,position,rotation,frame)&&!Flag(wand,"Visible"),"tracking loss retained pose");
                Require((bool)Call(wand,"ShowTrackingUnavailable",true,frame)&&!Flag(wand,"Visible")&&Flag(wand,"TrackingHintVisible"),"missing tracking was not exclusively text");
                var hint=camera.transform.Find("Vive wand tracking loss");
                Require(hint!=null&&hint.GetComponent<TextMesh>()!=null&&hint.gameObject.layer==4&&hint.GetComponent<Collider>()==null,
                    "tracking hint must be distinct, camera-relative and noninteractive");
                Require((bool)Call(wand,"Apply",true,true,position*.5f,rotation,frame)&&Flag(wand,"Visible")&&!Flag(wand,"TrackingHintVisible"),"recovery did not replace hint");
                Require(!(bool)Call(wand,"Apply",true,true,position,rotation,frame-1)&&!Flag(wand,"Visible"),"stale frame reappeared");
                Require(!(bool)Call(wand,"ShowTrackingUnavailable",true,frame-1)&&!Flag(wand,"TrackingHintVisible"),"stale hint persisted");
                Require(!(bool)Call(wand,"Apply",true,true,new Vector3(float.NaN,0,0),rotation,frame)&&
                    !(bool)Call(wand,"Apply",true,true,position,new Quaternion(0,0,0,0),frame),"invalid native pose accepted");
                Call(wand,"Apply",true,true,position,rotation,frame);wand.GetType().GetField("visibleFrame",Private).SetValue(wand,frame-1);
                Call(wand,"GuardRender",camera);Require(!Flag(wand,"Visible")&&!Flag(wand,"TrackingHintVisible"),"pre-cull retained an absent publication");
                var texture=(Texture2D)Get(wand,"texture");var material=(Material)Get(wand,"material");
                DestroyVisual(wand);wand=null;
                Require(mesh==null&&texture==null&&material==null,"owned mesh, texture or material leaked");
                Require(camera.transform.Find("Vive wand tracking loss")==null&&camera.GetCommandBuffers(CameraEvent.AfterEverything).Length==0,
                    "hint or camera command buffer leaked on teardown");
            }
            finally{DestroyVisual(wand);}
        }
        static Camera CameraFor(Scene scene,string name)
        {
            var camera=Root(scene,name).AddComponent<Camera>();camera.enabled=false;camera.cullingMask=0;
            camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=Color.black;camera.orthographic=true;
            camera.orthographicSize=.24f;camera.nearClipPlane=.01f;camera.farClipPlane=4;return camera;
        }
        static int RenderPixels(Camera camera,string path=null)
        {
            const int width=640,height=480;
            var rt=new RenderTexture(width,height,24,RenderTextureFormat.ARGB32);
            var image=new Texture2D(width,height,TextureFormat.RGB24,false);var active=RenderTexture.active;var prior=camera.targetTexture;
            int mask=camera.cullingMask;
            try
            {
                Require(rt.Create(),"could not allocate graphics output");camera.targetTexture=rt;camera.Render();
                Require(camera.cullingMask==mask,"visual rendering altered the task mask");
                RenderTexture.active=rt;image.ReadPixels(new Rect(0,0,width,height),0,0,false);image.Apply();
                int painted=0;foreach(var pixel in image.GetPixels32())if(pixel.r>8||pixel.g>8||pixel.b>8)painted++;
                if(path!=null)File.WriteAllBytes(path,image.EncodeToPNG());return painted;
            }
            finally{camera.targetTexture=prior;RenderTexture.active=active;rt.Release();UnityEngine.Object.DestroyImmediate(rt);UnityEngine.Object.DestroyImmediate(image);}
        }
        static void ActualCameraFixture(Scene scene)
        {
            Require(SystemInfo.graphicsDeviceType!=GraphicsDeviceType.Null,"graphics mode required for command-buffer pixels; do not use -nographics");
            var origin=Root(scene,"Pixel tracking origin").transform;
            var camera=CameraFor(scene,"Owner pixel camera");var other=CameraFor(scene,"Other pixel camera");
            var wand=Create(origin,3);var startup=new ViveStartupView(camera);startup.Hide();
            try
            {
                Call(wand,"ConfigureCamera",camera,4);Call(wand,"InstallModel",SyntheticModel(),Identity);
                Require(RenderPixels(camera)==0,"hidden model rendered into closed task mask");
                Require((bool)Call(wand,"Apply",true,true,new Vector3(0,0,.8f),Quaternion.identity,Time.frameCount),"pixel pose unavailable");
                Require(RenderPixels(other)==0&&Flag(wand,"Visible"),"another camera rendered or invalidated the owner model");
                int pixels=RenderPixels(camera);
                Require(pixels>20000&&pixels<100000,"AfterEverything did not actually render bounded pixels: "+pixels);
                Require(RenderPixels(other)==0,"owner render leaked commands into another camera");
                Require(!startup.WasRenderedBefore(Time.frameCount+1),"controller-only pixels fabricated a hidden startup witness");
                wand.GetType().GetField("visibleFrame",Private).SetValue(wand,Time.frameCount-1);
                Require(RenderPixels(camera)==0&&!Flag(wand,"Visible")&&((CommandBuffer)Get(wand,"draw")).sizeInBytes==0,"new frame rendered or retained stale controller commands");
                Call(wand,"Apply",true,true,new Vector3(0,0,.8f),Quaternion.identity,Time.frameCount);
                Require(RenderPixels(camera)>20000,"fresh pose did not restore camera pixels");
                Call(wand,"Apply",true,false,new Vector3(0,0,.8f),Quaternion.identity,Time.frameCount);
                Require(RenderPixels(camera)==0,"tracking failure retained old draw commands");
                Call(wand,"Apply",true,true,new Vector3(0,0,.8f),Quaternion.identity,Time.frameCount);Call(wand,"OnDisable");
                Require(RenderPixels(camera)==0,"disabled visual retained old draw commands");
                Require(camera.cullingMask==0&&other.cullingMask==0&&camera.GetComponent<ViveStroopPreviewRig>()==null,
                    "pixel fixture unexpectedly enabled task presentation or a rig");
            }
            finally{DestroyVisual(wand);startup.Dispose();}
        }
        public static void Validate()
        {
            checks=0;var scene=NewIsolatedScene();
            try{RememberedPoseFixture(scene);ActualCameraFixture(scene);}
            finally{CloseIsolatedScene(scene);}
            Debug.Log("VIVE_TRACKED_WAND_VISUAL_FIXTURE_PASSED: "+checks+" checks; native mesh/grip conversion, actual black-mask camera pixels, other-camera isolation, stale/invalid/disabled clearing and disposal. Synthetic graphics evidence, not headset acceptance.");
        }
        static string NewOutput()
        {
            string value=Environment.GetEnvironmentVariable("STROOP_VIVE_WAND_FIXTURE_OUTPUT");
            string workspace=Environment.GetEnvironmentVariable("STROOP_VIVE_FIXTURE_WORKSPACE");
            Require(!String.IsNullOrEmpty(value)&&!String.IsNullOrEmpty(workspace)&&Path.IsPathRooted(value)&&Path.IsPathRooted(workspace),"explicit output and workspace paths required");
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
        public static void ValidateRender()
        {
            Validate();string output=NewOutput();var scene=NewIsolatedScene();MonoBehaviour wand=null;
            try
            {
                var camera=CameraFor(scene,"Synthetic native payload capture");
                var origin=Root(scene,"Synthetic model fixture origin").transform;wand=Create(origin,3);
                Call(wand,"ConfigureCamera",camera,4);Call(wand,"InstallModel",SyntheticModel(),Identity);
                camera.transform.SetPositionAndRotation(new Vector3(0,0,-.8f),Quaternion.identity);
                Call(wand,"Apply",true,true,Vector3.zero,Quaternion.identity,Time.frameCount);
                Require(RenderPixels(camera,Path.Combine(output,"synthetic-model-front.png"))>20000,"synthetic front view has no model pixels");
                Vector3 centre=new Vector3(0,0,.04f),eye=centre+new Vector3(.25f,.20f,-.7f);
                camera.transform.SetPositionAndRotation(eye,Quaternion.LookRotation(centre-eye,Vector3.up));
                Call(wand,"Apply",true,true,Vector3.zero,Quaternion.identity,Time.frameCount);
                Require(RenderPixels(camera,Path.Combine(output,"synthetic-model-angle.png"))>10000,"synthetic angled view has no model pixels");
                Debug.Log("VIVE_TRACKED_WAND_RENDER_FIXTURE_PASSED: synthetic native payload rendered with a closed scene mask. Official installed-model PNGs use ViveInstalledControllerModelFixture; no hardware acceptance.");
            }
            finally{DestroyVisual(wand);CloseIsolatedScene(scene);}
        }
    }
}
#endif
