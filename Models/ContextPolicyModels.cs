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

    /// <summary>
    /// 摘要长度上限，直接作为压缩请求的 max_output_tokens。全量压缩不再按「材料 ÷ 强度」推算长度，
    /// 所以这里是唯一的长度旋钮；实际生效值还要被压缩模型的输出能力与阈值的 1/4 再夹一次。
    /// </summary>
    [ObservableProperty]
    private long _summaryMaxTokens = DefaultSummaryMaxTokens;

    /// <summary>超过阈值时，先把旧工具结果换成占位说明（零模型成本），不够再做全量摘要。</summary>
    [ObservableProperty]
    private bool _toolResultClearingEnabled = true;

    /// <summary>清理时原样保留的最近工具结果总量（字符）。保留区之外的全部清掉。</summary>
    [ObservableProperty]
    private long _keepRecentToolResultChars = DefaultKeepRecentToolResultChars;

    public const long DefaultSummaryMaxTokens = 8192;
    public const long MinSummaryMaxTokens = 1024;
    public const long MaxSummaryMaxTokens = 32_768;
    public const long DefaultKeepRecentToolResultChars = 120_000;
    public const long MinKeepRecentToolResultChars = 20_000;
    public const long MaxKeepRecentToolResultChars = 800_000;
}

public sealed class WorkspaceContextPolicyOverride
{
    public long? ContextCapTokens { get; set; }
    public bool? AutoCompress { get; set; }
    public long? CompressionThresholdTokens { get; set; }
    public long? SummaryMaxTokens { get; set; }

    /// <summary>
    /// 旧键 <c>targetSummaryTokens</c>（上限语义）的只读入口：读到就当作 <see cref="SummaryMaxTokens"/>，
    /// 永不写出（getter 恒为 null，且 WhenWritingNull）。旧的 <c>keepRecentRounds</c> /
    /// <c>compressionStrength</c> 没有对应属性，反序列化时被忽略即丢弃。
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("targetSummaryTokens")]
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public long? LegacyTargetSummaryTokens
    {
        get => null;
        set => SummaryMaxTokens ??= value;
    }

    public bool? ToolResultClearingEnabled { get; set; }
    public long? KeepRecentToolResultChars { get; set; }
    public int? WorkspaceKnowledgeCharBudget { get; set; }

    /// <summary>旧键 <c>workspaceKnowledgeTokenBudget</c>（token）的只读入口：读到就按 ×3 换成字符预算，永不写出。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("workspaceKnowledgeTokenBudget")]
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public int? LegacyWorkspaceKnowledgeTokenBudget
    {
        get => null;
        set => WorkspaceKnowledgeCharBudget ??= value is { } tokens ? checked(tokens * 3) : null;
    }
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
    long SummaryMaxTokens,
    bool ToolResultClearingEnabled,
    long KeepRecentToolResultChars,
    ContextPolicyValueSource ContextWindowSource,
    ContextPolicyValueSource CompressionThresholdSource,
    IReadOnlyList<string> Warnings,
    long MaxOutputCeilingTokens = 0)
{
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
        SummaryMaxTokens,
        ToolResultClearingEnabled,
        KeepRecentToolResultChars);
}
