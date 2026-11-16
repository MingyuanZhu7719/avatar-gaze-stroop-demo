using System;

namespace AvatarStroop.Runtime
{
    // Presentation delay only: it grants no experiment, recording or input
    // authority. Missing renders restart the warning instead of silently
    // consuming its five seconds while the headset is unavailable.
    internal sealed class VivePracticeCountdown
    {
        internal const double DurationSeconds=5;
        readonly double duration;
        internal VivePracticeCountdown(double durationSeconds=DurationSeconds)
        {if(Double.IsNaN(durationSeconds)||Double.IsInfinity(durationSeconds)||durationSeconds<=0)throw new ArgumentOutOfRangeException("durationSeconds");duration=durationSeconds;}
        double elapsed,lastTime=Double.NaN;
        int lastFrame=-1;
        internal double RemainingSeconds {get{return Math.Max(0,duration-elapsed);}}
        internal void Reset(){elapsed=0;lastTime=Double.NaN;lastFrame=-1;}
        internal void ObserveRendered(double now,int frame)
        {
            if(Double.IsNaN(now)||Double.IsInfinity(now)||now<0||frame<0){Reset();return;}
            if(frame==lastFrame)return; // Stereo/duplicate callbacks count once.
            if(frame!=lastFrame+1||Double.IsNaN(lastTime)||now<lastTime||now-lastTime>.25)elapsed=0;
            else elapsed=Math.Min(duration,elapsed+now-lastTime);
            lastTime=now;lastFrame=frame;
        }
        internal bool CompleteForFrame(double now,int frame)
        {
            return !Double.IsNaN(now)&&!Double.IsInfinity(now)&&frame==lastFrame+1&&
                now>=lastTime&&now-lastTime<=.25&&elapsed>=duration-1e-9;
        }
    }
}
