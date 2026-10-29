# Quest 3 PC Link development preview

This optional build adds a head-tracked participant view and Touch controller interaction to the existing simulation. **Quest 3 is a preview device. It does not replace VIVE Pro Eye, measure participant eye movements, or supply real WoZ gaze.** Avatar control remains the owner's explicitly selected automatic or independent synthetic source. The apparatus gate remains closed.

## Open the preview

1. Connect Quest 3 through the already installed Meta Quest Link application and enter Link in the headset. No system runtime switch is needed for the RICHARD configuration inspected for this work.
2. Open `AvatarStroopQuestPreview.exe` from the complete Quest preview package, keeping the `Participant` and `Operator` folders beside it. Click **Open Quest Link preview**. Only the participant application initializes OpenXR; the operator is a separate desktop application. The owner controls remain separate from both roles.
3. When head pose and both Touch controllers are available, the owner begins practice. After practice, the owner explicitly selects and locks each block's source, then confirms begin. No source or practice starts automatically.

If OpenXR initialization fails, its diagnostic is shown in the desktop mirror/owner status. Connect Link first, close the two preview processes normally, then explicitly start a new preview. This build does not silently retry initialization or change the system OpenXR runtime.

| Context | Touch operation |
|---|---|
| Answer the ink colour | X = red, Y = green, A = blue, B = yellow |
| Practice correction / Ready after rest | New right-trigger press |
| Questionnaire | Left stick up/down selects 1-5; return to centre between steps; A confirms the displayed candidate |
| Pause | Left menu button |
| While paused | Right trigger actively resumes; B withdraws and ends the preview |
| Interrupted practice awaiting a protocol decision | B withdraws; no retry or continuation is added |

Release a button before another response. A questionnaire starts with a visible, **unsubmitted** candidate of 3; it is never recorded without a new A press. A simultaneous stick change and A press changes the candidate only. Phase changes, item changes and device reconnection establish a new input baseline, so held buttons cannot skip a stage or submit the next item. Simultaneous face buttons have a documented X/Y/A/B priority and produce at most one action; other simultaneous edges are consumed.

In this headset preview, keyboard colour/rating/continue keys are disabled to avoid mixed-device double responses. PC **P** and **Escape** remain participant safety controls; they take priority over the controller action in that frame. The desktop operator's **P** still requests safety stop. Owner source selection and remote resume rules are unchanged.

The existing eight-trial practice, one allowed retry, four blocks of 24 planned trials, five separate ratings, minimum 30-second rest and explicit source locking are unchanged. The existing fixed simulation fixture is disclosed by the owner window. Practice, D, questionnaire, rest and explicit pause keep the existing avatar visibility rules. No hand/body synchronization, participant gaze measurement, locomotion or study calibration is added.

## Rendering and evidence boundaries

The participant camera uses the official XR Origin and Input System Tracked Pose Driver with position and rotation updates before rendering. A device-relative origin and 1.6 m scene offset preserve the existing development geometry; this is not an anthropometric or experimental calibration. The fixed world-space task and avatar remain in the scene as the participant moves their head. D3D11 and MultiPass are selected for this isolated preview so the existing TextMesh and overlay materials do not require single-pass shader changes.

The headset HUD labels **SIMULATED GAZE** and shows current runtime/head/controller diagnostics and controls. Lost tracking or focus makes input unavailable and resets its edge baseline. This does not add an automatic task pause, resume, source fallback or study fault-recovery policy. A desktop mirror alone cannot acknowledge presentation: the existing camera-render witness is additionally gated on available head tracking and stereo rendering. Even a successful render callback is a software observation, **not photon onset or proof that a person saw the stimulus**.

The usual synthetic journals and exports remain available. Input provenance identifies Quest Touch preview press edges and unvalidated timing; head-camera motion is not relabelled as avatar or eye data. Runtime diagnostic transitions are written to the participant player log. No formal headset latency, eye-tracking validity, two-PC timing or real-apparatus acceptance is claimed.

## Reproduce with existing tools

The ordinary project remains pinned to Unity 6000.0.48f1 with its desktop package manifest. The optional scripts create a **separate directory** using installed Unity **6000.3.25f1** and copy the already cached official packages listed in [the compatibility assessment](xr-preview-prerequisites.md). They perform no downloads, license activation or system runtime/driver changes. Package source is not vendored into this repository.

```text
python scripts/prepare-quest-preview.py --editor "C:\Unity6000.3.25f1\Editor\Unity.exe" --project "C:\workspace\quest-preview-6000.3.25f1" --package-cache "C:\existing-project\Library\PackageCache"
python scripts/build-quest-preview.py --editor "C:\Unity6000.3.25f1\Editor\Unity.exe" --project "C:\workspace\quest-preview-6000.3.25f1"
```

