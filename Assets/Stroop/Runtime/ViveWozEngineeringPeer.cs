using System;
using System.Net;
using System.Threading;
using AvatarStroop.Core;

namespace AvatarStroop.Runtime
{
    // An explicit, separate, no-task engineering pair. Endpoint equality is
    // not authentication. No source here is eligible for experiment admission.
    public sealed class ViveWozEngineeringConfiguration
    {
        readonly IPEndPoint local,remote;
        public PeerIdentity Own {get;private set;}
        public PeerIdentity Expected {get;private set;}
        public IPEndPoint Local {get{return new IPEndPoint(local.Address,local.Port);}}
        public IPEndPoint Remote {get{return new IPEndPoint(remote.Address,remote.Port);}}
        public bool AllowRemote {get;private set;}
        public string MappingVersion {get;private set;}
        public string Epoch {get;private set;}
        public DevelopmentFaultConfiguration Faults {get;private set;}
        public DevelopmentClockConfiguration Clocks {get;private set;}
        public double MaximumLocalObservationAge {get;private set;}
        public ViveWozEngineeringConfiguration(PeerIdentity own,PeerIdentity expected,IPEndPoint local,IPEndPoint remote,
            bool allowRemote,string mappingVersion,string epoch,DevelopmentFaultConfiguration faults,DevelopmentClockConfiguration clocks,double maximumLocalObservationAge)
        {
            if(own==null||expected==null||local==null||remote==null||faults==null||clocks==null)throw new ArgumentNullException("Explicit peers, endpoints and policies are required");
            if(own.SessionId!=expected.SessionId||own.Role==expected.Role||own.PeerId==expected.PeerId||own.ClockId==expected.ClockId||
                own.Source!=ControlSource.LiveOperator||expected.Source!=ControlSource.LiveOperator)throw new ArgumentException("Distinct engineering peers required");
            if(String.IsNullOrWhiteSpace(epoch)||mappingVersion!=ViveProEyeRehearsalCoordinates.MappingVersion||
                (own.Role==PeerRole.Operator?own.SourceId:expected.SourceId)!=ViveDevelopmentTransport.EngineeringOperatorStream(mappingVersion,epoch))
                throw new ArgumentException("An explicit engineering-only stream and compatible preview mapping are required");
            if(local.Port==0||remote.Port==0||local.Equals(remote)||local.AddressFamily!=remote.AddressFamily||
                !IsUnicast(local.Address)||!IsUnicast(remote.Address))
                throw new ArgumentException("Distinct explicitly addressed endpoints are required");
            if(!allowRemote&&(!IPAddress.IsLoopback(local.Address)||!IPAddress.IsLoopback(remote.Address)))throw new ArgumentException("Remote networking requires explicit opt-in");
            if(!WireCodec.Finite(maximumLocalObservationAge)||maximumLocalObservationAge<=0)throw new ArgumentOutOfRangeException("maximumLocalObservationAge");
            Own=own;Expected=expected;this.local=new IPEndPoint(local.Address,local.Port);this.remote=new IPEndPoint(remote.Address,remote.Port);
            AllowRemote=allowRemote;MappingVersion=mappingVersion;Epoch=epoch;Faults=faults;Clocks=clocks;MaximumLocalObservationAge=maximumLocalObservationAge;
        }
        static bool IsUnicast(IPAddress address)
        {
            if(address.Equals(IPAddress.Any)||address.Equals(IPAddress.IPv6Any)||address.Equals(IPAddress.None)||address.Equals(IPAddress.IPv6None))return false;
            if(address.AddressFamily==System.Net.Sockets.AddressFamily.InterNetworkV6)return !address.IsIPv6Multicast;
            return address.AddressFamily==System.Net.Sockets.AddressFamily.InterNetwork&&address.GetAddressBytes()[0]<224;
        }
    }

    // Single-owner update loop. No experiment, task, trial, session file,
    // source fallback or implicit XR initialization exists in this class.
    public sealed class ViveWozEngineeringPeer : IDisposable
    {
        readonly ViveDevelopmentTransport transport;
        readonly ViveProEyeHeadEyeSource source;
        readonly PeerRole role;
        readonly int owner=Thread.CurrentThread.ManagedThreadId;
        bool closed,published;
        public bool Faulted {get;private set;}
        public bool RecoveryDecisionRequired {get;private set;}
        public string LastIssue {get;private set;}
        public ViveDevelopmentRemotePose CurrentPose {get;private set;}
        public bool CanEnterLiveCountdown {get{return false;}}
        public bool CanRecordOrExport {get{return false;}}
        public bool ApparatusMappingValidated {get{return false;}}
        public bool CryptographicallyAuthenticated {get{return false;}}

