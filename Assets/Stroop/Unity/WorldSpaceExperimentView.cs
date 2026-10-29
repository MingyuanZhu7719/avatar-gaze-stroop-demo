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
        public const string GeometryVersion = "development-classroom-root-local-v2";
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
        [SerializeField] TextMesh previewInputText;
        [SerializeField] TextMesh previewStatusText;
        TextMesh developmentBanner;
        Transform headerBacking, footerBacking, worldMenuBacking;
        Transform controllerLegend;
        readonly Vector3[] hudCorners = new Vector3[4];
        readonly Vector3[] hudProjectionPoints = new Vector3[4], stimulusProjectionPoints = new Vector3[8];
        string previewDiagnostic = "", previewControls = "", faultCaption = "";
        int hudDiagnosticsRemaining=3;
        float nextHudDiagnosticAt;
        bool lastHudStereo;
        bool faultCoversStimulus;
        // A centred, bounded angular panel is easier to read through the headset
        // lenses than labels spread over the full render frustum. Distance alone
        // would not help because the text fitter otherwise scales with the frustum.
        const float HeadsetHudDistance = 2f;
        const float HeadsetHudWidthDegrees = 50f, HeadsetHudHeightDegrees = 40f;
        float HudDistance { get { return headsetPreview ? HeadsetHudDistance : 1.1f; } }
        public static readonly Vector3 FixedMenuPosition = new Vector3(-.5f,1.55f,1.90f);
        public const float FixedMenuWidth=1.35f, FixedMenuHeight=1.05f;
        public bool HasWorldFixedMenu { get { return headsetPreview&&role==Actor.Participant; } }
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
            // The full standing body reaches below a level 60-degree mono view.
            // A desktop-only pitch keeps the feet and board stand in frame without moving
            // the camera or gaze targets. Quest's tracked camera is not changed.
            if(!headsetPreview) camera.transform.rotation = Quaternion.Euler(15.5f, -4, 0);
            EnsureHudLayout();
            var witness = stimulusText.GetComponent<StimulusRenderWitness>();
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
            RefreshLegendVisibility();
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
            EnsureControllerLegend();RefreshLegendVisibility();
            EnsureHudLayout();
            previewDiagnostic = diagnostic ?? ""; previewControls = controls ?? "";
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
            previewStatusText.text = previewDiagnostic;
            previewInputText.text = previewControls;
            if(currentView != null && role == Actor.Participant) messageText.text = ParticipantMessage(currentView);
            RefreshWarnings();
            LayoutHud();
        }

        // Quest menus are fixed to the room, including faults and interrupted
        // phases. Desktop labels retain their existing camera layout. Neither
        // path changes stimulus/avatar transforms or presentation witnesses.
        void EnsureHudLayout()
        {
            if(warningRoot == null) return;
            warningRoot.SetParent(HasWorldFixedMenu?contentRoot:presentationCamera.transform,false);
            warningRoot.localPosition=HasWorldFixedMenu?FixedMenuPosition:new Vector3(0,0,HudDistance);
            warningRoot.localRotation=Quaternion.identity;
            warningRoot.localScale=Vector3.one;
            developmentBanner = warningRoot.Find("PermanentDevelopmentBanner").GetComponent<TextMesh>();
            foreach(var label in new[]{phaseText,messageText,provisionalText,avatarLabel})
            {
                if(label == null) continue;
                label.transform.SetParent(warningRoot, false);
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
            bool trial=currentView!=null&&(currentView.Phase==Phase.Practice||currentView.Phase==Phase.MainTask);
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
            string heading=headsetPreview?"QUEST LINK PREVIEW | SIMULATED GAZE\nHEAD VIEW ONLY | NO EYE TRACKING | NOT A STUDY RUN":"DESKTOP SIMULATION | HARDWARE UNVERIFIED";
            FitHud(developmentBanner,heading,frustum,new Rect(.02f,.02f,.96f,.09f),.034f,2);
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
                FitHud(previewInputText,previewControls,frustum,new Rect(.02f,.885f,.96f,.095f),.031f,3);
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
            LogHudProjection(frustum);
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
            label.text=WrapHud(value??"",columns,maxLines);
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
        bool HudOverlapsStimulus(Rect viewport,Rect frustum)
        {
            if(displayedTrial==null||stimulusText==null||!stimulusText.gameObject.activeInHierarchy) return false;
            var labelBox=HudBox(frustum,viewport);
            var bounds=stimulusText.GetComponent<MeshRenderer>().bounds;
            int at=0;
            for(int x=-1;x<=1;x+=2)for(int y=-1;y<=1;y+=2)for(int z=-1;z<=1;z+=2)
                stimulusProjectionPoints[at++]=bounds.center+Vector3.Scale(bounds.extents,new Vector3(x,y,z));
            hudProjectionPoints[0]=warningRoot.TransformPoint(new Vector3(labelBox.xMin,labelBox.yMin,0));
            hudProjectionPoints[1]=warningRoot.TransformPoint(new Vector3(labelBox.xMin,labelBox.yMax,0));
            hudProjectionPoints[2]=warningRoot.TransformPoint(new Vector3(labelBox.xMax,labelBox.yMin,0));
            hudProjectionPoints[3]=warningRoot.TransformPoint(new Vector3(labelBox.xMax,labelBox.yMax,0));
            for(int eyeIndex=0;eyeIndex<(presentationCamera.stereoEnabled?3:1);eyeIndex++)
            {
                var eye=eyeIndex==0?Camera.MonoOrStereoscopicEye.Mono:eyeIndex==1?Camera.MonoOrStereoscopicEye.Left:Camera.MonoOrStereoscopicEye.Right;
                Rect stimulusBox,hudBox;
                int stimulusVisible=ProjectHudPoints(stimulusProjectionPoints,eye,out stimulusBox);
                if(stimulusVisible==0) continue;
                if(stimulusVisible<0||ProjectHudPoints(hudProjectionPoints,eye,out hudBox)!=1||hudBox.Overlaps(stimulusBox)) return true;
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
        void RefreshLegendVisibility()
        {
            if(controllerLegend==null)return;
            bool useful=headsetPreview&&role==Actor.Participant&&currentView!=null&&
                (currentView.Phase==Phase.Practice||currentView.Phase==Phase.MainTask||currentView.Phase==Phase.PracticeCorrection);
            controllerLegend.gameObject.SetActive(useful);
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
                if(headsetPreview&&!CleanTaskPresentation&&String.Equals(fault.Trim(),previewDiagnostic.Trim(),StringComparison.Ordinal)) continue;
                if(shown<3){text.AppendLine(Wrap(fault,52,2));shown++;}else additional++;
            }
            if(additional>0) text.AppendLine("Additional fault flags: "+additional);
            faultCaption = text.Length == 0 ? (headsetPreview ? "" : "Development display; hardware validation pending.") : text.ToString().TrimEnd();
            faultText.text = faultCaption;
            faultText.color = text.Length == 0 ? new Color(1, .82f, .35f) : new Color(1, .42f, .35f);
        }

        void BeforeCameraCull(Camera camera)
        {
            if(camera != presentationCamera) return;
            LayoutHud();
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
            return PresentationIsAvailable() && !faultCoversStimulus && isActiveAndEnabled && role == Actor.Participant && GraphicsSettings.currentRenderPipeline == null &&
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
            // Text depth and head/eye offsets are preserved. Place the body
            // in front of the board so its task gaze does not pass through the
            // opaque board back. Bootstrap derives both gaze targets from these
            // final world-space anchors and the fixed body orientation.
            stimulusText.transform.localPosition = new Vector3(-.5f, 1.65f, 2);
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
                var board = Child("Whiteboard", room, new Vector3(-.5f,1.65f,2.04f));
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
            if(headsetPreview)EnsureControllerLegend();
            RefreshLegendVisibility();
        }
        void EnsureControllerLegend()
        {
            controllerLegend=contentRoot.Find("QuestControllerLegend");
            if(controllerLegend!=null)return;
            controllerLegend=Child("QuestControllerLegend",contentRoot,new Vector3(-1.60f,1.55f,2.04f));
            Primitive("LegendFrame",PrimitiveType.Cube,controllerLegend,Vector3.zero,new Vector3(.55f,.82f,.035f),new Color(.78f,.81f,.79f));
            Primitive("LegendFace",PrimitiveType.Cube,controllerLegend,new Vector3(0,0,-.022f),new Vector3(.525f,.795f,.014f),new Color(.12f,.15f,.16f));
            BuildControllerLegend("LeftController",-.135f,true);
            BuildControllerLegend("RightController",.135f,false);
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
            if(view.Phase == Phase.Preparation || view.Phase == Phase.Familiarization)
                return headsetPreview ? "Use the owner controls to begin practice\nonce the headset and both Touch controllers are ready.\nNo practice or source starts automatically." :
                    "Use the separate owner controls to begin practice.\nNo practice or source starts automatically.";
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
