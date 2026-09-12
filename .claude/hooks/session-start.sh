#!/usr/bin/env bash
# SessionStart hook — 让 Claude Code on the web 的远程容器开箱就能 build / 跑测试。
#
# 容器里唯一缺的东西是 .NET 10 SDK。装它有一个不显然的约束：
# **不要用官方的 dotnet-install.sh**。dot.net/v1/dotnet-install.sh 会重定向到
# builds.dotnet.microsoft.com，而它连同 aka.ms、download.visualstudio.microsoft.com、
# dotnetcli.azureedge.net 在这个环境的 egress 策略下一律返回 403 CONNECT。
# 能用的渠道是 Ubuntu 自己的仓库：Canonical 把 .NET 收进了官方 archive，
# noble-updates/main 里就有 dotnet-sdk-10.0（实测 10.0.112）。
#
# 本地开发机不需要这些（各人自己装了 SDK），所以整段只在远程跑。
set -euo pipefail

[ "${CLAUDE_CODE_REMOTE:-}" = "true" ] || exit 0

log() { echo "[session-start] $*"; }
die() { echo "[session-start] ERROR: $*" >&2; exit 1; }

PROJECT_DIR="${CLAUDE_PROJECT_DIR:-$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)}"

# ── 会话环境变量 ─────────────────────────────────────────────────────────────
# 每次 dotnet 调用都少一段 telemetry/logo 噪音；写进 CLAUDE_ENV_FILE 让整个会话继承。
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
if [ -n "${CLAUDE_ENV_FILE:-}" ] && ! grep -q 'DOTNET_CLI_TELEMETRY_OPTOUT' "$CLAUDE_ENV_FILE" 2>/dev/null; then
    {
        echo 'export DOTNET_CLI_TELEMETRY_OPTOUT=1'
        echo 'export DOTNET_NOLOGO=1'
    } >> "$CLAUDE_ENV_FILE"
fi

SUDO=""
if [ "$(id -u)" -ne 0 ]; then
    command -v sudo >/dev/null 2>&1 || die "not root and no sudo; cannot install the SDK"
    SUDO="sudo"
fi

have_sdk10() {
    command -v dotnet >/dev/null 2>&1 && dotnet --list-sdks 2>/dev/null | grep -q '^10\.'
}

if have_sdk10; then
    log "dotnet 10 SDK already present ($(dotnet --version)); skipping install"
else
    log "installing dotnet-sdk-10.0 from the Ubuntu archive ..."
    # apt-get update 的退出码刻意不当成门槛：容器预置了 deadsnakes / ondrej 两个 PPA 源，
    # 它们在 egress 策略下是 403，而 apt 把单个源失败降级为警告并仍然返回 0。
    # 也就是说 update 的退出码既不能证明成功、也不代表失败——真正的门槛是下面的 install
    # 和它后面的 have_sdk10 复查。archive.ubuntu.com 走 80 端口直连（不经代理），是通的。
    $SUDO apt-get update || log "WARNING: apt-get update reported a problem; continuing to install anyway"
    $SUDO env DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends dotnet-sdk-10.0 \
        || die "installing dotnet-sdk-10.0 failed; check that archive.ubuntu.com is reachable (do NOT switch to dotnet-install.sh, see the comment at the top)"
    have_sdk10 || die "dotnet 10 SDK still not on PATH after a successful install"
    log "installed dotnet $(dotnet --version)"
fi

# ── 预热 NuGet ───────────────────────────────────────────────────────────────
# restore 是唯一需要网络的构建步骤，也是最容易失败的一步；在这里做掉，容器状态被缓存后
# 后续会话的 build 与两个测试套件就完全离线。build 故意不做：它不需要网络，agent 自己会跑，
# 放进 hook 只是把每次会话启动多拖一两分钟。
if [ -f "$PROJECT_DIR/Athena.UI.sln" ]; then
    log "restoring NuGet packages for Athena.UI.sln ..."
    (cd "$PROJECT_DIR" && dotnet restore Athena.UI.sln) \
        || die "dotnet restore failed; the build and both test suites will not work"
    log "restore complete"
else
    log "WARNING: Athena.UI.sln not found under $PROJECT_DIR; skipped restore"
fi

log "ready — dotnet build and Scripts/run-headless-tests.sh should both work now"
