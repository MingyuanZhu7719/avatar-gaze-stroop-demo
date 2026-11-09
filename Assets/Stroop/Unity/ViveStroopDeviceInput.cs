#if STROOP_XR_PREVIEW
using System;
using AvatarStroop.Core;
using AvatarStroop.Runtime;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.XR.Management;
using UnityEngine.XR.OpenXR;
using UnityEngine.XR.OpenXR.Input;
using UnityEngine.XR.OpenXR.Features.Interactions;

namespace AvatarStroop.Unity
{
    // Explicitly selected physical wand. Zero is intentionally not a valid hand.
    public enum VivePreviewControllerHand { Left = 1, Right = 2 }

    // Pure gate shared by the physical adapter and synthetic regression tests.
    // Physical availability and neutral arming are intentionally independent:
    // a held button across a phase boundary is not a disconnected controller.
    public sealed class ViveStroopNeutralGate
    {
        bool armed,lastReady,previousTrigger,previousMenu;
        public ViveStroopNeutralState Observe(bool physicalReady,bool phaseChanged,bool deviceChanged,
            bool trackpadHeld,bool triggerHeld,bool menuHeld)
        {
            if(!physicalReady)
            {
                bool lost=lastReady;lastReady=false;armed=false;previousTrigger=previousMenu=false;
                return new ViveStroopNeutralState(false,false,lost,false,false);
            }
            if(phaseChanged||deviceChanged){armed=false;previousTrigger=previousMenu=false;}
            lastReady=true;
            if(!armed&&(trackpadHeld||triggerHeld||menuHeld))
                return new ViveStroopNeutralState(true,true,false,false,false);
            if(!armed){armed=true;previousTrigger=previousMenu=false;}
            bool triggerDown=triggerHeld&&!previousTrigger,menuDown=menuHeld&&!previousMenu;
            previousTrigger=triggerHeld;previousMenu=menuHeld;
            return new ViveStroopNeutralState(true,false,false,triggerDown,menuDown);
        }
    }
    public sealed class ViveStroopNeutralState
    {
        public bool PhysicalReady { get; private set; }
        public bool NeutralPending { get; private set; }
        public bool LostReady { get; private set; }
        public bool TriggerDown { get; private set; }
        public bool MenuDown { get; private set; }
        internal ViveStroopNeutralState(bool physicalReady,bool neutralPending,bool lostReady,bool triggerDown,bool menuDown)
        { PhysicalReady=physicalReady;NeutralPending=neutralPending;LostReady=lostReady;TriggerDown=triggerDown;MenuDown=menuDown; }
    }

    // One complete main-thread observation. Trigger/menu edges are raw physical
    // controls only; the caller assigns semantics and handles phase transitions.
    // An invalid sample must still reach VivePreviewTrackpadInput.Update so an
    // earlier held colour is released on focus/device/tracking loss.
    public sealed class ViveStroopDeviceFrame
    {
        public VivePreviewTrackpadSample Trackpad { get; private set; }
        public bool DeviceReady { get { return Trackpad.DeviceReady; } }
        public bool NeutralPending { get; private set; }
        // Owner start/restart must require input-ready, not just physical-ready.
        public bool InputReady { get { return DeviceReady&&!NeutralPending; } }
        public bool LostReady { get; private set; }
        public bool DeviceChanged { get; private set; }
        public bool TriggerHeld { get; private set; }
        public bool TriggerDown { get; private set; }
        public bool MenuHeld { get; private set; }
        public bool MenuDown { get; private set; }
        public string Diagnostic { get; private set; }
        internal ViveStroopDeviceFrame(VivePreviewTrackpadSample trackpad,bool neutralPending,bool lostReady,
            bool triggerHeld,bool triggerDown,bool menuHeld,bool menuDown,string diagnostic,bool deviceChanged=false)
        {
            Trackpad=trackpad;NeutralPending=neutralPending;LostReady=lostReady;TriggerHeld=triggerHeld;TriggerDown=triggerDown;
            MenuHeld=menuHeld;MenuDown=menuDown;Diagnostic=diagnostic;DeviceChanged=deviceChanged;
        }
    }

