#!/usr/bin/env python3
"""Bounded public source checks. No Unity Editor/player, native fixture or socket entry point is run.
Requires Python 3.10+ and an existing Unity Mono compiler; installs nothing.
All generated artifacts stay under build/public-woz-checks/<unique run>.
"""
import argparse
import datetime
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile

ROOT = Path(__file__).resolve().parents[1]
STATIC = (
    "test_woz_recording_participant_join.py",
    "test_woz_recording_operator_notice.py",
    "test_woz_recording_build_options.py",
    "test_woz_recording_package_guides.py",
)
BASE_TESTS = (
    "ViveLegacyWozBridgeMemoryTests.cs", "ViveDevelopmentParticipantTests.cs",
    "ViveWozOperatorSourceFactoryTests.cs", "ViveWozOfflineLifecycleTests.cs",
    "ViveLegacyWozSourceLifecycleTests.cs", "WozAuthenticatedControlTests.cs",
    "ViveLegacyWozProcessMemoryTests.cs", "ViveLegacyWozTwoProcessLoopback.cs",
    "ViveLegacyWozSceneTests.cs",
)
RECORDED_TESTS = ("ViveLegacyWozRawSchemaTests.cs", "ViveLegacyWozRecordingPumpTests.cs")
MODES = {
    "ordinary": "STROOP_TEST_BUILD",
    "recording": "STROOP_TEST_BUILD,VIVE_RAW_SYNTHETIC_TEST,VIVE_SYNTHETIC_BATCH_FIXTURE",
}

