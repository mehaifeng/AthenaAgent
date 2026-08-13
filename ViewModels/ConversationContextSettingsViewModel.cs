using Athena.UI.Models;
using Athena.UI.Services.Interfaces;
using System;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;

namespace Athena.UI.ViewModels;

public sealed class ConversationContextSettingsViewModel : ViewModelBase, IDisposable
{
    private readonly IOpenRouterModelMetadataCatalog? _catalog;
    private readonly IModelMetadataResolver? _metadataResolver;
    private readonly IModelContextPolicyResolver? _policyResolver;
    private readonly ILocalizationService? _localization;
    private AppConfig? _attachedConfig;

    public ConversationContextSettingsViewModel(
        AppSettingsState state,
        IOpenRouterModelMetadataCatalog? catalog = null,
        IModelMetadataResolver? metadataResolver = null,
        IModelContextPolicyResolver? policyResolver = null,
        ILocalizationService? localization = null)
    {
        State = state;
        _catalog = catalog;
        _metadataResolver = metadataResolver;
        _policyResolver = policyResolver;
        _localization = localization;
        State.PropertyChanged += OnStatePropertyChanged;
        if (_catalog != null) _catalog.CatalogChanged += OnCatalogChanged;
        if (_localization != null) _localization.LanguageChanged += OnLanguageChanged;
        Attach(State.Config);
    }

    public AppSettingsState State { get; }
    public Array ContextPolicyModes { get; } = Enum.GetValues<ContextPolicyMode>();
    public Array CompressionThresholdModes { get; } = Enum.GetValues<CompressionThresholdMode>();
    public Array CompressionStrengths { get; } = Enum.GetValues<CompressionStrength>();

    public bool IsCustomCap => State.Config.ContextPolicy.Mode is ContextPolicyMode.CustomCap or ContextPolicyMode.LegacyCustom;
    public bool IsCustomThreshold => State.Config.ContextPolicy.CompressionThresholdMode == CompressionThresholdMode.Custom;

    public string MainModelText => TryResolve(out var provider, out var model, out _, out _)
        ? $"{provider!.DisplayName} / {model!.Id}"
        : L("Settings.Context.Unconfigured", "Not configured");

    public string ContextWindowText => TryResolve(out _, out _, out var metadata, out _)
        ? $"{metadata!.ContextWindowTokens.Value:N0} tokens"
        : "—";

    public string ContextWindowSourceText => TryResolve(out _, out _, out var metadata, out _)
        ? L($"Settings.Context.Source.{metadata!.ContextWindowTokens.Source}", metadata.ContextWindowTokens.Source.ToString())
        : "—";

    public string MatchStatusText => TryResolve(out _, out _, out var metadata, out _)
        ? L($"Settings.Context.Match.{metadata!.Match.Status}", metadata.Match.Status.ToString())
        : "—";

    public string EffectivePolicyText => TryResolve(out _, out _, out _, out var policy)
        ? $"W {policy!.ContextWindowTokens:N0} · R {policy.OutputReserveTokens:N0} · S {policy.SafetyMarginTokens:N0} · B {policy.AvailableInputBudgetTokens:N0} · T {policy.CompressionThresholdTokens:N0}"
        : "—";

    /// <summary>
    /// 把几个旋钮换算成用户真正关心的结果：什么时候压、一次能吃多少历史、摘要最长多少。
    /// 只列旋钮而不显示派生结果，正是「摘要目标 Token」当初难以理解的原因。
    /// </summary>
    public string CompressionEffectText
    {
        get
        {
            if (!TryResolve(out _, out _, out _, out var policy)) return "—";
            if (!policy!.AutoCompress) return L("Settings.Context.Effect.Disabled", "Automatic compression is off.");
            return string.Format(
                L("Settings.Context.Effect.Format",
                    "Compresses at {0:N0} tokens · up to {1:N0} tokens of history per pass · summary at most {2:N0} tokens ({3}:1)"),
                policy.CompressionThresholdTokens,
                policy.MaxMaterialPerPassTokens,
                policy.TargetSummaryTokens,
                policy.SummaryRatio);
        }
    }

    public string CatalogText
    {
        get
        {
            if (_catalog == null || _catalog.Current == OpenRouterCatalogSnapshot.Empty)
                return L("Settings.Context.CatalogUnavailable", "Local seed/default");
            var stale = _catalog.IsStale ? L("Settings.Context.CatalogStale", "stale") : L("Settings.Context.CatalogFresh", "fresh");
            return $"{_catalog.Current.FetchedAtUtc.LocalDateTime:g} · {stale}";
        }
    }

