using System;
using System.Collections.Generic;
using System.Text;
using AvatarStroop.Core;
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
        [SerializeField] TextMesh previewInputText;
        bool presentationAvailable = true;
        Func<bool> presentationAvailabilityCheck;
        readonly List<Material> runtimeMaterials = new List<Material>();
        readonly HashSet<string> acknowledgedTrials = new HashSet<string>(StringComparer.Ordinal);
        readonly List<string> faults = new List<string>();
        TaskView currentView;
        AvatarOutput currentOutput;
        string displayedTrial, capturedTrial;
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
        public event Action<AvatarPresentationObservation> AvatarPresented;
        public Camera PresentationCamera { get { return presentationCamera; } }
        public Actor Role { get { return role; } }
        public Transform AvatarRoot { get { return avatarRoot; } }
        public Transform AvatarHead { get { return avatarHead; } }
        public Vector3 TaskWorldPosition { get { return stimulusText.transform.position; } }
        public bool OperatorViewProvisional { get; private set; }
        public bool HeadHeld { get; private set; } = true;
        public bool EyesHeld { get; private set; } = true;
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
            warningRoot.SetParent(camera.transform, false);
            warningRoot.localPosition = new Vector3(0, .29f, 1.1f);
            warningRoot.localRotation = Quaternion.identity;
            var witness = stimulusText.GetComponent<StimulusRenderWitness>();
            witness.Bind(this);
            foreach(var avatarWitness in avatarRoot.GetComponentsInChildren<AvatarRenderWitness>(true)) avatarWitness.Bind(this);
            RefreshOverlayFontTextures(null);
            ResetCapture();
            RefreshWarnings();
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
            capturedAvatar = null; capturedAvatarFrame = -1;
        }
        void OnDestroy()
        {
            foreach(var material in runtimeMaterials)
                if(material != null) Destroy(material);
            if(Application.isPlaying && warningRoot != null) Destroy(warningRoot.gameObject);
        }

        public void ApplyTaskView(TaskView view)
        {
            RequireLayout();
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
            stimulusText.gameObject.SetActive(nextTrial != null);
            phaseText.text = (role == Actor.Participant ? "PARTICIPANT" : "OPERATOR") + "  |  " + view.Phase;
            OperatorViewProvisional = role == Actor.Operator && view.OperatorViewProvisional;
            provisionalText.text = OperatorViewProvisional ? "PROVISIONAL OPERATOR VIEW\nD / non-main display policy awaits review" : "";
            // No correctness, RT, response result, or score enters the operator view.
            messageText.text = role == Actor.Operator ? "" : ParticipantMessage(view);
            RefreshAvatarVisibility();
            RefreshWarnings();
        }

        public void SetPresentationAvailable(bool available)
        {
            presentationAvailable = available;
            if(!available) ResetCapture();
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
            RequireLayout(); headsetPreview = true;
            var banner = warningRoot.Find("PermanentDevelopmentBanner").GetComponent<TextMesh>();
            banner.text = "QUEST LINK PREVIEW | SIMULATED GAZE | NOT A STUDY RUN";
            if(previewInputText == null)
            {
                previewInputText = Text("QuestPreviewInputStatus", warningRoot, new Vector3(0, -.61f, 0), .013f, 48, Color.white);
                SetOverlay(previewInputText);
            }
            previewInputText.text = diagnostic + "\n" + controls;
            if(currentView != null && role == Actor.Participant) messageText.text = ParticipantMessage(currentView);
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
            // Requires an explicit configured language; there is no study default.
            Questionnaire.Instruction(language);
            questionnaireLanguage = language;
            if(currentView != null && role == Actor.Participant) messageText.text = ParticipantMessage(currentView);
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
        void RefreshWarnings()
        {
            if(faultText == null) return;
            var text = new StringBuilder();
            if(GraphicsSettings.currentRenderPipeline != null)
                text.AppendLine("RENDER ACK UNAVAILABLE: built-in pipeline required.");
            if(role == Actor.Participant && currentView != null && currentView.OperatorAvatarVisible && !String.IsNullOrWhiteSpace(poseWarning))
                text.AppendLine(poseWarning);
            int shown = Math.Min(faults.Count, 3);
            for(int i = 0; i < shown; i++) text.AppendLine(Wrap(faults[i], 52, 2));
            if(faults.Count > shown) text.AppendLine("Additional fault flags: " + (faults.Count - shown));
            faultText.text = text.Length == 0 ? "Development display; hardware validation pending." : text.ToString().TrimEnd();
            faultText.color = text.Length == 0 ? new Color(1, .82f, .35f) : new Color(1, .42f, .35f);
        }

        void BeforeCameraCull(Camera camera)
        {
            if(camera != presentationCamera) return;
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
                capturedRevision == presentationRevision && CanAcknowledge()) stimulusObservedByCamera = true;
        }
        internal void ObserveAvatarRender(Camera camera)
        {
            if(camera == presentationCamera && capturedAvatarFrame == Time.frameCount && capturedAvatarRevision == avatarRevision)
                avatarObservedByCamera = true;
        }
        void AfterCameraRender(Camera camera)
        {
            if(camera != presentationCamera) return;
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
            ResetCapture();
            var callback = StimulusRendered;
            if(!present || callback == null || !acknowledgedTrials.Add(trial)) return;
            if(callback != null) callback(trial, Time.realtimeSinceStartupAsDouble);
        }
        bool CanAcknowledge()
        {
            // A witness on the actual TextMesh must be culled in for this camera,
            // followed by its completed render. Hidden/stale views cannot acknowledge.
            return PresentationIsAvailable() && isActiveAndEnabled && role == Actor.Participant && GraphicsSettings.currentRenderPipeline == null &&
                presentationCamera != null && presentationCamera.isActiveAndEnabled && presentationCamera.targetTexture == null &&
                presentationCamera.rect.width > 0 && presentationCamera.rect.height > 0 &&
                !String.IsNullOrEmpty(displayedTrial) && currentView != null && currentView.TrialId == displayedTrial &&
                (currentView.Phase == Phase.Practice || currentView.Phase == Phase.MainTask) &&
                stimulusText != null && stimulusText.gameObject.activeInHierarchy &&
                stimulusText.GetComponent<MeshRenderer>().enabled;
        }
        void ResetCapture() { capturedTrial = null; capturedFrame = -1; stimulusObservedByCamera = false; }
        void RequireLayout() { if(contentRoot == null) throw new InvalidOperationException("Initialize the presentation with its camera first."); }

        void BuildLayout()
        {
            contentRoot = Child("WorldSpaceContent", transform, Vector3.zero);
            stimulusText = Text("Stimulus", contentRoot, new Vector3(.5f, 1.65f, 2), .13f, 100, Color.white);
            stimulusText.gameObject.AddComponent<StimulusRenderWitness>().Bind(this);
            stimulusText.gameObject.SetActive(false);
            phaseText = Text("RoleAndPhase", contentRoot, new Vector3(.5f, 2.05f, 2), .035f, 50, Color.white);
            messageText = Text("ParticipantInstructions", contentRoot, new Vector3(.5f, 1.25f, 2), .032f, 48, Color.white);
            provisionalText = Text("ProvisionalOperatorPolicy", contentRoot, new Vector3(.5f, .85f, 2), .029f, 48, new Color(1, .7f, .25f));
            avatarRoot = Child("FixedBodyAvatar", contentRoot, new Vector3(-.65f, .9f, 2.5f));
            avatarRoot.localRotation = Quaternion.Euler(0, 180, 0);
            Primitive("FixedTorso", PrimitiveType.Capsule, avatarRoot, Vector3.zero, new Vector3(.42f, .48f, .3f), new Color(.27f, .45f, .55f));
            avatarHead = Child("DrivenHead", avatarRoot, new Vector3(0, .7f, 0));
            Primitive("HeadShape", PrimitiveType.Sphere, avatarHead, Vector3.zero, new Vector3(.4f, .45f, .36f), new Color(.77f, .68f, .56f));
            leftEye = BuildEye("LeftEye", new Vector3(-.09f, .05f, .16f));
            rightEye = BuildEye("RightEye", new Vector3(.09f, .05f, .16f));
            foreach(var renderer in avatarRoot.GetComponentsInChildren<MeshRenderer>(true)) renderer.gameObject.AddComponent<AvatarRenderWitness>().Bind(this);
            avatarLabel = Text("AvatarScope", contentRoot, new Vector3(-.65f, .25f, 2.5f), .028f, 48, Color.white);
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
            if(view.Phase == Phase.Preparation || view.Phase == Phase.Familiarization)
                return headsetPreview ? "QUEST LINK PREVIEW\nWaiting for the separate owner control window.\nOpen AvatarStroopQuestPreview.exe to start.\nHead view only; all avatar gaze is simulated." :
                    "DESKTOP SIMULATION\nWaiting for the separate owner control window.\nOpen AvatarStroopPreview.exe to start a preview.\nNo headset or real eye tracking is connected.";
            if(view.Phase == Phase.BlockPreparation || view.Phase == Phase.BlockReady)
                return "Waiting for the owner to configure and begin\nthis block in the separate preview control window.";
            if(view.Phase == Phase.PracticeRetry) return "Practice needs one retry.\nAsk the owner to start it in the control window.";
            if(view.Phase == Phase.PracticeCorrection) return view.PracticeCorrection + (headsetPreview ? "\nRight trigger to continue when ready." : "\nPress Space to continue when ready.");
            if(view.Phase == Phase.Questionnaire)
            {
                if(questionnaireLanguage == null) return "QUESTIONNAIRE LANGUAGE NOT CONFIGURED";
                var message = new StringBuilder(Wrap(Questionnaire.Instruction(questionnaireLanguage), 48, 2));
                message.Append("\n\n").Append(Wrap(view.QuestionnaireItem ?? "", 48, 5)).AppendLine();
                for(int score = 1; score <= 5; score++) message.Append("\n").Append(score).Append("  ").Append(Questionnaire.Anchor(score, questionnaireLanguage));
                return message.ToString();
            }
            if(view.Phase == Phase.Rest) return view.ReadyEnabled ? "Rest as long as you need.\n" + (headsetPreview ? "Right trigger when ready." : "Press Space when ready.") : "Rest. Ready is not yet available.";
            if(view.Phase == Phase.Paused) return headsetPreview ? "Paused\nRight trigger to actively resume.\nB to withdraw and end preview." : "Paused\nPress R to actively resume when ready.";
            if(view.Phase == Phase.NeedsProtocolDecision) return headsetPreview ? "Practice was interrupted.\nWaiting for an explicit owner protocol decision.\nNo automatic retry or continuation. B withdraws." : "Practice was interrupted.\nWaiting for an explicit owner protocol decision.\nNo automatic retry or continuation. Esc withdraws.";
            if(view.Phase == Phase.Ended) return "Simulation ended.\nThe owner can export the actual records.\nClose this window when finished.";
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
