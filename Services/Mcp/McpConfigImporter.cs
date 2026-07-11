using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text.Json;
using Athena.UI.Models;

namespace Athena.UI.Services.Mcp;

/// <summary>
/// 解析 Claude Desktop / 主流 MCP 客户端通用的配置片段，产出 <see cref="McpServerConfig"/> 列表。
/// 事实标准格式：<c>{ "mcpServers": { "名称": { "command": "...", "args": [...], "env": {...} } } }</c>。
/// 也容忍直接传入内层 map（即最外层就是 名称→定义）。纯函数，便于单测。
/// </summary>
public static class McpConfigImporter
{
    public static IReadOnlyList<McpServerConfig> Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new FormatException("导入内容为空。");

        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(json);
            root = doc.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            throw new FormatException($"JSON 解析失败：{ex.Message}");
        }

        if (root.ValueKind != JsonValueKind.Object)
            throw new FormatException("顶层必须是 JSON 对象。");

        // 允许带 mcpServers 包裹层，或直接是 名称→定义 的 map。
        JsonElement serversMap = root.TryGetProperty("mcpServers", out var wrapped) ? wrapped : root;
        if (serversMap.ValueKind != JsonValueKind.Object)
            throw new FormatException("`mcpServers` 必须是对象。");

        var result = new List<McpServerConfig>();
        foreach (var server in serversMap.EnumerateObject())
        {
            if (server.Value.ValueKind != JsonValueKind.Object)
                continue;

            var url = GetString(server.Value, "url");
            var typeHint = GetString(server.Value, "type"); // "http"/"sse"/"streamable-http" 等
            var isHttp = !string.IsNullOrWhiteSpace(url)
                || (typeHint != null && (typeHint.Contains("http", StringComparison.OrdinalIgnoreCase) || typeHint.Contains("sse", StringComparison.OrdinalIgnoreCase)));

            var cfg = new McpServerConfig
            {
                Name = server.Name,
                Enabled = true,
                Transport = isHttp ? McpTransportKind.Http : McpTransportKind.Stdio,
                Command = GetString(server.Value, "command") ?? string.Empty,
                WorkingDirectory = GetString(server.Value, "cwd") ?? string.Empty,
                Url = url ?? string.Empty
            };

            if (server.Value.TryGetProperty("args", out var args) && args.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in args.EnumerateArray())
                    if (a.ValueKind == JsonValueKind.String)
                        cfg.Arguments.Add(new McpArgEntry(a.GetString() ?? string.Empty));
            }

            if (server.Value.TryGetProperty("env", out var env) && env.ValueKind == JsonValueKind.Object)
            {
                foreach (var e in env.EnumerateObject())
                    cfg.Environment.Add(new McpEnvEntry { Key = e.Name, Value = e.Value.ValueKind == JsonValueKind.String ? e.Value.GetString() ?? string.Empty : e.Value.ToString() });
            }

            // 远程服务器的自定义头（Authorization 等）
            if (server.Value.TryGetProperty("headers", out var headers) && headers.ValueKind == JsonValueKind.Object)
            {
                foreach (var h in headers.EnumerateObject())
                    cfg.Headers.Add(new McpEnvEntry { Key = h.Name, Value = h.Value.ValueKind == JsonValueKind.String ? h.Value.GetString() ?? string.Empty : h.Value.ToString() });
            }

            result.Add(cfg);
        }

        if (result.Count == 0)
            throw new FormatException("未解析到任何服务器条目。");

        return result;
    }

    private static string? GetString(JsonElement obj, string prop)
        => obj.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
