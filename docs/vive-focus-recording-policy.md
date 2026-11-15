# Focus changes and local recording

This follow-up changes the participant application's focus policy. Focus loss alone preserves the pre-recording choices and an already-started raw writer. Starting recording still requires an explicit desktop preparation/arm and both headset choices.

| Observed condition | Recording | Task and input |
| --- | --- | --- |
| Desktop window loses focus; VR remains focused and tracked | Continues; desktop focus state is recorded | VR task can continue using current tracked input |
| VR loses focus; real head and eye samples remain available | Continues with focus-state metadata | Task is held; an active trial is interrupted and presentation is unconfirmed |
| VR focus returns | Same recording remains active | Automatic/NoAvatar require fresh input and neutral release, then Trigger explicitly resumes; LiveWoZ remains held as described below |
| Unrecognized or fatal SDK, pose, device-identity, queue or storage failure | Existing error and incomplete-output handling applies | The actual failure remains visible; missing observations remain unknown |
| User stops or withdraws | Existing explicit stop/withdrawal applies | Task and output completion must be checked separately |

## Output interpretation

The existing raw journal stores `focus_changed` and focus-related task events using its task-event format. The raw format version is unchanged. Focus state is a local software observation, separate from SDK measurement-validity flags and from proof that the headset displayed a stimulus.

The analysis safety-event table exposes focus state and its quality declaration. Existing interruption events retain interrupted trials as interrupted, with no invented response time or incorrect answer. Restoring focus does not silently resume a held trial.

An uncommitted presentation reservation may be abandoned at a focus boundary before any presentation mutation has occurred. It establishes no stimulus onset. A committed presentation is retained as evidence; it is not rolled back.

## LiveWoZ forwarding boundary

For a second-PC LiveWoZ task, XR focus loss suspends positive remote forwarding while retaining healthy local capture. The local task stays held in this build; it has no verified source-resume path. A received software revoke acknowledgement is distinguished from unknown remote cleanup, and neither establishes remote physical-device shutdown. A prepared plan is retained as unconsumed evidence rather than automatically restarted. Use an explicit stop or withdrawal when finished; this hold is not a completed experiment.

## Recording setup diagnostics

After explicit desktop preparation, the setup arm expires after 15 minutes. Once the two headset choices have been accepted, automatic Start and Practice can wait for current headset/controller/eye evidence and neutral controls for up to three minutes. No files are recorded merely by opening the view. A changed design or controller identity, changed clock or explicit cancellation invalidates the corresponding authorization; canceled or expired requests do not restart automatically.

The folder text in setup is an unapproved draft. The confirmed setup's folder and budget remain tied to the current software choices. Focus changes alone retain them; a genuine cancellation clears that authorization. Re-entering focus never starts recording or creates files by itself.

## Validation boundary

Automated validation uses synthetic task, input and writer fixtures plus an isolated Unity build. These checks cover focus transitions, held controls, uncommitted presentation reservations, task interruption, continuing journal output and retained real-error handling. They do not establish native SteamVR focus behaviour, physical tracking accuracy, wearer identity or complete hardware recording coverage. The final build evidence and current executable path are recorded in the task's local progress files.

## Explicit one-person startup

1. On the desktop review the intended design, folder and output limits, then choose **Prepare raw + CSV and arm recording + Practice**. Named multi-block requires an explicit option and stimulus list for each row before preparation.
2. Start the classroom view. Eye diagnostics start when the view is ready without saving samples.
3. In the headset confirm both choices and release controls. After current validated neutral input and fresh tracking evidence are available, recording and Practice start without another desktop click. An attached writer does not by itself prove that files opened, writes completed or bytes are durable.
4. Confirm the prepared formal plan after Practice, then separately begin the first block. A trial still needs its own presentation witness before a response can be scored.

## Active recording recovery boundary

A recognized temporary same-reference headset pose loss, or same-established-controller pose loss, holds the task and preserves the active raw session. Gap metadata marks missing measurements unknown. After three consecutive fresh, neutral, current-valid frames, the interrupted planned item can be retried with a distinct identity and new presentation/response timing; earlier completed responses are retained. An overlapping XR focus loss can require an additional fresh Trigger; pre-existing manual/focus pauses are not automatically resumed. Live WoZ's old source is not restarted.

Unrecognized failures, wrong/replaced device identity, changed reference or clock, eye SDK/status faults (including unavailable eye data or calibration), unsafe pending render/writer states, and explicit stop/withdrawal keep their existing fail-closed handling. The app does not infer absent samples, validate photon delivery, or restore a different controller under the same recording identity.

Validation is synthetic only. Installed Start requires the actual Windows player and SDK with live XR, so Editor fixtures do not run that end-to-end path or certify real hardware. Managed fixtures exercise the verified in-memory record and first practice response path, and the isolated Unity fixtures exercise UI and render-related boundaries without launching the player.

## Initial capture lock contention

After the writer attaches, the initial preparation frame may briefly wait
for the exact task or eye-worker lock, or for a fresh nonduplicate eye frame.
The view is masked and no trial input, presentation witness, or coverage is
claimed while waiting. The retry is bounded by two seconds or 120 frames.
Persistent contention, invalid authority, and invalid SDK/status/storage
results remain failures. The first detected fault category is reported
without sample payloads. This bounded startup wait does not silently extend
reaction time during an active trial; an unknown active-frame interval
still fails closed. Recognized same-device pose loss follows the separate
tracking-gap recovery described above.
