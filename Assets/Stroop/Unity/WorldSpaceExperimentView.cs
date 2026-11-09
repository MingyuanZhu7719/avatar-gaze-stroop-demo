using System;
using System.Collections.Generic;
using System.Text;
using AvatarStroop.Core;
using AvatarStroop.Runtime;
using UnityEngine;
using UnityEngine.Rendering;

namespace AvatarStroop.Unity
{
    // Development presentation only. Neither the primitive geometry nor the camera
    // render completion callback establishes headset visibility, latency or photon onset.
    public sealed class WorldSpaceExperimentView : MonoBehaviour
    {
        public const string RenderClockId = "unity-realtime-since-startup";
        public const string RenderTimestampMeaning = "camera-post-render-proxy-not-photon-onset";
        public const string GeometryVersion = "development-classroom-centered-reference-v4";
        public static readonly Vector3 CenteredParticipantReference = new Vector3(-.5f,1.6f,0);
        public static readonly Vector3 BoardPosition = new Vector3(-.5f,1.55f,1.94f);
        public static readonly Vector3 TaskDisplayPosition = new Vector3(-.5f,1.55f,1.90f);
        public const float DesktopVerticalFieldOfView = 64f;
        // One preview size for every word/ink; never fit or resize per trial.
        // fontSize remains 100 for glyph atlas quality, independently of world size.
        public const float StimulusCharacterSize = .0225f;
        [SerializeField] Camera presentationCamera;
        [SerializeField] Actor role = Actor.Participant;
        [SerializeField] Transform contentRoot;
        [SerializeField] Transform warningRoot;
        [SerializeField] Transform avatarRoot;
        [SerializeField] Transform avatarHead;
        [SerializeField] Transform leftEye;
        [SerializeField] Transform rightEye;
        [SerializeField] TextMesh stimulusText;
        [SerializeField] TextMesh phaseText;
        [SerializeField] TextMesh messageText;
        [SerializeField] TextMesh avatarLabel;
        [SerializeField] TextMesh faultText;
        [SerializeField] TextMesh provisionalText;
        [SerializeField] bool headsetPreview;
        // Explicit opt-in display-only VIVE profile. Until input and the task
        // bootstrap are configured it must not present Quest controls or a trial.
        [SerializeField] bool viveUnconfiguredPreview;
        [SerializeField] bool viveTaskPreview;
        [SerializeField] TextMesh previewInputText;
        [SerializeField] TextMesh previewStatusText;
#if STROOP_XR_PREVIEW
        int recordingChoiceRevision=-1,recordingChoiceCaptureFrame=-1,recordingChoiceObserved,recordingChoiceSeenRevision=-1,recordingChoiceSeenFrame=-1;
        string recordingChoiceExpected,recordingChoiceStatusCaptured,recordingChoiceControlsCaptured;
        const string RecordingChoiceControls="Release controls; Trigger: agree | Menu: decline";
#endif
        TextMesh developmentBanner;
        Transform headerBacking, footerBacking, worldMenuBacking;
        Transform controllerLegend;
        Transform viveControllerLegend;
        Transform phaseAnimationRoot, progressTrack, progressFill, instructionDemo, demoStick, demoConfirm, completionSparks;
        TextMesh phaseNumber, demoUp, demoDown, demoConfirmLabel, demoCaption;
        Transform viveInstructionDemo, viveDemoTouch, viveDemoTrigger, viveDemoRearWand, viveDemoTriggerCallout;
        TextMesh viveDemoUp, viveDemoDown, viveDemoTriggerLabel, viveSelectionText;
        int viveQuestionnaireRating=3;
        double countdownRemaining, countdownDuration, restRemaining, restDuration, instructionRemaining, instructionDuration;
        bool completed, questionnaireInstructionPending;
        int questionnaireAnsweredCount;
        double instructionAnimationStartedAt;
        bool questionnaireResponseVisible, questionnaireInstructionVisible, restResponseVisible;
        double completedAt;
        readonly Vector3[] hudCorners = new Vector3[4];
        readonly Vector3[] hudProjectionPoints = new Vector3[4], stimulusProjectionPoints = new Vector3[8];
        // The stimulus box is identical for every label in one layout pass, so it is
        // projected once per pass instead of once per label per eye.
        readonly Rect[] stimulusEyeBoxes = new Rect[3];
        readonly int[] stimulusEyeVisibility = new int[3];
        int layoutStamp, stimulusProjectionStamp = -1;
        string previewDiagnostic = "", previewControls = "", faultCaption = "";
        int hudDiagnosticsRemaining=3;
        float nextHudDiagnosticAt;
        bool lastHudStereo;
        bool faultCoversStimulus;
        bool hasPresentationFault;
        // A centred, bounded angular panel is easier to read through the headset
        // lenses than labels spread over the full render frustum. Distance alone
        // would not help because the text fitter otherwise scales with the frustum.
        const float HeadsetHudDistance = 2f;
        const float HeadsetHudWidthDegrees = 50f, HeadsetHudHeightDegrees = 40f;
        float HudDistance { get { return headsetPreview ? HeadsetHudDistance : 1.1f; } }
        public static readonly Vector3 FixedMenuPosition = new Vector3(-.5f,1.55f,1.90f);
        public const float FixedMenuWidth=1.35f, FixedMenuHeight=1.05f;
        public bool HasWorldFixedMenu { get { return headsetPreview&&role==Actor.Participant; } }
        // Input consumers combine this last completed layout result with the
        // engine's instruction gate and their existing device/readiness guards.
        // A fault panel or introduction must never accept an unseen rating.
        public bool QuestionnaireResponseVisible
        {
            get { return questionnaireResponseVisible&&role==Actor.Participant&&currentView!=null&&
                currentView.Phase==Phase.Questionnaire&&!questionnaireInstructionPending&&questionnaireLanguage!=null&&!hasPresentationFault&&
                !String.IsNullOrWhiteSpace(currentView.QuestionnaireItem)&&messageText!=null&&
                messageText.gameObject.activeInHierarchy&&messageText.GetComponent<MeshRenderer>().enabled&&
                (!viveTaskPreview||(viveSelectionText!=null&&viveSelectionText.gameObject.activeInHierarchy&&
                    viveSelectionText.GetComponent<MeshRenderer>().enabled)); }
        }
        public bool QuestionnaireInstructionVisible
        {
            get { return questionnaireInstructionVisible&&role==Actor.Participant&&QuestionnaireIntro&&
                questionnaireLanguage!=null&&!hasPresentationFault&&messageText!=null&&messageText.gameObject.activeInHierarchy&&
                messageText.GetComponent<MeshRenderer>().enabled&&(!headsetPreview||(previewInputText!=null&&
                    previewInputText.gameObject.activeInHierarchy&&previewInputText.GetComponent<MeshRenderer>().enabled))&&
                (!viveTaskPreview||(viveInstructionDemo!=null&&viveInstructionDemo.gameObject.activeInHierarchy&&
                    viveDemoTouch!=null&&viveDemoTouch.GetComponent<MeshRenderer>().enabled&&
                    viveDemoTrigger!=null&&viveDemoTrigger.GetComponent<MeshRenderer>().enabled&&
                    viveDemoRearWand!=null&&viveDemoRearWand.GetComponent<MeshRenderer>().enabled&&
                    viveDemoTriggerCallout!=null&&viveDemoTriggerCallout.GetComponent<MeshRenderer>().enabled&&
                    viveDemoUp!=null&&viveDemoUp.gameObject.activeInHierarchy&&viveDemoUp.GetComponent<MeshRenderer>().enabled&&
                    viveDemoDown!=null&&viveDemoDown.gameObject.activeInHierarchy&&viveDemoDown.GetComponent<MeshRenderer>().enabled&&
                    viveDemoTriggerLabel!=null&&viveDemoTriggerLabel.gameObject.activeInHierarchy&&viveDemoTriggerLabel.GetComponent<MeshRenderer>().enabled&&
                    demoCaption!=null&&demoCaption.gameObject.activeInHierarchy&&demoCaption.GetComponent<MeshRenderer>().enabled)); }
        }
        public bool RestResponseVisible
        {
            get { return restResponseVisible&&role==Actor.Participant&&currentView!=null&&currentView.Phase==Phase.Rest&&!hasPresentationFault&&
                messageText!=null&&messageText.gameObject.activeInHierarchy&&messageText.GetComponent<MeshRenderer>().enabled; }
        }
        // A/B automatic gaze targets a declared reference station, never live
        // HMD samples. Actual startup alignment preserves the tracked height.
        public Vector3 ParticipantReferencePosition
        {get{return HasWorldFixedMenu?CenteredParticipantReference:presentationCamera.transform.position;}}
        public bool CanShowWorldDiagnostics
        {
            get { return HasWorldFixedMenu&&isActiveAndEnabled&&contentRoot!=null&&warningRoot!=null&&
                warningRoot.parent==contentRoot&&warningRoot.gameObject.activeInHierarchy&&faultText!=null&&
                faultText.GetComponent<MeshRenderer>()!=null&&worldMenuBacking!=null&&
                presentationCamera!=null&&presentationCamera.isActiveAndEnabled; }
        }
        bool presentationAvailable = true;
        Func<bool> presentationAvailabilityCheck;
        readonly List<Material> runtimeMaterials = new List<Material>();
        readonly HashSet<string> acknowledgedTrials = new HashSet<string>(StringComparer.Ordinal);
        readonly List<string> faults = new List<string>();
        TaskView currentView;
        AvatarOutput currentOutput;
        string displayedTrial, capturedTrial;
        string capturedStimulusText;
        Color capturedStimulusColor;
        bool capturedStimulusContent;
        long presentationRevision, capturedRevision;
        int capturedFrame = -1;
        bool stimulusObservedByCamera;
        string poseWarning = "Avatar input is not connected.";
        string questionnaireLanguage;
        long avatarRevision, capturedAvatarRevision, capturedTaskRevision;
        int capturedAvatarFrame = -1;
        bool avatarObservedByCamera;
        AvatarPresentationObservation capturedAvatar;

        // Subscribe only for the participant application. Validate the current engine
        // trial again at the receiving boundary before calling Experiment.MarkPresented.
        public event Action<string, double> StimulusRendered;
        // VIVE raw recording needs the identity and actual TextMesh content from
        // this render pass, rather than a separately sampled current view. This
        // is a software render proxy, not proof of HMD visibility or persistence.
        internal sealed class StimulusRenderObservation
        {
            internal readonly string TrialId,Text;
            internal readonly long Revision;
            internal readonly int Frame;
            internal readonly double UnityRenderSeconds;
            internal readonly Color InkColor;
            internal StimulusRenderObservation(string trial,long revision,int frame,double time,string text,Color ink)
            {TrialId=trial;Revision=revision;Frame=frame;UnityRenderSeconds=time;Text=text;InkColor=ink;}
            internal bool MatchesTask(Colour word,Colour ink)
            {return !String.IsNullOrEmpty(TrialId)&&Revision>=0&&Frame>=0&&
                !Double.IsNaN(UnityRenderSeconds)&&!Double.IsInfinity(UnityRenderSeconds)&&UnityRenderSeconds>=0&&
                String.Equals(Text,word.ToString().ToUpperInvariant(),StringComparison.Ordinal)&&InkColor.Equals(Ink(ink));}
        }
        internal event Action<StimulusRenderObservation> ViveRawStimulusRendered;
        internal Func<bool> ViveRawObservationRequired;
        // A raw non-trial panel needs an actual participant-camera render pass,
        // not just an enabled TextMesh. It remains a software proxy, not proof
        // of either-eye photon delivery, comprehension, consent or persistence.
        internal sealed class ViveRawPanelObservation
        {
            internal readonly Phase Phase; internal readonly int Block,Frame,Rating,Answered;
            internal readonly bool Intro,Ready; internal readonly string Item,Correction,CountdownText;
            internal readonly long Revision; internal readonly double UnityRenderSeconds;
            internal ViveRawPanelObservation(Phase phase,int block,int frame,int rating,int answered,bool intro,bool ready,
                string item,string correction,string countdownText,long revision,double seconds)
            {Phase=phase;Block=block;Frame=frame;Rating=rating;Answered=answered;Intro=intro;Ready=ready;Item=item;Correction=correction;CountdownText=countdownText;Revision=revision;UnityRenderSeconds=seconds;}
        }
        internal Func<ViveRawPanelObservation,bool> ViveRawPanelRendered;
        ViveRawPanelObservation rawPanelExpected,rawPanelSeen;
        long rawPanelRevision; int rawPanelCaptureFrame=-1,rawPanelObserved,rawPanelRequired;
        string rawPanelPhaseText,rawPanelMessageText,rawPanelSelectionText,rawPanelCountdownText;
        // A failed status query must never assert that recording is off.
        // This is a display choice, not authority to record or expose a task.
        bool ViveRawAttached()
        {
            if(!viveTaskPreview||ViveRawObservationRequired==null)return false;
            try{return ViveRawObservationRequired();}catch(Exception){return true;}
        }
        internal static bool ViveRawPanelPhase(Phase phase)
        {return ViveRawPanelPolicy.IsPanelPhase(phase);}
        bool SameRawPanel(ViveRawPanelObservation sample,TaskView task,bool intro,int rating,int answered)
        {
            return sample!=null&&ViveRawPanelPolicy.SamePanelState(sample.Phase,sample.Block,sample.Intro,sample.Rating,
                sample.Answered,sample.Ready,sample.Item,sample.Correction,task,intro,rating,answered)&&
                (task.Phase!=Phase.Countdown||String.Equals(sample.CountdownText,Math.Ceiling(countdownRemaining).ToString("0"),StringComparison.Ordinal));
        }
        // Only the next owner update may consume a render; phase/item/rating
        // changes or a skipped frame require another camera witness.
        internal bool HasPriorViveRawPanelWitness(TaskView task,bool intro,int rating,int answered,int frame)
        {
            return ViveRawAttached()&&frame>=0&&rawPanelSeen!=null&&
                (long)frame-rawPanelSeen.Frame==1&&SameRawPanel(rawPanelSeen,task,intro,rating,answered)&&
                SameRawPanel(rawPanelSeen,currentView,questionnaireInstructionPending,viveQuestionnaireRating,questionnaireAnsweredCount);
        }
        internal bool MatchesViveRawPanel(ViveRawPanelObservation sample,TaskView task,bool intro,int rating,int answered)
        {return SameRawPanel(sample,task,intro,rating,answered)&&SameRawPanel(sample,currentView,questionnaireInstructionPending,viveQuestionnaireRating,questionnaireAnsweredCount);}
        internal bool TryPrepareViveRawPanel(int frame,out long revision)
        {
            revision=-1;rawPanelSeen=null;
            if(!ViveRawAttached()||role!=Actor.Participant||!viveTaskPreview||currentView==null||
                !ViveRawPanelPhase(currentView.Phase)||frame<0||rawPanelExpected!=null||rawPanelRevision==Int64.MaxValue)return false;
            bool intro=currentView.Phase==Phase.Questionnaire&&questionnaireInstructionPending;
            revision=++rawPanelRevision;
            rawPanelExpected=new ViveRawPanelObservation(currentView.Phase,currentView.BlockIndex,frame,
                currentView.Phase==Phase.Questionnaire&&!intro?viveQuestionnaireRating:0,
                currentView.Phase==Phase.Questionnaire?questionnaireAnsweredCount:0,intro,currentView.ReadyEnabled,
                currentView.QuestionnaireItem,currentView.PracticeCorrection,
                currentView.Phase==Phase.Countdown?Math.Ceiling(countdownRemaining).ToString("0"):null,revision,0);
            return true;
        }
        internal void CancelViveRawPanel()
        {rawPanelExpected=null;rawPanelSeen=null;rawPanelCaptureFrame=-1;rawPanelObserved=rawPanelRequired=0;}
        internal void SuppressViveRawPanel()
        {if(role==Actor.Participant&&viveTaskPreview&&warningRoot!=null)warningRoot.gameObject.SetActive(false);}
        internal bool TryRevealViveRawPanel()
        {
            if(!ViveRawAttached()||rawPanelExpected==null||warningRoot==null||role!=Actor.Participant)return false;
            warningRoot.gameObject.SetActive(true);
            return warningRoot.gameObject.activeInHierarchy;
        }
        static bool BindRawPanelElement(Transform element,WorldSpaceExperimentView owner,int bit)
        {
            if(element==null||!element.gameObject.activeInHierarchy)return false;
            var renderer=element.GetComponent<Renderer>();if(renderer==null||!renderer.enabled)return false;
            var witness=element.GetComponent<ViveRawPanelRenderWitness>();
            if(witness==null)witness=element.gameObject.AddComponent<ViveRawPanelRenderWitness>();
            witness.Bind(owner,bit);return true;
        }
        internal void ObserveViveRawPanelElement(Camera camera,int bit)
        {
            if(camera==presentationCamera&&rawPanelExpected!=null&&rawPanelCaptureFrame==Time.frameCount&&
                rawPanelExpected.Frame==Time.frameCount)rawPanelObserved|=bit;
        }
        public event Action<AvatarPresentationObservation> AvatarPresented;
        public Camera PresentationCamera { get { return presentationCamera; } }
        public Actor Role { get { return role; } }
        public Transform AvatarRoot { get { return avatarRoot; } }
        public Transform AvatarHead { get { return avatarHead; } }
        public Vector3 TaskWorldPosition { get { return stimulusText.transform.position; } }
        public bool OperatorViewProvisional { get; private set; }
        public bool HeadHeld { get; private set; } = true;
        public bool EyesHeld { get; private set; } = true;
        public bool CleanTaskPresentation
        {
            get { return headsetPreview&&role==Actor.Participant&&currentView!=null&&
                (currentView.Phase==Phase.Practice||currentView.Phase==Phase.MainTask); }
        }
        public AvatarPose AppliedPose
        {
            get
            {
                RequireLayout();
                Quaternion q = avatarHead.localRotation;
                Vector3 d = q * (leftEye.localRotation * Vector3.forward);
                return new AvatarPose(new Rotation(q.x, q.y, q.z, q.w), new Vec3(d.x, d.y, d.z));
            }
        }

