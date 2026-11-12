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
        gui = section("void DrawPairingForm()", "void OnGUI()")
        disclosure = "The participant computer may save mapped operator head/eye poses, source, time and status."
        privacy = "Participant answers, response times, ratings and raw eye data stay on the participant computer."
        consent = 'forwardingAndStorage=GUILayout.Toggle(forwardingAndStorage,"Forward my head/eye control and allow participant-local storage for this session.",toggleStyle);'
        self.assertIn(disclosure, gui)
        self.assertIn(privacy, gui)
        self.assertLess(gui.index(disclosure), gui.index(consent))
        self.assertLess(gui.index(privacy), gui.index(consent))
        self.assertRegex(gui, r"if\(participantLocalRecording\)\s+forwardingAndStorage=GUILayout.Toggle")

    def test_unrecorded_notices_and_forwarding_action_remain(self):
        for text in (
            "DEVELOPMENT | Recording off | Alignment unvalidated",
            'participantLocalRecording?"Participant-local storage permitted":"Recording off"',
            "Hardware and alignment require validation. Authentication does not encrypt LAN messages.",
            'scope=GUILayout.Toggle(scope,participantLocalRecording?"Run a participant-local recording development test.":"Run an unrecorded development test.",toggleStyle);',
            'networkNotice=GUILayout.Toggle(networkNotice,"Allow authenticated, unencrypted messages on this isolated laboratory LAN.",toggleStyle);',
            'else forwarding=GUILayout.Toggle(forwarding,"Forward my head/eye control for this session.",toggleStyle);',
        ):
            self.assertIn(text, SOURCE)

    def test_invalid_offer_cannot_enable_connect_or_preserve_consent(self):
        opening = section("void OpenFromOwnerForm()", "void Update()")
        gui = section("void DrawPairingForm()", "void OnGUI()")
        self.assertIn("!offerReviewed||!scope||!networkNotice||", opening)
        self.assertIn("!(participantLocalRecording?forwardingAndStorage:forwarding)", opening)
        self.assertIn('if(!offerReviewed){GUILayout.Label("A valid offer is required before permissions can be confirmed.",mutedStyle);return;}', gui)
        self.assertIn("bool ready=offerReviewed&&scope&&networkNotice&&(participantLocalRecording?forwardingAndStorage:forwarding)&&", gui)
        self.assertIn("rig!=null&&rig.HeadReady&&rig.Worker!=null&&valid;", gui)
        self.assertIn('if(OwnerButton("Connect to participant",ready))', gui)
        button = section("bool OwnerButton(", "string NextOwnerAction()")
        self.assertIn("GUI.enabled=old&&enabled;", button)
        self.assertIn("GUILayout.Button(label,buttonStyle", button)
        self.assertLess(gui.index("if(!offerReviewed)"), gui.index("forwardingAndStorage=GUILayout.Toggle"))

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
        gui = section("void DrawPairingForm()", "void OnGUI()")
        self.assertRegex(gui, r'if\(OwnerButton\("Connect to participant",ready\)\)\s*try\{OpenFromOwnerForm\(\);\}')
        self.assertIn("if(process==null&&!closed)DrawPairingForm();", section("void OnGUI()", "void Close()"))
        for name in ("scope", "networkNotice", "forwarding", "forwardingAndStorage"):
            self.assertNotRegex(SOURCE, r"\b" + name + r"\s*=\s*true\b")

    def test_recorded_banners_report_declaration_not_recording_success(self):
        self.assertIn("process!=null?process.ParticipantLocalRecordingDeclared:offerReviewed&&participantLocalRecording", SOURCE)
        self.assertIn('view.SetFaults(new[]{ParticipantStorageDeclared?', SOURCE)
        gui = section("void OnGUI()", "void Close()")
        self.assertIn('NetworkOpen?"CONNECTED  |  "+(ParticipantStorageDeclared?"Participant-local storage permitted":"Recording off"):"NOT CONNECTED"', gui)
        self.assertIn("Hardware and alignment require validation.", gui)
        for text in (
            "DEVELOPMENT | Participant-local recording declared | Alignment unvalidated",
            "The participant computer may save mapped operator head/eye poses, source, time and status.",
        ):
            self.assertIn(text, SOURCE)
        for claim in ("Recording active", "Recording saved", "Recording complete", "Saving on participant computer", "Hardware verified", "Identity verified"):
            self.assertNotIn(claim, SOURCE)

    def test_existing_local_start_and_stop_controls_remain_explicit(self):
        self.assertEqual(1, SOURCE.count("rig.BeginView()"))
        self.assertEqual(1, SOURCE.count("rig.BeginEye()"))
        self.assertIn('if(OwnerButton("Start headset view",rig!=null&&!rig.ViewActive&&!closed&&process==null))rig.BeginView();', SOURCE)
        self.assertIn('if(OwnerButton("Start eye tracking",rig!=null&&rig.HeadReady&&rig.Worker==null&&!closed&&process==null))rig.BeginEye();', SOURCE)
        self.assertIn('if(OwnerButton("Stop forwarding and pause participant",process!=null&&!process.Terminal&&!closed))RequestPause();', SOURCE)
        self.assertIn('if(OwnerButton("Stop headset and eye source",rig!=null&&rig.ViewActive)){BeforeSourceStop();rig.StopView();}', SOURCE)
        gui = section("void OnGUI()", "void Close()")
        self.assertLess(gui.index("GUILayout.EndScrollView()"), gui.index('OwnerButton("Stop forwarding and pause participant"'))
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
