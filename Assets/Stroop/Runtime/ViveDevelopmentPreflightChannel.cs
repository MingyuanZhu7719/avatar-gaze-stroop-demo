using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using AvatarStroop.Core;

namespace AvatarStroop.Runtime
{
    // Fixed receive buffer: one extra byte detects truncation on platforms that
    // return a truncated datagram; Windows can instead report MessageSize.
    internal sealed class ViveBoundedPreflightSocket : IUdpSocket
    {
        readonly Socket socket;
        public IPEndPoint LocalEndpoint {get{return (IPEndPoint)socket.LocalEndPoint;}}
        public ViveBoundedPreflightSocket(IPEndPoint local)
        {
            socket=new Socket(local.AddressFamily,SocketType.Dgram,ProtocolType.Udp);
            try{socket.Blocking=false;socket.Bind(local);}catch{socket.Dispose();throw;}
        }
        public bool Poll(){return socket.Poll(0,SelectMode.SelectRead);}
        public byte[] Receive(ref IPEndPoint sender)
        {
            var buffer=new byte[ViveDevelopmentPreflightWire.MaxPacketBytes+1];
            EndPoint remote=new IPEndPoint(socket.AddressFamily==AddressFamily.InterNetworkV6?IPAddress.IPv6Any:IPAddress.Any,0);
            int length=socket.ReceiveFrom(buffer,ref remote);sender=(IPEndPoint)remote;
            if(length==buffer.Length)return buffer;
            var exact=new byte[length];Buffer.BlockCopy(buffer,0,exact,0,length);return exact;
        }
        public void Send(byte[] bytes,IPEndPoint remote)
        {if(socket.SendTo(bytes,remote)!=bytes.Length)throw new SocketException((int)SocketError.MessageSize);}
        public void Dispose(){socket.Dispose();}
    }
    // VDPF development channel only. Datagram endpoint equality is not
    // authentication; socket acceptance is not delivery, and no code here
    // admits an apparatus session or supplies a research recording.
    public sealed class ViveDevelopmentPreflightChannel : IDisposable
    {
        readonly VivePreflightEndpointConfiguration config;
        readonly VivePreflightParticipant participant;
        readonly VivePreflightOperator op;
        IUdpSocket socket;
        Func<bool> transportHealthy;
        Action<ViveDevelopmentPreflightChannel> detachTransport;
        readonly int ownerThread=Thread.CurrentThread.ManagedThreadId;
        bool attempted,disposed,faulted;
        public bool Active {get{ValidateTransport();return socket!=null&&!disposed&&!faulted;}}
        public bool RecoveryDecisionRequired {get{ValidateTransport();return faulted||(participant!=null?participant.RecoveryDecisionRequired:op.RecoveryDecisionRequired);}}
        public bool CanEnterCountdown {get{return false;}}
        public bool CryptographicallyAuthenticated {get{return false;}}
        public string LastIssue {get;private set;}
        // Build the validator from the same immutable endpoint/session config as
        // the socket. Callers cannot accidentally pair an unrelated validator.
        public ViveDevelopmentPreflightChannel(VivePreflightEndpointConfiguration config,IClock clock,ClockSynchronizer mapping)
        {
            if(config==null||clock==null||mapping==null)throw new ArgumentNullException("Explicit channel configuration and clocks required");
            this.config=config;
            if(config.Role==PeerRole.Participant)participant=new VivePreflightParticipant(config,clock,mapping);
            else if(config.Role==PeerRole.Operator)op=new VivePreflightOperator(config,clock,mapping);
            else throw new ArgumentException("Valid endpoint role required");
        }
        internal void AttachTransport(Func<bool> healthy,Action<ViveDevelopmentPreflightChannel> detach)
        {if(healthy==null||detach==null||transportHealthy!=null)throw new InvalidOperationException("One control transport binding is required");transportHealthy=healthy;detachTransport=detach;}
        internal void InvalidateFromTransport(){Fault("control_transport_unavailable");}
        void ValidateTransport()
        {
            if(disposed||faulted||transportHealthy==null)return;
            bool healthy=false;try{healthy=transportHealthy();}catch(Exception){healthy=false;}
            if(!healthy)Fault("control_transport_unavailable");
        }
        // Creation/binding happens only after both explicit approvals. A failed
        // or stopped channel is terminal; make a new session/epoch, not a retry.
        public void Start(bool explicitStart,bool explicitTrustedNetwork)
        {
            Owner();
            if(!explicitStart)throw new InvalidOperationException("Explicit preflight channel start is required");
            if(config.RemoteNetworkExplicitlyEnabled&&!explicitTrustedNetwork)throw new InvalidOperationException("Remote trusted-network confirmation is required");
            if(attempted||disposed||RecoveryDecisionRequired)throw new InvalidOperationException("Channel epoch already started, faulted or ended");
            throw new InvalidOperationException("Unauthenticated VIVE preflight networking is disabled, including loopback");
        }
        // Deterministic synthetic injection, never used by the application API.
        internal void StartForTest(ViveInMemoryDatagramSocket fake)
        {
            Owner();
            if(fake==null||attempted||disposed||RecoveryDecisionRequired)throw new InvalidOperationException("Fresh, unfaulted synthetic socket required");
            if(config.RemoteNetworkExplicitlyEnabled||!fake.LocalEndpoint.Equals(config.Local)||!IPAddress.IsLoopback(config.Remote.Address))throw new ArgumentException("Synthetic local endpoint mismatch");
            attempted=true;socket=fake;
        }
        void Owner()
        {if(Thread.CurrentThread.ManagedThreadId!=ownerThread)throw new InvalidOperationException("Preflight channel calls must use its owning thread");ValidateTransport();}
        void Require(PeerRole role)
        {Owner();if(!Active||config.Role!=role)throw new InvalidOperationException("Active channel and matching role required");}
        public void ConfirmSource(bool explicitOwnerConfirmation)
        {
            Owner();if(disposed||faulted)throw new InvalidOperationException("The channel is not available");
            if(!explicitOwnerConfirmation)throw new InvalidOperationException("Explicit source confirmation required");
            Checked(()=>{if(participant!=null)participant.ConfirmSource(true);else op.ConfirmSource(true);return true;});
        }
        public bool ObserveOperatorTask(VivePreflightOperatorTask task)
        {Owner();if(config.Role!=PeerRole.Operator||disposed||faulted)throw new InvalidOperationException("Available operator channel required");return Checked(()=>op.ObserveTask(task));}
        // Unexpected state/clock exceptions cannot leave a previously ready
        // candidate usable. Preserve the exception for the caller after latching.
        T Checked<T>(Func<T> action)
        {
            try
            {
                T result=action();
                if(RecoveryDecisionRequired)Fault("preflight_recovery_required");
                return result;
            }
            catch(Exception e){Fault("preflight_state_failed:"+e.GetType().Name);throw;}
        }
        bool Send(byte[] bytes)
        {
            if(bytes==null||bytes.Length==0||bytes.Length>ViveDevelopmentPreflightWire.MaxPacketBytes)throw new ArgumentException("Bounded preflight packet required");
            try{socket.Send(bytes,config.Remote);return true;}
            catch(Exception e) when(e is SocketException||e is ObjectDisposedException||e is UnauthorizedAccessException||e is System.Security.SecurityException)
            {Fault("socket_send_failed:"+e.GetType().Name);return false;}
        }
        public bool Begin(TaskSnapshot task,bool taskConfirmed,bool liveConfirmed)
        {Require(PeerRole.Participant);return Checked(()=>Send(participant.BeginContext(task,taskConfirmed,liveConfirmed)));}
        public bool Retry(TaskSnapshot task,bool taskConfirmed,bool liveConfirmed)
        {Require(PeerRole.Participant);return Checked(()=>Send(participant.RetryChallenge(task,taskConfirmed,liveConfirmed)));}
        public bool SendProof(ViveProEyeObservation observation,bool sourceReady,VivePreflightOperatorTask task)
        {
            Require(PeerRole.Operator);var proof=Checked(()=>op.CreateProof(observation,sourceReady,task));
            if(proof==null){LastIssue=op.LastIssue;return false;}return Send(proof);
        }
        // At most one bounded datagram per call; the caller must tick readiness
        // even if no packet arrives. Invalid/wrong-source packets never become proof.
        public bool PollParticipant(TaskSnapshot task,bool taskConfirmed,bool liveConfirmed)
        {
            Require(PeerRole.Participant);IPEndPoint sender;byte[] bytes;
            if(!Receive(out bytes,out sender))return false;
            bool accepted=Checked(()=>participant.AcceptProof(bytes,sender,task,taskConfirmed,liveConfirmed));
            LastIssue=participant.LastIssue;return accepted;
        }
        public bool PollOperator(VivePreflightOperatorTask task)
        {
            Require(PeerRole.Operator);IPEndPoint sender;byte[] bytes;
            if(!Receive(out bytes,out sender))return false;
            bool accepted=Checked(()=>op.AcceptChallenge(bytes,sender,task));LastIssue=op.LastIssue;return accepted;
        }
        public bool CandidateReady(TaskSnapshot task,bool taskConfirmed,bool liveConfirmed)
        {Require(PeerRole.Participant);return Checked(()=>participant.CandidateReady(task,taskConfirmed,liveConfirmed));}
        bool Receive(out byte[] bytes,out IPEndPoint sender)
        {
            bytes=null;sender=null;
            try{if(!socket.Poll()){LastIssue="no_datagram";return false;}bytes=socket.Receive(ref sender);}
            catch(SocketException e) when(e.SocketErrorCode==SocketError.WouldBlock){LastIssue="no_datagram";return false;}
            catch(SocketException e) when(e.SocketErrorCode==SocketError.MessageSize){LastIssue="packet_size";return false;}
            catch(Exception e) when(e is SocketException||e is ObjectDisposedException||e is UnauthorizedAccessException||e is System.Security.SecurityException)
            {Fault("socket_receive_failed:"+e.GetType().Name);return false;}
            if(sender==null||!config.MatchesRemote(sender)){LastIssue="unexpected_endpoint";return false;}
            if(bytes==null||bytes.Length==0||bytes.Length>ViveDevelopmentPreflightWire.MaxPacketBytes){LastIssue="packet_size";return false;}
            return true;
        }
        // Invoke on peer disappearance / transport loss even when the local
        // socket itself remains healthy; no automatic reopening or resume.
        public void SignalLoss(string reason)
        {Owner();if(String.IsNullOrWhiteSpace(reason))throw new ArgumentException("Explicit reason required");Fault(reason);}
        void Fault(string reason)
        {
            if(faulted)return;faulted=true;LastIssue=reason;
            try{if(participant!=null)participant.SignalLoss(reason);else op.SignalLoss(reason);}
            catch(Exception e){LastIssue=reason+"; preflight loss callback failed:"+e.GetType().Name;}
            finally
            {
                if(socket!=null){try{socket.Dispose();}catch(Exception e){LastIssue=reason+"; socket cleanup failed:"+e.GetType().Name;}finally{socket=null;}}
            }
        }
        public void Dispose()
        {
            Owner();
            if(disposed)return;
            if(attempted&&!faulted)Fault("channel_closed");
            disposed=true;if(socket!=null){socket.Dispose();socket=null;}
            if(detachTransport!=null){var detach=detachTransport;detachTransport=null;detach(this);}
        }
    }
}
