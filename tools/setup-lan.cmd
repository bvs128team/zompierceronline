@echo off
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File "%~dp0Setup-LAN.ps1"
if errorlevel 1 (
  echo LAN setup did not finish. Read LAN-firewall-setup.log in the game folder or run Configure-LAN.ps1 from an administrator PowerShell.
  pause
  exit /b 1
)
echo LAN setup finished. Start the game normally.
pause
