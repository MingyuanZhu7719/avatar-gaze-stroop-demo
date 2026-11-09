# avatar-gaze-stroop-demo
VR avatar gaze and Stroop demo planning.


## Current VIVE development source

The current source includes fixed VIVE colours (up red, right green, down blue, left yellow), a fixed 0.5 trackpad threshold, headset startup alignment, clearer pause diagnostics without a participant manual-pause control, and a controlled Legacy/named recording candidate. Existing Quest preview behaviour remains supported.

**Ordinary builds do not record.** Recording requires the explicit controlled Windows XR Development configuration; Legacy recording additionally requires its separate build option. Owner acknowledgement, headset choices and explicit start actions remain necessary. This is not a validated research release or a hardware-tested public player. No participant records, private logs, executables or vendor SDK binaries are distributed here.

- [Current recording scope, validation and limits](docs/legacy-recording-public-evidence-20261109b.md)
- [Short local self-test guide](docs/controlled-recording-self-test-guide.txt)
- [Exact public production-source hashes and build restrictions](legacy-recording-public-manifest-20261109b.json)

The current public source was verified by managed compilation and metadata checks against 195 production input hashes. Historical private player and synthetic-test results are identified separately in the evidence note. Real-headset recording, physical gaze accuracy and complete/durable capture remain unaccepted.

<!-- BEGIN current-experiment-design -->
## Current experiment design

**Start here: [VR Stroop avatar pilot experiment design](docs/experiment-design.md).**

The latest user-accepted working baseline is now readable directly on GitHub. It includes four within-participant conditions, **24 trials per block / 96 planned main trials**, the **6/8 practice rule with at most one retry**, bilingual post-block items, stimulus and counterbalancing tables, and visual explanations of the procedure, gaze timing, and conceptual logging/control design.

**WoZ clarification, 28 October 2026:** the confirmed design uses VIVE Pro Eye with separate participant and operator setups. The operator has an **independent computer and application** that must capture real head rotation and eye gaze and transmit them live to the participant-side application and avatar. Automatic mode drives the same avatar by program through the shared presentation interface. This is not button-based target switching or a single-computer, two-headset design. Real-time transport, synchronization, latency, tracking validity, and source/deviation logging remain requirements to implement and validate. The operator computer's exact configuration, SDK versions, network protocol/details, and clock-synchronization method remain open. Body, hand, and voice synchronization are outside scope. This is an architecture specification, not a claim of implemented or tested integration.

- [Complete experiment design](docs/experiment-design.md)
- [Evidence, adaptations, and decision record](docs/evidence-and-decisions.md)
- [Mermaid diagrams and static SVG versions](docs/diagrams.md)
- [Automatic and live WoZ architecture](docs/experiment-design.md#7-automatic-and-operator-control)
- [Open lab and ethics items](docs/experiment-design.md#11-resolved-decisions-and-open-lab-items)

**Status:** accepted design baseline dated 26 October 2026, not a claim of software implementation, apparatus validation, or ethics approval. Participant testing remains subject to the applicable approvals and protocol freeze.

**Historical version:** [Avatar_Gaze_Experiment_Plan.docx](docs/Avatar_Gaze_Experiment_Plan.docx) is retained unchanged for reference. Use the Markdown design above for the current protocol; this update does not add or refresh a DOCX.
<!-- END current-experiment-design -->

<!-- BEGIN software-requirements -->
## Software requirements

- [Approved PRD v0.1](docs/prd.md): roles, two-app views, experiment flow, control sources, faults, data, and acceptance criteria.
- Software implementation and hardware validation are tracked separately from the accepted experiment design. Unresolved interaction and technical choices remain explicit in the PRD.
<!-- END software-requirements -->

<!-- BEGIN software-implementation -->
## Software implementation

Milestones 1–4 implement the portable experiment core, control and fault contracts, two independent simulation applications, owner controls, auditable CSV exports, and opt-in continuous simulation journals with offline evidence extraction. Unity presentation and scene-generation source is included and compiles against the installed Unity assemblies. **Actual VR rendering, real VIVE Pro Eye acquisition, and two-PC/headset validation remain pending; apparatus sessions are blocked.**

- [Development milestones, actual test results, and remaining prerequisites](docs/development.md)
- [Windows environment, local regression and standalone build verification](docs/windows-validation.md)
- [Direct desktop preview launcher and owner controls](docs/desktop-preview.md) - implemented and built; shares the Windows Framework compatibility fix exercised through the Quest owner UI.
- [Quest 3 PC Link preview and Touch interaction](docs/quest-preview.md) - actual owner UI and desktop player inspection performed; physical headset/controller acceptance remains separate.
- [Headset-preview package and compatibility assessment](docs/xr-preview-prerequisites.md)
- [Requirements and regression coverage](docs/requirements-tests.md)
- [Unity presentation adapter and verification limits](docs/unity-adapter.md)
- [Local development owner console](docs/owner-console.md)
- [Data export fields, provenance, and manifest verification](docs/data-schema.md)
- [Continuous simulation journals, flush boundaries, and interrupted-process evidence](docs/journal.md)
<!-- END software-implementation -->
