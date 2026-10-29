using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Text;
using AvatarStroop.Core;
using AvatarStroop.Runtime;
using UnityEngine;

namespace AvatarStroop.Unity
{
    // Development wiring only. Owner actions use explicit local file IPC or Editor
    // inspector commands. No participant selector or real-device admission path.
    public sealed class DevelopmentApplicationBootstrap : MonoBehaviour
    {
        [SerializeField] string simulationSessionId="development-synthetic-session";
        [SerializeField] int participantPort=44510;
        [SerializeField] int operatorPort=44511;
        [SerializeField] ControlSelection ownerNextBlockSource=ControlSelection.Automatic;
        WorldSpaceExperimentView view;
        ParticipantSimulationApplication participant;
        OperatorSimulationApplication operatorApp;
        string actionError;
        string recordingSetupError;
        string startupError,statusWriteError;
        DesktopPreviewOptions previewOptions;
        bool startupReleased;
        bool updateFailed;
        bool worldDiagnosticFailed;
        long statusSequence;
        float nextStatusAt;
        OwnerCommandDispatcher ownerCommands;
#if STROOP_XR_PREVIEW
        QuestLinkPreviewRig questPreview;
#endif
        static readonly KeyCode[] colourKeys={KeyCode.Alpha1,KeyCode.Alpha2,KeyCode.Alpha3,KeyCode.Alpha4};

        void Start()
        {
            Application.runInBackground=true;
            try
            {
                previewOptions=DesktopPreviewOptions.Parse(Environment.GetCommandLineArgs(),simulationSessionId,participantPort,operatorPort);
                startupReleased=previewOptions.StartupPath==null;
                view=GetComponent<WorldSpaceExperimentView>();
                if(view==null)throw new InvalidOperationException("Development bootstrap needs its role's world-space view");
#if STROOP_XR_PREVIEW
                questPreview=GetComponent<QuestLinkPreviewRig>();
#endif
                if(view.Role==Actor.Participant)
                {
                    Transform root=view.AvatarRoot;Vector3 origin=view.AvatarHead.position;
                    Vector3 participantDirection=root.InverseTransformDirection(view.PresentationCamera.transform.position-origin);
                    Vector3 taskDirection=root.InverseTransformDirection(view.TaskWorldPosition-origin);
                    var geometry=DevelopmentGeometry.FromLocalDirections(ToVec(participantDirection),ToVec(taskDirection),WorldSpaceExperimentView.GeometryVersion);
                    participant=new ParticipantSimulationApplication(previewOptions.SessionId,previewOptions.ParticipantPort,previewOptions.OperatorPort,geometry);
                    view.SetQuestionnaireLanguage(participant.Experiment.Configuration.QuestionnaireLanguage);
                    view.StimulusRendered+=AcknowledgeStimulus;
                    view.AvatarPresented+=RecordAvatarPresentation;
                    if(previewOptions.OwnerCommandsPath!=null)
                        ownerCommands=new OwnerCommandDispatcher(previewOptions.OwnerCommandsPath,participant.Identity.SessionId,ExecuteOwnerCommand);
                    view.ApplyTaskView(participant.Experiment.View(Actor.Participant));
                }
                else operatorApp=new OperatorSimulationApplication(previewOptions.SessionId,previewOptions.OperatorPort,previewOptions.ParticipantPort,false);
                ConfigureRecording();
                if(previewOptions.StartupPath!=null)DesktopPreviewStartup.WriteReady(previewOptions.StartupPath,previewOptions.SessionId,view.Role);
            }
            catch(Exception error)
            {
                startupError="Preview startup failed: "+error.Message;
                Debug.LogError(startupError);
                ShowFaults(new[]{startupError});
            }
            PublishStatus(true);
        }

        void ConfigureRecording()
        {
            try
            {
                string path=previewOptions.JournalPath;
                if(path==null)return;
                SimulationPeer app=participant!=null?(SimulationPeer)participant:operatorApp;
                app.EnableRecording(path,new ExportProvenance("unity-development-journal",
                    "participant app monotonic observation of camera-post-render callback; not photon onset",InputTimingProvenance),.1);
                if(app.RecordingWarning!=null)ShowFaults(new[]{app.RecordingWarning});
            }
            catch(Exception error){recordingSetupError="Recording setup failed: "+error.Message;ShowFaults(new[]{recordingSetupError});}
        }

