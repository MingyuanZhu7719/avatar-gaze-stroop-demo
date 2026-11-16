# TODO

## Deferred: eye-tracking spatial accuracy

Deferred at the user's request on 2026-11-16 while task features and offline data analysis are developed.

- [ ] Add a head-orientation reference and deviation feedback for matched-viewpoint validation.
- [ ] Repeat fixed-target eye/cheek checks before and after HTC calibration with comparable head-relative target angles; record headset fit changes.
- [ ] Investigate persistent spatial bias and SDK calibration-state fluctuations. A Ready flag or successful calibration UI is not an accuracy pass.
- [ ] Define and validate accuracy requirements for fine eye-versus-face AOIs before research use.
- [ ] Keep native exposure/display timing validation separate from the existing software timestamps.

The acquisition, raw recording, software timing and fixed-target validation tools remain available. Fine AOI accuracy remains unresolved; do not silently correct offsets, widen AOIs, or treat missing AOIs as zero dwell. See [fixed-target validation](vive-gaze-validation.md).
