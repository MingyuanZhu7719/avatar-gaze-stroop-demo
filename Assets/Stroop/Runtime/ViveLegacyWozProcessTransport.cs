using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using AvatarStroop.Core;

namespace AvatarStroop.Runtime
{
    // Role-local owned boundary. No diagnostic receipt/provider/clock is adapted.
    // Public controllers select only the private OS implementation; the sealed
    // memory implementation exists solely in the explicit fixture build.
    internal sealed class ViveLegacyWozProcessTransport : IDisposable
    {
        internal const int MaximumDatagramBytes=4096,MaximumDrain=16;
        internal interface ISocket:IDisposable
        {void Start(IPEndPoint local);bool Poll();int Receive(byte[] buffer,out IPEndPoint sender);int Send(byte[] bytes,IPEndPoint remote);}
        readonly ISocket socket;
        readonly Thread thread=Thread.CurrentThread;
        readonly object receiptToken=new object();
        readonly MonotonicClock clock;
        readonly IPEndPoint local,remote;
        readonly WozLanContext context;
        readonly WozAuthenticatedLanEndpoint lane;
        readonly WozAuthenticatedControlEndpoint control;
        readonly byte[] buffer=new byte[MaximumDatagramBytes+1];
        long laneSequence,controlSequence;
        double lastTime=-1;
        bool running,terminal,closeAttempted;
        internal bool Running {get{return running&&!terminal;}}
        internal bool Terminal {get{return terminal;}}
        internal bool CleanupUncertain {get;private set;}
        internal string LastIssue {get;private set;}
        internal WozDiagnosticFailure Failure {get;private set;}
        internal bool UsesClock(MonotonicClock value){return Object.ReferenceEquals(clock,value);}
        internal WozLanContext Context {get{return context;}}
        internal bool UsesOwnedSystemSocket {get{return socket is SystemSocket;}}

