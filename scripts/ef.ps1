#!/usr/bin/env pwsh
# Run dotnet-ef inside the SDK container so no local .NET SDK is required.
#
#   ./scripts/ef.ps1 migrations add Init
#   ./scripts/ef.ps1 database update
#   ./scripts/ef.ps1 migrations list

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot

$inner = @'
set -e
export PATH="$PATH:/cache/.dotnet/tools"
if ! command -v dotnet-ef >/dev/null 2>&1; then
  dotnet tool install --global dotnet-ef --version "10.*" >/dev/null
fi
# dotnet ef does not restore, and a fresh clone has no assets file for it to read.
dotnet restore src/Boh.Web/Boh.Web.csproj >/dev/null
dotnet ef "$@" --project src/Boh.Web/Boh.Web.csproj
'@

# Persistent, writable HOME for the NuGet cache and the dotnet-ef tool. A host directory
# rather than a named volume, which Docker creates owned by root.
$cacheDir =
    if ($env:XDG_CACHE_HOME)   { Join-Path $env:XDG_CACHE_HOME 'boh-ef' }
    elseif ($env:LOCALAPPDATA) { Join-Path $env:LOCALAPPDATA 'boh-ef' }
    else                       { Join-Path $HOME '.cache/boh-ef' }

New-Item -ItemType Directory -Force -Path $cacheDir | Out-Null

docker run --rm `
    -v "${repoRoot}:/src" `
    -w /src `
    -v "${cacheDir}:/cache" `
    -e HOME=/cache `
    -e NUGET_PACKAGES=/cache/packages `
    -e DOTNET_CLI_TELEMETRY_OPTOUT=1 `
    -e DOTNET_NOLOGO=1 `
    mcr.microsoft.com/dotnet/sdk:10.0 `
    bash -c $inner -- @args

if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
