#if STROOP_XR_PREVIEW
using System;
using System.Collections;
using System.IO;
using System.Threading.Tasks;
using AvatarStroop.Core;
using AvatarStroop.Runtime;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.XR;
using UnityEngine.XR.Management;
using UnityEngine.XR.OpenXR;

namespace AvatarStroop.Unity
{
    // Owns a separate, explicitly started engineering recording. No study owner,
    // trial admission, network, or study journal is constructed in this scene.
    [DefaultExecutionOrder(100)]
    public sealed class ViveGazeValidationController : MonoBehaviour
    {
        public const string SceneName="ViveGazeValidation";
        const double TargetSeconds=3,CountdownSeconds=3,MaximumRunSeconds=600;
        static readonly string[] Passes={"stable_1","stable_2","gentle_turn_1","gentle_turn_2"};
        [SerializeField] ViveGazeValidationView presentation;
        XRManagerSettings manager;
        XRDisplaySubsystem display;
        XRInputSubsystem input;
        MonotonicClock clock;
        VivePreviewStartAlignment alignment;
        ViveGazeValidationCapture capture;
        ViveGazeValidationSession session;
        Task saveTask;
        TrackingOriginModeFlags alignedMode;
        InputDevice alignedDevice;
        uint presenceEpoch;
        bool hasPresenceEpoch,ownsLoader,starting,running,headReady,referenceChanged;
        bool recording,stopping,finished,normalCompletion,quitRequested,roundPause;
        bool initialEyeSeen;
        int alignedFrame=-1,visibleMask,passIndex,targetIndex;
        long unretainedSamples;
        double startAt,countdownAt=Double.NaN,targetRequestedAt=Double.NaN,headLostAt=Double.NaN,stopAt;
        double lastAcquisitionAt;
        double? targetOnset;
        string folder,finishReason,status="Calibrate in HTC's interface, then start the headset view.";
        GUIStyle label,button;
        Vector2 scroll;

