using Athena.UI.Models;
using Athena.UI.Services.Interfaces;
using System;
using System.Collections.Generic;

namespace Athena.UI.Services.Context;

public sealed class ModelContextPolicyResolver : IModelContextPolicyResolver
{
    public ResolvedContextPolicy Resolve(
        ResolvedModelMetadata model,
        AppContextPolicy app,
        WorkspaceContextPolicyOverride? workspace,
        AiModelRole role)
    {
        var warnings = new List<string>();
        var modelWindow = model.ContextWindowTokens.Value;
        if (modelWindow < 1024) throw new InvalidOperationException("Chat context window must be at least 1024 tokens.");

        long? selectedCap = workspace?.ContextCapTokens;
        var capSource = workspace?.ContextCapTokens.HasValue == true
            ? ContextPolicyValueSource.WorkspaceOverride
            : (app.Mode is ContextPolicyMode.CustomCap or ContextPolicyMode.LegacyCustom) && app.CustomCapTokens.HasValue
                ? ContextPolicyValueSource.AppOverride
                : ContextPolicyValueSource.ModelMetadata;
        if (!selectedCap.HasValue && app.Mode is ContextPolicyMode.CustomCap or ContextPolicyMode.LegacyCustom)
            selectedCap = app.CustomCapTokens;
        if (selectedCap is < 1024)
        {
            warnings.Add(ModelWarnings.InvalidContextCapIgnored);
            selectedCap = null;
            capSource = ContextPolicyValueSource.ModelMetadata;
        }
        var window = Math.Min(modelWindow, selectedCap ?? long.MaxValue);

        var minInputReserve = Math.Min(4096L, Math.Max(512L, FloorPercent(window, 25)));
        var safety = Math.Min(32_768L, Math.Max(256L, CeilPercent(window, 5)));
        var maxOutputAllowed = checked(window - minInputReserve - safety);
        if (maxOutputAllowed < 0) throw new InvalidOperationException("Context window cannot reserve positive input and safety budgets.");
        var roleOutput = GetRoleRequestedOutput(role);
        var modelOutput = model.MaxCompletionTokens.Value ?? long.MaxValue;
        var output = Math.Min(roleOutput, Math.Min(modelOutput, maxOutputAllowed));
        if (output < 0) throw new InvalidOperationException("Resolved output reserve is invalid.");
        // 保留额之外再记一个「这个模型单次最多能写多长」。保留额必须保守（它从输入预算里扣），
        // 但请求上限没有理由跟着它一起缩：窗口装下输入之后剩下的那部分，白留着也是浪费。
        // 元数据没给出输出上限时留 0 = 未知，由 ResolveRequestOutputTokens 退回保留额。
        var outputCeiling = model.MaxCompletionTokens.Value.HasValue
            ? Math.Max(output, Math.Min(model.MaxCompletionTokens.Value.Value, maxOutputAllowed))
            : 0L;
        var inputBudget = checked(window - output - safety);
        if (inputBudget < minInputReserve) throw new InvalidOperationException("Resolved input budget is below the minimum reserve.");

        long? explicitThreshold = workspace?.CompressionThresholdTokens;
        var thresholdSource = workspace?.CompressionThresholdTokens.HasValue == true
            ? ContextPolicyValueSource.WorkspaceOverride
            : app.CompressionThresholdMode == CompressionThresholdMode.Custom && app.CustomCompressionThresholdTokens.HasValue
                ? ContextPolicyValueSource.AppOverride
                : model.ContextWindowTokens.Source == MetadataValueSource.ApplicationDefault
                    ? ContextPolicyValueSource.ApplicationDefaultAssumption
                    : ContextPolicyValueSource.AppDefault;
        if (!explicitThreshold.HasValue
            && app.CompressionThresholdMode == CompressionThresholdMode.Custom)
            explicitThreshold = app.CustomCompressionThresholdTokens;

        long threshold;
        if (explicitThreshold.HasValue)
        {
            if (explicitThreshold <= 0)
            {
                warnings.Add(ModelWarnings.InvalidCompressionThresholdIgnored);
                threshold = DefaultThreshold(model, inputBudget);
                thresholdSource = model.ContextWindowTokens.Source == MetadataValueSource.ApplicationDefault
                    ? ContextPolicyValueSource.ApplicationDefaultAssumption
                    : ContextPolicyValueSource.AppDefault;
            }
            else
            {
                threshold = Math.Min(explicitThreshold.Value, inputBudget);
                if (explicitThreshold > inputBudget) warnings.Add(ModelWarnings.CompressionThresholdClamped);
            }
        }
        else
        {
            threshold = DefaultThreshold(model, inputBudget);
        }

        var keep = Math.Clamp(workspace?.KeepRecentRounds ?? app.KeepRecentRounds, 1, 50);
        var target = Math.Clamp(workspace?.TargetSummaryTokens ?? app.TargetSummaryTokens, 128, 65_536);
        var ratio = (workspace?.CompressionStrength ?? app.CompressionStrength).SummaryRatio();
        var autoCompress = workspace?.AutoCompress ?? app.AutoCompress;
        if (selectedCap.HasValue && selectedCap > modelWindow) warnings.Add(ModelWarnings.ContextCapClampedToModel);
        return new ResolvedContextPolicy(
            modelWindow, window, output, safety, inputBudget, threshold, autoCompress, keep, target, ratio,
            capSource, thresholdSource, warnings, outputCeiling);
    }

    private static long DefaultThreshold(ResolvedModelMetadata model, long inputBudget) =>
        model.ContextWindowTokens.Source == MetadataValueSource.ApplicationDefault
            ? Math.Min(Athena.UI.Services.ModelMetadata.ModelMetadataResolver.UnknownCompressionThresholdTokens, inputBudget)
            : FloorPercent(inputBudget, 80);

    private static long GetRoleRequestedOutput(AiModelRole role) => role switch
    {
        AiModelRole.Approval => 256,
        AiModelRole.KnowledgeMaintenance or AiModelRole.ImageRecognition => 4096,
        AiModelRole.Embedding => 0,
        _ => 16_000
    };

    private static long FloorPercent(long value, long percent)
        => checked((value / 100) * percent + (value % 100) * percent / 100);

    private static long CeilPercent(long value, long percent)
        => checked((value / 100) * percent + ((value % 100) * percent + 99) / 100);
}
