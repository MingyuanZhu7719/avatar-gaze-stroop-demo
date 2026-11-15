# Eye acquisition cadence and calibration evidence

The 15 November local rehearsal contained 7,907 admitted eye observations over
215.831 software seconds (36.63 observations/s). That is an export observation
density, not the native sensor rate. Inspection found two software limitations:

1. `ViveProEyeWorker` previously retained only `Latest`; the owner admitted at most
   one sample per Unity update. Intermediate native frames could be overwritten.
2. On this Windows machine, 120 `ManualResetEvent.WaitOne(5)` waits measured a
   median of 15.556 ms, mean 15.830 ms, and p95 16.135 ms. The nominal 5 ms delay
   was not a reliable 5 ms delay. These measurements were made without eye
   acquisition; they do not isolate the prior run's vendor IPC cost.

The worker now uses an interruptible high-resolution Windows waitable timer.
An isolated probe using the same installed Unity Mono runtime measured 120 waits
at median 5.489 ms, mean 5.471 ms, p95 5.955 ms, with stop interruption passing.
It does not change global timer resolution. Unsupported platforms fall back to
the ordinary wait, and `UsesHighResolutionPollDelay` reports the active mode.
This does not guarantee any native data rate; provider API durations and the
native frame sequence must be checked in a new hardware run.

The separately authorized acquisition FIFO retains every distinct successful
native frame identity, including invalid directions and calibration-required
reports, independently of Unity updates. It retains no invented simultaneous
head pose or task/AOI context. Its counters distinguish eligible provider polls,
duplicate identities, native sequence gaps and sequence discontinuities.
Sequence gaps are differences between observed native frame numbers, not proof
of where or why a frame was lost. The existing admitted observations and task
authority continue to require current quality/head/input/render checks.

In the readable CSV, distinguish the record kinds before calculating rates:

| `record_kind` | Meaning |
|---|---|
| `eye_acquisition` | Unique native frames recorded by the background worker, including invalid quality/calibration-required data. Use this stream for acquisition cadence and channel-validity coverage. |
| `observation` | The subset admitted on the Unity owner thread. Optional AOI/pose/scene associations describe their recorded software times. Do not count these again as additional native frames. |
| `eye_observation` | Coalesced absence/duplicate/staleness markers. Marker count is not a lost-frame count or a time denominator. |

Acquisition timing columns include `eye_poll_started`, `eye_poll_returned`,
individual native status/device/calibration/read return brackets and the raw
calibration result/needed output. These use the task's same `IClock`; separately
paired Unity clock values are recorded with the AOI association. Neither clock
pairing establishes the meaning of the opaque native timestamp.

The FIFO holds 2,048 records. Overflow faults recording admission instead of
silently discarding the oldest record. Identity deduplication is bounded at
262,144 distinct frame/timestamp pairs per admitted run; reaching that budget
also faults admission. Consent/revocation and graceful terminal drain use the
same run boundary as status evidence. A poll entered before admission or
returned after graceful seal is outside this acquisition stream.

## Calibration interpretation and pinned native ABI

Metadata-only inspection of the installed client established:

| Binary | File version | SHA-256 |
|---|---|---|
| `SRanipal.dll` | 1.3.5.1 | `66F1FDF93988A7FA332B5F65DBB6833970A795F43149F8F2C9704640B6CADD10` |
| `ViveSR_Client.dll` | 1.3.5.1 | `B46EBE194C91151F0C4467780435CA37D778B97205CB15586787443E361E5B93` |

The pinned `IsUserNeedCalibration` export (RVA `0x8520`) forwards to
`ViveSR_GetParameterBool` using parameter 13. The client's export (RVA `0x5dd0`)
copies one byte from the IPC reply into the output buffer. Its C++ boolean ABI
is now expressed as `UnmanagedType.I1`, including the device boolean return.
The prior calibration output buffer was initialized false on each call, so
this ABI correction is **not evidence that it caused the earlier transitions**.

Every exported acquisition includes API-start/return brackets on the shared
monotonic software clock, the calibration call's own result code and successful
boolean output. Failed calls retain an unknown boolean. Calibration-required
reports are neither smoothed into ready nor suppressed. `ReportedReady` remains
an SDK report, not an accuracy validation. The native integer timestamp remains
opaque; these brackets do not establish sensor exposure or photon onset.

The earlier recording cannot distinguish all vendor/runtime causes of the
calibration changes. The new hardware test must compare raw calibration output,
API durations, validity masks and sample gaps; blink, headset removal, or poor
physical calibration must not be inferred from that flag alone.

## AOI geometry and software-time association

