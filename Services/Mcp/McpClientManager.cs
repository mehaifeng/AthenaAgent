using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Athena.UI.Models;
using Athena.UI.Models.Mcp;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Serilog;

namespace Athena.UI.Services.Mcp;

/// <summary>
/// 管理所有 MCP 服务器（stdio）的生命周期，并实现 <see cref="IMcpToolHost"/>。
/// 单实例、线程安全：外部调用 StartAsync/StopAsync 触发进程连接与释放；主模型经 DiscoveryFunctions 访问只读快照。
/// </summary>
public sealed class McpClientManager : IMcpToolHost, IMcpServerController, IAsyncDisposable
{
    private readonly McpToolRegistry _registry;
    private readonly ILogger _logger;
    private readonly Dictionary<string, McpClient> _clients = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _mutex = new(1, 1);

    public McpClientManager(McpToolRegistry registry, ILogger logger)
    {
        _registry = registry;
        _logger = logger.ForContext<McpClientManager>();
    }

    public IReadOnlyList<McpToolDescriptor> ListTools(string? serverFilter = null)
        => _registry.Snapshot(serverFilter);

    public McpToolDescriptor? Find(string fullyQualifiedName)
        => _registry.Find(fullyQualifiedName);

    public async Task<McpCallResult> CallToolAsync(string fullyQualifiedName, JsonElement arguments, CancellationToken cancellationToken)
    {
        var desc = _registry.Find(fullyQualifiedName)
            ?? throw new InvalidOperationException($"MCP tool not found: {fullyQualifiedName}");

        McpClient? client;
        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { _clients.TryGetValue(desc.Server, out client); }
        finally { _mutex.Release(); }

        if (client is null)
            return new McpCallResult(true, $"服务器 `{desc.Server}` 未连接。");

        var args = FlattenArguments(arguments);
        var result = await client.CallToolAsync(desc.OriginalName, args, cancellationToken: cancellationToken).ConfigureAwait(false);
        return new McpCallResult(result.IsError == true, ExtractTextContent(result));
    }

