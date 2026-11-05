using System;
using AvatarStroop.Core;

namespace AvatarStroop.Runtime
{
    public enum ViveSdkDirectionStatus { Convertible, Nonfinite, Degenerate, MagnitudeOverflow }

    // The installed HTC GetGazeRay Ray overload constructs a Unity Ray, which
    // normalizes its direction when magnitude > 1e-5f. This conversion makes a
    // derived unit direction; the native vector and mask remain unchanged.
    // Callers must separately require the native direction-validity flag/mask
    // and the runtime, calibration, freshness and head gates. Math cannot make
    // an unreported/invalid native gaze usable.
    public static class ViveProEyeSdkDirection
    {
        public const double MinimumRayMagnitude=0.00001f;

        public static bool TryGetUnitDirection(Vec3 rawDirection,out Vec3 unitDirection)
        {return ConvertForRay(rawDirection,out unitDirection)==ViveSdkDirectionStatus.Convertible;}

        public static ViveSdkDirectionStatus ConvertForRay(Vec3 rawDirection,out Vec3 unitDirection)
        {
            unitDirection=new Vec3(Double.NaN,Double.NaN,Double.NaN);
            if(!rawDirection.IsFinite)return ViveSdkDirectionStatus.Nonfinite;
            // Native SDK components are floats. Keep a conservative, explicit
            // malformed-magnitude guard before derived conversion. Unity/JIT may
            // retain wider intermediate precision; this stored-float overflow
            // guard is intentionally stricter, not an assertion of Ray parity
            // for astronomically large values. Raw payloads remain unchanged.
            float nativeX=(float)rawDirection.X,nativeY=(float)rawDirection.Y,nativeZ=(float)rawDirection.Z;
            float nativeLengthSquared=nativeX*nativeX+nativeY*nativeY+nativeZ*nativeZ;
            if(Single.IsInfinity(nativeLengthSquared)||Single.IsNaN(nativeLengthSquared))return ViveSdkDirectionStatus.MagnitudeOverflow;
            float nativeMagnitude=(float)Math.Sqrt(nativeLengthSquared);
            if(nativeMagnitude<=MinimumRayMagnitude)return ViveSdkDirectionStatus.Degenerate;
            double scale=Math.Max(Math.Abs(rawDirection.X),Math.Max(Math.Abs(rawDirection.Y),Math.Abs(rawDirection.Z)));
            // Derive the unit copy with scaled double arithmetic after the SDK
            // magnitude gate. This does not modify the original native vector.
            double x=rawDirection.X/scale,y=rawDirection.Y/scale,z=rawDirection.Z/scale;
            double scaledMagnitude=Math.Sqrt(x*x+y*y+z*z);
            unitDirection=new Vec3(x/scaledMagnitude,y/scaledMagnitude,z/scaledMagnitude);
            return ViveSdkDirectionStatus.Convertible;
        }
    }
}
