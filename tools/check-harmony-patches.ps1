# Checks every Harmony patch of the mod against the installed game, without running it:
# the patched method must exist, and every patch parameter that is not a Harmony
# special (__instance, __result, __args, ___field, __state, __originalMethod, a finalizer's
# __exception) must
# name a parameter of that method (Harmony binds by name and fails at game start).
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File ZompiercerLAN/tools/check-harmony-patches.ps1
#
# Pairs are read from the sources: `.Patch(AccessTools.Method(typeof(T), "M"[, types]) ...
# HarmonyMethod(typeof(C), nameof(P))`, including patch methods held in a local variable
# and target names listed in a `foreach (var name in new[] { ... })`.
# This file is ASCII on purpose (Windows PowerShell 5.1).
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$gameRoot = [IO.Path]::GetFullPath((Join-Path $projectRoot '..'))
$dll = Join-Path $projectRoot 'bin\Release\net472\ZompiercerLAN.dll'
if (-not (Test-Path -LiteralPath $dll)) { throw "Build the mod first: $dll" }
Add-Type -LiteralPath (Join-Path $gameRoot 'BepInEx\core\Mono.Cecil.dll')
$resolver = New-Object Mono.Cecil.DefaultAssemblyResolver
$resolver.AddSearchDirectory((Join-Path $gameRoot 'Zompiercer_Data\Managed'))
$resolver.AddSearchDirectory((Join-Path $gameRoot 'BepInEx\core'))
$read = New-Object Mono.Cecil.ReaderParameters
$read.AssemblyResolver = $resolver
$mod = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($dll, $read)

function AllTypes($module) {
    $list = New-Object System.Collections.Generic.List[Mono.Cecil.TypeDefinition]
    $stack = New-Object System.Collections.Generic.Stack[Mono.Cecil.TypeDefinition]
    foreach ($t in $module.Types) { $stack.Push($t) }
    while ($stack.Count -gt 0) { $t = $stack.Pop(); $list.Add($t); foreach ($n in $t.NestedTypes) { $stack.Push($n) } }
    return $list
}
$modTypes = AllTypes $mod.MainModule
$gameAssemblies = @{}
function GameType([string]$name) {
    foreach ($file in @('Assembly-CSharp.dll', 'Assembly-CSharp-firstpass.dll', 'UnityEngine.CoreModule.dll')) {
        if (-not $gameAssemblies.ContainsKey($file)) {
            $path = Join-Path $gameRoot ('Zompiercer_Data\Managed\' + $file)
            $gameAssemblies[$file] = if (Test-Path -LiteralPath $path) { AllTypes ([Mono.Cecil.AssemblyDefinition]::ReadAssembly($path, $read)).MainModule } else { @() }
        }
        $found = @($gameAssemblies[$file] | Where-Object { $_.Name -eq $name -or $_.FullName -eq $name })
        if ($found.Count -gt 0) { return $found[0] }
    }
    return $null
}

$special = '^(__instance|__result|__args|__state|__originalMethod|__runOriginal|__exception|___.+)$'
$failures = 0; $checked = 0
foreach ($source in Get-ChildItem -LiteralPath $projectRoot -Filter *.cs) {
    $text = [IO.File]::ReadAllText($source.FullName)
    $locals = @{}
    foreach ($m in [regex]::Matches($text, 'var\s+(\w+)\s*=\s*new HarmonyMethod\(typeof\((\w+)\),\s*nameof\((\w+)\)\)')) { $locals[$m.Groups[1].Value] = @($m.Groups[2].Value, $m.Groups[3].Value) }
    $names = @()
    foreach ($m in [regex]::Matches($text, 'foreach \(var name in new\[\] \{([^}]*)\}\)')) { $names += [regex]::Matches($m.Groups[1].Value, '"(\w+)"') | ForEach-Object { $_.Groups[1].Value } }
    foreach ($m in [regex]::Matches($text, '\.Patch\(AccessTools\.Method\(typeof\(([\w\.]+)\),\s*("(\w+)"|name)(,\s*new\[\]\s*\{([^}]*)\})?\)(.*?)\)\s*;')) {
        $targetType = $m.Groups[1].Value.Split('.')[-1]
        $targetNames = if ($m.Groups[2].Value -eq 'name') { $names } else { @($m.Groups[3].Value) }
        $patches = @()
        foreach ($p in [regex]::Matches($m.Groups[6].Value, 'new HarmonyMethod\((?:AccessTools\.Method\()?typeof\((\w+)\),\s*(?:nameof\((\w+)\)|"(\w+)")')) { $patches += ,@($p.Groups[1].Value, ($p.Groups[2].Value + $p.Groups[3].Value)) }
        foreach ($p in [regex]::Matches($m.Groups[6].Value, '(?:prefix|postfix|transpiler):\s*(\w+)\b')) { if ($locals.ContainsKey($p.Groups[1].Value)) { $patches += ,$locals[$p.Groups[1].Value] } }
        if ($patches.Count -eq 0) { Write-Host ("?    no patch method parsed: " + $source.Name + ": " + $m.Value.Substring(0, [Math]::Min(100, $m.Value.Length))); continue }
        $gameType = GameType $targetType
        foreach ($targetName in $targetNames) {
            $targets = if ($gameType -eq $null) { @() } else { @($gameType.Methods | Where-Object Name -eq $targetName) }
            if ($targets.Count -eq 0) { Write-Host ("FAIL " + $targetType + "." + $targetName + ": not in the game"); $failures++; continue }
            foreach ($patch in $patches) {
                $patchType = $modTypes | Where-Object Name -eq $patch[0] | Select-Object -First 1
                $patchMethod = if ($patchType) { $patchType.Methods | Where-Object Name -eq $patch[1] | Select-Object -First 1 } else { $null }
                if ($patchMethod -eq $null) { Write-Host ("FAIL patch " + $patch[0] + "." + $patch[1] + " not found"); $failures++; continue }
                $checked++
                foreach ($parameter in $patchMethod.Parameters) {
                    if ($parameter.Name -match $special) { continue }
                    if ($patchMethod.Name -match 'Rays$|^Replace') { continue } # transpilers take instructions
                    $ok = @($targets | Where-Object { @($_.Parameters | Where-Object Name -eq $parameter.Name).Count -gt 0 }).Count -gt 0
                    if (-not $ok) {
                        Write-Host ("FAIL " + $patch[0] + "." + $patch[1] + " parameter '" + $parameter.Name + "' is not a parameter of " + $targetType + "." + $targetName + "(" + (($targets[0].Parameters | ForEach-Object Name) -join ', ') + ")")
                        $failures++
                    }
                }
            }
        }
    }
}
Write-Host ("Harmony patches checked: " + $checked + ", failures: " + $failures)
if ($failures -gt 0) { exit 1 }
