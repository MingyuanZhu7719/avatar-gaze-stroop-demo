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
        readonly Transform completionSparks;
        readonly Material coolSpark,warmSpark;
        bool completed;
        bool blockCompleted;
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
                completionSparks=new GameObject("GentleCompletionFireworks").transform;
                completionSparks.gameObject.layer=Layer;completionSparks.SetParent(root.transform,false);
                coolSpark=new Material(Shader.Find("AvatarStroop/WorldSpaceOverlay")){color=new Color(.61f,.77f,.79f)};
                warmSpark=new Material(Shader.Find("AvatarStroop/WorldSpaceOverlay")){color=new Color(.84f,.73f,.51f)};
                for(int burst=0;burst<2;burst++)for(int ray=0;ray<10;ray++)
                {
                    var spark=GameObject.CreatePrimitive(PrimitiveType.Quad);spark.name="Spark"+burst+"_"+ray;
                    spark.layer=Layer;spark.transform.SetParent(completionSparks,false);
                    var collider=spark.GetComponent<Collider>();if(collider!=null)UnityEngine.Object.DestroyImmediate(collider);
                    var mesh=spark.GetComponent<MeshRenderer>();mesh.sharedMaterial=burst==0?coolSpark:warmSpark;
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
        internal bool BeginRender(Camera candidate,bool trackedNow,bool stopped,bool controllerUnavailable=false)
        {
            if(disposed||!trackedNow||camera==null||candidate!=camera||root==null||text==null||
                !camera.isActiveAndEnabled||camera.targetTexture!=null||camera.cullingMask!=0||active)return false;
            // Fixed strings only; no paths, native exceptions, raw data or
            // promises of recording, recovery or compositor delivery.
            bool celebrating=!stopped&&completed;
            bool practice=!stopped&&!controllerUnavailable&&!celebrating&&!Double.IsNaN(practiceRemaining)&&practiceRemaining>=0;
            text.text=stopped?"Task blocked.\nCheck recording status on the desktop.":
                celebrating?(blockCompleted?"Block complete!\nWell done. A few questions are next.":"Thank you!\nYou have completed all blocks."):
                controllerUnavailable?"Controller view unavailable.\nCheck controller tracking and the desktop.":
                practice?"Practice starts in\n"+Math.Max(1,Math.Ceiling(practiceRemaining))+"\n"+practiceInstruction:
                "Task on hold.\nCheck the desktop for the next step.";
            // The same muted, six-second radial animation as the ordinary
            // completion panel; it never re-opens the task's camera layers.
            LayoutCompletionFireworks(celebrating,completionElapsed);
            text.characterSize=practice?.015f*(1+.035f*Mathf.Sin((float)(practiceRemaining%1)*Mathf.PI)):.012f;
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
            for(int burst=0;burst<2;burst++)
            {
                float age=(float)((elapsed+.45+burst*.7)%3)/3;
                float radius=.025f+.135f*age,size=.016f*Mathf.Sin(age*Mathf.PI);
                for(int ray=0;ray<10;ray++)
                {
                    float angle=(ray*36+burst*18)*Mathf.Deg2Rad;
                    var spark=completionSparks.GetChild(burst*10+ray);
                    spark.localPosition=new Vector3((burst==0?-.38f:.38f)+Mathf.Cos(angle)*radius,.13f+Mathf.Sin(angle)*radius-age*age*.025f,-.006f);
                    spark.localRotation=Quaternion.Euler(0,0,angle*Mathf.Rad2Deg-90);
                    spark.localScale=new Vector3(size*.65f,size*1.5f,1);
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
            foreach(var material in new[]{coolSpark,warmSpark})if(material!=null)
            {if(Application.isPlaying)UnityEngine.Object.Destroy(material);else UnityEngine.Object.DestroyImmediate(material);}
        }
    }
}
#endif
