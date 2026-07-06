using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Athena.UI.Models;

namespace Athena.UI.Services;

/// <summary>
/// 工具风险分级器（静态，单一真源）。新增工具时在此登记其风险档位。
/// 未登记的工具一律视为 <see cref="ToolRisk.Sensitive"/>（fail-safe：新工具默认需审批）。
/// </summary>
public static class ToolRiskClassifier
{
    // 只读 / 无副作用：均衡模式下自动放行。
    private static readonly HashSet<string> ReadOnlyTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "read_system_file", "get_file_info", "search_in_file", "list_system_directory",
        "get_document_outline", "recall_from_memory", "view_self_configuration",
        "web_search", "list_tasks"
    };

    // 破坏性且不可逆：始终高危对待（终端命令单独走命令级评估）。
    private static readonly HashSet<string> DestructiveTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "delete_system_file"
    };

    // 写本地状态 / 外部副作用 / 花钱：默认每次询问。
    private static readonly HashSet<string> SensitiveTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "write_system_file", "modify_system_file", "move_system_file", "copy_system_file",
        "create_directory", "create_new_memory", "modify_self_configuration",
        "run_browser_task", "generate_image", "create_task", "cancel_task",
        "parse_office_document", "dispatch_subagents"
    };

    /// <summary>
    /// 判定某次工具调用的风险档位。终端命令会解析 command/arguments 做命令级评估。
    /// </summary>
    /// <returns>风险档位与（可选）高危原因说明。</returns>
    public static (ToolRisk Risk, string? Reason) Classify(string functionName, string? argumentsJson)
    {
        if (string.IsNullOrEmpty(functionName))
        {
            return (ToolRisk.Sensitive, null);
        }

        if (string.Equals(functionName, "execute_terminal_command", StringComparison.OrdinalIgnoreCase))
        {
            return TerminalCommandRisk.Evaluate(argumentsJson);
        }

        if (ReadOnlyTools.Contains(functionName)) return (ToolRisk.ReadOnly, null);
        if (DestructiveTools.Contains(functionName)) return (ToolRisk.Destructive, "该操作不可逆");
        if (SensitiveTools.Contains(functionName)) return (ToolRisk.Sensitive, null);

        // 未登记工具：fail-safe 归为敏感，默认需审批。
        return (ToolRisk.Sensitive, "未分级的工具，出于安全默认需要审批");
    }
}

