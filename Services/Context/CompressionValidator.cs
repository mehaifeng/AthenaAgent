using Athena.UI.Models;
using Athena.UI.Services.Interfaces;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace Athena.UI.Services.Context;

public sealed class CompressionValidator : ICompressionValidator
{
    /// <summary>摘要末尾结构化附录的标题行；句柄只经这一个通道传递。</summary>
    public const string AppendixHeader = "[hard_facts]";

    public CompressionValidationResult Validate(
        CompressionPlan plan,
        CompressionCandidate candidate,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(plan.PlanId, candidate.PlanId, StringComparison.Ordinal)
            || plan.BaseRevision != candidate.BaseRevision
            || plan.PromptVersion != candidate.PromptVersion)
            return Result(CompressionValidationStatus.Stale, 0, plan.PreCompressionEstimate, 0, [], "Candidate does not match the current plan.");
        if (string.IsNullOrWhiteSpace(candidate.Summary)
            || candidate.Summary.StartsWith("error", StringComparison.OrdinalIgnoreCase)
            || candidate.Summary.StartsWith("[error", StringComparison.OrdinalIgnoreCase))
            return Result(CompressionValidationStatus.Empty, 0, plan.PreCompressionEstimate, 0, [], "Candidate summary is empty or an error response.");

        var summaryTokens = ConversationContext.EstimateTokens(candidate.Summary);
        if (summaryTokens > plan.TargetSummaryTokens)
            return Result(CompressionValidationStatus.OverBudget, summaryTokens, plan.PreCompressionEstimate, 0, [], "Candidate exceeds the target summary budget.");

        var compressedTokens = EstimateMaterialTokens(plan.Material);
        var postEstimate = Math.Max(0, plan.PreCompressionEstimate - compressedTokens) + summaryTokens;
        var benefit = Math.Max(0, plan.PreCompressionEstimate - postEstimate);
        var requiredBenefit = CompressionFeasibility.RequiredBenefitTokens(plan.PreCompressionEstimate);
        if (benefit < requiredBenefit)
            return Result(CompressionValidationStatus.InsufficientBenefit, summaryTokens, postEstimate, benefit, [], "Candidate does not provide material compression benefit.");

        // 句柄由生成侧结构化追加，这里只做兜底自检。摘要「写得全不全」不在验收范围内：
        // 那是质量问题，不是状态损坏，拿它一票否决只会让每次压缩都先花掉几十秒再判死。
        var missingHandles = ExtractHardAnchors(plan.Material)
            .Where(anchor => !candidate.Summary.Contains(anchor.Value, StringComparison.Ordinal))
            .ToArray();
        if (missingHandles.Length > 0)
            return Result(CompressionValidationStatus.MissingHardAnchors, summaryTokens, postEstimate, benefit,
                missingHandles,
                $"Candidate omitted {missingHandles.Length} attachment handle(s) the conversation can still be asked about.");

