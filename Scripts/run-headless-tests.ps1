# run-headless-tests.ps1 — Athena.UI.HeadlessTests 无头测试套件运行脚本
#
# 为什么不用 `dotnet run --project Athena.UI.HeadlessTests`：
#   1. 它会先全量构建整个解决方案（慢）；
#   2. 运行中的 Athena.UI 应用会锁住 bin\Debug\net10.0\Athena.UI.exe，导致构建失败（MSB3027）。
#
# 本脚本用 -p:UseAppHost=false 跳过 apphost/exe 生成（所有工程只出 DLL），
# 直接以 DLL 方式运行套件，并把套件的退出码（0=全部通过）透传给调用方。
$ErrorActionPreference = "Stop"
Set-Location (Join-Path $PSScriptRoot "..")

dotnet build Athena.UI.HeadlessTests -p:UseAppHost=false --nologo -v q
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$dll = "Athena.UI.HeadlessTests/bin/Debug/net10.0/Athena.UI.HeadlessTests.dll"
dotnet $dll
exit $LASTEXITCODE
