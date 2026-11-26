@echo off
setlocal DisableDelayedExpansion
title WoZ Operator - diagnostic launch
set "package=%~dp0"
set "operator=%package%AvatarStroopViveWozOperatorDevelopment.exe"
if not exist "%operator%" goto missing_player
if not exist "%package%AvatarStroopViveWozOperatorDevelopment_Data\." goto missing_player
tasklist /FI "IMAGENAME eq AvatarStroopViveWozOperatorDevelopment.exe" /FO CSV /NH 2>nul | find /I "AvatarStroopViveWozOperatorDevelopment.exe" >nul
if not errorlevel 1 goto already_running

:choose_folder
set "runfolder=%package%WoZLogs\run-%RANDOM%-%RANDOM%"
if exist "%runfolder%" goto choose_folder
mkdir "%runfolder%" 2>nul
if not exist "%runfolder%\." goto write_failed
set "XR_RUNTIME_JSON=%ProgramFiles(x86)%\Steam\steamapps\common\SteamVR\steamxr_win64.json"
if not "%~1"=="" set "XR_RUNTIME_JSON=%~f1"
>"%runfolder%\Launcher.txt" echo WoZ Operator diagnostic launch
>>"%runfolder%\Launcher.txt" echo Started %DATE% %TIME%
>>"%runfolder%\Launcher.txt" echo SteamVR manifest: "%XR_RUNTIME_JSON%"
if not exist "%runfolder%\Launcher.txt" goto write_failed
for %%R in ("%XR_RUNTIME_JSON%") do if /I not "%%~nxR"=="steamxr_win64.json" goto missing_runtime
if not exist "%XR_RUNTIME_JSON%" goto missing_runtime
for %%R in ("%XR_RUNTIME_JSON%") do if not exist "%%~dpRbin\vrclient_x64.dll" goto missing_runtime
echo Opening the operator program with SteamVR and diagnostic logging.
echo After reproducing an issue, use Export debug ZIP inside the program.
echo Closing the operator normally will open this launch's log folder.
echo Log folder: "%runfolder%"
start "" /wait /D "%package%" "%operator%" -logFile "%runfolder%\Player.log"
set "result=%ERRORLEVEL%"
>>"%runfolder%\Launcher.txt" echo Process exit code: %result%
if not exist "%runfolder%\Player.log" >>"%runfolder%\Launcher.txt" echo The player did not create Player.log during this launch.
start "" explorer.exe "%runfolder%"
exit /b %result%

:missing_player
echo Keep this CMD file beside AvatarStroopViveWozOperatorDevelopment.exe and its complete Data folder.
pause
exit /b 1

:already_running
echo Close the existing WoZ Operator program, then double-click this file again.
pause
exit /b 1

:write_failed
echo Cannot write WoZLogs beside this program. Copy the complete Operator folder to your Desktop and try again.
pause
exit /b 1

:missing_runtime
>>"%runfolder%\Launcher.txt" echo SteamVR's Windows x64 manifest or runtime library was unavailable. The operator was not launched.
echo Locate steamxr_win64.json in your installed SteamVR folder.
echo Drag that file onto Start-WoZ-Operator.cmd to use that installation.
echo Send Launcher.txt if you need help locating it.
start "" explorer.exe "%runfolder%"
pause
exit /b 1
