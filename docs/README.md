# Documentation

Use the operating guides for a current package, the development guide for source builds, and the design/PRD for protocol requirements. Build-specific manifests and validation logs are generated beside each package; old source-hash snapshots are not current build evidence.

## Running and building

| Document | Use it for |
| --- | --- |
| [VIVE owner workflow](vive-owner-workflow.md) | Participant setup, practice, formal blocks, controller mapping and opening CSV. |
| [Local study analyzer](study-analyzer.md) | Windows program for completed VRAW/CSV, behavioural summaries, individual ratings, eye quality and analysis rules. |
| [Fixed-target eye validation](vive-gaze-validation.md) | Separate recorded accuracy check, target sequence, gaze/AOI summaries and limitations. |
| [Local recording self-test](controlled-recording-self-test-guide.txt) | Recording choices, short withdrawal checks, complete-session output and storage limits. |
| [WoZ participant-local recording](woz-participant-local-recording.md) | Separate participant/operator workflow and recording scope. |
| [Development](development.md) | Current architecture, isolated builds, toolchains and validation commands. |
| [XR prerequisites](xr-preview-prerequisites.md) | Installed packages and runtime requirements for headset builds. |
| [Quest preview](quest-preview.md) | Quest PC Link development preview and Touch input. |
| [Desktop preview](desktop-preview.md) | Desktop launcher and non-headset preview. |
| [Owner console](owner-console.md) | Separate local development control interface. |

## Data and implementation

| Document | Scope |
| --- | --- |
| [Eye acquisition, AOI and timing](vive-eye-acquisition-cadence.md) | Native callbacks, calibration-query provenance, VRAW eye streams, AOI geometry and software clock interpretation. |
| [VIVE Pro Eye adapter](vive-pro-eye-adapter.md) | Installed SDK binding, acquisition/rehearsal interfaces and coordinate boundaries. |
| [Recording and focus policy](vive-focus-recording-policy.md) | Focus, tracking and recording behavior. |
| [Readiness diagnostics](vive-readiness-diagnostics.md) | Controller and start-readiness diagnostics. |
| [View failure diagnostics](vive-view-failure-diagnostics.md) | XR/view startup diagnostics. |
| [Data schema](data-schema.md) | Portable snapshot exports; distinct from participant-local VRAW recording. |
| [Simulation journal](journal.md) | Continuous simulation journals, extraction and interrupted-process evidence. |
| [Unity adapter](unity-adapter.md) | Presentation contracts and camera-render evidence. |
| [Requirements and tests](requirements-tests.md) | Behavioral requirements and their regression coverage. |

## Research requirements

| Document | Scope |
| --- | --- |
| [Experiment design](experiment-design.md) | Research conditions, trial/block design, ratings and unresolved study decisions. |
| [Software PRD](prd.md) | Product requirements and later approved workflow clarifications. |
| [Evidence and decisions](evidence-and-decisions.md) | Scientific rationale, adaptations and decision provenance. |
| [Diagrams](diagrams.md) | Procedure and control/logging diagrams, with SVG versions in `assets/`. |

Implementation, local development observations and research acceptance are separate. Calibration accuracy, eye/face localization, physical display timing and two-PC WoZ require their own validation.

[TODO](TODO.md) tracks explicitly deferred work, including eye-tracking spatial accuracy.

## Historical material

Superseded milestone reports, the earlier DOCX plan and frozen publication manifests are retained in [the pre-cleanup Git revision](https://github.com/MingyuanZhu7719/avatar-gaze-stroop-demo/tree/daaab2c42aed92360c9d679a9fef8c608c01cf53/docs). Use the current documents above for operating or building this checkout.
