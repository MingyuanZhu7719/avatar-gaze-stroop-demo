#!/usr/bin/env python3
"""Two real local processes/UDP sockets with synthetic data. Does not verify two computers or VR."""
from pathlib import Path
import csv
import hashlib
import os
import socket
import shutil
import subprocess
import sys
import time
from test import ROOT, unity_tools

def free_port():
    with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as stream:
        stream.bind(("127.0.0.1", 0))
        return stream.getsockname()[1]

def read_report(path):
    values, trials, faults = {}, {}, set()
    for line in path.read_text().splitlines():
        parts = line.split("\t")
        if parts[0] == "trial": trials[parts[1]] = parts[2]
        elif parts[0] == "fault": faults.add(tuple(parts[1:]))
        else: values[parts[0]] = parts[1]
    return values, trials, faults

def csv_rows(path):
    with path.open(newline="", encoding="utf-8") as stream:
        return list(csv.DictReader(stream))

def verify_export(report, source_digest):
    directory = Path(report["export_directory"])
    session = csv_rows(directory / "session.csv")[0]
    assert session["session_completed"] == "false" and session["build_source_digest"] == source_digest, session
    assert session["phase"] == report["phase"] and session["data_label"] == "SIMULATION", session
    for entry in csv_rows(directory / "manifest.csv"):
        path = directory / entry["file_name"]
        assert hashlib.sha256(path.read_bytes()).hexdigest() == entry["sha256"], entry
        assert len(csv_rows(path)) == int(entry["row_count"]), entry
        assert entry["complete_bundle"] == "true" and entry["session_completed"] == "false", entry
    assert len(csv_rows(directory / "operator_samples.csv")) > 20
    assert not csv_rows(directory / "participant_samples.csv"), "Unavailable participant tracking must stay missing"
    for row in csv_rows(directory / "avatar_presentations.csv"):
        assert row["timestamp_semantics"] == "headless_controller_evaluation_not_rendered", row
        assert row["renderer_observed"] == "", "Headless process cannot claim a camera rendered"
        assert row["actual_source"] != "Automatic", row
    interrupted = [r for r in csv_rows(directory / "trials.csv") if r["outcome"] == "Interrupted"]
    assert all(r["correct"] == r["reaction_seconds"] == "" for r in interrupted)

def run_scenario(launch, build, scenario, source_digest):
    output = build / ("integration-" + scenario + "-" + str(time.time_ns()))
    output.mkdir()
    participant_port, operator_port = free_port(), free_port()
    while operator_port == participant_port: operator_port = free_port()
    session = "synthetic-loopback-" + str(time.time_ns())
    environment = dict(os.environ, STROOP_BUILD_SOURCE_DIGEST=source_digest)
    processes = []
    try:
        for role, local, remote in [("participant", participant_port, operator_port), ("operator", operator_port, participant_port)]:
            processes.append(subprocess.Popen(launch + [role, session, str(local), str(remote), str(output), scenario], env=environment))
        for process in processes:
            if process.wait(timeout=20) != 0: raise RuntimeError("Independent app failed")
    finally:
        for process in processes:
            if process.poll() is None: process.terminate()
    print("Evidence: " + str(output), flush=True)
    try:
        verify_scenario(output, scenario, source_digest)
    except (AssertionError, OSError, ValueError):
        # Preserve exact bounded evidence in hosted CI logs as well as local files.
        for role in ("participant", "operator"):
            diagnostic = output / (role + "-clock-exchanges.tsv")
            if diagnostic.is_file():
                print("FAILED RUN CLOCK EVIDENCE: " + str(diagnostic), file=sys.stderr)
                print(diagnostic.read_text(encoding="utf-8"), file=sys.stderr)
        raise

