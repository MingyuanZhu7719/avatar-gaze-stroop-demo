# PRD: VR Stroop Avatar Pilot

**Version: v0.1 - 2026-10-28 - User-approved development baseline**

Baseline: [Published experiment design d9db8f8](https://github.com/MingyuanZhu7719/avatar-gaze-stroop-demo/blob/d9db8f84678f435b2cf62343ccbb717c5d256868/docs/experiment-design.md). This document translates that design and the latest requirements for views in both applications, owner-only mode selection, and in-VR fault reporting into software requirements. It does not change research parameters or establish that runnable software or device validation already exists. SDK, communication, and synchronization implementations belong in a separate technical design.

**Pending protocol change:** The later request for four named experimental options - Automatic, live human WoZ, no avatar, and direct gaze at the participant's eyes - is not implemented by this English-only revision. Automatic behavior and the direct-eye targeting method remain undefined. The current A/B/C/D conditions and separate control sources below retain their existing meanings until that redesign is explicitly specified; Automatic must not silently be equated with the old alternating B condition.

**Current implementation boundary:** The development preview implements the existing four conditions with automatic reference control or explicitly simulated head/eye input. Real VIVE Pro Eye WoZ acquisition and calibrated mapping are not connected; apparatus sessions remain blocked. Participant-directed automatic gaze currently aims at a fixed declared reference station, not a measured or continuously followed eye position. Software tests, builds, and synthetic renders do not establish acceptance of real WoZ or direct eye contact.

**Local hardware candidate, 4 November 2026:** A separate [VIVE Pro Eye acquisition adapter and hardware rehearsal](vive-pro-eye-adapter.md) now reads the installed HTC runtime with explicit validity and timing provenance. It does not yet connect live acquisition to experimental WoZ transport or recording, and it does not change the apparatus gate, conditions or task bindings. Build and API-read evidence must be distinguished from valid calibrated eye output and wearer acceptance.

The automatic source executes the A/B/C reference targets. `SimulatorRehearsal` instead supplies an independent deterministic head/eye oscillator for transport and rendering checks; it does not follow or retime itself to the assigned A/B/C trajectory. A condition label with that source is therefore not evidence that the intended attention condition was performed. D visibility remains hidden for either source.

**Current preview-flow clarification (2026-11-04):** Section 16 supersedes the initial section 15 preview: confirm the control source once after practice, use a three-second animated countdown before each block, keep the questionnaire guide until A is pressed, and let A end each inter-block rest immediately when the participant is ready. The optional 30-second rest indicator is guidance, not a minimum or an automatic transition. Completion still follows the final questionnaire. New configuration and source provenance are recorded; historical sessions and baseline fixtures are preserved.

## 1. Product objective

Support a pilot whose procedure can be checked and repeated: present different avatar gaze conditions, examine differences in participant experience, and record behavior, eye tracking, and the actual presentation. **Changes in feeling observed have research value in their own right; feeling evaluated is still measured, without requiring a statistically significant effect.**

Software success means correct flow, traceable presentation and sources, visible failures, and complete records that can be reconciled. The principal research comparisons and statistical settings remain undecided. This PRD does not designate RT as the primary research endpoint, or make statistical significance, an expected direction, or a requirement that a particular condition produce more perceived evaluation a software acceptance criterion.

## 2. Roles, devices, and visible content

Use **two VIVE Pro Eye headsets, two independent computers, and two independent applications**. The operator application acquires and transmits real head rotation and eye gaze in real time; the participant application receives them and drives the operator avatar visible to the participant. Automatic mode drives the same avatar programmatically. This direction of real-time control is confirmed as a requirement, but the link has not been measured on the apparatus. Showing a participant avatar in the reverse direction does not establish a requirement to synchronize that participant's eye gaze in reverse.

| Role or interface | Confirmed responsibilities / visible content | Boundaries |
|---|---|---|
| Participant VR | Perform Stroop, respond with buttons, and provide ratings; see the task and operator avatar during the ordinary main task | No avatar in practice; D hides the operator avatar; questionnaires and rest hide it according to the baseline. Participants do not select the experimental mode |
| Operator VR | See the participant avatar and current task item; in WoZ, control their own head rotation and eye gaze according to the assigned condition and timing | The participant avatar's driving signals, pose, and extent of eye-gaze synchronization remain undecided; whether to show accuracy, response results, or RT remains undecided |
| Experiment owner | Configure the session, select the available control source, check the assigned condition, order, and B initial target, and confirm the start; in the current preview confirm once after practice and retain that source, with configuration recorded for each block | This means the people in charge of the experiment, not the participant. Mode selection must not be exposed to participants or arbitrarily override counterbalanced allocation; real WoZ remains unavailable |
| Administration screen / entry point | Provide session and flow controls, configuration checks, and data export | Screen layout, any additional VR administration entry point, and whether one person may hold multiple roles remain undecided. No new account system is assumed |

The operator-side content during practice, D, questionnaires, and rest, and the participant avatar's appearance and geometric mapping, still require interface-design decisions. The confirmed participant-side no-avatar rules remain unchanged. Do not automatically add body, hand, or voice synchronization.

## 3. Experiment flow and retained parameters

| State / stage | Required software behavior |
|---|---|
| Session preparation | Establish a study session ID; check versions, owner configuration, device connections, head/eye tracking, mapping, and matching task items across both applications; record necessary screening / consent status. Software checks do not constitute research approval |
| Familiarization and calibration | Support familiarization with all four colors, button mappings, and word meanings; check participant measurement gaze separately from operator control gaze. Calibration and validity criteria await device validation; add no hidden practice pass criteria |
| Practice | 8 trials per attempt, no participant-side avatar, 4 congruent + 4 incongruent; score only the first response and require at least 6/8 (75%) to pass; at most two attempts. After an error, show a correction and wait for an explicit Continue action; correction does not change the score. End the standardized task after two failed attempts |
| Main-task preparation | Enter the next block in the assigned order. In the updated preview, the owner selects and locks the source once after practice and confirms the first block; subsequent blocks retain that source and enter a three-second countdown after the participant continues from rest, subject to current source readiness. Record each block's condition and retained source provenance. Formal study source allocation remains undecided |
| Main-task execution | One block each of A, B, C, and D; 24 trials per block: 12 congruent + 12 incongruent, for 96 planned trials. Record and advance on the first valid new button press; holding a button must not answer multiple trials. No fixed three-second wait, participant-side correctness feedback, or unapproved response deadline |
| Post-block ratings | Hide the operator avatar from the participant; collect five separate 1-5 ratings in order: focus, distraction, feeling observed, feeling evaluated, and worry about negative evaluation. Use identical wording and order across conditions; retain the [baseline items and anchors](https://github.com/MingyuanZhu7719/avatar-gaze-stroop-demo/blob/d9db8f84678f435b2cf62343ccbb717c5d256868/docs/experiment-design.md#8-post-block-questionnaire); do not combine them into a total score |
| Rest and the next block | In the updated preview, provide neutral rest after ratings for blocks 1-3, excluding rating time. A continues whenever the participant is ready, including immediately; any displayed 30-second indicator is optional guidance. Never advance automatically or enforce a minimum. Block 4 retains all five ratings and then completes without a final rest; historical baseline fixtures retain their explicitly configured policy |
| Explicit pause / withdrawal | Retain participant pause and withdrawal and operator safety-stop controls. An explicit pause follows the baseline: hide the task and operator avatar from the participant and freeze task and active-gaze clocks; mark an interrupted trial without treating it as an incorrect response. Explicit resumption proceeds to the next planned trial without silently repeating the interrupted item. Automatic pause and reconnection recovery for tracking / network faults remain separate decisions |
| Ending and export | Distinguish normal completion, practice failure, withdrawal, and technical ending; preserve actual completion and interruption records, export under the approved data arrangements, and do not fabricate uncompleted trials |

Continue to generate items and orders according to the baseline: RED / GREEN / BLUE / YELLOW; each of the four congruent word-color pairs appears three times, and each of the twelve incongruent pairs appears once, giving six occurrences of each word and ink color. Use four constrained orders with the same multiset, avoid immediate repetition of an identical word-color stimulus, and record list IDs / seeds. Williams sequences are **ABDC, BCAD, CDBA, DACB**, with B's initial gaze target balanced independently. Do not always put D first or add trials based on performance. 48 trials per block is not the current baseline.

## 4. Two control modes in the current baseline

| Mode | Control source | Required shared elements |
|---|---|---|
| Automatic | A program source in the participant application generates the prescribed head/eye movement | The same participant-visible avatar, presentation interface, Stroop flow, questionnaires, and logging structure |
| Real head/eye WoZ | An independent operator computer / application acquires real head rotation and eye gaze from its VIVE Pro Eye and transmits them in real time for mapping in the participant application | Shared elements do not imply identical trajectories, latency, or actual execution precision; button-based target switches or prerecorded automatic trajectories must not substitute for WoZ |

- A is participant-directed, B alternates, C is task-directed, and D hides the avatar. B retains the prescribed **3.0-second dwell + 0.5-second turn, with a planned 7.0-second cycle**. Automatic mode executes the reference trajectory; the operator follows the rule, with actual deviations recorded separately.
- The operator avatar's body stays fixed. Real operator head/eye signals must not be forcibly retimed into the automatic trajectory or silently switched, blended, or replaced with the automatic source. Participant gaze, errors, or response speed must not select gaze targets or cue the operator to depart from the rule.
- Selected sources and changes must remain traceable. Switching modes during a run is not part of the confirmed flow. Two control modes do not automatically create eight research conditions.

## 5. Failures and interruptions

| Event | Confirmed software requirements | Still to be decided |
|---|---|---|
| Interrupted / invalid head or eye tracking | Report the fault clearly inside VR, not only in desktop logs; distinguish head and eye validity; record source, occurrence / detection / recovery events, and affected stages. Invalid data must not be labelled valid | Which application, or whether both, shows the warning; detection thresholds, wording / duration; automatic pause, which content is hidden / held, and the recovery flow |
| Network interruption / disconnection | Report the fault clearly inside VR and record connection state, data freshness, and fault times. Do not silently switch to automatic control or present old samples as current valid input | Timeout and reconnection criteria, automatic pause, who confirms recovery, the resumption state / trial, and treatment of data collected during the fault |
| Explicit pause / safety stop | Apply the explicit-pause requirements in section 3 and retain clocks, states, and interruption records that can be checked | Specific button mappings and confirmation interfaces belong in interface / technical design |

**An in-VR warning does not mean a pause policy has been selected.** Automatic pause and fault recovery remain undecided and must not silently become developer-chosen defaults. Their final rules must respect the baseline principles of not silently repeating interrupted items or scoring them as incorrect responses. Warning placement and content also require review; do not add performance feedback without confirmation. Section 13 defines the later, development-preview-only new-session restart policy; formal apparatus recovery remains unresolved.

## 6. Data and auditability

Data streams must be joinable by session, block, and trial / segment IDs. Use pseudonymous study IDs and retain protocol and software versions. Identity linkage, access, retention, and deletion arrangements remain undecided; do not claim complete anonymity. Export machine-readable data and field descriptions; specific file formats and storage technology belong in a separate decision.

| Data stream | Minimum retained content |
|---|---|
| Session and flow | Owner configuration, selected mode / actual source, operator and device roles, order, B initial target, lists / seeds, practice attempts, state transitions, and completion / withdrawal / interruption reasons |
| Task items and behavior | Word, font color, congruency, trial identity, actual presentation and first-response times, RT, correctness, validity, and interruption flags; do not return correctness feedback to participants during the main task |
| Operator control and avatar presentation | Real head rotation / eye gaze, separate validity, sample source and time; prescribed targets / times, actual received / mapped / rendered state, dwell / turns, and deviations |
| Participant eye tracking | Independent participant source, time, validity, calibration results, and AOI classification; never substitute operator gaze. Retain the baseline handling of AOIs, invalid data, and structural non-applicability in D |
| Transport and synchronization | Source capture, send, receive, and presentation times and clock definitions; clock mapping, offset / drift and uncertainty; measurable latency, packet loss, out-of-order and stale samples, and tracking / network faults |
| Experience and rest | Five raw ratings, item version and language, rating / actual rest duration, Ready, pause, and headset-removal events; inclusion of the optional attribution item remains undecided |

Synchronizing behavior and gaze means that timelines must be alignable and auditable; it does not establish any achieved accuracy. Specific synchronization algorithms, sample rates, and performance thresholds must be specified in the technical design and measured on the apparatus. Simulated input must be explicitly labelled and kept separate from real acquired data.

## 7. Software acceptance checklist

Use repeatable test sessions and exported records for acceptance. These are software-function checks, not requirements for predetermined psychological effects or statistically significant results. Final pass criteria involving unresolved items must first be specified.

| ID | Verifiable acceptance requirement |
|---|---|
| AC-01 Roles and views | Verify that participants see the task and operator avatar and that operators see the participant avatar and the same current task item. Apply participant-side hiding rules in practice, D, questionnaires, and rest. Do not mistake displaying a participant avatar for implemented synchronization of that participant's eye gaze |
| AC-02 Mode permissions | Before each block, the owner can select the mode, check and lock the assigned condition, order, and B initial target, and inspect the recorded configuration. Participants cannot select the mode or arbitrarily override counterbalanced allocation. Being the WoZ operator does not automatically confer experiment-owner authority |
| AC-03 Complete flow | Test passing practice, passing after one retry, failing both attempts, the normal four blocks, extended rest, explicit pause, and withdrawal. Trial counts, rating order, and state records must match section 3 |
| AC-04 Task and behavior | Automatically check the 24-trial composition and list constraints; check matching task / trial identity in both applications. In the main task, one new press advances only once, held buttons do not answer repeatedly, there is no fixed three-second wait or participant-side correctness feedback, and RT and first response can be reconciled against event times |
| AC-05 Control sources | Automatic and real operator sources pass through the same presentation interface; actual source and avatar output can be checked in logs. Automatic reference times are testable, while actual human trajectories and deviations are preserved rather than forced into identical trajectories to simulate a pass |
| AC-06 Visible VR faults | Inject head / eye tracking failures and network interruptions separately; verify identifiable in-VR errors and corresponding logs, with no silent automatic fallback or fabricated valid samples. Accept warning endpoints, deadlines, and pause / recovery behavior only after the unresolved rules are frozen |
| AC-07 Joined evidence | Join behavior, both parties' eye tracking, avatar presentation, ratings, and faults from a test session by identity and time. Missing / invalid data, mode provenance, and interruptions remain visible; retain five separate ratings and no fabricated completion records |
| AC-08 Apparatus evidence | Validate the two-application data link and views on two independent computers and two VIVE Pro Eye setups. Record actual latency, synchronization error, tracking validity, and recovery events. Numerical thresholds remain undecided; desktop demonstrations or configuration screenshots do not count as apparatus acceptance |

## 8. Out of scope and unresolved items

This PRD does not add body / hand / voice synchronization, turn WoZ into an avatar that reacts to participant performance, introduce mode switching during a run, adopt 48 trials per block or additional research conditions, select SDK / engine versions, network protocols, or synchronization algorithms, or define statistical significance as software success.

| Category | Decisions still required |
|---|---|
| Product and interaction | Which application shows fault warnings; automatic pause on tracking / network failure, who confirms reconnection, how to resume, and the next trial; whether operators see accuracy / response results / RT; the participant avatar's driving signals and extent of eye-gaze synchronization; administration entry points and operator views outside the main task |
| Technical design (separate document) | Engine / SDK versions, operator-computer configuration, communication protocol and network settings, coordinate mapping, sampling and data formats, synchronization, timeout / tracking-validity / latency thresholds, disconnection / failure safety policies, and persistence implementation |
| Research and collection readiness | Principal research comparisons, sample size / precision, statistical models and thresholds, RT processing, and other unresolved parameters; suitability of the English questionnaire for the participant protocol and inclusion of the optional attribution item; approval, screening, calibration / AOIs, data retention, and withdrawal arrangements. Keep these unresolved rather than selecting values in this PRD or converting them into software-success criteria |

**Approval status:** The user approved this document's software scope, views and roles in both applications, flow, logging, and verifiable requirements, and authorized publication and staged development. Unresolved items remain unresolved; development records separately report implementation, testing, and apparatus-validation status. The current application presentation language is English, as specified in section 14.


## 9. Classroom presentation requirement (user clarification, 2026-10-29)

The participant presentation must place the existing coloured Stroop word on a world-space whiteboard in a simple classroom, with a standing observer avatar beside the board. The intended composition is a board to the left/centre and the observer to the right, visible together from the participant's normal viewing position. The reference is a spatial-composition example; its letter matrix is not a replacement task. The operator application continues to show the participant avatar and task according to the role and phase rules above.

Use restrained walls, a floor, and a board frame; avoid unnecessary classroom decorations, animated elements, or visual distractors. The avatar must not obscure the board. Maintain controllable head and eye orientation and derive gaze targets from the actual board/avatar geometry. The current repository contains only procedural primitives, so the first implementation may use a clearly described stylized standing figure; a photorealistic or humanoid-rigged asset is not part of this clarification.

Preserve the trial plan, words, ink colours, response mapping, correctness scoring, timing, prescribed gaze conditions, role separation, and real-experiment admission gates. Practice and condition D retain their no-observer rule; avatar body parts must follow the same visibility gate as the head and eyes. A neutral dark task inset within the light whiteboard surface may preserve the existing ink contrast. It must not become a new condition or change the task word size in response to the current trial.

The original classroom revision retained the accepted Quest instruction/status HUD at 2 m with its bounded angular layout. Section 11 supersedes its head-following presentation with a fixed world-space menu; neither requirement is a scale factor for the room. The task word belongs to the board, not the head-following HUD. Quest input remains preview-only, and the final VIVE trackpad colour mapping remains unselected. Scene/build checks and illustrative non-trial render fixtures are not headset or experiment acceptance; the revised classroom requires a separate wearer layout review.


## 10. Fixed response legend and clean task view (user clarification, 2026-10-29)

After hands-on use the user reported that operation was OK and requested two presentation changes. This is scoped usability feedback, not acceptance of research validity, calibrated geometry, measured timing, eye tracking or the complete experiment flow.

During active participant testing, display a fixed graphical response legend to the LEFT of the whiteboard. For this Quest preview it must show the existing X=red, Y=green, A=blue, B=yellow mapping as recognisable left/right controller button layouts with colour swatches and readable button labels. All four mappings must be visible, without covering the board, task word or avatar. The legend remains at the same world-space location across gaze conditions, including D; it must not be parented to the avatar or participant camera. This does not select or implement a colour mapping for the final VIVE trackpad controller.

In healthy active Practice and MainTask states, remove the participant's head-following introduction, phase/status/developer text and duplicate textual mapping. The established room should contain only the fixed graphical legend, board/task word and the avatar when the existing phase/condition permits it. This clarification supersedes the earlier requirement to retain the instruction/status HUD during active trials; the retained non-trial presentation is further superseded by the fixed-menu clarification in section 11.

Keep essential tracking, controller availability, connection, recording and other genuine failure messages visible. Removing the healthy status string must not silently hide missing Touch input when head tracking still works. Preserve necessary practice-correction, questionnaire and pause/withdraw instructions in their own states, all owner/source-selection safeguards, the no-avatar rules, scoring, timing and gaze-condition semantics. A warning must not automatically choose a source, resume the task, or change the existing fault policy.


## 11. Fixed classroom menu and practice legend (user clarification, 2026-10-29)

At 08:17 UTC the user clarified that the entire Quest Link Preview menu must stop following the participant's head/gaze. Present preparation, instructions, practice correction, pause/withdraw, questionnaires and essential fault information on fixed world-space surfaces in the classroom. Use an opaque contrasting background and readable layout so white text never depends on the room or light board frame for contrast. Moving or turning the tracked head must not move, rotate or reflow these surfaces with it. This supersedes the earlier head-following instruction HUD, including non-trial states; preserve the trial stimulus and its existing geometry.

Hide the ordinary menu while answering in Practice and MainTask. Preserve necessary fault visibility and the existing presentation-acknowledgement safeguards if a warning obscures the stimulus; do not silently advance, pause, resume or choose a source. The fixed graphical X red / Y green / A blue / B yellow legend must be visibly available throughout both practice and main trials and through practice correction at its established left-of-whiteboard position. Check actual phase transitions, not only a fresh MainTask fixture. All four mappings must remain readable without covering the task word or avatar. Practice and D retain their no-avatar rule; no VIVE trackpad mapping is selected.

Visual checks must cover Preparation, practice, practice correction, main task, pause, questionnaire and essential faults, including the response legend and fixed transforms under head motion. Editor render fixtures are software evidence, not HMD captures or user acceptance. Do not replace a user's live session without authorization or run a second competing XR player.

At 08:23 UTC the user precisely corrected the questionnaire navigation: UP must decrement the selected rating (5 to 4 to 3 to 2 to 1), and DOWN must increment it (1 to 2 to 3 to 4 to 5). Keep the range and existing endpoint policy. Preserve scale meanings, item text, neutral/held-input gating and separate confirmation. The displayed selected number and recorded/exported rating must remain identical; reversing scoring or anchor meanings is not an acceptable navigation fix.


## 12. Whiteboard-centred participant start (user clarification, 2026-10-29)

At 09:34 UTC the user reported that the new fixed-menu build worked, but the participant stood to the right of the whiteboard. Align the participant's intended initial horizontal position with the whiteboard centre and face the board at the existing appropriate viewing distance. This must account for the real tracked headset's initial tracking-space X/Z and yaw rather than merely repositioning an Editor camera. Preserve current height, pitch/roll and subsequent physical motion; apply at most one initial application-space origin correction, not a continuous head lock or repeated recenter on focus/tracking recovery.

Preserve the board, its left graphical response guide and its right observer arrangement, the fixed menu, task words/ink/size, timing, scoring, questionnaire semantics, gaze-condition definitions and real-experiment gates. The nominal participant reference used for avatar gaze must agree with the centred standing anchor, without accidentally adopting an uninitialized camera pose or introducing live HMD-driven avatar control. Document the necessary preview target-angle/geometry-version change. Do not change OS/runtime recenter settings, drivers or VIVE bindings.

Check initial valid and invalid poses, offset/yaw compensation, one-time application, movement after alignment, repeated/focus-recovery calls and fixed scene/gaze transforms. Build independently and preserve the current session. The user's "ok" accepts usability of the previous fixed-menu preview only; the centred revision needs its own headset check and does not constitute research or apparatus validation.

## 13. First-entry height and explicit preview restart (user clarification, 2026-11-02)

At first entry, resolve the active runtime's existing Floor/Device height convention before showing the classroom, and obtain actual current OpenXR focus without depending on a first edge callback. Keep real measured height and physical pitch/roll. Only the initial application-space X/Z/yaw centring is permitted; subsequent headset movement or re-wear must not trigger another alignment. A neutral initial waiting view is acceptable while the reference/pose is unavailable, with status available in the owner window. This changes no system tracking-origin mode or recenter setting.

For a latched connection-recovery state, provide an explicit development-owner action labelled as ending the current preview and reconnecting into a NEW session, with confirmation that previous progress will not resume and existing records are retained. Record the request, fault context, TechnicalEnd and any interrupted pending trial in the old session; require the exact command acknowledgement before requesting orderly process closure. Require both owned processes to have known exits and both journal chains/close-flush receipts to verify before creating the new session. Unknown command results, storage failures, missing receipts or closure timeouts stop the chain for review; no force termination or automatic retry is permitted.

The new session must have a fresh session ID and separate journals/owner queue, start at Preparation, and inherit no prior-session readiness. Under the existing source policy, SimulatorRehearsal requires fresh handshake, clock and matching-sample readiness; Automatic and practice retain their existing gates. Practice and source choices remain explicit owner actions. Do not resume or repeat an old trial, merge the two runs as continuous valid data, clear the old recovery latch, relax the timeout threshold, or infer validity from packets returning. Delayed packets from previous sessions, duplicates and out-of-order traffic must remain rejected. The recovery action must remain reachable when only the operator has failed but the participant still publishes fresh status. This is a development-preview restart policy; formal apparatus and in-session experimental recovery remain gated.

## 14. English-only PRD and application presentation (user clarification, 2026-11-02)

The PRD and all application-visible text must be in English only, with no Chinese localization option. This includes titles, menus, buttons, instructions, status and error messages, confirmation dialogs, questionnaires, and rating anchors in participant, operator, and owner applications. Preserve the meaning of each instruction and questionnaire item, response mappings, rating direction and values, timing, permissions, and failure behavior.

This language change does not implement the pending four-option experimental redesign described above. Preserve stored data identifiers and historical records; do not rewrite previous sessions or present historical translations as new participant data. English presentation does not establish participant comprehension or apparatus / research acceptance.

## 15. Initial preview countdowns, readable ratings, completion and export location (2026-11-04; flow superseded by section 16)

After the owner locks a block source and confirms readiness, show an animated three-second countdown before the first task item. Derive its number and progress from the experiment's countdown clock, not a second independent display timer. Explicit pauses freeze countdown progress. This is a block-start countdown, not a response deadline or a delay added between Stroop trials.

Use a larger, fixed world-space questionnaire presentation with the complete unchanged English question, anchors and selected value visible. Before selecting the first item of each block's questionnaire, show a three-second animated guide: move the left stick UP to decrease the value, DOWN to increase it, centre between steps, and press A to confirm. Do not enter a rating during the guide or carry a held A/stick across the guide boundary. Preserve all five item IDs, meanings, 1-5 values, order and exported values. Pause and withdrawal remain available. Record the instruction interval separately; the questionnaire duration includes that introduction and excludes explicit pauses.

During rests after blocks 1-3, display an animated countdown using the actual configured minimum duration and remaining experiment time. At zero, show Ready and allow an explicit participant action; never advance automatically or prevent a longer rest. After the fifth rating following block 4, record Completed and show a fixed thank-you presentation with a restrained fireworks animation. Do not show completion fireworks for withdrawal, practice failure or technical termination. The ending must not bypass the final questionnaire, recording failures or normal journal finalization. No automatic application shutdown is introduced.

The owner continues to choose and lock a source separately before each block. The preview currently uses the explicitly labelled BCAD fixture, so its fourth block is D and hides the observer irrespective of source selection. This is not a new per-participant counterbalancing allocator. Automatic reference control and simulated head/eye input remain distinct; simulated input is not live WoZ. The pending four-option redesign is not implemented by this change.

For newly launched previews, save explicit owner exports beneath the session's `exports` directory. After a successful export, return a session- and request-bound receipt identifying the exact completed CSV directory. The owner interface must distinguish raw session records from exported CSVs and offer an explicit Open exported data action only for a verified receipt. A successful command acknowledgement without a matching verified path must not be presented as a known export location. Export failures and uncertain outcomes stay visible; do not retry automatically, overwrite exports, move old data or fabricate a successful receipt. Existing builds' `Participant/exports` files remain in their original locations.

## 16. Board alignment, persistent progress and participant-paced continuation (2026-11-04)

Move the physical whiteboard toward the participant so its writing region and fixed experiment menu share the same world-space centre and appropriate depth layers. Preserve the centred starting station, measured height and subsequent physical movement. Keep the graphical response mapping on the left and observer on the right; update the declared task gaze target with the board geometry. Validate geometry, occlusion and oblique views, then obtain a separate headset check. Desktop or Editor renders alone do not establish comfortable binocular alignment.

The questionnaire controls guide loops without a timeout until the participant presses A. That press dismisses only the guide: require release and a fresh press before any answer can be confirmed. Do not select a hidden rating from stick movement during the guide or a fault. Preserve UP decreasing and DOWN increasing the 1-5 value, wording, item order and exported scoring. Pause, tracking/input loss and repeated guide visits must preserve explicit input boundaries.

Keep progress tracks and their visible fill in a deterministic render order for the whole relevant phase. Countdown reflects its experiment clock; questionnaire selection reflects completed items; rest may show elapsed progress toward the configured 30-second guidance and remain full afterwards. Do not hide a bar just because a timer reaches zero. Preserve essential fault visibility and prevent interaction with covered content. Pausing freezes protocol progress; resuming does not invent completion.

Rest after blocks 1-3 ends on a new A press whenever the participant chooses. A held from the preceding final rating must not continue rest. The rest indicator never imposes a minimum and never advances the protocol itself. The final five-item questionnaire is still required before Completed and the thank-you/fireworks view; there is no final rest.

Confirm the source only once, after passing practice and before the first experimental block. The first block still requires the owner's explicit lock and start. Retain the choice for all four assigned conditions and record its original confirmation plus each block's inherited source. After subsequent rests, start the three-second countdown when the same selected source is ready, without another owner lock/start. If that source is unavailable or a recovery gate remains latched, wait visibly; do not switch sources or bypass the gate. Condition order and the distinction between source and condition remain unchanged. This does not define or implement the pending four-option redesign or real WoZ.

Build this revision separately, preserve old builds and all current/historical records, and do not replace an active session. Verify fresh/held buttons, repeated/paused phases, all four blocks, source failure and final export. User feedback on the preceding build is usability feedback, not formal experimental validation.
