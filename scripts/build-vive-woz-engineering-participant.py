#!/usr/bin/env python3
"""Build a dormant VIVE WoZ participant candidate in a new isolated Unity project.

This builds only. It never starts a player, headset, worker, socket, capture,
performs a File.Move test, changes the system XR runtime, or accepts a license.
The caller must first freeze all application sources, not just viewer files.
"""
import argparse
from collections import Counter
import datetime
import importlib.util
import json
import os
from pathlib import Path
import re
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("quest_prepare", ROOT / "scripts/prepare-quest-preview.py")
prepare = importlib.util.module_from_spec(spec)
spec.loader.exec_module(prepare)


def resolve_without_links(path):
    absolute = Path(os.path.abspath(path))
    for parent in (absolute,) + tuple(absolute.parents):
        prepare.reject_link(parent)
    return absolute.resolve()


def new_separate_path(path, workspace, source, others):
    if (not path.is_relative_to(workspace) or path == workspace or path == source or
            path.is_relative_to(source) or source.is_relative_to(path)):
        raise ValueError("Paths must be separate, new workspace directories outside source: " + str(path))
    for other in others:
        if path == other or path.is_relative_to(other) or other.is_relative_to(path):
            raise ValueError("Project, output and log directories must not overlap.")
    for parent in (path,) + tuple(path.parents):
        prepare.reject_link(parent)
    if path.exists():
        raise ValueError("Refusing to reuse an existing directory: " + str(path))


def run_editor(editor, project, logs, action, method, output=None):
    stamp = datetime.datetime.now(datetime.timezone.utc).strftime("%Y%m%dT%H%M%S%fZ")
    log = logs / ("unity-engineering-participant-" + action + "-" + stamp + ".log")
    command = [str(editor), "-batchmode", "-quit", "-projectPath", str(project), "-buildTarget", "Win64",
               "-executeMethod", method, "-logFile", str(log), "-nographics"]
    env = os.environ.copy()
    if output is not None:
        env["STROOP_VIVE_ENGINEERING_PARTICIPANT_OUTPUT"] = str(output)
    print("Unity " + action + "; log: " + str(log), flush=True)
    result = subprocess.run(command, env=env, cwd=str(ROOT))
    contents = log.read_text(encoding="utf-8", errors="replace") if log.is_file() else ""
    expected = {"configure": "VIVE_CLASSROOM_CONFIGURED:", "fixture": "VIVE_WOZ_ENGINEERING_PARTICIPANT_FIXTURE:", "build": "VIVE_WOZ_ENGINEERING_PARTICIPANT:"}[action]
    if result.returncode or expected not in contents:
        raise ValueError("Unity " + action + " did not complete; inspect " + str(log))
    return str(log)


