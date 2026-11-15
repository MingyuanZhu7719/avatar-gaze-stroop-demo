# XR build prerequisites

The ordinary desktop project has no XR package dependency. Its
`ProjectSettings/ProjectVersion.txt` retains the desktop baseline. Headset builds
use a separate prepared project and the pinned **Unity 6000.3.25f1** Editor;
they do not upgrade or add packages to the checkout in place.

The shared preparation helper is named `scripts/prepare-quest-preview.py` for
historical reasons, but both Quest Link and VIVE builders use it. It copies
existing official package-cache contents into local package dependencies,
checks their versions and inventories the application source. It does not
download packages or install an Editor, driver or vendor SDK.

| Package | Version pinned by the preparation script |
|---|---|
| `com.unity.xr.openxr` | 1.17.1 |
| `com.unity.xr.management` | 4.7.0 |
| `com.unity.xr.core-utils` | 2.5.2 |
| `com.unity.inputsystem` | 1.19.0 |
| `com.unity.xr.legacyinputhelpers` | 2.1.12 |

These are the supported build inputs, not a claim that they are the newest
packages or compatible with every Unity release. An Editor/package upgrade
requires a separate dependency and API review; do not bypass the preparation
script's version checks. XR configuration and subsequent build steps use
separate Editor invocations so the selected input backend can take effect.

## Host runtime and hardware

| Target | Required setup | What it provides |
|---|---|---|
| Desktop preview | Existing supported Unity/.NET toolchain | Ordinary cameras and desktop input; no headset eye tracking. |
| Quest PC Link preview | Windows, Meta Quest Link, connected Quest headset and Touch controllers | Stereo display, tracked head and preview input. Quest does not replace VIVE eye acquisition. |
| VIVE participant/diagnostic | Windows x64, SteamVR/OpenXR, VIVE tracking/controller setup and supported installed HTC eye client | Classroom or diagnostics, with explicitly started eye acquisition. |
| VIVE WoZ operator | Its own tracked headset and eye client; explicitly configured laboratory LAN pairing | Separate operator-control source; the participant package can separately enable participant-local recording. |

The Quest route uses the host's configured Link/OpenXR setup. The VIVE launcher
validates SteamVR and selects it through the child-process `XR_RUNTIME_JSON`
environment before Unity starts. Neither builder changes the system runtime.
The installed eye-client ABI and loader restrictions are documented in the
[VIVE adapter](vive-pro-eye-adapter.md).

Android SDK/NDK/JDK, AR Foundation, XR Interaction Toolkit and a complete Meta
XR SDK are not dependencies of these Windows PC preview builders. Account,
runtime-consent and calibration setup belong to the relevant vendor tools;
the application does not perform those actions automatically.

## Prepare and build

Use new sibling workspace directories for prepared projects and outputs. Keep
earlier builds and recordings separate. For the classroom participant or eye
diagnostic, prepare the project before invoking the corresponding builder:

```text
python scripts/prepare-quest-preview.py --editor "<Unity.exe>" --project "<new-project>" --package-cache "<existing-PackageCache>"
```

Then follow [Quest preview](quest-preview.md), [VIVE adapter and build targets](vive-pro-eye-adapter.md),
or [desktop preview](desktop-preview.md). The separate WoZ operator builder
performs its own isolated-project preparation and requires an unused project,
output and log directory.

Open the generated package through its launcher. A successful build or source
inventory check does not establish visible stereo output, readable UI,
controller behavior, gaze accuracy or recording completeness. Verify those on
the actual setup, and retain session-specific evidence outside the source tree.