        public void Initialize(Camera camera, Actor displayRole)
        {
            if(camera == null) throw new ArgumentNullException("camera");
            if(displayRole != Actor.Participant && displayRole != Actor.Operator)
                throw new ArgumentException("A presentation is participant or operator, never an owner mode selector.", "displayRole");
            if(contentRoot != null && role != displayRole)
                throw new InvalidOperationException("Create a separate presentation for each application role.");
            presentationCamera = camera; role = displayRole;
            if(contentRoot == null) BuildLayout();
            EnsureClassroomLayout();
            // The closer, full-menu-height board and standing feet span more
            // than the old 60-degree mono crop at this pitch. A 64-degree desktop
            // view retains both with margin, without moving camera/gaze anchors.
            // An established headset view keeps its tracked projection and pose.
            if(!headsetPreview)
            {
                camera.transform.rotation = Quaternion.Euler(15.5f, -4, 0);
                camera.fieldOfView = DesktopVerticalFieldOfView;
            }
            EnsureHudLayout();
            var witness = stimulusText.GetComponent<StimulusRenderWitness>();
            if(witness == null) witness = stimulusText.gameObject.AddComponent<StimulusRenderWitness>();
            witness.Bind(this);
            foreach(var avatarWitness in avatarRoot.GetComponentsInChildren<AvatarRenderWitness>(true)) avatarWitness.Bind(this);
            RefreshOverlayFontTextures(null);
            ResetCapture();
            RefreshWarnings();
            LayoutHud();
        }

        void Awake()
        {
            if(presentationCamera != null) Initialize(presentationCamera, role);
        }
        void OnEnable()
        {
            Camera.onPreCull += BeforeCameraCull;
            Camera.onPostRender += AfterCameraRender;
            Font.textureRebuilt += RefreshOverlayFontTextures;
        }
        void OnDisable()
        {
            Camera.onPreCull -= BeforeCameraCull;
            Camera.onPostRender -= AfterCameraRender;
            Font.textureRebuilt -= RefreshOverlayFontTextures;
            ResetCapture();
            CancelViveRawPanel();
            capturedAvatar = null; capturedAvatarFrame = -1;
        }
        void OnDestroy()
        {
            foreach(var material in runtimeMaterials)
                if(material != null) Destroy(material);
            if(Application.isPlaying && warningRoot != null) Destroy(warningRoot.gameObject);
        }
        void LateUpdate() { LayoutHud(); }

        public void ApplyTaskView(TaskView view)
        {ApplyTaskViewCore(view,true);}

        // Raw preparation changes only the hidden mesh. A main-camera mask
        // alone is not sufficient when other scene cameras are present.
        internal void PrepareViveRawTaskView(TaskView view)
        {
            if(!viveTaskPreview||role!=Actor.Participant)throw new InvalidOperationException("VIVE participant view required.");
            ApplyTaskViewCore(view,false);
        }
        void ApplyTaskViewCore(TaskView view,bool showTrialMesh)
        {
            RequireLayout();
            if(viveUnconfiguredPreview)throw new InvalidOperationException("VIVE classroom input and task are not configured.");
            if(view == null) throw new ArgumentNullException("view");
            bool taskPhase = view.Phase == Phase.MainTask || (role == Actor.Participant && view.Phase == Phase.Practice);
            string nextTrial = taskPhase && view.Stimulus != null && !String.IsNullOrEmpty(view.TrialId) ? view.TrialId : null;
            string nextWord = nextTrial == null ? "" : view.Stimulus.Word.ToString().ToUpperInvariant();
            Color nextInk = nextTrial == null ? Color.white : Ink(view.Stimulus.Ink);
            if(displayedTrial != nextTrial || stimulusText.text != nextWord || stimulusText.color != nextInk)
            {
                presentationRevision++;
                ResetCapture();
            }
            currentView = view; displayedTrial = nextTrial;
            stimulusText.text = nextWord; stimulusText.color = nextInk;
            stimulusText.gameObject.SetActive(nextTrial != null&&showTrialMesh);
            phaseText.text = (role == Actor.Participant ? "PARTICIPANT" : "OPERATOR") + "  |  " + view.Phase;
            OperatorViewProvisional = role == Actor.Operator && view.OperatorViewProvisional;
            provisionalText.text = OperatorViewProvisional ? "PROVISIONAL OPERATOR VIEW\nD / non-main display policy awaits review" : "";
            // No correctness, RT, response result, or score enters the operator view.
            SetMessageText(role == Actor.Operator ? "" : ParticipantMessage(view));
            RefreshAvatarVisibility();
            RefreshLegendVisibility();
            RefreshWarnings();
        }

        // Presentation consumes protocol time; it never advances a phase, enables
        // input, or decides that an interrupted experiment was completed.
        public void SetPhaseProgress(double countdownRemaining, double countdownDuration,
            double restRemaining, double restDuration, double instructionRemaining,
            double instructionDuration, bool completed, bool questionnaireInstructionPending=false, int questionnaireAnsweredCount=0)
        {
            this.countdownDuration=SafeSeconds(countdownDuration);
            this.countdownRemaining=Math.Min(SafeSeconds(countdownRemaining),this.countdownDuration);
            this.restDuration=SafeSeconds(restDuration);
            this.restRemaining=Math.Min(SafeSeconds(restRemaining),this.restDuration);
            this.instructionDuration=SafeSeconds(instructionDuration);
            this.instructionRemaining=Math.Min(SafeSeconds(instructionRemaining),this.instructionDuration);
            if(questionnaireInstructionPending&&!this.questionnaireInstructionPending)
                instructionAnimationStartedAt=Time.realtimeSinceStartupAsDouble;
            this.questionnaireInstructionPending=questionnaireInstructionPending;
            this.questionnaireAnsweredCount=Mathf.Clamp(questionnaireAnsweredCount,0,Questionnaire.Count);
            if(completed&&!this.completed)completedAt=Time.realtimeSinceStartupAsDouble;
            this.completed=completed;
        }
        static double SafeSeconds(double value)
        {return Double.IsNaN(value)||Double.IsInfinity(value)?0:Math.Max(0,value);}
        bool QuestionnaireIntro {get{return currentView!=null&&currentView.Phase==Phase.Questionnaire&&questionnaireInstructionPending;}}
        bool UsesPhasePanel
        {get{return HasWorldFixedMenu&&currentView!=null&&(currentView.Phase==Phase.Countdown||currentView.Phase==Phase.Questionnaire||
            currentView.Phase==Phase.Rest||(currentView.Phase==Phase.Ended&&completed));}}

        public void SetPresentationAvailable(bool available)
        {
            presentationAvailable = available;
            if(!available){ResetCapture();CancelViveRawPanel();}
        }

        public void SetPresentationAvailabilityCheck(Func<bool> check)
        {
            presentationAvailabilityCheck = check; ResetCapture();
        }
        bool PresentationIsAvailable()
        {
            return presentationAvailable && (presentationAvailabilityCheck == null || presentationAvailabilityCheck());
        }

        public void SetHeadsetPreview(string diagnostic, string controls)
        {
            bool wasViveUnconfigured=viveUnconfiguredPreview;
            viveUnconfiguredPreview=false;
            viveTaskPreview=false;
            if(viveControllerLegend!=null)viveControllerLegend.gameObject.SetActive(false);
            if(wasViveUnconfigured&&avatarRoot!=null)RefreshAvatarVisibility();
            SetHeadsetPreviewCore(diagnostic,controls);
        }

        public void SetViveUnconfiguredPreview(string diagnostic)
        {
#if STROOP_XR_PREVIEW
            recordingChoiceRevision=-1;recordingChoiceExpected=null;recordingChoiceSeenRevision=-1;
#endif
            if(currentView!=null)throw new InvalidOperationException("Cannot switch an active task to the unconfigured VIVE classroom view.");
            viveUnconfiguredPreview=true;
            viveTaskPreview=false;
            // A static placeholder is visible for spatial layout only. This is
            // not a condition, source, live operator, or task presentation.
            if(avatarRoot!=null)avatarRoot.gameObject.SetActive(true);
            SetHeadsetPreviewCore(diagnostic,"VIVE input unavailable. No study or practice is running.");
        }

        // Development-only pre-session choice. Rendering this notice does not
        // establish informed research consent or authorize a file operation.
        public void SetVivePreSessionRecordingChoice(string diagnostic)
        {
            if(currentView!=null)throw new InvalidOperationException("A task is already active.");
            viveUnconfiguredPreview=true;viveTaskPreview=false;
            if(avatarRoot!=null)avatarRoot.gameObject.SetActive(false);
            SetHeadsetPreviewCore(diagnostic,"Release controls; Trigger: rehearse choice | Menu: decline");
            SetFaults(new string[0]);
        }

        // Separate from the input rehearsal. This is a software selection,
        // not verification of wearer identity or informed study consent.
        internal void SetViveParticipantRecordingChoice(string diagnostic,int revision)
        {
            if(currentView!=null)throw new InvalidOperationException("A task is already active.");
            viveUnconfiguredPreview=true;viveTaskPreview=false;
            if(avatarRoot!=null)avatarRoot.gameObject.SetActive(false);
            SetHeadsetPreviewCore(diagnostic,"Release controls; Trigger: agree | Menu: decline");
#if STROOP_XR_PREVIEW
            if(recordingChoiceRevision!=revision||!String.Equals(recordingChoiceExpected,diagnostic,StringComparison.Ordinal))
            {recordingChoiceRevision=revision;recordingChoiceExpected=diagnostic;recordingChoiceSeenRevision=-1;recordingChoiceSeenFrame=-1;}
#endif
            SetFaults(new string[0]);
        }
#if STROOP_XR_PREVIEW
        internal bool WasViveRecordingChoiceRendered(int revision,int currentFrame)
        {return recordingChoiceSeenRevision==revision&&recordingChoiceSeenFrame>=0&&recordingChoiceSeenFrame==currentFrame-1;}
        internal void ObserveViveRecordingChoiceElement(Camera camera,int bit)
        {if(camera==presentationCamera&&recordingChoiceCaptureFrame==Time.frameCount)recordingChoiceObserved|=bit;}
        void PrepareViveRecordingChoiceRender(Camera camera)
        {
            recordingChoiceCaptureFrame=-1;recordingChoiceObserved=0;
            if(recordingChoiceRevision<0||recordingChoiceExpected==null||!viveUnconfiguredPreview||role!=Actor.Participant||
                GraphicsSettings.currentRenderPipeline!=null||!isActiveAndEnabled||camera==null||!camera.isActiveAndEnabled||
                camera.targetTexture!=null||camera.cullingMask==0||camera.rect.width<=0||camera.rect.height<=0||previewStatusText==null||previewInputText==null||
                !RecordingChoiceTextMatches(previewStatusText.text,recordingChoiceExpected)||
                !RecordingChoiceTextMatches(previewInputText.text,RecordingChoiceControls))return;
            if(!BindRecordingChoiceElement(previewStatusText,1)||!BindRecordingChoiceElement(previewInputText,2))return;
            recordingChoiceStatusCaptured=previewStatusText.text;recordingChoiceControlsCaptured=previewInputText.text;
            recordingChoiceCaptureFrame=Time.frameCount;
        }
        bool BindRecordingChoiceElement(TextMesh value,int bit)
        {
            if(value==null||!value.gameObject.activeInHierarchy)return false;
            var renderer=value.GetComponent<Renderer>();if(renderer==null||!renderer.enabled)return false;
            var witness=value.GetComponent<ViveRecordingChoiceRenderWitness>();
            if(witness==null)witness=value.gameObject.AddComponent<ViveRecordingChoiceRenderWitness>();
            witness.Bind(this,bit);return true;
        }
        void FinishViveRecordingChoiceRender(Camera camera)
        {
            if(camera!=presentationCamera||recordingChoiceCaptureFrame!=Time.frameCount||recordingChoiceObserved!=3||
                recordingChoiceRevision<0||!viveUnconfiguredPreview||!isActiveAndEnabled||!camera.isActiveAndEnabled||camera.targetTexture!=null||camera.cullingMask==0||
                camera.rect.width<=0||camera.rect.height<=0||!RecordingChoiceElementVisible(previewStatusText)||!RecordingChoiceElementVisible(previewInputText)||
                !String.Equals(previewStatusText.text,recordingChoiceStatusCaptured,StringComparison.Ordinal)||
                !String.Equals(previewInputText.text,recordingChoiceControlsCaptured,StringComparison.Ordinal)||
                !RecordingChoiceTextMatches(previewStatusText.text,recordingChoiceExpected)||
                !RecordingChoiceTextMatches(previewInputText.text,RecordingChoiceControls))return;
            recordingChoiceSeenRevision=recordingChoiceRevision;recordingChoiceSeenFrame=Time.frameCount;
        }
        static bool RecordingChoiceTextMatches(string shown,string expected)
        {
            if(String.IsNullOrWhiteSpace(shown)||String.IsNullOrWhiteSpace(expected))return false;
            return String.Equals(String.Join(" ",shown.Split((char[])null,StringSplitOptions.RemoveEmptyEntries)),
                String.Join(" ",expected.Split((char[])null,StringSplitOptions.RemoveEmptyEntries)),StringComparison.Ordinal);
        }
        static bool RecordingChoiceElementVisible(TextMesh value)
        {if(value==null||!value.gameObject.activeInHierarchy)return false;var renderer=value.GetComponent<Renderer>();return renderer!=null&&renderer.enabled;}
#endif

