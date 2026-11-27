# TODO

## Before full cloud WoZ acceptance

- [ ] Resolve or explicitly define an acceptable response to received-pose gaps under Photon WSS. The 2026-11-27 synthetic cloud probe stopped at a 152 ms pose receipt age against the current 150 ms limit, while the task lease remained valid (508/750 ms). Servicing messages first did not avoid the gap. Do not equate ordinary 200–300 ms round trips with continuous pose availability or silently extend the research timing contract. See [cloud evidence](woz-easy-pairing.md#当前云端验收缺口2026-11-27).
- [ ] Run all three assigned tasks end to end on two physical PCs/Vive headsets using matching packages, then compare participant ID, allocation, control mode, layouts, answers and eye missingness in the saved files. Software fixtures are not this acceptance test.

## Deferred: eye-tracking spatial accuracy

Deferred at the user's request on 2026-11-16 while task features and offline data analysis are developed.

- [ ] Add a head-orientation reference and deviation feedback for matched-viewpoint validation.
- [ ] Repeat fixed-target eye/cheek checks before and after HTC calibration with comparable head-relative target angles; record headset fit changes.
- [ ] Investigate persistent spatial bias and SDK calibration-state fluctuations. A Ready flag or successful calibration UI is not an accuracy pass.
- [ ] Define and validate accuracy requirements for fine eye-versus-face AOIs before research use.
- [ ] Keep native exposure/display timing validation separate from the existing software timestamps.

The acquisition, raw recording, software timing and fixed-target validation tools remain available. Fine AOI accuracy remains unresolved; do not silently correct offsets, widen AOIs, or treat missing AOIs as zero dwell. See [fixed-target validation](vive-gaze-validation.md).