    // Development-only input adapter for the official Unity HTC Vive Controller
    // OpenXR profile. Instantiate once, before XR InitializeLoader starts; Enable
    // occurs in the constructor so OpenXR can suggest these bindings at startup.
    // Mapping and sector threshold are supplied to VivePreviewTrackpadInput by
    // the caller. This type neither chooses colours nor records/advances a task.
    // Recreate only after the loader is shut down; call Dispose on the main thread.
    public sealed class ViveStroopDeviceInput : IDisposable
    {
        readonly InputActionMap actions;
        readonly InputAction position,rotation,tracked,trackingState,trackpad,trackpadClick,trigger,menu;
        readonly int creatorThread;
        bool disposed,hasPhase;
        readonly ViveStroopNeutralGate neutralGate=new ViveStroopNeutralGate();
        readonly ViveStroopSafetyMenuGate safetyMenuGate=new ViveStroopSafetyMenuGate();
        Phase previousPhase;
        InputDevice lastDevice;
        InputDevice lastSafetyDevice;
        InputDevice lastStartupDevice;
        public VivePreviewControllerHand Hand { get; private set; }

        public ViveStroopDeviceInput(VivePreviewControllerHand hand)
        {
            if(hand!=VivePreviewControllerHand.Left&&hand!=VivePreviewControllerHand.Right)
                throw new ArgumentOutOfRangeException("hand", "Select an explicit left or right VIVE controller.");
            var manager=XRGeneralSettings.Instance==null?null:XRGeneralSettings.Instance.Manager;
            if(manager==null||manager.activeLoader!=null||manager.isInitializationComplete)
                throw new InvalidOperationException("Register VIVE inputs before starting the configured XR loader.");
            Hand=hand;creatorThread=System.Threading.Thread.CurrentThread.ManagedThreadId;
            actions=new InputActionMap("ViveStroopDevelopment");
            try
            {
                string device="<ViveController>{"+(hand==VivePreviewControllerHand.Left?"LeftHand":"RightHand")+"}/";
                position=Add("Position",device+"devicePosition","Vector3");
                rotation=Add("Rotation",device+"deviceRotation","Quaternion");
                tracked=Add("Tracked",device+"isTracked","Button");
                trackingState=Add("TrackingState",device+"trackingState","Integer");
                trackpad=Add("Trackpad",device+"trackpad","Vector2");
                trackpadClick=Add("TrackpadClick",device+"trackpadClicked","Button");
                trigger=Add("Trigger",device+"triggerPressed","Button");
                menu=Add("Menu",device+"menu","Button");
                actions.Enable();
            }
            catch { actions.Dispose(); throw; }
        }

        InputAction Add(string name,string binding,string type)
        { return actions.AddAction(name,InputActionType.Value,binding,expectedControlLayout:type); }
        static bool Bound(InputAction action){return action!=null&&action.enabled&&action.controls.Count==1;}
        static bool Active(InputAction action){return Bound(action)&&OpenXRInput.GetActionIsActive(action);}
        static ViveControllerActionDiagnostic Inspect(InputAction action,bool requiresActive)
        {
            try
            {
                bool enabled=action!=null&&action.enabled;
                int count=action==null?0:action.controls.Count;
                // Match the existing gate exactly: query OpenXR active state only
                // for enabled actions with precisely one resolved control.
                bool active=requiresActive&&enabled&&count==1&&OpenXRInput.GetActionIsActive(action);
                return new ViveControllerActionDiagnostic(enabled,count,requiresActive,active);
            }
            catch(Exception)
            {
                // The individual action remains unavailable, but other action
                // statuses can still explain the selected hand's failure.
                return new ViveControllerActionDiagnostic(false,0,requiresActive,false,true);
            }
        }
        static string DetectedWandHands()
        {
            bool left=false,right=false;
            foreach(var device in InputSystem.devices)
            {
                if(!(device is HTCViveControllerProfile.ViveController)||!device.added||!device.enabled)continue;
                foreach(var usage in device.usages){if(usage.ToString()=="LeftHand")left=true;if(usage.ToString()=="RightHand")right=true;}
            }
            return " Detected VIVE wand hands: "+(left&&right?"Left and Right":left?"Left":right?"Right":"none")+". Check the selected hand; it will not be changed automatically.";
        }
        static bool Finite(float value){return !Single.IsNaN(value)&&!Single.IsInfinity(value);}
        static bool ValidButton(float value){return Finite(value)&&value>=0&&value<=1;}
        static bool ValidPosition(Vector3 p){return Finite(p.x)&&Finite(p.y)&&Finite(p.z);}
        static bool ValidRotation(Quaternion q)
        {
            if(!Finite(q.x)||!Finite(q.y)||!Finite(q.z)||!Finite(q.w))return false;
            return Mathf.Abs(q.x*q.x+q.y*q.y+q.z*q.z+q.w*q.w-1f)<.02f;
        }
        void MainThread()
        {
            if(System.Threading.Thread.CurrentThread.ManagedThreadId!=creatorThread)
                throw new InvalidOperationException("Read and dispose the VIVE device adapter on its creating Unity thread.");
            if(disposed)throw new ObjectDisposedException("ViveStroopDeviceInput");
        }
        ViveStroopDeviceFrame Unavailable(string message)
        {
            var gate=neutralGate.Observe(false,false,false,false,false,false);lastDevice=null;
            return new ViveStroopDeviceFrame(new VivePreviewTrackpadSample(false,false,0,0),false,gate.LostReady,false,false,false,false,message);
        }

