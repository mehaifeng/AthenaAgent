using Athena.UI.Models;
using Athena.UI.Services;
using Athena.UI.Services.Interfaces;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Athena.UI.ViewModels;

public partial class ProviderModelsViewModel : ViewModelBase, IDisposable
{
    private readonly AppConfigurationSession _configurationSession;
    private readonly IModelCatalogService _catalogService;
    private CancellationTokenSource? _refreshCancellation;
    private bool _disposed;

    public ProviderModelsViewModel(AppConfigurationSession configurationSession, IModelCatalogService catalogService)
    {
        _configurationSession = configurationSession;
        _catalogService = catalogService;
        _config = configurationSession.Current;
        RebuildRoles();
        _configurationSession.CurrentChanged += OnCurrentConfigChanged;
    }

    [ObservableProperty]
    private AppConfig _config;

    public ObservableCollection<OpenAiProviderConfiguration> Providers => Config.AiModels.Providers;
    public ObservableCollection<ProviderRoleSelectionViewModel> Roles { get; } = new();

    [ObservableProperty]
    private OpenAiProviderConfiguration? _selectedProvider;

    [ObservableProperty]
    private string _manualModelId = string.Empty;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private bool _isRefreshing;

    [RelayCommand]
    private void AddProvider()
    {
        var provider = new OpenAiProviderConfiguration
        {
            DisplayName = $"供应商 {Providers.Count + 1}",
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
            StatusText = "无法删除：仍被这些分工引用：" + string.Join("、", references);
            return;
        }
        Providers.Remove(provider);
        SelectedProvider = Providers.FirstOrDefault();
        RebuildRoles();
    }

    [RelayCommand]
    private async Task RefreshModelsAsync(OpenAiProviderConfiguration? provider)
    {
        provider ??= SelectedProvider;
        if (provider == null || _disposed) return;
        var cancellation = new CancellationTokenSource();
        _refreshCancellation = cancellation;
        IsRefreshing = true;
        try
        {
            var result = await _catalogService.GetModelsAsync(provider.BaseUrl, provider.ApiKey, cancellation.Token);
            if (_disposed || cancellation.IsCancellationRequested) return;
            if (!result.Success)
            {
                StatusText = "刷新失败，已保留旧列表：" + result.ErrorMessage;
                return;
            }

            var modelIds = new HashSet<string>(result.Models, StringComparer.OrdinalIgnoreCase);

            // OpenRouter: 额外拉取精确的 Embedding 模型列表并合并。
            if (IsOpenRouter(provider.BaseUrl))
            {
                var embedResult = await _catalogService.GetEmbeddingModelsAsync(provider.BaseUrl, provider.ApiKey, cancellation.Token);
                if (!_disposed && !cancellation.IsCancellationRequested && embedResult.Success)
                {
                    foreach (var id in embedResult.Models) modelIds.Add(id);
                }
            }

            var manual = provider.Models.Where(model => model.IsManual).ToList();
            provider.Models.Clear();
            foreach (var id in modelIds.OrderBy(id => id, StringComparer.OrdinalIgnoreCase))
            {
                provider.Models.Add(new ProviderModelDescriptor
                {
                    Id = id,
                    DisplayName = id,
                    Capability = Classify(id)
                });
            }
            foreach (var model in manual.Where(manualModel => provider.Models.All(model => model.Id != manualModel.Id))) provider.Models.Add(model);
            provider.ModelsRefreshedAt = DateTimeOffset.Now;
            StatusText = $"已发现 {provider.Models.Count} 个模型";
            RebuildRoles();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        finally
        {
            if (ReferenceEquals(_refreshCancellation, cancellation))
            {
                _refreshCancellation = null;
                if (!_disposed) IsRefreshing = false;
            }
            cancellation.Dispose();
        }
    }

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
        if (SelectedProvider.Models.All(model => !string.Equals(model.Id, id, StringComparison.OrdinalIgnoreCase)))
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
    }

    private void RebuildRoles()
    {
        Roles.Clear();
        AddRole("主对话", Config.AiModels.MainConversation, ModelCapability.Text);
        AddRole("标题生成", Config.AiModels.TitleGeneration, ModelCapability.Text);
        AddRole("上下文压缩", Config.AiModels.ContextCompression, ModelCapability.Text);
        AddRole("自动审批", Config.AiModels.Approval, ModelCapability.Text);
        AddRole("Embedding", Config.AiModels.Embedding, ModelCapability.Embedding);
        AddRole("自动化浏览器", Config.AiModels.BrowserAgent, ModelCapability.Text);
        AddRole("子代理", Config.AiModels.SubAgent, ModelCapability.Text);
        AddRole("知识整理", Config.AiModels.KnowledgeMaintenance, ModelCapability.Text);
        AddRole("图像识别", Config.AiModels.ImageRecognition, ModelCapability.Text);
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
        SelectedProvider = null;
        RebuildRoles();
    }

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
        _refreshCancellation?.Cancel();
        _refreshCancellation = null;
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
