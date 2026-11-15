# Avatar gaze and Stroop demo

Unity development software for a VR Stroop task with automatic or live operator-controlled avatar gaze, participant-local eye tracking, and post-block ratings.

Start with the [VIVE owner workflow](docs/vive-owner-workflow.md) to run a built package, or the [development guide](docs/development.md) to build and test the source. The [documentation index](docs/README.md) separates operating instructions, implementation details, and research requirements.

## Current capabilities

| Component | Current scope |
| --- | --- |
| VIVE participant | Classroom presentation, one VIVE wand, practice, Legacy A/B/C/D and Named multi-block development plans, automatic avatar control, questionnaires, and explicit local recording. |
| Eye acquisition | Installed SRanipal native callbacks, a bounded acquisition queue independent of Unity updates, channel-validity and calibration-query evidence, and graceful completion draining. |
| Gaze analysis records | Raw native frame identity and software timestamps; admitted observations add head pose, completed-render scene geometry and exclusive task/eyes/remaining-face AOI proxies. |
| Live WoZ | Separate participant and operator applications, explicit pairing and pose forwarding, and an optional participant-local recording build. Physical two-PC acceptance remains separate. |
| Other development tools | Quest PC Link preview, desktop preview, independent-process simulation, owner console, and read-only journal analysis. |

The local VIVE Automatic recording flow has been exercised in development. Eye/face localization accuracy, calibration-state fluctuations, hardware exposure/display synchronization, and two-physical-PC WoZ still require dedicated validation. This is development software, not an accepted research apparatus.

The [experiment design](docs/experiment-design.md) defines the research baseline. The [PRD](docs/prd.md) records software requirements and subsequent user-approved clarifications. Development build defaults do not replace a frozen study protocol.

## Run a VIVE package

1. Open the package's outer `AvatarStroopViveEyePreview.exe` launcher and follow its `START-HERE.txt`. Keep exactly one VIVE wand connected.
2. Choose **Preview (no files)** or **Record locally**. Recording availability depends on the explicit build options.
3. For a local self-test, choose the design, complete any required block/list choices, review the new session folder, and select **Prepare raw + CSV and arm recording + Practice**.
4. Select **Start classroom view**, complete the two separate headset recording choices, and release the controls. Practice starts once the current readiness checks pass.
5. After practice, use the displayed source confirmation action, such as **Confirm Automatic**, then **Begin first block**.
6. After normal completion and file closure, use **Open CSV** to open the session CSV in the Windows default application.

The fixed trackpad mapping is up/red, right/green, down/blue, left/yellow. In questionnaires, up/down changes the rating and trigger confirms. The [self-test guide](docs/controlled-recording-self-test-guide.txt) covers setup, withdrawal checks, output limits and completed-session verification. The [WoZ guide](docs/woz-participant-local-recording.md) covers the separate two-PC path.

## Build and test

VIVE/Quest Windows packages use a marked isolated Unity project and locally available packages. Follow [Development](docs/development.md) and its linked package instructions for preparation and build commands; running a build does not launch a headset session or start recording. Vendor SDKs and runtime installation are external prerequisites.

Portable regression with an existing .NET 8 SDK:

```sh
dotnet run --project AvatarStroop.Regression.csproj --configuration Release
python -B -m unittest discover -s scripts -p "test_*.py"
```

With an installed Unity Mono toolchain, `python scripts/test.py` is an alternative portable test runner. Set `STROOP_UNITY_CONTENTS` to the installation's `Editor/Data` directory on Windows/Linux or `Unity.app/Contents` on macOS when it is outside the discovery paths. Recording-specific suites and Unity verification commands are listed in the development guide.

[GitHub Actions](.github/workflows/regression.yml) runs portable regression on Windows and Ubuntu, independent-process simulation, and Windows Framework launcher/pairing checks. Software tests do not substitute for headset or spatial-accuracy validation.

## Repository layout

| Path | Purpose |
| --- | --- |
| `Assets/Stroop/Core/` | Experiment state, trial plans, control/transport contracts and portable data models. |
| `Assets/Stroop/Runtime/` | Eye acquisition, recording, source/clock handling, journal readers and development adapters. |
| `Assets/Stroop/Unity/` | Classroom, headset/controller integration and owner interface. `Editor/` contains build entrypoints and Unity fixtures. |
| `Apps/` | Standalone launchers, simulator host, owner console and acquisition probe. |
| `Tests/` | Portable regression, compatibility and synthetic fault/recording tests. |
| `scripts/` | Isolated-project preparation, builds, regression and integration tools. |
| `docs/` | Current operating guides, technical contracts and research requirements. |
| `Packages/`, `ProjectSettings/` | Source-project Unity configuration; isolated XR package preparation is handled by the build scripts. |

Historical-looking names are not a deprecation marker: Legacy A/B/C/D is an active task path, old journal readers preserve data compatibility, and synthetic candidates support executable regression tests.

## Recording and interpretation

Recording is opt-in. The repository contains source and documentation; player packages, private recordings, machine logs and vendor binaries stay outside version control.

Use `record_kind=eye_acquisition` for native acquisition cadence and validity coverage. `observation` can refer to the same native frame and must not be counted as an additional sensor sample. AOI records describe admitted observations and explicit software pose/render times; missing AOIs are not zero dwell. D/no-avatar makes eye/face AOIs not applicable. These proxies are not fixation or mutual-gaze classifications, and software timestamps are not measured photon onset.

See [eye acquisition, AOI and timing](docs/vive-eye-acquisition-cadence.md), [recording/focus behavior](docs/vive-focus-recording-policy.md), and [data formats](docs/README.md#data-and-implementation). Earlier milestone narratives and frozen source snapshots are available in [Git history before the repository cleanup](https://github.com/MingyuanZhu7719/avatar-gaze-stroop-demo/tree/daaab2c42aed92360c9d679a9fef8c608c01cf53); they do not describe the current checkout.
