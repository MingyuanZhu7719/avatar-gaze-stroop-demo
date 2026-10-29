#!/usr/bin/env python3
"""Compile preview launchers against the installed Windows .NET Framework.

Unity supplies the C# compiler only. Its default Mono references expose APIs
missing from Framework, so both default compiler configuration and implicit
standard references are disabled. No SDK, runtime or package is installed.
Running this script performs only the headless Framework compatibility tests.
"""
import argparse
import os
from pathlib import Path
import subprocess
import sys
import tempfile

ROOT = Path(__file__).resolve().parents[1]
ASSEMBLIES = ("mscorlib.dll", "System.dll", "System.Core.dll", "System.Windows.Forms.dll", "System.Drawing.dll")


def framework_references():
    if sys.platform != "win32":
        raise ValueError("Preview launchers require the installed Windows .NET Framework.")
    windows = Path(os.environ.get("SystemRoot", r"C:\Windows"))
    for architecture in ("Framework64", "Framework"):
        directory = windows / "Microsoft.NET" / architecture / "v4.0.30319"
        references = [directory / name for name in ASSEMBLIES]
        if all(path.is_file() for path in references):
            return references
    raise ValueError("The installed Windows .NET Framework reference assemblies are unavailable; refusing Unity Mono references.")


def compiler_command(mono, csc):
    return [str(mono), str(csc), "-noconfig", "-nostdlib+", "-nologo", "-langversion:7.3", "-warnaserror"] + [
        "-r:" + str(path) for path in framework_references()]


def runtime_sources(source):
    return sorted((source / "Assets/Stroop/Core").glob("*.cs")) + sorted((source / "Assets/Stroop/Runtime").glob("*.cs"))


def verify_preview_runtime(mono, csc, source, work_directory):
    """Use the real Windows CLR, with temporary test files confined to the build directory."""
    work_directory.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="framework-check-", dir=work_directory) as temporary:
        binary = Path(temporary) / "AvatarStroop.FrameworkPreviewSmoke.exe"
        sources = runtime_sources(source) + sorted((source / "Tests").glob("*.cs"))
        command = compiler_command(mono, csc) + ["-target:exe", "-main:AvatarStroop.Tests.FrameworkPreviewSmoke", "-out:" + str(binary)]
        subprocess.run(command + [str(path) for path in sources], check=True, cwd=source)
        # Do not prefix this with Mono: that would hide the deployment incompatibility.
        subprocess.run([str(binary)], check=True, cwd=source,
                       env=dict(os.environ, TEMP=temporary, TMP=temporary))


def compile_preview_launcher(mono, csc, source, launcher, quest=False):
    command = compiler_command(mono, csc) + ["-target:winexe", "-main:AvatarStroop.Apps.DesktopPreviewLauncher", "-out:" + str(launcher)]
    if quest:
        command.append("-define:QUEST_PREVIEW_LAUNCHER")
    sources = runtime_sources(source) + [source / "Apps/DesktopPreviewLauncher.cs"]
    subprocess.run(command + [str(path) for path in sources], check=True, cwd=source)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--editor", type=Path, required=True)
    parser.add_argument("--output", type=Path, default=ROOT / "build/framework-preview")
    args = parser.parse_args()
    try:
        data = args.editor.resolve().parent / "Data"
        mono = data / "MonoBleedingEdge/bin/mono.exe"
        csc = data / "MonoBleedingEdge/lib/mono/4.5/csc.exe"
        if not mono.is_file() or not csc.is_file():
            raise ValueError("The selected installed Editor's Mono/C# compiler is unavailable.")
        verify_preview_runtime(mono, csc, ROOT, args.output.resolve())
        return 0
    except (OSError, ValueError, subprocess.CalledProcessError) as error:
        parser.error(str(error))


if __name__ == "__main__":
    sys.exit(main())
