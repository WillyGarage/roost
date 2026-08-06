@echo off
REM Runs install-autostart.ps1 with elevation AND an execution-policy bypass.
REM
REM Both are needed and neither is obvious: registering a highest-privileges scheduled
REM task requires admin, and the default machine execution policy (Restricted) refuses to
REM run a .ps1 from disk at all. Double-click this, approve the UAC prompt, done.
REM
REM -ExecutionPolicy Bypass applies only to the launched process, so nothing about the
REM machine's policy is changed.

echo Requesting administrator rights...

powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "Start-Process powershell -Verb RunAs -ArgumentList '-NoProfile','-ExecutionPolicy','Bypass','-NoExit','-File','%~dp0install-autostart.ps1'"

if errorlevel 1 (
    echo.
    echo Could not elevate. Approve the UAC prompt, or run this from an elevated prompt:
    echo   powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0install-autostart.ps1"
)
