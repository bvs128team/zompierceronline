@echo off
setlocal
cd /d "%~dp0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Uninstall-LAN.ps1" -Execute
if errorlevel 1 (
  echo ZompiercerLAN uninstall failed. Read the error above.
  pause
  exit /b 1
)
pause
