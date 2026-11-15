"""Source-only scene wiring checks. Never launches a compiler or native fixture."""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[1]
MAIN = (ROOT / 'Assets/Stroop/Unity/ViveStroopDevelopmentBootstrap.cs').read_text(encoding='utf-8-sig')
SCENE = (ROOT / 'Assets/Stroop/Unity/ViveStroopDevelopmentBootstrap.LegacyWozScene.cs').read_text(encoding='utf-8-sig')
FORMAL = (ROOT / 'Assets/Stroop/Unity/ViveStroopDevelopmentBootstrap.FormalConfirm.cs').read_text(encoding='utf-8-sig')
OWNER_UI = (ROOT / 'Assets/Stroop/Unity/ViveStroopDevelopmentBootstrap.OwnerUi.cs').read_text(encoding='utf-8-sig')

def part(text, start, end):
    return text[text.index(start):text.index(end, text.index(start))]

class ParticipantJoin(unittest.TestCase):
    def test_mode_defaults_off_and_uses_separate_design(self):
        self.assertIn('bool legacyWozRecordingSelected;', MAIN)
        self.assertIn('bool operatorWoz=false', MAIN)
        self.assertIn('if(operatorWoz&&!ViveLegacyWozRecordingBuildPolicy.Available)', MAIN)
        self.assertIn('ViveLegacyRecordingDesign.SelectOperatorWoz', MAIN)
        self.assertIn('LiveWoZ local recording: files also store mapped operator head/eye poses', MAIN)

    def test_exact_installed_admission_and_worker_retained(self):
        self.assertIn('legacyWozInstalledAdmission=admission;legacyWozInstalledWorker=installed;', MAIN)
        self.assertIn('legacyWozInstalledAdmission.UsesExactSources(participant,legacyWozInstalledWorker,sharedClock)', SCENE)
        self.assertIn('legacyWozInstalledWorker.UsesInstalledSdkPath', SCENE)
        self.assertIn('ViveLegacyWozRecordingBinding.CreateInstalled(owner,legacyWozInstalledAdmission,legacyWozInstalledWorker,namedRawPump)', SCENE)

    def test_offer_created_on_staged_poll_not_raw_ongui(self):
        self.assertIn('pendingLegacyWozOffer=()=>', SCENE)
        poll = part(MAIN, 'public void Poll(bool headReady)', 'void RequestNamedBlockPlanStart')
        self.assertLess(poll.index('if(pendingLegacyWozOffer!=null)'), poll.index('PollLegacyWozBeforeTask()'))
        self.assertIn('pendingLegacyWozOffer=null;create()', poll)

    def test_raw_plan_path_never_uses_unrecorded_countdown(self):
        self.assertIn('if(RecordedLegacyWoz)RequestNamedBlockPlanStart(false);else legacyWozScene.BeginCountdown()', SCENE)
        # The formal button moved into its own guarded helper. Recorded WoZ
        # must still use the separate source workflow, never this local plan.
        self.assertIn('if(configured)DrawPrimaryTaskControls();', MAIN)
        primary = part(OWNER_UI, 'void DrawPrimaryTaskControls()', 'public void DrawOwnerSafetyControls()')
        self.assertIn('DrawFormalConfirmationControls();', primary)
        controls = FORMAL[FORMAL.index('void DrawFormalConfirmationControls()'):]
        self.assertIn('participant.LegacyWozRecordingDeclared||ownerPreviewWoz)return;', controls)
        reason = part(FORMAL, 'string FormalConfirmationUnavailableReason()', 'internal void RequestFormalConfirmation()')
        self.assertIn('if(participant.LegacyWozRecordingDeclared||ownerPreviewWoz)return', reason)
        self.assertIn('string blocked=FormalConfirmationUnavailableReason();', FORMAL)

    def test_exact_scene_apply_precedes_raw_reveal(self):
        body = part(MAIN, 'internal bool PrepareRawScenePresentation()', 'internal bool RequestNamedRawCompletionIfEnded()')
        self.assertLess(body.index('TryPreparePresentation('), body.index('legacyWozScene.Present();'))
        self.assertLess(body.index('legacyWozScene.Present();'), body.index('view.TryRevealViveRawTrial'))
        self.assertIn('pendingRawPresentation.WozEvidence.Applied', body)

    def test_only_existing_raw_callback_commits_and_notes_onset(self):
        body = part(MAIN, 'void OnViveRawStimulusRendered', 'public void BeforeXrStop')
        self.assertLess(body.index('namedRawPump.CommitPresentation('), body.index('legacyWozScene.NotePresented'))
        self.assertNotIn('participant.MarkPresented(', body)

    def test_every_owned_clear_invalidates_pending_application(self):
        body = part(MAIN, 'void AbortRawScenePresentation()', 'internal bool NamedRawTaskCompleted')
        self.assertIn('InvalidateAppliedPresentation()', body)
        self.assertIn('task.LegacyWozRecording.InvalidateAppliedPresentation()', SCENE)

    def test_safety_stops_raw_before_negative_only_process_poll(self):
        body = part(MAIN, 'internal bool ServiceNamedRawSafety()', 'internal bool VerifyNamedRawEpoch()')
        self.assertLess(body.index('pump.Stop()'), body.index('legacyWozProcess.Tick()'))
        self.assertNotIn('TickMaintainedPreflight', body)

    def test_completed_uses_owned_drain_not_second_recorder(self):
        self.assertIn('participant.LegacyWozRecording.RequestCompletedDrain():pump.RequestCompletedDrain()', MAIN)
        self.assertIn('if(RecordedLegacyWoz&&participant.Completed)', SCENE)

    def test_phase_render_and_camera_keep_proof_checks(self):
        self.assertIn('(legacyWozScene!=null&&!FocusHeldLiveWoz&&!legacyWozScene.MayShowTask)', MAIN)
        trial = part(MAIN, 'internal bool PrepareRawScenePresentation()', 'internal bool RequestNamedRawCompletionIfEnded()')
        self.assertIn('(legacyWozScene==null||!legacyWozScene.MayShowTask)', trial)
        self.assertIn('Camera.onPreCull+=GuardLegacyWozCamera;Camera.onPostRender+=GuardLegacyWozCamera;', SCENE)
        self.assertIn('legacyWozScene.RecheckPresentation()', SCENE)

    def test_desktop_focus_alone_does_not_revoke_recording_or_pairing(self):
        body = part(MAIN, 'void OnApplicationFocus(bool focused)', 'void OnDisable()')
        self.assertNotIn('AbortRawScenePresentation(', body)
        self.assertNotIn('CloseLegacyWozScene(', body)
        self.assertNotIn('recordingSetup.Cancel(', body)
        self.assertNotIn('focus loss stops recording.', SCENE)

    def test_waiting_checks_controller_loss_before_network_or_owner_action(self):
        poll = part(MAIN, 'public void Poll(bool headReady)', 'void RequestNamedBlockPlanStart')
        self.assertLess(poll.index('recordedWozFrame=device.Read'), poll.index('if(pendingLegacyWozOffer!=null)'))
        self.assertLess(poll.index('recordedWozFrame.DeviceChanged'), poll.index('PollLegacyWozBeforeTask()'))
        self.assertIn('var frame=recordedWozFrame??device.Read', poll)

    def test_focus_suspended_woz_cannot_consume_pending_plan_after_restoration(self):
        poll = part(MAIN, 'internal void PollWithFocus(bool headReady,bool xrFocused)', 'void RequestNamedBlockPlanStart')
        held = poll.index('if(HoldSuspendedLiveWozAfterCurrentGuards())return;')
        dispatch = poll.index('ProcessNamedBlockPlanStart(headReady,recordedWozFrame)')
        self.assertLess(poll.index('recordedWozFrame.DeviceChanged'), held)
        self.assertLess(held, dispatch)

if __name__ == '__main__':
    unittest.main()
