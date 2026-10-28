using System;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace AvatarStroop.Core
{
    public enum PeerRole { Participant, Operator }
    public enum WireKind { Hello, HeadEye, Task, TimeRequest, TimeReply, SafetyStopRequest, SafetyStopAck }

    // Engineering protocol identity, not authentication. Use only on an explicitly trusted development network.
    public sealed class PeerIdentity
    {
        public string SessionId { get; private set; }
        public string PeerId { get; private set; }
        public PeerRole Role { get; private set; }
        public string SourceId { get; private set; }
        public ControlSource Source { get; private set; }
        public string ClockId { get; private set; }
        public PeerIdentity(string sessionId, string peerId, PeerRole role, string sourceId, ControlSource source, string clockId)
        {
            WireCodec.RequireIdentity(sessionId); WireCodec.RequireIdentity(peerId); WireCodec.RequireIdentity(sourceId); WireCodec.RequireIdentity(clockId);
            if(!Enum.IsDefined(typeof(PeerRole),role) || !Enum.IsDefined(typeof(ControlSource),source)) throw new ArgumentException("Unknown peer provenance");
            SessionId=sessionId; PeerId=peerId; Role=role; SourceId=sourceId; Source=source; ClockId=clockId;
        }
    }

    // Operator correspondence intentionally excludes response, correctness, RT and rating data.
    public sealed class TaskSnapshot
    {
        public string SessionId { get; private set; }
        public int BlockIndex { get; private set; }
        public int Segment { get; private set; }
        public string TrialId { get; private set; }
        public Colour? Word { get; private set; }
        public Colour? Ink { get; private set; }
        public Phase Phase { get; private set; }
        public Condition? Condition { get; private set; }
        public TaskSnapshot(string sessionId,int blockIndex,int segment,string trialId,Colour? word,Colour? ink,Phase phase,Condition? condition)
        {
            WireCodec.RequireIdentity(sessionId);
            if(trialId!=null) WireCodec.RequireIdentity(trialId);
            if(blockIndex < -1 || blockIndex > 3 || segment < 0 || !Enum.IsDefined(typeof(Phase),phase) ||
                (condition.HasValue && !Enum.IsDefined(typeof(Condition),condition.Value)) ||
                (word.HasValue && !Enum.IsDefined(typeof(Colour),word.Value)) || (ink.HasValue && !Enum.IsDefined(typeof(Colour),ink.Value)) ||
                word.HasValue!=ink.HasValue || (word.HasValue && trialId==null)) throw new ArgumentException("Invalid task snapshot");
            SessionId=sessionId; BlockIndex=blockIndex; Segment=segment; TrialId=trialId; Word=word; Ink=ink; Phase=phase; Condition=condition;
        }
    }

    public sealed class WirePacket
    {
        public PeerIdentity Peer { get; private set; }
        public WireKind Kind { get; private set; }
        public long Sequence { get; private set; }
        public double SentLocalSeconds { get; private set; }
        public string ProtocolVersion { get; private set; }
        public HeadEyeSample Sample { get; private set; }
        public TaskSnapshot Snapshot { get; private set; }
        public string ExchangeId { get; private set; }
        public string StopRequestId {get;private set;}
        public string RequestClockId { get; private set; }
        public double RequestSentSeconds { get; private set; }
        public double RemoteReceivedSeconds { get; private set; }
        public double RemoteSentSeconds { get; private set; }
        WirePacket(PeerIdentity peer,WireKind kind,long sequence,double sent)
        {
            if(peer==null || sequence<0 || !WireCodec.Finite(sent) || sent<0) throw new ArgumentException("Invalid packet envelope");
            Peer=peer; Kind=kind; Sequence=sequence; SentLocalSeconds=sent;
        }
        public static WirePacket Hello(PeerIdentity peer,long sequence,double sent,string protocolVersion)
        { WireCodec.RequireIdentity(protocolVersion); return new WirePacket(peer,WireKind.Hello,sequence,sent){ProtocolVersion=protocolVersion}; }
        public static WirePacket Tracking(PeerIdentity peer,long packetSequence,HeadEyeSample sample)
        {
            if(peer==null || sample==null || peer.Role!=PeerRole.Operator || peer.Source==ControlSource.Automatic || sample.SessionId!=peer.SessionId || sample.StreamId!=peer.SourceId || sample.ClockId!=peer.ClockId ||
                sample.Source!=peer.Source || (peer.Role==PeerRole.Operator ? StreamRole.OperatorControl : StreamRole.ParticipantMeasurement)!=sample.Role)
                throw new ArgumentException("Tracking and peer provenance differ");
            return new WirePacket(peer,WireKind.HeadEye,packetSequence,sample.SendLocalSeconds){Sample=sample};
        }
        public static WirePacket Task(PeerIdentity peer,long sequence,double sent,TaskSnapshot snapshot)
        {
            if(peer==null || snapshot==null || snapshot.SessionId!=peer.SessionId || peer.Role!=PeerRole.Participant) throw new ArgumentException("Task must originate from participant task host");
            return new WirePacket(peer,WireKind.Task,sequence,sent){Snapshot=snapshot};
        }
        public static WirePacket TimeRequest(PeerIdentity peer,long sequence,double sent,string exchangeId)
        {
            WireCodec.RequireIdentity(exchangeId);
            return new WirePacket(peer,WireKind.TimeRequest,sequence,sent){ExchangeId=exchangeId,RequestClockId=peer.ClockId,RequestSentSeconds=sent};
        }
        public static WirePacket TimeReply(PeerIdentity peer,long sequence,double sent,string exchangeId,string requestClockId,double requestSent,double remoteReceived)
        {
            WireCodec.RequireIdentity(exchangeId); WireCodec.RequireIdentity(requestClockId);
            if(!WireCodec.Finite(requestSent) || requestSent<0 || !WireCodec.Finite(remoteReceived) || remoteReceived<0 || remoteReceived>sent)
                throw new ArgumentException("Invalid time exchange");
            return new WirePacket(peer,WireKind.TimeReply,sequence,sent){ExchangeId=exchangeId,RequestClockId=requestClockId,RequestSentSeconds=requestSent,RemoteReceivedSeconds=remoteReceived,RemoteSentSeconds=sent};
        }
        public static WirePacket SafetyStopRequest(PeerIdentity peer,long sequence,double sent,string requestId)
        {
            WireCodec.RequireIdentity(requestId);
            if(peer==null||peer.Role!=PeerRole.Operator)throw new ArgumentException("Only operator can request remote safety stop");
            return new WirePacket(peer,WireKind.SafetyStopRequest,sequence,sent){StopRequestId=requestId};
        }
        public static WirePacket SafetyStopAck(PeerIdentity peer,long sequence,double sent,string requestId)
        {
            WireCodec.RequireIdentity(requestId);
            if(peer==null||peer.Role!=PeerRole.Participant)throw new ArgumentException("Only participant host acknowledges applying stop");
            return new WirePacket(peer,WireKind.SafetyStopAck,sequence,sent){StopRequestId=requestId};
        }
    }

    public static class WireCodec
    {
        public const string Version = "2";
        public const int MaxPacketBytes = 2048;
        static readonly CultureInfo Invariant=CultureInfo.InvariantCulture;
        static readonly UTF8Encoding StrictUtf8=new UTF8Encoding(false,true);
        public static bool Finite(double value) {return !Double.IsNaN(value) && !Double.IsInfinity(value);}
        internal static void RequireIdentity(string value)
        {
            if(String.IsNullOrWhiteSpace(value) || StrictUtf8.GetByteCount(value)>128) throw new ArgumentException("Identity must contain 1..128 UTF-8 bytes");
            foreach(char c in value) if(Char.IsControl(c)) throw new ArgumentException("Identity contains a control character");
        }
        static string Id(string value) { RequireIdentity(value); return Convert.ToBase64String(StrictUtf8.GetBytes(value)); }
        static string ReadId(string value)
        {
            var decoded=StrictUtf8.GetString(Convert.FromBase64String(value));
            RequireIdentity(decoded);
            if(Id(decoded)!=value) throw new FormatException("Noncanonical identity token");
            return decoded;
        }
        static string Number(double value)
        { if(!Finite(value)) throw new ArgumentException("Wire numbers must be finite; retain raw invalid readings in local diagnostics"); return value.ToString("R",Invariant); }
        static double ReadNumber(string value)
        {
            if(value.Length==0 || value.Length>32) throw new FormatException("Invalid number length");
            foreach(char c in value) if(!(c>='0'&&c<='9') && c!='.' && c!='-' && c!='+' && c!='e' && c!='E') throw new FormatException("Invalid number token");
            double result;
            if(!Double.TryParse(value,NumberStyles.Float,Invariant,out result) || !Finite(result)) throw new FormatException("Invalid finite number");
            return result;
        }
        static long ReadLong(string value)
        {
            long result;
            if(!Int64.TryParse(value,NumberStyles.AllowLeadingSign,Invariant,out result) || result.ToString(Invariant)!=value) throw new FormatException("Invalid integer");
            return result;
        }
        static int ReadInt(string value) {return checked((int)ReadLong(value));}
        static bool ReadBool(string value) {if(value=="1")return true;if(value=="0")return false;throw new FormatException("Invalid validity bit");}
        static T ReadEnum<T>(string value) where T:struct
        {
            T result;
            if(!Enum.TryParse<T>(value,false,out result) || !Enum.IsDefined(typeof(T),result) || result.ToString()!=value) throw new FormatException("Unknown enum token");
            return result;
        }
        static string Optional<T>(T? value) where T:struct {return value.HasValue?value.Value.ToString():"~";}
        static T? ReadOptional<T>(string value) where T:struct {return value=="~"?(T?)null:ReadEnum<T>(value);}

        public static byte[] Encode(WirePacket packet)
        {
            if(packet==null) throw new ArgumentNullException("packet");
            var p=packet.Peer;
            string header=String.Join("|",new[]{"ASW",Version,packet.Kind.ToString(),Id(p.SessionId),Id(p.PeerId),p.Role.ToString(),Id(p.SourceId),p.Source.ToString(),Id(p.ClockId),packet.Sequence.ToString(Invariant),Number(packet.SentLocalSeconds)});
            string payload;
            switch(packet.Kind)
            {
                case WireKind.Hello: payload=Id(packet.ProtocolVersion); break;
                case WireKind.HeadEye:
                    var s=packet.Sample;
                    payload=String.Join("|",new[]{s.Sequence.ToString(Invariant),Id(s.DeviceId),s.Role.ToString(),s.BlockIndex.ToString(Invariant),s.Segment.ToString(Invariant),Number(s.CaptureLocalSeconds),s.HeadReportedValid?"1":"0",s.EyeReportedValid?"1":"0",Number(s.RawHeadRotation.X),Number(s.RawHeadRotation.Y),Number(s.RawHeadRotation.Z),Number(s.RawHeadRotation.W),Number(s.RawEyeDirection.X),Number(s.RawEyeDirection.Y),Number(s.RawEyeDirection.Z)}); break;
                case WireKind.Task:
                    var v=packet.Snapshot;
                    payload=String.Join("|",new[]{v.BlockIndex.ToString(Invariant),v.Segment.ToString(Invariant),v.TrialId==null?"~":Id(v.TrialId),Optional(v.Word),Optional(v.Ink),v.Phase.ToString(),Optional(v.Condition)}); break;
                case WireKind.TimeRequest: payload=Id(packet.ExchangeId); break;
                case WireKind.TimeReply: payload=String.Join("|",new[]{Id(packet.ExchangeId),Id(packet.RequestClockId),Number(packet.RequestSentSeconds),Number(packet.RemoteReceivedSeconds)}); break;
                case WireKind.SafetyStopRequest:
                case WireKind.SafetyStopAck: payload=Id(packet.StopRequestId);break;
                default: throw new ArgumentException("Unknown packet kind");
            }
            byte[] bytes=Encoding.ASCII.GetBytes(header+"|"+payload);
            if(bytes.Length>MaxPacketBytes) throw new ArgumentException("Packet exceeds wire limit");
            return bytes;
        }
        public static bool TryDecode(byte[] bytes,out WirePacket packet,out string error)
        {
            packet=null; error=null;
            if(bytes==null || bytes.Length==0 || bytes.Length>MaxPacketBytes) {error="packet_size";return false;}
            foreach(byte b in bytes) if(b<33 || b>126) {error="non_ascii_or_whitespace";return false;}
            var f=Encoding.ASCII.GetString(bytes).Split('|');
            if(f.Length<12 || f[0]!="ASW") {error="envelope";return false;}
            if(f[1]!=Version) {error="wire_version";return false;}
            try
            {
                var kind=ReadEnum<WireKind>(f[2]);
                int fields=kind==WireKind.HeadEye?26:kind==WireKind.Task?18:kind==WireKind.TimeReply?15:12;
                if(f.Length!=fields) {error="field_count";return false;}
                var peer=new PeerIdentity(ReadId(f[3]),ReadId(f[4]),ReadEnum<PeerRole>(f[5]),ReadId(f[6]),ReadEnum<ControlSource>(f[7]),ReadId(f[8]));
                long seq=ReadLong(f[9]); double sent=ReadNumber(f[10]);
                if(seq<0 || sent<0) throw new FormatException("Negative envelope time or sequence");
                switch(kind)
                {
                    case WireKind.Hello: packet=WirePacket.Hello(peer,seq,sent,ReadId(f[11]));break;
                    case WireKind.HeadEye:
                        long sampleSequence=ReadLong(f[11]);
                        int block=ReadInt(f[14]), segment=ReadInt(f[15]); double capture=ReadNumber(f[16]);
                        if(sampleSequence<0 || block < 0 || block > 3 || segment<0 || capture<0 || capture>sent) throw new FormatException("Invalid tracking chronology");
                        var sample=new HeadEyeSample(peer.SessionId,peer.SourceId,ReadId(f[12]),peer.ClockId,block,segment,sampleSequence,capture,sent,peer.Source,ReadEnum<StreamRole>(f[13]),new Rotation(ReadNumber(f[19]),ReadNumber(f[20]),ReadNumber(f[21]),ReadNumber(f[22])),new Vec3(ReadNumber(f[23]),ReadNumber(f[24]),ReadNumber(f[25])),ReadBool(f[17]),ReadBool(f[18]));
                        packet=WirePacket.Tracking(peer,seq,sample);break;
                    case WireKind.Task:
                        packet=WirePacket.Task(peer,seq,sent,new TaskSnapshot(peer.SessionId,ReadInt(f[11]),ReadInt(f[12]),f[13]=="~"?null:ReadId(f[13]),ReadOptional<Colour>(f[14]),ReadOptional<Colour>(f[15]),ReadEnum<Phase>(f[16]),ReadOptional<Condition>(f[17])));break;
                    case WireKind.TimeRequest: packet=WirePacket.TimeRequest(peer,seq,sent,ReadId(f[11]));break;
                    case WireKind.TimeReply: packet=WirePacket.TimeReply(peer,seq,sent,ReadId(f[11]),ReadId(f[12]),ReadNumber(f[13]),ReadNumber(f[14]));break;
                    case WireKind.SafetyStopRequest: packet=WirePacket.SafetyStopRequest(peer,seq,sent,ReadId(f[11]));break;
                    case WireKind.SafetyStopAck: packet=WirePacket.SafetyStopAck(peer,seq,sent,ReadId(f[11]));break;
                }
                return true;
            }
            catch(Exception ex) when(ex is ArgumentException || ex is FormatException || ex is OverflowException)
            { packet=null; error="invalid_payload:"+ex.GetType().Name;return false; }
        }
    }

    public sealed class UdpReceiveResult
    {
        public bool Accepted { get; internal set; }
        public string Error { get; internal set; }
        public WirePacket Packet { get; internal set; }
        public IPEndPoint RemoteEndpoint { get; internal set; }
        public UdpConnectionFault ConnectionFault { get; internal set; }
    }

    public sealed class UdpConnectionFault
    {
        public SocketError SocketErrorCode {get;internal set;}
        public string Operation {get;internal set;}
        public string Reason {get{return "udp_connection_reset:"+Operation;}}
    }

    public sealed class UdpSendResult
    {
        // Socket acceptance is not a delivery acknowledgement.
        public bool Sent {get;internal set;}
        public UdpConnectionFault ConnectionFault {get;internal set;}
    }

    // Injectable socket boundary for deterministic platform-error regressions.
    internal interface IUdpSocket : IDisposable
    {
        IPEndPoint LocalEndpoint {get;}
        bool Poll();
        byte[] Receive(ref IPEndPoint sender);
        void Send(byte[] bytes,IPEndPoint remote);
    }

    internal sealed class UdpSocket : IUdpSocket
    {
        readonly UdpClient client;
        public IPEndPoint LocalEndpoint {get{return (IPEndPoint)client.Client.LocalEndPoint;}}
        public UdpSocket(IPEndPoint localEndpoint){client=new UdpClient(localEndpoint);}
        public bool Poll(){return client.Client.Poll(0,SelectMode.SelectRead);}
        public byte[] Receive(ref IPEndPoint sender){return client.Receive(ref sender);}
        public void Send(byte[] bytes,IPEndPoint remote){client.Send(bytes,bytes.Length,remote);}
        public void Dispose(){client.Close();}
    }

    // One configured peer, no discovery or automatic peer/source replacement. Poll from an application's own loop.
    public sealed class UdpPeer : IDisposable
    {
        readonly IUdpSocket socket;
        readonly IPEndPoint remote;
        readonly IPEndPoint local;
        bool disposed;
        public IPEndPoint LocalEndpoint {get{return new IPEndPoint(local.Address,local.Port);}}
        public bool RemoteNetworkExplicitlyEnabled {get;private set;}
        public UdpConnectionFault ConnectionFault {get;private set;}
        public UdpPeer(IPEndPoint localEndpoint,IPEndPoint remoteEndpoint,bool allowRemoteNetwork)
            :this(localEndpoint,remoteEndpoint,allowRemoteNetwork,null){}
        internal UdpPeer(IPEndPoint localEndpoint,IPEndPoint remoteEndpoint,bool allowRemoteNetwork,IUdpSocket socket)
        {
            if(localEndpoint==null || remoteEndpoint==null || remoteEndpoint.Port==0 || localEndpoint.AddressFamily!=remoteEndpoint.AddressFamily)
                throw new ArgumentException("Explicit compatible endpoints required");
            if(!allowRemoteNetwork && (!IPAddress.IsLoopback(localEndpoint.Address) || !IPAddress.IsLoopback(remoteEndpoint.Address)))
                throw new ArgumentException("Remote network exposure requires explicit opt-in");
            remote=new IPEndPoint(remoteEndpoint.Address,remoteEndpoint.Port); RemoteNetworkExplicitlyEnabled=allowRemoteNetwork;
            this.socket=socket??new UdpSocket(localEndpoint);
            var bound=this.socket.LocalEndpoint;local=new IPEndPoint(bound.Address,bound.Port);
        }
        UdpConnectionFault Reset(string operation,SocketException error)
        {
            ConnectionFault=new UdpConnectionFault{SocketErrorCode=error.SocketErrorCode,Operation=operation};
            // WSAECONNRESET on UDP reports a prior ICMP Port Unreachable. End this
            // socket epoch; never retry, reopen it or suppress unrelated errors.
            Dispose();return ConnectionFault;
        }
        public UdpSendResult Send(WirePacket packet)
        {
            var bytes=WireCodec.Encode(packet);
            if(ConnectionFault!=null)return new UdpSendResult{ConnectionFault=ConnectionFault};
            if(disposed)throw new ObjectDisposedException("UdpPeer");
            try {socket.Send(bytes,remote);return new UdpSendResult{Sent=true};}
            catch(SocketException error) when(error.SocketErrorCode==SocketError.ConnectionReset)
            {return new UdpSendResult{ConnectionFault=Reset("send",error)};}
        }
        // A terminal fault is returned once, then Poll returns null without I/O.
        // Callers must audit the explicit fault and gate current input immediately.
        public UdpReceiveResult Poll()
        {
            if(ConnectionFault!=null)return null;
            if(disposed)throw new ObjectDisposedException("UdpPeer");
            IPEndPoint sender=null;byte[] bytes;string operation="poll";
            try
            {
                if(!socket.Poll())return null;
                operation="receive";bytes=socket.Receive(ref sender);
            }
            catch(SocketException socketError) when(socketError.SocketErrorCode==SocketError.ConnectionReset)
            {var fault=Reset(operation,socketError);return new UdpReceiveResult{ConnectionFault=fault,Error=fault.Reason};}
            var result=new UdpReceiveResult{RemoteEndpoint=sender};
            if(!sender.Address.Equals(remote.Address) || sender.Port!=remote.Port) {result.Error="unexpected_endpoint";return result;}
            WirePacket packet; string error;
            result.Accepted=WireCodec.TryDecode(bytes,out packet,out error);result.Packet=packet;result.Error=error;return result;
        }
        public void Dispose() {if(disposed)return;disposed=true;socket.Dispose();}
    }
}
