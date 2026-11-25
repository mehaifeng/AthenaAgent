#!/usr/bin/env bash
set -euo pipefail

repo_root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
sdk_version=0.58.1
sdk_dir="$repo_root/artifacts/cua-driver/$sdk_version"
driver_path=${ATHENA_CUA_DRIVER_LIBRARY:-}

if [[ -z $driver_path ]]; then
    if [[ $(uname -s) != Linux || $(uname -m) != x86_64 ]]; then
        printf 'Set ATHENA_CUA_DRIVER_LIBRARY to the native Cua Driver SDK for this platform.\n' >&2
        exit 1
    fi
    driver_path="$sdk_dir/libcua_driver_sdk.so"
    if [[ ! -f $driver_path ]]; then
        mkdir -p "$sdk_dir"
        scratch=$(mktemp -d "$sdk_dir/.download.XXXXXX")
        trap 'rm -rf -- "$scratch"' EXIT
        wheel_url=https://files.pythonhosted.org/packages/2b/b3/c195e9fc8b588a9d04e8c7f51862977ee5856057adbb1db34be1a3d3333b/cua_driver-0.58.1-py3-none-manylinux_2_31_x86_64.whl
        digest=52c3b28a9cf4dce24dd2a05ccd0d51d950bfb8d76703568a47385827367f40fb
        curl --fail --silent --show-error --location --proto '=https' --max-time 90 "$wheel_url" --output "$scratch/driver.whl"
        printf '%s  %s\n' "$digest" "$scratch/driver.whl" | sha256sum --check --status
        unzip -p "$scratch/driver.whl" cua_driver/libcua_driver_sdk.so > "$scratch/libcua_driver_sdk.so"
        unzip -p "$scratch/driver.whl" "cua_driver-$sdk_version.dist-info/licenses/LICENSE" > "$sdk_dir/LICENSE"
        mv -f -- "$scratch/libcua_driver_sdk.so" "$driver_path"
        rm -rf -- "$scratch"
        trap - EXIT
    fi
fi

driver_path=$(realpath "$driver_path")
if [[ ! -f $driver_path ]]; then
    printf 'Cua Driver SDK not found at %s\n' "$driver_path" >&2
    exit 1
fi

if [[ ! -d "$repo_root/artifacts/ptc/feed" ]]; then
    bash "$repo_root/Libraries/Ptc/scripts/build-local-feed.sh"
fi
output_dir="$sdk_dir/athena"
dotnet build "$repo_root/Athena.UI.csproj" -o "$output_dir" -p:UseAppHost=false -v:q

if [[ ${1:-} == --app ]]; then
    exec env ATHENA_CUA_DRIVER_LIBRARY="$driver_path" dotnet "$output_dir/Athena.UI.dll"
fi
exec env ATHENA_CUA_DRIVER_LIBRARY="$driver_path" dotnet "$output_dir/Athena.UI.dll" --computer-use-demo
