# VR Stroop pilot evidence and decision record

**Updated 28 October 2026.** Read this alongside the [current experiment design](experiment-design.md). This appendix distinguishes published precedents, project-specific adaptations, and lab questions that remain unresolved. The control-architecture clarification below is a local design revision, not an implementation or apparatus-validation report. It does not turn literature parameters into validated settings for this apparatus.

## 1 What changed from the earlier plan

The following working decisions incorporate Richard's review and the user's subsequent acceptance of the latest design. Acceptance of the design is not ethics approval or confirmation that the software exists.

| Topic | Current decision | Reason and remaining boundary |
|---|---|---|
| No-avatar baseline | Add D; each participant completes A–D | C remains an observer-present condition, so it cannot substitute for absence |
| Independent-variable label | Retain “attentional contingency,” with the assigned orientation rules delivered by an automatic source or live operator head-and-eye source | Participant gaze and performance must not select targets. Operator eye gaze is a separate control input in live WoZ; final wording with Richard remains open |
| Movement | Automatic head-and-eye reference motion, or the operator's actual head rotation and eye gaze mapped in real time to the same avatar; body fixed | Do not force the human signal to equal the automatic trajectory. Scope excludes body translation or pose, hands, and voice; validate the actual mapping and visibility |
| Outcomes | Perceived evaluation plus separate observation, worry, focus, and distraction indicators | Incorporates attention/distraction without treating distinct constructs as one score |
| Practice threshold | At least **6/8**, exactly **75%** | The latest accepted project rule; not the 80% criterion used in the cited precedent |
| Practice cap | **Two total attempts**, one initial attempt plus one retry | Resolves the earlier ambiguity around “two rounds” |
| Main trial count | **24 per block**, 12 congruent + 12 incongruent | Replaces tentative 20-trial blocks; balances all four colours and all twelve mismatched pairs |
| Possible longer blocks | 48 per block only as a separately approved future change | Not an automatic escalation, extra participant-specific exposure, or present baseline |
| B timing | 3.0 s settled dwell + 0.5 s turn, repeated; planned full cycle 7.0 s | Automatic reference and the timing rule for the operator to follow; retain actual live deviations rather than retiming or replacing them to create a matching trace |
| Control modes | Program-driven automatic control and real-time head-and-eye WoZ drive the same participant-visible avatar | WoZ uses the operator's continuous tracked input, not buttons that switch targets. Share the experimental flow, presentation interface, and logging, not a guaranteed common trajectory implementation |
| Two-headset and host roles | Participant and operator each use a VIVE Pro Eye, a separate computer, and a separate application | The model choice and two-computer/two-application architecture are confirmed. Operator-PC specifications, SDK/runtime, communication details, and measured connection performance remain unverified |
| Acquisition and transport | Operator capture application/PC acquires actual head rotation and eye gaze, then transmits them in real time to the participant application/PC for avatar mapping; automatic control is generated in the participant application | Dual-device capture, clock alignment, transport performance, tracking validity, and failure handling still require design and validation; never silently replace live input with automatic control |
| Colour screening | Genuine Ishihara red–green screening plus headset colour/button familiarisation | Edition/manual and screening rule remain unconfirmed; no added HRR test or claim of comprehensive colour diagnosis |
| Order and breaks | Williams sequences; same ratings; 30 s minimum additional rest | Improves procedural consistency without claiming complete washout or removal of demand effects |
| Eye versus face | The participant's VIVE Pro Eye gaze is an independent measurement stream; its acquisition and ability to separate eyes from face still require validation | Operator gaze controls the avatar and cannot substitute for participant gaze. Face looks do not establish eye contact; D eye/face measures are structurally not applicable |
| Ethics and apparatus | Separate apparatus verification from participant research authorization | Pilot recruitment and testing require the applicable approvals; no broad exemption inferred from informal lab checks |

## 2 Published evidence and project adaptations

