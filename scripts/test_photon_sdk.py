"""Dependency/config integrity checks; never creates a network client."""
import hashlib
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

def load(name, filename):
    spec = importlib.util.spec_from_file_location(name, Path(__file__).with_name(filename))
    module = importlib.util.module_from_spec(spec); spec.loader.exec_module(module); return module

sdk = load("test_photon_sdk_module", "photon-sdk.py")
prepare = load("test_photon_prepare", "prepare-quest-preview.py")

class PhotonSdkTests(unittest.TestCase):
    def test_config_rejects_duplicate_unknown_empty_and_mismatched_values(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / sdk.CONFIG
            valid = {"appId": "12345678-1234-1234-1234-123456789abc", "region": "asia"}
            path.write_text(json.dumps(valid)); self.assertEqual(valid, sdk.validate_config(path))
            for invalid in (dict(valid, region="ASIA"), dict(valid, appId="00000000-0000-0000-0000-000000000000"),
                            dict(valid, server="other"), dict(valid, appVersion="other"),
                            dict(valid, appVersion="avatar-stroop-woz-v1"), dict(valid, appVersion="avatar-woz-photon-1")):
                path.write_text(json.dumps(invalid))
                with self.assertRaises(ValueError): sdk.validate_config(path)
            path.write_text(json.dumps(valid)[:-1] + ',"region":"eu"}')
            with self.assertRaises(ValueError): sdk.validate_config(path)

    def test_manifest_cannot_be_rewritten_to_bless_modified_vendor_bytes(self):
        with tempfile.TemporaryDirectory() as directory:
            source = Path(directory); root = source / "Assets/Photon"; root.mkdir(parents=True)
            dll = root / "sample.dll"; dll.write_bytes(b"vendor")
            pinned = source / "pinned.json"
            record = {"version": sdk.VERSION, "archive_sha256": sdk.ARCHIVE_SHA256,
                      "files": {"sample.dll": hashlib.sha256(b"vendor").hexdigest()}}
            pinned.write_text(json.dumps(record)); (root / sdk.MANIFEST).write_text(json.dumps(record))
            with patch.object(sdk, "PINNED", pinned):
                self.assertEqual(record, sdk.verify(source))
                dll.write_bytes(b"changed")
                with self.assertRaises(ValueError): sdk.verify(source)
                record["files"]["sample.dll"] = hashlib.sha256(b"changed").hexdigest()
                (root / sdk.MANIFEST).write_text(json.dumps(record))
                with self.assertRaises(ValueError): sdk.verify(source)

    def test_missing_sdk_explains_import_before_any_compiler_action(self):
        with tempfile.TemporaryDirectory() as directory:
            with self.assertRaisesRegex(ValueError, "scripts/photon-sdk.py"):
                sdk.verify(Path(directory))

    def test_source_snapshot_includes_binary_and_pinned_manifest(self):
        with tempfile.TemporaryDirectory() as directory:
            source=Path(directory); root=source / "Assets/Photon"; root.mkdir(parents=True)
            scripts=source / "scripts"; scripts.mkdir()
            (root / "PhotonClient.dll").write_bytes(b"vendor")
            (root / "sdk-manifest.json.meta").write_text("generated")
            (scripts / "photon-sdk-manifest.json").write_text("pinned")
            inventory=prepare.source_inventory(source)
            self.assertIn("Assets/Photon/PhotonClient.dll",inventory)
            self.assertIn("scripts/photon-sdk-manifest.json",inventory)
            self.assertNotIn("Assets/Photon/sdk-manifest.json.meta",inventory)

if __name__ == "__main__": unittest.main()
