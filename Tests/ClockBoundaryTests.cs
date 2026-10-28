using System;
using AvatarStroop.Core;

namespace AvatarStroop.Tests
{
    public static class ClockBoundaryTests
    {
        static PeerIdentity Peer()
        {return new PeerIdentity("clock-boundary","operator",PeerRole.Operator,"simulator/operator",ControlSource.Simulator,"remote-clock");}
        static ClockSynchronizer New()
        {return new ClockSynchronizer("local-clock","remote-clock",new DevelopmentClockConfiguration(3,.2,.2,2,200));}
        static bool Reply(ClockSynchronizer map,string id,double t0,double t1,double t2,double t3)
        {
            Check.True(map.RegisterRequest(id,t0));
            return map.ObserveReply(WirePacket.TimeReply(Peer(),0,t2,id,"local-clock",t0,t1),t3);
        }
        static void Accept(ClockSynchronizer map,string id,double t0,double t1,double t2,double t3)
        {Check.True(Reply(map,id,t0,t1,t2,t3),map.LastRejection);Check.True(map.Snapshot().Available,map.Snapshot().UnavailableReason);}
        static ClockSynchronizer Pinned(int count=3)
        {
            var map=New();for(int i=0;i<count;i++)Accept(map,"pin-"+i,i,i+.5,i+.5,i);return map;
        }
        static void Unchanged(ClockMappingSnapshot before,ClockMappingSnapshot after)
        {
            Check.Equal(before.Available,after.Available);Check.Equal(before.SampleCount,after.SampleCount);Check.Equal(before.Revision,after.Revision);
            Check.Equal(before.LocalReferenceSeconds,after.LocalReferenceSeconds);Check.Equal(before.RemoteReferenceSeconds,after.RemoteReferenceSeconds);
            Check.Equal(before.LocalSecondsPerRemoteSecond,after.LocalSecondsPerRemoteSecond);
            Check.Equal(before.MinimumLocalSecondsPerRemoteSecond,after.MinimumLocalSecondsPerRemoteSecond);
            Check.Equal(before.MaximumLocalSecondsPerRemoteSecond,after.MaximumLocalSecondsPerRemoteSecond);
            Check.Equal(before.RateUncertaintyLocalSecondsPerRemoteSecond,after.RateUncertaintyLocalSecondsPerRemoteSecond);
            Check.Equal(before.UncertaintySeconds,after.UncertaintySeconds);Check.Equal(before.PointEstimateMethod,after.PointEstimateMethod);
        }

