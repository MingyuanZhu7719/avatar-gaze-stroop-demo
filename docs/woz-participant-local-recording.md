# Current Legacy WoZ participant-local recording

This is the explicitly selected A/B/C/D WoZ path, including authenticated, role-local processes and controlled-scene dependencies. It is available for Ink colour, Word meaning and Visual search in a WoZ-enabled [Vive-Study participant package](vive-participant-recording.md), as well as the developer package. Software/build checks do not establish hardware or study validation.

## Data flow and consent

Recording is off by default. Ordinary previews do not acquire recording authority. The separate controlled Windows XR development build requires the controlled-recording, Legacy-raw and Legacy-WoZ recording options together. Runtime checks still require the participant's owner notice, both headset choices, explicit SDK/source starts and exact current source authority.

The operator must separately accept head/eye forwarding and participant-local storage of mapped operator poses and source times. Starting forwarding alone does not imply storage permission. The participant can stop or withdraw; source loss, stale evidence and revocation fail closed without Automatic fallback or automatic resume.

The operator receives the current task. A participant who enables live avatar sharing also sends classroom-aligned head orientation and combined head-local gaze for display during active WoZ trials. This choice is independent of local recording. The operator sees the participant from the evaluator's seat, with the board and participant in the same classroom coordinate frame. A missing, invalid or expired pose hides the avatar until fresh tracking returns. Participant answers, reaction times, questionnaire ratings, participant raw eyes and files are not sent to the operator. The operator writes troubleshooting logs, but does not record experiment task/raw-eye files. Pairing keys, tokens, offer contents and packets are not journaled.

To check the two-way view, use matching participant and operator builds on two PCs. After classroom alignment, choose live avatar sharing in the participant headset, then use [easy pairing](woz-easy-pairing.md). During a formal trial, the participant can face the board and look first at the word and then at the evaluator with eyes only. Repeat with a head turn. The operator should see separate eye and head movements. Keep following the assigned condition when delivering the experiment. How accurately an operator can recognize either target still needs a two-headset check.

At block preparation, the participant enters the Photon room code and clicks **Join room**. The operator clicks **Accept this participant** after checking the intended PC shows the same code. Current clocks and the private offer are exchanged automatically. Forwarding/storage permissions and **Connect to participant** remain explicit. **Begin current block** stays a separate readiness-gated participant action. See the [room workflow](woz-easy-pairing.md).

Photon room traffic uses encrypted WSS through the third-party Photon cloud. Raw recordings, answers, RT and ratings stay local. **Advanced: manual LAN connection** is a separate authenticated but unencrypted fallback. Neither mode changes firewall permissions or global XR settings. Local and remote clocks remain distinct; source timestamps alone do not establish synchronization or physical timing.

## Local records

Version 5 adds the developer WoZ context and operator-pose provenance. Version 7 combines this WoZ provenance with the assigned participant ID, task, allocation and software version; assigned Automatic files retain version 6. Applied presentation evidence is committed through the existing participant-local recorder. Native file access retains the revocable admission, identity, budget, private-stage, close and independent-readback checks.

Typed WoZ evidence v3 adds the observed software display time, original pose receipt age, 150 ms hold threshold, selected receipt cap and FRESH/HELD/CLEARED state. The approved Photon forward cap is 300 ms; original timestamps are retained while a pose is held. The analyzer exports `woz_pose_holds.csv` with observed bounds and explicit recovery/clear/open outcomes. A hold is not a new operator or participant eye acquisition. Evidence v1/v2 remain readable without inventing hold status for historical files.

A short one- or two-trial stop produces at most a raw prefix. It is not normal session completion and cannot authorize a completed-session CSV. Do not run four blocks merely to check withdrawal. A later full session needs normal completion and successful local verification before a completed export. No claim of durable storage or remote operator cleanup acknowledgment is made.

## Public validation and boundaries

Use the [development verification guide](development.md#verification), the exact source revision and the generated package manifest to identify what was checked. The bounded public test entry point is:

    python scripts/test-public-woz.py --static-only
    python scripts/test-public-woz.py --unity-data "/path/to/existing/Editor/Data"

The runner requires Python 3.10+, an already installed Unity Mono compiler, and .NET Framework 4.8-compatible reference assemblies. Windows references are discovered through SystemRoot; --framework-references accepts an explicit existing directory. Cross-platform managed execution has not been validated. It installs nothing, runs only the listed source/mock and memory suites, and writes compiler/test artifacts under a unique build/public-woz-checks directory. It does not launch Unity, a player, SDK, native-recording fixture, socket, LAN or device. The two-process loopback source is compiled only as a dependency; its entry point is not called.

Existing machine-only native fixture roots and allowlists are empty and explicitly rejected. No generalized path authorization replaces them. Three machine-specific native fixture executables and private build/native scripts are omitted. The public runner's narrow CS0649 exceptions are pinned to exact reviewed fixture declaration lines and remain visible in logs; other warnings fail compilation.

Build both roles from the same reviewed revision and retain their generated manifests with the local packages. A managed API compilation does not validate a player or physical two-PC operation. Eye-acquisition and AOI evidence have their own [sampling and timing contract](vive-eye-acquisition-cadence.md); source availability and a local rehearsal do not establish scientific validity, spatial accuracy or durability. Private recordings, logs and player binaries are not distributed here.

## Wire compatibility

WoZ wire v3 requires matching builds on both computers. It carries task kind, response rule and the complete frozen Visual search layout, plus the participant's array-presentation stage. The operator first sees the target, then the array after the participant's software onset has been acknowledged and synchronized. Network transit delays still apply; this is not an optical simultaneity guarantee. New Photon rooms use a separate app version from older builds, which cannot pair with them.

`WireCodec.Number` preserves round-trip `R` spellings only when invariant parsing returns the original double bits; otherwise it uses a verified `G17` spelling. Signed zeros normalize to `0`. This addresses the [.NET Framework round-trip formatting limitation](https://learn.microsoft.com/en-us/dotnet/standard/base-types/standard-numeric-format-strings#round-trip-format-specifier-r). Canonical-byte checks, authentication and replay rejection remain. `Tests/WozAuthenticatedLanTests.cs` and `Tests/WozAuthenticatedControlTests.cs` retain the executable compatibility regressions.

