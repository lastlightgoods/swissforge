<#
.SYNOPSIS
  Registers the SwissForge add-in with ESPRIT.

.DESCRIPTION
  Two things have to happen for ESPRIT to load an add-in:

    1. The assembly must be registered with COM, so the CLSID resolves.
       That is regasm's job, and it needs administrator rights.

    2. ESPRIT must be told the add-in exists, via a registry key under the
       vendor's AddIns hive with LoadBehavior set to 1.

  The AddIns key moved when DP Technology became part of Hexagon, and the exact
  path varies by version. This script writes every known candidate rather than
  guessing: extra keys under a hive your version does not read are inert, and
  that is a far cheaper failure than the add-in silently never loading.

.PARAMETER DllPath
  Path to SwissForge.AddIn.dll. Defaults to the folder this script sits in.

.PARAMETER WhatIf
  Show what would be written without changing anything.

.EXAMPLE
  # From an elevated PowerShell prompt:
  .\Install-SwissForge.ps1 -DllPath 'C:\SwissForge\SwissForge.AddIn.dll'
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string] $DllPath
)

$ErrorActionPreference = 'Stop'

function Assert-Admin {
    $identity  = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw "This script must run from an elevated PowerShell prompt. COM registration writes to HKLM."
    }
}

function Get-Regasm {
    # The 64-bit regasm, because ESPRIT is a 64-bit host and registering with the
    # 32-bit one puts the CLSID in a hive ESPRIT will never look in.
    $candidates = @(
        "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\regasm.exe",
        "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319\regasm.exe"
    )
    foreach ($c in $candidates) { if (Test-Path $c) { return $c } }
    throw "regasm.exe not found. The .NET Framework 4.x developer tools are required."
}

Assert-Admin

if (-not $DllPath) {
    $DllPath = Join-Path $PSScriptRoot 'SwissForge.AddIn.dll'
}
$DllPath = (Resolve-Path $DllPath).Path

if (-not (Test-Path $DllPath)) { throw "Not found: $DllPath" }

Write-Host ""
Write-Host "  SwissForge add-in installer"
Write-Host "  Assembly: $DllPath"
Write-Host ""

# ---------------------------------------------------------------- 1. COM registration
$regasm = Get-Regasm
Write-Host "  Registering with COM using $regasm"

if ($PSCmdlet.ShouldProcess($DllPath, "regasm /codebase")) {
    # /codebase records the full path, so the DLL does not have to live in the GAC.
    & $regasm $DllPath /codebase /nologo
    if ($LASTEXITCODE -ne 0) { throw "regasm failed with exit code $LASTEXITCODE." }
    Write-Host "    ok"
}

# ---------------------------------------------------------------- 2. tell ESPRIT
$progId = 'SwissForge.AddIn'

$hives = @(
    'HKLM:\SOFTWARE\D.P.Technology\ESPRIT\AddIns',
    'HKLM:\SOFTWARE\WOW6432Node\D.P.Technology\ESPRIT\AddIns',
    'HKLM:\SOFTWARE\Hexagon\ESPRIT\AddIns',
    'HKLM:\SOFTWARE\Hexagon\ESPRIT EDGE\AddIns',
    'HKCU:\SOFTWARE\D.P.Technology\ESPRIT\AddIns',
    'HKCU:\SOFTWARE\Hexagon\ESPRIT\AddIns',
    'HKCU:\SOFTWARE\Hexagon\ESPRIT EDGE\AddIns'
)

Write-Host ""
Write-Host "  Writing add-in registration keys"

foreach ($hive in $hives) {
    $key = Join-Path $hive $progId
    if ($PSCmdlet.ShouldProcess($key, "create add-in key")) {
        try {
            New-Item -Path $key -Force | Out-Null
            New-ItemProperty -Path $key -Name 'FriendlyName' -Value 'SwissForge' -PropertyType String -Force | Out-Null
            New-ItemProperty -Path $key -Name 'Description'  -Value 'Cutting data, multi-channel cycle time, quoting, and NC program auditing for Swiss-type work.' -PropertyType String -Force | Out-Null
            New-ItemProperty -Path $key -Name 'LoadBehavior' -Value 1 -PropertyType DWord -Force | Out-Null
            Write-Host "    ok      $key"
        }
        catch {
            Write-Host "    skipped $key  ($($_.Exception.Message))"
        }
    }
}

Write-Host ""
Write-Host "  Done."
Write-Host ""
Write-Host "  Next:"
Write-Host "    1. Restart ESPRIT."
Write-Host "    2. Look for SwissForge in the add-in manager."
Write-Host "    3. If it does not appear, run swissforge-probe.exe and check the output."
Write-Host ""
Write-Host "  A note on LoadBehavior: 1 means 'load on demand'. If your version only"
Write-Host "  honours 3 ('load at startup'), change the value and restart. The add-in"
Write-Host "  is cheap to start either way."
Write-Host ""
