param([Parameter(Mandatory=$true)][string]$PackageRoot)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath $PackageRoot).Path.TrimEnd('\')
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$gameRoot = [IO.Path]::GetFullPath((Join-Path $projectRoot '..'))
Add-Type -LiteralPath (Join-Path $gameRoot 'BepInEx\core\Mono.Cecil.dll')
$files = @(Get-ChildItem -LiteralPath $root -File -Recurse -Force)
if (@(Get-ChildItem -LiteralPath $root -Recurse -Force | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }).Count) { throw 'Linked package entry.' }
$manifest = @{}
foreach ($line in [IO.File]::ReadAllLines((Join-Path $root 'MANIFEST.txt'))) {
    if ($line -match '^([A-Fa-f0-9]{64})  (.+)$') {
        $name = $Matches[2]
        if ($manifest.ContainsKey($name) -or $name.Contains('..') -or [IO.Path]::IsPathRooted($name)) { throw 'Invalid manifest entry.' }
        $manifest[$name] = $Matches[1]
    }
}
if ($manifest.Count -ne $files.Count - 1) { throw 'Manifest does not cover every package file.' }
# Names of this machine and its owner live in an optional local file (one per line), not here.
$markerFile = Join-Path $PSScriptRoot 'private-markers.txt'
$privateMarkers = if (Test-Path -LiteralPath $markerFile) { (@(Get-Content -LiteralPath $markerFile | Where-Object { $_.Trim() } | ForEach-Object { [regex]::Escape($_.Trim()) }) -join '|') } else { '' }
$personalMatches = 0
foreach ($file in $files) {
    $name = $file.FullName.Substring($root.Length + 1).Replace('\','/')
    if ($name -ne 'MANIFEST.txt' -and (!$manifest.ContainsKey($name) -or (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash -ne $manifest[$name])) { throw "Manifest mismatch: $name" }
    if ($name -match '(?i)(^|/)(bin|obj|diagnostics|\.git|Saves|LAN-sessions|LAN-backups|LAN-inventory-checkpoints)(/|$)|\.log$|\.dpapi$|\.tac$|guests\.dat|local\.zompiercer\.lan\.cfg|(^|/)BepInEx\.cfg$') { throw "Private/runtime entry: $name" }
    $bytes = [IO.File]::ReadAllBytes($file.FullName)
    foreach ($encoding in @([Text.Encoding]::ASCII,[Text.Encoding]::Unicode,[Text.Encoding]::UTF8)) {
        $value = $encoding.GetString($bytes)
        if ($value -match '(?i)[A-Z]:\\Users\\|/Users/|/home/|\.codex[\\/]visualizations|-----BEGIN (RSA |EC |OPENSSH )?PRIVATE KEY-----|\bgh[pousr]_[A-Za-z0-9]{30,}|github_pat_[A-Za-z0-9_]{30,}|\bAKIA[A-Z0-9]{16}\b|\bsk-[A-Za-z0-9]{32,}' -or ($privateMarkers -and $value -match ('(?i)' + $privateMarkers))) {
            $personalMatches++
            Write-Output "Unexpected personal path or credential marker in $name"
        }
    }
}
if ($personalMatches) { throw "Privacy scan failed ($personalMatches matches); values suppressed." }
$assembly = [Mono.Cecil.AssemblyDefinition]::ReadAssembly((Join-Path $root 'BepInEx\plugins\ZompiercerLAN.dll'))
try {
    $plugin = $assembly.MainModule.Types | Where-Object FullName -eq 'ZompiercerLAN.LanPlugin'
    $version = ($plugin.Fields | Where-Object Name -eq 'PluginVersion').Constant
    if ($version -ne '1.4.20') { throw 'Unexpected release version.' }
    $pins = $assembly.MainModule.Types | Where-Object FullName -eq 'ZompiercerLAN.LanWorkerIntegrity'
    foreach ($pair in @(@('Executable','ZompiercerLAN.Network.exe'),@('Configuration','ZompiercerLAN.Network.exe.config'),@('Crypto','BouncyCastle.Cryptography.dll'))) {
        $expected = ($pins.Fields | Where-Object Name -eq $pair[0]).Constant
        if ((Get-FileHash -LiteralPath (Join-Path $root ('BepInEx\plugins\ZompiercerLAN.Network\' + $pair[1]))).Hash -ne $expected) { throw "Worker integrity mismatch: $($pair[1])" }
    }
    if (@($assembly.MainModule.GetDebugHeader().Entries | Where-Object {$_.Directory.Type.ToString() -eq 'CodeView'}).Count) { throw 'Own DLL contains a PDB reference.' }
} finally { $assembly.Dispose() }
if (@(Get-ChildItem -LiteralPath (Join-Path $root 'BepInEx\plugins\ZompiercerLAN.Network') -Force).Count -ne 3) { throw 'Unexpected worker file.' }
Write-Output "PASS: release version, all $($manifest.Count) file hashes, worker pins, no personal paths/private file/credential markers. Built-in relay service settings are intentionally included."