        void Update()
        {
            if(startupError!=null||view==null)
            {
                ShowFaults(new[]{startupError,statusWriteError});
                PublishStatus(false);return;
            }
            if(!startupReleased)
            {
                try{startupReleased=DesktopPreviewStartup.IsReleased(previewOptions.StartupPath,previewOptions.SessionId);}
                catch(Exception error){startupError="Preview startup failed: "+error.Message;Debug.LogError(startupError);}
                if(!startupReleased)
                {
                    ShowFaults(new[]{startupError??"Waiting for both preview windows",statusWriteError});
                    PublishStatus(false);return;
                }
            }
            try
            {
                updateFailed=false;
                if(participant!=null)
                {
                    participant.Tick();
                    if(ownerCommands!=null)ownerCommands.DispatchPending();
                    ReadParticipantInput();participant.RefreshPresentation();participant.CaptureRecording();
                    view.ApplyTaskView(participant.Experiment.View(Actor.Participant));
                    if(participant.CurrentAvatar!=null)view.ApplyAvatarOutput(participant.CurrentAvatar);
                    else view.ClearAvatarOutput();
                    var warnings=RecordingWarnings(participant);
                    if(participant.CurrentFaults!=null)
                    {
                        bool selectedTracking=participant.Experiment.LockedControl==ControlSelection.SimulatorRehearsal;
                        warnings.AddRange(participant.CurrentFaults.Warnings
                            .Where(x=>selectedTracking||x.Kind==FaultKind.ConnectionFault||x.Kind==FaultKind.RecoveryDecisionRequired).Select(x=>x.Kind+": "+x.Message));
                        if(selectedTracking)warnings.AddRange(participant.ApplicationWarnings);
                    }
                    if(actionError!=null)warnings.Add(actionError);
                    ShowFaults(warnings);
                }
                else if(operatorApp!=null)
                {
                    if(Input.GetKeyDown(KeyCode.P))operatorApp.RequestSafetyStop();
                    operatorApp.Tick();view.ApplyTaskView(operatorApp.View());
                    var warnings=RecordingWarnings(operatorApp);
                    warnings.AddRange(operatorApp.Monitor.Snapshot(operatorApp.Clock.Now,operatorApp.Synchronizer).Warnings
                        .Where(x=>x.Kind==FaultKind.ConnectionFault||x.Kind==FaultKind.RecoveryDecisionRequired).Select(x=>x.Kind+": "+x.Message));
                    if(operatorApp.CurrentTask==null)warnings.Add("Task stream unavailable or stale. No participant eye/body synchronization.");
                    if(operatorApp.StopDelivery.Pending)warnings.Add("SAFETY STOP PENDING — delivery is not acknowledged.");
                    else if(operatorApp.StopDelivery.Acknowledged)warnings.Add("Safety stop acknowledged by participant application.");
                    if(actionError!=null)warnings.Add(actionError);
                    ShowFaults(warnings);
                }
            }
            catch(Exception error)
            {updateFailed=true;actionError=error.Message;var warnings=RecordingWarnings(participant!=null?(SimulationPeer)participant:operatorApp);warnings.Add(actionError);ShowFaults(warnings);}
            PublishStatus(false);
        }

        string ExecuteOwnerCommand(OwnerCommand command)
        {
            try
            {
#if STROOP_XR_PREVIEW
                if(questPreview!=null&&(command.Kind==OwnerCommandKind.Practice||command.Kind==OwnerCommandKind.Begin)&&!questPreview.InputReady)
                    return "Quest preview is not ready: "+questPreview.Diagnostic;
#endif
                string rejection=OwnerCommandExecution.Execute(command,participant.OwnerBeginPractice,participant.OwnerLockNextBlock,participant.OwnerBeginBlock,ExportSnapshot);
                actionError=rejection==null?null:"Owner action rejected: "+rejection;
                return rejection;
            }
            catch(Exception error){actionError="Owner action failed: "+error.Message;throw;}
        }

