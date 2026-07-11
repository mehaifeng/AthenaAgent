using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Athena.UI.Models;
using Athena.UI.Services.Interfaces;
using Serilog;

namespace Athena.UI.Services.Mcp;

/// <summary>
/// 允许主模型经自然语言管理 MCP 服务器（新增 / 移除）。
/// 均为敏感操作：走 FunctionRegistry 的审批闸门，用户在弹窗中确认具体 command/args 后才落地。
/// 保存配置即触发 IConfigService.ConfigChanged → McpLifecycleService 增量热重启，无需额外接线。
/// </summary>
public sealed class McpManagementFunctions
{
    private readonly IConfigService _configService;
    private readonly ILogger _logger;

    public McpManagementFunctions(IConfigService configService, ILogger logger)
    {
        _configService = configService;
        _logger = logger.ForContext<McpManagementFunctions>();
    }

    /// <summary>
    /// 新增或更新一个 MCP 服务器。传 url → Http 远程服务器（可带 headers）；否则为 stdio（需 command）。
    /// 参数：name, command?, args?(string[]), env?(object), url?, headers?(object)。
    /// </summary>
    public async Task<FunctionResult> AddServerAsync(string? name, string? command, JsonElement args, JsonElement env, string? url, JsonElement headers)
    {
        if (string.IsNullOrWhiteSpace(name))
            return FunctionResult.FailureResult("参数 `name` 不能为空。");

        var isHttp = !string.IsNullOrWhiteSpace(url);
        if (!isHttp && string.IsNullOrWhiteSpace(command))
            return FunctionResult.FailureResult("请提供 `command`（stdio，如 npx/uvx/docker）或 `url`（http 远程服务器）之一。");

        var config = await _configService.LoadAsync();

        var server = new McpServerConfig
        {
            Name = name.Trim(),
            Transport = isHttp ? McpTransportKind.Http : McpTransportKind.Stdio,
            Command = command?.Trim() ?? string.Empty,
            Url = url?.Trim() ?? string.Empty,
            Enabled = true,
            StartupTimeoutSeconds = 15,
            CallTimeoutSeconds = 60
        };

        // 弱模型常把数组/对象错发成 JSON 字符串（甚至空串）。这里统一容忍并解析。
        if (TryCoerce(args, JsonValueKind.Array, out var argsEl))
        {
            foreach (var a in argsEl.EnumerateArray())
                if (a.ValueKind == JsonValueKind.String)
                    server.Arguments.Add(new McpArgEntry(a.GetString() ?? string.Empty));
        }

        if (TryCoerce(env, JsonValueKind.Object, out var envEl))
        {
            foreach (var e in envEl.EnumerateObject())
                server.Environment.Add(new McpEnvEntry
                {
                    Key = e.Name,
                    Value = e.Value.ValueKind == JsonValueKind.String ? e.Value.GetString() ?? string.Empty : e.Value.ToString()
                });
        }

        if (TryCoerce(headers, JsonValueKind.Object, out var headersEl))
        {
            foreach (var h in headersEl.EnumerateObject())
                server.Headers.Add(new McpEnvEntry
                {
                    Key = h.Name,
                    Value = h.Value.ValueKind == JsonValueKind.String ? h.Value.GetString() ?? string.Empty : h.Value.ToString()
                });
        }

        // 同名 upsert：替换旧条目（用户已在审批弹窗看到本次内容）。
        var existing = config.McpServers.FirstOrDefault(
            s => string.Equals(s.Name, server.Name, StringComparison.OrdinalIgnoreCase));
        var replaced = existing != null;
        if (existing != null) config.McpServers.Remove(existing);
        config.McpServers.Add(server);

        // 新增服务器却没开总开关等于白配置：顺带打开 EnableMcp。
        var enabledMcp = false;
        if (!config.EnableMcp) { config.EnableMcp = true; enabledMcp = true; }

        await _configService.SaveAsync(config);
        _logger.Information("MCP 服务器 {Name} 已{Action}（command={Command}）", server.Name, replaced ? "更新" : "新增", server.Command);

        var msg = replaced ? $"已更新 MCP 服务器 `{server.Name}`。" : $"已新增 MCP 服务器 `{server.Name}`。";
        if (enabledMcp) msg += " 已自动开启 MCP 扩展总开关。";
        msg += " 正在后台连接，可用 mcp_list_tools 查看其工具。";

        return FunctionResult.SuccessResult(msg, new
        {
            name = server.Name,
            command = server.Command,
            argsCount = server.Arguments.Count,
            replaced,
            enabledMcp
        });
    }

