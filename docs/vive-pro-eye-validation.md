# VIVE Pro Eye hardware rehearsal: software validation

This local engineering candidate is based on e0af82bc76c6906d4abf4352fb69441a0a081f3b. It adds an explicitly started hardware rehearsal and acquisition adapter, not formal apparatus admission or live two-computer WoZ. The package has no study trials, sample recording or network transmission. Device serials, eye images and pupil measurements are not retained by the adapter.

## Executed checks

The installed Unity 6000.3.25f1 toolchain compiled the Core/Runtime, Unity and Editor sources with warnings treated as errors. The final source passed 329 portable regression groups in the normal host environment, including a controlled stop/publication race. A sandbox run had 34 existing temporary-file/loopback restrictions; those failures are not represented as passes. The suite passed in the scoped host test without changing network or security settings.

The prior SDK-ray v3 player build passed 76 scene checks, 380 scene/render checks across seven synthetic states, and five harmless Windows child-environment launcher checks. It predates the stop-counter correction; the corrected source was compiled and regression tested but has not been rebuilt into that player. The fresh-cyan and retained-amber Editor renders were inspected. The opt-in CLI ABI self-check does not initialize the native client. Source inventories and binary hashes are recorded in each local build manifest; generated builds, test logs and fixtures are not published as source.

Synthetic coverage includes native ABI layout and dispatch, data-presence polarity, separate head/eye validity, SDK-derived ray normalization, coordinate rotation, clock/sequence/freshness faults, delayed native calls, nonblocking stop, restart quarantine, marker expiry and aggregate-counter partitioning. Portable tests use injected managed providers and buffers and never load the vendor DLL. Existing task, export and journal regressions remain part of the complete suite.

## Corrected implementation defects

- The installed native field named no_user is a positive data-presence byte; the adapter translates it to logical absence semantics at the boundary.
- Native version 2 is the initialization/release selector. Status queries use the installed client's separate eye-status selector 0.
- HTC's Ray overload normalizes a derived direction. The adapter preserves the raw vector and native mask, derives a separate unit ray, and retains all runtime, calibration, presence, freshness and head gates. Nonfinite, tiny/zero and overflowing inputs stay blocked. No shared Core validity threshold is relaxed.
- Quaternion mapping uses the inverse rather than the conjugate so an accepted finite-precision quaternion does not scale the gaze vector.
- Display-only amber retention holds the original world point for at most 100 ms. It never fabricates an eye sample, refreshes a sample timestamp or reprojects old gaze with a new head pose.

The conservative float-overflow guard is intentional; exact Unity behavior for enormous malformed values is not claimed. Practical-scale Ray parity and invalid-input rejection are separately checked.

## Hardware and study boundary

The v3 hardware rehearsal has been manually exercised. This public record makes no quantitative accuracy, precision, sustained validity, apparatus mapping or research acceptance claim. Private wearer observations and local operational diagnostics are retained outside version control; no raw eye samples are included in this repository.

The diagnostic marker is a direction-only intersection projected from HMD centre, not a measured eye-origin ray or fixation classifier. Native eye timestamps retain their original opaque values; local observation time is not hardware capture time. The geometric observer is a placeholder.

Real operator-to-participant transport, a VIVE classroom/task build, approved trackpad bindings, apparatus coordinate/rig calibration, provenance-aware recording and export, and two-PC timing/failure validation remain outstanding. Existing simulation or Quest tests do not establish these capabilities. The formal apparatus gate and the pending condition-design decisions are unchanged.

See [the adapter contract and completion matrix](vive-pro-eye-adapter.md) for exact scope and safe build/use instructions. No additional wearer test is scheduled by this validation record.
