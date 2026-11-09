Historical version scope: This note describes the closed-gate recording candidate. Its gate-state and validation statements are historical, not the policy of the newer optional controlled Development version. See controlled-recording-development-evidence-20261109b.md and controlled-recording-public-manifest-20261109b.json for the newer version and its untested hardware boundaries.

# Installed recording backend: historical synthetic evidence

Date: 2026-11-09. These results belong to a private integration snapshot before public sanitization. They are not executions of this public tree.

## Observed behavior

A controlled native test exercised the shared installed backend with fabricated input: task participant, recording pump, native writer, same-handle source validation, raw staging, CSV conversion, ordered identity/close operations, directory cleanup and guarded receipt factories.

The successful historical run checked 56 trials (8 practice, 24 Automatic and 24 No Avatar), 10 ratings and 270 synthetic eye rows. Source and raw-copy hashes matched. Independent CSV parsing reconciled eye rows and task fields with the strict source reader. Synthetic-only, opaque-native-time, ingest-not-sample-alignment and unknown writer-confirmation warnings were retained. Duplicate receipts were rejected and later revocation invalidated returned receipts. Durability remained false.

Three additional scenarios reached their expected fail-closed outcomes: participant withdrawal before raw identity, CSV output budget failure, and withdrawal before owner report. An earlier withdrawal test reached a fail-closed state but failed an assertion because its injection did not set the participant-intent bit. A separately named corrected test passed; the failed evidence was retained privately. It must not be counted as a successful initial test.

## Public source restrictions

The machine-bound native fixtures and their run/compile/package scripts are excluded. Their runtime entry is disabled before filesystem work, alongside the two pre-existing empty fixture allowlists. The public code does not substitute a user-selectable, relative or temporary native fixture directory. Native results are historical, not publicly reproducible as supplied, and no native check was rerun for public preparation.

The private snapshot previously passed 134 focused recording tests, 75 focused pause/alignment/input tests and a managed compilation in the 192-source actual player assembly layout. The public preparation separately recompiles the sanitized production source in that layout with private installed references; this is managed compilation only, not an exact public-repository player build. See recording-public-manifest-20261109a.json for current results.

All six production gates remain false. No Unity bootstrap or eye SDK acceptance, real gaze/trial/questionnaire session, consent validation, physical timing, complete sampling or durable storage is established. No binaries, raw logs, machine paths or diagnostic directory contents are included here.
