using System;
using System.Collections.Generic;
using System.Diagnostics;
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
                return await LaunchWindowsAsync(ct);
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

    /// <summary>ms-screenclip 触发后承载框选覆盖层的宿主进程候选名（不含 .exe）。</summary>
    /// <remarks>Win10 为 ScreenClippingHost；Win11 合并 Snipping Tool 后可能是 SnippingTool/ScreenSketch。</remarks>
    private static readonly string[] WindowsOverlayProcessNames =
        { "ScreenClippingHost", "SnippingTool", "ScreenSketch" };

    /// <summary>
    /// Windows: 触发 ms-screenclip 后监听其覆盖层宿主进程，进程退出即代表用户完成或取消截图，
    /// 从而把"完成时机未知的异步"转化为"阻塞到用户结束"，调用方可立即还原窗口、短轮询读取剪贴板。
    /// 若覆盖层进程始终未出现（不同 Windows 版本差异），回退为异步语义由调用方长轮询兜底。
    /// </summary>
    private async Task<ScreenCaptureLaunchResult> LaunchWindowsAsync(CancellationToken ct)
    {
        // 记录触发前已存在的同名进程，避免误监听到截图无关的残留进程。
        var beforePids = SnapshotOverlayPids();

        // ms-screenclip 由 explorer 触发后立即返回，覆盖层进程稍后异步拉起。
        var result = await _cliService.ExecuteAsync(
            "explorer.exe", new[] { "ms-screenclip:" }, waitForExit: false, ct: ct);
        if (result.ExitCode != 0)
        {
            return ScreenCaptureLaunchResult.Failed;
        }

        // 轮询等待新出现的覆盖层进程（覆盖层异步拉起，Win11 Snipping Tool 冷启动较慢，给约 5 秒）。
        Process? overlay = null;
        for (var i = 0; i < 50 && overlay == null; i++)
        {
            ct.ThrowIfCancellationRequested();
            overlay = FindNewOverlayProcess(beforePids);
            if (overlay == null)
            {
                await Task.Delay(100, ct);
            }
        }

        if (overlay == null)
        {
            // 未捕获到覆盖层进程：回退到异步语义，由调用方长轮询剪贴板兜底。
            _logger.Debug("未捕获到 ms-screenclip 覆盖层进程，回退为异步轮询模式");
            return ScreenCaptureLaunchResult.LaunchedAsync;
        }

        try
        {
            // 监听仅持续到覆盖层进程退出（完成或取消）；带安全超时防止极端情况下永不退出。
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromMinutes(5));
            await overlay.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.Warning("等待截图覆盖层进程退出超时");
        }
        finally
        {
            overlay.Dispose();
        }

        return ScreenCaptureLaunchResult.CompletedBlocking;
    }

    private static HashSet<int> SnapshotOverlayPids()
    {
        var pids = new HashSet<int>();
        foreach (var name in WindowsOverlayProcessNames)
        {
            foreach (var p in Process.GetProcessesByName(name))
            {
                pids.Add(p.Id);
                p.Dispose();
            }
        }
        return pids;
    }

    private static Process? FindNewOverlayProcess(HashSet<int> beforePids)
    {
        foreach (var name in WindowsOverlayProcessNames)
        {
            var processes = Process.GetProcessesByName(name);
            Process? match = null;
            foreach (var p in processes)
            {
                if (match == null && !beforePids.Contains(p.Id))
                {
                    match = p; // 命中后保留，其余释放。
                }
                else
                {
                    p.Dispose();
                }
            }
            if (match != null)
            {
                return match;
            }
        }
        return null;
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

        // 交互式截图工具在用户取消（Esc）时返回非零退出码（macOS screencapture、
        // flameshot、gnome-screenshot 均如此），据此让调用方立即结束而非空轮询剪贴板。
        if (result.ExitCode != 0)
        {
            _logger.Debug("截图被用户取消: {Command} (ExitCode={ExitCode})", command, result.ExitCode);
            return ScreenCaptureLaunchResult.Cancelled;
        }

        return ScreenCaptureLaunchResult.CompletedBlocking;
    }
}
