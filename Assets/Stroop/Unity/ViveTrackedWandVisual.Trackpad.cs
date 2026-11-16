#if STROOP_XR_PREVIEW
using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace AvatarStroop.Unity
{
    internal sealed partial class ViveTrackedWandVisual
    {
        // Geometry for the installed Valve vr_controller_vive_1_5 trackpad:
        // component centre native RHS (0,.002,.049), surface tilt X=6.55 deg.
        // Its OBJ surface rises at most 2.973 mm above that component plane.
        // These are model coordinates: the existing model matrix supplies the
        // single inverse-grip transform, for both the body and these markings.
        internal const string TrackpadModelName="vr_controller_vive_1_5";
        internal const float TrackpadTiltDegrees=6.55f,TrackpadRadius=.019f,TrackpadInnerRadius=.006f,TrackpadLift=.0035f;
        static readonly Vector3 TrackpadComponentCenter=new Vector3(0,.002f,-.049f);
        static readonly Quaternion TrackpadTilt=Quaternion.Euler(-TrackpadTiltDegrees,0,0);
        static readonly Color32[] TrackpadColours={new Color32(255,0,0,255),new Color32(0,255,0,255),
            new Color32(0,0,255,255),new Color32(255,255,0,255)};
        bool trackpadMapping;
        Mesh[] trackpadSectors;
        Material[] trackpadMaterials;
        Texture2D[] trackpadTextures;
        internal void SetTrackpadMapping(bool enabled){trackpadMapping=enabled;}
        internal bool TrackpadMappingVisible
        {get{return trackpadMapping&&Visible&&ModelName==TrackpadModelName;}}
        static Vector3 TrackpadPoint(float radius,float degrees)
        {
            float radians=degrees*Mathf.Deg2Rad;
            return TrackpadComponentCenter+TrackpadTilt*new Vector3(radius*Mathf.Cos(radians),TrackpadLift,radius*Mathf.Sin(radians));
        }
        void EnsureTrackpadOverlay()
        {
            if(trackpadSectors!=null)return;
            if(material==null||material.shader==null)return;
            trackpadSectors=new Mesh[4];trackpadMaterials=new Material[4];trackpadTextures=new Texture2D[4];
            try
            {
                for(int sector=0;sector<4;sector++)
                {
                    const int steps=16;var vertices=new Vector3[(steps+1)*2];var indices=new int[steps*6];var uv=new Vector2[vertices.Length];
                    float middle=90-sector*90;
                    for(int i=0;i<=steps;i++)
                    {
                        float angle=middle-42+84f*i/steps;
                        vertices[i*2]=TrackpadPoint(TrackpadInnerRadius,angle);vertices[i*2+1]=TrackpadPoint(TrackpadRadius,angle);
                        uv[i*2]=uv[i*2+1]=new Vector2(.5f,.5f);
                        if(i==steps)continue;
                        int p=i*6,j=i*2;indices[p]=j;indices[p+1]=j+3;indices[p+2]=j+1;
                        indices[p+3]=j;indices[p+4]=j+2;indices[p+5]=j+3;
                    }
                    trackpadSectors[sector]=new Mesh{name="Vive trackpad mapping sector "+sector,hideFlags=HideFlags.DontSave,
                        vertices=vertices,uv=uv,triangles=indices};
                    trackpadSectors[sector].RecalculateBounds();trackpadSectors[sector].RecalculateNormals();
                    var texture=trackpadTextures[sector]=new Texture2D(1,1,TextureFormat.RGBA32,false,false)
                        {name="Vive mapping colour "+sector,hideFlags=HideFlags.DontSave};
                    texture.SetPixel(0,0,TrackpadColours[sector]);texture.Apply(false,true);
                    trackpadMaterials[sector]=new Material(material.shader){name="Vive trackpad mapping "+sector,
                        hideFlags=HideFlags.DontSave,mainTexture=texture,enableInstancing=true};
                    trackpadMaterials[sector].SetFloat("_SurfaceLighting",0);
                }
            }
            catch{ClearTrackpadOverlay();throw;}
        }
        void AppendTrackpadOverlay(CommandBuffer commands,Matrix4x4 modelToWorld)
        {
            if(commands==null||!TrackpadMappingVisible)return;
            EnsureTrackpadOverlay();if(trackpadSectors==null)return;
            for(int i=0;i<4;i++)commands.DrawMesh(trackpadSectors[i],modelToWorld,trackpadMaterials[i],0,0);
        }
        void ClearTrackpadOverlay()
        {
            if(trackpadSectors!=null)foreach(var value in trackpadSectors)DestroyOwned(value);
            if(trackpadMaterials!=null)foreach(var value in trackpadMaterials)DestroyOwned(value);
            if(trackpadTextures!=null)foreach(var value in trackpadTextures)DestroyOwned(value);
            trackpadSectors=null;trackpadMaterials=null;trackpadTextures=null;
        }
    }
}
#endif