        public void Configure(ViveGazeValidationView view)
        {
            if(Application.isPlaying||view==null)throw new InvalidOperationException("Author a validation view outside Play Mode.");
            presentation=view;
        }
        void Awake()
        {
            Application.runInBackground=true;
            Application.wantsToQuit+=WantsToQuit;
            clock=new MonotonicClock("vive-gaze-validation-"+Guid.NewGuid().ToString("N"));
            if(presentation==null){status="Validation scene references are missing.";return;}
            presentation.Initialize(clock);
            presentation.TargetFirstRendered+=OnTargetRendered;
            visibleMask=presentation.PresentationCamera.cullingMask;
            presentation.PresentationCamera.cullingMask=0;
        }
        void OnGUI()
        {
            using(var desktop=ViveOwnerDesktopLayout.Begin())
            {
                if(label==null)
                {
                    label=new GUIStyle(GUI.skin.label){fontSize=20,wordWrap=true};
                    button=new GUIStyle(GUI.skin.button){fontSize=20,wordWrap=true};
                }
                var colour=GUI.color;GUI.color=new Color(.055f,.07f,.09f,1);
                GUI.DrawTexture(new Rect(0,0,desktop.Width,desktop.Height),Texture2D.whiteTexture);GUI.color=colour;
                GUILayout.BeginArea(new Rect(20,20,Mathf.Max(100,desktop.Width-40),Mathf.Max(100,desktop.Height-40)));
                scroll=GUILayout.BeginScrollView(scroll);
                GUILayout.Label("EYE TRACKING | Fixed-target validation",label);
                GUILayout.Label("Look at each small target. Four passes: two with a steady head, then two with gentle head turns. About 2 minutes plus a break.",label);
                GUILayout.Label(status,label);GUILayout.Space(12);
                if(!starting&&!running&&!finished&&!stopping)
                {
                    if(GUILayout.Button("Start headset view",button,GUILayout.Height(48)))BeginView();
                    if(GUILayout.Button("Back to task setup",button,GUILayout.Height(44)))SceneManager.LoadScene("ViveClassroomPreview");
                }
                if(running&&!recording&&!stopping&&!finished)
                {
                    GUILayout.Label("Start saves local gaze rays, validity/calibration reports, head poses and target timing/positions in a separate validation folder. No study responses are collected.",label);
                    GUI.enabled=headReady;
                    if(GUILayout.Button("Start validation and record",button,GUILayout.Height(48)))BeginRecording();
                    GUI.enabled=true;
                }
                if(recording)
                {
                    GUILayout.Label("Unique samples retained: "+session.SampleCount+" | Pass "+(passIndex+1)+" / 4 | Target "+(targetIndex+1)+" / "+presentation.TargetCount,label);
                    var latest=capture.Latest;
                    if(latest!=null)GUILayout.Label("HTC: "+latest.Runtime+" | Calibration report: "+latest.Calibration,label);
                    if(roundPause)
                    {
                        GUI.enabled=headReady;
                        if(GUILayout.Button("Begin gentle head-turn passes",button,GUILayout.Height(48)))ContinueRound();
                        GUI.enabled=true;
                    }
                    if(GUILayout.Button("Stop and save incomplete run",button,GUILayout.Height(48)))Finish(false,"operator_stopped");
                }
                if(finished&&saveTask!=null&&saveTask.Status==TaskStatus.RanToCompletion)
                {
                    if(GUILayout.Button("Open validation summary",button,GUILayout.Height(48)))OpenOutput("summary.csv");
                    if(GUILayout.Button("Open sample CSV",button,GUILayout.Height(48)))OpenOutput("samples.csv");
                    GUILayout.Label("Saved in: "+folder,label);
                }
                if(!stopping&&(saveTask==null||saveTask.IsCompleted))
                    if(GUILayout.Button("Close application",button,GUILayout.Height(44)))RequestQuit();
                GUILayout.EndScrollView();GUILayout.EndArea();
            }
        }
        public void BeginView()
        {
            if(starting||running||stopping||finished||presentation==null)return;
            if(!ViveControlledRecordingBuildPolicy.Available)
            {status="This package does not include local validation recording.";return;}
            if(!VivePreviewLaunchPlan.IsExpectedChildEnvironment(Environment.GetEnvironmentVariable(VivePreviewLaunchPlan.MarkerVariable),
                Environment.GetEnvironmentVariable(VivePreviewLaunchPlan.RuntimeVariable)))
            {status="Open the outer AvatarStroopViveEyePreview.exe launcher to use SteamVR.";return;}
            presentation.Initialize(clock);
            starting=true;status="Starting SteamVR. Put on the headset and hold your head naturally steady.";
            StartCoroutine(InitializeView());
        }
        IEnumerator InitializeView()
        {
            manager=XRGeneralSettings.Instance==null?null:XRGeneralSettings.Instance.Manager;
            if(manager==null||manager.activeLoader!=null){ViewFailed("Expected an uninitialized OpenXR loader.");yield break;}
            ownsLoader=true;
            IEnumerator operation;
            try{operation=manager.InitializeLoader();}catch(Exception e){ViewFailed(e.Message);yield break;}
            while(true)
            {
                bool more;object current=null;
                try{more=operation.MoveNext();if(more)current=operation.Current;}
                catch(Exception e){ViewFailed(e.Message);yield break;}
                if(!more)break;yield return current;
            }
            try
            {
                if(manager.activeLoader==null||String.IsNullOrEmpty(OpenXRRuntime.name)||!OpenXRRuntime.name.StartsWith("SteamVR",StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("The SteamVR OpenXR runtime is unavailable.");
                display=manager.activeLoader.GetLoadedSubsystem<XRDisplaySubsystem>();
                input=manager.activeLoader.GetLoadedSubsystem<XRInputSubsystem>();
                if(display==null||input==null)throw new InvalidOperationException("Headset display/input is unavailable.");
                manager.StartSubsystems();
                alignment=new VivePreviewStartAlignment();
                input.trackingOriginUpdated+=ReferenceChanged;
                presentation.TrackingOrigin.SetPositionAndRotation(Vector3.zero,Quaternion.identity);
                presentation.CameraOffset.SetLocalPositionAndRotation(Vector3.zero,Quaternion.identity);
                presentation.PresentationCamera.transform.SetLocalPositionAndRotation(Vector3.zero,Quaternion.identity);
                starting=false;running=true;
                presentation.SetInstruction("Hold your head naturally steady.\nThe operator will start the targets.");
            }
            catch(Exception e){ViewFailed(e.Message);}
        }
        void ReferenceChanged(XRInputSubsystem subsystem){if(alignment!=null&&alignment.Applied)referenceChanged=true;}
        void UpdateHead()
        {
            var device=InputDevices.GetDeviceAtXRNode(XRNode.Head);
            bool tracked;InputTrackingState flags;Vector3 position;Quaternion rotation;
            bool pose=device.isValid&&device.TryGetFeatureValue(CommonUsages.isTracked,out tracked)&&tracked&&
                device.TryGetFeatureValue(CommonUsages.trackingState,out flags)&&
                (flags&(InputTrackingState.Position|InputTrackingState.Rotation))==(InputTrackingState.Position|InputTrackingState.Rotation);
            pose=device.TryGetFeatureValue(CommonUsages.devicePosition,out position)&&Finite(position)&&pose;
            pose=device.TryGetFeatureValue(CommonUsages.deviceRotation,out rotation)&&Finite(rotation)&&pose;
            var mode=input!=null&&input.running?input.GetTrackingOriginMode():TrackingOriginModeFlags.Unknown;
            bool supported=mode==TrackingOriginModeFlags.Device||mode==TrackingOriginModeFlags.Floor;
            var presence=OpenXRSettings.Instance==null?null:OpenXRSettings.Instance.GetFeature<ViveVerifiedPresenceFeature>();
            bool present=presence==null||!presence.enabled||!presence.CapabilityVerified||OpenXRUtility.IsUserPresent;
            if(presence!=null)
            {
                if(hasPresenceEpoch&&presence.SessionEpoch!=presenceEpoch&&alignment.Applied)referenceChanged=true;
                presenceEpoch=presence.SessionEpoch;hasPresenceEpoch=true;
            }
            pose=pose&&supported&&present&&OpenXRUtility.IsSessionFocused&&display!=null&&display.running&&input!=null&&input.running;
            if(alignment.Applied&&(mode!=alignedMode||!device.Equals(alignedDevice)))referenceChanged=true;
            if(!alignment.Applied&&alignment.TryApply(pose,mode,Time.frameCount,Time.realtimeSinceStartupAsDouble,
                presentation.TrackingOrigin,presentation.CameraOffset,position,rotation,
                new Vector3(WorldSpaceExperimentView.BoardPosition.x,WorldSpaceExperimentView.TaskDisplayPosition.y,WorldSpaceExperimentView.CenteredParticipantReference.z)))
            {alignedMode=mode;alignedDevice=device;alignedFrame=Time.frameCount;}
            headReady=pose&&alignment.Applied&&!referenceChanged&&Time.frameCount>alignedFrame;
            presentation.PresentationCamera.cullingMask=headReady?visibleMask:0;
            if(headReady)presentation.PresentationCamera.transform.SetLocalPositionAndRotation(position,rotation);
        }
        void BeginRecording()
        {
            if(!running||!headReady||recording||stopping||finished)return;
            try
            {
                string id="validation-"+DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ")+"-"+Guid.NewGuid().ToString("N");
                string package=Path.GetFullPath(Path.Combine(Application.dataPath,"..",".."));
                folder=Path.Combine(package,"LocalGazeValidationData",id);
                Directory.CreateDirectory(folder);
                session=new ViveGazeValidationSession(id);
                capture=new ViveGazeValidationCapture(clock);
                if(!capture.Start())throw new InvalidOperationException("Eye capture could not start: "+capture.Diagnostic);
                recording=true;startAt=clock.Now;passIndex=targetIndex=0;countdownAt=Double.NaN;
                status="Starting eye capture. Targets will begin after the countdown.";
                presentation.SetInstruction("Eye validation is recording.\nWaiting for the eye tracker...");
                Debug.Log("VIVE_GAZE_VALIDATION_STARTED: "+id+"; softwareClock="+clock.ClockId);
            }
            catch(Exception e)
            {
                if(capture!=null){recording=true;Finish(false,"start_failed: "+e.Message);}
                else{status="Recording could not start: "+e.Message;finished=true;}
            }
        }
        void OnTargetRendered(ViveGazeValidationTarget target,double onset,int frame)
        {
            if(!recording||stopping||roundPause||targetOnset.HasValue)return;
            try{session.BeginTarget(target,onset,frame);targetOnset=onset;}
            catch(Exception e){Finish(false,"target_event_failed: "+e.Message);}
        }
        void Update()
        {
            try
            {
                if(running)UpdateHead();
                if(capture!=null&&session!=null&&!finished)DrainSamples();
                if(stopping)FinishStop();
                else if(recording)AdvanceSequence();
                else if(running&&!finished)status=headReady?"Headset ready. Complete HTC calibration before starting the recorded targets.":
                    referenceChanged?"Tracking reference changed. Close and reopen this validation.":"Waiting for focused headset tracking and a steady initial pose.";
                if(saveTask!=null&&saveTask.IsCompleted)
                {
                    if(saveTask.IsFaulted)status="Saving failed: "+saveTask.Exception.GetBaseException().Message+". Files must not be treated as a completed output.";
                    else status=(normalCompletion?"Target sequence completed and files saved.":"Incomplete run saved: "+finishReason)+
                        " Review the summary; no automatic accuracy pass is assigned.";
                }
                if(quitRequested&&!starting&&!stopping&&(saveTask==null||saveTask.IsCompleted))Application.Quit();
            }
            catch(Exception e){if(recording||stopping)Finish(false,"validation_error: "+e.Message);else status="Validation error: "+e.Message;}
        }
        void DrainSamples()
        {
            if(capture==null)return;
            ViveRawEyeAcquisition sample;int budget=1024;
            while(budget-->0&&capture.TryDequeue(out sample))
            {
                initialEyeSeen=true;lastAcquisitionAt=clock.Now;
                try{session.Observe(sample,presentation.Capture(sample.Poll.Eye,headReady));}
                catch(Exception e){unretainedSamples++;Finish(false,"sample_processing_failed: "+e.Message);}
            }
        }
        void AdvanceSequence()
        {
            double now=clock.Now;
            if(!String.IsNullOrEmpty(capture.Fault)){Finish(false,"eye_capture_fault: "+capture.Fault);return;}
            if(capture.HasExited){Finish(false,"eye_capture_exited");return;}
            if(now-startAt>MaximumRunSeconds){Finish(false,"time_budget_exceeded");return;}
            if(referenceChanged){Finish(false,"head_reference_changed");return;}
            if(!headReady)
            {
                if(Double.IsNaN(headLostAt))headLostAt=now;
                if(targetOnset.HasValue&&now-headLostAt>.5){Finish(false,"head_tracking_or_focus_lost");return;}
                if(!targetOnset.HasValue)countdownAt=Double.NaN;
                status="Waiting for focused headset tracking.";return;
            }
            headLostAt=Double.NaN;
            if(roundPause)return;
            if(!initialEyeSeen)
            {if(now-startAt>20)Finish(false,"eye_start_timeout");return;}
            if(now-lastAcquisitionAt>5){Finish(false,"eye_acquisition_stalled");return;}
            if(Double.IsNaN(countdownAt))countdownAt=now;
            if(now-countdownAt<CountdownSeconds)
            {
                string instruction=passIndex<2?"Keep your head naturally steady.":"Turn your head gently while watching each target.";
                presentation.SetInstruction(instruction+"\nTargets begin in "+Math.Ceiling(CountdownSeconds-(now-countdownAt))+"...");return;
            }
            if(Double.IsNaN(targetRequestedAt))
            {
                targetRequestedAt=now;presentation.ShowTarget(targetIndex,Passes[passIndex]);
                status="Follow target "+(targetIndex+1)+" of "+presentation.TargetCount+": "+presentation.GetTargetLabel(targetIndex)+". "+
                    (passIndex<2?"Head naturally steady.":"Gentle head turns.");return;
            }
            if(!targetOnset.HasValue)
            {if(now-targetRequestedAt>5)Finish(false,"target_not_rendered");return;}
            if(now-targetOnset.Value<TargetSeconds)return;
            session.EndTarget(now);presentation.ClearTarget();targetOnset=null;targetRequestedAt=Double.NaN;
            targetIndex++;
            if(targetIndex<presentation.TargetCount)return;
            targetIndex=0;passIndex++;
            if(passIndex==Passes.Length){Finish(true,"all_targets_completed");return;}
            countdownAt=Double.NaN;
            if(passIndex==2)
            {
                roundPause=true;status="Steady-head passes finished. Next: gentle head turns while keeping your eyes on each target.";
                presentation.SetInstruction("Take a short break.\nNext: turn your head gently while looking at each target.\nThe operator will continue when you are ready.");
            }
        }
        void ContinueRound(){if(!roundPause||!headReady)return;roundPause=false;countdownAt=Double.NaN;}
        void Finish(bool completed,string reason)
        {
            if(finished)return;
            if(stopping){if(!completed){normalCompletion=false;finishReason=reason;}return;}
            normalCompletion=completed;finishReason=reason;recording=false;stopping=true;stopAt=clock.Now;
            if(targetOnset.HasValue){session.EndTarget(clock.Now);targetOnset=null;}
            presentation.ClearTarget();presentation.SetInstruction("Targets finished.\nSaving the validation... You may rest.");
            status="Stopping the eye tracker and saving...";
            if(capture!=null)capture.RequestStop();
        }
        void FinishStop()
        {
            if(capture!=null&&!capture.HasExited)
            {if(clock.Now-stopAt>15)status="Waiting for HTC eye-client shutdown. The interface remains responsive; output is not yet finalized.";return;}
            DrainSamples();
            if(capture!=null&&capture.PendingCount!=0)return;
            if(capture==null||!capture.ProviderDrained||!capture.CleanupConfirmed||!String.IsNullOrEmpty(capture.Fault))
            {normalCompletion=false;finishReason="capture_cleanup_incomplete: "+(capture==null?"capture unavailable":capture.Fault??capture.Diagnostic);}
            session.SetCaptureOutcome(capture==null?"capture_unavailable":
                "provider_drained="+capture.ProviderDrained+"; cleanup_confirmed="+capture.CleanupConfirmed+
                "; acquired="+capture.AcquisitionCount+"; dequeued="+capture.DequeuedCount+"; duplicates="+capture.DuplicatePolls+
                "; native_gaps="+capture.NativeSequenceGapCount+"; discontinuities="+capture.NativeSequenceDiscontinuities+
                "; rejected="+capture.RejectedAcquisitions+"; maximum_pending="+capture.MaximumPendingCount+
                "; unretained="+unretainedSamples+"; fault="+(capture.Fault??"none")+
                "; high_resolution_poll_delay="+capture.UsesHighResolutionPollDelay+"; native_diagnostic="+capture.LastNativeDiagnostic+
                "; software_clock="+clock.ClockId+"; geometry="+ViveGazeValidationView.SceneGeometryVersion);
            session.Finish(clock.Now,normalCompletion,finishReason);
            capture?.Dispose();capture=null;stopping=false;finished=true;
            StopView();
            var saved=session;string destination=folder;
            saveTask=Task.Run(()=>saved.Save(destination));
            Debug.Log("VIVE_GAZE_VALIDATION_ENDED: "+finishReason+"; samples="+session.SampleCount);
        }
        void OpenOutput(string filename)
        {
            try{System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Path.Combine(folder,filename)){UseShellExecute=true});}
            catch(Exception e){status="Could not open the saved file: "+e.Message;}
        }
        void StopView()
        {
            running=starting=headReady=false;
            if(presentation!=null)presentation.PresentationCamera.cullingMask=0;
            if(input!=null)input.trackingOriginUpdated-=ReferenceChanged;
            try{if(ownsLoader&&manager!=null){manager.StopSubsystems();manager.DeinitializeLoader();}}
            catch(Exception e){status="OpenXR cleanup: "+e.Message;}
            finally{ownsLoader=false;manager=null;display=null;input=null;}
        }
        void ViewFailed(string message){StopView();finished=true;status="Headset view could not start: "+message;}
        void RequestQuit()
        {
            quitRequested=true;
            if(recording||stopping)Finish(false,"application_closed");
            else if(!starting)StopView();
        }
        bool WantsToQuit()
        {
            if(!starting&&!recording&&!stopping&&(saveTask==null||saveTask.IsCompleted)){StopView();return true;}
            RequestQuit();return false;
        }
        void OnDestroy()
        {
            Application.wantsToQuit-=WantsToQuit;
            if(presentation!=null)presentation.TargetFirstRendered-=OnTargetRendered;
            if(capture!=null)capture.RequestStop();
            StopView();
        }
        static bool Finite(Vector3 v){return F(v.x)&&F(v.y)&&F(v.z);}
        static bool Finite(Quaternion v){return F(v.x)&&F(v.y)&&F(v.z)&&F(v.w)&&Mathf.Abs(v.x*v.x+v.y*v.y+v.z*v.z+v.w*v.w-1)<.00001f;}
        static bool F(float v){return !Single.IsNaN(v)&&!Single.IsInfinity(v);}
    }
}
#endif