The CSV contains two distinct sample streams. `eye_acquisition` retains the
worker's unique native frame/timestamp pairs, including invalid quality.
`observation` is the subset admitted with a current Unity head/input/task
observation. They can contain the **same native sample**: do not concatenate and
count them as two sensor samples. Use `(native_frame, native_timestamp)` within
one session to link them. Only an admitted observation can carry an AOI
attachment; an unmatched acquisition has **missing AOI**, never zero dwell or
an inferred face/task assignment. Do not forward-fill AOIs onto that stream.

`aoi_policy` identifies `vive-classroom-exclusive-surface-proxies-v1`. Its
categories are exclusive and restricted to `MainTask`:

| Category | Analytic geometry |
|---|---|
| `task` | Front of the actual dark writing-area cube, a fixed 1.38 m by 1.08 m area in the present layout; not the changing bounds of individual word glyphs. |
| `eyes` | Actual transformed white-eye and pupil sphere/ellipsoid proxies. |
| `remaining_face` | Front (+Z in head-local coordinates) hemisphere of the head ellipsoid, excluding a nearer eye proxy. |
| `other` | A valid ray hitting none of those surfaces, or a nearer back-of-head/back-of-board surface. |
| `invalid` | Unusable combined origin/direction, missing/stale scene association, unavailable presentation, or invalid transform/time evidence. Consult `aoi_reason`. |
| `outside_task` | Practice, questionnaire, rest, pause, and other non-main-task phases. |

Intersections choose the nearest positive distance among these declared
surfaces, preserving eye/remaining-face exclusivity and task/head occlusion.
These are analytic proxies of the primitive classroom geometry, not a
triangle-accurate or full-scene occlusion test. Other objects, material overlays,
the headset optics, and gaze accuracy still require apparatus validation.
The `aoi_geometry_0` through `aoi_geometry_5` names are respectively `task`,
`head`, `left_eye`, `right_eye`, `left_pupil`, and `right_pupil`. Each stores
enabled state, Unity world position in metres, quaternion rotation, and scale.
Non-positive or sheared transforms are rejected for classification. The
snapshot never creates physics colliders or changes task presentation.

In D/no-avatar, `aoi_active_task=true` and `aoi_avatar_applicable=false`; eye/face
outcomes are **structurally not applicable**, not evidence of zero social gaze.
The task AOI remains usable. A physically invalid ray is `invalid`, not `other`.
There is no camera-forward or camera-origin fallback for missing eye data.

The scene is intentionally masked at the start of every recording update for
safety. Therefore AOI geometry is copied immediately **before** that mask,
without moving or bypassing the guard. Current Unity camera pose and the linked
eye ray are captured afterwards. These separate times are retained:

| CSV fields | Meaning |
|---|---|
| `eye_observed` | Local monotonic software time when the SDK read returned; not sensor capture time. |
| `aoi_scene_capture_local`, `aoi_scene_capture_completed_local` | Shared-clock bracket around the before-mask scene snapshot. |
| `aoi_scene_unity_seconds`, `aoi_scene_unity_frame` | Unity time/frame associated with that scene snapshot. |
| `aoi_capture_local`, `aoi_capture_completed_local` | Shared-clock bracket around current camera-pose/ray projection. |
| `aoi_unity_seconds`, `aoi_unity_frame` | Unity time/frame associated with the pose/ray observation. |
| `aoi_eye_to_scene_seconds`, `aoi_eye_to_pose_seconds` | Signed software-time differences from the eye read; inspect their distribution. |
| `aoi_last_render_frame`, `aoi_last_render_unity_seconds` | Last completed participant-camera render proxy; absent remains -1/null. |
| `aoi_task_revision`, `aoi_avatar_revision`, `aoi_last_render_*_revision` | Scene/render revision evidence; the per-frame guard can subsequently change the live avatar revision. |

The eye origin is converted from SDK millimetres with X reflection, then
transformed by the current Unity camera. The combined ray direction uses the
same X reflection and camera rotation. The attached pose was **not measured at
the native eye capture instant**. Do not describe the ray as hardware-aligned,
the clock offset columns as a validated native-clock conversion, or the render
proxy as photon onset. Scene identity must match the same Unity update and
task phase/block/trial; a failed association is explicitly invalid.

Before perceived-evaluation analysis, retain raw recordings and separately
report native acquisition quality, admitted pose/AOI coverage, association
lags, and missing intervals. Restrict gaze outcomes to valid active-task
observations, use time-weighted durations with a predeclared maximum gap rule,
and report coverage against total active-task time. Do not bridge long gaps or
include questionnaire/rest time in gaze denominators. Spatial calibration and
independent validation must show whether eyes can be distinguished from nearby
face; otherwise collapse to face-level analysis. A hit on these proxies alone
is neither a fixation nor mutual gaze nor evidence that someone felt evaluated.
