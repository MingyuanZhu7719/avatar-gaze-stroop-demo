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
