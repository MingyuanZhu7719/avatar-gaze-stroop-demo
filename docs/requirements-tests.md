# Requirements and regression map

Acceptance identifiers refer to [PRD section 7](prd.md). This map identifies executable software coverage; the result for a revision comes from its actual test run. Physical measurements and research acceptance remain separate. Build and test entry points are in [development](development.md#verification).

| Requirement | Representative executable coverage | Remaining apparatus evidence |
| --- | --- | --- |
| AC-01 roles and views | `ExperimentTests`, runtime tests and Unity view fixtures verify task-context correspondence, role-specific visibility and response-free operator context. | Participant/operator headset presentation and agreed operator-view behavior. |
| AC-02 owner configuration | Allocation, owner-command and VIVE setup fixtures verify immutable assignments, explicit source/plan selection, role authority, blocked incomplete plans and fresh recording preparation. | Approved lab operating procedure and access controls. |
| AC-03 experiment flow | `ExperimentTests` checks practice scoring/retry, constrained blocks, presentation-dependent responses, questionnaires, minimum rest, pause/resume segments, interruptions and withdrawal. VIVE owner fixtures exercise the actual application transitions. | Complete wearer/controller flow under the frozen study protocol. |
| AC-04 stimuli, input and RT | `TrialPlanTests` checks compositions, list balance, deterministic allocation and invalid inputs. Input and render fixtures check press edges, neutral release, current-trial witnesses and first-response retention. | Physical stimulus onset, response latency and uncertainty. |
| AC-05 control sources | `GazeControlTests`, transport/source tests and raw automatic-avatar fixtures check trajectory boundaries, shared presentation, source identity/locking, raw/effective validity, no fallback, and A/B/C versus hidden D. | Validated operator-to-avatar and participant-eye mappings; physical two-device operation. |
| AC-06 faults | Transport, lifecycle, focus/readiness and recording tests distinguish stale input, latched source failure, recoverable pose holds, terminal writer failure, withdrawal and stop acknowledgment. | Approved fault thresholds/recovery policy and visible behavior in the actual apparatus. |
| AC-07 data and provenance | Data, journal, VRAW and acquisition tests check field meaning, bounds, identities, hash-chain prefixes, no-overwrite behavior, independent clocks, interrupted records, export and explicit missing/invalid observations. | Spatial accuracy, physical synchronization, completeness assessment, storage/retention policy and durability. |
| AC-08 apparatus | Portable and Unity checks exercise software contracts; separate applications and loopback integration exercise local transport. | Two lab computers/headsets, installed runtime compatibility and end-to-end performance. |

## Task and control invariants

The portable tests retain the accepted 24-trial list constraints and 96 planned main trials, independent counterbalancing factors, explicit presentation before RT, and separate questionnaire items. Versioned tutorial/exam coverage checks current-task teaching, ten-question exams with an eight-correct threshold, feedback/result boundaries and return to tutorial after failure. Historical eight-question, six-correct and bounded-retry configurations retain their compatibility tests. Interrupted observations are not silently scored as errors or replaced with successful trials. Synthetic clocks and headless presentation acknowledgments are labelled as such.

Control-source tests bind role, stream, source, device, session, block and segment. Participant measurement gaze cannot become operator control. Raw validity, effective transport usability and actual applied/held avatar output remain distinct. A connection recovery latch cannot be cleared by simply receiving another packet, and absence of live input does not select Automatic.

Clock tests retain feasible uncertainty bounds and explicit map revisions, including invalidation after a bounded observation window fills. Canonical wire-format tests include CLR numeric round-trip regressions, authentication, replay rejection and malformed values. Both WoZ peers must use the same serialization runtime/build.

## Recording and eye evidence

`JournalTests`, `JournalCaptureTests` and `JournalRuntimeTests` cover the continuous simulation journal. `scripts/integration.py` exercises separate participant/operator processes, task correspondence, export hashes and row counts, safety-stop and interrupted-journal extraction. Completion evidence cannot revive unavailable live control, and a clean export is not proof that the study session completed.

The VIVE test families cover native ABI/dispatch and callback lifetime, bounded acquisition queues, health timestamps, native-sequence identity, admission/withdrawal, transactional source and stage handling, ordered close/identity checks, raw parsing and CSV conversion. `ViveNativeAcquisitionTests` and `ViveRawEyeEvidenceTests` cover callback evidence and its serialization. Tests use injected providers and synthetic payloads; their counts are not a real sensor sample rate or a device-accuracy measurement.

`ViveEyeAoiFixture` checks transformed surface intersections, eye/remaining-face exclusivity, D applicability, invalid rays, software-time brackets and stale/mismatched scene rejection. `ViveRawAutomaticAvatarFixture` uses production classroom geometry and the actual automatic reveal/render/guard path for B/C/A/D. It requires a render-time fault to stay invalid after the warning clears, while a later per-frame mask preserves an already valid historical render snapshot. These Editor callbacks are software evidence; known-target wearer validation is still required for spatial accuracy.

Recording tests distinguish a retryable `Busy` result from a terminal failed operation. Deterministic contention schedules preserve the production contract instead of assuming an immediate worker result. Fault injection checks that unknown cleanup stays unknown, stale receipts cannot authorize another session, and invalid/absent gaze does not become a fabricated valid sample.

## Running and interpreting checks

Use the current [CI workflow](../.github/workflows/regression.yml), [build/test commands](development.md#verification), package-specific fixtures and the code affected by a change to select coverage. Report the exact commit, configuration, commands and failures. Earlier milestone pass totals remain in Git history and should not be presented as results for the current checkout.

No regression establishes a psychological effect, calibration accuracy, physical timing, study consent or research approval. The experiment-design and PRD acceptance criteria remain the authority for those requirements.
