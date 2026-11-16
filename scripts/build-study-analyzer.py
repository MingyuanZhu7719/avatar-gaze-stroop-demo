#!/usr/bin/env python3
"""Build the local study analyzer using the installed Unity compiler and Windows CLR."""
import argparse
from pathlib import Path
import shutil
import subprocess
import sys

from windows_framework import ROOT, compiler_command, runtime_sources


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--editor", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    data = args.editor.resolve().parent / "Data"
    mono = data / "MonoBleedingEdge/bin/mono.exe"
    csc = data / "MonoBleedingEdge/lib/mono/4.5/csc.exe"
    if not mono.is_file() or not csc.is_file():
        parser.error("Installed Unity compiler not found.")
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=True)
    sources = runtime_sources(ROOT) + [ROOT / "Apps/StudyAnalyzer.cs"]
    # Both entry points contain identical analysis logic; the CLI additionally
    # provides visible stdout/stderr for scripted batch use.
    for name, target in (("AvatarStroopAnalyzer.exe", "winexe"), ("AvatarStroopAnalyzer.Cli.exe", "exe")):
        command = compiler_command(mono, csc) + ["-target:" + target, "-main:AvatarStroop.Apps.StudyAnalyzer", "-out:" + str(output / name)]
        subprocess.run(command + [str(p.relative_to(ROOT)) for p in sources], cwd=ROOT, check=True)
        # The installed compiler does not generate an SDK target-framework
        # configuration. Both entry points need modern Framework path handling
        # before Path.GetFullPath and the Windows file picker are initialized.
        shutil.copyfile(ROOT / "Apps/StudyAnalyzer.config", output / (name + ".config"))
    print(output)
    return 0


if __name__ == "__main__":
    sys.exit(main())
