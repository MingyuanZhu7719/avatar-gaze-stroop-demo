# Desktop simulation preview

Open **`AvatarStroopPreview.exe`** from the complete Windows preview folder. Keep its `Participant` and `Operator` subfolders beside it. This is a separate **owner control window** using the installed Windows .NET Framework; it is not a participant mode selector and is not a VR application.

1. Click **Open desktop preview** (the window uses Chinese labels). It opens the two role windows with a fresh simulation session, journals and separate loopback ports. Both processes must bind before the startup barrier releases; no source or practice starts automatically.
2. Click **Begin / retry practice** in the owner window. In the participant window, answer the ink colour: **1 red, 2 green, 3 blue, 4 yellow**. Release the key before the next response. Space continues after practice correction.
3. After practice, inspect the assigned condition, explicitly choose **Automatic** or **independent simulated head/eye signals**, then lock and confirm the block. The selector has no default and resets at each new block. Real WoZ is unavailable.
4. Answer ratings using 1–5. Space means Ready only after the actual minimum rest. P pauses; R actively resumes in the participant window. The operator window's P requests safety stop. Escape withdraws.
5. The owner may export the actual synthetic records, then request normal closure of the two windows. The owner window does not force-kill processes or close unrelated applications.

The existing development fixture is clearly disclosed before launch: BCAD order, B starts toward the participant, English questionnaire, one-second countdown, seed 20261028. These are the pre-existing simulation fixtures, not newly selected study defaults. Practice remains eight trials and at most one retry; main task remains 96 planned trials. The avatar is intentionally hidden during practice, questionnaire, rest and D. No automatic responses or skipped phases are added.

Launching a participant executable alone now gives an explicit message explaining that it is waiting for the separate owner controls. It does not pretend to be running an experiment. The owner window uses acknowledged, idempotent commands through the existing local file interface; it cannot override allocation, enable real apparatus, change source mid-block, or remotely resume. Pending/uncertain commands are not automatically retried. Session/sequence checks and a three-second UI freshness check disable controls on stale status or either process exiting; this UI diagnostic timeout does not change experimental fault policy.

Synthetic logs and journals are kept in a new `sessions/preview-…` folder beside the launcher. Exported CSV bundles use the participant host's existing `exports` directory. Keep this local development directory under the owner's control; it is not production authentication or a research data-management system.

## Build with already installed tools

Prepare a disposable project copy containing the exact current `Assets/Stroop` sources and package manifest. The source baseline remains Unity 6000.0.48f1; the executed Windows desktop check uses installed 6000.6.7f1 in that copy. No XR package is introduced.

```text
python scripts/build-desktop-preview.py --editor "C:\Program Files\Unity\Hub\Editor\6000.6.7f1\Editor\Unity.exe" --project "C:\absolute\disposable-project"
```

Output defaults to `build/desktop-preview`. The script rejects mismatched player/launcher sources and package manifests before building. Add `--check-only` to validate consistency without launching the Editor. It creates two Windows x64 Mono players and compiles the owner window with installed Unity Mono/Roslyn plus Windows Forms. No tool download, license activation or system runtime selection is performed.

## Verification on RICHARD, 28 October 2026

- 148 portable C# regression groups passed (including ten new startup/status contract groups).
- Managed Unity compilation passed against installed 6000.6.7f1 assemblies: 18 core/runtime, five Unity runtime and two Editor sources, warnings as errors.
- Independent-process simulation, safety-stop, CSV/journal checks and forced-termination recovery passed.
- Both final Unity player builds succeeded with zero errors and zero warnings. The separate Windows Forms owner executable compiled with warnings as errors. The package source consistency check passed after refreshing two stale files in the disposable copy.
- Independent review corrected operator-exit control gating, status-text height, atomic startup-ready publication and mixed-source build prevention.

**Actual UI acceptance is still pending.** The attempted Computer Use launch of `AvatarStroopPreview.exe` returned **“Computer Use app approval timed out.”** A subsequent read-only window inventory showed no preview window. No approval dialog was operated, the action was not retried through another mechanism, and no actual screenshot or button-interaction pass is claimed. The application-specific automation permission is separate from Unity licensing and from running the executable manually.

The new direct-launch path is implemented and built, but the test counts and successful builds do not certify visible typography, camera render acknowledgements, actual keyboard interaction, or headset output. `RunKind.Apparatus` remains blocked. See [XR preview prerequisites](xr-preview-prerequisites.md) before choosing a headset integration path.
