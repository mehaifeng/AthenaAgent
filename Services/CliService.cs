using Athena.UI.Services.Interfaces;
using CliWrap;
using CliWrap.Buffered;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
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
        _logger.Information("执行命令: {Command} {Arguments} (dir={Dir}, wait={Wait})",
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
                var result = await cmd.ExecuteBufferedAsync(linkedCts.Token);

                return new CliResult
                {
                    ExitCode = result.ExitCode,
                    StandardOutput = result.StandardOutput,
                    StandardError = result.StandardError,
                    RunTime = result.RunTime
                };
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                _logger.Warning("命令执行超时被强制终止: {Command} {Arguments} (timeout={Timeout}s)", command, argsForLog, effectiveTimeout);
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
}