def run(command, output, timeout):
    flags = subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0
    result = subprocess.run(command, cwd=ROOT, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                            timeout=timeout, creationflags=flags, text=True, encoding="utf-8", errors="replace")
    output.write_text(result.stdout, encoding="utf-8")
    print(result.stdout, end="")
    if result.returncode:
        raise RuntimeError("Check failed with exit code " + str(result.returncode) + ": " + output.name)
    return result.stdout

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--unity-data", type=Path, help="Existing Editor/Data or Unity.app/Contents; managed compiler only.")
    parser.add_argument("--static-only", action="store_true")
    parser.add_argument("--suite", action="append", choices=("unrecorded-memory", "recording-memory", "recorded-acquisition", "original-local-raw"), help="Run only selected managed suites; default is all four.")
    parser.add_argument("--framework-references", type=Path, help="Existing .NET Framework 4.8-compatible references; Windows default is derived from SystemRoot.")
    args = parser.parse_args()
    base = ROOT / "build/public-woz-checks"
    base.mkdir(parents=True, exist_ok=True)
    out = Path(tempfile.mkdtemp(prefix=datetime.datetime.now(datetime.timezone.utc).strftime("%Y%m%dT%H%M%S-"), dir=base))
    result = {"scope": "Sanitized public source; not a player reproduction or hardware acceptance", "static": [], "managed": []}
    for name in STATIC:
        run([sys.executable, "-B", str(ROOT / "scripts" / name)], out / (name + ".log"), 60)
        result["static"].append(name)
    if not args.static_only:
        spec = importlib.util.spec_from_file_location("public_compiler_discovery", ROOT / "scripts/test.py")
        discovery = importlib.util.module_from_spec(spec); spec.loader.exec_module(discovery)
        if args.unity_data:
            os.environ["STROOP_UNITY_CONTENTS"] = str(args.unity_data)
        toolset = discovery.unity_tools()
        if not toolset:
            raise RuntimeError("No existing Unity Mono compiler. Supply --unity-data; no installation is performed.")
        unity, mono, csc = toolset
        refs = args.framework_references or (Path(os.environ["SystemRoot"]) / "Microsoft.NET/Framework64/v4.0.30319" if os.name == "nt" else unity / "MonoBleedingEdge/lib/mono/4.8-api")
        if not all((refs / name).is_file() for name in ("mscorlib.dll", "System.dll", "System.Core.dll")):
            raise RuntimeError("Existing .NET Framework 4.8-compatible reference assemblies are required; use --framework-references.")
        support = (ROOT / "Tests/TestRunner.cs").read_text(encoding="utf-8-sig")
        support = support[:support.index("    public static class Program")] + "}"
        (out / "FocusedTestSupport.cs").write_text(support, encoding="utf-8")
        (out / "LocalRawRegression.cs").write_text(
            'using System; namespace AvatarStroop.Tests { internal static class LocalRawRegression { static int Main(){'
            'var suite=new TestSuite();ViveLegacyRawSchemaTests.Register(suite);ViveLegacyRawChoiceTests.Register(suite);'
            'ViveLegacyRawLifecycleTests.Register(suite);Console.WriteLine("RESULT original local raw "+suite.Passed+" passed; "+suite.Failed+" failed.");'
            'return suite.Failed==0?0:1;} } }', encoding="utf-8")
        core = sorted((ROOT / "Assets/Stroop/Core").glob("*.cs")) + sorted((ROOT / "Assets/Stroop/Runtime").glob("*.cs"))
        def compile_run(name, mode, tests, main, extra=()):
            if args.suite and name not in args.suite:
                return
            sources = core + [ROOT / "Tests" / x for x in tests] + [out / "FocusedTestSupport.cs"] + list(extra)
            binary = out / (name + ".exe")
            options = ["-nostdlib+", "-r:" + str(refs / "mscorlib.dll"), "-r:" + str(refs / "System.dll"), "-r:" + str(refs / "System.Core.dll"), "-nologo", "-langversion:7.3", "-warnaserror", "-target:exe",
                       "-define:" + MODES[mode], "-main:" + main, "-out:" + str(binary)]
            # Explicit fixture-only warnings remain visible and are checked against reviewed declarations.
            if mode == "recording":
                options.append("-warnaserror-:0649")
            rsp = out / (name + ".rsp")
            rsp.write_text("\n".join('"' + x + '"' for x in options + [str(x.relative_to(ROOT)) for x in sources]), encoding="utf-8")
            log = run([str(mono), str(csc), "-noconfig", "@" + str(rsp)], out / (name + "-compile.log"), 120)
            if mode == "recording":
                import re
                allowed = json.loads((ROOT / "scripts/public-woz-fixture-warning-declarations.json").read_text())
                for match in re.finditer(r"(?m)^(.+?)\((\d+),\d+\): warning CS0649:", log):
                    relative = match[1].replace("\\", "/")
                    declaration = (ROOT / relative).read_text(encoding="utf-8-sig").splitlines()[int(match[2])-1].strip()
                    if declaration not in allowed.get(relative, []):
                        raise RuntimeError("Unreviewed fixture warning: " + relative + ":" + match[2])
            run([str(binary)] if os.name == "nt" else [str(mono), str(binary)], out / (name + "-tests.log"), 240)
            result["managed"].append({"name": name, "core_runtime_sources": len(core), "binary_sha256": hashlib.sha256(binary.read_bytes()).hexdigest()})
        compile_run("unrecorded-memory", "ordinary", BASE_TESTS, "AvatarStroop.Tests.ViveLegacyWozSceneTests")
        compile_run("recording-memory", "recording", RECORDED_TESTS, "AvatarStroop.Tests.ViveLegacyWozRecordingPumpTests")
        compile_run("recorded-acquisition", "recording", RECORDED_TESTS + ("ViveLegacyWozRecordingSceneAcquisitionTests.cs",),
                    "AvatarStroop.Tests.ViveLegacyWozRecordingSceneAcquisitionTests", (ROOT / "Assets/Stroop/Unity/ViveLegacyWozRawSceneAcquisition.cs",))
        compile_run("original-local-raw", "recording", RECORDED_TESTS + ("ViveLegacyRawSchemaTests.cs", "ViveLegacyRawChoiceTests.cs", "ViveLegacyRawLifecycleTests.cs"),
                    "AvatarStroop.Tests.LocalRawRegression", (out / "LocalRawRegression.cs",))
    (out / "result.json").write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
    print("Public check results: " + str(out))
    return 0

if __name__ == "__main__":
    sys.exit(main())

