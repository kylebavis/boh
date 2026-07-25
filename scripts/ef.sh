#!/usr/bin/env bash
# Run dotnet-ef inside the SDK container so no local .NET SDK is required.
#
#   ./scripts/ef.sh migrations add Init
#   ./scripts/ef.sh database update
#   ./scripts/ef.sh migrations list
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

# The SDK needs a writable HOME for the NuGet cache and the dotnet-ef tool, and it has to
# persist or every run re-downloads. A host directory rather than a named volume: Docker
# creates a named volume owned by root, and this container runs as the invoking user so the
# files it generates are not root-owned.
CACHE_DIR="${XDG_CACHE_HOME:-$HOME/.cache}/boh-ef"
mkdir -p "$CACHE_DIR"

docker run --rm \
  -u "$(id -u):$(id -g)" \
  -v "$REPO_ROOT:/src" \
  -v "$CACHE_DIR:/cache" \
  -w /src \
  -e HOME=/cache \
  -e NUGET_PACKAGES=/cache/packages \
  -e DOTNET_CLI_TELEMETRY_OPTOUT=1 \
  -e DOTNET_NOLOGO=1 \
  mcr.microsoft.com/dotnet/sdk:10.0 \
  bash -c '
    set -e
    export PATH="$PATH:/cache/.dotnet/tools"
    if ! command -v dotnet-ef >/dev/null 2>&1; then
      dotnet tool install --global dotnet-ef --version "10.*" >/dev/null
    fi
    # dotnet ef does not restore, and a fresh clone has no assets file for it to read.
    dotnet restore src/Boh.Web/Boh.Web.csproj >/dev/null
    dotnet ef "$@" --project src/Boh.Web/Boh.Web.csproj
  ' -- "$@"
