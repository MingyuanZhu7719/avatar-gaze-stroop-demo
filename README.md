# avatar-gaze-stroop-demo
VR avatar gaze and Stroop demo planning.

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
- [Requirements and regression coverage](docs/requirements-tests.md)
- [Unity presentation adapter and verification limits](docs/unity-adapter.md)
- [Local development owner console](docs/owner-console.md)
- [Data export fields, provenance, and manifest verification](docs/data-schema.md)
- [Continuous simulation journals, flush boundaries, and interrupted-process evidence](docs/journal.md)
<!-- END software-implementation -->
