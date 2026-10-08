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
  mcr.microsoft.com/dotnet/sdk:11.0 \
  bash -c '
    set -e
    export PATH="$PATH:/cache/.dotnet/tools"
    # The cache outlives SDK upgrades, so match the tool to the EF Core major.
    if ! dotnet tool list --global | grep -qE "^dotnet-ef +11."; then
      dotnet tool update --global dotnet-ef --version "11.*" >/dev/null
    fi
    # dotnet ef does not restore, and a fresh clone has no assets file for it to read.
    dotnet restore src/Boh.Web/Boh.Web.csproj >/dev/null
    # The project comes from .config/dotnet-ef.json.
    dotnet ef "$@"
  ' -- "$@"
