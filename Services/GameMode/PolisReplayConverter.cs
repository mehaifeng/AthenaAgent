using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace Athena.UI.Services.GameMode;

/// <summary>
/// 回放转换的输入：归档里一条消息的投影。只有角色、时间和工具调用的结构——<b>没有正文</b>。
/// 工具结果是否成功、推理有多长，由调用方从原消息里读出布尔值和长度再填进来，正文本身不进这里。
/// </summary>
public sealed record PolisReplayMessage(
    string Role,
    DateTimeOffset Timestamp,
    bool IsHidden,
    string? ToolCallsJson,
    string? ToolCallId,
    bool? ToolSucceeded,
    long? DurationMs,
    int ReasoningChars);

/// <summary>
/// 一条回放事件。<see cref="T"/> 与 <see cref="End"/> 是相对回放开始的毫秒数。
/// <see cref="Type"/>：<c>turn</c>（你交出一份委托）、<c>think</c>（等模型：雅典娜沉思）、
/// <c>tool</c>（一次工具调用）、<c>deliver</c>（这一回合交付）。
/// </summary>
public sealed record PolisReplayEvent
{
    public required long T { get; init; }
    public required string Type { get; init; }
    public long? End { get; init; }
    public int? Turn { get; init; }
    public string? Id { get; init; }
    public string? Tool { get; init; }
    public string? Category { get; init; }
    /// <summary><c>inside</c>（工作区内，带 <see cref="Path"/>）、<c>outside</c>（城外）或 <c>none</c>（这个调用没有路径）。</summary>
    public string? Place { get; init; }
    /// <summary>工作区相对路径，'/' 分隔；空串表示工作区根目录本身。只在 <see cref="Place"/> 为 inside 时出现。</summary>
    public string? Path { get; init; }
    /// <summary>路径所在的建筑（顶层文件夹名），空串是广场。转换时不知道城里有哪些建筑，由 <see cref="PolisFixture.Compose"/> 按快照补上。</summary>
    public string? Building { get; init; }
    public bool? Ok { get; init; }
    /// <summary>这一轮推理的字符数（只有长度），驱动沉思时光的明暗。</summary>
    public int? Reasoning { get; init; }
    /// <summary>结束时间是估的（老消息没有记录回合总时长）。</summary>
    public bool? Estimated { get; init; }
    /// <summary>派发子代理时的侍女人数。</summary>
    public int? Agents { get; init; }
}

public sealed record PolisReplay(
    int SchemaVersion,
    long DurationMs,
    int Turns,
    int ToolCalls,
    int CompressedGaps,
    IReadOnlyList<PolisReplayEvent> Events)
{
    public const int CurrentSchemaVersion = 1;
}

public sealed record PolisReplayOptions
{
    /// <summary>上一回合交付到下一次提问之间的空闲，超过这个值就剪到这个值：回放不重演你离开电脑的那十分钟。</summary>
    public long MaxIdleGapMs { get; init; } = 3_000;

    /// <summary>
    /// 气泡没记回合总时长（老归档）时，最后一轮"等模型"按这个时长估：logs.db 里非首轮等模型的中位数是 4.7–4.8 秒。
    /// </summary>
    public long EstimatedFinalThinkMs { get; init; } = 4_800;

    /// <summary>同一轮里结果时间相差不超过这个值的连续工具，视为同一批并发执行（它们的结果是一起回填的）。</summary>
    public long ParallelToleranceMs { get; init; } = 30;

    /// <summary>路径比较是否忽略大小写（macOS / Windows 的默认文件系统都不区分）。</summary>
    public bool IgnoreCase { get; init; } = true;

    public static PolisReplayOptions Default { get; } = new();
}

