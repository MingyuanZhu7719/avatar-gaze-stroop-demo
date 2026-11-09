using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AvatarStroop.Core;

namespace AvatarStroop.Runtime
{
    // Development-only coordinate transform. The supplied rotation maps vectors
    // in operator tracking coordinates to the avatar root frame. It is an explicit
    // engineering input, not evidence of a calibrated apparatus or physical wearer.
    // This assumes the tracked HMD-local axes already correspond to the avatar
    // head's local axes; a rig with a different bind/rest orientation needs its
    // own validated correction. No origin or target is manufactured. This class
    // does not control a renderer or establish sample freshness; consumers must
    // separately gate both channels and hide stale/invalid output. Version is a
    // configuration fingerprint, not authentication, calibration or consent.
    public sealed class ViveWozFrameMapper : IViveProEyeCoordinateMapper
    {
        readonly Rotation trackingToAvatar;
        readonly ViveProEyeRehearsalCoordinates sdk = new ViveProEyeRehearsalCoordinates();
        public string Version { get; private set; }
        public bool MappingValidatedForApparatus { get { return false; } }

        // Callers must agree on this version over an authenticated channel and
        // verify the resulting transform against the actual apparatus.
        public ViveWozFrameMapper(Rotation trackingToAvatar, string alignmentId)
        {
            if (!trackingToAvatar.IsUnit) throw new ArgumentException("A finite unit alignment rotation is required", "trackingToAvatar");
            WireCodec.RequireIdentity(alignmentId);
            Rotation unit=trackingToAvatar.Unit();
            // A quaternion and its negation encode the same frame. Canonicalize
            // the sign and signed zero before constructing the fingerprint.
            bool negate=unit.W<0 || (unit.W==0 && (unit.X<0 ||
                (unit.X==0 && (unit.Y<0 || (unit.Y==0 && unit.Z<0)))));
            this.trackingToAvatar = new Rotation(Canonical(unit.X,negate),Canonical(unit.Y,negate),
                Canonical(unit.Z,negate),Canonical(unit.W,negate));
            string content = "vive-woz-frame-unvalidated-v1\n" + alignmentId + "\n" +
                this.trackingToAvatar.X.ToString("R",CultureInfo.InvariantCulture) + "," +
                this.trackingToAvatar.Y.ToString("R",CultureInfo.InvariantCulture) + "," +
                this.trackingToAvatar.Z.ToString("R",CultureInfo.InvariantCulture) + "," +
                this.trackingToAvatar.W.ToString("R",CultureInfo.InvariantCulture);
            byte[] digest;
            using (var sha = SHA256.Create()) digest=sha.ComputeHash(Encoding.UTF8.GetBytes(content));
            var hex=new StringBuilder(64);
            foreach(byte part in digest) hex.Append(part.ToString("x2",CultureInfo.InvariantCulture));
            Version = "vive-woz-frame-unvalidated-v1/" + hex;
        }

        static double Canonical(double value,bool negate)
        {double result=negate?-value:value;return result==0?0:result;}

        public bool TryMapHead(Rotation rawTrackingRotation, out Rotation mappedHead)
        {
            mappedHead = new Rotation(Double.NaN, Double.NaN, Double.NaN, Double.NaN);
            if (!rawTrackingRotation.IsUnit) return false;
            Rotation a = trackingToAvatar, b = rawTrackingRotation.Unit();
            var product = new Rotation(a.W*b.X+a.X*b.W+a.Y*b.Z-a.Z*b.Y,
                a.W*b.Y-a.X*b.Z+a.Y*b.W+a.Z*b.X,
                a.W*b.Z+a.X*b.Y-a.Y*b.X+a.Z*b.W,
                a.W*b.W-a.X*b.X-a.Y*b.Y-a.Z*b.Z);
            if (!product.IsUnit) return false;
            mappedHead = product.Unit();
            return true;
        }

        public bool TryMapEye(Rotation rawTrackingRotation, Vec3 rawSdkDirection, out Vec3 mappedEye)
        {
            mappedEye = new Vec3(Double.NaN, Double.NaN, Double.NaN);
            Vec3 trackingEye;
            if (!sdk.TryMapEye(rawTrackingRotation, rawSdkDirection, out trackingEye)) return false;
            Rotation q = trackingToAvatar;
            double x=trackingEye.X,y=trackingEye.Y,z=trackingEye.Z;
            double ix=q.W*x+q.Y*z-q.Z*y,iy=q.W*y+q.Z*x-q.X*z,iz=q.W*z+q.X*y-q.Y*x,iw=-q.X*x-q.Y*y-q.Z*z;
            var result = new Vec3(ix*q.W-iw*q.X-iy*q.Z+iz*q.Y,
                iy*q.W-iw*q.Y-iz*q.X+ix*q.Z,iz*q.W-iw*q.Z-ix*q.Y+iy*q.X);
            if (!result.IsUnit) return false;
            mappedEye = result.Unit();
            return true;
        }
    }
}
