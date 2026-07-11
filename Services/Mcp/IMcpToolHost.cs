using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Athena.UI.Models.Mcp;

namespace Athena.UI.Services.Mcp;

/// <summary>
/// MCP 工具宿主抽象：将真实的 SDK 客户端与消费方（DiscoveryFunctions / Registry）解耦，
/// 便于在单元测试中替换成 fake 实现，不启动子进程即可覆盖发现-调用链。
/// </summary>
public interface IMcpToolHost
{
    /// <summary>返回当前已发现的工具快照。</summary>
    IReadOnlyList<McpToolDescriptor> ListTools(string? serverFilter = null);

    /// <summary>按 FullyQualifiedName 查找工具描述，找不到返回 null。</summary>
    McpToolDescriptor? Find(string fullyQualifiedName);

    /// <summary>调用 MCP 工具，返回原始文本结果（已由宿主序列化）。</summary>
    Task<McpCallResult> CallToolAsync(string fullyQualifiedName, JsonElement arguments, CancellationToken cancellationToken);
}

/// <summary>MCP 工具调用结果。IsError=true 时 Content 为服务器返回的错误摘要。</summary>
public sealed record McpCallResult(bool IsError, string Content);

/// <summary>
/// 服务器进程的启停控制抽象。让 <see cref="McpLifecycleService"/> 不直接依赖 SDK，
/// 便于单测中用 fake 模拟连接成功/失败以验证重试逻辑。
/// </summary>
public interface IMcpServerController
{
    /// <summary>启动（或重启）一个服务器。返回是否连接成功。</summary>
    Task<bool> StartServerAsync(Athena.UI.Models.McpServerConfig config, CancellationToken cancellationToken = default);

    /// <summary>停止并释放某服务器的连接。</summary>
    Task StopServerAsync(string serverName);
}
