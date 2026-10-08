using System;

namespace Athena.UI.Models;

/// <summary>
/// 会话用量显示状态的落盘形状：供应商最后一次回报的用量，以及当时它是否仍描述着这段上下文。
/// 它是会话状态而不是消息字段，所以不放在 <c>ChatMessage</c> 上、也就不碰消息持久化白名单。
/// 重启后打开会话直接显示它，不必等下一次请求重新测量；null 表示这段会话从未收到过 usage。
/// </summary>
public sealed class ConversationUsageRecord
{
    /// <summary>为真表示落盘时显示的是实测值；为假表示当时处于「待测」（压缩/清理/撤销/回退之后）。</summary>
    public bool Measured { get; set; }

    /// <summary>最后一次实测的 input + output tokens；待测时保留，作为压缩节省角标的被减数。</summary>
    public long CurrentTokens { get; set; }

    public long CachedInputTokens { get; set; }

    /// <summary>待测期间的可信下界（压缩摘要的实测 output tokens），没有则为 0。</summary>
    public long LowerBoundTokens { get; set; }

    public DateTimeOffset? LastUsageAt { get; set; }

    public string? LastRequestId { get; set; }

    /// <summary>回报这次用量的供应商与模型（<c>provider\u001fmodel</c>）；恢复时与当前主模型不一致就降为待测。</summary>
    public string ModelFingerprint { get; set; } = string.Empty;

    public ConversationUsageRecord Clone() => (ConversationUsageRecord)MemberwiseClone();
}
