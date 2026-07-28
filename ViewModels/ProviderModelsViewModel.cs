using Athena.UI.Models;
using Athena.UI.Services.Interfaces;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;

namespace Athena.UI.ViewModels;

public partial class ProviderModelsViewModel : ViewModelBase
{
    private readonly IConfigService _configService;
    private readonly IModelCatalogService _catalogService;

    public ProviderModelsViewModel(IConfigService configService, IModelCatalogService catalogService)
    {
        _configService = configService;
        _catalogService = catalogService;
        Config = configService.Load();
        foreach (var provider in Config.AiModels.Providers) Observe(provider);
        RebuildRoles();
    }

    public AppConfig Config { get; }
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
    private async Task AddProviderAsync()
    {
        var provider = new OpenAiProviderConfiguration
        {
            DisplayName = $"供应商 {Providers.Count + 1}",
            ProviderPreset = "OpenAI",
            BaseUrl = "https://api.openai.com/v1"
        };
        Providers.Add(provider);
        Observe(provider);
        SelectedProvider = provider;
        RebuildRoles();
        await SaveAsync();
    }

    [RelayCommand]
    private async Task DeleteProviderAsync(OpenAiProviderConfiguration? provider)
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
        provider.PropertyChanged -= OnProviderChanged;
        Providers.Remove(provider);
        SelectedProvider = Providers.FirstOrDefault();
        RebuildRoles();
        await SaveAsync();
    }

    [RelayCommand]
    private async Task RefreshModelsAsync(OpenAiProviderConfiguration? provider)
    {
        provider ??= SelectedProvider;
        if (provider == null) return;
        IsRefreshing = true;
        try
        {
            var result = await _catalogService.GetModelsAsync(provider.BaseUrl, provider.ApiKey);
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
            await SaveAsync();
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    [RelayCommand]
    private async Task AddManualModelAsync()
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
        await SaveAsync();
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
        var role = new ProviderRoleSelectionViewModel(name, settings, Providers, SaveAsync);
        Roles.Add(role);
    }

    private void Observe(OpenAiProviderConfiguration provider) => provider.PropertyChanged += OnProviderChanged;

    private void OnProviderChanged(object? sender, PropertyChangedEventArgs e) => _ = SaveAsync();

    private Task SaveAsync() => _configService.SaveAsync(Config);

    private static ModelCapability Classify(string id)
    {
        if (id.Contains("embed", StringComparison.OrdinalIgnoreCase)) return ModelCapability.Embedding;
        if (id.Contains("image", StringComparison.OrdinalIgnoreCase) || id.Contains("dall", StringComparison.OrdinalIgnoreCase)) return ModelCapability.Image;
        if (id.Contains("tts", StringComparison.OrdinalIgnoreCase) || id.Contains("audio", StringComparison.OrdinalIgnoreCase)) return ModelCapability.Speech;
        return ModelCapability.Text;
    }
}

public partial class ProviderRoleSelectionViewModel : ViewModelBase
{
    private readonly Func<Task> _save;
    private readonly ObservableCollection<OpenAiProviderConfiguration> _providers;

    public ProviderRoleSelectionViewModel(
        string name,
        ModelRoleSettings settings,
        ObservableCollection<OpenAiProviderConfiguration> providers,
        Func<Task> save)
    {
        Name = name;
        Settings = settings;
        _providers = providers;
        _save = save;
        _selectedProvider = providers.FirstOrDefault(provider => provider.Id == settings.ProviderId);
        if (_selectedProvider == null && providers.Count == 1)
        {
            _selectedProvider = providers[0];
            Settings.ProviderId = providers[0].Id;
        }
        _selectedModel = _selectedProvider?.Models.FirstOrDefault(model => model.Id == settings.Model);
        Settings.PropertyChanged += (_, _) => _ = _save();
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
        _ = _save();
    }

    partial void OnSelectedModelChanged(ProviderModelDescriptor? value)
    {
        Settings.Model = value?.Id ?? string.Empty;
        _ = _save();
    }
}
