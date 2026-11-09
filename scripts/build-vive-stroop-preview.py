#!/usr/bin/env python3
"""Build a standalone VIVE development task preview from a marked isolated project.
It does not open a player, initialize hardware, start capture, or modify the system runtime.
"""
import argparse
import datetime
import importlib.util
import os
from pathlib import Path
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[1]
def load(name, file):
    spec = importlib.util.spec_from_file_location(name, Path(__file__).with_name(file))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module

prepare = load("quest_prepare", "prepare-quest-preview.py")
rehearsal = load("vive_rehearsal_build", "build-vive-eye-preview.py")

def validate_recording_options(controlled, legacy):
    if legacy and not controlled:
        raise ValueError("--legacy-raw-recording-development requires --controlled-recording-development.")


def editor_environment(output, controlled=False, legacy=False):
    validate_recording_options(controlled, legacy)
    environment = dict(os.environ, STROOP_VIVE_CLASSROOM_OUTPUT=str(output))
    # Ambient variables must never turn an ordinary/named build into a legacy build.
    environment.pop("STROOP_VIVE_CONTROLLED_RECORDING_DEVELOPMENT", None)
    environment.pop("STROOP_VIVE_LEGACY_RAW_RECORDING_DEVELOPMENT", None)
    if controlled: environment["STROOP_VIVE_CONTROLLED_RECORDING_DEVELOPMENT"] = "1"
    if legacy: environment["STROOP_VIVE_LEGACY_RAW_RECORDING_DEVELOPMENT"] = "1"
    return environment


def recording_manifest(controlled=False, legacy=False):
    validate_recording_options(controlled, legacy)
    return {"recording_default": False,
            "optional_local_eye_and_task_recording": controlled,
            "controlled_recording_development": controlled,
            "legacy_raw_recording_development": legacy,
            "named_explicit_recording_requested": controlled,
            "legacy_explicit_recording_requested": legacy,
            "preview_configuration_records": False,
            "preview_task_records": "memory only until close; no task or eye files",
            "recording_scope": ("named and legacy explicit self-tests" if legacy else
                                "named explicit self-tests only; legacy is memory-only" if controlled else
                                "ordinary preview only; recording unavailable"),
            "coverage_scope": ("controlled local self-test; real hardware and session coverage unvalidated" if controlled else
                               "local VIVE recording unavailable; no session coverage established")}


def build_intent(source, output, controlled=False, legacy=False):
    validate_recording_options(controlled, legacy)
    return {"source": str(source), "output": str(output), "purpose": "VIVE development task preview",
            "controlled_recording_development": controlled, "legacy_raw_recording_development": legacy}


def start_here_text(controlled=False, legacy=False, guide=""):
    scope = recording_manifest(controlled, legacy)["recording_scope"]
    heading = ("VIVE DEVELOPMENT PREVIEW / LOCAL SELF-TEST\nPackage scope: " + scope + ".\n"
               "Recording is OFF by default. Building or opening the package starts no task, XR, eye source or file capture.\n"
               "Confirm configuration for development preview is non-recording: task records stay in memory until close, with no task or eye files.\n")
    if controlled:
        return heading + "Runtime, storage, separate headset choices and explicit start checks must still pass.\n\n" + guide
    return heading + (
        "Participant controller manual pause is disabled. Trigger confirms selections, continues and explicitly resumes. Ask the operator to pause or stop. Desktop Withdraw task is the reliable withdrawal control; Escape is not handled in every failed state.\n"
        "Trackpad colours: up red, right green, down blue, left yellow. No formal study or two-PC network is enabled.\n"
        "Open the outer launcher only when no other VIVE preview is running. Select a design and wand, confirm the non-recording configuration, then start the classroom view. The trackpad sector threshold is fixed at 0.5.\n"
        "Follow the headset startup prompt, release controls and hold still briefly. Real head movement is preserved; the virtual floor may shift.\n"
        "Enable eye preview is a separate explicit diagnostic action, not file recording. Calibration, tracking and focus must remain valid.\n"
        "The pre-session input rehearsal is not consent and does not create a session or record data.\n"
        "SteamVR is selected for this process only. The system runtime and existing Quest build are not changed.\n"
        "This package has not been validated in a headset and does not establish eye accuracy or complete-session coverage.\n")