The isolated project must be a sibling under the source workspace parent. Output defaults to the sibling `quest-preview-players-6000.3.25f1` directory. The project records source/package metadata, uses `STROOP_XR_PREVIEW`, and enables both input backends to retain existing keyboard safety controls. An explicit source refresh is available with `--refresh-source`; stale/mixed source sets are rejected before building. Configuration and player build run in separate Editor invocations, so the input backend change takes effect. The operator has no Quest rig and automatic XR loading/running is disabled globally in this isolated project.

The five official packages are copied from the existing cache into `LocalPackages` and referenced with local `file:` dependencies. This keeps their development tests disabled unless explicitly requested; embedding packages would opt their tests in and require unrelated test dependencies. The resolved lockfile contains only these local packages and the installed Editor's built-in module dependency closure. Post-build comparison found four package trees byte-identical to their cached origins and eight `.meta` differences in the OpenXR tree. Package manifests and pinned versions still match; this is not a claim that every imported package file remained byte-identical. See [Unity package test discovery](https://docs.unity3d.com/6000.1/Documentation/Manual/cus-tests.html#tests).

The separate owner executable is compiled from `Apps/DesktopPreviewLauncher.cs` with `QUEST_PREVIEW_LAUNCHER`. The ordinary desktop launcher still builds without that define.

## Verification record

- 164 portable C# regression groups passed, including 16 Quest input groups, actual core transitions and withdrawal from interrupted-practice decision state; 11 compiler-discovery tests passed.
- Independent-process simulation, safety-stop, CSV/journal verification and forced-termination recovery passed. These processes did not initialize XR or open a player UI.
- Desktop managed compilation still passes with the XR source disabled by its compilation guard.
- Unity 6000.3.25f1 imported the five official local packages and compiled the XR-enabled project. The resolved dependency lock contains five local packages and ten installed built-in modules; no Meta SDK or package test framework was added.
- Static scene checks passed: participant has one linked camera and one valid Quest rig/driver/origin, device origin with 1.6 m offset and both stereo eyes; operator has one linked camera and no Quest rig. OpenXR automatic loading/running is off; only the participant starts it explicitly.
- Both Windows x64 Mono builds succeeded with zero errors. The participant build reports three upstream package warnings (deprecated APIs and an unused Input System field); the operator build reports zero warnings. The separate Quest owner launcher compiled with warnings as errors. Original package code was not patched to suppress warnings.
- Final source/package metadata checks passed. The ordinary desktop managed compile also passed against Unity 6000.6.7f1 (19 core/runtime, six Unity runtime and three Editor source files, warnings as errors; guarded XR files inactive).
- These are code, import, static scene and build checks. Hosted portable CI is associated with the exact publication commit separately; it does not build Unity or exercise a headset.
- The initial build checks above did not include player UI or hardware acceptance. On 2026-10-29 the user separately approved launching and operating the preview through Computer Use. The actual desktop inspection found overlapping/clipped scene text and a Windows Framework launcher polling failure; neither was detected by the earlier portable/Mono compile checks.

### Windows visual acceptance follow-up (2026-10-29)

The launcher and both role windows were opened through approved Computer Use. The initial participant mirror rendered scene text, P paused, R did not bypass the Quest resume requirement, and Escape ended the simulation. The owner refused to close while its players were alive; its close-players action closed both normally. No unrelated windows were closed.

The initial participant log identified **Meta Quest 3**, two stereo swapchain views and transitions into OpenXR `FOCUSED`. USB enumeration and Meta's current device cache also reported the headset and two controllers connected. Connection alone is not a tracking or interaction pass. Headset diagnostic logging distinguishes missing bindings, invalid tracking/pose, native action activity and focus without recording raw physical pose samples in that diagnostic.

The HMD readiness check incorrectly required native interaction-profile pose-action activity. HMD view/reference-space tracking is independent of those actions. The correction retains resolved/enabled head bindings, `isTracked`, both position/rotation tracking bits, a finite normalized pose, running display, focus checks and the stereo presentation witness; action activity remains required for Touch controls. The second real player run confirmed the failure mechanism: valid tracked head data with tracking flags 15 and both native HMD action-active values false. The corrected code reported head/Touch availability and later withheld readiness when XR focus was lost. No head pose became avatar gaze or eye-tracking data.

