# VR Stroop avatar pilot experiment design

**Protocol baseline: 26 October 2026 · WoZ architecture clarified: 28 October 2026 · Within participant · Four conditions · 96 planned main trials**

This document records the latest user-accepted design for a lightweight pilot of avatar attentional orientation during a colour–word Stroop task. The pilot will assess whether the procedure is usable, the gaze manipulation is visible, and perceived evaluation and attention can be measured meaningfully before a larger study is planned.

**Status:** the design is accepted as the working baseline; it is not a validated standard, an implementation report, or evidence of ethics approval. Hardware, calibration performance, several lab procedures, and the participant protocol freeze still require confirmation. No participant results are presented here.

[Evidence and decision record](evidence-and-decisions.md) · [Editable Mermaid diagrams](diagrams.md) · [Repository home](../README.md)

## 1 Design at a glance

| Element | Accepted working baseline |
|---|---|
| Design | Each participant completes A, B, C, and D once |
| Main task | Select the **font colour**, ignoring the word meaning |
| Stimuli | RED, GREEN, BLUE, YELLOW in four corresponding ink colours |
| Main trials | **24 per block: 12 congruent + 12 incongruent; 96 planned trials overall** |
| Practice | 8 trials without an avatar; pass at **6/8 = 75%**; **two total attempts**, meaning one initial attempt and at most one retry |
| Avatar movement | Head orientation and eye gaze are represented; automatic co-timing is the reference, while WoZ maps the operator's real head rotation and eye gaze; avatar body remains fixed |
| Alternating gaze | Prescribed rule: 3.0 s settled dwell at each target + 0.5 s per turn = **7.0 s per full cycle**; record actual WoZ deviations |
| Control | Automatic program control or live dual-headset WoZ; both drive the same avatar and share task flow, presentation interface, questionnaires, and logs |
| Post-block measures | Five separate, custom **1–5** experience items, identical across all four conditions |
| Rest | After ratings, at least **30 s of additional neutral rest**, extendable by the participant |
| Order | Williams sequences **ABDC, BCAD, CDBA, DACB** |
| Safety | Pause or withdraw at any time; an interrupted trial is not an error |

The numerical defaults are project-specific decisions. They should be checked on the apparatus and versioned before participant data collection, rather than presented as literature-validated thresholds.

## 2 Research question and conditions

The pilot asks whether assigned avatar head-and-eye orientation patterns produce distinguishable experiences of evaluation, observation, worry, focus, and distraction, alongside measurable task and gaze behaviour. The assigned patterns remain prescribed rules in both automatic and WoZ control; their realized trajectories need not be identical.

Richard's term **attentional contingency** is retained with an explicit operational definition: **assigned avatar head-and-eye attentional-orientation patterns, not real-time responsiveness to participant gaze or performance**. The visible avatar is driven by the program in automatic mode and by the operator's live head rotation and eye gaze in WoZ mode. Confirm the preferred final label with Richard.

| Code | Condition | Head and eyes during the active block | Role in the comparison |
|---|---|---|---|
| **A** | Participant directed | Remain oriented toward the participant target | Sustained participant-directed observer |
| **B** | Alternating | Alternate between participant and task targets under the prescribed timing rule; automatic execution or live operator enactment | Alternating orientation, with visible transitions |
| **C** | Task directed | Remain oriented toward the task target | Task-focused observer, **not** an absent-observer baseline |
| **D** | Avatar absent | Avatar hidden | No-avatar baseline |

Keep avatar appearance, scene, task position, controller mapping, and other permitted animation features consistent across A–C. The avatar's body remains fixed. Participant gaze, mistakes, hesitation, and response speed must not select gaze targets or cue operator departures from the assigned rule. **Operator** head rotation and eye gaze are the WoZ control input; they are distinct from participant measurements. Any participant-target geometric alignment must be documented separately from eye-gaze contingency. Body posture, body position, hands, and voice synchronization are outside the confirmed scope.

