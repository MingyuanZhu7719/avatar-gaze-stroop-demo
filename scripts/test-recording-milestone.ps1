param([string]$UnityData)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
if(!$UnityData){$editor=Get-ChildItem (Join-Path $env:ProgramFiles 'Unity\Hub\Editor') -Directory | Sort-Object Name -Descending | Select-Object -First 1; if(!$editor){throw 'Installed Unity Editor not found.'}; $UnityData=Join-Path $editor.FullName 'Editor\Data'}
$mono=Join-Path $UnityData 'MonoBleedingEdge\bin\mono.exe'
$csc=Join-Path $UnityData 'MonoBleedingEdge\lib\mono\4.5\csc.exe'
if(!(Test-Path -LiteralPath $mono)-or !(Test-Path -LiteralPath $csc)){throw 'Installed Unity Mono compiler unavailable.'}
Push-Location $root
try {
 New-Item -ItemType Directory -Path build -Force | Out-Null
 @'
using System;
using AvatarStroop.Tests;
public static class RecordingMilestoneMemoryRunner
{
 public static int Main(){var suite=new TestSuite();ViveRecordingMilestoneMemoryTests.Register(suite);ViveRawSessionJournalReaderTests.Register(suite);ViveRawSessionTerminalTests.Register(suite);ViveRawTrialProjectionTests.Register(suite);ViveRawSessionStreamExportTests.Register(suite);ViveRawReadableAnalysisCsvTests.Register(suite);ViveRawTaskFrameMeasurementTests.Register(suite);ViveNamedRecordingUiAdmissionTests.Register(suite);ViveInstalledOutputFailureTests.Register(suite);ViveNamedRawRecordingPumpTests.Register(suite);ViveNamedRawRecordingPumpTests.RegisterSceneInstallBoundaryTests(suite);ViveRawPrivateStageTests.Register(suite);ViveRawCompletionFenceTests.Register(suite);ViveRawAsynchronousPrefixTests.Register(suite);VivePrivateTargetFactoryRaceTests.Register(suite);Console.WriteLine("RESULT "+suite.Passed+" passed; "+suite.Failed+" failed");return suite.Failed==0?0:1;}
}
'@ | Set-Content -LiteralPath 'build\RecordingMilestoneMemoryRunner.cs' -Encoding utf8
 # Native/file fixtures may be compiled but are never registered or invoked.
 # The two CS0649 warnings are pre-existing unused synthetic async CSV hooks.
 $sources=@(Get-ChildItem Assets\Stroop\Core,Assets\Stroop\Runtime,Tests -Filter '*.cs' -Recurse -File | Sort-Object FullName | ForEach-Object {'"'+$_.FullName.Substring($root.Length+1)+'"'})
 $arguments=@('-nologo','-langversion:7.3','-warnaserror','-warnaserror-:0649','-define:STROOP_TEST_BUILD,VIVE_RAW_SYNTHETIC_TEST','-target:exe','-main:RecordingMilestoneMemoryRunner','-out:build/RecordingMilestoneMemoryRunner.exe')+$sources+'"build\RecordingMilestoneMemoryRunner.cs"'
 $arguments | Set-Content -LiteralPath 'build\RecordingMilestoneMemoryRunner.rsp' -Encoding utf8
 & $mono $csc '@build\RecordingMilestoneMemoryRunner.rsp' 2>&1 | Tee-Object -FilePath 'build\recording-milestone-compile.log'
 if($LASTEXITCODE -ne 0){throw "Compilation failed: $LASTEXITCODE"}
 & $mono 'build\RecordingMilestoneMemoryRunner.exe' 2>&1 | Tee-Object -FilePath 'build\recording-milestone-memory-tests.log'
 if($LASTEXITCODE -ne 0){throw "Memory tests failed: $LASTEXITCODE"}
} finally {Pop-Location}

