#!/usr/bin/env python3
"""Import/verify the official pinned Photon Realtime SDK; no download or login.

Vendor code stays ignored. Only Realtime sources and the Windows-compatible
PhotonClient runtime are imported, not Chat, sample assets or demo projects.
"""
import argparse
import hashlib
import json
from pathlib import Path
import re
import shutil
import subprocess
import sys
import uuid
import zipfile

ROOT = Path(__file__).resolve().parents[1]
VERSION = "5.1.21"
ARCHIVE_SHA256 = "5c35ac02fa1bba0d9e9a2eba3ac4158bca1582c8b516cbcd1f39799589a0d09a"
OSS_SHA256 = "1b75b3dbb7e84783ecd50dfed3a53adb0725a0688cc25cef8675a0fc8d7e7a28"
MANIFEST = "sdk-manifest.json"
CONFIG = "photon-woz.local.json"
PINNED = Path(__file__).with_name("photon-sdk-manifest.json")

def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()

def library(source):
    return source / "Assets/Photon/PhotonLibs/PhotonClient.dll"

def verify(source):
    root = source / "Assets/Photon"
    marker = root / MANIFEST
    if not marker.is_file():
        raise ValueError("Photon SDK missing. Run scripts/photon-sdk.py with the official pinned archive and OSS notices first.")
    record = json.loads(marker.read_text(encoding="utf-8"))
    expected = json.loads(PINNED.read_text(encoding="utf-8"))
    if record != expected or record.get("version") != VERSION or record.get("archive_sha256") != ARCHIVE_SHA256:
        raise ValueError("Photon SDK manifest version/hash differs from the pinned SDK.")
    actual = {p.relative_to(root).as_posix(): digest(p) for p in sorted(root.rglob("*"))
              if p.is_file() and p.name not in (MANIFEST, MANIFEST + ".meta")}
    # Unity may create metadata for our SDK manifest, but all executable/source
    # inputs and their supplied import metadata must retain the imported hash.
    if actual != record.get("files"):
        raise ValueError("Photon SDK files changed or do not match the pinned import manifest.")
    return record

