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
    private readonly IConfigService _configService;
    private readonly ILogger _logger;

    // 禁止在终端中使用的文件系统操作命令（AI 应优先使用现有的文件系统工具）
    private static readonly string[] RestrictedCommands = { "mkdir", "rm", "rmdir", "ls", "dir", "cp", "mv", "copy", "move", "del", "erase" };

    public CliFunctions(ICliService cliService, IConfigService configService, ILogger logger)
    {
        _cliService = cliService;
        _configService = configService;
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
                var maxChars = _configService.Load().MaxTerminalOutputChars;
                var stdout = TerminalOutputTruncator.Process(result.StandardOutput, maxChars);
                var stderr = TerminalOutputTruncator.Process(result.StandardError, maxChars);
                var msg = waitForExit ? $"命令执行成功 (ExitCode: {result.ExitCode})" : $"进程已在后台启动 (PID: {result.ProcessId})";
                msg += TruncationNote(stdout.OmittedChars + stderr.OmittedChars, stdout.CollapsedLines + stderr.CollapsedLines);
                return FunctionResult.SuccessResult(msg, new
                {
                    stdout = stdout.Text,
                    stderr = stderr.Text,
                    stdoutOmitted = stdout.OmittedChars,
                    stderrOmitted = stderr.OmittedChars,
                    stdoutCollapsedLines = stdout.CollapsedLines,
                    stderrCollapsedLines = stderr.CollapsedLines,
                    runTime = result.RunTime.TotalSeconds + "s",
                    pid = result.ProcessId
                });
            }
            else
            {
                var msg = result.ExitCode == -2
                    ? "命令执行超时被终止"
                    : $"命令执行失败 (ExitCode: {result.ExitCode})";
                var maxChars = _configService.Load().MaxTerminalOutputChars;
                var stdout = TerminalOutputTruncator.Process(result.StandardOutput, maxChars);
                var stderr = TerminalOutputTruncator.Process(result.StandardError, maxChars);
                msg += TruncationNote(stdout.OmittedChars + stderr.OmittedChars, stdout.CollapsedLines + stderr.CollapsedLines);
                return new FunctionResult
                {
                    Success = false,
                    Message = msg,
                    Data = new
                    {
                        stdout = stdout.Text,
                        stderr = stderr.Text,
                        stdoutOmitted = stdout.OmittedChars,
                        stderrOmitted = stderr.OmittedChars,
                        stdoutCollapsedLines = stdout.CollapsedLines,
                        stderrCollapsedLines = stderr.CollapsedLines
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

    /// <summary>
    /// 输出被智能压缩（省略字符或折叠重复行）时，在消息里显式标注，让模型知道
    /// 看到的是不完整摘要、应缩小搜索范围重试而不是盲目重复同一命令。
    /// </summary>
    private static string TruncationNote(int omittedChars, int collapsedLines) =>
        omittedChars > 0 || collapsedLines > 0
            ? $" 输出过大，已智能压缩（省略 {omittedChars} 字符、折叠 {collapsedLines} 行重复内容）。如需完整信息，请缩小搜索范围后重试。"
            : string.Empty;
}