These are comparisons of **assigned gaze policies over a fixed number of trials**, not equal-duration gaze doses. B also changes motion and transition frequency. The design cannot isolate a pure perceived-evaluation mechanism, establish mediation, or test reactive social contingency. The pilot should describe differences without assuming a particular ordering of the conditions.

## 3 Participant flow

![Participant flow from ethics and consent through screening, practice, four counterbalanced blocks, ratings, rest, and study close. A failed first practice allows one retry; a failed second practice does not enter the main task.](assets/participant-flow.svg)

*Figure 1. Planned participant procedure. Pause and withdrawal remain available throughout; the diagram does not grant permission to recruit.*

### Preparation before scored practice

1. **Authorization and consent.** Complete the applicable lab ethics process before participant research and recruitment. Use the approved consent and screening procedure. Informal apparatus checks are not a general exemption from ethics requirements.
2. **Colour-vision screening.** Use genuine Ishihara materials according to the actual edition/manual and lab-approved rule. The edition, administration procedure, and inclusion rule remain open. Ishihara is used here for red–green screening, not as a comprehensive test of all colour-vision deficiencies; the cited concise-edition instructions specifically exclude blue–yellow diagnosis. Do not substitute self-report, add HRR, or invent a universal plate threshold. [Ishihara instructions, introduction](https://web.stanford.edu/group/vista/wikiupload/0/0a/Ishihara.14.Plate.Instructions.pdf).
3. **Fit and task familiarisation.** On the actual headset, confirm recognition of all four displayed colours and demonstrate correct use of **each** colour-to-button mapping. Instruction and correction are allowed. This is familiarisation, not an undisclosed additional scored gate.
4. **Word and rule comprehension.** Check understanding of RED, GREEN, BLUE, and YELLOW and the instruction to select font colour rather than word meaning. Record relevant English-reading familiarity: recognising four words does not establish equivalent automatic reading.
5. **Eye-tracking readiness.** VIVE Pro Eye is the confirmed model; verify the participant-side application's usable eye-tracking stream and calibrate/validate it at the task and avatar locations before using the planned participant gaze outcomes. Operator tracking does not establish valid participant measurements. Calibrate the operator's head/eye mapping independently in the separate operator application; verify coordinate alignment, usable tracking, and transmission/timing across the two computers before testing. SDK access, actual stream quality, and eye-versus-face resolution remain unverified. These are apparatus checks, not additional scored practice criteria.

### Practice and the entry rule

| Step | Procedure | Decision |
|---|---|---|
| Attempt 1 | 8 trials; 4 congruent + 4 incongruent; no avatar | At least 6 first responses correct → proceed |
| If below 6/8 | Repeat the standard instruction and briefly review the demonstrated mapping | Offer one equivalent 8-trial set with a different order/content arrangement |
| Attempt 2 | Same scoring rule; no avatar | At least 6/8 → proceed; otherwise end the standardized task |

Within each practice congruency category, each ink colour appears once. The four incongruent items also use each word once, for example RED/green, GREEN/blue, BLUE/yellow, YELLOW/red. Score only the first response. After an error, show the correct font colour and response instruction, then wait for an explicit Continue action; correction does not change the score. Practice RT is not a main-task outcome.

Do not add a per-colour or per-congruency passing rule after someone reaches 6/8. Record attempts, first-response accuracy, error types, and technical issues. A failed practice is a training outcome, not a cognitive diagnosis. Any subsequent usability exploration must be separately labelled and must not become main-task data.

Criterion-based practice has a published precedent, but **6/8 and the two-attempt cap are our adaptations**. Patra et al. used eight practice items with an 80% criterion, which requires at least 7/8, and their task/context differed. [Patra et al., 2023, §2.3.1, p. 11](https://upload.wikimedia.org/wikipedia/commons/1/11/Variation_in_how_cognitive_control_modulates_sentence_processing.pdf).

## 4 Main task and stimulus balance

The first valid **new** button press records the response and advances immediately to the next stimulus. There is no forced three-second wait and no correctness feedback in the main task. Use press-edge/release handling so a held button cannot answer successive trials. Log actual stimulus onset and response time; do not silently add a response deadline.

### The 24-trial block

Each table cell is the **planned number of presentations in one block**, not a result. Bold diagonal cells are congruent.

| Word shown ↓ / Ink colour → | Red | Green | Blue | Yellow | Word total |
|---|---:|---:|---:|---:|---:|
| RED | **3** | 1 | 1 | 1 | 6 |
| GREEN | 1 | **3** | 1 | 1 | 6 |
| BLUE | 1 | 1 | **3** | 1 | 6 |
| YELLOW | 1 | 1 | 1 | **3** | 6 |
| **Ink total** | **6** | **6** | **6** | **6** | **24** |

- **12 congruent:** all four matched pairs, three repetitions each.
- **12 incongruent:** all twelve possible mismatched word–ink pairs, once each.
- Each word and ink colour appears three times within each congruency category.
- Prepare four constrained orders with identical multisets; avoid immediate repetition of the exact word–ink stimulus. Apply any further run-length constraints equally to all conditions. Store list IDs/seeds and vary list-to-condition assignment rather than fixing a potentially easier list to one condition.

Twenty-four is the smallest 50:50 block that includes all twelve distinct incongruent pairs. It improves balance relative to 10 + 10 trials, but does not by itself establish statistical power.

### Apparatus check before the participant protocol is frozen

Check completion time, gaze visibility, logging, and fatigue using the 24-trial baseline. Against the automatic reference and prescribed operator timing rule, a pragmatic engineering check is whether a competent, fast, accurate run provides time for at least **two complete seven-second reference cycles** in B and whether both directions are visibly distinguishable. This is not a requirement that actual WoZ movements have exact seven-second periods, a validated psychological exposure dose, or an individual participant exclusion criterion.

For WoZ, evaluate realized operator-driven dwell, transitions, and received/rendered timing against this prescribed reference. Do not count an idealized cue schedule as observed avatar exposure or assume that a human-enacted cycle is exactly seven seconds.

If exposure is too brief, a **future 48-trial block** may be proposed: 24 congruent plus 24 incongruent, 192 main trials overall. It requires a separate decision and approval before testing; it is **not** adopted here. Never add trials only for faster participants, continue until a participant reaches a gaze-cycle quota, or tune gaze timing from responses. Freeze the same count for the cohort and identify any later protocol version separately.

**Session duration is not yet established.** Measure it during apparatus checks, including screening, fitting/calibration, familiarisation, 8–16 practice trials, four main blocks, four questionnaires, rest, and any extra pauses. Fixed trial counts do not imply fixed block duration; no unmeasured minute estimate is treated as a participant commitment.

## 5 Condition order and initial gaze target

| Allocation sequence | Block 1 | Block 2 | Block 3 | Block 4 |
|---|---|---|---|---|
| 1 | A | B | D | C |
| 2 | B | C | A | D |
| 3 | C | D | B | A |
| 4 | D | A | C | B |

Use a pre-generated randomized allocation in sets of four for eligible participants entering the main task. With equal allocation, every condition occurs once at each position and all twelve ordered adjacent pairs occur once. D is not always first. Keep allocations as even as possible for incomplete sets and report assigned and completed sequence counts; withdrawals must not trigger outcome-based reassignment.

Balance B's initial target, participant or task, as evenly as practicable **independently** of sequence. This is a scheduling factor, not four additional conditions. The Williams arrangement balances position and first-order carry-over under equal allocation; it does not eliminate learning, fatigue, expectation, or higher-order carry-over. [Williams, 1949, publisher abstract](https://connectsci.au/ch/article-abstract/2/2/149/614/Experimental-Designs-Balanced-for-the-Estimation?redirectedFrom=fulltext).

## 6 Alternating gaze timing

![Seven-second gaze cycle: participant-directed settled dwell from 0 to 3 seconds, a turn from 3 to 3.5 seconds, task-directed settled dwell from 3.5 to 6.5 seconds, and a return turn from 6.5 to 7 seconds.](assets/gaze-timeline.svg)

*Figure 2. B with a participant-first initial target. Reverse the target labels for task-first sessions. The bar is the automatic reference and prescribed operator timing rule, not measured WoZ movement.*

The table below specifies the intended rule in both modes. In WoZ the operator attempts to enact it through actual head rotation and eye gaze. Record the realized trace and deviations rather than replacing it with the ideal schedule.

| Active-block time | Avatar state | Duration |
|---|---|---:|
| 0.0–3.0 s | Settled at initial target | 3.0 s |
| 3.0–3.5 s | Transition to other target | 0.5 s |
| 3.5–6.5 s | Settled at other target | 3.0 s |
| 6.5–7.0 s | Transition back to initial target | 0.5 s |
| 7.0 s onward | Repeat until block end | 7.0 s per full cycle |

The prescribed initial state is the assigned initial target at the first main-trial onset, when the active clock begins. Intended transition onsets are 3.0, 6.5, 10.0, 13.5… seconds; intended arrivals are 3.5, 7.0, 10.5, 14.0… seconds. **Do not describe this as changing targets every three seconds.** Automatic output and actual WoZ output must be logged separately from these intended times.

For automatic control, use one fixed smooth interpolation curve with no snapping; head and eyes share transition onset and duration. In WoZ, map the operator's real head rotation and eye gaze to the avatar. Human head and eye movements may differ in onset and duration; do not force them into the automatic trajectory, snap them to preset targets, or silently substitute automatic signals. The avatar body stays fixed in both modes. Specify the automatic reference duration rather than an unqualified angular speed. Coordinate mapping, any necessary retargeting/filtering, and their effects on fidelity and latency require explicit specification and apparatus validation; none is selected or claimed implemented here.

The prescribed active clock is independent of response timing, correctness, and participant gaze. It stops at block end and freezes during a pause. Pause handling must suspend visible control; continuing live acquisition for diagnostics, if chosen, must be separately marked and excluded from active-task exposure. Log participant-directed dwell, task-directed dwell, and transitions separately. Report realized exposure, cycles, switch count, and block duration: a block ending partway through a cycle need not have an exact 50:50 target-dwell split.

Published work motivates seconds-scale dwell and explicit transition timing, not these exact parameters. Gregory used 500-ms head turns but 30-ms eye movements, so synchronized 500-ms eyes **in the automatic reference** are a project adaptation, not a constraint imposed on the operator's actual eye movements. [Gregory, 2021, Experiment 1, pp. 3–4](https://d-nb.info/1244731927/34). See the [evidence table](evidence-and-decisions.md#2-published-evidence-and-project-adaptations) for the remaining boundaries.

## 7 Automatic and operator control

The user-confirmed WoZ architecture uses **VIVE Pro Eye in separate participant and operator setups**. The participant wears one headset while doing Stroop. The operator wears the other and uses an **independent computer and independent application** to capture real head rotation and eye gaze and transmit them in real time to the participant-side computer/application and visible avatar. This is a two-computer, two-application design, not one computer driving both headsets. WoZ is not an operator-button target switch or operator-confirmed automatic playback. These are design requirements, not claims of implemented acquisition, transport, or avatar integration.

Automatic mode remains program-driven. Both modes drive the **same avatar** through a common presentation interface and share the Stroop flow, questionnaires, and logging structure. A shared interface does not imply identical input generation, trajectories, timing fidelity, or validated equivalence. Availability of two modes does not create an eight-condition experiment; the mode and its assignment must be explicit in the frozen protocol.

| Mode | Intended use | Restrictions |
|---|---|---|
| Automatic program control | Program generates the prescribed avatar head/eye movement | Fixed schedule; no participant-performance- or participant-gaze-triggered target selection |
| Live dual-headset WoZ | Operator headset captures real head rotation and eye gaze for real-time avatar mapping | Follow the assigned condition and timing rule; preserve source identity and actual deviations; no hidden automatic replacement |

### Device acquisition and shared presentation

The required conceptual path is **operator VIVE Pro Eye → operator application on its independent computer → timestamped real-time transport → participant application on its separate computer → coordinate mapping and avatar presentation**. Participant task responses remain on their own task path. Participant eye tracking must be acquired and validated in its own application as a separate measurement stream; operator gaze is a control stream and must never be relabelled as participant gaze.

The headset model and two-computer/two-application separation are confirmed. Dual-device acquisition and live transport are still requirements, not completed components. The operator computer's exact configuration, runtime/SDK versions and compatibility, capture/render sampling, coordinate transforms, network protocol and physical network details, and clock-synchronization method remain to be confirmed and validated. No specific SDK version, transport protocol, or synchronization algorithm is selected here. The confirmed synchronized content is limited to **head rotation and eye gaze**; body posture/position, hands, and voice are not added.

### Prescribed rule and actual operator behaviour

Before each block, select and lock condition and source mode. A and C prescribe sustained orientation without discretionary target switches; D hides the avatar. In B the operator follows the existing 3.0-s dwell / 0.5-s turn rule, initial-target assignment, and planned clock, without reacting to participant errors, hesitation, gaze, or apparent distraction. Use written instructions and rehearsal, preferably with the same operator. Any cues must guide the human operator rather than secretly generate the avatar's motion.

Treat the intended timing as a reference, not proof of human precision. Log actual head and eye signals, rendered state, dwell/turn timing, wrong or missed target changes, operator ID, and deviation reason. Preserve the reference schedule and flag missed events; do not use hidden automatic catch-up, time-warping, or a silent switch of control source to manufacture agreement. Unscripted apparatus exploration remains separately labelled and must not be pooled with standardized blocks as though equivalent.

### Synchronization, transport, and failure requirements

The following must be specified, implemented, and validated before participant use; they are not assertions of current functionality.

| Requirement | Evidence and records needed |
|---|---|
| Time alignment | Source/capture, receive, and presentation timestamps; device and clock identity; mapping to a common reference; measured clock offset, drift, and synchronization uncertainty |
| Real-time transport | Sample/sequence identity, arrival order, loss, jitter, stale data, and measured end-to-end capture-to-avatar latency; acceptance thresholds remain to be set from apparatus checks |
| Source provenance | Selected and actual source mode, operator/device identity, mapping/calibration version, and every source or validity-state change; no unlogged source blending |
| Tracking validity | Separate head and eye validity, calibration outcomes, dropouts and recovery; do not infer eye validity from valid head tracking |
| Failure and safe resumption | Specify and validate the pause/hide and reacquisition behaviour for lost/stale tracking or connection failure; flag affected segments and never silently fall back to automatic control |

![Conceptual architecture with automatic program control or live operator head-and-eye acquisition and transmission, a common avatar presentation interface, separate participant measurements, and synchronization and validity logging.](assets/control-logging.svg)

*Figure 3. Planned responsibilities and data flow, not implemented or tested components. Operator head rotation and eye gaze control the avatar in WoZ; participant responses and participant gaze remain separate measured outcomes. Hardware/SDK, transport, timing accuracy, and failure handling still require validation.*

## 8 Post-block questionnaire

Hide the avatar before ratings. Use neutral researcher behaviour and identical wording, anchors, and item order in A–D. These are **five separate custom pilot indicators**, not a validated scale or a translated established instrument.

**Instruction:** “Please answer about the block you have just completed.”  
**中文：**“请根据刚刚完成的这一组作答。”

| Score | English anchor | Chinese anchor |
|---|---|---|
| 1 | Strongly disagree | 非常不同意 |
| 2 | Disagree | 不同意 |
| 3 | Neither agree nor disagree | 中立（既不赞同也不反对） |
| 4 | Agree | 同意 |
| 5 | Strongly agree | 非常同意 |

Present items in the order below in every block: focus and distraction first, then observation, evaluation, and apprehension.

| Order and construct | English item | Chinese equivalent |
|---|---|---|
| 1 · Task focus | During this block, I kept my attention on selecting the font colour. | 在刚才这一组中，我把注意力保持在选择字体颜色这件事上。 |
| 2 · Distraction | During this block, my attention was drawn away from selecting the font colour. | 在刚才这一组中，我的注意力被从选择字体颜色这件事上引开了。 |
| 3 · Feeling observed | During this block, I felt watched while completing the task. | 在刚才这一组中，我做任务时有被注视的感觉。 |
| 4 · Perceived evaluation | During this block, I felt that my task performance was being evaluated. | 在刚才这一组中，我觉得自己的任务表现正在受到评价。 |
| 5 · Evaluation apprehension | During this block, I worried about receiving a negative judgment of my task performance. | 在刚才这一组中，我担心自己的任务表现会受到负面评价。 |

**Scoring:** retain each item separately. Do not sum them, use one reliability coefficient to assert a common construct, or treat reverse-scored focus as a validated distraction measure. Observation, evaluation, worry, attention, and social presence are not interchangeable.

The no-avatar condition may still receive nonzero observation or evaluation ratings because the researcher or recording remains salient. Do not change the questions to mention the researcher only in D. Avatar-specific follow-ups, if later needed, must not replace the common four-condition outcomes.

**Optional descriptive attribution check** — decide whether to include it before freezing the questionnaire, then use it identically in all conditions:

“If you felt evaluated, what contributed to that feeling? Select all that apply.”  
“如果你有被评价的感觉，哪些因素使你产生这种感觉？可多选。”

- Virtual character, if present / 虚拟人物（若该组出现）
- Researcher / 研究人员
- Awareness that task performance was recorded / 知道任务表现会被记录
- Other / 其他
- Did not feel evaluated / 没有被评价的感觉
- Unsure / 不确定

“Did not feel evaluated” is exclusive of substantive source selections. Attribution is descriptive; it does not prove that a selected source caused the feeling.

Repeated questions may prime later blocks. Common wording and counterbalancing do not remove that risk. Check item comprehension in the approved pilot; confirm the administration language and Chinese wording before collection, and version any later revisions. The rationale for separating these constructs comes partly from robot-observer Stroop work, not from validation of these new items. [Koban, Haggadone, and Banks, 2021, Measures, pp. 5–6](https://assets.pubpub.org/hy2snuct/11630427850230.pdf).

## 9 Rest interruption and withdrawal

### After each block

1. End the active task and hide the avatar.
2. Collect the five ratings in the same neutral presentation.
3. Provide **at least 30 seconds of additional neutral rest**, excluding rating time. Enable Ready after the minimum; allow longer rest.
4. Check comfort/readiness neutrally. Allow headset removal. Recalibrate as needed after removal or fit adjustment and validate before continuing; use a brief validation at other block boundaries too.

Record actual break lengths and headset removal. Thirty seconds is a practical starting value, **not a proven washout interval**. The final block is followed by the same ratings and rest, then the approved closing procedure.

### At any time during a block

| Event | Required action | Data handling |
|---|---|---|
| Participant pause or operator safety stop | Immediately hide the word and avatar; enter a neutral paused state; stop task timing and the active gaze clock | Mark the current trial interrupted, not incorrect; no valid uninterrupted RT |
| Participant chooses to resume comfortably | Check headset/tracking as needed; give the same brief readiness countdown; restore the paused reference schedule; in WoZ revalidate/reacquire the live source before visible resumption | Continue with the **next planned trial**; start a new logged segment; do not silently repeat or replace the interrupted item, or synthesize operator signals to conceal the interruption |
| Discomfort, distress, or withdrawal | Stop promptly and follow the approved lab procedure; continuing is never required | Preserve only what the consent/data-withdrawal arrangement permits; do not require a reason or exit questionnaire |
| Technical failure | Follow a pre-specified technical policy | Retain all attempted blocks in the audit trail; no automatic whole-block rerun |

Preserve completed valid trials and flag interrupted blocks. Report an uninterrupted-block sensitivity summary rather than automatically excluding the whole participant. Behavioural and gaze-data validity are separate decisions. Do not impute missing conditions or manufacture equal valid-trial counts.

## 10 Logging and pilot outputs

### Minimum records

| Record | Fields to retain |
|---|---|
| Session | Pseudonymous participant ID; protocol/software version; participant and operator device roles/models and sampling configuration; necessary consent/screening status; word/colour/button checks; practice attempts; sequence; B initial target; selected/actual mode and operator |
| Trial | Condition; block/segment; list/seed; trial index; word; ink; congruency; intended and first actual response; accuracy; stimulus onset; response timestamp/RT; technical and interruption flags |
| Avatar state | Intended/realized target; prescribed event time; actual source and rendered head/eye state; transition onset and arrival; realized dwell, turns and cycle counts; pauses and deviations |
| Operator control acquisition | Head rotation and eye-gaze samples with separate validity; source device/clock and sample identity; raw and mapped values as appropriate; calibration/mapping version; capture timestamps and dropouts |
| Transmission and synchronization | Send/receive/presentation timestamps and clock mapping; offset/drift and uncertainty; latency, jitter, loss, stale/out-of-order samples, failure/recovery and source changes |
| Participant eye tracking | Participant-source timestamp and validity; raw/calibrated gaze as appropriate; AOI assignment; calibration and validation outcomes; never substitute operator control samples |
| Questionnaire and breaks | Separate item responses; item version and language; actual rating/rest duration; readiness, headset removal, and optional attribution |

Require a common timing reference so trial, avatar, operator acquisition, participant gaze, and pause events can be aligned. The synchronization method and achieved accuracy remain to be implemented and verified; timestamps alone do not prove alignment. Keep simulated desktop input visibly labelled and separate from both real operator control and participant eye-tracking data. Storage location, access, retention, identifier linkage, and withdrawal handling remain subject to the approved data-management plan.

### Gaze definitions

These outcome definitions refer to **participant gaze**, not the operator gaze used to control the avatar. VIVE Pro Eye is selected, but usable participant-side SDK acquisition and the quality needed for these measurements must be verified separately.

- Exclusive active-task AOI categories: **task, eyes, remaining face, other**. Invalid tracking is a separate status, never “other.” Eyes and remaining face must not overlap in exclusive counts; any combined face total must be explicitly labelled.
- For comparable task-attention proportions, use **valid active-task gaze time** as the stated denominator; also report valid-data coverage relative to total active-task time. Pause, questionnaire, and rest periods are outside active-task time.
- If validation cannot separate eyes from adjacent face reliably, use face-level analysis. Looking at a face is not equivalent to looking at the eyes, mutual gaze, or feeling evaluated. Any future mutual-gaze measure needs explicit spatial/temporal criteria and adequate calibration.
- D has no avatar eye/face AOI: mark these measures **structurally not applicable**, not comparable social-gaze observations of zero. Task-AOI measures remain available in all four conditions.

### What this pilot will report

Report recruitment-to-main-task flow, training outcomes, completion and pauses, block duration, realized gaze exposure, WoZ head/eye deviations if used, source provenance, synchronization uncertainty and transport latency, tracking coverage/loss, item comprehension/distributions, discomfort, and data completeness. Summarize correct-trial RT and accuracy by condition and congruency with valid-trial counts. Do not pool control modes as though actual trajectories or timing fidelity were identical without an explicit protocol and justified comparability analysis.

Retain raw data and predefine technical validity and RT-processing rules. Do not select post hoc trimming or exclusions to obtain an expected result, and do not automatically adjust RT comparisons for block duration, which is largely produced by the RTs themselves.

The pilot sample size and precision objectives remain open. Neither 24 nor 48 trials per condition demonstrates adequate power for a condition-by-congruency interaction. A small, nonsignificant contrast is not evidence of no effect; later study planning requires a defined estimand and appropriate precision/power analysis.

## 11 Resolved decisions and open lab items

**Resolved for this working baseline:** four conditions including D; head orientation and eye gaze with a fixed avatar body; 6/8 practice with two total attempts; 24 main trials per block; five separate common items; the prescribed 3.0-s dwell and 0.5-s transitions; 30-s minimum additional rest; the four Williams sequences; automatic program control and live dual-headset WoZ with real operator head rotation and eye-gaze mapping to the same avatar. These decisions remain subject to apparatus feasibility and the approved participant protocol; actual WoZ head/eye timing is measured rather than assumed equal to the automatic reference.

**Verified development-laptop background:** screenshots show an AMD Ryzen 9 8945HS, 32 GB RAM, NVIDIA GeForce RTX 4060 Laptop GPU with 8 GB dedicated VRAM, and an NVMe SSD. This is configuration evidence, not a VR compatibility, performance, or headset-connection test. VIVE Pro Eye and a separate operator computer/application are confirmed; the operator computer's detailed configuration remains unknown. The confirmed architecture requires the participant and operator applications to run on separate computers. Network details and demonstrated integration/performance remain open.

| Open item | Evidence or decision needed before participant testing |
|---|---|
| Ethics and recruitment | Applicable approval and permitted recruitment; approved consent, safety/stop procedure, compensation, and data-withdrawal terms |
| Hardware and software | VIVE Pro Eye and separate participant/operator computers and applications are confirmed. Verify the development laptop's actual Windows/VR environment, the operator computer's configuration, actual headset connections and usable streams, controller support, engine/build configuration, runtime/SDK versions and device/host compatibility |
| Geometry and mapping | Final participant button mapping, seated arrangement, target positions and avatar visibility; operator-head rotation and eye-gaze coordinate calibration/retargeting, with no added body/hand/voice synchronization |
| Eye-tracking precision | Separate operator-control and participant-measurement calibration/validation evidence, sampling and validity rules; participant AOI sizes/separation and face-level fallback |
| Dual-device acquisition and transport | Operator application must capture/send; participant application must receive/map/present on a different computer. Sampling rates, network protocol/physical network details, clock definitions and synchronization method remain open; implement and validate offset/drift/uncertainty, latency/jitter/loss, source provenance, and tracking/connection-failure handling |
| Colour screening | Genuine Ishihara edition/manual, administration conditions, and lab-approved screening/inclusion rule |
| Apparatus feasibility | Verify 24-trial duration, two-cycle engineering screen, animation readability, fatigue, input handling, and log fidelity; separately approve any later 48-trial change |
| Participant control mode | Specify mode assignment in the frozen protocol; rehearse the live WoZ operator's prescribed conditions/timing, define deviation measures/tolerances and safe reacquisition, and do not assume automatic/WoZ equivalence |
| Questionnaire | Confirm language, comprehension, Chinese equivalence, and whether the optional attribution item is included |
| Sampling and analysis | Pilot N and precision rationale, population/eligibility, sequence allocation, list assignment, technical exclusions, and planned descriptive analyses |
| Terminology and data handling | Confirm Richard's preferred final label; settle storage, access, retention, identifier linkage, and withdrawal handling |

**Document precedence:** this Markdown plan is the current design entry point. The older `docs/Avatar_Gaze_Experiment_Plan.docx` is retained as a historical version and is not updated or deleted by this documentation revision. The diagrams describe the plan, not completed software.