/// <summary>
/// 终端命令的命令级风险评估库。把 execute_terminal_command 的 command + arguments 拼成
/// 完整命令行后，按「明确高危 / 常见只读 / 其余敏感」三类打分。
/// 这是评审第一条「终端零校验」的补丁：审批弹窗本身就是终端的执行前闸门。
/// </summary>
public static class TerminalCommandRisk
{
    // 明确高危的可执行名（作为顶层命令时）。
    private static readonly HashSet<string> DestructiveCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "rm", "rmdir", "del", "erase", "rd",
        "mkfs", "dd", "fdisk", "diskpart", "format",
        "sudo", "su", "doas",
        "shutdown", "reboot", "halt", "poweroff",
        "kill", "killall", "pkill", "taskkill",
        "chown", "chmod", "chgrp",
        "reg", "regedit",             // Windows 注册表
        "netsh", "iptables"           // 网络/防火墙改写
    };

    // 常见只读 / 无害命令（作为顶层命令时可降级）。
    private static readonly HashSet<string> ReadOnlyCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "ls", "dir", "pwd", "cat", "type", "echo", "whoami", "hostname", "date",
        "which", "where", "whereis", "head", "tail", "wc", "find", "grep", "findstr",
        "env", "printenv", "uname", "ver", "df", "du", "ps", "top"
    };

    // 明确高危的命令片段模式（对完整命令行做正则匹配，捕获管道执行、fork 炸弹、覆盖重定向等）。
    private static readonly (Regex Pattern, string Reason)[] DangerPatterns =
    {
        (new Regex(@"\|\s*(sudo\s+)?(sh|bash|zsh|python[0-9.]*|node|powershell|pwsh|cmd)\b", RegexOptions.IgnoreCase), "把下载内容通过管道直接执行"),
        (new Regex(@"\b(curl|wget|iwr|invoke-webrequest)\b.*\|\s*\w+", RegexOptions.IgnoreCase), "从网络下载并直接执行"),
        (new Regex(@"rm\s+-[a-z]*[rf]", RegexOptions.IgnoreCase), "递归/强制删除"),
        (new Regex(@":\(\)\s*\{.*\};\s*:", RegexOptions.None), "疑似 fork 炸弹"),
        (new Regex(@">\s*/(dev|etc|sys|proc)\b", RegexOptions.IgnoreCase), "向系统关键路径写入/覆盖"),
        (new Regex(@"\bchmod\s+-?R?\s*777\b", RegexOptions.IgnoreCase), "放开全部权限 (chmod 777)"),
        (new Regex(@"\bmkfs\b|\bdd\s+if=", RegexOptions.IgnoreCase), "磁盘级写入，可能抹除数据"),
    };

    public static (ToolRisk Risk, string? Reason) Evaluate(string? argumentsJson)
    {
        var (command, commandLine) = Parse(argumentsJson);

        // 1) 完整命令行的高危片段优先（能抓住 `bash -c "rm -rf ~"`、`curl x | sh` 等绕过顶层命令名的写法）。
        foreach (var (pattern, reason) in DangerPatterns)
        {
            if (pattern.IsMatch(commandLine))
            {
                return (ToolRisk.Destructive, $"命令包含高危模式：{reason}");
            }
        }

        // 2) 顶层命令名判定。
        if (!string.IsNullOrEmpty(command))
        {
            var bare = StripPath(command);
            if (DestructiveCommands.Contains(bare))
            {
                return (ToolRisk.Destructive, $"命令 '{bare}' 属高危操作");
            }
            if (ReadOnlyCommands.Contains(bare) && !ContainsWriteRedirect(commandLine))
            {
                return (ToolRisk.ReadOnly, null);
            }
            // 显式版本/帮助探测视为只读。
            if (IsVersionOrHelpProbe(commandLine))
            {
                return (ToolRisk.ReadOnly, null);
            }
        }

        // 3) 其余终端命令：默认敏感，需审批。
        return (ToolRisk.Sensitive, null);
    }

    /// <summary>把 command + arguments 拼成用于展示 / 白名单匹配的完整命令行。</summary>
    public static string BuildCommandLine(string? argumentsJson)
    {
        var (_, commandLine) = Parse(argumentsJson);
        return commandLine;
    }

    /// <summary>取顶层命令名（去路径、去引号），用于白名单聚合键。</summary>
    public static string ExtractCommandName(string? argumentsJson)
    {
        var (command, _) = Parse(argumentsJson);
        return StripPath(command);
    }

    private static (string Command, string CommandLine) Parse(string? argumentsJson)
    {
        string command = string.Empty;
        var parts = new List<string>();
        try
        {
            if (!string.IsNullOrWhiteSpace(argumentsJson)
                && JsonNode.Parse(argumentsJson) is JsonObject obj)
            {
                command = obj["command"]?.ToString()?.Trim() ?? string.Empty;
                if (!string.IsNullOrEmpty(command))
                {
                    parts.Add(command);
                }

                if (obj["arguments"] is JsonArray arr)
                {
                    foreach (var item in arr)
                    {
                        var s = item?.ToString();
                        if (!string.IsNullOrEmpty(s))
                        {
                            parts.Add(s);
                        }
                    }
                }
            }
        }
        catch
        {
            // 参数非法 JSON：按空处理，最终落到「敏感」默认档。
        }

        return (command, string.Join(' ', parts).Trim());
    }

    private static string StripPath(string command)
    {
        if (string.IsNullOrEmpty(command)) return string.Empty;
        var trimmed = command.Trim().Trim('"', '\'');
        var slash = trimmed.LastIndexOfAny(new[] { '/', '\\' });
        if (slash >= 0 && slash < trimmed.Length - 1)
        {
            trimmed = trimmed[(slash + 1)..];
        }
        // 去掉 Windows 可执行后缀便于匹配。
        foreach (var ext in new[] { ".exe", ".cmd", ".bat", ".com" })
        {
            if (trimmed.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
            {
                trimmed = trimmed[..^ext.Length];
                break;
            }
        }
        return trimmed;
    }

    private static bool ContainsWriteRedirect(string commandLine) =>
        commandLine.Contains('>') || Regex.IsMatch(commandLine, @"\btee\b", RegexOptions.IgnoreCase);

    private static bool IsVersionOrHelpProbe(string commandLine) =>
        Regex.IsMatch(commandLine, @"(^|\s)(-v|--version|-V|--help|-h|version)(\s|$)", RegexOptions.IgnoreCase);
}
