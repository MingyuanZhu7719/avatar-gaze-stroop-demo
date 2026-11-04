# Continuous simulation recording and offline evidence extraction

M4 adds an opt-in recorder to the two development simulation applications. It does not admit apparatus sessions, acquire real eyes/headsets, choose a participant fault policy, or resume an interrupted experiment. Existing CSV snapshot export remains a separate operation. Recording uses the installed C# runtime and no added package.

## Enable and inspect

The console host accepts `--journal` after its existing role/session/ports/output/scenario arguments. It creates `participant-journal.asj` or `operator-journal.asj` inside the explicitly supplied output directory. Each path must be unused. The integration launcher supplies the option to both roles, coordinates their startup, and uses the existing opt-in scenario-completion evidence handshake; manually starting a peer before its counterpart has bound is not an accepted readiness procedure.

The Unity development bootstrap accepts `--stroop-journal /absolute/path/new-session.asj` independently of its optional owner command-file argument. Supply a different unused file for each role and an existing parent directory. Current executed compilation, scene and player-build evidence is recorded in [development.md](development.md); those checks do not establish headset or study validation. No recording directory, access policy or retention rule is selected for a study.

For an existing .NET SDK, extract an offline journal with:

```sh
dotnet run --project AvatarStroop.Simulator.csproj -- journal-extract /absolute/path/participant-journal.asj /absolute/path/evidence-parent
```

The installed-Mono integration build exposes the same `journal-extract` arguments on `build/AvatarStroop.Simulator.exe`. Extraction creates a fresh `journal-extraction-<guid>` directory and never overwrites the original or an earlier extraction. It does not run an experiment or open a network connection. The source must be offline; the reader does not share its file handle with a writer.

The result contains:

| File | Meaning |
|---|---|
| `verified-prefix.asj` | Exact source bytes through the last completely validated record. No close marker is added. |
| `records.csv` | Original journal sequence, kind and field count for each validated record. |
| `fields.csv` | One named value per record; `is_null` distinguishes null from an empty string. UTF-8 CSV supports multiline values. |
| `recovery.csv` | Source status, read error, original/verified byte counts, record count and close-marker observation. Runtime flush acknowledgment is explicitly unavailable from journal bytes alone. |
| `manifest.csv` | SHA-256 and row counts for extraction outputs; finalized extraction is separate from a complete journal or completed experiment. |

A successful extraction can contain an incomplete or invalid source prefix. Read `journal_status` and `read_error`; the tool's success only means these evidence files were written. Invalid records are never skipped. `ReadFailure`, `Invalid`, `Truncated` and `UncleanEnd` remain distinguishable in the report. A prefix with no complete valid header contains zero verified bytes and has no inferred identity.

## Three distinct persistence facts

1. `LastWrittenSequence` advances only after a complete frame has been accepted by the writer's stream. This can still be buffered.
2. `LastFlushedSequence` and `LastFlushedOffset` advance only after `FileStream.Flush(true)` returns successfully. The configured host/Unity interval is an explicitly labelled **0.1-second development fixture**. Event-loop stalls can delay it; it is not a measured loss bound or study performance threshold. Console `JOURNAL_ACK` lines contain a sequence/byte boundary and a phase/acquisition context that has been captured and flushed before the line is emitted.
3. Offline parsing reports complete, checksum-valid bytes and whether a `__close` marker is readable. A readable close marker cannot establish that the original flush returned successfully: a flush can fail after those bytes became readable. It also cannot establish that the study completed. Only a recorded `session_observation` with an actual `EndReason.Completed` reports experiment completion.

The final console report retains the runtime's acknowledged sequence/offset and any recorder error. A killed-process check retains the last acknowledgment it actually observed externally. Recovery may find valid later records; those are readable evidence, not a fabricated runtime acknowledgment. No promise is made about physical power loss, directory-entry durability, disk controller caches, disk-full apparatus behavior, or measured storage latency.

Expected file creation/write/flush/close failures latch the recorder error and retain the previous successful acknowledgment. Subsequent recording does not silently retry, switch files, or mark success. Both Unity roles include the error and boundary in their development warning surface. The console host fails explicitly. The experiment engine does not automatically pause, resume, alter scoring or create a completion/technical-end event because of that recorder failure. Existing in-memory evidence can still be inspected or separately exported; this does not repair a journal gap.

## Recorded observations

