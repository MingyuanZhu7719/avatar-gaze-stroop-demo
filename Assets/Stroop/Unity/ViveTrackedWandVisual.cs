#if STROOP_XR_PREVIEW
using System;
using System.Collections.Generic;
using UnityEngine;

namespace AvatarStroop.Unity
{
    // A local visual of the currently selected physical wand. It is not an
    // input device, an interactable, or evidence that a participant saw it.
    // Axes are Unity tracked-device local: +Z toward hoop, +Y controls.
    internal sealed class ViveTrackedWandVisual : MonoBehaviour
    {
        Transform model;
        GameObject trackingHint;
        int visibleFrame=-1;
        readonly List<Mesh> meshes=new List<Mesh>();
        readonly List<Material> materials=new List<Material>();
        internal bool Visible {get{return model!=null&&model.gameObject.activeInHierarchy;}}
        internal bool TrackingHintVisible {get{return trackingHint!=null&&trackingHint.activeInHierarchy;}}
        internal static ViveTrackedWandVisual Create(Transform trackingSpace,int layer=0)
        {
            if(trackingSpace==null)throw new ArgumentNullException("trackingSpace");
            if(layer<0||layer>31)throw new ArgumentOutOfRangeException("layer");
            var root=new GameObject("Tracked Vive wand");root.SetActive(false);root.layer=layer;
            root.transform.SetParent(trackingSpace,false);
            var visual=root.AddComponent<ViveTrackedWandVisual>();
            try{visual.Build(layer);root.SetActive(true);return visual;}
            catch{DestroyOwned(root);throw;}
        }

        internal bool Apply(bool eligible,bool poseValid,Vector3 trackingPosition,Quaternion trackingRotation,int observationFrame)
        {
            if(!eligible||!poseValid||observationFrame!=Time.frameCount||!isActiveAndEnabled||model==null||
                !Finite(trackingPosition.x)||!Finite(trackingPosition.y)||!Finite(trackingPosition.z)||
                !Unit(trackingRotation)){Hide();return false;}
            // Set the current exact tracking-local transform BEFORE enabling
            // geometry, so a new/recovered model cannot flash at the origin.
            transform.SetLocalPositionAndRotation(trackingPosition,trackingRotation.normalized);
            visibleFrame=observationFrame;
            if(trackingHint!=null&&trackingHint.activeSelf)trackingHint.SetActive(false);
            model.gameObject.SetActive(true);
            return true;
        }
        internal void ConfigureCamera(Camera camera,int hintLayer=0)
        {
            if(camera==null)throw new ArgumentNullException("camera");
            if(hintLayer<0||hintLayer>31)throw new ArgumentOutOfRangeException("hintLayer");
            if(trackingHint!=null)DestroyOwned(trackingHint);
            trackingHint=new GameObject("Vive wand tracking loss");trackingHint.SetActive(false);trackingHint.layer=hintLayer;
            try
            {
                trackingHint.transform.SetParent(camera.transform,false);
                trackingHint.transform.localPosition=new Vector3(0,-.30f,1.20f);
                var text=trackingHint.AddComponent<TextMesh>();
                text.font=Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
                text.fontSize=64;text.characterSize=.009f;text.color=new Color(.93f,.94f,.96f);
                text.anchor=TextAnchor.MiddleCenter;text.alignment=TextAlignment.Center;text.richText=false;
                text.text="Controller view unavailable.\nCheck controller tracking.";
                var renderer=text.GetComponent<MeshRenderer>();renderer.sharedMaterial=text.font.material;
                renderer.shadowCastingMode=UnityEngine.Rendering.ShadowCastingMode.Off;renderer.receiveShadows=false;
            }
            catch{DestroyOwned(trackingHint);trackingHint=null;throw;}
        }
        internal bool ShowTrackingUnavailable(bool eligible,int observationFrame)
        {
            Hide();
            if(!eligible||observationFrame!=Time.frameCount||!isActiveAndEnabled||trackingHint==null)return false;
            visibleFrame=observationFrame;trackingHint.SetActive(true);return true;
        }
        internal void Hide()
        {
            visibleFrame=-1;
            if(model!=null&&model.gameObject.activeSelf)model.gameObject.SetActive(false);
            if(trackingHint!=null&&trackingHint.activeSelf)trackingHint.SetActive(false);
        }
        void ValidateFrame(){if(visibleFrame!=Time.frameCount)Hide();}
        void GuardRender(Camera camera){ValidateFrame();}
        void OnEnable(){Camera.onPreCull+=GuardRender;ValidateFrame();}
        void OnDisable(){Camera.onPreCull-=GuardRender;Hide();}
        void OnDestroy()
        {
            Camera.onPreCull-=GuardRender;
            DestroyOwned(trackingHint);trackingHint=null;
            foreach(var mesh in meshes)DestroyOwned(mesh);
            foreach(var material in materials)DestroyOwned(material);
            meshes.Clear();materials.Clear();
        }
        static void DestroyOwned(UnityEngine.Object value)
        {if(value==null)return;if(Application.isPlaying)Destroy(value);else DestroyImmediate(value);}
        static bool Finite(float value){return !float.IsNaN(value)&&!float.IsInfinity(value);}
        static bool Unit(Quaternion q)
        {
            if(!Finite(q.x)||!Finite(q.y)||!Finite(q.z)||!Finite(q.w))return false;
            float n=q.x*q.x+q.y*q.y+q.z*q.z+q.w*q.w;
            return Finite(n)&&Mathf.Abs(n-1f)<=.02f;
        }

