<#
.SYNOPSIS
  Removes the SwissForge add-in registration.

.DESCRIPTION
  Unregisters the COM class and removes the add-in keys from every hive the
  installer writes to. Leaves your configuration file and outbox alone - those
  live under %APPDATA%\SwissForge and are yours, not the installer's.
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string] $DllPath
)

$ErrorActionPreference = 'Continue'

if (-not $DllPath) { $DllPath = Join-Path $PSScriptRoot 'SwissForge.AddIn.dll' }

$regasm = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\regasm.exe"
if ((Test-Path $regasm) -and (Test-Path $DllPath)) {
    if ($PSCmdlet.ShouldProcess($DllPath, "regasm /unregister")) {
        & $regasm $DllPath /unregister /nologo
    }
}

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

foreach ($hive in $hives) {
    $key = Join-Path $hive $progId
    if (Test-Path $key) {
        if ($PSCmdlet.ShouldProcess($key, "remove")) {
            Remove-Item -Path $key -Recurse -Force
            Write-Host "  removed $key"
        }
    }
}

Write-Host ""
Write-Host "  Uninstalled. Your settings in %APPDATA%\SwissForge were left in place."
Write-Host ""
