using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;
using System.Text.Json.Serialization;
using Athena.UI.Models.Mcp;

namespace Athena.UI.Models;

/// <summary>
/// 单个 MCP 服务器的配置。仅支持 stdio 传输（P1/P2 目标）。
/// 名称在同一份配置内需唯一；工具在暴露给模型时用 name 作为命名空间前缀。
/// Status/StatusDetail/DiscoveredToolCount 为运行期状态，不参与持久化。
/// </summary>
public partial class McpServerConfig : ObservableObject
{
    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private bool _enabled = true;

    // 传输方式：Stdio（拉起本地子进程）或 Http（Streamable HTTP / SSE 远程服务器）。
    [ObservableProperty]
    private McpTransportKind _transport = McpTransportKind.Stdio;

    // —— Stdio 专用 ——
    [ObservableProperty]
    private string _command = string.Empty;

    // 序列化为扁平字符串数组，内存里用 McpArgEntry 包装以支持行内编辑。
    [ObservableProperty]
    [property: JsonConverter(typeof(McpArgListJsonConverter))]
    private ObservableCollection<McpArgEntry> _arguments = new();

    [ObservableProperty]
    private ObservableCollection<McpEnvEntry> _environment = new();

    [ObservableProperty]
    private string _workingDirectory = string.Empty;

    // —— Http 专用 ——
    [ObservableProperty]
    private string _url = string.Empty;

    // 自定义请求头（如 Authorization: Bearer xxx），键值形式复用 McpEnvEntry。
    [ObservableProperty]
    private ObservableCollection<McpEnvEntry> _headers = new();

    [ObservableProperty]
    private int _startupTimeoutSeconds = 15;

    [ObservableProperty]
    private int _callTimeoutSeconds = 60;

    // —— 运行期状态（不持久化）——
    [ObservableProperty]
    [property: JsonIgnore]
    private McpConnectionStatus _status = McpConnectionStatus.Idle;

    [ObservableProperty]
    [property: JsonIgnore]
    private string _statusDetail = string.Empty;

    [ObservableProperty]
    [property: JsonIgnore]
    private int _discoveredToolCount;

    // UI 折叠状态（不持久化）：默认折叠，仅在展开时显示可编辑详情。
    [ObservableProperty]
    [property: JsonIgnore]
    private bool _isExpanded;
}

/// <summary>MCP 服务器传输方式。</summary>
public enum McpTransportKind
{
    /// <summary>本地子进程，经 stdin/stdout 通信。</summary>
    Stdio,
    /// <summary>远程 HTTP 服务器（Streamable HTTP 或 SSE，自动探测）。</summary>
    Http
}

/// <summary>MCP 服务器运行期连接状态。</summary>
public enum McpConnectionStatus
{
    /// <summary>未连接（未启用或尚未尝试）。</summary>
    Idle,
    /// <summary>正在连接。</summary>
    Connecting,
    /// <summary>已连接并发现工具。</summary>
    Connected,
    /// <summary>连接失败，详情见 StatusDetail。</summary>
    Failed
}

public partial class McpArgEntry : ObservableObject
{
    [ObservableProperty]
    private string _value = string.Empty;

    public McpArgEntry() { }
    public McpArgEntry(string value) { _value = value; }
}

public partial class McpEnvEntry : ObservableObject
{
    [ObservableProperty]
    private string _key = string.Empty;

    [ObservableProperty]
    private string _value = string.Empty;
}
