using Athena.UI.Services;
using Athena.UI.Services.Interfaces;
using Athena.UI.Services.ComputerUse;
using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.Generic;

namespace Athena.UI.ViewModels;

public sealed partial class AppSettingsViewModel : ViewModelBase, IDisposable
{
    private readonly AppSettingsState _state;
    private readonly ILocalizationService? _localizationService;
    private bool _disposed;

    public AppSettingsViewModel(
        AppConfigurationSession configurationSession,
        AboutViewModel about,
        ToolApprovalModePickerViewModel approvalModePicker,
        ILocalizationService? localizationService = null,
        IOpenRouterModelMetadataCatalog? metadataCatalog = null,
        IModelMetadataResolver? metadataResolver = null,
        IModelContextPolicyResolver? contextPolicyResolver = null,
        IUserInteractionService? userInteractionService = null,
        IPetDexCatalogService? petDexCatalogService = null,
        EmbeddedBrowser? browser = null,
        EmbeddedBrowserViewModel? browserSurface = null,
        ComputerUseService? computerUse = null,
        ModelRoleProbeService? roleProbes = null)
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
        ToolApproval = new ToolApprovalSettingsViewModel(_state, approvalModePicker);
        AgentRuntime = new AgentRuntimeSettingsViewModel(_state);
        Browser = new EmbeddedBrowserSettingsViewModel(_state, browser, browserSurface, computerUse, userInteractionService,
            localizationService, roleProbes ?? new ModelRoleProbeService(metadataResolver, metadataCatalog));
        RuntimeDiagnostics = new RuntimeDiagnosticsViewModel(
            localizationService,
            metadataCatalog,
            userInteractionService);

        Sections =
        [
            new("Settings.Nav.General", "General", "AthenaIconSettings", General, localizationService),
            new("Settings.Nav.ConversationContext", "Conversation & context", "AthenaIconConversationSettings", ConversationContext, localizationService),
            new("Settings.Nav.ToolApproval", "Tool approval", "AthenaIconToolApproval", ToolApproval, localizationService),
            new("Settings.Nav.AgentRuntime", "Execution & concurrency", "AthenaIconAgentRuntime", AgentRuntime, localizationService),
            new("Settings.Nav.EmbeddedBrowser", "Built-in browser", "AthenaIconFileWeb", Browser, localizationService),
            new("Settings.Nav.RuntimeDiagnostics", "Runtime diagnostics", "AthenaIconDiagnostics", RuntimeDiagnostics, localizationService),
            new("Settings.Nav.About", "About & updates", "AthenaIconAbout", about, localizationService)
        ];
        _selectedSection = Sections[0];
        Browser.OpenInMainRequested += OnBrowserOpenInMainRequested;
        if (_localizationService != null)
            _localizationService.LanguageChanged += OnLanguageChanged;
    }

    public event EventHandler? ReturnToMainRequested;

    public GeneralSettingsViewModel General { get; }
    public ConversationContextSettingsViewModel ConversationContext { get; }
    public ToolApprovalSettingsViewModel ToolApproval { get; }
    public AgentRuntimeSettingsViewModel AgentRuntime { get; }
    public EmbeddedBrowserSettingsViewModel Browser { get; }
    public RuntimeDiagnosticsViewModel RuntimeDiagnostics { get; }
    public IReadOnlyList<AppSettingsSection> Sections { get; }

    [ObservableProperty]
    private AppSettingsSection _selectedSection;

    partial void OnSelectedSectionChanged(AppSettingsSection value) => Browser.Show(ReferenceEquals(value.Content, Browser));

    private void OnBrowserOpenInMainRequested(object? sender, EventArgs e) =>
        ReturnToMainRequested?.Invoke(this, EventArgs.Empty);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Browser.OpenInMainRequested -= OnBrowserOpenInMainRequested;
        if (_localizationService != null)
            _localizationService.LanguageChanged -= OnLanguageChanged;
        ConversationContext.Dispose();
        RuntimeDiagnostics.Dispose();
        General.Dispose();
        Browser.Dispose();
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
