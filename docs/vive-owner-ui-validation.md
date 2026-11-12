# VIVE interface validation - 12 November 2026

This update starts from `c7cf1cc38b52bf21bfe1f4b311cf8e34ac752ea2`. It preserves the experiment protocol, fixed mapping, separate task/eye choices, explicit recording start, fault handling and existing Quest path. Published source excludes real records, private logs, vendor SDK binaries and player executables.

| Check | Observed result |
| --- | --- |
| Portable C# regression on installed Unity Mono | 1,832 passed, 0 failed |
| Windows .NET Framework compatibility smoke | 65 passed, 0 failed |
| Python regressions | 84 passed, 0 failed: 70 existing build/UI/guide contracts and 14 killed-journal observer/parser cases |
| Managed Unity Editor and Windows-player conditional compilation | Passed using Unity 6000.3.25f1; warnings treated as errors except existing serialized-field warnings |
| Participant Windows package | Built successfully with optional controlled/Legacy/WoZ development capabilities; recording, eye capture, XR and network task transport default off |
| Operator Windows package | Built successfully; forwarding, capture and participant-local storage require explicit readiness/permission gates |
| Participant launcher environment checks | 5 passed, 0 failed; SteamVR override is scoped to the child process |
| Synthetic classroom rendering | 19 images generated, including conditions, questionnaire options and trigger animation states |
| Guarded synthetic owner/recording/presentation suites | 12 suites passed; Unity exited successfully. Owner flow, operator permissions, single-controller neutral/ambiguity, choices, evidence isolation, Legacy presentation/configuration, safety menu, raw-pump safety and Named Automatic/NoAvatar/multi-block presentation were exercised. |
| Actual desktop inspection | Participant window text readable on the 200% Windows display; maximize is enabled. A Windows firewall prompt prevented further click-through; no firewall permission was changed. |

The first isolated test runs could not deliver local UDP/Unity IPC through the sandbox. The authorized normal-environment regression and native builds succeeded with the existing compiler and license; no SDK installation, account change or security-setting change was required. Superseded preliminary builds and failed fixture logs were retained locally.

The expanded aggregate exposed outdated Editor test setup: a recording choice without a valid capability context, an omitted optional argument during reflection and missing English questionnaire initialization. The fixtures were corrected to match production initialization while retaining cancellation, timeout and presentation assertions. Production recording gates were not weakened to satisfy the tests. Editor-only fixture corrections do not change player code.

An initial Linux CI forced-termination check timed out; the unmodified check passed on retry. The original cause remains unconfirmed. Audit found that its observer only retained the latest acknowledgment and could miss earlier durable MainTask progress after the driver reached Rest. The harness now retains qualifying historical acknowledgments and prints bounded failure diagnostics. Process-alive, role/sequence, journal offset/context and recovered sample-identity checks remain intact. This proves interruption after acknowledged progress; it does not establish that a trial was active at the kill instant. The 14 new regressions cover delayed observation and rejection of missing, malformed or insufficient progress. Application code and the experiment protocol are unaffected by this test-only repair.

The [current C# source manifest](evidence/vive-owner-ui-source-manifest.json) identifies the published application and fixture snapshot. Historical validation notes describe their own earlier snapshots.

## What remains to verify physically

- One real VIVE wand through setup, practice, all blocks, questionnaires, rest and completion.
- Physical disconnect/reconnect and tracking loss behavior, with control release before input resumes.
- Eye-tracking accuracy and sample completeness, real two-computer WoZ behavior and a separately approved complete-recording integrity check.

Synthetic fixtures do not establish any of those physical outcomes. An eye marker, completed task or displayed path alone does not prove a complete saved session. Local raw/CSV output still requires explicit recording setup and post-close review.
