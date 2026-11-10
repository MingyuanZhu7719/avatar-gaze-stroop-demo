#if STROOP_XR_PREVIEW
using System;
using System.Globalization;
using System.Net;
using AvatarStroop.Core;
using AvatarStroop.Runtime;
using UnityEngine;

namespace AvatarStroop.Unity
{
    // Actual operator scene. Old unauthenticated adapter controls are retired.
    // Nothing starts XR, acquisition, forwarding or sockets without local action.
    public sealed class ViveWozOperatorDevelopmentBootstrap : MonoBehaviour
    {
        ViveWozOperatorDevelopmentRig rig;
        ViveLegacyWozOperatorProcess process;
        WorldSpaceExperimentView view;
        bool closed,scope,forwarding,forwardingAndStorage,networkNotice,offerReviewed,participantLocalRecording;
        string token="",localIp="",localPort="45172",remoteIp="",remotePort="45171",maximumAge="2";
        string status="Not paired. XR, eye acquisition and network are off.";
        Vector2 scroll;
        public bool Configured {get{return process!=null;}}
        public bool NetworkOpen {get{return process!=null&&!process.Terminal&&!closed;}}
        public bool PreflightReady {get{return process!=null&&process.PhasePreflightCurrent;}}
        public bool CanEnterLiveCountdown {get{return false;}} // Participant owns this decision.
        public bool CanRecordOrExport {get{return false;}}
        public string Status {get{return status;}}
        void Awake()
        {
            Application.runInBackground=true;
            var cameraObject=new GameObject("OperatorDevelopmentCamera");cameraObject.tag="MainCamera";
            var camera=cameraObject.AddComponent<Camera>();camera.transform.position=new Vector3(0,1.6f,0);
            camera.backgroundColor=new Color(.035f,.045f,.06f);camera.clearFlags=CameraClearFlags.SolidColor;
            camera.nearClipPlane=.05f;camera.farClipPlane=20;camera.fieldOfView=60;
            view=new GameObject("OperatorDevelopmentView").AddComponent<WorldSpaceExperimentView>();view.Initialize(camera,Actor.Operator);
            ClearTask();rig=gameObject.AddComponent<ViveWozOperatorDevelopmentRig>();rig.Configure(camera);rig.BeforeStopping=BeforeSourceStop;
        }
        static string Input(string label,string value)
        {GUILayout.BeginHorizontal();GUILayout.Label(label,GUILayout.Width(240));value=GUILayout.TextField(value);GUILayout.EndHorizontal();return value;}
        bool ParticipantStorageDeclared {get{return process!=null?process.ParticipantLocalRecordingDeclared:offerReviewed&&participantLocalRecording;}}
        void ClearOfferReview()
        {offerReviewed=false;participantLocalRecording=false;scope=false;networkNotice=false;forwarding=false;forwardingAndStorage=false;}
        void ReviewOfferMode()
        {
            // An edited offer never inherits consent from the previous session/mode.
            ClearOfferReview();
            char[] secret=token.ToCharArray();
            try{using(var pairing=ViveLegacyWozProcessPairing.Parse(secret))
                {participantLocalRecording=pairing.ParticipantLocalRecordingDeclared;offerReviewed=true;}}
            catch(ArgumentException){}
            finally{Array.Clear(secret,0,secret.Length);}
        }
        void OpenFromOwnerForm()
        {
            if(closed||process!=null||!offerReviewed||!scope||!networkNotice||
                !(participantLocalRecording?forwardingAndStorage:forwarding)||rig==null||!rig.HeadReady||rig.Worker==null)
                throw new InvalidOperationException("Explicit local scope, forwarding and current installed source required");
            var clock=rig.SharedClock as MonotonicClock;if(clock==null)throw new InvalidOperationException("Exact sealed rig clock required");
            var local=new IPEndPoint(IPAddress.Parse(localIp),Int32.Parse(localPort,CultureInfo.InvariantCulture));
            var remote=new IPEndPoint(IPAddress.Parse(remoteIp),Int32.Parse(remotePort,CultureInfo.InvariantCulture));
            double age=Double.Parse(maximumAge,CultureInfo.InvariantCulture);char[] secret=token.ToCharArray();token="";
            try
            {using(var pairing=ViveLegacyWozProcessPairing.Parse(secret))
                {
                    if(pairing.ParticipantLocalRecordingDeclared!=participantLocalRecording)
                        throw new InvalidOperationException("Review this offer's recording mode before consent");
                    process=ViveLegacyWozOperatorProcess.BindInstalled(pairing,clock,rig.Worker,local,remote,age);
                }
                bool confirmed=process.ParticipantLocalRecordingDeclared?
                    process.ConfirmForwardingAndParticipantLocalStorage():process.ConfirmLocalOperatorSource();
                if(!confirmed||!process.Start())throw new InvalidOperationException("Owned process start failed");
                status="Connected; waiting for participant.";}
            catch{if(process!=null)process.Dispose();closed=true;throw;}
            finally{Array.Clear(secret,0,secret.Length);}
        }
        void Update()
        {
            if(process==null||closed)return;
            try
            {
                if(rig==null||!rig.HeadReady)process.RequestSafetyStop();
                process.Tick();
                if(!process.Terminal){process.PollPhasePreflight();process.PollObservation();}
                var task=process.CurrentTask;
                if(task==null)ClearTask();else view.ApplyTaskView(ViveWozOperatorTaskView.FromFreshTask(task));
                view.SetFaults(new[]{ParticipantStorageDeclared?
                    "DEVELOPMENT | Participant-local recording declared | Alignment unvalidated":
                    "DEVELOPMENT | Recording off | Alignment unvalidated"});
                if(process.Terminal)
                    status=process.ParticipantStopAcknowledged?"Participant pause acknowledged; no automatic resume.":
                        process.SourceRevokeAcknowledged?"Source stopped; acknowledgment sent.":"Stopped: "+process.LastIssue;
                else status="Source confirmed: "+process.SourceConfirmed+" | Fresh tracking: "+process.PhasePreflightCurrent;
            }
            catch(Exception e){status="Operator failure: "+e.GetType().Name;Close();}
        }
        void ClearTask(){if(view!=null){view.ApplyTaskView(ViveWozOperatorTaskView.FromFreshTask(null));view.ClearAvatarOutput();}}
        void BeforeSourceStop(){token="";ClearOfferReview();if(process!=null&&!process.Terminal)process.RequestSafetyStop();ClearTask();}
        public void RequestPause(){BeforeSourceStop();status=process!=null&&!process.Terminal?"Stop requested; waiting for participant.":"Stopped: "+(process==null?"not connected":process.LastIssue);}
        void OnGUI()
        {
            GUILayout.BeginArea(new Rect(10,10,Mathf.Max(1,Mathf.Min(900,Screen.width-20)),Mathf.Max(1,Mathf.Min(720,Screen.height-20))),GUI.skin.box);
            scroll=GUILayout.BeginScrollView(scroll);
            GUILayout.Label(ParticipantStorageDeclared?
                "Legacy WoZ development | Participant-local recording declared | Hardware/alignment unvalidated":
                "Legacy WoZ development | Recording off | Hardware/alignment unvalidated");
            GUILayout.Label("Authenticated messages, not encrypted. Operator head/eye only; participant answers and raw eyes stay local.");
            GUILayout.Label(status);GUILayout.Label(rig==null?"Rig unavailable":rig.Status);
            GUI.enabled=rig!=null&&!rig.ViewActive&&!closed;
            if(GUILayout.Button("Start operator XR view"))rig.BeginView();
            GUI.enabled=rig!=null&&rig.HeadReady&&rig.Worker==null&&!closed;
            if(GUILayout.Button("Start installed eye worker"))rig.BeginEye();
            GUI.enabled=true;
            if(process==null&&!closed)
            {
                GUILayout.Label("Clock ID for participant pairing:");
                GUILayout.TextField(rig==null||rig.SharedClock==null?"unavailable":rig.SharedClock.ClockId);
                string enteredOffer=Input("Temporary participant offer",token);
                if(enteredOffer!=token){token=enteredOffer;ReviewOfferMode();}
                localIp=Input("Operator local IPv4",localIp);localPort=Input("Operator local port",localPort);
                remoteIp=Input("Participant IPv4",remoteIp);remotePort=Input("Participant port",remotePort);
                maximumAge=Input("Connection timeout (seconds)",maximumAge);
                if(offerReviewed)
                {
                    if(participantLocalRecording)
                    {
                        GUILayout.Label("Participant-local recording declared. Participant PC may save mapped operator head/eye poses plus source, time and status.");
                        GUILayout.Label("Participant raw eyes, answers, timings and ratings never go to the operator.");
                        scope=GUILayout.Toggle(scope,"Run a participant-local recording development test.");
                    }
                    else scope=GUILayout.Toggle(scope,"Run an unrecorded development test.");
                    networkNotice=GUILayout.Toggle(networkNotice,"Allow plaintext messages on this isolated laboratory LAN.");
                    if(participantLocalRecording)
                        forwardingAndStorage=GUILayout.Toggle(forwardingAndStorage,"Forward my head/eye control and allow participant-local storage for this session.");
                    else forwarding=GUILayout.Toggle(forwarding,"Forward my head/eye control for this session.");
                }
                else GUILayout.Label("Paste a valid offer to review its recording mode before consent.");
                GUI.enabled=offerReviewed&&scope&&networkNotice&&(participantLocalRecording?forwardingAndStorage:forwarding)&&rig!=null&&rig.HeadReady&&rig.Worker!=null;
                if(GUILayout.Button("Import offer and connect"))try{OpenFromOwnerForm();}catch(Exception e){status="Pairing/start rejected: "+e.GetType().Name;}
                GUI.enabled=true;
            }
            GUI.enabled=process!=null&&!process.Terminal&&!closed;
            if(GUILayout.Button("Stop forwarding and pause participant"))RequestPause();
            GUI.enabled=rig!=null&&rig.ViewActive;
            if(GUILayout.Button("Stop operator source and XR")){BeforeSourceStop();rig.StopView();}
            GUI.enabled=true;
            GUILayout.EndScrollView();GUILayout.EndArea();
        }
        void Close(){if(closed)return;token="";try{BeforeSourceStop();}finally{closed=true;try{if(process!=null)process.Dispose();}finally{ClearTask();}}}
        void OnApplicationFocus(bool focused){if(!focused)BeforeSourceStop();}
        void OnDisable(){Close();}
        void OnDestroy(){Close();if(rig!=null)rig.BeforeStopping=null;}
    }
}
#endif
