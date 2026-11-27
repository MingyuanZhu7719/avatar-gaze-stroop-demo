using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using AvatarStroop.Core;
using Photon.Client;
using Photon.Realtime;

namespace AvatarStroop.Runtime
{
    // This object owns the actual Photon client. No public callback or injected
    // byte provider can manufacture a production network receipt.
    public sealed class ViveWozPhotonSession : IDisposable
    {
        const byte Hello=31,Confirm=32,ConfirmAck=33,Offer=34,OperatorStart=35,ParticipantStart=36,Datagram=37;
        const string Version="avatar-woz-photon-3";
        readonly Thread thread=Thread.CurrentThread;
        readonly MonotonicClock clock;
        readonly bool participant;
        readonly RealtimeClient client;
        readonly Callbacks callbacks;
        readonly string nonce=Guid.NewGuid().ToString("N");
        readonly ViveWozPhotonInbox inbox=new ViveWozPhotonInbox();
        string remoteNonce,remoteClock,offer,offerHash,claimedHash,claimedEpoch;
        int peerActor,localActor;
        bool terminal,disposed,joined,helloReceived,confirmed,confirmSent,offerPublished,offerTaken,channelClaimed,channelStarted,channelClosed;
        double lastTime=-1,setupStarted;
        public string RoomCode {get;private set;}
        public string Status {get;private set;}
        public string FailureReason {get;private set;}
        public bool Terminal {get{return terminal||disposed;}}
        public bool RoomReady {get{return !Terminal&&joined;}}
        public bool PeerConnected {get{return !Terminal&&peerActor>0&&helloReceived;}}
        public bool PeerConfirmed {get{return !Terminal&&confirmed;}}
        public bool ReadyForOffer {get{return participant&&PeerConfirmed&&!offerPublished;}}
        public string OperatorClockId {get;private set;}
        public bool OfferAvailable {get{return !participant&&!Terminal&&offer!=null&&!offerTaken;}}
        public bool OperatorStarted {get;private set;}
        public bool ParticipantStarted {get;private set;}
        public bool Complete {get{return !Terminal&&OperatorStarted&&ParticipantStarted;}}
        public string Region {get;private set;}
        public string TimingProfile {get{return ViveWozTimingPolicy.Photon.Name;}}
        public double NetworkVisualBudgetSeconds {get{return ViveWozTimingPolicy.Photon.NetworkTaskLease;}}
        public double LocalReceiptBudgetSeconds {get{return ViveWozTimingPolicy.Photon.ReceivedPoseAge;}}
        public double LocalCaptureBudgetSeconds {get{return ViveWozTimingPolicy.Photon.LocalCaptureAge;}}
        public double ControlProofBudgetSeconds {get{return ViveWozTimingPolicy.Photon.ControlProofLease;}}
        public int PeerActorNumber {get{return peerActor;}}
        public long RoundTripMilliseconds {get{return client.RealtimePeer.Stats.RoundtripTime;}}

