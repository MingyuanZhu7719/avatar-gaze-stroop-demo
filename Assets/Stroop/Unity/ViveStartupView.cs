#if STROOP_XR_PREVIEW
using System;
using UnityEngine;
using UnityEngine.Rendering;
namespace AvatarStroop.Unity
{
    // Short head-relative startup instructions only. Never reveals task content.
    public sealed class ViveStartupView : IDisposable
    {
        public const int Layer=30,Mask=1<<Layer;
        readonly Camera camera;
        readonly CameraClearFlags oldClear;
        readonly Color oldBackground;
        readonly GameObject root;
        readonly TextMesh text;
        int renderedFrame=-1;
        bool shown,disposed;
        public ViveStartupView(Camera camera)
        {
            if(camera==null)throw new ArgumentNullException("camera");
            this.camera=camera;oldClear=camera.clearFlags;oldBackground=camera.backgroundColor;
            root=new GameObject("ViveStartupInstructions");root.layer=Layer;
            root.transform.SetParent(camera.transform,false);root.transform.localPosition=new Vector3(0,0,1.4f);
            text=root.AddComponent<TextMesh>();text.font=Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            text.fontSize=64;text.characterSize=.012f;text.color=Color.white;
            text.anchor=TextAnchor.MiddleCenter;text.alignment=TextAlignment.Center;text.richText=false;
            var renderer=text.GetComponent<MeshRenderer>();renderer.sharedMaterial=text.font.material;
            renderer.shadowCastingMode=ShadowCastingMode.Off;renderer.receiveShadows=false;
            root.SetActive(false);Camera.onPostRender+=Rendered;
        }
        public bool WasRenderedBefore(int frame){return shown&&renderedFrame==frame-1;}
        void Rendered(Camera rendered)
        {if(!disposed&&shown&&rendered==camera&&root.activeInHierarchy&&camera.cullingMask==Mask)renderedFrame=Time.frameCount;}
        public void Show(bool tracked,string message)
        {
            if(disposed)return;
            camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=new Color(.025f,.025f,.035f,1);
            if(text.text!=message){text.text=message;renderedFrame=-1;}
            shown=tracked;root.SetActive(tracked);camera.cullingMask=tracked?Mask:0;
            if(!tracked)renderedFrame=-1;
        }
        public void Hide()
        {
            if(disposed)return;
            shown=false;renderedFrame=-1;root.SetActive(false);
            camera.clearFlags=oldClear;camera.backgroundColor=oldBackground;
        }
        public void Dispose()
        {
            if(disposed)return;Hide();disposed=true;Camera.onPostRender-=Rendered;
            if(root!=null){if(Application.isPlaying)UnityEngine.Object.Destroy(root);else UnityEngine.Object.DestroyImmediate(root);}
        }
    }
}
#endif
