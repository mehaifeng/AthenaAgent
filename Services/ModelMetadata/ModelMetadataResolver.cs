using Athena.UI.Models;
using Athena.UI.Services.Interfaces;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Athena.UI.Services.ModelMetadata;

public sealed class ModelMetadataResolver(ModelIdentityMatcher matcher) : IModelMetadataResolver
{
    public const long UnknownContextWindowTokens = 1_000_000;
    public const long UnknownCompressionThresholdTokens = 262_144;

    public ResolvedModelMetadata Resolve(
        OpenAiProviderConfiguration provider,
        ProviderModelDescriptor model,
        ProviderModelMetadataProfile? profile,
        OpenRouterCatalogSnapshot snapshot,
        bool isCatalogStale = false)
    {
        var host = Uri.TryCreate(provider.BaseUrl, UriKind.Absolute, out var uri) ? uri.Host : null;
        var identity = new ExternalModelIdentity(provider.Id, provider.ProviderPreset, host, model.Id, model.DisplayName);
        var match = matcher.Match(identity, snapshot, profile, isCatalogStale);
        var matched = match.SelectedOpenRouterModelId == null
            ? null
            : snapshot.Models.FirstOrDefault(candidate => string.Equals(candidate.Id, match.SelectedOpenRouterModelId, StringComparison.Ordinal));
        var automatic = profile?.BindingMode != ModelMetadataBindingMode.PinnedOpenRouter;
        var openRouterSource = automatic ? MetadataValueSource.AutomaticOpenRouter : MetadataValueSource.PinnedOpenRouter;
        var warnings = new List<string>();

        // 供应商在 /v1/models 里自报的元数据。它比 OpenRouter 目录可靠的地方只有一点，
        // 但那一点是决定性的：它来自实际承接请求的那一家，而 OpenRouter 那一层是拿模型 ID
        // 去猜同一个模型（见 ModelIdentityMatcher），带前缀的聚合端点尤其容易猜歪。
        // 因此它在 MetadataValueSource 里排在两个 OpenRouter 层之上、用户覆盖之下。
        var reported = model.Reported;

        long? referencedContext = null;
        if (matched != null)
        {
            referencedContext = IsOpenRouterProvider(provider)
                ? matched.TopProvider?.ContextLength ?? matched.ContextLength
                : matched.ContextLength;
            if (referencedContext is < 1024) referencedContext = null;
        }

        var reportedContext = reported?.ContextLength is >= 1024 and var selfReported ? selfReported : (long?)null;

        ResolvedMetadataValue<long> context;
        if (profile?.Overrides.ContextWindowTokens is >= 1024 and var overriddenContext)
            context = new ResolvedMetadataValue<long>(overriddenContext, MetadataValueSource.UserOverride);
        else if (reportedContext.HasValue)
            context = new ResolvedMetadataValue<long>(reportedContext.Value, MetadataValueSource.ProviderReported);
        else if (referencedContext.HasValue)
            context = new ResolvedMetadataValue<long>(referencedContext.Value, openRouterSource);
        else
        {
            context = new ResolvedMetadataValue<long>(UnknownContextWindowTokens, MetadataValueSource.ApplicationDefault);
            warnings.Add(ModelWarnings.UnknownModelAssumption);
            if (matched != null) warnings.Add(ModelWarnings.OpenRouterFieldMissing);
        }

        long? referencedMax = matched?.TopProvider?.MaxCompletionTokens;
        if (referencedMax <= 0 || referencedMax > context.Value) referencedMax = null;
        long? reportedMax = reported?.MaxCompletionTokens;
        if (reportedMax <= 0 || reportedMax > context.Value) reportedMax = null;

        ResolvedMetadataValue<long?> maxCompletion;
        if (profile?.Overrides.MaxCompletionTokens is > 0 and var maxOverride)
            maxCompletion = new ResolvedMetadataValue<long?>(Math.Min(maxOverride, context.Value), MetadataValueSource.UserOverride);
        else if (reportedMax.HasValue)
            maxCompletion = new ResolvedMetadataValue<long?>(reportedMax, MetadataValueSource.ProviderReported);
        else
            maxCompletion = new ResolvedMetadataValue<long?>(referencedMax, referencedMax.HasValue ? openRouterSource : MetadataValueSource.ApplicationDefault);

        var tools = ResolveCapability(profile?.Overrides.SupportsTools, matched, openRouterSource, "tools");
        var reasoning = ResolveCapability(profile?.Overrides.SupportsReasoning, matched, openRouterSource, "reasoning", "include_reasoning");
        var structured = ResolveCapability(profile?.Overrides.SupportsStructuredOutput, matched, openRouterSource, "structured_outputs", "response_format");
        var responses = ResolveResponsesCapability(profile?.Overrides.SupportsResponses, reported, matched, openRouterSource);
        var inputs = profile?.Overrides.InputModalities is { Count: > 0 } overriddenInputs
            ? overriddenInputs.ToHashSet(StringComparer.OrdinalIgnoreCase)
            : ToSet(reported?.InputModalities)
              ?? matched?.Architecture.InputModalities
              ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var outputs = profile?.Overrides.OutputModalities is { Count: > 0 } overriddenOutputs
            ? overriddenOutputs.ToHashSet(StringComparer.OrdinalIgnoreCase)
            : ToSet(reported?.OutputModalities)
              ?? matched?.Architecture.OutputModalities
              ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (match.Status == ModelMatchStatus.PinnedModelMissing) warnings.Add(ModelWarnings.PinnedOpenRouterModelMissing);
        if (isCatalogStale) warnings.Add(ModelWarnings.OpenRouterCatalogStale);
        var effortOverride = profile?.Overrides.ReasoningEffort ?? ReasoningEffort.Auto;
        ResolvedMetadataValue<ReasoningEffort>? effort = effortOverride != ReasoningEffort.Auto
            ? new ResolvedMetadataValue<ReasoningEffort>(effortOverride, MetadataValueSource.UserOverride)
            : null;
        return new ResolvedModelMetadata(provider.Id, model.Id, match, context, maxCompletion, tools, reasoning, structured, inputs, outputs, warnings, matched?.Architecture.Tokenizer, responses, effort);
    }

