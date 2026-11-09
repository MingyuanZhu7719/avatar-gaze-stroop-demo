#if STROOP_XR_PREVIEW
using System;
using System.Globalization;
using System.Net;
using AvatarStroop.Core;
using AvatarStroop.Runtime;
using UnityEngine;

namespace AvatarStroop.Unity
{
    // Development-only operator window. Binding never opens a socket, starts XR,
    // starts a worker, records data, or confirms a source. The application that
    // owns the worker must supply the same monotonic clock to both components.
    public sealed class ViveWozOperatorDevelopmentBootstrap : MonoBehaviour
    {
        ViveDevelopmentPeerConfiguration configuration;

        ViveProEyeWorker worker;

        ViveDevelopmentAppAdapter adapter;
        ViveDevelopmentPreflightChannel preflight;
        long displayedSequence=-1;
        double displayedReceipt;
        bool taskVisible;
        bool bound, closed, trustedNetwork;
        string status="Not configured. XR, eye tracking and network are off. Live preflight is unavailable.";
        WorldSpaceExperimentView view;
        ViveWozOperatorDevelopmentRig rig;
        Vector2 scroll;
        bool developmentScope;
        string session="",operatorPeer="",participantPeer="",participantSource="",participantClock="",epoch="",localIp="",localPort="",remoteIp="",remotePort="";
        string connectionAge="",trackingAge="",mappedAge="",driftSamples="",driftSpan="",roundTrip="",extrapolation="",driftPpm="",observationAge="",retrySeconds="";

        public bool Configured {get{return bound;}}
        public bool NetworkOpen {get{return adapter!=null&&adapter.IsOpen;}}
        public bool PreflightReady {get{return false;}}
        public bool CanEnterLiveCountdown {get{return false;}}
        public bool CanRecordOrExport {get{return false;}}
        public long DisplayedPacketSequence {get{return taskVisible?displayedSequence:-1;}}
        public double? DisplayedLocalReceiptSeconds {get{return taskVisible?(double?)displayedReceipt:null;}}
        public string Status {get{return status;}}

