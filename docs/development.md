# Development architecture and verification

This guide describes the maintained source, build entry points and verification boundaries. The [experiment design](experiment-design.md) and [PRD](prd.md) define the research requirements. Development previews, synthetic tests and a successful build do not by themselves admit a research session.

## Architecture

| Location | Responsibility |
| --- | --- |
| `Assets/Stroop/Core` | Deterministic task flow, allocation and stimuli, control sources, transport contracts, audit export and descriptive gaze summaries. No Unity or vendor SDK dependency. |
| `Assets/Stroop/Runtime` | Participant/operator applications, VIVE acquisition, recording admission, bounded writers/readers, evidence extraction and launcher contracts. |
| `Assets/Stroop/Unity` | World-space classroom, tracked rigs, owner interfaces, device input and scene/render observations. |
| `Assets/Stroop/Unity/Editor` | Scene generation, guarded build configuration and synthetic Unity fixtures. |
| `Apps` | Separate simulator, owner-console, Windows launcher, eye-probe and WoZ diagnostic entry points. |
| `Tests` | Portable managed regression cases. Unity-specific fixtures stay under the Editor directory. |
| `scripts` | Compiler discovery, isolated project preparation, build helpers and integration checks. |

The participant application owns the task, responses and participant-local records. The independent operator application supplies only its selected control stream. Automatic and live operator sources use the same avatar presentation interface; a missing live source does not silently fall back to Automatic. Participant measurement gaze is distinct from operator control gaze. Body, hand and voice synchronization are outside this implementation's scope.

The six SDK-style projects have separate purposes: `AvatarStroop.Regression`, `AvatarStroop.Simulator`, `AvatarStroop.OwnerConsole`, `AvatarStroop.DesktopPreview`, `AvatarStroop.FrameworkPreviewSmoke` and `AvatarStroop.WozLanPairingUiTests`. Windows launchers and CLR compatibility checks target .NET Framework 4.8; the portable regression, simulator and console target .NET 8. Keep these entry points when changing shared runtime code.

## Toolchain and isolated builds

The source project's `ProjectSettings/ProjectVersion.txt` retains Unity **6000.0.48f1**. The Quest/VIVE XR preparation and build helpers use the separately configured **6000.3.25f1** toolchain and existing package cache. They prepare an isolated project rather than silently upgrading the source checkout. Consult the chosen helper's `--help` and its package-specific guide before building.

`scripts/test.py` discovers an existing Unity Mono/Roslyn compiler. `STROOP_UNITY_CONTENTS` can explicitly select `Editor/Data` on Windows/Linux or `Unity.app/Contents` on macOS. An invalid explicit path fails instead of silently selecting another installation. Existing .NET 8 provides an alternative for portable tests. These helpers do not install Unity, activate a license, install the HTC SDK or change the system OpenXR runtime.

| Package or check | Entry point and guide |
| --- | --- |
| Two-role desktop simulation | `scripts/build-desktop-preview.py`; [desktop workflow](desktop-preview.md) |
| Quest Link simulation preview | `scripts/prepare-quest-preview.py`, `scripts/build-quest-preview.py`; [Quest workflow](quest-preview.md) |
| Standalone VIVE eye diagnostic | `scripts/build-vive-eye-preview.py`, `scripts/build-vive-eye-probe.py`; [adapter and ABI](vive-pro-eye-adapter.md) |
| VIVE participant classroom | `scripts/build-vive-stroop-preview.py`; [owner workflow](vive-owner-workflow.md) |
| VIVE operator application | `scripts/build-vive-woz-operator-preview.py`; [WoZ recording/data flow](woz-participant-local-recording.md) |
| Unity import, player and fixture verification | Configured isolated project with the selected package's Editor build entry points; [presentation adapter](unity-adapter.md) |

Recording capabilities require their explicit build options: controlled recording first, optional Legacy raw recording second, and optional Legacy WoZ recording third. Runtime owner preparation, separate headset choices, source readiness and recording authority still apply. Building a capable package starts no acquisition or recording. Generated source inventories and binary hashes belong with that package; an old manually maintained hash list does not describe a later source revision.

## Verification

Run the checks appropriate to the affected component from the repository root:

```text
dotnet run --project AvatarStroop.Regression.csproj --configuration Release
python scripts/integration.py --dotnet
python scripts/test_unity_tools.py
```

For the installed Unity compiler path, `python scripts/test.py` runs the portable regression. Unity compilation, asset import, scene generation, player builds and Editor fixtures use the configured isolated project with the selected package's real assembly layout and intended build symbols. A portable managed regression does not cover conditional XR paths.

The bounded WoZ runner supports `python scripts/test-public-woz.py --static-only` and managed suites using an existing compiler/reference set; see the [WoZ guide](woz-participant-local-recording.md). Python helper regressions live in `scripts/test_*.py`. The [GitHub workflow](../.github/workflows/regression.yml) runs portable regression/integration on Linux and Windows, with separate Windows Framework and pairing-UI checks. Inspect the workflow result for the exact commit being reviewed rather than carrying forward an earlier pass count.

Synthetic Unity fixtures exercise owner input, recording setup, camera callbacks and real scene transforms without a wearer. AOI checks include production B/C/A/D reveal/render/mask/reveal flow, historical rendered geometry versus current camera pose, and rejection of a fault present during the captured render. Those software checks do not measure gaze accuracy or prove headset visibility.

Nonblocking lifecycle results have distinct contracts. `Busy` can be a no-effect, retryable admission result; a failed writer `Finish()` is terminal and must not be treated as that same result. Tests force contention and cleanup schedules explicitly. Simulator completion may use validated historical terminal evidence without restoring a latched live-control source. Keep these distinctions when modifying asynchronous fixtures.

## Recording and timing

The [portable snapshot schema](data-schema.md) and [simulation journal](journal.md) are separate from native VIVE VRAW and its readable CSV export. Native callback acquisitions retain their original identity, validity flags and process receipt time. Unity observations attach separately timed camera/scene evidence and native-frame linkage. Repeated Unity observations of one native frame are not extra sensor samples. See [eye acquisition, calibration, AOI and software timing](vive-eye-acquisition-cadence.md) for processing rules and limitations.

Storage work uses bounded queues and explicit file budgets. Failed or uncertain I/O is not promoted to a successful completed output. Withdrawal can leave an incomplete retained prefix. Default local preparation allows 256 MiB source raw, an equally bounded raw stage and 256 MiB CSV; the combined file limits can reach approximately 768 MiB. These are limits, not preallocated capacity or a completion guarantee. The [self-test guide](controlled-recording-self-test-guide.txt) covers preparation, stopping and output review.

## Validation boundaries and provenance

Formal `RunKind.Apparatus` admission remains separate from development modes. Software tests, local build success, a physical single-headset check and two-computer study acceptance are different evidence. Calibration accuracy, anatomical/scene mapping, measured physical timing, storage durability and research protocol approval need their own validation. Do not infer those properties from native validity flags, a closed file or a displayed path.

Keep private recordings, machine logs, vendor binaries and generated players outside source control. Report validation with the exact revision, configuration, commands and results for that run. Earlier milestone reports and source-publication snapshots remain available in [Git history](https://github.com/MingyuanZhu7719/avatar-gaze-stroop-demo/tree/daaab2c42aed92360c9d679a9fef8c608c01cf53/docs); they are not current build inputs or a current acceptance dashboard.