        // Independent device continuity for pre-placement confirmation. A
        // following Poll(false) may reset ordinary task edges, not this identity.
        public ViveStroopDeviceFrame ReadStartupAlignment(bool trackedHead)
        {
            var frame=Read(Phase.Preparation,trackedHead);
            if(!frame.DeviceReady){lastStartupDevice=null;return frame;}
            var selected=trigger.controls[0].device;
            bool changed=lastStartupDevice!=null&&!object.ReferenceEquals(lastStartupDevice,selected);
            lastStartupDevice=selected;
            return new ViveStroopDeviceFrame(frame.Trackpad,frame.NeutralPending,frame.LostReady,
                frame.TriggerHeld,frame.TriggerDown,frame.MenuHeld,frame.MenuDown,frame.Diagnostic,changed||frame.DeviceChanged);
        }

        public void ResetSafetyMenu()
        {MainThread();safetyMenuGate.Reset();lastSafetyDevice=null;}

        // Only the selected wand's menu and the minimum tracking/focus data
        // are read here. No trigger, trackpad, task response or eye sample is
        // read when the recording lifecycle has blocked the ordinary poll.
        public bool ReadSafetyMenu(bool presentationFocused)
        {
            MainThread();
            try
            {
                if(!presentationFocused||!OpenXRUtility.IsSessionFocused||!Bound(position)||!Bound(rotation)||
                    !Bound(tracked)||!Bound(trackingState)||!Active(menu))
                {safetyMenuGate.Reset();lastSafetyDevice=null;return false;}
                InputDevice selected=menu.controls[0].device;
                if(!(selected is HTCViveControllerProfile.ViveController)||!selected.added||!selected.enabled||
                    position.controls[0].device!=selected||rotation.controls[0].device!=selected||
                    tracked.controls[0].device!=selected||trackingState.controls[0].device!=selected)
                {safetyMenuGate.Reset();lastSafetyDevice=null;return false;}
                Vector3 p=position.ReadValue<Vector3>();Quaternion q=rotation.ReadValue<Quaternion>();
                float tracking=tracked.ReadValue<float>();int flags=trackingState.ReadValue<int>();float button=menu.ReadValue<float>();
                if(!ValidPosition(p)||!ValidRotation(q)||!ValidButton(tracking)||tracking<=.5f||(flags&3)!=3||!ValidButton(button))
                {safetyMenuGate.Reset();lastSafetyDevice=null;return false;}
                bool changed=lastSafetyDevice!=null&&!object.ReferenceEquals(lastSafetyDevice,selected);
                lastSafetyDevice=selected;return safetyMenuGate.Observe(true,changed,button>.5f);
            }
            catch(Exception){safetyMenuGate.Reset();lastSafetyDevice=null;return false;}
        }

