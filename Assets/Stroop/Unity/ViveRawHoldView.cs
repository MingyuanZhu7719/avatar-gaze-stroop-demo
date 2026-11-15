#if STROOP_XR_PREVIEW
using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace AvatarStroop.Unity
{
    // Not part of the task/recording view; no input, render witness, journal or
    // experiment callback. This one layer is reserved for the hold text.
    internal sealed class ViveRawHoldView : IDisposable
    {
        internal const int Layer=29,Mask=1<<Layer;
        readonly Camera camera;
        readonly GameObject root;
        readonly TextMesh text;
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
            }
            catch{DestroyRoot();throw;}
        }
        internal bool BeginRender(Camera candidate,bool trackedNow,bool stopped,bool controllerUnavailable=false)
        {
            if(disposed||!trackedNow||camera==null||candidate!=camera||root==null||text==null||
                !camera.isActiveAndEnabled||camera.targetTexture!=null||camera.cullingMask!=0||active)return false;
            // Fixed strings only; no paths, native exceptions, raw data or
            // promises of recording, recovery or compositor delivery.
            text.text=stopped?"Task blocked.\nCheck recording status on the desktop.":
                controllerUnavailable?"Controller view unavailable.\nCheck controller tracking and the desktop.":
                "Task on hold.\nCheck the desktop for the next step.";
            oldClear=camera.clearFlags;oldBackground=camera.backgroundColor;active=true;
            camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=new Color(.025f,.025f,.035f,1);
            camera.cullingMask=Mask;
            root.SetActive(true);
            return true;
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
        {if(disposed)return;EndRender();disposed=true;DestroyRoot();}
    }
}
#endif
