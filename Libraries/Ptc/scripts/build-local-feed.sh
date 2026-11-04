#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "$0")/../../.." && pwd)"
cd "$root"
dotnet restore Libraries/Ptc/Ptc.slnx --locked-mode --nologo
dotnet pack Libraries/Ptc/src/Ptc.Core/Ptc.Core.csproj -c Release --no-restore -p:Version=0.1.0-preview.1 -o artifacts/ptc/feed --nologo
dotnet pack Libraries/Ptc/src/Ptc.JavaScript/Ptc.JavaScript.csproj -c Release --no-restore -p:Version=0.1.0-preview.1 -o artifacts/ptc/feed --nologo
