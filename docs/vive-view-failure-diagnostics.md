# VIVE view failure diagnostics

When the participant view throws, the first failure now remains visible on the desktop after view shutdown and later cleanup. Local development logs preserve the error type and call stack; arbitrary exception messages are excluded. A new explicit, eligible view-start attempt clears the earlier diagnostic. Safety cancellation, tracking checks, masking, recording choices and explicit recording start remain unchanged.

## Reported jumping view

The November 12, 2026 headset check found two separate issues:

| Evidence | Supported conclusion |
| --- | --- |
| SteamVR reported an HMD tracking reset for an implausible gravity solution, and later another out-of-bounds pose reset after the Stroop XR session stopped. | Tracking resets occurred in the runtime; the specific physical cause is not established. |
| The latest player log did not contain an application exception; the earlier player log recorded a task-stop exception type without its origin. | Neither log establishes an application exception as the cause of the observed jumping. |
| The five initial-placement and stability source files match the preceding package byte-for-byte. | There is no source change to those algorithms in this interface update. This does not establish physical tracking correctness. |
| The prior view-update exception handler set an error and then called a stop routine that replaced the status. | The desktop could show only an off/cancelled state and hide the original failure. This diagnostic defect is repaired here. |

This update is **not a verified fix for headset jumping**. Do not start recording while tracking is unstable. An off view and cancelled recording choices are shutdown outcomes, not a diagnosis of the cause. Keep the headset off while investigating; no one needs to endure a moving view for troubleshooting.

Before a later wearer test, check base-station visibility and unobstructed headset sensors, and remove or cover reflective surfaces. HTC's [VIVE Pro Eye drift guidance](https://www.vive.com/us/support/vive-pro-eye/category_howto/what-to-do-when-seeing-drifting-objects.html) and [base-station placement guidance](https://www.vive.com/us/support/vive-pro-eye/category_howto/tips-for-setting-up-the-base-stations.html) describe these checks. A base-station setup change may require room setup again. First establish stable SteamVR tracking independently of Stroop; this step has not been performed by the diagnostic build.

## Validation scope

The edit-mode fixture injects an exception into the actual failure/stop path. It checks that the first failure survives later stop, cleanup status and secondary failures, that rejected start requests preserve it, and that logs retain a synthetic call stack without the exception message. The fixture starts no loader, eye source, task, player or output writer.

The fixture is included in `ViveOwnerUiRegressionFixture`. A successful fixture or player build does not establish headset stability, gaze accuracy, a two-PC session, or complete recording coverage. Keep earlier packages and real recording files intact.
