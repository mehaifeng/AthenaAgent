using System.Text.Json;
using System.Text.Json.Nodes;
using Athena.UI.Services.Interfaces;

namespace Athena.UI.Models;

/// <summary>
/// 把工具调用（函数名 + 参数 + 结果 JSON）整理成 UI 卡片可读的展示文本。
/// 新增工具时，只需在 <see cref="Summarize"/> / <see cref="Glyph"/> 中补充对应分支即可。
/// </summary>
public static class ToolCallDisplay
{
    private const int SummaryMaxLength = 80;
    private const int ResultPreviewMaxLength = 1200;

    /// <summary>
    /// 工具类别对应的应用图标契约 key（Styles/AppIcons.axaml 中的 AthenaIcon* 名称）。
    /// 经由 ToolIconKeyToGeometryConverter 解析为 PathIcon 的 Geometry。
    /// 新增工具时务必在此登记：未登记的工具全部落到同一个通用图标上，卡片就失去了辨识度。
    /// </summary>
    public static string IconKey(string functionName) => functionName switch
    {
        // 检索与记忆
        "web_search" => "AthenaIconWebSearch",
        "recall_from_memory" => "AthenaIconMemoryRecall",
        "create_new_memory" => "AthenaIconSave",

        // 文件系统
        "read_system_file" => "AthenaIconOpenFile",
        "get_file_info" => "AthenaIconInfo",
        "list_system_directory" => "AthenaIconDirectory",
        "search_in_file" => "AthenaIconSearch",
        "write_system_file" => "AthenaIconEdit",
        "modify_system_file" => "AthenaIconEdit",
        "create_directory" => "AthenaIconFolderReveal",
        "move_system_file" => "AthenaIconExport",
        "copy_system_file" => "AthenaIconCopy",
        "delete_system_file" => "AthenaIconDelete",

        // 文档与表格
        "get_document_outline" => "AthenaIconDocumentParsing",
        "parse_office_document" => "AthenaIconDocumentParsing",
        "inspect_document" or "create_document" or "edit_document"
            or "convert_document" or "validate_document" => "AthenaIconFileDocument",
        "inspect_spreadsheet" or "create_spreadsheet" or "edit_spreadsheet"
            or "modify_spreadsheet_structure" or "convert_spreadsheet"
            or "validate_spreadsheet" => "AthenaIconFileSpreadsheet",

        // 外部能力
        "execute_terminal_command" => "AthenaIconTerminal",
        "generate_image" => "AthenaIconImageGeneration",
        "run_browser_task" => "AthenaIconBrowserTask",
        "dispatch_subagents" => "AthenaIconSubAgents",
        "activate_skill" or "read_skill_resource" => "AthenaIconSkills",
        "mcp_add_server" or "mcp_remove_server" or "mcp_list_tools"
            or "mcp_get_tool_schema" or "mcp_call_tool" or "mcp_import_json" => "AthenaIconConnectors",

        // 计划任务与自我配置
        "create_task" => "AthenaIconTaskCreate",
        "update_task" => "AthenaIconTaskCreate",
        "list_tasks" => "AthenaIconTaskList",
        "cancel_task" => "AthenaIconBlocked",
        "run_task_now" => "AthenaIconScheduledRun",
        "view_self_configuration" => "AthenaIconSettings",
        "modify_self_configuration" => "AthenaIconSettings",

        _ => "AthenaIconToolGeneric"
    };

