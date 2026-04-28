using Athena.UI.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Athena.UI.Services.Browser;

public sealed class BrowserActionRegistry
{
    private readonly Dictionary<string, BrowserActionDefinition> _definitions;

    public BrowserActionRegistry()
    {
        _definitions = CreateDefaultDefinitions()
            .ToDictionary(definition => NormalizeName(definition.Name), StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<BrowserActionDefinition> Definitions => _definitions.Values.ToList();

    public bool TryGet(string name, out BrowserActionDefinition definition) =>
        _definitions.TryGetValue(NormalizeName(name), out definition!);

    public async Task<BrowserActionResult> ExecuteAsync(
        BrowserSession session,
        BrowserAgentAction action,
        CancellationToken cancellationToken = default)
    {
        action.Name = NormalizeName(action.Name);
        if (!TryGet(action.Name, out var definition))
        {
            return new BrowserActionResult
            {
                Success = false,
                Action = BrowserActionType.None,
                ActionName = action.Name,
                SessionId = session.SessionId,
                Message = $"Unknown browser action: {action.Name}",
                Error = $"Unknown browser action: {action.Name}"
            };
        }

        return await session.ExecuteActionAsync(action, definition, cancellationToken);
    }

    public static string NormalizeName(string? name)
    {
        var normalized = (name ?? string.Empty).Trim().ToLowerInvariant().Replace("-", "_");
        return normalized switch
        {
            "goto" or "go_to" or "open" or "open_url" => "navigate",
            "type" or "type_text" or "fill" => "input",
            "press_key" or "presskey" or "key" => "send_keys",
            "upload" or "set_file" or "set_input_files" => "upload_file",
            "close" => "close_tab",
            "finish" => "done",
            "extract_text" or "extracttext" => "extract",
            "select" => "select_dropdown",
            "dropdown" or "get_dropdown_options" => "dropdown_options",
            "scroll_to_text" => "find_text",
            _ => normalized
        };
    }

    private static IEnumerable<BrowserActionDefinition> CreateDefaultDefinitions()
    {
        yield return Definition("search", BrowserActionType.Search, "Search the web with duckduckgo, google, or bing.", """{"query":"string","engine":"duckduckgo|google|bing"}""", terminates: true);
        yield return Definition("navigate", BrowserActionType.Navigate, "Navigate to an absolute http or https URL.", """{"url":"string","new_tab":"boolean"}""", terminates: true);
        yield return Definition("go_back", BrowserActionType.GoBack, "Go back in browser history.", "{}", terminates: true);
        yield return Definition("wait", BrowserActionType.Wait, "Wait for page changes or loading.", """{"seconds":"number"}""");
        yield return Definition("click", BrowserActionType.Click, "Click an indexed visible element.", """{"index":"number"}""");
        yield return Definition("input", BrowserActionType.Input, "Input text into an indexed editable element.", """{"index":"number","text":"string","clear":"boolean"}""");
        yield return Definition("upload_file", BrowserActionType.Upload, "Upload a local file using an indexed file input.", """{"index":"number","path":"string"}""");
        yield return Definition("switch_tab", BrowserActionType.SwitchTab, "Switch to a tab shown in browser state.", """{"tab_id":"string"}""", terminates: true);
        yield return Definition("close_tab", BrowserActionType.CloseTab, "Close a tab shown in browser state.", """{"tab_id":"string"}""");
        yield return Definition("extract", BrowserActionType.ExtractText, "Extract visible text from the current page.", """{"query":"string","extract_links":"boolean"}""");
        yield return Definition("search_page", BrowserActionType.SearchPage, "Search current page text for a literal or regex pattern.", """{"pattern":"string","regex":"boolean","case_sensitive":"boolean","context_chars":"number","max_results":"number"}""");
        yield return Definition("find_elements", BrowserActionType.FindElements, "Find indexed visible elements by tag, role, or text.", """{"selector":"string","max_results":"number"}""");
        yield return Definition("scroll", BrowserActionType.Scroll, "Scroll the page or an element.", """{"down":"boolean","pages":"number","deltaY":"number"}""");
        yield return Definition("send_keys", BrowserActionType.PressKey, "Send a key or shortcut such as Enter, Escape, Control+A.", """{"keys":"string"}""");
        yield return Definition("find_text", BrowserActionType.FindText, "Find text in the current page.", """{"text":"string"}""");
        yield return Definition("screenshot", BrowserActionType.Screenshot, "Capture a screenshot artifact.", """{"file_name":"string"}""");
        yield return Definition("save_as_pdf", BrowserActionType.SaveAsPdf, "Save the current page as a PDF artifact.", """{"file_name":"string"}""");
        yield return Definition("dropdown_options", BrowserActionType.DropdownOptions, "Inspect a dropdown candidate.", """{"index":"number"}""");
        yield return Definition("select_dropdown", BrowserActionType.SelectDropdown, "Select an option in a native dropdown.", """{"index":"number","text":"string"}""");
        yield return Definition("evaluate", BrowserActionType.Evaluate, "Run security-gated browser JavaScript.", """{"code":"string"}""", terminates: true);
        yield return Definition("done", BrowserActionType.Finish, "Finish the task with final answer and success flag.", """{"text":"string","success":"boolean"}""", terminal: true);
    }

    private static BrowserActionDefinition Definition(
        string name,
        BrowserActionType type,
        string description,
        string schema,
        bool terminates = false,
        bool terminal = false) => new()
        {
            Name = name,
            Type = type,
            Description = description,
            ParametersJsonSchema = schema,
            TerminatesSequence = terminates,
            IsTerminal = terminal
        };
}
