#if STROOP_XR_PREVIEW
using System;
using System.Collections.Generic;
using UnityEngine;

namespace AvatarStroop.Unity
{
    // Head-relative notices have no TextMesh wrapping of their own. Keep their
    // existing glyph size and wrap by measured geometry inside a central panel,
    // also inset from both render frustums. This is a software layout bound,
    // not a claim about physical lens visibility or compositor delivery.
    internal sealed class ViveHeadsetNoticeLayout
    {
        internal const float Distance=1.4f,WidthDegrees=46f,HeightDegrees=38f;
        const float BoundsTolerance=.003f,ReuseMargin=.001f;
        readonly Camera camera;
        readonly TextMesh text;
        readonly MeshRenderer renderer;
        readonly Vector3[] corners=new Vector3[4];
        string source;
        float characterSize;
        Rect previousBounds;
        bool applied;

        internal ViveHeadsetNoticeLayout(Camera camera,TextMesh text)
        {this.camera=camera;this.text=text;renderer=text.GetComponent<MeshRenderer>();}

        Rect Frustum(Camera.MonoOrStereoscopicEye eye)
        {
            camera.CalculateFrustumCorners(new Rect(0,0,1,1),Distance,eye,corners);
            float left=Single.PositiveInfinity,bottom=left,right=Single.NegativeInfinity,top=right;
            foreach(var corner in corners)
            {left=Mathf.Min(left,corner.x);right=Mathf.Max(right,corner.x);bottom=Mathf.Min(bottom,corner.y);top=Mathf.Max(top,corner.y);}
            return Rect.MinMaxRect(left,bottom,right,top);
        }
        static Rect Intersect(Rect a,Rect b)
        {return Rect.MinMaxRect(Mathf.Max(a.xMin,b.xMin),Mathf.Max(a.yMin,b.yMin),Mathf.Min(a.xMax,b.xMax),Mathf.Min(a.yMax,b.yMax));}
        internal Rect SafeBounds
        {
            get
            {
                float halfWidth=Distance*Mathf.Tan(WidthDegrees*.5f*Mathf.Deg2Rad);
                float halfHeight=Distance*Mathf.Tan(HeightDegrees*.5f*Mathf.Deg2Rad);
                var central=Rect.MinMaxRect(-halfWidth,-halfHeight,halfWidth,halfHeight);
                var frustum=camera.stereoEnabled?Intersect(Frustum(Camera.MonoOrStereoscopicEye.Left),Frustum(Camera.MonoOrStereoscopicEye.Right)):
                    Frustum(Camera.MonoOrStereoscopicEye.Mono);
                frustum=new Rect(frustum.x+frustum.width*.10f,frustum.y+frustum.height*.08f,frustum.width*.80f,frustum.height*.84f);
                return Intersect(central,frustum);
            }
        }
        float Width(string value)
        {text.text=value;return renderer.localBounds.size.x;}
        string Wrap(string value,float width)
        {
            var lines=new List<string>();
            foreach(string paragraph in value.Replace("\r","").Split('\n'))
            {
                string line="";
                foreach(string word in paragraph.Split(new[]{' ','\t'},StringSplitOptions.RemoveEmptyEntries))
                {
                    string next=line.Length==0?word:line+" "+word;
                    if(Width(next)<=width){line=next;continue;}
                    if(line.Length>0){lines.Add(line);line="";}
                    // A long unbroken token is preserved too; never ellipsize a
                    // confirmation or hide the end of a participant instruction.
                    foreach(char character in word)
                    {
                        next=line+character;
                        if(line.Length>0&&Width(next)>width){lines.Add(line);line="";}
                        line+=character;
                    }
                }
                lines.Add(line);
            }
            return String.Join("\n",lines);
        }
        bool CanReuseBounds(Rect bounds)
        {
            // Small projection changes must not rebuild identical instructions
            // or invalidate their completed-camera witness. Compare against the
            // last applied layout so cumulative drift eventually causes a reflow.
            // The tolerance is smaller than the 10 mm wrapping inset per side.
            if(Mathf.Abs(previousBounds.xMin-bounds.xMin)>BoundsTolerance||Mathf.Abs(previousBounds.xMax-bounds.xMax)>BoundsTolerance||
                Mathf.Abs(previousBounds.yMin-bounds.yMin)>BoundsTolerance||Mathf.Abs(previousBounds.yMax-bounds.yMax)>BoundsTolerance)return false;
            var geometry=renderer.localBounds;var position=text.transform.localPosition;
            return position.x+geometry.min.x>=bounds.xMin+ReuseMargin&&position.x+geometry.max.x<=bounds.xMax-ReuseMargin&&
                position.y+geometry.min.y>=bounds.yMin+ReuseMargin&&position.y+geometry.max.y<=bounds.yMax-ReuseMargin;
        }
        internal bool Apply(string message,float size)
        {
            message=message??"";var bounds=SafeBounds;
            if(bounds.width<=.05f||bounds.height<=.05f)return false;
            if(applied&&source==message&&characterSize==size&&CanReuseBounds(bounds))return false;
            source=message;characterSize=size;previousBounds=bounds;applied=true;
            text.characterSize=size;text.anchor=TextAnchor.MiddleCenter;text.alignment=TextAlignment.Center;
            text.transform.localScale=Vector3.one;
            // A small geometry margin absorbs glyph overhang and atlas rounding.
            text.text=Wrap(message,bounds.width-.02f);
            var geometry=renderer.localBounds;
            text.transform.localPosition=new Vector3(bounds.center.x-geometry.center.x,bounds.center.y-geometry.center.y,Distance);
            return true;
        }
    }
}
#endif
