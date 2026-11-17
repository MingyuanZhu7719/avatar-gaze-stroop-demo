# Local study data analyzer

`AvatarStroopAnalyzer.exe` is a Windows desktop tool for completed participant-local **VRAW** recordings and their **readable analysis CSV** exports. It runs locally with the installed Windows .NET Framework 4.8; no account, Python installation or upload is needed. The separate fixed-target validation CSV is a different schema and is rejected with a message.

Keep each executable with its matching `.exe.config` file when moving the analyzer package. Both the GUI and CLI support original input paths longer than 260 characters without moving or copying recordings; Windows extended-path handling is confined to opening the original read-only stream, and source names and hashes remain unchanged.

## Use

1. Finish the participant session and allow recording/export cleanup to complete.
2. Open the analyzer, select one or more completed files, and choose an output parent directory. If the Windows picker rejects a long path, paste the full path into **完整路径** and click **添加路径**; quoted paths copied from Explorer are accepted. Select **one file per session**: VRAW is preferred because its record chain can be checked. Selecting both the VRAW and its CSV would duplicate a session and is rejected.
3. Click **开始分析**, then **打开报告**. Each successful run creates a new `StroopAnalysis-*` folder. Input files are opened read-only. Rejected, incomplete or cancelled reads do not publish a report.

The report links to these tables:

| File | Contents |
| --- | --- |
| `trials.csv` | Reconstructed trials with `Tutorial`, `PracticeExam`, legacy `Practice`, `Formal` or `Unknown` scope, training protocol, exam attempt and tutorial round. Includes condition, segment, task kind, response rule, correctness, software timing and exclusion flags. Search adds set size, target/selected item, outcome, search-and-selection duration and separate timeout timestamps; Stroop retains word/ink. Interrupted attempts and retries remain separate rows. |
| `blocks.csv` | Counts, accuracy and correct-response RT mean/median/sample SD/p95. Stroop is separated by session/block/rule/congruence; Visual search by session/block/set size (12 or 24), with timeout counts. Training scopes, exam attempts and tutorial rounds stay separate. |
| `training_attempts.csv` | One row per recorded practice completion or identifiable exam attempt. Preserves declared score, pass threshold, pass result and completion status alongside reconstructed eligible/correct trial counts. Missing completions, duplicate summaries and contradictory results are flagged. |
| `contrasts.csv` | Stroop-only incongruent minus congruent RT differences within each formal block/rule, with both sample counts. No search trials enter this table. |
| `visual_search_items.csv` | Every recorded search layout: item identity, six-column/four-row grid slot, colour, pattern and target flag. Layouts are decoded from the recorded payload, not reconstructed from a seed. These are layout coordinates, not measured eye AOIs. |
| `visual_search_events.csv` | Recorded controller-ray hover changes and click misses, with trial/context and local event time. Separate `action=cue` rows preserve target-preview first Unity render time, rendered elapsed seconds and the software-camera timing basis; these are not controller or photon observations and do not change array-onset reaction time. A hover does not establish that the participant found or fixated an item. Submitted item choices remain in `trials.csv`. |
| `ratings.csv` | Individual questionnaire responses, language, version and duplicate/invalid-value flags. No composite is invented. |
| `eye_quality.csv` | Native acquisition counts, observed callback receipt frequency, ray validity, SDK calibration/runtime states and separate geometry-observation/quality-marker counts. |
| `aoi_observations.csv` | Active-task valid geometry observation counts and proportions. These are not fixations or dwell duration. D/no-avatar eye/face measures are not applicable. |
| `sources.csv` | Input SHA-256, session, source/run kind, recorded block completion, chain verification status and analyzer version. |
| `event_counts.csv` | Task event counts, including pauses and transitions. |
| `methods.txt` | Exact default processing and interpretation rules. |

Empty statistics mean unavailable; they are not zero. Block indices are zero-based; conditions come from recorded allocation rather than assuming A/B/C/D order. Session IDs are software identities, not authenticated participant IDs. Batch analysis retains separate sessions and does not perform between-participant inference.

## Default processing

- Tutorial, practice exam and legacy practice are separate from formal trials; only `scope=Formal` enters formal behavioural summaries and contrasts. Unknown or contradictory learning stages are not treated as formal. Accuracy requires a single unambiguous plan/presentation/response, known rule/colours and no interruption or correctness contradiction. Missing responses are not counted as errors.
- `training_protocol=tutorial-exam-v1` identifies the new unlimited tutorial / 10-question exam flow. `practice_attempt` and `tutorial_round` preserve repeats. An exam passes at eight correct first responses. The report displays the recorded result and separately checks it against available trial evidence; it does not invent a pass for an unfinished exam. Historical eight-question practice retains its original six-correct threshold, explicitly labelled as inferred when the original event omitted it.
- RT summaries use correct eligible trials with finite positive RT and consistent task-time declarations. There is **no automatic outlier trimming**. The response software timestamp is reconstructed from the response's declared onset plus RT; log emission time is retained separately. Software/render timing is not measured photon exposure.
- `InkColor` uses rendered ink as the answer. `WordMeaning` uses the word's meaning. Old logs without a rule are labelled `legacy_inferred` InkColor; the modes are never pooled into one contrast.
- `VisualSearch` is a separate task, displayed as `SelectTarget` in the analysis response-rule column. It has no Stroop word, ink or congruence. Correctness is recomputed from the selected and target item IDs after checking the full recorded layout and metadata. A valid explicit timeout counts as incorrect for search accuracy and is reported separately; an unfinished/missing response is not assumed to be a timeout. Correct-response RT summaries exclude timeouts and wrong selections. `search_selection_seconds` includes finding and controller selection; it is not pure search latency. Timeout timestamps do not populate response timestamps.
- VRAW verification checks record integrity and a source terminal. CSV parsing checks its structure and exact raw-encoded fields but does not cryptographically re-verify the original VRAW chain. A source terminal does not prove all planned trials were completed.
- Native acquisitions have no reliable task/block alignment, so native frequency is reported for the whole source, using local callback receipt times. Geometry observations retain their recorded task context. Repeated native identities in observation rows are not new native acquisitions.
- Calibration `ReportedReady` is an SDK status, not a spatial accuracy result. Fine eye/face AOI validation is [deferred](TODO.md). No gaps are interpolated and no fixation algorithm is applied.

The [Visual search development task](visual-search.md) defines the first version's stimulus, selection and allocation contract. Its board/item geometry does not establish validated object-level eye tracking.

For perceived-evaluation research, freeze eligibility, trial/participant exclusion criteria, RT outlier rules and allowable missingness before inferential analysis. Check condition differences in missingness/interruptions and preserve the questionnaire version. `observed`, `evaluated` and `negative_evaluation_worry` are individual custom items, not an automatically validated scale. Existing development recordings remain labelled with their original run/source scope.

## Build and command line

```powershell
python scripts/build-study-analyzer.py --editor C:/Unity6000.3.25f1/Editor/Unity.exe --output C:/path/to/analyzer
C:/path/to/analyzer/AvatarStroopAnalyzer.Cli.exe --input C:/data/session.vraw --output C:/results
```

Repeat `--input` for multiple sessions. The command exits nonzero if an input is invalid, incomplete, locked for writing or a session is duplicated. It prints the report path on success. Both executables use the same analysis implementation.
