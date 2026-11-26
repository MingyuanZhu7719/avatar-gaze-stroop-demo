@echo off
setlocal DisableDelayedExpansion
title WoZ Operator - diagnostic launch
if not exist "%~dp0WoZOperatorLauncher.exe" goto missing_launcher
"%~dp0WoZOperatorLauncher.exe" %*
set "result=%ERRORLEVEL%"
exit /b %result%

:missing_launcher
echo Keep Start-WoZ-Operator.cmd beside WoZOperatorLauncher.exe and the complete Operator package.
pause
exit /b 1