The header fixes journal/session/role/clock, `Simulation`/`SIMULATION`, protocol/software version, exporter identity, actual source digest when supplied, and timestamp semantics. The software is `0.4.0-dev`; the approved protocol version and experimental parameters are unchanged. Header provenance is never inferred from a filename. Unity's unknown build digest stays `unavailable`.

Participant records include configuration and allocated lists, engine events and their complete fields, immutable trial observations, independent ratings, accepted operator samples, avatar observations, clock and transport evidence, plus any caller-supplied independent participant sample/window/AOI records. In the current applications no participant measurement stream is attached; a `participant_measurement_configuration` record explicitly states its absence. Absence is not zero gaze, attention or tracking loss.

Trial objects mutate after insertion. The recorder indexes new trial identities once, uses newly observed engine events to identify changed trials, and writes immutable state updates for onset, first response, interruption and technical flags. `observed_through_engine_sequence` identifies the capture boundary. Unchanged captures do not repeatedly serialize the entire history. Capture occurs after runtime ticks, synthetic responses, owner commands, participant input and camera callbacks, and during export/shutdown. A crash can interrupt those boundaries; extracted observations are not an atomic serialized experiment transaction, and the extractor does not manufacture missing records or reconstruct an executable continuation.

Operator acquisition is appended before calling the application's send method. Separate `operator_sample_send_result` records distinguish a socket-accepted send, a terminal send fault and suppression; none is a delivery acknowledgment. Raw acquisition and receiver acceptance use different record kinds and remain joinable by session, stream, block, segment and sample sequence. The operator also records changed task observations, monitor faults and clock evidence. Clock exchanges retain all four named-clock timestamps continuously even after the existing diagnostic ring retains only its last 256 entries.

`send_local_seconds` in a raw sample is the preparation timestamp assigned before journal writing and the socket call. It does not measure kernel emission. Send-result records additionally retain the monotonic entry/return boundaries of the application's `Send` method, explicitly labelled as such. Synchronous recording may affect timing; its overhead is not validated apparatus latency. Raw invalid quaternions/directions and reported flags are preserved, including `NaN` and infinities; effective validity remains separate. Double values use invariant round-trip formatting. Headless avatar records retain `headless_controller_evaluation_not_rendered` and no invented renderer/applied-pose values.

## File grammar and bounds

`avatar-stroop-journal-1` begins with eight ASCII bytes `ASJNL001`. Each frame has a little-endian 32-bit payload length and its bitwise complement, the payload, and a 32-byte SHA-256 digest. The protected length is checked before allocating or reading the payload. Payloads are at most 1 MiB and contain at most 4,096 named fields.

The payload contains an Int64 sequence, the journal's 16-byte GUID token, a UTF-8 kind string, an Int32 field count, and sorted name/value strings. Each string has an Int32 byte length; `-1` is permitted only for a null value. UTF-8 is strict. Empty keys, duplicate keys, invalid lengths, unexpected reserved kinds, extra payload bytes, wrong sequence/token/schema and checksum mismatches stop parsing at the prior verified boundary. Sequence zero must be the complete `__header`; `__close` has no fields and forbids later bytes.

Each digest is `SHA256(previous_digest || payload)`, with 32 zero bytes before the header. This detects accidental corruption, reordered/duplicated records and mixed journal files. The length complement additionally catches damaged in-range lengths before they can disguise intact following records as a missing tail. These checks are not authentication; they do not resist deliberate rewriting of lengths, payloads and hashes. The reader is streaming and does not retain the whole journal in memory.

## Verification boundary

The M4 portable tests cover exact text/null/numeric preservation; every cut within a final frame; checksum, protected-length, sequence and identity failures; exclusive creation/no overwrite; callback error propagation; injected partial-write and flush failures; mutable trial updates and a complete deterministic session; independent gaze and raw/control/render separation; extraction preservation; and recording faults without invented task policy. A fake socket verifies that acquisition is already recorded when a tracking send fails and that delivery remains unacknowledged.