        // VIVE raw candidate has no trusted per-trial open receipt. A camera
        // cull alone cannot protect another camera, so explicitly hide the
        // stimulus mesh as well. This is never invoked by the Quest path.
        internal void SuppressViveRawStimulus()
        {
            if(role==Actor.Participant&&stimulusText!=null)
                stimulusText.gameObject.SetActive(false);
        }
        // Identity of the task mesh prepared while the camera is masked.
        // This is a local view check, not permission to reveal the mesh.
        internal bool TryGetViveRawTrialIdentity(string trialId,Colour word,Colour ink,out long revision)
        {
            revision=-1;
            if(!viveTaskPreview||role!=Actor.Participant||currentView==null||currentView.Stimulus==null||
                String.IsNullOrEmpty(trialId)||!String.Equals(displayedTrial,trialId,StringComparison.Ordinal)||
                !String.Equals(currentView.TrialId,trialId,StringComparison.Ordinal)||currentView.Stimulus.Word!=word||currentView.Stimulus.Ink!=ink||
                stimulusText==null||stimulusText.gameObject.activeSelf||contentRoot==null||!contentRoot.gameObject.activeInHierarchy||
                !String.Equals(stimulusText.text,word.ToString().ToUpperInvariant(),StringComparison.Ordinal)||!stimulusText.color.Equals(Ink(ink)))return false;
            revision=presentationRevision;return revision>=0;
        }
        internal bool TryRevealViveRawTrial(string trialId,Colour word,Colour ink,long expectedRevision)
        {
            long current;
            if(!TryGetViveRawTrialIdentity(trialId,word,ink,out current)||current!=expectedRevision)return false;
            stimulusText.gameObject.SetActive(true);
            return stimulusText.gameObject.activeInHierarchy&&presentationRevision==expectedRevision&&
                String.Equals(displayedTrial,trialId,StringComparison.Ordinal)&&
                String.Equals(stimulusText.text,word.ToString().ToUpperInvariant(),StringComparison.Ordinal)&&stimulusText.color.Equals(Ink(ink));
        }

        // Only an explicitly configured, complete mapping may enter this
        // developer-only presentation. It never enables the apparatus gate.
        public void ConfigureViveDevelopmentPreview(IDictionary<ViveTrackpadDirection,Colour> mapping,double threshold)
        {
            if(currentView!=null)throw new InvalidOperationException("Configure VIVE before a task is created.");
            var validation=new VivePreviewTrackpadInput(mapping,threshold);
            if(validation==null)throw new InvalidOperationException("Invalid trackpad configuration.");
            viveUnconfiguredPreview=false;viveTaskPreview=true;
            EnsureViveControllerLegend(mapping);
            RefreshAvatarVisibility();
            SetHeadsetPreviewCore("VIVE development simulation; eye data is not recorded.","Trackpad: select | Trigger: confirm / continue");
            SetFaults(new string[0]);
        }

        string vivePauseDiagnostic;
        bool viveLegacyPreviewNotice;
        // Presentation only. This cannot enable recording or alter task/input gates.
        public void SetViveLegacyPreviewNotice(bool enabled){viveLegacyPreviewNotice=enabled;}
        bool ShowViveLegacyPreviewNotice {get{return viveLegacyPreviewNotice&&(viveTaskPreview||viveUnconfiguredPreview);}}
        public void SetVivePauseDiagnostic(string diagnostic){vivePauseDiagnostic=diagnostic;}
        public void SetViveDevelopmentStatus(string diagnostic,string controls)
        {
            if(!viveTaskPreview)throw new InvalidOperationException("VIVE development presentation has not been configured.");
            SetHeadsetPreviewCore(diagnostic,controls);
        }

        // Display-only mirror of the participant's candidate; it never selects
        // or submits a rating. Bootstrap updates this before applying each view.
        public void SetViveQuestionnaireRating(int selected)
        {
            if(!viveTaskPreview)throw new InvalidOperationException("VIVE development presentation has not been configured.");
            if(selected<1||selected>5)throw new ArgumentOutOfRangeException("selected");
            viveQuestionnaireRating=selected;
        }

        void SetHeadsetPreviewCore(string diagnostic, string controls)
        {
            RequireLayout();
            string nextDiagnostic = diagnostic ?? "", nextControls = controls ?? "";
            // Called several times per frame by the Quest rig; only relayout when a
            // rendered string actually changes.
            bool changed = !headsetPreview || nextDiagnostic != previewDiagnostic || nextControls != previewControls;
            headsetPreview = true;
            if(!viveUnconfiguredPreview&&!viveTaskPreview)EnsureControllerLegend();
            RefreshLegendVisibility();
            EnsureHudLayout();
            previewDiagnostic = nextDiagnostic; previewControls = nextControls;
            if(previewStatusText == null)
            {
                previewStatusText = Text("QuestPreviewRuntimeStatus", warningRoot, Vector3.zero, .02f, 48, Color.white);
                SetOverlay(previewStatusText);
            }
            if(previewInputText == null)
            {
                previewInputText = Text("QuestPreviewInputStatus", warningRoot, Vector3.zero, .02f, 48, Color.white);
                SetOverlay(previewInputText);
            }
            if(previewStatusText.text != previewDiagnostic) previewStatusText.text = previewDiagnostic;
            if(previewInputText.text != previewControls) previewInputText.text = previewControls;
            if(currentView != null && role == Actor.Participant) SetMessageText(ParticipantMessage(currentView));
            RefreshWarnings();
            if(changed) LayoutHud();
        }

        // Quest menus are fixed to the room, including faults and interrupted
        // phases. Desktop labels retain their existing camera layout. Neither
        // path changes stimulus/avatar transforms or presentation witnesses.
        void EnsureHudLayout()
        {
            if(warningRoot == null) return;
            // Called on every Quest diagnostic update, so parenting work is conditional
            // and the banner lookup tolerates a scene authored before it existed.
            var parent = HasWorldFixedMenu?contentRoot:presentationCamera.transform;
            if(warningRoot.parent != parent) warningRoot.SetParent(parent,false);
            warningRoot.localPosition=HasWorldFixedMenu?FixedMenuPosition:new Vector3(0,0,HudDistance);
            warningRoot.localRotation=Quaternion.identity;
            warningRoot.localScale=Vector3.one;
            var banner = warningRoot.Find("PermanentDevelopmentBanner");
            if(banner == null)
            {
                // A scene authored before the banner existed still gets one instead of
                // failing the whole layout pass.
                developmentBanner = Text("PermanentDevelopmentBanner", warningRoot, Vector3.zero, .019f, 48, new Color(1, .75f, .2f));
                SetOverlay(developmentBanner);
            }
            else developmentBanner = banner.GetComponent<TextMesh>();
            foreach(var label in new[]{phaseText,messageText,provisionalText,avatarLabel})
            {
                if(label == null) continue;
                if(label.transform.parent != warningRoot) label.transform.SetParent(warningRoot, false);
                label.transform.localRotation = Quaternion.identity;
                var material = label.GetComponent<MeshRenderer>().sharedMaterial;
                if(material == null || material.shader == null || material.shader.name != "AvatarStroop/WorldSpaceOverlay") SetOverlay(label);
            }
            headerBacking = warningRoot.Find("WarningBacking");
            footerBacking = warningRoot.Find("HudFooterBacking");
            if(footerBacking == null)
            {
                var backing = Primitive("HudFooterBacking",PrimitiveType.Quad,warningRoot,Vector3.zero,Vector3.one,Color.black);
                var material = NewMaterial(Shader.Find("AvatarStroop/WorldSpaceOverlay"),new Color(.025f,.025f,.035f,.94f));
                material.renderQueue = 4990; backing.GetComponent<MeshRenderer>().sharedMaterial = material;
                footerBacking = backing.transform;
            }
            if(HasWorldFixedMenu)
            {
                worldMenuBacking=warningRoot.Find("FixedWorldMenuBacking");
                if(worldMenuBacking==null)
                {
                    var backing=Primitive("FixedWorldMenuBacking",PrimitiveType.Quad,warningRoot,Vector3.zero,Vector3.one,Color.black);
                    var material=NewMaterial(Shader.Find("AvatarStroop/WorldSpaceOverlay"),new Color(.025f,.025f,.035f,1));
                    material.renderQueue=4990;backing.GetComponent<MeshRenderer>().sharedMaterial=material;
                    worldMenuBacking=backing.transform;
                }
                EnsurePhaseAnimation();
            }
        }

