using System;
using UnityEngine;

namespace AvatarStroop.Unity
{
    // Keep tracking/render submission alive while withholding an uncalibrated
    // room. Ordinary tracking/focus losses after the first view do not hide
    // fault or pause UI and never reset the participant's placement.
    public sealed class QuestPreviewStartupView
    {
        readonly Camera camera;
        readonly int cullingMask;
        readonly CameraClearFlags clearFlags;
        readonly Color background;
        public bool Revealed {get; private set;}

        public QuestPreviewStartupView(Camera camera)
        {
            if(camera==null)throw new ArgumentNullException("camera");
            this.camera=camera;
            cullingMask=camera.cullingMask;clearFlags=camera.clearFlags;background=camera.backgroundColor;
            camera.cullingMask=0;camera.clearFlags=CameraClearFlags.SolidColor;
            camera.backgroundColor=new Color(.025f,.025f,.035f,1);
        }
        public bool TryReveal(bool ready)
        {
            if(Revealed||!ready)return false;
            camera.cullingMask=cullingMask;camera.clearFlags=clearFlags;camera.backgroundColor=background;
            Revealed=true;
            return true;
        }
    }
}
