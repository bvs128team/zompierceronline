param(
    [string]$GameRoot,
    [switch]$Plan,
    [switch]$Remove
)
$ErrorActionPreference = 'Stop'

function Assert-LanFirewallRule {
    param($Rule, $App, $Port, $Address, $Interface, $Spec, [string]$Executable, [string]$Package)
    # NetSecurity unwraps a single RemoteAddress into a string on Windows
    # PowerShell 5.1. Index the captured array, never the original string.
    $remoteAddresses = @($Address.RemoteAddress)
    $interfaceNames = ((@($Interface.InterfaceType) -join ',') -replace '\s','') -split ',' | Sort-Object
    $expectedProtocol = $(if ($Spec.Protocol -eq 'UDP') { '17' } else { '6' })
    $checks = @(
        @{ Field='Enabled'; Actual=$Rule.Enabled; Expected='True'; Valid=([string]$Rule.Enabled -eq 'True') },
        @{ Field='Action'; Actual=$Rule.Action; Expected='Allow'; Valid=([string]$Rule.Action -eq 'Allow') },
        @{ Field='Direction'; Actual=$Rule.Direction; Expected='Inbound'; Valid=([string]$Rule.Direction -eq 'Inbound') },
        @{ Field='Profile'; Actual=$Rule.Profile; Expected='Private'; Valid=([string]$Rule.Profile -eq 'Private') },
        @{ Field='EdgeTraversalPolicy'; Actual=$Rule.EdgeTraversalPolicy; Expected='Block'; Valid=([string]$Rule.EdgeTraversalPolicy -eq 'Block') },
        @{ Field='Program'; Actual=$App.Program; Expected=$Executable; Valid=([string]$App.Program -eq $Executable) },
        @{ Field='Package'; Actual=$App.Package; Expected=$Package; Valid=([string]$App.Package -eq $Package) },
        @{ Field='Protocol'; Actual=$Port.Protocol; Expected=$Spec.Protocol; Valid=([string]$Port.Protocol -in @($Spec.Protocol,$expectedProtocol)) },
        @{ Field='RemoteAddress'; Actual=$remoteAddresses; Expected='LocalSubnet'; Valid=($remoteAddresses.Count -eq 1 -and $remoteAddresses[0] -eq 'LocalSubnet') },
        @{ Field='LocalPort'; Actual=$Port.LocalPort; Expected=$Spec.Ports; Valid=((@($Port.LocalPort | Sort-Object) -join ',') -eq (@($Spec.Ports | Sort-Object) -join ',')) },
        @{ Field='InterfaceType'; Actual=$Interface.InterfaceType; Expected='Wired,Wireless'; Valid=(($interfaceNames -join ',') -eq 'Wired,Wireless') }
    )
    $failures = @($checks | Where-Object { -not $_.Valid } | ForEach-Object {
        '{0}: expected [{1}], actual [{2}]' -f $_.Field, (@($_.Expected) -join ', '), (@($_.Actual) -join ', ')
    })
    if ($failures.Count -gt 0) {
        throw ('Firewall rule verification failed for {0}: {1}' -f $Spec.Name, ($failures -join '; '))
    }
}

