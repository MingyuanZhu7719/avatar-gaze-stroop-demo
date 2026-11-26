# VIVE Pro Eye acquisition and application integration

The Windows VIVE path connects the installed HTC eye client to the classroom
participant, the separate WoZ operator application, and an optional diagnostic
rehearsal. These are distinct build targets. Participant task input uses the
implemented VIVE trackpad mapping; controlled development builds can record
locally after separate task/eye choices. These features do not establish
apparatus accuracy or approve a study protocol.

See [owner workflow](vive-owner-workflow.md) for participant operation,
[participant-local WoZ recording](woz-participant-local-recording.md) for pairing
and storage, and [eye acquisition, calibration and AOI](vive-eye-acquisition-cadence.md)
for the recording schema and timestamp meanings.

## Native dependency and lifetime

The participant launcher reads the first SteamVR runtime directory registered in
`%LOCALAPPDATA%\openvr\openvrpaths.vrpath`. Custom drive locations, spaces and
Unicode paths are supported. A missing or invalid registration produces a launch
error; it does not silently select a different installation under Program Files.
Start the intended SteamVR installation to refresh its registration.

The selected `steamxr_win64.json` is passed only to the child player through
`XR_RUNTIME_JSON`, with `VR_OVERRIDE` pinned to the same installation for OpenVR's
internal runtime lookup. The optional controller model loader uses the same manifest's
installation for `bin/win64/openvr_api.dll`, verifies the loaded DLL path and never
rediscovers another runtime. The system OpenXR setting is unchanged. Player logs
include `VIVE_RUNTIME_SELECTION`, `VIVE_CONTROLLER_NATIVE_CONTEXT` and XR device
metadata transitions (`VIVE_XR_DEVICE_CHANGE`) to diagnose cross-machine input
failures. A model-loading error does not change task input readiness.

`SrAnipalViveProEyeProvider` locates the existing Windows x64 `SRanipal.dll`
through an explicit path override, the running SR_Runtime location, HTC's
registered installation, then the legacy default installation. Both known
eye-calibration plugin layouts are supported, including custom drive locations.
See [installation diagnostics](woz-operator-debugging.md) for the optional path
file and checks that do not initialize or capture eye data. The supported
client file version is pinned to **1.3.5.1**. Runtime-service and client-library
versions are distinct; an updated client requires an ABI review before use.
The loader uses an absolute DLL path and restricted dependency search flags.
Participant sessions keep their head/controller task input alive if native eye
initialization or polling fails. The formal block may start with missing gaze;
behavioral responses and questionnaires continue while the recording marks
eye absence and its reason. An unavailable eye sample never reuses an earlier
direction. Native failures do not silently restart the HTC client within the
session. Explicit withdrawal, invalid task/head authority and recording I/O
failures retain their existing stop handling. This policy does not replace the
operator's live control source with an automatic avatar.
Build scripts do not bundle vendor binaries, install a runtime, change the
system OpenXR selection, accept eye-data agreements, or invoke calibration.

The native v2 payload is 240 bytes. Left, right and combined eye records begin
at offsets 16, 64 and 112, with 48-byte eye records. Interop uses the Windows
calling convention; device and calibration booleans use the one-byte C++ ABI.
The exported header field named `no_user` represents positive data availability
in this client. Decode inverts it into the adapter's logical `NoUser` absence
flag; it is not a reliable physical-wear detector.

The installed provider uses native eye callbacks. It immediately copies the
allowed fields and original monotonic receipt time into a bounded FIFO. Status,
device and calibration queries run separately; each callback retains the prior
completed health report and its age. The polling implementation remains for
providers without callback support and compatibility tests. Callback results
identify the actual registration return code, not a fabricated eye-read result.

Native initialization, queries, callback draining and release belong to the
background worker. Unity consumes a current immutable eye snapshot and acquires
head pose independently. A cache read cannot refresh the eye's timestamp.
Normal recording completion unregisters callbacks and drains their tail before
sealing; withdrawal revokes admission immediately. Queue overflow or uncertain
cleanup is reported as a fault. A stopped worker must finish cleanup before
another capture can start.

## Data, validity and coordinates

| Data | Meaning |
|---|---|
| Native frame and timestamp | Original signed Int32 values. Timestamp units, epoch and wrap-to-host mapping remain opaque. |
| Eye receipt and health-query times | Process-monotonic observations with separate callback, query and dequeue brackets; not sensor exposure or photon onset. |
| Left/right/combined rays | Raw origin in millimetres, raw direction and original UInt64 mask. Origin and direction validity are independent. Pupil measurements and images are not retained. |
| Head observation | Independently timed XR quaternion and device/tracking validity; not a simultaneous head pose attached to every callback. |
| Calibration | Successful raw `NeedsCalibration` output and its result code, or unknown after failure. `ReportedReady` is not quantitative accuracy validation. |
| Operator control sample | `ViveProEyeHeadEyeSource` supplies `IHeadEyeSource` with operator-control provenance, separate from participant eye measurement. |

