using Athena.UI.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Athena.UI.Services.Context;

/// <summary>
/// 摘要末尾由<b>代码</b>追加的确定性附录。能由本地代码百分之百保证的事，不拿去赌模型的复述能力：
/// <list type="bullet">
/// <item><c>[hard_facts]</c>：附件 id 与落点路径，一行一个值（句柄的唯一传递通道）。</item>
/// <item><c>[recent_files]</c>：被压缩消息的工具调用参数里出现过的文件路径，最近 20 个、去重。</item>
/// <item><c>[latest_user_requests]</c>：最近 3 条用户消息原文，单条有字符上限，超出标注截断。
/// 等价于 Claude Code 的「All user messages」，但由代码保证——用户意图不靠模型转述。</item>
/// </list>
/// 再次压缩时，上一份摘要的附录被<b>解析</b>出来与新材料合并（用户请求只取最新 3 条、文件只取最新 20 个），
/// 而不是整块抄进正文，所以越压越长不会发生。用户请求正文带长度前缀，内容里出现任何标记文本都不会被误读。
/// </summary>
public static class CompressionAppendix
{
    public const string HardFactsHeader = "[hard_facts]";
    public const string RecentFilesHeader = "[recent_files]";
    public const string UserRequestsHeader = "[latest_user_requests]";

    public const int MaxUserRequests = 3;
    public const int MaxRecentFiles = 20;

    /// <summary>
    /// 单条用户请求的字符上限，约 2K token（按全项目通用的 1 token ≈ 3 字符的保守折中）。
    /// 用字符而不是 token 计：附录是代码保证的材料，不该为了截断而依赖估算器。
    /// </summary>
    public const int UserRequestMaxChars = 6_000;

    private const string UserRequestMarker = "[user_request chars=";

