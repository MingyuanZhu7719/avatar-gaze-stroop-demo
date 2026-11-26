#if STROOP_XR_PREVIEW
using System;
using System.Globalization;
using System.Net;
using System.Threading.Tasks;
using AvatarStroop.Core;
using AvatarStroop.Runtime;
using UnityEngine;

namespace AvatarStroop.Unity
{
    // Actual operator scene. Old unauthenticated adapter controls are retired.
    // Nothing starts XR, acquisition, forwarding or sockets without local action.
    public sealed partial class ViveWozOperatorDevelopmentBootstrap : MonoBehaviour
    {
        ViveWozOperatorDevelopmentRig rig;
        ViveLegacyWozOperatorProcess process;
        WorldSpaceExperimentView view;
        ViveWozOperatorDiagnostics diagnostics;
        Task<string> diagnosticExport;
        string exportStatus="",reportedProcessIssue;
        bool closed,scope,forwarding,forwardingAndStorage,networkNotice,offerReviewed,participantLocalRecording;
        bool participantAvatarSharing;
        string token="",localIp="",localPort="45172",remoteIp="",remotePort="45171",maximumAge="2";
        string status="Not paired. Experiment forwarding is off.";
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
            diagnostics=new ViveWozOperatorDiagnostics();
            try{InitializeScene();}
            catch(Exception error){diagnostics.Fail("SCENE_START_FAILED","The classroom could not start. Export the debug ZIP and reopen the app.",error);closed=true;}
        }
        void InitializeScene()
        {
            var cameraObject=new GameObject("OperatorDevelopmentCamera");cameraObject.tag="MainCamera";
            var camera=cameraObject.AddComponent<Camera>();camera.transform.position=new Vector3(0,1.6f,0);
            camera.backgroundColor=new Color(.035f,.045f,.06f);camera.clearFlags=CameraClearFlags.SolidColor;
            camera.nearClipPlane=.05f;camera.farClipPlane=20;camera.fieldOfView=60;
            view=new GameObject("OperatorDevelopmentView").AddComponent<WorldSpaceExperimentView>();view.Initialize(camera,Actor.Operator);
            view.ConfigureWozOperatorHeadsetPresentation();
            view.PlaceWozOperatorView(camera.transform.position);
            ClearTask();rig=gameObject.AddComponent<ViveWozOperatorDevelopmentRig>();rig.AttachDiagnostics(diagnostics);rig.Configure(camera);rig.BeforeStopping=BeforeSourceStop;
            rig.PlaceOperatorStation=view.PlaceWozOperatorView;
            diagnostics.Record("SCENE_READY","Build "+Application.version+" | "+Application.buildGUID);
        }
        string Input(string label,string value)
        {
            GUILayout.Label(label,textStyle);
            return GUILayout.TextField(value,fieldStyle,GUILayout.MinHeight(32));
        }
        bool ParticipantStorageDeclared {get{return process!=null?process.ParticipantLocalRecordingDeclared:offerReviewed&&participantLocalRecording;}}
        void ClearOfferReview()
        {offerReviewed=false;participantLocalRecording=false;participantAvatarSharing=false;scope=false;networkNotice=false;forwarding=false;forwardingAndStorage=false;}
        void ReviewOfferMode()
        {
            // An edited offer never inherits consent from the previous session/mode.
            ClearOfferReview();
            char[] secret=token.ToCharArray();
            try{using(var pairing=ViveLegacyWozProcessPairing.Parse(secret))
                {participantLocalRecording=pairing.ParticipantLocalRecordingDeclared;participantAvatarSharing=pairing.ParticipantAvatarSharingDeclared;offerReviewed=true;}}
            catch(ArgumentException){}
            finally{Array.Clear(secret,0,secret.Length);}
        }
        void OpenFromOwnerForm()
        {
            if(closed||process!=null||!offerReviewed||!scope||!networkNotice||
                !(participantLocalRecording?forwardingAndStorage:forwarding)||rig==null||!rig.HeadReady||rig.Worker==null)
                throw new InvalidOperationException("Explicit local scope, forwarding and current installed source required");
            var clock=rig.SharedClock as MonotonicClock;if(clock==null)throw new InvalidOperationException("Exact sealed rig clock required");
            IPEndPoint local=null,remote=null;
            if(easyPairing==null)
            {
                local=new IPEndPoint(IPAddress.Parse(localIp),Int32.Parse(localPort,CultureInfo.InvariantCulture));
                remote=new IPEndPoint(IPAddress.Parse(remoteIp),Int32.Parse(remotePort,CultureInfo.InvariantCulture));
            }
            double age=Double.Parse(maximumAge,CultureInfo.InvariantCulture);char[] secret=token.ToCharArray();token="";
            try
            {using(var pairing=ViveLegacyWozProcessPairing.Parse(secret))
                {
                    if(pairing.ParticipantLocalRecordingDeclared!=participantLocalRecording || pairing.ParticipantAvatarSharingDeclared!=participantAvatarSharing)
                        throw new InvalidOperationException("Review this offer's recording mode before consent");
                    process=easyPairing!=null?
                        ViveLegacyWozOperatorProcess.BindInstalledPhoton(pairing,clock,rig.Worker,easyPairing,age):
                        ViveLegacyWozOperatorProcess.BindInstalled(pairing,clock,rig.Worker,local,remote,age);
                }
                bool confirmed=process.ParticipantLocalRecordingDeclared?
                    process.ConfirmForwardingAndParticipantLocalStorage():process.ConfirmLocalOperatorSource();
                if(!confirmed||!process.Start())throw new InvalidOperationException("Owned process start failed");
                if(process.ParticipantAvatarSharingDeclared)view.BeginWozParticipantAvatarView(view.PresentationCamera.transform.position);
                status="Connected; waiting for participant.";diagnostics.Record("CONNECTION_STARTED",status);
                if(easyPairing!=null)easyPairing.MarkOperatorStarted();}
            catch{if(process!=null)process.Dispose();StopEasyPairing();closed=true;throw;}
            finally{Array.Clear(secret,0,secret.Length);}
        }
        void Update()
        {
            PollDiagnosticExport();
            PollEasyPairing();
            if(view!=null)view.SetWozOperatorHeadsetStatus(OperatorHeadsetInstruction());
            if(process==null||closed)return;
            try
            {
                if(rig==null||!rig.HeadReady)process.RequestSafetyStop();
                process.Tick();
                if(!process.Terminal){process.PollPhasePreflight();process.PollObservation();}
                var task=process.CurrentTask;
                if(task==null||process.Terminal)ClearTask();
                else
                {
                    view.ApplyTaskView(ViveWozOperatorTaskView.FromFreshTask(task));
                    Rotation head;Vec3 headLocalEyes;
                    if(rig.HeadReady && process.TryGetParticipantAvatarPose(out head,out headLocalEyes))
                        view.ShowWozParticipantPose(head,headLocalEyes);
                    else view.ClearWozParticipantPose();
                }
                view.SetFaults(new[]{ParticipantStorageDeclared?
                    "DEVELOPMENT | Participant-local recording declared | Alignment unvalidated":
                    "DEVELOPMENT | Recording off | Alignment unvalidated"});
                if(process.Terminal)
                {
                    DrainStoppedEasyPairing();
                    status=process.ParticipantStopAcknowledged?"Participant pause acknowledged; no automatic resume.":
                        process.SourceRevokeAcknowledged?"Source stopped; acknowledgment sent.":"Stopped: "+process.LastIssue;
                    if(reportedProcessIssue!=status)
                    {
                        reportedProcessIssue=status;
                        if(process.ParticipantStopAcknowledged||process.SourceRevokeAcknowledged)diagnostics.Record("CONNECTION_CLOSED",status);
                        else if(String.IsNullOrEmpty(diagnostics.LastError))diagnostics.Fail("CONNECTION_STOPPED",status+". Check tracking and both computers' connection logs before starting a new session.");
                        else diagnostics.Record("CONNECTION_STOPPED",status);
                    }
                }
                else status="Source confirmed: "+process.SourceConfirmed+" | Fresh tracking: "+process.PhasePreflightCurrent;
            }
            catch(Exception e){status="Operator failure: "+e.GetType().Name;diagnostics.Fail("OPERATOR_FAILED",status+". Export diagnostics and reopen the app.",e);Close();}
        }
        string OperatorHeadsetInstruction()
        {
            if(closed||process!=null&&process.Terminal)return "Session stopped.\nCheck the operator computer.";
            if(process!=null)return process.CurrentTask!=null&&process.CurrentTask.Phase==Phase.MainTask?
                "Look naturally at the participant or whiteboard.":"Waiting for the participant.\nKeep the headset on.";
            if(rig==null||!rig.ViewActive)return "Start the headset view on the computer.";
            if(!rig.HeadReady)return "Keep the headset in view of the base stations.\nClose the SteamVR menu.";
            if(rig.Worker==null)return "Classroom ready.\nStart eye tracking on the computer.";
            if(!offerReviewed)return "Create a room on the computer.\nAsk the participant to join.";
            return "Participant joined.\nReview and connect on the computer.";
        }
        void ClearTask(){if(view!=null){view.ClearWozParticipantPose();view.ApplyTaskView(ViveWozOperatorTaskView.FromFreshTask(null));view.ClearAvatarOutput();}}
        void BeforeSourceStop(){StopUnstartedEasyPairing();token="";ClearOfferReview();if(process!=null&&!process.Terminal)process.RequestSafetyStop();ClearTask();}
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
            if(!offerReviewed)return showManualPairing?"Copy the clock ID to the participant app, then paste its pairing offer here.":
                easyPairing==null?"Create a room and enter its code on the participant computer.":easyPairing.Status;
            if(!scope||!networkNotice||!(participantLocalRecording?forwardingAndStorage:forwarding))return "Review the session mode and confirm the three permissions below.";
            return "Connect to the participant when you are ready.";
        }
        static bool ValidEndpoint(string address,string port)
        {
            IPAddress parsed;int number;
            return IPAddress.TryParse(address,out parsed)&&parsed.AddressFamily==System.Net.Sockets.AddressFamily.InterNetwork&&
                Int32.TryParse(port,NumberStyles.None,CultureInfo.InvariantCulture,out number)&&number>0&&number<=65535;
        }
        bool ConnectionSettingsValid()
        {
            if(!showManualPairing)return easyPairing!=null&&!easyPairing.Terminal&&easyPairing.PeerConfirmed;
            double age;
            return ValidEndpoint(localIp,localPort)&&ValidEndpoint(remoteIp,remotePort)&&
                Double.TryParse(maximumAge,NumberStyles.Float,CultureInfo.InvariantCulture,out age)&&
                !Double.IsNaN(age)&&!Double.IsInfinity(age)&&age>0;
        }
        void DrawPairingForm()
        {
            Section("2  Pair with the participant");
            if(easyPairing==null&&!offerReviewed)
                showManualPairing=GUILayout.Toggle(showManualPairing,"Advanced: manual LAN connection",toggleStyle);
            if(!showManualPairing)
            {
                DrawEasyPairingForm();
                if(!offerReviewed)return;
            }
            else
            {
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
            }
            Section("3  Review and connect");
            if(!offerReviewed){GUILayout.Label("A valid offer is required before permissions can be confirmed.",mutedStyle);return;}
            GUILayout.Label(participantLocalRecording?"Participant-local storage requested":"Recording off",sectionStyle);
            GUILayout.Label(participantAvatarSharing?"Two-way view: the participant shares live head direction and combined gaze during active WoZ trials.":"One-way view: participant head and gaze are not shared.",textStyle);
            if(participantLocalRecording)
                GUILayout.Label("The participant computer may save mapped operator head/eye poses, source, time and status.",textStyle);
            GUILayout.Label("Participant answers, response times, ratings and raw eye data stay on the participant computer.",mutedStyle);
            scope=GUILayout.Toggle(scope,participantLocalRecording?"Run a participant-local recording development test.":"Run an unrecorded development test.",toggleStyle);
            networkNotice=GUILayout.Toggle(networkNotice,showManualPairing?
                "Allow authenticated, unencrypted messages on this isolated laboratory LAN.":
                "Allow Photon cloud relay for task state and permitted live head/gaze over an encrypted internet connection.",toggleStyle);
            if(participantLocalRecording)
                forwardingAndStorage=GUILayout.Toggle(forwardingAndStorage,"Forward my head/eye control and allow participant-local storage for this session.",toggleStyle);
            else forwarding=GUILayout.Toggle(forwarding,"Forward my head/eye control for this session.",toggleStyle);
            bool valid=ConnectionSettingsValid();
            if(!valid)GUILayout.Label(showManualPairing?"Enter valid IPv4 addresses, ports (1-65535) and a positive timeout.":"A current accepted Photon room is required.",mutedStyle);
            bool ready=offerReviewed&&scope&&networkNotice&&(participantLocalRecording?forwardingAndStorage:forwarding)&&
                rig!=null&&rig.HeadReady&&rig.Worker!=null&&valid;
            if(OwnerButton("Connect to participant",ready))
                try{OpenFromOwnerForm();}catch(Exception e){status="Connection rejected: "+e.GetType().Name;diagnostics.Fail("CONNECT_FAILED",status+(showManualPairing?". Check the IP addresses, ports and a fresh pairing offer for this clock ID.":". Create a new room and participant session."),e);}
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
            if(NetworkOpen)GUILayout.Label(process.ParticipantAvatarSharingDeclared?"Participant avatar sharing on":"Participant avatar sharing off",textStyle);
            GUILayout.Label("Next: "+NextOwnerAction(),textStyle);
            if(diagnostics!=null&&!String.IsNullOrEmpty(diagnostics.LastError))
            {
                var colour=GUI.color;GUI.color=new Color(1f,.65f,.55f);
                string message=diagnostics.LastError.Replace('\r',' ').Replace('\n',' ');
                if(message.Length>240)message=message.Substring(0,240)+"... See diagnostics for details.";
                GUILayout.Label(diagnostics.LastErrorCode+": "+message,textStyle);GUI.color=colour;
            }
            if(closed||status.StartsWith("Connection rejected:",StringComparison.Ordinal)||status.StartsWith("Operator failure:",StringComparison.Ordinal))
                GUILayout.Label(status,textStyle);
            GUILayout.Space(8);scroll=GUILayout.BeginScrollView(scroll);
            Section("1  Headset and eye tracking");
            GUILayout.Label("Headset: "+(rig==null?"Off":rig.HeadTrackingStatus),textStyle);
            GUILayout.Label("Eye source: "+(rig==null?"Off":rig.EyeStatus),textStyle);
            if(OwnerButton("Start headset view",rig!=null&&!rig.ViewActive&&!closed&&process==null))rig.BeginView();
            if(OwnerButton("Start eye tracking",rig!=null&&rig.HeadReady&&rig.Worker==null&&!closed&&process==null))rig.BeginEye();
            if(process==null&&!closed)DrawPairingForm();
            else
            {
                Section("Session status");GUILayout.Label(status,textStyle);
                GUILayout.Label("The participant computer starts each block. Your headset drives the evaluator avatar.",mutedStyle);
            }
            GUILayout.Space(12);showDiagnostics=GUILayout.Toggle(showDiagnostics,"Show diagnostics",toggleStyle);
            if(showDiagnostics)
            {
                GUILayout.Label(status,mutedStyle);GUILayout.Label(rig==null?"Rig unavailable":rig.Status,mutedStyle);
                if(!String.IsNullOrEmpty(easyPairingTiming))
                    GUILayout.Label((easyPairing!=null?"Current cloud timing\n":"Last cloud timing snapshot\n")+easyPairingTiming,mutedStyle);
                if(rig!=null&&rig.Worker!=null)GUILayout.Label(rig.Worker.Diagnostic,mutedStyle);
                if(diagnostics!=null){GUILayout.Label(diagnostics.LastError,mutedStyle);GUILayout.Label("Diagnostic folder",mutedStyle);GUILayout.TextField(diagnostics.DirectoryPath,fieldStyle);GUILayout.Label(diagnostics.StorageIssue,mutedStyle);}
                GUILayout.Label("Hardware and alignment require validation. Photon uses encrypted internet transport; manual LAN messages are unencrypted. Head/eye control is sent only after the permissions above are confirmed.",mutedStyle);
            }
            GUILayout.EndScrollView();GUILayout.Space(8);
            DrawDiagnosticActions();
            if(OwnerButton("Stop forwarding and pause participant",process!=null&&!process.Terminal&&!closed))RequestPause();
            if(OwnerButton("Stop headset and eye source",rig!=null&&rig.ViewActive)){BeforeSourceStop();rig.StopView();}
            GUILayout.EndArea();
            }
        }
        void Close()
        {
            if(closed)return;token="";closed=true;
            try{BeforeSourceStop();}catch(Exception e){if(diagnostics!=null)diagnostics.Record("STOP_CLEANUP_FAILED",e.ToString());}
            try{if(process!=null)process.Dispose();}catch(Exception e){if(diagnostics!=null)diagnostics.Record("PROCESS_CLEANUP_FAILED",e.ToString());}
            try{StopEasyPairing();}catch(Exception e){if(diagnostics!=null)diagnostics.Record("ROOM_CLEANUP_FAILED",e.ToString());}
            try{ClearTask();}catch(Exception e){if(diagnostics!=null)diagnostics.Record("VIEW_CLEANUP_FAILED",e.ToString());}
        }
        // Pairing does not forward poses. Comparing the two screens must not
        // cancel setup; a live process retains its existing focus-stop rule.
        void OnApplicationFocus(bool focused){if(!focused&&process!=null)BeforeSourceStop();}
        void OnDisable(){Close();}
        void OnDestroy(){try{Close();}finally{if(rig!=null)rig.BeforeStopping=null;if(diagnostics!=null)diagnostics.Dispose();}}
    }
}
#endif
