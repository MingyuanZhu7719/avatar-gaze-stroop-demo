using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    // All data and datagrams are in-memory fixtures. These tests do not
    // initialize an XR loader, native eye client, or application UI.
    public static class ViveDevelopmentAppAdapterTests
    {
        sealed class Clock : IClock
        {public double Now {get;set;} public string ClockId {get;private set;} public Clock(string id){ClockId=id;}}
        sealed class Provider : IViveProEyeProvider
        {
            readonly Clock clock;int sequence;
            public bool Available=true,Invalid;
            public int Reads;
            public Provider(Clock clock){this.clock=clock;}
            public ViveProEyePoll Poll()
            {
                Reads++;if(!Available)return null;
                int id=++sequence;double now=clock.Now;
                var ray=new ViveEyeRay(Invalid?0UL:2UL,false,!Invalid,new Vec3(0,0,0),new Vec3(.2,0,1));
                return new ViveProEyePoll(new ViveHeadFrame(id,now,true,true,Rotation.Identity),
                    new ViveEyeFrame(id,id*8,now,false,null,null,ray),ViveEyeRuntimeState.Working,ViveEyeCalibrationState.ReportedReady,true,0);
            }
        }
        sealed class Host : IViveDevelopmentParticipantHost
        {
            public TaskSnapshot Task;
            public bool LiveSourceSelected {get;set;}
            public bool PauseActuallyChangesTask=true,ThrowOnClear;
            public ViveDevelopmentRemotePose Rendered;
            public int Clears,Applied,OperatorStops,TransportLosses;
            public TaskSnapshot ReadAuthoritativeTask(){return Task;}
            public void ClearLivePose(){Clears++;if(ThrowOnClear)throw new InvalidOperationException("synthetic renderer fault");Rendered=null;}
            public void ApplyLivePose(ViveDevelopmentRemotePose pose){if(pose==null)throw new ArgumentNullException("pose");Applied++;Rendered=pose;}
            public void PauseByOperator(){OperatorStops++;if(PauseActuallyChangesTask)Task=TaskFor(Phase.Paused,Condition.A);}
            public void PauseForTransportLoss(){TransportLosses++;if(PauseActuallyChangesTask)Task=TaskFor(Phase.Paused,Condition.A);}
        }
        static TaskSnapshot TaskFor(Phase phase,Condition condition)
        {return new TaskSnapshot("app-adapter-session",0,0,"trial-1",Colour.Red,Colour.Blue,phase,condition);}
        static int nextPort=45000;static int Port(){return ++nextPort;}
        static void Rejected(Action action)
        {
            bool rejected=false;
            try{action();}catch(ArgumentException){rejected=true;}catch(InvalidOperationException){rejected=true;}
            Check.True(rejected,"Expected an explicit failure rather than silently proceeding");
        }
        sealed class Pair : IDisposable
        {
            public readonly Clock PClock=new Clock("app-participant-clock"),OClock=new Clock("app-operator-clock");
            public readonly Host ParticipantHost=new Host {Task=TaskFor(Phase.MainTask,Condition.A)};
            public readonly Provider Sensor;
            public readonly ViveDevelopmentAppAdapter Participant,Operator;
            public readonly ViveDevelopmentPeerConfiguration ParticipantConfig,OperatorConfig;
            public readonly string Stream,Mapping;
            public int SourceCreations;
            public Pair()
            {
                Mapping=ViveProEyeRehearsalCoordinates.MappingVersion;Stream=ViveDevelopmentTransport.OperatorStream(Mapping,"app-epoch");
                var participant=new PeerIdentity("app-adapter-session","participant",PeerRole.Participant,"app-participant",ControlSource.LiveOperator,PClock.ClockId);
                var op=new PeerIdentity("app-adapter-session","operator",PeerRole.Operator,Stream,ControlSource.LiveOperator,OClock.ClockId);
                int first=Port(),second=Port();while(first==second)second=Port();
                var firstEndpoint=new IPEndPoint(IPAddress.Loopback,first);var secondEndpoint=new IPEndPoint(IPAddress.Loopback,second);
                var faults=new DevelopmentFaultConfiguration(.5,.5,.5);var clocks=new DevelopmentClockConfiguration(3,.2,.2,2,200);
                ParticipantConfig=new ViveDevelopmentPeerConfiguration(PeerRole.Participant,participant,op,firstEndpoint,secondEndpoint,false,
                    "vive-pro-eye-no-serial",Mapping,"app-epoch",faults,clocks,.2,.1);
                OperatorConfig=new ViveDevelopmentPeerConfiguration(PeerRole.Operator,op,participant,secondEndpoint,firstEndpoint,false,
                    "vive-pro-eye-no-serial",Mapping,"app-epoch",faults,clocks,.2,.1);
                Sensor=new Provider(OClock);
                Participant=new ViveDevelopmentAppAdapter(ParticipantConfig,PClock,ParticipantHost,null);
                Operator=new ViveDevelopmentAppAdapter(OperatorConfig,OClock,null,task=>{
                    SourceCreations++;
                    return new ViveProEyeHeadEyeSource("app-adapter-session",task.BlockIndex,task.Segment,Stream,OClock,Sensor,new ViveProEyeRehearsalCoordinates());});
            }
            public void Start(){var ps=new ViveInMemoryDatagramSocket(ParticipantConfig.Local);var os=new ViveInMemoryDatagramSocket(OperatorConfig.Local);
                ps.Other=os;os.Other=ps;Participant.StartInMemoryFixture(ps);Operator.StartInMemoryFixture(os);}
            public void Confirm(){Participant.ConfirmLiveSource(true);Operator.ConfirmLiveSource(true);ParticipantHost.LiveSourceSelected=true;}
            public void Step()
            {
                PClock.Now+=.025;OClock.Now+=.025;
                Participant.TickParticipant(true);Operator.TickOperator(true);Thread.Sleep(1);
            }
            public void Steps(int count){for(int i=0;i<count;i++)Step();}
            public void Dispose(){Participant.Dispose();Operator.Dispose();}
        }
        public static void Register(TestSuite suite)
        {
            suite.Run("VIVE app adapter remains dormant and requires explicit owner source confirmation",()=>{
                using(var pair=new Pair())
                {
                    Check.True(!pair.Participant.IsOpen&&!pair.Operator.IsOpen);
                    Check.True(!pair.Participant.HasExplicitConfirmation&&!pair.Operator.HasExplicitConfirmation);
                    Check.True(!pair.Participant.CanRecordOrExport&&!pair.Participant.HardwareCaptureTimeVerified);
                    Rejected(()=>pair.Participant.StartNetwork(false,false));Check.True(!pair.Participant.IsOpen);
                    Rejected(()=>pair.Participant.StartNetwork(true,true));Rejected(()=>pair.Operator.StartNetwork(true,true));
                    Check.True(!pair.Participant.IsOpen&&!pair.Operator.IsOpen&&pair.Sensor.Reads==0);
                    Rejected(()=>pair.Operator.ConfirmLiveSource(true));
                    pair.Start();pair.ParticipantHost.LiveSourceSelected=true;
                    pair.Steps(70);Check.Equal(0,pair.Sensor.Reads);Check.True(pair.ParticipantHost.Rendered==null);
                    Rejected(()=>pair.Operator.ConfirmLiveSource(false));
                    Rejected(()=>pair.Participant.ConfirmLiveSource(false));
                    Check.True(!pair.Participant.HasExplicitConfirmation&&!pair.Operator.HasExplicitConfirmation);
                    pair.Confirm();pair.Steps(20);
                    Check.True(pair.Sensor.Reads>0&&pair.ParticipantHost.Rendered!=null);
                    Check.True(!pair.ParticipantHost.Rendered.HardwareCaptureTimeVerified&&!pair.ParticipantHost.Rendered.ApparatusMappingValidated);
                }
            });
            suite.Run("VIVE app adapter requires explicit trust before creating a remote socket",()=>{
                using(var pair=new Pair())
                {
                    var remote=new ViveDevelopmentPeerConfiguration(PeerRole.Participant,
                        new PeerIdentity("remote-session","p",PeerRole.Participant,"p-stream",ControlSource.LiveOperator,pair.PClock.ClockId),
                        new PeerIdentity("remote-session","o",PeerRole.Operator,pair.Stream,ControlSource.LiveOperator,pair.OClock.ClockId),
                        new IPEndPoint(IPAddress.Loopback,Port()),new IPEndPoint(IPAddress.Parse("192.0.2.1"),12000),true,
                        "vive-pro-eye-no-serial",pair.Mapping,"app-epoch",new DevelopmentFaultConfiguration(.5,.5,.5),
                        new DevelopmentClockConfiguration(3,.2,.2,2,200),.2,.1);
                    using(var adapter=new ViveDevelopmentAppAdapter(remote,pair.PClock,pair.ParticipantHost,null))
                    {Rejected(()=>adapter.StartNetwork(true,false));Check.True(!adapter.IsOpen);}
                }
            });
            suite.Run("VIVE app adapter rejects unsafe endpoints and mismatched roles without opening sockets",()=>{
                using(var pair=new Pair())
                {
                    var op=pair.OperatorConfig.Own;var participant=pair.ParticipantConfig.Own;
                    var good=new IPEndPoint(IPAddress.Loopback,Port());
                    var faults=new DevelopmentFaultConfiguration(.5,.5,.5);var clocks=new DevelopmentClockConfiguration(3,.2,.2,2,200);
                    Rejected(()=>new ViveDevelopmentPeerConfiguration(PeerRole.Participant,op,participant,good,new IPEndPoint(IPAddress.Loopback,Port()),false,
                        "vive-pro-eye-no-serial",pair.Mapping,"app-epoch",faults,clocks,.2,.1));
                    Rejected(()=>new ViveDevelopmentPeerConfiguration(PeerRole.Operator,op,participant,new IPEndPoint(IPAddress.Any,12001),good,true,
                        "vive-pro-eye-no-serial",pair.Mapping,"app-epoch",faults,clocks,.2,.1));
                    Rejected(()=>new ViveDevelopmentPeerConfiguration(PeerRole.Operator,op,participant,good,good,false,
                        "vive-pro-eye-no-serial",pair.Mapping,"app-epoch",faults,clocks,.2,.1));
                    Rejected(()=>new ViveDevelopmentAppAdapter(pair.ParticipantConfig,pair.OClock,pair.ParticipantHost,null));
                    Check.True(!pair.Participant.IsOpen&&!pair.Operator.IsOpen);
                }
            });
            suite.Run("VIVE app adapter clears local pose on source loss, hidden condition and unavailable task",()=>{
                using(var pair=new Pair())
                {
                    pair.Start();pair.Confirm();pair.Steps(75);Check.True(pair.ParticipantHost.Rendered!=null);
                    pair.Sensor.Available=false;pair.OClock.Now+=.025;pair.PClock.Now+=.025;pair.Operator.TickOperator(true);
                    pair.PClock.Now+=.025;pair.OClock.Now+=.025;pair.Participant.TickParticipant(true);
                    Check.True(pair.ParticipantHost.Rendered==null&&pair.Participant.CurrentPose==null);
                    pair.Sensor.Available=true;pair.Steps(10);Check.True(pair.ParticipantHost.Rendered!=null);
                    pair.Sensor.Invalid=true;pair.OClock.Now+=.025;pair.PClock.Now+=.025;pair.Operator.TickOperator(true);
                    pair.PClock.Now+=.025;pair.OClock.Now+=.025;pair.Participant.TickParticipant(true);
                    Check.True(pair.ParticipantHost.Rendered==null);
                    pair.Sensor.Invalid=false;pair.Steps(10);Check.True(pair.ParticipantHost.Rendered!=null);
                    pair.ParticipantHost.LiveSourceSelected=false;pair.Step();Check.True(pair.ParticipantHost.Rendered==null);
                    pair.ParticipantHost.LiveSourceSelected=true;pair.Steps(8);Check.True(pair.ParticipantHost.Rendered!=null);
                    pair.ParticipantHost.Task=TaskFor(Phase.MainTask,Condition.D);pair.Step();Check.True(pair.ParticipantHost.Rendered==null);
                    pair.ParticipantHost.Task=null;pair.Step();Check.True(pair.ParticipantHost.Rendered==null);
                }
            });
            suite.Run("VIVE app adapter acknowledges remote stop only after a real participant pause",()=>{
                using(var pair=new Pair())
                {
                    pair.Start();pair.Confirm();pair.Steps(75);Check.True(pair.ParticipantHost.Rendered!=null);
                    Check.True(pair.Operator.RequestRemotePause());pair.Steps(12);
                    Check.Equal(1,pair.ParticipantHost.OperatorStops);Check.Equal(Phase.Paused,pair.ParticipantHost.Task.Phase);
                    Check.True(pair.Operator.SafetyStopAcknowledged&&!pair.Operator.SafetyStopPending);
                    Check.True(pair.ParticipantHost.Rendered==null);
                }
                using(var pair=new Pair())
                {
                    pair.Start();pair.Confirm();pair.Steps(75);pair.ParticipantHost.PauseActuallyChangesTask=false;
                    Check.True(pair.Operator.RequestRemotePause());pair.Steps(12);
                    Check.True(pair.Participant.Faulted&&pair.ParticipantHost.Rendered==null);
                    Check.True(pair.ParticipantHost.OperatorStops>0);
                    Check.True(pair.Operator.SafetyStopPending&&!pair.Operator.SafetyStopAcknowledged);
                }
            });
            suite.Run("VIVE app adapter latches communication loss and cannot automatically resume",()=>{
                using(var pair=new Pair())
                {
                    pair.Start();pair.Confirm();pair.Steps(75);Check.True(pair.ParticipantHost.Rendered!=null);
                    pair.PClock.Now+=1;pair.Participant.TickParticipant(true);
                    Check.True(pair.Participant.RecoveryDecisionRequired&&pair.ParticipantHost.Rendered==null);
                    Check.Equal(1,pair.ParticipantHost.TransportLosses);Check.Equal(Phase.Paused,pair.ParticipantHost.Task.Phase);
                    pair.OClock.Now+=1;pair.Steps(30);
                    Check.True(pair.Participant.RecoveryDecisionRequired&&pair.ParticipantHost.Rendered==null);
                    Check.Equal(1,pair.ParticipantHost.TransportLosses);
                    Rejected(()=>pair.Participant.ConfirmLiveSource(true));
                }
            });
            suite.Run("VIVE app adapter does not pause an automatic-source task on peer loss",()=>{
                using(var pair=new Pair())
                {
                    pair.Start();pair.Confirm();pair.Steps(75);Check.True(pair.ParticipantHost.Rendered!=null);
                    pair.ParticipantHost.LiveSourceSelected=false;
                    pair.PClock.Now+=1;pair.Participant.TickParticipant(true);
                    Check.True(pair.Participant.RecoveryDecisionRequired&&pair.ParticipantHost.Rendered==null);
                    Check.Equal(0,pair.ParticipantHost.TransportLosses);Check.Equal(Phase.MainTask,pair.ParticipantHost.Task.Phase);
                }
            });
            suite.Run("VIVE app adapter pauses a live task on controller clock failure",()=>{
                using(var pair=new Pair())
                {
                    pair.Start();pair.Confirm();pair.Steps(75);Check.True(pair.ParticipantHost.Rendered!=null);
                    double prior=pair.PClock.Now;pair.PClock.Now-=1;pair.Participant.TickParticipant(true);
                    Check.True(pair.Participant.Faulted&&pair.ParticipantHost.Rendered==null);
                    Check.Equal(1,pair.ParticipantHost.TransportLosses);Check.Equal(Phase.Paused,pair.ParticipantHost.Task.Phase);
                    pair.PClock.Now=prior+2;pair.Participant.TickParticipant(true);
                    Check.True(pair.Participant.Faulted&&pair.ParticipantHost.Rendered==null);
                    Check.Equal(1,pair.ParticipantHost.TransportLosses);
                }
            });
            suite.Run("VIVE app adapter reports an ineffective fault pause without pretending it succeeded",()=>{
                using(var pair=new Pair())
                {
                    pair.Start();pair.Confirm();pair.Steps(75);Check.True(pair.ParticipantHost.Rendered!=null);
                    pair.ParticipantHost.PauseActuallyChangesTask=false;pair.PClock.Now-=1;pair.Participant.TickParticipant(true);
                    Check.True(pair.Participant.Faulted&&pair.ParticipantHost.Rendered==null);
                    Check.Equal(1,pair.ParticipantHost.TransportLosses);Check.Equal(Phase.MainTask,pair.ParticipantHost.Task.Phase);
                    Check.True(pair.Participant.LastIssue.Contains("fault_pause_failed"));
                }
            });
            suite.Run("VIVE app adapter attempts to pause live task even if renderer clearing fails",()=>{
                using(var pair=new Pair())
                {
                    pair.Start();pair.Confirm();pair.Steps(75);Check.True(pair.ParticipantHost.Rendered!=null);
                    pair.ParticipantHost.ThrowOnClear=true;pair.Participant.TickParticipant(true);
                    Check.True(pair.Participant.Faulted);
                    Check.Equal(1,pair.ParticipantHost.TransportLosses);Check.Equal(Phase.Paused,pair.ParticipantHost.Task.Phase);
                    Check.True(pair.Participant.LastIssue.Contains("clear_failed"));
                    pair.ParticipantHost.ThrowOnClear=false;
                }
            });
        }
    }
}
