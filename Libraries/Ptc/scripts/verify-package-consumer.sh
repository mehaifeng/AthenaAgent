#!/usr/bin/env bash
set -euo pipefail
python3 "$(dirname "$0")/verify_package_consumer.py" "$@"