The table summarizes only what is used to motivate the current design. The project does not reproduce any one source protocol. Source locations were checked against accessible full text, except Williams, for which the publisher abstract was available.

| Source and location | Relevant published precedent | Use here and limit |
|---|---|---|
| [Patra et al., 2023](https://upload.wikimedia.org/wikipedia/commons/1/11/Variation_in_how_cognitive_control_modulates_sentence_processing.pdf), §2.3.1, journal p. 11 | Eight-item Stroop practice with congruent, incongruent, and neutral trials; repeated to an 80% criterion; participants already had earlier-session exposure | Supports criterion-based practice in principle. It does **not** validate this pilot's 6/8 threshold, two-attempt cap, or two-category practice set |
| [Koban, Haggadone, and Banks, 2021](https://assets.pubpub.org/hy2snuct/11630427850230.pdf), Procedure/Stroop Task p. 4; Measures pp. 5–6 | Robot-observer Stroop study; three four-trial practice rounds; distinct measures relating to feeling observed, evaluation apprehension, and task attention | Closest observer-task precedent for keeping constructs distinct. Its 12-trial practice, attention-control trials, and 1,000-ms response-to-stimulus interval are not adopted; it does not validate our five custom items |
| [Gregory, 2021](https://d-nb.info/1244731927/34), Experiment 1, Stimuli, PDF pp. 3–4 | Screen-based avatar cueing used 500-ms head turns, 30-ms eye movements, and a 1,000-ms direct-gaze hold | Motivates 500 ms as a head-turn starting point only. Synchronized 500-ms head/eye movement belongs to the automatic reference; it does not guarantee the operator's live head and eye motion share that timing. This reference motion and 3-s settled dwell are project adaptations, not replicated source parameters |
| [Bee et al., 2010](https://research.tees.ac.uk/ws/portalfiles/portal/6400665/100053.pdf), Gaze Model, PDF p. 3 | Noninteractive normal character looked toward the user for 1–3 s and away for 3–6 s; interactive behaviour was specified separately | Seconds-scale precedent and a useful separation of preset versus reactive control. Does not establish symmetric 3-s dwell for a Stroop task |
| [Koller et al., 2023](https://www.frontiersin.org/journals/robotics-and-ai/articles/10.3389/frobt.2023.1062714/full), §4 and Figure 1 | Conversational robot used fixed 10-s gaze cycles with 0.5-s movement explicitly included; WoZ phase triggers were restricted and operator training was described | Supports explicit movement accounting and defined operator rules. The button/phase-trigger WoZ precedent does not establish this design's continuous head-and-eye capture, two-device transport, or real-time avatar mapping. The conversational schedule, reactive speech-phase triggers, and questionnaire scales are not transferred to this task |
| [Li et al., 2025](https://scholars.cityu.edu.hk/ws/portalfiles/portal/275685450/273199588.pdf), §§3.4–3.5, journal pp. 7–8 | Used the 36-item, seven-point Networked Minds social-presence measure; cue-fixation allocation used total fixation time on selected social cues as its denominator | Social presence is not relabelled as perceived evaluation. Our custom five-item set and active-task valid-gaze denominator are different choices, stated explicitly |
| [Williams, 1949](https://connectsci.au/ch/article-abstract/2/2/149/614/Experimental-Designs-Balanced-for-the-Estimation?redirectedFrom=fulltext), publisher abstract | Describes balanced construction for the effect of a single preceding treatment, with an even number of treatments | Motivates the four-sequence arrangement. Position and adjacent-pair balance were also checked directly; no claim that all carry-over disappears. Full original text was not available in this review |
| [Ishihara concise-edition instructions](https://web.stanford.edu/group/vista/wikiupload/0/0a/Ishihara.14.Plate.Instructions.pdf), introduction and administration sections | Describes red–green-oriented plates and explicitly excludes blue–yellow diagnosis; specifies edition-dependent administration/scoring | Establishes the scope boundary only. The cited concise edition is **not assumed to be the lab's edition**. Confirm the genuine materials and applicable manual rather than importing its scoring rule |

The **24-trial count, 6/8 practice criterion, retry cap, symmetric gaze dwell, common custom item wording, and 30-second rest** remain local design choices. Published use of a similar number or format elsewhere does not validate them here. Five-point response options are retained for a short pilot questionnaire; they do not confer psychometric validation.

## 3 Design checks that can be verified without participant data

### Stimulus arithmetic

| Check | Calculation | Result |
|---|---|---:|
| Matched word–ink pairs | 4 pairs × 3 presentations | 12 congruent |
| Mismatched word–ink pairs | 4 words × 3 other ink colours | 12 incongruent |
| Main block size | 12 + 12 | 24 |
| Planned main task | 4 blocks × 24 | 96 |
| Per-word and per-colour count | 3 congruent + 3 incongruent | 6 per block |
| Practice threshold | 6 ÷ 8 | 75% |
| Planned full B cycle | 3.0 + 0.5 + 3.0 + 0.5 | 7.0 s automatic reference and operator timing rule; actual live timing is recorded separately |

### Williams adjacency check

| Sequence | Ordered adjacent pairs |
|---|---|
| ABDC | AB, BD, DC |
| BCAD | BC, CA, AD |
| CDBA | CD, DB, BA |
| DACB | DA, AC, CB |

Across one complete allocation set, all twelve ordered pairs of different conditions appear once, and each condition occupies each block position once. Unequal allocation or withdrawal can alter the realized balance and must be reported.

### Control-source and measurement boundaries

| Stream or component | Role in the revised design | Required boundary |
|---|---|---|
| Automatic source | Program in the participant application/PC supplies head-and-eye reference states to the avatar presentation interface | Keep A–D and the planned B timing; participant performance and gaze do not choose targets |
| Live WoZ source | The operator's VIVE Pro Eye and independent capture application/PC supply actual head rotation and eye-gaze samples for real-time transmission to the participant application/PC | Follow the assigned condition and timing rules; retain actual movement and deviations. No button-target substitute, forced automatic trace, or silent automatic fallback |
| Participant headset and application | Participant VIVE Pro Eye presents Stroop and the same avatar through the participant application/PC | This is separate from the operator's VIVE Pro Eye, computer, and application; the chosen architecture is not a single computer driving both headsets |
| Participant gaze | Independent validity/AOI measurement stream from the participant VIVE Pro Eye; acquisition and calibration still to verify | Preserve its own source and timestamps; never relabel operator gaze as participant measurement |
| Shared components | Experimental flow, avatar presentation interface, questionnaires, and logging | Sharing an interface does not assert identical trajectory generation or an already implemented system |

### Dual-device engineering checks still required

| Requirement | Evidence or design decision still needed |
|---|---|
| Source and mode provenance | Record automatic versus live WoZ source, operator/device identifiers, condition, block, trial, and segment; retain operator head-rotation and eye-gaze samples |
| Capture-to-render timing | Record source-capture, receive, and render timestamps and their definitions; design and validate clock synchronization, offset and drift handling |
| Real-time transport | Use the confirmed two VIVE Pro Eye / two-computer / two-application roles. Verify operator-PC specifications and choose compatible SDK/runtime, communication protocol, and network details after checks; none is selected here. Measure end-to-end latency, packet loss, and out-of-order delivery rather than assuming instant transfer |
| Realized presentation | Log mapped/rendered avatar head and eye states, actual exposure, dwell/turn timing, and departures from the assigned condition/reference schedule |
| Tracking and failure handling | Record source tracking validity and stale/missing data; define and validate the fail-safe policy, thresholds, and recovery. No silent fallback from live WoZ to automatic control |

Only head rotation and eye gaze are in the live control scope. The avatar body remains fixed; body translation or pose, hand tracking, and voice are not added. These are requirements to design and verify, not claims that capture, transmission, synchronization, or failure handling already works.

### What these checks do not establish

Correct arithmetic and a readable diagram do not establish headset accuracy, animation quality, timing fidelity, item validity, safety, recruitment readiness, or sufficient power. Apparatus checks and the [open lab items](experiment-design.md#11-resolved-decisions-and-open-lab-items) remain necessary.

## 4 Reference details

- **Patra, A., Kirkwood, J., Middleton, E. L., and Thothathiri, M. (2023).** *Variation in how cognitive control modulates sentence processing.* Royal Society Open Science, 10, 211969. [Full text](https://upload.wikimedia.org/wikipedia/commons/1/11/Variation_in_how_cognitive_control_modulates_sentence_processing.pdf).
- **Koban, K., Haggadone, B. A., and Banks, J. (2021).** *The Observant Android: Limited Social Facilitation and Inhibition From a Copresent Social Robot.* Technology, Mind, and Behavior. [Full text](https://assets.pubpub.org/hy2snuct/11630427850230.pdf).
- **Gregory, S. E. A. (2021, online publication).** *Investigating facilitatory versus inhibitory effects of dynamic social and non-social cues on attention in a realistic space.* Psychological Research. [Full text](https://d-nb.info/1244731927/34).
- **Bee, N., Wagner, J., André, E., Charles, F., Pizzi, D., and Cavazza, M. (2010).** *Interacting with a Gaze-Aware Virtual Character.* International IUI Workshop on Eye Gaze in Intelligent Human Machine Interaction. [Full text](https://research.tees.ac.uk/ws/portalfiles/portal/6400665/100053.pdf).
- **Koller et al. (2023).** *Robotic gaze and human views: A systematic exploration of robotic gaze aversion and its effects on human behaviors and attitudes.* Frontiers in Robotics and AI, 10, 1062714. [Full text](https://www.frontiersin.org/journals/robotics-and-ai/articles/10.3389/frobt.2023.1062714/full).
- **Li, C., Dai, Y., Chen, G., Liu, J., Li, P., and Ip, H. H. (2025).** *Avatar-mediated communication in collaborative virtual environments: A study on users' attention allocation and perception of social interactions.* Computers in Human Behavior, 167, 108598. [Full text](https://scholars.cityu.edu.hk/ws/portalfiles/portal/275685450/273199588.pdf).
- **Williams, E. J. (1949).** *Experimental Designs Balanced for the Estimation of Residual Effects of Treatments.* Australian Journal of Scientific Research, Series A: Physical Sciences, 2(2), 149–168. [Publisher abstract](https://connectsci.au/ch/article-abstract/2/2/149/614/Experimental-Designs-Balanced-for-the-Estimation?redirectedFrom=fulltext).
- **Ishihara, S.** *The Series of Plates Designed as a Test for Color Deficiency, Concise Edition: Instructions.* Kanehara & Co. [Instruction booklet hosted by Stanford](https://web.stanford.edu/group/vista/wikiupload/0/0a/Ishihara.14.Plate.Instructions.pdf). The lab edition and manual remain to be confirmed.

## 5 Document history

| Version | Scope |
|---|---|
| Earlier Word plan | Historical design document at `docs/Avatar_Gaze_Experiment_Plan.docx`; retained unchanged |
| 26 October 2026 Markdown baseline | Latest accepted four-condition design, explicit practice rule and timing, balanced 24-trial blocks, bilingual custom items, control restrictions, pause rules, evidence boundaries, and open lab checks |
| 28 October 2026 local control-architecture revision | Confirms a VIVE Pro Eye, computer, and application for each of the participant and operator roles. The independent operator capture application/PC transmits live head rotation and eye gaze to the participant application/PC, whose avatar interface also accepts its local automatic source. Flow, presentation interface, and logs are shared; actual live deviations and unresolved integration checks remain explicit. Other experimental parameters are retained |

Future changes to trial count, questionnaires, control policy, timing, eligibility, or data handling should receive a new dated protocol version before the affected cohort starts. Preserve the version used for each session; do not overwrite the provenance of previously collected data.