        Rect HudFrustum(Camera.MonoOrStereoscopicEye eye)
        {
            presentationCamera.CalculateFrustumCorners(new Rect(0,0,1,1),HudDistance,eye,hudCorners);
            float minX=Single.PositiveInfinity,minY=minX,maxX=Single.NegativeInfinity,maxY=maxX;
            foreach(var corner in hudCorners)
            {minX=Mathf.Min(minX,corner.x);maxX=Mathf.Max(maxX,corner.x);minY=Mathf.Min(minY,corner.y);maxY=Mathf.Max(maxY,corner.y);}
            return Rect.MinMaxRect(minX,minY,maxX,maxY);
        }
        static Rect Intersect(Rect a,Rect b)
        {return Rect.MinMaxRect(Mathf.Max(a.xMin,b.xMin),Mathf.Max(a.yMin,b.yMin),Mathf.Min(a.xMax,b.xMax),Mathf.Min(a.yMax,b.yMax));}
        static Rect HudBox(Rect frustum,Rect viewport)
        {
            // Layout rectangles use a conventional top-left origin.
            return new Rect(frustum.xMin+viewport.x*frustum.width,frustum.yMax-(viewport.y+viewport.height)*frustum.height,
                viewport.width*frustum.width,viewport.height*frustum.height);
        }
        void LayoutHud()
        {
            questionnaireResponseVisible=false;
            questionnaireInstructionVisible=false;
            restResponseVisible=false;
            layoutStamp++;
            if(warningRoot == null || developmentBanner == null || presentationCamera == null) return;
            var frustum=HasWorldFixedMenu?new Rect(-FixedMenuWidth*.5f,-FixedMenuHeight*.5f,FixedMenuWidth,FixedMenuHeight):HudFrustum(Camera.MonoOrStereoscopicEye.Mono);
            if(!HasWorldFixedMenu&&presentationCamera.stereoEnabled)
            {
                // One shared layout for MultiPass, never a per-active-eye change.
                frustum=Intersect(frustum,HudFrustum(Camera.MonoOrStereoscopicEye.Left));
                frustum=Intersect(frustum,HudFrustum(Camera.MonoOrStereoscopicEye.Right));
            }
            if(frustum.width<=.05f||frustum.height<=.05f) return;
            // Inset the render bounds, then keep headset labels in a central
            // angular panel. Render bounds alone do not establish lens visibility.
            if(!HasWorldFixedMenu)frustum=new Rect(frustum.x+frustum.width*.06f,frustum.y+frustum.height*.04f,frustum.width*.88f,frustum.height*.92f);
            if(headsetPreview&&!HasWorldFixedMenu)
            {
                float halfWidth=HudDistance*Mathf.Tan(HeadsetHudWidthDegrees*.5f*Mathf.Deg2Rad);
                float halfHeight=HudDistance*Mathf.Tan(HeadsetHudHeightDegrees*.5f*Mathf.Deg2Rad);
                frustum=Intersect(frustum,Rect.MinMaxRect(-halfWidth,-halfHeight,halfWidth,halfHeight));
                if(frustum.width<=.05f||frustum.height<=.05f) return;
            }
            if(!HasWorldFixedMenu)warningRoot.localPosition=new Vector3(0,0,HudDistance);
            // Invalid/degenerate layouts above keep the previous conservative
            // gate; only a completed layout may clear an obscuring fault.
            faultCoversStimulus=false;
            if(viveUnconfiguredPreview)
            {
                // This stage provides a room view only. It cannot display task
                // prompts or an unapproved controller mapping as usable controls.
                if(phaseAnimationRoot!=null)phaseAnimationRoot.gameObject.SetActive(false);
                PositionBacking(headerBacking,frustum,new Rect(),false);
                PositionBacking(footerBacking,frustum,new Rect(),false);
                PositionBacking(worldMenuBacking,frustum,new Rect(0,0,1,1),true);
                FitHud(developmentBanner,ViveStroopRecordingScopePresentation.Banner(ShowViveLegacyPreviewNotice,"VIVE CLASSROOM PREVIEW | NOT A STUDY RUN"),frustum,new Rect(.04f,.03f,.92f,.08f),.034f,2);
                FitHud(phaseText,"Input not configured",frustum,new Rect(.04f,.17f,.92f,.08f),.04f,2);
                FitHud(messageText,"Classroom view only. No task or practice is running.",frustum,new Rect(.04f,.30f,.92f,.16f),.04f,3);
                FitHud(previewStatusText,previewDiagnostic,frustum,new Rect(.04f,.52f,.92f,.14f),.034f,4);
                FitHud(previewInputText,ViveStroopRecordingScopePresentation.Controls(ShowViveLegacyPreviewNotice,previewControls),frustum,new Rect(.04f,.69f,.92f,.14f),.034f,ShowViveLegacyPreviewNotice?100:4);
                FitHud(faultText,faultCaption,frustum,new Rect(.04f,.84f,.92f,.12f),.03f,4);
                FitHud(provisionalText,"",frustum,new Rect(),.02f,1);
                FitHud(avatarLabel,"",frustum,new Rect(),.02f,1);
                return;
            }
            bool trial=currentView!=null&&(currentView.Phase==Phase.Practice||currentView.Phase==Phase.MainTask);
            if(phaseAnimationRoot!=null)phaseAnimationRoot.gameObject.SetActive(false);
            if(CleanTaskPresentation)
            {
                foreach(var label in new[]{developmentBanner,phaseText,messageText,provisionalText,avatarLabel,previewStatusText,previewInputText})
                    if(label!=null){label.text="";label.GetComponent<MeshRenderer>().enabled=false;}
                PositionBacking(headerBacking,frustum,new Rect(0,0,1,.185f),false);
                PositionBacking(footerBacking,frustum,new Rect(0,.695f,1,.30f),false);
                // Real faults still have a dedicated readable area. Healthy status,
                // introductory text and the duplicate controller list stay hidden.
                var faultArea=new Rect(.02f,.73f,.96f,.24f);
                if(!HasWorldFixedMenu&&HudOverlapsStimulus(faultArea,frustum))faultArea=new Rect(.02f,.02f,.96f,.24f);
                PositionBacking(worldMenuBacking,frustum,faultArea,!String.IsNullOrWhiteSpace(faultCaption));
                FitHud(faultText,faultCaption,frustum,faultArea,.032f,7);
                if(!String.IsNullOrWhiteSpace(faultCaption)&&!faultText.GetComponent<MeshRenderer>().enabled)
                {
                    // A real fault must not silently disappear if the head moves
                    // the stimulus across both slots. Keep the fault visible and
                    // withhold presentation acknowledgement for the covered word.
                    faultText.GetComponent<MeshRenderer>().enabled=true;
                    faultCoversStimulus=true;
                }
                LogHudProjection(frustum);
                return;
            }
            if(UsesPhasePanel)
            {
                LayoutPhasePanel(frustum);
                LogHudProjection(frustum);
                return;
            }
            string heading=viveTaskPreview?(ViveRawAttached()?"VIVE DEVELOPMENT TASK | RECORDING STATUS UNVERIFIED\nSIMULATION | NOT A STUDY RUN":"VIVE DEVELOPMENT TASK | NO DATA SAVED\nSIMULATION | NOT A STUDY RUN"):headsetPreview?"QUEST LINK PREVIEW | SIMULATED GAZE\nHEAD VIEW ONLY | NO EYE TRACKING | NOT A STUDY RUN":"DESKTOP SIMULATION | HARDWARE UNVERIFIED";
            FitHud(developmentBanner,ViveStroopRecordingScopePresentation.Banner(ShowViveLegacyPreviewNotice,heading),frustum,new Rect(.02f,.02f,.96f,.09f),.034f,2);
            // Always format from original state, never re-wrap an already fitted
            // TextMesh string. Resizing must recover the complete source text.
            string phase=(role==Actor.Participant?"PARTICIPANT":"OPERATOR")+" | "+(currentView==null?"Initializing":currentView.Phase.ToString());
            string provisional=OperatorViewProvisional?"PROVISIONAL OPERATOR VIEW\nD / non-main display policy awaits review":"";
            string instructions=currentView==null||role==Actor.Operator||trial?"":ParticipantMessage(currentView);
            string scope=role==Actor.Operator?"PARTICIPANT AVATAR PLACEHOLDER\nStatic; no reverse head / eye tracking":"PROVISIONAL AVATAR GEOMETRY\nFixed body; shared head / eye renderer";
            FitHud(phaseText,phase,frustum,new Rect(.02f,.13f,.96f,.045f),.034f,1);
            FitHud(provisionalText,provisional,frustum,new Rect(.04f,.20f,.92f,.075f),.031f,2);
            FitHud(messageText,instructions,frustum,
                role==Actor.Participant?new Rect(.04f,.21f,.92f,.40f):new Rect(.04f,.29f,.92f,.32f),.035f,100);
            FitHud(avatarLabel,scope,frustum,new Rect(.02f,.62f,.96f,.065f),.026f,2);
            if(headsetPreview)
            {
                FitHud(faultText,faultCaption,frustum,new Rect(.02f,.70f,.96f,.085f),.028f,3);
                FitHud(previewStatusText,previewDiagnostic,frustum,new Rect(.02f,.79f,.96f,.08f),.031f,3);
                FitHud(previewInputText,ViveStroopRecordingScopePresentation.Controls(ShowViveLegacyPreviewNotice,previewControls),frustum,new Rect(.02f,.885f,.96f,.095f),.031f,ShowViveLegacyPreviewNotice?100:3);
            }
            else FitHud(faultText,faultCaption,frustum,new Rect(.02f,.73f,.96f,.23f),.031f,7);
            // Opaque panels and centre instructions must not cover a live trial.
            PositionBacking(headerBacking,frustum,new Rect(0,0,1,.185f),!trial);
            PositionBacking(footerBacking,frustum,new Rect(0,.695f,1,.30f),!trial);
            if(HasWorldFixedMenu)
            {
                // The instructions must have their own opaque surface: the
                // whiteboard's pale frame is not a reliable text background.
                headerBacking.gameObject.SetActive(false);footerBacking.gameObject.SetActive(false);
                PositionBacking(worldMenuBacking,frustum,new Rect(0,0,1,1),true);
            }
            questionnaireInstructionVisible=role==Actor.Participant&&QuestionnaireIntro;
            questionnaireResponseVisible=role==Actor.Participant&&currentView!=null&&currentView.Phase==Phase.Questionnaire&&!questionnaireInstructionPending;
            restResponseVisible=role==Actor.Participant&&currentView!=null&&currentView.Phase==Phase.Rest;
            LogHudProjection(frustum);
        }
        void EnsurePhaseAnimation()
        {
            if(phaseAnimationRoot!=null)return;
            phaseAnimationRoot=warningRoot.Find("PhaseProgressAnimation");
            if(phaseAnimationRoot!=null)
            {
                progressTrack=phaseAnimationRoot.Find("ProgressTrack");progressFill=phaseAnimationRoot.Find("ProgressFill");
                phaseNumber=phaseAnimationRoot.Find("PhaseRemainingSeconds").GetComponent<TextMesh>();
                instructionDemo=phaseAnimationRoot.Find("QuestionnaireControllerGuide");
                demoStick=instructionDemo.Find("AnimatedLeftStick");demoConfirm=instructionDemo.Find("AnimatedConfirmA");
                demoConfirmLabel=instructionDemo.Find("ConfirmButtonA").GetComponent<TextMesh>();
                demoUp=instructionDemo.Find("LeftStickUp").GetComponent<TextMesh>();demoDown=instructionDemo.Find("LeftStickDown").GetComponent<TextMesh>();
                demoCaption=phaseAnimationRoot.Find("ControllerGuideStep").GetComponent<TextMesh>();
                completionSparks=phaseAnimationRoot.Find("GentleCompletionFireworks");
                SetProgressRenderOrder();return;
            }
            phaseAnimationRoot=Child("PhaseProgressAnimation",warningRoot,Vector3.zero);
            progressTrack=FlowShape("ProgressTrack",PrimitiveType.Quad,phaseAnimationRoot,Vector3.zero,Vector3.one,new Color(.16f,.23f,.26f));
            progressFill=FlowShape("ProgressFill",PrimitiveType.Quad,phaseAnimationRoot,Vector3.zero,Vector3.one,new Color(.42f,.73f,.75f));
            SetProgressRenderOrder();
            phaseNumber=Text("PhaseRemainingSeconds",phaseAnimationRoot,Vector3.zero,.02f,64,new Color(.73f,.90f,.91f));SetOverlay(phaseNumber);
            instructionDemo=Child("QuestionnaireControllerGuide",phaseAnimationRoot,Vector3.zero);
            FlowShape("LeftControllerFace",PrimitiveType.Sphere,instructionDemo,new Vector3(-.29f,.06f,0),new Vector3(.20f,.27f,.02f),new Color(.36f,.42f,.45f));
            FlowShape("LeftControllerGrip",PrimitiveType.Capsule,instructionDemo,new Vector3(-.29f,-.055f,0),new Vector3(.085f,.06f,.02f),new Color(.36f,.42f,.45f));
            FlowShape("StickTravel",PrimitiveType.Quad,instructionDemo,new Vector3(-.29f,.065f,-.01f),new Vector3(.013f,.13f,1),new Color(.68f,.75f,.76f));
            demoStick=FlowShape("AnimatedLeftStick",PrimitiveType.Sphere,instructionDemo,new Vector3(-.29f,.065f,-.02f),new Vector3(.065f,.065f,.02f),new Color(.68f,.90f,.90f));
            FlowShape("RightControllerFace",PrimitiveType.Sphere,instructionDemo,new Vector3(.30f,.06f,0),new Vector3(.20f,.27f,.02f),new Color(.36f,.42f,.45f));
            FlowShape("RightControllerGrip",PrimitiveType.Capsule,instructionDemo,new Vector3(.30f,-.055f,0),new Vector3(.085f,.06f,.02f),new Color(.36f,.42f,.45f));
            demoConfirm=FlowShape("AnimatedConfirmA",PrimitiveType.Sphere,instructionDemo,new Vector3(.30f,.035f,-.02f),new Vector3(.080f,.080f,.02f),new Color(.68f,.90f,.90f));
            demoConfirmLabel=Text("ConfirmButtonA",instructionDemo,new Vector3(.30f,.035f,-.04f),.009f,64,new Color(.08f,.15f,.16f));demoConfirmLabel.text="A";SetOverlay(demoConfirmLabel);
            demoUp=Text("LeftStickUp",instructionDemo,Vector3.zero,.02f,48,Color.white);SetOverlay(demoUp);
            demoDown=Text("LeftStickDown",instructionDemo,Vector3.zero,.02f,48,Color.white);SetOverlay(demoDown);
            demoCaption=Text("ControllerGuideStep",phaseAnimationRoot,Vector3.zero,.02f,48,Color.white);SetOverlay(demoCaption);
            completionSparks=Child("GentleCompletionFireworks",phaseAnimationRoot,Vector3.zero);
            for(int burst=0;burst<2;burst++)for(int ray=0;ray<10;ray++)
                FlowShape("Spark"+burst+"_"+ray,PrimitiveType.Quad,completionSparks,Vector3.zero,new Vector3(.012f,.025f,1),
                    burst==0?new Color(.61f,.77f,.79f):new Color(.84f,.73f,.51f));
            phaseAnimationRoot.gameObject.SetActive(false);
        }
        void EnsureViveQuestionnaireVisuals()
        {
            if(!viveTaskPreview||viveInstructionDemo!=null)return;
            // Created only for VIVE: Quest retains its original stick/A guide.
            viveInstructionDemo=Child("ViveQuestionnaireControllerGuide",phaseAnimationRoot,Vector3.zero);
            FlowShape("ViveWandHead",PrimitiveType.Sphere,viveInstructionDemo,new Vector3(-.20f,.06f,0),new Vector3(.23f,.28f,.02f),new Color(.36f,.42f,.45f));
            FlowShape("ViveWandGrip",PrimitiveType.Capsule,viveInstructionDemo,new Vector3(-.20f,-.07f,0),new Vector3(.09f,.08f,.02f),new Color(.36f,.42f,.45f));
            FlowShape("ViveTrackpad",PrimitiveType.Sphere,viveInstructionDemo,new Vector3(-.20f,.075f,-.015f),new Vector3(.16f,.16f,.015f),new Color(.18f,.24f,.26f));
            viveDemoTouch=FlowShape("ViveAnimatedTrackpadPress",PrimitiveType.Sphere,viveInstructionDemo,new Vector3(-.20f,.115f,-.03f),new Vector3(.055f,.055f,.015f),new Color(.68f,.90f,.90f));
            // A second, side-rear view links the highlighted control to the
            // back of the same wand shape. The front trackpad remains on the
            // left; no grip or other button is highlighted as a trigger.
            FlowShape("ViveRearWandHead",PrimitiveType.Sphere,viveInstructionDemo,new Vector3(.21f,.095f,0),new Vector3(.15f,.22f,.02f),new Color(.36f,.42f,.45f));
            viveDemoRearWand=FlowShape("ViveRearWandGrip",PrimitiveType.Capsule,viveInstructionDemo,new Vector3(.21f,-.045f,0),new Vector3(.075f,.095f,.02f),new Color(.36f,.42f,.45f));
            viveDemoTrigger=FlowShape("ViveAnimatedTrigger",PrimitiveType.Capsule,viveInstructionDemo,new Vector3(.26f,.025f,-.02f),new Vector3(.042f,.035f,.02f),new Color(.68f,.90f,.90f));
            viveDemoTrigger.localEulerAngles=new Vector3(0,0,-18);
            viveDemoTriggerCallout=FlowShape("ViveRearTriggerCallout",PrimitiveType.Quad,viveInstructionDemo,new Vector3(.35f,.025f,-.015f),new Vector3(.13f,.004f,1),new Color(.68f,.90f,.90f));
            // Keep the label independent of the animated trigger for contrast.
            viveDemoTriggerLabel=Text("ViveTriggerLabel",viveInstructionDemo,Vector3.zero,.02f,48,Color.white);SetOverlay(viveDemoTriggerLabel);
            viveDemoUp=Text("ViveTrackpadUp",viveInstructionDemo,Vector3.zero,.02f,48,Color.white);SetOverlay(viveDemoUp);
            viveDemoDown=Text("ViveTrackpadDown",viveInstructionDemo,Vector3.zero,.02f,48,Color.white);SetOverlay(viveDemoDown);
            viveSelectionText=Text("ViveQuestionnaireSelection",phaseAnimationRoot,Vector3.zero,.02f,64,new Color(.68f,.90f,.90f));SetOverlay(viveSelectionText);
            viveInstructionDemo.gameObject.SetActive(false);viveSelectionText.gameObject.SetActive(false);
        }
        void SetProgressRenderOrder()
        {
            // Transparent distance sorting is insufficient: as a left-anchored
            // fill shrinks, its centre can be farther from the camera than the
            // track. ZTest Always then lets the opaque track paint over the fill.
            // Explicit queues preserve the fill from either eye and off-axis.
            progressTrack.GetComponent<Renderer>().sharedMaterial.renderQueue=4995;
            progressFill.GetComponent<Renderer>().sharedMaterial.renderQueue=4996;
        }
        Transform FlowShape(string name,PrimitiveType type,Transform parent,Vector3 position,Vector3 scale,Color colour)
        {
            var shape=Primitive(name,type,parent,position,scale,colour);
            var material=NewMaterial(Shader.Find("AvatarStroop/WorldSpaceOverlay"),colour);
            material.renderQueue=4995;shape.GetComponent<MeshRenderer>().sharedMaterial=material;
            return shape.transform;
        }
        void FitCentered(TextMesh label,string value,Rect frustum,Rect area,float lineHeight)
        {
            FitHud(label,value,frustum,area,lineHeight,100);
            var box=HudBox(frustum,area);var bounds=label.GetComponent<MeshRenderer>().localBounds;
            var scale=label.transform.localScale.x;
            label.transform.localPosition=new Vector3(box.center.x-bounds.center.x*scale,box.center.y-bounds.center.y*scale,-.002f);
        }
        void LayoutPhasePanel(Rect frustum)
        {
            Phase phase=currentView.Phase;bool questionnaire=phase==Phase.Questionnaire;
            bool intro=questionnaire&&QuestionnaireIntro,rest=phase==Phase.Rest,countdown=phase==Phase.Countdown;
            EnsureViveQuestionnaireVisuals();
            bool celebration=phase==Phase.Ended&&completed;
            foreach(var label in new[]{provisionalText,avatarLabel,previewStatusText})
                if(label!=null){label.text="";label.GetComponent<MeshRenderer>().enabled=false;}
            PositionBacking(headerBacking,frustum,new Rect(),false);PositionBacking(footerBacking,frustum,new Rect(),false);
            PositionBacking(worldMenuBacking,frustum,new Rect(0,0,1,1),true);
            FitHud(developmentBanner,ViveStroopRecordingScopePresentation.Banner(ShowViveLegacyPreviewNotice,viveTaskPreview?(ViveRawAttached()?"VIVE DEVELOPMENT TASK | RECORDING STATUS UNVERIFIED | NOT A STUDY RUN":"VIVE DEVELOPMENT TASK | NO DATA SAVED | NOT A STUDY RUN"):"QUEST PREVIEW | SIMULATED GAZE | NOT A STUDY RUN"),frustum,new Rect(.04f,.025f,.92f,.04f),.026f,2);
            if(!String.IsNullOrWhiteSpace(faultCaption))
            {
                // Essential faults take precedence over progress decorations.
                // This presentation does not itself pause/resume the protocol.
                FitCentered(phaseText,"Preview needs attention",frustum,new Rect(.04f,.08f,.92f,.08f),.050f);
                FitHud(messageText,"",frustum,new Rect(.04f,.18f,.92f,.1f),.04f,100);
                FitHud(faultText,faultCaption,frustum,new Rect(.04f,.22f,.92f,.48f),.043f,100);
                FitHud(previewInputText,ViveStroopRecordingScopePresentation.Controls(ShowViveLegacyPreviewNotice,"Please wait. Use the owner controls for recovery.\nThe session does not resume automatically."),frustum,new Rect(.04f,.79f,.92f,.15f),.035f,100);
                phaseAnimationRoot.gameObject.SetActive(false);return;
            }
            string title=questionnaire?(intro?"How to answer":"About the last block"):rest?"Rest":countdown?"Get ready":"Thank you!";
            FitCentered(phaseText,title,frustum,new Rect(.04f,.08f,.92f,.08f),.057f);
            string message=ParticipantMessage(currentView);
            if(questionnaire&&!intro)
            {
                FitHud(messageText,message,frustum,new Rect(.04f,.185f,.92f,viveTaskPreview?.49f:.54f),.045f,100);
                questionnaireResponseVisible=true;
            }
            else if(intro)
            {
                FitCentered(messageText,message,frustum,new Rect(.04f,.66f,.92f,viveTaskPreview?.14f:.16f),viveTaskPreview?.047f:.034f);
                questionnaireInstructionVisible=true;
            }
            else
                FitCentered(messageText,message,frustum,new Rect(.04f,.55f,.92f,.15f),.045f);
            restResponseVisible=rest;
            string controls=viveTaskPreview?(intro?"Ask the operator if you need to stop":"Click trackpad TOP/BOTTOM | Trigger: confirm / continue"):intro?"Press A when ready to open the questions.\nThe guide repeats until you continue.":
                questionnaire?previewControls:rest?(currentView.ReadyEnabled?"A: continue whenever you are ready. No minimum wait.":"Continue is not yet available."):
                countdown?"Keep the controllers ready.":"The owner can export the records. You may remove the headset.";
            FitHud(previewInputText,ViveStroopRecordingScopePresentation.Controls(ShowViveLegacyPreviewNotice,controls),frustum,new Rect(.04f,.81f,.92f,.105f),.032f,100);
            FitHud(faultText,faultCaption,frustum,new Rect(.04f,.925f,.92f,.065f),.026f,100);
            phaseAnimationRoot.gameObject.SetActive(true);
            phaseNumber.gameObject.SetActive(countdown||rest);instructionDemo.gameObject.SetActive(intro&&!viveTaskPreview);demoCaption.gameObject.SetActive(questionnaire);
            if(viveInstructionDemo!=null)viveInstructionDemo.gameObject.SetActive(intro&&viveTaskPreview);
            if(viveSelectionText!=null)viveSelectionText.gameObject.SetActive(questionnaire&&!intro&&viveTaskPreview);
            if(questionnaire&&!intro&&viveTaskPreview)
                FitCentered(viveSelectionText,ViveQuestionnairePresentation.Selection(viveQuestionnaireRating,questionnaireLanguage),frustum,new Rect(.05f,.70f,.90f,.045f),.032f);
            if(questionnaire&&!intro)
                FitCentered(demoCaption,questionnaireAnsweredCount+" of "+Questionnaire.Count+" answers submitted",frustum,new Rect(.08f,.78f,.84f,.025f),.024f);
            if(countdown||rest)
            {
                double remaining=countdown?countdownRemaining:restRemaining;
                string number=countdown?Math.Ceiling(remaining).ToString("0"):
                    Math.Floor(Math.Max(0,restDuration-restRemaining)).ToString("0")+(restRemaining<=0?"+":"");
                FitCentered(phaseNumber,number,frustum,new Rect(.25f,.245f,.50f,.25f),.22f);
                // A single smooth, small pulse per second; no flashing or camera motion.
                float pulse=1+.035f*Mathf.Sin((float)(remaining%1)*Mathf.PI);
                var centre=phaseNumber.GetComponent<Renderer>().localBounds.center;
                Vector3 anchor=phaseNumber.transform.localPosition+centre*phaseNumber.transform.localScale.x;
                phaseNumber.transform.localScale*=pulse;
                phaseNumber.transform.localPosition=anchor-centre*phaseNumber.transform.localScale.x;
            }
            if(intro&&!viveTaskPreview)
            {
                LayoutInstructionDemo(frustum,Time.realtimeSinceStartupAsDouble-instructionAnimationStartedAt);
            }
            if(intro&&viveTaskPreview)
                LayoutViveInstructionDemo(frustum,Time.realtimeSinceStartupAsDouble-instructionAnimationStartedAt);
            bool progress=countdown||rest||questionnaire;
            progressTrack.gameObject.SetActive(progress);progressFill.gameObject.SetActive(progress);
            if(progress)
            {
                var box=HudBox(frustum,new Rect(.08f,.755f,.84f,.018f));
                if(intro)box=HudBox(frustum,new Rect(.08f,.65f,.84f,.008f));
                // Countdown means remaining protocol time. Rest means elapsed
                // portion of the optional reference, and stays full afterwards.
                // The intro is an untimed, repeating demonstration, not a wait.
                float fraction=countdown?(countdownDuration<=0?0:Mathf.Clamp01((float)(countdownRemaining/countdownDuration))):
                    rest?(restDuration<=0?1:Mathf.Clamp01((float)(1-restRemaining/restDuration))):intro?1:questionnaireAnsweredCount/(float)Questionnaire.Count;
                progressTrack.localPosition=new Vector3(box.center.x,box.center.y,-.002f);progressTrack.localScale=new Vector3(box.width,box.height,1);
                progressFill.localPosition=new Vector3(box.xMin+box.width*fraction*.5f,box.center.y,-.004f);progressFill.localScale=new Vector3(box.width*fraction,box.height,1);
            }
            LayoutCompletionFireworks(celebration,Time.realtimeSinceStartupAsDouble-completedAt);
        }
        void LayoutInstructionDemo(Rect frustum,double elapsed)
        {
            double normalized=SafeSeconds(elapsed)%6/6;
            int step=(int)(normalized*3);float within=(float)(normalized*3-step);
            float travel=Mathf.Sin(within*Mathf.PI)*.060f*(step==0?1:step==1?-1:0);
            instructionDemo.localPosition=new Vector3(0,.015f,-.004f);
            demoStick.localPosition=new Vector3(-.29f,.065f+travel,-.02f);
            float size=.080f*(1+(step==2?.10f*Mathf.Sin(within*Mathf.PI):0));demoConfirm.localScale=new Vector3(size,size,.02f);
            FitCentered(demoUp,"UP / LOWER",frustum,new Rect(.13f,.255f,.31f,.045f),.030f);
            FitCentered(demoDown,"DOWN / HIGHER",frustum,new Rect(.13f,.60f,.31f,.045f),.030f);
            FitCentered(demoCaption,step==0?"1  LEFT STICK UP: 3 to 2":step==1?"2  LEFT STICK DOWN: 2 to 3":"3  PRESS A TO CONFIRM",frustum,new Rect(.04f,.18f,.92f,.055f),.038f);
            demoUp.color=step==0?new Color(.68f,.90f,.90f):Color.white;demoDown.color=step==1?new Color(.68f,.90f,.90f):Color.white;
        }
        void LayoutViveInstructionDemo(Rect frustum,double elapsed)
        {
            double time=SafeSeconds(elapsed);int step=ViveQuestionnairePresentation.GuideStep(time);
            float within=(float)((time%2)/2);float pulse=Mathf.Sin(within*Mathf.PI);
            viveInstructionDemo.localPosition=new Vector3(0,.015f,-.004f);
            viveDemoTouch.localPosition=new Vector3(-.20f,.075f+(step==0?.045f:step==1?-.045f:0),-.03f);
            float dot=step<2?.055f*(1+.13f*pulse):.045f;
            viveDemoTouch.localScale=new Vector3(dot,dot,.015f);
            float triggerTravel=(float)ViveQuestionnairePresentation.GuideTriggerTravel(time);
            viveDemoTrigger.localPosition=new Vector3(.26f-.025f*triggerTravel,.025f,-.02f);
            viveDemoTrigger.localEulerAngles=new Vector3(0,0,-18-12*triggerTravel);
            viveDemoTouch.GetComponent<Renderer>().sharedMaterial.color=step<2?new Color(.68f,.90f,.90f):new Color(.36f,.42f,.45f);
            viveDemoTrigger.GetComponent<Renderer>().sharedMaterial.color=step==2?new Color(.68f,.90f,.90f):new Color(.36f,.42f,.45f);
            viveDemoTriggerCallout.GetComponent<Renderer>().sharedMaterial.color=step==2?new Color(.68f,.90f,.90f):new Color(.36f,.42f,.45f);
            // Labels occupy the clear left/right margins rather than drawing
            // over the wand grip or relying on the tiny animated trigger icon.
            FitCentered(viveDemoUp,"TOP: LOWER",frustum,new Rect(.035f,.27f,.22f,.065f),.040f);
            FitCentered(viveDemoDown,"BOTTOM: RAISE",frustum,new Rect(.035f,.55f,.22f,.065f),.040f);
            FitCentered(viveDemoTriggerLabel,"BACK TRIGGER\nPULL / RELEASE",frustum,new Rect(.64f,.50f,.32f,.10f),.035f);
            FitCentered(demoCaption,ViveQuestionnairePresentation.GuideCaption(step),frustum,new Rect(.04f,.18f,.92f,.065f),.045f);
            viveDemoUp.color=step==0?new Color(.68f,.90f,.90f):Color.white;
            viveDemoDown.color=step==1?new Color(.68f,.90f,.90f):Color.white;
        }
        void LayoutCompletionFireworks(bool celebration,double elapsed)
        {
            // Two small muted radial bursts, at most six seconds. No audio,
            // bright flashes, random motion, or effects after withdrawal/fault.
            bool visible=celebration&&elapsed>=0&&elapsed<6;
            completionSparks.gameObject.SetActive(visible);
            if(!visible)return;
            for(int burst=0;burst<2;burst++)
            {
                float age=(float)((elapsed+.45+burst*.7)%3)/3;
                float radius=.025f+.135f*age;
                float size=.016f*Mathf.Sin(age*Mathf.PI);
                for(int ray=0;ray<10;ray++)
                {
                    float angle=(ray*36+burst*18)*Mathf.Deg2Rad;
                    var spark=completionSparks.GetChild(burst*10+ray);
                    spark.localPosition=new Vector3((burst==0?-.38f:.38f)+Mathf.Cos(angle)*radius,.13f+Mathf.Sin(angle)*radius-age*age*.025f,-.006f);
                    spark.localRotation=Quaternion.Euler(0,0,angle*Mathf.Rad2Deg-90);
                    spark.localScale=new Vector3(size*.65f,size*1.5f,1);
                }
            }
        }
        void LogHudProjection(Rect frustum)
        {
            if(!Application.isPlaying||!headsetPreview) return;
            bool stereo=presentationCamera.stereoEnabled;
            if(stereo!=lastHudStereo){lastHudStereo=stereo;hudDiagnosticsRemaining=3;nextHudDiagnosticAt=0;}
            if(hudDiagnosticsRemaining<=0||Time.realtimeSinceStartup<nextHudDiagnosticAt) return;
            hudDiagnosticsRemaining--;nextHudDiagnosticAt=Time.realtimeSinceStartup+2;
            Debug.Log("HUD_LAYOUT stereo="+stereo+" screen="+Screen.width+"x"+Screen.height+
                " camera_pixels="+presentationCamera.pixelWidth+"x"+presentationCamera.pixelHeight+
                " layout="+(HasWorldFixedMenu?"world-fixed":"camera-attached")+" panel_bounds="+frustum.ToString("F3")+
                " root_local="+warningRoot.localPosition.ToString("F3")+
                " root_scale="+warningRoot.localScale.ToString("F3"));
            for(int i=0;i<(stereo?3:1);i++)
            {
                var eye=i==0?Camera.MonoOrStereoscopicEye.Mono:i==1?Camera.MonoOrStereoscopicEye.Left:Camera.MonoOrStereoscopicEye.Right;
                var matrix=i==0?presentationCamera.worldToCameraMatrix:presentationCamera.GetStereoViewMatrix(i==1?Camera.StereoscopicEye.Left:Camera.StereoscopicEye.Right);
                // Relative matrix agreement only; never emit physical world pose.
                Debug.Log("HUD_VIEW "+eye+" camera_relative_origin="+matrix.MultiplyPoint(presentationCamera.transform.position).ToString("F3")+
                    " header="+LabelViewport(developmentBanner,eye)+" body="+LabelViewport(messageText,eye)+
                    " status="+LabelViewport(previewStatusText,eye)+" controls="+LabelViewport(previewInputText,eye));
            }
#if STROOP_XR_PREVIEW
            var displays=new List<UnityEngine.XR.XRDisplaySubsystem>();SubsystemManager.GetSubsystems(displays);
            foreach(var display in displays)
            {
                if(!display.running) continue;
                UnityEngine.XR.XRDisplaySubsystem.XRMirrorViewBlitDesc descriptor;
                int mirrorMode=display.GetPreferredMirrorBlitMode();
                if(!display.GetMirrorViewBlitDesc(null,out descriptor,mirrorMode)) {Debug.Log("HUD_MIRROR descriptor unavailable; mode="+mirrorMode);continue;}
                Debug.Log("HUD_MIRROR mode="+mirrorMode+" native="+descriptor.nativeBlitAvailable+" blits="+descriptor.blitParamsCount);
                for(int i=0;i<Math.Min(descriptor.blitParamsCount,4);i++)
                {
                    UnityEngine.XR.XRDisplaySubsystem.XRBlitParams blit;descriptor.GetBlitParameter(i,out blit);
                    Debug.Log("HUD_MIRROR "+i+" source="+blit.srcRect.ToString("F3")+" destination="+blit.destRect.ToString("F3")+
                        " texture="+(blit.srcTex==null?"none":blit.srcTex.width+"x"+blit.srcTex.height)+" slice="+blit.srcTexArraySlice);
                }
            }
#endif
        }
        string LabelViewport(TextMesh label,Camera.MonoOrStereoscopicEye eye)
        {
            if(label==null||String.IsNullOrEmpty(label.text)) return "empty";
            var bounds=label.GetComponent<MeshRenderer>().localBounds;int at=0;
            for(int x=-1;x<=1;x+=2)for(int y=-1;y<=1;y+=2)for(int z=-1;z<=1;z+=2)
                stimulusProjectionPoints[at++]=label.transform.TransformPoint(bounds.center+Vector3.Scale(bounds.extents,new Vector3(x,y,z)));
            Rect rectangle;int visible=ProjectHudPoints(stimulusProjectionPoints,eye,out rectangle);
            return visible+":"+rectangle.ToString("F3");
        }
        void PositionBacking(Transform backing,Rect frustum,Rect viewport,bool visible)
        {
            if(backing==null) return;
            backing.gameObject.SetActive(visible);
            var box=HudBox(frustum,viewport);
            backing.localPosition=new Vector3(box.center.x,box.center.y,.01f);
            backing.localScale=new Vector3(box.width,box.height,1);
        }
        void FitHud(TextMesh label,string value,Rect frustum,Rect viewport,float lineHeight,int maxLines)
        {
            if(label==null) return;
            var box=HudBox(frustum,viewport);
            float preferredHeight=frustum.height*lineHeight;
            int columns=Mathf.Clamp(Mathf.FloorToInt(box.width/(preferredHeight*.52f)),24,110);
            string fitted=WrapHud(value??"",columns,maxLines);
            // TextMesh.text regenerates the glyph mesh even for an identical string.
            if(label.text!=fitted) label.text=fitted;
            label.anchor=TextAnchor.UpperLeft;label.alignment=TextAlignment.Left;
            label.transform.localScale=Vector3.one;
            var renderer=label.GetComponent<MeshRenderer>();
            var bounds=renderer.localBounds;
            if(bounds.size.x<=.00001f||bounds.size.y<=.00001f||label.text.Length==0){renderer.enabled=false;return;}
            int lines=1;foreach(char character in label.text)if(character=='\n')lines++;
            float scale=Mathf.Min(box.width/bounds.size.x,box.height/bounds.size.y,preferredHeight*lines/bounds.size.y);
            label.transform.localScale=Vector3.one*scale;
            label.transform.localPosition=new Vector3(box.xMin-bounds.min.x*scale,box.yMax-bounds.max.y*scale,0);
            // Overlay text has ZTest Always. Hide any label that would obscure the
            // current stimulus instead of claiming its renderer proves visibility.
            renderer.enabled=!HudOverlapsStimulus(viewport,frustum);
        }
        static Camera.MonoOrStereoscopicEye EyeFor(int index)
        {
            return index==0?Camera.MonoOrStereoscopicEye.Mono:index==1?Camera.MonoOrStereoscopicEye.Left:Camera.MonoOrStereoscopicEye.Right;
        }
        void ProjectStimulusForLayout()
        {
            if(stimulusProjectionStamp==layoutStamp) return;
            stimulusProjectionStamp=layoutStamp;
            var bounds=stimulusText.GetComponent<MeshRenderer>().bounds;
            int at=0;
            for(int x=-1;x<=1;x+=2)for(int y=-1;y<=1;y+=2)for(int z=-1;z<=1;z+=2)
                stimulusProjectionPoints[at++]=bounds.center+Vector3.Scale(bounds.extents,new Vector3(x,y,z));
            int eyes=presentationCamera.stereoEnabled?3:1;
            for(int index=0;index<eyes;index++)
            {
                Rect box;
                stimulusEyeVisibility[index]=ProjectHudPoints(stimulusProjectionPoints,EyeFor(index),out box);
                stimulusEyeBoxes[index]=box;
            }
        }
        bool HudOverlapsStimulus(Rect viewport,Rect frustum)
        {
            if(displayedTrial==null||stimulusText==null||!stimulusText.gameObject.activeInHierarchy) return false;
            ProjectStimulusForLayout();
            var labelBox=HudBox(frustum,viewport);
            hudProjectionPoints[0]=warningRoot.TransformPoint(new Vector3(labelBox.xMin,labelBox.yMin,0));
            hudProjectionPoints[1]=warningRoot.TransformPoint(new Vector3(labelBox.xMin,labelBox.yMax,0));
            hudProjectionPoints[2]=warningRoot.TransformPoint(new Vector3(labelBox.xMax,labelBox.yMin,0));
            hudProjectionPoints[3]=warningRoot.TransformPoint(new Vector3(labelBox.xMax,labelBox.yMax,0));
            for(int eyeIndex=0;eyeIndex<(presentationCamera.stereoEnabled?3:1);eyeIndex++)
            {
                int stimulusVisible=stimulusEyeVisibility[eyeIndex];
                if(stimulusVisible==0) continue;
                Rect hudBox;
                if(stimulusVisible<0||ProjectHudPoints(hudProjectionPoints,EyeFor(eyeIndex),out hudBox)!=1||hudBox.Overlaps(stimulusEyeBoxes[eyeIndex])) return true;
            }
            return false;
        }
        int ProjectHudPoints(Vector3[] points,Camera.MonoOrStereoscopicEye eye,out Rect rectangle)
        {
            float minX=Single.PositiveInfinity,minY=minX,maxX=Single.NegativeInfinity,maxY=maxX;int visible=0;
            foreach(var worldPoint in points)
            {
                var point=presentationCamera.WorldToViewportPoint(worldPoint,eye);
                if(point.z<=presentationCamera.nearClipPlane) continue;
                visible++;minX=Mathf.Min(minX,point.x);maxX=Mathf.Max(maxX,point.x);minY=Mathf.Min(minY,point.y);maxY=Mathf.Max(maxY,point.y);
            }
            rectangle=visible==0?new Rect():Rect.MinMaxRect(minX-.005f,minY-.005f,maxX+.005f,maxY+.005f);
            return visible==0?0:visible==points.Length?1:-1;
        }
        static string WrapHud(string value,int columns,int maxLines)
        {
            var lines=new List<string>();
            foreach(string paragraph in value.Replace("\r","").Split('\n'))
            {
                if(paragraph.Length==0){lines.Add("");continue;}
                lines.AddRange(Wrap(paragraph,columns,Int32.MaxValue).Split('\n'));
            }
            if(lines.Count>maxLines)
            {lines.RemoveRange(maxLines,lines.Count-maxLines);lines[maxLines-1]+=" ... (see owner/log)";}
            return String.Join("\n",lines);
        }

