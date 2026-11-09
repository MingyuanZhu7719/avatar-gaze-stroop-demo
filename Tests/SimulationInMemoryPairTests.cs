using System;
using System.Net;
using System.Threading;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    // Exercises the production simulation state machines with the sealed
    // in-process datagram fixture; it never binds or sends on a real socket.
    public static class SimulationInMemoryPairTests
    {
        static void Pump(ParticipantSimulationApplication participant,OperatorSimulationApplication op,Func<bool> done,string stage)
        {
            double deadline=participant.Clock.Now+4;
            while(!done()&&participant.Clock.Now<deadline){op.Tick();participant.Tick();Thread.Sleep(2);}
            Check.True(done(),"In-memory pair did not reach "+stage);
        }
        public static void Register(TestSuite suite)
        {
            suite.Run("in-memory paired simulation reaches source readiness and resume context",()=>{
                var a=new IPEndPoint(IPAddress.Loopback,41001);var b=new IPEndPoint(IPAddress.Loopback,41002);
                var left=new ViveInMemoryDatagramSocket(a);var right=new ViveInMemoryDatagramSocket(b);left.Other=right;right.Other=left;
                var pPeer=new UdpPeer(a,b,false,left);var oPeer=new UdpPeer(b,a,false,right);
                using(var p=new ParticipantSimulationApplication("memory-pair",a.Port,b.Port,null,pPeer,false))
                using(var o=new OperatorSimulationApplication("memory-pair",b.Port,a.Port,false,false,oPeer))
                {
                    var e=p.Experiment;p.OwnerBeginPractice();
                    for(int i=0;i<8;i++){e.MarkPresented(e.CurrentTrial.TrialId);var colour=e.CurrentTrial.Stimulus.Ink;e.ButtonChanged(colour,true);e.ButtonChanged(colour,false);}
                    p.OwnerLockNextBlock(ControlSelection.SimulatorRehearsal);
                    Pump(p,o,()=>p.SourceReady(ControlSelection.SimulatorRehearsal),"initial source readiness");
                    p.OwnerBeginBlock();Pump(p,o,()=>e.Phase==Phase.MainTask,"first main block");
                    e.MarkPresented(e.CurrentTrial.TrialId);e.Pause(Actor.Participant);p.RefreshPresentation();
                    Check.True(!p.CurrentAvatar.AvatarVisible);e.Resume(true);p.RefreshPresentation();
                    Check.True(!p.SourceReady(ControlSelection.SimulatorRehearsal));
                    Pump(p,o,()=>e.Phase==Phase.MainTask,"resumed main block");
                    Check.Equal(1,e.CurrentTrial.PlannedIndex);Check.Equal(1,p.CurrentAvatar.InputSample.Segment);
                    Check.True(p.CurrentAvatar.HeadValid);
                }
            });
        }
    }
}
