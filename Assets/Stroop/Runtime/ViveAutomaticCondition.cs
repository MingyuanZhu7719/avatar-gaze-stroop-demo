using System;
using AvatarStroop.Core;

namespace AvatarStroop.Runtime
{
    public sealed class ViveAutomaticReference
    {
        public AvatarPose Pose {get;private set;}
        public ReferencePhase Phase {get;private set;}
        public GazeTarget? FromTarget {get;private set;}
        public GazeTarget? ToTarget {get;private set;}
        public double TransitionFraction {get;private set;}
        public double ActiveSeconds {get;private set;}
        public double CycleSeconds {get;private set;}
        public string GeometryVersion {get;private set;}
        internal ViveAutomaticReference(ReferenceGaze reference,AvatarPose pose)
        {
            Pose=pose;Phase=reference.Phase;FromTarget=reference.FromTarget;ToTarget=reference.ToTarget;
            TransitionFraction=reference.TransitionFraction;ActiveSeconds=reference.ActiveSeconds;
            CycleSeconds=reference.CycleSeconds;GeometryVersion=reference.GeometryVersion;
        }
    }

    // The newer, explicitly named Automatic option has its own entry point.
    // This is a trajectory candidate, not an allocation or a study admission.
    // The supplied target geometry is explicit: it does not prove an eye
    // position, coordinate calibration, or a real participant is present.
    public static class ViveAutomaticCondition
    {
        public const string ScheduleVersion="vive-automatic-task-first-3-0.5-v1";
        public const double DwellSeconds=AutomaticGaze.DwellSeconds,TurnSeconds=AutomaticGaze.TurnSeconds,CycleSeconds=AutomaticGaze.CycleSeconds;
        // Chord distance for two normalized directions; 1e-4 is about 0.0058°.
        // This is a numerical representation tolerance, not a research or
        // physical gaze calibration threshold.
        const double DirectionTolerance=0.0001;

        // AvatarPose uses root-local coordinates for both channels. The head's
        // forward axis is +Z, as in the Unity avatar renderer. Normalize the
        // accepted unit quaternion before rotating +Z to avoid scale drift.
        static Vec3 Forward(Rotation q)
        {
            double n=Math.Sqrt(q.X*q.X+q.Y*q.Y+q.Z*q.Z+q.W*q.W);
            double x=q.X/n,y=q.Y/n,z=q.Z/n,w=q.W/n;
            return new Vec3(2*(x*z+w*y),2*(y*z-w*x),1-2*(x*x+y*y));
        }
        static void RequireAligned(AvatarPose pose,string target)
        {
            var forward=Forward(pose.HeadRotation);var eye=pose.EyeDirection;
            double n=Math.Sqrt(eye.X*eye.X+eye.Y*eye.Y+eye.Z*eye.Z);
            double dx=forward.X-eye.X/n,dy=forward.Y-eye.Y/n,dz=forward.Z-eye.Z/n;
            if(dx*dx+dy*dy+dz*dz>DirectionTolerance*DirectionTolerance)
                throw new ArgumentException("Head and eye directions must coincide at the "+target+" target", "explicitGeometry");
        }

        // The caller supplies elapsed active time from the current block, not
        // a response, wall-clock time or independently restarted trial clock.
        // Pausing and resuming the clock is the owning experiment's duty.
        public static ViveAutomaticReference Evaluate(double activeBlockSeconds,AutomaticGazeGeometry explicitGeometry)
        {
            if(explicitGeometry==null)throw new ArgumentNullException("explicitGeometry");
            RequireAligned(explicitGeometry.TaskPose,"task");
            RequireAligned(explicitGeometry.ParticipantPose,"participant");
            // Reuse the existing mathematically equivalent, tested trajectory
            // while fixing its initial reference to the task board. This does
            // not turn the legacy B allocation into the new study condition.
            var reference=AutomaticGaze.Evaluate(Condition.B,GazeTarget.Task,activeBlockSeconds,explicitGeometry);
            // Independent quaternion/vector slerp can point different ways
            // mid-turn even when their endpoints agree (for example, a head
            // quaternion with roll). Derive the eye direction from each
            // interpolated head orientation so both channels remain aligned.
            var head=reference.Pose.Value.HeadRotation;
            return new ViveAutomaticReference(reference,new AvatarPose(head,Forward(head)));
        }
    }
}
