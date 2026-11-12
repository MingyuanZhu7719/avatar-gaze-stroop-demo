# VIVE owner workflow

The desktop window now presents setup, the next action and local-output status in one place. The classroom scene, fixed colour mapping, trial protocol and recording authority remain separate from desktop presentation.

## Participant application

| Step | Preview | Record locally |
| --- | --- | --- |
| Run mode | Choose **Preview (no files)**. | Choose **Record locally**. This is a development self-test, not study admission. |
| Design | Select the existing design and any required lists. | Select Legacy A/B/C/D or the available Named multi-block design. |
| Setup | **Configure preview (no recording)**. | Select an existing private local folder, read the data notice and continue to the two headset recording choices. |
| Hardware | **Start classroom view**; enable eye tracking explicitly if required. | Start the view, complete both current choices, then enable eye tracking and wait for valid source readiness. |
| Start | **Begin practice**. No task, eye or CSV files are saved. | Explicitly select **Start recording + raw and CSV output**, then **Begin practice**. |
| Completion | Preview results remain in memory until close. | Review writer status and raw/CSV candidate paths. A fault or unconfirmed close is not a successful save. |

Storage limits are under **Advanced storage limits**. Defaults are 256 MiB raw, 64 MiB CSV and a 300-second CSV cleanup timeout. These are output limits, not reserved disk space or a guarantee of completion. Raw-only alternatives and diagnostic memory views remain under separate advanced sections.

Keep exactly one VIVE wand connected. The application automatically selects its reported role, so no left/right dropdown is required. With no wand or multiple wands, input stays blocked. The device and its role stay pinned for that classroom-view run; a changed device/role requires stopping and restarting the view. Reconnection requires released controls before another press is accepted.

The mapping is fixed in practice and formal trials:

| Trackpad | Colour |
| --- | --- |
| Up | Red |
| Right | Green |
| Down | Blue |
| Left | Yellow |

The sector threshold remains 0.5. For questionnaire ratings, up selects a lower value and down selects a higher value; trigger confirms. Trigger continues instructions/rest or resumes an eligible paused preview. It does not manually pause a task. The desktop owner retains pause and withdrawal controls; recording interruptions retain their existing terminal safety rules.

## Operator application

1. Start the operator headset view and explicitly enable eye tracking.
2. Paste the participant's current pairing offer; review the private-LAN endpoint and pairing status.
3. Review the permissions for live pose forwarding and, where requested, participant-local recording. Consent resets when the offer changes.
4. Connect only after the displayed readiness and authorization checks pass. Live status and stop controls remain visible; technical details are folded under diagnostics.

The operator has no authority to start participant trials, export participant records or bypass participant consent. LAN transport remains subject to the existing network policy. This interface change does not turn a development package into a validated study build.

## Validation boundary

Regression and synthetic Unity fixtures test software behavior without a wearer. They do not establish physical controller reliability, eye-tracking accuracy, real two-PC acceptance or research-ready recording. Current package validation and exact local executable paths are reported with the build deliverables.
