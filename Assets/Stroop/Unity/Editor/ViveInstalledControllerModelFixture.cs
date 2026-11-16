#if UNITY_EDITOR && STROOP_XR_PREVIEW
using System;
using System.IO;
using System.Reflection;
using System.Text;
using AvatarStroop.Runtime;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace AvatarStroop.Unity.Editor
{
    // Optional installed-asset geometry/appearance check. It does not start an
    // XR scene and cannot establish physical pose or binocular display acceptance.
    public static class ViveInstalledControllerModelFixture
    {
        const BindingFlags Flags=BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
        static object Call(object value,string method,params object[] args)
        {return (value as Type??value.GetType()).GetMethod(method,Flags).Invoke(value is Type?null:value,args);}
        static object Field(object value,string field){return value.GetType().GetField(field,Flags).GetValue(value);}
        static void Require(bool condition,string message)
        {if(!condition)throw new InvalidOperationException("Installed controller model: "+message);}
        static Color32[] Capture(Camera camera,RenderTexture target,Texture2D pixels,string path=null)
        {
            camera.Render();RenderTexture.active=target;
            pixels.ReadPixels(new Rect(0,0,target.width,target.height),0,0);pixels.Apply();
            Require(camera.cullingMask==0,"controller rendering unmasked scene geometry");
            if(path!=null)File.WriteAllBytes(path,pixels.EncodeToPNG());return pixels.GetPixels32();
        }
        static int Count(Color32[] pixels,int threshold)
        {int result=0;foreach(var p in pixels)if(p.r>threshold||p.g>threshold||p.b>threshold)result++;return result;}
        static void CheckInterior(Camera camera,Transform model,Color32[] pixels,Vector3 nativeUnityPoint,string name)
        {
            var point=camera.WorldToScreenPoint(model.TransformPoint(nativeUnityPoint));
            int x=Mathf.RoundToInt(point.x),y=Mathf.RoundToInt(point.y),bright=0;
            Require(point.z>camera.nearClipPlane&&x>3&&y>3&&x<1020&&y<1020,name+": interior projection clipped");
            for(int dy=-3;dy<=3;dy++)for(int dx=-3;dx<=3;dx++)
            {var p=pixels[(y+dy)*1024+x+dx];if(p.r>25||p.g>25||p.b>25)bright++;}
            Require(bright>42,name+": expected a filled interior, got only "+bright+" / 49 visible pixels");
        }
        static void View(Camera camera,Transform model,Mesh mesh,Vector3 direction,Vector3 up,float distance)
        {
            Vector3 centre=model.TransformPoint(mesh.bounds.center);
            var n=model.TransformDirection(direction).normalized;
            camera.transform.SetPositionAndRotation(centre+n*distance,Quaternion.LookRotation(-n,model.TransformDirection(up)));
        }
        static void CheckFilledSurface(Camera camera,RenderTexture target,Texture2D pixels,Material material,
            string name,string output,StringBuilder report)
        {
            var texture=material.mainTexture;float lighting=material.GetFloat("_SurfaceLighting");
            Color32[] silhouette;
            try
            {
                // Separate true opaque coverage from the dark native texture.
                material.mainTexture=Texture2D.whiteTexture;material.SetFloat("_SurfaceLighting",0);
                silhouette=Capture(camera,target,pixels);
            }
            finally{material.mainTexture=texture;material.SetFloat("_SurfaceLighting",lighting);}
            var textured=Capture(camera,target,pixels,Path.Combine(output,name+".png"));
            int white=0,filled=0,left=target.width,right=-1,bottom=target.height,top=-1;
            for(int i=0;i<silhouette.Length;i++)
            {
                var p=silhouette[i];if(p.r<230||p.g<230||p.b<230)continue;
                white++;int x=i%target.width,y=i/target.width;
                left=Math.Min(left,x);right=Math.Max(right,x);bottom=Math.Min(bottom,y);top=Math.Max(top,y);
                var t=textured[i];if(t.r>25||t.g>25||t.b>25)filled++;
            }
            int rectangle=(right-left+1)*(top-bottom+1);
            Require(white>9000&&white<850000,name+": bounded full mesh coverage failed: "+white);
            Require(left>1&&bottom>1&&right<target.width-2&&top<target.height-2,name+": body clipped against image edges");
            Require(rectangle>0&&(double)white/rectangle>.30,name+": only thin contour geometry remained");
            Require((double)filled/white>.94,name+": dark texture lost most opaque interior coverage");
            report.AppendLine(name+": opaque_pixels="+white+"; fill_ratio="+((double)white/rectangle).ToString("F4")+"; readable_fraction="+((double)filled/white).ToString("F4"));
        }
        public static void Validate()
        {
            ViveSyntheticFixtureIsolation.OpenKnownSavedAnchor();
            string output=Path.Combine(Environment.GetEnvironmentVariable("STROOP_VIVE_FIXTURE_WORKSPACE")??Path.GetDirectoryName(Application.dataPath),"work","installed-controller-preview");
            var args=Environment.GetCommandLineArgs();for(int i=0;i<args.Length-1;i++)if(args[i]=="-stroopControllerPreviewOutput")output=Path.GetFullPath(args[i+1]);
            var sourceType=typeof(ViveDevelopmentParticipant).Assembly.GetType("AvatarStroop.Runtime.ViveSteamVrRenderModelSource",true);
            var visualType=typeof(ViveStartupView).Assembly.GetType("AvatarStroop.Unity.ViveTrackedWandVisual",true);
            IDisposable source=null;GameObject cameraObject=null,tracking=null;RenderTexture target=null;Texture2D pixels=null;
            var scene=default(Scene);var previous=RenderTexture.active;var report=new StringBuilder();
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
                camera.backgroundColor=Color.black;camera.nearClipPlane=.05f;camera.farClipPlane=20;camera.fieldOfView=50;camera.aspect=1;
                var visual=(Component)Call(visualType,"Create",tracking.transform,0);
                Call(visual,"OnDisable");Call(visual,"OnEnable");Call(visual,"InstallModel",data,grip[1]);Call(visual,"ConfigureCamera",camera,0);
                Require(camera.GetCommandBuffers(CameraEvent.AfterForwardAlpha).Length==1&&camera.GetCommandBuffers(CameraEvent.AfterEverything).Length==0,
                    "controller geometry must precede final XR output/resolve");
                Require((bool)Call(visual,"Apply",true,true,new Vector3(0,1.1f,.4f),Quaternion.Euler(25,-25,5),Time.frameCount),"synthetic grip pose rejected");
                var mesh=(Mesh)Field(visual,"mesh");var model=(Transform)Field(visual,"model");var material=(Material)Field(visual,"material");
                target=new RenderTexture(1024,1024,24,RenderTextureFormat.ARGB32);target.Create();
                camera.targetTexture=target;pixels=new Texture2D(1024,1024,TextureFormat.RGB24,false);Directory.CreateDirectory(output);
                var tilt=Quaternion.Euler(-6.55f,0,0);Vector3 front=tilt*Vector3.up,up=tilt*Vector3.forward;
                var names=new[]{"installed-vive-front-030m","installed-vive-front-050m","installed-vive-oblique-035m","installed-vive-back-035m"};
                var directions=new[]{front,front,(front+Vector3.right*.65f).normalized,-front};
                var distances=new[]{.30f,.50f,.35f,.35f};
                for(int i=0;i<names.Length;i++)
                {
                    View(camera,model,mesh,directions[i],up,distances[i]);
                    CheckFilledSurface(camera,target,pixels,material,names[i],output,report);
                    if(i<2)
                    {
                        var interior=Capture(camera,target,pixels);
                        CheckInterior(camera,model,interior,new Vector3(0,.005f,-.049f),"native trackpad "+names[i]);
                        CheckInterior(camera,model,interior,new Vector3(0,.000087f,-.123392f),"native grip body "+names[i]);
                    }
                }
                View(camera,model,mesh,front,up,.30f);Call(visual,"SetTrackpadMapping",true);
                var coloured=Capture(camera,target,pixels,Path.Combine(output,"installed-vive-colour-trackpad.png"));
                var colours=new int[4];foreach(var p in coloured)
                {
                    if(p.r>180&&p.g<60&&p.b<60)colours[0]++;
                    if(p.r<60&&p.g>180&&p.b<60)colours[1]++;
                    if(p.r<60&&p.g<60&&p.b>180)colours[2]++;
                    if(p.r>180&&p.g>180&&p.b<60)colours[3]++;
                }
                for(int i=0;i<4;i++)Require(colours[i]>300,"official pad quadrant hidden or inside native mesh: "+i+"="+colours[i]);
                report.AppendLine("pad_pixels_red_green_blue_yellow="+String.Join(",",colours));
                Call(visual,"SetTrackpadMapping",false);
                Call(visual,"Hide");Require(Count(Capture(camera,target,pixels),8)==0,"hidden model leaked through black mask");
                Require((bool)Call(visual,"Apply",true,true,new Vector3(0,1.1f,.4f),Quaternion.Euler(25,-25,5),Time.frameCount),"pose could not recover");
                visualType.GetField("visibleFrame",Flags).SetValue(visual,Time.frameCount-1);
                Require(Count(Capture(camera,target,pixels),8)==0,"stale frame retained native model/markings");
                report.AppendLine("event=AfterForwardAlpha; near_m=0.05; no task scene layer or second camera; synthetic perspective views, not headset acceptance");
                File.WriteAllText(Path.Combine(output,"installed-model-checks.txt"),report.ToString());
                Debug.Log("VIVE_INSTALLED_CONTROLLER_MODEL_FIXTURE_PASSED: model=vr_controller_vive_1_5 vertices="+mesh.vertexCount+" triangles="+mesh.triangles.Length/3+
                    " texture="+Field(data,"Width")+"x"+Field(data,"Height")+"; 4 filled-surface views at 0.30-0.50m; actual native pad colour coverage; hidden/stale clears; output="+output+
                    "; installed asset and shader with synthetic poses; no XR/headset acceptance.");
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
