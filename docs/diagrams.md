# Experiment diagrams

[Back to the experiment design](experiment-design.md)

These Mermaid diagrams provide editable, GitHub-native views of the accepted design. The linked static SVGs contain the same operational content and remain readable without Mermaid support. They describe the protocol and conceptual architecture, not completed software or collected results.

## Participant flow

[View the accessible static flowchart](assets/participant-flow.svg)

```mermaid
flowchart TD
    AUTH["Required ethics and lab authorization before recruitment"] --> SCREEN["Approved consent and Ishihara red–green screening<br/>Genuine materials; edition and rule to confirm"]
    SCREEN --> SETUP["Actual-headset colour and button familiarisation<br/>Word understanding, fit, calibration and validation"]
    SETUP --> P1["Practice attempt 1<br/>8 trials, no avatar; score first responses"]
    P1 --> G1{"At least 6 of 8 correct<br/>(75%)?"}
    G1 -->|Pass| BLOCK["Next assigned condition<br/>24 planned trials: 12 congruent and 12 incongruent"]
    G1 -->|Below 6 of 8| RETRY["Repeat standard instruction<br/>One equivalent 8-trial retry"]
    RETRY --> G2{"At least 6 of 8 correct<br/>(75%)?"}
    G2 -->|Pass| BLOCK
    G2 -->|Below 6 of 8| TRAINEND["End standardized task<br/>Record training outcome; no main-task data"]
    BLOCK --> RATINGS["Hide avatar<br/>Five separate 1–5 ratings; same wording in A–D"]
    RATINGS --> REST["At least 30 seconds of additional neutral rest<br/>Extendable; comfort and tracking check"]
    REST --> FOUR{"Four blocks completed?"}
    FOUR -->|No| BLOCK
    FOUR -->|Yes| CLOSE["Close session under the approved procedure"]
    SAFETY["Pause or withdraw at any time<br/>Hide word and avatar; freeze both clocks<br/>Interrupted trial is not an error<br/>If continuing, use the next planned trial"]
```

The safety node applies throughout the session, not only after a block. Condition order is assigned from **ABDC, BCAD, CDBA, DACB**. There are four conditions and 96 planned main trials; interruption can reduce the number of valid completed trials.

## Seven second gaze cycle

[View the scaled static timeline](assets/gaze-timeline.svg)

The SVG uses a proportional time axis. This Mermaid view is a state sequence, **not** a time-scaled plot.

```mermaid
flowchart LR
    P["Participant directed<br/>3.0 s settled dwell<br/>0.0–3.0 s"] --> PT["Turn to task<br/>0.5 s<br/>3.0–3.5 s"]
    PT --> T["Task directed<br/>3.0 s settled dwell<br/>3.5–6.5 s"]
    T --> TP["Turn to participant<br/>0.5 s<br/>6.5–7.0 s"]
    TP --> P
```

- This illustration begins at the participant target. Counterbalance the initial target independently of condition order; reverse the target labels for a task-first block.
- Begin with the first main-trial onset. Stop at block end and freeze the active clock during pauses.
- Head and eyes share movement onset and duration; the body stays fixed. Transitions are separate logged states, not settled gaze.
- The preset policy ignores participant responses, accuracy and eye gaze. A full cycle lasts **7.0 seconds**, not 6 seconds; the target does not simply change every 3 seconds.

## Conceptual control and logging

[View the accessible static architecture](assets/control-logging.svg)

```mermaid
flowchart TD
    CONFIG["Locked block configuration and active clock<br/>Condition, mode, list or seed, initial B target"]
    CONFIG --> AUTO["Automatic preset scheduler<br/>Recommended participant mode"]
    CONFIG --> MANUAL["Scripted operator cues<br/>Alternative only if selected"]
    AUTO --> ANIM["Shared head-and-eye animator<br/>Body fixed; log intended and realized states"]
    MANUAL --> ANIM
    INPUT["First valid new button press"] --> TRIAL["Stroop trial state<br/>Onset, response and accuracy"]
    EYE["Eye-tracking samples"] --> AOI["Validity and AOI classification<br/>Keep invalid tracking separate"]
    SURVEY["Five separate post-block ratings"]
    ANIM --> LOG["Common timestamped audit log<br/>Session, block, trial and segment IDs<br/>Exposure, deviations, pauses, coverage and missingness"]
    TRIAL --> LOG
    AOI --> LOG
    SURVEY --> LOG
    STOP["Always-available safety stop<br/>Hide word and avatar; freeze both clocks"] -.-> CONFIG
    STOP -.-> TRIAL
```

Choose **one control route per block**. Both routes use the same task, animation and logging design; free manual apparatus exploration is kept separate from standardized participant testing. The diagram does not create eight participant conditions.

Eye gaze and responses are measured outputs. They never feed the avatar target-selection policy. Any use of headset position solely to maintain geometric target alignment must be documented separately; it is not eye-gaze contingency.
