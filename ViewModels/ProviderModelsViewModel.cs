using Athena.UI.Models;
using Athena.UI.Services;
using Athena.UI.Services.Interfaces;
using Athena.UI.Services.ModelMetadata;
using Athena.UI.Services.OrcaRouter;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Athena.UI.ViewModels;

public partial class ProviderModelsViewModel : ViewModelBase, IDisposable
{
    private readonly AppConfigurationSession _configurationSession;
    private readonly IModelCatalogService _catalogService;
    private readonly IOpenRouterModelMetadataCatalog? _metadataCatalog;
    private readonly IModelMetadataResolver _metadataResolver;
    private readonly ILocalizationService? _localizationService;
    private readonly IUserInteractionService? _userInteractionService;
    private CancellationTokenSource? _refreshCancellation;
    private CancellationTokenSource? _metadataRefreshCancellation;
    private long _refreshGeneration;
    private bool _disposed;

    public ProviderModelsViewModel(
        AppConfigurationSession configurationSession,
        IModelCatalogService catalogService,
        IOpenRouterModelMetadataCatalog? metadataCatalog = null,
        IModelMetadataResolver? metadataResolver = null,
        ILocalizationService? localizationService = null,
        IUserInteractionService? userInteractionService = null,
        IOrcaRouterConnectService? orcaRouterConnectService = null)
    {
        _configurationSession = configurationSession;
        _catalogService = catalogService;
        _metadataCatalog = metadataCatalog;
        _metadataResolver = metadataResolver ?? new ModelMetadataResolver(new ModelIdentityMatcher());
        _localizationService = localizationService;
        _userInteractionService = userInteractionService;
        _config = configurationSession.Current;
        OrcaRouter = new OrcaRouterConnectViewModel(orcaRouterConnectService, localizationService, ApplyOrcaRouterAsync);
        RebuildFilterOptions();
        RebuildRoles();
        SelectedProvider = Providers.FirstOrDefault();
        _configurationSession.CurrentChanged += OnCurrentConfigChanged;
        if (_metadataCatalog != null) _metadataCatalog.CatalogChanged += OnMetadataCatalogChanged;
        if (_localizationService != null) _localizationService.LanguageChanged += OnLanguageChanged;
    }

    [ObservableProperty]
    private AppConfig _config;

    /// <summary>「使用 OrcaRouter 账号接入」入口。服务不可用时它自己保持禁用并说明原因。</summary>
    public OrcaRouterConnectViewModel OrcaRouter { get; }

    public ObservableCollection<OpenAiProviderConfiguration> Providers => Config.AiModels.Providers;
    public ObservableCollection<ProviderRoleSelectionViewModel> Roles { get; } = new();
    public ObservableCollection<ProviderModelMetadataItemViewModel> MetadataModels { get; } = new();
    private readonly List<ProviderModelMetadataItemViewModel> _allMetadataModels = [];
    public ObservableCollection<ProviderMetadataFilterOption<ProviderMetadataCapabilityFilter>> CapabilityFilters { get; } = new();
    public ObservableCollection<ProviderMetadataFilterOption<ProviderMetadataMatchFilter>> MatchFilters { get; } = new();
    public ObservableCollection<ProviderMetadataFilterOption<ProviderProtocol>> ProtocolOptions { get; } = new();

    [ObservableProperty]
    private ProviderMetadataFilterOption<ProviderProtocol>? _selectedProtocolOption;

    [ObservableProperty]
    private string _metadataSearchText = string.Empty;

    [ObservableProperty]
    private ProviderMetadataFilterOption<ProviderMetadataCapabilityFilter>? _selectedCapabilityFilter;

