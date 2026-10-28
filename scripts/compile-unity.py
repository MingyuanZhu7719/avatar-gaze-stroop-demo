#!/usr/bin/env python3
"""Compile actual core/runtime/editor C# against an existing Unity installation.

This neither launches/activates the Editor nor imports assets, compiles shaders,
installs packages, or validates rendered UI/XR. Uses test.py's compiler discovery.
"""
from pathlib import Path
import subprocess
import sys

from test import ROOT, unity_tools


def main():
    try:
        installed = unity_tools()
    except ValueError as error:
        print(str(error), file=sys.stderr)
        return 2
    if installed is None:
        print("No installed Unity compiler found. Set STROOP_UNITY_CONTENTS to an existing Editor/Data (Windows/Linux) or Unity.app/Contents (macOS).", file=sys.stderr)
        return 2
    base, mono, csc = installed
    managed = base / "Managed/UnityEngine"
    modules = sorted(managed.glob("UnityEngine*.dll"))
    editor = sorted(managed.glob("UnityEditor*.dll"))
    # Unity's modular APIs target netstandard; use its installed reference facade.
    facade = base / "MonoBleedingEdge/lib/mono/4.5/Facades/netstandard.dll"
    if not modules or not editor or not facade.is_file():
        print("Installed Unity managed/reference assemblies are missing.", file=sys.stderr)
        return 2
    output = ROOT / "build/unity-adapter"
    output.mkdir(parents=True, exist_ok=True)
    core = output / "AvatarStroop.Core.dll"
    runtime = output / "AvatarStroop.Unity.dll"
    editor_output = output / "AvatarStroop.Unity.Editor.dll"
    compiler = [str(mono), str(csc), "-nologo", "-langversion:7.3", "-warnaserror", "-target:library"]

    def compile_sources(target, sources, references):
        if not sources:
            raise RuntimeError("No source files for " + target.name)
        command = compiler + ["-out:" + str(target)] + ["-r:" + str(reference) for reference in references]
        subprocess.run(command + [str(source) for source in sources], check=True, cwd=str(ROOT))
        print("PASS " + target.name + " (" + str(len(sources)) + " actual source files; warnings as errors)", flush=True)

    print("Installed compiler: " + str(csc), flush=True)
    print("Installed Unity references: " + str(managed), flush=True)
    compile_sources(core, sorted((ROOT / "Assets/Stroop/Core").rglob("*.cs")) + sorted((ROOT / "Assets/Stroop/Runtime").rglob("*.cs")), [])
    unity_sources = sorted((ROOT / "Assets/Stroop/Unity").rglob("*.cs"))
    compile_sources(runtime, [source for source in unity_sources if "Editor" not in source.parts], [facade, core] + modules)
    compile_sources(editor_output, [source for source in unity_sources if "Editor" in source.parts], [facade, core, runtime] + modules + editor)
    print("Managed compile complete. Editor import, shader compilation, camera rendering, player builds and headset validation are separate checks.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