def run_editor(editor, project, output, action, controlled=False, legacy=False):
    environment = editor_environment(output, controlled, legacy)
    suffix = datetime.datetime.now(datetime.timezone.utc).strftime("%Y%m%dT%H%M%S%fZ")
    log = output / ("unity-vive-classroom-" + action.lower() + "-" + suffix + ".log")
    command = [str(editor), "-batchmode", "-quit", "-projectPath", str(project), "-buildTarget", "Win64",
               "-executeMethod", "AvatarStroop.Unity.Editor.ViveStroopPreviewBuild." + action, "-logFile", str(log)]
    if action != "RenderFixture": command.append("-nographics")
    print("Unity " + action + "; log: " + str(log), flush=True)
    result = subprocess.run(command, env=environment)
    output_text = log.read_text(encoding="utf-8", errors="replace") if log.is_file() else ""
    expected = {"Configure": "VIVE_CLASSROOM_CONFIGURED:", "BuildWindowsPreview": "VIVE_CLASSROOM_BUILD_COMPLETE:",
                "RenderFixture": "VIVE_CLASSROOM_FIXTURE_COMPLETE:"}[action]
    if result.returncode or expected not in output_text:
        raise ValueError("Unity " + action + " failed or did not acknowledge completion; inspect " + str(log))

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--editor", type=Path, required=True)
    parser.add_argument("--project", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--check-only", action="store_true")
    parser.add_argument("--controlled-recording-development", action="store_true", help="Build a separately marked Development player with explicit opt-in local test recording; never starts it.")
    parser.add_argument("--legacy-raw-recording-development", action="store_true", help="Also opt into legacy A/B/C/D raw recording capability; requires --controlled-recording-development. No recording starts automatically.")
    parser.add_argument("--render-fixture", action="store_true", help="Render a synthetic Editor image; no XR or eye capture.")
    args = parser.parse_args()
    try:
        validate_recording_options(args.controlled_recording_development, args.legacy_raw_recording_development)
        if sys.platform != "win32": raise ValueError("Windows is required.")
        source, project, editor, output = ROOT.resolve(), args.project.resolve(), args.editor.resolve(), args.output.resolve()
        prepare.verify_workspace_project(source, project)
        prepare.check_editor(editor)
        record = prepare.verify_prepared(source, project)
        # A distinct new output only: never reuse or overwrite the rehearsal.
        if (not output.is_relative_to(source.parent) or output == source.parent or output == source or
            output.is_relative_to(source) or source.is_relative_to(output) or output == project or
            output.is_relative_to(project) or project.is_relative_to(output)):
            raise ValueError("Output must be a separate workspace directory.")
        for parent in (output,) + tuple(output.parents): prepare.reject_link(parent)
        if output.exists() and any(output.iterdir()): raise ValueError("Choose a new empty output directory; existing builds are preserved.")
        if args.check_only:
            print("Marked project, source inventory, cached packages and new output verified; no program launched.")
            return 0
        output.mkdir(parents=True, exist_ok=True)
        intent = project / "vive-classroom-isolated-intent.json"
        if intent.exists():
            raise ValueError("Classroom intent already exists. Preserve the previous project and use a new isolated project.")
        prepare.write_json(intent, build_intent(source, output, args.controlled_recording_development, args.legacy_raw_recording_development))
        marker = output / "vive-classroom-build.json"
        provenance = {"format": 1, "project": str(project), "editor_version": prepare.EDITOR_VERSION,
                      "source_files": record["source_files"], "packages": prepare.PACKAGES, "status": "configuring",
                      "explicit_xr_start": True, "explicit_eye_start": True, "development_task_requires_configuration": True,
                      "separate_eye_and_task_choices_required": True, "existing_local_directory_required": True,
                      "sends_network": False, "input_configured": False,
                      "headset_validated": False, "eye_accuracy_validated": False, "system_runtime_changed": False}
        provenance.update(recording_manifest(args.controlled_recording_development, args.legacy_raw_recording_development))
        prepare.write_json(marker, provenance)
        run_editor(editor, project, output, "Configure", args.controlled_recording_development, args.legacy_raw_recording_development);prepare.verify_prepared(source, project)
        run_editor(editor, project, output, "BuildWindowsPreview", args.controlled_recording_development, args.legacy_raw_recording_development);prepare.verify_prepared(source, project)
        player = output / "UnityPlayer/AvatarStroopViveEyePlayer.exe"
        launcher = rehearsal.compile_launcher(editor, source, output)
        prepare.verify_prepared(source, project)
        if args.render_fixture:
            run_editor(editor, project, output, "RenderFixture", args.controlled_recording_development, args.legacy_raw_recording_development);prepare.verify_prepared(source, project)
        if not player.is_file() or not launcher.is_file() or list(output.rglob("SRanipal.dll")):
            raise ValueError("Missing player/launcher or unexpected vendor DLL.")
        guide = ""
        if args.controlled_recording_development:
            (output / "LocalSelfTestData").mkdir(exist_ok=False)
            guide = (source / "docs/controlled-recording-self-test-guide.txt").read_text(encoding="utf-8")
        (output / "START-HERE.txt").write_text(start_here_text(args.controlled_recording_development, args.legacy_raw_recording_development, guide), encoding="utf-8")
        provenance.update(status="built", executable=launcher.name, executable_sha256=prepare.digest(launcher),
                          player=player.relative_to(output).as_posix(), player_sha256=prepare.digest(player),
                          scene_checks=prepare.read_json(output / "vive-classroom-scene-validation.json"))
        prepare.write_json(marker, provenance)
        print("Built VIVE development preview: " + str(launcher) + "; not headset validated.")
        return 0
    except (OSError, ValueError, KeyError, subprocess.CalledProcessError) as error:
        parser.error(str(error))

if __name__ == "__main__":
    sys.exit(main())
