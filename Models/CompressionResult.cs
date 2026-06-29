using System;
using System.Collections.Generic;

namespace Athena.UI.Models;

/// <summary>
/// 上下文压缩结果。
/// </summary>
public sealed class CompressionResult
{
    /// <summary>合并后的摘要文本（已含 <c>[Summary]:</c> 前缀；无需压缩时为 null）。</summary>
    public string? Summary { get; init; }

    /// <summary>本次被压缩（归档进摘要）的消息条数。</summary>
    public int CompressedCount { get; init; }

    /// <summary>本次被标记为已压缩的消息引用，供撤销使用。</summary>
    public IReadOnlyList<ChatMessage> CompressedMessages { get; init; } = Array.Empty<ChatMessage>();

    /// <summary>是否走了本地兜底（次级模型不可用或 AI 摘要失败）。</summary>
    public bool UsedFallback { get; init; }

    /// <summary>表示"未发生压缩"的空结果。</summary>
    public static CompressionResult None { get; } = new();
}
