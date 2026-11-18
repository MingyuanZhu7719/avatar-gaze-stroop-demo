# VIVE participant recording

Run one Automatic A/B/C/D task per session. Give each person one study code from P001 to P999. Issue codes consecutively and use the same code for Ink colour, Word meaning, Visual search and any repeat attempt. Keep the enrollment register separately from the recording folder. Use a separate package/data folder for hardware rehearsals so that test records do not enter the participant dataset.

1. Open the outer `AvatarStroopViveEyePreview.exe` in the participant package. Enter the participant code and select the task. Review the assigned four conditions and stimulus lists before preparing the session. Each launch creates a fresh session identity even if the code and task are repeated.
2. Prepare local recording and start the classroom. Have the wearer make the two on-headset recording choices. The tutorial and ten-question exam record task responses. Eye recording starts with the first formal countdown after an exam score of at least 8/10 and the formal readiness checks.
3. Confirm Automatic and begin the first block. Following the fourth block and its questionnaire, allow the recorder to finish closing, then open the CSV. Each run has its own session folder under `LocalParticipantData` by default. Keep interrupted attempts separate when reviewing completed sessions.
4. Load the completed CSV **or** its matching VRAW into the updated study analyzer. Use `participant_id` to link tasks, `participant_task` to identify the task, and the session ID to distinguish attempts. Verify the assigned condition and list for each block before comparing results.

## Allocation

The four Williams orders are ABDC, BCAD, CDBA and DACB. Within each consecutive enrollment group (P001–P004, P005–P008, and so on), each task uses all four orders. Each condition receives each of the four stimulus lists once. Across sixteen consecutive slots starting at P001 and each subsequent group of sixteen, each order is paired with all four list rotations. B's initial gaze is also balanced. A participant's code and selected task fully determine the plan, so repeating a task reproduces its allocation. Missed sessions and withdrawals can make the observed sample uneven. The researcher selects which of the three tasks to administer first.

The raw session header and readable CSV carry the participant code, task, allocation scheme, block order, zero-based list indices and B's initial gaze target. Existing developer recordings can be analyzed alongside participant runs and have no assigned participant code. Fine eye-versus-face gaze accuracy remains on the [validation backlog](TODO.md).

## Build

Use the isolated Unity project preparation in `docs/development.md`. Run `scripts/build-vive-stroop-preview.py` with `--participant-recording`, the pinned Unity editor, the prepared project, and a new output directory. Build the matching analyzer with `scripts/build-study-analyzer.py`.
