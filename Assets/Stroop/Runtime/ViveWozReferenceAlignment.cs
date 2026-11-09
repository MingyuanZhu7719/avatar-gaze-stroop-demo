using System;
using AvatarStroop.Core;

namespace AvatarStroop.Runtime
{
    // A mathematical candidate from a caller-supplied corresponding pose; it
    // does not collect measurements, infer positions or validate an apparatus.
    public enum ViveWozReferenceConvention
    {
        Unspecified = 0,
        HmdLocalToTrackingAndAvatarHeadLocalToRoot = 1
    }

    public enum ViveWozHeadAxisAssumption
    {
        Unspecified = 0,
        HmdAndAvatarHeadLocalAxesAssumedAligned = 1
    }

    public sealed class ViveWozReferenceAlignment
    {
        public ViveWozFrameMapper Mapper { get; private set; }
        // A fitted reference residual can be tiny even for a bad reference.
        // Neither this value nor a holdout residual is a validation decision.
        public double FittedReferenceResidualDegrees { get; private set; }
        public bool ValidatedForApparatus { get { return false; } }

        ViveWozReferenceAlignment(ViveWozFrameMapper mapper,double residual)
        {Mapper=mapper;FittedReferenceResidualDegrees=residual;}

        // Both caller-supplied poses must correspond to the same reference
        // event. This function cannot check simultaneity or the axis assumption.
        public static ViveWozReferenceAlignment Create(Rotation hmdLocalToTracking,
            Rotation avatarHeadLocalToRoot,ViveWozReferenceConvention convention,
            ViveWozHeadAxisAssumption axisAssumption,string alignmentId)
        {
            if(convention!=ViveWozReferenceConvention.HmdLocalToTrackingAndAvatarHeadLocalToRoot)
                throw new ArgumentException("Explicit reference coordinate convention required", "convention");
            if(axisAssumption!=ViveWozHeadAxisAssumption.HmdAndAvatarHeadLocalAxesAssumedAligned)
                throw new ArgumentException("Explicit aligned local-axis assumption required", "axisAssumption");
            if(!hmdLocalToTracking.IsUnit||!avatarHeadLocalToRoot.IsUnit)
                throw new ArgumentException("Both reference orientations must be finite unit rotations");

            Rotation h=hmdLocalToTracking.Unit(), target=avatarHeadLocalToRoot.Unit();
            // qAlignment * qHmd = qTarget; inverse of a unit quaternion is its conjugate.
            Rotation inverse=new Rotation(-h.X,-h.Y,-h.Z,h.W);
            var alignment=new Rotation(target.W*inverse.X+target.X*inverse.W+target.Y*inverse.Z-target.Z*inverse.Y,
                target.W*inverse.Y-target.X*inverse.Z+target.Y*inverse.W+target.Z*inverse.X,
                target.W*inverse.Z+target.X*inverse.Y-target.Y*inverse.X+target.Z*inverse.W,
                target.W*inverse.W-target.X*inverse.X-target.Y*inverse.Y-target.Z*inverse.Z);
            if(!alignment.IsUnit)throw new ArgumentException("Reference alignment is not a valid rotation");
            var mapper=new ViveWozFrameMapper(alignment.Unit(),alignmentId);
            Rotation mapped;
            if(!mapper.TryMapHead(h,out mapped))throw new InvalidOperationException("Reference mapping failed");
            double residual;
            if(!TryOrientationResidualDegrees(mapped,target,out residual))throw new InvalidOperationException("Reference residual unavailable");
            return new ViveWozReferenceAlignment(mapper,residual);
        }

        // Caller may provide an independent known correspondence. A numeric
        // residual is not gaze accuracy, user consent, or calibration approval.
        public bool TryMeasureHoldoutResidualDegrees(Rotation hmdLocalToTracking,
            Rotation expectedAvatarHeadLocalToRoot,out double residualDegrees)
        {
            residualDegrees=Double.NaN;Rotation mapped;
            return expectedAvatarHeadLocalToRoot.IsUnit&&Mapper.TryMapHead(hmdLocalToTracking,out mapped)&&
                TryOrientationResidualDegrees(mapped,expectedAvatarHeadLocalToRoot,out residualDegrees);
        }

        static bool TryOrientationResidualDegrees(Rotation actual,Rotation expected,out double result)
        {
            result=Double.NaN;
            if(!actual.IsUnit||!expected.IsUnit)return false;
            Rotation a=actual.Unit(),b=expected.Unit();
            double dot=Math.Abs(a.X*b.X+a.Y*b.Y+a.Z*b.Z+a.W*b.W);
            if(Double.IsNaN(dot)||Double.IsInfinity(dot))return false;
            result=2*Math.Acos(Math.Min(1.0,dot))*180.0/Math.PI;
            return !Double.IsNaN(result)&&!Double.IsInfinity(result);
        }
    }
}
