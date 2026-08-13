using Athena.UI.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Athena.UI.Services.Context;

/// <summary>
/// 压缩可行性的零成本判定。全部输入都能从本地字符统计得出，所以它必须跑在任何模型调用
/// 之前：一次注定被验收拒绝的尝试要花 20–175 秒并完整计费，事后才发现不可行等于白烧。
/// <para>
/// 分两个层面。<b>生成形状</b>（压缩比、Critical 锚点占比）说的是「这份材料本身能不能被
/// 摘要承载」，与上下文无关，生成器自己就该拒绝；<b>收益门槛</b>说的是「压了值不值」，
/// 取决于整段上下文有多大，属于规划期的策略问题。混在一起会让生成器拒绝它无权评判的活。
/// </para>
/// </summary>
public static class CompressionFeasibility
{
    /// <summary>
    /// 压缩强度未知时的兜底压缩比，对应 <see cref="CompressionStrength.Balanced"/>。
    /// 真实事故里的比例是 150,876 → 12,000（12.6:1）——在那个比例下模型既要覆盖全部事实
    /// 又要守住预算，实际通过率接近零。
    /// </summary>
    public const double DefaultFeasibleRatio = 8.0;

    /// <summary>句柄附录允许占用的目标预算比例；超出说明这份材料本就无法被摘要承载。</summary>
    public const double MaxHandleAnchorBudgetFraction = 0.30;

    /// <summary>收益门槛。与 <see cref="CompressionValidator"/> 共用同一套算式，避免两处漂移。</summary>
    public static long RequiredBenefitTokens(long preCompressionEstimate)
        => Math.Max(1_024L, (long)Math.Ceiling(preCompressionEstimate * 0.20));

    /// <summary>生成形状判定：只看这份材料能否被摘要承载，不涉及收益策略。</summary>
    public static CompressionFeasibilityVerdict Evaluate(CompressionPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return Evaluate(
            CompressionValidator.EstimateMaterialTokens(plan.Material),
            plan.TargetSummaryTokens,
            CompressionValidator.ExtractHardAnchors(plan.Material),
            summaryRatio: plan.MainModelPolicy.SummaryRatio);
    }

    public static CompressionFeasibilityVerdict Evaluate(
        long materialTokens,
        long targetTokens,
        IReadOnlyList<CompressionHardAnchor> anchors,
        long? requiredBenefitTokens = null,
        double? summaryRatio = null)
    {
        var maxRatio = summaryRatio is > 0 ? summaryRatio.Value : DefaultFeasibleRatio;
        // 附录逐条列出句柄，成本可以精确预算，不必猜。
        var handleTokens = anchors.Sum(anchor => (long)ConversationContext.EstimateTokens(anchor.Value) + 2);
        var ratio = targetTokens <= 0 ? double.PositiveInfinity : materialTokens / (double)targetTokens;
        var projectedBenefit = Math.Max(0, materialTokens - targetTokens);

        CompressionFeasibilityVerdict Verdict(bool feasible, string reason) => new(
            feasible, reason, materialTokens, targetTokens, ratio,
            anchors.Count, handleTokens, projectedBenefit);

        if (materialTokens <= 0 || targetTokens < 128)
            return Verdict(false, "Material or target budget is too small to compress.");

        if (ratio > maxRatio)
            return Verdict(false,
                $"Required compression ratio {ratio:0.0}:1 exceeds the configured strength {maxRatio:0.0}:1.");

        if (handleTokens > targetTokens * MaxHandleAnchorBudgetFraction)
            return Verdict(false,
                $"{anchors.Count} attachment handles need {handleTokens} tokens, over "
                + $"{MaxHandleAnchorBudgetFraction:P0} of the {targetTokens}-token target.");

        // 规划期才传收益门槛：把 InsufficientBenefit 从「生成之后才发现」提前到「发请求之前」。
        if (requiredBenefitTokens is { } required && projectedBenefit < required)
            return Verdict(false,
                $"Projected benefit {projectedBenefit} tokens is below the required {required}.");

        return Verdict(true, string.Empty);
    }
}