`scripts/integration.py` exercises the separate roles with recording enabled. Its journal checks independently decode framing/hashes/UTF-8/nulls in Python, compare every extracted field and manifest, and reconcile normal source/receiver observations. An additional normal simulation uses the explicit `--journal-kill-fixture` flag together with recording and startup rendezvous. That test lifetime waits for the launcher to terminate it and fails after 65 seconds if termination never arrives; it cannot claim ordinary scenario completion. The launcher waits up to 30 seconds for a flushed participant MainTask observation and at least 20 acquired operator samples, then checks that both processes are still alive and kills them without disposal or final export. It checks externally observed flush boundaries, incomplete recovered prefixes, original-byte preservation and shared sample identities. These timeouts are engineering test bounds; no experiment response deadline or transport threshold changes. Platform-specific process termination uses Python's `Popen.kill()`; no physical power-loss claim follows.

Execution evidence for the final source snapshot belongs in [development.md](development.md). Merely having these tests or compiling their code does not claim that a process-kill scenario or hosted Windows CI has passed.

## Explicit development preview restart

The owner may explicitly end a preview and reconnect as a new session. This is not journal replay or continuation: the old session retains its fault timeline, `preview_restart_requested` request identity, technical issue, `TechnicalEnd`, and any pending trial's `Interrupted` outcome. A matching accepted owner-command acknowledgement, following successful durable capture of `TechnicalEnd`, is required before the launcher requests normal closure. No existing result, rating or interruption is relabelled as uninterrupted data.

New builds create `<journal-path>.closed` only after successful final capture, journal close and disposal, with a returned `FileStream.Flush(true)` boundary. A new receipt is written and flushed to a unique staging file, then published without overwriting any prior receipt. It binds session, role, clock, journal ID, acknowledged sequence/byte offset and the SHA-256 of the complete journal. Failure retains available evidence and reports a recording-close warning; there is no fabricated historical receipt or automatic retry. This attests a successful software flush return, not physical power-loss durability or research completion.

Before the requested fresh launch, both owned processes must have confirmed exits. The launcher then verifies both complete checksum chains, terminal close markers and matching close receipts, plus exactly the participant technical ending associated with the acknowledged restart request. Missing, truncated, corrupt, mismatched or unacknowledged records stop the restart. Older packages' journals have no close receipts and are not retrospectively admitted by this gate. The old files remain readable and extractable through the existing offline workflow.

Successful restart creates a separate session ID, journal pair and owner queue in Preparation, with no source choice or practice action. Previous-session packets remain rejected even if an OS port is reused. The original receiver's fault latch and 250 ms engineering threshold stay intact. Sessions are never merged into one continuous valid record.

## Preview flow and export receipts (2026-11-04)

New Unity previews explicitly configure a three-second block countdown, a three-second questionnaire controls guide, a 30-second minimum rest between blocks, and no rest after the final five-item questionnaire. `session_created` retains the configured durations, `skip_final_rest`, and presentation/duration-policy identifiers. Instruction start/completion and skipped final rest are separate events. Questionnaire phase duration includes its guide and excludes explicit pause time. Completion follows the final rating; it does not claim a CSV export or a closed journal. Older records are not rewritten and default headless fixture behavior remains available.

New owner-requested CSV snapshots reside under the session's `exports` directory. Each successful request may publish a separate `export-<request-id>.receipt` only after the complete bundle verifies. This receipt binds the session, exact command request and export directory to the manifest hash; the owner revalidates it before presenting or opening the directory. It is an export-location receipt, not a journal close receipt, proof of physical durability, or authority to resume/restart. CSV failure, missing or mismatched evidence, and an uncertain receipt publication never produce an invented success location or an automatic retry. If files were created before a subsequent receipt failure, preserve them for explicit review. Existing builds' exports remain in their original `Participant/exports` directories.

## Participant-paced preview amendment (2026-11-04)

The subsequent preview records explicit acknowledgement of its unlimited questionnaire guide, `rest_policy=participant_continue_no_minimum_no_automatic_advance`, and `source_lock_policy=explicit_once_after_practice_retained_for_session`. The configured rest duration is now display guidance in that policy, not an enforced minimum. Instruction completion records the actual participant acknowledgement; time passing alone cannot create it. Explicit pause time remains excluded.

One `session_source_confirmed` event identifies the original owner choice. Each `block_configuration_locked` event remains present for export compatibility, with `selection_origin` identifying explicit versus retained selection and a reference to the original confirmation. `retained_source_countdown_started` records readiness-gated continuation. Reusing the choice does not reuse old source samples or mark failed input valid. Legacy configurations and raw historical records are not migrated. Export receipts, final questionnaire requirements and journal close boundaries remain unchanged.
