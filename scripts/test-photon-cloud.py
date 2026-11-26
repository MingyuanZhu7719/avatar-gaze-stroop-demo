#!/usr/bin/env python3
"""Opt-in synthetic Photon room/protocol check in an isolated Unity play-mode project.

No headset, participant data, raw recordings, or installed eye provider is used.
Internet and a private local Photon config are required. Production timing
checks remain active: a reachable cloud room alone is not a pass.
"""
import argparse
import importlib.util
import os
from pathlib import Path
import shutil
import subprocess

ROOT = Path(__file__).resolve().parents[1]

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--editor', type=Path, required=True)
    parser.add_argument('--project', type=Path, required=True)
    parser.add_argument('--photon-config', type=Path, required=True)
    args = parser.parse_args()
    spec = importlib.util.spec_from_file_location('photon_sdk', ROOT / 'scripts/photon-sdk.py')
    sdk = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(sdk)
    sdk.verify(ROOT)
    sdk.validate_config(args.photon_config)
    prep_spec = importlib.util.spec_from_file_location('preview_prepare', ROOT / 'scripts/prepare-quest-preview.py')
    prepare = importlib.util.module_from_spec(prep_spec)
    prep_spec.loader.exec_module(prepare)
    prepare.check_editor(args.editor.resolve())
    project = args.project.resolve()
    if project.exists():
        parser.error('Use a new isolated project directory; existing work is never overwritten.')
    assets = project / 'Assets'
    (assets / 'Stroop').mkdir(parents=True)
    for folder in ('Core', 'Runtime'):
        shutil.copytree(ROOT / 'Assets/Stroop' / folder, assets / 'Stroop' / folder)
    shutil.copytree(ROOT / 'Assets/Photon', assets / 'Photon')
    shutil.copy2(ROOT / 'Tests/ViveWozPhotonCloudProbe.cs', assets / 'ViveWozPhotonCloudProbe.cs')
    (assets / 'csc.rsp').write_text('-define:STROOP_TEST_BUILD\n', encoding='utf-8')
    (project / 'Packages').mkdir()
    (project / 'Packages/manifest.json').write_text('{"dependencies":{}}\n', encoding='utf-8')
    (project / 'ProjectSettings').mkdir()
    (project / 'ProjectSettings/ProjectVersion.txt').write_text('m_EditorVersion: ' + prepare.EDITOR_VERSION + '\n', encoding='utf-8')
    env = dict(os.environ, STROOP_PHOTON_CONFIG=str(args.photon_config.resolve()))
    log = project / 'photon-cloud.log'
    command = [str(args.editor.resolve()), '-batchmode', '-nographics', '-projectPath', str(project),
               '-executeMethod', 'AvatarStroop.Tests.ViveWozPhotonCloudProbe.Run', '-logFile', str(log)]
    result = subprocess.run(command, env=env, timeout=240)
    text = log.read_text(encoding='utf-8', errors='replace') if log.exists() else ''
    for line in text.splitlines():
        if line.startswith(('PASS photon_cloud', 'FAIL photon_cloud', 'photon_')):
            print(line)
    print('Full local log:', log)
    passed = ('PASS photon_cloud handshake=' in text and 'PASS photon_cloud wrong_room=1' in text
              and 'FAIL photon_cloud' not in text and result.returncode == 0)
    return 0 if passed else 1

if __name__ == '__main__':
    raise SystemExit(main())
