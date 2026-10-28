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

The five official packages are copied unchanged into `LocalPackages` and referenced with local `file:` dependencies. This keeps their development tests disabled unless explicitly requested; embedding packages would opt their tests in and require unrelated test dependencies. The resolved lockfile contains only these local packages and the installed Editor's built-in module dependency closure. See [Unity package test discovery](https://docs.unity3d.com/6000.1/Documentation/Manual/cus-tests.html#tests).

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
- Actual player UI, headset stereo output, tracked movement, controller operation and readable in-headset layout remain **unverified**. The earlier desktop-launch app-control approval timed out; it is not permission to launch the new preview. No alternate launch mechanism was used to bypass it.

Official API references: [Unity OpenXR input and action bindings](https://docs.unity3d.com/Packages/com.unity.xr.openxr@1.17/manual/input.html), [XR display subsystem](https://docs.unity3d.com/6000.3/Documentation/ScriptReference/XR.XRDisplaySubsystem.html), [Meta Link setup](https://developers.meta.com/vr/documentation/unity/unity-link/).
