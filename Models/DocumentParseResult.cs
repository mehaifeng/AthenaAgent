namespace Athena.UI.Models;

/// <summary>
/// 文档解析结果
/// </summary>
public class DocumentParseResult
{
    public bool Success { get; init; }

    /// <summary>解析得到的 Markdown 文本（成功时有效）。</summary>
    public string Markdown { get; init; } = string.Empty;

    /// <summary>错误信息（失败时有效）。</summary>
    public string? ErrorMessage { get; init; }

    public static DocumentParseResult Ok(string markdown) => new() { Success = true, Markdown = markdown };

    public static DocumentParseResult Fail(string message) => new() { Success = false, ErrorMessage = message };
}