def validate_scene(project):
    scene = project / "Assets/Stroop/Generated/ViveWozEngineeringParticipant.unity"
    meta = project / "Assets/Stroop/Unity/ViveWozEngineeringParticipantBootstrap.cs.meta"
    if not scene.is_file() or not meta.is_file():
        raise ValueError("Generated participant scene or bootstrap metadata is missing.")
    text = scene.read_text(encoding="utf-8-sig")
    match = re.search(r"(?m)^guid:\s*([0-9a-f]{32})\s*$", meta.read_text(encoding="utf-8-sig"))
    counts = Counter(re.findall(r"(?m)^--- !u!(\d+)", text))
    if (not match or counts["1"] != 1 or counts["4"] != 1 or counts["114"] != 1 or
            len(re.findall(r"(?m)^\s*m_Name: ViveWozEngineeringParticipant\s*$", text)) != 1 or
            len(re.findall(r"(?m)^\s*m_Script: \{fileID: 11500000, guid: " + match.group(1) + r", type: 3\}\s*$", text)) != 1):
        raise ValueError("Participant scene does not contain exactly one expected bootstrap and root.")
    return {"scene": str(scene), "sha256": prepare.digest(scene), "root_count": 1,
            "bootstrap_count": 1, "bootstrap_guid": match.group(1),
            "xr_autostart_disabled_checked_by_editor": True,
            "default_preflight_and_live_gate_false_checked_by_editor": True,
            "player_launched": False, "headset_validated": False}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--editor", required=True, type=Path)
    parser.add_argument("--package-cache", required=True, type=Path)
    parser.add_argument("--project", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--logs", required=True, type=Path)
    parser.add_argument("--check-only", action="store_true")
    args = parser.parse_args()
    try:
        if sys.platform != "win32":
            raise ValueError("This isolated participant candidate requires Windows.")
        source, workspace = ROOT.resolve(), ROOT.resolve().parent
        editor, cache = args.editor.resolve(), args.package_cache.resolve()
        project, output, logs = (resolve_without_links(args.project), resolve_without_links(args.output),
                                 resolve_without_links(args.logs))
        prepare.verify_workspace_project(source, project)
        new_separate_path(project, workspace, source, (output, logs))
        new_separate_path(output, workspace, source, (project, logs))
        new_separate_path(logs, workspace, source, (project, output))
        builtins = prepare.check_editor(editor)
        packages = prepare.find_packages(cache)
        prepare.package_closure(packages, builtins, prepare.read_json(source / "Packages/manifest.json"))
        if (source / "Assets/csc.rsp").exists():
            raise ValueError("Source compiler options require review before preparing a preview.")
        frozen = prepare.source_inventory(source)
        if args.check_only:
            print("Paths, editor, cached packages and current source inventory checked; no files created.")
            return 0
        logs.mkdir(parents=True, exist_ok=False)
        prepare_command = [sys.executable, str(source / "scripts/prepare-quest-preview.py"), "--editor", str(editor),
                           "--project", str(project), "--package-cache", str(cache)]
        subprocess.run(prepare_command, cwd=str(source), check=True)
        record = prepare.verify_prepared(source, project)
        if frozen != record["source_files"]:
            raise ValueError("Source changed during snapshot preparation; preserve evidence and stop.")
        prepare.write_json(project / "vive-classroom-isolated-intent.json",
                           {"source": str(source), "purpose": "isolated engineering participant build XR setup only"})
        configure_log = run_editor(editor, project, logs, "configure", "AvatarStroop.Unity.Editor.ViveStroopPreviewBuild.Configure")
        prepare.verify_prepared(source, project)
        fixture_log = run_editor(editor, project, logs, "fixture", "AvatarStroop.Unity.Editor.ViveWozEngineeringParticipantFixture.Run")
        prepare.verify_prepared(source, project)
        prepare.write_json(project / "vive-engineering-participant-isolated-intent.json",
                           {"source": str(source), "output": str(output), "purpose": "participant engineering-only pose candidate"})
        build_log = run_editor(editor, project, logs, "build", "AvatarStroop.Unity.Editor.ViveWozEngineeringParticipantBuild.BuildWindowsCandidate", output)
        prepare.verify_prepared(source, project)
        if frozen != prepare.source_inventory(source):
            raise ValueError("Source changed during build; preserve output for investigation and do not treat as verified.")
        scene = validate_scene(project)
        player = output / "AvatarStroopViveWozEngineeringParticipant.exe"
        if not player.is_file() or list(output.rglob("SRanipal.dll")):
            raise ValueError("Expected participant player missing or unexpected vendor DLL bundled.")
        evidence = {"format": 1, "status": "built_static_checks_only", "source": str(source),
                    "project": str(project), "editor": str(editor), "packages": prepare.PACKAGES,
                    "source_files": frozen, "player": str(player), "player_sha256": prepare.digest(player),
                    "scene": scene, "configure_log": configure_log, "fixture_log": fixture_log, "build_log": build_log,
                    "pose_view_freshness": "requires fresh engineering transport; not hardware validated",
                    "preview_avatar": "live pose only; uncalibrated", "preflight_gate": "closed",
                    "physical_network_tested": False, "player_launched": False, "system_runtime_changed": False}
        prepare.write_json(logs / "engineering-participant-build-evidence.json", evidence)
        print("Built isolated participant candidate; static checks only: " + str(player))
        return 0
    except (OSError, ValueError, KeyError, subprocess.CalledProcessError) as error:
        parser.exit(2, "Engineering participant build stopped: " + str(error) + "\n")


if __name__ == "__main__":
    sys.exit(main())

