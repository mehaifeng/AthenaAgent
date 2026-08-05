using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;

namespace Athena.UI.Services.Preview;

/// <summary>
/// Office 预览的可预览类型判定（纯逻辑，零平台依赖，可被测试工程链接）。
/// 预览支持 OOXML/PDF 新格式；老二进制格式（.doc/.xls/.ppt）给出明确的不支持提示。
/// </summary>
public static class OfficePreviewTypes
{
    private static readonly HashSet<string> PreviewableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".docx", ".xlsx", ".pptx", ".pdf",
        ".docm", ".xlsm", ".pptm" // 带宏版本与对应非宏格式结构相同，预览引擎可读
    };

    private static readonly HashSet<string> LegacyExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".doc", ".xls", ".ppt", ".pps"
    };

    public static bool IsPreviewable(string path)
        => PreviewableExtensions.Contains(Path.GetExtension(path));

    public static bool IsLegacyOffice(string path)
        => LegacyExtensions.Contains(Path.GetExtension(path));

    /// <summary>前端分派用的类型键（pdf/docx/xlsx/pptx）。</summary>
    public static string? PreviewType(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".pdf" => "pdf",
            ".docx" or ".docm" => "docx",
            ".xlsx" or ".xlsm" => "xlsx",
            ".pptx" or ".pptm" => "pptx",
            _ => null
        };
    }
}

/// <summary>Range 请求头解析结果。</summary>
public enum OfficeRangeResult
{
    /// <summary>无 Range 头（或该头应被忽略），返回 200 整文件。</summary>
    None,
    /// <summary>单段可满足的范围，返回 206。</summary>
    Valid,
    /// <summary>不可满足的范围（起始越界/非法），返回 416。</summary>
    Invalid
}

/// <summary>
/// 极简单段 Range 解析器（PDF.js 分块加载所需）。
/// 仅支持 bytes=a-b / bytes=a- / bytes=-n 三种形态；多段（逗号分隔）按"忽略 Range"处理，
/// 对 PDF.js 最安全（其会退化为整文件拉取）。
/// </summary>
public static class OfficeRangeParser
{
    public static OfficeRangeResult TryParse(string? header, long total, out long start, out long end)
    {
        start = 0;
        end = total - 1;
        if (string.IsNullOrWhiteSpace(header)) return OfficeRangeResult.None;
        if (total <= 0) return OfficeRangeResult.Invalid;

        var spec = header.Trim();
        if (!spec.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase)) return OfficeRangeResult.None;
        var value = spec["bytes=".Length..].Trim();
        if (value.Contains(',')) return OfficeRangeResult.None; // 多段不支持：忽略 Range，整文件 200

        var dash = value.IndexOf('-');
        if (dash < 0) return OfficeRangeResult.Invalid;
        var startText = value[..dash].Trim();
        var endText = value[(dash + 1)..].Trim();

        if (startText.Length == 0)
        {
            // 后缀形式 bytes=-n：最后 n 字节
            if (!long.TryParse(endText, out var n) || n <= 0) return OfficeRangeResult.Invalid;
            start = Math.Max(0, total - n);
            end = total - 1;
            return OfficeRangeResult.Valid;
        }

        if (!long.TryParse(startText, out var rangeStart)) return OfficeRangeResult.Invalid;
        if (rangeStart >= total) return OfficeRangeResult.Invalid; // 起始越界 → 416
        start = rangeStart;
        end = endText.Length == 0
            ? total - 1
            : long.TryParse(endText, out var rangeEnd)
                ? Math.Min(rangeEnd, total - 1)
                : total - 1;
        if (end < start) return OfficeRangeResult.Invalid; // 空范围（如 bytes=5-4）→ 416
        return OfficeRangeResult.Valid;
    }
}

/// <summary>预览服务器使用的 Content-Type 映射。</summary>
public static class OfficeMimeMap
{
    public static string ForPath(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".mjs" or ".js" => "application/javascript; charset=utf-8", // ES module 必需，Chromium/WebKit 均接受
            ".css" => "text/css; charset=utf-8",
            ".html" or ".htm" => "text/html; charset=utf-8",
            ".json" or ".map" => "application/json",
            ".svg" => "image/svg+xml",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".woff2" => "font/woff2",
            ".pdf" => "application/pdf",
            ".docx" or ".docm" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            ".xlsx" or ".xlsm" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            ".pptx" or ".pptm" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
            _ => "application/octet-stream"
        };
    }
}

/// <summary>
/// 预览文件会话存储：sessionId → 绝对路径的映射 + 进程级访问令牌。
/// 仅本进程内有效，杜绝外部进程猜测路径读取用户文件。
/// </summary>
public sealed class OfficePreviewSessionStore
{
    private readonly ConcurrentDictionary<string, string> _sessions = new(StringComparer.Ordinal);
    private readonly string _token;

    public OfficePreviewSessionStore()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        _token = Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public string Token => _token;

    /// <summary>固定时间比较，避免本地攻击者通过时序猜测令牌。</summary>
    public bool ValidateToken(string? candidate)
    {
        if (string.IsNullOrEmpty(candidate) || candidate.Length != _token.Length) return false;
        var a = System.Text.Encoding.UTF8.GetBytes(candidate);
        var b = System.Text.Encoding.UTF8.GetBytes(_token);
        return CryptographicOperations.FixedTimeEquals(a, b);
    }

    public string CreateSession(string path)
    {
        var id = Guid.NewGuid().ToString("N");
        _sessions[id] = Path.GetFullPath(path);
        return id;
    }

    public bool TryGetSession(string sessionId, out string path)
        => _sessions.TryGetValue(sessionId, out path!);

    public void ReleaseSession(string sessionId) => _sessions.TryRemove(sessionId, out _);

    public void ReleaseAll() => _sessions.Clear();

    public int SessionCount => _sessions.Count;
}
