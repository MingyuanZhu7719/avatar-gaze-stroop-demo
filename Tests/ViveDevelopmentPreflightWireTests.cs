using System;
using System.Text;
using AvatarStroop.Core;
using AvatarStroop.Runtime;

namespace AvatarStroop.Tests
{
    public static class ViveDevelopmentPreflightWireTests
    {
        const string Session="wire-session",OperatorClock="operator-clock",ParticipantClock="participant-clock",Mapping="mapping-v1",Epoch="epoch-1";
        static readonly string Stream=ViveDevelopmentTransport.OperatorStream(Mapping,Epoch);
        static ViveDevelopmentPreflightChallenge Request(string challenge="challenge")
        {return new ViveDevelopmentPreflightChallenge(Session,Stream,OperatorClock,ParticipantClock,Mapping,Epoch,challenge,0,0,Phase.BlockReady,3);}
        static ViveDevelopmentPreflightProof Proof(string challenge="challenge",long sequence=1,long headSequence=1,int eyeSequence=Int32.MinValue)
        {return new ViveDevelopmentPreflightProof(Session,Stream,OperatorClock,Mapping,Epoch,challenge,0,0,Phase.BlockReady,sequence,headSequence,eyeSequence,3.6,3.65,3.67,3.68,true,true);}
        static string Text(byte[] bytes){return Encoding.ASCII.GetString(bytes);}
        static bool Decode(string value,out ViveDevelopmentPreflightChallenge challenge,out ViveDevelopmentPreflightProof proof)
        {string error;return ViveDevelopmentPreflightWire.TryDecode(Encoding.UTF8.GetBytes(value),out challenge,out proof,out error);}
        static string Change(byte[] source,int index,string value)
        {var f=Text(source).Split('|');f[index]=value;return String.Join("|",f);}
        static void Reject(string value)
        {ViveDevelopmentPreflightChallenge c;ViveDevelopmentPreflightProof p;Check.True(!Decode(value,out c,out p));Check.Equal(null,c);Check.Equal(null,p);}
        static ClockSynchronizer MappingClock()
        {
            var mapping=new ClockSynchronizer(ParticipantClock,OperatorClock,new DevelopmentClockConfiguration(3,.2,.2,2,200));
            var peer=new PeerIdentity(Session,"operator",PeerRole.Operator,Stream,ControlSource.LiveOperator,OperatorClock);
            for(int i=0;i<3;i++)
            {string id="clock-"+i;double t=i;Check.True(mapping.RegisterRequest(id,t));Check.True(mapping.ObserveReply(WirePacket.TimeReply(peer,i,t+.5,id,ParticipantClock,t,t+.5),t));}
            Check.True(mapping.Snapshot().Available);return mapping;
        }
        public static void Register(TestSuite suite)
        {
            suite.Run("VIVE preflight wire matches fixed challenge and proof byte fixtures",()=>{
                const string stream="dml2ZS1wcm8tZXllL29wZXJhdG9yL2Rldi12MS84MzRkMmNjZGY5YWVlYTQwOTc0NmYwNThiOWI4YjMwZjZmYWU5NWE1MjYyMmQxNWY3NjU2YmYzZTAxNmU3YmZi";
                string challenge="VDPF|1|C|d2lyZS1zZXNzaW9u|"+stream+"|b3BlcmF0b3ItY2xvY2s=|cGFydGljaXBhbnQtY2xvY2s=|bWFwcGluZy12MQ==|ZXBvY2gtMQ==|Y2hhbGxlbmdl|0|0|BlockReady|4008000000000000";
                string proof="VDPF|1|P|d2lyZS1zZXNzaW9u|"+stream+"|b3BlcmF0b3ItY2xvY2s=|bWFwcGluZy12MQ==|ZXBvY2gtMQ==|Y2hhbGxlbmdl|0|0|BlockReady|1|1|-2147483648|400ccccccccccccd|400d333333333333|400d5c28f5c28f5c|400d70a3d70a3d71|1|1";
                Check.Equal(challenge,Text(ViveDevelopmentPreflightWire.EncodeChallenge(Request())));
                Check.Equal(proof,Text(ViveDevelopmentPreflightWire.EncodeProof(Proof())));
                ViveDevelopmentPreflightChallenge c;ViveDevelopmentPreflightProof p;
                Check.True(Decode(challenge,out c,out p));Check.Equal("challenge",c.Challenge);Check.Equal(3d,c.IssuedLocalSeconds);
                Check.True(Decode(proof,out c,out p));Check.Equal(Int32.MinValue,p.NativeEyeFrameSequence);Check.Equal(3.67,p.EyeObservedSeconds);
            });
            suite.Run("VIVE preflight wire challenge roundtrips all bound context without admitting countdown",()=>{
                var original=Request("挑战");ViveDevelopmentPreflightChallenge c;ViveDevelopmentPreflightProof p;string error;
                Check.True(ViveDevelopmentPreflightWire.TryDecode(ViveDevelopmentPreflightWire.EncodeChallenge(original),out c,out p,out error));
                Check.Equal(null,p);Check.Equal(null,error);Check.Equal(original.SessionId,c.SessionId);Check.Equal(original.OperatorStreamId,c.OperatorStreamId);
                Check.Equal(original.OperatorClockId,c.OperatorClockId);Check.Equal(original.ParticipantClockId,c.ParticipantClockId);
                Check.Equal(original.MappingVersion,c.MappingVersion);Check.Equal(original.Epoch,c.Epoch);Check.Equal(original.Challenge,c.Challenge);
                Check.Equal(original.Block,c.Block);Check.Equal(original.Segment,c.Segment);Check.Equal(original.Phase,c.Phase);
                Check.Equal(BitConverter.DoubleToInt64Bits(original.IssuedLocalSeconds),BitConverter.DoubleToInt64Bits(c.IssuedLocalSeconds));
                Check.True(!new ViveDevelopmentPreflight(Session,Stream,OperatorClock,ParticipantClock,Mapping,Epoch,.5).CanEnterCountdown);
            });
            suite.Run("VIVE preflight wire preserves proof context signed eye sequence flags and exact timestamps",()=>{
                var original=Proof(eyeSequence:Int32.MinValue);ViveDevelopmentPreflightChallenge c;ViveDevelopmentPreflightProof p;string error;
                Check.True(ViveDevelopmentPreflightWire.TryDecode(ViveDevelopmentPreflightWire.EncodeProof(original),out c,out p,out error));
                Check.Equal(null,c);Check.Equal(null,error);Check.Equal(original.SessionId,p.SessionId);Check.Equal(original.OperatorStreamId,p.OperatorStreamId);
                Check.Equal(original.OperatorClockId,p.OperatorClockId);Check.Equal(original.MappingVersion,p.MappingVersion);Check.Equal(original.Epoch,p.Epoch);
                Check.Equal(original.Challenge,p.Challenge);Check.Equal(original.Block,p.Block);Check.Equal(original.Segment,p.Segment);Check.Equal(original.Phase,p.Phase);
                Check.Equal(original.Sequence,p.Sequence);Check.Equal(original.HeadObservationSequence,p.HeadObservationSequence);Check.Equal(Int32.MinValue,p.NativeEyeFrameSequence);
                Check.Equal(BitConverter.DoubleToInt64Bits(original.OperatorChallengeReceivedSeconds),BitConverter.DoubleToInt64Bits(p.OperatorChallengeReceivedSeconds));
                Check.Equal(BitConverter.DoubleToInt64Bits(original.HeadObservedSeconds),BitConverter.DoubleToInt64Bits(p.HeadObservedSeconds));
                Check.Equal(BitConverter.DoubleToInt64Bits(original.EyeObservedSeconds),BitConverter.DoubleToInt64Bits(p.EyeObservedSeconds));
                Check.Equal(BitConverter.DoubleToInt64Bits(original.OperatorSendMethodSeconds),BitConverter.DoubleToInt64Bits(p.OperatorSendMethodSeconds));
                Check.True(p.HeadUsable&&p.EyeUsable&&!p.HardwareCaptureTimeVerified);
            });
            suite.Run("VIVE preflight wire refuses malformed noncanonical oversized and wrong protocol packets",()=>{
                var bytes=ViveDevelopmentPreflightWire.EncodeProof(Proof());var f=Text(bytes).Split('|');
                Reject("");Reject(new string('x',ViveDevelopmentPreflightWire.MaxPacketBytes+1));Reject(Text(bytes)+"|");
                Reject(Change(bytes,0,"ASW"));Reject(Change(bytes,1,"2"));Reject(Change(bytes,2,"X"));
                Reject(Change(bytes,3,"!!!!"));Reject(Change(bytes,3,Convert.ToBase64String(new byte[]{0xff})));
                Reject(Change(bytes,3,new string('A',176)));Reject(Change(bytes,9,"00"));Reject(Change(bytes,10,"-1"));
                Reject(Change(bytes,11,"MainTask"));Reject(Change(bytes,12,"+1"));Reject(Change(bytes,13,"-1"));
                Reject(Change(bytes,14,"2147483648"));Reject(Change(bytes,15,"7ff0000000000000"));
                Reject(Change(bytes,16,"ABCDEF0000000000"));Reject(Change(bytes,17,"0000"));Reject(Change(bytes,19,"true"));
                Reject(Text(bytes)+"\n");Reject(String.Join("|",f,0,f.Length-1));
                ViveDevelopmentPreflightChallenge c;ViveDevelopmentPreflightProof p;string error;
                Check.True(!ViveDevelopmentPreflightWire.TryDecode(null,out c,out p,out error));Check.Equal("packet_size",error);
            });
            suite.Run("VIVE preflight wire challenge rejects malformed lengths identities and noncanonical base64",()=>{
                var bytes=ViveDevelopmentPreflightWire.EncodeChallenge(Request());var f=Text(bytes).Split('|');
                Reject(String.Join("|",f,0,f.Length-1));Reject(Text(bytes)+"|extra");
                Reject(Change(bytes,3,"d2lyZS1zZXNzaW9u="));Reject(Change(bytes,6,"YQ"));
                Reject(Change(bytes,12,"Preparation"));Reject(Change(bytes,13,"7ff8000000000000"));
                string max=new string('a',128);var request=Request(max);ViveDevelopmentPreflightChallenge c;ViveDevelopmentPreflightProof p;string error;
                Check.True(ViveDevelopmentPreflightWire.TryDecode(ViveDevelopmentPreflightWire.EncodeChallenge(request),out c,out p,out error));Check.Equal(max,c.Challenge);
                Check.Throws<ArgumentException>(()=>Request(new string('a',129)));
                string utf8Max=new string('\u00e9',64);Check.True(ViveDevelopmentPreflightWire.TryDecode(ViveDevelopmentPreflightWire.EncodeChallenge(Request(utf8Max)),out c,out p,out error));
                Check.Throws<ArgumentException>(()=>Request(new string('\u00e9',65)));
            });
            suite.Run("VIVE preflight wire keeps phase source clock and chronology guards",()=>{
                Check.Throws<ArgumentException>(()=>new ViveDevelopmentPreflightChallenge(Session,Stream,OperatorClock,OperatorClock,Mapping,Epoch,"x",0,0,Phase.BlockReady,1));
                Check.Throws<ArgumentException>(()=>new ViveDevelopmentPreflightChallenge(Session,"wrong",OperatorClock,ParticipantClock,Mapping,Epoch,"x",0,0,Phase.BlockReady,1));
                Check.Throws<ArgumentException>(()=>new ViveDevelopmentPreflightChallenge(Session,Stream,OperatorClock,ParticipantClock,Mapping,Epoch,"x",0,0,Phase.MainTask,1));
                var bytes=ViveDevelopmentPreflightWire.EncodeProof(Proof());Reject(Change(bytes,16,Text(bytes).Split('|')[15]));
            });
            suite.Run("VIVE preflight wire decoded proof remains subject to endpoint freshness and replay gate",()=>{
                var gate=new ViveDevelopmentPreflight(Session,Stream,OperatorClock,ParticipantClock,Mapping,Epoch,.5);var clock=MappingClock();
                var challenge=gate.BeginContext(0,0,Phase.BlockReady,3);var original=Proof(challenge,10,10,10);
                ViveDevelopmentPreflightChallenge c;ViveDevelopmentPreflightProof p;string error;
                Check.True(ViveDevelopmentPreflightWire.TryDecode(ViveDevelopmentPreflightWire.EncodeProof(original),out c,out p,out error));
                Check.True(!gate.Observe(p,false,3.2,clock));Check.True(gate.Observe(p,true,3.2,clock),gate.LastRejection);
                Check.True(!gate.Observe(p,true,3.21,clock));Check.True(gate.CandidateReady(3.22,clock));
                var old=Proof(challenge,9,9,9);Check.True(ViveDevelopmentPreflightWire.TryDecode(ViveDevelopmentPreflightWire.EncodeProof(old),out c,out p,out error));
                Check.True(!gate.Observe(p,true,3.23,clock));Check.True(gate.CandidateReady(3.24,clock));
                Check.True(!gate.CandidateReady(4,clock));Check.True(gate.RecoveryDecisionRequired);Check.True(!gate.CanEnterCountdown);
            });
        }
    }
}
