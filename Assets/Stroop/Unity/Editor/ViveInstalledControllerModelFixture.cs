#if UNITY_EDITOR && STROOP_XR_PREVIEW
using System;
using System.IO;
using System.Reflection;
using AvatarStroop.Runtime;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AvatarStroop.Unity.Editor
{
    // Optional installed-asset check. SteamVR must already be running; this
    // uses its background model client, never starts an XR scene or tracks a hand.
    public static class ViveInstalledControllerModelFixture
    {
        const BindingFlags Flags=BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
        static object Call(object value,string method,params object[] args)
        {return (value as Type??value.GetType()).GetMethod(method,Flags).Invoke(value is Type?null:value,args);}
        static object Field(object value,string field){return value.GetType().GetField(field,Flags).GetValue(value);}
        static void Require(bool condition,string message)
        {if(!condition)throw new InvalidOperationException("Installed controller model: "+message);}
        public static void Validate()
        {
            ViveSyntheticFixtureIsolation.OpenKnownSavedAnchor();
            string output=Path.Combine(Environment.GetEnvironmentVariable("STROOP_VIVE_FIXTURE_WORKSPACE")??Path.GetDirectoryName(Application.dataPath),"work","installed-controller-preview");
            var args=Environment.GetCommandLineArgs();for(int i=0;i<args.Length-1;i++)if(args[i]=="-stroopControllerPreviewOutput")output=Path.GetFullPath(args[i+1]);
            var sourceType=typeof(ViveDevelopmentParticipant).Assembly.GetType("AvatarStroop.Runtime.ViveSteamVrRenderModelSource",true);
            var visualType=typeof(ViveStartupView).Assembly.GetType("AvatarStroop.Unity.ViveTrackedWandVisual",true);
            IDisposable source=null;GameObject cameraObject=null,tracking=null;RenderTexture target=null;Texture2D pixels=null;
            var scene=default(Scene);var previous=RenderTexture.active;
            try
            {
                source=(IDisposable)Activator.CreateInstance(sourceType,true);
                Require((bool)Call(source,"TryOpen"),"SteamVR must already be running: "+sourceType.GetProperty("LastIssue",Flags).GetValue(source,null));
                object data=null;var deadline=DateTime.UtcNow.AddSeconds(15);
                while(data==null&&DateTime.UtcNow<deadline){data=Call(source,"PollModel","vr_controller_vive_1_5");if(data==null)System.Threading.Thread.Sleep(20);}
                Require(data!=null,"installed Vive model/texture loading timed out");
                object[] grip={"vr_controller_vive_1_5",null};Require((bool)Call(source,"TryGrip",grip),"installed grip component unavailable");
                scene=EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Additive);
                cameraObject=new GameObject("Installed controller fixture camera");tracking=new GameObject("Synthetic controller tracking space");
                SceneManager.MoveGameObjectToScene(cameraObject,scene);SceneManager.MoveGameObjectToScene(tracking,scene);
                var camera=cameraObject.AddComponent<Camera>();camera.cullingMask=0;camera.clearFlags=CameraClearFlags.SolidColor;
                camera.backgroundColor=Color.black;camera.nearClipPlane=.01f;camera.fieldOfView=40;camera.aspect=1;
                var visual=(Component)Call(visualType,"Create",tracking.transform,0);
                Call(visual,"OnDisable");Call(visual,"OnEnable");Call(visual,"InstallModel",data,grip[1]);Call(visual,"ConfigureCamera",camera,0);
                Require((bool)Call(visual,"Apply",true,true,new Vector3(0,0,.7f),Quaternion.Euler(25,-25,5),Time.frameCount),"synthetic grip pose rejected");
                var mesh=(Mesh)Field(visual,"mesh");var model=(Transform)Field(visual,"model");var centre=model.TransformPoint(mesh.bounds.center);
                camera.transform.position=centre+new Vector3(.15f,.25f,-1).normalized*(mesh.bounds.extents.magnitude/Mathf.Tan(20*Mathf.Deg2Rad)*1.2f);
                camera.transform.LookAt(centre);target=new RenderTexture(1024,1024,24,RenderTextureFormat.ARGB32);target.Create();
                camera.targetTexture=target;pixels=new Texture2D(1024,1024,TextureFormat.RGB24,false);
                camera.Render();RenderTexture.active=target;pixels.ReadPixels(new Rect(0,0,1024,1024),0,0);pixels.Apply();
                int visible=0;foreach(var pixel in pixels.GetPixels32())if(pixel.r>12||pixel.g>12||pixel.b>12)visible++;
                Require(camera.cullingMask==0&&visible>1000&&visible<900000,"masked-camera command buffer did not render a bounded visible controller: "+visible);
                Directory.CreateDirectory(output);string image=Path.Combine(output,"installed-vive-controller.png");File.WriteAllBytes(image,pixels.EncodeToPNG());
                Call(visual,"Hide");camera.Render();pixels.ReadPixels(new Rect(0,0,1024,1024),0,0);pixels.Apply();
                int hidden=0;foreach(var pixel in pixels.GetPixels32())if(pixel.r>12||pixel.g>12||pixel.b>12)hidden++;
                Require(hidden==0,"hidden controller or unrelated scene geometry leaked through the black camera");
                Debug.Log("VIVE_INSTALLED_CONTROLLER_MODEL_FIXTURE_PASSED: model=vr_controller_vive_1_5 vertices="+mesh.vertexCount+" triangles="+mesh.triangles.Length/3+" texture="+Field(data,"Width")+"x"+Field(data,"Height")+" visible_pixels="+visible+" png="+image+"; installed asset, grip conversion and shader only, synthetic pose; no XR scene or headset-pose acceptance.");
            }
            finally
            {
                RenderTexture.active=previous;
                if(tracking!=null)UnityEngine.Object.DestroyImmediate(tracking);
                if(cameraObject!=null)UnityEngine.Object.DestroyImmediate(cameraObject);
                if(pixels!=null)UnityEngine.Object.DestroyImmediate(pixels);
                if(target!=null){target.Release();UnityEngine.Object.DestroyImmediate(target);}
                if(source!=null)source.Dispose();
                if(scene.IsValid()&&scene.isLoaded)EditorSceneManager.CloseScene(scene,true);
            }
        }
    }
}
#endif