        public static ViveWozPhotonSession CreateOperator(string appId,string region,MonotonicClock clock)
        {return new ViveWozPhotonSession(appId,region,clock,false,NewRoomCode());}
        public static ViveWozPhotonSession JoinParticipant(string appId,string region,MonotonicClock clock,string roomCode)
        {return new ViveWozPhotonSession(appId,region,clock,true,NormalizeRoomCode(roomCode));}
        ViveWozPhotonSession(string appId,string region,MonotonicClock clock,bool participant,string code)
        {
            Guid parsed;if(clock==null||!Guid.TryParse(appId,out parsed)||parsed==Guid.Empty)throw new ArgumentException("A configured Photon Realtime application and current clock are required.");
            if(String.IsNullOrWhiteSpace(region))region="asia";
            region=region.Trim().ToLowerInvariant();if(region.Length>16)throw new ArgumentException("Invalid Photon region.");
            foreach(char c in region)if(c<'a'||c>'z')throw new ArgumentException("Invalid Photon region.");
            this.clock=clock;this.participant=participant;RoomCode=code;Region=region;
            if(!participant)OperatorClockId=clock.ClockId;
            setupStarted=Now;Status="Connecting to Photon Cloud...";
            client=new RealtimeClient();callbacks=new Callbacks(this);client.AddCallbackTarget(callbacks);
            // All traffic, including relayed events, uses TLS. No UDP fallback.
            var settings=Settings(appId,region);
            if(!client.ConnectUsingSettings(settings))Fail("connect_not_started");
        }
        internal static AppSettings Settings(string appId,string region)
        {return new AppSettings{AppIdRealtime=appId,AppVersion=Version,FixedRegion=region,
            Protocol=ConnectionProtocol.WebSocketSecure,EnableProtocolFallback=false,
            ClientLogging=LogLevel.Off,NetworkLogging=LogLevel.Off};}
        internal static bool ValidPeerHello(string[] fields,bool participant)
        {return fields!=null&&fields.Length==4&&fields[0]==Version&&fields[1]==(participant?"operator":"participant")&&
            Nonce(fields[2])&&!String.IsNullOrEmpty(fields[3])&&fields[3].Length<=160;}
        void Owner(){if(!Object.ReferenceEquals(thread,Thread.CurrentThread))throw new InvalidOperationException("Photon owner thread required.");}
        double Now
        {get{Owner();double at;if(!clock.TryReadRecordingAdmissionTime(out at)||at<lastTime)throw new InvalidOperationException("Current monotonic clock required.");lastTime=at;return at;}}
        public void Service()
        {
            Owner();if(disposed)return;
            try{client.Service();if(!terminal&&!Complete&&Now-setupStarted>600)Fail("setup_expired");}
            catch(Exception e){Fail("service_"+e.GetType().Name);}
        }
        public void ConfirmPeer()
        {
            Owner();if(participant||!PeerConnected||confirmSent||confirmed)throw new InvalidOperationException("A current participant must be reviewed first.");
            confirmSent=true;if(Send(Confirm,new[]{nonce,remoteNonce}))Status="Waiting for participant confirmation...";
        }
        public void PublishOffer(string token)
        {
            Owner();if(!ReadyForOffer||!channelClaimed||String.IsNullOrEmpty(token)||token.Length>2048||Hash(token)!=claimedHash)
                throw new InvalidOperationException("The current owned participant offer is required.");
            offerPublished=true;if(Send(Offer,new[]{nonce,remoteNonce,token}))Status="Session sent. Waiting for operator review...";
        }
        public string TakeOffer()
        {Owner();if(!OfferAvailable)return null;offerTaken=true;string value=offer;offer=null;return value;}
        public void MarkOperatorStarted()
        {
            Owner();if(participant||!offerTaken||!channelStarted||OperatorStarted||Terminal)throw new InvalidOperationException("Current operator process must be started first.");
            OperatorStarted=true;if(Send(OperatorStart,new[]{nonce,remoteNonce}))Status="Operator ready. Waiting for participant...";
        }
        public void MarkParticipantStarted()
        {
            Owner();if(!participant||!OperatorStarted||!channelStarted||ParticipantStarted||Terminal)throw new InvalidOperationException("Current participant process must be started first.");
            ParticipantStarted=true;if(Send(ParticipantStart,new[]{nonce,remoteNonce}))Status="Connected through Photon Cloud.";
        }
        bool Send(byte code,object value)
        {
            if(Terminal||peerActor<1||!client.InRoom)return false;
            if(!client.OpRaiseEvent(code,value,new RaiseEventArgs{TargetActors=new[]{peerActor},CachingOption=EventCaching.DoNotCache},
                new SendOptions{Reliability=true,Encrypt=true,Channel=0}))
            {Fail("event_not_queued");return false;}return true;
        }
        void ConnectedToMaster()
        {
            if(Terminal)return;
            bool queued=participant?client.OpJoinRoom(new EnterRoomArgs{RoomName=RoomCode}):
                client.OpCreateRoom(new EnterRoomArgs{RoomName=RoomCode,RoomOptions=new RoomOptions{
                    MaxPlayers=2,IsVisible=false,IsOpen=true,PlayerTtl=0,EmptyRoomTtl=0,CleanupCacheOnLeave=true}});
            if(!queued)Fail("room_request_not_queued");else Status=participant?"Joining room...":"Creating room...";
        }
        void JoinedRoom()
        {
            if(Terminal)return;
            if(client.CurrentRoom==null||client.CurrentRoom.Name!=RoomCode||client.CurrentRoom.MaxPlayers!=2||client.CurrentRoom.PlayerCount>2)
            {Fail("room_identity");return;}
            localActor=client.LocalPlayer.ActorNumber;
            if(localActor<1||participant==(client.CurrentRoom.MasterClientId==localActor)){Fail("room_role");return;}
            joined=true;
            foreach(var player in client.CurrentRoom.Players.Values)if(player.ActorNumber!=localActor)Pin(player.ActorNumber);
            if(peerActor>0)SendHello();else Status="Room ready. Ask the participant to enter the room code.";
        }
        void Pin(int actor)
        {
            if(actor<1||actor==localActor||(peerActor!=0&&peerActor!=actor)){Fail("peer_changed");return;}
            peerActor=actor;inbox.Pin(actor);
            if(!participant&&client.CurrentRoom!=null)client.CurrentRoom.IsOpen=false;
        }
        void PlayerEntered(Player player)
        {if(Terminal)return;if(client.CurrentRoom.PlayerCount!=2){Fail("room_membership");return;}Pin(player.ActorNumber);if(!Terminal)SendHello();}
        void SendHello(){Send(Hello,new[]{Version,participant?"participant":"operator",nonce,clock.ClockId});}
        void Event(EventData value)
        {
            Owner();if(Terminal||value==null)return;
            // Server events and other actors are not peer instructions.
            if(value.Sender!=peerActor||peerActor<1)return;
            double received=Now;
            if(value.Code==Datagram)
            {
                if(channelClosed)return;
                if(!channelClaimed||!confirmed){Fail("packet_before_pairing");return;}
                string error;if(!inbox.Enqueue(value.Sender,value.CustomData as byte[],received,out error))Fail(error);return;
            }
            if(value.Code<Hello||value.Code>ParticipantStart)return;
            string[] fields=value.CustomData as string[];
            if(fields==null){Fail("setup_shape");return;}
            if(value.Code==Hello)
            {
                if(!ValidPeerHello(fields,participant))
                {Fail("peer_hello");return;}
                if(helloReceived){if(remoteNonce!=fields[2]||remoteClock!=fields[3])Fail("peer_hello_changed");return;}
                remoteNonce=fields[2];remoteClock=fields[3];helloReceived=true;
                if(participant){OperatorClockId=fields[3];Status="Connected. Waiting for the operator to confirm this participant.";}
                else Status="Participant joined. Confirm this is the intended participant.";
                return;
            }
            if(!helloReceived||fields.Length<2||fields[0]!=remoteNonce||fields[1]!=nonce){Fail("setup_identity");return;}
            switch(value.Code)
            {
                case Confirm:
                    if(!participant||confirmed||fields.Length!=2){Fail("unexpected_confirmation");return;}
                    confirmed=true;Send(ConfirmAck,new[]{nonce,remoteNonce});Status="Connected. Preparing the current session...";break;
                case ConfirmAck:
                    if(participant||!confirmSent||confirmed||fields.Length!=2){Fail("unexpected_confirmation_ack");return;}
                    confirmed=true;Status="Participant confirmed. Waiting for session...";break;
                case Offer:
                    if(participant||!confirmed||offer!=null||offerTaken||fields.Length!=3||String.IsNullOrEmpty(fields[2])||fields[2].Length>2048)
                    {Fail("unexpected_offer");return;}
                    char[] chars=fields[2].ToCharArray();try{using(var parsed=ViveLegacyWozProcessPairing.Parse(chars))
                        {if(parsed.Context.Operator.ClockId!=clock.ClockId)throw new InvalidOperationException();}}
                    catch{Fail("offer_identity");return;}finally{Array.Clear(chars,0,chars.Length);}
                    offer=fields[2];offerHash=Hash(offer);Status="Session received. Review forwarding and storage choices.";break;
                case OperatorStart:
                    if(!participant||!offerPublished||OperatorStarted||fields.Length!=2){Fail("unexpected_operator_start");return;}
                    OperatorStarted=true;Status="Operator ready. Starting participant...";break;
                case ParticipantStart:
                    if(participant||!OperatorStarted||ParticipantStarted||fields.Length!=2){Fail("unexpected_participant_start");return;}
                    ParticipantStarted=true;Status="Connected through Photon Cloud.";break;
            }
        }
        // A channel can be claimed once, only by the exact pairing/clock/role.
        internal Channel Claim(ViveLegacyWozProcessPairing pairing,PeerRole role,MonotonicClock localClock)
        {
            Owner();if(Terminal||!confirmed||channelClaimed||!Object.ReferenceEquals(clock,localClock)||pairing==null||
                (role==PeerRole.Participant)!=participant||pairing.Context.Operator.ClockId!=OperatorClockId||
                pairing.Context.Peer(role).ClockId!=clock.ClockId||pairing.Context.Peer(participant?PeerRole.Operator:PeerRole.Participant).ClockId!=remoteClock||(!participant&&!offerTaken))
                throw new InvalidOperationException("Current confirmed Photon room and exact pairing required.");
            char[] chars=pairing.RevealToken();string hash;try{hash=Hash(new string(chars));}finally{Array.Clear(chars,0,chars.Length);}
            if(!participant&&hash!=offerHash)throw new InvalidOperationException("The reviewed Photon offer differs from the process pairing.");
            claimedHash=hash;claimedEpoch=pairing.Context.Epoch;channelClaimed=true;return new Channel(this,claimedEpoch);
        }
        internal sealed class Channel : IDisposable
        {
            readonly ViveWozPhotonSession owner;readonly string epoch;bool closed;
            internal Channel(ViveWozPhotonSession owner,string epoch){this.owner=owner;this.epoch=epoch;}
            internal bool Current {get{return !closed&&!owner.Terminal&&owner.channelClaimed&&owner.claimedEpoch==epoch;}}
            internal string Failure {get{return owner.FailureReason;}}
            internal bool UsesClock(MonotonicClock value){return Object.ReferenceEquals(owner.clock,value);}
            internal void Start(){owner.Owner();if(!Current||owner.channelStarted)throw new InvalidOperationException("Photon channel unavailable.");owner.channelStarted=true;}
            internal bool Poll(){owner.Owner();if(!Current)throw new InvalidOperationException("Photon channel closed.");return owner.inbox.Count>0;}
            internal int Receive(byte[] bytes,out double received)
            {owner.Owner();if(!Current)throw new InvalidOperationException("Photon channel closed.");return owner.inbox.Receive(bytes,out received);}
            internal bool Send(byte[] bytes){owner.Owner();if(!Current||!owner.channelStarted)return false;return owner.Send(Datagram,(byte[])bytes.Clone());}
            public void Dispose(){if(closed)return;owner.Owner();closed=true;owner.channelClosed=true;owner.inbox.Clear();owner.Flush();}
        }
        void Flush(){if(client!=null&&!disposed)client.RealtimePeer.SendOutgoingCommands();}
        void Fail(string reason)
        {
            if(terminal||disposed)return;terminal=true;FailureReason=reason;Status="Photon connection stopped: "+reason;
            offer=null;offerHash=null;claimedHash=null;inbox.Clear();
            if(client!=null)try{Flush();client.Disconnect();}catch{}
        }
        public void Dispose()
        {Owner();if(disposed)return;Fail("closed_locally");disposed=true;client.RemoveCallbackTarget(callbacks);client.Disconnect();}
        internal static string NormalizeRoomCode(string value)
        {
            value=(value??"").Trim().Replace("-","").Replace(" ","").ToUpperInvariant();
            if(value.Length!=8)throw new ArgumentException("Enter the eight-character room code.");
            foreach(char c in value)if("ABCDEFGHJKLMNPQRSTUVWXYZ23456789".IndexOf(c)<0)throw new ArgumentException("Invalid room code character.");
            return value;
        }
        static string NewRoomCode()
        {const string alphabet="ABCDEFGHJKLMNPQRSTUVWXYZ23456789";var bytes=new byte[8];using(var rng=RandomNumberGenerator.Create())rng.GetBytes(bytes);var code=new char[8];for(int i=0;i<8;i++)code[i]=alphabet[bytes[i]%alphabet.Length];return new string(code);}
        static bool Nonce(string value){Guid parsed;return value!=null&&value.Length==32&&Guid.TryParseExact(value,"N",out parsed)&&parsed!=Guid.Empty;}
        static string Hash(string value){using(var sha=SHA256.Create())return Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(value)));}
        sealed class Callbacks:IConnectionCallbacks,IMatchmakingCallbacks,IInRoomCallbacks,IOnEventCallback
        {
            readonly ViveWozPhotonSession owner;internal Callbacks(ViveWozPhotonSession owner){this.owner=owner;}
            public void OnConnected(){}
            public void OnConnectedToMaster(){owner.ConnectedToMaster();}
            public void OnDisconnected(DisconnectCause cause){owner.Fail("disconnected_"+cause);}
            public void OnRegionListReceived(RegionHandler handler){}
            public void OnCustomAuthenticationResponse(Dictionary<string,object> data){}
            public void OnCustomAuthenticationFailed(string message){owner.Fail("authentication_failed");}
            public void OnFriendListUpdate(List<FriendInfo> friends){}
            public void OnCreatedRoom(){}
            public void OnCreateRoomFailed(short code,string message){owner.Fail("create_room_"+code);}
            public void OnJoinedRoom(){owner.JoinedRoom();}
            public void OnJoinRoomFailed(short code,string message){owner.Fail("join_room_"+code);}
            public void OnJoinRandomFailed(short code,string message){owner.Fail("unexpected_random_join");}
            public void OnLeftRoom(){owner.Fail("room_left");}
            public void OnPlayerEnteredRoom(Player player){owner.PlayerEntered(player);}
            public void OnPlayerLeftRoom(Player player){if(player.ActorNumber==owner.peerActor)owner.Fail("peer_left");}
            public void OnRoomPropertiesUpdate(PhotonHashtable properties){}
            public void OnPlayerPropertiesUpdate(Player player,PhotonHashtable properties){}
            public void OnMasterClientSwitched(Player player){owner.Fail("room_owner_changed");}
            public void OnEvent(EventData data){owner.Event(data);}
        }
    }

    // Pure bounded buffer. It has no authority to issue PhotonReceipt objects.
    internal sealed class ViveWozPhotonInbox
    {
        internal const int Capacity=64;
        readonly Queue<Tuple<byte[],double>> queue=new Queue<Tuple<byte[],double>>();
        int actor;double last=-1;
        internal int Count {get{return queue.Count;}}
        internal void Pin(int value){if(value<1||(actor!=0&&actor!=value))throw new InvalidOperationException("Exact peer required.");actor=value;}
        internal bool Enqueue(int sender,byte[] bytes,double at,out string error)
        {
            error=null;if(sender!=actor||actor==0){error="foreign_peer";return false;}
            if(bytes==null||bytes.Length<8||bytes.Length>ViveLegacyWozProcessTransport.MaximumDatagramBytes){error="packet_size";return false;}
            if(!WireCodec.Finite(at)||at<0||at<last){error="receipt_clock";return false;}
            if(queue.Count>=Capacity){error="packet_backlog";return false;}
            last=at;queue.Enqueue(Tuple.Create((byte[])bytes.Clone(),at));return true;
        }
        internal int Receive(byte[] bytes,out double at)
        {var item=queue.Dequeue();at=item.Item2;if(bytes.Length<item.Item1.Length)throw new ArgumentException("Receive buffer too small.");Buffer.BlockCopy(item.Item1,0,bytes,0,item.Item1.Length);return item.Item1.Length;}
        internal void Clear(){while(queue.Count>0){var item=queue.Dequeue();Array.Clear(item.Item1,0,item.Item1.Length);}}
    }
}
