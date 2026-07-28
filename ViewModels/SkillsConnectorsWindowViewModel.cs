using CommunityToolkit.Mvvm.ComponentModel;
using Athena.UI.Services.Interfaces;
using System;
using System.Collections.Generic;

namespace Athena.UI.ViewModels;

public sealed partial class SkillsConnectorsWindowViewModel : ViewModelBase, IDisposable
{
    public SkillsConnectorsWindowViewModel(
        SkillsViewModel skills,
        McpConnectionsViewModel mcpConnections,
        SpeechSettingsViewModel speech,
        ImageGenerationSettingsViewModel imageGeneration,
        WebSearchSettingsViewModel webSearch,
        DocumentParserSettingsViewModel documentParser,
        ILocalizationService? localizationService = null)
    {
        string Text(string key, string fallback) => localizationService?.GetString(key, fallback) ?? fallback;
        Sections =
        [
            new(Text("Connectors.Nav.Skills", "Skills"), skills),
            new(Text("Connectors.Nav.Mcp", "MCP"), mcpConnections),
            new(Text("Connectors.Nav.Speech", "Speech"), speech),
            new(Text("Connectors.Nav.Image", "Image generation"), imageGeneration),
            new(Text("Connectors.Nav.WebSearch", "Web Search"), webSearch),
            new(Text("Connectors.Nav.Document", "Document parsing"), documentParser)
        ];
        _selectedSection = Sections[0];
    }

    public IReadOnlyList<SkillsConnectorSection> Sections { get; }

    [ObservableProperty]
    private SkillsConnectorSection _selectedSection;

    public void Dispose()
    {
        foreach (var section in Sections)
            if (section.Content is IDisposable disposable) disposable.Dispose();
    }
}

public sealed record SkillsConnectorSection(string Title, ViewModelBase Content);
