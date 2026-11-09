Historical version scope: This note describes the closed-gate recording candidate. Its gate-state and validation statements are historical, not the policy of the newer optional controlled Development version. See controlled-recording-development-evidence-20261109b.md and controlled-recording-public-manifest-20261109b.json for the newer version and its untested hardware boundaries.

# Recording milestone: public source evidence

Date: 2026-11-09. This version is a closed-gate source milestone, not a recording-capable player.

## Software changes

A requested installed output that fails to start is no longer classified as source-only completion. After the existing fallback cleanup, the sink marks the destination unknown, revokes further I/O, and reports failure. Earlier uncertain cleanup remains pending. The source, raw and CSV identity lifecycles and one-shot receipt checks are also integrated.

The memory fixture exercises 8 practice trials, 24 Automatic trials, 24 No Avatar trials and 10 questionnaire responses. It checks the actual codec, strict reader, trial/rating projection and CSV conversion with fabricated gaze and timestamps. It covers corruption, truncation and closed-gate claims. Output-start failure tests construct metadata only; they do not open their synthetic placeholder directory.

## Public reproduction

On Windows with an installed Unity Mono compiler, run scripts/test-recording-milestone.ps1 and supply -UnityData pointing to that installation's Editor/Data directory when it is not in the default Unity Hub location. The runner only registers the named memory suites. It does not start Unity, a player, an SDK or a native file fixture. Its focused suite is not the complete repository regression suite. Two existing unused synthetic hook warnings (CS0649) remain visible; other warnings are errors.

The historical integration snapshot passed 134 recording memory tests and 75 focused pause/alignment/input tests. Historical results must not be presented as exact public-tree testing. Current sanitized-copy results and boundaries are recorded in recording-public-manifest-20261109a.json.

## Boundaries

All six production gates remain false: recording UI admission, sink production, sink completion, relative file capability, exclusive destination and readable CSV. Two legacy native fixture allowlists are empty with an explicit empty-string rejection. The integration fixture root is empty and its entry throws before any path resolution or filesystem access. There is no fallback directory or environment-variable override.

Native fixtures, machine-specific compilation/execution/packaging scripts, binaries, raw logs and local paths are not exported. Native results cannot be reproduced from this public source as supplied. See recording-installed-backend-evidence-20261109a.md and native-identity-resolution-20261109a.md for bounded historical summaries.

No public-tree Unity/player build, Unity bootstrap acceptance, real eye SDK delivery, physical stimulus timing, gaze accuracy, sample coverage, participant consent or durable-storage acceptance is claimed. An explicitly reviewed recording-capable build and supervised real-data reconciliation remain separate future work.