        public static void Register(TestSuite t)
        {
            t.Run("M3 joint clock admission refines seed90 and accepts thirty tight-transit replies",()=>{
                const double rate=.99987815759763032,offset=.25060742765227678;
                double[,] times={
                    {6.3291410805327546,6.5877313349182458,6.5879615061488215,6.3381420059866462},
                    {6.495104248735637,6.7476185995231193,6.7502077541853,6.5007544490430211},
                    {6.6610674169385193,6.9109226470880154,6.9174651172658361,6.6683832210480238},
                    {6.8270305851414008,7.0768081138215528,7.081818708671098,6.8320450371445895}};
                var map=New();
                for(int i=0;i<3;i++)Accept(map,"seed90-"+i,times[i,0],times[i,1],times[i,2],times[i,3]);
                double oldSlope=map.Snapshot().LocalSecondsPerRemoteSecond;
                Check.True((times[3,3]-times[3,0])-oldSlope*(times[3,2]-times[3,1])<0,"Old point estimate would reject this physical exchange");
                Check.True((times[3,3]-times[3,0])-(times[3,2]-times[3,1])/rate>0);
                Accept(map,"seed90-3",times[3,0],times[3,1],times[3,2],times[3,3]);
                for(int i=0;i<30;i++)
                {
                    double t0=7+i*.2,t1=offset+rate*(t0+.000001),t2=offset+rate*(t0+.005001),t3=t0+.005002;
                    Accept(map,"tight-"+i,t0,t1,t2,t3);
                    var snapshot=map.Snapshot();Check.Equal(5+i,snapshot.SampleCount);Check.Equal((long)(5+i),snapshot.Revision);
                    Check.True(snapshot.MinimumLocalSecondsPerRemoteSecond<=1/rate && snapshot.MaximumLocalSecondsPerRemoteSecond>=1/rate);
                    foreach(double query in new[]{t2,t2+1})
                    {
                        double local,u;Check.True(map.TryMapRemote(query,out local,out u));
                        double truth=(query-offset)/rate;Check.True(Math.Abs(local-truth)<=u,"True clock stays within full causal uncertainty");
                    }
                }
            });
            t.Run("M3 RTT quality rejects a candidate without altering the accepted clock window",()=>{
                var map=Pinned();var before=map.Snapshot();
                Check.True(!Reply(map,"slow",3,3.51,3.52,3.4));
                Check.Equal("candidate_round_trip_quality_not_established",map.LastRejection);Unchanged(before,map.Snapshot());
                Check.True(!map.ExpireRequest("slow"),"Rejected reply correlation is consumed");
                // Receipt earlier than the rejected reply is valid: only accepted receipt time is retained.
                Accept(map,"next",3.05,3.56,3.57,3.08);Check.Equal(before.Revision+1,map.Revision);
            });
            t.Run("M3 RTT quality does not truncate feasible rates to hide an over-limit true clock",()=>{
                var map=New();
                // Local elapsed .4 minus remote processing .3 at slope 1 looks like .1.
                // The feasible true local/remote slope .1 instead has .37 transit.
                double t0=1,t1=10,t2=10.3,t3=1.4;
                Check.True((t3-t0)-(t2-t1)<.2);
                Check.True((t3-t0)-.1*(t2-t1)>.2);
                Check.True(.1*t1>=t0 && .1*t2<=t3,"Over-limit true map satisfies timestamp causality");
                Check.True(!Reply(map,"ambiguous-quality",t0,t1,t2,t3));
                Check.Equal("candidate_round_trip_quality_not_established",map.LastRejection);
                Check.Equal(0,map.Snapshot().SampleCount);Check.Equal(0L,map.Revision);Check.True(!map.Snapshot().Available);
                double local,u;Check.True(!map.TryMapRemote(10.2,out local,out u));
            });
            t.Run("M3 zero-processing replies enforce raw RTT and retain unbounded causal rate",()=>{
                var map=New();Accept(map,"at-limit",0,.25,.25,.2);
                var before=map.Snapshot();Check.Equal((double?)0,before.MinimumLocalSecondsPerRemoteSecond);Check.Equal(null,before.MaximumLocalSecondsPerRemoteSecond);
                Check.True(!Reply(map,"over-limit",1,1.25,1.25,1.21));Unchanged(before,map.Snapshot());
                var noDelay=New();Accept(noDelay,"zero",0,.5,.5,0);Check.Near(0,noDelay.Snapshot().UncertaintySeconds.Value);
            });
            t.Run("M3 initial clock estimate can use a feasible nonunit rate before drift fitting",()=>{
                var map=New();Accept(map,"bootstrap",0,1,1.1,.01);
                var snapshot=map.Snapshot();Check.True(!snapshot.DriftAvailable);Check.Equal("feasible_rate_interval_midpoint",snapshot.PointEstimateMethod);
                Check.Equal((double?)0,snapshot.MinimumLocalSecondsPerRemoteSecond);
                Check.Near(.01/(1.1-1),snapshot.MaximumLocalSecondsPerRemoteSecond.Value);
                Check.True(snapshot.LocalSecondsPerRemoteSecond>0 && snapshot.LocalSecondsPerRemoteSecond<snapshot.MaximumLocalSecondsPerRemoteSecond);
                // No quality-derived minimum rate was substituted for the causal zero bound.
                Check.True(snapshot.RateUncertaintyLocalSecondsPerRemoteSecond>=snapshot.LocalSecondsPerRemoteSecond);
            });
            t.Run("M3 incompatible affine candidate cannot poison a usable map",()=>{
                var map=Pinned();var before=map.Snapshot();
                Check.True(!Reply(map,"incompatible",3,3,3,3.01));Check.Equal("candidate_incompatible_rate_bounds",map.LastRejection);
                Unchanged(before,map.Snapshot());Accept(map,"feasible",4,4.5,4.5,4);
            });
            t.Run("M3 full64 clock window evicts only on a successfully admitted candidate",()=>{
                var map=Pinned(64);var before=map.Snapshot();Check.Equal(64,before.SampleCount);
                Check.True(!Reply(map,"rejected-65",64,64.5,64.5,64.3));Unchanged(before,map.Snapshot());
                Accept(map,"accepted-65",65,65.5,65.5,65);var after=map.Snapshot();
                Check.Equal(64,after.SampleCount);Check.Equal(65L,after.Revision);Check.Near(65.5,after.RemoteReferenceSeconds);
                Check.Equal((double?)1,after.MinimumLocalSecondsPerRemoteSecond);Check.Equal((double?)1,after.MaximumLocalSecondsPerRemoteSecond);
            });
            t.Run("M3 clock rollback invalidation remains latched after a feasible later reply",()=>{
                var map=Pinned();Check.True(!Reply(map,"rollback",3,.1,.1,3.01));
                Check.Equal("reordered_reply_or_remote_clock_rollback",map.LastRejection);Check.True(!map.Snapshot().Available);
                Check.Equal(4L,map.Revision);Check.Equal("clock_invalidated",map.Snapshot().UnavailableReason);
                Check.True(!Reply(map,"later",4,4.5,4.5,4));Check.Equal("candidate_clock_invalidated",map.LastRejection);
                Check.Equal(4L,map.Revision);Check.Equal(3,map.Snapshot().SampleCount);Check.True(!map.Snapshot().Available);
            });
        }
    }
}
