using Athena.UI.Models;
using Athena.UI.Services.GameMode;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace Athena.UI.ViewModels.GameMode;

/// <summary>推给页面的一个雅典娜事件（实时模式），时间在推送时换成"多久以前"。</summary>
public sealed record PolisLiveEvent(string Type)
{
    public DateTimeOffset At { get; init; }
    public int? Turn { get; init; }
    public string? Id { get; init; }
    public string? Tool { get; init; }
    public string? Category { get; init; }
    public string? Place { get; init; }
    public string? Path { get; init; }
    public string? Building { get; init; }
    public bool? Ok { get; init; }
    public int? Reasoning { get; init; }
    public int? Agents { get; init; }
    public string? ItemId { get; init; }
    public bool? Waiting { get; init; }
    /// <summary>交付那一回合的气泡（只在 C# 这边用，不发给页面）。</summary>
    public string? MessageId { get; init; }
}

/// <summary>
/// 从气泡已经在用的渲染模型推导雅典娜的事件（设计稿 12.3）：<see cref="ChatMessage.Segments"/>、
/// <see cref="ToolCallEntry.Status"/>、推理段的长度。不给 <c>IChatService.StreamMessageAsync</c> 加回调——
/// 它已经有 11 个了；游戏和气泡是同一个会话的两种呈现，就该读同一份状态。
///
/// 做法是"有变化就整体比对一遍"：上一次推出去的状态记在这里，每次按当前消息列表算出新增的部分。
/// 这比逐个订阅事件更不容易漏——气泡渲染模型的更新路径很多（流式、工具回填、压缩、撤销），
/// 而这里只关心几样东西的终态。没有时间戳（渲染模型里没有），事件按第一次看到它的时刻盖章，误差是一次推送间隔。
/// </summary>
public sealed class PolisEventProjector
{
    /// <summary>推理长度至少涨这么多才再报一次（只用来驱动沉思时光的明暗，不必逐字）。</summary>
    public const int ReasoningStep = 240;

    private readonly HashSet<string> _announcedUsers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ToolCallStatus> _tools = new(StringComparer.Ordinal);
    private readonly HashSet<string> _delivered = new(StringComparer.Ordinal);
    private readonly Dictionary<ChatMessageSegment, int> _reasoning = new(ReferenceEqualityComparer.Instance);
    private readonly List<PolisAthenaTouch> _touches = new();
    // 工具调用 → 它在 _touches 里的那一条（工具做完时把时间窗收口）
    private readonly Dictionary<string, int> _touchIndex = new(StringComparer.Ordinal);
    private int _turn;

    public PolisEventProjector(string? workspaceRoot, string? homeDirectory, IReadOnlySet<string> buildingKeys)
    {
        WorkspaceRoot = workspaceRoot;
        HomeDirectory = homeDirectory;
        BuildingKeys = buildingKeys ?? throw new ArgumentNullException(nameof(buildingKeys));
    }

    public string? WorkspaceRoot { get; }
    public string? HomeDirectory { get; }
    public IReadOnlySet<string> BuildingKeys { get; set; }

    /// <summary>这一回合雅典娜碰过的地方（归属判断用，见 <see cref="PolisChanges.Attribute"/>）。</summary>
    public IReadOnlyList<PolisAthenaTouch> Touches => _touches;

    /// <summary>
    /// 切到一个会话时：已有的一切都算"已经演过"，不重演历史；只有正在进行的那一回合（气泡还在流）
    /// 把它的开场和还在跑的工具报出来，让她一出现就在干活。返回这些事件。
    /// </summary>
    public IReadOnlyList<PolisLiveEvent> Baseline(IReadOnlyList<ChatMessage> messages, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(messages);
        _announcedUsers.Clear();
        _tools.Clear();
        _delivered.Clear();
        _reasoning.Clear();
        _touches.Clear();
        _touchIndex.Clear();
        _turn = 0;

        var running = messages.Count > 0 && messages[^1] is { Role: "assistant" } last && (last.IsStreaming || last.IsLoading);
        var lastUser = -1;
        for (var i = 0; i < messages.Count; i++)
        {
            var message = messages[i];
            if (IsUser(message))
            {
                _turn++;
                _announcedUsers.Add(message.Id);
                lastUser = i;
            }
            else if (IsAssistantBubble(message) && !(running && i == messages.Count - 1))
            {
                _delivered.Add(message.Id);
            }
        }

        var events = new List<PolisLiveEvent>();
        if (!running)
        {
            foreach (var entry in ToolEntries(messages)) _tools[Key(entry)] = entry.Status;
            foreach (var segment in ReasoningSegments(messages)) _reasoning[segment] = segment.Text.Length;
            return events;
        }

        // 正在进行的这一回合：之前回合的工具都算演过，这一回合的按当前状态报出来
        var bubble = messages[^1];
        var earlier = messages.Take(messages.Count - 1).ToList();
        foreach (var entry in ToolEntries(earlier)) _tools[Key(entry)] = entry.Status;
        foreach (var segment in ReasoningSegments(earlier)) _reasoning[segment] = segment.Text.Length;
        events.Add(new PolisLiveEvent("turn") { At = now, Turn = Math.Max(1, _turn) });
        // 这一回合的推理与工具照常报（推理长度驱动沉思时光的明暗）
        events.AddRange(ProjectBubble(bubble, now));
        return events;
    }

