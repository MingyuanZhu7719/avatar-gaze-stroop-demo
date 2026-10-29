#if STROOP_XR_PREVIEW
using System;
using System.Collections;
using AvatarStroop.Core;
using AvatarStroop.Runtime;
using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.XR;
using UnityEngine.XR;
using UnityEngine.XR.Management;
using UnityEngine.XR.OpenXR.Input;

namespace AvatarStroop.Unity
{
    // Participant-only PC Link presentation. The tracked camera never supplies
    // avatar control or participant eye data, and never admits RunKind.Apparatus.
    [DefaultExecutionOrder(-200)]
    public sealed class QuestLinkPreviewRig : MonoBehaviour
    {
        [SerializeField] Camera previewCamera;
        [SerializeField] XROrigin origin;
        [SerializeField] TrackedPoseDriver poseDriver;
        WorldSpaceExperimentView view;
        XRManagerSettings manager;
        XRDisplaySubsystem display;
        InputActionMap actions;
        InputAction position, rotation, trackingState, tracked, x, y, a, b, trigger, menu, stick;
        readonly QuestPreviewInputRouter router = new QuestPreviewInputRouter();
        bool ownsLoader, focusKnown, displayFocused;
        string failure, lastDiagnostic;
        public bool HeadViewReady { get; private set; }
        public bool InputReady { get; private set; }
        public string Diagnostic { get; private set; } = "Waiting for OpenXR and Quest Link";

        // Explicit scene authoring only; no global runtime selection or startup hook.
        public void Configure(Camera camera)
        {
            if(camera == null) throw new ArgumentNullException("camera");
            view = GetComponent<WorldSpaceExperimentView>();
            if(view == null || view.Role != Actor.Participant)
                throw new InvalidOperationException("Quest preview rig belongs only to the participant scene");
            previewCamera = camera;
            if(origin == null)
            {
                var root = new GameObject("QuestPreviewOrigin"); root.SetActive(false);
                var offset = new GameObject("DeviceOriginHeightOffset"); offset.transform.SetParent(root.transform, false);
                // XROrigin applies CameraYOffset only once a subsystem is ready.
                // Preserve the baseline geometry during bootstrap initialization too.
                offset.transform.localPosition = new Vector3(0,1.6f,0);
                camera.transform.SetParent(offset.transform, false);
                camera.transform.localPosition = Vector3.zero; camera.transform.localRotation = Quaternion.identity;
                poseDriver = camera.gameObject.AddComponent<TrackedPoseDriver>();
                origin = root.AddComponent<XROrigin>();
                origin.Origin = root; origin.Camera = camera; origin.CameraFloorOffsetObject = offset;
                origin.CameraYOffset = 1.6f; origin.RequestedTrackingOriginMode = XROrigin.TrackingOriginMode.Device;
                root.SetActive(true);
            }
            camera.stereoTargetEye = StereoTargetEyeMask.Both;
            view.SetHeadsetPreview("Quest Link preview; waiting for headset and controllers", "X red | Y green | A blue | B yellow\nLeft menu: pause; while paused, B: withdraw");
            view.SetPresentationAvailable(false);
        }

        void Awake()
        {
            view = GetComponent<WorldSpaceExperimentView>();
            try
            {
                if(view == null || view.Role != Actor.Participant || previewCamera == null || origin == null || poseDriver == null)
                    throw new InvalidOperationException("Participant Quest preview rig is incomplete");
                view.SetPresentationAvailable(false);
                view.SetPresentationAvailabilityCheck(LivePresentationAvailable);
                // Register and enable every action BEFORE OpenXR initializes its
                // session. Bindings are never added during an active XR session.
                actions = new InputActionMap("QuestPreview");
                // OpenXR reports device and centre-eye poses identically.
                // HMD reference-space tracking is not an interaction-profile
                // pose action; its tracked flags validate these camera inputs.
                position = Add("HeadPosition", "<XRHMD>/devicePosition", "Vector3");
                rotation = Add("HeadRotation", "<XRHMD>/deviceRotation", "Quaternion");
                trackingState = Add("HeadTrackingState", "<XRHMD>/trackingState", "Integer");
                tracked = Add("HeadTracked", "<XRHMD>/isTracked", "Button");
                x = Add("RedX", "<OculusTouchController>{LeftHand}/primaryButton", "Button");
                y = Add("GreenY", "<OculusTouchController>{LeftHand}/secondaryButton", "Button");
                a = Add("BlueA", "<OculusTouchController>{RightHand}/primaryButton", "Button");
                b = Add("YellowB", "<OculusTouchController>{RightHand}/secondaryButton", "Button");
                trigger = Add("Continue", "<OculusTouchController>{RightHand}/triggerPressed", "Button");
                menu = Add("Pause", "<OculusTouchController>{LeftHand}/menu", "Button");
                stick = Add("RatingSelection", "<OculusTouchController>{LeftHand}/thumbstick", "Vector2");
                poseDriver.enabled = false;
                poseDriver.trackingType = TrackedPoseDriver.TrackingType.RotationAndPosition;
                poseDriver.updateType = TrackedPoseDriver.UpdateType.UpdateAndBeforeRender;
                poseDriver.ignoreTrackingState = false;
                poseDriver.positionInput = new InputActionProperty(position);
                poseDriver.rotationInput = new InputActionProperty(rotation);
                poseDriver.trackingStateInput = new InputActionProperty(trackingState);
                actions.Enable(); poseDriver.enabled = true;
            }
            catch(Exception error) { Fail(error.Message); }
        }