/// <summary>
/// 把一段真实会话转成回放事件（设计稿 6.2 / M0）。时间全部来自归档里每条消息的 <c>Timestamp</c>：
/// 隐藏的工具调用载体在这一轮模型输出结束时生成，工具结果在它执行完、回填上下文时生成，
/// 可见气泡在回合开始时生成并在结束时记下 <c>DurationMs</c>。所以：
/// 等模型 = 上一个锚点 → 载体；工具 = 载体（或同轮上一批的结束）→ 结果；最后一轮 = 最后一个锚点 → 气泡开始 + 总时长。
/// </summary>
public static class PolisReplayConverter
{
    public static PolisReplay Convert(
        IReadOnlyList<PolisReplayMessage> messages,
        string workspaceRoot,
        string? homeDirectory,
        PolisReplayOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        options ??= PolisReplayOptions.Default;

        var events = new List<PolisReplayEvent>();
        var firstUser = messages.FirstOrDefault(m => IsRole(m, "user"));
        if (firstUser is null) return new PolisReplay(PolisReplay.CurrentSchemaVersion, 0, 0, 0, 0, events);

        var origin = firstUser.Timestamp;
        long shift = 0;
        long? lastDeliver = null;
        var turn = 0;
        var toolCount = 0;
        var compressedGaps = 0;
        long lastEmitted = 0;

        // 按用户消息切回合；回合内最后一条可见的助手消息才是气泡
        var userIndexes = messages.Select((m, i) => (m, i)).Where(x => IsRole(x.m, "user")).Select(x => x.i).ToList();
        for (var u = 0; u < userIndexes.Count; u++)
        {
            var start = userIndexes[u];
            var stop = u + 1 < userIndexes.Count ? userIndexes[u + 1] : messages.Count;
            var user = messages[start];
            turn++;

            var t = Rel(user.Timestamp);
            if (lastDeliver is { } delivered && t - delivered > options.MaxIdleGapMs)
            {
                shift += t - delivered - options.MaxIdleGapMs;
                compressedGaps++;
                t = Rel(user.Timestamp);
            }
            Emit(new PolisReplayEvent { T = t, Type = "turn", Turn = turn });

            var anchor = user.Timestamp;
            var bubbleIndex = -1;
            for (var i = stop - 1; i > start; i--)
            {
                if (IsRole(messages[i], "assistant") && !messages[i].IsHidden && string.IsNullOrEmpty(messages[i].ToolCallsJson))
                {
                    bubbleIndex = i;
                    break;
                }
            }

            var results = new Dictionary<string, PolisReplayMessage>(StringComparer.Ordinal);
            for (var i = start + 1; i < stop; i++)
            {
                var m = messages[i];
                if (IsRole(m, "tool") && !string.IsNullOrEmpty(m.ToolCallId)) results.TryAdd(m.ToolCallId, m);
            }

            for (var i = start + 1; i < stop; i++)
            {
                var carrier = messages[i];
                if (!IsRole(carrier, "assistant") || string.IsNullOrEmpty(carrier.ToolCallsJson)) continue;

                var calls = ParseCalls(carrier.ToolCallsJson);
                var roundEnd = Later(carrier.Timestamp, anchor);
                Emit(new PolisReplayEvent
                {
                    T = Rel(anchor),
                    End = Rel(roundEnd),
                    Type = "think",
                    Reasoning = carrier.ReasoningChars > 0 ? carrier.ReasoningChars : null
                });

                // 同一批并发的工具，结果是 Task.WhenAll 之后一起回填的：时间几乎相同。它们共享同一个开始时间。
                var prevEnd = roundEnd;
                var k = 0;
                while (k < calls.Count)
                {
                    var groupStart = prevEnd;
                    var firstEnd = ResultTime(calls[k]);
                    var j = k;
                    var groupEnd = groupStart;
                    while (j < calls.Count)
                    {
                        var end = ResultTime(calls[j]);
                        if (j > k && (firstEnd is null || end is null || Math.Abs((end.Value - firstEnd.Value).TotalMilliseconds) > options.ParallelToleranceMs)) break;
                        var finished = end is { } e ? Later(e, groupStart) : groupStart;
                        groupEnd = Later(groupEnd, finished);
                        EmitTool(calls[j], groupStart, finished);
                        j++;
                    }
                    prevEnd = groupEnd;
                    k = j;
                }
                anchor = prevEnd;
            }

            DateTimeOffset turnEnd;
            bool estimated;
            if (bubbleIndex >= 0 && messages[bubbleIndex].DurationMs is { } turnMs && turnMs > 0)
            {
                turnEnd = Later(messages[bubbleIndex].Timestamp.AddMilliseconds(turnMs), anchor);
                estimated = false;
            }
            else
            {
                turnEnd = anchor.AddMilliseconds(options.EstimatedFinalThinkMs);
                if (stop < messages.Count && messages[stop].Timestamp > anchor && messages[stop].Timestamp < turnEnd) turnEnd = messages[stop].Timestamp;
                estimated = true;
            }

            Emit(new PolisReplayEvent
            {
                T = Rel(anchor),
                End = Rel(turnEnd),
                Type = "think",
                Estimated = estimated ? true : null
            });
            var deliverAt = Rel(turnEnd);
            Emit(new PolisReplayEvent { T = deliverAt, Type = "deliver", Turn = turn, Estimated = estimated ? true : null });
            lastDeliver = Math.Max(deliverAt, lastEmitted);

            DateTimeOffset? ResultTime(ToolCallRef call)
                => results.TryGetValue(call.Id, out var r) ? r.Timestamp : null;

            void EmitTool(ToolCallRef call, DateTimeOffset begin, DateTimeOffset end)
            {
                toolCount++;
                var category = PolisToolCategories.ForTool(call.Name);
                var (place, path) = ResolvePath(ExtractTargetPath(call.ArgumentsJson), workspaceRoot, homeDirectory, options.IgnoreCase);
                results.TryGetValue(call.Id, out var result);
                Emit(new PolisReplayEvent
                {
                    T = Rel(begin),
                    End = Rel(end),
                    Type = "tool",
                    Id = "t" + toolCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    Tool = call.Name,
                    Category = PolisToolCategories.Slug(category),
                    Place = place,
                    Path = path,
                    // 没有结果的调用是被打断的（用户停止或进程退出）：算失败
                    Ok = result?.ToolSucceeded ?? false,
                    Agents = category == PolisActionCategory.SubAgents ? CountTasks(call.ArgumentsJson) : null
                });
            }
        }

        var totalMs = events.Count == 0 ? 0 : events.Max(e => Math.Max(e.T, e.End ?? e.T));
        return new PolisReplay(PolisReplay.CurrentSchemaVersion, totalMs, turn, toolCount, compressedGaps, events);

        long Rel(DateTimeOffset at) => (long)Math.Round((at - origin).TotalMilliseconds) - shift;

        void Emit(PolisReplayEvent e)
        {
            // 时钟回拨或乱序的时间戳不能让事件倒着走
            var t = Math.Max(e.T, lastEmitted);
            var end = e.End is { } x ? Math.Max(x, t) : (long?)null;
            lastEmitted = t;
            events.Add(e with { T = t, End = end });
        }
    }