    /// <summary>
    /// 生成人类可读的一行摘要。<paramref name="argumentsJson"/> 为模型给出的参数 JSON。
    /// </summary>
    public static string Summarize(string functionName, string? argumentsJson, ILocalizationService? localizationService = null)
    {
        JsonObject? args = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(argumentsJson))
            {
                args = JsonNode.Parse(argumentsJson) as JsonObject;
            }
        }
        catch
        {
            // 参数非法 JSON 时退化为仅显示函数名
        }

        string? key = functionName switch
        {
            "web_search" or "recall_from_memory" => Str(args, "query"),
            "read_system_file" or "get_file_info" or "write_system_file"
                or "modify_system_file" or "delete_system_file" or "list_system_directory"
                or "create_directory" or "get_document_outline" or "search_in_file" => Str(args, "path"),
            "move_system_file" or "copy_system_file" => Str(args, "path"),
            "execute_terminal_command" => Str(args, "command"),
            "generate_image" => Str(args, "prompt"),
            "run_browser_task" => Str(args, "instruction") ?? Str(args, "intent"),
            "create_task" or "update_task" => Str(args, "name") ?? Str(args, "instruction"),
            "run_task_now" or "cancel_task" => Str(args, "taskId"),
            "create_new_memory" => Str(args, "content") ?? Str(args, "intent"),
            _ => FirstStringValue(args)
        };

        var label = FriendlyName(functionName, localizationService);
        return string.IsNullOrWhiteSpace(key)
            ? label
            : $"{label} · {Truncate(CollapseWhitespace(key), SummaryMaxLength)}";
    }

    /// <summary>
    /// 把参数 JSON 整形（缩进）以便在「详情」中阅读。
    /// </summary>
    public static string PrettyArguments(string? argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
        {
            return string.Empty;
        }

        try
        {
            var node = JsonNode.Parse(argumentsJson);
            return node?.ToJsonString(new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            }) ?? argumentsJson.Trim();
        }
        catch
        {
            return argumentsJson.Trim();
        }
    }

    /// <summary>
    /// 解析工具结果 JSON（形如 {success, message, data}），返回是否成功与可读预览文本。
    /// </summary>
    public static (bool success, string preview) ParseResult(string? resultJson)
    {
        if (string.IsNullOrWhiteSpace(resultJson))
        {
            return (true, string.Empty);
        }

        try
        {
            var node = JsonNode.Parse(resultJson);
            if (node is JsonObject obj)
            {
                var success = obj["success"]?.GetValue<bool>() ?? true;
                var message = obj["message"]?.ToString() ?? string.Empty;
                var data = obj["data"];

                var preview = message;
                if (data is not null)
                {
                    var dataText = data is JsonValue jv
                        ? jv.ToString()
                        : data.ToJsonString(new JsonSerializerOptions
                        {
                            WriteIndented = true,
                            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                        });

                    if (!string.IsNullOrWhiteSpace(dataText) && dataText != "null")
                    {
                        preview = string.IsNullOrWhiteSpace(preview)
                            ? dataText
                            : $"{preview}\n{dataText}";
                    }
                }

                return (success, Truncate(preview.Trim(), ResultPreviewMaxLength));
            }
        }
        catch
        {
            // 非标准 JSON：原样截断
        }

        return (true, Truncate(resultJson.Trim(), ResultPreviewMaxLength));
    }

    private static string FriendlyName(string functionName, ILocalizationService? localizationService)
    {
        var (key, fallback) = functionName switch
        {
            "web_search" => ("Tool.Name.WebSearch", "Web search"),
            "recall_from_memory" => ("Tool.Name.RecallMemory", "Recall memory"),
            "create_new_memory" => ("Tool.Name.CreateMemory", "Save memory"),
            "read_system_file" => ("Tool.Name.ReadFile", "Read file"),
            "get_file_info" => ("Tool.Name.FileInfo", "Get file info"),
            "list_system_directory" => ("Tool.Name.ListDirectory", "List directory"),
            "get_document_outline" => ("Tool.Name.DocumentOutline", "Parse document"),
            "search_in_file" => ("Tool.Name.SearchFile", "Search file contents"),
            "write_system_file" => ("Tool.Name.WriteFile", "Write file"),
            "modify_system_file" => ("Tool.Name.ModifyFile", "Modify file"),
            "create_directory" => ("Tool.Name.CreateDirectory", "Create directory"),
            "move_system_file" => ("Tool.Name.MoveFile", "Move file"),
            "copy_system_file" => ("Tool.Name.CopyFile", "Copy file"),
            "delete_system_file" => ("Tool.Name.DeleteFile", "Delete file"),
            "execute_terminal_command" => ("Tool.Name.ExecuteCommand", "Run command"),
            "generate_image" => ("Tool.Name.GenerateImage", "Generate image"),
            "run_browser_task" => ("Tool.Name.BrowserTask", "Browser task"),
            "create_task" => ("Tool.Name.CreateTask", "Create task"),
            "update_task" => ("Tool.Name.UpdateTask", "Update task"),
            "list_tasks" => ("Tool.Name.ListTasks", "List tasks"),
            "cancel_task" => ("Tool.Name.CancelTask", "Cancel task"),
            "run_task_now" => ("Tool.Name.RunTaskNow", "Run task now"),
            "view_self_configuration" => ("Tool.Name.ViewConfiguration", "View configuration"),
            "modify_self_configuration" => ("Tool.Name.ModifyConfiguration", "Modify configuration"),
            _ => (string.Empty, functionName)
        };

        return string.IsNullOrEmpty(key)
            ? fallback
            : localizationService?.GetString(key, fallback) ?? fallback;
    }

    private static string? Str(JsonObject? args, string key)
    {
        if (args is null)
        {
            return null;
        }

        var value = args[key];
        var s = value?.ToString();
        return string.IsNullOrWhiteSpace(s) ? null : s;
    }

    private static string? FirstStringValue(JsonObject? args)
    {
        if (args is null)
        {
            return null;
        }

        foreach (var pair in args)
        {
            if (pair.Value is JsonValue jv)
            {
                var s = jv.ToString();
                if (!string.IsNullOrWhiteSpace(s))
                {
                    return s;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// 把任意空白（换行、制表、连续空格）压成单个空格，确保摘要恒为单行，
    /// 避免收起状态下的工具卡片标题被多行文本（如多行记忆正文/命令）撑高。
    /// </summary>
    private static string CollapseWhitespace(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        return System.Text.RegularExpressions.Regex.Replace(value, @"\s+", " ").Trim();
    }

    private static string Truncate(string value, int max)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= max)
        {
            return value;
        }

        return value[..max].TrimEnd() + "…";
    }
}
