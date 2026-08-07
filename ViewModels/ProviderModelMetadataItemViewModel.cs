using Athena.UI.Models;
using Athena.UI.Services.Interfaces;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace Athena.UI.ViewModels;

/// <summary>
/// Presents one provider-inventory identity together with resolved OpenRouter facts and
/// user intent. Automatic match results remain derived and are never persisted.
/// </summary>
public sealed partial class ProviderModelMetadataItemViewModel : ViewModelBase
{
    private readonly IModelMetadataResolver _resolver;
    private readonly Func<OpenRouterCatalogSnapshot> _snapshot;
    private readonly Func<bool> _isCatalogStale;
    private readonly Func<ProviderModelMetadataProfile?> _findProfile;
    private readonly Func<ProviderModelMetadataProfile> _ensureProfile;
    private readonly Func<string, string, string> _getString;
    private bool _loading;
    private long? _contextWindowOverride;
    private long? _maxCompletionOverride;
    private bool? _supportsToolsOverride;
    private bool? _supportsReasoningOverride;
    private bool? _supportsStructuredOutputOverride;
    private bool? _supportsResponsesOverride;
    private string _inputModalitiesOverride = string.Empty;
    private string _outputModalitiesOverride = string.Empty;
    private OpenRouterModelMetadata? _selectedPinnedModel;

    public ProviderModelMetadataItemViewModel(
        OpenAiProviderConfiguration provider,
        ProviderModelDescriptor model,
        IModelMetadataResolver resolver,
        Func<OpenRouterCatalogSnapshot> snapshot,
        Func<bool> isCatalogStale,
        Func<ProviderModelMetadataProfile?> findProfile,
        Func<ProviderModelMetadataProfile> ensureProfile,
        Func<string, string, string> getString)
    {
        Provider = provider;
        Model = model;
        _resolver = resolver;
        _snapshot = snapshot;
        _isCatalogStale = isCatalogStale;
        _findProfile = findProfile;
        _ensureProfile = ensureProfile;
        _getString = getString;
        ReloadFromProfile();
    }

    public OpenAiProviderConfiguration Provider { get; }
    public ProviderModelDescriptor Model { get; }
    public string ExternalModelId => Model.Id;
    public string DisplayName => Model.DisplayName;
    public ModelCapability Capability => Model.Capability;
    public bool IsAvailable => Model.IsAvailable;
    public string AvailabilityText => Model.IsAvailable
        ? _getString("ProviderModels.Metadata.Available", "Available")
        : _getString("ProviderModels.Metadata.ProviderUnavailable", "Provider unavailable");

    public ResolvedModelMetadata Resolved { get; private set; } = null!;
    public ModelMatchStatus MatchStatus => Resolved.Match.Status;
    public string MatchStatusText => _getString($"ProviderModels.Match.{Resolved.Match.Status}", Resolved.Match.Status.ToString());
    public string MatchDetails => string.Format(
        _getString("ProviderModels.Metadata.MatchDetails", "{0} · score {1} · margin {2}"),
        Resolved.Match.WinningLayer ?? "—",
        Resolved.Match.Score?.ToString(CultureInfo.InvariantCulture) ?? "—",
        Resolved.Match.Margin?.ToString(CultureInfo.InvariantCulture) ?? "—");
    public string OpenRouterModelId => Resolved.Match.SelectedOpenRouterModelId ?? "—";
    public string OpenRouterModelName => MatchedOpenRouterModel?.Name ?? "—";
    public string ContextWindowText => $"{Resolved.ContextWindowTokens.Value:N0} · {FormatSource(Resolved.ContextWindowTokens.Source)}";
    public string MaxCompletionText => $"{(Resolved.MaxCompletionTokens.Value.HasValue ? Resolved.MaxCompletionTokens.Value.Value.ToString("N0", CultureInfo.CurrentCulture) : "—")} · {FormatSource(Resolved.MaxCompletionTokens.Source)}";
    public string ToolsText => $"{FormatSupport(Resolved.SupportsTools.Value)} · {FormatSource(Resolved.SupportsTools.Source)}";
    public string ReasoningText => $"{FormatSupport(Resolved.SupportsReasoning.Value)} · {FormatSource(Resolved.SupportsReasoning.Source)}";
    public string StructuredOutputText => $"{FormatSupport(Resolved.SupportsStructuredOutput.Value)} · {FormatSource(Resolved.SupportsStructuredOutput.Source)}";
    public string ResponsesText => Resolved.SupportsResponses is { } responses
        ? $"{FormatSupport(responses.Value)} · {FormatSource(responses.Source)}"
        : "—";
    public string InputModalitiesText => Resolved.InputModalities.Count == 0 ? "—" : string.Join(", ", Resolved.InputModalities.Order(StringComparer.OrdinalIgnoreCase));
    public string OutputModalitiesText => Resolved.OutputModalities.Count == 0 ? "—" : string.Join(", ", Resolved.OutputModalities.Order(StringComparer.OrdinalIgnoreCase));
    public string WarningText
    {
        get
        {
            var warnings = Resolved.Warnings.Select(warning =>
                _getString($"ProviderModels.Warning.{warning}", warning)).ToList();
            if (Resolved.Match.IsExpired)
                warnings.Add(_getString("ProviderModels.Warning.OpenRouterModelExpired", "The matched OpenRouter model is expired."));
            return string.Join(Environment.NewLine, warnings);
        }
    }
    public bool HasWarnings => !string.IsNullOrWhiteSpace(WarningText);
    public bool IsCatalogStale => Resolved.Match.IsStale;
    public bool IsExpired => Resolved.Match.IsExpired;
    public IReadOnlyList<ModelMatchCandidate> MatchCandidates => Resolved.Match.Candidates;
    public IReadOnlyList<OpenRouterModelMetadata> OpenRouterModels => _snapshot().Models;
    public OpenRouterModelMetadata? MatchedOpenRouterModel => Resolved.Match.SelectedOpenRouterModelId == null
        ? null
        : _snapshot().Models.FirstOrDefault(model => string.Equals(model.Id, Resolved.Match.SelectedOpenRouterModelId, StringComparison.Ordinal));
    public string RawOpenRouterJson
    {
        get
        {
            var matched = MatchedOpenRouterModel;
            if (matched == null) return string.Empty;
            if (matched.Raw is { } raw)
            {
                try
                {
                    return JsonSerializer.Serialize(raw, new JsonSerializerOptions { WriteIndented = true });
                }
                catch
                {
                    return raw.GetRawText();
                }
            }
            return JsonSerializer.Serialize(matched, new JsonSerializerOptions { WriteIndented = true });
        }
    }
    public bool HasRawOpenRouterJson => !string.IsNullOrWhiteSpace(RawOpenRouterJson);