    [ObservableProperty]
    private ProviderMetadataFilterOption<ProviderMetadataMatchFilter>? _selectedMatchFilter;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedMetadataModel))]
    [NotifyPropertyChangedFor(nameof(HasNoSelectedMetadataModel))]
    private ProviderModelMetadataItemViewModel? _selectedMetadataModel;

    public bool HasSelectedMetadataModel => SelectedMetadataModel != null;
    public bool HasNoSelectedMetadataModel => SelectedMetadataModel == null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMetadataStatusText))]
    private string _metadataStatusText = string.Empty;

    public bool HasMetadataStatusText => !string.IsNullOrWhiteSpace(MetadataStatusText);

    [ObservableProperty]
    private bool _isMetadataRefreshing;

    public string MetadataCatalogText => _metadataCatalog == null
        ? GetString("ProviderModels.Metadata.CatalogUnavailable", "OpenRouter catalog unavailable")
        : string.Format(
            GetString("ProviderModels.Metadata.CatalogStatus", "Revision {0} · {1:g} · {2} models · {3}"),
            _metadataCatalog.Current.CatalogRevision,
            _metadataCatalog.Current.FetchedAtUtc.ToLocalTime(),
            _metadataCatalog.Current.Models.Count,
            _metadataCatalog.IsStale ? GetString("ProviderModels.Metadata.Stale", "stale") : GetString("ProviderModels.Metadata.Fresh", "fresh"));

    [ObservableProperty]
    private OpenAiProviderConfiguration? _selectedProvider;

    [ObservableProperty]
    private string _manualModelId = string.Empty;

    /// <summary>
    /// 刷新状态按供应商归属，而不是整页共用一条。右侧配置区渲染的永远是 <see cref="SelectedProvider"/>，
    /// 而切换选中并不会停下已经发出的刷新——共用一个 StatusText/IsRefreshing 时，A 的 loading 和
    /// 「发现 N 个模型」会画在 B 的配置区里，看上去像是 B 刷新出来的结果。
    /// </summary>
    private readonly Dictionary<string, string> _providerStatus = new(StringComparer.Ordinal);

    /// <summary>正在刷新的那一个供应商；同一时刻只有一个（新刷新会取消上一个）。</summary>
    private string? _refreshingProviderId;

    public string SelectedProviderStatusText =>
        SelectedProvider is { } provider && _providerStatus.TryGetValue(provider.Id, out var text)
            ? text
            : string.Empty;

    public bool IsSelectedProviderRefreshing =>
        _refreshingProviderId != null
        && SelectedProvider is { } provider
        && string.Equals(_refreshingProviderId, provider.Id, StringComparison.Ordinal);

    private void SetProviderStatus(OpenAiProviderConfiguration provider, string text)
    {
        if (string.IsNullOrEmpty(text)) _providerStatus.Remove(provider.Id);
        else _providerStatus[provider.Id] = text;
        OnPropertyChanged(nameof(SelectedProviderStatusText));
    }

    private void SetRefreshingProvider(string? providerId)
    {
        _refreshingProviderId = providerId;
        OnPropertyChanged(nameof(IsSelectedProviderRefreshing));
    }

    [RelayCommand]
    private void AddProvider()
    {
        var provider = new OpenAiProviderConfiguration
        {
            DisplayName = "OpenAI",
            ProviderPreset = "OpenAI",
            BaseUrl = "https://api.openai.com/v1"
        };
        Providers.Add(provider);
        SelectedProvider = provider;
        RebuildRoles();
    }

    [RelayCommand]
    private void DeleteProvider(OpenAiProviderConfiguration? provider)
    {
        provider ??= SelectedProvider;
        if (provider == null) return;
        var references = Roles.Where(role => role.Settings.ProviderId == provider.Id).Select(role => role.Name).ToList();
        if (references.Count > 0)
        {
            SetProviderStatus(provider, string.Format(
                GetString("ProviderModels.Status.ProviderReferenced", "Cannot delete: still referenced by {0}"),
                string.Join(", ", references)));
            return;
        }
        Providers.Remove(provider);
        _providerStatus.Remove(provider.Id);
        SelectedProvider = Providers.FirstOrDefault();
        RebuildRoles();
    }

    /// <summary>
    /// 把一次成功的 OrcaRouter 接入落到配置上：同 host 只保留一条连接，选中它，
    /// 只有在主对话还没选过模型时才顺带指派，然后照常刷一次库存。
    /// </summary>
    private async Task ApplyOrcaRouterAsync(OrcaRouterConnectResult result)
    {
        if (_disposed || result.Endpoints is not { } endpoints || string.IsNullOrWhiteSpace(result.ApiKey)) return;

        var provider = OrcaRouterProviderBinder.Bind(Config.AiModels, endpoints, result.ApiKey);
        SelectedProvider = provider;
        OrcaRouterProviderBinder.AssignMainConversationIfUnset(Config.AiModels, provider, endpoints.DefaultModel);
        RebuildRoles();
        RebuildMetadataModels();
        await RefreshModelsAsync(provider);
    }

    [RelayCommand]
    private async Task RefreshModelsAsync(OpenAiProviderConfiguration? provider)
    {
        provider ??= SelectedProvider;
        if (provider == null || _disposed) return;
        _refreshCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        _refreshCancellation = cancellation;
        var generation = Interlocked.Increment(ref _refreshGeneration);
        var fingerprint = new ProviderRefreshFingerprint(provider.Id, provider.BaseUrl, provider.ApiKey);
        // 上一轮的「发现 N 个模型」不该和新的 loading 并排显示。
        SetProviderStatus(provider, string.Empty);
        SetRefreshingProvider(provider.Id);
        try
        {
            var result = await _catalogService.GetModelsAsync(provider.BaseUrl, provider.ApiKey, cancellation.Token);
            if (!CanApplyRefresh(provider, cancellation, generation, fingerprint)) return;
            if (!result.Success)
            {
                SetProviderStatus(provider, string.Format(
                    GetString("ProviderModels.Status.InventoryFailed", "Refresh failed; previous inventory retained: {0}"),
                    result.ErrorMessage));
                return;
            }

            var modelIds = new HashSet<string>(result.Models, StringComparer.Ordinal);
            var reported = result.Reported;

            // OpenRouter: 额外拉取精确的 Embedding 模型列表并合并。
            if (IsOpenRouter(provider.BaseUrl))
            {
                var embedResult = await _catalogService.GetEmbeddingModelsAsync(provider.BaseUrl, provider.ApiKey, cancellation.Token);
                if (CanApplyRefresh(provider, cancellation, generation, fingerprint) && embedResult.Success)
                {
                    foreach (var id in embedResult.Models) modelIds.Add(id);

                    // Embedding 列表是另一次过滤查询，带回来的是同一批记录的自报字段；
                    // 合进主快照，否则只出现在这一次响应里的模型会被当成"供应商没报"。
                    if (embedResult.Reported is { Count: > 0 } embedReported && reported != null)
                    {
                        var combined = new Dictionary<string, ProviderReportedModelMetadata>(reported, StringComparer.Ordinal);
                        foreach (var pair in embedReported) combined[pair.Key] = pair.Value;
                        reported = combined;
                    }
                }
            }

            if (!CanApplyRefresh(provider, cancellation, generation, fingerprint)) return;
            var referencedIds = GetReferencedModelIds(provider.Id);
            var merged = ProviderModelInventoryMerger.Merge(provider.Models, modelIds, referencedIds, Classify, reported);
            provider.Models.Clear();
            foreach (var model in merged) provider.Models.Add(model);
            provider.ModelsRefreshedAt = DateTimeOffset.Now;
            SetProviderStatus(provider, string.Format(
                GetString("ProviderModels.Status.InventoryCount", "Discovered {0} models"),
                provider.Models.Count));
            RebuildRoles();
            RebuildMetadataModels();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        finally
        {
            if (ReferenceEquals(_refreshCancellation, cancellation))
            {
                _refreshCancellation = null;
                if (!_disposed) SetRefreshingProvider(null);
            }
            cancellation.Dispose();
        }
    }

    private bool CanApplyRefresh(
        OpenAiProviderConfiguration provider,
        CancellationTokenSource cancellation,
        long generation,
        ProviderRefreshFingerprint fingerprint)
        => !_disposed
           && !cancellation.IsCancellationRequested
           && ReferenceEquals(_refreshCancellation, cancellation)
           && Volatile.Read(ref _refreshGeneration) == generation
           && Providers.Contains(provider)
           && fingerprint == new ProviderRefreshFingerprint(provider.Id, provider.BaseUrl, provider.ApiKey);

    private HashSet<string> GetReferencedModelIds(string providerId)
    {
        var referenced = Config.AiModels.ModelMetadataProfiles
            .Where(profile => string.Equals(profile.ProviderId, providerId, StringComparison.Ordinal))
            .Select(profile => profile.ExternalModelId)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var role in Roles.Where(role => string.Equals(role.Settings.ProviderId, providerId, StringComparison.Ordinal)))
        {
            if (!string.IsNullOrWhiteSpace(role.Settings.Model)) referenced.Add(role.Settings.Model);
        }
        return referenced;
    }

    private readonly record struct ProviderRefreshFingerprint(string ProviderId, string BaseUrl, string ApiKey);

    private static bool IsOpenRouter(string? baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl) || !Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out var uri))
            return false;
        return uri.Host.Equals("openrouter.ai", StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith(".openrouter.ai", StringComparison.OrdinalIgnoreCase);
    }

    [RelayCommand]
    private void AddManualModel()
    {
        if (SelectedProvider == null || string.IsNullOrWhiteSpace(ManualModelId)) return;
        var id = ManualModelId.Trim();
        if (SelectedProvider.Models.All(model => !string.Equals(model.Id, id, StringComparison.Ordinal)))
        {
            SelectedProvider.Models.Add(new ProviderModelDescriptor
            {
                Id = id,
                DisplayName = id,
                Capability = Classify(id),
                IsManual = true
            });
        }
        ManualModelId = string.Empty;
        RebuildRoles();
        RebuildMetadataModels();
    }

    private void RebuildRoles()
    {
        Roles.Clear();
        AddRole(GetString("ProviderModels.Role.MainConversation", "Main conversation"), Config.AiModels.MainConversation, ModelCapability.Text);
        AddRole(GetString("ProviderModels.Role.TitleGeneration", "Title generation"), Config.AiModels.TitleGeneration, ModelCapability.Text);
        AddRole(GetString("ProviderModels.Role.ContextCompression", "Context compression"), Config.AiModels.ContextCompression, ModelCapability.Text);
        AddRole(GetString("ProviderModels.Role.Approval", "Automatic approval"), Config.AiModels.Approval, ModelCapability.Text);
        AddRole("Embedding", Config.AiModels.Embedding, ModelCapability.Embedding);
        AddRole(GetString("ProviderModels.Role.BrowserAgent", "Browser agent"), Config.AiModels.BrowserAgent, ModelCapability.Text);
        AddRole(GetString("ProviderModels.Role.SubAgent", "Sub-agent"), Config.AiModels.SubAgent, ModelCapability.Text);
        AddRole(GetString("ProviderModels.Role.KnowledgeMaintenance", "Knowledge maintenance"), Config.AiModels.KnowledgeMaintenance, ModelCapability.Text);
        AddRole(GetString("ProviderModels.Role.ImageRecognition", "Image recognition"), Config.AiModels.ImageRecognition, ModelCapability.Text);
        AddRole(GetString("ProviderModels.Role.Companion", "Virtual pet"), Config.AiModels.Companion, ModelCapability.Text);
    }

    private void AddRole(string name, ModelRoleSettings settings, ModelCapability requiredCapability)
    {
        var role = new ProviderRoleSelectionViewModel(name, settings, Providers, requiredCapability);
        Roles.Add(role);
    }

    private void OnCurrentConfigChanged(object? sender, AppConfig config)
    {
        Config = config;
        OnPropertyChanged(nameof(Providers));
        _providerStatus.Clear();
        SelectedProvider = null;
        RebuildRoles();
        RebuildMetadataModels();
    }

    partial void OnSelectedProviderChanged(OpenAiProviderConfiguration? value)
    {
        RebuildMetadataModels();
        // 这两条是按供应商归属的投影，换了选中就得重新问一遍。
        OnPropertyChanged(nameof(SelectedProviderStatusText));
        OnPropertyChanged(nameof(IsSelectedProviderRefreshing));
        SelectedProtocolOption = value == null
            ? null
            : ProtocolOptions.FirstOrDefault(option => option.Value == value.Protocol);
    }

    partial void OnSelectedProtocolOptionChanged(ProviderMetadataFilterOption<ProviderProtocol>? value)
    {
        if (SelectedProvider == null || value == null) return;
        if (SelectedProvider.Protocol == value.Value) return;
        SelectedProvider.Protocol = value.Value;
    }

    partial void OnMetadataSearchTextChanged(string value) => ApplyMetadataFilters();
    partial void OnSelectedCapabilityFilterChanged(ProviderMetadataFilterOption<ProviderMetadataCapabilityFilter>? value) => ApplyMetadataFilters();
    partial void OnSelectedMatchFilterChanged(ProviderMetadataFilterOption<ProviderMetadataMatchFilter>? value) => ApplyMetadataFilters();

    private void RebuildMetadataModels()
    {
        var selectedId = SelectedMetadataModel?.ExternalModelId;
        _allMetadataModels.Clear();
        var provider = SelectedProvider;
        if (provider != null)
        {
            foreach (var model in provider.Models.OrderBy(model => model.Id, StringComparer.Ordinal))
            {
                var capturedModel = model;
                _allMetadataModels.Add(new ProviderModelMetadataItemViewModel(
                    provider,
                    model,
                    _metadataResolver,
                    () => _metadataCatalog?.Current ?? OpenRouterCatalogSnapshot.Empty,
                    () => _metadataCatalog?.IsStale ?? false,
                    () => FindMetadataProfile(provider.Id, capturedModel.Id),
                    () => EnsureMetadataProfile(provider.Id, capturedModel.Id),
                    GetString));
            }
        }
        ApplyMetadataFilters();
        SelectedMetadataModel = _allMetadataModels.FirstOrDefault(item =>
                                    string.Equals(item.ExternalModelId, selectedId, StringComparison.Ordinal))
                                ?? MetadataModels.FirstOrDefault();
        OnPropertyChanged(nameof(MetadataCatalogText));
    }

    private void ApplyMetadataFilters()
    {
        var search = MetadataSearchText?.Trim() ?? string.Empty;
        var capability = SelectedCapabilityFilter?.Value ?? ProviderMetadataCapabilityFilter.All;
        var match = SelectedMatchFilter?.Value ?? ProviderMetadataMatchFilter.All;
        var filtered = _allMetadataModels.Where(item =>
            (search.Length == 0
             || item.ExternalModelId.Contains(search, StringComparison.OrdinalIgnoreCase)
             || item.DisplayName.Contains(search, StringComparison.OrdinalIgnoreCase)
             || item.OpenRouterModelId.Contains(search, StringComparison.OrdinalIgnoreCase)
             || item.OpenRouterModelName.Contains(search, StringComparison.OrdinalIgnoreCase))
            && (capability == ProviderMetadataCapabilityFilter.All
                || string.Equals(item.Capability.ToString(), capability.ToString(), StringComparison.Ordinal))
            && (match == ProviderMetadataMatchFilter.All
                || string.Equals(item.MatchStatus.ToString(), match.ToString(), StringComparison.Ordinal)));
        MetadataModels.Clear();
        foreach (var item in filtered) MetadataModels.Add(item);
        if (SelectedMetadataModel != null && !MetadataModels.Contains(SelectedMetadataModel))
            SelectedMetadataModel = MetadataModels.FirstOrDefault();
    }

    private ProviderModelMetadataProfile? FindMetadataProfile(string providerId, string externalModelId) =>
        Config.AiModels.ModelMetadataProfiles.FirstOrDefault(profile =>
            string.Equals(profile.ProviderId, providerId, StringComparison.Ordinal)
            && string.Equals(profile.ExternalModelId, externalModelId, StringComparison.Ordinal));

    private ProviderModelMetadataProfile EnsureMetadataProfile(string providerId, string externalModelId)
    {
        var existing = FindMetadataProfile(providerId, externalModelId);
        if (existing != null) return existing;
        var created = new ProviderModelMetadataProfile
        {
            ProviderId = providerId,
            ExternalModelId = externalModelId
        };
        Config.AiModels.ModelMetadataProfiles.Add(created);
        return created;
    }

    [RelayCommand]
    private async Task RefreshMetadataCatalogAsync()
    {
        if (_metadataCatalog == null || _disposed) return;
        var cancellation = new CancellationTokenSource();
        Interlocked.Exchange(ref _metadataRefreshCancellation, cancellation)?.Cancel();
        IsMetadataRefreshing = true;
        MetadataStatusText = GetString("ProviderModels.Metadata.Refreshing", "Refreshing OpenRouter metadata…");
        try
        {
            var result = await _metadataCatalog.RefreshAsync(force: true, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            MetadataStatusText = result.Message;
            RefreshMetadataResolution();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            MetadataStatusText = GetString("ProviderModels.Metadata.Cancelled", "Metadata refresh cancelled.");
        }
        finally
        {
            _ = Interlocked.CompareExchange(ref _metadataRefreshCancellation, null, cancellation);
            cancellation.Dispose();
            IsMetadataRefreshing = false;
        }
    }

    [RelayCommand]
    private void CopyMetadataText(string? text)
    {
        if (string.IsNullOrEmpty(text)) return;
        if (App.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            _ = TopLevel.GetTopLevel(desktop.MainWindow)?.Clipboard?.SetTextAsync(text);
    }

    [RelayCommand]
    private async Task ExportMetadataCsvAsync()
    {
        if (_userInteractionService == null || SelectedProvider == null || _disposed) return;
        var safeProviderName = string.Concat(SelectedProvider.DisplayName
            .Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));
        var path = await _userInteractionService.PickSaveFileAsync(
            GetString("ProviderModels.Metadata.ExportPickerTitle", "Export model metadata"),
            $"{(string.IsNullOrWhiteSpace(safeProviderName) ? "provider" : safeProviderName)}-model-metadata.csv",
            "CSV",
            ["*.csv"]);
        if (string.IsNullOrWhiteSpace(path) || _disposed) return;

        try
        {
            var rows = _allMetadataModels.Select(ToCsvRow).ToArray();
            await ModelMetadataCsvExporter.WriteAtomicallyAsync(path, rows);
            MetadataStatusText = string.Format(
                GetString("ProviderModels.Metadata.ExportSucceeded", "Exported {0} models to {1}"),
                rows.Length,
                path);
        }
        catch (Exception ex)
        {
            MetadataStatusText = string.Format(
                GetString("ProviderModels.Metadata.ExportFailed", "CSV export failed: {0}"),
                ex.Message);
        }
    }

    private static ModelMetadataCsvRow ToCsvRow(ProviderModelMetadataItemViewModel item) => new(
        item.Provider.Id,
        item.Provider.DisplayName,
        item.ExternalModelId,
        item.DisplayName,
        item.IsAvailable ? "Available" : "Unavailable",
        item.Capability.ToString(),
        item.MatchStatus.ToString(),
        item.Resolved.Match.Score,
        item.Resolved.Match.Margin,
        item.Resolved.Match.SelectedOpenRouterModelId ?? string.Empty,
        item.Resolved.ContextWindowTokens.Value,
        item.Resolved.ContextWindowTokens.Source.ToString(),
        item.Resolved.MaxCompletionTokens.Value,
        item.Resolved.MaxCompletionTokens.Source.ToString(),
        string.Join('|', item.Resolved.InputModalities.Order(StringComparer.OrdinalIgnoreCase)),
        string.Join('|', item.Resolved.OutputModalities.Order(StringComparer.OrdinalIgnoreCase)),
        item.Resolved.SupportsTools.Value.ToString(),
        item.Resolved.SupportsReasoning.Value.ToString(),
        item.Resolved.SupportsStructuredOutput.Value.ToString(),
        item.WarningText);

    private void OnMetadataCatalogChanged(object? sender, EventArgs e)
    {
        if (Dispatcher.UIThread.CheckAccess()) RefreshMetadataResolution();
        else Dispatcher.UIThread.Post(RefreshMetadataResolution);
    }

    private void RefreshMetadataResolution()
    {
        if (_disposed) return;
        foreach (var item in _allMetadataModels) item.RefreshResolved();
        ApplyMetadataFilters();
        OnPropertyChanged(nameof(MetadataCatalogText));
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        RebuildFilterOptions();
        RebuildRoles();
        RefreshMetadataResolution();
        OnPropertyChanged(nameof(MetadataCatalogText));
    }

    private void RebuildFilterOptions()
    {
        var selectedCapability = SelectedCapabilityFilter?.Value ?? ProviderMetadataCapabilityFilter.All;
        var selectedMatch = SelectedMatchFilter?.Value ?? ProviderMetadataMatchFilter.All;
        CapabilityFilters.Clear();
        CapabilityFilters.Add(new(ProviderMetadataCapabilityFilter.All, GetString("ProviderModels.Filter.AllCapabilities", "All capabilities")));
        CapabilityFilters.Add(new(ProviderMetadataCapabilityFilter.Text, GetString("ProviderModels.Filter.Text", "Text")));
        CapabilityFilters.Add(new(ProviderMetadataCapabilityFilter.Embedding, "Embedding"));
        CapabilityFilters.Add(new(ProviderMetadataCapabilityFilter.Image, GetString("ProviderModels.Filter.Image", "Image")));
        CapabilityFilters.Add(new(ProviderMetadataCapabilityFilter.Speech, GetString("ProviderModels.Filter.Speech", "Speech")));
        CapabilityFilters.Add(new(ProviderMetadataCapabilityFilter.Unknown, GetString("ProviderModels.Filter.Unknown", "Unknown")));
        MatchFilters.Clear();
        MatchFilters.Add(new(ProviderMetadataMatchFilter.All, GetString("ProviderModels.Filter.AllMatches", "All match states")));
        MatchFilters.Add(new(ProviderMetadataMatchFilter.Matched, GetString("ProviderModels.Filter.Matched", "Matched")));
        MatchFilters.Add(new(ProviderMetadataMatchFilter.Ambiguous, GetString("ProviderModels.Filter.Ambiguous", "Ambiguous")));
        MatchFilters.Add(new(ProviderMetadataMatchFilter.Unmatched, GetString("ProviderModels.Filter.Unmatched", "Unmatched")));
        MatchFilters.Add(new(ProviderMetadataMatchFilter.PinnedModelMissing, GetString("ProviderModels.Filter.PinnedMissing", "Pinned model missing")));
        MatchFilters.Add(new(ProviderMetadataMatchFilter.CustomOnly, "CustomOnly"));
        SelectedCapabilityFilter = CapabilityFilters.First(option => option.Value == selectedCapability);
        SelectedMatchFilter = MatchFilters.First(option => option.Value == selectedMatch);

        var selectedProtocol = SelectedProvider?.Protocol ?? ProviderProtocol.Auto;
        ProtocolOptions.Clear();
        ProtocolOptions.Add(new(ProviderProtocol.Auto, GetString("ProviderModels.Protocol.Auto", "Auto")));
        ProtocolOptions.Add(new(ProviderProtocol.ChatCompletions, GetString("ProviderModels.Protocol.ChatCompletions", "Chat Completions")));
        ProtocolOptions.Add(new(ProviderProtocol.Responses, GetString("ProviderModels.Protocol.Responses", "Responses")));
        SelectedProtocolOption = ProtocolOptions.First(option => option.Value == selectedProtocol);
    }

    private string GetString(string key, string fallback) => _localizationService?.GetString(key, fallback) ?? fallback;

    private static ModelCapability Classify(string id)
    {
        if (id.Contains("embed", StringComparison.OrdinalIgnoreCase)) return ModelCapability.Embedding;
        if (id.Contains("image", StringComparison.OrdinalIgnoreCase) || id.Contains("dall", StringComparison.OrdinalIgnoreCase)) return ModelCapability.Image;
        if (id.Contains("tts", StringComparison.OrdinalIgnoreCase) || id.Contains("audio", StringComparison.OrdinalIgnoreCase)) return ModelCapability.Speech;
        return ModelCapability.Text;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _configurationSession.CurrentChanged -= OnCurrentConfigChanged;
        if (_metadataCatalog != null) _metadataCatalog.CatalogChanged -= OnMetadataCatalogChanged;
        if (_localizationService != null) _localizationService.LanguageChanged -= OnLanguageChanged;
        OrcaRouter.Dispose();
        _refreshCancellation?.Cancel();
        _refreshCancellation = null;
        _metadataRefreshCancellation?.Cancel();
        _metadataRefreshCancellation = null;
    }
}

