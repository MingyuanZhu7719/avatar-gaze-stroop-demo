#if STROOP_XR_PREVIEW
using System;
using System.Globalization;
using System.Net;
using AvatarStroop.Core;
using AvatarStroop.Runtime;
using UnityEngine;

namespace AvatarStroop.Unity
{
    public sealed partial class ViveStroopDevelopmentBootstrap
    {
        ViveLegacyWozParticipantProcess legacyWozProcess;
        ViveLegacyWozControlledScene legacyWozScene;
        bool legacyWozStarted,legacyWozScope,legacyWozPlaintext,legacyWozClosed;
        bool legacyWozMaskLatched;
        Action pendingLegacyWozOffer;
        bool RecordedLegacyWoz {get{return participant!=null&&participant.LegacyWozRecordingDeclared;}}
        bool RecordedLegacyWozReady {get{return RecordedLegacyWoz&&ViveLegacyWozRecordingBuildPolicy.Available&&
            namedRawPump!=null&&legacyWozInstalledAdmission!=null&&legacyWozInstalledWorker!=null&&
            legacyWozInstalledWorker.UsesInstalledSdkPath&&legacyWozInstalledAdmission.UsesExactSources(participant,legacyWozInstalledWorker,sharedClock)&&
            namedRawPump.UsesRecordingBinding(participant,legacyWozInstalledWorker,sharedClock,legacyWozInstalledAdmission);}}
        internal bool WaitForLegacyWozRawAcquisition()
        {
            if(!RecordedLegacyWoz||namedRawPump==null)return false;
            var state=ViveLegacyWozRawSceneAcquisition.Evaluate(participant,legacyWozScene,legacyWozStarted);
            if(state==ViveLegacyWozRawSceneAcquisition.State.Ready)return false;
            suppressInputUntilReleased=true;
            if(state==ViveLegacyWozRawSceneAcquisition.State.Stopped)FailNamedRawBeforeFrame();
            return true;
        }
        string legacyWozOffer="",legacyWozOperatorClock="",legacyWozLocalIp="",legacyWozRemoteIp="";
        string legacyWozLocalPort="45171",legacyWozRemotePort="45172",legacyWozMaximumAge="2";
        string legacyWozStatus="No WoZ source selected. Network is off.";
        sealed class OwnedLegacySink:IViveLegacyWozSceneSink
        {
            readonly ViveStroopDevelopmentBootstrap scene;
            readonly ViveDevelopmentParticipant task;
            readonly WorldSpaceExperimentView target;
            readonly ViveStroopPreviewRig rig;
            internal OwnedLegacySink(ViveStroopDevelopmentBootstrap scene)
            {this.scene=scene;task=scene.participant;target=scene.view;rig=scene.rig;}
            public bool LocallyReady {get{return scene!=null&&scene.isActiveAndEnabled&&Application.isFocused&&
                System.Object.ReferenceEquals(scene.participant,task)&&System.Object.ReferenceEquals(scene.view,target)&&System.Object.ReferenceEquals(scene.rig,rig)&&
                rig!=null&&rig.HeadReady&&task.InputReady&&!scene.PresentationBlocked&&scene.recording==null&&
                (!task.RawRecordingRequested||(scene.RecordedLegacyWozReady&&task.LegacyWozRecording!=null&&task.LegacyWozRecording.SceneAuthorityCurrent&&rig.RawSceneTrackingCurrent()));}}
            public void Clear()
            {
                if(task!=null&&task.LegacyWozRecording!=null)task.LegacyWozRecording.InvalidateAppliedPresentation();
                if(target==null){scene.legacyWozMaskLatched=true;if(rig!=null)rig.BlockTaskPresentation();throw new InvalidOperationException("Owned scene no longer exists");}
                bool failed=false;
                try{target.SetPresentationAvailable(false);}catch{failed=true;}
                try{target.SuppressViveRawStimulus();}catch{failed=true;}
                try{target.ClearAvatarOutput();}catch{failed=true;}
                if(failed||target.AvatarRoot==null||target.AvatarRoot.gameObject.activeSelf)
                {scene.legacyWozMaskLatched=true;if(rig!=null)rig.BlockTaskPresentation();throw new InvalidOperationException("Owned scene clear unconfirmed");}
            }
            public void Apply(AvatarOutput output)
            {
                if(!LocallyReady||target==null||output==null||output.MappingVersion!=ViveProEyeRehearsalCoordinates.MappingVersion||
                    output.SelectedSource!=ControlSource.LiveOperator)throw new InvalidOperationException("Current owned mapped scene required");
                target.ApplyAvatarOutput(output);
            }
        }
        static string WozInput(string label,string value)
        {GUILayout.BeginHorizontal();GUILayout.Label(label,GUILayout.Width(250));value=GUILayout.TextField(value);GUILayout.EndHorizontal();return value;}
        void CreateLegacyWozOffer()
        {
            if(legacyWozClosed||legacyWozProcess!=null||!legacyWozScope||!legacyWozPlaintext||participant==null||participant.HasNamedOption||
                (participant.RawRecordingRequested?!RecordedLegacyWozReady:namedRawPump!=null)||recording!=null||!participant.InputReady||rig==null||!rig.HeadReady)
                throw new InvalidOperationException("Current exact Legacy mode and explicit local development choice required");
            var local=new IPEndPoint(IPAddress.Parse(legacyWozLocalIp),Int32.Parse(legacyWozLocalPort,CultureInfo.InvariantCulture));
            var remote=new IPEndPoint(IPAddress.Parse(legacyWozRemoteIp),Int32.Parse(legacyWozRemotePort,CultureInfo.InvariantCulture));
            double age=Double.Parse(legacyWozMaximumAge,CultureInfo.InvariantCulture);
            var owner=CreateLegacyWozTaskBinding();
            try
            {
                if(RecordedLegacyWoz)ViveLegacyWozRecordingBinding.CreateInstalled(owner,legacyWozInstalledAdmission,legacyWozInstalledWorker,namedRawPump);
                using(var offer=ViveLegacyWozProcessPairing.CreateFreshParticipantOffer(owner,Actor.Owner,"local-participant-owner",legacyWozOperatorClock))
                {
                    legacyWozProcess=ViveLegacyWozParticipantProcess.Create(offer,owner,local,remote,age);
                    legacyWozScene=new ViveLegacyWozControlledScene(legacyWozProcess,new OwnedLegacySink(this));
                    Camera.onPreCull+=GuardLegacyWozCamera;Camera.onPostRender+=GuardLegacyWozCamera;
                    char[] secret=offer.RevealToken();try{legacyWozOffer=new string(secret);}finally{Array.Clear(secret,0,secret.Length);}
                }
                legacyWozStatus="Start operator with this offer first, then start participant here.";
            }
            catch{CloseLegacyWozTaskBinding();throw;}
        }
        void StartLegacyWoz()
        {
            if(legacyWozProcess==null||legacyWozClosed||legacyWozStarted)return;
            legacyWozOffer="";
            if(!legacyWozProcess.Start()){legacyWozStatus="Start failed: "+legacyWozProcess.LastIssue;CloseLegacyWozScene();return;}
            legacyWozStarted=true;legacyWozStatus="Waiting for operator and tracking.";
        }
        bool PollLegacyWozBeforeTask()
        {
            if(legacyWozScene==null||legacyWozProcess==null)return false;
            if(RecordedLegacyWoz&&participant.Completed){legacyWozScene.AfterTaskMutation();return true;}
            bool ready=legacyWozScene.PumpBeforeTask();
            if(legacyWozProcess.Terminal)
                legacyWozStatus=legacyWozProcess.SourceRevokeAcknowledged?"Paused; operator stop acknowledged.":
                    "Stopped: "+legacyWozProcess.LastIssue+". No automatic resume.";
            else legacyWozStatus=ready?"Connected; current task ready.":"Waiting for current operator data; task hidden.";
            return ready;
        }
        void RevokeLegacyWoz()
        {
            legacyWozOffer="";
            bool sent=legacyWozProcess!=null&&!legacyWozProcess.Terminal&&legacyWozProcess.RevokeSource();
            legacyWozStatus=sent?"Paused; waiting for operator stop acknowledgment.":"Stop requested. Check cleanup status: "+(legacyWozProcess==null?"not connected":legacyWozProcess.LastIssue);
        }
        void CloseLegacyWozScene()
        {
            pendingLegacyWozOffer=null;
            legacyWozOffer="";
            Camera.onPreCull-=GuardLegacyWozCamera;Camera.onPostRender-=GuardLegacyWozCamera;
            if(legacyWozProcess==null&&legacyWozScene==null)return;
            legacyWozClosed=true;
            if(legacyWozScene!=null)legacyWozScene.Dispose();else if(legacyWozProcess!=null)legacyWozProcess.Dispose();
        }
        void GuardLegacyWozCamera(Camera camera)
        {if(legacyWozStarted&&legacyWozScene!=null&&view!=null&&camera==view.PresentationCamera)legacyWozScene.RecheckPresentation();}
        void DrawLegacyWozControls()
        {
            if(participant==null||participant.HasNamedOption||(participant.RawRecordingRequested?!RecordedLegacyWozReady:namedRawPump!=null)||recording!=null)return;
            GUILayout.Space(8);GUILayout.Label(RecordedLegacyWoz?"Legacy WoZ development | Participant-local recording | Hardware/alignment unvalidated":"Legacy WoZ development | Recording off | Hardware/alignment unvalidated");
            GUILayout.Label("Authenticated messages, not encrypted. Answers, ratings and raw eyes stay local.",recordingWrap);
            if(RecordedLegacyWoz)GUILayout.Label("Local files also include mapped operator head/eye poses and source times. Operator must accept this mode.",recordingWrap);
            GUILayout.Label(legacyWozStatus,recordingWrap);
            if(legacyWozProcess==null&&!legacyWozClosed&&pendingLegacyWozOffer==null&&participant.Phase==Phase.BlockPreparation)
            {
                legacyWozOperatorClock=WozInput("Existing operator clock ID",legacyWozOperatorClock);
                legacyWozLocalIp=WozInput("Participant local IPv4",legacyWozLocalIp);legacyWozLocalPort=WozInput("Participant local port",legacyWozLocalPort);
                legacyWozRemoteIp=WozInput("Operator IPv4",legacyWozRemoteIp);legacyWozRemotePort=WozInput("Operator port",legacyWozRemotePort);
                legacyWozMaximumAge=WozInput("Connection timeout (seconds)",legacyWozMaximumAge);
                legacyWozScope=GUILayout.Toggle(legacyWozScope,RecordedLegacyWoz?"Use LiveWoZ for all four recorded blocks.":"Use LiveWoz for all four blocks; no recording.");
                legacyWozPlaintext=GUILayout.Toggle(legacyWozPlaintext,"Allow plaintext messages on this isolated laboratory LAN.");
                GUI.enabled=legacyWozScope&&legacyWozPlaintext&&participant.InputReady&&rig.HeadReady;
                if(GUILayout.Button("Select LiveWoz and create offer"))
                {
                    if(RecordedLegacyWoz)
                    {
                        // OnGUI has no staged eye epoch. Capture the choice,
                        // then run the owned factory on the next rig/Poll frame.
                        string clock=legacyWozOperatorClock,local=legacyWozLocalIp,localPort=legacyWozLocalPort,
                            remote=legacyWozRemoteIp,remotePort=legacyWozRemotePort,age=legacyWozMaximumAge;
                        pendingLegacyWozOffer=()=>{legacyWozOperatorClock=clock;legacyWozLocalIp=local;legacyWozLocalPort=localPort;
                            legacyWozRemoteIp=remote;legacyWozRemotePort=remotePort;legacyWozMaximumAge=age;CreateLegacyWozOffer();};
                    }
                    else try{CreateLegacyWozOffer();}catch(Exception e){legacyWozStatus="Offer rejected: "+e.GetType().Name;}
                }
                GUI.enabled=true;
            }
            if(!String.IsNullOrEmpty(legacyWozOffer))
            {GUILayout.Label("Private temporary offer: copy to operator, then start operator first.",recordingWrap);GUILayout.TextField(legacyWozOffer);}
            if(RecordedLegacyWoz)GUILayout.Label("Keep this participant view focused. Transfer the offer to the separate operator PC; focus loss stops recording.",recordingWrap);
            GUI.enabled=legacyWozProcess!=null&&!legacyWozClosed&&!legacyWozStarted;
            if(GUILayout.Button("Operator started: connect participant"))StartLegacyWoz();
            GUI.enabled=legacyWozStarted&&legacyWozScene!=null&&legacyWozScene.CanBeginCountdown;
            if(GUILayout.Button("Begin current block")){if(RecordedLegacyWoz)RequestNamedBlockPlanStart(false);else legacyWozScene.BeginCountdown();Render();}
            GUI.enabled=legacyWozProcess!=null&&!legacyWozProcess.Terminal;
            if(GUILayout.Button("Revoke source and pause task"))RevokeLegacyWoz();
            GUI.enabled=true;
        }
    }
}
#endif
