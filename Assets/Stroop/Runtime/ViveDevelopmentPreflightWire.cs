using System;
using System.Globalization;
using System.Text;
using AvatarStroop.Core;

namespace AvatarStroop.Runtime
{
    // Development-only challenge. Identities describe intended peers; they do
    // not authenticate an endpoint and cannot establish headset readiness.
    public sealed class ViveDevelopmentPreflightChallenge
    {
        public string SessionId {get;private set;}
        public string OperatorStreamId {get;private set;}
        public string OperatorClockId {get;private set;}
        public string ParticipantClockId {get;private set;}
        public string MappingVersion {get;private set;}
        public string Epoch {get;private set;}
        public string Challenge {get;private set;}
        public int Block {get;private set;}
        public int Segment {get;private set;}
        public Phase Phase {get;private set;}
        public double IssuedLocalSeconds {get;private set;}
        public ViveDevelopmentPreflightChallenge(string sessionId,string operatorStreamId,string operatorClockId,string participantClockId,
            string mappingVersion,string epoch,string challenge,int block,int segment,Phase phase,double issuedLocalSeconds)
        {
            WireCodec.RequireIdentity(sessionId);WireCodec.RequireIdentity(operatorStreamId);WireCodec.RequireIdentity(operatorClockId);
            WireCodec.RequireIdentity(participantClockId);WireCodec.RequireIdentity(mappingVersion);WireCodec.RequireIdentity(epoch);WireCodec.RequireIdentity(challenge);
            if(operatorClockId==participantClockId||operatorStreamId!=ViveDevelopmentTransport.OperatorStream(mappingVersion,epoch)||block<0||block>3||segment<0||
                (phase!=Phase.BlockReady&&phase!=Phase.Countdown)||!WireCodec.Finite(issuedLocalSeconds)||issuedLocalSeconds<0)
                throw new ArgumentException("Invalid development preflight challenge context");
            SessionId=sessionId;OperatorStreamId=operatorStreamId;OperatorClockId=operatorClockId;ParticipantClockId=participantClockId;
            MappingVersion=mappingVersion;Epoch=epoch;Challenge=challenge;Block=block;Segment=segment;Phase=phase;IssuedLocalSeconds=issuedLocalSeconds;
        }
    }

    // Strictly framed ASCII codec, distinct from wire v2. Parsing does not
    // authenticate, verify a challenge, check age or accept/replay a proof.
    // The existing endpoint check and ViveDevelopmentPreflight.Observe must run
    // after decode. No network is opened and no readiness gate is changed here.
    public static class ViveDevelopmentPreflightWire
    {
        public const string Version="1";
        public const int MaxPacketBytes=2048;
        static readonly UTF8Encoding StrictUtf8=new UTF8Encoding(false,true);
        static readonly CultureInfo Inv=CultureInfo.InvariantCulture;

