using CommunityToolkit.Mvvm.ComponentModel;
using System;
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
    IReadOnlyList<string> Warnings,
    long MaxOutputCeilingTokens = 0)
{
    /// <summary>
    /// 单次压缩能吃下的历史上限：摘要长度封顶时，材料最多是它的 <see cref="SummaryRatio"/> 倍。
    /// 这个值不随压缩阈值变化——它由模型一次能输出多长决定，所以提高阈值只会增加压缩趟数。
    /// </summary>
    public long MaxMaterialPerPassTokens => TargetSummaryTokens * SummaryRatio;

    /// <summary>
    /// 本次请求发给供应商的 max_output_tokens。
    ///
    /// 它和 <see cref="OutputReserveTokens"/> 是两件事，此前被同一个数字兼任，代价是模型在
    /// 1M 窗口上也只能写 16K：
    /// - <see cref="OutputReserveTokens"/> 是**预算保留额**——从窗口里划给输出的那一块，必须保守，
    ///   因为它直接从输入预算里扣（窗口越小越致命），也参与执行身份与校准 profile。
    /// - 这里返回的是**本次请求的输出上限**——窗口在装下这次输入之后还剩多少，取模型元数据允许的
    ///   上限与之较小者。留着不用的窗口没有任何意义，不如交给模型。
    ///
    /// 下界永远是 <see cref="OutputReserveTokens"/>：输入预算保证了输入不会超过
    /// <see cref="AvailableInputBudgetTokens"/>，所以余量天然不低于保留额，既有保证一分不少。
    /// 元数据没给出模型输出上限时（Ceiling=0）退回保留额，即改动前的行为。
    /// </summary>
    public long ResolveRequestOutputTokens(long requestInputTokens)
    {
        if (MaxOutputCeilingTokens <= 0) return OutputReserveTokens;
        var headroom = ContextWindowTokens - SafetyMarginTokens - Math.Max(0, requestInputTokens);
        return Math.Max(OutputReserveTokens, Math.Min(MaxOutputCeilingTokens, headroom));
    }

    /// <summary>
    /// 预算摘要（三处诊断面板共用，别再各写一份）：W 窗口 · R 输出保留额 · O 单次输出上限
    /// （元数据给出时才显示——R 与 O 不是一回事，看不到 O 的话「元数据写着 384K 为什么只能写 16K」
    /// 就无从解释）· S 安全余量 · B 输入预算 · T 压缩阈值。
    /// </summary>
    public string BudgetSummary =>
        $"W {ContextWindowTokens:N0} · R {OutputReserveTokens:N0} · "
        + (MaxOutputCeilingTokens > 0 ? $"O {MaxOutputCeilingTokens:N0} · " : string.Empty)
        + $"S {SafetyMarginTokens:N0} · B {AvailableInputBudgetTokens:N0} · T {CompressionThresholdTokens:N0}";

    public string Identity => string.Join(':',
        ModelContextWindowTokens,
        ContextWindowTokens,
        OutputReserveTokens,
        MaxOutputCeilingTokens,
        SafetyMarginTokens,
        AvailableInputBudgetTokens,
        CompressionThresholdTokens,
        AutoCompress,
        KeepRecentRounds,
        TargetSummaryTokens,
        SummaryRatio);
}
