#if STROOP_XR_PREVIEW
using System;
using System.Collections;
using AvatarStroop.Core;
using AvatarStroop.Runtime;
using UnityEngine;
using UnityEngine.XR;
using UnityEngine.XR.Management;
using UnityEngine.XR.OpenXR;

namespace AvatarStroop.Unity
{
    // An explicitly started local hardware rehearsal. It cannot create a study,
    // send network packets, record samples, choose conditions or accept consent.
    public sealed class ViveProEyePreview : MonoBehaviour
    {
        [SerializeField] Camera previewCamera;
        [SerializeField] Transform trackingOrigin, cameraOffset, gazeMarker, avatarHead, leftEye, rightEye;
        [SerializeField] TextMesh statusText;
        XRManagerSettings manager;
        XRDisplaySubsystem display;
        XRInputSubsystem input;
        ViveProEyeWorker worker;
        ViveProEyeHeadEyeSource source;
        MonotonicClock clock;
        QuestPreviewStartAlignment alignment;
        TrackingOriginModeFlags alignedMode;
        bool starting, running, ownsLoader, referenceChanged, headReady, stopping, quitRequested, restartBlocked;
        int alignedFrame, visibleMask=-1;
        long headSequence, usableEyes;
        readonly VivePreviewMarkerContinuity markerContinuity=new VivePreviewMarkerContinuity();
        readonly VivePreviewDiagnostics diagnostics=new VivePreviewDiagnostics();
        GUIStyle desktopLabel,desktopButton;
        Vector2 desktopScroll;
        string status = "STOPPED - no eye capture\nUse Begin live preview on the desktop.\nNo study session or data file is created.";
        string stoppedMessage;
        Quaternion rawRotation;
        Vector3 rawPosition;

        public bool CaptureStarted { get { return starting || running || stopping; } }
        public string Diagnostic { get { return status; } }
        public Camera PreviewCamera { get { return previewCamera; } }
        public Transform GazeMarker { get { return gazeMarker; } }
        public TextMesh StatusText { get { return statusText; } }

        public void Configure(Camera camera, Transform origin, Transform offset, Transform marker,
            Transform head, Transform left, Transform right, TextMesh text)
        {
            if(Application.isPlaying) throw new InvalidOperationException("Author the diagnostic scene outside Play Mode.");
            previewCamera=camera; trackingOrigin=origin; cameraOffset=offset; gazeMarker=marker;
            avatarHead=head; leftEye=left; rightEye=right; statusText=text;
            HideLiveIndicators();
        }

        void Awake()
        {
            Application.runInBackground=true;
            Application.wantsToQuit+=WantsToQuit;
            HideLiveIndicators();
            if(statusText!=null) statusText.text=status;
        }

