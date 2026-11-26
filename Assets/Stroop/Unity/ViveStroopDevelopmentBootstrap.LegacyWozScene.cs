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
        readonly ViveWozWearerAvatarChoice avatarChoice=new ViveWozWearerAvatarChoice();
        object AvatarChoiceSession
        {
            get
            {
                if(recordingSetup!=null&&recordingSetupDesign==0&&legacyWozRecordingSelected&&
                    recordingSetup.Current!=ViveNamedRecordingSetup.Step.Cancelled&&recordingSetup.Current!=ViveNamedRecordingSetup.Step.Declined)
                    return recordingSetup;
                return configured&&ownerPreviewWoz&&participant!=null&&!participant.RawRecordingRequested&&!participant.HasNamedOption?participant:null;
            }
        }
        internal bool ShowWozAvatarChoice(ViveStartupView prompt,bool trackedAndFocused)
        {
            var session=AvatarChoiceSession;
            if(session==null||legacyWozClosed||legacyWozProcess!=null||namedRawPump!=null||!avatarChoice.Pending(session))return false;
            int frame=Time.frameCount;
            bool witnessed=prompt.WasRenderedBefore(frame);
            const string question="Share your avatar with the experimenter?\nDuring trials they can see your head turns and gaze.\nThe selected connection may use Photon cloud relay.\nTrigger: share   |   Trackpad click: keep private\nRelease all controls before choosing.";
            prompt.Show(trackedAndFocused,question);
            var input=ReadStartupAlignmentInput(trackedAndFocused);
            avatarChoice.Observe(frame,witnessed,trackedAndFocused&&input!=null&&input.DeviceReady&&!input.DeviceChanged,
                input!=null&&input.TriggerHeld,input!=null&&input.Trackpad.Pressed,input!=null&&input.MenuHeld);
            return true;
        }
        internal bool LiveParticipantAvatarEnabled {get{return legacyWozStarted&&legacyWozProcess!=null&&!legacyWozProcess.Terminal&&
            legacyWozProcess.ParticipantAvatarSharingDeclared&&avatarChoice.Granted(AvatarChoiceSession);}}
        internal bool UnrecordedWozEyeWarmup {get{return LiveParticipantAvatarEnabled&&!RecordedLegacyWoz&&participant!=null&&
            (participant.Phase==Phase.BlockPreparation||participant.Phase==Phase.BlockReady||participant.Phase==Phase.Countdown||participant.Phase==Phase.MainTask);}}
        internal void UpdateParticipantAvatar(ViveProEyeObservation observation,Quaternion trackingToClassroom,MonotonicClock viewClock,bool trackingCurrent)
        {
            if(!LiveParticipantAvatarEnabled)return;
            if(trackingCurrent&&object.ReferenceEquals(viewClock,sharedClock)&&!PresentationBlocked&&!FocusHeldLiveWoz&&
                !focusRecoveryPending&&participant!=null&&participant.Phase==Phase.MainTask&&observation!=null)
            {
                Rotation head;Vec3 eyes;
                if(TryMapParticipantAvatar(observation,trackingToClassroom,out head,out eyes))
                {
                    legacyWozProcess.TrySendParticipantAvatarPose(head,eyes,observation.Sample.CaptureLocalSeconds);
                    return;
                }
                // A faster display may see the same still-valid native eye frame twice.
                // Keep its original network expiry; never resend it with a new time.
                var raw=observation.RawPoll;
                if(observation.HeadUsable&&observation.EyeDisposition==ViveFrameDisposition.Duplicate&&raw!=null&&
                    raw.Runtime==ViveEyeRuntimeState.Working&&raw.Calibration==ViveEyeCalibrationState.ReportedReady&&
                    raw.EyeDeviceConnected&&raw.Eye!=null&&!raw.Eye.NoUser&&raw.Eye.Combined!=null&&
                    raw.Eye.Combined.DirectionReportedValid&&(raw.Eye.Combined.ValidityMask&2UL)!=0&&
                    (!raw.NativeResultCode.HasValue||raw.NativeResultCode.Value==0))return;
            }
            legacyWozProcess.ClearParticipantAvatarPose();
        }
        internal static bool TryMapParticipantAvatar(ViveProEyeObservation observation,Quaternion trackingToClassroom,out Rotation head,out Vec3 eyes)
        {
            head=default(Rotation);eyes=default(Vec3);
            if(observation==null||!observation.HeadUsable||!observation.EyeUsable||
                observation.MappingVersion!=ViveProEyeRehearsalCoordinates.MappingVersion||
                observation.Sample==null||!observation.Sample.HeadValid||!observation.Sample.EyeValid)return false;
            var sample=observation.Sample;
            var rotation=sample.RawHeadRotation;
            var trackingHead=new Quaternion((float)rotation.X,(float)rotation.Y,(float)rotation.Z,(float)rotation.W);
            var classroomHead=trackingToClassroom*trackingHead;
            var mapped=sample.RawEyeDirection;
            // The shared Vive mapper has already reflected SDK X and applied the physical head rotation.
            var headLocal=Quaternion.Inverse(trackingHead)*new Vector3((float)mapped.X,(float)mapped.Y,(float)mapped.Z);
            head=new Rotation(classroomHead.x,classroomHead.y,classroomHead.z,classroomHead.w);
            eyes=new Vec3(headLocal.x,headLocal.y,headLocal.z);
            return head.IsUnit&&eyes.IsUnit;
        }
        bool RecordedLegacyWoz {get{return participant!=null&&participant.LegacyWozRecordingDeclared;}}
        bool RecordedLegacyWozReady {get{return RecordedLegacyWoz&&ViveLegacyWozRecordingBuildPolicy.Available&&
            namedRawPump!=null&&legacyWozInstalledAdmission!=null&&legacyWozInstalledWorker!=null&&
            legacyWozInstalledWorker.UsesInstalledSdkPath&&legacyWozInstalledAdmission.UsesExactSources(participant,legacyWozInstalledWorker,sharedClock)&&
            namedRawPump.UsesRecordingBinding(participant,legacyWozInstalledWorker,sharedClock,legacyWozInstalledAdmission);}}
        internal bool WaitForLegacyWozRawAcquisition()
        {
            if(!RecordedLegacyWoz||namedRawPump==null)return false;
            // A negative-only focus suspension is not source acquisition or a
            // positive pose. Keep the true Paused status panel available.
            if(FocusHeldLiveWoz&&legacyWozProcess!=null&&legacyWozProcess.FocusSuspendStarted)
                return participant.Phase!=Phase.Paused;
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
            public bool LocallyReady {get{return scene!=null&&scene.isActiveAndEnabled&&
                System.Object.ReferenceEquals(scene.participant,task)&&System.Object.ReferenceEquals(scene.view,target)&&System.Object.ReferenceEquals(scene.rig,rig)&&
                rig!=null&&rig.HeadReady&&rig.XrFocused&&task.InputReady&&!scene.PresentationBlocked&&scene.recording==null&&
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
            if(legacyWozClosed||legacyWozProcess!=null||!legacyWozScope||!legacyWozPlaintext||!avatarChoice.Answered(AvatarChoiceSession)||participant==null||participant.HasNamedOption||
                (participant.RawRecordingRequested?!RecordedLegacyWozReady:namedRawPump!=null)||recording!=null||!participant.InputReady||rig==null||!rig.HeadReady)
                throw new InvalidOperationException("Current exact Legacy mode and explicit local development choice required");
            IPEndPoint local=null,remote=null;
            if(easyWozLobby==null)
            {
                local=new IPEndPoint(IPAddress.Parse(legacyWozLocalIp),Int32.Parse(legacyWozLocalPort,CultureInfo.InvariantCulture));
                remote=new IPEndPoint(IPAddress.Parse(legacyWozRemoteIp),Int32.Parse(legacyWozRemotePort,CultureInfo.InvariantCulture));
            }
            double age=Double.Parse(legacyWozMaximumAge,CultureInfo.InvariantCulture);
            var owner=CreateLegacyWozTaskBinding();
            try
            {
                if(RecordedLegacyWoz)ViveLegacyWozRecordingBinding.CreateInstalled(owner,legacyWozInstalledAdmission,legacyWozInstalledWorker,namedRawPump);
                using(var offer=ViveLegacyWozProcessPairing.CreateFreshParticipantOffer(owner,Actor.Owner,"local-participant-owner",legacyWozOperatorClock,avatarChoice.Granted(AvatarChoiceSession)))
                {
                    legacyWozProcess=easyWozLobby!=null?
                        ViveLegacyWozParticipantProcess.CreatePhoton(offer,owner,easyWozLobby,age):
                        ViveLegacyWozParticipantProcess.Create(offer,owner,local,remote,age);
                    legacyWozScene=new ViveLegacyWozControlledScene(legacyWozProcess,new OwnedLegacySink(this));
                    Camera.onPreCull+=GuardLegacyWozCamera;Camera.onPostRender+=GuardLegacyWozCamera;
                    char[] secret=offer.RevealToken();try{legacyWozOffer=new string(secret);}finally{Array.Clear(secret,0,secret.Length);}
                }
                legacyWozStatus="Start operator with this offer first, then start participant here.";
            }
            catch{CloseLegacyWozTaskBinding();legacyWozClosed=true;throw;}
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
            if(FocusHeldLiveWoz)return MaintainLegacyWozDuringFocusGap();
            if(RecordedLegacyWoz&&participant.Completed){legacyWozScene.AfterTaskMutation();return true;}
            bool ready=legacyWozScene.PumpBeforeTask();
            if(legacyWozProcess.Terminal)
            {
                DrainStoppedEasyWozPairing();
                legacyWozStatus=legacyWozProcess.SourceRevokeAcknowledged?"Paused; operator stop acknowledged.":
                    "Stopped: "+legacyWozProcess.LastIssue+". No automatic resume.";
            }
            else legacyWozStatus=ready?"Connected; current task ready.":"Waiting for current operator data; task hidden.";
            return ready;
        }
        bool MaintainLegacyWozDuringFocusGap()
        {
            if(!legacyWozStarted||legacyWozProcess==null)return true;
            legacyWozProcess.ClearParticipantAvatarPose();
            if(FocusHeldLiveWoz||participant!=null&&participant.IsXrFocusInteractionHeld||
                legacyWozProcess.FocusSuspendStarted||legacyWozProcess.FocusForwardingSuspending||legacyWozProcess.FocusForwardingSuspended)
            {
                if(!legacyWozProcess.TickFocusHoldSuspension())return false;
                if(namedRawPump!=null&&!legacyWozProcess.RecordFocusSuspensionMetadataForCurrentFrame())return false;
                if(legacyWozScene!=null&&!legacyWozScene.ClearForSafety())return false;
                legacyWozStatus=legacyWozProcess.FocusForwardingSuspended?
                    (legacyWozProcess.FocusForwardingCleanupUnknown?"Remote forwarding suspended; acknowledgment or cleanup remains unknown.":"Remote forwarding suspension acknowledged."):
                    "Requesting remote forwarding suspension; acknowledgment is pending.";
                return true;
            }
            // Continue authenticated task/status renewal and incoming packets,
            // without asking the scene to certify a hidden presentation. True
            // source, clock or transport errors keep their terminal handling.
            legacyWozProcess.TickMaintainedPreflight();
            if(legacyWozScene!=null)
            {
                if(!legacyWozScene.ClearForSafety())return false;
                legacyWozScene.AfterTaskMutation();
            }
            if(legacyWozProcess.Terminal)
            {DrainStoppedEasyWozPairing();legacyWozStatus="Stopped: "+legacyWozProcess.LastIssue+". No automatic resume.";return false;}
            legacyWozStatus="XR focus unavailable; protocol maintenance continues and task presentation is held.";
            return true;
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
            try
            {
                if(legacyWozProcess==null&&legacyWozScene==null)return;
                legacyWozClosed=true;
                if(legacyWozScene!=null)legacyWozScene.Dispose();else if(legacyWozProcess!=null)legacyWozProcess.Dispose();
            }
            finally{CloseEasyWozPairing();}
        }
        void GuardLegacyWozCamera(Camera camera)
        {
            if(!legacyWozStarted||legacyWozScene==null||view==null||camera!=view.PresentationCamera)return;
            if(rig!=null&&(!rig.XrFocused||FocusHeldLiveWoz||focusRecoveryPending))
            {if(!legacyWozScene.ClearForSafety()&&namedRawPump!=null)FailNamedRawBeforeFrame();return;}
            legacyWozScene.RecheckPresentation();
        }
        void DrawLegacyWozControls()
        {
            if(participant==null||participant.HasNamedOption||(participant.RawRecordingRequested?!RecordedLegacyWozReady:namedRawPump!=null)||recording!=null)return;
            GUILayout.Space(8);GUILayout.Label(RecordedLegacyWoz?"Legacy WoZ development | Participant-local recording | Hardware/alignment unvalidated":"Legacy WoZ development | Recording off | Hardware/alignment unvalidated");
            GUILayout.Label(manualWozPairing?"Manual LAN: authenticated messages, not encrypted. Answers, ratings and raw eyes stay local.":
                "Photon cloud relay: encrypted internet transport. Answers, ratings and raw eye files stay local.",recordingWrap);
            GUILayout.Label(avatarChoice.Answered(AvatarChoiceSession)?(avatarChoice.Granted(AvatarChoiceSession)?
                "Headset choice: share live head turns and gaze direction during trials.":"Headset choice: keep participant avatar private."):
                "Waiting for the participant's live avatar sharing choice in the headset.",recordingWrap);
            if(RecordedLegacyWoz)GUILayout.Label("Local files also include mapped operator head/eye poses and source times. Operator must accept this mode.",recordingWrap);
            GUILayout.Label(legacyWozStatus,recordingWrap);
            if(ownerDiagnostics&&!String.IsNullOrEmpty(easyWozTiming))
                GUILayout.Label((easyWozLobby!=null?"Current cloud timing\n":"Last cloud timing snapshot\n")+easyWozTiming,recordingWrap);
            DrawEasyWozPairingControls();
            if(manualWozPairing&&legacyWozProcess==null&&!legacyWozClosed&&pendingLegacyWozOffer==null&&participant.Phase==Phase.BlockPreparation)
            {
                legacyWozOperatorClock=WozInput("Existing operator clock ID",legacyWozOperatorClock);
                legacyWozLocalIp=WozInput("Participant local IPv4",legacyWozLocalIp);legacyWozLocalPort=WozInput("Participant local port",legacyWozLocalPort);
                legacyWozRemoteIp=WozInput("Operator IPv4",legacyWozRemoteIp);legacyWozRemotePort=WozInput("Operator port",legacyWozRemotePort);
                legacyWozMaximumAge=WozInput("Connection timeout (seconds)",legacyWozMaximumAge);
                legacyWozScope=GUILayout.Toggle(legacyWozScope,RecordedLegacyWoz?"Use LiveWoZ for all four recorded blocks.":"Use LiveWoz for all four blocks; no recording.");
                legacyWozPlaintext=GUILayout.Toggle(legacyWozPlaintext,"Allow plaintext messages on this isolated laboratory LAN.");
                GUI.enabled=legacyWozScope&&legacyWozPlaintext&&avatarChoice.Answered(AvatarChoiceSession)&&participant.InputReady&&rig.HeadReady;
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
            if(manualWozPairing&&!String.IsNullOrEmpty(legacyWozOffer))
            {GUILayout.Label("Private temporary offer: copy to operator, then start operator first.",recordingWrap);GUILayout.TextField(legacyWozOffer);}
            if(manualWozPairing)
            {
                if(RecordedLegacyWoz)GUILayout.Label("Transfer the offer to the separate operator PC. Focus changes are recorded; healthy raw capture continues while an unfocused task is held.",recordingWrap);
                GUI.enabled=legacyWozProcess!=null&&!legacyWozClosed&&!legacyWozStarted;
                if(GUILayout.Button("Operator started: connect participant"))StartLegacyWoz();
            }
            GUI.enabled=legacyWozStarted&&legacyWozScene!=null&&legacyWozScene.CanBeginCountdown;
            if(GUILayout.Button("Begin current block")){if(RecordedLegacyWoz)RequestNamedBlockPlanStart(false);else legacyWozScene.BeginCountdown();Render();}
            GUI.enabled=legacyWozProcess!=null&&!legacyWozProcess.Terminal;
            if(GUILayout.Button("Stop WoZ source and task"))RevokeLegacyWoz();
            GUI.enabled=true;
        }
    }

    internal sealed class ViveWozWearerAvatarChoice
    {
        object owner;
        bool? answer,pendingPress;
        bool armed;
        int lastFrame=-1;
        internal bool Answered(object session){return session!=null&&object.ReferenceEquals(owner,session)&&answer.HasValue;}
        internal bool Granted(object session){return Answered(session)&&answer==true;}
        internal void Reset(){owner=null;answer=null;pendingPress=null;armed=false;lastFrame=-1;}
        internal bool Pending(object session)
        {
            if(session==null)return false;
            if(!object.ReferenceEquals(owner,session)){owner=session;answer=null;pendingPress=null;armed=false;lastFrame=-1;}
            return !answer.HasValue;
        }
        internal void Observe(int frame,bool witnessed,bool ready,bool trigger,bool pad,bool menu)
        {
            if(owner==null||answer.HasValue||frame==lastFrame)return;
            bool continuous=lastFrame==frame-1;
            lastFrame=frame;
            if(!continuous||!ready||!witnessed||menu||trigger&&pad){armed=false;pendingPress=null;return;}
            if(!trigger&&!pad)
            {
                if(pendingPress.HasValue)answer=pendingPress;
                pendingPress=null;armed=true;return;
            }
            if(armed){pendingPress=trigger;armed=false;}
            else if(pendingPress.HasValue&&pendingPress.Value!=trigger)pendingPress=null;
        }
    }
}
#endif
