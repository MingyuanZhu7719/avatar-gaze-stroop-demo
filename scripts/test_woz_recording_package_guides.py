"""Package-guide-only regression. Imports do not launch tools or touch data."""
import importlib.util
from pathlib import Path
import unittest

ROOT=Path(__file__).resolve().parents[1]
def load(name, filename):
    spec=importlib.util.spec_from_file_location(name,ROOT/'scripts'/filename)
    module=importlib.util.module_from_spec(spec);spec.loader.exec_module(module);return module
participant=load('recorded_participant_guide','build-vive-stroop-preview.py')
operator=load('recorded_operator_guide','build-vive-woz-operator-preview.py')

class PackageGuides(unittest.TestCase):
    def test_ordinary_participant_stays_unrecorded(self):
        text=participant.start_here_text()
        self.assertIn('ordinary preview only; recording unavailable',text)
        self.assertIn('WoZ recording/export is unavailable',text)
        self.assertFalse(participant.recording_manifest()['network_recording_available'])

    def test_recorded_short_check_does_not_claim_completed_csv(self):
        text=participant.start_here_text(True,True,'SYNTHETIC GUIDE',True)
        for value in ('one or two main trials','raw prefix','not a completed session or completed-session CSV',
                      'Do not run four blocks just for this check','later full, normally completed session'):
            self.assertIn(value,text)

    def test_operator_discloses_mode_before_storage_acceptance(self):
        text=operator.start_here_text()
        self.assertIn('This operator does not record files',text)
        offer='Paste the private temporary offer into Participant pairing offer'
        review='Review and confirm the three permissions'
        storage='For a recorded offer, explicitly accept participant-local storage'
        connect='Click Connect to participant first'
        for instruction in (offer,review,storage,connect):
            self.assertIn(instruction,text)
        self.assertLess(text.index(offer),text.index(review))
        self.assertLess(text.index(review),text.index(storage))
        self.assertLess(text.index(storage),text.index(connect))
        self.assertNotIn('Network WoZ recording/export is unavailable',text)
        self.assertIn('No participant answers, RT, ratings, raw eyes or files are sent',text)

    def test_operator_short_check_and_local_runtime_are_explicit(self):
        text=operator.start_here_text()
        self.assertIn('one- or two-trial stop leaves a raw prefix',text)
        self.assertIn('do not run four blocks',text)
        self.assertIn("$start.EnvironmentVariables['XR_RUNTIME_JSON'] = $runtime",text)
        self.assertNotIn('SetEnvironmentVariable',text)
        self.assertNotIn('Set-ItemProperty',text)
        self.assertIn('No data submission is needed',text)

    def test_operator_metadata_has_role_specific_capabilities(self):
        text=(ROOT/'scripts/build-vive-woz-operator-preview.py').read_text(encoding='utf-8-sig')
        self.assertIn('"operator_recording_available": False',text)
        self.assertIn('"participant_recorded_mode_forwarding_available": True',text)
        self.assertNotIn('"network_recording_available": False',text)

if __name__=='__main__': unittest.main()