        Material Paint(Shader shader,Color color)
        {
            var material=new Material(shader){color=color,hideFlags=HideFlags.DontSave};
            materials.Add(material);return material;
        }
        void Build(int layer)
        {
            var shader=Shader.Find("Unlit/Color"); // Already used by the authored classroom.
            if(shader==null)throw new InvalidOperationException("Tracked wand visual shader unavailable.");
            model=new GameObject("Wand geometry").transform;
            model.gameObject.layer=layer;model.SetParent(transform,false);model.gameObject.SetActive(false);
            var shell=Paint(shader,new Color(.105f,.12f,.155f));
            var rim=Paint(shader,new Color(.19f,.22f,.28f));
            var dark=Paint(shader,new Color(.051f,.059f,.081f));
            var pad=Paint(shader,new Color(.23f,.28f,.36f));
            var accent=Paint(shader,new Color(.31f,.56f,.77f));

            // Approximately 21 cm from handle tip to tracking hoop, expressed
            // in meters. The hoop is a real open mesh, not a painted hole.
            Part("Open tracking hoop",Ring(.040f,.009f,.007f),new Vector3(0,.003f,.049f),rim,layer);
            Part("Hoop underside",Ring(.040f,.008f,.004f),new Vector3(0,-.003f,.049f),shell,layer);
            Part("Tracking neck",Oval(.020f,.012f,.029f),new Vector3(0,.002f,.006f),shell,layer);
            Part("Grip shell",Oval(.019f,.016f,.060f),new Vector3(0,0,-.052f),shell,layer);
            Part("Upper control housing",Oval(.023f,.014f,.034f),new Vector3(0,.009f,-.022f),rim,layer);
            Part("Trackpad bezel",Oval(.0190f,.0048f,.0185f),new Vector3(0,.0200f,-.0240f),dark,layer);
            Part("Round trackpad",Oval(.0167f,.0033f,.0163f),new Vector3(0,.0230f,-.0240f),pad,layer);
            Part("Menu button",Oval(.0047f,.0023f,.0030f),new Vector3(0,.0223f,.0005f),dark,layer);
            Part("System button",Oval(.0040f,.0021f,.0038f),new Vector3(0,.0159f,-.0510f),dark,layer);
            Part("Small grip accent",Oval(.0020f,.0014f,.0024f),new Vector3(0,.0140f,-.060f),accent,layer);
            Part("Underside trigger",Oval(.012f,.0100f,.0150f),new Vector3(0,-.0165f,-.017f),dark,layer,
                Quaternion.Euler(-21,0,0));
            Part("Left squeeze detail",Oval(.003f,.005f,.020f),new Vector3(-.017f,0,-.058f),dark,layer);
            Part("Right squeeze detail",Oval(.003f,.005f,.020f),new Vector3(.017f,0,-.058f),dark,layer);
        }
        void Part(string label,Mesh mesh,Vector3 offset,Material material,int layer,Quaternion? localRotation=null)
        {
            meshes.Add(mesh);
            var part=new GameObject(label);part.layer=layer;part.transform.SetParent(model,false);
            part.transform.SetLocalPositionAndRotation(offset,localRotation??Quaternion.identity);
            part.AddComponent<MeshFilter>().sharedMesh=mesh;
            var renderer=part.AddComponent<MeshRenderer>();renderer.sharedMaterial=material;
            renderer.shadowCastingMode=UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows=false;
        }
        static Mesh Surface(int uCount,int vCount,Func<float,float,Vector3> point,bool flip=false)
        {
            var vertices=new Vector3[(uCount+1)*(vCount+1)];
            var triangles=new int[uCount*vCount*6];
            int stride=vCount+1;
            for(int u=0;u<=uCount;u++)
                for(int v=0;v<=vCount;v++)
                    vertices[u*stride+v]=point((float)u/uCount,(float)v/vCount);
            for(int u=0,i=0;u<uCount;u++)
                for(int v=0;v<vCount;v++)
                {
                    int a=u*stride+v,b=a+1,c=a+stride,d=c+1;
                    triangles[i++]=a;triangles[i++]=flip?c:b;triangles[i++]=flip?b:c;
                    triangles[i++]=b;triangles[i++]=flip?c:d;triangles[i++]=flip?d:c;
                }
            var mesh=new Mesh{hideFlags=HideFlags.DontSave,vertices=vertices,triangles=triangles};
            mesh.RecalculateNormals();mesh.RecalculateBounds();return mesh;
        }
        static Mesh Ring(float radius,float width,float height)
        {
            return Surface(48,10,(u,v)=>{
                float a=u*Mathf.PI*2,b=v*Mathf.PI*2,r=radius+width*Mathf.Cos(b);
                return new Vector3(r*Mathf.Cos(a),height*Mathf.Sin(b),r*Mathf.Sin(a));
            });
        }
        static Mesh Oval(float x,float y,float z)
        {
            return Surface(24,12,(u,v)=>{
                float a=u*Mathf.PI*2,b=(v-.5f)*Mathf.PI,r=Mathf.Cos(b);
                return new Vector3(x*r*Mathf.Cos(a),y*Mathf.Sin(b),z*r*Mathf.Sin(a));
            });
        }
    }
}
#endif
