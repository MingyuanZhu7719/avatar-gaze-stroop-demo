using System;

namespace AvatarStroop.Core
{
    // Derives an explicit development reference from scene geometry. This does not
    // calibrate an operator headset or validate an apparatus coordinate mapping.
    public static class DevelopmentGeometry
    {
        // Inputs point from the avatar head to each target, expressed in avatar-root
        // local space. Their lengths do not affect the resulting head/eye directions.
        public static AutomaticGazeGeometry FromLocalDirections(Vec3 participantDirection,Vec3 taskDirection,string version)
        {
            var participant=NormalizeDirection(participantDirection,"participantDirection");
            var task=NormalizeDirection(taskDirection,"taskDirection");
            return new AutomaticGazeGeometry(FromForward(participant),participant,FromForward(task),task,version);
        }

        static Vec3 NormalizeDirection(Vec3 direction,string parameter)
        {
            if(!direction.IsFinite) throw new ArgumentException("Direction must contain only finite values",parameter);
            double scale=Math.Max(Math.Abs(direction.X),Math.Max(Math.Abs(direction.Y),Math.Abs(direction.Z)));
            if(scale == 0) throw new ArgumentException("Direction must be nonzero",parameter);
            // Scale first so finite very large or subnormal inputs remain usable.
            double x=direction.X/scale,y=direction.Y/scale,z=direction.Z/scale;
            double length=Math.Sqrt(x*x+y*y+z*z);
            return new Vec3(x/length,y/length,z/length);
        }

        static Rotation FromForward(Vec3 direction)
        {
            double radialScale=Math.Max(Math.Abs(direction.X),Math.Abs(direction.Y));
            if(radialScale == 0)
                return direction.Z < 0 ? new Rotation(0,1,0,0) : Rotation.Identity;
            double x=direction.X/radialScale,y=direction.Y/radialScale;
            double radialLength=Math.Sqrt(x*x+y*y);
            double radial=radialScale*radialLength;
            // atan2 avoids the cancellation of (1 + z) near the backwards target.
            // The cross product of +Z and the target supplies the shortest-arc axis.
            double halfAngle=Math.Atan2(radial,direction.Z)/2;
            double sin=Math.Sin(halfAngle);
            return new Rotation(-y/radialLength*sin,x/radialLength*sin,0,Math.Cos(halfAngle));
        }
    }
}
