using Athena.UI.Models;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Athena.UI.Services.Interfaces;

public interface ICompressionCandidateGenerator
{
    /// <summary>
    /// 生成压缩候选。<paramref name="onProgress"/> 排在取消令牌之后，是为了让既有的
    /// 位置调用（plan, token）保持编译通过；新调用点请用具名实参。
    /// </summary>
    Task<CompressionGenerationResult> GenerateAsync(
        CompressionPlan plan,
        CancellationToken cancellationToken = default,
        Action<CompressionProgress>? onProgress = null);
}

public interface ICompressionTextGenerator
{
    string ModelFingerprint { get; }

    /// <summary>
    /// 最近一次 <see cref="GenerateAsync"/> 调用供应商回报的 usage。压缩模型调用本身也消耗 token，
    /// 它的 input tokens 是被压材料大小的上界、output tokens 是摘要大小的精确值——
    /// 两者之差是压缩释放空间的审计下界，用于待测期间给出即时节省角标和自动压缩防抖门槛。
    /// 供应商不报 usage 时为 null。
    /// </summary>
    TokenUsageSnapshot? LastUsage { get; }

    Task<string?> GenerateAsync(
        string systemPrompt,
        string userPrompt,
        int maxOutputTokens,
        CancellationToken cancellationToken = default);
}
