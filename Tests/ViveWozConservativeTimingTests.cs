using System;
using System.Net;
using System.Threading;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    // In-process clocks, provider and sealed queues only. No OS network,
    // headset, authentication, file, credential or participant data.
    public static class ViveWozConservativeTimingTests
    {
        sealed class Clock : IClock
        {
            public double Now {get;set;}
            public string ClockId {get;private set;}
            public Clock(string id){ClockId=id;}
        }
        sealed class Sensor : IViveProEyeProvider
        {
            public ViveProEyePoll Next;
            public ViveProEyePoll Poll(){return Next;}
        }
        sealed class Pair : IDisposable
        {
            readonly string stream;
            readonly Sensor sensor=new Sensor();
            readonly ViveProEyeHeadEyeSource source;
            int sequence;
            public readonly Clock ParticipantClock=new Clock("participant-clock"),OperatorClock=new Clock("operator-clock");
            public readonly ViveDevelopmentTransport Participant,Operator;
            public readonly ViveInMemoryDatagramSocket OperatorSocket,ParticipantSocket;
            public Pair()
            {
                string map=ViveProEyeRehearsalCoordinates.MappingVersion;
                stream=ViveDevelopmentTransport.EngineeringOperatorStream(map,"test-epoch");
                var op=new PeerIdentity("test-session","operator",PeerRole.Operator,stream,ControlSource.LiveOperator,OperatorClock.ClockId);
                var participant=new PeerIdentity("test-session","participant",PeerRole.Participant,"fixture-participant",ControlSource.LiveOperator,ParticipantClock.ClockId);
                var opEndpoint=new IPEndPoint(IPAddress.Loopback,43101);var participantEndpoint=new IPEndPoint(IPAddress.Loopback,43102);
                var left=new ViveInMemoryDatagramSocket(opEndpoint);var right=new ViveInMemoryDatagramSocket(participantEndpoint);
                OperatorSocket=left;ParticipantSocket=right;
                left.Other=right;right.Other=left;
                var faults=new DevelopmentFaultConfiguration(.5,.5,.5);var clocks=new DevelopmentClockConfiguration(3,.2,.2,2,200);
                Operator=ViveDevelopmentTransport.CreateInMemoryFixture(left,participantEndpoint,OperatorClock,op,participant,"vive-pro-eye-no-serial",map,"test-epoch",faults,clocks,.3,null,true);
                Participant=ViveDevelopmentTransport.CreateInMemoryFixture(right,opEndpoint,ParticipantClock,participant,op,"vive-pro-eye-no-serial",map,"test-epoch",faults,clocks,.3,null,true);
                source=new ViveProEyeHeadEyeSource("test-session",0,0,stream,OperatorClock,sensor,new ViveProEyeRehearsalCoordinates());
            }
            public void Warm()
            {
                for(int i=0;i<80;i++){OperatorClock.Now+=.025;ParticipantClock.Now+=.025;Operator.Tick();Participant.Tick();}
                Check.True(Participant.DevelopmentClockAvailable,"synthetic map unavailable");
            }
            public bool Send(bool head,bool eye)
            {
                int next=++sequence;double time=OperatorClock.Now;
                var ray=new ViveEyeRay(eye?2UL:0UL,false,eye,new Vec3(0,0,0),new Vec3(.5,0,1));
                sensor.Next=new ViveProEyePoll(new ViveHeadFrame(next,time,true,head,Rotation.Identity),
                    new ViveEyeFrame(next,next*8,time,false,null,null,ray),ViveEyeRuntimeState.Working,ViveEyeCalibrationState.ReportedReady,true,0);
                HeadEyeSample ignored;Check.True(source.TryRead(out ignored),"synthetic source unavailable");
                return Operator.SendObservation(source.LastObservation);
            }
            public void ReceiveAfter(double seconds){ParticipantClock.Now+=seconds;Participant.Tick();}
            public void Dispose(){Participant.Dispose();Operator.Dispose();}
        }
        public static void Register(TestSuite suite)
        {
            suite.Run("WOZ engineering timing requires the whole interval before original receipt",()=>{
                Check.True(ViveDevelopmentTransport.ConservativeEngineeringCaptureTime(10,.125,10,.125,10.125,10.25,.375,0));
                Check.True(!ViveDevelopmentTransport.ConservativeEngineeringCaptureTime(10,.125,10,.125,10.1,10.25,.5,0));
                // The oldest axis can precede receipt while the send upper bound
                // still allows the other axis to fall after it.
                Check.True(!ViveDevelopmentTransport.ConservativeEngineeringCaptureTime(9.8,.05,10.1,.1,10.15,10.25,.6,0));
                Check.True(!ViveDevelopmentTransport.ConservativeEngineeringCaptureTime(10,.125,10,.125,10.125,10.5,.5,0));
                Check.True(!ViveDevelopmentTransport.ConservativeEngineeringCaptureTime(10,.125,10,.125,10.125,10.0,.5,0));
            });
            suite.Run("WOZ engineering timing rejects epoch crossing nonfinite values and overflow",()=>{
                Check.True(!ViveDevelopmentTransport.ConservativeEngineeringCaptureTime(.05,.1,.05,.1,.2,.2,.5,0));
                Check.True(!ViveDevelopmentTransport.ConservativeEngineeringCaptureTime(2.0,.125,2.0,.125,2.2,2.2,.5,1.9));
                Check.True(!ViveDevelopmentTransport.ConservativeEngineeringCaptureTime(Double.NaN,.1,1,.1,2,2,2,0));
                Check.True(!ViveDevelopmentTransport.ConservativeEngineeringCaptureTime(1,-.1,1,.1,2,2,2,0));
                Check.True(!ViveDevelopmentTransport.ConservativeEngineeringCaptureTime(1.5,.1,1,.1,2,2,2,0));
                Check.True(!ViveDevelopmentTransport.ConservativeEngineeringCaptureTime(1,.1,Double.MaxValue,Double.MaxValue,2,2,2,0));
            });
            suite.Run("WOZ engineering delayed synthetic observations deliver while each invalid axis clears",()=>{
                using(var pair=new Pair())
                {
                    pair.Warm();Check.True(pair.Send(true,true));pair.ReceiveAfter(.15);
                    Check.True(pair.Participant.CurrentPose(0,0)!=null,"timely delayed fixture did not deliver");
                    pair.OperatorClock.Now+=.02;Check.True(!pair.Send(false,true));pair.ReceiveAfter(.02);
                    Check.True(pair.Participant.CurrentPose(0,0)==null,"invalid head retained an old pose");
                    pair.OperatorClock.Now+=.02;Check.True(!pair.Send(true,false));pair.ReceiveAfter(.02);
                    Check.True(pair.Participant.CurrentPose(0,0)==null,"invalid eye retained an old pose");
                }
            });
            suite.Run("WOZ engineering shared sample whose send interval crosses receipt stays hidden",()=>{
                using(var pair=new Pair())
                {
                    pair.Warm();Check.True(pair.Send(true,true));pair.ReceiveAfter(0);
                    Check.True(pair.Participant.CurrentPose(0,0)==null,"future-uncertain sample was exposed");
                }
            });
            suite.Run("WOZ engineering clock rollback and cross-thread access cannot return pose",()=>{
                using(var pair=new Pair())
                {
                    pair.Warm();Check.True(pair.Send(true,true));pair.ReceiveAfter(.15);Check.True(pair.Participant.CurrentPose(0,0)!=null);
                    Exception cross=null;var thread=new Thread(()=>{try{pair.Participant.CurrentPose(0,0);}catch(Exception error){cross=error;}});
                    thread.Start();thread.Join();Check.True(cross is InvalidOperationException,"cross-thread read was not rejected");
                    cross=null;thread=new Thread(()=>{try{pair.Participant.Tick();}catch(Exception error){cross=error;}});
                    thread.Start();thread.Join();Check.True(cross is InvalidOperationException,"cross-thread update was not rejected");
                    pair.ParticipantClock.Now-=.25;
                    Check.Throws<InvalidOperationException>(()=>pair.Participant.CurrentPose(0,0));
                    Check.True(pair.Participant.CurrentPose(0,0)==null,"faulted clock returned a pose");
                }
            });
            suite.Run("WOZ engineering duplicate cannot renew pose and expiry clears it",()=>{
                using(var pair=new Pair())
                {
                    pair.Warm();Check.True(pair.Send(true,true));var original=(byte[])pair.OperatorSocket.Sent.Clone();
                    pair.ReceiveAfter(.15);Check.True(pair.Participant.CurrentPose(0,0)!=null);
                    pair.ParticipantSocket.Incoming.Enqueue(Tuple.Create(original,pair.OperatorSocket.LocalEndpoint));
                    pair.ReceiveAfter(.2);Check.True(pair.Participant.CurrentPose(0,0)!=null,"duplicate invalidated the still-fresh accepted pose");
                    pair.ReceiveAfter(.4);Check.True(pair.Participant.CurrentPose(0,0)==null,"duplicate extended the old pose");
                }
            });
            suite.Run("WOZ engineering synthetic disconnect clears current pose",()=>{
                using(var pair=new Pair())
                {
                    pair.Warm();Check.True(pair.Send(true,true));pair.ReceiveAfter(.15);Check.True(pair.Participant.CurrentPose(0,0)!=null);
                    pair.ParticipantSocket.FailPoll=true;pair.Participant.Tick();
                    Check.True(pair.Participant.CurrentPose(0,0)==null,"connection fault retained a pose");
                }
            });
        }
    }
}
