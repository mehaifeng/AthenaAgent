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

    public async Task<CliResult> ExecuteAsync(string command, IEnumerable<string> arguments, string? workingDirectory = null, IDictionary<string, string>? environmentVariables = null, bool waitForExit = true, CancellationToken ct = default)
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

            var result = await cmd.ExecuteBufferedAsync(ct);

            return new CliResult
            {
                ExitCode = result.ExitCode,
                StandardOutput = result.StandardOutput,
                StandardError = result.StandardError,
                RunTime = result.RunTime
            };
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
