# Synthetic writer fixture follow-up

The first main CI run at `e2454341ac23a98ac8267ca3ff77afa87ede185f` passed Ubuntu and CLR4, but Windows reported one failed assertion in **separate synthetic sessions receive distinct tokens**. The [original run](https://github.com/MingyuanZhu7719/avatar-gaze-stroop-demo/actions/runs/43182961290) is retained.

## Contract and diagnosis

`ViveSessionJournalAsyncWriter.Finish()` differs from the worker admission API repaired earlier. A contended queue gate makes Finish return `false` and terminate the synthetic writer as Faulted or Uncertain. It is not a no-effect Busy result and must not be retried.

The old token fixture constructed two background workers and immediately asserted `x.Finish() && y.Finish()`. Worker startup can briefly own a queue gate, so the test can fail before comparing tokens; the conjunction can also skip the second Finish call. The CI's single `Expected true` message does not itself establish which Boolean assertion failed or demonstrate a GUID collision. The token is an instance `Guid.NewGuid()` value, not shared static state.

An independent probe compiled the unchanged writer. Forced queue contention produced one failed Finish request, terminal Faulted state, detached queue, an unclean header-only prefix and no flush/dispose. A separate controlled setup paused both first header writes outside queue gates. Twenty pairs each called Finish exactly once, reached Closed and retained valid hash chains with distinct decoded tokens. This reproduces the scheduling-sensitive precondition while preserving the actual token assertion.

## Test-only repair

Positive enqueue/finish fixtures pause the first header write outside the queue gate before issuing their single owner requests. The helper preserves existing target hooks and releases/withdraws/joins its worker during cleanup. The token case requests both finishes separately, then checks Closed and the actual decoded token inequality.

A separate held-queue-gate test deliberately calls Finish once during a reserved header write. It requires prompt `false`, detached queue, sticky Uncertain state and no successful close/flush/dispose. No helper retries a failed Finish and no runtime behavior is relaxed.

Only the test fixture and documentation change. Runtime writer source remains SHA256 `991f49928b8cfafa41aafec719d512a66092e8c72361550d0451410254e41f20`; VIVE application inputs and launchers are unchanged. Existing participant/operator packages require no rebuild. Real HMD, two-PC LAN and complete-recording acceptance remain separate.

Local validation: **19 focused cases passed**, **1,836 full regression cases passed**, and the controlled Finish-failure reproduction passed with distinct decoded tokens. Independent original-source probing also completed 20 controlled writer pairs. Focused cases are included in the full count; pairs are separate probe schedules.

Final focused/full results and the exact latest main CI are recorded in the PR and task completion record; previous successful PR runs are not substituted for main verification.
