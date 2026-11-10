"""Source/in-memory mocked WoZ build-option checks; no files, compiler or app runs."""
import contextlib
import copy
import importlib.util
import io
import itertools
import json
import os
from pathlib import Path
import re
import unittest
from unittest.mock import patch


ROOT = Path(__file__).resolve().parents[1]
SPEC = importlib.util.spec_from_file_location("woz_recording_build", Path(__file__).with_name("build-vive-stroop-preview.py"))
build = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(build)
EDITOR = (ROOT / "Assets/Stroop/Unity/Editor/ViveStroopPreviewBuild.cs").read_text(encoding="utf-8-sig")
BUILDER = (ROOT / "scripts/build-vive-stroop-preview.py").read_text(encoding="utf-8-sig")
MODES = ((False, False, False), (True, False, False), (True, True, False), (True, True, True))
ENV_NAMES = ("STROOP_VIVE_CONTROLLED_RECORDING_DEVELOPMENT", "STROOP_VIVE_LEGACY_RAW_RECORDING_DEVELOPMENT", "STROOP_VIVE_LEGACY_WOZ_RECORDING_DEVELOPMENT")
FLAG_NAMES = ("controlled_recording_development", "legacy_raw_recording_development", "legacy_woz_recording_development")


def editor_manifest(controlled, legacy, woz):
    """Decode only literals and the three boolean substitutions in the JSON source."""
    expression = EDITOR.split('File.WriteAllText(Path.Combine(output,"vive-classroom-scene-validation.json"),', 1)[1].split(");", 1)[0]
    token = re.compile(r'\s*(?:(?P<literal>"(?:\\.|[^"\\])*")|\((?P<flag>controlled|legacy|woz)\?"true":"false"\)|(?P<plus>\+))')
    values = dict(controlled=controlled, legacy=legacy, woz=woz)
    offset, parts, need_value = 0, [], True
    while offset < len(expression):
        match = token.match(expression, offset)
        if match is None or bool(match.group("plus")) == need_value:
            raise AssertionError("Unexpected scene-manifest expression; no source evaluation allowed")
        if match.group("literal"):
            parts.append(json.loads(match.group("literal")))
        elif match.group("flag"):
            parts.append("true" if values[match.group("flag")] else "false")
        need_value = not need_value
        offset = match.end()
    if need_value:
        raise AssertionError("Incomplete scene-manifest expression")
    return json.loads("".join(parts))


