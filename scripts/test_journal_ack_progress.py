"""Kill-fixture observer regressions; no simulator, socket or process is launched."""
import contextlib
import io
from pathlib import Path
import tempfile
import unittest

import journal_integration as journal


class JournalAckProgressTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.root = Path(self.directory.name)

    def parse(self, role, rows, trailing=""):
        path = self.root / (role + "-stdout.txt")
        path.write_text("SIMULATION ONLY\n" + "".join(
            "JOURNAL_ACK\t" + "\t".join(str(value) for value in row) + "\n" for row in rows) + trailing,
            encoding="utf-8")
        return journal._acknowledgments(path, role)

    def participant_batch(self):
        return self.parse("participant", [
            ("participant", 1, 100, "Practice", 0),
            ("participant", 2, 200, "MainTask", 0),
            ("participant", 3, 300, "Questionnaire", 0),
            ("participant", 4, 400, "Rest", 0),
        ])

    def test_delayed_observer_retains_main_task_from_a_batch_ending_in_rest(self):
        records = self.participant_batch()
        selected = journal._latch_qualifying_acknowledgment("participant", records)
        self.assertEqual("Rest", records[-1]["phase"])
        self.assertEqual(records[1], selected)
        self.assertEqual((2, 200), (selected["sequence"], selected["offset"]))

    def test_absent_main_task_is_not_replaced_by_later_progress(self):
        records = self.parse("participant", [
            ("participant", 1, 100, "Practice", 0),
            ("participant", 2, 200, "Rest", 100),
        ])
        self.assertIsNone(journal._latch_qualifying_acknowledgment("participant", records))

    def test_operator_below_required_sample_count_does_not_qualify(self):
        records = self.parse("operator", [("operator", 1, 100, "MainTask", 19)])
        self.assertIsNone(journal._latch_qualifying_acknowledgment("operator", records))

    def test_operator_sample_threshold_still_requires_twenty_acknowledged_samples(self):
        records = self.parse("operator", [
            ("operator", 1, 100, "Countdown", 19),
            ("operator", 2, 200, "Countdown", 20),
        ])
        self.assertEqual(records[1], journal._latch_qualifying_acknowledgment("operator", records))

    def test_latched_context_is_not_cleared_by_later_nonqualifying_phase(self):
        records = self.participant_batch()
        selected = journal._latch_qualifying_acknowledgment("participant", records[:2])
        self.assertIs(selected, journal._latch_qualifying_acknowledgment("participant", records[2:], selected))
        self.assertIs(selected, journal._latch_qualifying_acknowledgment("participant", [], selected))

    def test_first_peer_progress_does_not_imply_other_peer_progress(self):
        participant = journal._latch_qualifying_acknowledgment("participant", self.participant_batch())
        operator = journal._latch_qualifying_acknowledgment("operator", self.parse(
            "operator", [("operator", 1, 100, "MainTask", 19)]))
        self.assertIsNotNone(participant)
        self.assertIsNone(operator)

    def test_role_mismatch_is_rejected_by_the_stdout_parser(self):
        with self.assertRaises(AssertionError):
            self.parse("participant", [("operator", 1, 100, "MainTask", 20)])

    def test_duplicate_sequence_is_rejected_even_when_later_phase_qualifies(self):
        with self.assertRaises(AssertionError):
            self.parse("participant", [
                ("participant", 1, 100, "Practice", 0),
                ("participant", 1, 200, "MainTask", 0),
            ])

    def test_decreasing_sequence_is_rejected(self):
        with self.assertRaises(AssertionError):
            self.parse("participant", [
                ("participant", 2, 100, "Practice", 0),
                ("participant", 1, 200, "MainTask", 0),
            ])

    def test_duplicate_or_decreasing_offsets_are_rejected(self):
        for offset in (100, 99):
            with self.subTest(offset=offset), self.assertRaises(AssertionError):
                self.parse("participant", [
                    ("participant", 1, 100, "Practice", 0),
                    ("participant", 2, offset, "MainTask", 0),
                ])

    def test_incomplete_stdout_line_cannot_create_progress(self):
        records = self.parse("participant", [("participant", 1, 100, "Practice", 0)],
                             "JOURNAL_ACK\tparticipant\t2\t200\tMainTask\t0")
        self.assertEqual(1, len(records))
        self.assertIsNone(journal._latch_qualifying_acknowledgment("participant", records))

    def test_malformed_or_negative_ack_values_are_rejected(self):
        for row in (("operator", -1, 100, "MainTask", 20),
                    ("operator", 1, 0, "MainTask", 20),
                    ("operator", 1, 100, "MainTask", -1),
                    ("operator", 1, 100, "MainTask")):
            with self.subTest(row=row), self.assertRaises(AssertionError):
                self.parse("operator", [row])

    def test_history_diagnostics_separate_latest_phase_from_qualifying_evidence(self):
        summary = journal._acknowledgment_history("participant", self.participant_batch())
        self.assertEqual(4, summary["count"])
        self.assertEqual(1, summary["qualifying_count"])
        self.assertEqual("Rest", summary["latest"]["phase"])
        self.assertEqual({"Practice": 1, "MainTask": 1, "Questionnaire": 1, "Rest": 1}, summary["phase_counts"])

    def test_failure_stdout_tail_is_bounded_and_diagnostics_do_not_raise(self):
        for role in ("participant", "operator"):
            (self.root / (role + "-stdout.txt")).write_text("OLD-BEGIN\n" + "x" * 6000 + "\nRECENT-END\n", encoding="utf-8")
        stream = io.StringIO()
        with contextlib.redirect_stdout(stream):
            journal._print_killed_failure_diagnostics(self.root)
        output = stream.getvalue()
        self.assertNotIn("OLD-BEGIN", output)
        self.assertEqual(2, output.count("RECENT-END"))
        self.assertLess(len(output), 9500)


if __name__ == "__main__":
    unittest.main()
