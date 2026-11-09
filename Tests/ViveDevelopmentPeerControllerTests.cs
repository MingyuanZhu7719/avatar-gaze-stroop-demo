using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    public static class ViveDevelopmentPeerControllerTests
    {
        sealed class Clock : IClock
        {public double Now {get;set;} public string ClockId {get;private set;} public Clock(string id){ClockId=id;}}
        sealed class Provider : IViveProEyeProvider
        {
            readonly Clock clock;int sequence;
            public bool Available=true,Invalid,Throw;
            public int Reads;
            public Provider(Clock clock){this.clock=clock;}
            public ViveProEyePoll Poll()
            {
                Reads++;if(Throw)throw new InvalidOperationException("private detail");
                if(!Available)return null;
                int id=++sequence;double time=clock.Now;
                var ray=new ViveEyeRay(Invalid?0UL:2UL,false,!Invalid,new Vec3(0,0,0),new Vec3(.2,0,1));
                return new ViveProEyePoll(new ViveHeadFrame(id,time,true,true,Rotation.Identity),
                    new ViveEyeFrame(id,id*8,time,false,null,null,ray),ViveEyeRuntimeState.Working,ViveEyeCalibrationState.ReportedReady,true,0);
            }
        }
        sealed class Pair : IDisposable
        {
            public readonly Clock ParticipantClock=new Clock("participant-controller-clock"), OperatorClock=new Clock("operator-controller-clock");
            public readonly ViveDevelopmentPeerController Participant,Operator;
            public readonly Provider Sensor;
            public TaskSnapshot Task;
            public bool Live=true,Ready=true,SourceReady=true,FailFactory,WrongStream;
            public int FactoryCalls,Stops;
            readonly string stream;
            public Pair()
            {
                var mapping=ViveProEyeRehearsalCoordinates.MappingVersion;
                stream=ViveDevelopmentTransport.OperatorStream(mapping,"controller-epoch");
                var op=new PeerIdentity("controller-session","op",PeerRole.Operator,stream,ControlSource.LiveOperator,OperatorClock.ClockId);
                var participant=new PeerIdentity("controller-session","participant",PeerRole.Participant,"participant-development",ControlSource.LiveOperator,ParticipantClock.ClockId);
                var pa=new IPEndPoint(IPAddress.Loopback,42001);var oa=new IPEndPoint(IPAddress.Loopback,42002);
                var ps=new ViveInMemoryDatagramSocket(pa);var os=new ViveInMemoryDatagramSocket(oa);ps.Other=os;os.Other=ps;
                var faults=new DevelopmentFaultConfiguration(.5,.5,.5);
                var clocks=new DevelopmentClockConfiguration(3,.2,.2,2,200);
                var pTransport=ViveDevelopmentTransport.CreateInMemoryFixture(ps,oa,ParticipantClock,participant,op,"vive-pro-eye-no-serial",mapping,"controller-epoch",faults,clocks,.2,()=>{Stops++;Task=TaskFor(Phase.Paused,0,0,Condition.A);});
                var oTransport=ViveDevelopmentTransport.CreateInMemoryFixture(os,pa,OperatorClock,op,participant,"vive-pro-eye-no-serial",mapping,"controller-epoch",faults,clocks,.2);
                Sensor=new Provider(OperatorClock);
                Participant=new ViveDevelopmentPeerController(pTransport,ParticipantClock,"controller-session",PeerRole.Participant,.1);
                Operator=new ViveDevelopmentPeerController(oTransport,OperatorClock,"controller-session",PeerRole.Operator,.1,task=>{
                    FactoryCalls++;if(FailFactory)throw new InvalidOperationException("private path");
                    return new ViveProEyeHeadEyeSource("controller-session",task.BlockIndex,task.Segment,WrongStream?"vive-pro-eye/operator/unexpected":stream,OperatorClock,Sensor,new ViveProEyeRehearsalCoordinates());});
                Task=TaskFor(Phase.MainTask,0,0,Condition.A);
            }
            public static TaskSnapshot TaskFor(Phase phase,int block,int segment,Condition condition)
            {return new TaskSnapshot("controller-session",block,segment,"trial-1",Colour.Red,Colour.Blue,phase,condition);}
            public void Step()
            {
                ParticipantClock.Now+=.025;OperatorClock.Now+=.025;
                Participant.TickParticipant(()=>Task,Live,Ready);Operator.TickOperator(SourceReady);Thread.Sleep(1);
            }
            public void Steps(int count){for(int i=0;i<count;i++)Step();}
            public void Warm(){Steps(75);}
            public void Dispose(){Participant.Dispose();Operator.Dispose();}
        }
        public static void Register(TestSuite suite)
        {
            suite.Run("VIVE peer controllers require explicit owner enable and relay only live selected task",()=>{
                using(var pair=new Pair())
                {
                    pair.Warm();Check.Equal(0,pair.Sensor.Reads);Check.True(pair.Participant.CurrentPose==null);
                    pair.Operator.SetOperatorEnabled(true);pair.Steps(15);
                    Check.True(pair.Sensor.Reads>0&&pair.Participant.CurrentPose!=null);
                    Check.True(!pair.Participant.CurrentPose.HardwareCaptureTimeVerified&&!pair.Participant.CurrentPose.ApparatusMappingValidated);
                    pair.Live=false;pair.Step();Check.True(pair.Participant.CurrentPose==null);
                    pair.Live=true;pair.Ready=false;pair.Step();Check.True(pair.Participant.CurrentPose==null);
                    pair.Ready=true;pair.Steps(4);Check.True(pair.Participant.CurrentPose!=null);
                }
            });
            suite.Run("VIVE peer controllers clear lost invalid disabled or hidden sources",()=>{
                using(var pair=new Pair())
                {
                    pair.Operator.SetOperatorEnabled(true);pair.Warm();Check.True(pair.Participant.CurrentPose!=null);
                    pair.Sensor.Invalid=true;pair.Operator.TickOperator(true);Check.True(pair.Operator.LastInvalidationSocketAccepted);
                    pair.ParticipantClock.Now+=.025;pair.OperatorClock.Now+=.025;pair.Participant.TickParticipant(()=>pair.Task,true,true);
                    Check.True(pair.Participant.CurrentPose==null);
                    pair.Sensor.Invalid=false;pair.Steps(8);Check.True(pair.Participant.CurrentPose!=null);
                    pair.Sensor.Available=false;pair.OperatorClock.Now+=.025;pair.ParticipantClock.Now+=.025;pair.Operator.TickOperator(true);
                    Check.True(pair.Operator.LastInvalidationSocketAccepted);
                    pair.ParticipantClock.Now+=.025;pair.OperatorClock.Now+=.025;pair.Participant.TickParticipant(()=>pair.Task,true,true);Check.True(pair.Participant.CurrentPose==null);
                    pair.Sensor.Available=true;pair.Steps(8);Check.True(pair.Participant.CurrentPose!=null);
                    pair.Sensor.Throw=true;pair.OperatorClock.Now+=.025;pair.ParticipantClock.Now+=.025;pair.Operator.TickOperator(true);
                    Check.True(pair.Operator.LastInvalidationSocketAccepted);
                    pair.ParticipantClock.Now+=.025;pair.OperatorClock.Now+=.025;pair.Participant.TickParticipant(()=>pair.Task,true,true);Check.True(pair.Participant.CurrentPose==null);
                    pair.Sensor.Throw=false;pair.Steps(8);Check.True(pair.Participant.CurrentPose!=null);
                    pair.Operator.SetOperatorEnabled(false);Check.True(pair.Operator.LastInvalidationSocketAccepted);
                    pair.ParticipantClock.Now+=.025;pair.OperatorClock.Now+=.025;pair.Participant.TickParticipant(()=>pair.Task,true,true);Check.True(pair.Participant.CurrentPose==null);
                    pair.Operator.SetOperatorEnabled(true);pair.Steps(8);Check.True(pair.Participant.CurrentPose!=null,"pose did not return after operator enabled: "+pair.Operator.LastIssue);
                    int readsBefore=pair.Sensor.Reads;
                    pair.SourceReady=false;pair.Operator.TickOperator(false);Check.Equal(readsBefore,pair.Sensor.Reads);
                    Check.True(pair.Operator.LastInvalidationSocketAccepted,"readiness loss did not send invalid state: "+pair.Operator.LastIssue);
                    pair.ParticipantClock.Now+=.025;pair.OperatorClock.Now+=.025;pair.Participant.TickParticipant(()=>pair.Task,true,true);Check.True(pair.Participant.CurrentPose==null,"readiness loss did not clear receiver");
                    pair.SourceReady=true;pair.Steps(8);Check.True(pair.Participant.CurrentPose!=null,"pose did not return after readiness: "+pair.Operator.LastIssue);
                    pair.Task=Pair.TaskFor(Phase.MainTask,0,0,Condition.D);pair.Step();Check.True(pair.Participant.CurrentPose==null);
                    pair.Task=null;pair.Step();Check.True(pair.Participant.CurrentPose==null);
                }
            });
            suite.Run("VIVE peer controllers acquire a new source for each active block context",()=>{
                using(var pair=new Pair())
                {
                    pair.Operator.SetOperatorEnabled(true);pair.Warm();Check.True(pair.Participant.CurrentPose!=null);
                    Check.Equal(1,pair.FactoryCalls);
                    pair.Task=Pair.TaskFor(Phase.MainTask,1,0,Condition.B);pair.Step();Check.True(pair.Participant.CurrentPose==null);
                    pair.Steps(10);Check.Equal(2,pair.FactoryCalls);Check.True(pair.Participant.CurrentPose!=null);
                    pair.Task=Pair.TaskFor(Phase.MainTask,1,1,Condition.B);pair.Step();Check.True(pair.Participant.CurrentPose==null);
                    pair.Steps(10);Check.Equal(3,pair.FactoryCalls);Check.True(pair.Participant.CurrentPose!=null);
                }
            });
            suite.Run("VIVE peer rejects a mismatched source and cannot resume after clock rollback",()=>{
                using(var pair=new Pair())
                {
                    pair.Operator.SetOperatorEnabled(true);pair.Warm();Check.True(pair.Participant.CurrentPose!=null);
                    pair.WrongStream=true;pair.Task=Pair.TaskFor(Phase.MainTask,1,0,Condition.A);pair.Steps(8);
                    Check.True(pair.Participant.CurrentPose==null);Check.True(pair.Operator.LastIssue.Contains("wrong_source_identity"));
                }
                using(var pair=new Pair())
                {
                    pair.Operator.SetOperatorEnabled(true);pair.Warm();Check.True(pair.Participant.CurrentPose!=null);
                    double prior=pair.OperatorClock.Now;pair.OperatorClock.Now-=1;pair.Operator.TickOperator(true);
                    Check.True(pair.Operator.Faulted);Check.True(!pair.Operator.LastInvalidationSocketAccepted);
                    pair.OperatorClock.Now=prior+2;pair.Operator.TickOperator(true);Check.True(pair.Operator.Faulted);
                    pair.ParticipantClock.Now+=1;pair.Participant.TickParticipant(()=>pair.Task,true,true);
                    Check.True(pair.Participant.CurrentPose==null);
                }
            });
            suite.Run("VIVE peer safety stop reads participant task after pause callback before showing pose",()=>{
                using(var pair=new Pair())
                {
                    pair.Operator.SetOperatorEnabled(true);pair.Warm();Check.True(pair.Participant.CurrentPose!=null);
                    Check.True(pair.Operator.RequestSafetyStop());
                    for(int i=0;i<12&&!pair.Operator.SafetyStopAcknowledged;i++)pair.Step();
                    Check.Equal(1,pair.Stops);Check.True(pair.Operator.SafetyStopAcknowledged);
                    Check.Equal(Phase.Paused,pair.Task.Phase);Check.True(pair.Participant.CurrentPose==null);
                }
            });
            suite.Run("VIVE peer factory failures latch and wrong session never renders",()=>{
                using(var pair=new Pair())
                {
                    pair.Operator.SetOperatorEnabled(true);pair.Warm();Check.True(pair.Participant.CurrentPose!=null);
                    pair.FailFactory=true;pair.Task=Pair.TaskFor(Phase.MainTask,1,0,Condition.A);pair.Steps(8);
                    Check.True(pair.Operator.Faulted&&pair.Participant.CurrentPose==null);
                    Check.True(!pair.Operator.LastIssue.Contains("private path"));
                    Check.Throws<InvalidOperationException>(()=>pair.Operator.SetOperatorEnabled(true));
                    pair.Task=new TaskSnapshot("wrong-session",0,0,"trial-2",Colour.Red,Colour.Blue,Phase.MainTask,Condition.A);
                    pair.Step();Check.True(pair.Participant.CurrentPose==null);
                }
            });
        }
    }
}