Windows owner builds now use the installed Windows .NET Framework references explicitly. Unity supplies the compiler, not the target framework. A direct Windows CLR console probe covers status and owner-command roundtrips, corruption, acknowledgements and restart idempotency; it must run without Mono. The original build reproduced 12 failures in its 19 checks, while the corrected references passed all 19. This guards against the missing `String.Split(char, StringSplitOptions)` overload that stopped the actual launcher.

The second UI run confirmed the owner fix. Two explicit Practice requests received separate rejections for lost XR focus, remained in Preparation and did not select a source. P paused, R did not bypass the Quest resume path, Escape ended, and Export returned an accepted acknowledgement. All 14 exported file hashes and row counts matched the manifest; the simulation was incomplete with zero trials and zero ratings. Both role windows closed normally and Open became available again. The operator layout was visually readable; the Quest participant mirror still needed stereo/mirror projection diagnosis at this checkpoint.

The third run used the same layout with bounded projection diagnostics. Its participant window showed the complete simulation banner, Preparation instructions, missing-display message and controls without overlap. However, OpenXR entered only `IDLE`, never `READY` or `FOCUSED`, and the display was not running. All sampled HUD bounds were inside the mono viewport; no running-display mirror descriptor was available. This proves readable mono fallback only. It does **not** establish that the prior XR mirror clipping is fixed or that the physical headset is disconnected. A Practice request was explicitly rejected for the missing display and no source was selected. Normal closure preserved the session logs.

`HUD_LAYOUT`, `HUD_VIEW` and `HUD_MIRROR` report at most three sample groups per stereo-state transition, two seconds apart. They record local HUD geometry, camera-relative matrix agreement, projected text bounds and mirror rectangles; they do not record physical world-space head pose. The next XR check must enter Link before starting a new preview, then compare the actual eye bounds and mirror crop before changing geometry. The unresolved XR clipping remains a known preview defect, not an accepted headset layout.

The final diagnostic player build succeeded with zero errors; the participant reports one obsolete-overload warning for `GetMirrorViewBlitDesc`, and the operator reports zero warnings. Source and isolated-copy hashes match. At the third run the USB interfaces were still started, the Meta runtime was unchanged and OVRService was running. The evidence places the missing prerequisite between the available USB/runtime and an active XR session; it does not distinguish headset sleep from being outside Link. The smallest next device step is to wake/wear Quest 3, enter or return to Link, then explicitly open a new preview.

Physical Touch responses, a deliberate head-movement check, readable in-headset output, full task/questionnaires and latency are not established by the desktop observations above.

### Wearer feedback and a farther central HUD

After the user put on Quest 3, a fourth run entered `FOCUSED` and produced stereo views. Its sampled text bounds were inside both eye viewports, while the requested left-eye desktop mirror used a source rectangle `(0.114, 0.267, 0.753, 0.448)`. The actual desktop screenshot showed the corresponding crop. These render bounds did not prove comfortable visibility through the lenses: the user explicitly reported text being cut off **inside the headset** and asked to move it farther away. That feedback takes precedence over a viewport-only check.

The Quest preview HUD is now **2.0 m** from the camera instead of 1.1 m, with its layout area intersected with a central **50-degree horizontal by 40-degree vertical** angular box. At that distance the maximum panel is approximately **1.865 by 1.456 m**. Increasing distance alone would leave the same angular footprint if the layout simply expanded to the full frustum; the angular limit prevents that. Ordinary desktop HUD distance remains 1.1 m. This changes preview instructions, labels and status only: task stimulus/avatar world geometry, experiment parameters, presentation witnesses, input gates and the existing stimulus-overlap suppression are unchanged.

The rebuilt participant and operator players each succeeded with zero errors and zero warnings; the Windows Framework probe passed 19 checks. The saved scene and runtime diagnostic both confirm a 2 m HUD root and the central angular bounds. A new desktop inspection showed all fallback text within the window, but its OpenXR session was still `IDLE` at that observation. **A brief wearer recheck is pending; this adjustment is not yet a confirmed in-headset clipping fix.** No practice or task answer was requested or submitted during these display checks. The mirror diagnostic now uses the three-argument API with the subsystem's preferred mode, instead of the obsolete overload which implicitly requests LeftEye.

Official API references: [Unity OpenXR input and action bindings](https://docs.unity3d.com/Packages/com.unity.xr.openxr@1.17/manual/input.html), [XR display subsystem](https://docs.unity3d.com/6000.3/Documentation/ScriptReference/XR.XRDisplaySubsystem.html), [Meta Link setup](https://developers.meta.com/vr/documentation/unity/unity-link/).