    public ModelMetadataBindingMode BindingMode
    {
        get => _findProfile()?.BindingMode ?? ModelMetadataBindingMode.Automatic;
        set
        {
            if (BindingMode == value) return;
            var profile = _ensureProfile();
            profile.BindingMode = value;
            if (value != ModelMetadataBindingMode.PinnedOpenRouter) profile.PinnedOpenRouterModelId = null;
            RefreshResolved();
        }
    }

    public OpenRouterModelMetadata? SelectedPinnedModel
    {
        get => _selectedPinnedModel;
        set
        {
            if (ReferenceEquals(_selectedPinnedModel, value)) return;
            SetProperty(ref _selectedPinnedModel, value);
            if (_loading || value == null) return;
            var profile = _ensureProfile();
            profile.BindingMode = ModelMetadataBindingMode.PinnedOpenRouter;
            profile.PinnedOpenRouterModelId = value.Id;
            OnPropertyChanged(nameof(BindingMode));
            RefreshResolved();
        }
    }

    public long? ContextWindowOverride
    {
        get => _contextWindowOverride;
        set => SetOverride(ref _contextWindowOverride, value, (overrides, next) => overrides.ContextWindowTokens = next);
    }

    public long? MaxCompletionOverride
    {
        get => _maxCompletionOverride;
        set => SetOverride(ref _maxCompletionOverride, value, (overrides, next) => overrides.MaxCompletionTokens = next);
    }

    public bool? SupportsToolsOverride
    {
        get => _supportsToolsOverride;
        set => SetOverride(ref _supportsToolsOverride, value, (overrides, next) => overrides.SupportsTools = next);
    }

    public bool? SupportsReasoningOverride
    {
        get => _supportsReasoningOverride;
        set => SetOverride(ref _supportsReasoningOverride, value, (overrides, next) => overrides.SupportsReasoning = next);
    }

    public bool? SupportsStructuredOutputOverride
    {
        get => _supportsStructuredOutputOverride;
        set => SetOverride(ref _supportsStructuredOutputOverride, value, (overrides, next) => overrides.SupportsStructuredOutput = next);
    }

    public bool? SupportsResponsesOverride
    {
        get => _supportsResponsesOverride;
        set => SetOverride(ref _supportsResponsesOverride, value, (overrides, next) => overrides.SupportsResponses = next);
    }

    public string InputModalitiesOverride
    {
        get => _inputModalitiesOverride;
        set => SetModalities(ref _inputModalitiesOverride, value, (overrides, next) => overrides.InputModalities = next);
    }

    public string OutputModalitiesOverride
    {
        get => _outputModalitiesOverride;
        set => SetModalities(ref _outputModalitiesOverride, value, (overrides, next) => overrides.OutputModalities = next);
    }

    [RelayCommand]
    private void UseAutomaticMatch()
    {
        var profile = _ensureProfile();
        profile.BindingMode = ModelMetadataBindingMode.Automatic;
        profile.PinnedOpenRouterModelId = null;
        ReloadFromProfile();
    }

    [RelayCommand]
    private void UseCustomOnly()
    {
        var profile = _ensureProfile();
        profile.BindingMode = ModelMetadataBindingMode.CustomOnly;
        profile.PinnedOpenRouterModelId = null;
        ReloadFromProfile();
    }

    [RelayCommand]
    private void ResetOverrides()
    {
        var profile = _findProfile();
        if (profile == null) return;
        profile.Overrides = new ModelMetadataOverrides();
        ReloadFromProfile();
    }

