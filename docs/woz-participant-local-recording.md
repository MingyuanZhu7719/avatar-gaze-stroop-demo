# Current Legacy WoZ participant-local recording

This is development source for an explicitly selected Legacy WoZ mode, including the earlier authenticated, role-local process and controlled-scene dependencies. It is not a validated study release.

## Data flow and consent

Recording is off by default. Ordinary previews do not acquire recording authority. The separate controlled Windows XR development build requires the controlled-recording, Legacy-raw and Legacy-WoZ recording options together. Runtime checks still require the participant's owner notice, both headset choices, explicit SDK/source starts and exact current source authority.

The operator must separately accept head/eye forwarding and participant-local storage of mapped operator poses and source times. Starting forwarding alone does not imply storage permission. The participant can stop or withdraw; source loss, stale evidence and revocation fail closed without Automatic fallback or automatic resume.

Only response-free task context goes to the operator. Participant answers, reaction times, questionnaire ratings, participant raw eyes and files are not sent to the operator. The operator does not record files. Pairing keys, tokens, offer contents and packets are not journaled. Keep temporary offers private; do not publish them or test data.

Messages are authenticated but unencrypted. Use only an explicitly approved isolated laboratory LAN and exact paired endpoints. This source does not configure firewall permissions, change global XR settings or authorize public-network use. Local and remote clocks remain distinct; source timestamps are not evidence of clock synchronization or end-to-end physical timing.

## Local records

Version 5 adds a separate Legacy WoZ context and operator-pose provenance while preserving the named and Legacy-local formats. Applied presentation evidence is committed through the existing participant-local recorder. Native file access retains the revocable admission, identity, budget, private-stage, close and independent-readback checks.

A short one- or two-trial stop produces at most a raw prefix. It is not normal session completion and cannot authorize a completed-session CSV. Do not run four blocks merely to check withdrawal. A later full session needs normal completion and successful local verification before a completed export. No claim of durable storage or remote operator cleanup acknowledgment is made.

## Public validation and boundaries

See [current evidence](woz-recording-public-evidence-20261110a.md) and the root public manifest for exact current source hashes and checks. The public test entry point is:

    python scripts/test-public-woz.py --static-only
    python scripts/test-public-woz.py --unity-data "/path/to/existing/Editor/Data"

The runner requires Python 3.10+, an already installed Unity Mono compiler, and .NET Framework 4.8-compatible reference assemblies. Windows references are discovered through SystemRoot; --framework-references accepts an explicit existing directory. Cross-platform managed execution has not been validated. It installs nothing, runs only the listed source/mock and memory suites, and writes compiler/test artifacts under a unique build/public-woz-checks directory. It does not launch Unity, a player, SDK, native-recording fixture, socket, LAN or device. The two-process loopback source is compiled only as a dependency; its entry point is not called.

Existing machine-only native fixture roots and allowlists are empty and explicitly rejected. No generalized path authorization replaces them. Three machine-specific native fixture executables and private build/native scripts are omitted. The public runner's narrow CS0649 exceptions are pinned to exact reviewed fixture declaration lines and remain visible in logs; other warnings fail compilation.

Private participant and operator packages were compiled before this source publication. Their recorded evidence is historical and separate from fresh checks of this sanitized source. Public managed API compilation is not a reproduction of those private player binaries. Physical two-PC LAN, actual HMD visibility/alignment, real SDK/gaze acquisition, accuracy, sampling completeness, scientific validity and durability are not accepted. No real-user recording was performed for public preparation.