        // The same render entry point accepts automatic and incoming live/simulator
        // outputs. Reference trajectory is intentionally never used as a fallback.
        public void ApplyAvatarOutput(AvatarOutput output)
        {
            RequireLayout();
            if(output == null) throw new ArgumentNullException("output");
            currentOutput = output;
            if(role == Actor.Participant)
            {
                poseWarning = "";
                HeadHeld = EyesHeld = true;
                if(!output.ActualPose.HasValue)
                    poseWarning = "NO CURRENT AVATAR POSE — provisional held display";
                else
                {
                    AvatarPose pose = output.ActualPose.Value;
                    bool headApplied = output.HeadValid && pose.HeadRotation.IsUnit;
                    bool eyesApplied = output.EyeValid && pose.EyeDirection.IsUnit;
                    HeadHeld = !headApplied; EyesHeld = !eyesApplied;
                    if(headApplied)
                    {
                        Rotation q = pose.HeadRotation;
                        avatarHead.localRotation = new Quaternion((float)q.X, (float)q.Y, (float)q.Z, (float)q.W);
                    }
                    if(eyesApplied)
                    {
                        Vec3 d = pose.EyeDirection;
                        // EyeDirection is avatar-root-local. Remove the head's actual
                        // applied rotation before assigning child-eye local rotations.
                        Vector3 local = Quaternion.Inverse(avatarHead.localRotation) * new Vector3((float)d.X, (float)d.Y, (float)d.Z);
                        Vector3 up = Mathf.Abs(Vector3.Dot(local.normalized, Vector3.up)) > .999f ? Vector3.forward : Vector3.up;
                        leftEye.localRotation = rightEye.localRotation = Quaternion.LookRotation(local, up);
                    }
                    if(!headApplied) poseWarning += "HEAD INVALID — held development pose\n";
                    if(!eyesApplied) poseWarning += "EYES INVALID — held development pose";
                }
            }
            avatarRevision++;
            RefreshAvatarVisibility();
            RefreshWarnings();
        }
        public void ClearAvatarOutput()
        {
            RequireLayout();currentOutput=null;avatarRevision++;
            poseWarning="No selected avatar source for this phase.";RefreshAvatarVisibility();RefreshWarnings();
        }

