#!/usr/bin/env python3
"""Build an explicitly started VIVE Pro Eye hardware rehearsal in an isolated project.

Prepare with prepare-quest-preview.py first to reuse the existing cached official
Unity package set. Does not launch a player, install a vendor SDK, copy vendor
binaries, change the system OpenXR runtime or accept eye-data consent.
"""
import argparse
import datetime
import importlib.util
import os
from pathlib import Path
import subprocess
import sys
import tempfile
from windows_framework import compiler_command, runtime_sources

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("quest_prepare", Path(__file__).with_name("prepare-quest-preview.py"))
prepare = importlib.util.module_from_spec(spec)
spec.loader.exec_module(prepare)


def verify_output(source, project, output):
    if (not output.is_relative_to(source.parent) or output == source.parent or
            output == source or output.is_relative_to(source) or source.is_relative_to(output) or
            output == project or output.is_relative_to(project) or project.is_relative_to(output)):
        raise ValueError("Output must be a separate workspace directory outside both source and prepared project.")
    for parent in (output,) + tuple(output.parents):
        prepare.reject_link(parent)
    prepare.plain_tree(output)
    if output.exists() and any(output.iterdir()):
        marker = output / "vive-eye-preview-build.json"
        if not marker.is_file() or prepare.read_json(marker).get("project") != str(project):
            raise ValueError("Refusing to overwrite an unrelated nonempty output directory.")


def run_editor(editor, project, output, action, graphics=False):
    suffix = datetime.datetime.now(datetime.timezone.utc).strftime("%Y%m%dT%H%M%S%fZ")
    log = output / ("unity-" + action.lower() + "-" + suffix + ".log")
    command = [str(editor), "-batchmode", "-quit", "-projectPath", str(project), "-buildTarget", "Win64",
               "-executeMethod", "AvatarStroop.Unity.Editor.ViveProEyePreviewBuild." + action, "-logFile", str(log)]
    if not graphics:
        command.append("-nographics")
    print("Unity " + action + "; log: " + str(log), flush=True)
    result = subprocess.run(command, env=dict(os.environ, STROOP_VIVE_PREVIEW_OUTPUT=str(output)))
    text = log.read_text(encoding="utf-8", errors="replace") if log.is_file() else ""
    expected = {"Configure": "VIVE_PREVIEW_CONFIGURED:", "BuildWindowsPreview": "VIVE_PREVIEW_BUILD_COMPLETE:",
                "RenderFixture": "VIVE_PREVIEW_FIXTURE_COMPLETE:"}[action]
    if result.returncode or expected not in text:
        raise ValueError("Unity " + action + " failed or did not acknowledge completion; inspect " + str(log))


