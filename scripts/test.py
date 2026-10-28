#!/usr/bin/env python3
"""Compile/run the shared C# core with installed Unity Mono, or an existing dotnet SDK.
No package installation or Unity Editor/license startup is required.
"""
import glob
import os
from pathlib import Path
import shutil
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[1]

def unity_tools():
    explicit = os.environ.get("STROOP_UNITY_CONTENTS")
    if explicit is not None:
        roots = [explicit] if explicit else []
    else:
        if sys.platform == "win32":
            pattern = str(Path(os.environ.get("ProgramFiles", r"C:\Program Files")) / "Unity/Hub/Editor/*/Editor/Data")
        elif sys.platform == "darwin":
            pattern = "/Applications/Unity/Hub/Editor/*/Unity.app/Contents"
        else:
            pattern = str(Path.home() / "Unity/Hub/Editor/*/Editor/Data")
        roots = sorted(glob.glob(pattern), reverse=True)
    for candidate in roots:
        base = Path(candidate)
        mono = base / "MonoBleedingEdge/bin" / ("mono.exe" if sys.platform == "win32" else "mono")
        csc = base / "MonoBleedingEdge/lib/mono/4.5/csc.exe"
        if mono.is_file() and csc.is_file():
            return base, mono, csc
    if explicit is not None:
        raise ValueError("STROOP_UNITY_CONTENTS has no usable Unity Mono compiler: " + repr(explicit) +
                         ". Set it to an existing Editor/Data (Windows/Linux) or Unity.app/Contents (macOS).")
    return None

def main():
    try:
        tools = unity_tools()
    except ValueError as error:
        print(str(error), file=sys.stderr)
        return 2
    if not tools:
        dotnet = shutil.which("dotnet")
        if dotnet:
            return subprocess.call([dotnet, "run", "--project", str(ROOT / "AvatarStroop.Regression.csproj")], cwd=str(ROOT))
        print("No installed compiler found. Set STROOP_UNITY_CONTENTS to an existing Editor/Data (Windows/Linux) or Unity.app/Contents (macOS), or use an existing .NET 8 SDK.", file=sys.stderr)
        return 2
    base, mono, csc = tools
    output = ROOT / "build"
    output.mkdir(exist_ok=True)
    sources = sorted((ROOT / "Assets/Stroop/Core").rglob("*.cs")) + sorted((ROOT / "Assets/Stroop/Runtime").rglob("*.cs")) + sorted((ROOT / "Tests").rglob("*.cs"))
    binary = output / "AvatarStroop.Regression.exe"
    command = [str(mono), str(csc), "-nologo", "-langversion:7.3", "-warnaserror", "-target:exe", "-main:AvatarStroop.Tests.Program", "-out:" + str(binary)]
    print("Compiler: " + str(csc), flush=True)
    subprocess.run(command + [str(p) for p in sources], check=True, cwd=str(ROOT))
    return subprocess.call([str(mono), str(binary)], cwd=str(ROOT))

if __name__ == "__main__":
    sys.exit(main())
