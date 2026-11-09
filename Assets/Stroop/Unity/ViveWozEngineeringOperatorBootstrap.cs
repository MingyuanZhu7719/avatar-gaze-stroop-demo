#if STROOP_XR_PREVIEW
using System;
using System.Globalization;
using System.Net;
using System.Diagnostics;
using AvatarStroop.Core;
using AvatarStroop.Runtime;
using UnityEngine;

namespace AvatarStroop.Unity
{
    // A separate pose-only engineering sender. It does not create an
    // Experiment, task, trial, recording, export, or preflight decision.
    public sealed class ViveWozEngineeringOperatorBootstrap : MonoBehaviour
    {
        ViveWozOperatorDevelopmentRig rig;
        ViveWozEngineeringPeer peer;
        ViveProEyeWorker worker;
        ViveWozEngineeringXrLease xrLease;
        bool closed,developmentScope,trustedNetwork,otherXrClosed,networkAttempted;
        string status="XR, eye worker and network are off.";
        string session="",operatorPeer="",participantPeer="",participantSource="",participantClock="",epoch="";
        string localIp="",localPort="",remoteIp="",remotePort="";
        string connectionAge="",trackingAge="",mappedAge="",driftSamples="",driftSpan="",roundTrip="",extrapolation="",driftPpm="",observationAge="";
        Vector2 scroll;
        public bool NetworkOpen {get{return peer!=null&&!closed;}}
        public bool CanEnterLiveCountdown {get{return false;}}
        public bool CanRecordOrExport {get{return false;}}

        void Awake()
        {
            Application.runInBackground=true;
            var cameraObject=new GameObject("EngineeringOperatorCamera");
            cameraObject.tag="MainCamera";
            var camera=cameraObject.AddComponent<Camera>();
            camera.transform.position=new Vector3(0,1.6f,0);
            camera.backgroundColor=new Color(.035f,.045f,.06f);
            camera.clearFlags=CameraClearFlags.SolidColor;camera.nearClipPlane=.05f;camera.farClipPlane=20;
            var marker=new GameObject("EngineeringOnlyNotice");
            marker.transform.position=new Vector3(0,1.5f,2f);
            var text=marker.AddComponent<TextMesh>();
            text.text="ENGINEERING POSE ONLY\nNo task or recording\nMapping uncalibrated";
            text.anchor=TextAnchor.MiddleCenter;text.alignment=TextAlignment.Center;text.characterSize=.04f;text.fontSize=40;
            rig=gameObject.AddComponent<ViveWozOperatorDevelopmentRig>();rig.Configure(camera);
        }
        static string Input(string label,string value)
        {GUILayout.BeginHorizontal();GUILayout.Label(label,GUILayout.Width(270));value=GUILayout.TextField(value);GUILayout.EndHorizontal();return value;}
        static double Number(string value){return Double.Parse(value,NumberStyles.Float,CultureInfo.InvariantCulture);}

