using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Athena.UI.Models;

public enum CapabilitySupport
{
    Unknown,
    Supported,
    Unsupported
}

public sealed record OpenRouterArchitecture(
    HashSet<string> InputModalities,
    HashSet<string> OutputModalities,
    string? Tokenizer,
    string? InstructType);

public sealed record OpenRouterTopProvider(long? ContextLength, long? MaxCompletionTokens);

public sealed record OpenRouterPricing(string? Prompt, string? Completion, string? Image);

public sealed record OpenRouterModelMetadata(
    string Id,
    string? CanonicalSlug,
    string Name,
    long? CreatedUnixSeconds,
    string? Description,
    long? ContextLength,
    OpenRouterArchitecture Architecture,
    OpenRouterTopProvider? TopProvider,
    OpenRouterPricing? Pricing,
    HashSet<string> SupportedParameters,
    IReadOnlyDictionary<string, JsonElement>? DefaultParameters,
    DateTimeOffset? ExpirationDate,
    JsonElement? Raw = null);

public sealed record OpenRouterCatalogSnapshot(
    int SchemaVersion,
    string CatalogRevision,
    DateTimeOffset FetchedAtUtc,
    string SourceUrl,
    string ContentHash,
    string? ETag,
    IReadOnlyList<OpenRouterModelMetadata> Models)
{
    public static OpenRouterCatalogSnapshot Empty { get; } = new(
        1, "empty", DateTimeOffset.MinValue,
        "https://openrouter.ai/api/v1/models?output_modalities=text", "", null, []);
}

public sealed record ExternalModelIdentity(
    string ProviderId,
    string? ProviderPreset,
    string? BaseUrlHost,
    string ExternalModelId,
    string? DisplayName = null);

public enum ModelMatchStatus
{
    Matched,
    Ambiguous,
    Unmatched,
    PinnedModelMissing,
    CustomOnly
}

public sealed record ModelMatchCandidate(
    string OpenRouterModelId,
    int Score,
    string Rule,
    IReadOnlyList<string> Conflicts);

public sealed record ModelMatchResult(
    ModelMatchStatus Status,
    string? SelectedOpenRouterModelId,
    string? WinningLayer,
    int? Score,
    int? RunnerUpScore,
    int? Margin,
    bool IsUniqueAtWinningLayer,
    IReadOnlyList<ModelMatchCandidate> Candidates,
    IReadOnlyList<string> HardConflicts,
    string CatalogRevision,
    bool IsStale,
    bool IsExpired);

public enum MetadataValueSource
{
    UserOverride,
    ProviderReported,
    PinnedOpenRouter,
    AutomaticOpenRouter,
    ApplicationDefault
}

public sealed record ResolvedMetadataValue<T>(T Value, MetadataValueSource Source);

public sealed record ResolvedModelMetadata(
    string ProviderId,
    string ExternalModelId,
    ModelMatchResult Match,
    ResolvedMetadataValue<long> ContextWindowTokens,
    ResolvedMetadataValue<long?> MaxCompletionTokens,
    ResolvedMetadataValue<CapabilitySupport> SupportsTools,
    ResolvedMetadataValue<CapabilitySupport> SupportsReasoning,
    ResolvedMetadataValue<CapabilitySupport> SupportsStructuredOutput,
    IReadOnlySet<string> InputModalities,
    IReadOnlySet<string> OutputModalities,
    IReadOnlyList<string> Warnings,
    string? TokenizerHint = null,
    ResolvedMetadataValue<CapabilitySupport>? SupportsResponses = null,
    ResolvedMetadataValue<ReasoningEffort>? ReasoningEffort = null);

public enum ModelCatalogRefreshStatus
{
    Succeeded,
    NotModified,
    SkippedFresh,
    Cancelled,
    Quarantined,
    Failed
}

public sealed record ModelCatalogRefreshResult(
    ModelCatalogRefreshStatus Status,
    string Message,
    int ModelCount = 0,
    Exception? Exception = null);

public sealed record OpenRouterCatalogPointer(
    int SchemaVersion,
    string? CurrentRevision,
    string? PreviousRevision,
    string? ETag,
    DateTimeOffset LastCheckedAtUtc);
