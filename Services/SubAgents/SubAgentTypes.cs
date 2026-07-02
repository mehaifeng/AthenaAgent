using System;
using System.Collections.Generic;

namespace Athena.UI.Services.SubAgents;

/// <summary>agent_type 预设：工具白名单 + 系统提示。决定子代理能做什么、怎么做。</summary>
public sealed class SubAgentTypePreset
{
    public string Name { get; init; } = "general";
    public string SystemPrompt { get; init; } = string.Empty;
    public IReadOnlyList<string> AllowedTools { get; init; } = Array.Empty<string>();
}

public static class SubAgentTypes
{
    // 任何预设都不得包含 dispatch_subagents —— 结构性禁止子代理再派子代理（仅允许主→子一层）。
    private const string CommonTail =
        "\n\nYou are an autonomous sub-agent working on ONE delegated task with your own isolated context. " +
        "You cannot see the main conversation. Use your tools to complete the task, then reply with a concise, " +
        "self-contained summary of what you found or did — this summary is the ONLY thing returned to the orchestrator. " +
        "Do not ask questions; make reasonable assumptions and proceed.";

    private static readonly string[] FileTools =
    {
        "get_file_info", "search_in_file", "get_document_outline", "read_system_file", "write_system_file",
        "modify_system_file", "delete_system_file", "list_system_directory", "create_directory",
        "move_system_file", "copy_system_file", "recall_from_memory"
    };

    private static readonly string[] ResearchTools =
    {
        "web_search", "run_browser_task", "recall_from_memory", "get_file_info", "search_in_file",
        "get_document_outline", "read_system_file", "list_system_directory"
    };

    private static readonly string[] GeneralTools =
    {
        "web_search", "run_browser_task", "recall_from_memory", "create_new_memory",
        "get_file_info", "search_in_file", "get_document_outline", "read_system_file", "write_system_file",
        "modify_system_file", "list_system_directory", "create_directory", "move_system_file", "copy_system_file",
        "execute_terminal_command", "generate_image"
    };

    private static readonly Dictionary<string, SubAgentTypePreset> _presets = new(StringComparer.OrdinalIgnoreCase)
    {
        ["researcher"] = new SubAgentTypePreset
        {
            Name = "researcher",
            AllowedTools = ResearchTools,
            SystemPrompt = "You are a research sub-agent. Gather, verify, and distill information relevant to the task." + CommonTail
        },
        ["file_worker"] = new SubAgentTypePreset
        {
            Name = "file_worker",
            AllowedTools = FileTools,
            SystemPrompt = "You are a file-operations sub-agent. Read, search, and edit local files precisely and safely." + CommonTail
        },
        ["general"] = new SubAgentTypePreset
        {
            Name = "general",
            AllowedTools = GeneralTools,
            SystemPrompt = "You are a general-purpose sub-agent." + CommonTail
        },
    };

    public static SubAgentTypePreset Resolve(string? agentType)
        => agentType != null && _presets.TryGetValue(agentType, out var preset) ? preset : _presets["general"];
}
