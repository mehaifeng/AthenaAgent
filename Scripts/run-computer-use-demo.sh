#!/usr/bin/env bash
set -euo pipefail

repo_root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
if [[ ! -d "$repo_root/artifacts/ptc/feed" ]]; then
    bash "$repo_root/Libraries/Ptc/scripts/build-local-feed.sh"
fi
if [[ ${1:-} == --app ]]; then
    exec dotnet run --project "$repo_root/Athena.UI.csproj"
fi
exec dotnet run --project "$repo_root/Athena.UI.csproj" -- --computer-use-demo
