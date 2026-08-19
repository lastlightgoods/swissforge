<#
.SYNOPSIS
  Builds SwissForge on Windows.

.DESCRIPTION
  Builds everything, including the .NET Framework 4.8 projects that only compile on
  Windows: the ESPRIT adapter, the add-in, and the probe.

  You need the .NET SDK 8 or later. Visual Studio is optional - if you have it, the
  solution opens normally, but nothing here requires it.

.PARAMETER Configuration
  Debug or Release. Defaults to Release.

.PARAMETER Output
  Where to stage the built artifacts. Defaults to .\dist

.EXAMPLE
  .\build.ps1
  .\build.ps1 -Configuration Debug
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug','Release')]
    [string] $Configuration = 'Release',
    [string] $Output = "$PSScriptRoot\dist"
)

$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot

try {
    Write-Host ""
    Write-Host "  Building SwissForge ($Configuration)"
    Write-Host ""

    # 1. Engine tests first. If the engines are wrong, nothing downstream is worth building.
    Write-Host "  [1/3] Verifying the engine layer"
    dotnet run --project tests\SwissForge.Core.Tests -c $Configuration --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "Engine tests failed. Stopping." }

    # 2. Everything, including the Windows-only projects.
    Write-Host ""
    Write-Host "  [2/3] Building the solution"
    dotnet build SwissForge.sln -c $Configuration --nologo -v minimal
    if ($LASTEXITCODE -ne 0) { throw "Build failed." }

    # 3. Stage
    Write-Host ""
    Write-Host "  [3/3] Staging to $Output"
    if (Test-Path $Output) { Remove-Item $Output -Recurse -Force }
    New-Item -ItemType Directory -Path $Output | Out-Null

    $addInBin = "src\SwissForge.AddIn\bin\$Configuration\net48"
    $probeBin = "src\SwissForge.Probe\bin\$Configuration\net48"
    $cliBin   = "src\SwissForge.Cli\bin\$Configuration\net8.0"

    foreach ($pair in @(
        @{ From = $addInBin; To = 'addin' },
        @{ From = $probeBin; To = 'probe' },
        @{ From = $cliBin;   To = 'cli'   }
    )) {
        if (Test-Path $pair.From) {
            $dest = Join-Path $Output $pair.To
            New-Item -ItemType Directory -Path $dest -Force | Out-Null
            Copy-Item "$($pair.From)\*" $dest -Recurse -Force
            Write-Host "    $($pair.To)"
        } else {
            Write-Host "    $($pair.To)  (not built - $($pair.From) missing)"
        }
    }

    Copy-Item 'src\SwissForge.AddIn\install\*' (Join-Path $Output 'addin') -Force -ErrorAction SilentlyContinue
    Copy-Item 'docs\*' $Output -Recurse -Force -ErrorAction SilentlyContinue
    Copy-Item 'samples\*' (Join-Path $Output 'samples') -Recurse -Force -ErrorAction SilentlyContinue

    Write-Host ""
    Write-Host "  Done. Artifacts in $Output"
    Write-Host ""
    Write-Host "  To install the add-in, from an ELEVATED PowerShell prompt:"
    Write-Host "    cd '$Output\addin'"
    Write-Host "    .\Install-SwissForge.ps1"
    Write-Host ""
    Write-Host "  To check what your ESPRIT actually exposes (ESPRIT running, document open):"
    Write-Host "    $Output\probe\swissforge-probe.exe --full"
    Write-Host ""
}
finally {
    Pop-Location
}
