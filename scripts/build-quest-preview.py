#!/usr/bin/env python3
"""Configure/build the isolated Windows Quest Link preview, without launching it.

Run prepare-quest-preview.py first. The ordinary desktop project's manifest and
Editor version remain unchanged. No system OpenXR runtime or driver is changed.
"""
import argparse
import importlib.util
import os
from pathlib import Path
import subprocess
import sys
from windows_framework import framework_references, verify_preview_runtime, compile_preview_launcher

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("quest_prepare", Path(__file__).with_name("prepare-quest-preview.py"))
prepare = importlib.util.module_from_spec(spec)
spec.loader.exec_module(prepare)


def verify_output(source, project, output):
    if (not output.is_relative_to(source.parent) or output == source.parent or
            output == project or output.is_relative_to(project) or
            output == source or output.is_relative_to(source) or
            source.is_relative_to(output) or project.is_relative_to(output)):
        raise ValueError("Build output must be a separate directory in the workspace, outside both projects.")
    for path in (output,) + tuple(output.parents):
        prepare.reject_link(path)
    prepare.plain_tree(output)
    if output.exists() and any(output.iterdir()):
        marker = output / "quest-preview-build.json"
        if not marker.is_file() or prepare.read_json(marker).get("project") != str(project):
            raise ValueError("Refusing to overwrite an unrelated nonempty output directory.")


def run_editor(editor, project, output, action):
    log = output / ("unity-" + action.lower() + ".log")
    command = [str(editor), "-batchmode", "-nographics", "-quit", "-projectPath", str(project),
               "-buildTarget", "Win64", "-executeMethod", "AvatarStroop.Unity.Editor.QuestPreviewBuild." + action,
               "-logFile", str(log)]
    print("Unity " + action + "; log: " + str(log), flush=True)
    result = subprocess.run(command, env=dict(os.environ, STROOP_QUEST_PREVIEW_OUTPUT=str(output)))
    text = log.read_text(encoding="utf-8", errors="replace") if log.is_file() else "No Editor log was created."
    expected = "QUEST_PREVIEW_CONFIGURED:" if action == "Configure" else "QUEST_PREVIEW_BUILD_COMPLETE:"
    if result.returncode or expected not in text:
        print("See the local Editor log for diagnostics: " + str(log), file=sys.stderr)
        raise ValueError("Editor " + action + " failed or did not acknowledge completion (exit " + str(result.returncode) + ").")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--editor", required=True, type=Path)
    parser.add_argument("--project", required=True, type=Path)
    parser.add_argument("--output", type=Path, default=ROOT.parent / "quest-preview-players-6000.3.25f1")
    parser.add_argument("--configure-only", action="store_true", help="Resolve/import packages and save XR scenes; do not build players or launcher.")
    parser.add_argument("--check-only", action="store_true", help="Read-only source, package, Editor and output checks; do not launch any program.")
    args = parser.parse_args()
    try:
        if sys.platform != "win32":
            raise ValueError("Quest Link preview requires Windows.")
        source, project, editor, output = ROOT.resolve(), args.project.resolve(), args.editor.resolve(), args.output.resolve()
        prepare.check_editor(editor)
        record = prepare.verify_prepared(source, project)
        verify_output(source, project, output)
        mono = editor.parent / "Data/MonoBleedingEdge/bin/mono.exe"
        csc = editor.parent / "Data/MonoBleedingEdge/lib/mono/4.5/csc.exe"
        if not mono.is_file() or not csc.is_file():
            raise ValueError("The existing Editor's Mono/C# compiler is unavailable.")
        framework_references()
        if args.check_only:
            print("Quest preview source, pinned package metadata, Editor and output checks passed. No program launched.")
            return 0
        output.mkdir(parents=True, exist_ok=True)
        provenance = {"format": 1, "project": str(project), "editor": str(editor),
                      "editor_version": prepare.EDITOR_VERSION, "packages": prepare.PACKAGES,
                      "source_files": record["source_files"], "status": "configuring", "hardware_validated": False}
        marker = output / "quest-preview-build.json"
        prepare.write_json(marker, provenance)
        # Both input backends take effect on the second Editor invocation.
        run_editor(editor, project, output, "Configure")
        prepare.verify_prepared(source, project)
        provenance["status"] = "configured"
        prepare.write_json(marker, provenance)
        if args.configure_only:
            print("Isolated XR scenes configured. No player, launcher or headset was launched.")
            return 0
        verify_preview_runtime(mono, csc, source, output)
        run_editor(editor, project, output, "BuildWindowsPreview")
        prepare.verify_prepared(source, project)
        # Compile from the same verified snapshot as the two players.
        launcher = output / "AvatarStroopQuestPreview.exe"
        compile_preview_launcher(mono, csc, project, launcher, quest=True)
        prepare.verify_prepared(source, project)
        binaries = [launcher, output / "Participant/AvatarStroopParticipant.exe", output / "Operator/AvatarStroopOperator.exe"]
        if not all(path.is_file() for path in binaries):
            raise ValueError("A required preview executable is missing.")
        (output / "START-HERE.txt").write_text(
            "Avatar Stroop - QUEST LINK DEVELOPMENT PREVIEW / SIMULATION\n\n"
            "This is a Windows PC Link preview, not a standalone Quest APK.\n"
            "Use the already configured Meta Quest Link connection before launching.\n"
            "Open AvatarStroopQuestPreview.exe; retain both role folders beside it.\n"
            "The owner explicitly starts the preview, practice, and each block source.\n"
            "Participant Touch controls: X red, Y green, A blue, B yellow (ink colour).\n"
            "Right trigger: Continue / Ready / active resume; left menu: pause.\n"
            "While paused, B withdraws. Ratings: left stick up/down; A confirms.\n"
            "The operator retains its existing desktop controls. No source is selected by default.\n"
            "Only the participant starts OpenXR. No system runtime is selected by this package.\n"
            "Head pose and controllers do not provide participant eye tracking or real WoZ gaze.\n"
            "Synthetic avatar gaze is explicitly labelled. Not valid for formal apparatus trials.\n"
            "Build checks do not prove headset visibility; actual headset acceptance remains outstanding.\n"
            "No head-mounted display or player was launched by the build script.\n",
            encoding="utf-8")
        provenance.update(status="built", executables={p.relative_to(output).as_posix(): prepare.digest(p) for p in binaries},
                          configuration=prepare.read_json(project / "quest-preview-configured.json"))
        prepare.write_json(marker, provenance)
        print("Quest preview built: " + str(launcher) + "; hardware acceptance remains unverified.")
        return 0
    except (OSError, ValueError, KeyError, subprocess.CalledProcessError) as error:
        parser.error(str(error))


if __name__ == "__main__":
    sys.exit(main())
