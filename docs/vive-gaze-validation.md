# Fixed-target eye validation

This is a separate engineering check in VIVE packages built with `--controlled-recording-development`. It uses the classroom board and avatar geometry, with a static participant-facing head and eyes. It does not create a study task, record Stroop answers, or establish accuracy in every moving-avatar condition.

## Run

1. Open the outer `AvatarStroopViveEyePreview.exe` launcher. Before configuring a task, select **Fixed-target eye validation**.
2. Fit the headset and complete calibration in HTC's interface. Changing the fit, lens distance or IPD afterward requires another calibration. See [HTC calibration](https://www.vive.com/us/support/vive-pro-eye/category_howto/calibrating-eye-tracking.html).
3. Select **Start headset view**. Wear the headset, face comfortably forward, and hold naturally still while the initial classroom placement settles.
4. Select **Start validation and record**. This explicitly starts the eye client and local validation capture. Follow each small target for three seconds. The first half-second of each displayed target is marked as settling time and excluded from stable-window summaries.
5. Complete two passes with your head naturally steady. At the break, select **Begin gentle head-turn passes** and complete two further passes while turning gently and continuing to look at each target. No controller answers are required.
6. Wait for capture closure and saving. **Open validation summary** opens `summary.csv`; **Open sample CSV** opens `samples.csv` using the Windows default application. Close and reopen the package for another run.

The target sequence takes about 96 seconds, plus countdowns and the break. Targets include the board, both eyes, both cheeks and background. The scene displays known targets rather than live gaze feedback. The marker has no collider and is excluded from AOI classification.

Geometry version `classroom-fixed-participant-facing-validation-v2` places the background point above the board at Unity world `(-0.5, 2.30, 1.90)` metres. Version v1 placed it far to the left at `(-1.8, 1.6, 1.95)`, which could become highly peripheral after looking at the avatar. This is a target-layout improvement, not an eye-ray correction or a claim about peripheral tracking accuracy. Marker size, fixed timing and AOI geometry remain unchanged. The geometry version is recorded in `session.txt`, and actual target positions are recorded in `targets.csv` and `samples.csv`; retain v1 runs and distinguish the versions when comparing results. Use the matching session bundle when reading `summary.csv`, which does not itself contain the layout version; do not pool v1 and v2 background rows solely by target ID.

**Stop and save incomplete run** preserves the acquired prefix with an incomplete outcome. Persistent head/focus loss during a target, reference-space changes, acquisition faults, time/memory limits or unconfirmed shutdown also prevent a completed outcome. Calibration-required samples are retained; a fluctuating calibration flag alone does not stop the target sequence.

## Files and timing

Each run has a unique folder under the package's `LocalGazeValidationData`. The format is separate from task VRAW/CSV and must not be mixed into study trials. Samples remain in bounded memory while running and are exported after capture stops; forcibly terminating the process before export can lose that run. Existing files are never overwritten.

- `samples.csv`: unique native eye identities and raw rays/masks, calibration/runtime reports and their timing, callback/publication times, separately observed head pose, target identity/position, scene geometry, AOI result, angular errors and exclusion reasons.
- `targets.csv`: target/pass identities and software display intervals, including targets with no usable samples.
- `summary.csv`: per-target/pass sample counts, eligibility and validity coverage, AOI counts, angular error/bias and dispersion summaries.
- `summary.txt`: run outcome, capture diagnostics, timing/metric definitions and interpretation notes.
- `session.txt`: published last after all other output files have been written and closed; records whether this was a completed or incomplete sequence. Its absence means export did not finish.

Native sensor timestamps remain opaque. The target onset is the first observed completed camera render, while its offset is the main-thread request to hide it; neither measures photon onset. Native callback receipt, render snapshots and camera-pose observations use the same process clock but have distinct timestamps. Delayed native samples join the target interval that contained their callback arrival, not the currently displayed target at dequeue. Stale or unmatched geometry/pose is retained with exclusion reasons.

## Interpretation

Compare steady-head and gentle-turn passes, each target's angular bias/spread, and eye-versus-face misclassification. Investigate systematic offsets, large pose/callback skew, invalid samples, native frame gaps and calibration-report transitions. Do not substitute head-forward gaze for invalid eyes. Do not treat repeated observations of a native frame as additional sensor samples.

Summaries retain a geometric diagnostic subset and a stricter SDK-ready subset, so calibration-required valid rays can be examined without being silently promoted to ready data. No automatic scientific pass is assigned: the acceptable error and confusion rate depend on the actual AOI size/separation and the planned analysis. A successful sequence/save establishes neither eye/face accuracy nor fixation or mutual-gaze detection. Quantitative conclusions require the wearer's target-following test and review of its recorded results.

Portable regression covers known-angle calculations, delayed target joins, validity exclusions, export integrity and bounded native capture/shutdown. The Unity build validates marker placement and the production primitive AOI intersection path without starting hardware. Headset testing remains separate.

The build also checks the background point against scene-mesh occlusion and a set of ordinary camera poses. Those synthetic viewport checks do not reproduce a headset's per-eye projection or establish that a wearer perceived the marker. The render witness records camera submission, not visibility through the lenses. Steady/turn pass names describe instructions; use recorded head-pose trajectories to determine how much movement actually occurred.