    /// <summary>按当前消息列表算出这一次新增的事件。</summary>
    public IReadOnlyList<PolisLiveEvent> Project(IReadOnlyList<ChatMessage> messages, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var events = new List<PolisLiveEvent>();
        foreach (var message in messages)
        {
            if (IsUser(message))
            {
                if (_announcedUsers.Add(message.Id))
                {
                    _turn++;
                    // 新的一回合：归属只看这一回合的工具
                    _touches.Clear();
                    _touchIndex.Clear();
                    events.Add(new PolisLiveEvent("turn") { At = now, Turn = _turn });
                }
                continue;
            }
            if (!string.Equals(message.Role, "assistant", StringComparison.Ordinal)) continue;
            events.AddRange(ProjectBubble(message, now));
        }
        return events;
    }

    private IEnumerable<PolisLiveEvent> ProjectBubble(ChatMessage message, DateTimeOffset now)
    {
        foreach (var segment in message.Segments)
        {
            if (segment.IsReasoning)
            {
                var length = segment.Text.Length;
                var known = _reasoning.TryGetValue(segment, out var was);
                if (!known || length - was >= ReasoningStep)
                {
                    _reasoning[segment] = length;
                    if (length > 0) yield return new PolisLiveEvent("think") { At = now, Reasoning = length };
                }
                continue;
            }
            if (!segment.IsToolCallGroup) continue;
            foreach (var entry in segment.ToolCalls)
            {
                var key = Key(entry);
                if (!_tools.TryGetValue(key, out var status))
                {
                    _tools[key] = entry.Status;
                    var start = DescribeTool(entry, key, now);
                    yield return start;
                    if (entry.Status != ToolCallStatus.Running)
                        yield return EndOf(entry, key, now);
                    continue;
                }
                if (status == ToolCallStatus.Running && entry.Status != ToolCallStatus.Running)
                {
                    _tools[key] = entry.Status;
                    yield return EndOf(entry, key, now);
                }
            }
        }

        // 交付：气泡停止流式输出（成功、停止、报错都算——你等的就是这一刻）
        if (IsAssistantBubble(message) && !message.IsStreaming && !message.IsLoading && _delivered.Add(message.Id))
        {
            yield return new PolisLiveEvent("deliver") { At = now, Turn = _turn, MessageId = message.Id };
        }
    }

    private PolisLiveEvent DescribeTool(ToolCallEntry entry, string key, DateTimeOffset now)
    {
        var category = PolisToolCategories.ForTool(entry.Name);
        var (place, path) = Resolve(entry);
        var building = place == "inside" ? PolisFixture.SiteOf(path, BuildingKeys) : null;
        var touch = category == PolisActionCategory.SubAgents
            ? new PolisAthenaTouch(null, now, null, WholeWorkspace: true)
            : place == "inside" ? new PolisAthenaTouch(path, now, null) : null;
        if (touch != null)
        {
            _touchIndex[key] = _touches.Count;
            _touches.Add(touch);
        }
        return new PolisLiveEvent("tool-start")
        {
            At = now,
            Id = key,
            Tool = entry.Name,
            Category = PolisToolCategories.Slug(category),
            Place = place,
            Path = path,
            Building = building,
            Agents = category == PolisActionCategory.SubAgents ? CountTasks(entry.Arguments) : null
        };
    }

    private PolisLiveEvent EndOf(ToolCallEntry entry, string key, DateTimeOffset now)
    {
        // 工具做完：它碰过的地方的时间窗收口（之后 5 秒的宽限期由归属判断自己加）
        if (_touchIndex.TryGetValue(key, out var index) && index < _touches.Count)
            _touches[index] = _touches[index] with { End = now };
        return new PolisLiveEvent("tool-end") { At = now, Id = key, Ok = entry.Status == ToolCallStatus.Success };
    }

