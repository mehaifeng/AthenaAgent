using Athena.UI.Models;
using Athena.UI.Services.Interfaces;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace Athena.UI.Services.Context;

public sealed class CompressionValidator : ICompressionValidator
{
    /// <summary>摘要末尾结构化附录里句柄块的标题行；句柄只经这一个通道传递。</summary>
    public const string AppendixHeader = CompressionAppendix.HardFactsHeader;

    /// <summary>
    /// 验收只剩四件事：候选属于当前计划（Stale）、不是空文本或错误回包（Empty）、
    /// 摘要比它取代的材料短（InsufficientBenefit）、附件句柄一个不少（MissingHardAnchors）。
    /// 压缩比与 20% 收益这两道门已经取消——它们正是一轮 27.5 万 token 时每轮都弹「压缩未成功」的原因。
    /// 收益按字符比：压缩后比压缩前小，两边共有的未压缩部分互相抵消，所以只需比新摘要与它取代的东西——本次材料加上上一份摘要。
    /// 摘要长度不在这里否决：它由 max_output_tokens 在 API 一侧执行，一次已付费的调用不该因为长度被毙。
    /// </summary>
    public CompressionValidationResult Validate(
        CompressionPlan plan,
        CompressionCandidate candidate,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(plan.PlanId, candidate.PlanId, StringComparison.Ordinal)
            || plan.BaseRevision != candidate.BaseRevision
            || plan.PromptVersion != candidate.PromptVersion)
            return Result(CompressionValidationStatus.Stale, 0, 0, [], "Candidate does not match the current plan.");
        if (string.IsNullOrWhiteSpace(candidate.Summary)
            || candidate.Summary.StartsWith("error", StringComparison.OrdinalIgnoreCase)
            || candidate.Summary.StartsWith("[error", StringComparison.OrdinalIgnoreCase))
            return Result(CompressionValidationStatus.Empty, 0, 0, [], "Candidate summary is empty or an error response.");

        long summaryChars = candidate.Summary.Length;
        // 被取代的是「本次材料 + 上一份摘要」：新摘要合并了旧摘要（含附录），只拿材料比，
        // 第一次压缩之后新增材料一旦短于旧摘要，每次再压都会被误判成没有收益。
        var materialChars = MeasureMaterialChars(plan.Material) + (plan.ExistingSummary?.Length ?? 0);
        if (summaryChars >= materialChars)
            return Result(CompressionValidationStatus.InsufficientBenefit, summaryChars, materialChars, [],
                "The summary would not be smaller than the history it replaces.");

        // 句柄由生成侧结构化追加，这里只做兜底自检。摘要「写得全不全」不在验收范围内：
        // 那是质量问题，不是状态损坏，拿它一票否决只会让每次压缩都先花掉几十秒再判死。
        var missingHandles = ExtractHardAnchors(plan.Material)
            .Where(anchor => !candidate.Summary.Contains(anchor.Value, StringComparison.Ordinal))
            .ToArray();
        if (missingHandles.Length > 0)
            return Result(CompressionValidationStatus.MissingHardAnchors, summaryChars, materialChars,
                missingHandles,
                $"Candidate omitted {missingHandles.Length} attachment handle(s) the conversation can still be asked about.");

        return Result(CompressionValidationStatus.Valid, summaryChars, materialChars, [], string.Empty);
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
        => CompressionAppendix.Parse(text).HardFacts;

    /// <summary>
    /// 整份压缩材料的字符数：正文、工具调用 JSON、推理结论，加上附件引用。
    /// 与生成器渲染给压缩模型的内容同口径，不经过任何 token 换算。
    /// </summary>
    public static long MeasureMaterialChars(IReadOnlyList<CompressionMaterialMessage> material)
        => material.Sum(MeasureMaterialChars);

    private static long MeasureMaterialChars(CompressionMaterialMessage message)
    {
        long total = (message.Content?.Length ?? 0)
                     + (message.ToolCallsJson?.Length ?? 0)
                     + (message.ReasoningContent?.Length ?? 0)
                     + 32; // 消息头（角色、ID）
        total += message.Attachments.Sum(item => 64L + item.Id.Length + item.FileName.Length + item.StoredPath.Length);
        return total;
    }

    private static CompressionValidationResult Result(
        CompressionValidationStatus status,
        long summaryChars,
        long materialChars,
        IReadOnlyList<CompressionHardAnchor> missing,
        string error) => new(status, summaryChars, materialChars, missing, error);
}