        InputAction Add(string name, string binding, string type)
        {
            return actions.AddAction(name, InputActionType.Value, binding, expectedControlLayout:type);
        }

        IEnumerator Start()
        {
            if(failure != null) yield break;
            manager = XRGeneralSettings.Instance == null ? null : XRGeneralSettings.Instance.Manager;
            if(manager == null) { Fail("OpenXR project loader is not configured"); yield break; }
            if(manager.activeLoader != null) { Fail("Unexpected preinitialized XR loader; preview requires participant-only startup"); yield break; }
            ownsLoader = true;
            var initialize = manager.InitializeLoader();
            while(true)
            {
                bool more; object current = null;
                try { more = initialize.MoveNext(); if(more) current = initialize.Current; }
                catch(Exception error) { Fail("OpenXR initialization failed: " + error.Message); yield break; }
                if(!more) break;
                yield return current;
            }
            if(manager.activeLoader == null)
            {
                Fail("OpenXR unavailable. Connect Quest Link first, then explicitly restart this preview."); yield break;
            }
            try
            {
                display = manager.activeLoader.GetLoadedSubsystem<XRDisplaySubsystem>();
                if(display == null) throw new InvalidOperationException("OpenXR display subsystem is unavailable");
                display.displayFocusChanged += DisplayFocusChanged;
                manager.StartSubsystems();
                Debug.Log("QUEST PREVIEW: OpenXR start requested; actual headset display has not been verified. Gaze remains synthetic.");
            }
            catch(Exception error) { Fail("OpenXR startup failed: " + error.Message); }
        }

        void DisplayFocusChanged(bool focused)
        {
            focusKnown = true; displayFocused = focused;
            if(!focused)
            {
                HeadViewReady = InputReady = false;
                if(view != null) view.SetPresentationAvailable(false);
            }
        }
        bool LivePresentationAvailable()
        {
            // Recheck at the actual pre-cull/post-render witness boundary too;
            // loss between Update and rendering cannot acknowledge a stale view.
            try { RefreshState(); return HeadViewReady && previewCamera != null && previewCamera.stereoEnabled; }
            catch(Exception error) { Fail("XR presentation unavailable: " + error.Message); return false; }
        }
        void Fail(string message)
        {
            failure = message; HeadViewReady = InputReady = false; Diagnostic = message;
            if(view != null) view.SetPresentationAvailable(false);
            Debug.LogError("QUEST PREVIEW: " + message);
        }
        static bool Bound(InputAction action) { return action != null && action.enabled && action.controls.Count > 0; }
        static bool Active(InputAction action) { return Bound(action) && OpenXRInput.GetActionIsActive(action); }
        static bool Pressed(InputAction action) { return Bound(action) && action.ReadValue<float>() > .5f; }
        static bool Finite(float value) { return !Single.IsNaN(value) && !Single.IsInfinity(value); }
        bool ValidPose()
        {
            if(!Bound(position) || !Bound(rotation)) return false;
            Vector3 p = position.ReadValue<Vector3>(); Quaternion q = rotation.ReadValue<Quaternion>();
            float norm = q.x*q.x + q.y*q.y + q.z*q.z + q.w*q.w;
            return Finite(p.x) && Finite(p.y) && Finite(p.z) && Finite(q.x) && Finite(q.y) && Finite(q.z) && Finite(q.w) && Mathf.Abs(norm-1f)<.02f;
        }

