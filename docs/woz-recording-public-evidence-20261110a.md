# Current public WoZ recording evidence

This note describes the current sanitized source, not a series of historical versions. The publication base is commit b4839b138bb5534c6513aa328cd51c403d9fa447. The root manifest lists the exact overlay and all 217 production-source SHA-256 hashes.

## Changes

The cumulative update includes authenticated role-local pairing and transport, Legacy task/source ownership and controlled scene integration, the participant-local Legacy WoZ recording schema and lifecycle, operator disclosure and explicit storage acceptance, and the participant/operator development build helpers.

The final source also includes the Editor reflection-fixture argument correction made after private packaging. That correction did not change production runtime code or rebuild the already compiled private packages.

## Public checks performed

- The frozen checkpoint SHA-256 is B9EEA87F2F47B9A23E9AC26EB9094C14977C9E502152F3D6663ACB1ECFD9CFE2. All 522 checkpointed source files matched before preparation.
- All 217 sanitized production sources compiled against existing Unity/XR managed API references in ordinary-preview, Legacy-controlled and Legacy-WoZ-controlled modes. All three compiler exits were zero with empty diagnostic logs.
- 40 source/mock checks passed: 12 participant wiring, 11 operator notice, 12 build-option and 5 package-guide checks.
- 41 recording-memory, 3 recorded-acquisition and 26 original local-raw tests passed. The first combined unrecorded run had 120 passes and 1 fixture-initialization failure: `uncertain clear suppresses safety ack and replacement owner`, with `P=task_send_failed`. An unchanged scene-only rerun passed 11/11; a private 200-fresh-fixture diagnostic did not reproduce the failure. The final original-order combined diagnostic, with test-only logging of the inner transport reason, had 121 passes and 0 failures (exit 0). The initial failure remains unexplained and these results must not be described as a completely green combined regression.
- Five former machine-specific fixture path values are empty. All three fixture-root helpers reject an empty root before path combination or filesystem work. Both single-directory native fixture factories retain explicit empty-allowlist rejection. No public/general path replaces these values.
- All other production changes match the frozen source, with only remote-matching line-ending preservation where applicable. No generated build output or raw results are included.

The portable bounded entry point is scripts/test-public-woz.py. Use --static-only for source/mock tests. The managed memory suites require an existing Unity Mono compiler and .NET Framework 4.8-compatible references. Windows references are located through SystemRoot, or supply --framework-references explicitly. Cross-platform managed execution has not been validated. The runner creates a unique output directory and does not overwrite earlier checks.

Production managed API compilation used existing locally built Unity package references. Those references, compiler outputs and local response files are not published. This compile-only result is neither a redistributable player package nor a reproduction of the private player binaries.

## Separate historical private evidence

Before public preparation, the private current package evidence recorded 217 production-source API compilations in three modes, 41 recording-memory tests, 3 acquisition tests, 121 unrecorded regressions, 26 original local-raw regressions and 4 fixed native synthetic scenarios. Participant and operator packages were compiled. A later pure Editor reflection check passed 35 assertions after the test-only correction. These are separate private checks, not claims that public preparation replayed native fixtures or rebuilt players.

## Not established

No Unity Editor, player, SDK, native recording fixture, network/socket test, physical LAN, HMD or real-user capture was launched during public preparation. The full general repository regression suite was not run as part of these bounded checks. CI status must be reported separately for the actual published commit.

Physical two-PC operation, headset visibility/alignment, real tracking validity, gaze accuracy, complete sampling, synchronized clocks, end-to-end timing, scientific validity and durable storage remain unaccepted. There is no remote operator cleanup acknowledgment claim. Stopped raw prefixes do not authorize normal completed-session CSV export.

See [participant-local recording boundaries](woz-participant-local-recording.md) for explicit participant/operator consent and data flow.

