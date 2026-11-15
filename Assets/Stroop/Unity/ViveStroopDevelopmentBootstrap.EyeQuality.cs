#if STROOP_XR_PREVIEW
namespace AvatarStroop.Unity
{
    public sealed partial class ViveStroopDevelopmentBootstrap
    {
        // Gaze acquisition and its publication lock do not own behavioral
        // timing. The pump records missing observations separately while the
        // independently verified exact-run head/task epoch remains current.
        internal bool RawTaskEpochThisFrame {get{return namedRawPump!=null&&namedRawPump.RawTaskEpochThisFrame;}}
    }
}
#endif
