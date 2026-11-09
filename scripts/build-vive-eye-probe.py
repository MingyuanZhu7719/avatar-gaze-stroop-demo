#!/usr/bin/env python3
"""Compile the opt-in native eye health CLI using the existing Unity compiler.
No native library is loaded and no device, consent or recording is started.
"""
import subprocess
from pathlib import Path
from test import ROOT, unity_tools
from windows_framework import compiler_command, runtime_sources


def main():
    installed = unity_tools()
    if installed is None:
        raise RuntimeError("An installed Unity compiler is required; nothing is downloaded.")
    _, mono, csc = installed
    output = ROOT / "build/vive-eye-probe"
    output.mkdir(parents=True, exist_ok=True)
    binary = output / "ViveEyeProbe.exe"
    command = compiler_command(mono, csc) + ["-target:exe", "-platform:x64", "-main:AvatarStroop.Apps.ViveEyeProbe", "-out:" + str(binary)]
    subprocess.run(command + [str(p) for p in runtime_sources(ROOT)] + [str(ROOT / "Apps/ViveEyeProbe.cs")], check=True, cwd=ROOT)
    subprocess.run([str(binary), "--self-check"], check=True, cwd=ROOT)
    print("Built opt-in probe: " + str(binary) + "; no hardware acquisition started.")


if __name__ == "__main__":
    main()
