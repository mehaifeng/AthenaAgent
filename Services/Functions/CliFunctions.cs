using Athena.UI.Services.Interfaces;
using Serilog;
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Athena.UI.Services.Functions;

/// <summary>
/// CLI 命令交互工具
/// </summary>
public class CliFunctions
{
    private readonly ICliService _cliService;
    private readonly ILogger _logger;

    public CliFunctions(ICliService cliService, ILogger logger)
    {
        _cliService = cliService;
        _logger = logger.ForContext<CliFunctions>();
    }

    /// <summary>
    /// 执行控制台命令并捕获输出
    /// </summary>
    public async Task<FunctionResult> ExecuteTerminalCommandAsync(string command, string? workingDirectory = null, bool waitForExit = true)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(command))
                return FunctionResult.FailureResult("错误: 必须提供 command 参数。");

            // 检测是否包含 shell 特殊操作符
            bool needsShell = Regex.IsMatch(command, @"[|&;<>]|^[~*?]");

            string shell;
            string[] shellArgs;

            if (OperatingSystem.IsWindows())
            {
                // Windows: cmd.exe 处理管道和重定向
                shell = "cmd";
                shellArgs = new[] { "/c", command };
            }
            else
            {
                // macOS/Linux: zsh 处理管道和重定向
                shell = "zsh";
                shellArgs = new[] { "-c", command };
            }

            var result = await _cliService.ExecuteAsync(shell, shellArgs, workingDirectory, null, waitForExit);

            if (result.IsSuccess)
            {
                var msg = waitForExit ? $"命令执行成功 (ExitCode: {result.ExitCode})" : $"进程已在后台启动 (PID: {result.ProcessId})";
                return FunctionResult.SuccessResult(msg, new
                {
                    stdout = result.StandardOutput,
                    stderr = result.StandardError,
                    exitCode = result.ExitCode,
                    runTime = result.RunTime.TotalSeconds + "s",
                    pid = result.ProcessId
                });
            }
            else
            {
                return new FunctionResult
                {
                    Success = false,
                    Message = $"命令执行失败 (ExitCode: {result.ExitCode})",
                    Data = new
                    {
                        stdout = result.StandardOutput,
                        stderr = result.StandardError
                    }
                };
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "CLI 工具执行异常");
            return FunctionResult.FailureResult($"执行过程中出现异常: {ex.Message}");
        }
    }
}