    /// <summary>
    /// 工具参数里的目标 → 城内（工作区相对路径）/ 城外 / 没有地点。纯字符串运算（<see cref="PolisReplayConverter.ResolvePath"/>）；
    /// 神殿（没有工作区）里的文件操作一律算城外（设计稿 9.1）。
    /// </summary>
    private (string Place, string? Path) Resolve(ToolCallEntry entry)
    {
        var target = PolisReplayConverter.ExtractTargetPath(entry.Arguments);
        if (WorkspaceRoot == null) return (target == null ? "none" : "outside", null);
        return PolisReplayConverter.ResolvePath(target, WorkspaceRoot, HomeDirectory, ignoreCase: !OperatingSystem.IsLinux());
    }

    /// <summary>
    /// 这个会话最后一次工具调用的去处（类别 / 城内外 / 建筑）：切到这份委托时雅典娜出现在那里（设计稿 9.2）；
    /// 重启后站在她上次干活的地方（10.3 第 4 条）。没有工具调用返回 null（站在广场）。
    /// </summary>
    public PolisLiveEvent? LastPlace(IReadOnlyList<ChatMessage> messages, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var last = ToolEntries(messages).LastOrDefault();
        return last == null ? null : DescribeToolDetached(last, now);
    }

    private PolisLiveEvent DescribeToolDetached(ToolCallEntry entry, DateTimeOffset now)
    {
        var category = PolisToolCategories.ForTool(entry.Name);
        var (place, path) = Resolve(entry);
        return new PolisLiveEvent("place")
        {
            At = now,
            Tool = entry.Name,
            Category = PolisToolCategories.Slug(category),
            Place = place,
            Path = path,
            Building = place == "inside" ? PolisFixture.SiteOf(path, BuildingKeys) : null
        };
    }

    /// <summary>这一回合里写进工作区、并且成功了的文件（交付成果用），按出现的先后。</summary>
    public IReadOnlyList<(string RelativePath, string Tool)> WrittenFiles(ChatMessage bubble)
    {
        ArgumentNullException.ThrowIfNull(bubble);
        var written = new List<(string, string)>();
        if (WorkspaceRoot == null) return written;
        foreach (var entry in bubble.Segments.Where(s => s.IsToolCallGroup).SelectMany(s => s.ToolCalls))
        {
            if (entry.Status != ToolCallStatus.Success) continue;
            if (PolisToolCategories.ForTool(entry.Name) != PolisActionCategory.Write) continue;
            var (place, path) = Resolve(entry);
            if (place == "inside" && !string.IsNullOrEmpty(path)) written.Add((path, entry.Name));
        }
        return written;
    }

    private static IEnumerable<ToolCallEntry> ToolEntries(IReadOnlyList<ChatMessage> messages)
        => messages.Where(m => string.Equals(m.Role, "assistant", StringComparison.Ordinal))
            .SelectMany(m => m.Segments)
            .Where(s => s.IsToolCallGroup)
            .SelectMany(s => s.ToolCalls);

    private static IEnumerable<ChatMessageSegment> ReasoningSegments(IReadOnlyList<ChatMessage> messages)
        => messages.Where(m => string.Equals(m.Role, "assistant", StringComparison.Ordinal))
            .SelectMany(m => m.Segments)
            .Where(s => s.IsReasoning);

    private static bool IsUser(ChatMessage message)
        => string.Equals(message.Role, "user", StringComparison.Ordinal) && !message.IsHidden;

    /// <summary>可见的助手气泡（不是隐藏的工具调用载体）。</summary>
    private static bool IsAssistantBubble(ChatMessage message)
        => string.Equals(message.Role, "assistant", StringComparison.Ordinal)
           && !message.IsHidden
           && string.IsNullOrEmpty(message.ToolCallsJson);

    private static string Key(ToolCallEntry entry)
        => string.IsNullOrEmpty(entry.ToolCallId) ? "anon:" + System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(entry) : entry.ToolCallId!;

    private static int? CountTasks(string argumentsJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                   && doc.RootElement.TryGetProperty("tasks", out var tasks)
                   && tasks.ValueKind == JsonValueKind.Array
                ? tasks.GetArrayLength()
                : null;
        }
        catch (JsonException)
        {
            return null;   // 参数坏了：侍女人数未知，页面按默认人数画
        }
    }
}