        void PublishStatus(bool force)
        {
            if(previewOptions==null||previewOptions.StatusPath==null||(view!=null&&view.Role!=Actor.Participant))return;
            if(!force&&Time.realtimeSinceStartup<nextStatusAt)return;
            nextStatusAt=Time.realtimeSinceStartup+.25f;
            try
            {
                var experiment=participant==null?null:participant.Experiment;
                var messages=new List<string>();
                if(startupError!=null)messages.Add(startupError);
                if(recordingSetupError!=null)messages.Add(recordingSetupError);
                if(participant!=null&&participant.RecordingWarning!=null)messages.Add(participant.RecordingWarning);
                if(actionError!=null)messages.Add(actionError);
                if(statusWriteError!=null)messages.Add(statusWriteError);
#if STROOP_XR_PREVIEW
                if(questPreview!=null)messages.Add("QUEST LINK PREVIEW | SIMULATED AVATAR GAZE | "+questPreview.Diagnostic);
#endif
                if(messages.Count==0)messages.Add(!startupReleased?"Waiting for both preview windows":ownerCommands==null?
                    "Open "+OwnerLauncherName+" for owner controls. Waiting for an explicit owner action.":"Development preview; waiting for explicit owner actions.");
                // A past rejected owner action remains visible, but does not replace
                // the currently locked source's readiness after that source recovers.
                bool ready=startupError==null&&startupReleased&&recordingSetupError==null&&!updateFailed&&experiment!=null&&
                    participant.RecordingWarning==null&&experiment.LockedControl.HasValue&&participant.SourceReady(experiment.LockedControl.Value);
#if STROOP_XR_PREVIEW
                if(questPreview!=null)ready=ready&&questPreview.InputReady;
#endif
                var status=new DesktopPreviewStatus(previewOptions.SessionId,statusSequence,experiment==null?Phase.Preparation:experiment.Phase,
                    experiment==null?-1:experiment.BlockIndex,experiment==null?null:experiment.CurrentCondition,
                    experiment==null?null:experiment.LockedControl,ready,BoundedMessage(String.Join("\n",messages)));
                DesktopPreviewStatus.Write(previewOptions.StatusPath,status);
                statusSequence=checked(statusSequence+1);statusWriteError=null;
            }
            catch(Exception error){statusWriteError="Preview status unavailable: "+error.Message;}
        }

        static string BoundedMessage(string message)
        {
            message=Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(message));
            if(message.Length>DesktopPreviewStatus.MaximumMessageBytes)message=message.Substring(0,DesktopPreviewStatus.MaximumMessageBytes);
            while(Encoding.UTF8.GetByteCount(message)>DesktopPreviewStatus.MaximumMessageBytes||
                (message.Length>0&&Char.IsHighSurrogate(message[message.Length-1])))message=message.Substring(0,message.Length-1);
            return message;
        }

        void ShowFaults(IEnumerable<string> messages)
        {
            if(view==null){worldDiagnosticFailed=true;return;}
            try{view.SetFaults(messages);worldDiagnosticFailed=false;}
            catch(Exception error)
            {
                if(!worldDiagnosticFailed)Debug.LogWarning("Preview diagnostic view unavailable: "+error.Message);
                worldDiagnosticFailed=true;
            }
        }

        void OnGUI()
        {
            // Quest instructions and actual errors already use the fixed room
            // presentation. Keep this emergency desktop fallback only when that
            // view is unavailable; do not duplicate a screen-attached menu.
            if(view!=null&&view.CanShowWorldDiagnostics&&!worldDiagnosticFailed)return;
            string notice=startupError??statusWriteError??actionError??recordingSetupError;
            if(notice==null&&participant!=null)notice=participant.RecordingWarning;
            if(notice==null&&operatorApp!=null)notice=operatorApp.RecordingWarning;
            if(notice==null&&participant!=null&&ownerCommands==null&&(view==null||(!view.HasWorldFixedMenu&&!view.CleanTaskPresentation)))
                notice="Open "+OwnerLauncherName+" to use the separate owner controls.\nThis participant window waits for an explicit owner action; no practice or source is selected automatically.";
            if(notice==null)return;
            var style=new GUIStyle(GUI.skin.box){fontSize=18,wordWrap=true,alignment=TextAnchor.UpperLeft};
            GUI.Box(new Rect(12,12,Mathf.Max(100,Mathf.Min(Screen.width-24,900)),150),notice,style);
        }