        return Result(CompressionValidationStatus.Valid, summaryTokens, postEstimate, benefit, [], string.Empty);
    }

    /// <summary>
    /// 材料里的句柄：附件 id 与它在磁盘上的落点。取自消息上的结构化附件元数据，不靠正则去文本里猜。
    /// <para>
    /// 这里曾经还抽 url / path / error / number / constraint / tool_call_id / command，并要求摘要逐字保留：
    /// 一段真实会话抽出 129 条，模型自然复述率 6%–15%，于是每次压缩都先花掉 30–100 秒再被判死；
    /// 就算全塞进附录，那也是一串脱离上下文的裸标识符，占着预算却无法参与推理。tool_call_id 更是纯废字——
    /// 规划器只压完整轮次，调用和结果一起消失，请求里没有任何东西再引用它。
    /// 这些内容的价值在于和「发生了什么」绑在一起被叙述出来，因此交给压缩提示词，不做结构化保证。
    /// </para>
    /// </summary>
    public static IReadOnlyList<CompressionHardAnchor> ExtractHardAnchors(
        IReadOnlyList<CompressionMaterialMessage> material)
    {
        var anchors = new Dictionary<string, CompressionHardAnchor>(StringComparer.Ordinal);
        void Add(string kind, string? value)
        {
            value = value?.Trim();
            if (!string.IsNullOrWhiteSpace(value))
                anchors.TryAdd(kind + "" + value, new CompressionHardAnchor(kind, value));
        }

        foreach (var message in material)
        {
            foreach (var attachment in message.Attachments)
            {
                Add("attachment_id", attachment.Id);
                Add("attachment_path", attachment.StoredPath);
            }
        }
        return anchors.Values.ToArray();
    }

    /// <summary>
    /// 从既有摘要里取回句柄：只认我们自己写下的附录块，不做正则猜测。
    /// 附录是句柄唯一的传递通道——旧实现用正则去摘要正文里重新抽锚点，等于让上一轮的附录
    /// 变成下一轮必须保留的锚点，清单只增不减，压得越多摘要里的裸标识符越多。
    /// <para>
    /// 一行一个值，且扫描文本里的每一个附录块：reduce 层可能把上一层的附录抄进正文，
    /// 之后生成侧又追加一份，只认第一块会把另一半句柄留在后面。
    /// 更早的版本曾在同一行用逗号分隔多个值，那种摘要在这里会退化成一条合并后的怪值；
    /// 它仍会被原样写进新附录并继续传下去，不会让压缩失败，新附件也不受影响。
    /// </para>
    /// </summary>
    internal static IReadOnlyList<CompressionHardAnchor> ExtractHardAnchorsFromText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];

        var anchors = new Dictionary<string, CompressionHardAnchor>(StringComparer.Ordinal);
        var cursor = 0;
        while (true)
        {
            var header = text.IndexOf(AppendixHeader, cursor, StringComparison.Ordinal);
            if (header < 0) break;
            cursor = header + AppendixHeader.Length;
            foreach (var line in text[cursor..].Split('\n'))
            {
                var entry = line.Trim();
                if (entry.Length == 0) continue;
                var separator = entry.IndexOf(':', StringComparison.Ordinal);
                if (separator <= 0) break;
                var kind = entry[..separator].Trim();
                // 附录之后可能还跟着别的内容；遇到不认识的行就停，不去正文里乱抓。
                // 下一个附录块的标题行没有冒号，同样在这里收住，交给外层循环接着找。
                if (kind is not ("attachment_id" or "attachment_path")) break;
                var value = entry[(separator + 1)..].Trim();
                if (value.Length == 0) continue;
                anchors.TryAdd(kind + "" + value, new CompressionHardAnchor(kind, value));
            }
        }
        return anchors.Values.ToArray();
    }

    /// <summary>整份压缩材料的估算 token 数。可行性判定与收益计算共用同一把尺。</summary>
    public static long EstimateMaterialTokens(IReadOnlyList<CompressionMaterialMessage> material)
        => material.Sum(EstimateMaterialTokens);

    private static long EstimateMaterialTokens(CompressionMaterialMessage message)
    {
        long total = ConversationContext.EstimateTokens(message.Content)
                     + ConversationContext.EstimateTokens(message.ToolCallsJson)
                     + ConversationContext.EstimateTokens(message.ReasoningContent)
                     + 8;
        total += message.Attachments.Sum(item =>
            (long)ConversationContext.AttachmentManifestTokenCost
            + (item.Kind == AttachmentKind.Image ? ConversationContext.EstimateImageTokens(item.Width, item.Height) : 0));
        return total;
    }

    private static CompressionValidationResult Result(
        CompressionValidationStatus status,
        long summaryTokens,
        long postEstimate,
        long benefit,
        IReadOnlyList<CompressionHardAnchor> missing,
        string error) => new(status, summaryTokens, postEstimate, benefit, missing, error);
}
