using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.Generic;

namespace Athena.UI.Models;

public enum ContextPolicyMode
{
    Auto,
    CustomCap,
    LegacyCustom
}

public enum CompressionThresholdMode
{
    Auto,
    Custom
}

/// <summary>
/// 压缩强度：一次压缩把多少历史浓缩成一份摘要。这是用户真正在意的取舍——
/// 摘要保留多少细节，与压缩多久打断一次对话，是同一枚硬币的两面。
/// <para>
/// 它取代了旧的「摘要目标 Token」。那是个绝对值，既看不出含义，又和模型的单次输出
/// 能力隐式耦合；材料只有 2,000 token 时仍按 12,000 去要摘要，结果是压完反而更大。
/// 现在摘要长度由「材料 ÷ 强度」自动得出，只受模型输出能力与可选上限约束。
/// </para>
/// </summary>
public enum CompressionStrength
{
    /// <summary>保守 4:1——摘要更详细，但每次吃下的历史更少，压缩触发得更频繁。</summary>
    Conservative,

    /// <summary>平衡 8:1——默认。</summary>
    Balanced,

    /// <summary>激进 16:1——一次吃掉更多历史、打断更少，代价是摘要更粗、细节丢得更多。</summary>
    Aggressive
}

public static class CompressionStrengthExtensions
{
    /// <summary>摘要压缩比：材料 token ÷ 摘要 token。</summary>
    public static int SummaryRatio(this CompressionStrength strength) => strength switch
    {
        CompressionStrength.Conservative => 4,
        CompressionStrength.Aggressive => 16,
        _ => 8
    };
}

/// <summary>应用级上下文默认策略；不携带任何会话运行状态。</summary>
public partial class AppContextPolicy : ObservableObject
{
    [ObservableProperty]
    private ContextPolicyMode _mode = ContextPolicyMode.Auto;

    [ObservableProperty]
    private long? _customCapTokens;

    [ObservableProperty]
    private CompressionThresholdMode _compressionThresholdMode = CompressionThresholdMode.Auto;

    [ObservableProperty]
    private long? _customCompressionThresholdTokens;

    [ObservableProperty]
    private bool _autoCompress = true;

    [ObservableProperty]
    private int _keepRecentRounds = 3;

    [ObservableProperty]
    private CompressionStrength _compressionStrength = CompressionStrength.Balanced;

    /// <summary>
    /// 摘要长度的<b>上限</b>，不是目标值。实际长度由「材料 ÷ 压缩强度」得出；
    /// 这里只在你想额外压低单次成本与等待时长时才起作用。
    /// </summary>
    [ObservableProperty]
    private long _targetSummaryTokens = 8192;
}

public sealed class WorkspaceContextPolicyOverride
{
    public long? ContextCapTokens { get; set; }
    public bool? AutoCompress { get; set; }
    public long? CompressionThresholdTokens { get; set; }
    public int? KeepRecentRounds { get; set; }
    public CompressionStrength? CompressionStrength { get; set; }
    public long? TargetSummaryTokens { get; set; }
    public int? WorkspaceKnowledgeTokenBudget { get; set; }
}

public enum ContextPolicyValueSource
{
    ModelMetadata,
    AppDefault,
    AppOverride,
    WorkspaceOverride,
    ApplicationDefaultAssumption
}

public sealed record ResolvedContextPolicy(
    long ModelContextWindowTokens,
    long ContextWindowTokens,
    long OutputReserveTokens,
    long SafetyMarginTokens,
    long AvailableInputBudgetTokens,
    long CompressionThresholdTokens,
    bool AutoCompress,
    int KeepRecentRounds,
    long TargetSummaryTokens,
    int SummaryRatio,
    ContextPolicyValueSource ContextWindowSource,
    ContextPolicyValueSource CompressionThresholdSource,
    IReadOnlyList<string> Warnings)
{
    /// <summary>
    /// 单次压缩能吃下的历史上限：摘要长度封顶时，材料最多是它的 <see cref="SummaryRatio"/> 倍。
    /// 这个值不随压缩阈值变化——它由模型一次能输出多长决定，所以提高阈值只会增加压缩趟数。
    /// </summary>
    public long MaxMaterialPerPassTokens => TargetSummaryTokens * SummaryRatio;

    public string Identity => string.Join(':',
        ModelContextWindowTokens,
        ContextWindowTokens,
        OutputReserveTokens,
        SafetyMarginTokens,
        AvailableInputBudgetTokens,
        CompressionThresholdTokens,
        AutoCompress,
        KeepRecentRounds,
        TargetSummaryTokens,
        SummaryRatio);
}
