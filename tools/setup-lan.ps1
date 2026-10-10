param([switch]$Remove)
$ErrorActionPreference = 'Stop'
$lanSetupPath = Join-Path $PSScriptRoot 'Configure-LAN.ps1'
if (-not (Test-Path -LiteralPath $lanSetupPath -PathType Leaf)) { throw 'Configure-LAN.ps1 is missing beside this setup script.' }
$lanPowerShell = Join-Path ([Environment]::GetFolderPath('System')) 'WindowsPowerShell\v1.0\powershell.exe'
$lanArguments = '-NoProfile -ExecutionPolicy Bypass -File "' + $lanSetupPath + '" -GameRoot "' + $PSScriptRoot + '"'
if ($Remove) { $lanArguments += ' -Remove' }
$lanProcess = Start-Process -FilePath $lanPowerShell -ArgumentList $lanArguments -Verb RunAs -WindowStyle Hidden -Wait -PassThru
if ($lanProcess.ExitCode -ne 0) {
    $lanSetupLog = Join-Path $PSScriptRoot 'LAN-firewall-setup.log'
    throw ('LAN setup failed (exit code {0}). Check the latest run in "{1}" for the specific error.' -f $lanProcess.ExitCode, $lanSetupLog)
}
