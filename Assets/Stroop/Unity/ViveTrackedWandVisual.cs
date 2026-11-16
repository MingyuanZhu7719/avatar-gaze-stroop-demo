#if STROOP_XR_PREVIEW
using System;
using AvatarStroop.Runtime;
using UnityEngine;
using UnityEngine.Rendering;

namespace AvatarStroop.Unity
{
    // The runtime supplies the actual device mesh/texture. It has no Renderer,
    // collider or input component: only this camera's visual command buffer can
    // draw it, even when the task camera's scene layer mask is safely closed.
    internal sealed partial class ViveTrackedWandVisual : MonoBehaviour
    {
        Transform model;
        GameObject trackingHint;
        Mesh mesh;
        Material material;
        Texture2D texture;
        Camera ownerCamera;
        CommandBuffer draw;
        int visibleFrame=-1;
        int loggedEyes;
        internal const CameraEvent DrawEvent=CameraEvent.AfterForwardAlpha;
        internal string ModelName {get;private set;}
        internal bool Visible {get{return model!=null&&model.gameObject.activeInHierarchy&&visibleFrame==Time.frameCount;}}
        internal bool TrackingHintVisible {get{return trackingHint!=null&&trackingHint.activeInHierarchy;}}
        internal static ViveTrackedWandVisual Create(Transform trackingSpace,int layer=0)
        {
            if(trackingSpace==null)throw new ArgumentNullException("trackingSpace");
            if(layer<0||layer>31)throw new ArgumentOutOfRangeException("layer");
            var root=new GameObject("Tracked runtime controller");root.SetActive(false);root.layer=layer;
            root.transform.SetParent(trackingSpace,false);
            var visual=root.AddComponent<ViveTrackedWandVisual>();
            visual.model=new GameObject("Runtime controller model").transform;
            visual.model.SetParent(root.transform,false);visual.model.gameObject.SetActive(false);
            root.SetActive(true);return visual;
        }
        internal void InstallModel(ViveSteamVrRenderModelSource.Model data,float[] trackingToGrip)
        {
            Hide();
            if(data==null||String.IsNullOrEmpty(data.Name)||data.Vertices==null||data.Vertices.Length<24||data.Vertices.Length%8!=0||
                data.Indices==null||data.Indices.Length<3||data.Indices.Length%3!=0||data.Rgba==null||
                data.Width<1||data.Height<1||data.Width>4096||data.Height>4096||data.Rgba.Length!=data.Width*data.Height*4)
                throw new ArgumentException("Invalid runtime controller model");
            Matrix4x4 local=GripToModel(trackingToGrip);
            int count=data.Vertices.Length/8;
            var vertices=new Vector3[count];var normals=new Vector3[count];var uv=new Vector2[count];
            for(int i=0;i<count;i++)
            {
                int p=i*8;
                for(int j=0;j<8;j++)if(!Finite(data.Vertices[p+j]))throw new ArgumentException("Nonfinite model vertex");
                vertices[i]=new Vector3(data.Vertices[p],data.Vertices[p+1],-data.Vertices[p+2]);
                normals[i]=new Vector3(data.Vertices[p+3],data.Vertices[p+4],-data.Vertices[p+5]);
                uv[i]=new Vector2(data.Vertices[p+6],data.Vertices[p+7]);
                if(vertices[i].sqrMagnitude>4)throw new ArgumentException("Controller model is not meter scale");
            }
            var indices=new int[data.Indices.Length];
            for(int i=0;i<indices.Length;i+=3)
            {
                for(int j=0;j<3;j++)if(data.Indices[i+j]<0||data.Indices[i+j]>=count)throw new ArgumentException("Invalid model index");
                indices[i]=data.Indices[i];indices[i+1]=data.Indices[i+2];indices[i+2]=data.Indices[i+1];
            }
            var shader=Resources.Load<Shader>("ViveControllerModel");
            if(shader==null||!shader.isSupported)throw new InvalidOperationException("Controller texture shader unavailable");
            ClearModel();
            mesh=new Mesh{name=data.Name,hideFlags=HideFlags.DontSave,vertices=vertices,normals=normals,uv=uv,triangles=indices};
            mesh.RecalculateBounds();
            texture=new Texture2D(data.Width,data.Height,TextureFormat.RGBA32,false,false){name=data.Name+" texture",hideFlags=HideFlags.DontSave};
            texture.LoadRawTextureData(data.Rgba);texture.Apply(false,true);
            material=new Material(shader){name="Runtime controller texture",mainTexture=texture,hideFlags=HideFlags.DontSave,enableInstancing=true};
            model.SetLocalPositionAndRotation(local.GetColumn(3),local.rotation);model.localScale=Vector3.one;
            ModelName=data.Name;
        }
        // OpenVR model origin -> grip component, expressed in Unity coordinates.
        // Unity's tracked grip pose therefore needs the inverse component pose.
        internal static Matrix4x4 GripToModel(float[] native)
        {
            if(native==null||native.Length!=12)throw new ArgumentException("Missing grip component");
            var m=Matrix4x4.identity;
            for(int r=0;r<3;r++)for(int c=0;c<4;c++)
            {float x=native[r*4+c];if(!Finite(x))throw new ArgumentException("Invalid grip component");m[r,c]=x*(r==2?-1:1)*(c==2?-1:1);}
            var xAxis=(Vector3)m.GetColumn(0);var yAxis=(Vector3)m.GetColumn(1);var zAxis=(Vector3)m.GetColumn(2);
            if(Mathf.Abs(xAxis.sqrMagnitude-1)>.002f||Mathf.Abs(yAxis.sqrMagnitude-1)>.002f||Mathf.Abs(zAxis.sqrMagnitude-1)>.002f||
                Mathf.Abs(Vector3.Dot(xAxis,yAxis))>.002f||Mathf.Abs(Vector3.Dot(xAxis,zAxis))>.002f||Mathf.Abs(Vector3.Dot(yAxis,zAxis))>.002f||
                Mathf.Abs(m.determinant-1)>.003f||((Vector3)m.GetColumn(3)).sqrMagnitude>1)
                throw new ArgumentException("Grip component is not a rigid meter transform");
            return m.inverse;
        }
        internal bool Apply(bool eligible,bool poseValid,Vector3 trackingPosition,Quaternion trackingRotation,int observationFrame)
        {
            if(!eligible||!poseValid||observationFrame!=Time.frameCount||!isActiveAndEnabled||model==null||mesh==null||material==null||
                !Finite(trackingPosition.x)||!Finite(trackingPosition.y)||!Finite(trackingPosition.z)||!Unit(trackingRotation))
            {Hide();return false;}
            transform.SetLocalPositionAndRotation(trackingPosition,trackingRotation.normalized);
            visibleFrame=observationFrame;
            if(trackingHint!=null)trackingHint.SetActive(false);
            model.gameObject.SetActive(true);return true;
        }
        internal void ConfigureCamera(Camera camera,int hintLayer=0)
        {
            if(camera==null)throw new ArgumentNullException("camera");
            if(hintLayer<0||hintLayer>31)throw new ArgumentOutOfRangeException("hintLayer");
            DetachBuffer();ownerCamera=camera;
            draw=new CommandBuffer{name="Current tracked runtime controller only"};
            ownerCamera.AddCommandBuffer(DrawEvent,draw);
            DestroyOwned(trackingHint);
            trackingHint=new GameObject("Vive wand tracking loss");trackingHint.SetActive(false);trackingHint.layer=hintLayer;
            trackingHint.transform.SetParent(camera.transform,false);
            trackingHint.transform.localPosition=new Vector3(0,-.30f,1.20f);
            var text=trackingHint.AddComponent<TextMesh>();text.font=Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            text.fontSize=64;text.characterSize=.009f;text.color=new Color(.93f,.94f,.96f);
            text.anchor=TextAnchor.MiddleCenter;text.alignment=TextAlignment.Center;text.richText=false;
            text.text="Controller view unavailable.\nCheck controller tracking.";
            var renderer=text.GetComponent<MeshRenderer>();renderer.sharedMaterial=text.font.material;
            renderer.shadowCastingMode=ShadowCastingMode.Off;renderer.receiveShadows=false;
        }
        internal bool ShowTrackingUnavailable(bool eligible,int observationFrame)
        {
            Hide();
            if(!eligible||observationFrame!=Time.frameCount||!isActiveAndEnabled||trackingHint==null)return false;
            visibleFrame=observationFrame;trackingHint.SetActive(true);return true;
        }
        internal void Hide()
        {
            visibleFrame=-1;if(draw!=null)draw.Clear();
            if(model!=null)model.gameObject.SetActive(false);
            if(trackingHint!=null)trackingHint.SetActive(false);
        }
        void ValidateFrame(){if(visibleFrame!=Time.frameCount)Hide();}
        void GuardRender(Camera camera)
        {
            if(camera!=ownerCamera)return;
            if(draw!=null)draw.Clear();ValidateFrame();
            if(Visible&&draw!=null&&mesh!=null&&material!=null)
            {
                draw.DrawMesh(mesh,model.localToWorldMatrix,material,0,0);
                AppendTrackpadOverlay(draw,model.localToWorldMatrix);
                int eyeBit=1<<(int)camera.stereoActiveEye;
                if(Application.isPlaying&&(loggedEyes&eyeBit)==0)
                {
                    loggedEyes|=eyeBit;
                    Debug.Log("VIVE_CONTROLLER_DRAW: event="+DrawEvent+"; eye="+camera.stereoActiveEye+
                        "; stereo="+camera.stereoEnabled+"; near="+camera.nearClipPlane+
                        "; camera_local_controller="+camera.transform.InverseTransformPoint(transform.position).ToString("F3")+
                        "; model="+ModelName+"; triangles="+mesh.triangles.Length/3);
                }
            }
        }
        // Draw while the eye's geometry target, projection and depth buffer are
        // current, before final XR resolve/output. Keep commands until the next
        // owner pre-cull; Hide/Disable clear immediately and stale poses never draw.
        void OnEnable(){Camera.onPreCull+=GuardRender;ValidateFrame();}
        void OnDisable(){Camera.onPreCull-=GuardRender;Hide();}
        void ClearModel()
        {ClearTrackpadOverlay();DestroyOwned(mesh);DestroyOwned(material);DestroyOwned(texture);mesh=null;material=null;texture=null;ModelName=null;loggedEyes=0;}
        void DetachBuffer()
        {if(draw==null)return;if(ownerCamera!=null)ownerCamera.RemoveCommandBuffer(DrawEvent,draw);draw.Clear();draw.Release();draw=null;ownerCamera=null;}
        void OnDestroy()
        {
            Camera.onPreCull-=GuardRender;
            DetachBuffer();ClearModel();DestroyOwned(trackingHint);trackingHint=null;
        }
        static void DestroyOwned(UnityEngine.Object value)
        {if(value==null)return;if(Application.isPlaying)Destroy(value);else DestroyImmediate(value);}
        static bool Finite(float value){return !float.IsNaN(value)&&!float.IsInfinity(value);}
        static bool Unit(Quaternion q)
        {
            if(!Finite(q.x)||!Finite(q.y)||!Finite(q.z)||!Finite(q.w))return false;
            float n=q.x*q.x+q.y*q.y+q.z*q.z+q.w*q.w;return Finite(n)&&Mathf.Abs(n-1f)<=.02f;
        }
    }
}
#endif