def import_sdk(source, archive, oss):
    if digest(archive) != ARCHIVE_SHA256 or digest(oss) != OSS_SHA256:
        raise ValueError("Official Photon archive or OSS notices do not match the pinned SHA256.")
    root = source / "Assets/Photon"
    if root.exists():
        return verify(source)
    selected = {}
    with zipfile.ZipFile(archive) as package:
        for item in package.infolist():
            name = item.filename
            if name.startswith("Assets/Photon/PhotonRealtime/") or name in (
                    "Assets/Photon/PhotonRealtime.meta", "Assets/Photon/PhotonLibs.meta"):
                if item.is_dir():
                    continue
                relative = name[len("Assets/Photon/"):]
                if ".." in Path(relative).parts or Path(relative).is_absolute():
                    raise ValueError("Unsafe vendor archive path.")
                selected[relative] = package.read(item)
        selected["PhotonLibs/PhotonClient.dll"] = package.read("Assets/Photon/PhotonLibs/netstandard2.0/release/PhotonClient.dll")
        selected["PhotonLibs/PhotonClient.xml"] = package.read("Assets/Photon/PhotonLibs/netstandard2.0/release/PhotonClient.xml")
        selected["LICENSE.txt"] = package.read("license.txt")
        parent_meta = package.read("Assets/Photon.meta")
    selected["OSS-notices.pdf"] = oss.read_bytes()
    # Selecting only the release assembly avoids the vendor debug/release define
    # switch. This metadata is ours; vendor code and binaries are unchanged.
    selected["PhotonLibs/PhotonClient.dll.meta"] = (
        "fileFormatVersion: 2\nguid: e72033f2c1c44779a22bc476a4f544be\nPluginImporter:\n"
        "  externalObjects: {}\n  serializedVersion: 2\n  iconMap: {}\n  executionOrder: {}\n"
        "  defineConstraints: []\n  isPreloaded: 0\n  isOverridable: 0\n  isExplicitlyReferenced: 0\n"
        "  validateReferences: 1\n  platformData:\n  - first:\n      Any:\n    second:\n"
        "      enabled: 1\n      settings: {}\n  userData:\n  assetBundleName:\n  assetBundleVariant:\n").encode()
    for name in ("PhotonLibs/PhotonClient.xml", "LICENSE.txt", "OSS-notices.pdf"):
        selected[name + ".meta"] = ("fileFormatVersion: 2\nguid: " + uuid.uuid5(uuid.NAMESPACE_URL, "avatar-stroop-photon/" + name).hex + "\n").encode()
    root.mkdir(parents=True)
    (source / "Assets/Photon.meta").write_bytes(parent_meta)
    for name, contents in selected.items():
        target = root / name
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_bytes(contents)
    record = {"version": VERSION, "archive_sha256": ARCHIVE_SHA256,
              "source_url": "https://downloads.photonengine.com/download/photon-unity-sdk_v5-1-21.zip?pre=sp",
              "files": {name: hashlib.sha256(contents).hexdigest() for name, contents in sorted(selected.items())}}
    (root / MANIFEST).write_text(json.dumps(record, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    return verify(source)

def validate_config(path):
    def unique_pairs(pairs):
        result = {}
        for key, value in pairs:
            if key in result: raise ValueError("Photon config contains duplicate keys.")
            result[key] = value
        return result
    if path.stat().st_size > 4096: raise ValueError("Photon config is too large.")
    value = json.loads(path.read_text(encoding="utf-8-sig"), object_pairs_hook=unique_pairs)
    if not isinstance(value, dict) or set(value) != {"appId", "region"}:
        raise ValueError("Photon config must contain exactly appId and region.")
    if not isinstance(value.get("appId"), str) or not re.fullmatch(r"[0-9a-fA-F]{8}(?:-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}", value["appId"]) or uuid.UUID(value["appId"]).int == 0:
        raise ValueError("Photon config requires a Realtime App ID GUID.")
    if not isinstance(value.get("region"), str) or not re.fullmatch(r"[a-z]{2,8}", value["region"]):
        raise ValueError("Photon config requires a fixed lowercase region code.")
    return value

def package_files(source, destination, config=None):
    record = verify(source)
    notices = destination / "Photon-notices"
    notices.mkdir(exist_ok=False)
    for name in ("LICENSE.txt", "OSS-notices.pdf"):
        shutil.copy2(source / "Assets/Photon" / name, notices / name)
    configured = config is not None
    if configured:
        value = validate_config(config)
        target = destination / CONFIG
        if target.exists():
            raise ValueError("Refusing to overwrite an existing Photon configuration.")
        target.write_text(json.dumps(value, indent=2) + "\n", encoding="utf-8")
    return {"version": VERSION, "archive_sha256": record["archive_sha256"],
            "runtime_sha256": digest(library(source)), "config_included": configured,
            "transport": "WebSocketSecure", "real_network_tested": False}

def room_guide():
    return ("PHOTON ROOM CONNECTION\n"
            "Both computers need internet and the complete matching configured packages. No IP, port or clock ID is required.\n"
            "1. On the operator PC, start headset view and eye tracking, then click Create room.\n"
            "2. On the participant PC, choose WoZ, complete tutorial/practice and enter the room code at block preparation. Click Join room.\n"
            "3. Check the intended participant PC shows the same room code. The operator clicks Accept this participant. Clock IDs and the temporary offer are exchanged automatically.\n"
            "4. Review and confirm the three permissions. For a recorded offer, explicitly accept participant-local storage. Click Connect to participant.\n"
            "5. Participant connection completes automatically. Begin current block remains a separate participant action when tracking is ready.\n"
            "Photon relays room setup and allowed live head/gaze/task-state messages over encrypted internet connections (WSS). Photon is a third-party cloud relay, not an end-to-end encrypted local link.\n"
            "Photon uses a fixed 750 ms original-task/control-proof validity window, allowing two 300 ms request/response cycles plus 150 ms margin while a single renewal is outstanding. Advanced manual LAN keeps its existing limits. This is a control validity limit, not measured end-to-end gaze latency. New samples must still be sent within 100 ms of the local source observation; received pose residence remains limited to 150 ms. Two-PC headset validation is still required.\n"
            "No participant answers, RT, ratings, raw eyes or files are sent to the operator. Raw recordings and CSV files stay on the participant computer.\n"
            "Connection never enables recording or replaces either person's forwarding/sharing choices. A disconnect stops forwarding and requires a fresh room/session; no automatic resume.\n"
            "Advanced: manual LAN connection retains the older offer/IP workflow as a separate fallback. That legacy transport is authenticated, not encrypted. Never mix the two connection methods in one session.\n")

def compile_references(source, output, mono, csc, framework=()):
    """Compile the vendor layer for the actual host references, copy runtime DLL.

    No client is created or connected. Application warning policy stays strict.
    """
    verify(source)
    output.mkdir(parents=True, exist_ok=True)
    if framework:
        windows = Path(framework[0]).parents[2]
        candidates = list((windows / "assembly/GAC_MSIL/netstandard").glob("*/netstandard.dll"))
        if not candidates:
            raise ValueError("Installed Windows CLR netstandard facade is required for Photon compatibility tests.")
        facade = candidates[0]
        options = ["-noconfig", "-nostdlib+"] + ["-r:" + str(p) for p in framework]
    else:
        base = Path(csc).parents[4]
        facade = base / "MonoBleedingEdge/lib/mono/4.7.2-api/Facades/netstandard.dll"
        options = []
    if not facade.is_file():
        raise ValueError("Installed netstandard facade is missing.")
    client = output / "PhotonClient.dll"
    realtime = output / "Photon.Realtime.dll"
    shutil.copy2(library(source), client)
    files = sorted((source / "Assets/Photon/PhotonRealtime/Code").glob("*.cs"))
    command = [str(mono), str(csc), "-nologo", "-langversion:7.3", "-target:library", "-out:" + str(realtime),
               "-r:" + str(facade), "-r:" + str(client)] + options + [str(p.relative_to(source)) for p in files]
    subprocess.run(command, cwd=source, check=True)
    return ["-r:" + str(p) for p in (facade, realtime, client)]

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--archive", type=Path)
    parser.add_argument("--oss-notices", type=Path)
    parser.add_argument("--source", type=Path, default=ROOT)
    parser.add_argument("--verify", action="store_true")
    args = parser.parse_args()
    try:
        if args.verify:
            verify(args.source)
        elif args.archive and args.oss_notices:
            import_sdk(args.source, args.archive, args.oss_notices)
        else:
            raise ValueError("Provide --archive and --oss-notices, or use --verify.")
        print("Photon Realtime " + VERSION + " imported and verified; no connection started.")
    except (OSError, ValueError, KeyError, zipfile.BadZipFile) as error:
        parser.exit(2, str(error) + "\n")

if __name__ == "__main__":
    main()