def compile_launcher(editor, source, output):
    data = editor.parent / "Data"
    mono, csc = data / "MonoBleedingEdge/bin/mono.exe", data / "MonoBleedingEdge/lib/mono/4.5/csc.exe"
    compiler = compiler_command(mono, csc)
    plan = source / "Assets/Stroop/Runtime/VivePreviewLaunchPlan.cs"
    tests = source / "Tests/VivePreviewLaunchPlanTests.cs"
    # Exercise the actual Windows child environment using a harmless console
    # executable, never the Unity player or an XR/native-eye process.
    with tempfile.TemporaryDirectory(prefix="vive-launch-env-", dir=output) as temporary:
        directory = Path(temporary)
        child, smoke = directory / "EnvironmentChild.exe", directory / "EnvironmentSmoke.exe"
        subprocess.run(compiler + ["-target:exe", "-define:VIVE_LAUNCH_ENV_CHILD",
                       "-main:AvatarStroop.Tests.VivePreviewEnvironmentChild", "-out:" + str(child), str(plan), str(tests)], check=True, cwd=source)
        sources = runtime_sources(source) + sorted((source / "Tests").glob("*.cs"))
        subprocess.run(compiler + ["-target:exe", "-main:AvatarStroop.Tests.VivePreviewLaunchEnvironmentSmoke", "-out:" + str(smoke)] +
                       [str(path) for path in sources], check=True, cwd=source)
        subprocess.run([str(smoke), str(child)], check=True, cwd=source, env=dict(os.environ, TEMP=temporary, TMP=temporary))
    launcher = output / "AvatarStroopViveEyePreview.exe"
    subprocess.run(compiler + ["-target:winexe", "-main:AvatarStroop.Apps.ViveEyePreviewLauncher", "-out:" + str(launcher),
                   str(plan), str(source / "Apps/ViveEyePreviewLauncher.cs")], check=True, cwd=source)
    return launcher


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--editor", type=Path, required=True)
    parser.add_argument("--project", type=Path, required=True)
    parser.add_argument("--output", type=Path, default=ROOT.parent / "vive-eye-steamvr-players-6000.3.25f1")
    parser.add_argument("--check-only", action="store_true")
    parser.add_argument("--render-fixture", action="store_true", help="Also save a synthetic Editor render, without live capture.")
    args = parser.parse_args()
    try:
        if sys.platform != "win32":
            raise ValueError("This hardware rehearsal targets Windows x64 only.")
        source, project, editor, output = ROOT.resolve(), args.project.resolve(), args.editor.resolve(), args.output.resolve()
        prepare.check_editor(editor)
        record = prepare.verify_prepared(source, project)
        verify_output(source, project, output)
        if args.check_only:
            print("VIVE rehearsal source, package, Editor and output checks passed. No program was launched.")
            return 0
        output.mkdir(parents=True, exist_ok=True)
        marker = output / "vive-eye-preview-build.json"
        provenance = {"format": 1, "project": str(project), "editor": str(editor), "editor_version": prepare.EDITOR_VERSION,
                      "source_files": record["source_files"], "packages": prepare.PACKAGES, "status": "configuring",
                      "explicit_capture_start_required": True, "records_samples": False, "headset_validated": False,
                      "eye_accuracy_validated": False, "vendor_binaries_bundled": False,
                      "runtime_selection": "SteamVR via child-process XR_RUNTIME_JSON", "system_runtime_changed": False}
        prepare.write_json(marker, provenance)
        run_editor(editor, project, output, "Configure")
        prepare.verify_prepared(source, project)
        run_editor(editor, project, output, "BuildWindowsPreview")
        prepare.verify_prepared(source, project)
        player = output / "UnityPlayer/AvatarStroopViveEyePlayer.exe"
        executable = compile_launcher(editor, source, output)
        prepare.verify_prepared(source, project)
        if not player.is_file() or not executable.is_file() or list(output.rglob("SRanipal.dll")):
            raise ValueError("Missing player or unexpected bundled vendor runtime DLL.")
        if args.render_fixture:
            run_editor(editor, project, output, "RenderFixture", graphics=True)
            prepare.verify_prepared(source, project)
        (output / "START-HERE.txt").write_text(
            "AVATAR STROOP - VIVE PRO EYE HARDWARE REHEARSAL\n\n"
            "This is a local live hardware diagnostic, not an experimental block or WoZ study.\n"
            "Keep the existing approved SteamVR/SR_Runtime setup and completed user calibration.\n"
            "Do not run this alongside an active participant preview or another foreground XR application.\n"
            "Open the OUTER AvatarStroopViveEyePreview.exe, then explicitly click Begin live preview on the desktop.\n"
            "Do not open UnityPlayer/AvatarStroopViveEyePlayer.exe directly. Keep that folder beside the launcher.\n"
            "The launcher selects the installed SteamVR runtime only for its child process before Unity starts.\n"
            "Expected standard Steam location: Program Files (x86)/Steam/steamapps/common/SteamVR/steamxr_win64.json.\n"
            "This leaves the user's configured system/Quest OpenXR runtime unchanged; no registry or persistent environment is written.\n"
            "Missing SteamVR files or an existing old/new VIVE preview block launch. No fallback or process termination is performed.\n"
            "The program does not accept eye-data consent or start calibration.\n"
            "Stop capture releases only this process's client; it does not quit SR_Runtime or SteamVR.\n"
            "The native HTC runtime is loaded from its installed location and is not included in this package.\n"
            "The board shows current channel states and aggregate counts only. This preview does not save raw samples or send network packets.\n"
            "The already installed HTC runtime manages its own local device IPC and diagnostic logs.\n"
            "The cyan marker is a direction-only projection from HMD centre, not a measured eye origin or accuracy result.\n"
            "Amber means the previous accepted WORLD point (at most 100 ms old), never a new gaze sample.\n"
            "Invalid/stale/absent data or lost head tracking clears it immediately; Stop retains aggregate counters only.\n"
            "Head/eye tracking and mapping must still be independently validated before a study.\n"
            "No VIVE trackpad study mapping is implemented. Existing Quest/simulation flows remain separate.\n"
            "If present, synthetic-vive-eye-diagnostic-stopped.png is an Editor fixture, not a headset capture.\n",
            encoding="utf-8")
        provenance.update(status="built", executable=executable.name, executable_sha256=prepare.digest(executable),
                          player=player.relative_to(output).as_posix(), player_sha256=prepare.digest(player),
                          windows_child_environment_test="passed; harmless console child only",
                          scene_checks=prepare.read_json(output / "vive-eye-scene-validation.json"))
        prepare.write_json(marker, provenance)
        print("Built VIVE hardware rehearsal: " + str(executable) + "; live headset and eye validation remain separate.")
        return 0
    except (OSError, ValueError, KeyError, subprocess.CalledProcessError) as error:
        parser.error(str(error))


if __name__ == "__main__":
    sys.exit(main())
