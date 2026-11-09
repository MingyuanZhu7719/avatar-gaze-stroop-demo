#if STROOP_XR_PREVIEW
using System;
using System.Globalization;
using System.Net;
using AvatarStroop.Core;
using AvatarStroop.Runtime;
using UnityEngine;

namespace AvatarStroop.Unity
{
    // Separate engineering-only viewer: no Experiment, TaskSnapshot, trial,
    // source lock, recording or admission to the research application.
    public sealed class ViveWozEngineeringParticipantBootstrap : MonoBehaviour
    {
        ViveWozOperatorDevelopmentRig rig;
        ViveWozEngineeringPeer peer;
        WorldSpaceExperimentView view;
        ViveWozEngineeringXrLease xrLease;
        string status="Not configured. XR and network are off.";
        string session="",participantPeer="",operatorPeer="",participantSource="",operatorClock="",epoch="";
        string localIp="",localPort="",remoteIp="",remotePort="";
        string connectionAge="",trackingAge="",mappedAge="",driftSamples="",driftSpan="",roundTrip="",extrapolation="",driftPpm="",observationAge="";
        bool scope,trust,closed,noOtherXrApps;
        Vector2 scroll;
        public bool NetworkOpen {get{return peer!=null&&!closed;}}
        public bool CanEnterLiveCountdown {get{return false;}}
        public bool CanRecordOrExport {get{return false;}}

