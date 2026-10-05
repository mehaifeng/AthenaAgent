using System;
using System.Collections.Generic;
using System.IO;
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
        "read_system_file", "get_file_info", "search_in_file", "search_in_directory", "list_system_directory",
        "get_document_outline", "recall_from_memory", "view_self_configuration",
        "web_search", "list_tasks",
        // 表格/文档/演示只读：解析本地 OOXML 包，不落盘、不外发。
        "inspect_spreadsheet", "validate_spreadsheet", "inspect_document", "validate_document",
        "inspect_presentation", "validate_presentation",
        // MCP 元工具：仅返回快照/schema，无副作用。真正的调用 mcp_call_tool 仍走 fail-safe Sensitive。
        "mcp_list_tools", "mcp_get_tool_schema", "activate_skill", "read_skill_resource",
        // Office 工具集解锁：只是把工具声明与格式规范放进本次对话，本身不碰任何文件。
        "enable_office_tools"
    };

    // 破坏性且不可逆：始终高危对待（终端命令单独走命令级评估）。
    private static readonly HashSet<string> DestructiveTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "delete_system_file"
    };

    // 只增不改：写入路径必须尚不存在（OoxmlPackageService.EnsureCanWrite 强制），
    // 且 edit/convert 类始终写到与源不同的新路径、源文件原样保留。
    // 这类调用毁不掉任何既有内容，而用户往往刚说完「帮我做份 PPT」——
    // 一次 create → validate → inspect → edit → validate 却要弹 3~5 次框，
    // 打断成本远高于它提供的边际安全。均衡模式放行，严格模式仍逐次询问。
    // 注意：overwrite=true 会让它们变成可替换既有文件，届时降级失效（见 IsAdditiveWrite）。
    private static readonly HashSet<string> AdditiveWriteTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "create_spreadsheet", "edit_spreadsheet", "modify_spreadsheet_structure", "convert_spreadsheet",
        "create_document", "edit_document", "convert_document",
        "create_presentation", "edit_presentation",
        "create_directory"
    };

    // 只能由人批准的工具：无人值守路径（子代理）永不继承，自动审批模型也不替用户裁决。
    // 它们改的是应用自身的能力边界——尤其 modify_self_configuration 能写 Security.ToolApprovalMode，
    // 让一个模型批准它，等于让模型自己关掉审批闸门，是一条实打实的提权路径。
    // cron 四个工具同理：它们写的是「以后会自己跑什么」，一次放行换来的是此后每个触发点上的一整个自主会话。
    // 开发版日志里的 72 次审批弹窗（2026-08）中，用户拒绝的 8 次里有 3 次 create_task、2 次 modify_self_configuration。
    private static readonly HashSet<string> NeverUnattendedTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "modify_self_configuration",
        "mcp_add_server", "mcp_remove_server", "mcp_import_json",
        "create_task", "update_task", "cancel_task", "run_task_now"
    };

    /// <summary>
    /// 该工具是否只能由人批准：无人值守路径一律拒绝（无论 SubAgentsInheritApproval 如何设置），
    /// Automatic 模式下也不交给自动审批模型，而是照常弹窗。
    /// </summary>
    public static bool IsNeverUnattended(string functionName) =>
        !string.IsNullOrEmpty(functionName) && NeverUnattendedTools.Contains(functionName);

    // 写本地状态 / 外部副作用 / 花钱：默认每次询问。
    private static readonly HashSet<string> SensitiveTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "write_system_file", "modify_system_file", "move_system_file", "copy_system_file",
        "create_new_memory", "modify_self_configuration",
        // cron 任务写的是"以后会自动跑什么"，包括 run_task_now 这个立刻开出一个自主会话的按钮。
        "run_browser_task", "generate_image", "create_task", "update_task", "cancel_task", "run_task_now",
        "parse_office_document", "dispatch_subagents",
        // 单次 GET 落盘：能力上界被工具本身锁死（公网地址、非可执行、限额），但仍是写盘 + 出网。
        // 保持敏感档——外网内容进入工作区是真实的信任边界；但审批键按 host 聚合，
        // 放行一次 example.com 即覆盖同批次的其余下载，不会一张图一次弹窗。
        "fetch_url_to_file",
        // MCP：调用外部工具、增删外部服务器均需人工确认（新增=授权拉起外部子进程）。
        "mcp_call_tool", "mcp_add_server", "mcp_remove_server", "mcp_import_json"
    };

    /// <summary>
    /// 判定某次工具调用的风险档位。终端命令会解析 command/arguments 做命令级评估。
    /// </summary>
    /// <param name="sensitiveLocations">
    /// 只读终端命令不经审批就不能碰的位置，由调用方从当前配置构造。审批闸门与并发编排必须传同一份（见 <see cref="SensitiveLocations"/>）。
    /// </param>
    /// <returns>风险档位与（可选）高危原因说明。</returns>
    public static (ToolRisk Risk, string? Reason) Classify(string functionName, string? argumentsJson, SensitiveLocations sensitiveLocations)
    {
        if (string.IsNullOrEmpty(functionName))
        {
            return (ToolRisk.Sensitive, null);
        }

        if (string.Equals(functionName, "execute_terminal_command", StringComparison.OrdinalIgnoreCase))
        {
            return TerminalCommandRisk.Evaluate(argumentsJson, sensitiveLocations);
        }

        if (string.Equals(functionName, "get_document_outline", StringComparison.OrdinalIgnoreCase)
            && OutlineRequiresRemoteUpload(argumentsJson))
        {
            return (ToolRisk.Sensitive, "旧版 Office 二进制格式需要上传到远端文档解析服务");
        }

        if (ReadOnlyTools.Contains(functionName)) return (ToolRisk.ReadOnly, null);
        if (DestructiveTools.Contains(functionName)) return (ToolRisk.Destructive, "该操作不可逆");

        // overwrite=true 时这些工具可以替换既有文件，「只增不改」的前提消失，回落到敏感档。
        if (AdditiveWriteTools.Contains(functionName))
        {
            return IsAdditiveWrite(argumentsJson)
                ? (ToolRisk.AdditiveWrite, null)
                : (ToolRisk.Sensitive, "overwrite=true 会替换已存在的文件");
        }

        if (SensitiveTools.Contains(functionName)) return (ToolRisk.Sensitive, null);

        // 未登记工具：fail-safe 归为敏感，默认需审批。
        return (ToolRisk.Sensitive, "未分级的工具，出于安全默认需要审批");
    }

    /// <summary>
    /// 参数是否保持了「只增不改」：没有显式 overwrite=true。
    /// 参数不可解析时保守判否——宁可多问一次，也不要在看不懂参数时降档。
    /// </summary>
    private static bool IsAdditiveWrite(string? argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson)) return true;
        try
        {
            if (JsonNode.Parse(argumentsJson) is not JsonObject obj) return false;
            foreach (var pair in obj)
            {
                if (!string.Equals(pair.Key.Replace("_", string.Empty), "overwrite", StringComparison.OrdinalIgnoreCase))
                    continue;
                // 模型可能给 true / "true" 两种形态，两者都要当成覆盖。
                var raw = pair.Value?.ToString();
                return !string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase);
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool OutlineRequiresRemoteUpload(string? argumentsJson)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(argumentsJson) || JsonNode.Parse(argumentsJson) is not JsonObject obj)
                return false;
            string? path = null;
            foreach (var pair in obj)
            {
                if (!string.Equals(pair.Key.Replace("_", string.Empty), "path", StringComparison.OrdinalIgnoreCase))
                    continue;
                path = pair.Value?.ToString();
                break;
            }
            var extension = System.IO.Path.GetExtension(path);
            return string.Equals(extension, ".doc", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(extension, ".ppt", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(extension, ".xls", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>
/// 终端命令的命令级风险评估库。把 execute_terminal_command 的 command + arguments 拼成
/// 完整命令行后，按「明确高危 / 常见只读 / 其余敏感」三类打分。
/// 这是评审第一条「终端零校验」的补丁：审批弹窗本身就是终端的执行前闸门。
///
/// 只读是终端唯一不弹窗的档位——均衡与自动模式都直接放行，连自动审批模型都看不到它，
/// 所以只读给得很窄：版本 / 帮助探测要求探测参数是唯一的参数；env / printenv / pbpaste 从不只读；
/// find 带动作就不只读；只读命令的参数或工作目录落进受保护位置（<see cref="SensitiveLocations"/>）也不只读。
/// </summary>
public static class TerminalCommandRisk
{
    // 明确高危的可执行名（作为顶层命令，或作为 env / find -exec 要执行的命令时）。
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

    // 常见只读 / 无害命令（作为顶层命令时可降级）。参数或工作目录落进受保护位置时不降级（见 FindSensitiveLocation）。
    // env / printenv 曾在此列，见 SecretOutputCommands。
    private static readonly HashSet<string> ReadOnlyCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "ls", "dir", "pwd", "cat", "type", "echo", "whoami", "hostname", "date",
        "which", "where", "whereis", "head", "tail", "wc", "find", "grep", "findstr",
        "uname", "ver", "df", "du", "ps", "top"
    };

    // 照常运行就把机密打出来的命令 → 原因。env / printenv 曾被列为只读，而 API key 常常就放在环境变量里；
    // 它们与 pbpaste 都不能走探测豁免：`env -v` 的 -v 是 verbose，macOS 上照样打出全部环境变量（实测与 env 同为 76 行），
    // pbpaste 忽略不认识的参数（`pbpaste -v` 退出码 0、无报错），「唯一参数是 -v」照样交出剪贴板。
    // env 还会执行它后面的命令（`env rm notes.txt` 曾因此被判只读），见 EvaluateEnvironmentCommand。
    private static readonly Dictionary<string, string> SecretOutputCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        ["env"] = "会输出全部环境变量（API key 等凭据常在其中）",
        ["printenv"] = "会输出环境变量（API key 等凭据常在其中）",
        ["pbpaste"] = "会输出剪贴板内容（常有刚复制的密码）"
    };

    // 版本 / 帮助探测参数，只在它是命令唯一的参数时才算只读（见 IsVersionOrHelpProbe）。区分大小写：
    // 此前不分大小写，curl 的请求头参数 -H 被当成 -h，开发版日志里 4 次带 -H 的 curl 请求因此免审批。
    // 不收裸词 version：它是子命令而不是约定俗成的探测参数，`changeset version`、`lerna version` 会改写文件
    // （开发版日志 270 次终端调用里没有一次用到它）。
    private static readonly HashSet<string> ProbeArguments = new(StringComparer.Ordinal)
    {
        "-v", "-V", "--version", "-h", "--help"
    };

    // find 的动作。-delete 直接删；-exec 一族对每个匹配执行一条命令；-fprint 一族把结果写进文件。
    // find 的谓词区分大小写（-DELETE 是错误而不是删除），所以按 Ordinal 匹配。
    private static readonly HashSet<string> FindExecActions = new(StringComparer.Ordinal)
    {
        "-exec", "-execdir", "-ok", "-okdir"
    };
    private static readonly HashSet<string> FindWriteActions = new(StringComparer.Ordinal)
    {
        "-fprint", "-fprint0", "-fprintf", "-fls"
    };

    // find -exec 与 env 会执行嵌在参数里的命令，判定逐层往里走。只有刻意构造的输入才会嵌这么深；
    // 超出深度就看不清最终执行的是什么，按高危处理——落回敏感档的话，子代理继承审批时会放行。
    private const int MaxNestingDepth = 4;

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

    /// <summary>把命令藏进参数的解释器。它们的 -c/-Command 载荷需要再判一次。</summary>
    private static readonly HashSet<string> ShellExecutables = new(StringComparer.OrdinalIgnoreCase)
    {
        "sh", "bash", "zsh", "dash", "ksh", "fish", "csh", "tcsh",
        "powershell", "pwsh", "cmd"
    };

    /// <summary>取 shell 载荷里的首个命令名。分隔符两侧都看不到命令时返回空串。</summary>
    private static string ExtractShellPayloadCommand(string commandLine)
    {
        // 跳过解释器自身与它的开关（-c / -Command / /c / -NoProfile …），
        // 第一个不以 - 或 / 开头的词就是载荷的起点。
        var tokens = commandLine.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        for (int i = 1; i < tokens.Length; i++)
        {
            var token = tokens[i].Trim('"', '\'');
            if (token.Length == 0) continue;
            if (token[0] == '-' || token[0] == '/') continue;
            return token;
        }
        return string.Empty;
    }

    public static (ToolRisk Risk, string? Reason) Evaluate(string? argumentsJson, SensitiveLocations sensitiveLocations)
    {
        ArgumentNullException.ThrowIfNull(sensitiveLocations);
        var invocation = Parse(argumentsJson);
        return EvaluateInvocation(invocation.Command, invocation.Arguments, invocation.WorkingDirectory, sensitiveLocations, depth: 0);
    }

    /// <summary>
    /// 判一次进程调用。顶层是工具参数里的 command + arguments；find -exec 与 env 会把嵌在参数里的命令再交给这里判一次。
    /// </summary>
    private static (ToolRisk Risk, string? Reason) EvaluateInvocation(
        string command, IReadOnlyList<string> arguments, string? workingDirectory, SensitiveLocations sensitiveLocations, int depth)
    {
        if (depth > MaxNestingDepth)
        {
            return (ToolRisk.Destructive, "命令嵌套过深，无法确认最终会执行什么");
        }

        var commandLine = JoinCommandLine(command, arguments);

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

            // shell -c "<payload>" 会把真实命令藏在参数里：顶层命令名是 zsh/bash/cmd，
            // 而 DangerPatterns 只抓带标志的写法，于是 `zsh -c "rm notes.txt"` 会从
            // 破坏性降到敏感档。这里把 payload 的首个命令再判一次，堵掉这条降档路径。
            if (ShellExecutables.Contains(bare))
            {
                var payloadCommand = StripPath(ExtractShellPayloadCommand(commandLine));
                if (!string.IsNullOrEmpty(payloadCommand) && DestructiveCommands.Contains(payloadCommand))
                {
                    return (ToolRisk.Destructive, $"shell 参数里的命令 '{payloadCommand}' 属高危操作");
                }
            }

            if (DestructiveCommands.Contains(bare))
            {
                return (ToolRisk.Destructive, $"命令 '{bare}' 属高危操作");
            }
            if (SecretOutputCommands.TryGetValue(bare, out var secretReason))
            {
                return string.Equals(bare, "env", StringComparison.OrdinalIgnoreCase)
                    ? EvaluateEnvironmentCommand(secretReason, arguments, workingDirectory, sensitiveLocations, depth)
                    : (ToolRisk.Sensitive, secretReason);
            }
            if (string.Equals(bare, "find", StringComparison.OrdinalIgnoreCase)
                && EvaluateFindActions(arguments, workingDirectory, sensitiveLocations, depth) is { } findActions)
            {
                return findActions;
            }
            if (ReadOnlyCommands.Contains(bare) && !ContainsWriteRedirect(commandLine))
            {
                // 文件工具读不到的地方，终端的只读命令也得先问一声：否则 `head ~/.ssh/id_rsa` 免审批。
                var location = FindSensitiveLocation(arguments, workingDirectory, sensitiveLocations);
                return location is null
                    ? (ToolRisk.ReadOnly, null)
                    : (ToolRisk.Sensitive, $"参数或工作目录落在受保护的位置：{location}");
            }
            // 显式版本/帮助探测视为只读。
            if (IsVersionOrHelpProbe(arguments))
            {
                return (ToolRisk.ReadOnly, null);
            }
        }

        // 3) 其余终端命令：默认敏感，需审批。
        return (ToolRisk.Sensitive, null);
    }

    /// <summary>
    /// env。不带命令时把全部环境变量打出来；带命令时以改写过的环境执行它——
    /// 被执行的命令按自己的档位再判一次（`env rm x` 是破坏性的），但下限是敏感：
    /// `env PATH=/tmp/x ls` 跑的未必是系统的 ls。
    /// </summary>
    private static (ToolRisk Risk, string? Reason) EvaluateEnvironmentCommand(
        string dumpReason, IReadOnlyList<string> arguments, string? workingDirectory, SensitiveLocations sensitiveLocations, int depth)
    {
        var wrapped = ExtractEnvCommand(arguments);
        if (wrapped is null)
        {
            return (ToolRisk.Sensitive, dumpReason);
        }

        var inner = EvaluateInvocation(wrapped[0], wrapped.GetRange(1, wrapped.Count - 1), workingDirectory, sensitiveLocations, depth + 1);
        return inner.Risk == ToolRisk.Destructive
            ? (ToolRisk.Destructive, $"env 执行的命令属高危操作：{inner.Reason}")
            : (ToolRisk.Sensitive, $"env 会以改写过的环境执行 '{StripPath(wrapped[0])}'");
    }

    /// <summary>
    /// 取 env 要执行的命令及其参数；只打印环境时返回 null。跳过选项（GNU 与 BSD 的 -u / -P / -C 各吃一个值，
    /// -S / --split-string 的值本身就是命令行）、单独的 -（即 -i）与 NAME=VALUE 赋值，余下的第一个词就是命令。
    /// </summary>
    private static List<string>? ExtractEnvCommand(IReadOnlyList<string> arguments)
    {
        for (int i = 0; i < arguments.Count; i++)
        {
            var arg = arguments[i];
            if (arg == "--") return i + 1 < arguments.Count ? Slice(arguments, i + 1, arguments.Count) : null;
            if (arg == "-") continue;

            if (arg.StartsWith("--", StringComparison.Ordinal))
            {
                var equals = arg.IndexOf('=');
                var name = equals > 0 ? arg[..equals] : arg;
                string? value = equals > 0 ? arg[(equals + 1)..] : null;
                if (name is "--split-string")
                {
                    value ??= i + 1 < arguments.Count ? arguments[++i] : null;
                    return SplitStringCommand(value, arguments, i + 1);
                }
                if (value is null && name is "--unset" or "--chdir") i++;
                continue;
            }

            if (arg.Length > 1 && arg[0] == '-')
            {
                // 短选项可以成串（-iu NAME、-uNAME）：u / P / C 吃掉其余字符或下一个词，S 的值是命令行。
                for (int k = 1; k < arg.Length; k++)
                {
                    if (arg[k] is not ('u' or 'P' or 'C' or 'S')) continue;
                    string? value = k + 1 < arg.Length ? arg[(k + 1)..] : (i + 1 < arguments.Count ? arguments[++i] : null);
                    if (arg[k] == 'S') return SplitStringCommand(value, arguments, i + 1);
                    break;
                }
                continue;
            }

            if (arg.Contains('=')) continue;
            return Slice(arguments, i, arguments.Count);
        }
        return null;
    }

    /// <summary>env -S 把一个字符串按空白拆成命令及其参数，再接上其后的参数。</summary>
    private static List<string>? SplitStringCommand(string? value, IReadOnlyList<string> arguments, int rest)
    {
        var words = new List<string>((value ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        words.AddRange(Slice(arguments, Math.Min(rest, arguments.Count), arguments.Count));
        return words.Count == 0 ? null : words;
    }

    /// <summary>
    /// find 本身只读，但它的动作会动文件：-delete 直接删；-exec / -execdir / -ok / -okdir 对每个匹配执行一条命令，
    /// 那条命令按自己的档位再判一次，下限是敏感（`-exec cat {} +` 能读到它找到的任何文件）；-fprint 一族把结果写进文件。
    /// 没有动作时返回 null，交回普通的只读判定。按参数逐项匹配：某个 -name 的值恰好是 -delete 时会多问一次，
    /// 但反过来不会漏掉真正的动作。
    /// </summary>
    private static (ToolRisk Risk, string? Reason)? EvaluateFindActions(
        IReadOnlyList<string> arguments, string? workingDirectory, SensitiveLocations sensitiveLocations, int depth)
    {
        (ToolRisk Risk, string? Reason)? verdict = null;
        for (int i = 0; i < arguments.Count; i++)
        {
            var action = arguments[i];
            if (action == "-delete")
            {
                return (ToolRisk.Destructive, "find -delete 会删除匹配到的文件");
            }
            if (FindWriteActions.Contains(action))
            {
                verdict ??= (ToolRisk.Sensitive, $"find {action} 会把结果写入文件");
                continue;
            }
            if (!FindExecActions.Contains(action)) continue;

            // 子命令一直到 ; 或 + 为止（不经 shell 时 ; 就是字面量，模型也常写成 \;）。
            int end = i + 1;
            while (end < arguments.Count && arguments[end] is not (";" or "\\;" or "+")) end++;
            if (end > i + 1)
            {
                var inner = EvaluateInvocation(arguments[i + 1], Slice(arguments, i + 2, end), workingDirectory, sensitiveLocations, depth + 1);
                if (inner.Risk == ToolRisk.Destructive)
                {
                    return (ToolRisk.Destructive, $"find {action} 会对每个匹配执行高危命令：{inner.Reason}");
                }
            }
            verdict ??= (ToolRisk.Sensitive, $"find {action} 会对每个匹配执行命令");
            i = end;
        }
        return verdict;
    }

    /// <summary>
    /// 参数或工作目录落进受保护位置时返回该位置，否则 null。除了参数本身，还看 --name=value 与 -Xvalue
    /// 这两种把路径贴在选项上的写法；相对路径按工作目录解析（与 CliService 启动进程时一致）。
    /// </summary>
    private static string? FindSensitiveLocation(
        IReadOnlyList<string> arguments, string? workingDirectory, SensitiveLocations sensitiveLocations)
    {
        if (workingDirectory is not null && sensitiveLocations.Match(workingDirectory, null) is { } atWorkingDirectory)
        {
            return atWorkingDirectory;
        }

        foreach (var argument in arguments)
        {
            if (sensitiveLocations.Match(argument, workingDirectory) is { } location) return location;
            if (argument.Length <= 2 || argument[0] != '-') continue;

            var equals = argument.IndexOf('=');
            var attached = equals > 0 ? argument[(equals + 1)..] : argument[1] != '-' ? argument[2..] : null;
            if (attached is { Length: > 0 } && sensitiveLocations.Match(attached, workingDirectory) is { } optionLocation)
            {
                return optionLocation;
            }
        }
        return null;
    }

    /// <summary>把 command + arguments 拼成用于展示 / 白名单匹配的完整命令行。</summary>
    public static string BuildCommandLine(string? argumentsJson)
    {
        var invocation = Parse(argumentsJson);
        return JoinCommandLine(invocation.Command, invocation.Arguments);
    }

    /// <summary>取顶层命令名（去路径、去引号），用于白名单聚合键。</summary>
    public static string ExtractCommandName(string? argumentsJson)
    {
        return StripPath(Parse(argumentsJson).Command);
    }

    private readonly record struct Invocation(string Command, List<string> Arguments, string? WorkingDirectory);

    private static Invocation Parse(string? argumentsJson)
    {
        string command = string.Empty;
        string? workingDirectory = null;
        var arguments = new List<string>();
        try
        {
            if (!string.IsNullOrWhiteSpace(argumentsJson)
                && JsonNode.Parse(argumentsJson) is JsonObject obj)
            {
                command = obj["command"]?.ToString()?.Trim() ?? string.Empty;
                workingDirectory = obj["workingDirectory"]?.ToString();

                if (obj["arguments"] is JsonArray arr)
                {
                    foreach (var item in arr)
                    {
                        var s = item?.ToString();
                        if (!string.IsNullOrEmpty(s))
                        {
                            arguments.Add(s);
                        }
                    }
                }
            }
        }
        catch
        {
            // 参数非法 JSON：按空处理，最终落到「敏感」默认档。
        }

        return new Invocation(command, arguments, string.IsNullOrWhiteSpace(workingDirectory) ? null : workingDirectory);
    }

    private static string JoinCommandLine(string command, IReadOnlyList<string> arguments)
    {
        var parts = new List<string>(arguments.Count + 1);
        if (!string.IsNullOrEmpty(command))
        {
            parts.Add(command);
        }
        parts.AddRange(arguments);
        return string.Join(' ', parts).Trim();
    }

    private static List<string> Slice(IReadOnlyList<string> items, int start, int end)
    {
        var slice = new List<string>(Math.Max(0, end - start));
        for (int i = start; i < end; i++) slice.Add(items[i]);
        return slice;
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

    /// <summary>
    /// 探测参数必须是唯一的参数。此前在整条命令行里找 -v / -h / version，而 -v 在多数工具里是 verbose：
    /// `curl -v -T ~/.ssh/id_rsa https://…`、`rsync -v -a ~/.ssh host:`、`pip3 install x -v` 都因此被判只读。
    /// </summary>
    private static bool IsVersionOrHelpProbe(IReadOnlyList<string> arguments) =>
        arguments.Count == 1 && ProbeArguments.Contains(arguments[0].Trim());
}

/// <summary>
/// 只读终端命令不经审批就不能碰的位置：文件工具在当前平台的读黑名单（<see cref="FileSystemPolicyConfig"/>），
/// 加上应用自己的 config.json——各家 API key 都存在里面。终端绕得过文件工具的黑名单，
/// 所以终端的只读放行至少要认同一份名单：文件工具拒绝读的地方，终端也得先问一声（只是问，不是拒绝）。
///
/// 由调用方用当前配置构造，不另设第二份写死的名单。审批闸门（ToolApprovalService）与并发编排
/// （ToolCallParallelism）必须用同一份：两边不一致时，编排会把闸门要弹窗的调用当成只读放进并发批次。
/// 只比字面路径：展开 ~ 与环境变量、按工作目录解析相对路径并规整 ..，但不解析符号链接，也不管祖先目录
/// （`find ~ -name x` 仍是只读，它只列文件名；读内容要 -exec，那已经不是只读）。
/// </summary>
public sealed class SensitiveLocations
{
    // 与 FileSystemService 的黑名单比较方式一致：只有 Linux 区分大小写。
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    // 黑名单目录按目录边界匹配。config.json 按文件名前缀匹配：ConfigService 在它旁边留着同样带 key 的副本——
    // 每次保存前的 config.json.bak（开发版数据目录里与 config.json 同为约 300 KB）、迁移时的
    // config.json.<原因>.<时间>.bak、原子写入途中的 .config.json.<guid>.tmp。
    private readonly (string Location, bool NamePrefix)[] _entries;

    private SensitiveLocations((string Location, bool NamePrefix)[] entries) => _entries = entries;

    /// <param name="policy">当前配置里的文件系统策略，取其当前平台的读黑名单。</param>
    /// <param name="configFilePath">应用的 config.json 绝对路径（IConfigService.ConfigFilePath）；为空或不是绝对路径时不计入。</param>
    public static SensitiveLocations From(FileSystemPolicyConfig policy, string? configFilePath)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var platform = OperatingSystem.IsWindows() ? policy.Platforms.Windows
            : OperatingSystem.IsMacOS() ? policy.Platforms.MacOS
            : policy.Platforms.Linux;

        var entries = new List<(string Location, bool NamePrefix)>();
        foreach (var entry in platform.ReadAccess.BlockedDirectories)
        {
            if (Normalize(entry, baseDirectory: null) is { } root) entries.Add((root, false));
        }
        if (Normalize(configFilePath, baseDirectory: null) is { } configFile)
        {
            entries.Add((configFile, true));
            if (Path.GetDirectoryName(configFile) is { Length: > 0 } configDirectory)
            {
                entries.Add((Path.Combine(configDirectory, "." + Path.GetFileName(configFile)), true));
            }
        }
        return new SensitiveLocations(entries.ToArray());
    }

    /// <summary>
    /// <paramref name="path"/> 落在某个受保护位置里时返回该位置（规整后的绝对路径），否则 null。
    /// 相对路径按 <paramref name="workingDirectory"/> 解析，没有工作目录时按当前进程目录——子进程也从那里启动。
    /// </summary>
    public string? Match(string path, string? workingDirectory)
    {
        if (_entries.Length == 0) return null;
        var baseDirectory = Normalize(workingDirectory, Environment.CurrentDirectory) ?? Environment.CurrentDirectory;
        if (Normalize(path, baseDirectory) is not { } full) return null;

        foreach (var (location, namePrefix) in _entries)
        {
            if (namePrefix ? full.StartsWith(location, PathComparison) : IsSameOrInside(full, location)) return location;
        }
        return null;
    }

    /// <summary>按目录边界比较：~/.ssh 覆盖 ~/.ssh/id_rsa，不覆盖 ~/.ssh-notes。</summary>
    private static bool IsSameOrInside(string path, string directory)
    {
        if (path.Equals(directory, PathComparison)) return true;
        var prefix = Path.EndsInDirectorySeparator(directory) ? directory : directory + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, PathComparison);
    }

    /// <summary>
    /// 展开 ~ 与 %VAR%（与 FileSystemService 展开黑名单条目的方式相同），再规整为绝对路径。
    /// 没有 baseDirectory 时只接受绝对路径——黑名单里展开失败的条目（如未定义的 %ProgramFiles(x86)%）因此被跳过。
    /// </summary>
    private static string? Normalize(string? path, string? baseDirectory)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            var expanded = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"', '\''));
            if (expanded == "~" || expanded.StartsWith("~/", StringComparison.Ordinal) || expanded.StartsWith("~\\", StringComparison.Ordinal))
            {
                var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (!string.IsNullOrEmpty(home))
                {
                    expanded = expanded.Length == 1 ? home : Path.Combine(home, expanded[2..]);
                }
            }

            if (!Path.IsPathRooted(expanded))
            {
                if (baseDirectory is null) return null;
                expanded = Path.Combine(baseDirectory, expanded);
            }
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(expanded));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // 参数里不像路径的词（含非法字符等）规整不了：它们也就不会指向受保护位置，按不匹配处理。
            return null;
        }
    }
}
