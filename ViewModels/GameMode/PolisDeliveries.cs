using Athena.UI.Models;
using Athena.UI.Services.GameMode;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Athena.UI.ViewModels.GameMode;

/// <summary>
/// 从气泡已经在用的渲染模型里读两样东西（设计稿 12.3）：这一回合雅典娜碰过的地方——外部改动的归属用
/// （<see cref="PolisChanges.Attribute"/>），以及交付——气泡停止流式输出的那一刻，这一回合就成了一件成果。
/// 不给 <c>IChatService.StreamMessageAsync</c> 加回调：游戏和气泡是同一个会话的两种呈现，就该读同一份状态。
///
/// 直播拿掉之后（2026-10-13）只剩这两样。工具调用、推理、等审批都不再推给页面，雅典娜不镜像任何回合
/// （logs.db：工具中位 0.05 秒、三成调用间隔不到半秒、四成回合不调工具——没有一个时钟能让逐调用的镜像好看）。
/// 做法仍是"有变化就整体比对一遍"：上一次看到的状态记在这里，每次按当前消息列表算出新增的部分；
/// 这比逐个订阅事件更不容易漏——渲染模型的更新路径很多（流式、工具回填、压缩、撤销），这里只关心几样东西的终态。
/// </summary>
public sealed class PolisDeliveries
{
    private readonly HashSet<string> _announcedUsers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ToolCallStatus> _tools = new(StringComparer.Ordinal);
    private readonly HashSet<string> _delivered = new(StringComparer.Ordinal);
    private readonly List<PolisAthenaTouch> _touches = new();
    // 工具调用 → 它在 _touches 里的那一条（工具做完时把时间窗收口）
    private readonly Dictionary<string, int> _touchIndex = new(StringComparer.Ordinal);

    public PolisDeliveries(string? workspaceRoot, string? homeDirectory)
    {
        WorkspaceRoot = workspaceRoot;
        HomeDirectory = homeDirectory;
    }

    public string? WorkspaceRoot { get; }
    public string? HomeDirectory { get; }

    /// <summary>这一回合雅典娜碰过的地方（归属判断用，见 <see cref="PolisChanges.Attribute"/>）。</summary>
    public IReadOnlyList<PolisAthenaTouch> Touches => _touches;

    /// <summary>
    /// 切到一个会话：已有的一切都算"已经交付"，历史回合不再造成果；只有正在进行的那一回合（气泡还在流）
    /// 把它的工具记成她碰过的地方，时间窗从现在起算。
    /// </summary>
    public void Baseline(IReadOnlyList<ChatMessage> messages, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(messages);
        _announcedUsers.Clear();
        _tools.Clear();
        _delivered.Clear();
        _touches.Clear();
        _touchIndex.Clear();

        var running = messages.Count > 0 && messages[^1] is { Role: "assistant" } last && (last.IsStreaming || last.IsLoading);
        for (var i = 0; i < messages.Count; i++)
        {
            var message = messages[i];
            if (IsUser(message)) _announcedUsers.Add(message.Id);
            else if (IsAssistantBubble(message) && !(running && i == messages.Count - 1)) _delivered.Add(message.Id);
        }

        if (!running)
        {
            foreach (var entry in ToolEntries(messages)) _tools[Key(entry)] = entry.Status;
            return;
        }

        // 正在进行的这一回合：之前回合的工具都算过去了，这一回合的按当前状态记下
        foreach (var entry in ToolEntries(messages.Take(messages.Count - 1).ToList())) _tools[Key(entry)] = entry.Status;
        ObserveBubble(messages[^1], now);
    }

    /// <summary>按当前消息列表更新；返回这一次刚交付（停止流式输出）的气泡的 Id，按出现的先后。</summary>
    public IReadOnlyList<string> Observe(IReadOnlyList<ChatMessage> messages, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var delivered = new List<string>();
        foreach (var message in messages)
        {
            if (IsUser(message))
            {
                if (_announcedUsers.Add(message.Id))
                {
                    // 新的一回合：归属只看这一回合的工具
                    _touches.Clear();
                    _touchIndex.Clear();
                }
                continue;
            }
            if (!string.Equals(message.Role, "assistant", StringComparison.Ordinal)) continue;
            if (ObserveBubble(message, now)) delivered.Add(message.Id);
        }
        return delivered;
    }

    /// <summary>记下这个气泡里工具的起止；返回它是不是刚刚交付。</summary>
    private bool ObserveBubble(ChatMessage message, DateTimeOffset now)
    {
        foreach (var segment in message.Segments)
        {
            if (!segment.IsToolCallGroup) continue;
            foreach (var entry in segment.ToolCalls)
            {
                var key = Key(entry);
                if (!_tools.TryGetValue(key, out var status))
                {
                    _tools[key] = entry.Status;
                    Touch(entry, key, now);
                    if (entry.Status != ToolCallStatus.Running) Close(key, now);
                    continue;
                }
                if (status == ToolCallStatus.Running && entry.Status != ToolCallStatus.Running)
                {
                    _tools[key] = entry.Status;
                    Close(key, now);
                }
            }
        }

        // 交付：气泡停止流式输出（成功、停止、报错都算——你等的就是这一刻）
        return IsAssistantBubble(message) && !message.IsStreaming && !message.IsLoading && _delivered.Add(message.Id);
    }

    private void Touch(ToolCallEntry entry, string key, DateTimeOffset now)
    {
        PolisAthenaTouch? touch;
        if (PolisToolCategories.ForTool(entry.Name) == PolisActionCategory.SubAgents)
        {
            // 派发子代理期间，黄金侍女替她跑腿：整座城都算她碰过的
            touch = new PolisAthenaTouch(null, now, null, WholeWorkspace: true);
        }
        else
        {
            var (place, path) = Resolve(entry);
            touch = place == "inside" ? new PolisAthenaTouch(path, now, null) : null;
        }
        if (touch == null) return;
        _touchIndex[key] = _touches.Count;
        _touches.Add(touch);
    }

    /// <summary>工具做完：它碰过的地方的时间窗收口（之后 5 秒的宽限期由归属判断自己加）。</summary>
    private void Close(string key, DateTimeOffset now)
    {
        if (_touchIndex.TryGetValue(key, out var index) && index < _touches.Count)
            _touches[index] = _touches[index] with { End = now };
    }

    /// <summary>
    /// 工具参数里的目标 → 城内（工作区相对路径）/ 城外 / 没有地点（<see cref="PolisToolTargets.ResolvePath"/>）；
    /// 神殿（没有工作区）里的文件操作一律算城外（设计稿 9.1）。
    /// </summary>
    private (string Place, string? Path) Resolve(ToolCallEntry entry)
    {
        var target = PolisToolTargets.ExtractTargetPath(entry.Arguments);
        if (WorkspaceRoot == null) return (target == null ? "none" : "outside", null);
        return PolisToolTargets.ResolvePath(target, WorkspaceRoot, HomeDirectory, ignoreCase: !OperatingSystem.IsLinux());
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

    private static bool IsUser(ChatMessage message)
        => string.Equals(message.Role, "user", StringComparison.Ordinal) && !message.IsHidden;

    /// <summary>可见的助手气泡（不是隐藏的工具调用载体）。</summary>
    private static bool IsAssistantBubble(ChatMessage message)
        => string.Equals(message.Role, "assistant", StringComparison.Ordinal)
           && !message.IsHidden
           && string.IsNullOrEmpty(message.ToolCallsJson);

    private static string Key(ToolCallEntry entry)
        => string.IsNullOrEmpty(entry.ToolCallId) ? "anon:" + System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(entry) : entry.ToolCallId!;
}
