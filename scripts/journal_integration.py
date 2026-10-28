#!/usr/bin/env python3
"""Independent simulation journal evidence checks; no headset or power-loss claim.

Imported by integration.py after promotion. This module performs no work on import.
Process.kill uses SIGKILL on POSIX and TerminateProcess on Windows. It deliberately
does not run normal application shutdown, and is not a physical power-loss test.
"""
from pathlib import Path
import csv
import hashlib
import json
import os
import socket
import struct
import subprocess
import time
import uuid


def _rows(path):
    with Path(path).open(newline="", encoding="utf-8") as stream:
        return list(csv.DictReader(stream))


def _sha(path):
    digest = hashlib.sha256()
    with Path(path).open("rb") as stream:
        for block in iter(lambda: stream.read(65536), b""):
            digest.update(block)
    return digest.hexdigest()


def _binary_records(data):
    """Independent reference decoder for these bounded integration fixtures."""
    assert data[:8] == b"ASJNL001", "Invalid verified-prefix signature"
    offset, previous, identity, expected = 8, bytes(32), None, 0
    while offset < len(data):
        assert offset + 8 <= len(data), "Incomplete verified-prefix protected length"
        length, complement = struct.unpack_from("<ii", data, offset)
        assert complement == ~length, "Invalid verified-prefix length complement"
        assert 32 <= length <= 1024 * 1024, length
        end = offset + 8 + length + 32
        assert end <= len(data), "Incomplete verified-prefix frame"
        payload, checksum = data[offset + 8:end - 32], data[end - 32:end]
        assert hashlib.sha256(previous + payload).digest() == checksum, "Independent chain check failed"
        sequence = struct.unpack_from("<q", payload)[0]
        token, cursor = payload[8:24], 24
        assert sequence == expected, (sequence, expected)
        if identity is None:
            identity = token
        assert token == identity, "Mixed journal identity in verified prefix"

        def string(nullable=False):
            nonlocal cursor
            assert cursor + 4 <= len(payload)
            size = struct.unpack_from("<i", payload, cursor)[0]
            cursor += 4
            if nullable and size == -1:
                return None
            assert 0 <= size <= len(payload) - cursor, size
            value = payload[cursor:cursor + size].decode("utf-8", errors="strict")
            cursor += size
            return value

        kind = string()
        assert cursor + 4 <= len(payload)
        count = struct.unpack_from("<i", payload, cursor)[0]
        cursor += 4
        assert 0 <= count <= 4096, count
        fields = {}
        for _ in range(count):
            key, value = string(), string(nullable=True)
            assert key and key not in fields, key
            fields[key] = value
        assert cursor == len(payload), "Unparsed verified-prefix bytes"
        if sequence == 0:
            assert kind == "__header"
            assert fields["journal_id"] == uuid.UUID(bytes_le=token).hex
        yield {"sequence": sequence, "kind": kind, "fields": fields, "end_offset": end}
        offset, previous, expected = end, checksum, expected + 1