public partial class ProviderRoleSelectionViewModel : ViewModelBase
{
    private readonly ObservableCollection<OpenAiProviderConfiguration> _providers;
    private readonly ModelCapability _requiredCapability;

    public ProviderRoleSelectionViewModel(
        string name,
        ModelRoleSettings settings,
        ObservableCollection<OpenAiProviderConfiguration> providers,
        ModelCapability requiredCapability)
    {
        Name = name;
        Settings = settings;
        _providers = providers;
        _requiredCapability = requiredCapability;
        _selectedProvider = providers.FirstOrDefault(provider => provider.Id == settings.ProviderId);
        if (_selectedProvider == null && providers.Count == 1)
        {
            _selectedProvider = providers[0];
            Settings.ProviderId = providers[0].Id;
        }
        _selectedModel = FilterModels(_selectedProvider?.Models).FirstOrDefault(model => model.Id == settings.Model);
    }

    public string Name { get; }
    public ModelRoleSettings Settings { get; }
    public ObservableCollection<OpenAiProviderConfiguration> Providers => _providers;

    public IEnumerable<ProviderModelDescriptor> AvailableModels => FilterModels(SelectedProvider?.Models);

    private IEnumerable<ProviderModelDescriptor> FilterModels(ObservableCollection<ProviderModelDescriptor>? models)
    {
        if (models == null) return [];
        return _requiredCapability == ModelCapability.Embedding
            ? models.Where(m => m.Capability == ModelCapability.Embedding)
            : models.Where(m => m.Capability != ModelCapability.Embedding);
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AvailableModels))]
    private OpenAiProviderConfiguration? _selectedProvider;

    [ObservableProperty]
    private ProviderModelDescriptor? _selectedModel;

    partial void OnSelectedProviderChanged(OpenAiProviderConfiguration? value)
    {
        Settings.ProviderId = value?.Id ?? string.Empty;
        var available = FilterModels(value?.Models).ToList();
        if (value != null && available.All(model => model.Id != Settings.Model)) Settings.Model = available.FirstOrDefault()?.Id ?? string.Empty;
        SelectedModel = available.FirstOrDefault(model => model.Id == Settings.Model);
    }

    partial void OnSelectedModelChanged(ProviderModelDescriptor? value)
    {
        Settings.Model = value?.Id ?? string.Empty;
    }
}
