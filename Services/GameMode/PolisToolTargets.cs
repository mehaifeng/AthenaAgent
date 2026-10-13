using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Athena.UI.Services.GameMode;

/// <summary>
/// 一次工具调用指向哪里（纯字符串运算，不碰文件系统，不解析软链）。直播拿掉之后（2026-10-13）它只服务两件事：
/// 外部改动的归属（<see cref="PolisChanges.Attribute"/> 要知道她这一回合碰过哪些路径）和交付的成果
/// （写进工作区的那个文件）。原来在回放转换器里，回放连同转换器一起删了，这两个函数留下。
/// </summary>
public static class PolisToolTargets
{
    private static string? ReadString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>按 <see cref="PolisToolCategories.PathArgumentPriority"/> 取工具参数里的目标路径；参数不是 JSON 对象时为 null。</summary>
    public static string? ExtractTargetPath(string argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            foreach (var key in PolisToolCategories.PathArgumentPriority)
            {
                var value = ReadString(doc.RootElement, key);
                if (!string.IsNullOrWhiteSpace(value)) return value;
            }
        }
        catch (JsonException)
        {
            // 模型偶尔发出不合法的参数 JSON（日志里见过），这一次调用就没有地点
        }
        return null;
    }

    /// <summary>
    /// 路径 → 地点。纯字符串运算，不碰文件系统，不解析软链：
    /// <c>~</c> 按给定的主目录展开；相对路径一律算城外（文件工具把它解析到知识库 / AthenaData，终端按进程目录，都不是工作区）；
    /// 绝对路径规范化 '.' 和 '..' 后，落在工作区根目录之内的转成相对路径（根目录本身是空串，即广场）。
    /// 返回 <c>inside</c>（带相对路径）、<c>outside</c> 或 <c>none</c>（这个调用没有路径）。
    /// </summary>
    public static (string Place, string? Path) ResolvePath(string? rawPath, string workspaceRoot, string? homeDirectory, bool ignoreCase = true)
    {
        if (string.IsNullOrWhiteSpace(rawPath)) return ("none", null);
        var path = rawPath.Trim();
        if (homeDirectory != null && (path == "~" || path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal)))
        {
            path = homeDirectory.TrimEnd('/', '\\') + path[1..];
        }

        var normalized = Normalize(path);
        var root = Normalize(workspaceRoot);
        if (normalized is null || root is null) return ("outside", null);

        var comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (string.Equals(normalized, root, comparison)) return ("inside", string.Empty);
        var prefix = root.EndsWith('/') ? root : root + "/";
        return normalized.StartsWith(prefix, comparison)
            ? ("inside", normalized[prefix.Length..])
            : ("outside", null);
    }

    /// <summary>绝对路径的规范形式：'/' 分隔，消掉 '.' 和 '..'，去掉末尾的 '/'（根除外）。相对路径返回 null。</summary>
    private static string? Normalize(string path)
    {
        var p = path.Replace('\\', '/');
        string head;
        if (p.StartsWith('/'))
        {
            head = "/";
            p = p[1..];
        }
        else if (p.Length >= 2 && char.IsAsciiLetter(p[0]) && p[1] == ':')
        {
            head = char.ToUpperInvariant(p[0]) + ":/";
            p = p[2..].TrimStart('/');
        }
        else
        {
            return null;
        }

        var parts = new List<string>();
        foreach (var segment in p.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".") continue;
            if (segment == "..")
            {
                if (parts.Count > 0) parts.RemoveAt(parts.Count - 1);
                continue;
            }
            parts.Add(segment);
        }
        return head + string.Join('/', parts);
    }
}
