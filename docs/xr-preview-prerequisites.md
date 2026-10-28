# Headset preview prerequisites — proposal, not installed

The current preview is desktop-only: ordinary cameras, keyboard input, no XR loader or tracked camera. A Quest 3 preview could later use the existing Windows Meta Link runtime for stereo display and head pose. This would not replace VIVE Pro Eye, supply its eye tracking, or validate the research apparatus. The system OpenXR runtime and drivers have not been changed.

For the project's **6000.0.48f1** baseline, the following exact package combination is present in another existing local project's lockfile/cache and satisfies its declared dependencies:

| Official Unity package | Candidate version | Declared minimum Unity |
|---|---:|---:|
| `com.unity.xr.openxr` | 1.17.1 | 2022.3 |
| `com.unity.xr.management` | 4.7.0 | 2022.3 |
| `com.unity.xr.core-utils` | 2.5.2 | 2021.3 |
| `com.unity.inputsystem` | 1.19.0 | 2022.3 |
| `com.unity.xr.legacyinputhelpers` (indirect) | 2.1.12 | 2019.4 |

Official [Unity 6000.0](https://docs.unity3d.com/6000.0/Documentation/Manual/com.unity.xr.openxr.html) and [6000.3](https://docs.unity3d.com/6000.3/Documentation/Manual/com.unity.xr.openxr.html) package lists include OpenXR 1.17.1. The exact baseline Editor is absent on RICHARD; installed **6000.3.25f1 in an isolated copy** is therefore a candidate for testing this combination. These are metadata/source compatibility checks, not an executed integration result.

Do **not** copy that older cached combination into 6000.6.7f1. Core Utils 2.5.2 contains an unguarded `GetInstanceID()` Editor call, which conflicts with the API removal already encountered in this project. Its Legacy Input Helpers dependency also requests `com.unity.modules.vr`, removed in Unity 6.5. [Unity upgrade notes](https://docs.unity3d.com/6000.6/Documentation/Manual/UpgradeGuideUnity65.html) and the [6000.6 OpenXR package list](https://docs.unity3d.com/6000.6/Documentation/Manual/com.unity.xr.openxr.html) indicate a newer path; OpenXR 1.18.0 is the listed released baseline, but its complete compatible dependency set has not been verified here.

The minimal project work would enable the Windows Standalone OpenXR loader and add an XR Origin with a head-pose-driven camera, preserving separate roles and the simulation gate. Meta XR's full SDK, AR Foundation, XR Interaction Toolkit and Android SDK/NDK/JDK are not prerequisites for this limited PC Link display/head-pose preview. [OpenXR supported runtimes](https://docs.unity3d.com/Packages/com.unity.xr.openxr@1.17/manual/index.html), [Unity Link setup](https://docs.unity3d.com/Packages/com.unity.xr.meta-openxr@2.1/manual/get-started/link.html).

## Installation and permission boundary

No packages were added to this project. Adding cached packages is still a project dependency installation and remains pending the requested installation decision. Cache license files identify Unity Companion License and, for OpenXR distribution, Unity Package Distribution License; no new agreement was accepted. The Input System backend may require an Editor restart. [Official Input System installation](https://docs.unity3d.com/Packages/com.unity.inputsystem@1.19/manual/Installation.html).

Meta Link is already installed and registered as the current OpenXR runtime, but the user's headset connection and any required developer/account setup have not been verified. Do not create accounts, accept new agreements, change runtime or grant tracking permissions silently. [Meta Link development setup](https://developers.meta.com/vr/documentation/unity/unity-link/).

The next permitted development step after the installation decision is an isolated, explicitly labelled headset-preview project. Its success criterion is actual visible stereo rendering and tracked head movement; a successful build alone is insufficient. Real Pro Eye acquisition and formal apparatus acceptance remain separate work.