    private static DateTimeOffset Later(DateTimeOffset a, DateTimeOffset b) => a >= b ? a : b;

    private static bool IsRole(PolisReplayMessage m, string role) => string.Equals(m.Role, role, StringComparison.OrdinalIgnoreCase);

    private sealed record ToolCallRef(string Id, string Name, string ArgumentsJson);

    /// <summary><c>ToolCallsJson</c> 是 <c>[{"Id","FunctionName","Arguments"}]</c>；读不了的整条忽略，不让一条坏数据拖垮回放。</summary>
    private static List<ToolCallRef> ParseCalls(string toolCallsJson)
    {
        var calls = new List<ToolCallRef>();
        try
        {
            using var doc = JsonDocument.Parse(toolCallsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return calls;
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var id = ReadString(item, "Id") ?? string.Empty;
                var name = ReadString(item, "FunctionName");
                if (string.IsNullOrEmpty(name)) continue;
                calls.Add(new ToolCallRef(id, name, ReadString(item, "Arguments") ?? "{}"));
            }
        }
        catch (JsonException)
        {
            // 载体的 JSON 坏了：这一轮当作没有工具调用，回放照常继续
        }
        return calls;
    }

    private static string? ReadString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>按 <see cref="PolisToolCategories.PathArgumentPriority"/> 取工具参数里的目标路径；参数不是 JSON 对象时为 null。</summary>
    public static string? ExtractTargetPath(string argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            foreach (var key in PolisToolCategories.PathArgumentPriority)
            {
                var value = ReadString(doc.RootElement, key);
                if (!string.IsNullOrWhiteSpace(value)) return value;
            }
        }
        catch (JsonException)
        {
            // 模型偶尔发出不合法的参数 JSON（日志里见过），这一次调用就没有地点
        }
        return null;
    }

    private static int? CountTasks(string argumentsJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("tasks", out var tasks)
                && tasks.ValueKind == JsonValueKind.Array
                ? tasks.GetArrayLength()
                : null;
        }
        catch (JsonException)
        {
            return null;   // 参数坏了：侍女人数未知，网页按默认人数画
        }
    }

    /// <summary>
    /// 路径 → 地点。纯字符串运算，不碰文件系统，不解析软链：
    /// <c>~</c> 按给定的主目录展开；相对路径一律算城外（文件工具把它解析到知识库 / AthenaData，终端按进程目录，都不是工作区）；
    /// 绝对路径规范化 '.' 和 '..' 后，落在工作区根目录之内的转成相对路径（根目录本身是空串，即广场）。
    /// </summary>
    public static (string Place, string? Path) ResolvePath(string? rawPath, string workspaceRoot, string? homeDirectory, bool ignoreCase = true)
    {
        if (string.IsNullOrWhiteSpace(rawPath)) return ("none", null);
        var path = rawPath.Trim();
        if (homeDirectory != null && (path == "~" || path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal)))
        {
            path = homeDirectory.TrimEnd('/', '\\') + path[1..];
        }

        var normalized = Normalize(path);
        var root = Normalize(workspaceRoot);
        if (normalized is null || root is null) return ("outside", null);

        var comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (string.Equals(normalized, root, comparison)) return ("inside", string.Empty);
        var prefix = root.EndsWith('/') ? root : root + "/";
        return normalized.StartsWith(prefix, comparison)
            ? ("inside", normalized[prefix.Length..])
            : ("outside", null);
    }

    /// <summary>绝对路径的规范形式：'/' 分隔，消掉 '.' 和 '..'，去掉末尾的 '/'（根除外）。相对路径返回 null。</summary>
    private static string? Normalize(string path)
    {
        var p = path.Replace('\\', '/');
        string head;
        if (p.StartsWith('/'))
        {
            head = "/";
            p = p[1..];
        }
        else if (p.Length >= 2 && char.IsAsciiLetter(p[0]) && p[1] == ':')
        {
            head = char.ToUpperInvariant(p[0]) + ":/";
            p = p[2..].TrimStart('/');
        }
        else
        {
            return null;
        }

        var parts = new List<string>();
        foreach (var segment in p.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".") continue;
            if (segment == "..")
            {
                if (parts.Count > 0) parts.RemoveAt(parts.Count - 1);
                continue;
            }
            parts.Add(segment);
        }
        return head + string.Join('/', parts);
    }
}
