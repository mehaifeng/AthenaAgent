using CommunityToolkit.Mvvm.ComponentModel;
using Athena.UI.Services.Interfaces;
using System;
using System.Collections.Generic;

namespace Athena.UI.ViewModels;

public sealed partial class SkillsConnectorsWindowViewModel : ViewModelBase, IDisposable
{
    private readonly ILocalizationService? _localizationService;
    private bool _disposed;

    private static readonly (string Key, string Fallback, string IconKey)[] SectionTitles =
    [
        ("Connectors.Nav.Skills", "Skills", "SemiIconWrench"),
        ("Connectors.Nav.Connectors", "Connectors", "SemiIconLink"),
        ("Connectors.Nav.Speech", "Speech generation", "SemiIconVolume2"),
        ("Connectors.Nav.Image", "Image generation", "SemiIconImage"),
        ("Connectors.Nav.WebSearch", "Web Search", "SemiIconSearch"),
        ("Connectors.Nav.Document", "Document parsing", "SemiIconScan")
    ];

    public SkillsConnectorsWindowViewModel(
        SkillsViewModel skills,
        McpConnectionsViewModel mcpConnections,
        SpeechSettingsViewModel speech,
        ImageGenerationSettingsViewModel imageGeneration,
        WebSearchSettingsViewModel webSearch,
        DocumentParserSettingsViewModel documentParser,
        ILocalizationService? localizationService = null)
    {
        _localizationService = localizationService;
        string Text(string key, string fallback) => localizationService?.GetString(key, fallback) ?? fallback;
        ViewModelBase[] contents = [skills, mcpConnections, speech, imageGeneration, webSearch, documentParser];
        var sections = new SkillsConnectorSection[SectionTitles.Length];
        for (var i = 0; i < SectionTitles.Length; i++)
        {
            sections[i] = new SkillsConnectorSection(
                Text(SectionTitles[i].Key, SectionTitles[i].Fallback),
                SectionTitles[i].IconKey,
                contents[i]);
        }
        Sections = sections;
        _selectedSection = Sections[0];
        if (_localizationService != null)
        {
            _localizationService.LanguageChanged += OnLanguageChanged;
        }
    }

    public IReadOnlyList<SkillsConnectorSection> Sections { get; }

    [ObservableProperty]
    private SkillsConnectorSection _selectedSection;

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        for (var i = 0; i < Sections.Count; i++)
        {
            Sections[i].Title = _localizationService?.GetString(SectionTitles[i].Key, SectionTitles[i].Fallback)
                ?? SectionTitles[i].Fallback;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_localizationService != null)
        {
            _localizationService.LanguageChanged -= OnLanguageChanged;
        }
        foreach (var section in Sections)
            if (section.Content is IDisposable disposable) disposable.Dispose();
    }
}

public sealed partial class SkillsConnectorSection : ObservableObject
{
    private string _title;

    public SkillsConnectorSection(string title, string iconKey, ViewModelBase content)
    {
        _title = title;
        IconKey = iconKey;
        Content = content;
    }

    public string Title
    {
        get => _title;
        set => SetProperty(ref _title, value);
    }

    /// <summary>Semi 图标资源 key（由 ToolIconKeyToGeometryConverter 解析为 PathIcon Geometry）。</summary>
    public string IconKey { get; }

    public ViewModelBase Content { get; }
}
