"""Compiler discovery regressions; no compiler or Unity Editor is launched."""
import contextlib
import io
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

import test as compiler
import integration


class UnityToolsTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.root = Path(self.directory.name)
        self.environment = patch.dict(compiler.os.environ)
        self.environment.start()
        compiler.os.environ.pop("STROOP_UNITY_CONTENTS", None)
        self.addCleanup(self.environment.stop)

    def installation(self, base, executable):
        mono = base / "MonoBleedingEdge/bin" / executable
        csc = base / "MonoBleedingEdge/lib/mono/4.5/csc.exe"
        for path in (mono, csc):
            path.parent.mkdir(parents=True, exist_ok=True)
            path.touch()
        return base, mono, csc

    def test_windows_discovers_program_files_and_uses_exe(self):
        program_files = self.root / "Program Files"
        expected = self.installation(program_files / "Unity/Hub/Editor/6000.6.7f1/Editor/Data", "mono.exe")
        (expected[1].parent / "mono").touch()  # Unity also ships this non-Windows launcher.
        with patch.object(compiler.sys, "platform", "win32"), patch.dict(compiler.os.environ, {"ProgramFiles": str(program_files)}):
            self.assertEqual(expected, compiler.unity_tools())

    def test_explicit_path_wins_without_searching_default_installations(self):
        expected = self.installation(self.root / "Pinned Unity/Editor/Data", "mono.exe")
        with patch.object(compiler.sys, "platform", "win32"), patch.dict(compiler.os.environ, {"STROOP_UNITY_CONTENTS": str(expected[0])}), patch.object(compiler.glob, "glob") as search:
            self.assertEqual(expected, compiler.unity_tools())
            search.assert_not_called()

    def test_invalid_explicit_path_never_falls_back(self):
        for broken_path in ("", str(self.root / "missing"), str(self.root / "Incomplete/Editor/Data")):
            with self.subTest(path=broken_path), patch.dict(compiler.os.environ, {"STROOP_UNITY_CONTENTS": broken_path}), patch.object(compiler.glob, "glob") as search, patch.object(compiler.shutil, "which") as dotnet, contextlib.redirect_stderr(io.StringIO()) as errors:
                self.assertEqual(2, compiler.main())
                self.assertIn("STROOP_UNITY_CONTENTS", errors.getvalue())
                search.assert_not_called()
                dotnet.assert_not_called()

    def test_windows_rejects_non_exe_launcher(self):
        base, _, _ = self.installation(self.root / "Editor/Data", "mono")
        with patch.object(compiler.sys, "platform", "win32"), patch.dict(compiler.os.environ, {"STROOP_UNITY_CONTENTS": str(base)}):
            with self.assertRaises(ValueError):
                compiler.unity_tools()

    def test_empty_explicit_path_does_not_use_current_directory(self):
        with patch.dict(compiler.os.environ, {"STROOP_UNITY_CONTENTS": ""}), patch.object(compiler.Path, "is_file", return_value=True) as is_file:
            with self.assertRaises(ValueError):
                compiler.unity_tools()
            is_file.assert_not_called()

    def test_missing_csc_is_not_a_usable_installation(self):
        expected = self.installation(self.root / "Editor/Data", "mono.exe")
        expected[2].unlink()
        with patch.object(compiler.sys, "platform", "win32"), patch.dict(compiler.os.environ, {"STROOP_UNITY_CONTENTS": str(expected[0])}):
            with self.assertRaises(ValueError):
                compiler.unity_tools()

    def test_automatic_discovery_skips_incomplete_installations(self):
        expected = self.installation(self.root / "Complete/Editor/Data", "mono.exe")
        incomplete = self.installation(self.root / "Incomplete/Editor/Data", "mono.exe")
        incomplete[2].unlink()
        with patch.object(compiler.sys, "platform", "win32"), patch.object(compiler.glob, "glob", return_value=[str(expected[0]), str(incomplete[0])]):
            self.assertEqual(expected, compiler.unity_tools())

    def test_unix_discovery_keeps_platform_layout_and_mono(self):
        cases = (("darwin", "/Applications/Unity/Hub/Editor/*/Unity.app/Contents", self.root / "Unity.app/Contents"),
                 ("linux", str(self.root / "Unity/Hub/Editor/*/Editor/Data"), self.root / "Editor/Data"))
        for platform, pattern, base in cases:
            expected = self.installation(base, "mono")
            with self.subTest(platform=platform), patch.object(compiler.sys, "platform", platform), patch.object(compiler.Path, "home", return_value=self.root), patch.object(compiler.glob, "glob", return_value=[str(base)]) as search:
                self.assertEqual(expected, compiler.unity_tools())
                search.assert_called_once_with(pattern)

    def test_no_detected_installation_returns_none(self):
        with patch.object(compiler.glob, "glob", return_value=[]):
            self.assertIsNone(compiler.unity_tools())

    def test_integration_reports_explicit_path_errors_without_running(self):
        with patch.object(integration.sys, "argv", ["integration.py"]), patch.dict(compiler.os.environ, {"STROOP_UNITY_CONTENTS": str(self.root / "missing")}), patch.object(integration.shutil, "which") as dotnet, patch.object(integration.subprocess, "run") as run, contextlib.redirect_stderr(io.StringIO()) as errors:
            self.assertEqual(2, integration.main())
            self.assertIn("STROOP_UNITY_CONTENTS", errors.getvalue())
            dotnet.assert_not_called()
            run.assert_not_called()

    def test_integration_dotnet_flag_bypasses_unity_discovery(self):
        source = self.root / "Apps/SimulatorHost.cs"
        source.parent.mkdir()
        source.touch()
        with patch.object(integration.sys, "argv", ["integration.py", "--dotnet"]), patch.dict(compiler.os.environ, {"STROOP_UNITY_CONTENTS": str(self.root / "missing")}), patch.object(integration, "ROOT", self.root), patch.object(integration, "unity_tools") as discovery, patch.object(integration.shutil, "which", return_value="dotnet"), patch.object(integration.subprocess, "run") as run, patch.object(integration, "run_scenario"), patch.object(integration, "run_killed_recording"), contextlib.redirect_stdout(io.StringIO()):
            self.assertEqual(0, integration.main())
            discovery.assert_not_called()
            run.assert_called_once()
            self.assertEqual("dotnet", run.call_args.args[0][0])


if __name__ == "__main__":
    unittest.main()
