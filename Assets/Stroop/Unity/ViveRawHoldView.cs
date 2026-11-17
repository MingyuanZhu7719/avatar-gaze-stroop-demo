#if STROOP_XR_PREVIEW
using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace AvatarStroop.Unity
{
    // Not part of the task/recording view; no input, render witness, journal or
    // experiment callback. This layer also hosts the pre-practice warning and
    // normal terminal celebration, without revealing any task renderer.
    internal sealed class ViveRawHoldView : IDisposable
    {
        internal const int Layer=29,Mask=1<<Layer;
        readonly Camera camera;
        readonly GameObject root;
        readonly TextMesh text;
        readonly ViveHeadsetNoticeLayout noticeLayout;
        readonly Transform completionSparks;
        const int BurstCount=6,RaysPerBurst=16,TrailSegments=3;
        readonly Material[] sparkMaterials=new Material[BurstCount*TrailSegments];
        static readonly Vector2[] burstCentres={new Vector2(-.61f,.36f),new Vector2(.61f,.36f),
            new Vector2(-.57f,-.36f),new Vector2(.57f,-.36f),new Vector2(-.24f,.64f),new Vector2(.24f,.64f)};
        static readonly Color[] burstColours={new Color(.36f,.86f,.96f),new Color(1f,.80f,.36f),
            new Color(1f,.49f,.52f),new Color(.77f,.60f,1f),new Color(.46f,.95f,.72f),new Color(.51f,.68f,1f)};
        bool completed;
        bool blockCompleted,practiceIsExam,formalEyeWait;
        double completionElapsed,practiceRemaining=Double.NaN;
        string practiceInstruction="Get ready";
        bool active,disposed;
        CameraClearFlags oldClear;
        Color oldBackground;

        internal ViveRawHoldView(Camera camera)
        {
            if(camera==null)throw new ArgumentNullException("camera");
            this.camera=camera;
            root=new GameObject("ViveRawNeutralHold");
            root.SetActive(false);root.layer=Layer;
            try
            {
                root.transform.SetParent(camera.transform,false);
                root.transform.localPosition=new Vector3(0,0,1.4f);
                text=root.AddComponent<TextMesh>();
                text.font=Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
                text.fontSize=64;text.characterSize=.012f;text.color=Color.white;
                text.anchor=TextAnchor.MiddleCenter;text.alignment=TextAlignment.Center;text.richText=false;
                var renderer=text.GetComponent<MeshRenderer>();
                renderer.sharedMaterial=text.font.material;
                renderer.shadowCastingMode=ShadowCastingMode.Off;renderer.receiveShadows=false;
                noticeLayout=new ViveHeadsetNoticeLayout(camera,text);
                completionSparks=new GameObject("GentleCompletionFireworks").transform;
                completionSparks.gameObject.layer=Layer;completionSparks.SetParent(root.transform,false);
                var shader=Shader.Find("AvatarStroop/WorldSpaceOverlay");
                for(int burst=0;burst<BurstCount;burst++)for(int trail=0;trail<TrailSegments;trail++)
                    sparkMaterials[burst*TrailSegments+trail]=new Material(shader){color=burstColours[burst]};
                for(int burst=0;burst<BurstCount;burst++)for(int ray=0;ray<RaysPerBurst;ray++)for(int trail=0;trail<TrailSegments;trail++)
                {
                    var spark=GameObject.CreatePrimitive(PrimitiveType.Quad);spark.name="Spark"+burst+"_"+ray+"_"+trail;
                    spark.layer=Layer;spark.transform.SetParent(completionSparks,false);
                    var collider=spark.GetComponent<Collider>();if(collider!=null)UnityEngine.Object.DestroyImmediate(collider);
                    var mesh=spark.GetComponent<MeshRenderer>();mesh.sharedMaterial=sparkMaterials[burst*TrailSegments+trail];
                    mesh.shadowCastingMode=ShadowCastingMode.Off;mesh.receiveShadows=false;
                }
                completionSparks.gameObject.SetActive(false);
            }
            catch{Dispose();throw;}
        }
        internal void SetTransition(bool completed,double elapsed,double practiceRemaining)
        {this.completed=completed;blockCompleted=false;completionElapsed=elapsed;this.practiceRemaining=practiceRemaining;}
        internal void SetBlockCompletion(double elapsed)
        {completed=true;blockCompleted=true;completionElapsed=elapsed;practiceRemaining=Double.NaN;}
        internal void SetPracticeInstruction(string instruction){practiceInstruction=instruction??"Get ready";}
        internal void SetPracticeExam(bool exam){practiceIsExam=exam;}
        internal void SetFormalEyeWait(bool waiting){formalEyeWait=waiting;}
        internal bool BeginRender(Camera candidate,bool trackedNow,bool stopped,bool controllerUnavailable=false)
        {
            if(disposed||!trackedNow||camera==null||candidate!=camera||root==null||text==null||
                !camera.isActiveAndEnabled||camera.targetTexture!=null||camera.cullingMask!=0||active)return false;
            // Fixed strings only; no paths, native exceptions, raw data or
            // promises of recording, recovery or compositor delivery.
            bool celebrating=!stopped&&completed;
            bool practice=!stopped&&!controllerUnavailable&&!celebrating&&!Double.IsNaN(practiceRemaining)&&practiceRemaining>=0;
            string message=stopped?"Task blocked.\nCheck recording status\non the desktop.":
                celebrating?(blockCompleted?"Block complete!\nA few questions are next.":
                    completionElapsed<6?"All blocks complete!\nWell done!":"Thank you for participating!\nYou may remove the headset."):
                controllerUnavailable?"Controller view unavailable.\nCheck tracking on the desktop.":
                formalEyeWait?"Preparing eye tracking.\nThe experiment will start\nwhen tracking is ready.":
                practice?(practiceIsExam?"Practice exam starts in\n":"Practice starts in\n")+Math.Max(1,Math.Ceiling(practiceRemaining))+"\n"+practiceInstruction:
                "Task on hold.\nCheck the desktop\nfor the next step.";
            noticeLayout.Apply(message,practice?.015f:.012f);
            // Staggered coloured bursts and short fading trails surround a
            // clear text area. No full-screen flashes or task camera layers.
            LayoutCompletionFireworks(celebrating,completionElapsed);
            oldClear=camera.clearFlags;oldBackground=camera.backgroundColor;active=true;
            camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=new Color(.025f,.025f,.035f,1);
            camera.cullingMask=Mask;
            root.SetActive(true);
            return true;
        }
        void LayoutCompletionFireworks(bool celebration,double elapsed)
        {
            bool visible=celebration&&elapsed>=0&&elapsed<6;
            completionSparks.gameObject.SetActive(visible);if(!visible)return;
            for(int burst=0;burst<BurstCount;burst++)
            {
                // Slow expansion/fade, a short quiet interval, and staggered
                // onset avoid simultaneous flashes. The final fade reaches
                // zero before the six-second presentation boundary.
                double sinceStart=elapsed-burst*.22;
                float age=sinceStart<0?-1:(float)(sinceStart%2.6)/2;
                float envelope=age>=0&&age<=1?Mathf.Sin(age*Mathf.PI):0;
                float ending=Mathf.Clamp01((float)(6-elapsed)/.55f);
                for(int trail=0;trail<TrailSegments;trail++)
                {
                    var tint=burstColours[burst];tint.a=envelope*ending*(1-trail*.28f);
                    sparkMaterials[burst*TrailSegments+trail].color=tint;
                }
                for(int ray=0;ray<RaysPerBurst;ray++)for(int trail=0;trail<TrailSegments;trail++)
                {
                    float angle=(ray*(360f/RaysPerBurst)+burst*13)*Mathf.Deg2Rad;
                    float trailAge=Mathf.Clamp01(age-trail*.045f);
                    float radius=.02f+.16f*(1-(1-trailAge)*(1-trailAge));
                    float size=.024f*envelope*ending*(1-trail*.22f);
                    var spark=completionSparks.GetChild((burst*RaysPerBurst+ray)*TrailSegments+trail);
                    spark.localPosition=new Vector3(burstCentres[burst].x+Mathf.Cos(angle)*radius,
                        burstCentres[burst].y+Mathf.Sin(angle)*radius-trailAge*trailAge*.02f,-.006f);
                    spark.localRotation=Quaternion.Euler(0,0,angle*Mathf.Rad2Deg-90);
                    spark.localScale=new Vector3(size*.60f,size*1.4f,1);
                }
            }
        }
        internal void EndRender()
        {
            if(!active)return;
            active=false;
            if(root!=null)root.SetActive(false);
            if(camera==null)return;
            if(camera.cullingMask==Mask)camera.cullingMask=0;
            camera.clearFlags=oldClear;camera.backgroundColor=oldBackground;
        }
        void DestroyRoot()
        {if(root!=null){if(Application.isPlaying)UnityEngine.Object.Destroy(root);else UnityEngine.Object.DestroyImmediate(root);}}
        public void Dispose()
        {
            if(disposed)return;EndRender();disposed=true;DestroyRoot();
            foreach(var material in sparkMaterials)if(material!=null)
            {if(Application.isPlaying)UnityEngine.Object.Destroy(material);else UnityEngine.Object.DestroyImmediate(material);}
        }
    }
}
#endif