class WozRecordingBuildOptionsTests(unittest.TestCase):
    def test_all_helpers_reject_missing_prerequisites_before_launch(self):
        with patch.object(build.subprocess, "run", side_effect=AssertionError("No process allowed")) as run:
            for controlled, legacy, woz in itertools.product((False, True), repeat=3):
                valid = (controlled, legacy, woz) in MODES
                actions = (
                    lambda: build.validate_recording_options(controlled, legacy, woz),
                    lambda: build.editor_environment(ROOT, controlled, legacy, woz),
                    lambda: build.recording_manifest(controlled, legacy, woz),
                    lambda: build.build_intent(ROOT, ROOT, controlled, legacy, woz),
                    lambda: build.start_here_text(controlled, legacy, "", woz),
                )
                for action in actions:
                    with self.subTest(mode=(controlled, legacy, woz), helper=action):
                        if valid:
                            action()
                        else:
                            with self.assertRaises(ValueError):
                                action()
                if not valid:
                    with self.assertRaises(ValueError):
                        build.run_editor("NEVER-LAUNCHED", ROOT, ROOT, "Configure", controlled, legacy, woz)
            run.assert_not_called()

    def test_ambient_flags_are_scrubbed_and_only_explicit_options_forwarded(self):
        ambient = dict.fromkeys(ENV_NAMES, "1")
        ambient["KEEP_SYNTHETIC_VALUE"] = "kept"
        for mode in MODES:
            with self.subTest(mode=mode), patch.dict(os.environ, ambient):
                result = build.editor_environment(ROOT, *mode)
                for name, requested in zip(ENV_NAMES, mode):
                    self.assertEqual("1" if requested else None, result.get(name))
                    self.assertEqual("1", os.environ[name])
                self.assertEqual("kept", result["KEEP_SYNTHETIC_VALUE"])
                self.assertEqual(str(ROOT), result["STROOP_VIVE_CLASSROOM_OUTPUT"])
        self.assertNotIn(ENV_NAMES[2], build.editor_environment(ROOT))
        self.assertNotIn(ENV_NAMES[2], build.editor_environment(ROOT, True, True))

    def test_intent_and_manifest_bind_each_exact_option(self):
        for controlled, legacy, woz in MODES:
            with self.subTest(mode=(controlled, legacy, woz)):
                manifest = build.recording_manifest(controlled, legacy, woz)
                intent = build.build_intent(ROOT, ROOT, controlled, legacy, woz)
                for name, requested in zip(FLAG_NAMES, (controlled, legacy, woz)):
                    self.assertIs(requested, manifest[name])
                    self.assertIs(requested, intent[name])
                self.assertIs(controlled, manifest["optional_local_eye_and_task_recording"])
                self.assertIs(controlled, manifest["named_explicit_recording_requested"])
                self.assertIs(legacy, manifest["legacy_explicit_recording_requested"])
                self.assertIs(woz, manifest["network_recording_available"])
                self.assertIs(woz, manifest["participant_local_woz_recording_available"])
                self.assertFalse(manifest["recording_default"])
                self.assertFalse(manifest["preview_configuration_records"])
                self.assertIn("memory only until close", manifest["preview_task_records"])
                self.assertIn("unvalidated" if woz else "unrecorded", manifest["woz_recording_scope"])

    def test_start_here_preserves_old_scopes_and_marks_recorded_woz_separately(self):
        ordinary = build.start_here_text()
        named = build.start_here_text(True, False, "SYNTHETIC GUIDE")
        legacy = build.start_here_text(True, True, "SYNTHETIC GUIDE")
        woz = build.start_here_text(True, True, "SYNTHETIC GUIDE", True)
        for text, scope in ((ordinary, "ordinary preview only; recording unavailable"),
                            (named, "named explicit self-tests only; legacy is memory-only"),
                            (legacy, "named and legacy explicit self-tests"),
                            (woz, "optional participant-local Legacy WoZ recording")):
            self.assertIn(scope, text)
            self.assertIn("Recording is OFF by default", text)
            self.assertIn("starts no task, XR, eye source or file capture", text)
            self.assertIn("non-recording", text)
            self.assertIn("no task or eye files", text)
        self.assertIn("OPTIONAL UNRECORDED LEGACY WOZ", ordinary)
        self.assertIn("WoZ recording/export is unavailable", ordinary)
        self.assertNotIn("WoZ recording/export is unavailable", woz)
        for text in (named, legacy, woz):
            self.assertTrue(text.endswith("SYNTHETIC GUIDE"))
        for text in (ordinary, named, legacy):
            self.assertNotIn("OPTIONAL PARTICIPANT-LOCAL LEGACY WOZ RECORDING", text)
        for phrase in ("both headset choices", "explicit recording start", "forwarding plus participant-local storage",
                       "mapped operator head/eye poses plus source, time and status", "Participant raw eyes, answers, timings and ratings never go to the operator",
                       "authenticated, not encrypted", "incomplete/UNKNOWN", "remain unvalidated", "No data submission",
                       "SEPARATE LOCAL AUTOMATIC / NAMED SELF-TEST GUIDE"):
            self.assertIn(phrase, woz)

    def test_every_editor_action_receives_only_explicit_environment(self):
        markers = {"Configure": "VIVE_CLASSROOM_CONFIGURED:", "BuildWindowsPreview": "VIVE_CLASSROOM_BUILD_COMPLETE:", "RenderFixture": "VIVE_CLASSROOM_FIXTURE_COMPLETE:"}
        for mode in MODES:
            for action, marker in markers.items():
                with self.subTest(mode=mode, action=action), patch.dict(os.environ, dict.fromkeys(ENV_NAMES, "1")), patch.object(build.subprocess, "run") as run, patch.object(Path, "is_file", return_value=True), patch.object(Path, "read_text", return_value=marker), contextlib.redirect_stdout(io.StringIO()):
                    run.return_value.returncode = 0
                    build.run_editor("NEVER-LAUNCHED", ROOT, ROOT, action, *mode)
                    run.assert_called_once()
                    command, = run.call_args.args
                    self.assertEqual("NEVER-LAUNCHED", command[0])
                    self.assertIn("AvatarStroop.Unity.Editor.ViveStroopPreviewBuild." + action, command)
                    self.assertEqual(action != "RenderFixture", "-nographics" in command)
                    for name, requested in zip(ENV_NAMES, mode):
                        self.assertEqual("1" if requested else None, run.call_args.kwargs["env"].get(name))

    def test_cli_rejects_woz_without_both_flags_before_workspace_or_launch(self):
        for controlled, legacy in ((False, False), (True, False), (False, True)):
            args = ["build", "--editor", "unused", "--project", "unused", "--output", "unused", "--legacy-woz-recording-development", "--check-only"]
            if controlled:
                args.append("--controlled-recording-development")
            if legacy:
                args.append("--legacy-raw-recording-development")
            with self.subTest(controlled=controlled, legacy=legacy), patch.object(build.sys, "argv", args), patch.object(build.prepare, "verify_workspace_project") as verify, patch.object(build.subprocess, "run") as run, patch.object(Path, "mkdir") as mkdir, contextlib.redirect_stderr(io.StringIO()) as errors:
                with self.assertRaises(SystemExit) as result:
                    build.main()
                self.assertEqual(2, result.exception.code)
                self.assertIn("requires both --controlled-recording-development and --legacy-raw-recording-development", errors.getvalue())
                verify.assert_not_called()
                run.assert_not_called()
                mkdir.assert_not_called()

    def test_cli_default_and_opt_in_wire_all_outputs_in_memory_only(self):
        for mode in MODES:
            for check_only in (False, True):
                with self.subTest(mode=mode, check_only=check_only), contextlib.ExitStack() as stack:
                    source = ROOT.parent / "synthetic-woz-source-only" / "source"
                    project, output = source.parent / "project", source.parent / "output"
                    args = ["build", "--editor", "NEVER-LAUNCHED", "--project", str(project), "--output", str(output), "--render-fixture"]
                    args += ["--" + name.replace("_", "-") for name, enabled in zip(FLAG_NAMES, mode) if enabled]
                    if check_only:
                        args.append("--check-only")
                    written = []
                    def capture_json(path, value):
                        written.append((path, copy.deepcopy(value)))
                    stack.enter_context(patch.object(build, "ROOT", source))
                    stack.enter_context(patch.object(build.sys, "argv", args))
                    stack.enter_context(patch.object(build.sys, "platform", "win32"))
                    stack.enter_context(patch.object(build.prepare, "verify_workspace_project"))
                    stack.enter_context(patch.object(build.prepare, "check_editor"))
                    stack.enter_context(patch.object(build.prepare, "verify_prepared", return_value={"source_files": {}}))
                    stack.enter_context(patch.object(build.prepare, "reject_link"))
                    stack.enter_context(patch.object(build.prepare, "write_json", side_effect=capture_json))
                    stack.enter_context(patch.object(build.prepare, "read_json", return_value={}))
                    stack.enter_context(patch.object(build.prepare, "digest", return_value="SYNTHETIC-NOT-A-FILE"))
                    editor = stack.enter_context(patch.object(build, "run_editor"))
                    launcher = stack.enter_context(patch.object(build.rehearsal, "compile_launcher", return_value=output / "launcher"))
                    process = stack.enter_context(patch.object(build.subprocess, "run", side_effect=AssertionError("No process allowed")))
                    stack.enter_context(patch.object(Path, "resolve", autospec=True, side_effect=lambda path: path))
                    stack.enter_context(patch.object(Path, "exists", return_value=False))
                    mkdir = stack.enter_context(patch.object(Path, "mkdir"))
                    stack.enter_context(patch.object(Path, "is_file", return_value=True))
                    stack.enter_context(patch.object(Path, "rglob", side_effect=lambda pattern: iter(())))
                    stack.enter_context(patch.object(Path, "read_text", return_value="SYNTHETIC GUIDE"))
                    text_write = stack.enter_context(patch.object(Path, "write_text", autospec=True, return_value=0))
                    stack.enter_context(patch.object(Path, "open", side_effect=AssertionError("No real file access in mocked main")))
                    stack.enter_context(contextlib.redirect_stdout(io.StringIO()))
                    self.assertEqual(0, build.main())
                    process.assert_not_called()
                    if check_only:
                        editor.assert_not_called()
                        launcher.assert_not_called()
                        mkdir.assert_not_called()
                        text_write.assert_not_called()
                        self.assertEqual([], written)
                        continue
                    self.assertEqual(["Configure", "BuildWindowsPreview", "RenderFixture"], [call.args[3] for call in editor.call_args_list])
                    for call in editor.call_args_list:
                        self.assertEqual(mode[:2], call.args[4:])
                        self.assertEqual(mode[2], call.kwargs.get("woz", False))
                    self.assertEqual(3, len(written))
                    intent, initial, final = [value for path, value in written]
                    for name, requested in zip(FLAG_NAMES, mode):
                        self.assertIs(requested, intent[name])
                        self.assertIs(requested, initial[name])
                        self.assertIs(requested, final[name])
                    self.assertIs(mode[2], final["network_recording_available"])
                    self.assertFalse(final["network_default"])
                    self.assertFalse(final["recording_default"])
                    self.assertEqual("built", final["status"])
                    text_write.assert_called_once()
                    self.assertEqual(output / "START-HERE.txt", text_write.call_args.args[0])
                    self.assertEqual(build.start_here_text(mode[0], mode[1], "SYNTHETIC GUIDE" if mode[0] else "", mode[2]), text_write.call_args.args[1])

    def test_editor_request_is_bound_to_environment_and_isolated_intent(self):
        reader = EDITOR.split("static string[] ReadRecordingBuildDefines(string project)", 1)[1].split("public static void BuildWindowsPreview()", 1)[0]
        for name in ENV_NAMES:
            self.assertIn('Environment.GetEnvironmentVariable("' + name + '")', reader)
        for name in FLAG_NAMES:
            self.assertIn("intent." + name, reader)
        self.assertIn("public bool legacy_woz_recording_development=false;", EDITOR)
        self.assertIn("PlayerSettings.GetScriptingDefineSymbols(UnityEditor.Build.NamedBuildTarget.Standalone)", reader)
        self.assertIn("ReadRecordingBuildDefines(Project());", EDITOR.split("public static void Configure()", 1)[1].split("QuestPreviewBuild.Configure();", 1)[0])
        self.assertIn("ReadRecordingBuildDefines(project);", EDITOR.split("public static void RenderFixture()", 1)[1])

    def test_editor_helper_requires_dependencies_exact_intent_and_no_persisted_opt_in(self):
        helper = EDITOR.split("// BEGIN PURE RECORDING BUILD REQUEST", 1)[1].split("// END PURE RECORDING BUILD REQUEST", 1)[0]
        self.assertIn('string woz=null,bool intentWoz=false)', helper)
        for name in ("control", "legacy", "woz"):
            self.assertIn('!(String.IsNullOrEmpty(' + name + ')||' + name + '=="1")', helper)
        self.assertIn("if(wozRequested&&(!controlled||!legacyRequested))", helper)
        self.assertIn("if(legacyRequested&&!controlled)", helper)
        self.assertIn("controlled!=intentControlled||legacyRequested!=intentLegacy||wozRequested!=intentWoz", helper)
        for define in ("STROOP_CONTROLLED_RECORDING_DEVELOPMENT", "STROOP_LEGACY_RAW_RECORDING_DEVELOPMENT", "STROOP_LEGACY_WOZ_RECORDING_DEVELOPMENT"):
            self.assertIn('define.Trim()=="' + define + '"', helper)
        self.assertIn('return wozRequested?new[]{"STROOP_CONTROLLED_RECORDING_DEVELOPMENT","STROOP_LEGACY_RAW_RECORDING_DEVELOPMENT","STROOP_LEGACY_WOZ_RECORDING_DEVELOPMENT"}', helper)
        self.assertIn('legacyRequested?new[]{"STROOP_CONTROLLED_RECORDING_DEVELOPMENT","STROOP_LEGACY_RAW_RECORDING_DEVELOPMENT"}', helper)
        self.assertIn('controlled?new[]{"STROOP_CONTROLLED_RECORDING_DEVELOPMENT"}:new string[0]', helper)
        self.assertNotIn("SetScriptingDefine", EDITOR)

    def test_editor_manifest_matches_request_without_claiming_runtime_success(self):
        for controlled, legacy, woz in MODES:
            with self.subTest(mode=(controlled, legacy, woz)):
                manifest = editor_manifest(controlled, legacy, woz)
                self.assertIs(legacy, manifest["legacy_raw_recording_development"])
                self.assertIs(woz, manifest["legacy_woz_recording_development"])
                self.assertIs(woz, manifest["participant_local_woz_recording_available"])
                self.assertIs(woz, manifest["network_recording_available"])
                self.assertIs(controlled, manifest["optional_local_recording"])
                self.assertIs(controlled, manifest["named_explicit_recording_requested"])
                for name in ("recording_default", "automatic_task_start", "preview_configuration_records", "network_default", "input_configured", "hardware_validated"):
                    self.assertFalse(manifest[name])
                self.assertTrue(manifest["separate_recording_choices_required"])
                self.assertTrue(manifest["unrecorded_legacy_woz_available"])

    def test_editor_only_assertions_and_xr_start_guards_are_preserved(self):
        self.assertIn("!ViveStroopDevelopmentBootstrap.CanEnableRecording(eye,taskChoice,worker,terminal)", EDITOR)
        self.assertIn('scope.Contains("Local VIVE recording is unavailable")', EDITOR)
        self.assertIn('scope.Contains("Legacy A/B/C/D has no recording or export")', EDITOR)
        self.assertIn("!general.InitManagerOnStart", EDITOR)
        self.assertIn("!general.Manager.automaticLoading&&!general.Manager.automaticRunning", EDITOR)
        self.assertIn("target=BuildTarget.StandaloneWindows64,options=controlled?BuildOptions.Development:BuildOptions.None", EDITOR)
        self.assertIn("extraScriptingDefines=recordingDefines", EDITOR)
        self.assertIn('bool woz=recordingDefines.Contains("STROOP_LEGACY_WOZ_RECORDING_DEVELOPMENT");', EDITOR)

    def test_existing_output_and_isolated_project_preservation_guards_remain(self):
        self.assertIn("Choose a new empty output directory; existing builds are preserved.", BUILDER)
        self.assertIn("Classroom intent already exists. Preserve the previous project and use a new isolated project.", BUILDER)
        self.assertIn("prepare.verify_workspace_project(source, project)", BUILDER)
        self.assertIn("for parent in (output,) + tuple(output.parents): prepare.reject_link(parent)", BUILDER)


if __name__ == "__main__":
    unittest.main()
