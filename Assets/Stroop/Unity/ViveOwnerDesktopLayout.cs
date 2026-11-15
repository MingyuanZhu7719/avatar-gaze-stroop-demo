#if STROOP_XR_PREVIEW
using System;
using System.Runtime.InteropServices;
using UnityEngine;

namespace AvatarStroop.Unity
{
    // Desktop IMGUI uses framebuffer pixels. Match the Windows window DPI so
    // owner controls remain readable on scaled displays; XR cameras are untouched.
    internal static class ViveOwnerDesktopLayout
    {
        internal static float ContentWidth {get;private set;}=800;
        internal static bool StackActions {get{return ContentWidth<500;}}
#if UNITY_EDITOR
        internal static Action<string,Rect> ObserveForFixture;
#endif
        internal static void Observe(string key,Rect rectangle)
        {
#if UNITY_EDITOR
            if(Event.current!=null&&Event.current.type==EventType.Repaint&&ObserveForFixture!=null)
            {var point=GUIUtility.GUIToScreenPoint(rectangle.position);ObserveForFixture(key,new Rect(point,rectangle.size));}
#endif
        }
        internal struct ContentScope:IDisposable
        {
            readonly float previous;
            internal ContentScope(float width){previous=ContentWidth;ContentWidth=Mathf.Max(80,width);}
            public void Dispose(){ContentWidth=previous;}
        }
        internal static ContentScope BeginContent(float width){return new ContentScope(width);}
        internal static int SelectionColumns(float width,int preferred,float minimumCellWidth)
        {return Mathf.Max(1,Mathf.Min(preferred,Mathf.FloorToInt((width+6)/(minimumCellWidth+6))));}
        // GUILayout.SelectionGrid derives its intrinsic size before wrapping.
        // Reserve measured rows at the actual scroll viewport width instead,
        // so long labels cannot widen the scroll body or clip adjacent choices.
        internal static int Selection(string key,int selected,string[] labels,int preferredColumns,float minimumCellWidth=110)
        {
            var style=GUI.skin.button;float width=ContentWidth;
            int columns=SelectionColumns(width,preferredColumns,minimumCellWidth);
            float cell=(width-6*(columns-1))/columns;
            float height=40;
            foreach(var text in labels)height=Mathf.Max(height,Mathf.Ceil(style.CalcHeight(new GUIContent(text),cell)));
            int rows=(labels.Length+columns-1)/columns;
            var bounds=GUILayoutUtility.GetRect(width,rows*height+6*(rows-1),GUILayout.Width(width),GUILayout.Height(rows*height+6*(rows-1)));
            for(int i=0;i<labels.Length;i++)
            {
                var rect=new Rect(bounds.x+(i%columns)*(cell+6),bounds.y+(i/columns)*(height+6),cell,height);
                if(GUI.Toggle(rect,selected==i,labels[i],style))selected=i;
                Observe(key+":"+i,rect);
            }
            return selected;
        }
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
        [DllImport("user32.dll")] static extern IntPtr GetActiveWindow();
        [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr window);
        [DllImport("user32.dll")] static extern uint GetDpiForSystem();
#endif
        internal static float ScaleForDpi(float dpi)
        {
            return Single.IsNaN(dpi)||Single.IsInfinity(dpi)||dpi<=0?1f:Mathf.Clamp(dpi/96f,1f,3f);
        }
        static float CurrentScale()
        {
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
            try
            {
                var window=GetActiveWindow();
                uint dpi=window==IntPtr.Zero?GetDpiForSystem():GetDpiForWindow(window);
                if(dpi>0)return ScaleForDpi(dpi);
            }
            catch(DllNotFoundException){}
            catch(EntryPointNotFoundException){}
#endif
            return ScaleForDpi(Screen.dpi);
        }
        internal struct Scope:IDisposable
        {
            readonly Matrix4x4 previous;
            internal readonly float Width,Height;
            internal Scope(float scale)
            {
                previous=GUI.matrix;Width=Screen.width/scale;Height=Screen.height/scale;
                GUI.matrix=Matrix4x4.Scale(new Vector3(scale,scale,1))*previous;
            }
            public void Dispose(){GUI.matrix=previous;}
        }
        internal static Scope Begin(){return new Scope(CurrentScale());}
    }
}
#endif
