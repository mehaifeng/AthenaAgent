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
        ILocalizationService? localizationService = null,
        IOpenRouterModelMetadataCatalog? metadataCatalog = null,
        IModelMetadataResolver? metadataResolver = null,
        IModelContextPolicyResolver? contextPolicyResolver = null,
        ITokenCalibrationService? tokenCalibration = null,
        IUserInteractionService? userInteractionService = null,
        IPetDexCatalogService? petDexCatalogService = null)
    {
        _localizationService = localizationService;
        _state = new AppSettingsState(configurationSession);
        General = new GeneralSettingsViewModel(_state, petDexCatalogService, localizationService);
        ConversationContext = new ConversationContextSettingsViewModel(
            _state,
            metadataCatalog,
            metadataResolver,
            contextPolicyResolver,
            localizationService);
        ToolApproval = new ToolApprovalSettingsViewModel(_state, localizationService);
        AgentRuntime = new AgentRuntimeSettingsViewModel(_state);
        RuntimeDiagnostics = new RuntimeDiagnosticsViewModel(
            _state,
            browserService,
            browserVisionService,
            localizationService,
            tokenCalibration,
            metadataCatalog,
            userInteractionService);

        Sections =
        [
            new("Settings.Nav.General", "General", "AthenaIconSettings", General, localizationService),
            new("Settings.Nav.ConversationContext", "Conversation & context", "AthenaIconConversationSettings", ConversationContext, localizationService),
            new("Settings.Nav.ToolApproval", "Tool approval", "AthenaIconToolApproval", ToolApproval, localizationService),
            new("Settings.Nav.AgentRuntime", "Execution & concurrency", "AthenaIconAgentRuntime", AgentRuntime, localizationService),
            new("Settings.Nav.RuntimeDiagnostics", "Runtime diagnostics", "AthenaIconDiagnostics", RuntimeDiagnostics, localizationService),
            new("Settings.Nav.About", "About & updates", "AthenaIconAbout", about, localizationService)
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
        ConversationContext.Dispose();
        RuntimeDiagnostics.Dispose();
        General.Dispose();
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
        string? iconKey,
        ViewModelBase content,
        ILocalizationService? localizationService)
    {
        _titleKey = titleKey;
        _fallbackTitle = fallbackTitle;
        IconKey = iconKey;
        Content = content;
        _title = fallbackTitle;
        RefreshTitle(localizationService);
    }

    [ObservableProperty]
    private string _title;

    /// <summary>左侧导航项图标的 AppIcons 资源 key；在 XAML 中经 ToolIconKeyToGeometryConverter 解析为 StreamGeometry。</summary>
    public string? IconKey { get; }

    public ViewModelBase Content { get; }

    public void RefreshTitle(ILocalizationService? localizationService) =>
        Title = localizationService?.GetString(_titleKey, _fallbackTitle) ?? _fallbackTitle;
}
