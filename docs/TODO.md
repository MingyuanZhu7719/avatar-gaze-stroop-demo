# TODO

## Before full cloud WoZ acceptance

- [ ] Resolve or explicitly account for cloud task-proof renewal stalls under the unchanged 750 ms lease. The idle 300 ms-pose-policy probe on 2026-11-27 stopped during Countdown: the preceding task proof reached 750 ms while the replacement task had been unacknowledged for 534 ms. This is separate from pose holding; the new pose policy does not establish cloud continuity.
- [ ] Verify the explicitly approved 300 ms Photon forward-pose retention policy on the physical setup, including recorded observations beyond 150 ms and a stop beyond 300 ms. The 2026-11-27 user decision followed the old policy stopping at a 152 ms pose receipt age while its task lease was valid (508/750 ms). Ordinary round trips do not establish continuous pose availability. See [timing policy and evidence](woz-easy-pairing.md#云端时序变更依据2026-11-27).
- [ ] Run all three assigned tasks end to end on two physical PCs/Vive headsets using matching packages, then compare participant ID, allocation, control mode, layouts, answers and eye missingness in the saved files. Software fixtures are not this acceptance test.

## Deferred: eye-tracking spatial accuracy

Deferred at the user's request on 2026-11-16 while task features and offline data analysis are developed.

- [ ] Add a head-orientation reference and deviation feedback for matched-viewpoint validation.
- [ ] Repeat fixed-target eye/cheek checks before and after HTC calibration with comparable head-relative target angles; record headset fit changes.
- [ ] Investigate persistent spatial bias and SDK calibration-state fluctuations. A Ready flag or successful calibration UI is not an accuracy pass.
- [ ] Define and validate accuracy requirements for fine eye-versus-face AOIs before research use.
- [ ] Keep native exposure/display timing validation separate from the existing software timestamps.

The acquisition, raw recording, software timing and fixed-target validation tools remain available. Fine AOI accuracy remains unresolved; do not silently correct offsets, widen AOIs, or treat missing AOIs as zero dwell. See [fixed-target validation](vive-gaze-validation.md).
