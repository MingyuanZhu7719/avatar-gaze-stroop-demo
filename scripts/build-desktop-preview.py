#!/usr/bin/env python3
"""Build the Windows desktop preview with an existing Unity installation only.

Use a disposable copy for --project if the installed Editor differs from the
baseline version. No package installation or license activation is performed.
"""
import argparse
import os
from pathlib import Path
import subprocess
import sys
from test import ROOT, unity_tools
from windows_framework import framework_references, verify_preview_runtime, compile_preview_launcher

def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--editor',required=True,type=Path)
    parser.add_argument('--project',required=True,type=Path)
    parser.add_argument('--output',type=Path,default=ROOT/'build/desktop-preview')
    parser.add_argument('--check-only',action='store_true',help='Check source consistency without compiling or launching the Editor.')
    args=parser.parse_args()
    if sys.platform!='win32':
        parser.error('This package uses the existing Windows .NET Framework and Windows Mono player module.')
    editor=args.editor.resolve(); project=args.project.resolve(); output=args.output.resolve()
    if not editor.is_file() or not (project/'Assets/Stroop/Unity/Editor/DevelopmentPlayerBuild.cs').is_file():
        parser.error('Existing Editor and prepared disposable project with current build source are required.')
    # Refuse a mixed package when an older disposable project is reused.
    source_paths=sorted(p.relative_to(ROOT) for p in (ROOT/'Assets').rglob('*')
                        if p.is_file() and p.suffix in ('.cs','.shader'))
    project_paths=sorted(p.relative_to(project) for p in (project/'Assets').rglob('*')
                         if p.is_file() and p.suffix in ('.cs','.shader'))
    if source_paths!=project_paths or any(not (project/p).is_file() or (ROOT/p).read_bytes()!=(project/p).read_bytes() for p in source_paths):
        parser.error('Disposable project sources differ from the launcher source tree. Refresh the isolated copy first.')
    if (ROOT/'Packages/manifest.json').read_bytes()!=(project/'Packages/manifest.json').read_bytes():
        parser.error('Disposable project package manifest differs from the source baseline.')
    if output==project or output.is_relative_to(project/'Assets'):
        parser.error('Output must be separate from the project Assets directory.')
    try:
        framework_references()
    except ValueError as error:
        parser.error(str(error))
    if args.check_only:
        print('Source and package consistency verified for both players and owner launcher.')
        return 0
    os.environ['STROOP_UNITY_CONTENTS']=str(editor.parent/'Data')
    installed=unity_tools()
    _,mono,csc=installed
    output.mkdir(parents=True,exist_ok=True)
    verify_preview_runtime(mono,csc,ROOT,output)
    env=dict(os.environ,STROOP_PREVIEW_OUTPUT=str(output))
    log=output/'unity-build.log'
    command=[str(editor),'-batchmode','-nographics','-noUpm','-quit','-projectPath',str(project),
             '-executeMethod','AvatarStroop.Unity.Editor.DevelopmentPlayerBuild.BuildWindowsPreview','-logFile',str(log)]
    print('Building two simulation players; log: '+str(log),flush=True)
    result=subprocess.run(command,env=env)
    if result.returncode:
        print(log.read_text(encoding='utf-8',errors='replace')[-6000:],file=sys.stderr)
        return result.returncode
    compile_preview_launcher(mono,csc,ROOT,output/'AvatarStroopPreview.exe')
    (output/'START-HERE.txt').write_text(
        'Avatar Stroop - DESKTOP SIMULATION ONLY\n\n'
        'Open AvatarStroopPreview.exe. Keep the Participant and Operator folders beside it.\n'
        'Click Open desktop preview, then Begin practice in the owner window.\n'
        'Answer in the participant window: 1 red, 2 green, 3 blue, 4 yellow (ink colour).\n'
        'Space = continue/Ready, P = pause, R = active resume, Esc = withdraw.\n'
        'At each block, the owner explicitly selects a source and confirms begin.\n'
        'No default source, no automatic responses, no headset/eye tracking.\n'
        'The fixed development fixture is BCAD, B begins toward participant, English, 1-second countdown.\n'
        'Close both preview windows before closing the owner window.\n'
        'Synthetic sessions/logs are retained in the sessions folder.\n',encoding='utf-8')
    print('Desktop preview ready: '+str(output/'AvatarStroopPreview.exe'))
    return 0

if __name__=='__main__':
    sys.exit(main())
