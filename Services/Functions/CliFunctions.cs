using Athena.UI.Services.Interfaces;
using Serilog;
using System;
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
                var permissionError = BuildPermissionErrorResult(result);
                if (permissionError != null)
                    return permissionError;

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

    private FunctionResult? BuildPermissionErrorResult(CliResult result)
    {
        var stderr = result.StandardError ?? string.Empty;
        var stdout = result.StandardOutput ?? string.Empty;
        var combined = $"{stderr}\n{stdout}";

        if (!IsPermissionDenied(combined))
            return null;

        var platform = GetPlatformName();

        return new FunctionResult
        {
            Success = false,
            Message = $"命令执行失败 (ExitCode: {result.ExitCode})",
            Data = new
            {
                stdout = result.StandardOutput,
                stderr = result.StandardError,
                errorType = "filesystem_permission_denied",
                platform,
                suggestedAction = GetSuggestedAction(platform),
                details = "The operating system denied access to one or more files or directories. This is a permission restriction, not a command syntax error."
            }
        };
    }

    private static bool IsPermissionDenied(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        return text.Contains("Operation not permitted", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("Permission denied", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("Access is denied", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("UnauthorizedAccessException", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("EACCES", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("EPERM", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetPlatformName()
    {
        if (OperatingSystem.IsMacOS())
            return "macos";
        if (OperatingSystem.IsWindows())
            return "windows";
        if (OperatingSystem.IsLinux())
            return "linux";
        return "unknown";
    }

    private static string GetSuggestedAction(string platform)
    {
        return platform switch
        {
            "macos" => "Grant Athena access in System Settings > Privacy & Security, or grant Full Disk Access if the agent needs broad filesystem access.",
            "windows" => "Check the current user's filesystem permissions, Windows Security protections, or rerun the app with elevated privileges if required.",
            "linux" => "Check filesystem permissions for the current user. If the app is sandboxed by Flatpak or Snap, grant the required filesystem access there.",
            _ => "Check the app's filesystem permissions and retry."
        };
    }
}