        ViveWozEngineeringPeer(ViveWozEngineeringConfiguration config,IClock clock,IViveProEyeProvider provider,ViveInMemoryDatagramSocket injected)
        {
            if(injected==null||config.AllowRemote||!injected.LocalEndpoint.Equals(config.Local))throw new InvalidOperationException("A sealed in-memory fixture is required");
            role=config.Own.Role;
            if(clock.ClockId!=config.Own.ClockId)throw new ArgumentException("Configured clock must match the actual clock");
            if(role==PeerRole.Operator&&provider==null||role==PeerRole.Participant&&provider!=null)throw new ArgumentException("Only the operator supplies a live provider");
            if(provider is ViveProEyeWorker)throw new InvalidOperationException("A real eye worker cannot enter an unauthenticated fixture");
            if(role==PeerRole.Operator)source=new ViveProEyeHeadEyeSource(config.Own.SessionId,0,0,config.Own.SourceId,clock,provider,new ViveProEyeRehearsalCoordinates(),config.MaximumLocalObservationAge);
            try
            {
                transport=ViveDevelopmentTransport.CreateInMemoryFixture(injected,config.Remote,clock,config.Own,config.Expected,"vive-pro-eye-no-serial",config.MappingVersion,
                    config.Epoch,config.Faults,config.Clocks,config.MaximumLocalObservationAge,null,true);
            }
            catch{injected.Dispose();throw;}
        }
        public static ViveWozEngineeringPeer Start(ViveWozEngineeringConfiguration config,IClock clock,IViveProEyeProvider provider,bool explicitDevelopment,bool trustedRemoteConfirmed)
        {
            if(config==null||clock==null)throw new ArgumentNullException("config/clock");
            if(!explicitDevelopment)throw new InvalidOperationException("Explicit engineering-only confirmation required");
            if(config.AllowRemote&&!trustedRemoteConfirmed)throw new InvalidOperationException("Explicit trusted network confirmation required; endpoint matching is not authentication");
            throw new InvalidOperationException("Unauthenticated VIVE pose networking is disabled, including loopback");
        }
        internal static ViveWozEngineeringPeer StartSynthetic(ViveWozEngineeringConfiguration config,IClock clock,IViveProEyeProvider provider,ViveInMemoryDatagramSocket socket)
        {return new ViveWozEngineeringPeer(config,clock,provider,socket);}
        void Require(PeerRole expected)
        {
            if(Thread.CurrentThread.ManagedThreadId!=owner)throw new InvalidOperationException("Engineering peer belongs to its creating thread");
            if(closed||role!=expected)throw new InvalidOperationException("Open peer with matching role required");
        }
        public void TickOperator(bool sourceReady)
        {
            Require(PeerRole.Operator);if(Faulted||RecoveryDecisionRequired)return;
            try
            {
                transport.Tick();
                if(transport.RecoveryDecisionRequired){RecoveryDecisionRequired=true;LastIssue="peer_recovery_required";Invalidate();return;}
                HeadEyeSample sample;
                if(!sourceReady||!source.TryRead(out sample)||sample==null||source.LastObservation==null)
                {LastIssue="fresh_head_and_eye_unavailable";Invalidate();return;}
                if(!transport.SendObservation(source.LastObservation)){LastIssue=transport.LastSendRejection;Invalidate();return;}
                published=true;LastIssue=null;
            }
            catch(Exception e){Faulted=true;LastIssue="operator_peer_fault:"+e.GetType().Name;Invalidate();}
        }
        public void TickParticipant()
        {
            Require(PeerRole.Participant);CurrentPose=null;if(Faulted||RecoveryDecisionRequired)return;
            try
            {
                transport.Tick();
                if(transport.RecoveryDecisionRequired){RecoveryDecisionRequired=true;LastIssue="peer_recovery_required";return;}
                CurrentPose=transport.CurrentPose(0,0);
                LastIssue=CurrentPose==null?"fresh_operator_pose_unavailable":null;
            }
            catch(Exception e){Faulted=true;CurrentPose=null;LastIssue="participant_peer_fault:"+e.GetType().Name;}
        }
        void Invalidate()
        {if(!published)return;try{if(transport.InvalidateLastPublishedPose())published=false;}catch(Exception){Faulted=true;}}
        public void Dispose()
        {
            if(closed)return;
            if(Thread.CurrentThread.ManagedThreadId!=owner)throw new InvalidOperationException("Engineering peer belongs to its creating thread");
            CurrentPose=null;if(role==PeerRole.Operator)Invalidate();closed=true;transport.Dispose();
        }
    }
}