    private bool TryResolve(
        out OpenAiProviderConfiguration? provider,
        out ProviderModelDescriptor? model,
        out ResolvedModelMetadata? metadata,
        out ResolvedContextPolicy? policy)
    {
        provider = null;
        model = null;
        metadata = null;
        policy = null;
        if (_metadataResolver == null || _policyResolver == null) return false;
        var config = State.Config;
        var role = config.AiModels.MainConversation;
        var selectedProvider = config.AiModels.Providers.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, role.ProviderId, StringComparison.Ordinal));
        provider = selectedProvider;
        if (selectedProvider == null || string.IsNullOrWhiteSpace(role.Model)) return false;
        var selectedModel = selectedProvider.Models.FirstOrDefault(candidate => string.Equals(candidate.Id, role.Model, StringComparison.Ordinal))
                ?? new ProviderModelDescriptor { Id = role.Model, DisplayName = role.Model, IsManual = true };
        model = selectedModel;
        var profile = config.AiModels.ModelMetadataProfiles.FirstOrDefault(candidate =>
            string.Equals(candidate.ProviderId, selectedProvider.Id, StringComparison.Ordinal)
            && string.Equals(candidate.ExternalModelId, selectedModel.Id, StringComparison.Ordinal));
        metadata = _metadataResolver.Resolve(
            selectedProvider,
            selectedModel,
            profile,
            _catalog?.Current ?? OpenRouterCatalogSnapshot.Empty,
            _catalog?.IsStale == true);
        policy = _policyResolver.Resolve(metadata, config.ContextPolicy, null, AiModelRole.MainConversation);
        return true;
    }

    private void Attach(AppConfig config)
    {
        Detach();
        _attachedConfig = config;
        config.ContextPolicy.PropertyChanged += OnRelevantPropertyChanged;
        config.AiModels.MainConversation.PropertyChanged += OnRelevantPropertyChanged;
        config.AiModels.Providers.CollectionChanged += OnRelevantCollectionChanged;
        config.AiModels.ModelMetadataProfiles.CollectionChanged += OnRelevantCollectionChanged;
        foreach (var provider in config.AiModels.Providers) provider.PropertyChanged += OnRelevantPropertyChanged;
        Refresh();
    }

    private void Detach()
    {
        if (_attachedConfig == null) return;
        _attachedConfig.ContextPolicy.PropertyChanged -= OnRelevantPropertyChanged;
        _attachedConfig.AiModels.MainConversation.PropertyChanged -= OnRelevantPropertyChanged;
        _attachedConfig.AiModels.Providers.CollectionChanged -= OnRelevantCollectionChanged;
        _attachedConfig.AiModels.ModelMetadataProfiles.CollectionChanged -= OnRelevantCollectionChanged;
        foreach (var provider in _attachedConfig.AiModels.Providers) provider.PropertyChanged -= OnRelevantPropertyChanged;
        _attachedConfig = null;
    }

    private void OnStatePropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(AppSettingsState.Config)) Attach(State.Config);
    }

    private void OnRelevantPropertyChanged(object? sender, PropertyChangedEventArgs args) => Refresh();

    private void OnRelevantCollectionChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        if (sender == State.Config.AiModels.Providers)
        {
            if (args.OldItems != null)
                foreach (OpenAiProviderConfiguration provider in args.OldItems) provider.PropertyChanged -= OnRelevantPropertyChanged;
            if (args.NewItems != null)
                foreach (OpenAiProviderConfiguration provider in args.NewItems) provider.PropertyChanged += OnRelevantPropertyChanged;
        }
        Refresh();
    }

    private void OnCatalogChanged(object? sender, EventArgs args) => Refresh();
    private void OnLanguageChanged(object? sender, EventArgs args) => Refresh();

    private void Refresh()
    {
        OnPropertyChanged(nameof(IsCustomCap));
        OnPropertyChanged(nameof(IsCustomThreshold));
        OnPropertyChanged(nameof(MainModelText));
        OnPropertyChanged(nameof(ContextWindowText));
        OnPropertyChanged(nameof(ContextWindowSourceText));
        OnPropertyChanged(nameof(MatchStatusText));
        OnPropertyChanged(nameof(EffectivePolicyText));
        OnPropertyChanged(nameof(CompressionEffectText));
        OnPropertyChanged(nameof(CatalogText));
    }

    private string L(string key, string fallback) => _localization?.GetString(key, fallback) ?? fallback;

    public void Dispose()
    {
        Detach();
        State.PropertyChanged -= OnStatePropertyChanged;
        if (_catalog != null) _catalog.CatalogChanged -= OnCatalogChanged;
        if (_localization != null) _localization.LanguageChanged -= OnLanguageChanged;
    }
}
