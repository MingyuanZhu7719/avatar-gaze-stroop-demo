# Unity development presentation adapter

This adapter is a **simulation development implementation**, not an accepted apparatus configuration. It uses the locally installed **Unity 6000.0.48f1** APIs. The world-space layout, primitive avatar, camera placement, colours, font sizing and held-pose fault display all require review and headset verification. The separate participant/operator simulation applications are wired into the scene bootstrap, but their Unity scenes have not run: the ordinary Editor batch attempt exited before import because no valid Unity Editor license was available. Real-device `RunKind.Apparatus` sessions remain globally blocked.

## Integration

`AvatarStroop.Unity.WorldSpaceExperimentView` exposes:

| Entry point | Responsibility |
|---|---|
| `Initialize(Camera, Actor)` | Bind one participant or operator presentation to its own camera; create the development layout when absent. An owner role is rejected. |
| `ApplyTaskView(TaskView)` | Display the current word in its ink colour; enforce phase visibility; show participant correction, questionnaire or rest text; label provisional operator D/non-main views. |
| `ApplyAvatarOutput(AvatarOutput)` | Apply the shared controller output to the same fixed-body avatar in either automatic or incoming control mode. Reference trajectories never substitute for missing received poses. |
| `ClearAvatarOutput()` | Clear the current input association when the application has no output, rather than attributing an earlier block's input to the current frame. |
| `SetQuestionnaireLanguage(string)` | Supply the explicitly configured `en` or `zh` language before questionnaire presentation; display the approved instruction and five anchors. No language is silently chosen. |
| `SetFaults(IEnumerable<string>)` | Show caller-supplied faults in camera-relative world space. Detection, pause, source switching and recovery are outside this renderer. |
| `StimulusRendered` | Participant-only `(trialId, renderProxySeconds)` callback after a matching completed camera render. The caller must recheck current trial/phase before invoking `Experiment.MarkPresented`. |
| `AvatarPresented` | Timestamped `AvatarPresentationObservation` recording applied head/eye pose, held-channel flags, input provenance and visibility evidence at a completed camera render. |

`AvatarRoot`, `AvatarHead` and `TaskWorldPosition` are available to derive an explicit development target geometry. `AppliedPose`, `HeadHeld` and `EyesHeld` expose the actual renderer state. No body transform is written when a gaze output is applied. The eye direction is avatar-root-local; the renderer removes the current head rotation before applying child-eye rotation, avoiding double application of head motion.

The operator application is configured to display a static participant avatar labelled **“PARTICIPANT AVATAR PLACEHOLDER / Static; no reverse head / eye tracking.”** This is provisional geometry, not replication of a tracked participant. The runtime has no participant tracking adapter; missing participant measurements stay missing. Operator views contain no correctness, response, score or RT. No mode-selection control exists in the participant display.

`DevelopmentApplicationBootstrap` creates `ParticipantSimulationApplication` or `OperatorSimulationApplication` according to the scene's role. The participant bootstrap derives the automatic target directions from the actual camera, avatar head and task positions through `DevelopmentGeometry.FromLocalDirections`; these remain development scene geometry, not apparatus calibration. It supplies the configured questionnaire language, refreshes presentation after current input, clears absent outputs, forwards faults to world-space text, and connects both render callbacks to the audit trail. The two applications use separate loopback UDP endpoints; this wiring does not constitute a two-computer test.

## Render evidence and its limits

The built-in render pipeline is required for acknowledgements. `Camera.onPreCull` snapshots the trial/revision; a `StimulusRenderWitness` on the actual TextMesh observes `OnWillRenderObject` for the assigned camera; `Camera.onPostRender` checks the same frame, camera, trial and revision before emitting once per trial. Hidden, replaced, disabled and culled stimuli do not acknowledge. Operator cameras, zero-sized camera viewports, render-to-texture cameras and Scriptable Render Pipelines do not acknowledge. A render callback alone is insufficient, because it also runs when a stimulus is hidden.

The timestamp clock is `unity-realtime-since-startup`, with the explicit meaning **`camera-post-render-proxy-not-photon-onset`**. The wired stimulus callback rechecks the core's current phase and trial before calling `Experiment.MarkPresented` on the participant application's named monotonic clock. It records a same-process clock pair for the Unity render proxy and application callback observation; callback/display delay remains unmeasured, so that pair establishes no synchronization accuracy. Camera completion does not establish GPU completion, display scanout, headset wear, lack of occlusion, readable glyphs or photon onset. Those need rendered tests and physical measurement.

`AvatarPresentationObservation` snapshots the transforms actually applied, rather than relabelling the latest input sample as displayed. It captures the phase, block, segment, trial and associated output with the frame. It separately reports requested visibility, whether at least one avatar renderer was observed by this camera, input validity and provenance, and `HeadHeld`/`EyesHeld`. Output or task changes during rendering invalidate that frame's snapshot. The bootstrap passes the captured context/output and applied pose to `AvatarPresentationRecord`; it does not substitute a later input sample. These remain camera-render observations with the same proxy limitations.

