using System;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

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
    /// 工具类别对应的 Semi 图标资源 key（StaticResource 名称）。
    /// 经由 ToolIconKeyToGeometryConverter 解析为 PathIcon 的 Geometry。
    /// </summary>
    public static string IconKey(string functionName) => functionName switch
    {
        "web_search" => "SemiIconSearch",
        "recall_from_memory" => "SemiIconHistory",
        "create_new_memory" => "SemiIconSave",
        "read_system_file" => "SemiIconFile",
        "get_file_info" => "SemiIconInfoCircle",
        "list_system_directory" => "SemiIconFolder",
        "get_document_outline" => "SemiIconArticle",
        "search_in_file" => "SemiIconSearch",
        "write_system_file" => "SemiIconEdit",
        "modify_system_file" => "SemiIconEdit",
        "create_directory" => "SemiIconFolderOpen",
        "move_system_file" => "SemiIconExport",
        "copy_system_file" => "SemiIconCopy",
        "delete_system_file" => "SemiIconDelete",
        "execute_terminal_command" => "SemiIconTerminal",
        "generate_image" => "SemiIconImage",
        "run_browser_task" => "SemiIconGlobeStroke",
        "create_task" => "SemiIconCalendarClock",
        "list_tasks" => "SemiIconList",
        "cancel_task" => "SemiIconAlarmStroked",
        "view_self_configuration" => "SemiIconSetting",
        "modify_self_configuration" => "SemiIconSetting",
        _ => "SemiIconWrench"
    };

    /// <summary>
    /// 生成人类可读的一行摘要。<paramref name="argumentsJson"/> 为模型给出的参数 JSON。
    /// </summary>
    public static string Summarize(string functionName, string? argumentsJson)
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
            "create_task" => Str(args, "instruction"),
            "create_new_memory" => Str(args, "content") ?? Str(args, "intent"),
            _ => FirstStringValue(args)
        };

        var label = FriendlyName(functionName);
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

    private static string FriendlyName(string functionName) => functionName switch
    {
        "web_search" => "搜索",
        "recall_from_memory" => "回忆记忆",
        "create_new_memory" => "写入记忆",
        "read_system_file" => "读取文件",
        "get_file_info" => "查看文件信息",
        "list_system_directory" => "列出目录",
        "get_document_outline" => "解析文档",
        "search_in_file" => "搜索文件内容",
        "write_system_file" => "写入文件",
        "modify_system_file" => "修改文件",
        "create_directory" => "创建目录",
        "move_system_file" => "移动文件",
        "copy_system_file" => "复制文件",
        "delete_system_file" => "删除文件",
        "execute_terminal_command" => "执行命令",
        "generate_image" => "生成图片",
        "run_browser_task" => "浏览器任务",
        "create_task" => "创建任务",
        "list_tasks" => "查看任务",
        "cancel_task" => "取消任务",
        "view_self_configuration" => "查看配置",
        "modify_self_configuration" => "修改配置",
        _ => functionName
    };

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
