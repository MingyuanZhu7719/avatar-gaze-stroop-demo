using System;

namespace AvatarStroop.Runtime
{
    // Preview-only startup check. It never samples or changes the XR runtime.
    // A stable reference is used once to place the virtual view at the board;
    // it is not a measurement of the participant's physical height.
    public sealed class ViveStartPoseStability
    {
        const double MaximumDistance=.03, MinimumQuaternionDot=.9993908270190958; // 4 degrees
        readonly double minimumSeconds,maximumGapSeconds;
        public ViveStartPoseStability(double minimumSeconds=.20,double maximumGapSeconds=Double.PositiveInfinity)
        {if(Double.IsNaN(minimumSeconds)||Double.IsInfinity(minimumSeconds)||minimumSeconds<.20)throw new ArgumentOutOfRangeException("minimumSeconds");if(Double.IsNaN(maximumGapSeconds)||maximumGapSeconds<=0)throw new ArgumentOutOfRangeException("maximumGapSeconds");this.minimumSeconds=minimumSeconds;this.maximumGapSeconds=maximumGapSeconds;}
        int mode,firstFrame,lastFrame,count;
        double started,lastTime,x,y,z,qx,qy,qz,qw;
        bool active;
        static bool Finite(double v){return !Double.IsNaN(v)&&!Double.IsInfinity(v);}
        void Reset(){active=false;count=0;}

        // This bounds the virtual shift, not the participant's physical height.
        // Keep this pure so pose-gate and correction boundaries can be tested
        // together without starting Unity or opening an XR device.
        public static bool AcceptsVerticalShift(float currentWorldY,float boardWorldY)
        {return Finite(currentWorldY)&&Finite(boardWorldY)&&Math.Abs(boardWorldY-currentWorldY)<=2f;}

        // Call on the main thread with a unique frame and local tracking pose.
        // mode 1/2 denotes the already reported Device/Floor reference mode.
        public bool Observe(bool ready,int referenceMode,int frame,double now,
            double px,double py,double pz,double rx,double ry,double rz,double rw)
        {
            double norm=rx*rx+ry*ry+rz*rz+rw*rw;
            if(!ready||(referenceMode!=1&&referenceMode!=2)||frame<0||!Finite(now)||now<0||
                !Finite(px)||!Finite(py)||!Finite(pz)||Math.Abs(px)>20||Math.Abs(pz)>20||py< -2||py>4||
                !Finite(norm)||Math.Abs(norm-1)>.02)
            {Reset();return false;}
            if(active&&(now<lastTime||frame<lastFrame)){Reset();return false;}
            if(active&&frame==lastFrame)return false;
            double dx=px-x,dy=py-y,dz=pz-z;
            double dot=Math.Abs(rx*qx+ry*qy+rz*qz+rw*qw);
            if(!active||now-lastTime>maximumGapSeconds||mode!=referenceMode||dx*dx+dy*dy+dz*dz>MaximumDistance*MaximumDistance||dot<MinimumQuaternionDot)
            {
                active=true;mode=referenceMode;firstFrame=frame;count=1;started=now;
                x=px;y=py;z=pz;qx=rx;qy=ry;qz=rz;qw=rw;
            }
            else count++;
            lastFrame=frame;lastTime=now;
            return count>=3&&frame>firstFrame&&now-started>=minimumSeconds;
        }
    }
}
