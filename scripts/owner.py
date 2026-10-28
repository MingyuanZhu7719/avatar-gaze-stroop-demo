#!/usr/bin/env python3
"""Run the separate simulation owner console using an installed compiler only."""
from pathlib import Path
import shutil
import subprocess
import sys
from test import ROOT, unity_tools

def main():
    args=sys.argv[1:]
    dotnet_only="--dotnet" in args
    if dotnet_only: args.remove("--dotnet")
    try:
        installed=None if dotnet_only else unity_tools()
    except ValueError as error:
        print(str(error),file=sys.stderr)
        return 2
    if installed:
        _,mono,csc=installed
        output=ROOT/"build"
        output.mkdir(exist_ok=True)
        binary=output/"AvatarStroop.OwnerConsole.exe"
        sources=sorted((ROOT/"Assets/Stroop/Core").rglob("*.cs"))+sorted((ROOT/"Assets/Stroop/Runtime").rglob("*.cs"))+[ROOT/"Apps/OwnerConsole.cs"]
        subprocess.run([str(mono),str(csc),"-nologo","-langversion:7.3","-warnaserror","-target:exe","-main:AvatarStroop.Apps.OwnerConsole","-out:"+str(binary)]+[str(p) for p in sources],check=True,cwd=str(ROOT))
        launch=[str(mono),str(binary)]
    else:
        dotnet=shutil.which("dotnet")
        if not dotnet:
            print("Use an existing .NET 8 SDK or set STROOP_UNITY_CONTENTS to an installed Editor/Data (Windows/Linux) or Unity.app/Contents (macOS).",file=sys.stderr)
            return 2
        launch=[dotnet,"run","--project",str(ROOT/"AvatarStroop.OwnerConsole.csproj"),"--"]
    return subprocess.call(launch+args,cwd=str(ROOT))

if __name__=="__main__":
    sys.exit(main())
