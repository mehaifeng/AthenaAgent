#!/usr/bin/env bash
# run-headless-tests.sh — Athena.UI.HeadlessTests 无头测试套件运行脚本（Git Bash / CI 用）。
# 不用 `dotnet run`：全量构建慢，且运行中的 Athena.UI 会锁 Athena.UI.exe 导致构建失败。
# -p:UseAppHost=false 跳过 apphost 生成（只出 DLL），直接跑套件，退出码透传。
#
# 只接受一个可选位置参数：截图输出路径（原样转发给套件，对应 ps1 的 -OutputPath）。
# 多余参数或旗标（如 --nologo）直接报错，绝不静默透传——否则截图会洒进当前工作目录。
set -euo pipefail
cd "$(dirname "$0")/.."

if [ "$#" -gt 1 ] || { [ "$#" -eq 1 ] && [[ "$1" == -* ]]; }; then
    echo "Usage: $0 [OutputPath]" >&2
    exit 2
fi

dotnet build Athena.UI.HeadlessTests -p:UseAppHost=false --nologo -v q

dotnet Athena.UI.HeadlessTests/bin/Debug/net10.0/Athena.UI.HeadlessTests.dll "$@"
