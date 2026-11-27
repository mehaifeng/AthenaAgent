#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."

gpu_output=${1:-obj/browser-gpu}
node_headers=${ATHENA_NODE_HEADERS:-$(node -p 'require("node:path").resolve(process.execPath, "../../include/node")')}
if [[ ! -f "$node_headers/node_api.h" ]]; then
    echo 'Set ATHENA_NODE_HEADERS to the directory containing node_api.h.' >&2
    exit 1
fi

mkdir -p "$gpu_output"
source_file=Services/ComputerUse/LinuxBrowserSurface.c
gpu_cflags=$(pkg-config --cflags egl glesv2 libdrm)
gpu_libs=$(pkg-config --libs egl glesv2)
cc -std=c11 -O2 -fPIC -shared -Wall -Wextra -Werror -DATHENA_ELECTRON -I "$node_headers" \
    "$source_file" -o "$gpu_output/athena-browser-surface.node"
cc -std=c11 -O2 -fPIC -shared -Wall -Wextra -Werror -Wl,-z,defs $gpu_cflags "$source_file" \
    $gpu_libs -o "$gpu_output/libathena-browser-surface.so"
