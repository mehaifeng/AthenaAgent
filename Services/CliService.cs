using Athena.UI.Services.Interfaces;
using CliWrap;
using Serilog;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Athena.UI.Services;

public class CliService : ICliService
{
    private readonly ILogger _logger = Log.ForContext<CliService>();

    // waitForExit=true 时的默认超时时间：多数命令几秒到几分钟内完成，5分钟足够覆盖常见场景（编译、格式化、简单安装等）。
    private const int DefaultTimeoutSeconds = 300;

    // 超时上限：即使调用方显式指定更长的 timeoutSeconds（如已知的大文件下载/长时间安装），也不能无限等待，
    // 避免因网络卡死、交互式提示（如 sudo 密码，见 npx playwright install chrome 的挂起问题）等原因导致进程被永久挂起且无任何反馈。
    private const int MaxTimeoutSeconds = 1800;

    public async Task<CliResult> ExecuteAsync(string command, IEnumerable<string> arguments, string? workingDirectory = null, IDictionary<string, string>? environmentVariables = null, bool waitForExit = true, int? timeoutSeconds = null, CancellationToken ct = default)
    {
        // 参数可能很长（如 TTS 的整段朗读文本），日志里截断，避免刷屏。
        var argsText = string.Join(" ", arguments);
        const int maxArgsLogLength = 120;
        var argsForLog = argsText.Length > maxArgsLogLength
            ? argsText.Substring(0, maxArgsLogLength) + $"…(+{argsText.Length - maxArgsLogLength} chars)"
            : argsText;
        _logger.Information("Executing command: {Command} {Arguments} (dir={Dir}, wait={Wait})",
            command, argsForLog, workingDirectory ?? "default", waitForExit);

        try
        {
            var cmd = Cli.Wrap(command)
                .WithArguments(arguments)
                .WithValidation(CommandResultValidation.None);

            if (!string.IsNullOrWhiteSpace(workingDirectory))
            {
                cmd = cmd.WithWorkingDirectory(workingDirectory);
            }

            if (environmentVariables != null)
            {
                // Convert IDictionary<string, string> to IReadOnlyDictionary<string, string?>
                var env = environmentVariables.ToDictionary(kvp => kvp.Key, kvp => (string?)kvp.Value);
                cmd = cmd.WithEnvironmentVariables(env);
            }

            if (!waitForExit)
            {
                // Fire and forget
                var task = cmd.ExecuteAsync(ct);
                return new CliResult
                {
                    ExitCode = 0,
                    ProcessId = task.ProcessId,
                    StandardOutput = $"Process started in background. PID: {task.ProcessId}"
                };
            }

            var effectiveTimeout = Math.Clamp(timeoutSeconds ?? DefaultTimeoutSeconds, 1, MaxTimeoutSeconds);
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(effectiveTimeout));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

            try
            {
                // 流式捕获原始字节而非直接取字符串：解码策略见 DecodeOutput。
                using var stdoutBuffer = new MemoryStream();
                using var stderrBuffer = new MemoryStream();
                var result = await cmd
                    .WithStandardOutputPipe(PipeTarget.ToStream(stdoutBuffer))
                    .WithStandardErrorPipe(PipeTarget.ToStream(stderrBuffer))
                    .ExecuteAsync(linkedCts.Token);

                return new CliResult
                {
                    ExitCode = result.ExitCode,
                    StandardOutput = DecodeOutput(stdoutBuffer.ToArray()),
                    StandardError = DecodeOutput(stderrBuffer.ToArray()),
                    RunTime = result.RunTime
                };
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                _logger.Warning("Command execution forcibly terminated due to timeout: {Command} {Arguments} (timeout={Timeout}s)", command, argsForLog, effectiveTimeout);
                return new CliResult
                {
                    ExitCode = -2,
                    StandardError = $"命令执行超时（超过 {effectiveTimeout} 秒未结束），进程已被强制终止。若这是已知的长时间任务（如大文件下载/安装），可通过 timeoutSeconds 参数延长超时（最大 {MaxTimeoutSeconds} 秒）；若命令需要交互式输入（如 sudo 密码），请改用无需交互确认的等效命令。"
                };
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to execute command: {Command}", command);
            return new CliResult
            {
                ExitCode = -1,
                StandardError = ex.Message
            };
        }
    }

    // 子进程输出的解码策略：Windows 上的 PowerShell/cmd 按系统 OEM 代码页（中文系统为 GBK/936）
    // 向管道写字节，而 .NET 默认按 UTF-8 解码，中文内容会全部乱码（字节恰好是合法 UTF-8 序列时
    // 显示成 Ŀ¼ 这类字符，否则显示为替换符 �）。因此先按严格 UTF-8 解码，字节流不合法
    // （说明来自本地代码页，如 git 的 UTF-8 内容永远是合法序列、不会误回退）时改用系统 OEM 代码页。
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static string DecodeOutput(byte[] bytes)
    {
        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            // 回退路径依赖代码页提供程序（Program.cs 已注册）；若未注册（如测试环境），
            // 退化为带替换符的 UTF-8，避免直接抛异常导致命令执行整体失败。
            try
            {
                return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage).GetString(bytes);
            }
            catch (ArgumentException)
            {
                return Encoding.UTF8.GetString(bytes);
            }
        }
    }
}
