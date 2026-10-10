# Executes the uninstaller ONLY in fresh disposable fixtures under this project's build directory.
param()
$ErrorActionPreference = 'Stop'
$project = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$build = Join-Path $project 'build'
for ($part = $build; $part; $part = [IO.Path]::GetDirectoryName($part)) {
    if ((Test-Path -LiteralPath $part) -and ((Get-Item -LiteralPath $part -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw "Linked fixture parent rejected: $part"
    }
}
$fixtures = Join-Path $build ('uninstall-safety-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixtures | Out-Null
$uninstaller = Join-Path $PSScriptRoot 'uninstall-lan.ps1'
$archive = Join-Path $project 'vendor\BepInEx_win_x64_5.4.23.5.zip'
if (-not (Test-Path -LiteralPath $archive)) {
    throw 'Put the official BepInEx_win_x64_5.4.23.5.zip (github.com/BepInEx/BepInEx/releases/tag/v5.4.23.5) into vendor\ first.'
}
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne '82F9878551030F54657792C0740D9D51A09500EEAE1FBA21106B0C441E6732C4') {
    throw 'Fixture archive differs from the pinned official release.'
}
Add-Type -AssemblyName System.IO.Compression.FileSystem
$seed = Join-Path $fixtures 'official'
[IO.Compression.ZipFile]::ExtractToDirectory($archive, $seed)
$loaderPaths = @(Get-ChildItem -LiteralPath $seed -Recurse -File | ForEach-Object {
    $_.FullName.Substring($seed.Length + 1)
} | Where-Object { $_ -ne 'changelog.txt' })
$preserved = @('LAN-firewall-setup.log','BepInEx\config\local.zompiercer.lan.cfg','BepInEx\config\BepInEx.cfg',
    'BepInEx\config\another-plugin.cfg','BepInEx\LogOutput.log','BepInEx\cache\chainloader_typeloader.dat',
    'BepInEx\LAN-backups\backup.dat','BepInEx\LAN-sessions\session.dat',
    'BepInEx\LAN-inventory-checkpoints\checkpoint.dat','Zompiercer_Data\save.dat','guest-sidecar.dat')
$runtime = @('BepInEx\plugins\ZompiercerLAN.dll',
    'BepInEx\plugins\ZompiercerLAN.Network\ZompiercerLAN.Network.exe',
    'BepInEx\plugins\ZompiercerLAN.Network\ZompiercerLAN.Network.exe.config',
    'BepInEx\plugins\ZompiercerLAN.Network\BouncyCastle.Cryptography.dll')
function Put([string]$Root, [string]$Relative, [string]$Value = 'fixture user data') {
    $path = Join-Path $Root $Relative
    New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($path)) -Force | Out-Null
    [IO.File]::WriteAllText($path, $Value)
}
function Assert([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}
function Snapshot([string]$Root) {
    @(Get-ChildItem -LiteralPath $Root -Recurse -File -Force | Sort-Object FullName | ForEach-Object {
        $_.FullName.Substring($Root.Length + 1) + '=' + (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
    }) -join "`n"
}
function Run-Case([string]$Name, [scriptblock]$Change, [bool]$KeepLoader = $false, [switch]$RemoveData) {
    $fixture = [IO.Path]::GetFullPath((Join-Path $fixtures $Name))
    Assert ($fixture.StartsWith($fixtures + '\', [StringComparison]::OrdinalIgnoreCase)) 'Fixture escaped the disposable root.'
    New-Item -ItemType Directory -Path (Join-Path $fixture 'Zompiercer_Data') -Force | Out-Null
    Put $fixture 'Zompiercer.exe' 'test fixture, not executable'
    foreach ($relative in $loaderPaths) {
        $target = Join-Path $fixture $relative
        New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($target)) -Force | Out-Null
        Copy-Item -LiteralPath (Join-Path $seed $relative) -Destination $target
    }
    foreach ($relative in @($preserved) + @($runtime)) { Put $fixture $relative }
    & $Change $fixture
    $before = Snapshot $fixture
    $beforeFiles = @(Get-ChildItem -LiteralPath $fixture -Recurse -File -Force | ForEach-Object { $_.FullName })
    $beforeHashes = @{}
    foreach ($path in $beforeFiles) { $beforeHashes[$path] = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash }
    $preview = @(& $uninstaller -GameRoot $fixture -RemoveModData:$RemoveData 6>&1 | ForEach-Object { $_.ToString() })
    Assert ((Snapshot $fixture) -ceq $before) "$Name : preview mutated files."
    $actual = @(& $uninstaller -GameRoot $fixture -Execute -RemoveModData:$RemoveData 6>&1 | ForEach-Object { $_.ToString() })
    $actions = '^(FILE |EMPTY DIRECTORY |MOD DATA DIRECTORY |KEEP NONEMPTY DIRECTORY )'
    Assert ((($preview | Where-Object { $_ -match $actions }) -join "`n") -ceq (($actual | Where-Object { $_ -match $actions }) -join "`n")) "$Name : preview and Execute plans differ."
    foreach ($path in $beforeFiles) {
        $removed = $preview -contains ('FILE ' + $path)
        if ($RemoveData -and $path -match '\\BepInEx\\LAN-(backups|sessions|inventory-checkpoints)\\') { $removed = $true }
        Assert ((Test-Path -LiteralPath $path) -ne $removed) "$Name : unexpected file result for $path"
        if (-not $removed) {
            Assert ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -eq $beforeHashes[$path]) "$Name : preserved file changed: $path"
        }
    }
    foreach ($relative in $runtime) { Assert (-not (Test-Path -LiteralPath (Join-Path $fixture $relative))) "$Name : mod runtime remains." }
    foreach ($relative in $preserved) {
        if ($RemoveData -and $relative -like 'BepInEx\LAN-*') { continue }
        Assert ([IO.File]::ReadAllText((Join-Path $fixture $relative)) -ceq 'fixture user data') "$Name : user data changed."
    }
    if ($KeepLoader) {
        foreach ($relative in $loaderPaths) { Assert (Test-Path -LiteralPath (Join-Path $fixture $relative)) "$Name : shared loader removed: $relative" }
    } else {
        foreach ($relative in $loaderPaths) { Assert (-not (Test-Path -LiteralPath (Join-Path $fixture $relative))) "$Name : official loader remains: $relative" }
    }
    Write-Output "PASS: $Name (preview and Execute agree)"
}
Run-Case 'standard' { param($fixture) }
foreach ($relative in $loaderPaths) {
    $script:changedRelative = $relative
    Run-Case ('changed-' + ($relative -replace '[^a-zA-Z0-9]','_')) {
        param($fixture)
        Put $fixture $script:changedRelative 'changed library or bootstrap configuration'
    } $true
}
Run-Case 'unknown-core' { param($fixture) Put $fixture 'BepInEx\core\unknown.dll' } $true
Run-Case 'unknown-core-directory' { param($fixture) Put $fixture 'BepInEx\core\extension\private.dll' } $true
Run-Case 'other-plugin' { param($fixture) Put $fixture 'BepInEx\plugins\Other\Plugin.dll' } $true
Run-Case 'other-patcher' { param($fixture) Put $fixture 'BepInEx\patchers\Patcher.dll' } $true
Run-Case 'changed-legacy-library' { param($fixture) Put $fixture 'BepInEx\plugins\BouncyCastle.Cryptography.dll' } $true
Run-Case 'partial-loader' {
    param($fixture)
    foreach ($relative in @('winhttp.dll','BepInEx\core\BepInEx.dll','BepInEx\core\Mono.Cecil.dll')) {
        Remove-Item -LiteralPath (Join-Path $fixture $relative)
    }
}
Run-Case 'modified-mod-and-unknown-worker-file' {
    param($fixture)
    Put $fixture 'BepInEx\plugins\ZompiercerLAN.dll' 'modified mod DLL still explicitly uninstalled'
    Put $fixture 'BepInEx\plugins\ZompiercerLAN.Network\personal.txt'
} $true
Run-Case 'nested-plugin-in-worker' { param($fixture) Put $fixture 'BepInEx\plugins\ZompiercerLAN.Network\Other\Plugin.dll' } $true
Run-Case 'explicit-mod-data-removal' { param($fixture) } -RemoveData
Write-Output "Disposable fixtures retained at $fixtures"
