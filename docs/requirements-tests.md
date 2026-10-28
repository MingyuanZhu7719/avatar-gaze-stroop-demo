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