        // The fault source owns detection and recovery. Showing a fault here neither
        // pauses the protocol nor changes its mode or validity flags.
        public void SetFaults(IEnumerable<string> messages)
        {
            RequireLayout();
            if(messages == null) throw new ArgumentNullException("messages");
            faults.Clear();
            foreach(string message in messages)
                if(!String.IsNullOrWhiteSpace(message)) faults.Add(message.Trim());
            RefreshWarnings();
        }

        public void SetQuestionnaireLanguage(string language)
        {
            // Validate the current English-only presentation contract. A legacy
            // language identifier must not silently become an English display.
            Questionnaire.Instruction(language);
            questionnaireLanguage = language;
            if(currentView != null && role == Actor.Participant) messageText.text = ParticipantMessage(currentView);
        }

        void SetMessageText(string value)
        {
            if(messageText != null && messageText.text != value) messageText.text = value;
        }
        void RefreshAvatarVisibility()
        {
            bool visible = currentView != null && (role == Actor.Operator
                ? currentView.ParticipantAvatarVisible
                : currentView.OperatorAvatarVisible && currentOutput != null && currentOutput.AvatarVisible);
            if(avatarRoot.gameObject.activeSelf != visible) avatarRevision++;
            avatarRoot.gameObject.SetActive(visible);
            avatarLabel.gameObject.SetActive(visible);
        }
        void RefreshLegendVisibility()
        {
            bool useful=!viveUnconfiguredPreview&&headsetPreview&&role==Actor.Participant&&currentView!=null&&
                (currentView.Phase==Phase.Practice||currentView.Phase==Phase.MainTask||currentView.Phase==Phase.PracticeCorrection);
            if(controllerLegend!=null)controllerLegend.gameObject.SetActive(useful&&!viveTaskPreview);
            if(viveControllerLegend!=null)viveControllerLegend.gameObject.SetActive(useful&&viveTaskPreview);
        }
        void RefreshWarnings()
        {
            if(faultText == null) return;
            var text = new StringBuilder();
            if(GraphicsSettings.currentRenderPipeline != null)
                text.AppendLine("RENDER ACK UNAVAILABLE: built-in pipeline required.");
            if(role == Actor.Participant && currentView != null && currentView.OperatorAvatarVisible && !String.IsNullOrWhiteSpace(poseWarning))
                text.AppendLine(poseWarning);
            int shown=0,additional=0;var unique=new HashSet<string>(StringComparer.Ordinal);
            foreach(string fault in faults)
            {
                if(!unique.Add(fault))continue;
                if(headsetPreview&&!CleanTaskPresentation&&!UsesPhasePanel&&String.Equals(fault.Trim(),previewDiagnostic.Trim(),StringComparison.Ordinal)) continue;
                if(shown<3){text.AppendLine(Wrap(fault,52,2));shown++;}else additional++;
            }
            if(additional>0) text.AppendLine("Additional fault flags: "+additional);
            hasPresentationFault=text.Length>0;
            faultCaption = text.Length == 0 ? (headsetPreview ? "" : "Development display; hardware validation pending.") : text.ToString().TrimEnd();
            if(faultText.text != faultCaption) faultText.text = faultCaption;
            // TextMesh.color also rebuilds the glyph mesh, so it is assigned only on change.
            var faultColour = text.Length == 0 ? new Color(1, .82f, .35f) : new Color(1, .42f, .35f);
            if(faultText.color != faultColour) faultText.color = faultColour;
        }

        void PrepareViveRawPanelCull(Camera camera)
        {
            rawPanelCaptureFrame=-1;rawPanelObserved=rawPanelRequired=0;
            var expected=rawPanelExpected;
            if(expected==null||camera!=presentationCamera||expected.Frame!=Time.frameCount||!ViveRawAttached()||
                !MatchesViveRawPanel(expected,currentView,questionnaireInstructionPending,viveQuestionnaireRating,questionnaireAnsweredCount)||
                !PresentationIsAvailable()||hasPresentationFault||!isActiveAndEnabled||GraphicsSettings.currentRenderPipeline!=null||
                !camera.isActiveAndEnabled||camera.targetTexture!=null||camera.cullingMask==0||camera.rect.width<=0||camera.rect.height<=0)return;
            // The actual renderers, not a camera callback alone, must be
            // admitted by this camera's culling pass. Mandatory visual pieces
            // depend on phase; no UI event is accepted merely from SetActive.
            int required=1|2;
            bool ready=BindRawPanelElement(phaseText==null?null:phaseText.transform,this,1)&&
                BindRawPanelElement(messageText==null?null:messageText.transform,this,2);
            if(expected.Phase==Phase.Countdown)
            {required|=4;ready=BindRawPanelElement(phaseNumber==null?null:phaseNumber.transform,this,4)&&ready&&
                String.Equals(expected.CountdownText,phaseNumber==null?null:phaseNumber.text,StringComparison.Ordinal);}
            if(expected.Phase==Phase.Questionnaire&&expected.Intro)
            {
                required|=8|16|32|64|128|256|1024|2048;
                ready=BindRawPanelElement(viveDemoTouch,this,8)&&ready;
                ready=BindRawPanelElement(viveDemoTrigger,this,16)&&ready;
                ready=BindRawPanelElement(demoCaption==null?null:demoCaption.transform,this,32)&&ready;
                ready=BindRawPanelElement(viveDemoUp==null?null:viveDemoUp.transform,this,64)&&ready;
                ready=BindRawPanelElement(viveDemoDown==null?null:viveDemoDown.transform,this,128)&&ready;
                ready=BindRawPanelElement(viveDemoTriggerLabel==null?null:viveDemoTriggerLabel.transform,this,256)&&ready;
                ready=BindRawPanelElement(viveDemoRearWand,this,1024)&&ready;
                ready=BindRawPanelElement(viveDemoTriggerCallout,this,2048)&&ready;
            }
            else if(expected.Phase==Phase.Questionnaire)
            {required|=512;ready=BindRawPanelElement(viveSelectionText==null?null:viveSelectionText.transform,this,512)&&ready;}
            if(!ready)return;
            rawPanelRequired=required;rawPanelCaptureFrame=Time.frameCount;
            rawPanelPhaseText=phaseText.text;rawPanelMessageText=messageText.text;
            rawPanelSelectionText=expected.Phase==Phase.Questionnaire&&!expected.Intro?viveSelectionText.text:null;
            rawPanelCountdownText=expected.Phase==Phase.Countdown?phaseNumber.text:null;
        }
        void FinishViveRawPanelRender(Camera camera)
        {
            var expected=rawPanelExpected;
            if(expected==null||camera!=presentationCamera||rawPanelCaptureFrame!=Time.frameCount||
                expected.Frame!=Time.frameCount||rawPanelRequired==0||(rawPanelObserved&rawPanelRequired)!=rawPanelRequired||
                !ViveRawAttached()||!PresentationIsAvailable()||hasPresentationFault||!isActiveAndEnabled||
                !camera.isActiveAndEnabled||camera.targetTexture!=null||camera.cullingMask==0||
                !MatchesViveRawPanel(expected,currentView,questionnaireInstructionPending,viveQuestionnaireRating,questionnaireAnsweredCount)||
                !String.Equals(rawPanelPhaseText,phaseText.text,StringComparison.Ordinal)||
                !String.Equals(rawPanelMessageText,messageText.text,StringComparison.Ordinal)||
                (rawPanelSelectionText!=null&&!String.Equals(rawPanelSelectionText,viveSelectionText.text,StringComparison.Ordinal))||
                (expected.Phase==Phase.Countdown&&!ViveRawPanelPolicy.CountdownTextMatches(expected.CountdownText,rawPanelCountdownText,phaseNumber==null?null:phaseNumber.text)))return;
            double at=Time.realtimeSinceStartupAsDouble;
            if(Double.IsNaN(at)||Double.IsInfinity(at)||at<0)return;
            var observed=new ViveRawPanelObservation(expected.Phase,expected.Block,expected.Frame,expected.Rating,expected.Answered,
                expected.Intro,expected.Ready,expected.Item,expected.Correction,expected.CountdownText,expected.Revision,at);
            rawPanelExpected=null;rawPanelCaptureFrame=-1;rawPanelObserved=rawPanelRequired=0;
            var callback=ViveRawPanelRendered;bool accepted=false;
            try{if(callback!=null)accepted=callback(observed);}catch(Exception){accepted=false;}
            rawPanelSeen=accepted?observed:null;
        }

