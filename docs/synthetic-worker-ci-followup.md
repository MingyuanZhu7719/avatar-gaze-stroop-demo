# Synthetic worker CI follow-up

The first post-merge `main` run for `2971d0e5149cc042ba5b0a46afdf6ccb69ea3361` failed two Ubuntu assertions: **expected Rejected, actual Busy**. The Windows regression and Windows CLR4 pairing checks passed. The original [failed run](https://github.com/MingyuanZhu7719/avatar-gaze-stroop-demo/actions/runs/43174532741) is retained; this follow-up does not replace it with a retry result.

## Cause

`ViveSyntheticCommitWorker.TrySubmit` is deliberately nonblocking. It first tries to acquire its gate, returning `Busy` without inspecting or changing the submission when that gate is occupied. It examines revoked/faulted state and frame validity only after acquiring the gate. The class already documents `Busy` as a no-effect, retryable result.

The tests incorrectly assumed immediate gate availability. Invalid input wakes the worker to process its fault, while `Revoke` wakes it to cancel pending work and exit. Either can briefly hold the gate before the next owner call. A newly started idle worker can also hold it. Thus an immediate `Rejected` assertion is scheduler-dependent.

| Situation | Permitted result | Required safety outcome |
| --- | --- | --- |
| Gate is occupied | `Busy` | No admission and no fake write from this call |
| Gate is available after revoke/fault | `Rejected` | No future admission |
| Gate is available for invalid input | `Rejected` | Fault is retained; no new write |
| Admission committed before concurrent revocation | A historical `Queued` result is possible | Worker cancels pending work; this is distinct from a new admission after revoke |

The admission CAS operations cannot replace the revoked lifecycle state. No runtime defect or post-revocation admission was demonstrated by the failed assertions.

## Repair

Only `Tests/ViveSyntheticCommitWorkerTests.cs` changes executable test code:

- Terminal-result helpers retry **only `Busy`**, within an existing bounded wait. An unexpected `Queued` or acknowledgment still fails immediately.
- Empty-ack assertions similarly distinguish contention from an actual unexpected acknowledgment.
- Three held-gate regressions force the formerly racy schedule for revocation, invalid frames and invalid ordinals. They require `Busy` while held, `Rejected` after release, unchanged fake prefixes, correct terminal flags and no extra acknowledgments.
- Existing backpressure, nonblocking and reservation/CAS expectations are preserved.

An independent probe compiled the original, uninstrumented worker and repeated 100 revoked and 100 faulted schedules. All 200 observed forced `Busy` followed by `Rejected`, with **zero queued submissions and zero fake bytes written**. This proves the contract mismatch under controlled contention; it does not reconstruct the exact thread schedule of the CI machine.

Local verification before publication:

| Check | Result |
| --- | --- |
| Full ordinary regression with local UDP available | 1,835 passed, 0 failed |
| Focused original-worker suite, included in the full suite | 12 passed, 0 failed |
| Existing five reservation/CAS cases using an explicitly instrumented temporary worker copy | 5 passed, 0 failed |
| Independent controlled schedules using the unmodified worker | 200 passed; no queued admission or added fake bytes |

The initial sandbox run failed only its two loopback-delivery cases; the log was retained and inspected before the authorized normal-environment full run. Instrumented copies and probe artifacts remain local and are not substituted for production source.

## Build and acceptance scope

The runtime worker is included in normal source compilation, but it is a simulation-only component. This repair leaves it and all application files byte-for-byte unchanged. Its SHA256 remains `aa93af1e9c4f5ec0e5aae23d4c6e87ccd5ac902fda6703536828d43fdb366f75`.

The existing reviewed participant and operator packages therefore require **no rebuild**. Their application source inventories and DLL hashes remain valid. This follow-up does not establish real VIVE, two-PC LAN, eye-tracking or completed-recording acceptance.

Final local and CI results are recorded in the PR and task completion record. The 200 probe cycles are separate controlled schedules, not 200 additional test-suite cases.