def verify_scenario(output, scenario, source_digest):
    participant, expected, faults = read_report(output / "participant-report.tsv")
    operator, observed, operator_faults = read_report(output / "operator-report.tsv")
    assert participant["pid"] != operator["pid"], "Apps must be independent processes"
    assert expected == observed, "Operator must see every same current trial and word/ink pair"
    assert int(participant["received_samples"]) > 20 and int(participant["valid_presentations"]) > 0, participant
    assert participant["automatic_fallbacks"] == "0", participant
    assert participant["clock_mapping_available"] == "True" and operator["clock_mapping_available"] == "True", (participant, operator)
    if scenario == "fault-script":
        assert participant["phase"] == "Rest", participant
        assert int(participant["main_trials"]) == 24 and int(participant["ratings"]) == 5, participant
        for fault in ("HeadInvalid", "EyeInvalid", "ConnectionStale", "RecoveryDecisionRequired"):
            assert ("fault_started", fault) in faults, (fault, faults)
    else:
        assert participant["phase"] == "Paused" and participant["applied_remote_stops"] == "1", participant
        assert participant["interrupted_main_trials"] == "1" and int(participant["main_trials"]) < 24, participant
        assert operator["stop_acknowledged"] == "True" and operator["stop_pending"] == "False", operator
    for role, report in (("participant", participant), ("operator", operator)):
        with (output / (role + "-clock-exchanges.tsv")).open(newline="", encoding="utf-8") as stream:
            exchanges = list(csv.DictReader(stream, delimiter="\t"))
        assert 3 <= len(exchanges) <= 256, (role, len(exchanges))
        assert exchanges[-1]["mapping_available"] == report["clock_mapping_available"], (role, exchanges[-1], report)
        for row in exchanges:
            assert float(row["t0_local_sent"]) <= float(row["t3_local_received"]), row
            assert float(row["t1_remote_received"]) <= float(row["t2_remote_sent"]), row
            assert row["local_clock"] != row["remote_clock"], row
    verify_export(participant, source_digest)
    failed_sends, suppressed = int(operator["samples_send_failed"]), int(operator["samples_suppressed"])
    assert 0 <= failed_sends <= 1 and suppressed >= 0, operator
    if failed_sends:
        assert ("connection_fault", "udp_connection_reset:send") in operator_faults, operator_faults
    with (output / "operator-acquisition.tsv").open(newline="", encoding="utf-8") as stream:
        assert len(list(csv.DictReader(stream, delimiter="\t"))) == int(operator["samples_sent"]) + failed_sends + suppressed
    print("PASS " + scenario + ": independent processes, matching tasks, source/clock provenance and hashed partial export")

def main():
    installed = unity_tools()
    build = ROOT / "build"
    build.mkdir(exist_ok=True)
    sources = sorted((ROOT / "Assets/Stroop/Core").glob("*.cs")) + sorted((ROOT / "Assets/Stroop/Runtime").glob("*.cs")) + [ROOT / "Apps/SimulatorHost.cs"]
    source_hash = hashlib.sha256()
    for source in sources:
        source_hash.update(source.relative_to(ROOT).as_posix().encode() + b"\0" + source.read_bytes())
    source_digest = "sha256:" + source_hash.hexdigest()
    if "--dotnet" in sys.argv or installed is None:
        dotnet = shutil.which("dotnet")
        if not dotnet:
            print("An existing .NET 8 SDK or Unity Mono compiler is required; nothing was installed.", file=sys.stderr)
            return 2
        subprocess.run([dotnet, "build", str(ROOT / "AvatarStroop.Simulator.csproj"), "--configuration", "Release"], cwd=str(ROOT), check=True)
        launch = [dotnet, str(build / "dotnet/AvatarStroop.Simulator/Release/net8.0/AvatarStroop.Simulator.dll")]
    else:
        _, mono, csc = installed
        binary = build / "AvatarStroop.Simulator.exe"
        subprocess.run([str(mono), str(csc), "-nologo", "-langversion:7.3", "-warnaserror", "-target:exe", "-main:AvatarStroop.Apps.SimulatorHost", "-out:"+str(binary)] + [str(p) for p in sources], check=True)
        launch = [str(mono), str(binary)]
    run_scenario(launch, build, "fault-script", source_digest)
    run_scenario(launch, build, "safety-stop", source_digest)
    print("SIMULATION ONLY: one host, no headset; partial software sessions, no study completion or actual camera rendering claimed.")
    return 0

if __name__ == "__main__":
    sys.exit(main())
