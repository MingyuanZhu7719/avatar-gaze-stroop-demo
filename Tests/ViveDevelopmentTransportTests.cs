using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    public static class ViveDevelopmentTransportTests
    {
        sealed class Clock : IClock
        {
            public double Now {get;set;}
            public string ClockId {get;private set;}
            public Clock(string id){ClockId=id;}
        }
        sealed class Provider : IViveProEyeProvider
        {
            public ViveProEyePoll Next;
            public ViveProEyePoll Poll(){return Next;}
        }
        sealed class BindingClock:IClock
        {
            public double Time;public bool Fail;
            public double Now {get{if(Fail)throw new IOException("synthetic clock fault");return Time;}}
            public string ClockId {get{return "operator-clock";}}
        }
        sealed class BindingFixture:IDisposable
        {
            public readonly IPEndPoint Local=new IPEndPoint(IPAddress.Loopback,41001),Remote=new IPEndPoint(IPAddress.Loopback,41002);
            public readonly BindingClock Clock=new BindingClock();public readonly ViveInMemoryDatagramSocket Socket;
            public readonly ViveDevelopmentTransport Transport;public readonly VivePreflightEndpointConfiguration Config;
            public BindingFixture()
            {
                string mapping=ViveProEyeRehearsalCoordinates.MappingVersion,stream=ViveDevelopmentTransport.OperatorStream(mapping,"epoch-1");
                var own=new PeerIdentity("binding-session","op",PeerRole.Operator,stream,ControlSource.LiveOperator,"operator-clock");
                var peer=new PeerIdentity("binding-session","p",PeerRole.Participant,"participant-source",ControlSource.LiveOperator,"participant-clock");
                Socket=new ViveInMemoryDatagramSocket(Local);Clock.Time=1;
                Transport=ViveDevelopmentTransport.CreateInMemoryFixture(Socket,Remote,Clock,own,peer,"vive-pro-eye-no-serial",mapping,"epoch-1",new DevelopmentFaultConfiguration(.5,.5,.5),new DevelopmentClockConfiguration(3,.2,.2,2,200),.3);
                Config=new VivePreflightEndpointConfiguration(PeerRole.Operator,new IPEndPoint(IPAddress.Loopback,41003),new IPEndPoint(IPAddress.Loopback,41004),false,"binding-session",stream,"operator-clock","participant-clock",mapping,"epoch-1","vive-pro-eye-no-serial",.3);
                Socket.Incoming.Enqueue(Tuple.Create(WireCodec.Encode(WirePacket.Hello(peer,0,1,SessionConfiguration.ProtocolVersion)),Remote));Transport.Tick();
            }
            public VivePreflightEndpointConfiguration Other(IPEndPoint local,IPEndPoint remote,bool allowRemote)
            {return new VivePreflightEndpointConfiguration(PeerRole.Operator,local,remote,allowRemote,Config.SessionId,Config.OperatorStreamId,Config.OperatorClockId,Config.ParticipantClockId,Config.MappingVersion,Config.Epoch,Config.ExpectedOperatorDeviceId,.3);}
            public void Dispose(){Transport.Dispose();}
        }
        sealed class Pair : IDisposable
        {
            public readonly Clock ParticipantClock=new Clock("participant-clock"),OperatorClock=new Clock("operator-clock");
            public readonly ViveDevelopmentTransport Participant,Operator;
            public readonly Provider Sensor=new Provider();
            public readonly ViveProEyeHeadEyeSource Source;
            public readonly string Stream,Mapping;
            public int AppliedStops;
            int frame;
            public Pair(bool synthetic=false)
            {
                Mapping=ViveProEyeRehearsalCoordinates.MappingVersion;
                Stream=ViveDevelopmentTransport.OperatorStream(Mapping,"epoch-1");
                var op=new PeerIdentity("test-session","op",PeerRole.Operator,Stream,ControlSource.LiveOperator,OperatorClock.ClockId);
                var p=new PeerIdentity("test-session","p",PeerRole.Participant,"vive-participant-dev",ControlSource.LiveOperator,ParticipantClock.ClockId);
                int a=41011,b=41012;
                var local=new IPEndPoint(IPAddress.Loopback,a);var remote=new IPEndPoint(IPAddress.Loopback,b);
                var participantSocket=new ViveInMemoryDatagramSocket(local);var operatorSocket=new ViveInMemoryDatagramSocket(remote);
                participantSocket.Other=operatorSocket;operatorSocket.Other=participantSocket;
                var faults=new DevelopmentFaultConfiguration(.5,.5,.5);
                var clocks=new DevelopmentClockConfiguration(3,.2,.2,2,200);
                Participant=ViveDevelopmentTransport.CreateInMemoryFixture(participantSocket,remote,ParticipantClock,p,op,"vive-pro-eye-no-serial",Mapping,"epoch-1",faults,clocks,.3,()=>AppliedStops++);
                Operator=ViveDevelopmentTransport.CreateInMemoryFixture(operatorSocket,local,OperatorClock,op,p,"vive-pro-eye-no-serial",Mapping,"epoch-1",faults,clocks,.3);
                Source=new ViveProEyeHeadEyeSource("test-session",0,0,Stream,OperatorClock,Sensor,new ViveProEyeRehearsalCoordinates());
            }
            public void Step(bool sendTask=true)
            {
                ParticipantClock.Now+=.025;OperatorClock.Now+=.025;
                Participant.Tick();Operator.Tick();
                if(sendTask)Participant.SendTask(new TaskSnapshot("test-session",0,0,"trial-1",Colour.Red,Colour.Blue,Phase.MainTask,Condition.A));
                Thread.Sleep(1);
            }
            public void Warm(){for(int i=0;i<60;i++)Step();}
            public ViveProEyeObservation Observation(bool eyeValid=true,int block=0,int segment=0)
            {
                frame++;double time=OperatorClock.Now;
                var ray=new ViveEyeRay(eyeValid?2UL:0UL,false,eyeValid,new Vec3(0,0,0),new Vec3(.5,0,1));
                Sensor.Next=new ViveProEyePoll(new ViveHeadFrame(frame,time,true,true,Rotation.Identity),
                    new ViveEyeFrame(frame,frame*8,time,false,null,null,ray),ViveEyeRuntimeState.Working,ViveEyeCalibrationState.ReportedReady,true,0);
                HeadEyeSample ignored;
                var source=block==0&&segment==0?Source:new ViveProEyeHeadEyeSource("test-session",block,segment,Stream,OperatorClock,Sensor,new ViveProEyeRehearsalCoordinates());
                Check.True(source.TryRead(out ignored));return source.LastObservation;
            }
            public void Dispose(){Participant.Dispose();Operator.Dispose();}
        }
        public static void Register(TestSuite suite)
        {
            suite.Run("VIVE synthetic no-task engineering peer transports then hides lost pose",()=>{
                string mapping=ViveProEyeRehearsalCoordinates.MappingVersion,epoch="pose-epoch",session="engineering-only";
                string stream=ViveDevelopmentTransport.EngineeringOperatorStream(mapping,epoch);
                Check.True(stream!=ViveDevelopmentTransport.OperatorStream(mapping,epoch));
                var op=new PeerIdentity(session,"op",PeerRole.Operator,stream,ControlSource.LiveOperator,"operator-clock");
                var participant=new PeerIdentity(session,"participant",PeerRole.Participant,"engineering-participant",ControlSource.LiveOperator,"participant-clock");
                var a=new IPEndPoint(IPAddress.Loopback,41101);var b=new IPEndPoint(IPAddress.Loopback,41102);
                var faults=new DevelopmentFaultConfiguration(.5,.5,.5);var clocks=new DevelopmentClockConfiguration(3,.2,.2,2,200);
                var oc=new ViveWozEngineeringConfiguration(op,participant,a,b,false,mapping,epoch,faults,clocks,.3);
                var pc=new ViveWozEngineeringConfiguration(participant,op,b,a,false,mapping,epoch,faults,clocks,.3);
                Check.Throws<ArgumentException>(()=>new ViveWozEngineeringConfiguration(op,participant,a,new IPEndPoint(IPAddress.Broadcast,41103),true,mapping,epoch,faults,clocks,.3));
                Check.Throws<ArgumentException>(()=>new ViveWozEngineeringConfiguration(op,participant,a,new IPEndPoint(IPAddress.Parse("239.1.1.1"),41103),true,mapping,epoch,faults,clocks,.3));
                var operatorClock=new Clock("operator-clock");var participantClock=new Clock("participant-clock");var sensor=new Provider();
                Check.Throws<InvalidOperationException>(()=>ViveWozEngineeringPeer.Start(oc,operatorClock,sensor,false,false));
                Check.Throws<InvalidOperationException>(()=>ViveWozEngineeringPeer.Start(oc,operatorClock,sensor,true,true));
                var left=new ViveInMemoryDatagramSocket(a);var right=new ViveInMemoryDatagramSocket(b);left.Other=right;right.Other=left;
                using(var realWorker=new ViveProEyeWorker(operatorClock,()=>{throw new InvalidOperationException("must not poll head");},()=>{throw new InvalidOperationException("must not create client");}))
                    Check.Throws<InvalidOperationException>(()=>ViveWozEngineeringPeer.StartSynthetic(oc,operatorClock,realWorker,left));
                using(var sender=ViveWozEngineeringPeer.StartSynthetic(oc,operatorClock,sensor,left))
                using(var receiver=ViveWozEngineeringPeer.StartSynthetic(pc,participantClock,null,right))
                {
                    Check.True(!sender.CanEnterLiveCountdown&&!sender.CanRecordOrExport&&!sender.ApparatusMappingValidated&&!sender.CryptographicallyAuthenticated);
                    for(int i=0;i<60;i++){operatorClock.Now+=.025;participantClock.Now+=.025;sender.TickOperator(false);receiver.TickParticipant();}
                    double time=operatorClock.Now;var ray=new ViveEyeRay(2UL,false,true,new Vec3(0,0,0),new Vec3(.5,0,1));
                    sensor.Next=new ViveProEyePoll(new ViveHeadFrame(1,time,true,true,Rotation.Identity),new ViveEyeFrame(1,8,time,false,null,null,ray),ViveEyeRuntimeState.Working,ViveEyeCalibrationState.ReportedReady,true,0);
                    // Give the simulated receive a later timestamp than the
                    // sender's uncertainty interval. An immediate same-clock
                    // receipt is now conservatively hidden by the WoZ fixture.
                    sender.TickOperator(true);participantClock.Now+=.15;receiver.TickParticipant();Check.True(receiver.CurrentPose!=null);
                    Check.Equal(stream,receiver.CurrentPose.StreamId);Check.True(!receiver.CurrentPose.TrialBound);
                    sender.TickOperator(false);receiver.TickParticipant();Check.True(receiver.CurrentPose==null);
                    Check.True(!sender.Faulted&&!receiver.Faulted);
                }
            });
            suite.Run("VIVE synthetic engineering transport rejects task and preflight operations",()=>{
                string mapping=ViveProEyeRehearsalCoordinates.MappingVersion,epoch="pose-epoch",session="engineering-only";
                string stream=ViveDevelopmentTransport.EngineeringOperatorStream(mapping,epoch);
                var op=new PeerIdentity(session,"op",PeerRole.Operator,stream,ControlSource.LiveOperator,"operator-clock");
                var participant=new PeerIdentity(session,"participant",PeerRole.Participant,"engineering-participant",ControlSource.LiveOperator,"participant-clock");
                var a=new IPEndPoint(IPAddress.Loopback,41111);var b=new IPEndPoint(IPAddress.Loopback,41112);
                var left=new ViveInMemoryDatagramSocket(a);var right=new ViveInMemoryDatagramSocket(b);left.Other=right;right.Other=left;
                var faults=new DevelopmentFaultConfiguration(.5,.5,.5);var clocks=new DevelopmentClockConfiguration(3,.2,.2,2,200);
                var clock=new Clock("participant-clock");
                using(var rejectedPeer=new UdpPeer(a,b,false,new ViveInMemoryDatagramSocket(a)))
                    Check.Throws<InvalidOperationException>(()=>new ViveDevelopmentTransport(rejectedPeer,clock,participant,op,"vive-pro-eye-no-serial",mapping,epoch,faults,clocks,.3,null,true));
                using(var transport=ViveDevelopmentTransport.CreateInMemoryFixture(left,b,clock,participant,op,"vive-pro-eye-no-serial",mapping,epoch,faults,clocks,.3,null,true))
                {
                    Check.True(transport.EngineeringPoseOnly&&!transport.CanRecordOrExport);
                    Check.Throws<InvalidOperationException>(()=>transport.SendTask(new TaskSnapshot(session,0,0,null,null,null,Phase.BlockReady,Condition.A)));
                    Check.Throws<InvalidOperationException>(()=>transport.CreatePreflightChannel(null));
                    Check.Throws<InvalidOperationException>(()=>transport.SendSafetyStop());
                }
                var normalOp=new PeerIdentity(session,"op",PeerRole.Operator,ViveDevelopmentTransport.OperatorStream(mapping,epoch),ControlSource.LiveOperator,"operator-clock");
                Check.Throws<ArgumentException>(()=>new ViveWozEngineeringConfiguration(participant,normalOp,a,b,false,mapping,epoch,faults,clocks,.3));
            });
            suite.Run("VIVE synthetic pose path retains tracking evidence and expires without new samples",()=>{
                using(var pair=new Pair(true))
                {
                    pair.Warm();var observation=pair.Observation();Check.True(pair.Operator.SendObservation(observation),pair.Operator.LastSendRejection);
                    for(int i=0;i<3;i++)pair.Step();
                    var pose=pair.Participant.CurrentPose(0,0);Check.True(pose!=null);
                    Check.Equal(observation.Sample.Sequence,pose.SampleSequence);Check.Near(observation.Sample.CaptureLocalSeconds,pose.RemoteObservedSeconds);
                    Check.Equal("test-session",pose.SessionId);Check.Equal(pair.Stream,pose.StreamId);Check.Equal("operator-clock",pose.RemoteClockId);
                    Check.True(pose.TrackingPacketSequence>=0&&!pose.TrialBound&&!pose.CryptographicallyAuthenticated);
                    double originalReceipt=pose.LocalReceivedSeconds;long originalPacket=pose.TrackingPacketSequence;
                    pair.Step();var repeated=pair.Participant.CurrentPose(0,0);Check.True(repeated!=null);
                    Check.Equal(originalPacket,repeated.TrackingPacketSequence);Check.Near(originalReceipt,repeated.LocalReceivedSeconds);
                    Check.True(pair.Participant.CurrentPose(0,1)==null);
                    for(int i=0;i<25;i++)pair.Step();
                    Check.True(pair.Participant.CurrentPose(0,0)==null);
                }
            });
            suite.Run("VIVE preflight is bound to peer IPs and remote network opt-in",()=>{
                using(var f=new BindingFixture()){
                    using(var channel=f.Transport.CreatePreflightChannel(f.Config))Check.True(!channel.Active);
                    var alternate=IPAddress.Parse("127.0.0.2");
                    Check.Throws<ArgumentException>(()=>f.Transport.CreatePreflightChannel(f.Other(new IPEndPoint(alternate,41003),f.Config.Remote,false)));
                    Check.Throws<ArgumentException>(()=>f.Transport.CreatePreflightChannel(f.Other(f.Config.Local,new IPEndPoint(alternate,41004),false)));
                    Check.Throws<ArgumentException>(()=>f.Transport.CreatePreflightChannel(f.Other(f.Config.Local,f.Config.Remote,true)));
                }
            });
            suite.Run("VIVE bound preflight fails closed on silent stale peer without a transport tick",()=>{
                using(var f=new BindingFixture()){
                    using(var channel=f.Transport.CreatePreflightChannel(f.Config)){
                    var socket=new ViveInMemoryDatagramSocket(f.Config.Local);channel.StartForTest(socket);Check.True(channel.Active);
                        f.Clock.Time+=1;Check.True(!channel.Active&&channel.RecoveryDecisionRequired&&socket.Closed);
                        Check.Throws<InvalidOperationException>(()=>f.Transport.CreatePreflightChannel(f.Config));
                    }
                }
            });
            suite.Run("VIVE bound preflight closes immediately on control peer dispose or connection fault",()=>{
                for(int scenario=0;scenario<3;scenario++)using(var f=new BindingFixture()){
                    using(var channel=f.Transport.CreatePreflightChannel(f.Config)){
                    var socket=new ViveInMemoryDatagramSocket(f.Config.Local);channel.StartForTest(socket);
                        if(scenario==0)f.Transport.Dispose();else if(scenario==1){f.Socket.FailPoll=true;f.Transport.Tick();}else{f.Socket.FailUnexpected=true;Check.Throws<IOException>(()=>f.Transport.Tick());}
                        Check.True(socket.Closed&&!channel.Active&&channel.RecoveryDecisionRequired);
                    }
                }
            });
            suite.Run("VIVE bound preflight fails closed on control clock exception",()=>{
                using(var f=new BindingFixture())using(var channel=f.Transport.CreatePreflightChannel(f.Config)){
                    var socket=new ViveInMemoryDatagramSocket(f.Config.Local);channel.StartForTest(socket);f.Clock.Fail=true;
                    Check.True(!channel.Active&&channel.RecoveryDecisionRequired&&socket.Closed);
                    Check.Throws<InvalidOperationException>(()=>f.Transport.CreatePreflightChannel(f.Config));
                }
            });
            suite.Run("VIVE preflight shares the existing peer clock map and original task receipt",()=>{
                using(var pair=new Pair())
                {
                    pair.Warm();TaskSnapshot first,again;long sequence,repeat;double receipt,repeatedReceipt;
                    Check.True(pair.Operator.TryGetCurrentTaskEvidence(out first,out sequence,out receipt));
                    Check.True(pair.Operator.TryGetCurrentTaskEvidence(out again,out repeat,out repeatedReceipt));
                    Check.True(Object.ReferenceEquals(first,again));Check.Equal(sequence,repeat);Check.Equal(receipt,repeatedReceipt);
                    var local=new IPEndPoint(IPAddress.Loopback,45111);var remote=new IPEndPoint(IPAddress.Loopback,45112);
                    var matching=new VivePreflightEndpointConfiguration(PeerRole.Operator,local,remote,false,"test-session",pair.Stream,
                        "operator-clock","participant-clock",pair.Mapping,"epoch-1","vive-pro-eye-no-serial",.3);
                    using(var channel=pair.Operator.CreatePreflightChannel(matching))
                    {Check.True(!channel.Active&&!channel.CanEnterCountdown&&!channel.CryptographicallyAuthenticated);}
                    var wrongSession=new VivePreflightEndpointConfiguration(PeerRole.Operator,local,remote,false,"other-session",pair.Stream,
                        "operator-clock","participant-clock",pair.Mapping,"epoch-1","vive-pro-eye-no-serial",.3);
                    Check.Throws<ArgumentException>(()=>pair.Operator.CreatePreflightChannel(wrongSession));
                    pair.Operator.Dispose();
                    Check.True(!pair.Operator.TryGetCurrentTaskEvidence(out first,out sequence,out receipt));
                    Check.Throws<InvalidOperationException>(()=>pair.Operator.CreatePreflightChannel(matching));
                }
            });
            suite.Run("VIVE invalid and valid packet ordering cannot revive or erase newer state",()=>{
                var peer=new PeerIdentity("test-session","op",PeerRole.Operator,"vive-pro-eye/operator/test",ControlSource.LiveOperator,"operator-clock");
                var monitor=new ReceiveMonitor(peer,new DevelopmentFaultConfiguration(10,10,10),"vive-pro-eye-no-serial","participant-clock");
                Func<long,double,bool,WirePacket> packet=(seq,time,valid)=>WirePacket.Tracking(peer,seq,
                    new HeadEyeSample(peer.SessionId,peer.SourceId,"vive-pro-eye-no-serial",peer.ClockId,0,0,seq,time,time,
                        ControlSource.LiveOperator,StreamRole.OperatorControl,valid?Rotation.Identity:new Rotation(0,0,0,0),
                        valid?new Vec3(0,0,1):new Vec3(0,0,0),valid,valid));
                Check.True(monitor.Observe(WirePacket.Hello(peer,0,1,SessionConfiguration.ProtocolVersion),1).Accepted);
                Check.True(monitor.Observe(packet(1,2,true),2).Accepted);
                Check.True(monitor.Observe(packet(3,3,false),3).Accepted);
                Check.True(!monitor.LastSample.HeadValid&&!monitor.LastSample.EyeValid);
                Check.True(!monitor.Observe(packet(2,2.5,true),3.1).Accepted);
                Check.True(!monitor.LastSample.HeadValid&&!monitor.LastSample.EyeValid);
                Check.True(monitor.Observe(packet(4,4,true),4).Accepted);
                Check.True(monitor.LastSample.HeadValid&&monitor.LastSample.EyeValid);
                Check.True(!monitor.Observe(packet(3,3,false),4.1).Accepted);
                Check.True(monitor.LastSample.HeadValid&&monitor.LastSample.EyeValid);
            });
            suite.Run("VIVE development invalid state immediately clears pose and old input cannot revive it",()=>{
                using(var pair=new Pair())
                {
                    pair.Warm();var old=pair.Observation();Check.True(pair.Operator.SendObservation(old));
                    for(int i=0;i<4;i++)pair.Step();
                    Check.True(pair.Participant.CurrentPose(0,0)!=null);
                    var invalid=pair.Observation(false);
                    Check.True(!pair.Operator.SendObservation(invalid));
                    Check.True(pair.Operator.LastInvalidationSent);
                    for(int i=0;i<4;i++)pair.Step();
                    Check.True(pair.Participant.CurrentPose(0,0)==null);
                    Check.True(!pair.Operator.SendObservation(old));
                    Check.True(pair.Participant.CurrentPose(0,0)==null);
                    var fresh=pair.Observation();Check.True(pair.Operator.SendObservation(fresh),pair.Operator.LastSendRejection);
                    for(int i=0;i<4;i++)pair.Step();
                    Check.True(pair.Participant.CurrentPose(0,0)!=null);
                    // An invalid observation for another context must not clear the current pose.
                    Check.True(!pair.Operator.SendObservation(pair.Observation(false,1,0)));
                    Check.True(!pair.Operator.LastInvalidationSent);
                    for(int i=0;i<4;i++)pair.Step();
                    Check.True(pair.Participant.CurrentPose(0,0)!=null);
                    Check.True(pair.Operator.InvalidateLastPublishedPose());
                    for(int i=0;i<4;i++)pair.Step();
                    Check.True(pair.Participant.CurrentPose(0,0)==null);
                    Check.True(!pair.Operator.SendObservation(fresh));
                }
            });
            suite.Run("VIVE in-memory paired peers deliver mapped pose and acknowledged safety stop",()=>{
                using(var pair=new Pair())
                {
                    pair.Warm();var observation=pair.Observation();double originalSend=observation.Sample.SendLocalSeconds;
                    // A newer heartbeat/time exchange must not make an already
                    // acquired, still-fresh observation look like clock rollback.
                    pair.ParticipantClock.Now+=.15;pair.OperatorClock.Now+=.15;
                    pair.Participant.Tick();pair.Operator.Tick();
                    Check.True(pair.Operator.SendObservation(observation),pair.Operator.LastSendRejection);
                    Check.Equal(originalSend,observation.Sample.SendLocalSeconds);
                    for(int i=0;i<10;i++)pair.Step();
                    var pose=pair.Participant.CurrentPose(0,0);
                    Check.True(pose!=null);Check.True(pose.DerivedMappedEyeDirection.IsUnit);
                    Check.Equal(pair.Mapping,pose.MappingVersion);Check.True(!pose.HardwareCaptureTimeVerified&&!pose.ApparatusMappingValidated);
                    Check.Equal("test-session",pose.SessionId);Check.Equal(pair.Stream,pose.StreamId);
                    Check.Equal("operator-clock",pose.RemoteClockId);Check.Equal(0,pose.BlockIndex);Check.Equal(0,pose.Segment);
                    Check.Equal(observation.Sample.Sequence,pose.SampleSequence);Check.Near(observation.Sample.CaptureLocalSeconds,pose.RemoteObservedSeconds);
                    Check.True(pose.TrackingPacketSequence>=0&&!pose.TrialBound&&!pose.CryptographicallyAuthenticated);
                    var reread=pair.Participant.CurrentPose(0,0);
                    Check.Equal(pose.TrackingPacketSequence,reread.TrackingPacketSequence);Check.Near(pose.LocalReceivedSeconds,reread.LocalReceivedSeconds);
                    Check.True(pair.Participant.CurrentPose(1,0)==null&&pair.Participant.CurrentPose(0,1)==null);
                    Check.True(pair.Operator.SendSafetyStop());
                    for(int i=0;i<10;i++)pair.Step();
                    Check.Equal(1,pair.AppliedStops);Check.True(pair.Operator.SafetyStopAcknowledged&&!pair.Operator.SafetyStopPending);
                }
            });
            suite.Run("VIVE development transport rejects incompatible stale invalid and mismatched context",()=>{
                using(var pair=new Pair())
                {
                    pair.Warm();var observation=pair.Observation();
                    var wrong=new ViveProEyeObservation {Sample=observation.Sample,RawPoll=observation.RawPoll,HeadUsable=true,EyeUsable=true,MappingVersion="different-map"};
                    Check.True(!pair.Operator.SendObservation(wrong));Check.Equal("missing_or_incompatible_development_provenance",pair.Operator.LastSendRejection);
                    Check.True(!pair.Operator.SendObservation(pair.Observation(false)));Check.Equal("unusable_or_unencodable_channels",pair.Operator.LastSendRejection);
                    Check.True(!pair.Operator.SendObservation(pair.Observation(true,1,0)));Check.Equal("current_task_or_context_unavailable",pair.Operator.LastSendRejection);
                    var sourceSample=observation.Sample;
                    var foreignSample=new HeadEyeSample("wrong-session",sourceSample.StreamId,sourceSample.DeviceId,sourceSample.ClockId,
                        0,0,9,sourceSample.CaptureLocalSeconds,sourceSample.SendLocalSeconds,sourceSample.Source,sourceSample.Role,
                        sourceSample.RawHeadRotation,sourceSample.RawEyeDirection,true,true);
                    var foreign=new ViveProEyeObservation {Sample=foreignSample,RawPoll=observation.RawPoll,HeadUsable=true,EyeUsable=true,MappingVersion=pair.Mapping};
                    Check.True(!pair.Operator.SendObservation(foreign));Check.Equal("wrong_source_identity",pair.Operator.LastSendRejection);
                    var futureTime=pair.OperatorClock.Now+1;
                    var futureSample=new HeadEyeSample(sourceSample.SessionId,sourceSample.StreamId,sourceSample.DeviceId,sourceSample.ClockId,
                        0,0,10,futureTime,futureTime,sourceSample.Source,sourceSample.Role,sourceSample.RawHeadRotation,sourceSample.RawEyeDirection,true,true);
                    var future=new ViveProEyeObservation {Sample=futureSample,RawPoll=observation.RawPoll,HeadUsable=true,EyeUsable=true,MappingVersion=pair.Mapping};
                    Check.True(!pair.Operator.SendObservation(future));Check.Equal("stale_or_future_local_observation",pair.Operator.LastSendRejection);
                    var old=pair.Observation();pair.OperatorClock.Now+=.4;
                    Check.True(!pair.Operator.SendObservation(old));Check.Equal("stale_or_future_local_observation",pair.Operator.LastSendRejection);
                }
            });
            suite.Run("VIVE development receiver stays latched after transport freshness is lost",()=>{
                using(var pair=new Pair())
                {
                    pair.Warm();Check.True(pair.Operator.SendObservation(pair.Observation()));
                    for(int i=0;i<5;i++)pair.Step();Check.True(pair.Participant.CurrentPose(0,0)!=null);
                    pair.ParticipantClock.Now+=1;pair.OperatorClock.Now+=1;
                    Check.True(pair.Participant.RecoveryDecisionRequired);
                    for(int i=0;i<50;i++)pair.Step();
                    Check.True(pair.Participant.RecoveryDecisionRequired);
                    Check.True(pair.Participant.CurrentPose(0,0)==null);
                }
            });
            suite.Run("VIVE development endpoint requires explicit remote opt in",()=>{
                var local=new IPEndPoint(IPAddress.Any,0);var remote=new IPEndPoint(IPAddress.Parse("192.0.2.1"),12000);
                Check.Throws<ArgumentException>(()=>new UdpPeer(local,remote,false));
                Check.Throws<ArgumentException>(()=>ViveDevelopmentTransport.OperatorStream(null,"epoch"));
                Check.True(ViveDevelopmentTransport.OperatorStream("one","epoch")!=ViveDevelopmentTransport.OperatorStream("two","epoch"));
                Check.True(ViveDevelopmentTransport.OperatorStream("one","epoch")!=ViveDevelopmentTransport.OperatorStream("one","other"));
            });
        }
    }
}
