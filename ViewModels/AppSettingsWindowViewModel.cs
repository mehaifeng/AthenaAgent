using Athena.UI.Services;
using Athena.UI.Services.Interfaces;
using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.Generic;

namespace Athena.UI.ViewModels;

public sealed partial class AppSettingsWindowViewModel : ViewModelBase, IDisposable
{
    private readonly AppSettingsState _state;
    private readonly ILocalizationService? _localizationService;
    private bool _disposed;

    public AppSettingsWindowViewModel(
        AppConfigurationSession configurationSession,
        AboutViewModel about,
        IHeadlessBrowserService? browserService = null,
        IBrowserVisionService? browserVisionService = null,
        ILocalizationService? localizationService = null)
    {
        _localizationService = localizationService;
        _state = new AppSettingsState(configurationSession);
        General = new GeneralSettingsViewModel(_state);
        ConversationContext = new ConversationContextSettingsViewModel(_state);
        ToolApproval = new ToolApprovalSettingsViewModel(_state, localizationService);
        AgentRuntime = new AgentRuntimeSettingsViewModel(_state);
        RuntimeDiagnostics = new RuntimeDiagnosticsViewModel(
            _state,
            browserService,
            browserVisionService,
            localizationService);

        Sections =
        [
            new("Settings.Nav.General", "General", General, localizationService),
            new("Settings.Nav.ConversationContext", "Conversation & context", ConversationContext, localizationService),
            new("Settings.Nav.ToolApproval", "Tool approval", ToolApproval, localizationService),
            new("Settings.Nav.AgentRuntime", "Agent runtime", AgentRuntime, localizationService),
            new("Settings.Nav.RuntimeDiagnostics", "Runtime diagnostics", RuntimeDiagnostics, localizationService),
            new("Settings.Nav.About", "About & updates", about, localizationService)
        ];
        _selectedSection = Sections[0];
        if (_localizationService != null)
            _localizationService.LanguageChanged += OnLanguageChanged;
    }

    public GeneralSettingsViewModel General { get; }
    public ConversationContextSettingsViewModel ConversationContext { get; }
    public ToolApprovalSettingsViewModel ToolApproval { get; }
    public AgentRuntimeSettingsViewModel AgentRuntime { get; }
    public RuntimeDiagnosticsViewModel RuntimeDiagnostics { get; }
    public IReadOnlyList<AppSettingsSection> Sections { get; }

    [ObservableProperty]
    private AppSettingsSection _selectedSection;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_localizationService != null)
            _localizationService.LanguageChanged -= OnLanguageChanged;
        ToolApproval.Dispose();
        RuntimeDiagnostics.Dispose();
        _state.Dispose();
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        foreach (var section in Sections)
            section.RefreshTitle(_localizationService);
    }
}

public sealed partial class AppSettingsSection : ViewModelBase
{
    private readonly string _titleKey;
    private readonly string _fallbackTitle;

    public AppSettingsSection(
        string titleKey,
        string fallbackTitle,
        ViewModelBase content,
        ILocalizationService? localizationService)
    {
        _titleKey = titleKey;
        _fallbackTitle = fallbackTitle;
        Content = content;
        _title = fallbackTitle;
        RefreshTitle(localizationService);
    }

    [ObservableProperty]
    private string _title;

    public ViewModelBase Content { get; }

    public void RefreshTitle(ILocalizationService? localizationService) =>
        Title = localizationService?.GetString(_titleKey, _fallbackTitle) ?? _fallbackTitle;
}
