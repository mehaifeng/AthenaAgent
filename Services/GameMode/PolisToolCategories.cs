using System;
using System.Collections.Generic;

namespace Athena.UI.Services.GameMode;

/// <summary>
/// 雅典娜的动作按工具类别归成十来种（设计稿 6.1）。每一种都意味着一套要做的动画，所以种类要克制。
/// 推理、审批、失败、交付不来自工具，由回放事件本身表达。
/// </summary>
public enum PolisActionCategory
{
    /// <summary>读文件、列目录、搜索：走进对应的建筑，展开卷轴。</summary>
    Read,
    /// <summary>写入、修改、删除、生成 Office 文档：在卷轴或石板上书写，建筑搭起脚手架。</summary>
    Write,
    /// <summary>执行终端命令：在锻炉前锻打。</summary>
    Terminal,
    /// <summary>网页搜索、抓取、浏览器任务：从港口放船出海。</summary>
    Web,
    /// <summary>读写记忆：去图书馆放入或取出卷轴。</summary>
    Memory,
    /// <summary>派发子代理：唤出黄金侍女。</summary>
    SubAgents,
    /// <summary>没登记的工具一律归工坊（与 <c>SubAgentZones</c> 未知工具归工坊同一约定）。</summary>
    Workshop
}

/// <summary>
/// 工具名 → 动作类别。这是游戏和猫头鹰村共用的唯一一张表（设计稿 6.1）：<c>SubAgentZones</c> 从它派生，
/// 新工具登记一次，两边就都有动作。覆盖 <c>FunctionRegistry</c> 注册的全部工具；Archive.Tests 逐个核对注册表，
/// 新工具忘了登记会被断言抓住。名字按不分大小写比较（与猫头鹰村原来的表一致）。
/// </summary>
public static class PolisToolCategories
{
    private static readonly Dictionary<string, PolisActionCategory> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        // 读：走进建筑，展开卷轴
        ["read_system_file"] = PolisActionCategory.Read,
        ["list_system_directory"] = PolisActionCategory.Read,
        ["search_in_file"] = PolisActionCategory.Read,
        ["search_in_directory"] = PolisActionCategory.Read,
        ["get_file_info"] = PolisActionCategory.Read,
        ["get_document_outline"] = PolisActionCategory.Read,
        ["parse_office_document"] = PolisActionCategory.Read,
        ["inspect_document"] = PolisActionCategory.Read,
        ["inspect_spreadsheet"] = PolisActionCategory.Read,
        ["inspect_presentation"] = PolisActionCategory.Read,
        ["validate_document"] = PolisActionCategory.Read,
        ["validate_spreadsheet"] = PolisActionCategory.Read,
        ["validate_presentation"] = PolisActionCategory.Read,

        // 写：书写，搭脚手架
        ["write_system_file"] = PolisActionCategory.Write,
        ["modify_system_file"] = PolisActionCategory.Write,
        ["delete_system_file"] = PolisActionCategory.Write,
        ["create_directory"] = PolisActionCategory.Write,
        ["move_system_file"] = PolisActionCategory.Write,
        ["copy_system_file"] = PolisActionCategory.Write,
        ["create_document"] = PolisActionCategory.Write,
        ["edit_document"] = PolisActionCategory.Write,
        ["convert_document"] = PolisActionCategory.Write,
        ["create_spreadsheet"] = PolisActionCategory.Write,
        ["edit_spreadsheet"] = PolisActionCategory.Write,
        ["modify_spreadsheet_structure"] = PolisActionCategory.Write,
        ["convert_spreadsheet"] = PolisActionCategory.Write,
        ["create_presentation"] = PolisActionCategory.Write,
        ["edit_presentation"] = PolisActionCategory.Write,

        // 锻炉
        ["execute_terminal_command"] = PolisActionCategory.Terminal,

        // 港口
        ["web_search"] = PolisActionCategory.Web,
        ["fetch_url_to_file"] = PolisActionCategory.Web,
        ["run_browser_task"] = PolisActionCategory.Web,

        // 图书馆
        ["recall_from_memory"] = PolisActionCategory.Memory,
        ["create_new_memory"] = PolisActionCategory.Memory,

        // 黄金侍女
        ["dispatch_subagents"] = PolisActionCategory.SubAgents,

        // 工坊：配置、定时任务、图像、技能、MCP 管理与调用
        ["view_self_configuration"] = PolisActionCategory.Workshop,
        ["modify_self_configuration"] = PolisActionCategory.Workshop,
        ["create_task"] = PolisActionCategory.Workshop,
        ["update_task"] = PolisActionCategory.Workshop,
        ["cancel_task"] = PolisActionCategory.Workshop,
        ["list_tasks"] = PolisActionCategory.Workshop,
        ["run_task_now"] = PolisActionCategory.Workshop,
        ["generate_image"] = PolisActionCategory.Workshop,
        ["activate_skill"] = PolisActionCategory.Workshop,
        ["read_skill_resource"] = PolisActionCategory.Workshop,
        ["mcp_list_tools"] = PolisActionCategory.Workshop,
        ["mcp_get_tool_schema"] = PolisActionCategory.Workshop,
        ["mcp_call_tool"] = PolisActionCategory.Workshop,
        ["mcp_add_server"] = PolisActionCategory.Workshop,
        ["mcp_remove_server"] = PolisActionCategory.Workshop,
        ["mcp_import_json"] = PolisActionCategory.Workshop,
    };

    /// <summary>
    /// 动作发生的地点从哪个参数取，按优先级：产出落在哪里（outputPath / destinationPath）优先于从哪里读。
    /// 终端命令的 <c>command</c> 本身从不读取——它可能带任何东西，回放里只留类别。
    /// </summary>
    public static IReadOnlyList<string> PathArgumentPriority { get; } = new[]
    {
        "outputPath", "destinationPath", "path", "filePath", "inputPath", "sourcePath", "workingDirectory"
    };

    public static IReadOnlyCollection<string> RegisteredTools => Map.Keys;

    public static bool IsRegistered(string toolName) => toolName != null && Map.ContainsKey(toolName);

    public static PolisActionCategory ForTool(string toolName)
        => toolName != null && Map.TryGetValue(toolName, out var category) ? category : PolisActionCategory.Workshop;

    /// <summary>JSON / 网页里用的小写名字。</summary>
    public static string Slug(PolisActionCategory category) => category switch
    {
        PolisActionCategory.Read => "read",
        PolisActionCategory.Write => "write",
        PolisActionCategory.Terminal => "terminal",
        PolisActionCategory.Web => "web",
        PolisActionCategory.Memory => "memory",
        PolisActionCategory.SubAgents => "subagents",
        _ => "workshop"
    };
}
