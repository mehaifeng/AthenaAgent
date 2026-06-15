namespace Athena.UI.Models;

/// <summary>
/// 文档附件的解析状态
/// </summary>
public enum DocumentParseState
{
    /// <summary>未发起解析（例如图片/音频，或解析功能未启用）。</summary>
    None = 0,

    /// <summary>正在解析（上传并轮询中）。</summary>
    Parsing = 1,

    /// <summary>解析成功，已得到 Markdown 文本。</summary>
    Parsed = 2,

    /// <summary>解析失败。</summary>
    Failed = 3
}