        public static byte[] EncodeChallenge(ViveDevelopmentPreflightChallenge c)
        {
            if(c==null)throw new ArgumentNullException("c");
            return Frame("C",Id(c.SessionId),Id(c.OperatorStreamId),Id(c.OperatorClockId),Id(c.ParticipantClockId),Id(c.MappingVersion),
                Id(c.Epoch),Id(c.Challenge),N(c.Block),N(c.Segment),c.Phase.ToString(),D(c.IssuedLocalSeconds));
        }
        public static byte[] EncodeProof(ViveDevelopmentPreflightProof p)
        {
            if(p==null)throw new ArgumentNullException("p");
            return Frame("P",Id(p.SessionId),Id(p.OperatorStreamId),Id(p.OperatorClockId),Id(p.MappingVersion),Id(p.Epoch),Id(p.Challenge),
                N(p.Block),N(p.Segment),p.Phase.ToString(),N(p.Sequence),N(p.HeadObservationSequence),N(p.NativeEyeFrameSequence),
                D(p.OperatorChallengeReceivedSeconds),D(p.HeadObservedSeconds),D(p.EyeObservedSeconds),D(p.OperatorSendMethodSeconds),
                p.HeadUsable?"1":"0",p.EyeUsable?"1":"0");
        }
        static byte[] Frame(params string[] values)
        {
            string value="VDPF|"+Version+"|"+String.Join("|",values);byte[] bytes=Encoding.ASCII.GetBytes(value);
            if(bytes.Length>MaxPacketBytes)throw new ArgumentException("Preflight packet exceeds bound");return bytes;
        }
        public static bool TryDecode(byte[] bytes,out ViveDevelopmentPreflightChallenge challenge,out ViveDevelopmentPreflightProof proof,out string error)
        {
            challenge=null;proof=null;error=null;
            if(bytes==null||bytes.Length==0||bytes.Length>MaxPacketBytes){error="packet_size";return false;}
            foreach(byte b in bytes)if(b<33||b>126){error="non_ascii_or_whitespace";return false;}
            string[] f=Encoding.ASCII.GetString(bytes).Split('|');
            if(f.Length<3||f[0]!="VDPF"){error="envelope";return false;}
            if(f[1]!=Version){error="wire_version";return false;}
            try
            {
                if(f[2]=="C")
                {
                    if(f.Length!=14){error="field_count";return false;}
                    challenge=new ViveDevelopmentPreflightChallenge(ReadId(f[3]),ReadId(f[4]),ReadId(f[5]),ReadId(f[6]),ReadId(f[7]),
                        ReadId(f[8]),ReadId(f[9]),ReadInt(f[10]),ReadInt(f[11]),ReadPhase(f[12]),ReadDouble(f[13]));
                }
                else if(f[2]=="P")
                {
                    if(f.Length!=21){error="field_count";return false;}
                    proof=new ViveDevelopmentPreflightProof(ReadId(f[3]),ReadId(f[4]),ReadId(f[5]),ReadId(f[6]),ReadId(f[7]),ReadId(f[8]),
                        ReadInt(f[9]),ReadInt(f[10]),ReadPhase(f[11]),ReadLong(f[12]),ReadLong(f[13]),ReadInt(f[14]),
                        ReadDouble(f[15]),ReadDouble(f[16]),ReadDouble(f[17]),ReadDouble(f[18]),ReadBool(f[19]),ReadBool(f[20]));
                }
                else {error="message_kind";return false;}
                return true;
            }
            catch(Exception e) when(e is ArgumentException||e is FormatException||e is OverflowException)
            {challenge=null;proof=null;error="invalid_payload:"+e.GetType().Name;return false;}
        }
        static string Id(string value){WireCodec.RequireIdentity(value);return Convert.ToBase64String(StrictUtf8.GetBytes(value));}
        static string ReadId(string value)
        {
            if(value.Length>172)throw new FormatException("Identity token too long");
            string decoded=StrictUtf8.GetString(Convert.FromBase64String(value));WireCodec.RequireIdentity(decoded);
            if(Id(decoded)!=value)throw new FormatException("Noncanonical identity");return decoded;
        }
        static string N(long value){return value.ToString(Inv);}
        static long ReadLong(string token)
        {
            long value;if(token.Length==0||token.Length>20||!Int64.TryParse(token,NumberStyles.AllowLeadingSign,Inv,out value)||N(value)!=token)
                throw new FormatException("Noncanonical integer");return value;
        }
        static int ReadInt(string token){return checked((int)ReadLong(token));}
        // Exact IEEE-754 binary payload avoids decimal rounding and makes exact
        // challenge receipt equality reliable across repeated proof messages.
        static string D(double value)
        {if(!WireCodec.Finite(value))throw new ArgumentException("Finite time required");return unchecked((ulong)BitConverter.DoubleToInt64Bits(value)).ToString("x16",Inv);}
        static double ReadDouble(string token)
        {
            if(token.Length!=16)throw new FormatException("Invalid time token");
            foreach(char c in token)if(!((c>='0'&&c<='9')||(c>='a'&&c<='f')))throw new FormatException("Noncanonical time token");
            ulong bits;if(!UInt64.TryParse(token,NumberStyles.AllowHexSpecifier,Inv,out bits))throw new FormatException("Invalid time bits");
            double value=BitConverter.Int64BitsToDouble(unchecked((long)bits));if(!WireCodec.Finite(value))throw new FormatException("Nonfinite time");return value;
        }
        static bool ReadBool(string token){if(token=="1")return true;if(token=="0")return false;throw new FormatException("Invalid flag");}
        static Phase ReadPhase(string token)
        {if(token=="BlockReady")return Phase.BlockReady;if(token=="Countdown")return Phase.Countdown;throw new FormatException("Invalid phase");}
    }
}
