#!/usr/bin/env python3
"""Compile and draw invisible synthetic WinForms fixtures, without launching players.

The fixture never calls Show, Application.Run, DoEvents, owner commands or
process-start methods. PNGs are offscreen DrawToBitmap evidence, not desktop or
headset captures. Only installed Unity compiler/Windows Framework tools are used.
"""
import argparse
import hashlib
import json
from pathlib import Path
import subprocess
import sys

from windows_framework import compiler_command, runtime_sources

ROOT = Path(__file__).resolve().parents[1]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--editor", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    output = args.output.resolve()
    if output == ROOT or output.is_relative_to(ROOT / "Assets"):
        parser.error("Synthetic evidence must be separate from source Assets.")
    data = args.editor.resolve().parent / "Data"
    mono = data / "MonoBleedingEdge/bin/mono.exe"
    csc = data / "MonoBleedingEdge/lib/mono/4.5/csc.exe"
    if not mono.is_file() or not csc.is_file():
        parser.error("The selected installed Unity compiler is unavailable.")
    output.mkdir(parents=True, exist_ok=True)
    if (output / "sessions").exists():
        parser.error("Refusing a directory containing session records.")
    sources = runtime_sources(ROOT) + [ROOT / "Apps/DesktopPreviewLauncher.cs", ROOT / "scripts/fixtures/EnglishLauncherPresentation.cs"]
    results = []
    for quest in (False, True):
        flavour = "quest" if quest else "desktop"
        binary = output / ("EnglishLauncherFixture-" + flavour + ".exe")
        command = compiler_command(mono, csc) + ["-target:exe", "-main:AvatarStroop.Fixtures.EnglishLauncherPresentation", "-out:" + str(binary)]
        if quest:
            command.append("-define:QUEST_PREVIEW_LAUNCHER")
        subprocess.run(command + [str(path) for path in sources], check=True, cwd=ROOT)
        result = subprocess.run([str(binary), str(output)], check=False, cwd=output, text=True, encoding="utf-8", errors="replace", capture_output=True)
        (output / (flavour + "-fixture.log")).write_text(result.stdout + result.stderr, encoding="utf-8")
        print(result.stdout, end="", flush=True)
        if result.stderr:
            print(result.stderr, file=sys.stderr, end="")
        results.append({"flavour": flavour, "exit_code": result.returncode})
    images = [{"file": p.name, "sha256": hashlib.sha256(p.read_bytes()).hexdigest()} for p in sorted(output.glob("*.png"))]
    evidence = {"kind": "synthetic-offscreen-winforms-not-desktop-capture", "render_method": "individual real controls DrawToBitmap composed at their laid-out coordinates; full-content PNGs use a synthetic content canvas, not a larger window or interactive scrolling proof",
                "visible_window": False,
                "player_or_session_started": False, "hardware_acceptance": False, "results": results, "images": images}
    (output / "launcher-presentation-evidence.json").write_text(json.dumps(evidence, indent=2) + "\n", encoding="utf-8")
    return 0 if all(r["exit_code"] == 0 for r in results) else 1


if __name__ == "__main__":
    sys.exit(main())
