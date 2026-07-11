using System;
using System.Collections.Generic;
using System.Linq;
using Athena.UI.Models;

namespace Athena.UI.Services.Mcp;

/// <summary>
/// MCP 目标状态计算与增量 diff（纯函数，无 SDK / 进程依赖，便于单测）。
/// </summary>
public static class McpConfigDiff
{
    /// <summary>
    /// 目标状态：仅当 EnableMcp 且服务器 Enabled 且传输必填项齐全（Stdio 需 Command，Http 需 Url）时才纳入。
    /// 同名后者覆盖前者。
    /// </summary>
    public static Dictionary<string, McpServerSpec> BuildDesired(AppConfig config)
    {
        var result = new Dictionary<string, McpServerSpec>(StringComparer.OrdinalIgnoreCase);
        if (!config.EnableMcp) return result;

        foreach (var s in config.McpServers)
        {
            if (!s.Enabled || string.IsNullOrWhiteSpace(s.Name)) continue;
            if (!IsRunnable(s)) continue;
            result[s.Name] = new McpServerSpec(s, Fingerprint(s));
        }
        return result;
    }

    /// <summary>传输必填项是否齐全。</summary>
    public static bool IsRunnable(McpServerConfig s) => s.Transport switch
    {
        McpTransportKind.Http => !string.IsNullOrWhiteSpace(s.Url),
        _ => !string.IsNullOrWhiteSpace(s.Command)
    };

    /// <summary>
    /// 纯函数 diff：返回需要停止与（重新）启动的服务器名。
    /// 指纹变化的服务器同时出现在两个列表里（先停后起 = 重启）。
    /// </summary>
    public static DiffPlan Diff(
        IReadOnlyDictionary<string, McpServerSpec> applied,
        IReadOnlyDictionary<string, McpServerSpec> desired)
    {
        var toStop = new List<string>();
        var toStart = new List<string>();

        foreach (var (name, spec) in applied)
        {
            if (!desired.TryGetValue(name, out var d) || d.Fingerprint != spec.Fingerprint)
                toStop.Add(name);
        }
        foreach (var (name, spec) in desired)
        {
            if (!applied.TryGetValue(name, out var a) || a.Fingerprint != spec.Fingerprint)
                toStart.Add(name);
        }
        return new DiffPlan(toStop, toStart);
    }

    // 指纹涵盖影响进程行为的全部字段；任一变化都触发重启。
    private static string Fingerprint(McpServerConfig s)
    {
        var args = string.Join("", s.Arguments.Select(a => a.Value));
        var env = string.Join("", s.Environment
            .Where(e => !string.IsNullOrEmpty(e.Key))
            .OrderBy(e => e.Key, StringComparer.Ordinal)
            .Select(e => $"{e.Key}={e.Value}"));
        var headers = string.Join("", s.Headers
            .Where(h => !string.IsNullOrEmpty(h.Key))
            .OrderBy(h => h.Key, StringComparer.Ordinal)
            .Select(h => $"{h.Key}={h.Value}"));
        return $"{(int)s.Transport}{s.Command}{args}{env}{s.WorkingDirectory}{s.Url}{headers}{s.StartupTimeoutSeconds}";
    }
}

/// <summary>目标状态里的一条服务器规格：原始配置 + 行为指纹。</summary>
public sealed record McpServerSpec(McpServerConfig Config, string Fingerprint);

/// <summary>diff 结果：需要停止与启动的服务器名列表。</summary>
public sealed record DiffPlan(IReadOnlyList<string> ToStop, IReadOnlyList<string> ToStart);
