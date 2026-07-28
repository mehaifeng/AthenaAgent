using Athena.UI.Models;
using Athena.UI.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.Generic;
using System.ComponentModel;

namespace Athena.UI.ViewModels;

public partial class DocumentParserSettingsViewModel : ViewModelBase, IDisposable
{
    private readonly AppConfigurationSession _configurationSession;

    public DocumentParserSettingsViewModel(AppConfigurationSession configurationSession)
    {
        _configurationSession = configurationSession;
        _config = configurationSession.Current;
        Config.PropertyChanged += OnConfigPropertyChanged;
        _configurationSession.CurrentChanged += OnCurrentConfigChanged;
    }

    [ObservableProperty] private AppConfig _config;

    public IReadOnlyList<DocumentParserMode> Modes { get; } =
        [DocumentParserMode.AgentLightweight, DocumentParserMode.Precision];

    public bool CanEditToken => Config.DocumentParserEnabled && Config.DocumentParserMode == DocumentParserMode.Precision;

    private void OnConfigPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(AppConfig.DocumentParserEnabled) or nameof(AppConfig.DocumentParserMode))
            OnPropertyChanged(nameof(CanEditToken));
    }

    private void OnCurrentConfigChanged(object? sender, AppConfig config)
    {
        Config.PropertyChanged -= OnConfigPropertyChanged;
        Config = config;
        Config.PropertyChanged += OnConfigPropertyChanged;
        OnPropertyChanged(nameof(CanEditToken));
    }

    public void Dispose()
    {
        Config.PropertyChanged -= OnConfigPropertyChanged;
        _configurationSession.CurrentChanged -= OnCurrentConfigChanged;
    }
}
