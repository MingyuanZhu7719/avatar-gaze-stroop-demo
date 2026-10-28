# Development architecture and verification

This is an implementation record, separate from the accepted [PRD](prd.md) and [experiment design](experiment-design.md). Development choices below are provisional apparatus adapters, not additions to the participant protocol. No participant data, headset verification, or Windows performance results are claimed.

## Installed toolchain and project structure

The available Mac has Unity **6000.0.48f1**, bundled Mono **6.13.0**, a working Roslyn C# compiler, and .NET runtime **6.0.21** without a .NET SDK. No tools or SDKs were installed and Unity licensing was not changed. `scripts/test.py` discovers an existing Unity installation; `STROOP_UNITY_CONTENTS` may point to another installed `Unity.app/Contents`. An existing .NET 8 SDK can also run `dotnet run --project AvatarStroop.Regression.csproj`.

The portable C# core lives in `Assets/Stroop/Core`, so the same source can be compiled in Unity and the independent regression executable. The core has no dependency on Unity frame timing, XR, a headset SDK, or external packages. Presentation adapters must acknowledge actual stimulus presentation with the current trial ID; generating a planned trial alone does not start RT. Production apparatus timing requires measurement of the eventual adapter's timestamp semantics and uncertainty.

Planned adapter boundaries are: participant application owns the task and first-response input; independent operator application captures its own head/eye stream; a shared avatar presentation interface accepts either the automatic source or the locked operator source; participant gaze remains a separate measurement stream. Simulated transport must be visibly labelled and stored in simulation sessions. A simulator is not real WoZ. There is no automatic fallback between sources.

## Milestone 1: deterministic task core

Implemented: constrained 24-trial lists, four Williams orders, independent B initial-target balancing, varying list assignment, practice with correction/Continue and at most two attempts, owner-only block configuration, presentation acknowledgement, press-edge input, fixed questionnaire wording, separate ratings, additional 30-second minimum rest including after the fourth block, explicit pause/resume, and structured in-memory events.

All four main lists use the same multiset and are distinct. Planning uses a versioned deterministic PRNG; list IDs encode algorithm/index/derived seed and the session records the original seed. Allocation IDs include algorithm version, roster count, seed, and slot. Freeze the generated roster before use: generating a different roster size is a different schedule, not an extension of the old one. The planner accepts no performance inputs and does not reassign after withdrawal.

The owner's role is an application-boundary capability, without a new account system. This core checks the role on configuration methods; the participant interface must not expose owner commands. The accepted assigned condition/order/B target cannot be overridden through block configuration. Configuration records the chosen source and requires an adapter readiness result before countdown; resumption during main task or its countdown also requires reacquisition. Real readiness is not implemented by a test boolean.

The engine hides task/avatar during explicit pause, freezes active time, flags an interrupted trial without correctness or RT, and resumes the next planned trial in a new segment after the same configured countdown. A pause on the final planned main trial resumes into ratings without adding a trial. Acquisition during a pause, if later implemented, is diagnostic rather than active exposure.

## Explicit unresolved boundaries

- Countdown duration and questionnaire language have no hidden study defaults. A caller must provide both; tests use **1 second** and English strictly as engineering fixtures. Confirm the participant protocol before use.
- Practice interruption scoring is not specified in the baseline. An interrupted scored-practice trial remains interrupted; resumption enters `NeedsProtocolDecision` instead of inventing a denominator, incorrect response, replacement, or extra attempt. Withdrawal and technical termination remain available. Ordinary main-task pause/resume is implemented.
- `FlagTechnicalIssue` marks an audit issue without selecting an automatic pause/recovery policy. Detection thresholds, notification side/content, automatic suspension, and recovery decisions remain unresolved. Production use must remain gated until these are frozen and validated.
- Non-main operator views currently expose no task/performance detail. Main operator view has the same current trial ID/stimulus and a participant-avatar visibility request; avatar appearance/drive remain unresolved. The operator-side D and non-main views are explicitly marked `OperatorViewProvisional`; the D visibility request is a development placeholder, not an accepted display decision. No reverse participant eye, body, hand, or voice synchronization is implied.
- This milestone contains view models and in-memory audit data, not a Unity scene, persistent export, real tracking, or two-computer transport. Subsequent milestones implement and test those adapters separately.
- No research endpoint, sample size, RT exclusion rule, significance criterion, fault timing threshold, or calibration pass criterion is introduced.

## Verification evidence

Run from repository root:

```sh
python3 scripts/test.py
```

On 28 October 2026 this compiled the actual C# sources with the installed Unity Roslyn compiler (C# 7.3, warnings as errors), then executed them with the bundled ARM64 Mono runtime: **28 test groups passed, 0 failed**. Property loops exercise many seeds and roster sizes; full-session regression completes 96 main trials plus all four questionnaires and rests. See [requirement mapping](requirements-tests.md).

The .NET project is provided for an existing SDK/CI, but no .NET SDK run has been performed on this Mac. Unity Editor import/build, rendered VR UI, real eye/head tracking, Windows builds, two physical computers, and VIVE Pro Eye measurements have **not** been run or verified.
