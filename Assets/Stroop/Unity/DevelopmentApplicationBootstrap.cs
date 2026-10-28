using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
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
        OwnerCommandDispatcher ownerCommands;
        static readonly KeyCode[] colourKeys={KeyCode.Alpha1,KeyCode.Alpha2,KeyCode.Alpha3,KeyCode.Alpha4};

        void Start()
        {
            Application.runInBackground=true;
            view=GetComponent<WorldSpaceExperimentView>();
            if(view==null)throw new InvalidOperationException("Development bootstrap needs its role's world-space view");
            if(view.Role==Actor.Participant)
            {
                Transform root=view.AvatarRoot;Vector3 origin=view.AvatarHead.position;
                Vector3 participantDirection=root.InverseTransformDirection(view.PresentationCamera.transform.position-origin);
                Vector3 taskDirection=root.InverseTransformDirection(view.TaskWorldPosition-origin);
                var geometry=DevelopmentGeometry.FromLocalDirections(ToVec(participantDirection),ToVec(taskDirection),"development-scene-root-local-v1");
                participant=new ParticipantSimulationApplication(simulationSessionId,participantPort,operatorPort,geometry);
                view.SetQuestionnaireLanguage(participant.Experiment.Configuration.QuestionnaireLanguage);
                view.StimulusRendered+=AcknowledgeStimulus;
                view.AvatarPresented+=RecordAvatarPresentation;
                string[] args=Environment.GetCommandLineArgs();
                for(int i=0;i<args.Length;i++)if(args[i]=="--stroop-owner-commands")
                {
                    if(ownerCommands!=null||i+1>=args.Length)throw new ArgumentException("One absolute owner command path is required");
                    ownerCommands=new OwnerCommandDispatcher(args[++i],participant.Identity.SessionId,c=>
                        OwnerCommandExecution.Execute(c,participant.OwnerBeginPractice,participant.OwnerLockNextBlock,participant.OwnerBeginBlock,ExportSnapshot));
                }
            }
            else operatorApp=new OperatorSimulationApplication(simulationSessionId,operatorPort,participantPort,false);
        }

        void Update()
        {
            if(view==null)return;
            try
            {
                if(participant!=null)
                {
                    participant.Tick();
                    if(ownerCommands!=null)ownerCommands.DispatchPending();
                    ReadParticipantInput();participant.RefreshPresentation();
                    view.ApplyTaskView(participant.Experiment.View(Actor.Participant));
                    if(participant.CurrentAvatar!=null)view.ApplyAvatarOutput(participant.CurrentAvatar);
                    else view.ClearAvatarOutput();
                    var warnings=new List<string>();
                    if(participant.Experiment.LockedControl==ControlSelection.SimulatorRehearsal && participant.CurrentFaults!=null)
                    {
                        warnings.AddRange(participant.CurrentFaults.Warnings.Select(x=>x.Kind+": "+x.Message));
                        warnings.AddRange(participant.ApplicationWarnings);
                    }
                    if(actionError!=null)warnings.Add(actionError);
                    view.SetFaults(warnings);
                }
                else if(operatorApp!=null)
                {
                    if(Input.GetKeyDown(KeyCode.P))operatorApp.RequestSafetyStop();
                    operatorApp.Tick();view.ApplyTaskView(operatorApp.View());
                    var warnings=new List<string>();
                    if(operatorApp.CurrentTask==null)warnings.Add("Task stream unavailable or stale. No participant eye/body synchronization.");
                    if(operatorApp.StopDelivery.Pending)warnings.Add("SAFETY STOP PENDING — delivery is not acknowledged.");
                    else if(operatorApp.StopDelivery.Acknowledged)warnings.Add("Safety stop acknowledged by participant application.");
                    view.SetFaults(warnings);
                }
            }
            catch(Exception error){actionError=error.Message;view.SetFaults(new[]{actionError});}
        }

        void ReadParticipantInput()
        {
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
                "participant app monotonic observation of camera-post-render callback; not photon onset","desktop new-key edge; not validated controller timing"));
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
            if(!Application.isPlaying||participant==null)throw new InvalidOperationException("Owner controls require the participant simulation application in Play mode");
            try{action();actionError=null;}catch(Exception error){actionError=error.Message;Debug.LogWarning(actionError);}
        }
        void OnDestroy()
        {
            if(view!=null){view.StimulusRendered-=AcknowledgeStimulus;view.AvatarPresented-=RecordAvatarPresentation;}
            if(ownerCommands!=null)ownerCommands.Dispose();
            if(participant!=null)participant.Dispose();if(operatorApp!=null)operatorApp.Dispose();
        }
    }
}
