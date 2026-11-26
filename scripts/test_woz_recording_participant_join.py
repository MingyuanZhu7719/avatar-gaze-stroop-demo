"""Source-only scene wiring checks. Never launches a compiler or native fixture."""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[1]
MAIN = (ROOT / 'Assets/Stroop/Unity/ViveStroopDevelopmentBootstrap.cs').read_text(encoding='utf-8-sig')
SCENE = (ROOT / 'Assets/Stroop/Unity/ViveStroopDevelopmentBootstrap.LegacyWozScene.cs').read_text(encoding='utf-8-sig')
FORMAL = (ROOT / 'Assets/Stroop/Unity/ViveStroopDevelopmentBootstrap.FormalConfirm.cs').read_text(encoding='utf-8-sig')
OWNER_UI = (ROOT / 'Assets/Stroop/Unity/ViveStroopDevelopmentBootstrap.OwnerUi.cs').read_text(encoding='utf-8-sig')
EASY = (ROOT / 'Assets/Stroop/Unity/ViveStroopDevelopmentBootstrap.EasyWozPairing.cs').read_text(encoding='utf-8-sig')

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

    def test_easy_pairing_cannot_bypass_current_raw_and_controller_guards(self):
        poll = part(MAIN, 'public void Poll(bool headReady)', 'void RequestNamedBlockPlanStart')
        self.assertLess(poll.index('recordedWozFrame.DeviceChanged'), poll.index('PollEasyWozPairing()'))
        self.assertLess(poll.index('PollEasyWozPairing()'), poll.index('if(pendingLegacyWozOffer!=null)'))
        self.assertIn('pendingLegacyWozOffer=()=>', EASY)
        self.assertIn('object.ReferenceEquals(easyWozParticipant,participant)', EASY)
        self.assertIn('object.ReferenceEquals(easyWozSession,AvatarChoiceSession)', EASY)
        self.assertIn('avatarChoice.Granted(easyWozSession)==easyWozAvatarShared', EASY)
        self.assertIn('RecordedLegacyWoz==easyWozRecorded', EASY)

    def test_easy_offer_is_private_and_never_created_by_gui_or_network_callback(self):
        gui = EASY[EASY.index('void DrawEasyWozPairingControls()'):]
        for call in ('CreateLegacyWozOffer()', 'PublishOffer(', 'StartLegacyWoz()', 'MarkParticipantStarted('):
            self.assertNotIn(call, gui)
        factory = part(EASY, 'pendingLegacyWozOffer=()=>', 'if(easyWozOfferPublished&&lobby.OperatorStarted')
        self.assertIn('!EasyWozSessionCurrent()||!lobby.ReadyForOffer', factory)
        self.assertLess(factory.index('CreateLegacyWozOffer()'), factory.index('lobby.PublishOffer(legacyWozOffer)'))
        self.assertIn('legacyWozOffer="";', factory)
        self.assertIn('if(manualWozPairing&&!String.IsNullOrEmpty(legacyWozOffer))', SCENE)

    def test_room_requires_local_join_and_accepted_peer_before_offer(self):
        self.assertIn('if(!lobby.PeerConnected||!lobby.PeerConfirmed)return false;', EASY)
        self.assertIn('if(GUILayout.Button("Join room"))BeginEasyWozPairing();', EASY)
        self.assertIn('easyWozLobby=ViveWozPhotonSession.JoinParticipant(', part(EASY, 'void BeginEasyWozPairing()', 'void CloseEasyWozPairing()'))
        self.assertIn('Joining does not change your headset sharing or recording choices.', EASY)
        self.assertIn('The selected connection may use Photon cloud relay.', SCENE)
        self.assertNotIn('PendingPeer', EASY)

    def test_easy_start_waits_for_operator_and_cancel_does_not_reuse_source(self):
        start = part(EASY, 'if(easyWozOfferPublished&&lobby.OperatorStarted', 'void DrawEasyWozPairingControls()')
        self.assertLess(start.index('StartLegacyWoz()'), start.index('lobby.MarkParticipantStarted()'))
        self.assertNotIn('CloseEasyWozPairing();', start)
        self.assertIn('if(sourceCreated){CloseLegacyWozTaskBinding();legacyWozClosed=true;}', EASY)
        self.assertIn('CloseEasyWozPairing();', part(SCENE, 'void CloseLegacyWozScene()', 'void GuardLegacyWozCamera'))
        self.assertIn('bool manualWozPairing,', EASY)
        self.assertNotIn('manualWozPairing=true', EASY)

    def test_cloud_service_is_independent_but_never_creates_task_authority(self):
        service = part(EASY, 'void Update()', 'bool CanWaitForEasyWoz()')
        self.assertIn('room.Service()', service)
        for forbidden in ('CreateLegacyWozOffer', 'StartLegacyWoz', 'CloseLegacyWozTaskBinding', 'MarkParticipantStarted'):
            self.assertNotIn(forbidden, service)
        close = part(SCENE, 'void CloseLegacyWozScene()', 'void GuardLegacyWozCamera')
        self.assertLess(close.index('legacyWozScene.Dispose()'), close.index('finally{CloseEasyWozPairing();}'))
        self.assertIn('ViveLegacyWozParticipantProcess.CreatePhoton(offer,owner,easyWozLobby,age)', SCENE)
        self.assertIn('ViveLegacyWozParticipantProcess.Create(offer,owner,local,remote,age)', SCENE)

    def test_terminal_room_drain_is_bounded_and_cannot_restart_source(self):
        drain = part(EASY, 'void DrainStoppedEasyWozPairing()', 'void CancelEasyWozPairing()')
        self.assertIn('easyWozDrainUntil<0', drain)
        self.assertIn('Time.realtimeSinceStartupAsDouble+1.0', drain)
        for forbidden in ('CreateLegacyWozOffer', 'StartLegacyWoz', 'MarkParticipantStarted'):
            self.assertNotIn(forbidden, drain)
        poll = part(EASY, 'bool PollEasyWozPairing()', 'void DrawEasyWozPairingControls()')
        self.assertLess(poll.index('if(easyWozDrainUntil>=0)return false;'), poll.index('if(lobby.ReadyForOffer'))
        service = part(EASY, 'void Update()', 'bool CanWaitForEasyWoz()')
        self.assertIn('Time.realtimeSinceStartupAsDouble>=easyWozDrainUntil', service)
        self.assertIn('CloseEasyWozPairing();', service)
        self.assertIn('DrainStoppedEasyWozPairing();', part(SCENE, 'bool PollLegacyWozBeforeTask()', 'void RevokeLegacyWoz()'))

    def test_cloud_timing_diagnostics_report_policy_not_render_latency_or_secrets(self):
        timing = part(EASY, 'void CaptureEasyWozTiming(', 'void DrainStoppedEasyWozPairing()')
        for field in ('room.Region', 'room.RoundTripMilliseconds', 'room.TimingProfile', 'room.NetworkVisualBudgetSeconds',
                      'room.ControlProofBudgetSeconds', 'room.LocalReceiptBudgetSeconds', 'room.LocalCaptureBudgetSeconds'):
            self.assertIn(field, timing)
        self.assertIn('Received pose age limit:', timing)
        self.assertIn('RTT is not headset display latency.', timing)
        for forbidden in ('RoomCode', 'appId', 'token', 'legacyWozOffer'):
            self.assertNotIn(forbidden, timing)
        self.assertIn('if(ownerDiagnostics&&!String.IsNullOrEmpty(easyWozTiming))', SCENE)
        self.assertLess(timing.index('if(!force&&now<nextEasyWozTiming)return;'), timing.index('easyWozTiming="Photon region:'))
        self.assertIn('CaptureEasyWozTiming(true);', part(EASY, 'void CloseEasyWozPairing()', 'void CaptureEasyWozTiming('))

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
