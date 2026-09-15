# run-headless-tests.ps1 — Athena.UI.HeadlessTests 无头测试套件运行脚本
#
# 为什么不用 `dotnet run --project Athena.UI.HeadlessTests`：
#   1. 它会先全量构建整个解决方案（慢）；
#   2. 运行中的 Athena.UI 应用会锁住 bin\Debug\net10.0\Athena.UI.exe，导致构建失败（MSB3027）。
#
# 本脚本用 -p:UseAppHost=false 跳过 apphost/exe 生成（所有工程只出 DLL），
# 直接以 DLL 方式运行套件，并把套件的退出码（0=全部通过）透传给调用方。
#
# 只接受显式的截图和构建输出参数。-BuildOutputPath 用于应用正在运行并锁定默认
# bin 目录时的隔离构建。任何其它参数仍会在 PowerShell 参数绑定阶段直接报错。
param(
    [string]$OutputPath = "",
    [string]$BuildOutputPath = ""
)

$ErrorActionPreference = "Stop"
Set-Location (Join-Path $PSScriptRoot "..")

if ($BuildOutputPath) {
    $resolvedBuildOutput = [IO.Path]::GetFullPath((Join-Path (Get-Location) $BuildOutputPath))
    New-Item -ItemType Directory -Path $resolvedBuildOutput -Force | Out-Null
    dotnet build Athena.UI.HeadlessTests -p:UseAppHost=false "-p:OutputPath=$resolvedBuildOutput" --nologo -v q
    $dll = Join-Path $resolvedBuildOutput "Athena.UI.HeadlessTests.dll"
} else {
    dotnet build Athena.UI.HeadlessTests -p:UseAppHost=false --nologo -v q
    $dll = "Athena.UI.HeadlessTests/bin/Debug/net10.0/Athena.UI.HeadlessTests.dll"
}
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

if ($OutputPath) {
    dotnet $dll $OutputPath
} else {
    dotnet $dll
}
exit $LASTEXITCODE