Invalid channels retain the prior local transform as a **labelled provisional development display**. If a head rotates while eye input is invalid, retained eye-local rotation naturally rotates with the head; the observation records the resulting root-local eye direction. This does not select a study fault policy, invent valid samples, or pause/resume the experiment. Head and eye validity remain independent. A persistent camera-relative warning reads **“SIMULATION / NO HEADSET VALIDATION.”** Fault text uses an always-visible, stereo-macro-capable development overlay shader; dynamic font atlas textures are rebound on initialization and atlas rebuild. Three supplied fault messages are displayed at once, with an additional-fault count if needed. Warning readability, location and salience in an actual headset remain untested.

## Project and scene generation

`ProjectSettings/ProjectVersion.txt` records 6000.0.48f1. `Packages/manifest.json` references only the installed built-in IMGUI, JSON serialization and physics modules at version 1.0.0; there are no external packages or XR providers. TextMesh uses Unity's built-in `LegacyRuntime.ttf`; language-specific glyph coverage and font fallback have not been validated.

The explicit Editor action `Avatar Stroop → Create development scenes (simulation)`, or execute method `AvatarStroop.Unity.Editor.DevelopmentSceneBuilder.CreateScenes`, creates:

- `Assets/Stroop/Generated/ParticipantDevelopment.unity`
- `Assets/Stroop/Generated/OperatorDevelopment.unity`

The generator is implemented to create one role, one camera and a `DevelopmentApplicationBootstrap` in each scene, matching the separate-application architecture. It preserves existing scene files and refuses to replace dirty open scenes. Generated materials are persisted as assets. The attempted Editor execution stopped before this generator ran, so scene creation, persistence and reload remain unverified. No XR provider or hardware runtime is selected.

## Development run procedure

The following describes the implemented wiring for a future licensed Editor environment; no rendered run or player build has been verified here.

1. Open the project with Unity 6000.0.48f1 and invoke the scene-generation menu above. Use separate running applications for the two scenes, with the participant scene as the participant application's startup scene and the operator scene as the operator application's startup scene. The current transport is restricted to one host's loopback interface.
2. Match the `simulationSessionId` in both bootstrap components and use a fresh synthetic session ID for each rehearsal. The development defaults are UDP ports **44510** for participant and **44511** for operator. The simulation runtime explicitly supplies the BCAD allocation fixture, English questionnaire and one-second countdown; these are engineering fixtures, not selected study settings.
3. The owner uses the participant bootstrap's Editor context-menu commands to begin/retry practice, lock the assigned block with `ownerNextBlockSource`, confirm readiness/start the countdown, and export. For a separate desktop owner console, launch the participant host with `--stroop-owner-commands /absolute/path/session.commands`, then use the commands and receipt checks in [Development owner console](owner-console.md). The path must be a fresh owner-controlled local file. `lock automatic` and `lock simulator` select the development source; `lock live` is explicitly rejected while real acquisition is unavailable. The participant display has no mode selector.
4. Participant keyboard inputs are **1/2/3/4 → Red/Green/Blue/Yellow** for the word's ink; in the questionnaire, **1–5** select the rating. **Space** continues a practice correction or requests Ready after rest; **P** pauses; **R** requests local resumption subject to the existing source-readiness/protocol guards; **Escape** withdraws. These are desktop development mappings, not validated VR controller inputs.
5. In the operator application, **P** or the `Operator / Safety stop` context command sends a safety-stop request. The display distinguishes pending delivery from participant acknowledgement. There is no remote resume command; an unacknowledged request must not be treated as a delivered stop.
6. Owner export writes a synthetic audit snapshot under the project's `exports` directory with render/clock provenance when callbacks have actually occurred. It does not fabricate participant eye tracking, completed trials, completed sessions or headset validation.

## Verification record

Run from the repository root:

```sh
python3 scripts/compile-unity.py
```

The script reuses `scripts/test.py` compiler discovery (`STROOP_UNITY_CONTENTS` may explicitly select an existing installation). It compiles the actual core and application runtime sources, Unity adapter/bootstrap, and Editor sources as separate assemblies, using the installed Unity Mono/Roslyn compiler and real Unity managed assemblies, C# 7.3 and **warnings as errors**. This compile script uses no fake Unity API stubs and performs no package installation, Editor launch or licensing action.

On 28 October 2026, the adapter and integrated managed assemblies compiled successfully against the installed Unity assemblies; the final integration check is recorded in [Development architecture and verification](development.md). This is managed API compilation evidence only. An independent source review identified the dynamic font-atlas rebinding and missing questionnaire anchors, both corrected before the adapter compile passed.

An ordinary Editor attempt used `-batchmode -nographics -noUpm -quit` with `-executeMethod AvatarStroop.Unity.Editor.DevelopmentSceneBuilder.CreateScenes`. It **failed with exit code 1 before asset import or scene generation**. The local evidence log, `build/unity-editor-check.log`, ends with **“No valid Unity Editor license found. Please activate your license.”** No license activation, retry, package-download workaround or screenshot substitute was performed. This is a failed prerequisite check, not a successful Editor import or render test.

**Still unverified:** Editor asset import and shader compilation; generated-scene save/reload and visible text; rendered acknowledgement under actual camera culling/pause/stale-frame scenarios; standalone player builds; actual headset warning visibility and typography; XR/controller input; real operator or participant tracking; two physical computers/two VIVE Pro Eye devices; calibrated geometry, timing and synchronization. Real-device apparatus operation remains blocked. Do not mark PRD AC-06 or AC-08 as passed on managed compilation or headless simulation evidence.
