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

        long? referencedContext = null;
        if (matched != null)
        {
            referencedContext = IsOpenRouterProvider(provider)
                ? matched.TopProvider?.ContextLength ?? matched.ContextLength
                : matched.ContextLength;
            if (referencedContext is < 1024) referencedContext = null;
        }

        ResolvedMetadataValue<long> context;
        if (profile?.Overrides.ContextWindowTokens is >= 1024 and var overriddenContext)
            context = new ResolvedMetadataValue<long>(overriddenContext, MetadataValueSource.UserOverride);
        else if (referencedContext.HasValue)
            context = new ResolvedMetadataValue<long>(referencedContext.Value, openRouterSource);
        else
        {
            context = new ResolvedMetadataValue<long>(UnknownContextWindowTokens, MetadataValueSource.ApplicationDefault);
            warnings.Add("UnknownModelAssumption");
            if (matched != null) warnings.Add("OpenRouterFieldMissing");
        }

        long? referencedMax = matched?.TopProvider?.MaxCompletionTokens;
        if (referencedMax <= 0 || referencedMax > context.Value) referencedMax = null;
        var maxCompletion = profile?.Overrides.MaxCompletionTokens is > 0 and var maxOverride
            ? new ResolvedMetadataValue<long?>(Math.Min(maxOverride, context.Value), MetadataValueSource.UserOverride)
            : new ResolvedMetadataValue<long?>(referencedMax, referencedMax.HasValue ? openRouterSource : MetadataValueSource.ApplicationDefault);

        var tools = ResolveCapability(profile?.Overrides.SupportsTools, matched, openRouterSource, "tools");
        var reasoning = ResolveCapability(profile?.Overrides.SupportsReasoning, matched, openRouterSource, "reasoning", "include_reasoning");
        var structured = ResolveCapability(profile?.Overrides.SupportsStructuredOutput, matched, openRouterSource, "structured_outputs", "response_format");
        var responses = ResolveCapability(profile?.Overrides.SupportsResponses, matched, openRouterSource, "responses");
        var inputs = profile?.Overrides.InputModalities is { Count: > 0 } overriddenInputs
            ? overriddenInputs.ToHashSet(StringComparer.OrdinalIgnoreCase)
            : matched?.Architecture.InputModalities ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var outputs = profile?.Overrides.OutputModalities is { Count: > 0 } overriddenOutputs
            ? overriddenOutputs.ToHashSet(StringComparer.OrdinalIgnoreCase)
            : matched?.Architecture.OutputModalities ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (match.Status == ModelMatchStatus.PinnedModelMissing) warnings.Add("PinnedOpenRouterModelMissing");
        if (isCatalogStale) warnings.Add("OpenRouterCatalogStale");
        return new ResolvedModelMetadata(provider.Id, model.Id, match, context, maxCompletion, tools, reasoning, structured, inputs, outputs, warnings, matched?.Architecture.Tokenizer, responses);
    }

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
