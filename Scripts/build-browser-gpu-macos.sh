#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."

if [[ "$(uname -s)" != Darwin ]]; then
  echo 'The IOSurface preview prototype builds on macOS.' >&2
  exit 1
fi

gpu_output=${1:-obj/browser-gpu}
node_headers=${ATHENA_NODE_HEADERS:-$(node -p 'require("node:path").resolve(process.execPath, "../../include/node")')}
if [[ ! -f "$node_headers/node_api.h" ]]; then
  echo 'Set ATHENA_NODE_HEADERS to the include/node directory of a Node.js installation.' >&2
  exit 1
fi

mkdir -p "$gpu_output"
source_file=Services/ComputerUse/MacBrowserSurface.m
xcrun clang -O2 -Wall -Wextra -Werror -fobjc-arc -dynamiclib "$source_file" -framework IOSurface -framework CoreFoundation -framework Metal \
  -lbsm -o "$gpu_output/libathena-browser-surface.dylib"
xcrun clang -O2 -Wall -Wextra -Werror -bundle -undefined dynamic_lookup -DATHENA_ELECTRON -I "$node_headers" \
  "$source_file" -framework IOSurface -o "$gpu_output/athena-browser-surface.node"