        List<string> RecordingWarnings(SimulationPeer app)
        {
            // The view has three warning slots. A recording failure must remain
            // visible when network/task faults already fill all of those slots.
            var warnings=new List<string>();
            if(app!=null&&app.RecordingWarning!=null)warnings.Add(app.RecordingWarning);
            if(recordingSetupError!=null)warnings.Add(recordingSetupError);
            if(statusWriteError!=null)warnings.Add(statusWriteError);
#if STROOP_XR_PREVIEW
            if(questPreview!=null&&!questPreview.InputReady)warnings.Add(questPreview.Diagnostic);
#endif
            return warnings;
        }

        string InputTimingProvenance
        {
            get
            {
#if STROOP_XR_PREVIEW
                if(questPreview!=null)return "Quest Touch preview new-press edge; host observation, not validated controller timing; synthetic gaze only";
#endif
                return "desktop new-key edge; not validated controller timing";
            }
        }
        static string OwnerLauncherName
        {
            get
            {
#if STROOP_XR_PREVIEW
                return "AvatarStroopQuestPreview.exe";
#else
                return "AvatarStroopPreview.exe";
#endif
            }
        }

        void ReadParticipantInput()
        {
#if STROOP_XR_PREVIEW
            if(questPreview!=null){ReadQuestPreviewInput();return;}
#endif
            var e=participant.Experiment;Phase inputPhase=e.Phase;
            for(int i=0;i<4;i++)
            {
                if(Input.GetKeyUp(colourKeys[i]))e.ButtonChanged((Colour)i,false);
                if(Input.GetKeyDown(colourKeys[i]))e.ButtonChanged((Colour)i,true);
            }
            if(inputPhase==Phase.Questionnaire)
            {
                for(int score=1;score<=5;score++)
                    if(Input.GetKeyDown((KeyCode)((int)KeyCode.Alpha1+score-1))){e.Rate(score);break;}
            }
            if(Input.GetKeyDown(KeyCode.Space))
            {
                if(inputPhase==Phase.PracticeCorrection)e.ContinuePractice();
                else if(inputPhase==Phase.Rest)e.Ready();
            }
            if(Input.GetKeyDown(KeyCode.P))e.Pause(Actor.Participant);
            if(Input.GetKeyDown(KeyCode.R)&&e.Phase==Phase.Paused)
                e.Resume(participant.SourceReady(e.LockedControl??ControlSelection.Automatic));
            if(Input.GetKeyDown(KeyCode.Escape))e.Withdraw();
        }

#if STROOP_XR_PREVIEW
        void ReadQuestPreviewInput()
        {
            var e=participant.Experiment;
            var frame=questPreview.ReadInput(e.Phase,e.QuestionIndex);
            foreach(var colour in frame.ReleasedColours)e.ButtonChanged(colour,false);
            // Consume XR edges even if a keyboard safety action takes priority.
            // Keyboard colour/rating/continue inputs are disabled for this preview
            // so two input devices cannot advance two trials or ratings in one frame.
            if(Input.GetKeyDown(KeyCode.Escape)){e.Withdraw();return;}
            if(Input.GetKeyDown(KeyCode.P)){e.Pause(Actor.Participant);return;}
            switch(frame.Action)
            {
                case QuestPreviewAction.Respond:e.ButtonChanged(frame.Response.Value,true);break;
                case QuestPreviewAction.SubmitRating:e.Rate(frame.Rating.Value);break;
                case QuestPreviewAction.ContinuePractice:e.ContinuePractice();break;
                case QuestPreviewAction.Ready:e.Ready();break;
                case QuestPreviewAction.Resume:e.Resume(questPreview.InputReady&&participant.SourceReady(e.LockedControl??ControlSelection.Automatic));break;
                case QuestPreviewAction.Pause:e.Pause(Actor.Participant);break;
                case QuestPreviewAction.Withdraw:e.Withdraw();break;
            }
        }
#endif

