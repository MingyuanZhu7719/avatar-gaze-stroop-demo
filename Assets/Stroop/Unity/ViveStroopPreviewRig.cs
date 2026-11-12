#if STROOP_XR_PREVIEW
using System;
using System.Collections;
using AvatarStroop.Core;
using AvatarStroop.Runtime;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.XR;
using UnityEngine.XR.Management;
using UnityEngine.XR.OpenXR;

namespace AvatarStroop.Unity
{
    // Independent VIVE development preview. Optional local recording is owned
    // by the bootstrap and consumes the same worker as the eye diagnostic.
    public sealed class ViveStroopPreviewRig : MonoBehaviour
    {
        public static string ScopeNotice {get{return ViveStroopRecordingScopePresentation.BuildDesignScope(ViveControlledRecordingBuildPolicy.Available,ViveStroopDevelopmentBootstrap.LegacyRecordingAvailable);}}
        const string StoppedNotice="View and eye client stopped. A configured task may remain paused. Stopping the view does not save or export task results.";
        [SerializeField] Camera previewCamera;
        [SerializeField] Transform trackingOrigin, cameraOffset;
        [SerializeField] Transform gazeMarker;
        [SerializeField] WorldSpaceExperimentView view;
        [SerializeField] ViveStroopDevelopmentBootstrap development;
        XRManagerSettings manager;
        XRDisplaySubsystem display;
        XRInputSubsystem input;
        MonotonicClock clock;
        VivePreviewStartAlignment alignment;
        TrackingOriginModeFlags alignedMode;
        ViveProEyeWorker worker;
        ViveStroopEyePump eyePump;
        ViveProEyeHeadEyeSource source;
        bool starting, running, ownsLoader, referenceChanged, headReady, stopping, restartBlocked, xrCleanupUncertain, quitRequested;
        bool rawInstallEyeStopUncertain,rawInstallFailureLatched;
        bool RawInstallFailure {get{return rawInstallFailureLatched||(development!=null&&development.NamedRawInstallFailed);}}
        internal void LatchRawInstallFailure(){rawInstallFailureLatched=true;}
        ViveStartupAlignmentGate startupGate;
        ViveStartupView startupView;
        uint presenceEpoch;
        bool hasPresenceEpoch,hasHeadDevice;
        InputDevice headDevice;
        string headStatus="Waiting for headset tracking.";
        int headDiagnosticFrame=-1;
        public string CurrentHeadDiagnostic
        {get{return headDiagnosticFrame==Time.frameCount?headStatus:"No complete headset readiness check in this frame.";}}
        string startupInputDiagnostic="Controller not configured.";
        bool startupInputReady;
        bool rawCompletionEyeStopRequested;
        bool rawEyeCleanupObserved,rawEyeCleanupSafe;
        bool hasSeenUsableEye;
        readonly VivePreviewMarkerContinuity gazeContinuity=new VivePreviewMarkerContinuity();
        double gazeObservedAt=Double.NaN;
        int alignedFrame, visibleMask=-1,headReadyFrame=-1;
        long headSequence;
        Vector3 rawPosition;
        Quaternion rawRotation;
        string status=StoppedNotice;
        GUIStyle label,button,panel;
        GUISkin ownerSkin;
        Vector2 ownerScroll;