def _extract(launch, source, parent, session, role, source_digest):
    source, parent = Path(source), Path(parent)
    original_hash, original_length = _sha(source), source.stat().st_size
    completed = subprocess.run(list(launch) + ["journal-extract", str(source), str(parent)],
                               capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=20)
    assert completed.returncode == 0, (completed.returncode, completed.stdout, completed.stderr)
    destinations = [line.split("\t", 1)[1] for line in completed.stdout.splitlines()
                    if line.startswith("extraction_directory\t")]
    assert len(destinations) == 1, completed.stdout
    directory = Path(destinations[0])
    assert directory.is_absolute() and directory.is_dir(), directory
    assert directory.parent.resolve() == parent.resolve(), directory
    assert source.stat().st_size == original_length and _sha(source) == original_hash, "Extraction modified its source"
    expected_files = {"records.csv", "fields.csv", "recovery.csv", "verified-prefix.asj"}
    manifest = _rows(directory / "manifest.csv")
    assert len(manifest) == len(expected_files) and {row["file_name"] for row in manifest} == expected_files, manifest
    assert {path.name for path in directory.iterdir()} == expected_files | {"manifest.csv"}, directory
    for entry in manifest:
        assert entry["schema_version"] == "avatar-stroop-journal-extraction-1" and entry["extraction_finalized"] == "true", entry
        path = directory / entry["file_name"]
        assert _sha(path) == entry["sha256"], entry
        if path.suffix == ".csv":
            assert len(_rows(path)) == int(entry["row_count"]), entry
        else:
            assert entry["row_count"] == "", "A binary file has no CSV row count"
    reports = _rows(directory / "recovery.csv")
    assert len(reports) == 1, reports
    recovery = reports[0]
    assert recovery["schema_version"] == "avatar-stroop-journal-extraction-1", recovery
    assert recovery["session_id"] == session and recovery["role"] == role and recovery["data_label"] == "SIMULATION", recovery
    assert int(recovery["source_bytes"]) == original_length, recovery
    assert recovery["runtime_flush_acknowledgement"] == "unavailable_from_journal_bytes;consult_external_runtime_ack_evidence", recovery
    assert recovery["session_completion_interpretation"] == "only_explicit_session_observation_may_report_completion;close_marker_does_not", recovery
    prefix = (directory / "verified-prefix.asj").read_bytes()
    assert len(prefix) == int(recovery["verified_bytes"]), recovery
    with source.open("rb") as stream:
        assert stream.read(len(prefix)) == prefix, "Extracted bytes differ from original prefix"
    binary = list(_binary_records(prefix))
    records, field_rows = _rows(directory / "records.csv"), _rows(directory / "fields.csv")
    assert len(records) == len(binary) == int(recovery["record_count"]), recovery
    fields = {index: {} for index in range(len(records))}
    for row in field_rows:
        index, key = int(row["record_sequence"]), row["field_name"]
        assert index in fields and key not in fields[index], row
        assert row["is_null"] in ("true", "false"), row
        if row["is_null"] == "true":
            assert row["field_value"] == "", row
        fields[index][key] = None if row["is_null"] == "true" else row["field_value"]
    for index, (record, raw) in enumerate(zip(records, binary)):
        assert int(record["record_sequence"]) == index == raw["sequence"], record
        assert record["record_kind"] == raw["kind"] and int(record["field_count"]) == len(fields[index]), record
        assert fields[index] == raw["fields"], "CSV extraction changed original typed fields"
    assert binary and binary[0]["kind"] == "__header"
    header = binary[0]["fields"]
    assert header["journal_schema"] == "avatar-stroop-journal-1" and header["session_id"] == session, header
    assert header["role"] == role and header["run_kind"] == "Simulation" and header["data_label"] == "SIMULATION", header
    assert header["build_source_digest"] == source_digest and source_digest.startswith("sha256:"), header
    for key in ("clock_id", "protocol_version", "software_version", "exporter_id", "stimulus_timestamp_semantics", "response_timestamp_semantics"):
        assert header[key], (key, header)
    close = [record for record in binary if record["kind"] == "__close"]
    assert len(close) <= 1 and (not close or close[0] is binary[-1]), close
    assert recovery["clean_close_marker_observed"] == ("true" if close else "false"), recovery
    assert all(not record["fields"] for record in close), close
    _verify_observations(binary, role)
    return {"directory": directory, "recovery": recovery, "records": binary,
            "source_sha256": original_hash, "source_bytes": original_length}


def _kind(evidence, kind):
    return [record["fields"] for record in evidence["records"] if record["kind"] == kind]


def _verify_observations(records, role):
    participant_configurations = []
    for record in records:
        kind, fields = record["kind"], record["fields"]
        if kind in ("operator_acquired_sample", "operator_received_sample"):
            assert fields["source"] == "Simulator" and fields["measurement_role"] == "OperatorControl", record
            assert fields["stream_id"].startswith("simulator/"), record
            assert kind == ("operator_acquired_sample" if role == "operator" else "operator_received_sample"), record
        if kind == "avatar_presentation":
            assert fields["timestamp_semantics"] == "headless_controller_evaluation_not_rendered", record
            assert fields["actual_source"] != "Automatic", record
            for name in ("renderer_observed", "renderer_head_held", "renderer_eyes_held", "renderer_visibility_requested"):
                assert fields[name] is None, "Headless process claimed renderer evidence"
            assert all(value is None for name, value in fields.items() if name.startswith("applied_")), record
        if kind == "trial_observation" and fields["outcome"] in ("Pending", "Interrupted"):
            assert all(fields[name] is None for name in ("correct", "reaction_seconds", "response", "response_seconds")), record
        if kind == "session_observation":
            assert fields["session_completed"] == "false" and fields["end_reason"] != "Completed", record
        if kind == "participant_measurement_configuration":
            participant_configurations.append(fields)
        assert kind not in ("participant_sample", "gaze_window", "gaze_interval"), "Unavailable measurement was manufactured"
    if role == "participant":
        assert len(participant_configurations) == 1, participant_configurations
        fields = participant_configurations[0]
        assert fields["available"] == "false" and fields["reason"] == "no_independent_participant_measurement_stream_attached", fields
        assert all(fields[name] is None for name in ("stream_id", "device_id", "reference_clock_id", "source")), fields
    else:
        assert not participant_configurations