        void BeforeCameraCull(Camera camera)
        {
            if(camera != presentationCamera) return;
            LayoutHud();
#if STROOP_XR_PREVIEW
            PrepareViveRecordingChoiceRender(camera);
#endif
            PrepareViveRawPanelCull(camera);
            ResetCapture();
            capturedAvatar = null; capturedAvatarFrame = -1; avatarObservedByCamera = false;
            if(PresentationIsAvailable() && isActiveAndEnabled && contentRoot != null && GraphicsSettings.currentRenderPipeline == null)
            {
                capturedAvatarRevision = avatarRevision; capturedTaskRevision = presentationRevision; capturedAvatarFrame = Time.frameCount;
                capturedAvatar = new AvatarPresentationObservation(role, currentView,
                    avatarRoot.gameObject.activeInHierarchy, AppliedPose, HeadHeld, EyesHeld, currentOutput);
            }
            if(!CanAcknowledge()) return;
            capturedTrial = displayedTrial; capturedRevision = presentationRevision;
            capturedFrame = Time.frameCount;
        }
        internal void ObserveStimulusRender(Camera camera)
        {
            if(camera == presentationCamera && capturedFrame == Time.frameCount && capturedTrial == displayedTrial &&
                capturedRevision == presentationRevision && CanAcknowledge())
            {
                stimulusObservedByCamera = true;
                // Optional raw-only capture. A failure here never enables a
                // trial; the original callback still runs its fail-stop path.
                try
                {
                    if(ViveRawObservationRequired!=null&&ViveRawObservationRequired())
                    {capturedStimulusText=stimulusText.text;capturedStimulusColor=stimulusText.color;capturedStimulusContent=true;}
                }
                catch(Exception){capturedStimulusContent=false;capturedStimulusText=null;}
            }
        }
        internal void ObserveAvatarRender(Camera camera)
        {
            if(camera == presentationCamera && capturedAvatarFrame == Time.frameCount && capturedAvatarRevision == avatarRevision)
                avatarObservedByCamera = true;
        }
        void AfterCameraRender(Camera camera)
        {
            if(camera != presentationCamera) return;
#if STROOP_XR_PREVIEW
            FinishViveRecordingChoiceRender(camera);
#endif
            FinishViveRawPanelRender(camera);
            var avatarCallback = AvatarPresented;
            if(PresentationIsAvailable() && capturedAvatar != null && capturedAvatarFrame == Time.frameCount && capturedAvatarRevision == avatarRevision &&
                capturedTaskRevision == presentationRevision && isActiveAndEnabled && camera.isActiveAndEnabled &&
                camera.targetTexture == null && camera.rect.width > 0 && camera.rect.height > 0)
            {
                capturedAvatar.Complete(Time.frameCount, Time.realtimeSinceStartupAsDouble, avatarObservedByCamera);
                var observation = capturedAvatar;
                capturedAvatar = null; capturedAvatarFrame = -1;
                if(avatarCallback != null) avatarCallback(observation);
            }
            bool present = stimulusObservedByCamera && capturedFrame == Time.frameCount &&
                capturedRevision == presentationRevision && capturedTrial == displayedTrial && CanAcknowledge();
            string trial = capturedTrial;
            long revision = capturedRevision;
            int frame = capturedFrame;
            string rawText = capturedStimulusText;
            Color rawColor = capturedStimulusColor;
            bool rawStable = false;
            try{rawStable=capturedStimulusContent&&String.Equals(rawText,stimulusText.text,StringComparison.Ordinal)&&rawColor.Equals(stimulusText.color);}
            catch(Exception){rawStable=false;}
            ResetCapture();
            var callback = StimulusRendered;
            var viveCallback = ViveRawStimulusRendered;
            if(!present || callback == null || !acknowledgedTrials.Add(trial)) return;
            double renderSeconds = Time.realtimeSinceStartupAsDouble;
            // A detailed-observation failure must not suppress the original
            // callback: the VIVE raw handler there still masks and fail-stops.
            if(viveCallback != null&&rawStable)
            {
                try{viveCallback(new StimulusRenderObservation(trial,revision,frame,renderSeconds,rawText,rawColor));}
                catch(Exception){}
            }
            if(callback != null) callback(trial, renderSeconds);
        }
        bool CanAcknowledge()
        {
            // A witness on the actual TextMesh must be culled in for this camera,
            // followed by its completed render. Hidden/stale views cannot acknowledge.
            return PresentationIsAvailable() && !faultCoversStimulus && isActiveAndEnabled && role == Actor.Participant && GraphicsSettings.currentRenderPipeline == null &&
                presentationCamera != null && presentationCamera.isActiveAndEnabled && presentationCamera.targetTexture == null &&
                presentationCamera.rect.width > 0 && presentationCamera.rect.height > 0 &&
                !String.IsNullOrEmpty(displayedTrial) && currentView != null && currentView.TrialId == displayedTrial &&
                (currentView.Phase == Phase.Practice || currentView.Phase == Phase.MainTask) &&
                stimulusText != null && stimulusText.gameObject.activeInHierarchy &&
                stimulusText.GetComponent<MeshRenderer>().enabled;
        }
        void ResetCapture() { capturedTrial = null; capturedFrame = -1; stimulusObservedByCamera = false; capturedStimulusContent=false; capturedStimulusText=null; }
        void RequireLayout() { if(contentRoot == null) throw new InvalidOperationException("Initialize the presentation with its camera first."); }