        void OnGUI()
        {
            // Desktop controls only: no VIVE study response mapping is invented.
            if(desktopLabel==null)
            {
                desktopLabel=new GUIStyle(GUI.skin.label){fontSize=26,wordWrap=true};
                desktopButton=new GUIStyle(GUI.skin.button){fontSize=26,wordWrap=true};
            }
            var area=new Rect(16,16,Mathf.Min(1100,Screen.width-32),Mathf.Min(720,Screen.height-32));
            Color previousColour=GUI.color;
            GUI.color=new Color(.02f,.03f,.04f,1f);
            GUI.DrawTexture(area,Texture2D.whiteTexture);
            GUI.color=previousColour;
            GUILayout.BeginArea(area, GUI.skin.box);
            GUILayout.Label("VIVE PRO EYE - LIVE HARDWARE REHEARSAL",desktopLabel);
            GUILayout.Label("No sample recording, network or study trials.",desktopLabel);
            GUILayout.Label("Consent and calibration remain in HTC's tools.",desktopLabel);
            GUILayout.Label("Direction adapter: HTC-derived ray v3 (unvalidated).",desktopLabel);
            GUILayout.BeginHorizontal();
            GUI.enabled=!starting&&!running&&!stopping&&worker==null&&!quitRequested&&!restartBlocked;
            if(GUILayout.Button("Begin live preview",desktopButton,GUILayout.Height(48))) BeginCapture();
            GUI.enabled=starting||running;
            if(GUILayout.Button("Stop capture",desktopButton,GUILayout.Height(48))) StopCapture();
            GUI.enabled=true;
            if(GUILayout.Button("Exit",desktopButton,GUILayout.Height(48))) RequestQuit();
            GUILayout.EndHorizontal();
            desktopScroll=GUILayout.BeginScrollView(desktopScroll);
            GUILayout.Label(status,desktopLabel);
            if(diagnostics.Observations>0)
            {
                GUILayout.Label(running?"CURRENT CAPTURE TOTALS":"LAST CAPTURE SUMMARY - retained in memory only",desktopLabel);
                GUILayout.Label("Native polls / successful reads: "+diagnostics.NativePolls+" / "+diagnostics.SuccessfulEyeReads,desktopLabel);
                GUILayout.Label("New eye frames: "+diagnostics.NewEyeFrames+" | With ready head: "+diagnostics.NewEyeFramesWithHeadReady,desktopLabel);
                GUILayout.Label("Native direction flag valid: "+diagnostics.CombinedMaskValidFrames+
                    " | Derived direction normalized: "+diagnostics.DirectionNormalizedFrames,desktopLabel);
                GUILayout.Label("Rejected direction - nonfinite: "+diagnostics.DirectionNonfiniteFrames+
                    " | Degenerate: "+diagnostics.DirectionDegenerateFrames+
                    " | Magnitude overflow: "+diagnostics.DirectionMagnitudeOverflowFrames,desktopLabel);
                GUILayout.Label("Usable mapped: "+diagnostics.UsableWithHeadFrames+" | Off board: "+diagnostics.UsableOffBoardFrames+
                    " | Healthy duplicate views: "+diagnostics.HealthyDuplicateViews,desktopLabel);
                GUILayout.Label("Rejected - data absent: "+diagnostics.DataAbsentFrames+" | Direction missing/invalid: "+
                    (diagnostics.CombinedMissingFrames+diagnostics.CombinedInvalidFrames),desktopLabel);
                GUILayout.Label("Rejected - runtime/calibration/device/API: "+
                    (diagnostics.RuntimeRejectedFrames+diagnostics.CalibrationRejectedFrames+diagnostics.DeviceRejectedFrames+diagnostics.NativeRejectedFrames)+
                    " | Head: "+diagnostics.HeadBlockedFrames+" | Mapping: "+diagnostics.MappingBlockedFrames,desktopLabel);
                GUILayout.Label("Views - duplicate: "+diagnostics.DuplicateViews+" | Stale: "+diagnostics.StaleViews+
                    " | Missing/other: "+(diagnostics.MissingViews+diagnostics.OtherDispositionViews),desktopLabel);
                GUILayout.Label("Totals include unworn time. Head-ready is not proof of wear or accuracy.",desktopLabel);
                GUILayout.Label("Derived normalization preserves direction, not proof of tracking quality. Raw values are unchanged.",desktopLabel);
            }
            if(worker!=null&&running)
            {
                var raw=source==null||source.LastObservation==null?null:source.LastObservation.RawPoll;
                var eye=raw==null?null:raw.Eye;
                Vec3 derived;
                string direction=eye==null||eye.Combined==null?"missing":!eye.Combined.DirectionReportedValid||(eye.Combined.ValidityMask&2UL)==0?"not reported valid":
                    ViveProEyeSdkDirection.TryGetUnitDirection(eye.Combined.Direction,out derived)?
                    "reported valid / ray convertible":"reported valid / "+ViveProEyeSdkDirection.ConvertForRay(eye.Combined.Direction,out derived);
                GUILayout.Label("Eye-data presence: "+(eye==null?"unknown":eye.NoUser?"unavailable":"available")+" | Combined direction: "+direction,desktopLabel);
            }
            GUILayout.EndScrollView();GUILayout.EndArea();
        }