def _report_value(report, *names):
    for name in names:
        if name in report:
            return report[name]
    raise AssertionError("Missing runtime report field: " + " or ".join(names))


def verify_recording(launch, output, role, report):
    """Validate a clean role journal against its independent runtime report."""
    output = Path(output)
    session = _report_value(report, "session_id", "journal_session_id")
    digest = _report_value(report, "build_source_digest", "journal_build_source_digest")
    assert report["journal_error"] == "", report
    evidence = _extract(launch, output / (role + "-journal.asj"), output / "journal-extractions", session, role, digest)
    recovery, records = evidence["recovery"], evidence["records"]
    assert recovery["journal_status"] == "CleanClose" and recovery["read_error"] == "", recovery
    assert records[-1]["kind"] == "__close", records[-1]
    assert int(report["journal_flushed_sequence"]) == records[-1]["sequence"], report
    assert int(report["journal_flushed_offset"]) == int(recovery["verified_bytes"]) == evidence["source_bytes"], report
    if role == "operator":
        assert len(_kind(evidence, "operator_acquired_sample")) == int(report["acquired_samples"]), report
    else:
        assert len(_kind(evidence, "operator_received_sample")) == int(report["received_samples"]), report
    print("PASS clean " + role + " journal: runtime flush boundary, independent chain/CSV fields, hashes and source provenance", flush=True)
    return evidence


def _free_port():
    with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as stream:
        stream.bind(("127.0.0.1", 0))
        return stream.getsockname()[1]


def _acknowledgments(path, role):
    text = path.read_text(encoding="utf-8", errors="replace")
    complete = text[:text.rfind("\n") + 1]
    result = []
    for line in complete.splitlines():
        if not line.startswith("JOURNAL_ACK\t"):
            continue
        parts = line.split("\t")
        assert len(parts) == 6 and parts[1] == role, line
        record = {"role": role, "sequence": int(parts[2]), "offset": int(parts[3]),
                  "phase": parts[4], "acquired_count": int(parts[5])}
        assert record["sequence"] >= 0 and record["offset"] > 0 and record["acquired_count"] >= 0, record
        if result:
            assert record["sequence"] > result[-1]["sequence"] and record["offset"] > result[-1]["offset"], result
        result.append(record)
    return result


def _sample_map(evidence, kind):
    result = {}
    for row in _kind(evidence, kind):
        key = tuple(row[name] for name in ("stream_id", "block_index", "segment", "sample_sequence"))
        assert key not in result, (kind, key)
        result[key] = row
    return result


