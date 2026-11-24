using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;

namespace Athena.UI.Services;

/// <summary>
/// 审批去重键的构造。这个键决定「本会话允许」和「始终允许」到底放行了多大一片。
///
/// 此前只有终端命令细化到了命令名，其余工具一律按函数名聚合——用户点一次
/// 「始终允许 write_system_file」，从此任何路径的任何写入永久放行。这是一个
/// 「要么每次都烦、要么一次全放开」的二元选择，用户会被逼向后者，净安全性反而更差。
///
/// 现在把作用域一起编进键里：
/// - 终端命令 → 命令名（放行 git 不等于放行 rm）
/// - 出网下载 → 主机名（放行一次 example.com 覆盖同批次下载，不会一张图一次弹窗）
/// - 文件写入 → 目标所在目录（放行 src/ 不等于放行整块磁盘）
/// 这样「始终允许」重新变成一个可以放心点的选项。
/// </summary>
public static class ToolApprovalKey
{
    /// <summary>工具名 → 决定作用域的那个路径参数名。</summary>
    private static readonly Dictionary<string, string> PathScopedTools = new(StringComparer.OrdinalIgnoreCase)
    {
        ["write_system_file"] = "path",
        ["modify_system_file"] = "path",
        ["delete_system_file"] = "path",
        ["create_directory"] = "path",
        // create_new_memory 刻意不在此列：它的 filePath 是相对知识库根的，
        // 在这里按当前工作目录归一化只会显示一个不存在的目录，误导用户。
        // 复制看目的地：源文件原样保留，风险在写到哪。
        ["copy_system_file"] = "destinationPath",
        // 移动看来源：内容是从那里消失的，目的地反而不会被覆盖。
        ["move_system_file"] = "sourcePath"
    };

    public readonly record struct Result(string Key, string? Scope, string? CommandName)
    {
        public bool IsTerminal => CommandName != null;
    }

    public static Result Build(string functionName, string? argumentsJson)
    {
        if (string.IsNullOrEmpty(functionName)) return new Result(string.Empty, null, null);

        if (string.Equals(functionName, "execute_terminal_command", StringComparison.OrdinalIgnoreCase))
        {
            var command = SafeCommandName(argumentsJson);
            return new Result($"terminal:{command}", command, command);
        }

        if (string.Equals(functionName, "fetch_url_to_file", StringComparison.OrdinalIgnoreCase))
        {
            var host = ExtractHost(argumentsJson);
            return host is null
                ? new Result(functionName, null, null)
                : new Result($"{functionName}@{host}", host, null);
        }

        if (PathScopedTools.TryGetValue(functionName, out var argumentName))
        {
            var directory = ExtractDirectory(argumentsJson, argumentName);
            return directory is null
                ? new Result(functionName, null, null)
                : new Result($"{functionName}@{directory}", directory, null);
        }

        return new Result(functionName, null, null);
    }

    private static string SafeCommandName(string? argumentsJson)
    {
        try { return TerminalCommandRisk.ExtractCommandName(argumentsJson); }
        catch { return string.Empty; }
    }

    private static string? ExtractHost(string? argumentsJson)
    {
        var url = ReadStringArgument(argumentsJson, "url");
        if (string.IsNullOrWhiteSpace(url)) return null;
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : null;
    }

    /// <summary>
    /// 取目标路径所在目录。相对路径按当前工作目录解析——归一化失败时返回 null，
    /// 退回按函数名聚合：这只会多问一次，绝不会少问一次。
    /// </summary>
    private static string? ExtractDirectory(string? argumentsJson, string argumentName)
    {
        var path = ReadStringArgument(argumentsJson, argumentName);
        if (string.IsNullOrWhiteSpace(path)) return null;

        try
        {
            var full = Path.GetFullPath(path);
            // 用父目录而非目标本身：文件写入按所在目录聚合，create_directory 按其父目录聚合，
            // 否则每建一个子目录都要重新授权一次。根目录没有父级时退回它自己。
            var directory = Path.GetDirectoryName(full);
            return string.IsNullOrEmpty(directory) ? full : directory;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>读取一个字符串参数，容忍模型输出 snake_case / 大小写差异。</summary>
    private static string? ReadStringArgument(string? argumentsJson, string argumentName)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson)) return null;
        try
        {
            if (JsonNode.Parse(argumentsJson) is not JsonObject obj) return null;
            var wanted = Canonicalize(argumentName);
            foreach (var pair in obj)
            {
                if (Canonicalize(pair.Key) == wanted) return pair.Value?.ToString();
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    private static string Canonicalize(string name) => name.Replace("_", string.Empty).ToLowerInvariant();
}