        public void Configure(Camera camera, Transform origin, Transform offset, WorldSpaceExperimentView classroom)
        {
            if(Application.isPlaying)throw new InvalidOperationException("Configure only outside Play Mode.");
            if(camera==null||origin==null||offset==null||classroom==null||classroom.Role!=Actor.Participant||!offset.IsChildOf(origin))
                throw new ArgumentException("A participant view and valid camera origin are required.");
            previewCamera=camera;trackingOrigin=origin;cameraOffset=offset;view=classroom;
            view.SetViveUnconfiguredPreview(status);
            view.SetFaults(new[]{"VIVE input is unavailable. No task has started."});
        }
        public bool ViewRunning {get{return starting||running||stopping;}}
        public string Status {get{return status;}}
        public bool HeadReady {get{return running&&headReady;}}
        internal MonotonicClock ActiveViewClock {get{return running&&!stopping?clock:null;}}
        public static Rect OwnerPanelBounds(float width,float height)
        {return new Rect(12,12,Mathf.Max(0,Mathf.Min(1050,width-24)),Mathf.Max(0,Mathf.Min(700,height-24)));}
        public bool RecordingWorkerReady {get{return running&&headReady&&worker!=null&&eyePump!=null&&worker.Started&&!stopping&&!restartBlocked&&
            development!=null&&development.Configured&&object.ReferenceEquals(clock,development.SharedClock)&&worker.UsesClock(clock);}}
        // This supplies only this rig's installed worker to the scene-owned
        // recording host. It neither creates a worker nor starts recording.
        internal bool TryGetNamedRawInstalledWorker(ViveStroopDevelopmentBootstrap requester,out ViveProEyeWorker result)
        {return TryGetRawInstalledWorker(false,requester,out result);}
        internal bool TryGetLegacyRawInstalledWorker(ViveStroopDevelopmentBootstrap requester,out ViveProEyeWorker result)
        {return TryGetRawInstalledWorker(true,requester,out result);}
        bool TryGetRawInstalledWorker(bool legacy,ViveStroopDevelopmentBootstrap requester,out ViveProEyeWorker result)
        {
            result=null;
            if(requester==null||!requester.MatchesRawRecordingKind(legacy)||!requester.RawRecordingDesignAvailable||
                !object.ReferenceEquals(development,requester)||!RecordingWorkerReady||!worker.UsesInstalledSdkPath)return false;
            result=worker;return true;
        }
        internal ViveStroopRecordingController EnableRecording(ViveDevelopmentParticipant task,IClock taskClock,string directory,bool eyeChoice,bool taskChoice)
        {
            throw new InvalidOperationException("Local VIVE recording is unavailable pending participant authorization and storage review.");
        }
        public void BlockTaskPresentation()
        {
            if(previewCamera!=null)previewCamera.cullingMask=0;
            if((RawInstallFailure||(development!=null&&development.NamedRawPumpAttached))&&view!=null)
            {view.SuppressViveRawStimulus();view.SuppressViveRawPanel();if(development!=null)development.MarkRawPanelHidden();}
        }
        // A per-frame software readiness check only. It is not an assertion
        // about photon delivery or the physical accuracy of head tracking.
        internal bool RawSceneTrackingCurrent()
        {
            if(development==null||!development.RawRecordingDesignAvailable||!running||!headReady||headReadyFrame!=Time.frameCount||
                GraphicsSettings.currentRenderPipeline!=null||display==null||!display.running||!OpenXRUtility.IsSessionFocused||referenceChanged||input==null||!input.running||
                input.GetTrackingOriginMode()!=alignedMode||previewCamera==null||!previewCamera.isActiveAndEnabled)return false;
            var device=InputDevices.GetDeviceAtXRNode(XRNode.Head);bool tracked;InputTrackingState flags;Vector3 position;Quaternion rotation;
            return device.isValid&&device.TryGetFeatureValue(CommonUsages.isTracked,out tracked)&&tracked&&
                device.TryGetFeatureValue(CommonUsages.trackingState,out flags)&&
                (flags&(InputTrackingState.Position|InputTrackingState.Rotation))==(InputTrackingState.Position|InputTrackingState.Rotation)&&
                device.TryGetFeatureValue(CommonUsages.devicePosition,out position)&&Finite(position)&&
                device.TryGetFeatureValue(CommonUsages.deviceRotation,out rotation)&&Finite(rotation)&&
                Mathf.Abs(rotation.x*rotation.x+rotation.y*rotation.y+rotation.z*rotation.z+rotation.w*rotation.w-1)<.000001f;
        }
        internal bool TryRevealRawScene()
        {
            if(!RawSceneTrackingCurrent()||visibleMask==0)return false;
            previewCamera.cullingMask=visibleMask;
            return previewCamera.cullingMask==visibleMask&&RawSceneTrackingCurrent();
        }
        public void ConfigureDevelopment(ViveStroopDevelopmentBootstrap task)
        {if(Application.isPlaying)throw new InvalidOperationException("Author development wiring outside Play Mode.");development=task;}
        public void ConfigureGazeMarker(Transform marker)
        {
            if(Application.isPlaying)throw new InvalidOperationException("Author the gaze marker outside Play Mode.");
            if(marker==null||marker.GetComponent<Renderer>()==null||marker.GetComponent<Collider>()!=null)
                throw new ArgumentException("A collider-free visible marker is required.");
            gazeMarker=marker;HideGazeMarker();
        }
        public Transform GazeMarker {get{return gazeMarker;}}
        void Awake()
        {
            Application.runInBackground=true;
            Application.wantsToQuit+=WantsToQuit;
            if(view!=null)view.SetViveUnconfiguredPreview(status);
        }
        void OnEnable(){Camera.onPreCull+=GuardRawSceneBeforeCull;}
        void OnDisable()
        {
            Camera.onPreCull-=GuardRawSceneBeforeCull;
            HideGazeMarker();gazeContinuity.Reset();
            if(RawInstallFailure){ServiceRawInstallFailure();return;}
            if(development!=null&&development.NamedRawPumpAttached)
            {try{BlockTaskPresentation();}finally{development.FailNamedRawAfterTask();}}
        }
        void GuardRawSceneBeforeCull(Camera camera)
        {
            // Any camera can render the shared marker; do not allow another
            // camera to draw it after the observation has expired.
            try{if(!GazeStillCurrent())HideGazeMarker();}catch(Exception){HideGazeMarker();}
            if(development==null||!development.NamedRawPumpAttached)return;
            try
            {
                // A second scene camera must not render an unverified raw
                // trial. Mask the mesh globally and end this interval first.
                if(camera!=previewCamera)
                {
                    if(ViveRawPanelPolicy.RejectOtherCamera(true,development.RawRecordingDesignAvailable,false,
                        development.NamedRawTrialActive,development.NamedRawPanelExposed))
                    {BlockTaskPresentation();development.FailNamedRawAfterTask();}
                    return;
                }
                if(!RawSceneTrackingCurrent())
                {BlockTaskPresentation();development.FailNamedRawAfterTask();}
            }
            catch(Exception)
            {
                try{if(previewCamera!=null)previewCamera.cullingMask=0;}catch(Exception){}
                development.FailNamedRawAfterTask();
            }
        }
        void OnGUI()
        {
            using(var desktop=ViveOwnerDesktopLayout.Begin())
            {
            var previousSkin=GUI.skin;var previousColor=GUI.color;
            EnsureOwnerStyles();GUI.skin=ownerSkin;
            // IMGUI overlays the desktop mirror only. A fully opaque background
            // prevents classroom TextMesh geometry from leaking through the UI;
            // it does not change headset camera visibility or stimulus gates.
            GUI.color=new Color(.055f,.07f,.09f,1);
            GUI.DrawTexture(new Rect(0,0,desktop.Width,desktop.Height),Texture2D.whiteTexture);
            GUI.color=Color.white;
            GUILayout.BeginArea(OwnerPanelBounds(desktop.Width,desktop.Height),panel);
            ownerScroll=GUILayout.BeginScrollView(ownerScroll);
            if(development!=null)development.DrawOwnerControls();
            else
            {
                GUILayout.Label("PARTICIPANT  |  Setup",label);
                GUILayout.Label("The classroom task is not configured.",label);
            }
            GUILayout.EndScrollView();
            GUILayout.Space(10);
            if(development!=null)development.DrawOwnerSafetyControls();
            if(GUILayout.Button("Close application",button,GUILayout.Height(40)))RequestQuit();
            GUILayout.EndArea();
            GUI.skin=previousSkin;GUI.color=previousColor;
            }
        }
        void EnsureOwnerStyles()
        {
            if(ownerSkin!=null)return;
            ownerSkin=Instantiate(GUI.skin);
            ownerSkin.label.fontSize=17;ownerSkin.label.wordWrap=true;
            ownerSkin.label.normal.textColor=new Color(.92f,.94f,.97f,1);
            ownerSkin.button.fontSize=17;ownerSkin.button.wordWrap=true;
            ownerSkin.button.padding=new RectOffset(12,12,10,10);
            ownerSkin.textField.fontSize=17;ownerSkin.textField.padding=new RectOffset(8,8,8,8);
            ownerSkin.toggle.fontSize=17;ownerSkin.toggle.wordWrap=true;
            ownerSkin.toggle.padding=new RectOffset(24,8,8,8);
            label=new GUIStyle(ownerSkin.label){fontSize=18,wordWrap=true};
            button=new GUIStyle(ownerSkin.button){fontSize=17,wordWrap=true};
            panel=new GUIStyle(){padding=new RectOffset(20,20,18,18)};
        }
        // Called only inside the configured owner panel. Starting XR/eye
        // preview remains explicit and never begins task or file recording.
        public void DrawSessionViewControls()
        {
            EnsureOwnerStyles();
            GUILayout.Space(6);
            GUILayout.Label("Headset and eye tracking",label);
            GUILayout.Label(stopping?"Stopping headset view...":starting?"Starting headset view...":
                !running?"Headset view is off.":!headReady?"Put on the headset and follow its prompt.":
                worker==null?"Headset ready. Eye tracking is off.":"Headset ready. Eye tracking client is on.");
            bool previousEnabled=GUI.enabled;
            GUILayout.BeginHorizontal();
            GUI.enabled=previousEnabled&&!starting&&!running&&!stopping&&!xrCleanupUncertain&&!quitRequested&&(development==null||!development.RecordingBlocked);
            if(GUILayout.Button("Start classroom view",button,GUILayout.Height(45)))BeginView();
            GUI.enabled=previousEnabled&&(starting||running);
            if(GUILayout.Button("Stop view",button,GUILayout.Height(45)))StopView();
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            GUI.enabled=previousEnabled&&running&&headReady&&worker==null&&!stopping&&!restartBlocked&&(development==null||(!development.RecordingBlocked&&!development.PreSessionChoiceActive));
            if(GUILayout.Button("Enable eye tracking",button,GUILayout.Height(45)))BeginEye();
            GUI.enabled=previousEnabled&&worker!=null&&!stopping&&(development==null||!development.NamedRawCompletionActive);
            if(GUILayout.Button("Stop eye tracking",button,GUILayout.Height(45)))StopEye();
            GUILayout.EndHorizontal();GUI.enabled=previousEnabled;
            if(xrCleanupUncertain||restartBlocked||RawInstallFailure||(!ViewRunning&&status!=StoppedNotice))GUILayout.Label(status,label);
            GUILayout.Space(8);
        }
        public void BeginView()
        {
            if(starting||running||stopping||xrCleanupUncertain||quitRequested||(development!=null&&development.RecordingBlocked))return;
            if(!VivePreviewLaunchPlan.IsExpectedChildEnvironment(Environment.GetEnvironmentVariable(VivePreviewLaunchPlan.MarkerVariable),
                Environment.GetEnvironmentVariable(VivePreviewLaunchPlan.RuntimeVariable)))
            {SetStatus("SteamVR was not prepared for this process. Open the outer VIVE preview launcher.");return;}
            if(previewCamera==null||trackingOrigin==null||cameraOffset==null||view==null)
            {SetStatus("Classroom scene references are incomplete. XR was not started.");return;}
            if(development!=null)development.BeforeViewStart();
            try{if(development!=null)development.PrepareInputBeforeXr();}
            catch(Exception error){SetStatus("VIVE input setup failed: "+error.Message);return;}
            starting=true;visibleMask=previewCamera.cullingMask;previewCamera.cullingMask=0;
            SetStatus("Starting the configured SteamVR OpenXR view. Eye capture has not started.");
            StartCoroutine(InitializeView());
        }
        IEnumerator InitializeView()
        {
            manager=XRGeneralSettings.Instance==null?null:XRGeneralSettings.Instance.Manager;
            if(manager==null||manager.activeLoader!=null){Fail("Expected an uninitialized OpenXR loader.");yield break;}
            ownsLoader=true;
            IEnumerator operation;
            try{operation=manager.InitializeLoader();}catch(Exception e){Fail("OpenXR startup failed: "+e.GetType().Name);yield break;}
            while(true)
            {
                bool more;object current=null;
                try{more=operation.MoveNext();if(more)current=operation.Current;}catch(Exception e){Fail("OpenXR startup failed: "+e.GetType().Name);yield break;}
                if(!more)break;yield return current;
            }
            try
            {
                if(manager.activeLoader==null||String.IsNullOrEmpty(OpenXRRuntime.name)||!OpenXRRuntime.name.StartsWith("SteamVR",StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("SteamVR OpenXR is unavailable or unexpected.");
                display=manager.activeLoader.GetLoadedSubsystem<XRDisplaySubsystem>();input=manager.activeLoader.GetLoadedSubsystem<XRInputSubsystem>();
                if(display==null||input==null)throw new InvalidOperationException("OpenXR display/input is unavailable.");
                manager.StartSubsystems();alignment=new VivePreviewStartAlignment();startupGate=new ViveStartupAlignmentGate();
                if(startupView!=null)startupView.Dispose();startupView=new ViveStartupView(previewCamera);
                hasPresenceEpoch=false;hasHeadDevice=false;referenceChanged=false;headReady=false;alignedFrame=-1;
                input.trackingOriginUpdated+=OnTrackingOriginUpdated;
                trackingOrigin.SetPositionAndRotation(Vector3.zero,Quaternion.identity);
                cameraOffset.localPosition=Vector3.zero;cameraOffset.localRotation=Quaternion.identity;
                previewCamera.transform.SetLocalPositionAndRotation(Vector3.zero,Quaternion.identity);
                clock=development!=null&&development.Configured?development.SharedClock:
                    new MonotonicClock("vive-classroom-view-"+Guid.NewGuid().ToString("N"));
                if(clock==null)throw new InvalidOperationException("Configured task clock is unavailable.");
                headSequence=0;
                starting=false;running=true;SetStatus("Put on the headset and follow the startup prompt. Eye capture is off.");
            }
            catch(Exception e){Fail("Classroom view unavailable: "+e.Message);}
        }
        void BeginEye()
        {
            if(!running||!headReady||worker!=null||stopping||restartBlocked||(development!=null&&(development.RecordingBlocked||development.PreSessionChoiceActive)))return;
            try
            {
                string epoch=Guid.NewGuid().ToString("N");
                hasSeenUsableEye=false;
                HideGazeMarker();gazeContinuity.Reset();
                worker=ViveProEyeWorker.CreateInstalled(clock,ReadHead);
                if(!worker.Start())throw new InvalidOperationException(worker.Diagnostic);
                eyePump=new ViveStroopEyePump(worker);
                source=new ViveProEyeHeadEyeSource("vive-classroom-preview-"+epoch,0,0,"vive-pro-eye/operator/classroom-diagnostic-"+epoch,
                    clock,eyePump,new ViveProEyeRehearsalCoordinates());
                SetStatus("Eye diagnostic starting. This does not start task or eye recording.");
            }
            catch(Exception e){SetStatus("Eye diagnostic unavailable: "+e.Message);StopEye();}
        }
        ViveHeadFrame ReadHead()
        {
            headDiagnosticFrame=-1;
            var device=InputDevices.GetDeviceAtXRNode(XRNode.Head);bool tracked=false;InputTrackingState flags=InputTrackingState.None;
            if(device.isValid)
            {
                if(hasHeadDevice&&!device.Equals(headDevice))
                {if(alignment.Applied)referenceChanged=true;else{startupGate.Reset();alignment.ResetPending();}}
                headDevice=device;hasHeadDevice=true;
            }
            bool gotPosition=device.TryGetFeatureValue(CommonUsages.devicePosition,out rawPosition);
            bool gotRotation=device.TryGetFeatureValue(CommonUsages.deviceRotation,out rawRotation);
            device.TryGetFeatureValue(CommonUsages.isTracked,out tracked);device.TryGetFeatureValue(CommonUsages.trackingState,out flags);
            bool validRotation=gotRotation&&Finite(rawRotation)&&Mathf.Abs(rawRotation.x*rawRotation.x+rawRotation.y*rawRotation.y+rawRotation.z*rawRotation.z+rawRotation.w*rawRotation.w-1)<.000001f;
            bool subsystems=display!=null&&display.running&&input!=null&&input.running;
            bool focused=OpenXRUtility.IsSessionFocused;
            var mode=input!=null&&input.running?input.GetTrackingOriginMode():TrackingOriginModeFlags.Unknown;
            bool supported=mode==TrackingOriginModeFlags.Device||mode==TrackingOriginModeFlags.Floor;
            bool pose=subsystems&&device.isValid&&tracked&&gotPosition&&Finite(rawPosition)&&validRotation&&
                (flags&(InputTrackingState.Position|InputTrackingState.Rotation))==(InputTrackingState.Position|InputTrackingState.Rotation)&&focused;
            var presence=OpenXRSettings.Instance==null?null:OpenXRSettings.Instance.GetFeature<ViveVerifiedPresenceFeature>();
            bool capable=presence!=null&&presence.enabled&&presence.CapabilityVerified;
            bool present=capable&&OpenXRUtility.IsUserPresent;
            if(presence!=null)
            {
                if(hasPresenceEpoch&&presenceEpoch!=presence.SessionEpoch)
                {if(alignment.Applied)referenceChanged=true;else{startupGate.Reset();alignment.ResetPending();}}
                presenceEpoch=presence.SessionEpoch;hasPresenceEpoch=true;
            }
            if(alignment.Applied&&supported&&mode!=alignedMode)referenceChanged=true;
            if(!alignment.Applied)
            {
                var controls=development==null?null:development.ReadStartupAlignmentInput(pose&&supported);
                startupInputReady=controls!=null&&controls.DeviceReady;
                startupInputDiagnostic=controls==null?"VIVE controller input is not configured.":controls.Diagnostic;
                bool eligible=startupGate.Observe(Time.frameCount,subsystems,pose&&supported,capable,present,
                    startupView!=null&&startupView.WasRenderedBefore(Time.frameCount),controls!=null&&controls.DeviceReady,
                    controls!=null&&controls.DeviceChanged,controls!=null&&controls.TriggerHeld,
                    controls!=null&&controls.Trackpad.Pressed,controls!=null&&controls.MenuHeld);
                Vector3 board=view.TaskWorldPosition;
                Vector3 target=new Vector3(board.x,board.y,WorldSpaceExperimentView.CenteredParticipantReference.z);
                if(alignment.TryApply(eligible,mode,Time.frameCount,Time.realtimeSinceStartupAsDouble,
                    trackingOrigin,cameraOffset,rawPosition,rawRotation,target))
                {alignedMode=mode;alignedFrame=Time.frameCount;}
            }
            headReady=pose&&alignment.Applied&&!referenceChanged&&mode==alignedMode&&Time.frameCount>alignedFrame&&(!capable||present);
            headReadyFrame=headReady?Time.frameCount:-1;
            if(pose)previewCamera.transform.SetLocalPositionAndRotation(rawPosition,rawRotation);
            ApplyTrackedMask(headReady);
            if(!subsystems)headStatus="Waiting for OpenXR display and input.";
            else if(!focused)headStatus="OpenXR is not focused. Put on the headset and close the SteamVR dashboard.";
            else if(!device.isValid)headStatus="Headset input device is unavailable.";
            else if(!tracked)headStatus="Headset tracking is unavailable. Check visibility to the base stations.";
            else if((flags&(InputTrackingState.Position|InputTrackingState.Rotation))!=(InputTrackingState.Position|InputTrackingState.Rotation))headStatus="Headset position or rotation is not tracked.";
            else if(!gotPosition||!Finite(rawPosition))headStatus="Headset position sample is invalid.";
            else if(!validRotation)headStatus="Headset rotation sample is invalid.";
            else if(!supported)headStatus="Unsupported tracking origin: "+mode+". View remains hidden.";
            else if(referenceChanged)headStatus="Tracking reference changed after placement. Stop view and start it again while the task is safely paused.";
            else if(capable&&!present)headStatus="Headset is not worn. Put it on to continue.";
            else if(alignment.HeightRejected)headStatus="Tracking reference needs an excessive vertical shift. Stop view and check room setup.";
            else if(headReady)headStatus="Head view ready. Eye capture is off.";
            else if(alignment.Applied)headStatus="Initial view placed. Preparing the next tracked frame.";
            else if(startupGate.AutomaticWearVerified||startupGate.ManualConfirmed)headStatus="Look naturally forward and hold still briefly. Preparing the view once.";
            else if(development==null||!development.StartupControllerConfigured)headStatus="Configure the session on the desktop, then restart the headset view.";
            else if(!startupInputReady)headStatus="Startup controller is not ready: "+startupInputDiagnostic;
            else headStatus="In the headset: release all controls, then press the trigger once to confirm you are wearing it.";
            headDiagnosticFrame=Time.frameCount;
            if(startupView!=null)
            {
                if(alignment.Applied)startupView.Hide();
                else
                {
                    string prompt=startupGate.KnownAbsent?"Put on the headset.":
                        alignment.HeightRejected?"View preparation paused.\nCheck the desktop status.":
                        startupGate.AutomaticWearVerified||startupGate.ManualConfirmed?"Look naturally forward.\nHold still briefly.\nPreparing your view...":
                        development==null||!development.StartupControllerConfigured?"Configure the session on the desktop,\nthen restart the view.":
                        !startupInputReady?"Controller unavailable.\nConnect exactly one VIVE controller.\nCheck controller status on the desktop.":
                        "Wearing the headset?\nRelease all controls, then press TRIGGER.\nLook naturally forward and hold still.";
                    startupView.Show(pose&&supported,prompt);
                }
            }
            return new ViveHeadFrame(headSequence++,clock.Now,device.isValid,headReady,new Rotation(rawRotation.x,rawRotation.y,rawRotation.z,rawRotation.w));
        }
        // Kept separate from XR sampling so Editor-only synthetic fixtures can
        // validate the actual camera gate without claiming a tracked headset.
        internal void ApplyTrackedMask(bool trackedHeadReady)
        {
            // No raw trial-open receipt producer exists yet. A task view can
            // activate the stimulus independently of PresentationAvailable,
            // so keep the physical camera masked for every raw-attached frame.
            bool rawOpenUnproven=development!=null&&development.NamedRawPumpAttached;
            if(rawOpenUnproven&&view!=null)view.SuppressViveRawStimulus();
            previewCamera.cullingMask=trackedHeadReady&&!rawOpenUnproven&&(development==null||!development.PresentationBlocked)?visibleMask:0;
        }
        void Update()
        {
            // No previous-frame marker may survive a skipped/blocked update.
            HideGazeMarker();
            // A partially initialized recorder cannot fall back to an
            // unrecorded task. Keep the scene masked and request worker stop
            // without waiting; the native outcome remains unconfirmed.
            if(RawInstallFailure)
            {
                ServiceRawInstallFailure();
                SetStatus("Recording startup failed; task is blocked and cleanup is unconfirmed."+RawEyeCleanupStatus);
                return;
            }
            // Before any new input, a missing prior-frame callback is a lost
            // presentation interval. Cancel rather than leave P held.
            if(development!=null&&development.ExpirePendingRawPresentation())
            {BlockTaskPresentation();SetStatus("Render callback did not complete in its frame; task safety is pending.");return;}
            // Completion is not a recording fault. Keep presentation masked,
            // stop ordinary polling and let the owner observe the asynchronous
            // drain without waiting for native I/O or accepting new task input.
            if(development!=null&&development.NamedRawCompletionActive)
            {
                try
                {
                    BlockTaskPresentation();
                    bool finishing=development.PollNamedRawCompletion();
                    if(finishing&&!rawCompletionEyeStopRequested)
                        rawCompletionEyeStopRequested=TryStopEyeForRawSafety();
                    ObserveRawEyeCleanup();
                    SetStatus((finishing?"Task ended; recording I/O may still be pending.":
                        "Recording safety is active; task stop and I/O may remain unconfirmed.")+RawEyeCleanupStatus);
                }
                catch(Exception)
                {
                    // Do not retry a task mutation or claim a confirmed stop.
                    // The existing safety path attempts revocation and leaves
                    // any in-flight I/O and task interval unknown.
                    try{development.FailNamedRawAfterTask();}catch(Exception){}
                    try{if(previewCamera!=null)previewCamera.cullingMask=0;}catch(Exception){}
                    status="Completion safety is unconfirmed; recording tail and eye cleanup are unknown.";
                }
                return;
            }
            // Until raw trial-open receipts exist, hide any stale stimulus even
            // when a fault/worker-status branch returns before head polling.
            if(development!=null&&development.NamedRawPumpAttached)BlockTaskPresentation();
            // This branch precedes FinishStop and all ordinary input or
            // worker polling. A contested task/worker lock must not block the
            // immediate camera mask or the next safety attempt.
            if(development!=null&&development.NamedRawPumpAttached&&development.RecordingBlocked)
            {
                if(previewCamera!=null)previewCamera.cullingMask=0;
                development.PollRecordingBlockedSafety();
                ObserveRawEyeCleanup();
                SetStatus("Recording stopped; task safety may be pending. Presentation remains blocked."+RawEyeCleanupStatus);return;
            }
            if(development!=null&&development.NamedRawPumpAttached)
            {
                object run;
                if(worker==null||stopping||eyePump==null||!worker.TryGetRecordingRunIdentity(out run))
                {
                    if(previewCamera!=null)previewCamera.cullingMask=0;
                    development.FailNamedRawBeforeFrame();
                    SetStatus("Eye input unavailable; recording and task safety are stopped or pending.");return;
                }
                // Status must be consumed before polling a new eye frame.
                // Only initial Preparation can wait for the first baseline;
                // active trials are interrupted instead of silently spanning
                // a period whose eye status was not observed.
                var preparation=development.PrepareNamedRawStatus();
                if(preparation!=ViveNamedRawRecordingPump.Preparation.Ready)
                {
                    if(previewCamera!=null)previewCamera.cullingMask=0;
                    if(preparation==ViveNamedRawRecordingPump.Preparation.WaitingBaseline&&development.CanWaitForNamedRawBaseline)
                    {if(view!=null)view.SetPresentationAvailable(false);SetStatus("Waiting for initial eye status; no task input is processed.");}
                    else
                    {development.FailNamedRawBeforeFrame();SetStatus("Eye status unavailable; task safety is stopped or pending.");}
                    return;
                }
            }
            FinishStop();
            if(quitRequested&&!starting&&!running&&!stopping&&worker==null){Application.Quit();return;}
            if(!running)return;
            try
            {
                if(development!=null&&development.RecordingBlocked)
                {
                    development.ObservePracticeFrame(null);
                    previewCamera.cullingMask=0;
                    // Keep the camera culled and drain pending safety work.
                    // No controller or eye input is polled in this blocked path.
                    development.PollRecordingBlockedSafety();
                    previewCamera.cullingMask=0;
                    SetStatus("Recording stopped or faulted. Task presentation and restart are blocked; review the owner controls.");return;
                }
                if(worker==null){ReadHead();if(development!=null){development.Poll(headReady);development.ObservePracticeFrame(null);}SetStatus(headStatus);return;}
                // Native cleanup may outlive the Stop action. Keep head view
                // responsive and never dereference the now-cleared eye source.
                if(stopping){ReadHead();if(development!=null)development.Poll(headReady);SetStatus("Stopping eye preview; waiting for native cleanup. No new samples are accepted.");return;}
                if((development==null||!development.NamedRawPumpAttached)&&!worker.Starting&&!worker.Started&&!worker.Stopping){SetStatus("Eye client unavailable: "+worker.Diagnostic);StopEye();return;}
                if(eyePump==null)throw new InvalidOperationException("Eye pump unavailable.");
                var currentPump=eyePump;
                if(development!=null&&development.NamedRawPumpAttached)
                {
                    if(!currentPump.TryBeginRecordingFrame(worker))
                    {previewCamera.cullingMask=0;development.FailNamedRawBeforeFrame();SetStatus("Eye snapshot unavailable; task safety is stopped or pending.");return;}
                }
                else currentPump.BeginFrame();
                try
                {
                    // Worker.Poll catches head-reader exceptions. Never leave
                    // a previous frame's ready flag or camera mask in place.
                    if(!currentPump.HeadReady)
                    {
                        if(headDiagnosticFrame!=Time.frameCount||headReady)
                        {headStatus="Head sample unavailable from the input pipeline.";headDiagnosticFrame=Time.frameCount;}
                        headReady=false;ApplyTrackedMask(false);
                        if(development!=null&&development.NamedRawPumpAttached)
                        {development.FailNamedRawBeforeFrame();return;}
                    }
                    if(development!=null&&development.NamedRawPumpAttached&&!development.StageNamedRawFrame(currentPump.Poll()))
                    {previewCamera.cullingMask=0;SetStatus("Eye or recording input unavailable; task safety is stopped or pending.");return;}
                    if(development!=null&&development.RecordingEnabled)
                    {
                        // Recording and diagnostics share this frame; the latter
                        // uses only the recorder's non-payload usability result.
                        bool recordedUsable=development.PollRecordingEye()&&headReady;
                        if(development.RecordingBlocked){development.ObservePracticeFrame(null);previewCamera.cullingMask=0;SetStatus(development.RecordingStatus);return;}
                        development.Poll(headReady);
                        if(stopping||worker==null||!object.ReferenceEquals(eyePump,currentPump))
                        {SetStatus("Eye stop requested; native cleanup may still be pending.");return;}
                        if(development.RecordingBlocked){development.ObservePracticeFrame(null);previewCamera.cullingMask=0;SetStatus(development.RecordingStatus);return;}
                        development.ObservePracticeFrame(currentPump.Poll());
                        if(recordedUsable)hasSeenUsableEye=true;
                        SetStatus("Local development recording: "+development.RecordingStatus);return;
                    }
                    HeadEyeSample sample;bool emitted=source.TryRead(out sample);
                    bool usable=emitted&&sample!=null&&sample.EyeValid&&source.LastObservation.EyeUsable&&headReady;
                    UpdateGazeMarker(sample,source.LastObservation,usable);
                    if(development!=null&&development.NamedRawPumpAttached&&!usable)
                    {previewCamera.cullingMask=0;development.FailNamedRawBeforeFrame();SetStatus("Eye input unavailable; task safety is stopped or pending.");return;}
                    if(development!=null&&development.NamedRawPumpAttached&&!development.VerifyNamedRawEpoch())
                    {previewCamera.cullingMask=0;SetStatus("Eye status changed before task input; task safety is stopped or pending.");return;}
                    if(development!=null)development.Poll(headReady);
                    if(stopping||worker==null||!object.ReferenceEquals(eyePump,currentPump))
                    {if(development!=null&&development.NamedRawPumpAttached){previewCamera.cullingMask=0;development.FailNamedRawAfterTask();}SetStatus("Eye stop requested; native cleanup may still be pending.");return;}
                    if(development!=null&&development.NamedRawPumpAttached&&development.PresentationBlocked)
                    {previewCamera.cullingMask=0;development.FailNamedRawAfterTask();SetStatus("Task input interval is unknown; presentation remains blocked.");return;}
                    if(development!=null&&development.WaitForLegacyWozRawAcquisition())
                    {
                        if(!development.PresentationBlocked&&!development.DrainNamedRawTaskEvents())development.FailNamedRawAfterTask();
                        previewCamera.cullingMask=0;return;
                    }
                    if(development!=null&&development.NamedRawInputBoundaryPending)
                    {
                        // A plan receipt may start a phase during this Poll.
                        // Preserve the neutral edge boundary and keep the
                        // entire view masked until a later neutral sample.
                        if(!development.DrainNamedRawTaskEvents())development.FailNamedRawAfterTask();
                        previewCamera.cullingMask=0;return;
                    }
                    if(development!=null&&development.NamedRawTrialActive&&development.RawRecordingDesignAvailable)
                    {
                        // Preparation drains before reserving. When a fresh
                        // token holds P, no further task/status drain is
                        // allowed before this frame's render callback.
                        if(!development.PrepareRawScenePresentation())
                        {previewCamera.cullingMask=0;development.FailNamedRawAfterTask();SetStatus("Raw trial presentation was not safely prepared; interval is unknown.");}
                        return;
                    }
                    if(development!=null&&development.NamedRawPumpAttached)
                    {
                        if(development.NamedRawTaskCompleted)
                        {
                            if(!development.DrainNamedRawTaskEvents()||!development.RequestNamedRawCompletionIfEnded())
                            {previewCamera.cullingMask=0;SetStatus("Recording completion is unconfirmed; presentation remains blocked.");return;}
                            previewCamera.cullingMask=0;SetStatus("Task ended; recording drain is pending.");return;
                        }
                        // A non-trial panel is revealed only for the participant
                        // camera after its exact phase/item frame is prepared.
                        // Its renderer and post-render witness are required
                        // before ordinary input on the next owner update.
                        if(!development.PrepareRawNonTrialScene())
                        {previewCamera.cullingMask=0;development.FailNamedRawAfterTask();SetStatus("Panel rendering is unconfirmed; task safety is stopped or pending.");}
                        return;
                    }
                    if(development!=null)development.ObservePracticeFrame(currentPump.Poll());
                    if(usable)hasSeenUsableEye=true;
                    SetStatus(!headReady?"Head tracking unavailable; recording is off.":hasSeenUsableEye?
                        "At least one usable eye direction received since start. Current quality is not verified. Recording is off.":
                        "Waiting for a fresh valid eye direction. Recording is off.");
                }
                finally{currentPump.EndFrame();}
            }
            catch(Exception e){SetStatus("View error: "+e.GetType().Name);StopView();}
        }
        void StopEye()
        {
            if(RawInstallFailure){ServiceRawInstallFailure();return;}
            // A recording owns this worker's observation and status stream.
            // Revoke that authority and mask presentation before requesting
            // worker cleanup; never synchronously wait for a busy eye worker.
            if(development!=null&&development.NamedRawPumpAttached)
            {
                development.RequestNamedRawStopForViewClose();
                SetStatus("Recording stop requested; task, eye and I/O cleanup may be pending.");
                return;
            }
            HideGazeMarker();gazeContinuity.Reset();
            if(development!=null)development.SuspendPracticeObservation();
            try{if(development!=null&&development.RecordingEnabled)development.StopRecording();}
            finally
            {
                source=null;
                try{if(eyePump!=null)eyePump.Close();}
                finally
                {
                    eyePump=null;
                    if(worker!=null){worker.StopAndCaptureDiagnostics();stopping=true;}
                }
            }
            FinishStop();
        }
        internal void StopEyeAfterWithdrawal(){StopEye();}
        // The worker request does not wait for its native call or cleanup. A
        // busy worker lock leaves the request pending for the next update.
        internal bool TryStopEyeForRawSafety()
        {
            if(worker==null)return true;
            if(!worker.TryRequestStop())return false;
            source=null;var oldPump=eyePump;eyePump=null;stopping=true;
            if(oldPump!=null)oldPump.Close();return true;
        }
        void ServiceRawInstallFailure()
        {
            try{BlockTaskPresentation();}catch(Exception){if(previewCamera!=null)previewCamera.cullingMask=0;}
            // A false result means the worker lock was busy; an exception may
            // mean the request already ran and must not be repeated.
            if(!stopping&&!rawInstallEyeStopUncertain)
                try{TryStopEyeForRawSafety();}catch(Exception){rawInstallEyeStopUncertain=true;}
            // This is a nonblocking cleanup observation, not a repeated stop.
            try{ObserveRawEyeCleanup();}catch(Exception){rawInstallEyeStopUncertain=true;}
        }
        // Unlike FinishStop, this path never waits for the worker lock or for
        // native cleanup. The recording mask and safety menu retain priority.
        void ObserveRawEyeCleanup()
        {
            if(!stopping||worker==null)return;
            bool safe;if(!worker.TryFinishStopped(out safe))return;
            worker=null;stopping=false;restartBlocked|=!safe;
            rawEyeCleanupObserved=true;rawEyeCleanupSafe=safe;
        }
        internal string RawEyeCleanupStatus
        {get{return !rawEyeCleanupObserved?" Eye worker exit or cleanup is pending or unknown.":
            rawEyeCleanupSafe?" Eye worker exited; no client cleanup failure was reported.":
            " Eye worker exited; client cleanup was reported unsafe or failed.";}}
        void FinishStop()
        {
            if(stopping&&worker!=null&&worker.HasExited)
            {
                restartBlocked=!worker.CanStart;worker.Dispose();worker=null;stopping=false;
                if(restartBlocked)SetStatus("Eye cleanup is unconfirmed. Restart is blocked for this window.");
                else if(!running)SetStatus(StoppedNotice);
            }
        }
        void StopView()
        {
            if(RawInstallFailure){ServiceRawInstallFailure();SetStatus("Recording start failed; cleanup remains unconfirmed.");return;}
            if(development!=null&&development.NamedRawPumpAttached)
            {development.RequestNamedRawStopForViewClose();SetStatus("Stop requested; task, eye or recording cleanup may remain pending. Presentation is blocked.");return;}
            StopAllCoroutines();starting=false;running=false;headReady=false;
            if(startupView!=null){startupView.Dispose();startupView=null;}
            try{if(development!=null)development.BeforeXrStop();}catch(Exception error){Debug.LogWarning("Development task stop: "+error.GetType().Name);}
            StopEye();
            if(input!=null)input.trackingOriginUpdated-=OnTrackingOriginUpdated;
            try{if(ownsLoader&&manager!=null){manager.StopSubsystems();manager.DeinitializeLoader();}}
            catch(Exception error){xrCleanupUncertain=true;Debug.LogWarning("XR cleanup uncertain: "+error.GetType().Name);}
            finally
            {
                ownsLoader=false;manager=null;display=null;input=null;
                try{if(development!=null)development.AfterXrStop();}catch(Exception error){Debug.LogWarning("Development input cleanup: "+error.GetType().Name);}
                if(previewCamera!=null)previewCamera.cullingMask=development!=null&&development.PresentationBlocked?0:visibleMask;
            }
            SetStatus(xrCleanupUncertain?"XR cleanup is unconfirmed. Close this preview; restart is blocked.":stopping?"STOPPING - waiting for native eye-client cleanup. No new samples are accepted.":
                StoppedNotice);
        }
        void Fail(string message){StopView();SetStatus(message);}
        void RequestQuit()
        {
            quitRequested=true;
            if(RawInstallFailure){ServiceRawInstallFailure();Application.Quit();return;}
            // An explicit Exit is not held hostage by a stuck task lock or
            // native call. Any in-flight write/cleanup remains uncertain.
            if(development!=null&&development.NamedRawPumpAttached)
            {development.RequestNamedRawStopForViewClose();Application.Quit();return;}
            StopView();
        }
        bool WantsToQuit(){if(RawInstallFailure){ServiceRawInstallFailure();return true;}if(development!=null&&development.NamedRawPumpAttached){development.RequestNamedRawStopForViewClose();return true;}if(worker==null&&!starting&&!running&&!stopping)return true;RequestQuit();return false;}
        void OnDestroy()
        {
            if(ownerSkin!=null){Destroy(ownerSkin);ownerSkin=null;}
            HideGazeMarker();gazeContinuity.Reset();
            if(startupView!=null){startupView.Dispose();startupView=null;}
            Application.wantsToQuit-=WantsToQuit;
            if(input!=null)input.trackingOriginUpdated-=OnTrackingOriginUpdated;
            if(RawInstallFailure)
            {ServiceRawInstallFailure();UnityEngine.Debug.LogWarning("Raw startup failed; eye and XR cleanup are unconfirmed.");return;}
            if(development!=null&&development.NamedRawPumpAttached)
            {
                development.RequestNamedRawStopForViewClose();if(worker!=null)worker.TryRequestStop();
                // Production attachment is closed: there is no owner that can
                // safely adopt unresolved writer/XR cleanup after scene teardown.
                UnityEngine.Debug.LogWarning("Raw safety owner destroyed; writer, eye and XR cleanup are unconfirmed.");return;
            }
            try{if(development!=null)development.BeforeXrStop();}catch(Exception){}
            if(worker!=null)worker.Stop();
            try{if(ownsLoader&&manager!=null){manager.StopSubsystems();manager.DeinitializeLoader();}}
            finally{try{if(development!=null)development.AfterXrStop();}catch(Exception){}}
        }
        void OnTrackingOriginUpdated(XRInputSubsystem subsystem)
        {if(alignment!=null){if(alignment.Applied)referenceChanged=true;else{alignment.ResetPending();if(startupGate!=null)startupGate.Reset();}}}
        // Direction feedback only: v3 has already applied the tracked head
        // rotation. The room offset is applied once; the ray starts at the HMD
        // centre, not a measured eye origin. A fixed depth covers the room and
        // is not a surface fixation estimate.
        public static bool TryProjectGazeDirection(Vector3 origin,Quaternion roomRotation,Vec3 trackingDirection,Vector3 cameraForward,out Vector3 point)
        {
            point=Vector3.zero;
            if(!Finite(origin)||!Finite(roomRotation)||!trackingDirection.IsUnit||!Finite(cameraForward))return false;
            float q=roomRotation.x*roomRotation.x+roomRotation.y*roomRotation.y+roomRotation.z*roomRotation.z+roomRotation.w*roomRotation.w;
            if(Mathf.Abs(q-1f)>.002f)return false;
            var direction=roomRotation*new Vector3((float)trackingDirection.X,(float)trackingDirection.Y,(float)trackingDirection.Z);
            if(!Finite(direction)||direction.sqrMagnitude<.99f||direction.sqrMagnitude>1.01f||Vector3.Dot(cameraForward,direction)<=0)return false;
            point=origin+direction.normalized*1.35f;return Finite(point);
        }
        void UpdateGazeMarker(HeadEyeSample sample,ViveProEyeObservation observation,bool usable)
        {
            Vec3? point=null;Vector3 projected;
            if(usable&&gazeMarker!=null&&previewCamera!=null&&cameraOffset!=null&&
                (development==null||(!development.NamedRawPumpAttached&&!development.RecordingEnabled))&&
                TryProjectGazeDirection(previewCamera.transform.position,cameraOffset.rotation,sample.RawEyeDirection,previewCamera.transform.forward,out projected))
                point=new Vec3(projected.x,projected.y,projected.z);
            var state=gazeContinuity.Update(observation,clock.Now,headReady,point);
            // A duplicate may be retained by the shared rehearsal continuity
            // helper, but is never displayed as a new cyan acquisition here.
            if(state!=VivePreviewMarkerState.Fresh||!gazeContinuity.Point.HasValue||!point.HasValue)return;
            var value=gazeContinuity.Point.Value;
            gazeMarker.position=new Vector3((float)value.X,(float)value.Y,(float)value.Z);
            gazeObservedAt=observation.RawPoll.Eye.ObservedLocalSeconds;
            if(GazeStillCurrent())gazeMarker.gameObject.SetActive(true);
        }
        bool GazeStillCurrent()
        {
            if(!running||!headReady||headReadyFrame!=Time.frameCount||stopping||worker==null||source==null||clock==null||
                display==null||!display.running||input==null||!input.running||!OpenXRUtility.IsSessionFocused||referenceChanged||gazeMarker==null||
                (development!=null&&(development.NamedRawPumpAttached||development.RecordingEnabled||development.RecordingBlocked)))return false;
            object run;if(!worker.TryGetRecordingRunIdentity(out run))return false;
            var head=InputDevices.GetDeviceAtXRNode(XRNode.Head);bool tracked;InputTrackingState flags;
            if(!head.isValid||!head.TryGetFeatureValue(CommonUsages.isTracked,out tracked)||!tracked||
                !head.TryGetFeatureValue(CommonUsages.trackingState,out flags)||
                (flags&(InputTrackingState.Position|InputTrackingState.Rotation))!=(InputTrackingState.Position|InputTrackingState.Rotation))return false;
            double now=clock.Now;
            return !Double.IsNaN(gazeObservedAt)&&!Double.IsInfinity(gazeObservedAt)&&now>=gazeObservedAt&&
                now-gazeObservedAt<=VivePreviewMarkerContinuity.MaximumAgeSeconds;
        }
        void HideGazeMarker(){if(gazeMarker!=null)gazeMarker.gameObject.SetActive(false);gazeObservedAt=Double.NaN;}
        void SetStatus(string value){if(status==value)return;status=value;if(view!=null){if(development!=null&&development.PreSessionChoiceActive)development.RenderPreSessionChoice();else if(development!=null&&development.Configured)view.SetViveDevelopmentStatus(value,development.ControlsHint);else view.SetViveUnconfiguredPreview(value);}}
        static bool Finite(float v){return !Single.IsNaN(v)&&!Single.IsInfinity(v);}
        static bool Finite(Vector3 v){return Finite(v.x)&&Finite(v.y)&&Finite(v.z);}
        static bool Finite(Quaternion q){return Finite(q.x)&&Finite(q.y)&&Finite(q.z)&&Finite(q.w);}
    }
}
#endif
