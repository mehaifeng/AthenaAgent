using Athena.UI.Models;
using Athena.UI.Services;
using Athena.UI.Services.Interfaces;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
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

    public bool UseCustomEmbeddingConnection
    {
        get => Config.EmbeddingCredentialSource == EmbeddingConnectionSource.Custom;
        set
        {
            var source = value ? EmbeddingConnectionSource.Custom : EmbeddingConnectionSource.Provider;
            if (Config.EmbeddingCredentialSource == source) return;
            Config.EmbeddingCredentialSource = source;
            OnPropertyChanged();
        }
    }

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
        if (Config.ChatAudioProviderId == provider.Id) references.Add("语音");
        if (Config.ImageGenerationProviderId == provider.Id) references.Add("图像生成");
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
            var manual = provider.Models.Where(model => model.IsManual).ToList();
            provider.Models.Clear();
            foreach (var id in result.Models)
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
        AddRole("主对话", Config.AiModels.MainConversation);
        AddRole("标题生成", Config.AiModels.TitleGeneration);
        AddRole("上下文压缩", Config.AiModels.ContextCompression);
        AddRole("自动审批", Config.AiModels.Approval);
        AddRole("Embedding", Config.AiModels.Embedding);
        AddRole("自动化浏览器", Config.AiModels.BrowserAgent);
        AddRole("子代理", Config.AiModels.SubAgent);
        AddRole("知识整理", Config.AiModels.KnowledgeMaintenance);
        AddRole("图像识别", Config.AiModels.ImageRecognition);
    }

    private void AddRole(string name, ModelRoleSettings settings)
    {
        var role = new ProviderRoleSelectionViewModel(name, settings, Providers);
        Roles.Add(role);
    }

    private void OnCurrentConfigChanged(object? sender, AppConfig config)
    {
        Config = config;
        OnPropertyChanged(nameof(Providers));
        OnPropertyChanged(nameof(UseCustomEmbeddingConnection));
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

    public ProviderRoleSelectionViewModel(
        string name,
        ModelRoleSettings settings,
        ObservableCollection<OpenAiProviderConfiguration> providers)
    {
        Name = name;
        Settings = settings;
        _providers = providers;
        _selectedProvider = providers.FirstOrDefault(provider => provider.Id == settings.ProviderId);
        if (_selectedProvider == null && providers.Count == 1)
        {
            _selectedProvider = providers[0];
            Settings.ProviderId = providers[0].Id;
        }
        _selectedModel = _selectedProvider?.Models.FirstOrDefault(model => model.Id == settings.Model);
    }

    public string Name { get; }
    public ModelRoleSettings Settings { get; }
    public ObservableCollection<OpenAiProviderConfiguration> Providers => _providers;
    public ObservableCollection<ProviderModelDescriptor> AvailableModels => SelectedProvider?.Models ?? [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AvailableModels))]
    private OpenAiProviderConfiguration? _selectedProvider;

    [ObservableProperty]
    private ProviderModelDescriptor? _selectedModel;

    partial void OnSelectedProviderChanged(OpenAiProviderConfiguration? value)
    {
        Settings.ProviderId = value?.Id ?? string.Empty;
        if (value != null && value.Models.All(model => model.Id != Settings.Model)) Settings.Model = value.Models.FirstOrDefault()?.Id ?? string.Empty;
        SelectedModel = value?.Models.FirstOrDefault(model => model.Id == Settings.Model);
    }

    partial void OnSelectedModelChanged(ProviderModelDescriptor? value)
    {
        Settings.Model = value?.Id ?? string.Empty;
    }
}
