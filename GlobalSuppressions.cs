using System.Diagnostics.CodeAnalysis;

[assembly: SuppressMessage(
    "Reliability",
    "CA2000:Dispose objects before losing scope",
    Justification = "The returned lease transfers disposal ownership to the awaiting caller.",
    Scope = "type",
    Target = "~T:Athena.UI.Services.ConversationExecutionCoordinator")]
[assembly: SuppressMessage(
    "Reliability",
    "CA2000:Dispose objects before losing scope",
    Justification = "Playwright pages are borrowed from and owned by the browser runtime session.",
    Scope = "type",
    Target = "~T:Athena.UI.Services.Browser.PlaywrightBrowserService")]
[assembly: SuppressMessage(
    "Reliability",
    "CA2000:Dispose objects before losing scope",
    Justification = "Serilog takes ownership of configured sinks and disposes them with the logger.",
    Scope = "type",
    Target = "~T:Athena.UI.Services.SerilogConfiguration")]
[assembly: SuppressMessage(
    "Reliability",
    "CA2000:Dispose objects before losing scope",
    Justification = "Editor-tab ownership transfers to the workbench collection, which disposes every removed tab.",
    Scope = "type",
    Target = "~T:Athena.UI.ViewModels.WorkspaceWorkbenchViewModel")]
[assembly: SuppressMessage(
    "Reliability",
    "CA2000:Dispose objects before losing scope",
    Justification = "MCP client ownership transfers to the manager registry and is released on removal.",
    Scope = "type",
    Target = "~T:Athena.UI.Services.Mcp.McpClientManager")]
[assembly: SuppressMessage(
    "Reliability",
    "CA2000:Dispose objects before losing scope",
    Justification = "Prepared imports transfer temporary-resource ownership to the import result.",
    Scope = "type",
    Target = "~T:Athena.UI.Services.Skills.SkillCatalogService")]
[assembly: SuppressMessage(
    "Reliability",
    "CA2000:Dispose objects before losing scope",
    Justification = "Conversation and session ownership transfers to the conversation tree, which disposes removed sessions.",
    Scope = "type",
    Target = "~T:Athena.UI.ViewModels.MainWindowViewModel")]