        public void BeginCapture()
        {
            if(starting||running||stopping||worker!=null||quitRequested||restartBlocked) return;
            if(!VivePreviewLaunchPlan.IsExpectedChildEnvironment(Environment.GetEnvironmentVariable(VivePreviewLaunchPlan.MarkerVariable),
                Environment.GetEnvironmentVariable(VivePreviewLaunchPlan.RuntimeVariable)))
            {
                SetStatus("SteamVR routing was not prepared for this player.\nClose this window and open the outer AvatarStroopViveEyePreview.exe.\nThe launcher selects SteamVR only for this preview process.");
                return;
            }
            if(previewCamera==null||trackingOrigin==null||cameraOffset==null||gazeMarker==null||
                avatarHead==null||leftEye==null||rightEye==null||statusText==null)
            {SetStatus("Preview scene references are incomplete. Capture has not started.");return;}
            starting=true;
            visibleMask=previewCamera.cullingMask;
            previewCamera.cullingMask=0;
            SetStatus("STARTING - waiting for the configured OpenXR runtime.\nNo calibration or consent action will be performed.");
            StartCoroutine(InitializeCapture());
        }

        IEnumerator InitializeCapture()
        {
            manager=XRGeneralSettings.Instance==null?null:XRGeneralSettings.Instance.Manager;
            if(manager==null||manager.activeLoader!=null)
            {Fail("Expected an uninitialized project OpenXR loader. Close this preview and inspect the runtime.");yield break;}
            ownsLoader=true;
            IEnumerator initialize;
            try {initialize=manager.InitializeLoader();}
            catch(Exception error) {Fail("OpenXR startup failed: "+error.Message);yield break;}
            while(true)
            {
                bool more;object current=null;
                try {more=initialize.MoveNext();if(more)current=initialize.Current;}
                catch(Exception error) {Fail("OpenXR startup failed: "+error.Message);yield break;}
                if(!more)break;
                yield return current;
            }
            try
            {
                if(manager.activeLoader==null)throw new InvalidOperationException("OpenXR could not obtain the headset. Reported runtime: "+RuntimeDescription()+
                    ". Use the outer VIVE launcher and check SteamVR headset readiness. A running SteamVR window alone does not select its OpenXR runtime.");
                string runtimeName=OpenXRRuntime.name;
                if(String.IsNullOrEmpty(runtimeName)||!runtimeName.StartsWith("SteamVR",StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Unexpected OpenXR runtime: "+RuntimeDescription()+
                        ". Eye capture was not started. Close this player and reopen through the outer VIVE launcher; no system runtime setting was changed.");
                display=manager.activeLoader.GetLoadedSubsystem<XRDisplaySubsystem>();
                input=manager.activeLoader.GetLoadedSubsystem<XRInputSubsystem>();
                if(display==null||input==null)throw new InvalidOperationException("OpenXR display/input subsystem is unavailable.");
                manager.StartSubsystems();
                alignment=new QuestPreviewStartAlignment();referenceChanged=false;headReady=false;alignedFrame=-1;
                trackingOrigin.SetPositionAndRotation(Vector3.zero,Quaternion.identity);
                cameraOffset.localPosition=new Vector3(0,1.6f,0);cameraOffset.localRotation=Quaternion.identity;
                previewCamera.transform.SetLocalPositionAndRotation(Vector3.zero,Quaternion.identity);
                string epoch=Guid.NewGuid().ToString("N");
                clock=new MonotonicClock("vive-pro-eye-preview-poll-"+epoch);
                headSequence=usableEyes=0;
                markerContinuity.Reset();diagnostics.Reset();
                // Only the worker constructs/calls/releases the blocking HTC client.
                // ReadHead remains on Unity's main thread when the source polls.
                worker=new ViveProEyeWorker(clock,ReadHead,()=>new SrAnipalViveProEyeProvider(clock,()=>null));
                if(!worker.Start())throw new InvalidOperationException(worker.Diagnostic);
                source=new ViveProEyeHeadEyeSource("vive-pro-eye-local-rehearsal-"+epoch,0,0,"vive-pro-eye/operator/preview-"+epoch,
                    clock,worker,new ViveProEyeRehearsalCoordinates());
                starting=false;running=true;
                SetStatus("STARTING HTC eye client on its worker.\nThe head view remains responsive; no eye data is assumed valid.");
            }
            catch(Exception error) {Fail("Capture unavailable: "+error.Message);}
        }

        ViveHeadFrame ReadHead()
        {
            var device=InputDevices.GetDeviceAtXRNode(XRNode.Head);
            bool tracked=false;InputTrackingState flags=InputTrackingState.None;
            bool gotPosition=device.TryGetFeatureValue(CommonUsages.devicePosition,out rawPosition);
            bool gotRotation=device.TryGetFeatureValue(CommonUsages.deviceRotation,out rawRotation);
            device.TryGetFeatureValue(CommonUsages.isTracked,out tracked);
            device.TryGetFeatureValue(CommonUsages.trackingState,out flags);
            bool rotationValid=gotRotation&&Finite(rawRotation)&&
                Mathf.Abs(rawRotation.x*rawRotation.x+rawRotation.y*rawRotation.y+rawRotation.z*rawRotation.z+rawRotation.w*rawRotation.w-1)<.000001f;
            var mode=input!=null&&input.running?input.GetTrackingOriginMode():TrackingOriginModeFlags.Unknown;
            bool supported=mode==TrackingOriginModeFlags.Device||mode==TrackingOriginModeFlags.Floor;
            bool pose=display!=null&&display.running&&device.isValid&&tracked&&gotPosition&&Finite(rawPosition)&&rotationValid&&
                (flags&(InputTrackingState.Position|InputTrackingState.Rotation))==(InputTrackingState.Position|InputTrackingState.Rotation)&&OpenXRUtility.IsSessionFocused;
            if(alignment.Applied&&supported&&mode!=alignedMode)referenceChanged=true;
            if(!alignment.Applied&&supported)
            {
                alignment.PrepareReferenceHeight(true,mode==TrackingOriginModeFlags.Floor,cameraOffset,1.6f);
                if(alignment.TryApply(pose,trackingOrigin,cameraOffset,rawPosition,rawRotation,new Vector3(0,1.6f,0)))
                {alignedMode=mode;alignedFrame=Time.frameCount;}
            }
            headReady=pose&&alignment.Applied&&!referenceChanged&&mode==alignedMode&&Time.frameCount>alignedFrame;
            previewCamera.cullingMask=headReady?visibleMask:0;
            if(headReady)previewCamera.transform.SetLocalPositionAndRotation(rawPosition,rawRotation);
            // The source consumes tracking-local rotation, before application-space
            // room alignment. Camera placement must never silently alter raw data.
            return new ViveHeadFrame(headSequence++,clock.Now,device.isValid,headReady,
                new Rotation(rawRotation.x,rawRotation.y,rawRotation.z,rawRotation.w));
        }

        void Update()
        {
            FinishPendingStop();
            if(quitRequested&&!stopping&&!starting&&!running&&worker==null){Application.Quit();return;}
            if(!running||source==null)return;
            try
            {
                if(!worker.Starting&&!worker.Started&&!worker.Stopping)
                {Fail("HTC capture is not running: "+worker.Diagnostic);return;}
                HeadEyeSample sample;
                bool emitted=source.TryRead(out sample);
                var observation=source.LastObservation;
                bool head=emitted&&sample!=null&&sample.HeadValid&&observation.HeadUsable;
                bool eye=emitted&&sample!=null&&sample.EyeValid&&observation.EyeUsable&&headReady;
                avatarHead.gameObject.SetActive(head);
                leftEye.gameObject.SetActive(eye);rightEye.gameObject.SetActive(eye);
                Vec3? projectedPoint=null;
                if(head)avatarHead.localRotation=ToQuaternion(sample.RawHeadRotation);
                if(eye)
                {
                    usableEyes++;
                    Vector3 trackingDirection=ToVector(sample.RawEyeDirection);
                    Vector3 eyeLocal=Quaternion.Inverse(avatarHead.localRotation)*trackingDirection;
                    var up=Mathf.Abs(Vector3.Dot(eyeLocal,Vector3.up))>.999f?Vector3.forward:Vector3.up;
                    leftEye.localRotation=rightEye.localRotation=Quaternion.LookRotation(eyeLocal,up);
                    // Direction-only engineering visualization from the HMD centre;
                    // not a measured eye origin or fixation/accuracy result.
                    Vector3 worldDirection=cameraOffset.rotation*trackingDirection;
                    var ray=new Ray(previewCamera.transform.position,worldDirection);
                    Vector3 point;
                    if(TryProjectDiagnosticRay(ray,out point))
                        projectedPoint=new Vec3(point.x,point.y,point.z);
                }
                diagnostics.Observe(observation,headReady,projectedPoint.HasValue);
                diagnostics.ObserveNative(worker.CaptureDiagnostics);
                var markerState=markerContinuity.Update(observation,clock.Now,headReady,projectedPoint);
                ApplyMarkerPresentation(markerState,markerContinuity.Point);
                string reason=referenceChanged?"Reference space changed - stop and explicitly restart this preview.":
                    !headReady?"Waiting for focused, tracked HMD pose; no usable mapped eye output.":
                    observation.EyeFailureReason??observation.FailureReason;
                var raw=observation.RawPoll;
                if(raw!=null&&raw.Calibration==ViveEyeCalibrationState.Required)
                    reason="Calibration required by HTC. Stop capture and use HTC's calibration interface.";
                string runtime=raw==null?"No provider observation":"Runtime: "+raw.Runtime+" | Calibration: "+raw.Calibration+
                    " | Code: "+(raw.NativeResultCode.HasValue?raw.NativeResultCode.Value.ToString():"unknown");
                string captureState=worker.Started?"LIVE CLIENT - no sample files or study trials":
                    worker.Starting?"STARTING HTC client - eye data pending":"HTC CLIENT NOT READY - awaiting cleanup";
                if(!worker.Started&&!worker.Starting)reason=worker.Diagnostic;
                SetStatus(captureState+
                    "\nHead: "+(head?"usable":"unavailable")+" | Eye: "+(eye?"usable":"unavailable")+
                    "\n"+runtime+"\nFrame: "+observation.EyeDisposition+
                    " | Usable eye observations: "+usableEyes+
                    "\nMarker: "+(markerState==VivePreviewMarkerState.Fresh?"new valid ray (cyan)":
                        markerState==VivePreviewMarkerState.Retained?"previous ray (amber), "+Math.Round(markerContinuity.AgeSeconds*1000)+" ms old":"hidden")+
                    "\n"+(String.IsNullOrEmpty(reason)?"Unvalidated tracking-to-avatar mapping; no accuracy claim.":reason));
            }
            catch(Exception error) {Fail("Capture stopped after an error: "+error.Message);}
        }

        public static bool TryProjectDiagnosticRay(Ray ray,out Vector3 markerPoint)
        {
            markerPoint=Vector3.zero;
            float distance;
            if(!new Plane(Vector3.back,new Vector3(0,0,2.46f)).Raycast(ray,out distance))return false;
            Vector3 point=ray.GetPoint(distance);
            if(!(Mathf.Abs(point.x)<1.24f&&point.y>.35f&&point.y<2.55f))return false;
            // Keep the board-plane projection, but draw the indicator in front of
            // the opaque target centres and status panel instead of inside them.
            point.z=2.43f;markerPoint=point;return true;
        }

        // Presentation only. An amber marker is the last accepted WORLD point,
        // never a new acquisition or a reprojection of an old eye with a new head.
        public void ApplyMarkerPresentation(VivePreviewMarkerState state,Vec3? point)
        {
            bool visible=state!=VivePreviewMarkerState.Hidden&&point.HasValue&&point.Value.IsFinite;
            gazeMarker.gameObject.SetActive(visible);
            if(!visible)return;
            gazeMarker.position=ToVector(point.Value);
            var renderer=gazeMarker.GetComponent<Renderer>();
            var properties=new MaterialPropertyBlock();renderer.GetPropertyBlock(properties);
            properties.SetColor("_Color",state==VivePreviewMarkerState.Fresh?Color.cyan:new Color(1f,.55f,.05f));
            renderer.SetPropertyBlock(properties);
        }

        public void StopCapture()
        {
            if(stopping)return;
            ReleaseResources("STOPPED - no eye capture\nThis preview does not save sample files or send network packets.\nBegin explicitly to start a new hardware rehearsal.");
        }
        void ReleaseResources(string message)
        {
            StopAllCoroutines();
            if(worker!=null)diagnostics.ObserveNative(worker.StopAndCaptureDiagnostics());
            running=starting=headReady=false;source=null;
            stoppedMessage=message;
            // Default Stop only signals; no Join or native API may stall the HMD/UI.
            try {if(ownsLoader&&manager!=null){manager.StopSubsystems();manager.DeinitializeLoader();}}
            finally
            {
                ownsLoader=false;display=null;input=null;manager=null;
                if(previewCamera!=null)previewCamera.cullingMask=visibleMask;
                HideLiveIndicators();
                stopping=worker!=null;
                if(stopping)SetStatus("STOPPING - waiting for the HTC client to finish safely.\nBegin is disabled until the previous worker exits.\n"+message);
                else SetStatus(message);
            }
            FinishPendingStop();
        }
        void FinishPendingStop()
        {
            if(stopping&&worker!=null&&worker.HasExited)
            {
                restartBlocked=!worker.CanStart;
                string completedDiagnostic=worker.Diagnostic;
                worker.Dispose();worker=null;stopping=false;
                SetStatus(restartBlocked?"CAPTURE DISABLED - native cleanup is not confirmed.\n"+completedDiagnostic+
                    "\nClose this preview and inspect the runtime before a new attempt.":stoppedMessage??"STOPPED - no eye capture");
            }
        }
        void RequestQuit() {quitRequested=true;StopCapture();FinishPendingStop();}
        bool WantsToQuit()
        {
            if(worker==null&&!starting&&!running&&!stopping)return true;
            RequestQuit();return false;
        }
        void Fail(string message) {ReleaseResources(message);}
        static string RuntimeDescription()
        {
            try
            {
                string name=OpenXRRuntime.name,version=OpenXRRuntime.version;
                return String.IsNullOrEmpty(name)?"not reported (see the OpenXR initialization log)":name+(String.IsNullOrEmpty(version)?"":" "+version);
            }
            catch(Exception){return "not available (see the OpenXR initialization log)";}
        }
        void SetStatus(string value)
        {
            status=value;
            if(statusText!=null)
            {
                statusText.text=Wrap(value,62);
                FitText(statusText,2.2f,.54f,.012f);
            }
        }
        // TextMesh characterSize is scaled by fontSize. Measure the resulting
        // geometry rather than treating characterSize as a world-space height.
        public static void FitText(TextMesh text,float maximumWidth,float maximumHeight,float maximumCharacterSize)
        {
            if(text==null||text.font==null||maximumWidth<=0||maximumHeight<=0)throw new ArgumentException("Text and positive fitting bounds are required.");
            text.font.RequestCharactersInTexture(text.text,text.fontSize,text.fontStyle);
            text.characterSize=maximumCharacterSize;
            var renderer=text.GetComponent<Renderer>();
            var size=renderer.bounds.size;
            if(!Finite(size))throw new InvalidOperationException("Text geometry has invalid bounds.");
            float scale=Mathf.Min(1,maximumWidth/Mathf.Max(size.x,.000001f),maximumHeight/Mathf.Max(size.y,.000001f));
            if(scale<1)text.characterSize*=scale*.98f;
        }
        void HideLiveIndicators()
        {
            markerContinuity.Reset();
            if(gazeMarker!=null)gazeMarker.gameObject.SetActive(false);
            if(avatarHead!=null)avatarHead.gameObject.SetActive(false);
        }
        void OnDestroy()
        {
            Application.wantsToQuit-=WantsToQuit;
            // Forced object destruction cannot join a blocked native call. Signal
            // its worker; that thread still owns and disposes its native client.
            if(worker!=null)worker.Stop();
            if(ownsLoader&&manager!=null){manager.StopSubsystems();manager.DeinitializeLoader();}
        }
        static bool Finite(float v) {return !Single.IsNaN(v)&&!Single.IsInfinity(v);}
        static bool Finite(Vector3 v) {return Finite(v.x)&&Finite(v.y)&&Finite(v.z);}
        static bool Finite(Quaternion q) {return Finite(q.x)&&Finite(q.y)&&Finite(q.z)&&Finite(q.w);}
        static Vector3 ToVector(Vec3 v) {return new Vector3((float)v.X,(float)v.Y,(float)v.Z);}
        static Quaternion ToQuaternion(Rotation q) {return new Quaternion((float)q.X,(float)q.Y,(float)q.Z,(float)q.W);}
        static string Wrap(string text,int width)
        {
            var result=new System.Text.StringBuilder();
            foreach(string line in text.Split('\n'))
            {
                int column=0;
                foreach(string word in line.Split(' '))
                {
                    if(column>0&&column+word.Length+1>width){result.Append('\n');column=0;}
                    if(column>0){result.Append(' ');column++;}
                    result.Append(word);column+=word.Length;
                }
                result.Append('\n');
            }
            return result.ToString().TrimEnd();
        }
    }
}
#endif
