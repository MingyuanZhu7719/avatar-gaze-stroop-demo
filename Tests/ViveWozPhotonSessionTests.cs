using System;
using System.Linq;
using System.Reflection;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    public static class ViveWozPhotonSessionTests
    {
        public static void Register(TestSuite suite)
        {
            suite.Run("Photon timing separates roundtrip proof lease from received pose age",()=>{
                var policy=ViveWozTimingPolicy.Photon;
                Check.Equal(.75,policy.NetworkTaskLease);Check.Equal(.75,policy.ControlProofLease);
                Check.True(policy.EvidenceFresh(false,.49,0,.24,10.04,10,10));
                Check.True(!policy.EvidenceFresh(true,.49,0,.24,10.04,10,10),"old received pose must expire at150ms");
                Check.True(policy.EvidenceFresh(true,.49,0,.40,10.04,10,10));
                Check.True(policy.EvidenceFresh(false,.70,0,.24,10.04,10,10));
                Check.True(!policy.EvidenceFresh(false,.751,0,.74,10.04,10,10),"original task must expire at750ms");
                Check.True(!policy.EvidenceFresh(true,.751,0,.74,10.04,10,10));
                Check.True(!policy.EvidenceFresh(true,.30,0,.29,10.101,10,10),"capture must remain100ms");
                Check.True(!policy.EvidenceFresh(false,.30,0,.29,10.101,10,10));
                Check.True(policy.OperatorProofFresh(.49,0,.01,.01));
                Check.True(!policy.OperatorProofFresh(.751,0,.74,.74),"operator cannot extend original task lease with a new sample");
                Check.Near(.45,policy.AvatarExpiry(0,.30,0));
                Check.Near(.75,policy.AvatarExpiry(0,.74,0));
            });
            suite.Run("LAN retains original150ms display and100ms operator proof limits",()=>{
                var policy=ViveWozTimingPolicy.Lan;
                Check.Equal(.15,policy.NetworkTaskLease);Check.Equal(.15,policy.ReceivedPoseAge);Check.Equal(.10,policy.LocalCaptureAge);
                Check.True(policy.EvidenceFresh(false,.149,0,.14,10.04,10,10));
                Check.True(!policy.EvidenceFresh(false,.151,0,.15,10.04,10,10));
                Check.True(!policy.OperatorProofFresh(.101,0,0,0));
                Check.Near(.15,policy.AvatarExpiry(0,.14,0));
                Check.Equal(0,typeof(ViveWozTimingPolicy).GetConstructors().Length);
            });
            suite.Run("Photon forces WSS and suppresses vendor payload logging",()=>{
                var settings=ViveWozPhotonSession.Settings("00000000-0000-0000-0000-000000000001","asia");
                Check.Equal(Photon.Client.ConnectionProtocol.WebSocketSecure,settings.Protocol);
                Check.True(!settings.EnableProtocolFallback);
                Check.Equal(Photon.Client.LogLevel.Off,settings.ClientLogging);
                Check.Equal(Photon.Client.LogLevel.Off,settings.NetworkLogging);
            });
            suite.Run("Photon inbox pins one actor and refuses replacements",()=>{
                var inbox=new ViveWozPhotonInbox();inbox.Pin(17);inbox.Pin(17);
                Check.Throws<InvalidOperationException>(()=>inbox.Pin(18));
                string reason;Check.True(!inbox.Enqueue(18,new byte[12],5,out reason));Check.Equal("foreign_peer",reason);Check.Equal(0,inbox.Count);
            });
            suite.Run("Photon inbox preserves original callback time and owns bytes",()=>{
                var inbox=new ViveWozPhotonInbox();inbox.Pin(1);var packet=new byte[12];packet[2]=91;string reason;
                Check.True(inbox.Enqueue(1,packet,15.75,out reason));packet[2]=7;
                var target=new byte[4097];double receipt;Check.Equal(12,inbox.Receive(target,out receipt));
                Check.Equal(15.75,receipt);Check.Equal((byte)91,target[2]);Check.Equal(0,inbox.Count);
            });
            suite.Run("Photon inbox stops unbounded backlog without replacing old receipts",()=>{
                var inbox=new ViveWozPhotonInbox();inbox.Pin(2);string reason;
                for(int i=0;i<ViveWozPhotonInbox.Capacity;i++)Check.True(inbox.Enqueue(2,new byte[8],i,out reason));
                Check.True(!inbox.Enqueue(2,new byte[8],80,out reason));Check.Equal("packet_backlog",reason);
                Check.Equal(ViveWozPhotonInbox.Capacity,inbox.Count);double at;inbox.Receive(new byte[8],out at);Check.Equal(0.0,at);
                inbox.Clear();Check.Equal(0,inbox.Count);
            });
            suite.Run("Photon inbox rejects oversize null and regressing receipt timestamps",()=>{
                var inbox=new ViveWozPhotonInbox();inbox.Pin(3);string reason;
                Check.True(!inbox.Enqueue(3,null,0,out reason));Check.True(!inbox.Enqueue(3,new byte[4097],0,out reason));
                Check.True(!inbox.Enqueue(3,new byte[7],0,out reason));Check.True(!inbox.Enqueue(3,new byte[8],Double.NaN,out reason));
                Check.True(!inbox.Enqueue(3,new byte[8],-1,out reason));Check.True(inbox.Enqueue(3,new byte[8],7,out reason));
                Check.True(!inbox.Enqueue(3,new byte[8],6,out reason));Check.Equal("receipt_clock",reason);Check.Equal(1,inbox.Count);
            });
            suite.Run("Photon room entry normalizes readable code and rejects ambiguous input",()=>{
                Check.Equal("ABCD2345",ViveWozPhotonSession.NormalizeRoomCode(" abcd-2345 "));
                Check.Throws<ArgumentException>(()=>ViveWozPhotonSession.NormalizeRoomCode("ABCD1234"));
                Check.Throws<ArgumentException>(()=>ViveWozPhotonSession.NormalizeRoomCode("ABCDEFG"));
                Check.Throws<ArgumentException>(()=>ViveWozPhotonSession.NormalizeRoomCode(null));
            });
            suite.Run("Photon production receipt has no public constructor or callback injector",()=>{
                Check.Equal(0,typeof(ViveLegacyWozParticipantProcess.PhotonEvidence).GetConstructors().Length);
                Check.Equal(0,typeof(ViveWozPhotonSession).GetConstructors().Length);
                Check.True(!typeof(ViveWozPhotonSession).GetInterfaces().Any(t=>t.Name=="IOnEventCallback"));
                Check.True(typeof(ViveWozPhotonSession).GetMethod("OnEvent",BindingFlags.Public|BindingFlags.Instance)==null);
                Check.True(!ViveLegacyWozParticipantProcess.IsActualNetworkEvidence(null));
            });
        }
    }
}