if ([string]::IsNullOrWhiteSpace($GameRoot)) {
    if (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'Zompiercer.exe')) { $GameRoot = $PSScriptRoot }
    else { $GameRoot = Join-Path $PSScriptRoot '../..' }
}
$lanRoot = [IO.Path]::GetFullPath($GameRoot).TrimEnd('\','/')
$lanDirectory = Join-Path $lanRoot 'BepInEx\plugins\ZompiercerLAN.Network'
$lanExecutable = Join-Path $lanDirectory 'ZompiercerLAN.Network.exe'
if (-not (Test-Path -LiteralPath (Join-Path $lanRoot 'Zompiercer.exe') -PathType Leaf) -or
    -not (Test-Path -LiteralPath $lanExecutable -PathType Leaf)) { throw 'Install Zompiercer LAN into the game directory first.' }
for ($lanAncestor = [IO.DirectoryInfo]$lanDirectory; $null -ne $lanAncestor; $lanAncestor = $lanAncestor.Parent) {
    if ($lanAncestor.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Linked game directories are not supported.' }
}
if ((Get-Item -LiteralPath $lanExecutable).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Linked worker executable is not supported.' }

# Use the same canonical path-derived AppContainer identity as the launcher.
# Deriving the SID does not create a profile or grant privileges.
$lanSha = [Security.Cryptography.SHA256]::Create()
try { $lanDigest = [BitConverter]::ToString($lanSha.ComputeHash([Text.Encoding]::UTF8.GetBytes($lanDirectory.ToUpperInvariant()))).Replace('-','') }
finally { $lanSha.Dispose() }
$lanProfile = 'ZompiercerLAN.' + $lanDigest.Substring(0,40)
if (-not ('LanFirewallSid' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Security.Principal;
public static class LanFirewallSid {
    [DllImport("userenv.dll", CharSet=CharSet.Unicode)] private static extern int DeriveAppContainerSidFromAppContainerName(string name, out IntPtr sid);
    [DllImport("advapi32.dll")] private static extern IntPtr FreeSid(IntPtr sid);
    public static string Derive(string name) {
        IntPtr sid;
        int hr = DeriveAppContainerSidFromAppContainerName(name, out sid);
        if (hr < 0) Marshal.ThrowExceptionForHR(hr);
        try { return new SecurityIdentifier(sid).Value; } finally { FreeSid(sid); }
    }
}
'@
}
$lanPackage = [LanFirewallSid]::Derive($lanProfile)
$lanRulePrefix = 'ZompiercerLAN.Private.' + $lanDigest.Substring(0,16)
$lanRuleSpecs = @(
    @{ Name = $lanRulePrefix + '.UDP'; Protocol = 'UDP'; Ports = @('27776','27777') },
    @{ Name = $lanRulePrefix + '.TCP'; Protocol = 'TCP'; Ports = @('27778') }
)
if ($Plan) {
    foreach ($lanSpec in $lanRuleSpecs) {
        [pscustomobject]@{Name=$lanSpec.Name;Program=$lanExecutable;Package=$lanPackage;Direction='Inbound';Profile='Private';Protocol=$lanSpec.Protocol;LocalPort=$lanSpec.Ports;RemoteAddress='LocalSubnet';InterfaceType=@('Wired','Wireless');EdgeTraversal='Block';Action= $(if($Remove){'Remove owned rule'}else{'Allow'})}
    }
    return
}
$lanIdentity = [Security.Principal.WindowsIdentity]::GetCurrent()
$lanPrincipal = New-Object Security.Principal.WindowsPrincipal($lanIdentity)
if (-not $lanPrincipal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Windows administrator permission is required only for firewall setup. Run the LAN setup command and accept the Windows prompt; run the game normally.'
}
Import-Module NetSecurity
$lanSetupLog = Join-Path $lanRoot 'LAN-firewall-setup.log'
if ((Test-Path -LiteralPath $lanSetupLog) -and ((Get-Item -LiteralPath $lanSetupLog).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Linked setup log is not supported.' }
Start-Transcript -LiteralPath $lanSetupLog -Append | Out-Null
foreach ($lanSpec in $lanRuleSpecs) {
    $lanExisting = Get-NetFirewallRule -PolicyStore PersistentStore -Name $lanSpec.Name -ErrorAction SilentlyContinue
    if ($Remove) {
        if ($lanExisting) { $lanExisting | Remove-NetFirewallRule }
        continue
    }
    $lanParameters = @{
        Name=$lanSpec.Name;DisplayName=('Zompiercer LAN - Private ' + $lanSpec.Protocol);Group='ZompiercerLAN private network';
        Description='Only this isolated Zompiercer LAN worker, on a private wired/Wi-Fi local subnet.';
        Program=$lanExecutable;Package=$lanPackage;Direction='Inbound';Action='Allow';Enabled='True';Profile='Private';
        Protocol=$lanSpec.Protocol;LocalPort=$lanSpec.Ports;RemoteAddress='LocalSubnet';
        InterfaceType=@('Wired','Wireless');EdgeTraversalPolicy='Block';PolicyStore='PersistentStore'
    }
    if ($lanExisting) {
        $lanParameters.Remove('Group')
        $lanParameters.Remove('DisplayName')
        $lanParameters.NewDisplayName = 'Zompiercer LAN - Private ' + $lanSpec.Protocol
        Set-NetFirewallRule @lanParameters | Out-Null
    }
    else { New-NetFirewallRule @lanParameters | Out-Null }
}
if (-not $Remove) {
    foreach ($lanSpec in $lanRuleSpecs) {
        $lanRule = Get-NetFirewallRule -PolicyStore PersistentStore -Name $lanSpec.Name
        $lanApp = $lanRule | Get-NetFirewallApplicationFilter
        $lanPort = $lanRule | Get-NetFirewallPortFilter
        $lanAddress = $lanRule | Get-NetFirewallAddressFilter
        $lanInterface = $lanRule | Get-NetFirewallInterfaceTypeFilter
        Assert-LanFirewallRule -Rule $lanRule -App $lanApp -Port $lanPort -Address $lanAddress -Interface $lanInterface -Spec $lanSpec -Executable $lanExecutable -Package $lanPackage
    }
}
Write-Output $(if($Remove){'Zompiercer LAN rules removed.'}else{'Zompiercer LAN private-network rules configured and verified. Start the game normally.'})
Stop-Transcript | Out-Null