    /// <summary>
    /// Responses 协议支持度。比其它三项多一个来源：供应商的 <c>supported_endpoint_types</c>
    /// 把 <c>openai</c> 与 <c>openai-response</c> 分开列（OrcaRouter 实测 196 个模型里 76 个带后者），
    /// 这是自报元数据唯一能覆盖到的能力位。
    ///
    /// 只在"报了 openai-response"时才发言。数组里没有它并不等于不支持——通道别名、
    /// 尚未登记的门面都会造成漏报，而这一层若在此时返回 Unknown，就会把本可以回答
    /// "支持"的 OpenRouter 层挡在后面。沉默让位，比抢答后答错好。
    /// </summary>
    private static ResolvedMetadataValue<CapabilitySupport> ResolveResponsesCapability(
        bool? value,
        ProviderReportedModelMetadata? reported,
        OpenRouterModelMetadata? matched,
        MetadataValueSource openRouterSource)
    {
        if (value.HasValue)
            return new ResolvedMetadataValue<CapabilitySupport>(value.Value ? CapabilitySupport.Supported : CapabilitySupport.Unsupported, MetadataValueSource.UserOverride);

        if (reported?.SupportedEndpointTypes is { Count: > 0 } endpoints
            && endpoints.Any(endpoint => string.Equals(endpoint, ResponsesEndpointType, StringComparison.OrdinalIgnoreCase)))
        {
            return new ResolvedMetadataValue<CapabilitySupport>(CapabilitySupport.Supported, MetadataValueSource.ProviderReported);
        }

        return ResolveCapability(null, matched, openRouterSource, "responses");
    }

    /// <summary><c>supported_endpoint_types</c> 里代表 OpenAI Responses 协议的取值。</summary>
    private const string ResponsesEndpointType = "openai-response";

    private static HashSet<string>? ToSet(List<string>? values)
        => values is { Count: > 0 } ? values.ToHashSet(StringComparer.OrdinalIgnoreCase) : null;

    private static ResolvedMetadataValue<CapabilitySupport> ResolveCapability(
        bool? value,
        OpenRouterModelMetadata? matched,
        MetadataValueSource openRouterSource,
        params string[] names)
    {
        if (value.HasValue)
            return new ResolvedMetadataValue<CapabilitySupport>(value.Value ? CapabilitySupport.Supported : CapabilitySupport.Unsupported, MetadataValueSource.UserOverride);
        if (matched == null)
            return new ResolvedMetadataValue<CapabilitySupport>(CapabilitySupport.Unknown, MetadataValueSource.ApplicationDefault);
        var supported = names.Any(name => matched.SupportedParameters.Contains(name));
        return new ResolvedMetadataValue<CapabilitySupport>(supported ? CapabilitySupport.Supported : CapabilitySupport.Unknown, openRouterSource);
    }

    private static bool IsOpenRouterProvider(OpenAiProviderConfiguration provider) =>
        Uri.TryCreate(provider.BaseUrl, UriKind.Absolute, out var uri)
        && (uri.Host.Equals("openrouter.ai", StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith(".openrouter.ai", StringComparison.OrdinalIgnoreCase));
}