The operator/preview source rejects invalid masks, missing data, malformed rays,
stale or reordered observations, unavailable calibration, and invalid head pose.
Raw values remain unchanged. A separate derived unit direction follows the HTC
Unity ray convention: reflect X, normalize a finite nondegenerate direction,
then apply the declared head/reference transform. Combined gaze has no automatic
left/right fallback. Normalization does not repair a missing validity bit or
establish spatial accuracy.

Participant raw acquisition deliberately retains invalid samples as evidence.
Its `eye_acquisition` stream is independent of Unity-admitted observations and
their AOI attachment. Eye quality can make an eye observation unavailable
without stopping an otherwise valid task/head interval. Analysis must use raw
masks and the correct stream denominator, rather than treating every exported
row as usable gaze.

The classroom AOI path transforms the combined origin and direction into Unity
world coordinates and intersects the most recent completed render's immutable
geometry. It records current-pose and rendered-scene clocks separately. Task,
eyes, remaining face and other are mutually exclusive surface-proxy categories;
invalid and outside-task rows retain explicit reasons. These categories are
not fixation detection, anatomical calibration, or proof of physical display
exposure. Condition D has no applicable avatar AOI.

The diagnostic marker is only a projected direction indicator. Its brief
display continuity never alters source validity, native counts or sample times.
A previously accepted marker is not a new eye sample.

## Build targets

Use the pinned Editor and cached package preparation in
[XR prerequisites](xr-preview-prerequisites.md). Project and output directories
must be distinct from the repository and from earlier builds. Each package
records source inventories and binary hashes; those are build evidence, not
hardware acceptance.

| Target | Build entry point | Purpose |
|---|---|---|
| Classroom participant | `scripts/build-vive-stroop-preview.py` | Task/controller flow, automatic or explicitly paired WoZ control, separately enabled local recording. |
| WoZ operator | `scripts/build-vive-woz-operator-preview.py` | Separate operator headset/source, explicit forwarding permission and participant pairing. No operator recording by default. |
| Eye diagnostic rehearsal | `scripts/build-vive-eye-preview.py` | Head/eye diagnostics and marker; no experiment task or participant recording. |
| Eye-only CLI probe | `scripts/build-vive-eye-probe.py` | Explicit short native diagnostic; no XR head capture. |

For a participant build, prepare a new isolated project, then select the
recording capabilities needed for that package:

```text
python scripts/prepare-quest-preview.py --editor "<Unity.exe>" --project "<new-project>" --package-cache "<existing-PackageCache>"
python scripts/build-vive-stroop-preview.py --editor "<Unity.exe>" --project "<new-project>" --output "<new-output>" --controlled-recording-development --legacy-raw-recording-development
```

Add `--legacy-woz-recording-development` only when the participant package also
needs local WoZ recording; it requires both preceding recording flags. Omit all
recording flags for a preview-only package. The operator builder prepares its
own new isolated project:

```text
python scripts/build-vive-woz-operator-preview.py --editor "<Unity.exe>" --package-cache "<existing-PackageCache>" --project "<new-operator-project>" --output "<new-operator-output>" --logs "<new-log-directory>"
```

The diagnostic builder uses a separately prepared project:

```text
python scripts/build-vive-eye-preview.py --editor "<Unity.exe>" --project "<new-diagnostic-project>" --output "<new-diagnostic-output>"
```

Use the package-root launcher and keep its `UnityPlayer` directory beside it.
The VIVE launcher validates the installed SteamVR runtime and sets
`XR_RUNTIME_JSON` only for its new Unity child, before XR initialization. It
does not change the Windows default runtime. Opening a package does not
silently start eye acquisition or recording. The CLI probe prints usage with
no arguments; `--self-check` checks ABI layout, while `--observe-live --seconds 3`
explicitly requests an eye-only observation. A duration bound cannot cancel a
blocked vendor call.

## Verification

Synthetic interop and callback tests cover payload semantics, invalid data,
native lifetime, bounded queues and clock provenance. Unity fixtures cover
render-scene association, masking and geometry classification. See
[development verification](development.md#verification) for maintained commands.

Physical acceptance remains build- and setup-specific: verify controller flow,
unique native cadence and invalid-data fractions, calibrated target directions,
head/eye independence, tracking loss/recovery, AOI eye/face hits, and any required
two-PC clock/transport behavior. Check the completed recording and its terminal
counts before analysis. Software receipt/scene clocks do not establish a native
sensor-to-photon clock mapping.
