using Athena.UI.Services.Interfaces;
using Athena.UI.Services.SubAgents;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Athena.UI.Services.Functions;

/// <summary>
/// CLI 命令交互工具
/// </summary>
public class CliFunctions
{
    private readonly ICliService _cliService;
    private readonly ILogger _logger;

    // 禁止在终端中使用的文件系统操作命令（AI 应优先使用现有的文件系统工具）
    private static readonly string[] RestrictedCommands = { "mkdir", "rm", "rmdir", "ls", "dir", "cp", "mv", "copy", "move", "del", "erase" };

    public CliFunctions(ICliService cliService, ILogger logger)
    {
        _cliService = cliService;
        _logger = logger.ForContext<CliFunctions>();
    }

    /// <summary>
    /// 执行控制台命令并捕获输出
    /// </summary>
    public async Task<FunctionResult> ExecuteTerminalCommandAsync(string command, List<string>? arguments = null, string? workingDirectory = null, bool waitForExit = true, int? timeoutSeconds = null)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(command))
                return FunctionResult.FailureResult("Error: the 'command' parameter is required.");

            // 检查是否为简单的文件系统操作
            if (RestrictedCommands.Contains(command.ToLower().Trim()))
            {
                return FunctionResult.FailureResult($"为了安全性与一致性，请使用现有的文件系统操作工具（如 list_system_directory, create_directory, delete_system_file 等）来完成此类任务，而不是直接调用终端命令 '{command}'。");
            }

            // 读取主对话循环经 AsyncLocal 透传的取消令牌，使用户点"停止"能中断终端命令的等待。
            var cancellationToken = ToolExecutionContext.CurrentCancellationToken;

            var args = arguments ?? new List<string>();
            var result = await _cliService.ExecuteAsync(command, args, workingDirectory, null, waitForExit, timeoutSeconds, cancellationToken);

            if (result.IsSuccess)
            {
                var msg = waitForExit ? $"命令执行成功 (ExitCode: {result.ExitCode})" : $"进程已在后台启动 (PID: {result.ProcessId})";
                return FunctionResult.SuccessResult(msg, new
                {
                    stdout = result.StandardOutput,
                    stderr = result.StandardError,
                    runTime = result.RunTime.TotalSeconds + "s",
                    pid = result.ProcessId
                });
            }
            else
            {
                var msg = result.ExitCode == -2
                    ? "命令执行超时被终止"
                    : $"命令执行失败 (ExitCode: {result.ExitCode})";
                return new FunctionResult
                {
                    Success = false,
                    Message = msg,
                    Data = new
                    {
                        stdout = result.StandardOutput,
                        stderr = result.StandardError
                    }
                };
            }
        }
        catch (OperationCanceledException)
        {
            _logger.Information("Terminal command execution cancelled by user stop request.");
            return FunctionResult.FailureResult("Terminal command execution was cancelled by the user.");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "CLI tool execution exception");
            return FunctionResult.FailureResult($"执行过程中出现异常: {ex.Message}");
        }
    }
}
