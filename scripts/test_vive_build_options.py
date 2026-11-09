"""Explicit recording build plumbing regressions. Never launches Unity or a player.

The C# check compiles only the verbatim pure build-request helper with synthetic
strings/booleans. Its inputs and logs stay under this source's build directory.
"""
import contextlib
import importlib.util
import io
import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[1]
CHECK = ROOT / "build/legacy-build-plumbing-check"
SPEC = importlib.util.spec_from_file_location("vive_stroop_build", Path(__file__).with_name("build-vive-stroop-preview.py"))
build = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(build)


class ViveBuildOptionsTests(unittest.TestCase):
    def setUp(self):
        CHECK.mkdir(parents=True, exist_ok=True)
        self.directory = tempfile.TemporaryDirectory(prefix="synthetic-", dir=CHECK)
        self.addCleanup(self.directory.cleanup)
        self.root = Path(self.directory.name)

    def test_environment_scrubs_inherited_flags_for_each_build_kind(self):
        ambient = {"STROOP_VIVE_CONTROLLED_RECORDING_DEVELOPMENT": "1",
                   "STROOP_VIVE_LEGACY_RAW_RECORDING_DEVELOPMENT": "1", "KEEP_TEST_VALUE": "kept"}
        for controlled, legacy in ((False, False), (True, False), (True, True)):
            with self.subTest(controlled=controlled, legacy=legacy), patch.dict(os.environ, ambient):
                result = build.editor_environment(self.root, controlled, legacy)
                self.assertEqual("1" if controlled else None, result.get("STROOP_VIVE_CONTROLLED_RECORDING_DEVELOPMENT"))
                self.assertEqual("1" if legacy else None, result.get("STROOP_VIVE_LEGACY_RAW_RECORDING_DEVELOPMENT"))
                self.assertEqual(str(self.root), result["STROOP_VIVE_CLASSROOM_OUTPUT"])
                self.assertEqual("kept", result["KEEP_TEST_VALUE"])
                self.assertEqual("1", os.environ["STROOP_VIVE_LEGACY_RAW_RECORDING_DEVELOPMENT"])

    def test_all_option_helpers_reject_legacy_without_controlled(self):
        actions = (lambda: build.editor_environment(self.root, False, True),
                   lambda: build.recording_manifest(False, True),
                   lambda: build.build_intent(self.root, self.root, False, True),
                   lambda: build.start_here_text(False, True),
                   lambda: build.run_editor("unused", self.root, self.root, "Configure", False, True))
        with patch.object(build.subprocess, "run") as run:
            for action in actions:
                with self.assertRaisesRegex(ValueError, "requires --controlled-recording-development"):
                    action()
            run.assert_not_called()

    def test_cli_rejects_invalid_combination_before_workspace_or_launch(self):
        args = ["build", "--editor", "unused", "--project", "unused", "--output", "unused",
                "--legacy-raw-recording-development", "--check-only"]
        with patch.object(build.sys, "argv", args), patch.object(build.prepare, "verify_workspace_project") as verify, patch.object(build.subprocess, "run") as run, contextlib.redirect_stderr(io.StringIO()) as error:
            with self.assertRaises(SystemExit) as result:
                build.main()
            self.assertEqual(2, result.exception.code)
            self.assertIn("requires --controlled-recording-development", error.getvalue())
            verify.assert_not_called()
            run.assert_not_called()

    def test_cli_rejects_unknown_flag_without_launch(self):
        with patch.object(build.sys, "argv", ["build", "--editor", "unused", "--project", "unused", "--output", "unused", "--legacy-recording"]), patch.object(build.subprocess, "run") as run, contextlib.redirect_stderr(io.StringIO()):
            with self.assertRaises(SystemExit) as result:
                build.main()
            self.assertEqual(2, result.exception.code)
            run.assert_not_called()

    def test_manifest_and_intent_bind_both_exact_flags(self):
        for controlled, legacy in ((False, False), (True, False), (True, True)):
            with self.subTest(controlled=controlled, legacy=legacy):
                intent = build.build_intent(self.root, self.root / "output", controlled, legacy)
                manifest = build.recording_manifest(controlled, legacy)
                for name, expected in (("controlled_recording_development", controlled), ("legacy_raw_recording_development", legacy)):
                    self.assertIs(expected, intent[name])
                    self.assertIs(expected, manifest[name])
                self.assertIs(controlled, manifest["named_explicit_recording_requested"])
                self.assertIs(legacy, manifest["legacy_explicit_recording_requested"])
                self.assertFalse(manifest["recording_default"])
                self.assertFalse(manifest["preview_configuration_records"])
                self.assertIn("memory only until close", manifest["preview_task_records"])

    def test_start_here_distinguishes_three_scopes_and_never_automatic(self):
        ordinary = build.start_here_text()
        named = build.start_here_text(True, False, "SYNTHETIC GUIDE")
        legacy = build.start_here_text(True, True, "SYNTHETIC GUIDE")
        self.assertIn("ordinary preview only; recording unavailable", ordinary)
        self.assertIn("named explicit self-tests only; legacy is memory-only", named)
        self.assertIn("named and legacy explicit self-tests", legacy)
        for text in (ordinary, named, legacy):
            self.assertIn("Recording is OFF by default", text)
            self.assertIn("non-recording", text)
            self.assertIn("no task or eye files", text)
        self.assertTrue(named.endswith("SYNTHETIC GUIDE"))
        self.assertTrue(legacy.endswith("SYNTHETIC GUIDE"))

    def test_run_editor_binds_only_explicit_environment_to_every_action(self):
        expected = {"Configure": "VIVE_CLASSROOM_CONFIGURED:", "BuildWindowsPreview": "VIVE_CLASSROOM_BUILD_COMPLETE:", "RenderFixture": "VIVE_CLASSROOM_FIXTURE_COMPLETE:"}
        for controlled, legacy in ((False, False), (True, False), (True, True)):
            for action, marker in expected.items():
                with self.subTest(controlled=controlled, legacy=legacy, action=action), patch.object(build.subprocess, "run") as run, patch.object(Path, "is_file", return_value=True), patch.object(Path, "read_text", return_value=marker), contextlib.redirect_stdout(io.StringIO()):
                    run.return_value.returncode = 0
                    build.run_editor("NEVER-LAUNCHED", self.root, self.root, action, controlled, legacy)
                    run.assert_called_once()
                    command, = run.call_args.args
                    self.assertEqual("NEVER-LAUNCHED", command[0])
                    self.assertIn("AvatarStroop.Unity.Editor.ViveStroopPreviewBuild." + action, command)
                    self.assertEqual("-nographics" in command, action != "RenderFixture")
                    environment = run.call_args.kwargs["env"]
                    self.assertEqual("1" if controlled else None, environment.get("STROOP_VIVE_CONTROLLED_RECORDING_DEVELOPMENT"))
                    self.assertEqual("1" if legacy else None, environment.get("STROOP_VIVE_LEGACY_RAW_RECORDING_DEVELOPMENT"))

    def test_main_wires_intent_manifest_and_start_here_without_real_build(self):
        for controlled, legacy in ((False, False), (True, False), (True, True)):
            with self.subTest(controlled=controlled, legacy=legacy):
                workspace = self.root / ("legacy" if legacy else "named" if controlled else "ordinary")
                source, project, output = workspace / "source", workspace / "project", workspace / "output"
                (source / "docs").mkdir(parents=True)
                project.mkdir()
                (source / "docs/controlled-recording-self-test-guide.txt").write_text("SYNTHETIC GUIDE", encoding="utf-8")
                args = ["build", "--editor", "NEVER-LAUNCHED", "--project", str(project), "--output", str(output)]
                if controlled: args.append("--controlled-recording-development")
                if legacy: args.append("--legacy-raw-recording-development")

                def fake_editor(editor, actual_project, actual_output, action, actual_controlled, actual_legacy):
                    self.assertEqual((project, output, controlled, legacy), (actual_project, actual_output, actual_controlled, actual_legacy))
                    if action == "BuildWindowsPreview":
                        (output / "UnityPlayer").mkdir()
                        (output / "UnityPlayer/AvatarStroopViveEyePlayer.exe").write_text("NOT A PROGRAM", encoding="utf-8")
                        (output / "vive-classroom-scene-validation.json").write_text("{}", encoding="utf-8")

                def fake_launcher(*unused):
                    result = output / "AvatarStroopViveEyePreview.exe"
                    result.write_text("NOT A PROGRAM", encoding="utf-8")
                    return result

                with patch.object(build, "ROOT", source), patch.object(build.sys, "argv", args), patch.object(build.sys, "platform", "win32"), patch.object(build.prepare, "verify_workspace_project"), patch.object(build.prepare, "check_editor"), patch.object(build.prepare, "verify_prepared", return_value={"source_files": {}}), patch.object(build.prepare, "reject_link"), patch.object(build, "run_editor", side_effect=fake_editor) as run, patch.object(build.rehearsal, "compile_launcher", side_effect=fake_launcher), patch.object(build.subprocess, "run", side_effect=AssertionError("No external process permitted in mocked build")), contextlib.redirect_stdout(io.StringIO()):
                    self.assertEqual(0, build.main())
                self.assertEqual(["Configure", "BuildWindowsPreview"], [call.args[3] for call in run.call_args_list])
                intent = json.loads((project / "vive-classroom-isolated-intent.json").read_text(encoding="utf-8"))
                manifest = json.loads((output / "vive-classroom-build.json").read_text(encoding="utf-8"))
                for name, value in (("controlled_recording_development", controlled), ("legacy_raw_recording_development", legacy)):
                    self.assertIs(value, intent[name])
                    self.assertIs(value, manifest[name])
                self.assertEqual("built", manifest["status"])
                self.assertEqual(controlled, (output / "LocalSelfTestData").is_dir())
                self.assertEqual(build.start_here_text(controlled, legacy, "SYNTHETIC GUIDE" if controlled else ""), (output / "START-HERE.txt").read_text(encoding="utf-8"))

    def test_editor_builder_uses_validated_defines_and_existing_xr_guards(self):
        source = (ROOT / "Assets/Stroop/Unity/Editor/ViveStroopPreviewBuild.cs").read_text(encoding="utf-8-sig")
        self.assertTrue(source.startswith("#if STROOP_XR_PREVIEW && UNITY_EDITOR"))
        method = source.split("public static void BuildWindowsPreview()", 1)[1].split("public static void RenderFixture()", 1)[0]
        self.assertIn("var recordingDefines=ReadRecordingBuildDefines(project);", method)
        self.assertIn('bool controlled=recordingDefines.Contains("STROOP_CONTROLLED_RECORDING_DEVELOPMENT");', method)
        self.assertIn('bool legacy=recordingDefines.Contains("STROOP_LEGACY_RAW_RECORDING_DEVELOPMENT");', method)
        self.assertIn("target=BuildTarget.StandaloneWindows64,options=controlled?BuildOptions.Development:BuildOptions.None", method)
        self.assertIn("extraScriptingDefines=recordingDefines", method)
        self.assertIn("!general.InitManagerOnStart", method)
        self.assertIn("!general.Manager.automaticLoading&&!general.Manager.automaticRunning", method)
        self.assertNotIn("SetScriptingDefine", source)
        reader = source.split("static string[] ReadRecordingBuildDefines(string project)", 1)[1].split("public static void BuildWindowsPreview()", 1)[0]
        self.assertIn('Environment.GetEnvironmentVariable("STROOP_VIVE_CONTROLLED_RECORDING_DEVELOPMENT")', reader)
        self.assertIn('Environment.GetEnvironmentVariable("STROOP_VIVE_LEGACY_RAW_RECORDING_DEVELOPMENT")', reader)
        self.assertIn("intent.controlled_recording_development,intent.legacy_raw_recording_development", reader)
        self.assertIn("PlayerSettings.GetScriptingDefineSymbols(UnityEditor.Build.NamedBuildTarget.Standalone)", reader)
        self.assertIn("ReadRecordingBuildDefines(Project());", source.split("public static void Configure()", 1)[1].split("QuestPreviewBuild.Configure();", 1)[0])
        self.assertIn("ReadRecordingBuildDefines(project);", source.split("public static void RenderFixture()", 1)[1])
        self.assertIn("string scope=ViveStroopPreviewRig.ScopeNotice;", source)
        self.assertIn('scope.Contains("Legacy A/B/C/D has no recording or export")', source)
        self.assertIn('scope.Contains("Local VIVE recording is unavailable")', source)

    def test_guide_requests_short_withdrawal_not_completed_four_block_run(self):
        guide = (ROOT / "docs/controlled-recording-self-test-guide.txt").read_text(encoding="utf-8")
        for phrase in ("ONLY 1-2 PRACTICE TRIALS", "existing private LOCAL directory", "NO completed CSV receipt", "incomplete/UNKNOWN", "does not request a long rerun", "no data upload is needed", "calibration", "tracking/focus", "NON-RECORDING"):
            self.assertIn(phrase, guide)
        self.assertNotIn("Complete 24 Automatic", guide)

    def test_actual_csharp_request_helper_exhaustive_matrix(self):
        windir = os.environ.get("WINDIR")
        if not windir:
            self.skipTest("Windows directory environment is unavailable; no compiler fallback")
        csc = Path(windir) / "Microsoft.NET/Framework64/v4.0.30319/csc.exe"
        if os.name != "nt" or not csc.is_file():
            self.skipTest("Official installed Windows C# compiler unavailable; no Unity fallback")
        source = (ROOT / "Assets/Stroop/Unity/Editor/ViveStroopPreviewBuild.cs").read_text(encoding="utf-8-sig")
        helper = source.split("// BEGIN PURE RECORDING BUILD REQUEST", 1)[1].split("// END PURE RECORDING BUILD REQUEST", 1)[0]
        harness = r'''
static class RequestChecks {
    static int Main() {
        string[] values={null,"","1","0","true"," 1","1 ","2"};
        string[] persisted={"STROOP_XR_PREVIEW","STROOP_XR_PREVIEW;STROOP_LEGACY_RAW_RECORDING_DEVELOPMENT","STROOP_CONTROLLED_RECORDING_DEVELOPMENT"," STROOP_LEGACY_RAW_RECORDING_DEVELOPMENT "};
        int checks=0;
        foreach(string c in values)foreach(string l in values)for(int intent=0;intent<4;intent++)for(int p=0;p<persisted.Length;p++) {
            bool ic=(intent&1)!=0,il=(intent&2)!=0;
            bool expected=(String.IsNullOrEmpty(c)||c=="1")&&(String.IsNullOrEmpty(l)||l=="1")&&
                (l!="1"||c=="1")&&ic==(c=="1")&&il==(l=="1")&&p==0;
            bool accepted=false;string[] result=null;
            try {result=ViveRecordingBuildRequest.Defines(c,l,ic,il,persisted[p]);accepted=true;}
            catch(InvalidOperationException) {}
            if(accepted!=expected)throw new Exception("build request acceptance mismatch");
            if(accepted) {
                string desired=l=="1"?"STROOP_CONTROLLED_RECORDING_DEVELOPMENT;STROOP_LEGACY_RAW_RECORDING_DEVELOPMENT":c=="1"?"STROOP_CONTROLLED_RECORDING_DEVELOPMENT":"";
                if(String.Join(";",result)!=desired)throw new Exception("per-build define mismatch");
            }
            checks++;
        }
        Console.WriteLine("PASS "+checks+" pure build-request cases; no Unity/player/runtime execution.");
        return 0;
    }
}
'''
        test_source = CHECK / "BuildRequestChecks.cs"
        executable = CHECK / "BuildRequestChecks.exe"
        test_source.write_text("using System;\n" + helper + harness, encoding="utf-8")
        compile_result = subprocess.run([str(csc), "/nologo", "/noconfig", "/warnaserror+", "/target:exe", "/out:" + str(executable), str(test_source)], capture_output=True, text=True)
        (CHECK / "build-request-compile.log").write_text(compile_result.stdout + compile_result.stderr, encoding="utf-8")
        self.assertEqual(0, compile_result.returncode, compile_result.stdout + compile_result.stderr)
        result = subprocess.run([str(executable)], capture_output=True, text=True)
        (CHECK / "build-request-tests.log").write_text(result.stdout + result.stderr, encoding="utf-8")
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertIn("PASS 1024 pure build-request cases", result.stdout)


if __name__ == "__main__":
    unittest.main()
