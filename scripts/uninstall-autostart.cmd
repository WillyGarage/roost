@echo off
REM Elevated, policy-bypassed wrapper for uninstall-autostart.ps1.
REM See install-autostart.cmd for why both are needed.

echo Requesting administrator rights...

powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "Start-Process powershell -Verb RunAs -ArgumentList '-NoProfile','-ExecutionPolicy','Bypass','-NoExit','-File','%~dp0uninstall-autostart.ps1'"

if errorlevel 1 (
    echo.
    echo Could not elevate. Approve the UAC prompt, or run this from an elevated prompt:
    echo   powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0uninstall-autostart.ps1"
)
