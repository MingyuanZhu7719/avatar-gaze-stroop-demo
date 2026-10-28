# Windows development verification — 28 October 2026

This checks the existing implementation on the newly connected Windows computer. It does not admit real apparatus sessions or change the approved experiment parameters. The source baseline was `26dee346c3c466139894c5c78acb6008c3389301`; all 69 files were obtained through the authorized GitHub connector and verified against their Git blob hashes. Local Git was installed but had no usable GitHub credential, so no login or credential creation was attempted. The local directory is a verified source snapshot, not a Git clone.

## Machine and installed tools

| Item | Observed result |
|---|---|
| Computer | RICHARD, ASUS ROG Zephyrus G14 GA403UV |
| CPU | AMD Ryzen 9 8945HS with Radeon 780M Graphics |
| Discrete GPU | NVIDIA GeForce RTX 4060 Laptop GPU; `nvidia-smi` reports 8188 MiB; driver 560.70 |
| Installed RAM | 32 GiB, verified from physical-memory entries |
| Git | 2.53.0.windows.3 |
| Unity Hub | 3.22.4 |
| Installed Editors found | 6000.6.7f1, 6000.3.25f1, 2022.3.62f3 |
| Editor used for this check | 6000.6.7f1, existing Windows Mono build module |
| Project baseline Editor | Still 6000.0.48f1; this exact version was not found locally |
| SteamVR | Installed; Steam manifest build ID 25998080. The executable's placeholder file version is not a confirmed release version |
| Default OpenXR runtime | Meta/Oculus for both registry views; SteamVR runtime files exist but were not selected |
| VIVE eye tracking | No VIVE/SRanipal/Tobii installation found in the checked locations; no connected HTC/Valve/VIVE USB device detected |

The active Windows Editor license was verified by a successful actual batch import, scene generation and builds. No software was installed; no credentials, license activation, OpenXR setting, firewall rule or access permission was changed. Older Unity/VR projects were only inspected, not opened or modified.

## Corrections

The shared Python compiler discovery previously searched only the macOS Unity Hub layout. On Windows it now searches the standard installed Hub directory and runs `mono.exe`, not the non-Windows `mono` file that is also shipped in that directory. macOS discovery is preserved; Linux's default Hub path is supported. `STROOP_UNITY_CONTENTS` can pin an existing `Editor/Data` directory on Windows/Linux or `Unity.app/Contents` on macOS. An explicitly invalid or empty value fails clearly instead of silently choosing another installation. The integration runner's `--dotnet` option bypasses Unity discovery.

Unity 6000.6.7f1 rejected `Material.GetInstanceID()` with CS0619 during the actual Editor import. The scene generator now deduplicates material objects using `HashSet<Material>`. Existing material-asset checks and scene-preservation behavior remain unchanged. This avoids the removed call without selecting a new project baseline or changing the generated experimental layout.

## Executed verification

| Check | Actual result |
|---|---|
| Full portable C# regression on RICHARD | **138 passed, 0 failed**, compiled with the installed Unity Mono/Roslyn compiler, C# 7.3, warnings as errors |
| Python discovery regression | **11 passed**, including Windows launcher choice, pinned/invalid/empty paths, incomplete installations, macOS/Linux layout, and explicit `--dotnet` bypass |
| Managed Unity compile | Passed: 17 core/runtime sources, five Unity runtime sources, one Editor source; real 6000.6.7f1 assemblies, warnings as errors |
| Independent-process integration | Passed: fault injection, matched task identities, safety-stop acknowledgement, explicit post-exit tick, partial CSV manifests, all four clean journals and forced-termination recovery |
| Separate owner console | Compiled and printed usage; no-argument exit 2 as expected |
| Actual Editor import and scene generation | Passed after the material fix, exit 0; both participant and operator scenes created |
| Saved scene reload | Both scenes reopened with one camera, view and bootstrap of the correct role; serialized camera/avatar references intact; 14 renderers per scene with material/shader references |
| Windows x64 Mono development player builds | **Both succeeded: 0 errors, 0 warnings** |

The Editor checks used a separate disposable project copy under the workspace, because the installed Editor differs from the project baseline. They did not upgrade the source snapshot's `ProjectVersion.txt`. Only the project's three built-in modules were present; no XR packages were added. A local verification harness reopened each scene, checked references and shader errors, then called `BuildPipeline.BuildPlayer` separately for the two roles using the installed Windows Mono module. This establishes builds with 6000.6.7f1, not compatibility testing with the uninstalled 6000.0.48f1 Editor.

The initial restricted-sandbox regression reported 127 passes and 11 failures involving loopback UDP and temporary export files. The same baseline binary passed all 138 groups with the authorized execution access needed for those operations. Those initial failures were not hidden or resolved by weakening assertions. Full Editor execution also required the authorized execution environment. No automatic approval rejection occurred.

Commands for an environment with the existing tools:

```text
python scripts/test_unity_tools.py
python scripts/test.py
python scripts/compile-unity.py
python scripts/integration.py
python scripts/owner.py
```

The first four commands should exit 0. The last intentionally prints usage and exits 2 without arguments. Local logs remain in `build/windows-*.log`. The workspace also retains `snapshot-provenance.json`, `unity-editor-import.log` (the original compiler failure), `unity-editor-import-fixed.log`, `unity-windows-build.log`, the isolated project, and separate player outputs. They contain synthetic test evidence, not participant data, and are not uploaded wholesale to GitHub.

## Not yet verified

The player launch log reached Direct3D 11 initialization on the RTX 4060. The subsequent native window inspection encountered a per-application computer-use approval prompt for `avatarstroopparticipant`; no usable participant-frame screenshot was obtained, and no approval prompt was operated by the agent. **Desktop visual correctness, rendered owner commands, camera acknowledgement/culling behavior and headset display remain unverified.** Build success is not visual acceptance.

Real VIVE Pro Eye acquisition, participant gaze, hardware calibration, controller mapping, two physical computers/headsets, VR fault visibility and physical latency/synchronization measurements remain pending. The application still rejects `RunKind.Apparatus`. PRD AC-06 and AC-08 are not passed by these checks.

The next hardware step is to connect one VIVE Pro Eye to RICHARD and verify that SteamVR recognizes it, then confirm the lab's installed/approved eye-tracking runtime and SDK. The current Meta OpenXR selection and missing VIVE eye components must be resolved through the intended device setup, not silently changed by this check. Two-computer/two-headset acceptance follows after the single-device acquisition path works. The study owner's unresolved fault/recovery and protocol decisions remain open.
