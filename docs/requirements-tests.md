# Requirements and regression map

Acceptance identifiers refer to [PRD §7](prd.md). Passing a software regression does not complete apparatus acceptance.

| Requirement | Source and executable coverage | Milestone 1 status |
|---|---|---|
| AC-01 roles/views | `Experiment.View`; tests compare both main trial IDs/word-colour pairs and practice/D/questionnaire/rest/pause visibility | Core view models covered; rendered VR and participant-avatar mapping pending |
| AC-02 owner configuration | `Allocation`, `Experiment.LockBlock`, `BeginCountdown`; unauthorized participant/operator calls, immutable assigned factors, locked source, readiness guards | Core covered; actual owner-only interface pending |
| AC-03 flow | `ExperimentTests`: 6/8 pass, 5/8→6/8 pass, 5/8→5/8 end, correction/Continue, 96-trial complete run, 20 separate ratings, fourth rest, extended rest, withdrawal, interrupted final trial | Covered with fake monotonic clock; interrupted-practice scoring is explicitly unresolved |
| AC-04 stimuli/input/RT | `TrialPlanTests` loops test all 16 pair counts, category/colour/word marginals, no identical adjacent stimulus, distinct/reproducible lists; `ExperimentTests` require onset, reject held keys, advance without 3-second wait, retain first response and reconstruct RT | Core covered; hardware input and physical display latency pending |
| AC-05 modes/source | Block source lock, simulator/apparatus separation, readiness and resumed-countdown regressions | Configuration covered; automatic trajectory, common presentation adapter, and live source pending |
| AC-06 VR faults | `FlagTechnicalIssue` records/flags without inventing automatic pause; pause visibility model tested | Actual detection, world-space warning presentation and frozen recovery policy pending |
| AC-07 data | Session/clock/block/segment/trial IDs; contiguous journal sequence; first-response timestamps; interruption null correctness/RT; separate rating items/version/language; allocation IDs distinguish different roster sizes | In-memory audit covered; persistence, cross-stream synchronization, gaze/AOI and transport data pending |
| AC-08 apparatus | Requires two independent computers/apps and two real VIVE Pro Eye headsets | Not tested; unavailable on this Mac |

`TrialPlanTests` registers eight test groups (composition, list reproducibility/distinctness, practice balance/retry, Williams position/adjacency balance, partial roster/B balance, list-to-condition variation, allocation identity, invalid indices). `ExperimentTests` registers twenty groups, with acceptance IDs in the executable output. Tests exercise public behavior and record evidence instead of duplicating implementation code. Operator-side D display is flagged provisional and is not asserted as an accepted product decision.

Independent read-only review found and prompted regression fixes for pause during either initial or resumed countdown bypassing source reacquisition, and allocation-ID reuse across roster sizes. Local execution also exposed a floating-point subtraction boundary in countdown completion; the engine compares absolute deadlines instead.

No test asserts a psychological effect, statistical significance, real hardware compatibility, or a synthetic stream being real eye tracking.

## Milestone 2 additions

`GazeControlTests` adds sixteen groups for exact/reversed B boundaries, smooth turns, quaternion/vector edge cases, pause/response-independent active time, shared presentation, source locking, raw/effective validity separation, per-axis transport masks, simulation/apparatus separation, unavailable real acquisition and the independently moving simulator. `FaultTransportTests` adds sixteen groups for strict wire round trips/corruption, packet/sample identity separation, handshake/provenance gates, order/loss/staleness, clock mapping, independent faults, latched recovery, task correspondence, synthetic 120-ppm drift, uncertainty/horizon, changing transit asymmetry and stale-capture rejection, malformed clock exchanges, and actual loopback/endpoint rejection. The complete M2 regression has **60 groups passing**.

| Acceptance | M2 evidence | Still required |
|---|---|---|
| AC-05 | Automatic reference and simulator/real-source contracts use the same avatar output; no fallback/retiming; all source identity and validity tests pass | Real VIVE acquisition, validated mapping and rendered integration |
| AC-06 | Separate head/eye/connection/freshness warning facts; strict capture-age gating and recovery-decision latch; apparatus mode blocked | World-space UI integration, approved message/side/timing and pause/recovery policy |
| AC-07 | Independent raw sample/wire sequence, named source clocks, tested offset/drift/uncertainty estimator, raw tracking preserved | Persistent cross-stream export and physical timing validation |
| AC-08 | One-Mac temporary UDP socket exchange only | Two computers/apps and two actual VIVE Pro Eye headsets |

Reconnection is deliberately not implicit. Once an established connection becomes stale, a later handshake cannot clear the development recovery latch or make cached data current. A participant performance result cannot enter the operator task packet grammar. No participant-eye transport is implemented by this operator-control channel.


## Milestone 3 additions

