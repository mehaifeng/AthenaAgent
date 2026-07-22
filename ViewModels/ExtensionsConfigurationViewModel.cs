using Athena.UI.Models;
using Athena.UI.Services.Interfaces;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;

namespace Athena.UI.ViewModels;

public partial class ExtensionsConfigurationViewModel : ViewModelBase
{
    private readonly IConfigService _configService;

    public ExtensionsConfigurationViewModel(IConfigService configService)
    {
        _configService = configService;
        Config = configService.Load();
        Config.PropertyChanged += OnConfigChanged;
        Config.AiModels.BrowserAgent.PropertyChanged += OnConfigChanged;
        _audioProvider = Providers.FirstOrDefault(provider => provider.Id == Config.ChatAudioProviderId);
        _imageProvider = Providers.FirstOrDefault(provider => provider.Id == Config.ImageGenerationProviderId);
        _browserProvider = Providers.FirstOrDefault(provider => provider.Id == Config.BrowserProviderId);
    }

    public AppConfig Config { get; }
    public ObservableCollection<OpenAiProviderConfiguration> Providers => Config.AiModels.Providers;

    [ObservableProperty] private OpenAiProviderConfiguration? _audioProvider;
    [ObservableProperty] private OpenAiProviderConfiguration? _imageProvider;
    [ObservableProperty] private OpenAiProviderConfiguration? _browserProvider;

    partial void OnAudioProviderChanged(OpenAiProviderConfiguration? value)
    {
        Config.ChatAudioProviderId = value?.Id ?? string.Empty;
        _ = _configService.SaveAsync(Config);
    }

    partial void OnImageProviderChanged(OpenAiProviderConfiguration? value)
    {
        Config.ImageGenerationProviderId = value?.Id ?? string.Empty;
        _ = _configService.SaveAsync(Config);
    }

    partial void OnBrowserProviderChanged(OpenAiProviderConfiguration? value)
    {
        Config.BrowserProviderId = value?.Id ?? string.Empty;
        Config.AiModels.BrowserAgent.ProviderId = value?.Id ?? string.Empty;
        _ = _configService.SaveAsync(Config);
    }

    private void OnConfigChanged(object? sender, PropertyChangedEventArgs e) => _ = _configService.SaveAsync(Config);

    [RelayCommand]
    private void SetApprovalMode(string? mode)
    {
        if (System.Enum.TryParse<ToolApprovalMode>(mode, true, out var parsed)) Config.ToolApprovalMode = parsed;
    }
}