        // The explicit owner config is validated by ViveDevelopmentPeerConfiguration.
        // This method has no device, network, capture or source-start side effects.
        public void Bind(ViveDevelopmentPeerConfiguration config,IClock sharedClock,ViveProEyeWorker existingWorker,bool ownerConfirmedDevelopmentScope)
        {
            if(!ownerConfirmedDevelopmentScope)throw new InvalidOperationException("Explicit development scope confirmation is required.");
            if(bound||closed)throw new InvalidOperationException("A new window and peer epoch are required.");
            if(config==null||sharedClock==null||existingWorker==null)throw new ArgumentNullException("Explicit operator configuration, clock and worker are required.");
            if(config.Role!=PeerRole.Operator||config.Own.ClockId!=sharedClock.ClockId||!existingWorker.UsesClock(sharedClock)||
                rig==null||!object.ReferenceEquals(rig.Worker,existingWorker)||!object.ReferenceEquals(rig.SharedClock,sharedClock))
                throw new ArgumentException("Operator role and the worker's actual shared clock are required.");
            var mapper=new ViveProEyeRehearsalCoordinates();
            if(config.MappingVersion!=mapper.Version||config.OperatorDeviceId!="vive-pro-eye-no-serial")
                throw new ArgumentException("The explicitly configured development mapping and device label must match the source.");
            var nextFactory=new ViveWozOperatorSourceFactory(config.Own.SessionId,config.Epoch,sharedClock,existingWorker,mapper,config.MaximumObservationAge,true);
            var nextAdapter=new ViveDevelopmentAppAdapter(config,sharedClock,null,nextFactory.Create);
            configuration=config;worker=existingWorker;adapter=nextAdapter;bound=true;
            status="Configured, not connected. Preflight proof is not connected; Live countdown remains blocked.";
        }
        // Invoke only after owner clicks an explicit network action. Matching an
        // endpoint is not authentication; trust selection must be independent.
        public void StartNetwork(bool ownerAction,bool trustedRemoteConfirmed)
        {
            if(!bound||closed)throw new InvalidOperationException("Explicit configuration is required.");
            adapter.StartNetwork(ownerAction,trustedRemoteConfirmed);
            status="Network started; source not confirmed. Preflight readiness remains unavailable.";
        }
        public void ConfirmLiveSource(bool ownerAction)
        {
            if(!bound||closed)throw new InvalidOperationException("Explicit configuration is required.");
            adapter.ConfirmLiveSource(ownerAction);
            status="Development source confirmed locally; participant admission remains blocked.";
        }
        // Creates a dormant channel using the control transport clock mapping.
        // Source lock is not established by wire v2; it is never started here.
        public void PreparePreflight(VivePreflightEndpointConfiguration config)
        {
            if(adapter==null||!adapter.IsOpen||preflight!=null)throw new InvalidOperationException("An open peer and unused preflight epoch are required.");
            preflight=adapter.CreatePreflightChannel(config);
        }
        public void RequestPause()
        {
            if(adapter==null||!adapter.IsOpen)return;
            status=adapter.RequestRemotePause()?"Pause requested; waiting for participant acknowledgement.":"Pause not accepted; participant state is unknown.";
        }
        void Awake()
        {
            Application.runInBackground=true;
            var cameraObject=new GameObject("OperatorDevelopmentCamera");
            cameraObject.tag="MainCamera";
            var camera=cameraObject.AddComponent<Camera>();
            camera.transform.position=new Vector3(0,1.6f,0);
            camera.backgroundColor=new Color(.035f,.045f,.06f);camera.clearFlags=CameraClearFlags.SolidColor;
            camera.nearClipPlane=.05f;camera.farClipPlane=20;camera.fieldOfView=60;
            var root=new GameObject("OperatorDevelopmentView");
            view=root.AddComponent<WorldSpaceExperimentView>();view.Initialize(camera,Actor.Operator);
            view.ApplyTaskView(ViveWozOperatorTaskView.FromFreshTask(null));
            view.SetFaults(new[]{"DEVELOPMENT ONLY - task unavailable. Live gate closed."});
            rig=gameObject.AddComponent<ViveWozOperatorDevelopmentRig>();rig.Configure(camera);
        }
        void Update()
        {
            if(adapter==null||!adapter.IsOpen)return;
            try
            {
                // Worker.Started establishes native initialization only. The
                // source itself validates each head/eye observation and age.
                adapter.TickOperator(worker!=null&&worker.Started);
                TaskSnapshot task=null;long sequence=-1;double receipt=0;
                bool fresh=!adapter.Faulted&&!adapter.RecoveryDecisionRequired&&adapter.TryGetOperatorTaskEvidence(out task,out sequence,out receipt);
                if(fresh)
                {
                    if(!taskVisible||sequence!=displayedSequence){view.ApplyTaskView(ViveWozOperatorTaskView.FromFreshTask(task));displayedSequence=sequence;displayedReceipt=receipt;taskVisible=true;}
                    view.SetFaults(new[]{"DEVELOPMENT ONLY - participant avatar is a static placeholder; source lock is unverified; Live gate closed."});
                }
                else ClearTask();
                if(adapter.Faulted||adapter.RecoveryDecisionRequired)
                    status="Peer fault/recovery: "+adapter.LastIssue+". New explicit epoch required; no automatic resume.";
                else if(adapter.SafetyStopAcknowledged)status="Participant acknowledged pause. Manual recovery remains required.";
                else if(adapter.SafetyStopPending)status="Pause requested; participant acknowledgement pending.";
                else if(adapter.LastIssue!=null)status="Development peer: "+adapter.LastIssue+". Live preflight remains unavailable.";
            }
            catch(Exception e){status="Operator tick failed: "+e.GetType().Name+". Live gate remains closed.";Close();}
        }
        void ClearTask()
        {
            if(taskVisible){view.ApplyTaskView(ViveWozOperatorTaskView.FromFreshTask(null));taskVisible=false;displayedSequence=-1;}
            view.SetFaults(new[]{"DEVELOPMENT ONLY - task unavailable or stale. Live gate closed."});
        }
        static string Input(string label,string value)
        {GUILayout.BeginHorizontal();GUILayout.Label(label,GUILayout.Width(270));value=GUILayout.TextField(value);GUILayout.EndHorizontal();return value;}
        static double Number(string value){return Double.Parse(value,NumberStyles.Float,CultureInfo.InvariantCulture);}
        void ConfigureFromOwnerForm()
        {
            if(rig==null||rig.Worker==null||!rig.Worker.Started||!rig.HeadReady)throw new InvalidOperationException("A focused operator head and manually started eye worker are required.");
            var ownClock=rig.SharedClock;var mapper=new ViveProEyeRehearsalCoordinates();
            string stream=ViveDevelopmentTransport.OperatorStream(mapper.Version,epoch);
            var own=new PeerIdentity(session,operatorPeer,PeerRole.Operator,stream,ControlSource.LiveOperator,ownClock.ClockId);
            var other=new PeerIdentity(session,participantPeer,PeerRole.Participant,participantSource,ControlSource.LiveOperator,participantClock);
            var local=new IPEndPoint(IPAddress.Parse(localIp),Int32.Parse(localPort,CultureInfo.InvariantCulture));
            var remote=new IPEndPoint(IPAddress.Parse(remoteIp),Int32.Parse(remotePort,CultureInfo.InvariantCulture));
            bool remoteNetwork=!IPAddress.IsLoopback(local.Address)||!IPAddress.IsLoopback(remote.Address);
            var faults=new DevelopmentFaultConfiguration(Number(connectionAge),Number(trackingAge),Number(mappedAge));
            var clocks=new DevelopmentClockConfiguration(Int32.Parse(driftSamples,CultureInfo.InvariantCulture),Number(driftSpan),Number(roundTrip),Number(extrapolation),Number(driftPpm));
            var config=new ViveDevelopmentPeerConfiguration(PeerRole.Operator,own,other,local,remote,remoteNetwork,"vive-pro-eye-no-serial",mapper.Version,epoch,faults,clocks,Number(observationAge),Number(retrySeconds));
            Bind(config,ownClock,rig.Worker,developmentScope);
        }
        void OnGUI()
        {
            GUILayout.BeginArea(new Rect(10,10,Mathf.Max(1,Mathf.Min(900,Screen.width-20)),Mathf.Max(1,Mathf.Min(600,Screen.height-20))),GUI.skin.box);
            scroll=GUILayout.BeginScrollView(scroll);
            GUILayout.Label("VIVE OPERATOR - DEVELOPMENT ONLY");
            GUILayout.Label("Fresh task snapshots are display only. Participant avatar is static; source lock and readiness proof are not connected. Wire v2 is not research eye data. Endpoint matching is not authentication.");
            GUILayout.Label(status);
            GUILayout.Label(rig==null?"XR rig unavailable":rig.Status);
            GUI.enabled=rig!=null&&!rig.ViewActive&&!closed;
            if(GUILayout.Button("Start operator XR view"))rig.BeginView();
            GUI.enabled=rig!=null&&rig.HeadReady&&rig.Worker==null&&!closed;
            if(GUILayout.Button("Start operator eye worker"))rig.BeginEye();
            GUI.enabled=rig!=null&&rig.ViewActive;
            if(GUILayout.Button("Stop operator view and peer")){Close();rig.StopView();}
            GUI.enabled=true;
            if(!bound&&!closed)
            {
                GUILayout.Label("Explicit development pairing (engineering timeouts only; not study thresholds). The participant must use the same session, epoch and matching identities.");
                GUILayout.Label("Operator clock ID: "+(rig==null||rig.SharedClock==null?"unavailable":rig.SharedClock.ClockId));
                session=Input("Session ID",session);operatorPeer=Input("Operator peer ID",operatorPeer);participantPeer=Input("Participant peer ID",participantPeer);
                participantSource=Input("Participant source ID",participantSource);participantClock=Input("Participant clock ID",participantClock);epoch=Input("Fresh peer epoch",epoch);
                localIp=Input("Operator local IP",localIp);localPort=Input("Operator local port",localPort);remoteIp=Input("Participant IP",remoteIp);remotePort=Input("Participant port",remotePort);
                connectionAge=Input("Connection stale seconds",connectionAge);trackingAge=Input("Tracking stale seconds",trackingAge);mappedAge=Input("Maximum mapped age seconds",mappedAge);
                driftSamples=Input("Clock minimum drift samples (>=3)",driftSamples);driftSpan=Input("Clock minimum span seconds",driftSpan);roundTrip=Input("Clock maximum round trip seconds",roundTrip);
                extrapolation=Input("Clock maximum extrapolation seconds",extrapolation);driftPpm=Input("Clock unmodeled drift ppm",driftPpm);
                observationAge=Input("Maximum observation age seconds",observationAge);retrySeconds=Input("Invalidation retry seconds",retrySeconds);
                developmentScope=GUILayout.Toggle(developmentScope,"I understand this is development-only and not a study or authenticated connection");
                GUI.enabled=developmentScope&&rig!=null&&rig.HeadReady&&rig.Worker!=null&&rig.Worker.Started;
                if(GUILayout.Button("Confirm development configuration")){try{ConfigureFromOwnerForm();}catch(Exception e){status="Configuration rejected: "+e.Message;}}
                GUI.enabled=true;
            }
            if(bound&&configuration.AllowRemoteNetwork)trustedNetwork=GUILayout.Toggle(trustedNetwork,"I explicitly trust this configured development network and remote peer (not authentication)");
            GUILayout.Label("Network disabled until an authenticated, encrypted transport is available.");
            GUI.enabled=false;
            if(GUILayout.Button("Start configured development network"))
            {try{StartNetwork(true,trustedNetwork);}catch(Exception e){status="Network start failed: "+e.Message;}}
            GUI.enabled=adapter!=null&&adapter.IsOpen&&!adapter.HasExplicitConfirmation&&!adapter.Faulted&&!adapter.RecoveryDecisionRequired;
            if(GUILayout.Button("Confirm local development source"))
            {try{ConfirmLiveSource(true);}catch(Exception e){status="Source confirmation failed: "+e.Message;}}
            GUI.enabled=adapter!=null&&adapter.IsOpen;
            if(GUILayout.Button("Request participant pause"))RequestPause();
            GUI.enabled=true;
            GUILayout.EndScrollView();GUILayout.EndArea();
        }
        void Close(){if(closed)return;closed=true;if(view!=null)ClearTask();if(preflight!=null)preflight.Dispose();if(adapter!=null)adapter.Dispose();}
        void OnDestroy(){Close();}
    }
}
#endif