The full local regression has **101 passing groups**: 60 prior groups plus one numerical-clock regression, eight joint-admission clock groups, 14 data/export groups, four runtime groups, five geometry groups and nine owner-command groups. `scripts/integration.py` supplies two additional independent-process scenarios; `scripts/compile-unity.py` verifies all actual Unity C# sources against the installed managed assemblies. [Development evidence and limitations](development.md#milestone-3-integrated-simulation-applications-and-auditable-export) distinguish simulation, code compilation, hosted CI and actual apparatus acceptance.

| Acceptance | M3 implementation and executable evidence | Remaining acceptance |
|---|---|---|
| AC-01 roles/views | Independent participant/operator runtimes; integration compares every visible trial ID and word/ink pair; world-space view source; post-input refresh and captured render context; no result/RT in task packets | Actual rendered views and headset ergonomics; operator participant-avatar drive and D/non-main decisions |
| AC-02 owner configuration | Separate explicit local-file owner console; session/exclusive-lock/journal/idempotence tests; wrong-phase/live rejection, fixed allocation factors, immutable source; Unity launch watcher | Executed standalone Unity workflow and final management layout/access boundary |
| AC-03 flow | Remote safety-stop retry/ack/idempotence; paired UDP pause/new-segment reacquisition, final response to ratings, 30-second rest and next block with segment reset/source usable; earlier full 96-trial/four-rest regression retained | Real controller mapping; approved interruption/fault recovery decisions |
| AC-04 input/timing | Camera witness/current-trial acknowledgement source compiled; captured render clock distinct from participant monotonic response clock; headless acknowledgment explicitly synthetic | Actual rendering, physical onset/input timing and uncertainty measurement |
| AC-05 gaze/source | Same controller output wired to participant view; actual scene A/C head/eye ray alignment; context-specific fresh source readiness; automatic/simulator source labels; no fallback | Real VIVE head/eye adapters and validated source mapping; full headset B presentation |
| AC-06 faults | World-space warning renderer source and independent head/eye masks; injected fault integration; selected-sample context warning; stop delivery pending/ack; apparatus remains blocked | Executed VR warning presentation and accepted threshold/pause/recovery policy |
| AC-07 audit | CSV escaping/invariant numbers, partial/export failure/no-overwrite, manifest hash/row checks, raw source vs controller vs applied renderer pose, participant roles/AOI validity/D NA, revisioned clock map beyond64 observations, source digest and software identity | Real measurement streams, actual render observations, continuous crash-resilient session persistence and physical synchronization validation |
| AC-08 apparatus | Actual separate processes and sockets on one Mac; SDK-style projects plus hosted Linux/Windows workflow | Two lab PCs with two real VIVE Pro Eye headsets; Unity player builds and hardware performance |

Independent review prompted regression fixes for source-ready context, operator stop delivery, target geometry, stale pre-input render audit context, next-block segment reset after a pause, continued clock audit after its bounded observation window fills, and a one-machine-step false-unavailable clock intersection. The numerical-clock regression keeps known true affine time inside the reported uncertainty and continues to reject stale captures. `ClockBoundaryTests` adds joint reply admission, thirty successive tight-transit replies, quality ambiguity without narrowing causal bounds, rejected-window preservation, zero-processing and nonunit bootstrap, bounded-window admission and latched rollback. Reconnect does not clear the recovery-decision latch; no test claims automatic recovery is scientifically approved.

The subsequent Windows UDP disconnect correction adds eight transport/monitor groups and five runtime groups. Injected poll/receive/send resets close the socket once and prevent later I/O; unrelated socket/programming exceptions propagate. Explicit faults immediately latch recovery before or after handshake, invalidate effective task/head-eye input while retaining raw diagnostics, and do not select pause/fallback/resume. Runtime tests cover pending stop acknowledgements, failed sample-send accounting, later diagnostic traffic and same-tick participant broadcast failure after pose evaluation. These extend AC-05/06/07 software evidence; platform-specific hosted integration and actual headset acceptance remain separate checks.

## Milestone 4 additions

`JournalTests`, `JournalCaptureTests` and `JournalRuntimeTests` add **18 portable groups**, which passed in an isolated M4 harness; the publishing task subsequently ran the full **132-group regression with zero failures**. They extend AC-07 with bounded continuous recording, mutable-trial observations, source-acquisition/receiver separation, exact provenance and offline prefix extraction. Injected write/flush and socket failures check the acknowledged boundary and prevent fabricated completion, delivery or recovery. The publishing task also executed `scripts/integration.py` successfully: all four normal role journals passed independent framing/hash/field checks, and forced termination of both roles preserved acknowledged prefixes and original bytes with matching raw sample identities. [Development evidence](development.md#milestone-4-continuous-simulation-recording-and-offline-prefix-extraction) distinguishes these local process results from exact-commit hosted CI and apparatus acceptance.

AC-06 includes sticky development recording warnings in both role adapters; these have compilation coverage and do not establish executed VR presentation. Real measurement, physical synchronization/storage behavior, headset-render observations, study storage/access/retention rules and accepted fault/recovery policy remain outside this milestone's verified scope. A clean recorder close or finalized extraction is never used as proof of study completion.
