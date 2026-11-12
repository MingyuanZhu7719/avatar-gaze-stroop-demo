# Participant readiness diagnostics

This update defers initial controller ownership until the existing readiness checks pass and repairs misleading participant-owner status. The experiment protocol, input-edge semantics, native SDK, tracking origin, storage choices and recording safety gates remain unchanged.

| Display | Meaning |
| --- | --- |
| Controller: ready | This update contains an eligible existing controller poll reporting ready, with headset readiness still available. |
| Controller: not ready | This update contains a controller poll reporting unavailable or waiting for neutral input. |
| Controller: not checked | No eligible current controller poll is available. This is not a claim that the controller is disconnected. The next line distinguishes unavailable headset readiness from an input poll that has not run. |
| Last eye-client failure | The first failed eye-client report for that explicit eye-start attempt, retained through cleanup and later head-only status updates. Initialization reports retain their native error number when supplied. A new eligible explicit eye start clears the prior report. |

## Automatic left or right controller selection

Both hand action groups are registered and enabled before XR startup. The displayed Left or Right is an OpenXR logical role, not a physical serial-number identification or a manual hand selection.

Initial discovery and established identity are separate. A unique logical candidate can be inspected and routed before it is ready, but its identity is first established only after the existing focus, headset, binding, same-device and pose requirements pass. A transient untracked candidate must not permanently lock the initial role. Once an identity is established, an ID or role change remains blocked; zero or multiple candidates are not reduced to a guessed choice. Neutral/reconnect and recording protection remain unchanged.

Owner-only diagnostic details identify the logical role, transient InputSystem device IDs, action binding consistency and observed tracking bits. They do not expose serial numbers or pose vectors and do not establish physical hardware identity. The participant's VR task view does not show these development details.

The previous owner UI could continue to display old `Right ... OK` text after headset loss or cancellation of recording choices. Those paths intentionally return before reading another controller sample. The new UI expires the old display instead of adding another hardware read or consuming input edges. It also rejects cached `participant.InputReady` as proof of a current controller check.

Asynchronous eye initialization could fail on the worker thread and have its diagnostic replaced by subsequent head-only status. The owner now copies the terminal worker report before the existing cleanup path. Complete provider diagnostic text remains in memory on the local owner panel; logs use a fixed failure marker rather than copying arbitrary exception messages, device identifiers or gaze payloads. The current headset-readiness reason is shown next to the eye controls when the headset is not ready.

## Reported session and remaining limits

The original November 12 initialization attempt left a `CreateContext` entry but no result code in available logs. Its precise cause cannot be reconstructed. Later connection logs showed a replacement controller reconnecting and OpenXR regaining focus; these do not establish successful eye initialization or usable gaze.

The previously found [SteamVR tracking resets](vive-view-failure-diagnostics.md) remain a separate hardware/runtime acceptance question. Establish stable tracking, one connected controller, neutral controls and headset focus before another wearer check. Recording choices invalidated by focus or device loss remain invalid; this patch does not restore them automatically.

Validation uses isolated edit-mode fixtures: real early-return and cancelled-choice paths with stale input model state, current/cross-frame display results, and a fake asynchronous initialization failure carrying a numeric code. Synthetic Input System devices check both enabled action groups, symmetric Left/Right binding and commit paths, an unready initial candidate, multiple-device rejection, and established-role changes. The fixture supplies the missing official Haptic dependency, registers the profile's compiled PoseControl type under a private layout name, and uses a private derived controller layout with explicit pose controls and a trackpad satisfying the package's StickControl constructor. It preserves existing global Pose/Vector2 layouts. This does not change production layouts or demonstrate native-profile equivalence. Positive commits use an explicit synthetic validated-pose seam; they do not demonstrate native OpenXR focus. No physical controller or installed eye SDK is used by these fixtures. Real headset, eye accuracy and complete-session recording acceptance remain pending.