    /// <summary>
    /// 从一段 JSON（Claude Desktop 的 mcpServers 格式）导入一个或多个服务器。
    /// 最贴合"用户粘贴配置"场景：模型只需把整段 JSON 原样作为字符串传入，无需构造嵌套对象。
    /// 参数：json（字符串）。
    /// </summary>
    public async Task<FunctionResult> ImportJsonAsync(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return FunctionResult.FailureResult("参数 `json` 不能为空。请传入 Claude Desktop 格式的 MCP 配置 JSON。");

        IReadOnlyList<McpServerConfig> parsed;
        try
        {
            parsed = McpConfigImporter.Parse(json);
        }
        catch (Exception ex)
        {
            return FunctionResult.FailureResult($"解析失败：{ex.Message}");
        }

        var config = await _configService.LoadAsync();
        var names = new List<string>();
        foreach (var incoming in parsed)
        {
            var existing = config.McpServers.FirstOrDefault(
                s => string.Equals(s.Name, incoming.Name, StringComparison.OrdinalIgnoreCase));
            if (existing != null) config.McpServers.Remove(existing);
            config.McpServers.Add(incoming);
            names.Add(incoming.Name);
        }

        var enabledMcp = false;
        if (!config.EnableMcp) { config.EnableMcp = true; enabledMcp = true; }

        await _configService.SaveAsync(config);
        _logger.Information("MCP 从 JSON 导入 {Count} 个服务器：{Names}", names.Count, string.Join(",", names));

        var msg = $"已导入 {names.Count} 个 MCP 服务器：{string.Join("、", names)}。";
        if (enabledMcp) msg += " 已自动开启 MCP 扩展总开关。";
        msg += " 正在后台连接，可用 mcp_list_tools 查看其工具。";
        return FunctionResult.SuccessResult(msg, new { imported = names, enabledMcp });
    }

    // 把 input 归一成期望的 JSON 种类：原本就是 → 直接用；是包着 JSON 的字符串 → 解析。空串/其它 → false。
    private static bool TryCoerce(JsonElement input, JsonValueKind wantKind, out JsonElement result)
    {
        if (input.ValueKind == wantKind) { result = input; return true; }
        if (input.ValueKind == JsonValueKind.String)
        {
            var s = input.GetString();
            if (!string.IsNullOrWhiteSpace(s))
            {
                try
                {
                    using var doc = JsonDocument.Parse(s);
                    if (doc.RootElement.ValueKind == wantKind)
                    {
                        result = doc.RootElement.Clone();
                        return true;
                    }
                }
                catch (JsonException) { }
            }
        }
        result = default;
        return false;
    }

    /// <summary>移除一个 MCP 服务器。参数：name。</summary>
    public async Task<FunctionResult> RemoveServerAsync(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return FunctionResult.FailureResult("参数 `name` 不能为空。");

        var config = await _configService.LoadAsync();
        var existing = config.McpServers.FirstOrDefault(
            s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
        if (existing == null)
            return FunctionResult.FailureResult($"未找到名为 `{name}` 的 MCP 服务器。");

        config.McpServers.Remove(existing);
        await _configService.SaveAsync(config);
        _logger.Information("MCP 服务器 {Name} 已移除", name);

        return FunctionResult.SuccessResult($"已移除 MCP 服务器 `{name}`，其子进程将被关闭。", new { name });
    }
}