    /// <summary>按配置启动/重启单个服务器（幂等：已连接则先停止）。返回是否连接成功。</summary>
    public async Task<bool> StartServerAsync(McpServerConfig config, CancellationToken cancellationToken = default)
    {
        if (!config.Enabled || string.IsNullOrWhiteSpace(config.Name))
            return false;
        // 传输相关的必填项：Stdio 需要 Command，Http 需要 Url。
        if (config.Transport == McpTransportKind.Stdio && string.IsNullOrWhiteSpace(config.Command))
            return false;
        if (config.Transport == McpTransportKind.Http && string.IsNullOrWhiteSpace(config.Url))
            return false;

        await StopServerAsync(config.Name).ConfigureAwait(false);
        SetStatus(config, McpConnectionStatus.Connecting, Loc("Config.Mcp.Status.Connecting", "连接中…"), 0);

        try
        {
            IClientTransport transport = BuildTransport(config);

            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connectCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, config.StartupTimeoutSeconds)));

            var client = await McpClient.CreateAsync(transport, cancellationToken: connectCts.Token).ConfigureAwait(false);
            var tools = await client.ListToolsAsync(cancellationToken: connectCts.Token).ConfigureAwait(false);

            var descriptors = tools.Select(t => new McpToolDescriptor(
                Server: config.Name,
                OriginalName: t.ProtocolTool.Name,
                FullyQualifiedName: McpToolNameEncoder.Encode(config.Name, t.ProtocolTool.Name),
                Description: t.Description,
                InputSchema: t.JsonSchema,
                OutputSchema: t.ReturnJsonSchema)).ToList();

            _registry.ReplaceServerTools(config.Name, descriptors);

            await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
            try { _clients[config.Name] = client; }
            finally { _mutex.Release(); }

            SetStatus(config, McpConnectionStatus.Connected,
                string.Format(Loc("Config.Mcp.Status.Connected", "已发现 {0} 个工具"), descriptors.Count),
                descriptors.Count);
            _logger.Information("MCP 服务器 {Server} 已连接，发现 {Count} 个工具", config.Name, descriptors.Count);
            return true;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "MCP 服务器 {Server} 连接失败", config.Name);
            _registry.RemoveServer(config.Name);
            SetStatus(config, McpConnectionStatus.Failed, SummarizeError(ex), 0);
            return false;
        }
    }

    public async Task StopServerAsync(string serverName)
    {
        McpClient? client = null;
        await _mutex.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_clients.Remove(serverName, out client)) { /* removed */ }
        }
        finally { _mutex.Release(); }

        _registry.RemoveServer(serverName);

        if (client is not null)
        {
            try { await client.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { _logger.Warning(ex, "关闭 MCP 服务器 {Server} 时出错", serverName); }
        }
    }

    // 按传输方式构建 SDK transport。Http 用 AutoDetect 兼容 Streamable HTTP 与 SSE。
    private static IClientTransport BuildTransport(McpServerConfig config)
    {
        if (config.Transport == McpTransportKind.Http)
        {
            var headers = config.Headers
                .Where(h => !string.IsNullOrEmpty(h.Key))
                .ToDictionary(h => h.Key, h => h.Value);

            return new HttpClientTransport(new HttpClientTransportOptions
            {
                Name = config.Name,
                Endpoint = new Uri(config.Url),
                TransportMode = HttpTransportMode.AutoDetect,
                ConnectionTimeout = TimeSpan.FromSeconds(Math.Max(1, config.StartupTimeoutSeconds)),
                AdditionalHeaders = headers.Count > 0 ? headers : null
            });
        }

        var envDict = config.Environment
            .Where(e => !string.IsNullOrEmpty(e.Key))
            .ToDictionary(e => e.Key, e => (string?)e.Value);

        // 过滤空白参数行（UI 里新增后未填的占位）。
        var argList = config.Arguments
            .Select(a => a.Value)
            .Where(v => !string.IsNullOrEmpty(v))
            .ToList();

        return new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = config.Name,
            Command = config.Command,
            Arguments = argList,
            WorkingDirectory = string.IsNullOrWhiteSpace(config.WorkingDirectory) ? null : config.WorkingDirectory,
            EnvironmentVariables = envDict.Count > 0 ? envDict : null
        });
    }

    // 解析本地化文本；App.Services 未就绪或缺键时回退中文默认，保证服务层不依赖 UI 生命周期。
    private static string Loc(string key, string fallback)
    {
        var localization = Athena.UI.App.Services?
            .GetService(typeof(Athena.UI.Services.Interfaces.ILocalizationService))
            as Athena.UI.Services.Interfaces.ILocalizationService;
        return localization?.GetString(key, fallback) ?? fallback;
    }

    // 运行期状态回填到 UI 绑定的 McpServerConfig（marshal 到 UI 线程，避免跨线程通知告警）。
    private static void SetStatus(McpServerConfig config, McpConnectionStatus status, string detail, int toolCount)
    {
        void Apply()
        {
            config.Status = status;
            config.StatusDetail = detail;
            config.DiscoveredToolCount = toolCount;
        }

        var dispatcher = Avalonia.Threading.Dispatcher.UIThread;
        if (dispatcher.CheckAccess()) Apply();
        else dispatcher.Post(Apply);
    }

    // 取异常链末端最具体的一句（含 MCP 服务器 stderr 尾巴），截断后作为 UI 提示。
    private static string SummarizeError(Exception ex)
    {
        var msg = ex.Message;
        for (var inner = ex.InnerException; inner is not null; inner = inner.InnerException)
            msg = inner.Message;
        msg = msg.Replace("\r", " ").Replace("\n", " ").Trim();
        return msg.Length > 200 ? msg[..199] + "…" : msg;
    }

    public async ValueTask DisposeAsync()
    {
        List<string> names;
        await _mutex.WaitAsync().ConfigureAwait(false);
        try { names = _clients.Keys.ToList(); }
        finally { _mutex.Release(); }
        foreach (var n in names) await StopServerAsync(n).ConfigureAwait(false);
    }

    // JsonElement → IReadOnlyDictionary<string, object?>，SDK 会用其内建 serializer 再串行化。
    private static IReadOnlyDictionary<string, object?>? FlattenArguments(JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object) return null;
        var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var p in arguments.EnumerateObject())
            dict[p.Name] = p.Value;
        return dict;
    }

    private static string ExtractTextContent(CallToolResult result)
    {
        if (result.Content is null || result.Content.Count == 0)
        {
            return result.StructuredContent?.GetRawText() ?? string.Empty;
        }

        var sb = new StringBuilder();
        foreach (var block in result.Content)
        {
            if (block is TextContentBlock text) sb.AppendLine(text.Text);
            else sb.AppendLine($"[{block.Type}]");
        }
        return sb.ToString().TrimEnd();
    }
}