        // The caller supplies the current phase and independently validated
        // headset/presentation readiness. All controls must be physically neutral
        // after startup, phase changes, loss or controller replacement before any
        // edge can be emitted. Never infer a press from reconnecting while held.
        public ViveStroopDeviceFrame Read(Phase phase,bool presentationReady)
        {
            MainThread();
            if(!Enum.IsDefined(typeof(Phase),phase))throw new ArgumentOutOfRangeException("phase");
            bool phaseChanged=!hasPhase||previousPhase!=phase;hasPhase=true;previousPhase=phase;
            try
            {
                if(!presentationReady)return Unavailable("XR presentation or head tracking is not ready.");
                if(!OpenXRUtility.IsSessionFocused)return Unavailable("OpenXR session is not focused.");
                var positionState=Inspect(position,false);var rotationState=Inspect(rotation,false);
                var trackedState=Inspect(tracked,false);var trackingStateState=Inspect(trackingState,false);
                var trackpadState=Inspect(trackpad,true);var clickState=Inspect(trackpadClick,true);
                var triggerState=Inspect(trigger,true);var menuState=Inspect(menu,true);
                if(!positionState.Ready||!rotationState.Ready||!trackedState.Ready||!trackingStateState.Ready||
                   !trackpadState.Ready||!clickState.Ready||!triggerState.Ready||!menuState.Ready)
                    return Unavailable(ViveControllerReadinessDiagnostic.Bindings(Hand.ToString(),new[] {
                        positionState,rotationState,trackedState,trackingStateState,trackpadState,clickState,triggerState,menuState })+DetectedWandHands());
                InputDevice device=trackpad.controls[0].device;
                if(!(device is HTCViveControllerProfile.ViveController))
                    return Unavailable(Hand+" input resolved to an unsupported controller profile.");
                if(!device.added||!device.enabled)
                    return Unavailable(Hand+" VIVE input device is not added or is disabled.");
                if(position.controls[0].device!=device||rotation.controls[0].device!=device||tracked.controls[0].device!=device||
                    trackingState.controls[0].device!=device||trackpadClick.controls[0].device!=device||
                    trigger.controls[0].device!=device||menu.controls[0].device!=device)
                    return Unavailable(Hand+" VIVE actions resolve to different devices; check the selected hand and bindings.");
                bool deviceChanged=lastDevice!=null&&!object.ReferenceEquals(device,lastDevice);
                lastDevice=device;
                var p=position.ReadValue<Vector3>();var q=rotation.ReadValue<Quaternion>();
                float tracking=tracked.ReadValue<float>();int flags=trackingState.ReadValue<int>();
                var axis=trackpad.ReadValue<Vector2>();float click=trackpadClick.ReadValue<float>();
                float triggerValue=trigger.ReadValue<float>(),menuValue=menu.ReadValue<float>();
                if(!ValidPosition(p)||!ValidRotation(q))return Unavailable(Hand+" VIVE controller pose is invalid.");
                if(!ValidButton(tracking))return Unavailable(Hand+" VIVE controller tracked value is invalid.");
                if(tracking<=.5f||(flags&3)!=3)return Unavailable(Hand+" VIVE controller pose tracking is not ready (tracked="+(tracking>.5f?"yes":"no")+", position="+((flags&1)!=0?"yes":"no")+", rotation="+((flags&2)!=0?"yes":"no")+").");
                if(!Finite(axis.x)||!Finite(axis.y)||axis.x< -1||axis.x>1||axis.y< -1||axis.y>1)
                    return Unavailable(Hand+" VIVE trackpad axis is invalid.");
                if(!ValidButton(click)||!ValidButton(triggerValue)||!ValidButton(menuValue))
                    return Unavailable(Hand+" VIVE trackpad click, trigger or menu value is invalid.");
                bool clicked=click>.5f,triggerHeld=triggerValue>.5f,menuHeld=menuValue>.5f;
                var gate=neutralGate.Observe(true,phaseChanged,deviceChanged,clicked,triggerHeld,menuHeld);
                // During neutral waiting the real pressed level still reaches the
                // trackpad router, so its held colour is released only on release.
                return new ViveStroopDeviceFrame(new VivePreviewTrackpadSample(true,clicked,axis.x,axis.y),gate.NeutralPending,gate.LostReady,
                    triggerHeld,gate.TriggerDown,menuHeld,gate.MenuDown,gate.NeutralPending?
                    "Release the trackpad, trigger and menu before accepting a new input.":
                    "Selected VIVE controller input is available; mapping is supplied separately.",deviceChanged);
            }
            catch(Exception error)
            {
                // Do not include device identity, paths or vendor exception text.
                return Unavailable("VIVE input unavailable: "+error.GetType().Name);
            }
        }

        public void Dispose()
        {
            if(disposed)return;
            MainThread();disposed=true;actions.Disable();actions.Dispose();
        }
    }
}
#endif