        // This is called only from the operator's explicit network button.
        // The endpoints and declarations identify peers, but do not
        // authenticate them. There is no automatic retry or task admission.
        void StartEngineeringPeer()
        {
            if(closed||peer!=null||rig==null||!rig.HeadReady||rig.Worker==null||!rig.Worker.Started)
                throw new InvalidOperationException("A focused XR view and started eye worker are required.");
            if(!developmentScope)throw new InvalidOperationException("Confirm the engineering-only scope.");
            var mapper=new ViveProEyeRehearsalCoordinates();var clock=rig.SharedClock;
            string stream=ViveDevelopmentTransport.EngineeringOperatorStream(mapper.Version,epoch);
            var own=new PeerIdentity(session,operatorPeer,PeerRole.Operator,stream,ControlSource.LiveOperator,clock.ClockId);
            var other=new PeerIdentity(session,participantPeer,PeerRole.Participant,participantSource,ControlSource.LiveOperator,participantClock);
            var local=new IPEndPoint(IPAddress.Parse(localIp),Int32.Parse(localPort,CultureInfo.InvariantCulture));
            var remote=new IPEndPoint(IPAddress.Parse(remoteIp),Int32.Parse(remotePort,CultureInfo.InvariantCulture));
            bool remoteNetwork=!IPAddress.IsLoopback(local.Address)||!IPAddress.IsLoopback(remote.Address);
            if(remoteNetwork&&!trustedNetwork)throw new InvalidOperationException("Explicit trusted-network confirmation is required.");
            var faults=new DevelopmentFaultConfiguration(Number(connectionAge),Number(trackingAge),Number(mappedAge));
            var clocks=new DevelopmentClockConfiguration(Int32.Parse(driftSamples,CultureInfo.InvariantCulture),Number(driftSpan),Number(roundTrip),Number(extrapolation),Number(driftPpm));
            var config=new ViveWozEngineeringConfiguration(own,other,local,remote,remoteNetwork,mapper.Version,epoch,faults,clocks,Number(observationAge));
            worker=rig.Worker;
            networkAttempted=true;
            peer=ViveWozEngineeringPeer.Start(config,clock,worker,true,trustedNetwork);
            status="Engineering-only peer started. It sends no task or recorded data; headset mapping is unvalidated.";
        }
        void Update()
        {
            // Both engineering viewers use the same process-local-session mutex.
            // It does not prove that unrelated XR applications are absent.
            if(xrLease!=null&&rig!=null&&!rig.ViewActive)ReleaseXrLease();
            if(peer==null||closed)return;
            try
            {
                peer.TickOperator(rig!=null&&rig.HeadReady&&worker!=null&&worker.Started);
                if(peer.Faulted||peer.RecoveryDecisionRequired)
                {status="Engineering peer stopped: "+peer.LastIssue+". A new explicit peer epoch is required.";ClosePeer();}
                else if(peer.LastIssue!=null)status="Engineering peer: "+peer.LastIssue;
                else status="Engineering pose sender active. Receiver delivery and apparatus alignment are not verified.";
            }
            catch(Exception error){status="Engineering peer failed: "+error.GetType().Name;ClosePeer();}
        }
        void BeginExclusiveView()
        {
            if(xrLease!=null||rig==null||rig.ViewActive)return;
            try
            {
                if(!otherXrClosed)throw new InvalidOperationException("Confirm that other XR applications have been closed.");
                int ownProcess=Process.GetCurrentProcess().Id;
                foreach(var process in Process.GetProcesses())
                {
                    using(process)
                    {
                        if(process.Id!=ownProcess&&process.ProcessName.StartsWith("AvatarStroop",StringComparison.OrdinalIgnoreCase))
                            throw new InvalidOperationException("Another AvatarStroop process is running. Close it before starting this engineering view.");
                    }
                }
                string leaseError;
                xrLease=ViveWozEngineeringXrLease.TryAcquire(out leaseError);
                if(xrLease==null)throw new InvalidOperationException(leaseError);
                rig.BeginView();
            }
            catch(Exception error)
            {
                if(xrLease!=null&&(rig==null||!rig.ViewActive))ReleaseXrLease();
                status="XR view not started: "+error.Message;
            }
        }
        void ReleaseXrLease()
        {
            if(xrLease!=null){xrLease.Dispose();xrLease=null;}
        }
        void OnGUI()
        {
            GUILayout.BeginArea(new Rect(10,10,Mathf.Max(1,Mathf.Min(900,Screen.width-20)),Mathf.Max(1,Mathf.Min(650,Screen.height-20))),GUI.skin.box);
            scroll=GUILayout.BeginScrollView(scroll);
            GUILayout.Label("VIVE WOZ POSE REHEARSAL — ENGINEERING ONLY");
            GUILayout.Label("No experiment, trials, preflight or recording. Head/eye mapping is uncalibrated. Endpoint matching is not authentication.");
            GUILayout.Label(status);GUILayout.Label(rig==null?"XR rig unavailable":rig.Status);
            otherXrClosed=GUILayout.Toggle(otherXrClosed,"I have closed other XR applications (SteamVR can remain running).");
            GUI.enabled=rig!=null&&!rig.ViewActive&&!closed;
            if(GUILayout.Button("Start operator XR view"))BeginExclusiveView();
            GUI.enabled=rig!=null&&rig.HeadReady&&rig.Worker==null&&!closed;
            if(GUILayout.Button("Start operator eye worker"))rig.BeginEye();
            GUI.enabled=rig!=null&&rig.ViewActive;
            if(GUILayout.Button("Stop view and engineering peer")){ClosePeer();rig.StopView();}
            GUI.enabled=true;
            if(!closed&&peer==null)
            {
                GUILayout.Label("Enter matching peer configuration and engineering timeouts; these are not study thresholds.");
                GUILayout.Label("Manually verify trusted-network unicast endpoints; this app cannot detect every subnet-directed broadcast.");
                GUILayout.Label("Operator clock ID: "+(rig==null||rig.SharedClock==null?"unavailable":rig.SharedClock.ClockId));
                session=Input("Session ID",session);operatorPeer=Input("Operator peer ID",operatorPeer);participantPeer=Input("Participant peer ID",participantPeer);
                participantSource=Input("Participant source ID",participantSource);participantClock=Input("Participant clock ID",participantClock);epoch=Input("Fresh peer epoch",epoch);
                localIp=Input("Operator local IP",localIp);localPort=Input("Operator local port",localPort);remoteIp=Input("Participant IP",remoteIp);remotePort=Input("Participant port",remotePort);
                connectionAge=Input("Connection stale seconds",connectionAge);trackingAge=Input("Tracking stale seconds",trackingAge);mappedAge=Input("Maximum mapped age seconds",mappedAge);
                driftSamples=Input("Clock minimum drift samples (>=3)",driftSamples);driftSpan=Input("Clock minimum span seconds",driftSpan);roundTrip=Input("Clock maximum round trip seconds",roundTrip);
                extrapolation=Input("Clock maximum extrapolation seconds",extrapolation);driftPpm=Input("Clock unmodeled drift ppm",driftPpm);
                observationAge=Input("Maximum local observation age seconds",observationAge);
                developmentScope=GUILayout.Toggle(developmentScope,"I understand this is an uncalibrated engineering-only pose rehearsal.");
                trustedNetwork=GUILayout.Toggle(trustedNetwork,"I explicitly trust the configured remote network and peer (this is not authentication).");
                GUILayout.Label("Network disabled until an authenticated, encrypted transport is available.");
                GUI.enabled=false;
                if(GUILayout.Button("Start engineering pose network"))
                {try{StartEngineeringPeer();}catch(Exception error){status="Peer start failed: "+error.Message;if(networkAttempted)ClosePeer();}}
                GUI.enabled=true;
            }
            GUILayout.EndScrollView();GUILayout.EndArea();
        }
        void ClosePeer()
        {
            if(closed)return;
            closed=true;
            if(peer!=null){try{peer.Dispose();}catch(Exception error){status="Peer cleanup failed: "+error.GetType().Name;}}
            peer=null;
        }
        void OnDestroy(){ClosePeer();if(rig==null||!rig.ViewActive)ReleaseXrLease();}
    }
}
#endif