        void Awake()
        {
            Application.runInBackground=true;
            var cameraObject=new GameObject("EngineeringParticipantCamera");cameraObject.tag="MainCamera";
            var camera=cameraObject.AddComponent<Camera>();camera.transform.position=new Vector3(-.5f,1.6f,0);
            camera.backgroundColor=new Color(.035f,.045f,.06f);camera.clearFlags=CameraClearFlags.SolidColor;
            camera.nearClipPlane=.05f;camera.farClipPlane=20;camera.fieldOfView=60;
            view=new GameObject("EngineeringClassroom").AddComponent<WorldSpaceExperimentView>();
            view.Initialize(camera,Actor.Participant);
            view.SetViveUnconfiguredPreview("ENGINEERING POSE ONLY - uncalibrated. No study, task, trial or recording.");
            view.ClearAvatarOutput();
            rig=gameObject.AddComponent<ViveWozOperatorDevelopmentRig>();rig.Configure(camera);
        }
        static string Input(string label,string value)
        {GUILayout.BeginHorizontal();GUILayout.Label(label,GUILayout.Width(270));value=GUILayout.TextField(value);GUILayout.EndHorizontal();return value;}
        static double Number(string value){return Double.Parse(value,NumberStyles.Float,CultureInfo.InvariantCulture);}
        void OpenPeer()
        {
            if(peer!=null||closed||!scope)throw new InvalidOperationException("Fresh explicit engineering configuration required.");
            var clock=rig.SharedClock;var mapping=new ViveProEyeRehearsalCoordinates();
            var stream=ViveDevelopmentTransport.EngineeringOperatorStream(mapping.Version,epoch);
            var own=new PeerIdentity(session,participantPeer,PeerRole.Participant,participantSource,ControlSource.LiveOperator,clock.ClockId);
            var other=new PeerIdentity(session,operatorPeer,PeerRole.Operator,stream,ControlSource.LiveOperator,operatorClock);
            var local=new IPEndPoint(IPAddress.Parse(localIp),Int32.Parse(localPort,CultureInfo.InvariantCulture));
            var remote=new IPEndPoint(IPAddress.Parse(remoteIp),Int32.Parse(remotePort,CultureInfo.InvariantCulture));
            bool remoteNetwork=!IPAddress.IsLoopback(local.Address)||!IPAddress.IsLoopback(remote.Address);
            var faults=new DevelopmentFaultConfiguration(Number(connectionAge),Number(trackingAge),Number(mappedAge));
            var clocks=new DevelopmentClockConfiguration(Int32.Parse(driftSamples,CultureInfo.InvariantCulture),Number(driftSpan),Number(roundTrip),Number(extrapolation),Number(driftPpm));
            var config=new ViveWozEngineeringConfiguration(own,other,local,remote,remoteNetwork,
                mapping.Version,epoch,faults,clocks,Number(observationAge));
            peer=ViveWozEngineeringPeer.Start(config,clock,null,true,trust);
            status="Engineering network opened; waiting for usable fresh head and eye.";
        }
        void Update()
        {
            if(xrLease!=null&&rig!=null&&!rig.ViewActive){xrLease.Dispose();xrLease=null;}
            if(peer==null||closed)return;
            try
            {
                peer.TickParticipant();
                var pose=peer.CurrentPose;
                if(pose==null||!PresentEngineeringPose(view,pose.MappedHeadRotation,pose.DerivedMappedEyeDirection,pose.MappingVersion,
                    rig.HeadReady&&!peer.Faulted&&!peer.RecoveryDecisionRequired))
                {view.ClearAvatarOutput();status="Pose unavailable. "+peer.LastIssue;return;}
                status="Live engineering pose received. Mapping is uncalibrated; no research data is being recorded.";
            }
            catch(Exception e){view.ClearAvatarOutput();status="Engineering viewer fault: "+e.GetType().Name;ClosePeer();}
        }
        // The caller must freshly revalidate the peer and HMD every frame. This
        // function alone is not an age check, a study gate or a transport proof.
        public static bool PresentEngineeringPose(WorldSpaceExperimentView target,Rotation head,Vec3 eye,string mapping,bool ready)
        {
                if(target==null)throw new ArgumentNullException("target");
                if(!ready||!head.IsUnit||!eye.IsUnit||String.IsNullOrWhiteSpace(mapping))
                {target.ClearAvatarOutput();return false;}
                var output=new AvatarOutput{AvatarVisible=true,SelectedSource=ControlSource.LiveOperator,
                    ActualSource=ControlSource.LiveOperator,ActualPose=new AvatarPose(head,eye),
                    HeadValid=true,EyeValid=true,MappingVersion=mapping,Status=GazeOutputStatus.SampleAvailable};
                target.ApplyAvatarOutput(output);
                // This separate viewer deliberately has no TaskView; therefore
                // its live engineering layer controls only the avatar root.
                target.AvatarRoot.gameObject.SetActive(true);return true;
        }
        void OnGUI()
        {
            GUILayout.BeginArea(new Rect(10,10,Mathf.Max(1,Mathf.Min(900,Screen.width-20)),Mathf.Max(1,Mathf.Min(680,Screen.height-20))),GUI.skin.box);
            scroll=GUILayout.BeginScrollView(scroll);
            GUILayout.Label("VIVE TWO-PC POSE VIEWER - ENGINEERING ONLY");
            GUILayout.Label("No study, trial or recording. Uncalibrated mapping; endpoint matching is not authentication.");
            GUILayout.Label(status);GUILayout.Label(rig==null?"XR unavailable":rig.Status);
            noOtherXrApps=GUILayout.Toggle(noOtherXrApps,"I closed other XR applications on this computer. SteamVR remains running.");
            GUI.enabled=rig!=null&&!rig.ViewActive&&!closed&&noOtherXrApps;
            if(GUILayout.Button("Start participant XR view"))BeginXr();
            GUI.enabled=rig!=null&&rig.ViewActive;
            if(GUILayout.Button("Stop viewer and network")){ClosePeer();rig.StopView();}
            GUI.enabled=true;
            if(peer==null&&!closed)
            {
                GUILayout.Label("Enter explicit engineering pairing and engineering-only timing values. Match the operator peer; never use these as study thresholds.");
                GUILayout.Label("Manually verify trusted-network unicast endpoints; this app cannot detect every subnet-directed broadcast.");
                GUILayout.Label("Participant clock ID: "+(rig==null||rig.SharedClock==null?"unavailable":rig.SharedClock.ClockId));
                session=Input("Engineering session ID",session);participantPeer=Input("Participant peer ID",participantPeer);operatorPeer=Input("Operator peer ID",operatorPeer);
                participantSource=Input("Participant source ID",participantSource);operatorClock=Input("Operator clock ID",operatorClock);epoch=Input("Fresh engineering epoch",epoch);
                localIp=Input("Participant local IP",localIp);localPort=Input("Participant local port",localPort);remoteIp=Input("Operator IP",remoteIp);remotePort=Input("Operator port",remotePort);
                connectionAge=Input("Connection stale seconds",connectionAge);trackingAge=Input("Tracking stale seconds",trackingAge);mappedAge=Input("Maximum mapped age seconds",mappedAge);
                driftSamples=Input("Clock minimum drift samples",driftSamples);driftSpan=Input("Clock minimum span seconds",driftSpan);roundTrip=Input("Clock maximum round trip seconds",roundTrip);
                extrapolation=Input("Clock maximum extrapolation seconds",extrapolation);driftPpm=Input("Clock unmodeled drift ppm",driftPpm);
                observationAge=Input("Maximum observation age seconds",observationAge);
                scope=GUILayout.Toggle(scope,"I understand this is an uncalibrated, non-study engineering viewer.");
                trust=GUILayout.Toggle(trust,"I trust the configured network and peer; this is not authentication.");
                GUILayout.Label("Network disabled until an authenticated, encrypted transport is available.");
                GUI.enabled=false;
                if(GUILayout.Button("Open engineering network")){try{OpenPeer();}catch(Exception e){status="Network not opened: "+e.Message;}}
                GUI.enabled=true;
            }
            GUILayout.EndScrollView();GUILayout.EndArea();
        }
        void BeginXr()
        {
            // Cooperative mutex covers our two viewers only. Detect other known
            // project players by name; unrelated/renamed XR apps still require
            // the explicit user check above.
            string[] known={"AvatarStroopViveEyePlayer","AvatarStroopViveWozOperatorDevelopment",
                "AvatarStroopViveWozEngineeringOperator","AvatarStroopViveWozEngineeringParticipant",
                "AvatarStroopParticipant","AvatarStroopQuestPreview","AvatarStroopViveParticipant"};
            try
            {
                int own=System.Diagnostics.Process.GetCurrentProcess().Id;
                foreach(var name in known)
                    foreach(var process in System.Diagnostics.Process.GetProcessesByName(name))
                        using(process){if(process.Id!=own){status="Close the other project player before starting XR: "+name;return;}}
                string error;xrLease=ViveWozEngineeringXrLease.TryAcquire(out error);
                if(xrLease==null){status=error;return;}
                rig.BeginView();
            }
            catch(Exception e)
            {
                if(xrLease!=null&&!rig.ViewActive){xrLease.Dispose();xrLease=null;}
                status="XR start blocked: "+e.GetType().Name;
            }
        }
        void ClosePeer(){if(closed)return;closed=true;if(view!=null)view.ClearAvatarOutput();if(peer!=null)peer.Dispose();peer=null;}
        void OnDestroy()
        {
            ClosePeer();
            // Never release while a loader could still be active. If destroyed
            // during shutdown, the process owns the mutex until it exits.
            if(xrLease!=null&&rig!=null&&!rig.ViewActive){xrLease.Dispose();xrLease=null;}
        }
    }
}
#endif
