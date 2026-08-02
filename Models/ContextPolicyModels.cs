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
    private long _targetSummaryTokens = 8192;
}

public sealed class WorkspaceContextPolicyOverride
{
    public long? ContextCapTokens { get; set; }
    public bool? AutoCompress { get; set; }
    public long? CompressionThresholdTokens { get; set; }
    public int? KeepRecentRounds { get; set; }
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
    ContextPolicyValueSource ContextWindowSource,
    ContextPolicyValueSource CompressionThresholdSource,
    IReadOnlyList<string> Warnings)
{
    public string Identity => string.Join(':',
        ModelContextWindowTokens,
        ContextWindowTokens,
        OutputReserveTokens,
        SafetyMarginTokens,
        AvailableInputBudgetTokens,
        CompressionThresholdTokens,
        AutoCompress,
        KeepRecentRounds,
        TargetSummaryTokens);
}