    public void RefreshResolved()
    {
        Resolved = _resolver.Resolve(Provider, Model, _findProfile(), _snapshot(), _isCatalogStale());
        _loading = true;
        _selectedPinnedModel = _findProfile()?.PinnedOpenRouterModelId is { } pinned
            ? _snapshot().Models.FirstOrDefault(model => string.Equals(model.Id, pinned, StringComparison.Ordinal))
            : null;
        _loading = false;
        NotifyResolvedProperties();
    }

    private void ReloadFromProfile()
    {
        var profile = _findProfile();
        _loading = true;
        _contextWindowOverride = profile?.Overrides.ContextWindowTokens;
        _maxCompletionOverride = profile?.Overrides.MaxCompletionTokens;
        _supportsToolsOverride = profile?.Overrides.SupportsTools;
        _supportsReasoningOverride = profile?.Overrides.SupportsReasoning;
        _supportsStructuredOutputOverride = profile?.Overrides.SupportsStructuredOutput;
        _supportsResponsesOverride = profile?.Overrides.SupportsResponses;
        _inputModalitiesOverride = Join(profile?.Overrides.InputModalities);
        _outputModalitiesOverride = Join(profile?.Overrides.OutputModalities);
        _loading = false;
        RefreshResolved();
        OnPropertyChanged(nameof(ContextWindowOverride));
        OnPropertyChanged(nameof(MaxCompletionOverride));
        OnPropertyChanged(nameof(SupportsToolsOverride));
        OnPropertyChanged(nameof(SupportsReasoningOverride));
        OnPropertyChanged(nameof(SupportsStructuredOutputOverride));
        OnPropertyChanged(nameof(SupportsResponsesOverride));
        OnPropertyChanged(nameof(InputModalitiesOverride));
        OnPropertyChanged(nameof(OutputModalitiesOverride));
    }

    private void SetOverride<T>(ref T field, T value, Action<ModelMetadataOverrides, T> apply)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        SetProperty(ref field, value);
        if (_loading) return;
        apply(_ensureProfile().Overrides, value);
        RefreshResolved();
    }

    private void SetModalities(
        ref string field,
        string value,
        Action<ModelMetadataOverrides, ObservableCollection<string>?> apply)
    {
        value ??= string.Empty;
        if (string.Equals(field, value, StringComparison.Ordinal)) return;
        SetProperty(ref field, value);
        if (_loading) return;
        var entries = value.Split([',', ';'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        apply(_ensureProfile().Overrides, entries.Count == 0 ? null : new ObservableCollection<string>(entries));
        RefreshResolved();
    }

    private void NotifyResolvedProperties()
    {
        OnPropertyChanged(nameof(AvailabilityText));
        OnPropertyChanged(nameof(Resolved));
        OnPropertyChanged(nameof(MatchStatus));
        OnPropertyChanged(nameof(MatchStatusText));
        OnPropertyChanged(nameof(MatchDetails));
        OnPropertyChanged(nameof(OpenRouterModelId));
        OnPropertyChanged(nameof(OpenRouterModelName));
        OnPropertyChanged(nameof(ContextWindowText));
        OnPropertyChanged(nameof(MaxCompletionText));
        OnPropertyChanged(nameof(ToolsText));
        OnPropertyChanged(nameof(ReasoningText));
        OnPropertyChanged(nameof(StructuredOutputText));
        OnPropertyChanged(nameof(ResponsesText));
        OnPropertyChanged(nameof(InputModalitiesText));
        OnPropertyChanged(nameof(OutputModalitiesText));
        OnPropertyChanged(nameof(WarningText));
        OnPropertyChanged(nameof(HasWarnings));
        OnPropertyChanged(nameof(IsCatalogStale));
        OnPropertyChanged(nameof(IsExpired));
        OnPropertyChanged(nameof(MatchCandidates));
        OnPropertyChanged(nameof(OpenRouterModels));
        OnPropertyChanged(nameof(MatchedOpenRouterModel));
        OnPropertyChanged(nameof(RawOpenRouterJson));
        OnPropertyChanged(nameof(HasRawOpenRouterJson));
        OnPropertyChanged(nameof(BindingMode));
        OnPropertyChanged(nameof(SelectedPinnedModel));
    }

    private static string Join(IEnumerable<string>? values) => values == null ? string.Empty : string.Join(", ", values);

    private string FormatSource(MetadataValueSource source) =>
        _getString($"ProviderModels.Source.{source}", source.ToString());

    private string FormatSupport(CapabilitySupport support) =>
        _getString($"ProviderModels.Support.{support}", support.ToString());
}

public sealed record ProviderMetadataFilterOption<T>(T Value, string Label);

public enum ProviderMetadataCapabilityFilter
{
    All,
    Text,
    Embedding,
    Image,
    Speech,
    Unknown
}

public enum ProviderMetadataMatchFilter
{
    All,
    Matched,
    Ambiguous,
    Unmatched,
    PinnedModelMissing,
    CustomOnly
}
