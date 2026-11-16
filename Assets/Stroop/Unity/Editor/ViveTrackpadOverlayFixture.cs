#if UNITY_EDITOR && STROOP_XR_PREVIEW
using System;
using System.Reflection;
using AvatarStroop.Runtime;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace AvatarStroop.Unity.Editor
{
    // Actual camera pixels, with an isolated native-format model payload. This
    // verifies the mapping axes and grip transform, not physical headset fit.
    public static class ViveTrackpadOverlayFixture
    {
        const BindingFlags Private=BindingFlags.Instance|BindingFlags.NonPublic;
        static int checks;
        static void Require(bool value,string detail)
        {checks++;if(!value)throw new InvalidOperationException("Vive trackpad overlay: "+detail);}
        static object Call(object value,string name,params object[] args)
        {return value.GetType().GetMethod(name,Private).Invoke(value,args);}
        static object Field(object value,string name){return value.GetType().GetField(name,Private).GetValue(value);}
        static bool Flag(object value,string name){return (bool)value.GetType().GetProperty(name,Private).GetValue(value,null);}
        static GameObject Root(Scene scene,string name)
        {var value=new GameObject(name);SceneManager.MoveGameObjectToScene(value,scene);return value;}
        static object Payload(string name)
        {
            var source=typeof(ViveDevelopmentParticipant).Assembly.GetType("AvatarStroop.Runtime.ViveSteamVrRenderModelSource",true);
            var type=source.GetNestedType("Model",BindingFlags.NonPublic);var model=Activator.CreateInstance(type,true);
            type.GetField("Name",Private).SetValue(model,name);
            // Body geometry is deliberately outside the pad-focused camera.
            type.GetField("Vertices",Private).SetValue(model,new float[]{.4f,0,.4f,0,1,0,0,0, .5f,0,.4f,0,1,0,1,0, .5f,0,.5f,0,1,0,1,1});
            type.GetField("Indices",Private).SetValue(model,new[]{0,1,2});
            type.GetField("Rgba",Private).SetValue(model,new byte[]{0,0,0,255});
            type.GetField("Width",Private).SetValue(model,1);type.GetField("Height",Private).SetValue(model,1);return model;
        }
        static Color32[] Render(Camera camera)
        {
            var target=new RenderTexture(512,512,24,RenderTextureFormat.ARGB32);var image=new Texture2D(512,512,TextureFormat.RGB24,false);
            var oldTarget=camera.targetTexture;var oldActive=RenderTexture.active;int mask=camera.cullingMask;
            try
            {
                Require(target.Create(),"render target failed");camera.targetTexture=target;camera.Render();
                Require(camera.cullingMask==mask,"render altered task mask");
                RenderTexture.active=target;image.ReadPixels(new Rect(0,0,512,512),0,0,false);image.Apply();return image.GetPixels32();
            }
            finally{camera.targetTexture=oldTarget;RenderTexture.active=oldActive;target.Release();UnityEngine.Object.DestroyImmediate(target);UnityEngine.Object.DestroyImmediate(image);}
        }
        static bool Matches(Color32 value,int colour)
        {
            if(colour==0)return value.r>180&&value.g<50&&value.b<50;
            if(colour==1)return value.g>180&&value.r<50&&value.b<50;
            if(colour==2)return value.b>180&&value.r<50&&value.g<50;
            return value.r>180&&value.g>180&&value.b<50;
        }
        static int Painted(Color32[] values)
        {int n=0;foreach(var value in values)if(value.r>8||value.g>8||value.b>8)n++;return n;}
        public static void Validate()
        {
            checks=0;Require(Application.isBatchMode&&!Application.isPlaying&&SystemInfo.graphicsDeviceType!=GraphicsDeviceType.Null,"isolated graphics Editor required");
            ViveSyntheticFixtureIsolation.OpenKnownSavedAnchor();
            var scene=EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Additive);MonoBehaviour wand=null;
            try
            {
                var origin=Root(scene,"Trackpad fixture tracking space").transform;
                origin.SetPositionAndRotation(new Vector3(2,.3f,-1),Quaternion.Euler(0,31,0));
                var camera=Root(scene,"Trackpad colour pixel camera").AddComponent<Camera>();
                camera.enabled=false;camera.cullingMask=0;camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=Color.black;
                camera.orthographic=true;camera.orthographicSize=.025f;camera.nearClipPlane=.01f;camera.farClipPlane=2;
                var type=typeof(ViveStroopPreviewRig).Assembly.GetType("AvatarStroop.Unity.ViveTrackedWandVisual",true);
                wand=(MonoBehaviour)type.GetMethod("Create",BindingFlags.NonPublic|BindingFlags.Static).Invoke(null,new object[]{origin,0});
                Call(wand,"OnDisable");Call(wand,"OnEnable");Call(wand,"ConfigureCamera",camera,0);
                float[] nativeGrip={0,0,1,.03f,0,1,0,.04f,-1,0,0,-.05f};
                Call(wand,"InstallModel",Payload("vr_controller_vive_1_5"),nativeGrip);
                Call(wand,"Apply",true,true,new Vector3(.2f,.5f,.4f),Quaternion.Euler(10,20,15),Time.frameCount);
                var model=(Transform)Field(wand,"model");
                var tilt=Quaternion.Euler(-6.55f,0,0);var centre=new Vector3(0,.002f,-.049f)+tilt*new Vector3(0,.0035f,0);
                Vector3 worldCentre=model.TransformPoint(centre),normal=model.TransformDirection(tilt*Vector3.up),up=model.TransformDirection(tilt*Vector3.forward);
                camera.transform.SetPositionAndRotation(worldCentre+normal*.25f,Quaternion.LookRotation(-normal,up));
                Require(Painted(Render(camera))==0&&!Flag(wand,"TrackpadMappingVisible"),"mapping appeared before explicit task permission");
                Call(wand,"SetTrackpadMapping",true);var pixels=Render(camera);
                Require(Flag(wand,"Visible")&&Flag(wand,"TrackpadMappingVisible"),"allowed mapping hid the native visual");
                // With camera up matched to physical pad up: red top, green
                // right, blue bottom, yellow left. Nonidentity grip and origin
                // above ensure an accidental second grip transform fails here.
                int[] xs={256,399,256,113},ys={399,256,113,256};
                for(int colour=0;colour<4;colour++)
                {
                    bool found=false;for(int y=ys[colour]-2;y<=ys[colour]+2;y++)for(int x=xs[colour]-2;x<=xs[colour]+2;x++)
                        if(Matches(pixels[y*512+x],colour))found=true;
                    Require(found,"wrong colour/position for physical direction "+colour);
                }
                Require(pixels[256*512+256].r<8&&pixels[256*512+256].g<8&&pixels[256*512+256].b<8,"centre should remain neutral");
                var sectors=(Mesh[])Field(wand,"trackpadSectors");var materials=(Material[])Field(wand,"trackpadMaterials");
                Require(sectors.Length==4&&materials.Length==4,"four cached colour sectors required");
                foreach(var mesh in sectors)Require(mesh.normals.Length==mesh.vertexCount&&mesh.normals[0].sqrMagnitude>.99f,"sector normals unavailable");
                foreach(var material in materials)Require(material.GetFloat("_SurfaceLighting")==0,"mapping colours must bypass model surface lighting");
                Render(camera);Require(System.Object.ReferenceEquals(sectors,Field(wand,"trackpadSectors")),"same mapping allocated new geometry per render");
                Call(wand,"SetTrackpadMapping",false);
                Require(Painted(Render(camera))==0&&Flag(wand,"Visible"),"disabling colour mapping hid model or retained colour pixels");
                Call(wand,"SetTrackpadMapping",true);wand.GetType().GetField("visibleFrame",Private).SetValue(wand,Time.frameCount-1);
                Require(Painted(Render(camera))==0&&!Flag(wand,"TrackpadMappingVisible"),"stale tracking retained colour mapping");
                Call(wand,"InstallModel",Payload("unsupported-synthetic-controller"),nativeGrip);
                Call(wand,"Apply",true,true,new Vector3(.2f,.5f,.4f),Quaternion.Euler(10,20,15),Time.frameCount);
                Require(Painted(Render(camera))==0&&!Flag(wand,"TrackpadMappingVisible"),"Vive-specific mapping projected onto another controller model");
                foreach(var mesh in sectors)Require(mesh==null,"replacement model leaked overlay mesh");
                foreach(var material in materials)Require(material==null,"replacement model leaked overlay material");
                Require(wand.GetComponentsInChildren<Renderer>(true).Length==0&&wand.GetComponentsInChildren<Collider>(true).Length==0,
                    "mapping added task-visible scene renderers or interactive colliders");
                Debug.Log("VIVE_TRACKPAD_OVERLAY_FIXTURE_PASSED: "+checks+" checks; actual four-direction colour pixels with transformed grip, opt-in/disabled/unsupported/stale gates, cached mesh and disposal. Synthetic camera evidence only.");
            }
            finally
            {
                if(wand!=null){Call(wand,"OnDestroy");UnityEngine.Object.DestroyImmediate(wand.gameObject);}
                EditorSceneManager.CloseScene(scene,true);
            }
        }
    }
}
#endif
