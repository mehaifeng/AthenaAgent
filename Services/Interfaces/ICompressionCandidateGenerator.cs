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

    Task<string?> GenerateAsync(
        string systemPrompt,
        string userPrompt,
        int maxOutputTokens,
        CancellationToken cancellationToken = default);
}