    /// <summary>工具调用参数里被当作文件路径的键（大小写不敏感）。</summary>
    private static readonly HashSet<string> PathArgumentKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "path", "filePath", "file_path", "targetPath", "target_path", "sourcePath", "source_path",
        "destinationPath", "destination_path", "inputPath", "input_path", "outputPath", "output_path"
    };

    public sealed record UserRequest(string Text, int OriginalChars)
    {
        public bool IsTruncated => OriginalChars > Text.Length;
    }

    /// <param name="Prose">第一个附录块之前的正文。</param>
    public sealed record Parsed(
        string Prose,
        IReadOnlyList<CompressionHardAnchor> HardFacts,
        IReadOnlyList<string> RecentFiles,
        IReadOnlyList<UserRequest> UserRequests);

    /// <summary>把正文与三个附录块拼成最终摘要；空块整块省略。</summary>
    public static string Build(
        string prose,
        IReadOnlyList<CompressionHardAnchor> hardFacts,
        IReadOnlyList<string> recentFiles,
        IReadOnlyList<UserRequest> userRequests)
    {
        var builder = new StringBuilder(prose.TrimEnd());

        if (hardFacts.Count > 0)
        {
            builder.AppendLine().AppendLine().AppendLine(HardFactsHeader);
            // 一行一个值：任何「同行多值」的分隔方案都要求分隔符不可能出现在值里，
            // 而附件落点挂在用户选定的安装目录下，逗号在路径里是合法字符。
            foreach (var anchor in hardFacts
                         .OrderBy(anchor => anchor.Kind, StringComparer.Ordinal)
                         .ThenBy(anchor => anchor.Value, StringComparer.Ordinal))
                builder.Append(anchor.Kind).Append(": ").AppendLine(anchor.Value);
        }

        if (recentFiles.Count > 0)
        {
            builder.AppendLine().AppendLine().AppendLine(RecentFilesHeader);
            foreach (var file in recentFiles) builder.Append("file: ").AppendLine(file);
        }

        if (userRequests.Count > 0)
        {
            builder.AppendLine().AppendLine().AppendLine(UserRequestsHeader);
            foreach (var request in userRequests)
            {
                builder.Append(UserRequestMarker).Append(request.Text.Length.ToString(CultureInfo.InvariantCulture));
                if (request.IsTruncated)
                    builder.Append(" truncated_from=").Append(request.OriginalChars.ToString(CultureInfo.InvariantCulture));
                builder.AppendLine("]");
                builder.AppendLine(request.Text);
            }
        }
        return builder.ToString().TrimEnd();
    }

    /// <summary>
    /// 逐行扫描整份摘要，收集<b>每一个</b>附录块（reduce 层可能把上一层的附录抄进正文，
    /// 之后生成侧又追加一份，只认第一块会把另一半留在后面）。无法识别的行结束当前块，
    /// 不会去正文里乱抓；用户请求按长度前缀整段跳过。
    /// </summary>
    public static Parsed Parse(string? summary)
    {
        if (string.IsNullOrEmpty(summary)) return new Parsed(string.Empty, [], [], []);

        var facts = new Dictionary<string, CompressionHardAnchor>(StringComparer.Ordinal);
        var files = new List<string>();
        var requests = new List<UserRequest>();
        var firstHeader = -1;
        var mode = Mode.None;

        var position = 0;
        while (position < summary.Length)
        {
            var lineEnd = summary.IndexOf('\n', position);
            var next = lineEnd < 0 ? summary.Length : lineEnd + 1;
            var line = summary.AsSpan(position, (lineEnd < 0 ? summary.Length : lineEnd) - position).Trim();

            var header = Mode.None;
            if (line.Equals(HardFactsHeader, StringComparison.Ordinal)) header = Mode.Facts;
            else if (line.Equals(RecentFilesHeader, StringComparison.Ordinal)) header = Mode.Files;
            else if (line.Equals(UserRequestsHeader, StringComparison.Ordinal)) header = Mode.Requests;
            if (header != Mode.None)
            {
                if (firstHeader < 0) firstHeader = position;
                mode = header;
                position = next;
                continue;
            }

            switch (mode)
            {
                case Mode.Facts:
                case Mode.Files:
                    if (line.IsEmpty) break; // 空行不结束块：块与块之间、块内都可能留空行
                    var entry = line.ToString();
                    var separator = entry.IndexOf(':', StringComparison.Ordinal);
                    var kind = separator > 0 ? entry[..separator].Trim() : string.Empty;
                    var value = separator > 0 ? entry[(separator + 1)..].Trim() : string.Empty;
                    if (mode == Mode.Facts && (kind is "attachment_id" or "attachment_path") && value.Length > 0)
                        facts.TryAdd(kind + "\u001f" + value, new CompressionHardAnchor(kind, value));
                    else if (mode == Mode.Files && kind == "file" && value.Length > 0)
                        files.Add(value);
                    else
                        mode = Mode.None; // 遇到不认识的行：附录之后可能还跟着别的内容，停下，不去正文里乱抓
                    break;

                case Mode.Requests:
                    if (line.IsEmpty) break;
                    if (!TryReadRequestHeader(line, out var length, out var originalChars))
                    {
                        mode = Mode.None;
                        break;
                    }
                    if (next + length > summary.Length)
                    {
                        // 长度前缀指向文本之外：被截断的摘要，保留已读到的，不再往下读。
                        mode = Mode.None;
                        next = summary.Length;
                        break;
                    }
                    requests.Add(new UserRequest(summary.Substring(next, length), Math.Max(length, originalChars)));
                    next += length;
                    if (next < summary.Length && summary[next] == '\r') next++;
                    if (next < summary.Length && summary[next] == '\n') next++;
                    break;
            }
            position = next;
        }

        var prose = firstHeader < 0 ? summary : summary[..firstHeader];
        return new Parsed(
            prose.TrimEnd(),
            facts.Values.ToArray(),
            DistinctKeepingLast(files).ToArray(),
            requests);
    }

    /// <summary>附录之前的正文；喂给模型的「上一份摘要」去掉附录，因为附录会被重新生成。</summary>
    public static string StripAppendices(string? summary) => Parse(summary).Prose;

    /// <summary>
    /// 合并上一份摘要里的用户请求与这一批材料里的用户消息，只留最新 3 条（旧的在前）。
    /// </summary>
    public static IReadOnlyList<UserRequest> MergeUserRequests(
        IEnumerable<UserRequest> previous,
        IEnumerable<CompressionMaterialMessage> material)
    {
        var fresh = material
            .Where(message => string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase)
                              && !string.IsNullOrWhiteSpace(message.Content))
            .Select(message => ToUserRequest(message.Content));
        return previous.Concat(fresh).TakeLast(MaxUserRequests).ToArray();
    }

    /// <summary>
    /// 合并上一份摘要里的文件路径与这批材料的工具调用参数里的路径：去重（后出现的算更新），
    /// 只留最新 20 个。
    /// </summary>
    public static IReadOnlyList<string> MergeRecentFiles(
        IEnumerable<string> previous,
        IEnumerable<CompressionMaterialMessage> material)
        => DistinctKeepingLast(previous.Concat(ExtractFilePaths(material))).TakeLast(MaxRecentFiles).ToArray();

    /// <summary>按出现顺序抽取工具调用参数里的文件路径（尚未去重）。</summary>
    public static IEnumerable<string> ExtractFilePaths(IEnumerable<CompressionMaterialMessage> material)
    {
        foreach (var message in material)
        {
            if (string.IsNullOrEmpty(message.ToolCallsJson)) continue;
            foreach (var path in ReadPathArguments(message.ToolCallsJson)) yield return path;
        }
    }

    public static UserRequest ToUserRequest(string content)
    {
        var text = content.Trim();
        if (text.Length <= UserRequestMaxChars) return new UserRequest(text, text.Length);
        // 头部保留得多一些：请求的主语和约束几乎总在开头；末尾的补充说明也可能是关键，所以保留一小段尾部。
        var head = (int)(UserRequestMaxChars * 0.75);
        var tail = UserRequestMaxChars - head;
        var clipped = text[..head] + "\n[... truncated by the application ...]\n" + text[^tail..];
        return new UserRequest(clipped, text.Length);
    }

    private static IEnumerable<string> ReadPathArguments(string toolCallsJson)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(toolCallsJson);
        }
        catch (JsonException)
        {
            yield break; // 残缺的 ToolCallsJson 只是少抽几个路径，不影响压缩本身
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array) yield break;
            foreach (var call in document.RootElement.EnumerateArray())
            {
                if (call.ValueKind != JsonValueKind.Object) continue;
                var arguments = call.EnumerateObject()
                    .FirstOrDefault(property => string.Equals(property.Name, "Arguments", StringComparison.OrdinalIgnoreCase))
                    .Value;
                if (arguments.ValueKind != JsonValueKind.String) continue;

                JsonDocument? parsed = null;
                try
                {
                    parsed = JsonDocument.Parse(arguments.GetString() ?? string.Empty);
                }
                catch (JsonException)
                {
                    // 参数不是合法 JSON（模型输出被截断）：没有可抽的路径。
                }
                if (parsed == null) continue;

                using (parsed)
                {
                    if (parsed.RootElement.ValueKind != JsonValueKind.Object) continue;
                    foreach (var property in parsed.RootElement.EnumerateObject())
                    {
                        if (!PathArgumentKeys.Contains(property.Name)
                            || property.Value.ValueKind != JsonValueKind.String)
                            continue;
                        var value = property.Value.GetString()?.Trim();
                        if (IsPlausiblePath(value)) yield return value!;
                    }
                }
            }
        }
    }

    private static bool IsPlausiblePath(string? value)
        => !string.IsNullOrEmpty(value) && value.Length <= 500 && value.IndexOfAny(['\r', '\n']) < 0;

    /// <summary>去重并让后出现的覆盖先出现的位置：「最近」按最后一次被碰到的时间算。</summary>
    private static IEnumerable<string> DistinctKeepingLast(IEnumerable<string> values)
    {
        var list = values.ToList();
        var lastIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < list.Count; i++) lastIndex[list[i]] = i;
        for (var i = 0; i < list.Count; i++)
        {
            if (lastIndex[list[i]] == i) yield return list[i];
        }
    }

    private static bool TryReadRequestHeader(ReadOnlySpan<char> line, out int length, out int originalChars)
    {
        length = 0;
        originalChars = 0;
        if (!line.StartsWith(UserRequestMarker, StringComparison.Ordinal) || !line.EndsWith("]", StringComparison.Ordinal))
            return false;
        var body = line[UserRequestMarker.Length..^1];
        var truncated = body.IndexOf(" truncated_from=", StringComparison.Ordinal);
        var lengthText = truncated < 0 ? body : body[..truncated];
        if (!int.TryParse(lengthText, NumberStyles.None, CultureInfo.InvariantCulture, out length) || length < 0)
            return false;
        if (truncated >= 0
            && !int.TryParse(body[(truncated + " truncated_from=".Length)..], NumberStyles.None, CultureInfo.InvariantCulture, out originalChars))
            return false;
        return true;
    }

    private enum Mode
    {
        None,
        Facts,
        Files,
        Requests
    }
}
