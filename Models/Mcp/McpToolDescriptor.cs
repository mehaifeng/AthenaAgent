using System.Text.Json;

namespace Athena.UI.Models.Mcp;

/// <summary>
/// 描述从某个 MCP 服务器发现的单个工具。
/// FullyQualifiedName 是暴露给主模型的名字（服务器命名空间前缀 + 长度约束 + 哈希兜底）。
/// </summary>
public sealed record McpToolDescriptor(
    string Server,
    string OriginalName,
    string FullyQualifiedName,
    string Description,
    JsonElement InputSchema,
    JsonElement? OutputSchema);