def run_killed_recording(launch, build, source_digest, release_startup):
    """Kill two independent processes after observed durable acknowledgments."""
    output = Path(build) / ("integration-journal-killed-" + str(time.time_ns()))
    output.mkdir()
    barrier = output / "startup"
    barrier.mkdir()
    participant_port, operator_port = _free_port(), _free_port()
    while operator_port == participant_port:
        operator_port = _free_port()
    session = "synthetic-journal-kill-" + str(time.time_ns())
    environment = dict(os.environ, STROOP_BUILD_SOURCE_DIGEST=source_digest)
    processes, handles, observed, kill_elapsed = {}, [], {}, {}
    endpoints = {"participant": (participant_port, operator_port), "operator": (operator_port, participant_port)}
    started = time.monotonic()
    try:
        for role, local, remote in (("participant", participant_port, operator_port), ("operator", operator_port, participant_port)):
            handle = (output / (role + "-stdout.txt")).open("wb")
            handles.append(handle)
            processes[role] = subprocess.Popen(list(launch) + [role, session, str(local), str(remote), str(output), "normal", "--journal", "--startup-barrier", str(barrier), "--journal-kill-fixture"],
                                               env=environment, stdout=handle, stderr=subprocess.STDOUT)
        assert processes["participant"].pid != processes["operator"].pid
        release_startup(output, barrier, processes, endpoints, session, started + 15)
        started = time.monotonic()
        # Bound this engineering check by progress, without assuming a particular
        # runner speed. Both processes must still be alive when we request kill.
        deadline = started + 30
        while time.monotonic() < deadline:
            for role, process in processes.items():
                assert process.poll() is None, (role, "exited before forced termination", (output / (role + "-stdout.txt")).read_text(errors="replace"))
                acks = _acknowledgments(output / (role + "-stdout.txt"), role)
                if acks:
                    observed[role] = acks[-1]
            if (observed.get("participant", {}).get("phase") == "MainTask" and
                    observed.get("operator", {}).get("acquired_count", 0) >= 20):
                break
            time.sleep(0.02)
        else:
            raise AssertionError("No qualifying main-task/source flush acknowledgments before kill deadline; evidence: " + str(output))
        for role, process in processes.items():
            assert process.poll() is None, (role, "exited before kill request")
            kill_elapsed[role] = time.monotonic() - started
            process.kill()
        for role, process in processes.items():
            assert process.wait(timeout=5) != 0, (role, "unexpected normal completion")
    finally:
        for process in processes.values():
            if process.poll() is None:
                process.kill()
            process.wait(timeout=5)
        for handle in handles:
            handle.close()
        evidence_path = output / "observed-acknowledgments.json"
        evidence_path.write_text(json.dumps({"session_id": session, "source_digest": source_digest,
                                            "acknowledgments": observed, "kill_elapsed_seconds": kill_elapsed,
                                            "pids": {role: process.pid for role, process in processes.items()}}, indent=2) + "\n", encoding="utf-8")
        print("Killed-journal evidence: " + str(output), flush=True)
    recovered = {}
    for role in ("participant", "operator"):
        assert not (output / (role + "-report.tsv")).exists(), "Normal shutdown report unexpectedly exists"
        recovered[role] = _extract(launch, output / (role + "-journal.asj"), output / "journal-extractions", session, role, source_digest)
        evidence, ack = recovered[role], observed[role]
        recovery = evidence["recovery"]
        assert recovery["journal_status"] in ("UncleanEnd", "Truncated"), recovery
        assert recovery["clean_close_marker_observed"] == "false", recovery
        assert all(record["kind"] != "__close" for record in evidence["records"])
        assert int(recovery["verified_bytes"]) >= ack["offset"], (role, recovery, ack)
        matches = [record for record in evidence["records"] if record["sequence"] == ack["sequence"]]
        assert len(matches) == 1 and matches[0]["end_offset"] == ack["offset"], (role, matches, ack)
        assert matches[0]["kind"] == "runtime_ack_context", matches
        assert matches[0]["fields"]["phase"] == ack["phase"] and int(matches[0]["fields"]["acquired_samples"]) == ack["acquired_count"], (role, matches, ack)
    acquired = _sample_map(recovered["operator"], "operator_acquired_sample")
    received = _sample_map(recovered["participant"], "operator_received_sample")
    assert len(acquired) >= observed["operator"]["acquired_count"] >= 20
    shared = acquired.keys() & received.keys()
    assert shared, "No source/receiver samples overlap in recovered prefixes"
    comparable = ("source", "measurement_role", "stream_id", "device_id", "clock_id", "block_index", "segment", "sample_sequence",
                  "capture_local_seconds", "send_local_seconds", "head_x", "head_y", "head_z", "head_w", "eye_x", "eye_y", "eye_z",
                  "head_reported_valid", "eye_reported_valid", "head_valid", "eye_valid")
    for key in shared:
        assert all(acquired[key][field] == received[key][field] for field in comparable), (key, acquired[key], received[key])
    # Abrupt termination leaves independently bounded source/receiver prefixes.
    # Neither equal counts nor receiver-subset membership establishes delivery.
    correlation = {"acquired_prefix_samples": len(acquired), "received_prefix_samples": len(received),
                   "matched_sample_identities": len(shared), "source_only_prefix_identities": len(acquired.keys() - received.keys()),
                   "receiver_only_prefix_identities": len(received.keys() - acquired.keys()),
                   "interpretation": "raw identity/value matches over intersecting recovered prefixes; no delivery or loss claim"}
    (output / "source-receiver-prefix-correlation.json").write_text(json.dumps(correlation, indent=2) + "\n", encoding="utf-8")
    assert any(row["phase"] == "MainTask" for row in _kind(recovered["participant"], "session_observation"))
    assert any(row["outcome"] == "Pending" for row in _kind(recovered["participant"], "trial_observation"))
    print("PASS forced process kill: acknowledged bytes recovered, original journals preserved, nullable partial trials, matched source/receiver raw fields", flush=True)
    return output