        // Historical transport evidence, not capture/identity or live admission.
        // Memory and actual process-socket receipts are distinct sealed types.
        internal abstract class Receipt
        {
            internal readonly ViveLegacyWozProcessTransport Owner;
            internal readonly object Token;
            internal readonly WozAuthenticatedLanEndpoint.VerifiedMessage Data;
            internal readonly WozAuthenticatedControlEndpoint.VerifiedControl Control;
            internal readonly double OriginalReceiptSeconds;
            internal abstract bool SocketCaptured {get;}
            internal Receipt(ViveLegacyWozProcessTransport owner,object token,double received,WozAuthenticatedLanEndpoint.VerifiedMessage data,WozAuthenticatedControlEndpoint.VerifiedControl control)
            {if(owner==null||!Object.ReferenceEquals(token,owner.receiptToken))throw new InvalidOperationException("Owned transport receipt required");
                Owner=owner;Token=token;OriginalReceiptSeconds=received;Data=data;Control=control;}
        }
        internal sealed class SocketReceipt:Receipt
        {
            internal override bool SocketCaptured {get{return true;}}
            internal SocketReceipt(ViveLegacyWozProcessTransport owner,object token,double at,WozAuthenticatedLanEndpoint.VerifiedMessage data,WozAuthenticatedControlEndpoint.VerifiedControl control)
                :base(owner,token,at,data,control){if(!(owner.socket is SystemSocket))throw new InvalidOperationException("Actual socket required");}
        }
        internal sealed class MemoryReceipt:Receipt
        {
            internal override bool SocketCaptured {get{return false;}}
            internal MemoryReceipt(ViveLegacyWozProcessTransport owner,object token,double at,WozAuthenticatedLanEndpoint.VerifiedMessage data,WozAuthenticatedControlEndpoint.VerifiedControl control)
                :base(owner,token,at,data,control){if(owner.socket is SystemSocket)throw new InvalidOperationException("Memory receipt cannot label a socket");}
        }
        internal bool Owns(Receipt receipt)
        {return receipt!=null&&Object.ReferenceEquals(receipt.Owner,this)&&Object.ReferenceEquals(receipt.Token,receiptToken);}
        internal ViveLegacyWozProcessTransport(ViveLegacyWozProcessPairing pairing,PeerRole role,MonotonicClock clock,IPEndPoint local,IPEndPoint remote,double age)
            :this(pairing,role,clock,local,remote,age,new SystemSocket()){}
        ViveLegacyWozProcessTransport(ViveLegacyWozProcessPairing pairing,PeerRole role,MonotonicClock clock,IPEndPoint local,IPEndPoint remote,double age,ISocket socket)
        {
            if(pairing==null||clock==null||socket==null||!Enum.IsDefined(typeof(PeerRole),role)||!WireCodec.Finite(age)||age<=0||age>10)
                throw new ArgumentException("Explicit role, sealed clock and bounded age required");
            context=pairing.Context;this.clock=clock;this.socket=socket;
            if(context.Peer(role).ClockId!=clock.ClockId)throw new ArgumentException("Existing local clock differs from paired identity");
            this.local=Endpoint(local);this.remote=Endpoint(remote);if(this.local.Equals(this.remote))throw new ArgumentException("Distinct endpoints required");
            if(IPAddress.IsLoopback(this.local.Address)!=IPAddress.IsLoopback(this.remote.Address))throw new ArgumentException("Do not mix loopback and LAN endpoints");
            byte[] key=pairing.CopyKey();try{lane=new WozAuthenticatedLanEndpoint(context,role,key,age);control=new WozAuthenticatedControlEndpoint(context,role,key,age);}
            finally{Array.Clear(key,0,key.Length);}
        }
        static IPEndPoint Endpoint(IPEndPoint endpoint)
        {
            if(endpoint==null||endpoint.AddressFamily!=AddressFamily.InterNetwork||endpoint.Port<1||endpoint.Port>65535)throw new ArgumentException("Exact IPv4 endpoint required");
            byte[] b=endpoint.Address.GetAddressBytes();bool privateHost=b[0]==10||(b[0]==172&&b[1]>=16&&b[1]<=31)||(b[0]==192&&b[1]==168)||b[0]==127;
            if(!privateHost||b[3]==0||b[3]==255||(b[0]==127&&!endpoint.Address.Equals(IPAddress.Loopback)))throw new ArgumentException("Explicit private LAN or exact 127.0.0.1 host required");
            return new IPEndPoint(new IPAddress(b),endpoint.Port);
        }
        void Owner(){if(!Object.ReferenceEquals(thread,Thread.CurrentThread))throw new InvalidOperationException("Transport owner thread required");}
        internal double Now
        {
            get{Owner();double now;if(!clock.TryReadRecordingAdmissionTime(out now)||now<lastTime){Fail("local_clock_failed");throw new InvalidOperationException("Owned clock failed");}lastTime=now;return now;}
        }
        internal bool Start()
        {Owner();if(terminal||running)return false;try{double now=Now;socket.Start(local);running=true;return true;}catch(Exception e){FailException("start",e);return false;}}
        bool Next(bool controlLane,out long next)
        {next=controlLane?controlSequence:laneSequence;if(next==Int64.MaxValue){Fail("sequence_exhausted");return false;}if(controlLane)controlSequence++;else laneSequence++;return true;}
        bool Send(byte[] bytes)
        {
            if(!Running)return false;if(bytes==null||bytes.Length>MaximumDatagramBytes){Fail("datagram_size");return false;}
            try{if(socket.Send(bytes,remote)!=bytes.Length){Fail("partial_send");return false;}return true;}
            catch(Exception e){FailException("send",e);return false;}
        }
        internal bool SendTask(ViveLegacyWozTaskOwner.Binding binding,out WozLanTaskContext task)
        {
            Owner();task=null;if(!Running)return false;long sequence;if(!Next(false,out sequence))return false;
            double now=Now;var value=new WozLanTaskContext(binding.Task,binding.WireRevision,sequence,now);byte[] bytes;string error;
            if(!lane.TryProtect(WirePacket.Task(context.Participant,sequence,now,binding.Task),value,null,out bytes,out error)){Fail(error);return false;}
            if(!Send(bytes))return false;task=value;return true;
        }
        internal bool SendControl(WozControlKind kind,WozControlBinding binding,string challenge,double? received=null,double? head=null,double? eye=null)
        {
            Owner();if(!Running)return false;long sequence;if(!Next(true,out sequence))return false;byte[] bytes;string error;
            if(!control.TryProtect(new WozControlMessage(kind,sequence,Now,binding,challenge,received,head,eye),out bytes,out error)){Fail(error);return false;}
            return Send(bytes);
        }
        internal bool SendStop(bool ack,string request)
        {
            Owner();if(!Running)return false;long sequence;if(!Next(false,out sequence))return false;byte[] bytes;string error;double now=Now;
            var packet=ack?WirePacket.SafetyStopAck(context.Participant,sequence,now,request):WirePacket.SafetyStopRequest(context.Operator,sequence,now,request);
            if(!lane.TryProtect(packet,null,null,out bytes,out error)){Fail(error);return false;}return Send(bytes);
        }
        internal bool SendPose(ViveLegacyWozObservationOwner.Candidate candidate,WozLanTaskContext task,double taskReceived)
        {
            Owner();if(!Running)return false;long sequence;if(!Next(false,out sequence))return false;var s=candidate.Sample;
            var sample=new HeadEyeSample(s.SessionId,context.Operator.SourceId,s.DeviceId,clock.ClockId,s.BlockIndex,s.Segment,s.Sequence,s.CaptureLocalSeconds,Now,
                context.Operator.Source,s.Role,s.RawHeadRotation,s.RawEyeDirection,s.HeadReportedValid,s.EyeReportedValid);
            byte[] bytes;string error;var observation=new WozLanPoseObservation(taskReceived,candidate.HeadObserved,candidate.EyeObserved);
            if(!lane.TryProtect(WirePacket.Tracking(context.Operator,sequence,sample),task,observation,out bytes,out error)){Fail(error);return false;}return Send(bytes);
        }
        // Return false for no datagram OR terminal rejection; caller must check
        // Terminal. Expected bindings are owned locally, never remote callbacks.
        internal bool Receive(WozLanTaskContext poseTask,WozControlBinding positive,WozControlBinding negative,out Receipt receipt,
            WozLanTaskContext retiredTask=null,WozControlBinding retiredControl=null)
        {
            Owner();receipt=null;if(!Running)return false;
            try
            {
                if(!socket.Poll())return false;
                IPEndPoint sender;int count=socket.Receive(buffer,out sender);double original=Now;
                // A foreign datagram is not a peer instruction or peer failure.
                // Consume one drain slot without issuing any receipt/effect.
                if(!remote.Equals(sender))return true;
                if(count<8||count>MaximumDatagramBytes){Fail("configured_peer_datagram_size");return false;}
                byte[] bytes=new byte[count];Buffer.BlockCopy(buffer,0,bytes,0,count);
                WozAuthenticatedLanEndpoint.VerifiedMessage data=null;WozAuthenticatedControlEndpoint.VerifiedControl message=null;string error;
                // Untrusted type only selects the expected negative binding.
                // No action occurs until full domain/role/context/MAC verification.
                if(bytes[0]=='W'&&bytes[1]=='O'&&bytes[2]=='Z'&&bytes[3]=='C')
                {
                    bool revoke=count>42&&(bytes[42]==(byte)WozControlKind.SourceRevoke||bytes[42]==(byte)WozControlKind.SourceRevokeAck);
                    if(!control.TryReceive(bytes,original,Now,revoke?negative:positive,out message,out error))
                    {
                        // Only a single locally retained prior context may be
                        // authenticated for DISCARD during renewal. A failed
                        // exact-binding comparison commits no replay state.
                        if(revoke||error!="unexpected_binding"||retiredControl==null||
                            !control.TryReceive(bytes,original,Now,retiredControl,out message,out error))
                        {Fail("control_rejected:"+error);return false;}
                    }
                }
                else if(!lane.TryReceive(bytes,original,Now,poseTask,out data,out error))
                {
                    if(error!="unexpected_pose_task"||retiredTask==null||!lane.TryReceive(bytes,original,Now,retiredTask,out data,out error))
                    {Fail("data_rejected:"+error);return false;}
                }
                receipt=socket is SystemSocket?(Receipt)new SocketReceipt(this,receiptToken,original,data,message):new MemoryReceipt(this,receiptToken,original,data,message);
                return true;
            }
            catch(SocketException e) when(e.SocketErrorCode==SocketError.WouldBlock){return false;}
            catch(Exception e){FailException("receive",e);return false;}
        }
        void FailException(string operation,Exception error)
        {Failure=new WozDiagnosticFailure(operation,error);Fail(operation+":"+error.GetType().Name+":hresult="+error.HResult+
            (error is SocketException?":socket="+(int)((SocketException)error).SocketErrorCode+":native="+((SocketException)error).NativeErrorCode:""));}
        void Fail(string issue){LastIssue=issue;Dispose();}
        public void Dispose()
        {Owner();running=false;terminal=true;lane.Dispose();control.Dispose();if(closeAttempted)return;closeAttempted=true;try{socket.Dispose();}catch(Exception e){CleanupUncertain=true;Failure=new WozDiagnosticFailure("close",e);LastIssue="socket_close_uncertain:"+e.GetType().Name+":hresult="+e.HResult;}}
        sealed class SystemSocket:ISocket
        {
            Socket value;
            public void Start(IPEndPoint endpoint)
            {value=new Socket(AddressFamily.InterNetwork,SocketType.Dgram,ProtocolType.Udp);value.ExclusiveAddressUse=true;value.Blocking=false;value.EnableBroadcast=false;value.DontFragment=true;value.ReceiveBufferSize=16384;value.Bind(endpoint);}
            public bool Poll(){return value.Poll(0,SelectMode.SelectRead);}
            public int Receive(byte[] bytes,out IPEndPoint sender)
            {EndPoint from=new IPEndPoint(IPAddress.Any,0);int count=value.ReceiveFrom(bytes,0,bytes.Length,SocketFlags.None,ref from);sender=(IPEndPoint)from;return count;}
            public int Send(byte[] bytes,IPEndPoint endpoint){return value.SendTo(bytes,endpoint);}
            public void Dispose(){if(value!=null){value.Dispose();value=null;}}
        }
#if STROOP_TEST_BUILD
        internal sealed class MemorySocket:ISocket
        {
            internal readonly Queue<Tuple<byte[],IPEndPoint>> Queue=new Queue<Tuple<byte[],IPEndPoint>>();
            internal MemorySocket Other;internal byte[] LastSent;internal bool FailClose;internal bool FailSend {get;set;}internal Action DuringSend;
            IPEndPoint bound;bool closed;
            public void Start(IPEndPoint endpoint){if(bound!=null||closed)throw new InvalidOperationException();bound=Endpoint(endpoint);}
            public bool Poll(){return Queue.Count>0;}
            public int Receive(byte[] bytes,out IPEndPoint sender){var entry=Queue.Dequeue();sender=entry.Item2;int count=Math.Min(bytes.Length,entry.Item1.Length);Buffer.BlockCopy(entry.Item1,0,bytes,0,count);return count;}
            public int Send(byte[] bytes,IPEndPoint endpoint)
            {var hook=DuringSend;DuringSend=null;if(hook!=null)hook();if(FailSend||closed||Other==null||!endpoint.Equals(Other.bound))throw new InvalidOperationException();LastSent=(byte[])bytes.Clone();Other.Queue.Enqueue(Tuple.Create((byte[])bytes.Clone(),bound));return bytes.Length;}
            public void Dispose(){closed=true;if(FailClose)throw new InvalidOperationException();}
        }
        internal static ViveLegacyWozProcessTransport Memory(ViveLegacyWozProcessPairing pairing,PeerRole role,MonotonicClock clock,IPEndPoint local,IPEndPoint remote,double age,MemorySocket socket)
        {return new ViveLegacyWozProcessTransport(pairing,role,clock,local,remote,age,socket);}
#endif
    }
}
