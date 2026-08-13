using System;
using System.Collections.Generic;

namespace Athena.UI.Models;

public sealed record ContextFeatureSnapshot(
    string RequestId,
    string ModelProfileKey,
    int EstimatorVersion,
    long CjkTextChars,
    long OtherTextChars,
    long StructuredJsonChars,
    int SystemMessageCount,
    int UserMessageCount,
    int AssistantMessageCount,
    int ToolMessageCount,
    long ToolDeclarationChars,
    long AttachmentManifestChars,
    int ImageCount,
    int KnownDimensionImageCount,
    int UnknownDimensionImageCount,
    int ImageTileUnits,
    int AutoDetailImageCount,
    long ImagePriorTokens,
    long HeuristicEstimate,
    string FixedOverheadFingerprint,
    string ContextFingerprint,
    bool ImageBinaryIncluded,
    bool IsImageFallback);

public sealed record PreparedChatRequest(
    string RequestId,
    long ConversationRevision,
    Athena.UI.Services.Context.EffectiveRequestRuntimeSnapshot Runtime,
    IReadOnlyList<OpenAI.Chat.ChatMessage> Messages,
    OpenAI.Chat.ChatCompletionOptions Options,
    ContextFeatureSnapshot Features,
    string ContextFingerprint);

public sealed record CalibratedTokenEstimate(
    long MeanTokens,
    long DecisionTokens,
    double Confidence,
    string ProfileKey,
    int SampleCount);

/// <summary>
/// 自上一个精确锚点以来新增内容的 token 区间。误差以增量为界，而不是以整段上下文为界——
/// 这是它与 <see cref="CalibratedTokenEstimate"/> 的根本差别。
/// </summary>
public sealed record DeltaTokenEstimate(
    long Low,
    long Expected,
    long High,
    double Confidence,
    int SampleCount);

public sealed record ProviderInputModalityUsage(
    long? TextTokens = null,
    long? ImageTokens = null,
    long? AudioTokens = null);

public sealed class TokenCalibrationProfile
{
    public string ProfileKey { get; set; } = string.Empty;
    public int EstimatorVersion { get; set; } = 1;
    public int SampleCount { get; set; }
    public int CleanDeltaSampleCount { get; set; }
    public double TextScale { get; set; } = 1;
    public double JsonScale { get; set; } = 1;
    public double FixedBias { get; set; }
    public double MessageOverhead { get; set; } = 4;
    public double EwmaAbsoluteError { get; set; }
    public double Mape { get; set; }
    // 增量标度：actualInputDelta / deltaCharScore。单参数、单观测，没有偏置项与之互搏，
    // 因此不会出现 TextScale/FixedBias 相互抵消式发散。
    public double DeltaScale { get; set; } = 1;
    public int DeltaSampleCount { get; set; }
    public double DeltaMape { get; set; }
    public long MinimumObservedHeuristic { get; set; }
    public long MaximumObservedHeuristic { get; set; }
    public int ImageSampleCount { get; set; }
    public int CleanImageSampleCount { get; set; }
    public int DirectImageUsageSampleCount { get; set; }
    public int LowWeightImageSampleCount { get; set; }
    public double ImageScale { get; set; } = 1;
    public double ImageEwmaAbsoluteError { get; set; }
    public double ImageMape { get; set; }
    public DateTimeOffset LastUpdatedAtUtc { get; set; }
    public string KeyId { get; set; } = string.Empty;
}

public sealed class TokenCalibrationDocument
{
    public int SchemaVersion { get; set; } = 1;
    public string KeyId { get; set; } = string.Empty;
    public Dictionary<string, TokenCalibrationProfile> Profiles { get; set; } = new(StringComparer.Ordinal);
}

public sealed record TokenCalibrationDiagnostics(
    int ProfileCount,
    long TextSampleCount,
    long CleanDeltaSampleCount,
    long ImageSampleCount,
    long CleanImageSampleCount,
    long DirectImageUsageSampleCount,
    DateTimeOffset? LastUpdatedAtUtc,
    int CurrentEstimatorVersion,
    string KeyId);
