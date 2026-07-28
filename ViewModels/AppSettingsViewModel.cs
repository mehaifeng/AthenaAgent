using Athena.UI.Models;
using Athena.UI.Services.Interfaces;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.ComponentModel;

namespace Athena.UI.ViewModels;

public sealed partial class AppSettingsViewModel : ViewModelBase
{
    private readonly IConfigService _configService;
    private readonly ILocalizationService _localizationService;

    public AppSettingsViewModel(IConfigService configService, ILocalizationService localizationService)
    {
        _configService = configService;
        _localizationService = localizationService;
        Config = configService.Load();

        // 浏览器页已从常规设置中移除，能力默认可用；实际调用仍受模型配置与工具审批约束。
        var browserWasDisabled = !Config.BrowserEnabled;
        Config.BrowserEnabled = true;
        Config.PropertyChanged += OnConfigChanged;
        if (browserWasDisabled) _ = _configService.SaveAsync(Config);
    }

    public AppConfig Config { get; }

    private void OnConfigChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppConfig.Theme)) App.SetTheme(Config.Theme);
        if (e.PropertyName == nameof(AppConfig.Language)) _localizationService.SwitchLanguage(Config.Language);
        _ = _configService.SaveAsync(Config);
    }

    [RelayCommand]
    private void SetApprovalMode(string? mode)
    {
        if (System.Enum.TryParse<ToolApprovalMode>(mode, true, out var parsed))
            Config.ToolApprovalMode = parsed;
    }
}
