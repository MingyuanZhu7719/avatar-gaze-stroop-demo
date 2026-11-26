#!/usr/bin/env python3
"""Run the four-block synthetic recording test on the Windows CLR and keep its VRAW and CSV.

Uses the compiler from an installed Unity Editor. For a non-Hub installation,
set STROOP_UNITY_CONTENTS to its Editor/Data directory before running this script.
"""
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile

from test import ROOT, regression_test_sources, unity_tools
from windows_framework import compiler_command, runtime_sources


def main():
    if sys.platform != "win32":
        raise ValueError("This fixture exercises the Windows recording writer.")
    tools = unity_tools()
    if not tools:
        raise ValueError("Set STROOP_UNITY_CONTENTS to the installed Unity Editor/Data directory.")
    _, mono, csc = tools
    job = Path(tempfile.mkdtemp(prefix="stroop-formal-recording-")).resolve()
    recorded = job / "Success"
    recorded.mkdir()
    print("Synthetic recording directory: " + str(recorded), flush=True)

    # Only the isolated compiler input can create recordings in this directory.
    sink = ROOT / "Assets/Stroop/Runtime/ViveRawSessionJournalSink.cs"
    source = sink.read_text(encoding="utf-8-sig")
    empty_root = 'const string LegacyIntegrationFixtureRoot="";'
    if source.count(empty_root) != 1:
        raise ValueError("The fixture's isolated output root could not be bound.")
    isolated_sink = job / sink.name
    isolated_sink.write_text(source.replace(empty_root, f'const string LegacyIntegrationFixtureRoot=@"{job}";'),
                             encoding="utf-8")
    sources = [isolated_sink if path == sink else path.relative_to(ROOT) for path in runtime_sources(ROOT)]
    sources.extend(path.relative_to(ROOT) for path in regression_test_sources(ROOT))
    sources.append(Path("Assets/Stroop/Unity/ViveLegacyWozRawSceneAcquisition.cs"))
    binary = job / "FormalRecordingFixture.exe"
    shutil.copyfile(ROOT / "Apps/StudyAnalyzer.config", str(binary) + ".config")
    args = compiler_command(mono, csc, ROOT, binary.parent) + [
        "-target:exe", "-main:AvatarStroop.Tests.ViveFormalEyeRecordingTests", "-nowarn:0649",
        "-define:STROOP_TEST_BUILD,VIVE_RAW_SYNTHETIC_TEST,VIVE_SYNTHETIC_BATCH_FIXTURE", "-out:" + str(binary)]
    subprocess.run(args + [str(path) for path in sources], check=True, cwd=ROOT)
    subprocess.run([str(binary)], check=True, cwd=job)


if __name__ == "__main__":
    try:
        main()
    except (OSError, ValueError, subprocess.CalledProcessError) as error:
        sys.exit(str(error))
