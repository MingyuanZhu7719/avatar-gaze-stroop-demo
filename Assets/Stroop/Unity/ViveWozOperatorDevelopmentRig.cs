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
        bool starting,running,ownsLoader,stopping,cleanupUncertain,quitRequested,headReady,stopRequested;
        long sequence;
        string status="XR and eye worker are off.";
        public string Status {get{return status;}}
        public IClock SharedClock {get{return clock;}}
        public ViveProEyeWorker Worker {get{return worker;}}
        public bool HeadReady {get{return running&&headReady;}}
        public bool ViewActive {get{return starting||running||stopping;}}
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
            starting=true;cameraView.cullingMask=0;status="Starting XR; eye worker remains off.";StartCoroutine(InitializeView());
        }
        IEnumerator InitializeView()
        {
            manager=XRGeneralSettings.Instance==null?null:XRGeneralSettings.Instance.Manager;
            if(manager==null||manager.activeLoader!=null){Fail("Expected an uninitialized XR loader.");yield break;}
            ownsLoader=true;IEnumerator operation;
            try{operation=manager.InitializeLoader();}catch(Exception e){Fail("XR initialization failed: "+e.GetType().Name);yield break;}
            while(true)
            {
                bool more;object current=null;
                try{more=operation.MoveNext();if(more)current=operation.Current;}catch(Exception e){Fail("XR initialization failed: "+e.GetType().Name);yield break;}
                if(!more)break;yield return current;
            }
            try
            {
                if(manager.activeLoader==null||String.IsNullOrEmpty(OpenXRRuntime.name)||!OpenXRRuntime.name.StartsWith("SteamVR",StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Expected the configured SteamVR OpenXR runtime.");
                display=manager.activeLoader.GetLoadedSubsystem<XRDisplaySubsystem>();
                input=manager.activeLoader.GetLoadedSubsystem<XRInputSubsystem>();
                if(display==null||input==null)throw new InvalidOperationException("OpenXR display or input is unavailable.");
                manager.StartSubsystems();starting=false;running=true;headReady=false;
                status="Waiting for focused head tracking; eye worker is off. Operator scene alignment is unvalidated.";
                if(stopRequested){stopRequested=false;StopView();}
            }
            catch(Exception e){Fail("XR view unavailable: "+e.Message);}
        }
        public void BeginEye()
        {
            if(!running||!headReady||worker!=null||stopping||cleanupUncertain||quitRequested)return;
            try
            {
                worker=new ViveProEyeWorker(clock,ReadHead,()=>new SrAnipalViveProEyeProvider(clock,()=>null));
                if(!worker.Start())throw new InvalidOperationException(worker.Diagnostic);
                status="Eye worker starting; initialization alone does not prove valid tracking.";
            }
            catch(Exception e){status="Eye worker start failed: "+e.Message;StopView();}
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
            headReady=running&&display!=null&&display.running&&hmd.isValid&&tracked&&p&&r&&finite&&Mathf.Abs(norm-1)<.000001f&&supported&&
                (flags&(InputTrackingState.Position|InputTrackingState.Rotation))==(InputTrackingState.Position|InputTrackingState.Rotation)&&OpenXRUtility.IsSessionFocused;
            cameraView.cullingMask=headReady?-1:0;
            if(headReady)cameraView.transform.SetPositionAndRotation(position,rotation);
            return new ViveHeadFrame(sequence++,clock.Now,hmd.isValid,headReady,new Rotation(rotation.x,rotation.y,rotation.z,rotation.w));
        }
        void Update()
        {
            if(running){try{ReadHead();}catch(Exception e){headReady=false;cameraView.cullingMask=0;status="Head tracking failed: "+e.GetType().Name;}}
            if(stopping&&worker!=null&&worker.HasExited)
            {
                if(!worker.CanStart)cleanupUncertain=true;
                worker.Dispose();worker=null;
            }
            if(stopping&&worker==null)FinishStop();
            if(quitRequested&&!starting&&!running&&!stopping&&worker==null)Application.Quit();
        }
        public void StopView()
        {
            if(starting){stopRequested=true;if(cameraView!=null)cameraView.cullingMask=0;status="Waiting for XR initialization to finish before cleanup.";return;}
            running=false;headReady=false;stopping=true;
            if(cameraView!=null)cameraView.cullingMask=0;
            if(worker!=null)worker.Stop();
            if(worker==null||worker.HasExited)FinishStop();
            else status="Stopping eye worker; waiting for native cleanup before unloading XR.";
        }
        void FinishStop()
        {
            if(worker!=null)
            {
                if(!worker.HasExited)return;
                if(!worker.CanStart)cleanupUncertain=true;
                worker.Dispose();worker=null;
            }
            try{if(ownsLoader&&manager!=null){manager.StopSubsystems();manager.DeinitializeLoader();}}
            catch(Exception e){cleanupUncertain=true;status="XR cleanup uncertain: "+e.GetType().Name;}
            finally{ownsLoader=false;manager=null;display=null;input=null;stopping=false;}
            if(!cleanupUncertain)status="XR and eye worker stopped. A new configured session requires explicit actions.";
        }
        void Fail(string message){starting=false;stopRequested=false;StopView();status=message;}
        bool WantsToQuit(){if(!starting&&!running&&!stopping&&worker==null)return true;quitRequested=true;StopView();return false;}
        void OnDestroy()
        {
            Application.wantsToQuit-=WantsToQuit;
            if(worker!=null)worker.Stop();
            // Do not unload the native stack underneath a still-running worker.
            if(worker==null||worker.HasExited)FinishStop();
        }
    }
}
#endif

