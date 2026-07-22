using Athena.UI.Models;
using Athena.UI.Services.Interfaces;
using CommunityToolkit.Mvvm.ComponentModel;
using System.ComponentModel;

namespace Athena.UI.ViewModels;

public sealed class AppSettingsViewModel : ViewModelBase
{
    private readonly IConfigService _configService;
    private readonly ILocalizationService _localizationService;

    public AppSettingsViewModel(IConfigService configService, ILocalizationService localizationService)
    {
        _configService = configService;
        _localizationService = localizationService;
        Config = configService.Load();
        Config.PropertyChanged += OnConfigChanged;
    }

    public AppConfig Config { get; }

    private void OnConfigChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppConfig.Theme)) App.SetTheme(Config.Theme);
        if (e.PropertyName == nameof(AppConfig.Language)) _localizationService.SwitchLanguage(Config.Language);
        _ = _configService.SaveAsync(Config);
    }
}
