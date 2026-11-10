"""Operator recording-disclosure source checks only; no app, compiler or device runs."""
from pathlib import Path
import re
import unittest


ROOT = Path(__file__).resolve().parents[1]
SOURCE = (ROOT / "Assets/Stroop/Unity/ViveWozOperatorDevelopmentBootstrap.cs").read_text(
    encoding="utf-8-sig"
)


def section(start, end):
    return SOURCE[SOURCE.index(start):SOURCE.index(end, SOURCE.index(start))]


class WozRecordingOperatorNoticeTests(unittest.TestCase):
    def test_review_uses_canonical_pairing_mode_without_starting_anything(self):
        review = section("void ReviewOfferMode()", "void OpenFromOwnerForm()")
        self.assertIn("ViveLegacyWozProcessPairing.Parse(secret)", review)
        self.assertIn("participantLocalRecording=pairing.ParticipantLocalRecordingDeclared;offerReviewed=true;", review)
        self.assertIn("catch(ArgumentException){}", review)
        self.assertIn("finally{Array.Clear(secret,0,secret.Length);}", review)
        self.assertNotIn("StartsWith", review)
        for forbidden in ("BindInstalled", "Confirm", ".Start(", "BeginView", "BeginEye"):
            self.assertNotIn(forbidden, review)

    def test_offer_edits_and_source_stop_clear_both_consent_choices(self):
        clear = section("void ClearOfferReview()", "void ReviewOfferMode()")
        for name in ("offerReviewed", "participantLocalRecording", "scope", "networkNotice", "forwarding", "forwardingAndStorage"):
            self.assertIn(name + "=false;", clear)
        review = section("void ReviewOfferMode()", "void OpenFromOwnerForm()")
        self.assertLess(review.index("ClearOfferReview();"), review.index("ViveLegacyWozProcessPairing.Parse(secret)"))
        self.assertIn("if(enteredOffer!=token){token=enteredOffer;ReviewOfferMode();}", SOURCE)
        stop = section("void BeforeSourceStop()", "public void RequestPause()")
        self.assertIn('token="";ClearOfferReview();', stop)

    def test_recorded_disclosure_precedes_explicit_storage_consent(self):
        gui = section("void OnGUI()", "void Close()")
        disclosure = "Participant-local recording declared. Participant PC may save mapped operator head/eye poses plus source, time and status."
        privacy = "Participant raw eyes, answers, timings and ratings never go to the operator."
        consent = 'forwardingAndStorage=GUILayout.Toggle(forwardingAndStorage,"Forward my head/eye control and allow participant-local storage for this session.");'
        self.assertIn(disclosure, gui)
        self.assertIn(privacy, gui)
        self.assertLess(gui.index(disclosure), gui.index(consent))
        self.assertLess(gui.index(privacy), gui.index(consent))
        self.assertRegex(gui, r"if\(participantLocalRecording\)\s+forwardingAndStorage=GUILayout.Toggle")

    def test_unrecorded_notices_and_forwarding_action_remain(self):
        for text in (
            "Legacy WoZ development | Recording off | Hardware/alignment unvalidated",
            "DEVELOPMENT | Recording off | Alignment unvalidated",
            "Authenticated messages, not encrypted. Operator head/eye only; participant answers and raw eyes stay local.",
            'else scope=GUILayout.Toggle(scope,"Run an unrecorded development test.");',
            'networkNotice=GUILayout.Toggle(networkNotice,"Allow plaintext messages on this isolated laboratory LAN.");',
            'else forwarding=GUILayout.Toggle(forwarding,"Forward my head/eye control for this session.");',
        ):
            self.assertIn(text, SOURCE)

    def test_invalid_offer_cannot_enable_connect_or_preserve_consent(self):
        opening = section("void OpenFromOwnerForm()", "void Update()")
        gui = section("void OnGUI()", "void Close()")
        self.assertIn("!offerReviewed||!scope||!networkNotice||", opening)
        self.assertIn("!(participantLocalRecording?forwardingAndStorage:forwarding)", opening)
        self.assertIn("if(offerReviewed)", gui)
        self.assertIn("Paste a valid offer to review its recording mode before consent.", gui)
        self.assertIn("GUI.enabled=offerReviewed&&scope&&networkNotice&&(participantLocalRecording?forwardingAndStorage:forwarding)", gui)

    def test_connect_reparses_and_matches_the_reviewed_mode_before_binding(self):
        opening = section("void OpenFromOwnerForm()", "void Update()")
        parse = opening.index("ViveLegacyWozProcessPairing.Parse(secret)")
        match = opening.index("if(pairing.ParticipantLocalRecordingDeclared!=participantLocalRecording)")
        bind = opening.index("ViveLegacyWozOperatorProcess.BindInstalled")
        self.assertLess(parse, match)
        self.assertLess(match, bind)
        self.assertIn("Review this offer's recording mode before consent", opening[match:bind])

    def test_connect_uses_the_distinct_consent_method_then_starts(self):
        opening = section("void OpenFromOwnerForm()", "void Update()")
        self.assertRegex(opening, r"bool confirmed=process\.ParticipantLocalRecordingDeclared\?\s*process\.ConfirmForwardingAndParticipantLocalStorage\(\):process\.ConfirmLocalOperatorSource\(\);")
        self.assertIn('if(!confirmed||!process.Start())throw new InvalidOperationException("Owned process start failed");', opening)
        self.assertLess(opening.index("bool confirmed="), opening.index("process.Start()"))
        self.assertEqual(1, SOURCE.count("process.ConfirmForwardingAndParticipantLocalStorage()"))
        self.assertEqual(1, SOURCE.count("process.ConfirmLocalOperatorSource()"))
        self.assertEqual(1, SOURCE.count("process.Start()"))

    def test_no_automatic_connect_or_consent_from_lifecycle_or_review(self):
        opening = section("void OpenFromOwnerForm()", "void Update()")
        outside_opening = SOURCE.replace(opening, "")
        for forbidden in (".ConfirmLocalOperatorSource(", ".ConfirmForwardingAndParticipantLocalStorage(", "process.Start(", "ViveLegacyWozOperatorProcess.BindInstalled("):
            self.assertNotIn(forbidden, outside_opening)
        self.assertEqual(2, SOURCE.count("OpenFromOwnerForm()"))
        self.assertIn('if(GUILayout.Button("Import offer and connect"))try{OpenFromOwnerForm();}', SOURCE)
        for name in ("scope", "networkNotice", "forwarding", "forwardingAndStorage"):
            self.assertNotRegex(SOURCE, r"\b" + name + r"\s*=\s*true\b")

    def test_recorded_banners_report_declaration_not_recording_success(self):
        self.assertIn("process!=null?process.ParticipantLocalRecordingDeclared:offerReviewed&&participantLocalRecording", SOURCE)
        self.assertIn('view.SetFaults(new[]{ParticipantStorageDeclared?', SOURCE)
        self.assertIn('GUILayout.Label(ParticipantStorageDeclared?', SOURCE)
        for text in (
            "DEVELOPMENT | Participant-local recording declared | Alignment unvalidated",
            "Legacy WoZ development | Participant-local recording declared | Hardware/alignment unvalidated",
        ):
            self.assertIn(text, SOURCE)
        for claim in ("Recording active", "Recording saved", "Recording complete", "Hardware verified", "Identity verified"):
            self.assertNotIn(claim, SOURCE)

    def test_existing_local_start_and_stop_controls_remain_explicit(self):
        self.assertEqual(1, SOURCE.count("rig.BeginView()"))
        self.assertEqual(1, SOURCE.count("rig.BeginEye()"))
        self.assertIn('if(GUILayout.Button("Start operator XR view"))rig.BeginView();', SOURCE)
        self.assertIn('if(GUILayout.Button("Start installed eye worker"))rig.BeginEye();', SOURCE)
        self.assertIn('if(GUILayout.Button("Stop forwarding and pause participant"))RequestPause();', SOURCE)
        self.assertIn('if(GUILayout.Button("Stop operator source and XR")){BeforeSourceStop();rig.StopView();}', SOURCE)
        self.assertIn("void OnApplicationFocus(bool focused){if(!focused)BeforeSourceStop();}", SOURCE)
        self.assertIn("void OnDisable(){Close();}", SOURCE)

    def test_public_surface_and_local_clock_binding_are_unchanged(self):
        self.assertEqual(
            ["Configured", "NetworkOpen", "PreflightReady", "CanEnterLiveCountdown", "CanRecordOrExport", "Status", "RequestPause"],
            re.findall(r"public\s+(?:bool|string|void)\s+(\w+)", SOURCE),
        )
        self.assertIn("public bool CanEnterLiveCountdown {get{return false;}}", SOURCE)
        self.assertIn("public bool CanRecordOrExport {get{return false;}}", SOURCE)
        self.assertIn("var clock=rig.SharedClock as MonotonicClock;", SOURCE)
        self.assertIn("BindInstalled(pairing,clock,rig.Worker,local,remote,age)", SOURCE)
        self.assertIn("finally{Array.Clear(secret,0,secret.Length);}", section("void OpenFromOwnerForm()", "void Update()"))


if __name__ == "__main__":
    unittest.main()
