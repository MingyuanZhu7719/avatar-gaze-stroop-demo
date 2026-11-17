# Visual search development task

Visual search v1 is an additional task in the existing Vive classroom. Select **Visual search** before configuring a session. It uses the existing Legacy A/B/C/D **Automatic** flow, a task-specific tutorial and practice exam, five-second countdowns, block-end fireworks, questionnaire, rests and final thank-you. Named and live WoZ search are not enabled in this first version. Existing Ink colour and Word meaning sessions remain separate task modes.

## Search and selection

The target is a coloured, patterned backpack. Each trial begins with a **1.5-second target preview**, followed by the search array; the target example remains above the search board. Each trial contains exactly one matching object, plus distractors sharing either its colour or its pattern. Colours are Red, Green, Blue and Yellow; patterns are Plain, Striped, Dotted and Cross. Participants search a fixed front-facing six-column/four-row board using one Vive wand, point at an object and press the trigger to submit.

All objects receive the same hover outline. Clicking anywhere within an object's hit area selects the whole object; hit areas do not overlap. A click on empty board space is logged and does not submit an answer. Formal and exam trials end on the first valid object selection, including an incorrect choice. Tutorial responses show correctness feedback and the correct target when needed, followed by the separate continue/enter-exam menu. The exam gives its score only at the end. Eye tracking does not select objects.

The development timeout for exam and formal trials is **30 seconds from the recorded presentation acknowledgement**. It is an initial pilot setting, not a frozen research exclusion rule. A timeout ends the trial and is recorded explicitly; it counts as an incorrect exam answer. Tutorial has no per-question deadline. A missing or interrupted recording is not labelled as a timeout.

## Allocation

- Tutorial: unlimited questions using the same object-matching and controller rules, with feedback after each answer. The participant explicitly chooses when to enter the exam.
- Practice exam: ten trials, five with 12 objects and five with 24. At least eight correct first responses passes. Failure returns to tutorial, and a new exam can be attempted without a fixed retry limit. This is `tutorial-exam-v1`; historical eight-item, six-correct practice recordings remain readable under their original protocol.
- Formal task: four blocks of 24 trials, each containing 12 trials at each set size; 96 formal trials in total.
- The current development condition order remains **B, C, A, D**, with stimulus lists 0–3. Search does not silently introduce a different study counterbalancing schedule.
- The four lists use distinct deterministic layouts. Each formal list visits all 24 target slots once; the assignment of set size balances rows, columns and board halves. Object size stays fixed across set sizes.
- The avatar follows the existing Automatic A/B/C/D rules. Search difficulty and target position are not used to drive its gaze, so its task-directed gaze is not an answer cue.

## Recording and analysis

Search task events explicitly declare `task_kind=VisualSearch` and `search_schema=visual-search-v1`; they do not invent Stroop word/ink answers. A prepared block contains an identity/hash for every layout. Each actual `trial_planned` event records the full canonical `visual_search_layout`, target attributes, target item, set size and layout seed. Readback checks the layout against its prepared identity. The raw and readable CSV retain the same event payload.

`stimulus_presented` retains the existing software presentation timing contract and acknowledges the search array, after the target preview. `visual_search_cue` separately records the first Unity cue-render time and accumulated rendered cue duration, using software camera callbacks rather than optical measurement. `first_response` stores the selected item, correctness, onset and search-plus-controller-selection duration. The primary duration runs **from array onset to the submitted selection**, excluding the 1.5-second preview; it includes both search and controller selection and is not pure search time. `trial_timed_out` stores elapsed time separately. `visual_search_pointer` records hover transitions and blank clicks. Hover is a controller observation, not a fixation or a measure of discovery.

The local analyzer adds task/set-size columns, object layout and pointer-event tables, and separate 12/24 summaries. It does not compute a Stroop congruence contrast for search. See [analyzer processing rules](study-analyzer.md). Existing eye acquisition and board/avatar AOIs continue to use their separate recording contract; fine object-level eye accuracy has not been accepted.

## Vive acceptance

Before participant use, verify both set sizes are clearly readable, every object can be selected comfortably, hover does not reveal correctness, blank clicks do not submit, and trigger holds cannot answer a newly opened trial. Complete all four blocks and compare raw/CSV trial, item, response/timeout and questionnaire counts. Check countdowns, fireworks and final completion with the wearer. Software tests alone do not establish physical usability or research readiness.
