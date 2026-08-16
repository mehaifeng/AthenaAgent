using Athena.UI.Services.Interfaces;
using Athena.UI.Services.SubAgents;
using Serilog;
using System;
using System.Collections.Generic;
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

    // 有一等替代工具的终端命令 → 指名道姓地改派过去。
    //
    // 此前这份名单只挡"改"和"列"（mkdir/rm/cp/ls…），却放行 cat/grep 这类"读"和"搜"，
    // 而工具描述又写着 "Use dedicated file tools for ordinary file reads"——指导和实际互相打架，
    // 模型只能靠试错找边界。现在按能力对齐：凡是有专用工具的都挡，并直接给出替代工具名，
    // 把一次死胡同变成一次改派，省掉一个来回。
    //
    // 只匹配顶层可执行名。`git grep`（command=git）、`bash -c "..."`（command=bash）不受影响，
    // 组合命令与管道仍走终端——挡的是"本可以用结构化工具做得更好"的那一类。
    private static readonly Dictionary<string, string> CommandReplacements = new(StringComparer.OrdinalIgnoreCase)
    {
        ["mkdir"] = "create_directory",
        ["rm"] = "delete_system_file",
        ["rmdir"] = "delete_system_file",
        ["del"] = "delete_system_file",
        ["erase"] = "delete_system_file",
        ["ls"] = "list_system_directory",
        ["dir"] = "list_system_directory",
        ["cp"] = "copy_system_file",
        ["copy"] = "copy_system_file",
        ["mv"] = "move_system_file",
        ["move"] = "move_system_file",
        // 读：read_system_file 带行号前缀、分块分页与安全策略，比裸 cat 更省上下文也更可控。
        ["cat"] = "read_system_file",
        ["type"] = "read_system_file",
        // 分页器会等交互输入，在无 TTY 的进程里必定挂到超时。
        ["less"] = "read_system_file",
        ["more"] = "read_system_file",
        // 搜：结果按文件聚合、带上下文与真实命中计数，且自动跳过构建产物与二进制文件。
        ["grep"] = "search_in_directory (or search_in_file for one file)",
        ["egrep"] = "search_in_directory (or search_in_file for one file)",
        ["fgrep"] = "search_in_directory (or search_in_file for one file)",
        ["findstr"] = "search_in_directory (or search_in_file for one file)",
        ["rg"] = "search_in_directory (or search_in_file for one file)"
    };

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

            // 有一等替代工具的命令：直接点名改派，不要让模型自己猜该用哪个工具。
            if (CommandReplacements.TryGetValue(command.Trim(), out var replacement))
            {
                return FunctionResult.FailureResult(
                    $"'{command}' 有专用工具，请改用 {replacement}——它带安全策略、结果分页与上下文预算，"
                    + $"比裸终端输出更省上下文也更可控。若确实需要 shell 组合（管道、重定向、多命令），"
                    + $"请把整条命令交给 shell 执行（如 command=\"zsh\"、arguments=[\"-c\", \"...\"]）。");
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
