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
This does not guarantee any native data rate. Native callback acquisition is
described below; cadence must still be checked in each new hardware run.

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
individual native status/device/calibration return brackets, the raw calibration
result/needed output, and mode-specific callback/read timing. These use the
task's same `IClock`; separately paired Unity clock values are recorded with the
AOI association. Neither clock pairing establishes the meaning of the opaque
native timestamp.

The FIFO holds 2,048 records. Overflow faults recording admission instead of
silently discarding the oldest record. Identity deduplication is bounded at
262,144 distinct frame/timestamp pairs per admitted run; reaching that budget
also faults admission. Consent/revocation and graceful terminal drain use the
same run boundary as status evidence. Pre-admission eye observations are not
recorded. Normal completion first quiesces and drains callback acquisition, as
specified below; withdrawal remains an immediate admission revocation.

## Native callback mode and completion

The installed provider uses `RegisterEyeDataCallback_v2` instead of obtaining
each eye sample through a blocking `GetEyeData_v2` read. The callback copies
only the allowed native gaze fields, original native frame/timestamp, local
receipt time, and the latest completed health report into a bounded in-memory
FIFO. It calls no Unity, disk, task, or native health API. A separate worker
drains the FIFO, using bounded bursts when it has a backlog; the ordinary poll
delay does not impose one delay per queued callback during each burst.

The archival FIFO and the live task snapshot serve different purposes. An
archived callback retains the health report known at its receipt time. The
live task snapshot must also agree with the current health state, so an old
Ready packet cannot hide a newer Required/error report. Repeated equivalent
Ready health queries do not manufacture a missing-eye interval merely because
the immutable health object changed. Both health observations must be fresh.
No historical callback receives a synthetic simultaneous head pose or task/AOI.

| Actual CSV columns | Interpretation |
|---|---|
| `eye_capture_mode` | `native_callback` for callback capture; `native_poll` for the earlier polling path. |
| `eye_native_result_source` | In callback mode, the generic native result comes from `callback_registration`, not an eye-read call. |
| `eye_callback_registration_result` | Actual registration result; no `GetEyeData_v2` success code is invented. |
| `eye_callback_observed` / `eye_eye_observed` | Original shared-clock callback receipt observation, retained when queued. |
| `eye_poll_started`, `eye_poll_returned` | Provider-operation bracket: callback dequeue plus any due health queries in callback mode; health queries plus native read in polling mode. |
| `eye_health_query_started`, `eye_health_query_completed` | Completed status/device/calibration query bracket associated with the callback. |
| `eye_health_age_at_callback_seconds` | Callback receipt minus health-query completion. |
| `eye_callback_to_dequeue_seconds` | Worker dequeue completion minus callback receipt; inspect backlog/latency. |

In callback mode, `eye_read_started`, `eye_read_returned`, and `eye_read_result`
are absent. Local callback receipt is still **not hardware capture time**.
Assess rates from unique native identities and original receipt times, not
dequeue cadence; a burst that drains queued data does not increase the sensor
rate or create additional samples.

Normal completion uses an asynchronous barrier. The owner requests completion
without waiting for native code. The worker unregisters the callback outside
queue/task locks, continuing to accept callbacks until successful unregister
and quiescence. It retains and drains the provider FIFO into the authorized
worker/writer pipeline before allowing the final seal. While completion is
pending, the owner continues draining worker records. Callbacks received after
the last task event but before this barrier may therefore be present without
invented task alignment. A failed unregister, uncertain quiescence, or queue
overflow is a fault, not a successful normal terminal record. Withdrawal/stop
use their separate immediate revocation path; they do not publish a buffered
tail after revocation.

The pinned native implementation protects invocation and unregister with the
same v2 callback-list mutex. Delegate and native resources remain alive until
quiescence; uncertain ownership is quarantined for the process lifetime. A
normal-completion test must verify that callbacks arriving during unregister
and all already-queued callbacks are counted before the terminal record.

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

Acquisitions retain the calibration call's own result code and successful
boolean output with its shared-clock query bracket. Failed calls retain an
unknown boolean. In callback mode health is queried after a nominal 100 ms
interval from the preceding query's completion; query duration and scheduling
add to the interval. Callback samples share that most recent completed report,
with its age preserved. A report older than 250 ms is unavailable for live
eligibility. This is a sampling/freshness policy, not evidence that the physical
calibration stayed constant between queries.

Calibration-required reports are neither smoothed into ready nor suppressed.
A lower count of Required transitions after switching from polling to 100 ms
health queries **does not establish that calibration fluctuation was fixed**.
Compare query times, raw successful `eye_calibration_needed` values, their result
codes, report age, and the callback channel-validity waveform. Short transitions
between health queries may be unobserved. `ReportedReady` remains an SDK report,
not an accuracy validation. The native integer timestamp remains opaque; these
brackets do not establish sensor exposure or photon onset.

The earlier recording cannot distinguish all vendor/runtime causes of the
calibration changes. The new hardware test must compare raw calibration output,
API durations, validity masks and sample gaps; blink, headset removal, or poor
physical calibration must not be inferred from that flag alone.

## AOI geometry and software-time association

The CSV contains two distinct sample streams. `eye_acquisition` retains the
worker's unique native frame/timestamp pairs, including invalid quality.
`observation` is the subset admitted with a current Unity head/input/task
observation. They can contain the **same native sample**: do not concatenate and
count them as two sensor samples. Use `(eye_native_frame, eye_native_timestamp)` within
one session to link them. Only an admitted observation can carry an AOI
attachment; an unmatched acquisition has **missing AOI**, never zero dwell or
an inferred face/task assignment. Do not forward-fill AOIs onto that stream.