        void RefreshState()
        {
            bool running = failure == null && display != null && display.running;
            bool bound = Bound(position) && Bound(rotation) && Bound(trackingState) && Bound(tracked);
            bool isTracked = bound && Pressed(tracked);
            int tracking = bound ? trackingState.ReadValue<int>() : 0;
            bool poseValid = bound && ValidPose();
            bool positionActive = Active(position), rotationActive = Active(rotation);
            // GetActionIsActive describes interaction-profile actions. The HMD
            // is located through OpenXR view/reference spaces, so that API can
            // report false for a valid tracked head. Retain it for Touch only.
            bool head = running && isTracked && (tracking & 3) == 3 && poseValid;
            HeadViewReady = head && (!focusKnown || displayFocused);
            InputReady = HeadViewReady && Active(x) && Active(y) && Active(a) && Active(b) && Active(trigger) && Active(menu) && Active(stick);
            Diagnostic = failure ?? (!running ? "Waiting for OpenXR display; connect Quest Link before launch" :
                !bound ? "Waiting for headset input bindings" :
                !isTracked || (tracking & 3) != 3 ? "Headset position / rotation tracking unavailable" :
                !poseValid ? "Headset pose is invalid; view is not current" :
                !HeadViewReady ? "Headset has no XR focus; return to the preview in Link" :
                !InputReady ? "Head pose available; waiting for both Touch controllers" :
                "Head pose and Touch input available | avatar gaze: SIMULATED");
            if(Diagnostic != lastDiagnostic)
            {
                Debug.Log("QUEST PREVIEW STATUS: " + Diagnostic + "; display=" + running + "; bound=" + bound +
                    "; tracked=" + isTracked + "; trackingFlags=" + tracking + "; validPose=" + poseValid +
                    "; positionAction=" + positionActive + "; rotationAction=" + rotationActive +
                    "; focusKnown=" + focusKnown + "; focused=" + displayFocused +
                    "; no automatic task pause/resume or source change");
                lastDiagnostic = Diagnostic;
            }
            // A desktop mirror alone must never acknowledge a headset presentation.
            if(view != null) view.SetPresentationAvailable(HeadViewReady && previewCamera != null && previewCamera.stereoEnabled);
        }

        void Update()
        {
            try { RefreshState(); }
            catch(Exception error) { Fail("XR input unavailable: " + error.Message); if(view != null) view.SetPresentationAvailable(false); }
            if(view != null) view.SetHeadsetPreview(Diagnostic, "X red | Y green | A blue | B yellow\nLeft menu: pause; while paused, B: withdraw");
        }

        public QuestPreviewInputFrame ReadInput(Phase phase, int questionIndex)
        {
            var controls = new QuestPreviewControls(false,false,false,false,false,false,false,0);
            try
            {
                if(InputReady)controls = new QuestPreviewControls(true, Pressed(x), Pressed(y), Pressed(a), Pressed(b),
                    Pressed(trigger), Pressed(menu), stick.ReadValue<Vector2>().y);
            }
            catch(Exception error) { Fail("Controller sample unavailable: " + error.Message); }
            // Even failed reads reach the router with DeviceReady=false, returning
            // release edges for previously emitted colours instead of holding them.
            var frame = router.Update(phase, questionIndex, controls);
            string hint;
            if(phase == Phase.Questionnaire)
                hint = "Left stick up/down: choose 1-5 | A: confirm\nSelected: " + (frame.CandidateRating.HasValue ? frame.CandidateRating.Value.ToString() : "--") + " (not submitted)";
            else if(phase == Phase.Paused) hint = "PAUSED | Right trigger: actively resume\nB: withdraw and end preview";
            else if(phase == Phase.NeedsProtocolDecision) hint = "Interrupted practice requires an owner protocol decision\nNo automatic retry or resume | B: withdraw";
            else if(phase == Phase.PracticeCorrection || phase == Phase.Rest) hint = "Right trigger: Continue / Ready when available\nLeft menu: pause";
            else hint = "X red | Y green | A blue | B yellow\nRelease before the next answer | Left menu: pause";
            if(view != null) view.SetHeadsetPreview(Diagnostic, hint);
            return frame;
        }

        void OnDestroy()
        {
            if(view != null) { view.SetPresentationAvailable(false); view.SetPresentationAvailabilityCheck(null); }
            if(display != null) display.displayFocusChanged -= DisplayFocusChanged;
            if(poseDriver != null) poseDriver.enabled = false;
            if(actions != null) { actions.Disable(); actions.Dispose(); }
            if(ownsLoader && manager != null)
            {
                manager.StopSubsystems(); manager.DeinitializeLoader();
            }
        }
    }
}
#endif
