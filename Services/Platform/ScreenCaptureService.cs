using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Athena.UI.Services.Interfaces;
using Serilog;

namespace Athena.UI.Services.Platform;

/// <summary>
/// 基于操作系统原生命令的交互式截图实现。结果统一写入系统剪贴板。
/// - macOS:  screencapture -i -c        （阻塞，自带框选 + 标注）
/// - Linux:  flameshot gui / gnome-screenshot -a -c / spectacle -r -b -c（首个可用者，阻塞）
/// - Windows: explorer ms-screenclip:   （系统截图工具，异步，结果进剪贴板）
/// </summary>
public class ScreenCaptureService : IScreenCaptureService
{
    private readonly ICliService _cliService;
    private readonly ILogger _logger;

    public ScreenCaptureService(ICliService cliService, ILogger logger)
    {
        _cliService = cliService;
        _logger = logger;
    }

    public bool IsSupported =>
        OperatingSystem.IsMacOS() || OperatingSystem.IsWindows() || OperatingSystem.IsLinux();

    public async Task<ScreenCaptureLaunchResult> LaunchInteractiveAsync(CancellationToken ct = default)
    {
        try
        {
            if (OperatingSystem.IsMacOS())
            {
                return await RunBlockingAsync("screencapture", new[] { "-i", "-c" }, ct);
            }

            if (OperatingSystem.IsWindows())
            {
                // ms-screenclip 由 explorer 触发后立即返回，截图结果稍后异步写入剪贴板。
                var result = await _cliService.ExecuteAsync(
                    "explorer.exe", new[] { "ms-screenclip:" }, waitForExit: false, ct: ct);
                return result.ExitCode == 0
                    ? ScreenCaptureLaunchResult.LaunchedAsync
                    : ScreenCaptureLaunchResult.Failed;
            }

            if (OperatingSystem.IsLinux())
            {
                // flameshot 标注能力最强，优先；否则退化到桌面环境自带工具。
                var candidates = new (string Cmd, string[] Args)[]
                {
                    ("flameshot", new[] { "gui" }),
                    ("gnome-screenshot", new[] { "-a", "-c" }),
                    ("spectacle", new[] { "-r", "-b", "-c" }),
                };

                foreach (var (cmd, args) in candidates)
                {
                    var launch = await RunBlockingAsync(cmd, args, ct);
                    if (launch != ScreenCaptureLaunchResult.Failed)
                    {
                        return launch;
                    }
                }

                _logger.Warning("未找到可用的 Linux 截图工具（flameshot/gnome-screenshot/spectacle）");
                return ScreenCaptureLaunchResult.Failed;
            }

            return ScreenCaptureLaunchResult.Unsupported;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "启动截图工具失败");
            return ScreenCaptureLaunchResult.Failed;
        }
    }

    private async Task<ScreenCaptureLaunchResult> RunBlockingAsync(string command, IEnumerable<string> args, CancellationToken ct)
    {
        var result = await _cliService.ExecuteAsync(command, args, waitForExit: true, ct: ct);

        // ExitCode -1 是 CliService 对"命令无法启动/不存在"的约定返回；据此判定该工具不可用。
        if (result.ExitCode == -1)
        {
            _logger.Debug("截图命令不可用: {Command} ({Error})", command, result.StandardError);
            return ScreenCaptureLaunchResult.Failed;
        }

        return ScreenCaptureLaunchResult.CompletedBlocking;
    }
}
