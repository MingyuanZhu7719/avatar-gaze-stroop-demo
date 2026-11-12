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
        bool showDiagnostics,showNetworkSettings;
        GUIStyle titleStyle,sectionStyle,textStyle,mutedStyle,buttonStyle,fieldStyle,toggleStyle;
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
        string Input(string label,string value)
        {
            GUILayout.Label(label,textStyle);
            return GUILayout.TextField(value,fieldStyle,GUILayout.MinHeight(32));
        }
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
        void EnsureOwnerStyles()
        {
            if(titleStyle!=null)return;
            textStyle=new GUIStyle(GUI.skin.label){fontSize=16,wordWrap=true};
            textStyle.normal.textColor=new Color(.94f,.96f,.98f);
            mutedStyle=new GUIStyle(textStyle);mutedStyle.normal.textColor=new Color(.72f,.78f,.84f);
            titleStyle=new GUIStyle(textStyle){fontSize=24,fontStyle=FontStyle.Bold};
            sectionStyle=new GUIStyle(textStyle){fontSize=18,fontStyle=FontStyle.Bold};
            buttonStyle=new GUIStyle(GUI.skin.button){fontSize=16,wordWrap=true,padding=new RectOffset(12,12,9,9)};
            fieldStyle=new GUIStyle(GUI.skin.textField){fontSize=16,padding=new RectOffset(8,8,6,6)};
            toggleStyle=new GUIStyle(GUI.skin.toggle){fontSize=16,wordWrap=true,padding=new RectOffset(22,4,4,5)};
            toggleStyle.normal.textColor=textStyle.normal.textColor;
        }
        void Section(string heading){GUILayout.Space(14);GUILayout.Label(heading,sectionStyle);GUILayout.Space(3);}
        bool OwnerButton(string label,bool enabled)
        {
            bool old=GUI.enabled;GUI.enabled=old&&enabled;
            bool pressed=GUILayout.Button(label,buttonStyle,GUILayout.MinHeight(40));GUI.enabled=old;return pressed;
        }
        string NextOwnerAction()
        {
            if(closed||process!=null&&process.Terminal)return "Session ended. Reopen the operator app for a new pairing.";
            if(process!=null)return process.PhasePreflightCurrent?
                "Ready. The participant computer controls when the block starts.":
                "Keep the headset tracked. Waiting for fresh tracking and participant readiness.";
            if(rig==null||!rig.ViewActive)return "Start the headset view.";
            if(!rig.HeadReady)return "Put on the headset and wait for tracking.";
            if(rig.Worker==null)return "Start eye tracking.";
            if(!offerReviewed)return "Copy the clock ID to the participant app, then paste its pairing offer here.";
            if(!scope||!networkNotice||!(participantLocalRecording?forwardingAndStorage:forwarding))return "Review the session mode and confirm the three permissions below.";
            return "Check both IP addresses, then connect to the participant.";
        }
        static bool ValidEndpoint(string address,string port)
        {
            IPAddress parsed;int number;
            return IPAddress.TryParse(address,out parsed)&&parsed.AddressFamily==System.Net.Sockets.AddressFamily.InterNetwork&&
                Int32.TryParse(port,NumberStyles.None,CultureInfo.InvariantCulture,out number)&&number>0&&number<=65535;
        }
        bool ConnectionSettingsValid()
        {
            double age;
            return ValidEndpoint(localIp,localPort)&&ValidEndpoint(remoteIp,remotePort)&&
                Double.TryParse(maximumAge,NumberStyles.Float,CultureInfo.InvariantCulture,out age)&&
                !Double.IsNaN(age)&&!Double.IsInfinity(age)&&age>0;
        }
        void DrawPairingForm()
        {
            Section("2  Pair with the participant");
            GUILayout.Label("Copy this clock ID into the participant app:",textStyle);
            string clock=rig==null||rig.SharedClock==null?"Unavailable":rig.SharedClock.ClockId;
            GUILayout.BeginHorizontal();GUILayout.TextField(clock,fieldStyle,GUILayout.MinHeight(32));
            if(OwnerButton("Copy",rig!=null&&rig.SharedClock!=null))GUIUtility.systemCopyBuffer=clock;
            GUILayout.EndHorizontal();
            GUILayout.Label("Participant pairing offer",textStyle);
            string enteredOffer=GUILayout.PasswordField(token,'*',fieldStyle,GUILayout.MinHeight(32));
            if(enteredOffer!=token){token=enteredOffer;ReviewOfferMode();}
            GUILayout.Label(offerReviewed?"Offer accepted. Review its session mode below.":"Paste the temporary offer from the participant app.",mutedStyle);
            localIp=Input("This computer's IPv4 address",localIp);
            remoteIp=Input("Participant computer's IPv4 address",remoteIp);
            showNetworkSettings=GUILayout.Toggle(showNetworkSettings,"Advanced network settings",toggleStyle);
            if(showNetworkSettings)
            {
                localPort=Input("This computer's port",localPort);remotePort=Input("Participant port",remotePort);
                maximumAge=Input("Connection timeout (seconds)",maximumAge);
            }
            Section("3  Review and connect");
            if(!offerReviewed){GUILayout.Label("A valid offer is required before permissions can be confirmed.",mutedStyle);return;}
            GUILayout.Label(participantLocalRecording?"Participant-local storage requested":"Recording off",sectionStyle);
            if(participantLocalRecording)
                GUILayout.Label("The participant computer may save mapped operator head/eye poses, source, time and status.",textStyle);
            GUILayout.Label("Participant answers, response times, ratings and raw eye data stay on the participant computer.",mutedStyle);
            scope=GUILayout.Toggle(scope,participantLocalRecording?"Run a participant-local recording development test.":"Run an unrecorded development test.",toggleStyle);
            networkNotice=GUILayout.Toggle(networkNotice,"Allow authenticated, unencrypted messages on this isolated laboratory LAN.",toggleStyle);
            if(participantLocalRecording)
                forwardingAndStorage=GUILayout.Toggle(forwardingAndStorage,"Forward my head/eye control and allow participant-local storage for this session.",toggleStyle);
            else forwarding=GUILayout.Toggle(forwarding,"Forward my head/eye control for this session.",toggleStyle);
            bool valid=ConnectionSettingsValid();
            if(!valid)GUILayout.Label("Enter valid IPv4 addresses, ports (1-65535) and a positive timeout.",mutedStyle);
            bool ready=offerReviewed&&scope&&networkNotice&&(participantLocalRecording?forwardingAndStorage:forwarding)&&
                rig!=null&&rig.HeadReady&&rig.Worker!=null&&valid;
            if(OwnerButton("Connect to participant",ready))
                try{OpenFromOwnerForm();}catch(Exception e){status="Connection rejected: "+e.GetType().Name;}
        }
        void OnGUI()
        {
            using(var desktop=ViveOwnerDesktopLayout.Begin())
            {
            EnsureOwnerStyles();
            var panel=new Rect(10,10,Mathf.Max(1,Mathf.Min(680,desktop.Width-20)),Mathf.Max(1,desktop.Height-20));
            var oldColour=GUI.color;GUI.color=new Color(.055f,.075f,.10f,1f);GUI.DrawTexture(panel,Texture2D.whiteTexture);GUI.color=oldColour;
            GUILayout.BeginArea(new Rect(panel.x+16,panel.y+12,Mathf.Max(1,panel.width-32),Mathf.Max(1,panel.height-24)));
            GUILayout.Label("WoZ Operator",titleStyle);
            GUILayout.Label(NetworkOpen?"CONNECTED  |  "+(ParticipantStorageDeclared?"Participant-local storage permitted":"Recording off"):"NOT CONNECTED",sectionStyle);
            GUILayout.Label("Next: "+NextOwnerAction(),textStyle);
            if(closed||status.StartsWith("Connection rejected:",StringComparison.Ordinal)||status.StartsWith("Operator failure:",StringComparison.Ordinal))
                GUILayout.Label(status,textStyle);
            GUILayout.Space(8);scroll=GUILayout.BeginScrollView(scroll);
            Section("1  Headset and eye tracking");
            GUILayout.Label("Headset: "+(rig!=null&&rig.HeadReady?"Tracked":rig!=null&&rig.ViewActive?"Waiting for tracking":"Off"),textStyle);
            GUILayout.Label("Eye source: "+(rig==null||rig.Worker==null?"Off":rig.Worker.Started?"Running; connection checks fresh samples":rig.Worker.Starting?"Starting":"Unavailable (see diagnostics)"),textStyle);
            if(OwnerButton("Start headset view",rig!=null&&!rig.ViewActive&&!closed&&process==null))rig.BeginView();
            if(OwnerButton("Start eye tracking",rig!=null&&rig.HeadReady&&rig.Worker==null&&!closed&&process==null))rig.BeginEye();
            if(process==null&&!closed)DrawPairingForm();
            else
            {
                Section("Session status");GUILayout.Label(status,textStyle);
                GUILayout.Label("Task and avatar control arrive from the participant computer. This app cannot start a block or export participant data.",mutedStyle);
            }
            GUILayout.Space(12);showDiagnostics=GUILayout.Toggle(showDiagnostics,"Show diagnostics",toggleStyle);
            if(showDiagnostics)
            {
                GUILayout.Label(status,mutedStyle);GUILayout.Label(rig==null?"Rig unavailable":rig.Status,mutedStyle);
                GUILayout.Label("Hardware and alignment require validation. Authentication does not encrypt LAN messages. Head/eye control is sent only after the permissions above are confirmed.",mutedStyle);
            }
            GUILayout.EndScrollView();GUILayout.Space(8);
            if(OwnerButton("Stop forwarding and pause participant",process!=null&&!process.Terminal&&!closed))RequestPause();
            if(OwnerButton("Stop headset and eye source",rig!=null&&rig.ViewActive)){BeforeSourceStop();rig.StopView();}
            GUILayout.EndArea();
            }
        }
        void Close(){if(closed)return;token="";try{BeforeSourceStop();}finally{closed=true;try{if(process!=null)process.Dispose();}finally{ClearTask();}}}
        void OnApplicationFocus(bool focused){if(!focused)BeforeSourceStop();}
        void OnDisable(){Close();}
        void OnDestroy(){Close();if(rig!=null)rig.BeforeStopping=null;}
    }
}
#endif
