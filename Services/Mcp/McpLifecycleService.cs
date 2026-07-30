using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Athena.UI.Models;
using Athena.UI.Services.Interfaces;
using Serilog;

namespace Athena.UI.Services.Mcp;

/// <summary>
/// 驱动 MCP 服务器的运行期生命周期：应用启动时按配置连接，配置保存后计算差异做增量重启，
/// 应用退出时释放全部子进程。所有连接动作串行化，避免并发保存把同一服务器拉起两次。
/// diff 计算委托给纯函数 <see cref="McpConfigDiff"/>。
/// </summary>
public sealed class McpLifecycleService : IAsyncDisposable
{
    private readonly IMcpServerController _controller;
    private readonly IConfigService _configService;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _applyLock = new(1, 1);

    // 上一次成功启动并生效的快照（以名称为键），用于计算 diff。
    // 关键：仅记录"启动成功"的服务器，失败者不入册，从而下次 apply 会重试。
    private Dictionary<string, McpServerSpec> _applied = new(StringComparer.OrdinalIgnoreCase);
    private bool _started;

    public McpLifecycleService(IMcpServerController controller, IConfigService configService, ILogger logger)
    {
        _controller = controller;
        _configService = configService;
        _logger = logger.ForContext<McpLifecycleService>();
    }

    /// <summary>应用启动时调用一次：连接当前配置里已启用的服务器，并订阅后续配置变更。</summary>
    public async Task StartAsync()
    {
        if (_started) return;
        _started = true;
        _configService.ConfigChanged += OnConfigChanged;
        await ApplyAsync(_configService.Load()).ConfigureAwait(false);
    }

    private async void OnConfigChanged(object? sender, AppConfig config)
    {
        try { await ApplyAsync(config).ConfigureAwait(false); }
        catch (Exception ex) { _logger.Error(ex, "应用 MCP 配置变更失败"); }
    }

    /// <summary>
    /// 计算目标状态（EnableMcp 关闭时目标为空集）与已应用状态的差异，只对新增/变更/删除的服务器动手。
    /// </summary>
    public async Task ApplyAsync(AppConfig config)
    {
        await _applyLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var desired = McpConfigDiff.BuildDesired(config);
            var plan = McpConfigDiff.Diff(_applied, desired);
            if (plan.ToStart.Count == 0 && plan.ToStop.Count == 0)
                return;

            foreach (var name in plan.ToStop)
            {
                await _controller.StopServerAsync(name).ConfigureAwait(false);
            }

            // 从上次成功集合起步：保留未变动且仍在目标内的成功项。
            var nextApplied = new Dictionary<string, McpServerSpec>(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, spec) in _applied)
            {
                if (desired.TryGetValue(name, out var d) && d.Fingerprint == spec.Fingerprint)
                    nextApplied[name] = spec;
            }

            int succeeded = 0, failed = 0;
            foreach (var name in plan.ToStart)
            {
                var ok = await _controller.StartServerAsync(desired[name].Config).ConfigureAwait(false);
                if (ok)
                {
                    nextApplied[name] = desired[name]; // 仅成功者入册
                    succeeded++;
                }
                else
                {
                    failed++; // 失败者不入册 → 下次 apply 会重试
                }
            }

            _applied = nextApplied;
            _logger.Information("MCP 配置已应用：成功 {Started} 失败 {Failed} 停止 {Stopped}", succeeded, failed, plan.ToStop.Count);
        }
        finally
        {
            _applyLock.Release();
        }
    }

    /// <summary>手动重连一个服务器：清掉其已应用记录，再触发一次 apply。用于失败后重试。</summary>
    public async Task ReconnectAsync(string serverName)
    {
        await _applyLock.WaitAsync().ConfigureAwait(false);
        try { _applied.Remove(serverName); }
        finally { _applyLock.Release(); }
        await _controller.StopServerAsync(serverName).ConfigureAwait(false);
        await ApplyAsync(_configService.Load()).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        _configService.ConfigChanged -= OnConfigChanged;
        if (_controller is IAsyncDisposable disposable)
            await disposable.DisposeAsync().ConfigureAwait(false);
    }
}
