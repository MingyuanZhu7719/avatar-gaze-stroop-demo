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
    // Single owner for this operator process's XR loader and HTC worker. Nothing
    // starts from Awake. Coordinates are development-only and unvalidated.
    public sealed class ViveWozOperatorDevelopmentRig : MonoBehaviour
    {
        Camera cameraView;
        XRManagerSettings manager;
        XRDisplaySubsystem display;
        XRInputSubsystem input;
        MonotonicClock clock;
        ViveProEyeWorker worker;
        ViveWozOperatorDiagnostics diagnostics;
        bool starting,running,ownsLoader,stopping,cleanupUncertain,quitRequested,headReady,stopRequested;
        bool stationPlaced,referenceChanged;
        TrackingOriginModeFlags stationMode;
        long sequence;
        string status="XR and eye worker are off.";
        string failureMessage;
        string headTrackingStatus="Headset view is off.",eyeStatus="Eye source is off.";
        string loggedHeadState,loggedEyeState;
        string loggedEyeInstallation;
        public string EyeInstallationDiagnostic {get{return loggedEyeInstallation??"";}}
        double headStateLoggedAt=double.NegativeInfinity,eyeStateLoggedAt=double.NegativeInfinity,eyeStartAt;
        public string Status {get{return failureMessage??status;}}
        public string HeadTrackingStatus {get{return headTrackingStatus;}}
        public string EyeStatus {get{return eyeStatus;}}
        public IClock SharedClock {get{return clock;}}
        public ViveProEyeWorker Worker {get{return worker;}}
        internal Action BeforeStopping;
        internal Action<Vector3> PlaceOperatorStation;
        public bool HeadReady {get{return running&&headReady;}}
        public bool ViewActive {get{return starting||running||stopping;}}
        internal void AttachDiagnostics(ViveWozOperatorDiagnostics value)
        {diagnostics=value;}
        void Record(string code,string message)
        {if(diagnostics!=null)diagnostics.Record(code,message);}
        void ReportFailure(string code,string message,Exception error=null)
        {
            bool primary=failureMessage==null;
            if(primary)failureMessage=message;
            if(diagnostics!=null)
            {
                if(primary)diagnostics.Fail(code,message,error);
                else diagnostics.Record(code,message+(error==null?"":"\n"+error));
            }
            else Debug.LogError(message+(error==null?"":"\n"+error));
        }
        public void Configure(Camera camera)
        {
            if(camera==null||cameraView!=null)throw new InvalidOperationException("One operator camera required.");
            cameraView=camera;
            clock=new MonotonicClock("vive-operator-clock-"+Guid.NewGuid().ToString("N"));
            Application.wantsToQuit+=WantsToQuit;
        }
        public void BeginView()
        {
            if(cameraView==null||starting||running||stopping||cleanupUncertain||quitRequested||worker!=null)return;
            failureMessage=null;
            stationPlaced=false;referenceChanged=false;
            loggedHeadState=null;loggedEyeState=null;
            headStateLoggedAt=double.NegativeInfinity;eyeStateLoggedAt=double.NegativeInfinity;
            Record("XR_START_REQUESTED","Starting the headset view.");
            headTrackingStatus="Initializing the OpenXR headset view.";
            starting=true;cameraView.cullingMask=0;status="Starting XR; eye worker remains off.";StartCoroutine(InitializeView());
        }
        IEnumerator InitializeView()
        {
            manager=XRGeneralSettings.Instance==null?null:XRGeneralSettings.Instance.Manager;
            if(manager==null){Fail("XR_MANAGER_MISSING","OpenXR settings are missing. Use the complete operator package.");yield break;}
            if(manager.activeLoader!=null){Fail("XR_ALREADY_INITIALIZED","The XR loader is already initialized. Close and reopen the operator program.");yield break;}
            ownsLoader=true;IEnumerator operation;
            try{operation=manager.InitializeLoader();}catch(Exception e){Fail("XR_INITIALIZATION_EXCEPTION","OpenXR initialization failed. "+e.Message,e);yield break;}
            while(true)
            {
                bool more;object current=null;
                try{more=operation.MoveNext();if(more)current=operation.Current;}catch(Exception e){Fail("XR_INITIALIZATION_EXCEPTION","OpenXR initialization failed. "+e.Message,e);yield break;}
                if(!more)break;yield return current;
            }
            try
            {
                string runtime=OpenXRRuntime.name;
                Record("XR_RUNTIME","OpenXR runtime: "+(String.IsNullOrEmpty(runtime)?"unavailable":runtime));
                if(!String.IsNullOrEmpty(runtime)&&!runtime.StartsWith("SteamVR",StringComparison.OrdinalIgnoreCase))
                {Fail("XR_WRONG_RUNTIME","OpenXR selected "+runtime+". Close this program and open Start-WoZ-Operator.cmd to use SteamVR.");yield break;}
                if(manager.activeLoader==null||String.IsNullOrEmpty(runtime))
                {Fail("XR_LOADER_UNAVAILABLE","OpenXR could not initialize the headset. Open SteamVR, check the headset connection, then launch with Start-WoZ-Operator.cmd.");yield break;}
                display=manager.activeLoader.GetLoadedSubsystem<XRDisplaySubsystem>();
                input=manager.activeLoader.GetLoadedSubsystem<XRInputSubsystem>();
                if(display==null){Fail("XR_DISPLAY_UNAVAILABLE","SteamVR initialized but its display subsystem is unavailable. Check SteamVR and reopen the operator program.");yield break;}
                if(input==null){Fail("XR_INPUT_UNAVAILABLE","SteamVR initialized but its input subsystem is unavailable. Check SteamVR and reopen the operator program.");yield break;}
                input.trackingOriginUpdated+=OnTrackingOriginUpdated;
                manager.StartSubsystems();starting=false;running=true;headReady=false;
                Record("XR_START_RETURNED","SteamVR subsystem start returned. Waiting for a running display and focused head tracking.");
                status="Waiting for focused head tracking; eye worker is off. Operator scene alignment is unvalidated.";
                if(stopRequested){stopRequested=false;StopView();}
            }
            catch(Exception e){Fail("XR_START_EXCEPTION","Headset view could not start. "+e.Message,e);}
        }
        public void BeginEye()
        {
            if(!running||!headReady||worker!=null||stopping||cleanupUncertain||quitRequested)return;
            try
            {
                failureMessage=null;
                loggedEyeInstallation=null;
                eyeStartAt=Time.realtimeSinceStartupAsDouble;
                eyeStatus="Starting the HTC eye client.";
                Record("EYE_START_REQUESTED",eyeStatus);
                worker=ViveProEyeWorker.CreateInstalled(clock,ReadHead);
                if(!worker.Start())throw new InvalidOperationException(worker.Diagnostic);
                status="Eye worker starting; initialization alone does not prove valid tracking.";
            }
            catch(Exception e){ReportFailure("EYE_START_FAILED","Eye tracking could not start. "+e.Message,e);eyeStatus=failureMessage;StopView();}
        }
        static bool Finite(float value){return !Single.IsNaN(value)&&!Single.IsInfinity(value);}
        ViveHeadFrame ReadHead()
        {
            var hmd=InputDevices.GetDeviceAtXRNode(XRNode.Head);bool tracked=false;InputTrackingState flags=InputTrackingState.None;
            Vector3 position;Quaternion rotation;
            bool p=hmd.TryGetFeatureValue(CommonUsages.devicePosition,out position),r=hmd.TryGetFeatureValue(CommonUsages.deviceRotation,out rotation);
            hmd.TryGetFeatureValue(CommonUsages.isTracked,out tracked);hmd.TryGetFeatureValue(CommonUsages.trackingState,out flags);
            float norm=rotation.x*rotation.x+rotation.y*rotation.y+rotation.z*rotation.z+rotation.w*rotation.w;
            bool finite=Finite(position.x)&&Finite(position.y)&&Finite(position.z)&&Finite(rotation.x)&&Finite(rotation.y)&&Finite(rotation.z)&&Finite(rotation.w);
            var mode=input!=null&&input.running?input.GetTrackingOriginMode():TrackingOriginModeFlags.Unknown;
            bool supported=mode==TrackingOriginModeFlags.Floor||mode==TrackingOriginModeFlags.Device;
            bool focused=OpenXRUtility.IsSessionFocused;
            headReady=running&&display!=null&&display.running&&hmd.isValid&&tracked&&p&&r&&finite&&Mathf.Abs(norm-1)<.000001f&&supported&&
                (flags&(InputTrackingState.Position|InputTrackingState.Rotation))==(InputTrackingState.Position|InputTrackingState.Rotation)&&focused;
            if(stationPlaced&&mode!=stationMode)referenceChanged=true;
            if(referenceChanged)
            {
                // A source already bound to this tracking frame cannot silently
                // inherit a new origin. Before eye acquisition, placement may retry.
                if(worker==null){stationPlaced=false;referenceChanged=false;}
                else headReady=false;
            }
            if(headReady&&!stationPlaced)
            {
                if(PlaceOperatorStation==null)throw new InvalidOperationException("Operator station placement is missing.");
                PlaceOperatorStation(position);stationPlaced=true;stationMode=mode;
                Record("OPERATOR_STATION_PLACED","Evaluator station placed beside the whiteboard using "+mode+" tracking. Raw head rotation is unchanged.");
            }
            string reason,message;
            if(!running||display==null||!display.running)
            {reason="HEAD_DISPLAY_STOPPED";message="The headset display is stopped.";}
            else if(!hmd.isValid)
            {reason="HEAD_DEVICE_MISSING";message="SteamVR has no connected headset. Check the headset cable and SteamVR status.";}
            else if(!tracked)
            {reason="HEAD_TRACKING_LOST";message="Headset tracking is unavailable. Keep the headset in view of the base stations.";}
            else if(!p||!r||(flags&(InputTrackingState.Position|InputTrackingState.Rotation))!=(InputTrackingState.Position|InputTrackingState.Rotation))
            {reason="HEAD_POSE_INCOMPLETE";message="SteamVR is waiting for a complete headset position and rotation.";}
            else if(!finite||Mathf.Abs(norm-1)>=.000001f)
            {reason="HEAD_POSE_INVALID";message="SteamVR returned an invalid headset pose. Wait for stable tracking.";}
            else if(!supported)
            {reason="HEAD_ORIGIN_UNAVAILABLE";message="SteamVR tracking origin is unavailable: "+mode+".";}
            else if(referenceChanged)
            {reason="HEAD_REFERENCE_CHANGED";message="Tracking origin changed. Stop and reopen the headset view before reconnecting.";}
            else if(!focused)
            {reason="HEAD_FOCUS_UNAVAILABLE";message="The headset view has no input focus. Wear the headset and close the SteamVR dashboard.";}
            else
            {reason="HEAD_TRACKED";message="Headset tracked and focused.";}
            headTrackingStatus=message;
            double now=Time.realtimeSinceStartupAsDouble;
            if(reason!=loggedHeadState&&now-headStateLoggedAt>=1)
            {loggedHeadState=reason;headStateLoggedAt=now;Record(reason,message);}
            cameraView.cullingMask=headReady?-1:0;
            if(headReady)cameraView.transform.SetPositionAndRotation(position,rotation);
            return new ViveHeadFrame(sequence++,clock.Now,hmd.isValid,headReady,new Rotation(rotation.x,rotation.y,rotation.z,rotation.w));
        }
        void Update()
        {
            if(running)
            {
                try{ReadHead();if(referenceChanged&&worker!=null){ReportFailure("HEAD_REFERENCE_CHANGED","SteamVR changed the tracking origin. Reopen the operator program before reconnecting.");StopView();}else UpdateEyeStatus();}
                catch(Exception e){headReady=false;cameraView.cullingMask=0;ReportFailure("TRACKING_UPDATE_EXCEPTION","Tracking update failed. "+e.Message,e);StopView();}
            }
            if(stopping&&worker!=null&&worker.HasExited)
            {
                FinishStop();
            }
            if(stopping&&worker==null)FinishStop();
            if(quitRequested&&!starting&&!running&&!stopping&&worker==null)Application.Quit();
        }
        void OnTrackingOriginUpdated(XRInputSubsystem subsystem)
        {if(stationPlaced)referenceChanged=true;}
        void UpdateEyeStatus()
        {
            if(worker==null||stopping)return;
            if(String.IsNullOrEmpty(loggedEyeInstallation))
            {
                string installation=worker.InstallationDiagnostic;
                if(!String.IsNullOrEmpty(installation)){loggedEyeInstallation=installation;Record("EYE_CLIENT_INSTALLATION",installation);}
            }
            double now=Time.realtimeSinceStartupAsDouble;
            string detail=worker.Diagnostic;
            string state;
            if(worker.Starting)
            {
                bool delayed=now-eyeStartAt>=15;
                state=delayed?"EYE_START_WAITING":"EYE_STARTING";
                eyeStatus=delayed?"Still waiting for the HTC eye client. Check that SRanipal is running and finish any HTC prompt in the headset.": "Starting the HTC eye client.";
            }
            else if(worker.Started)
            {state="EYE_RUNNING";eyeStatus="Eye client running. "+detail;}
            else
            {
                eyeStatus="Eye client stopped. "+detail;
                ReportFailure("EYE_CLIENT_FAILED",eyeStatus);
                StopView();
                return;
            }
            string observed=state+" "+detail;
            if(observed!=loggedEyeState&&now-eyeStateLoggedAt>=1)
            {loggedEyeState=observed;eyeStateLoggedAt=now;Record(state,state=="EYE_RUNNING"?eyeStatus:eyeStatus+" "+detail);}
        }
        public void StopView()
        {
            Record("XR_STOP_REQUESTED","Stopping the headset view and eye source.");
            NotifyBeforeStopping();
            if(starting){stopRequested=true;if(cameraView!=null)cameraView.cullingMask=0;status="Waiting for XR initialization to finish before cleanup.";return;}
            running=false;headReady=false;stopping=true;
            headTrackingStatus="Stopping the headset view.";
            if(cameraView!=null)cameraView.cullingMask=0;
            if(worker!=null)worker.Stop();
            if(worker==null||worker.HasExited)FinishStop();
            else{status="Stopping eye worker; waiting for native cleanup before unloading XR.";eyeStatus=status;}
        }
        void FinishStop()
        {
            if(worker!=null)
            {
                if(!worker.HasExited)return;
                if(!worker.CanStart){cleanupUncertain=true;ReportFailure("EYE_CLEANUP_UNCERTAIN","Eye client cleanup is unconfirmed. Reopen the operator program before starting again. "+worker.Diagnostic);}
                worker.Dispose();worker=null;
            }
            if(input!=null)input.trackingOriginUpdated-=OnTrackingOriginUpdated;
            try{if(ownsLoader&&manager!=null){manager.StopSubsystems();manager.DeinitializeLoader();}}
            catch(Exception e){cleanupUncertain=true;ReportFailure("XR_CLEANUP_FAILED","OpenXR cleanup failed. Reopen the operator program before starting again. "+e.Message,e);}
            finally{ownsLoader=false;manager=null;display=null;input=null;stopping=false;}
            headTrackingStatus="Headset view is off.";
            eyeStatus=failureMessage==null?"Eye source is off.":"Eye source is off after an error; see the error above.";
            if(!cleanupUncertain){status="XR and eye worker stopped. A new configured session requires explicit actions.";Record("XR_STOPPED",status);}
        }
        void Fail(string code,string message,Exception error=null)
        {ReportFailure(code,message,error);starting=false;stopRequested=false;StopView();status=message;}
        bool WantsToQuit(){if(!starting&&!running&&!stopping&&worker==null)return true;quitRequested=true;StopView();return false;}
        void NotifyBeforeStopping()
        {try{if(BeforeStopping!=null)BeforeStopping();}catch(Exception e){cleanupUncertain=true;ReportFailure("SCENE_CLEANUP_FAILED","Operator scene cleanup failed while stopping the source. "+e.Message,e);}}
        void OnDestroy()
        {
            NotifyBeforeStopping();
            Application.wantsToQuit-=WantsToQuit;
            if(worker!=null)worker.Stop();
            // Do not unload the native stack underneath a still-running worker.
            if(worker==null||worker.HasExited)FinishStop();
        }
    }
}
#endif

