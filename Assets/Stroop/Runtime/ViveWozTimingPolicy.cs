using System;
using AvatarStroop.Core;

namespace AvatarStroop.Runtime
{
    // Selected only from the actual transport implementation. This is a
    // software display lease, not measured motion-to-photon latency or eye accuracy.
    internal sealed class ViveWozTimingPolicy
    {
        internal static readonly ViveWozTimingPolicy Lan=new ViveWozTimingPolicy(false);
        internal static readonly ViveWozTimingPolicy Photon=new ViveWozTimingPolicy(true);
        readonly bool cloud;
        ViveWozTimingPolicy(bool cloud){this.cloud=cloud;}
        // One outstanding challenge cycle requires the old grant to survive
        // its own roundtrip plus the replacement roundtrip. The cloud profile
        // allows two nominal300ms cycles and150ms scheduling/jitter margin.
        internal string Name {get{return cloud?"photon-cloud-750ms":"lan-150ms";}}
        internal double NetworkTaskLease {get{return cloud?.75:.15;}}
        internal double ReceivedPoseAge {get{return .15;}}
        internal double ControlProofLease {get{return cloud?.75:.15;}}
        internal double OperatorProofLease {get{return cloud?.75:.10;}}
        internal double LocalCaptureAge {get{return .10;}}
        internal bool EvidenceFresh(bool pose,double now,double taskIssued,double receipt,double sent,double head,double eye)
        {
            return Age(now,taskIssued,NetworkTaskLease)&&Age(now,receipt,pose?ReceivedPoseAge:ControlProofLease)&&
                Age(sent,head,LocalCaptureAge)&&Age(sent,eye,LocalCaptureAge);
        }
        internal bool OperatorProofFresh(double now,double taskReceived,double head,double eye)
        {return (!cloud||Age(now,taskReceived,NetworkTaskLease))&&Age(now,head,OperatorProofLease)&&Age(now,eye,OperatorProofLease);}
        internal double AvatarExpiry(double taskReceived,double receipt,double captureAge)
        {return Math.Min(taskReceived+NetworkTaskLease,receipt+ReceivedPoseAge-captureAge);}
        static bool Age(double now,double then,double limit)
        {return WireCodec.Finite(now)&&WireCodec.Finite(then)&&then>=0&&now>=then&&now-then<=limit;}
    }
}
