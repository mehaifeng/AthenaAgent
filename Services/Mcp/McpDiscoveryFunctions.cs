using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Athena.UI.Services.Interfaces;
using Serilog;

namespace Athena.UI.Services.Mcp;

/// <summary>
/// MCP 工具发现 meta-tool 实现。
/// 主模型初始只看到本类提供的三个工具，不吃 N × schema token；需要时再拉取。
///   mcp_list_tools      — 仅返回 name+一句话摘要（可按 server/keyword 过滤）
///   mcp_get_tool_schema — 拿到 JSON schema 与完整描述
///   mcp_call_tool       — 实际调用
/// </summary>
public sealed class McpDiscoveryFunctions
{
    // 单次 list 返回的最大条目数，防止服务器工具爆炸时把 token 拉爆。
    public const int DefaultListLimit = 50;
    // 摘要长度上限：截取原描述首行前 N 字。
    public const int SummaryCharLimit = 140;

    private readonly IMcpToolHost _host;
    private readonly ILogger _logger;

    public McpDiscoveryFunctions(IMcpToolHost host, ILogger logger)
    {
        _host = host;
        _logger = logger.ForContext<McpDiscoveryFunctions>();
    }

    /// <summary>参数：<c>{ server?: string, keyword?: string, limit?: int }</c></summary>
    public Task<FunctionResult> ListToolsAsync(string? server, string? keyword, int? limit)
    {
        var all = _host.ListTools(server);
        IEnumerable<Models.Mcp.McpToolDescriptor> src = all;
        if (!string.IsNullOrWhiteSpace(keyword))
        {
            var kw = keyword.Trim();
            src = src.Where(d =>
                d.OriginalName.Contains(kw, StringComparison.OrdinalIgnoreCase) ||
                d.Description.Contains(kw, StringComparison.OrdinalIgnoreCase) ||
                d.Server.Contains(kw, StringComparison.OrdinalIgnoreCase));
        }

        var effectiveLimit = Math.Clamp(limit ?? DefaultListLimit, 1, 200);
        var page = src.Take(effectiveLimit).ToList();

        var payload = new
        {
            servers = _host.ListTools().Select(d => d.Server).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(s => s).ToArray(),
            total = all.Count,
            returned = page.Count,
            tools = page.Select(d => new
            {
                name = d.FullyQualifiedName,
                server = d.Server,
                summary = Summarize(d.Description)
            }).ToArray(),
            hint = "call mcp_get_tool_schema with `name` to fetch the JSON schema before invoking mcp_call_tool."
        };

        return Task.FromResult(FunctionResult.SuccessResult(
            $"listed {page.Count}/{all.Count} MCP tools",
            payload));
    }

    /// <summary>参数：<c>{ name: string }</c></summary>
    public Task<FunctionResult> GetToolSchemaAsync(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return Task.FromResult(FunctionResult.FailureResult("参数 `name` 不能为空。"));

        var desc = _host.Find(name);
        if (desc == null)
            return Task.FromResult(FunctionResult.FailureResult($"未找到 MCP 工具 `{name}`。先调用 mcp_list_tools 确认名字。"));

        var payload = new
        {
            name = desc.FullyQualifiedName,
            server = desc.Server,
            originalName = desc.OriginalName,
            description = desc.Description,
            inputSchema = desc.InputSchema,
            outputSchema = desc.OutputSchema
        };
        return Task.FromResult(FunctionResult.SuccessResult("ok", payload));
    }

    /// <summary>参数：<c>{ name: string, arguments: object|string }</c></summary>
    public async Task<FunctionResult> CallToolAsync(string? name, JsonElement arguments)
    {
        if (string.IsNullOrWhiteSpace(name))
            return FunctionResult.FailureResult("参数 `name` 不能为空。");

        var desc = _host.Find(name);
        if (desc == null)
            return FunctionResult.FailureResult($"未找到 MCP 工具 `{name}`。");

        // 归一化参数：弱模型常把对象错发成 JSON 字符串（甚至空串）。这里统一成对象，
        // 只有当模型给了非空、非对象、又不是合法 JSON 的东西时才报错要求其修正。
        if (!TryNormalizeArguments(arguments, out var normalized, out var normErr))
            return FunctionResult.FailureResult(normErr!);

        try
        {
            var result = await _host.CallToolAsync(name, normalized, default).ConfigureAwait(false);
            if (result.IsError)
                return FunctionResult.FailureResult($"MCP 工具 `{name}` 返回错误：{result.Content}");

            return FunctionResult.SuccessResult("ok", new { content = result.Content });
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "MCP tool {Name} threw", name);
            return FunctionResult.FailureResult($"MCP 工具 `{name}` 执行异常：{ex.Message}");
        }
    }

    /// <summary>
    /// 把模型传入的 arguments 统一成 JSON 对象元素：
    /// - Object → 原样。
    /// - 空串/Null/Undefined → 空对象 {}（无参工具可用；缺参工具会由服务器报缺字段，属正常反馈）。
    /// - 非空字符串 → 尝试按 JSON 解析；解析成对象则采用，否则报错请模型改成对象。
    /// </summary>
    internal static bool TryNormalizeArguments(JsonElement arguments, out JsonElement normalized, out string? error)
    {
        error = null;
        switch (arguments.ValueKind)
        {
            case JsonValueKind.Object:
                normalized = arguments;
                return true;

            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                normalized = EmptyObject();
                return true;

            case JsonValueKind.String:
                var s = arguments.GetString();
                if (string.IsNullOrWhiteSpace(s))
                {
                    normalized = EmptyObject();
                    return true;
                }
                try
                {
                    using var doc = JsonDocument.Parse(s);
                    if (doc.RootElement.ValueKind != JsonValueKind.Object)
                    {
                        normalized = default;
                        error = "参数 `arguments` 必须是一个 JSON 对象（如 {\"ip\":\"1.2.3.4\"}）。请勿传入数组或标量。";
                        return false;
                    }
                    normalized = doc.RootElement.Clone();
                    return true;
                }
                catch (JsonException)
                {
                    normalized = default;
                    error = "参数 `arguments` 是字符串但不是合法 JSON。请直接传对象，例如 {\"ip\":\"1.2.3.4\"}。";
                    return false;
                }

            default:
                normalized = default;
                error = "参数 `arguments` 必须是 JSON 对象（如 {\"ip\":\"1.2.3.4\"}）。";
                return false;
        }
    }

    private static JsonElement EmptyObject()
    {
        using var doc = JsonDocument.Parse("{}");
        return doc.RootElement.Clone();
    }

    private static string Summarize(string description)
    {
        if (string.IsNullOrEmpty(description)) return string.Empty;
        var firstLine = description.Split('\n', 2)[0].Trim();
        if (firstLine.Length <= SummaryCharLimit) return firstLine;
        return firstLine.Substring(0, SummaryCharLimit - 1) + "…";
    }
}