        void BuildLayout()
        {
            contentRoot = Child("WorldSpaceContent", transform, Vector3.zero);
            stimulusText = Text("Stimulus", contentRoot, new Vector3(-.5f, 1.65f, 2), StimulusCharacterSize, 100, Color.white);
            stimulusText.gameObject.AddComponent<StimulusRenderWitness>().Bind(this);
            stimulusText.gameObject.SetActive(false);
            phaseText = Text("RoleAndPhase", contentRoot, new Vector3(.5f, 2.05f, 2), .035f, 50, Color.white);
            messageText = Text("ParticipantInstructions", contentRoot, new Vector3(.5f, 1.25f, 2), .032f, 48, Color.white);
            provisionalText = Text("ProvisionalOperatorPolicy", contentRoot, new Vector3(.5f, .85f, 2), .029f, 48, new Color(1, .7f, .25f));
            avatarRoot = Child("FixedBodyAvatar", contentRoot, new Vector3(.65f, .9f, 1.9f));
            avatarRoot.localRotation = Quaternion.Euler(0, 225, 0);
            Primitive("FixedTorso", PrimitiveType.Capsule, avatarRoot, Vector3.zero, new Vector3(.42f, .48f, .3f), new Color(.27f, .45f, .55f));
            avatarHead = Child("DrivenHead", avatarRoot, new Vector3(0, .7f, 0));
            Primitive("HeadShape", PrimitiveType.Sphere, avatarHead, Vector3.zero, new Vector3(.4f, .45f, .36f), new Color(.77f, .68f, .56f));
            leftEye = BuildEye("LeftEye", new Vector3(-.09f, .05f, .16f));
            rightEye = BuildEye("RightEye", new Vector3(.09f, .05f, .16f));
            foreach(var renderer in avatarRoot.GetComponentsInChildren<MeshRenderer>(true)) renderer.gameObject.AddComponent<AvatarRenderWitness>().Bind(this);
            avatarLabel = Text("AvatarScope", contentRoot, new Vector3(.65f, .25f, 1.9f), .028f, 48, Color.white);
            avatarLabel.text = role == Actor.Operator ? "PARTICIPANT AVATAR PLACEHOLDER\nStatic; no reverse head / eye tracking" : "PROVISIONAL AVATAR GEOMETRY\nFixed body; shared head / eye renderer";
            avatarRoot.gameObject.SetActive(false); avatarLabel.gameObject.SetActive(false);
            warningRoot = Child("HeadsetVisibleDevelopmentWarning", presentationCamera.transform, new Vector3(0, .29f, 1.1f));
            var banner = Text("PermanentDevelopmentBanner", warningRoot, Vector3.zero, .019f, 48, new Color(1, .75f, .2f));
            banner.text = "SIMULATION / NO HEADSET VALIDATION";
            faultText = Text("WorldSpaceFaultMessages", warningRoot, new Vector3(0, -.07f, 0), .013f, 48, Color.white);
            faultText.anchor = TextAnchor.UpperCenter;
            SetOverlay(banner); SetOverlay(faultText);
            var backing = Primitive("WarningBacking", PrimitiveType.Quad, warningRoot, new Vector3(0, -.075f, .01f), new Vector3(1.05f, .24f, 1), new Color(.025f, .025f, .035f, .94f));
            Material backdrop = NewMaterial(Shader.Find("AvatarStroop/WorldSpaceOverlay"), new Color(.025f, .025f, .035f, .94f));
            backdrop.renderQueue = 4990; backing.GetComponent<MeshRenderer>().sharedMaterial = backdrop;
        }
        void EnsureClassroomLayout()
        {
            // Migrate the generated presentation as well as newly created scenes.
            // Align the physical writing inset with the fixed experiment menu.
            // From participant to board: text/task 1.900, menu backing 1.910,
            // writing front 1.915, white face front 1.925, frame front 1.940.
            // Bootstrap derives gaze targets from these final world anchors.
            stimulusText.transform.localPosition = TaskDisplayPosition;
            stimulusText.characterSize = StimulusCharacterSize;
            stimulusText.transform.localScale = Vector3.one;
            avatarRoot.localPosition = new Vector3(.65f, .9f, 1.9f);
            avatarRoot.localRotation = Quaternion.Euler(0,225,0);
            if(contentRoot.Find("ClassroomEnvironment") == null)
            {
                var room = Child("ClassroomEnvironment", contentRoot, Vector3.zero);
                Primitive("Floor", PrimitiveType.Cube, room, new Vector3(0,-.045f,2), new Vector3(6,.08f,6), new Color(.34f,.35f,.34f));
                Primitive("BackWall", PrimitiveType.Cube, room, new Vector3(0,1.5f,4.6f), new Vector3(6,3,.10f), new Color(.70f,.72f,.70f));
                Primitive("LeftWall", PrimitiveType.Cube, room, new Vector3(-3,1.5f,1.8f), new Vector3(.10f,3,5.6f), new Color(.62f,.65f,.64f));
                Primitive("RightWall", PrimitiveType.Cube, room, new Vector3(3,1.5f,1.8f), new Vector3(.10f,3,5.6f), new Color(.66f,.68f,.66f));
                Primitive("Skirting", PrimitiveType.Cube, room, new Vector3(0,.08f,4.53f), new Vector3(6,.16f,.03f), new Color(.43f,.46f,.44f));
                var board = Child("Whiteboard", room, BoardPosition);
                Primitive("Frame", PrimitiveType.Cube, board, new Vector3(0,0,.035f), new Vector3(1.4f,.94f,.07f), new Color(.39f,.42f,.42f));
                Primitive("OffWhiteFace", PrimitiveType.Cube, board, new Vector3(0,0,-.005f), new Vector3(1.34f,.88f,.02f), new Color(.90f,.91f,.87f));
                // Keep the existing saturated ink RGB against a neutral dark
                // writing area; yellow/green must not disappear on a white face.
                Primitive("WritingArea", PrimitiveType.Cube, board, new Vector3(0,0,-.02f), new Vector3(1.18f,.64f,.01f), new Color(.055f,.065f,.070f));
                Primitive("Tray", PrimitiveType.Cube, board, new Vector3(0,-.48f,-.035f), new Vector3(1.4f,.035f,.16f), new Color(.43f,.46f,.46f));
                for(int side=-1;side<=1;side+=2)
                {
                    Primitive(side<0?"LeftSupport":"RightSupport",PrimitiveType.Cube,board,new Vector3(side*.53f,-.97f,.055f),new Vector3(.035f,1.33f,.04f),new Color(.43f,.46f,.46f));
                    Primitive(side<0?"LeftFoot":"RightFoot",PrimitiveType.Cube,board,new Vector3(side*.53f,-1.62f,.04f),new Vector3(.20f,.05f,.46f),new Color(.37f,.40f,.40f));
                }
            }
            var physicalBoard=contentRoot.Find("ClassroomEnvironment/Whiteboard");
            physicalBoard.localPosition=BoardPosition;
            physicalBoard.Find("Frame").localScale=new Vector3(1.50f,1.20f,.07f);
            physicalBoard.Find("OffWhiteFace").localScale=new Vector3(1.44f,1.14f,.02f);
            physicalBoard.Find("WritingArea").localScale=new Vector3(1.38f,1.08f,.01f);
            physicalBoard.Find("Tray").localPosition=new Vector3(0,-.61f,-.035f);
            physicalBoard.Find("Tray").localScale=new Vector3(1.50f,.035f,.16f);
            for(int side=-1;side<=1;side+=2)
            {
                var support=physicalBoard.Find(side<0?"LeftSupport":"RightSupport");
                support.localPosition=new Vector3(side*.53f,-.90f,.055f);support.localScale=new Vector3(.035f,1.24f,.04f);
                physicalBoard.Find(side<0?"LeftFoot":"RightFoot").localPosition=new Vector3(side*.53f,-1.52f,.04f);
            }
            if(avatarRoot.Find("StandingBody") == null)
            {
                var body = Child("StandingBody",avatarRoot,Vector3.zero);
                var skin = new Color(.77f,.68f,.56f);
                var shirt = new Color(.27f,.45f,.55f);
                var trousers = new Color(.20f,.25f,.29f);
                Primitive("Neck",PrimitiveType.Cylinder,body,new Vector3(0,.49f,0),new Vector3(.11f,.075f,.11f),skin);
                for(int side=-1;side<=1;side+=2)
                {
                    string name=side<0?"Left":"Right";
                    Primitive(name+"Arm",PrimitiveType.Capsule,body,new Vector3(side*.28f,.035f,0),new Vector3(.11f,.32f,.11f),shirt);
                    Primitive(name+"Hand",PrimitiveType.Sphere,body,new Vector3(side*.28f,-.30f,0),new Vector3(.10f,.14f,.10f),skin);
                    Primitive(name+"Leg",PrimitiveType.Capsule,body,new Vector3(side*.115f,-.60f,0),new Vector3(.15f,.27f,.16f),trousers);
                    Primitive(name+"Shoe",PrimitiveType.Cube,body,new Vector3(side*.115f,-.865f,.045f),new Vector3(.17f,.07f,.28f),new Color(.12f,.14f,.16f));
                }
            }
            foreach(var renderer in avatarRoot.GetComponentsInChildren<MeshRenderer>(true))
            {
                var witness=renderer.GetComponent<AvatarRenderWitness>();
                if(witness==null)witness=renderer.gameObject.AddComponent<AvatarRenderWitness>();
                witness.Bind(this);
            }
            if(headsetPreview&&!viveUnconfiguredPreview&&!viveTaskPreview)EnsureControllerLegend();
            RefreshLegendVisibility();
        }
        void EnsureControllerLegend()
        {
            controllerLegend=contentRoot.Find("QuestControllerLegend");
            if(controllerLegend!=null){controllerLegend.localPosition=new Vector3(-1.60f,1.55f,BoardPosition.z);return;}
            controllerLegend=Child("QuestControllerLegend",contentRoot,new Vector3(-1.60f,1.55f,BoardPosition.z));
            Primitive("LegendFrame",PrimitiveType.Cube,controllerLegend,Vector3.zero,new Vector3(.55f,.82f,.035f),new Color(.78f,.81f,.79f));
            Primitive("LegendFace",PrimitiveType.Cube,controllerLegend,new Vector3(0,0,-.022f),new Vector3(.525f,.795f,.014f),new Color(.12f,.15f,.16f));
            BuildControllerLegend("LeftController",-.135f,true);
            BuildControllerLegend("RightController",.135f,false);
        }
        void EnsureViveControllerLegend(IDictionary<ViveTrackpadDirection,Colour> mapping)
        {
            if(viveControllerLegend!=null)throw new InvalidOperationException("A VIVE mapping is already configured.");
            viveControllerLegend=Child("ViveControllerLegend",contentRoot,new Vector3(-1.60f,1.55f,BoardPosition.z));
            Primitive("LegendFrame",PrimitiveType.Cube,viveControllerLegend,Vector3.zero,new Vector3(.55f,.82f,.035f),new Color(.78f,.81f,.79f));
            Primitive("LegendFace",PrimitiveType.Cube,viveControllerLegend,new Vector3(0,0,-.022f),new Vector3(.525f,.795f,.014f),new Color(.12f,.15f,.16f));
            Primitive("Trackpad",PrimitiveType.Sphere,viveControllerLegend,new Vector3(0,.05f,-.04f),new Vector3(.43f,.43f,.025f),new Color(.55f,.60f,.60f));
            LegendButton(viveControllerLegend,"U",mapping[ViveTrackpadDirection.Up],new Vector3(0,.19f,-.07f));
            LegendButton(viveControllerLegend,"R",mapping[ViveTrackpadDirection.Right],new Vector3(.14f,.05f,-.07f));
            LegendButton(viveControllerLegend,"D",mapping[ViveTrackpadDirection.Down],new Vector3(0,-.09f,-.07f));
            LegendButton(viveControllerLegend,"L",mapping[ViveTrackpadDirection.Left],new Vector3(-.14f,.05f,-.07f));
            var label=Text("TrackpadLabel",viveControllerLegend,new Vector3(0,.33f,-.045f),.0065f,64,Color.white);label.text="TRACKPAD";
            viveControllerLegend.gameObject.SetActive(false);
        }
        void BuildControllerLegend(string name,float x,bool left)
        {
            var root=Child(name,controllerLegend,new Vector3(x,0,-.035f));
            Primitive("Grip",PrimitiveType.Capsule,root,new Vector3(0,-.18f,0),new Vector3(.095f,.15f,.03f),new Color(.63f,.67f,.67f));
            Primitive("ControllerFace",PrimitiveType.Sphere,root,new Vector3(0,.075f,-.005f),new Vector3(.22f,.365f,.035f),new Color(.72f,.75f,.74f));
            Primitive("Thumbstick",PrimitiveType.Sphere,root,new Vector3(left?-.060f:.060f,.20f,-.03f),new Vector3(.047f,.047f,.015f),new Color(.17f,.20f,.21f));
            var hand=Text("HandLabel",root,new Vector3(0,.33f,-.045f),.0065f,64,Color.white);hand.text=left?"LEFT":"RIGHT";
            // Holder's top view: left Y is above/right of X; right B above/left
            // of A. These are original schematic icons, not controller CAD.
            LegendButton(root,left?"Y":"B",left?Colour.Green:Colour.Yellow,new Vector3(left?.040f:-.040f,.115f,-.035f));
            LegendButton(root,left?"X":"A",left?Colour.Red:Colour.Blue,new Vector3(left?-.015f:.015f,-.085f,-.035f));
        }
        void LegendButton(Transform root,string button,Colour colour,Vector3 position)
        {
            Primitive("Button"+button,PrimitiveType.Sphere,root,position,new Vector3(.10f,.10f,.018f),Ink(colour));
            var label=Text("Button"+button+"Label",root,position+new Vector3(0,0,-.018f),.0085f,64,
                colour==Colour.Green||colour==Colour.Yellow?Color.black:Color.white);
            label.text=button;
        }
        Transform BuildEye(string name, Vector3 position)
        {
            Transform eye = Child(name, avatarHead, position);
            Primitive("White", PrimitiveType.Sphere, eye, Vector3.zero, Vector3.one * .11f, Color.white);
            Primitive("Pupil", PrimitiveType.Sphere, eye, new Vector3(0, 0, .051f), new Vector3(.048f, .048f, .018f), Color.black);
            return eye;
        }
        GameObject Primitive(string name, PrimitiveType type, Transform parent, Vector3 position, Vector3 scale, Color colour)
        {
            var go = GameObject.CreatePrimitive(type); go.name = name;
            go.transform.SetParent(parent, false); go.transform.localPosition = position; go.transform.localScale = scale;
            // Colliders are not inputs or AOIs. They exist only because Unity's
            // primitive factory adds them; remove them for this presentation layer.
            var collider = go.GetComponent<Collider>();
            if(collider != null) { if(Application.isPlaying) Destroy(collider); else DestroyImmediate(collider); }
            var renderer = go.GetComponent<MeshRenderer>();
            renderer.sharedMaterial = NewMaterial(Shader.Find("Unlit/Color"), colour);
            renderer.shadowCastingMode = ShadowCastingMode.Off; renderer.receiveShadows = false;
            return go;
        }
        TextMesh Text(string name, Transform parent, Vector3 position, float size, int fontSize, Color colour)
        {
            var item = Child(name, parent, position).gameObject.AddComponent<TextMesh>();
            item.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            item.GetComponent<MeshRenderer>().sharedMaterial = item.font.material;
            item.GetComponent<MeshRenderer>().shadowCastingMode = ShadowCastingMode.Off;
            item.characterSize = size; item.fontSize = fontSize; item.color = colour;
            item.anchor = TextAnchor.MiddleCenter; item.alignment = TextAlignment.Center;
            item.richText = false; item.text = "";
            return item;
        }
        void SetOverlay(TextMesh text)
        {
            Material material = NewMaterial(Shader.Find("AvatarStroop/WorldSpaceOverlay"), Color.white);
            material.mainTexture = text.font.material.mainTexture; material.renderQueue = 5000;
            text.GetComponent<MeshRenderer>().sharedMaterial = material;
        }
        void RefreshOverlayFontTextures(Font rebuiltFont)
        {
            if(warningRoot == null) return;
            foreach(var text in warningRoot.GetComponentsInChildren<TextMesh>(true))
                if(text.font != null && (rebuiltFont == null || text.font == rebuiltFont))
                    text.GetComponent<MeshRenderer>().sharedMaterial.mainTexture = text.font.material.mainTexture;
        }
        Material NewMaterial(Shader shader, Color colour)
        {
            if(shader == null) throw new InvalidOperationException("Required built-in/development shader is missing.");
            var material = new Material(shader); material.color = colour;
            if(Application.isPlaying) runtimeMaterials.Add(material);
            return material;
        }
        static Transform Child(string name, Transform parent, Vector3 position)
        {
            var go = new GameObject(name); go.transform.SetParent(parent, false); go.transform.localPosition = position; return go.transform;
        }
        static Color Ink(Colour colour)
        {
            switch(colour)
            {
                case Colour.Red: return Color.red;
                case Colour.Green: return Color.green;
                case Colour.Blue: return Color.blue;
                case Colour.Yellow: return Color.yellow;
                default: throw new ArgumentOutOfRangeException("colour");
            }
        }
        string ParticipantMessage(TaskView view)
        {
            if(viveTaskPreview)
            {
                if(view.Phase==Phase.Ended&&ShowViveLegacyPreviewNotice)return ViveStroopRecordingScopePresentation.LegacyEndMessage(completed);
                if(view.Phase==Phase.Preparation||view.Phase==Phase.Familiarization)return ViveRawAttached()?"Waiting for the owner to start practice.\nRecording status and coverage are not confirmed.":"Waiting for the owner to start practice.\nDevelopment task only; no data is saved.";
                if(view.Phase==Phase.PracticeCorrection)return view.PracticeCorrection+"\nPress the trigger to continue.";
                if(view.Phase==Phase.Questionnaire&&QuestionnaireIntro)return "Click top or bottom, then release.\nPull the trigger when ready.";
                if(view.Phase==Phase.Rest)return "Rest as long as you need.\nPress the trigger to continue.";
                if(view.Phase==Phase.Paused)return "Paused.\n"+(String.IsNullOrEmpty(vivePauseDiagnostic)?"Cause unavailable; ask the operator.":vivePauseDiagnostic)+"\nRelease controls, then press Trigger to resume.";
                if(view.Phase==Phase.NeedsProtocolDecision)return "A protocol decision is pending.\nAsk the owner; no automatic continuation.";
                if(view.Phase==Phase.Ended&&!completed)return ViveRawAttached()?"Development task ended.\nAsk the owner to review recording status.\nThe remaining data may be unknown.":"Development task ended.\nNo task or eye session file was saved.\nYou may close the preview.";
            }
            if(view.Phase == Phase.Preparation || view.Phase == Phase.Familiarization)
                return headsetPreview ? "Use the owner controls to begin practice\nonce the headset and both Touch controllers are ready.\nNo practice or source starts automatically." :
                    "Use the separate owner controls to begin practice.\nNo practice or source starts automatically.";
            if(view.Phase == Phase.BlockPreparation || view.Phase == Phase.BlockReady)
            {
                if(view.SourceContinuationPending)
                    return "Waiting for the same confirmed source to be ready.\nNo additional owner confirmation is needed.\nIf a fault blocks readiness, follow the owner recovery guidance.";
                return "Waiting for the owner to configure and begin\nthis block in the separate preview control window.";
            }
            if(view.Phase == Phase.PracticeRetry) return "Practice needs one retry.\nAsk the owner to start it in the control window.";
            if(view.Phase == Phase.PracticeCorrection) return view.PracticeCorrection + (headsetPreview ? "\nRight trigger to continue when ready." : "\nPress Space to continue when ready.");
            if(view.Phase == Phase.Countdown) return (HasWorldFixedMenu?"":"Starting in "+Math.Ceiling(countdownRemaining).ToString("0")+" seconds.\n")+
                "The next block begins shortly.\nRespond to the font colour.";
            if(view.Phase == Phase.Questionnaire)
            {
                if(questionnaireLanguage == null) return "QUESTIONNAIRE LANGUAGE NOT CONFIGURED";
                if(QuestionnaireIntro) return headsetPreview?
                    "Left stick: UP lowers; DOWN raises.\nCentre between steps. A confirms.\nDemonstration only; no answer is recorded.":
                    "Use keys 1 to 5 to select and submit a rating.\nRead at your own pace. Press Space when ready\nto open the questions. No answer is recorded here.";
                var message = new StringBuilder(Wrap(Questionnaire.Instruction(questionnaireLanguage), 48, 2));
                message.Append("\n\n").Append(Wrap(view.QuestionnaireItem ?? "", 48, 5)).AppendLine();
                for(int score = 1; score <= 5; score++)
                    message.Append("\n").Append(viveTaskPreview?ViveQuestionnairePresentation.Option(score,viveQuestionnaireRating,questionnaireLanguage):score+"  "+Questionnaire.Anchor(score,questionnaireLanguage));
                return message.ToString();
            }
            if(view.Phase == Phase.Rest) return view.ReadyEnabled ? "Rest as long as you need.\n" +
                (HasWorldFixedMenu?(restDuration>0?"Seconds rested ("+Math.Ceiling(restDuration).ToString("0")+" s optional reference).":"Continue whenever ready."):
                headsetPreview?"A when ready.":"Press Space when ready.") : "Rest before continuing.\nContinue is not yet available.";
            if(view.Phase == Phase.Paused) return headsetPreview ? "Paused\nRight trigger to actively resume.\nB to withdraw and end preview." : "Paused\nPress R to actively resume when ready.";
            if(view.Phase == Phase.NeedsProtocolDecision) return headsetPreview ? "A protocol decision is pending.\nNo automatic retry, resume or continuation is applied.\nB withdraws." : "A protocol decision is pending.\nNo automatic retry, resume or continuation is applied.\nEsc withdraws.";
            if(view.Phase == Phase.Ended) return completed?"The preview is complete.\nThank you for taking part.":"Simulation ended.\nThe owner can export the actual records.\nClose this window when finished.";
            return "";
        }
        static string Wrap(string message, int columns, int maxLines)
        {
            var lines = new List<string>(); var line = new StringBuilder();
            foreach(string word in message.Replace('\n', ' ').Split(new[] {' '}, StringSplitOptions.RemoveEmptyEntries))
            {
                if(line.Length > 0 && line.Length + word.Length + 1 > columns) { lines.Add(line.ToString()); line.Length = 0; }
                if(line.Length > 0) line.Append(' ');
                string remainder = word;
                while(remainder.Length > columns)
                {
                    lines.Add(remainder.Substring(0, columns)); remainder = remainder.Substring(columns);
                }
                line.Append(remainder);
            }
            if(line.Length > 0) lines.Add(line.ToString());
            if(lines.Count > maxLines) { lines.RemoveRange(maxLines, lines.Count - maxLines); lines[maxLines - 1] += "…"; }
            return String.Join("\n", lines);
        }
    }
}
