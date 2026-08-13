using System;

namespace Athena.UI.Models;

/// <summary>
/// 一次由供应商回报的精确输入测量，绑定到它所度量的那段消息前缀。
/// <para>
/// 与估算的本质区别：锚点是<b>测量结果</b>。只要请求所处的 regime 未变
/// （<see cref="ProfileKey"/> 覆盖 provider/模型/协议/分词器/工具声明，
/// <see cref="FixedOverheadFingerprint"/> 覆盖系统提示等固定开销），
/// 且当前上下文仍以同一段前缀开头，<see cref="InputTokens"/> 就是该前缀的精确 token 数——
/// 回溯、分支、切换会话、重启应用之后都依然成立，无需重新估算。
/// </para>
/// </summary>
public sealed class ContextAnchorRecord
{
    /// <summary>被度量前缀的消息条数。</summary>
    public int PrefixMessageCount { get; set; }

    /// <summary>被度量前缀的消息 ID 序列摘要；回溯/分支后据此确认前缀确实逐条一致。</summary>
    public string PrefixDigest { get; set; } = string.Empty;

    /// <summary>供应商回报的输入 token（权威值）。</summary>
    public long InputTokens { get; set; }

    public long CachedInputTokens { get; set; }

    public long OutputTokens { get; set; }

    /// <summary>
    /// 校准分桶键（<c>ContextFeatureSnapshot.ModelProfileKey</c>）：
    /// provider|host|model|tokenizer|formatVersion|protocol|imageEncVersion|toolFingerprint。
    /// 换模型、换协议、开关 MCP/技能都会改变它，旧测量随即不可直接复用。
    /// </summary>
    public string ProfileKey { get; set; } = string.Empty;

    /// <summary>系统提示等固定开销的指纹（时间戳/GUID 已归一化）。</summary>
    public string FixedOverheadFingerprint { get; set; } = string.Empty;

    /// <summary>产生该测量时的会话修订号，仅用于诊断。</summary>
    public long Revision { get; set; }

    public DateTimeOffset ObservedAtUtc { get; set; }
}
