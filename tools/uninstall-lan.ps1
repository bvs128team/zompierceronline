# ZompiercerLAN uninstaller. Preview is the default; -Execute applies the plan.
# ASCII source for Windows PowerShell 5.1 on non-UTF-8 code pages.
param([string]$GameRoot = $PSScriptRoot, [switch]$Execute, [switch]$RemoveModData)
$ErrorActionPreference = 'Stop'
$plannedRemovals = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)

function Full([string]$Path) { [IO.Path]::GetFullPath($Path).TrimEnd('\','/') }
function Assert-Plain([string]$Path) {
    for ($part = $Path; $part; $part = [IO.Path]::GetDirectoryName($part)) {
        if ((Test-Path -LiteralPath $part) -and ((Get-Item -LiteralPath $part -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "Linked path rejected: $part"
        }
    }
}
function Assert-InRoot([string]$Path) {
    $resolved = Full $Path
    if (-not $resolved.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Path outside game root: $resolved"
    }
    Assert-Plain $resolved
    return $resolved
}
function Remove-File([string]$Relative) {
    $path = Assert-InRoot (Join-Path $root $Relative)
    if (-not (Test-Path -LiteralPath $path)) { return }
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Expected file: $path" }
    [void]$plannedRemovals.Add($path)
    Write-Host "FILE $path"
    if ($Execute) { Remove-Item -LiteralPath $path -Force }
}
function Remove-Empty([string]$Relative) {
    $path = Assert-InRoot (Join-Path $root $Relative)
    if (-not (Test-Path -LiteralPath $path)) { return }
    if (-not (Test-Path -LiteralPath $path -PathType Container)) { throw "Expected directory: $path" }
    $remaining = @(Get-ChildItem -LiteralPath $path -Force | Where-Object {
        $Execute -or -not $plannedRemovals.Contains((Full $_.FullName))
    })
    if ($remaining.Count -eq 0) {
        [void]$plannedRemovals.Add($path)
        Write-Host "EMPTY DIRECTORY $path"
        if ($Execute) { Remove-Item -LiteralPath $path }
    } else { Write-Host "KEEP NONEMPTY DIRECTORY $path" }
}
function Remove-ModDirectory([string]$Relative) {
    $path = Assert-InRoot (Join-Path $root $Relative)
    if (-not (Test-Path -LiteralPath $path)) { return }
    if (-not (Test-Path -LiteralPath $path -PathType Container)) { throw "Expected directory: $path" }
    # Inspect every descendant before any recursive operation; never follow a link.
    $pending = New-Object 'System.Collections.Generic.Stack[string]'
    $pending.Push($path)
    while ($pending.Count -gt 0) {
        $directory = Assert-InRoot $pending.Pop()
        foreach ($entry in Get-ChildItem -LiteralPath $directory -Force) {
            $child = Assert-InRoot $entry.FullName
            if ($entry.PSIsContainer) { $pending.Push($child) }
        }
    }
    [void]$plannedRemovals.Add($path)
    Write-Host "MOD DATA DIRECTORY $path"
    if ($Execute) { Remove-Item -LiteralPath $path -Recurse -Force }
}

$root = Full $GameRoot
Assert-Plain $root
if (-not (Test-Path -LiteralPath (Join-Path $root 'Zompiercer.exe') -PathType Leaf) -or
    -not (Test-Path -LiteralPath (Join-Path $root 'Zompiercer_Data') -PathType Container)) {
    throw 'GameRoot must be the Zompiercer installation directory.'
}
if (Get-Process -Name Zompiercer -ErrorAction SilentlyContinue) { throw 'Close Zompiercer before uninstalling.' }
if (Get-Process -Name ZompiercerLAN.Network -ErrorAction SilentlyContinue) { throw 'Close the ZompiercerLAN network worker before uninstalling.' }

$plugins = Assert-InRoot (Join-Path $root 'BepInEx\plugins')
$patchers = Assert-InRoot (Join-Path $root 'BepInEx\patchers')
$worker = Assert-InRoot (Join-Path $plugins 'ZompiercerLAN.Network')
# Pinned BepInEx 5.4.23.5 files, derived from official archive SHA256
# 82F9878551030F54657792C0740D9D51A09500EEAE1FBA21106B0C441E6732C4.
# Never use an editable installed manifest as evidence of ownership.
$loaderFiles = @{
    '.doorstop_version' = '75A2F501000D4FE28F74BC7EE66DAE5581959D28B6C44F0C1B0C05CD5BA261E6'
    'doorstop_config.ini' = '4D5C6DFA0F771C6A5B1B0C559ACA0BD0ECE7D08B08FFF894708DC3B73CE73CFC'
    'winhttp.dll' = '8C6CDBC38836DEE87E3368F5DE1994D7C0CCEBF29E4CE7ABA3C0981F9375412C'
    'BepInEx\core\0Harmony.dll' = '1A21CC03424FC82C3DD1346905D16494536B9595AE4162228D99FB7C285C1031'
    'BepInEx\core\0Harmony.xml' = 'D1F02FC3ADA3A13DA307DE421225BFE56EBE24064370980979391C4BE021672F'
    'BepInEx\core\0Harmony20.dll' = 'C755A5AB2712915A32F9A850D580A8C1B70FF1653BC76600F2767AA8FFCCC663'
    'BepInEx\core\BepInEx.dll' = '8255B28902886085C578B9E427D3073C97002DB85176D2090CDEDA90EF14CE70'
    'BepInEx\core\BepInEx.Harmony.dll' = '00C8F2AC48593E4D45CE4EB26CE4E68B18113C5C489B38B8BF0850F65830D601'
    'BepInEx\core\BepInEx.Harmony.xml' = 'A04FEDF08F7C81F5D01ABA6F2840A7FFCE50B79BBD24587D8DBE69AB73971D29'
    'BepInEx\core\BepInEx.Preloader.dll' = '55D3895351A9D16B63B6F35F1C01B44AC650979E853D0BD3A442B92A082AF64F'
    'BepInEx\core\BepInEx.Preloader.xml' = '33275B2783B7B99495A02C098EE86D6A9A2783E884A5C9369021F20C94CF99E7'
    'BepInEx\core\BepInEx.xml' = '7BA2266061B9F8A9F146218A312BFDD0D7F2B53A99A7E66C957215C6B66CA0D7'
    'BepInEx\core\HarmonyXInterop.dll' = '7CE1342D3AFA0334B59A3E38C0AED15E162B9ABE9D46F95F34BE44AF47F3B493'
    'BepInEx\core\Mono.Cecil.dll' = '7AE470288FFF4A402899C254D0A76CEFEF55877F5C54F96E83C797CC5BB6E2F6'
    'BepInEx\core\Mono.Cecil.Mdb.dll' = '5896D1898F616701FFF18F3B2C71E6B844D2390EF9F41E1C5FCCCE8CB27C698E'
    'BepInEx\core\Mono.Cecil.Pdb.dll' = '174DB44A067F58561510AF746F3CAEB032037762C57A31C8D9EE32DB25174984'
    'BepInEx\core\Mono.Cecil.Rocks.dll' = '54AC539FB5DDC8B44C0E9ACD0FCB7324F89D1A072EDF8EBC1B06DD691E3D3927'
    'BepInEx\core\MonoMod.RuntimeDetour.dll' = '40E49BB314391CD7BDDC2644F8553EEBA92C194B940836B103DF16955C464E0C'
    'BepInEx\core\MonoMod.RuntimeDetour.xml' = '54887808960D156550B37D602D08847607AA9E908D039F2765FB0B5E79394AA4'
    'BepInEx\core\MonoMod.Utils.dll' = '9D1495F147AC93C4F81F84538C1A326E8F8A6AEFC78D6289D798F3CE1162C5E9'
    'BepInEx\core\MonoMod.Utils.xml' = '0577B362023A3432D6E8D7934C5EDDC3E08FDBB19E191AF083E341562C5EDE38'
}
$legacyCryptoHash = '4F96977E9C67334742C683410B3A361258219F0D3084A5E0BC10FBA96CF23A0D'
$otherPlugins = @(if (Test-Path -LiteralPath $plugins -PathType Container) {
    Get-ChildItem -LiteralPath $plugins -Force | Where-Object {
        if ($_.Name -eq 'BouncyCastle.Cryptography.dll' -and -not $_.PSIsContainer) {
            $legacyPath = Assert-InRoot $_.FullName
            (Get-FileHash -LiteralPath $legacyPath -Algorithm SHA256).Hash -ne $legacyCryptoHash
        } else { $_.Name -notin @('ZompiercerLAN.dll','ZompiercerLAN.Network') }
    }
})
$otherPatchers = @(if (Test-Path -LiteralPath $patchers -PathType Container) { Get-ChildItem -LiteralPath $patchers -Force })
# BepInEx can discover plugins below subdirectories, including the worker directory.
$otherWorkerFiles = @(if (Test-Path -LiteralPath $worker -PathType Container) {
    Get-ChildItem -LiteralPath $worker -Force | Where-Object {
        $_.PSIsContainer -or $_.Name -notin @('ZompiercerLAN.Network.exe','ZompiercerLAN.Network.exe.config','BouncyCastle.Cryptography.dll')
    }
})
$keepLoader = $otherPlugins.Count -gt 0 -or $otherPatchers.Count -gt 0 -or $otherWorkerFiles.Count -gt 0
$core = Assert-InRoot (Join-Path $root 'BepInEx\core')
if (Test-Path -LiteralPath $core -PathType Container) {
    foreach ($entry in Get-ChildItem -LiteralPath $core -Force) {
        if ($entry.PSIsContainer -or -not $loaderFiles.ContainsKey(('BepInEx\core\' + $entry.Name))) {
            $keepLoader = $true
            Write-Host "Unknown core entry found; shared loader will remain: $($entry.FullName)"
        }
    }
}
foreach ($relative in $loaderFiles.Keys) {
    $path = Assert-InRoot (Join-Path $root $relative)
    if ((Test-Path -LiteralPath $path) -and (
        -not (Test-Path -LiteralPath $path -PathType Leaf) -or
        (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $loaderFiles[$relative])) {
        $keepLoader = $true
        Write-Host "Changed loader file found; shared loader will remain: $path"
    }
}
Write-Host "ZompiercerLAN uninstall plan for $root"
if ($keepLoader) { Write-Host 'Other plugins, patchers, or changed/unknown loader files found: shared loader will remain.' }
else { Write-Host 'No other BepInEx plugins or patchers found: known loader files will be removed.' }
if ($RemoveModData) { Write-Host 'Explicit data removal selected: LAN backups, sessions, and checkpoints will be deleted.' }
else { Write-Host 'Save backups and LAN session data are preserved.' }
Write-Host 'Original saves, guest sidecars, and the player identity are always preserved.'
if (-not $Execute) { Write-Host 'Preview only. Run with -Execute to apply.' }

# A successful firewall removal must happen before the setup files and worker vanish.
# The setup script requests Windows elevation only for this firewall operation.
$setup = Assert-InRoot (Join-Path $root 'Setup-LAN.ps1')
$configure = Assert-InRoot (Join-Path $root 'Configure-LAN.ps1')
if ((Test-Path -LiteralPath $setup -PathType Leaf) -and (Test-Path -LiteralPath $configure -PathType Leaf) -and
    (Test-Path -LiteralPath (Join-Path $worker 'ZompiercerLAN.Network.exe') -PathType Leaf)) {
    Write-Host 'FIREWALL: remove two path-specific ZompiercerLAN rules'
    if ($Execute) { & $setup -Remove }
} else { Write-Host 'FIREWALL: setup files or worker missing; inspect any remaining ZompiercerLAN rules manually.' }

Remove-File 'BepInEx\plugins\ZompiercerLAN.dll'
foreach ($name in @('ZompiercerLAN.Network.exe','ZompiercerLAN.Network.exe.config','BouncyCastle.Cryptography.dll')) {
    Remove-File (Join-Path 'BepInEx\plugins\ZompiercerLAN.Network' $name)
}
Remove-Empty 'BepInEx\plugins\ZompiercerLAN.Network'
Write-Host 'KEEP CONFIGURATION: BepInEx config files, cache, and logs are preserved.'
foreach ($name in @('Configure-LAN.ps1','Setup-LAN.ps1')) { Remove-File $name }
$setupCmd = (-join [char[]](0x041D,0x0430,0x0441,0x0442,0x0440,0x043E,0x0438,0x0442,0x044C)) + ' LAN.cmd'
Remove-File $setupCmd

if ($RemoveModData) {
    foreach ($name in @('BepInEx\LAN-backups','BepInEx\LAN-sessions','BepInEx\LAN-inventory-checkpoints')) {
        Remove-ModDirectory $name
    }
}

if (-not $keepLoader) {
    $legacyCrypto = Assert-InRoot (Join-Path $plugins 'BouncyCastle.Cryptography.dll')
    if (Test-Path -LiteralPath $legacyCrypto -PathType Leaf) {
        if ((Get-FileHash -LiteralPath $legacyCrypto -Algorithm SHA256).Hash -eq '4F96977E9C67334742C683410B3A361258219F0D3084A5E0BC10FBA96CF23A0D') {
            Remove-File 'BepInEx\plugins\BouncyCastle.Cryptography.dll'
        } else { Write-Host "KEEP CHANGED LEGACY LIBRARY $legacyCrypto" }
    }
    foreach ($relative in @($loaderFiles.Keys | Sort-Object)) {
        $path = Assert-InRoot (Join-Path $root $relative)
        if (Test-Path -LiteralPath $path -PathType Leaf) {
            # Recheck immediately before deletion in case files changed after planning.
            if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -eq $loaderFiles[$relative]) {
                Remove-File $relative
            } else { throw "Loader changed during uninstall; refusing deletion: $path" }
        }
    }
    foreach ($name in @('BepInEx\core','BepInEx\cache','BepInEx\config','BepInEx\plugins','BepInEx\patchers','BepInEx')) { Remove-Empty $name }
}
Write-Host 'Preserved: original game saves, guest sidecars, and %LOCALAPPDATA%\ZompiercerLAN.'
if (-not $RemoveModData) { Write-Host 'Preserved: BepInEx LAN-backups/LAN-sessions/LAN-inventory-checkpoints.' }
if ($Execute) { Write-Host 'ZompiercerLAN runtime removal finished. Restart the game normally.' }
