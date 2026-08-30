using Athena.UI.Models;
using System;
using System.Collections.Generic;

namespace Athena.UI.Services.SubAgents;

/// <summary>工具名 → 猫头鹰所在"场所"的映射。未知工具归入工坊。</summary>
public static class SubAgentZones
{
    private static readonly Dictionary<string, SubAgentZone> _map = new(StringComparer.OrdinalIgnoreCase)
    {
        // 文件区
        ["get_file_info"] = SubAgentZone.Files,
        ["search_in_file"] = SubAgentZone.Files,
        ["get_document_outline"] = SubAgentZone.Files,
        ["read_system_file"] = SubAgentZone.Files,
        ["write_system_file"] = SubAgentZone.Files,
        ["modify_system_file"] = SubAgentZone.Files,
        ["delete_system_file"] = SubAgentZone.Files,
        ["list_system_directory"] = SubAgentZone.Files,
        ["create_directory"] = SubAgentZone.Files,
        ["move_system_file"] = SubAgentZone.Files,
        ["copy_system_file"] = SubAgentZone.Files,

        // 电脑区
        ["web_search"] = SubAgentZone.Web,
        ["run_browser_task"] = SubAgentZone.Web,

        // 书房区（知识库记忆）
        ["recall_from_memory"] = SubAgentZone.Library,
        ["create_new_memory"] = SubAgentZone.Library,

        // 工坊
        ["execute_terminal_command"] = SubAgentZone.Workshop,
        ["generate_image"] = SubAgentZone.Workshop,
        ["view_self_configuration"] = SubAgentZone.Workshop,
        ["modify_self_configuration"] = SubAgentZone.Workshop,
        ["create_task"] = SubAgentZone.Workshop,
        ["update_task"] = SubAgentZone.Workshop,
        ["cancel_task"] = SubAgentZone.Workshop,
        ["list_tasks"] = SubAgentZone.Workshop,
        ["run_task_now"] = SubAgentZone.Workshop,
    };

    public static SubAgentZone ForTool(string functionName)
        => functionName != null && _map.TryGetValue(functionName, out var zone) ? zone : SubAgentZone.Workshop;
}
