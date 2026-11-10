"""Packaging source-selection regression. Compiler and process calls are mocked."""
import importlib.util
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest import mock

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("vive_launcher_sources", ROOT / "scripts/build-vive-eye-preview.py")
build = importlib.util.module_from_spec(spec)
spec.loader.exec_module(build)


class ViveLauncherSourceTests(unittest.TestCase):
    def test_assertions_are_verbatim_and_exclude_global_fixtures(self):
        source = (ROOT / "Tests/TestRunner.cs").read_text(encoding="utf-8-sig")
        assertions = source[source.index("    public static class Check"):source.index("    public sealed class ManualClock")]
        self.assertEqual("using System;\nnamespace AvatarStroop.Tests\n{\n" + assertions + "}\n", build.launcher_smoke_assertions(ROOT))
        for excluded in ("ManualClock", "Fixtures", "class Program", "AvatarStroop.Core"):
            self.assertNotIn(excluded, build.launcher_smoke_assertions(ROOT))

    def test_changed_assertion_boundaries_stop_compilation(self):
        original = (ROOT / "Tests/TestRunner.cs").read_text(encoding="utf-8-sig")
        for changed in (original.replace("public static class Check", "public static class RenamedCheck"),
                        original.replace("public sealed class ManualClock", "public sealed class RenamedClock"),
                        original.replace("public sealed class TestSuite", "public sealed class RenamedSuite"),
                        original + "    public static class Check"):
            with mock.patch.object(Path, "read_text", return_value=changed):
                with self.assertRaises(ValueError):
                    build.launcher_smoke_assertions(ROOT)

    def test_only_launch_plan_smoke_and_assertions_are_compiled(self):
        calls = []
        def capture(command, **kwargs):
            calls.append(command)
            if "-main:AvatarStroop.Tests.VivePreviewLaunchEnvironmentSmoke" in command:
                sources = [Path(value) for value in command if value.endswith(".cs")]
                self.assertEqual(["VivePreviewLaunchPlan.cs", "VivePreviewLaunchPlanTests.cs", "LauncherSmokeAssertions.cs"], [p.name for p in sources])
                self.assertEqual(build.launcher_smoke_assertions(ROOT), sources[2].read_text(encoding="utf-8"))
            return subprocess.CompletedProcess(command, 0)
        work = ROOT / "build/launcher-source-tests"
        work.mkdir(parents=True, exist_ok=True)
        with tempfile.TemporaryDirectory(prefix="mock-", dir=work) as temporary:
            with mock.patch.object(build, "compiler_command", return_value=["mock-compiler"]), mock.patch.object(build.subprocess, "run", side_effect=capture):
                result = build.compile_launcher(Path("C:/official/Editor/Unity.exe"), ROOT, Path(temporary))
            self.assertEqual("AvatarStroopViveEyePreview.exe", result.name)
        self.assertEqual(4, len(calls))
        self.assertEqual("EnvironmentSmoke.exe", Path(calls[2][0]).name)
        self.assertEqual("EnvironmentChild.exe", Path(calls[2][1]).name)
        self.assertEqual(["VivePreviewLaunchPlan.cs", "ViveEyePreviewLauncher.cs"], [Path(v).name for v in calls[3] if v.endswith(".cs")])
        self.assertTrue(all("TestRunner.cs" not in v and "WozLanPairingUiTests.cs" not in v for call in calls for v in call))


if __name__ == "__main__":
    unittest.main()
