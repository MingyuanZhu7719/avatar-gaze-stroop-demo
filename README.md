# avatar-gaze-stroop-demo
VR avatar gaze and Stroop demo planning.

## VIVE interface update - 12 November 2026

Participant setup now separates **Preview (no files)** from **Record locally**, with explicit source/recording starts, output status and fixed pause/withdraw controls. Exactly one VIVE wand is identified automatically; a missing, ambiguous or replaced controller cannot silently supply responses. The fixed colour map and 0.5 threshold are retained.

Both VIVE desktop interfaces have readable step sections, folded diagnostics, opaque panels, Windows DPI scaling and resizable windows. Quest presentation and the experiment protocol are unchanged. These are development packages; physical VIVE and two-PC acceptance remain separate.

- [Participant and operator workflow](docs/vive-owner-workflow.md)
- [Interface validation and limits](docs/vive-owner-ui-validation.md)


## WoZ recording source baseline - 10 November 2026

The current source adds a separate participant/operator WoZ workflow and explicit participant-local recording for the controlled Legacy task. It retains fixed VIVE colours (up red, right green, down blue, left yellow), the 0.5 trackpad threshold, headset startup alignment, pause diagnostics, and the existing single-PC/Quest paths.

**Networking and recording are opt-in.** Ordinary builds do not record. Controlled WoZ recording requires its explicit Windows XR Development build options, local task/eye choices, an output request and separate source/start actions. The operator must accept both forwarding and participant-local storage of mapped operator poses. Participant eye data, answers, reaction times and ratings stay on the participant computer; only response-free task context goes to the operator. Pairing credentials are not written to the journal.

This is a development candidate, not a validated research release. Private participant and operator packages compiled successfully, but neither two-physical-PC networking nor real SDK/HMD recording has been accepted. Software presentation evidence does not establish physical visibility, synchronized clocks, complete sampling, durable storage or remote operator cleanup. A withdrawal test produces an incomplete raw prefix, not normal completed-session CSV.

No participant records, private logs, executables, pairing credentials or vendor SDK binaries are distributed in this repository. Machine-specific synthetic fixture entrypoints remain disabled in the public source.

- [Current WoZ scope, public validation and limits](docs/woz-recording-public-evidence-20261110a.md)
- [Participant-local recording and short-test precautions](docs/woz-participant-local-recording.md)
- [Numeric codec repair, regression evidence and compatibility limits](docs/woz-numeric-codec-validation.md)
- [Current repaired production-source hashes](docs/evidence/woz-numeric-codec-production-manifest.json)
- [Initial public overlay manifest](woz-recording-public-manifest-20261110a.json)
- [Bounded portable public checks](scripts/test-public-woz.py)

**Investigation note:** One original memory test failed with task_send_failed without recording its inner cause. A separate deterministic check found a CLR4 numeric round-trip defect that can cause this rejection; the source now has a verified fallback and regression tests. The original event itself cannot be conclusively attributed. Existing private player packages predate this repair and have not been rebuilt. Build both roles from the same repaired revision before hardware acceptance.

The sanitized public source passed managed API compilation in three configurations against 217 production-source hashes. This is separate from the earlier private package builds and synthetic native tests. See the current evidence note for exact checks and exclusions.

Earlier validation notes and manifests describe their own source snapshots. The numeric-codec repair note and repaired production manifest take precedence for the two updated production files; the original failed-run record is retained.

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

Milestones 1-4 implement the portable experiment core, control and fault contracts, two independent simulation applications, owner controls, auditable CSV exports, and opt-in continuous simulation journals with offline evidence extraction. Unity presentation and scene-generation source is included and compiles against the installed Unity assemblies. **Actual VR rendering, real VIVE Pro Eye acquisition, and two-PC/headset validation remain pending; apparatus sessions are blocked.**

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