        void AcknowledgeStimulus(string trialId,double unityRenderProxySeconds)
        {
            if(participant==null)return;
            var e=participant.Experiment;
            if((e.Phase==Phase.Practice||e.Phase==Phase.MainTask)&&e.CurrentTrial!=null&&e.CurrentTrial.TrialId==trialId)
            {
                double observed=participant.Clock.Now;
                participant.Audit.RecordClock(new ClockAuditRecord(participant.CurrentAuditContext(),WorldSpaceExperimentView.RenderClockId,
                    participant.Clock.ClockId,"same-process-render-callback-pair-v1",observed,unityRenderProxySeconds-observed,null,null,
                    "camera_post_render_proxy_paired_at_callback;display_and_callback_delay_unmeasured"));
                e.MarkPresented(trialId);
                participant.CaptureRecording();
            }
            // Engine timestamp is the receiving app's monotonic callback observation.
            // unityRenderProxySeconds is a separate render clock; no photon-onset claim.
        }
        void RecordAvatarPresentation(AvatarPresentationObservation observed)
        {
            if(participant==null||participant.CurrentAvatar==null)return;
            if(observed.CapturedOutput==null)return;
            participant.Audit.RecordPresentation(new AvatarPresentationRecord(new AuditContext(observed.BlockIndex,observed.Segment,observed.TrialId),observed.ClockId,observed.RenderProxySeconds,
                observed.TimestampMeaning,observed.Phase,observed.CapturedOutput,"development_renderer_policy",observed.AppliedPose,
                observed.AnyAvatarRendererObserved,observed.HeadHeld,observed.EyesHeld,observed.VisibilityRequested));
            participant.CaptureRecording();
        }
        static Vec3 ToVec(Vector3 value){return new Vec3(value.x,value.y,value.z);}

        [ContextMenu("Owner / Begin or retry synthetic practice")]
        public void OwnerBeginPractice(){OwnerAction(()=>participant.OwnerBeginPractice());}
        [ContextMenu("Owner / Lock assigned block and selected source")]
        public void OwnerLockBlock(){OwnerAction(()=>participant.OwnerLockNextBlock(ownerNextBlockSource));}
        [ContextMenu("Owner / Confirm readiness and begin countdown")]
        public void OwnerBeginBlock(){OwnerAction(()=>participant.OwnerBeginBlock());}
        [ContextMenu("Owner / Export synthetic audit snapshot")]
        public void OwnerExport(){OwnerAction(ExportSnapshot);}
        void ExportSnapshot()
        {
            string folder=Path.GetFullPath(Path.Combine(Application.dataPath,"..","exports"));
            var result=participant.Export(folder,new ExportProvenance("unity-development-owner",
                "participant app monotonic observation of camera-post-render callback; not photon onset",InputTimingProvenance));
            if(!result.Success)throw new IOException(result.Error);
            Debug.Log("SIMULATION audit export: "+result.DirectoryPath);
        }
        [ContextMenu("Operator / Safety stop")]
        public void SafetyStop()
        {
            if(operatorApp!=null)operatorApp.RequestSafetyStop();
            else OwnerAction(()=>participant.Experiment.Pause(Actor.Operator));
        }
        void OwnerAction(Action action)
        {
            if(!Application.isPlaying||participant==null||startupError!=null||!startupReleased)throw new InvalidOperationException("Owner controls require an initialized participant preview with its startup barrier released");
            try{action();participant.CaptureRecording();actionError=null;}catch(Exception error){actionError=error.Message;Debug.LogWarning(actionError);}
        }
        void OnDestroy()
        {
            startupError="Preview closed";PublishStatus(true);
            if(view!=null){view.StimulusRendered-=AcknowledgeStimulus;view.AvatarPresented-=RecordAvatarPresentation;}
            if(ownerCommands!=null)ownerCommands.Dispose();
            if(participant!=null)participant.Dispose();if(operatorApp!=null)operatorApp.Dispose();
            if(participant!=null&&participant.RecordingWarning!=null)Debug.LogWarning(participant.RecordingWarning);
            if(operatorApp!=null&&operatorApp.RecordingWarning!=null)Debug.LogWarning(operatorApp.RecordingWarning);
        }
    }
}
