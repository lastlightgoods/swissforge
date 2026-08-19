#!/usr/bin/env bash
# Builds and runs the SwissForge engine tests.
# Works with no NuGet feed and no .NET Framework reference assemblies, so it runs
# anywhere the .NET 8 SDK is installed - Windows, macOS, Linux, or a CI container.
set -euo pipefail
cd "$(dirname "$0")"
exec dotnet run --project tests/SwissForge.Core.Tests -p:SwissForgeVerifyOnly=true --nologo -v q "$@"
