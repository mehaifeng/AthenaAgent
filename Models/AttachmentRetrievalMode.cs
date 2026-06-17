namespace Athena.UI.Models;

/// <summary>
/// 文本/文档类附件进入模型上下文的方式。
/// </summary>
public enum AttachmentRetrievalMode
{
    /// <summary>内容较小，直接内联进消息（ExtractedText 全文）。</summary>
    Inline = 0,

    /// <summary>内容过大，仅注入“清单卡 + 文件指针”，由模型用文件系统工具按需读取。</summary>
    Deferred = 1
}