`eye_aoi_policy` identifies `vive-classroom-exclusive-render-surface-proxies-v2`. Its
categories are exclusive and restricted to `MainTask`:

| Category | Analytic geometry |
|---|---|
| `task` | Front of the actual dark writing-area cube, a fixed 1.38 m by 1.08 m area in the present layout; not the changing bounds of individual word glyphs. |
| `eyes` | Actual transformed white-eye and pupil sphere/ellipsoid proxies. |
| `remaining_face` | Front (+Z in head-local coordinates) hemisphere of the head ellipsoid, excluding a nearer eye proxy. |
| `other` | A valid ray hitting none of those surfaces, or a nearer back-of-head/back-of-board surface. |
| `invalid` | Unusable combined origin/direction, missing/stale scene association, unavailable presentation, or invalid transform/time evidence. Consult `eye_aoi_reason`. |
| `outside_task` | Practice, questionnaire, rest, pause, and other non-main-task phases. |

Intersections choose the nearest positive distance among these declared
surfaces, preserving eye/remaining-face exclusivity and task/head occlusion.
These are analytic proxies of the primitive classroom geometry, not a
triangle-accurate or full-scene occlusion test. Other objects, material overlays,
the headset optics, and gaze accuracy still require apparatus validation.
The `eye_aoi_geometry_0` through `eye_aoi_geometry_5` names are respectively `task`,
`head`, `left_eye`, `right_eye`, `left_pupil`, and `right_pupil`. Each stores
enabled state, Unity world position in metres, quaternion rotation, and scale.
Non-positive or sheared transforms are rejected for classification. The
snapshot never creates physics colliders or changes task presentation.

In D/no-avatar, `eye_aoi_active_task=true` and `eye_aoi_avatar_applicable=false`; eye/face
outcomes are **structurally not applicable**, not evidence of zero social gaze.
The task AOI remains usable. A physically invalid ray is `invalid`, not `other`.
There is no camera-forward or camera-origin fallback for missing eye data.

The scene is intentionally masked at the start of every recording update for
safety. AOI geometry and renderer eligibility are now copied inside the actual
participant-camera **completed-render callback**, after matching the pre-cull
task/avatar revisions. The next update uses that immutable scene together with
the current Unity camera pose and linked eye ray. No safety guard is moved or
bypassed. These separate times are retained:

| CSV fields | Meaning |
|---|---|
| `eye_eye_observed` | Original local callback-receipt or polling read-return time, according to capture mode; not sensor capture time. |
| `eye_aoi_scene_capture_local`, `eye_aoi_scene_capture_completed_local` | Shared-clock bracket around the scene snapshot taken inside the completed-render callback. |
| `eye_aoi_scene_unity_seconds`, `eye_aoi_scene_unity_frame` | Unity time/frame associated with that scene snapshot. |
| `eye_aoi_capture_local`, `eye_aoi_capture_completed_local` | Shared-clock bracket around current camera-pose/ray projection. |
| `eye_aoi_unity_seconds`, `eye_aoi_unity_frame` | Unity time/frame associated with the pose/ray observation. |
| `eye_aoi_eye_to_scene_seconds`, `eye_aoi_eye_to_pose_seconds` | Signed software-time differences from the eye receipt/read; inspect their distribution. |
| `eye_aoi_scene_to_pose_seconds`, `eye_aoi_scene_frame_age` | Scene-to-current-pose age in software seconds and Unity frames; no interpolation is performed. |
| `eye_aoi_last_render_frame`, `eye_aoi_last_render_unity_seconds` | Last completed participant-camera render proxy; absent remains -1/null. |
| `eye_aoi_task_revision`, `eye_aoi_avatar_revision`, `eye_aoi_last_render_*_revision` | Scene/render revision evidence; the per-frame guard can subsequently change the live avatar revision. |

The eye origin is converted from SDK millimetres with X reflection, then
transformed by the current Unity camera. The combined ray direction uses the
same X reflection and camera rotation. The attached pose was **not measured at
the native eye capture instant**. Do not describe the ray as hardware-aligned,
the clock offset columns as a validated native-clock conversion, or the render
proxy as photon onset. Only a completed scene from the current or immediately
previous Unity frame is eligible, with at most 100 ms between scene completion
and pose capture. This engineering freshness cap is not an analysis gap-filling
rule. Task phase/block/trial and avatar applicability must still match. Skipped
render intervals, older scenes, and changed trial identities are invalid.

The first hardware test of policy v1 found that every main-task AOI was marked
`presentation_unavailable`. Its before-mask snapshot called the live readiness
predicate before the new update had read the head pose. That predicate correctly
requires `headReadyFrame == Time.frameCount`, so it was false at that point even
when the preceding frame had rendered successfully. The v2 completed-render
snapshot fixes this ordering error. It retains real render evidence instead of
forcing visibility true or weakening head tracking. Existing v1 files remain
readable with their original policy/meaning labels; their invalid AOIs are not
retrospectively changed into measurements.

Before perceived-evaluation analysis, retain raw recordings and separately
report native acquisition quality, admitted pose/AOI coverage, association
lags, and missing intervals. Restrict gaze outcomes to valid active-task
observations, use time-weighted durations with a predeclared maximum gap rule,
and report coverage against total active-task time. Do not bridge long gaps or
include questionnaire/rest time in gaze denominators. Spatial calibration and
independent validation must show whether eyes can be distinguished from nearby
face; otherwise collapse to face-level analysis. A hit on these proxies alone
is neither a fixation nor mutual gaze nor evidence that someone felt evaluated.
