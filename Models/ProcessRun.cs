using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Athena.UI.Models;

/// <summary>过程段摘要的计数。不可变：换一个实例就是一次通知，摘要转换器只看它。</summary>
public sealed record ProcessRunStats(
    int Reasoning,
    int Tools,
    int Read,
    int Search,
    int Write,
    int Command,
    int Other,
    int Failed,
    int Running)
{
    public static ProcessRunStats Empty { get; } = new(0, 0, 0, 0, 0, 0, 0, 0, 0);
}

/// <summary>
/// 「过程段」：助手气泡里夹在两段正文之间的一串连续过程行（每个思考段一行、每个工具调用一行）。
/// 它是 <see cref="ChatMessage.Segments"/> 的显示投影，不是数据——不落盘，段序也不因它改变。
///
/// 工具轮次远多于正文时，过程行会把气泡撑到几屏高。折叠规则（都是常量，没有开关）：
/// <list type="bullet">
/// <item>进行中（气泡还在流式、且这是最后一段）：超过 <see cref="LiveWindowRows"/> 行时，
/// 只显示摘要行 + 最近 <see cref="LiveWindowRows"/> 行。过程区的高度因此有上限，跑 5 轮和 50 轮一样高，
/// 而当前这一步和刚完成的几步始终看得见。</item>
/// <item>已结束（下一段正文开始，或整轮结束）：不少于 <see cref="FoldThresholdRows"/> 行时只剩摘要行。
/// 两行折成一行几乎不省空间，反而多一次点击，所以更短的不折。</item>
/// <item>用户的操作优先：点开了摘要就全量显示，结束时也不再折回去；亲手点过的行不会被窗口挤走。</item>
/// </list>
/// 被挤出视野的行不实例化（<see cref="VisibleRows"/> 里根本没有它），这是它对渲染预算的贡献：
/// 消息列表不虚拟化，一个 40 行的过程段收成一行，就是少布局 39 行。
/// </summary>
public sealed partial class ProcessRun : ObservableObject
{
    /// <summary>进行中的过程段在摘要行之下保留的行数。</summary>
    public const int LiveWindowRows = 3;

    /// <summary>已结束的过程段至少这么多行才折叠。</summary>
    public const int FoldThresholdRows = 3;

    private readonly List<ChatMessageSegment> _segments = new();

    /// <summary>按段序展开的全部过程行：思考段本身，或工具组里的每个 <see cref="ToolCallEntry"/>。</summary>
    private readonly List<object> _rows = new();

    private readonly HashSet<object> _subscribedRows = new(ReferenceEqualityComparer.Instance);

    /// <summary>用户亲手点过的行。只在用户收起整段时清空。</summary>
    private readonly HashSet<object> _pinnedRows = new(ReferenceEqualityComparer.Instance);

    /// <summary>当前真正渲染的行，总是 <see cref="_rows"/> 的一个保序子序列。</summary>
    public ObservableCollection<object> VisibleRows { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFoldable))]
    [NotifyPropertyChangedFor(nameof(Header))]
    [NotifyPropertyChangedFor(nameof(StatusIconKey))]
    [NotifyPropertyChangedFor(nameof(IsRunning))]
    [NotifyPropertyChangedFor(nameof(IsSucceeded))]
    private bool _isOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Header))]
    [NotifyPropertyChangedFor(nameof(ChevronIconKey))]
    private bool _isExpanded;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusIconKey))]
    [NotifyPropertyChangedFor(nameof(IsRunning))]
    [NotifyPropertyChangedFor(nameof(HasFailures))]
    [NotifyPropertyChangedFor(nameof(IsSucceeded))]
    private ProcessRunStats _stats = ProcessRunStats.Empty;

    public IReadOnlyList<ChatMessageSegment> Segments => _segments;

    public int RowCount => _rows.Count;

    /// <summary>这一段有没有东西可折。决定摘要行是否出现，也决定气泡级「展开全部过程」管不管它。</summary>
    public bool IsFoldable => IsOpen ? _rows.Count > LiveWindowRows : _rows.Count >= FoldThresholdRows;

    /// <summary>摘要行的内容；null 时不实例化摘要行。</summary>
    public ProcessRun? Header => IsFoldable || IsExpanded ? this : null;

    public bool IsRunning => IsOpen && Stats.Running > 0;

    public bool HasFailures => Stats.Failed > 0;

    public bool IsSucceeded => !IsRunning && !HasFailures;

    public string StatusIconKey => IsRunning
        ? "AthenaIconLoading"
        : HasFailures ? "AthenaIconFailure" : "AthenaIconSuccess";

    public string ChevronIconKey => IsExpanded ? "AthenaIconChevronDown" : "AthenaIconChevronRight";

    [RelayCommand]
    private void ToggleExpanded() => SetExpanded(!IsExpanded);

    /// <summary>用户收起整段时连同钉住的行一起收：「收起」点下去还剩几行在外面，读起来像没收干净。</summary>
    public void SetExpanded(bool expanded)
    {
        if (!expanded)
        {
            _pinnedRows.Clear();
        }

        if (IsExpanded == expanded)
        {
            SyncVisibleRows();
            return;
        }

        IsExpanded = expanded;
    }

    partial void OnIsOpenChanged(bool value) => SyncVisibleRows();

    partial void OnIsExpandedChanged(bool value) => SyncVisibleRows();

    internal void Add(ChatMessageSegment segment)
    {
        _segments.Add(segment);
        if (segment.IsToolCallGroup)
        {
            // 工具组先入段、调用项后追加（AppendToolCallGroup），所以必须听组内集合。
            segment.ToolCalls.CollectionChanged += OnToolCallsChanged;
        }

        RebuildRows();
    }

    /// <summary>投影整体重建时调用，断开对段与行的订阅。</summary>
    internal void Detach()
    {
        foreach (var segment in _segments.Where(segment => segment.IsToolCallGroup))
        {
            segment.ToolCalls.CollectionChanged -= OnToolCallsChanged;
        }

        foreach (var row in _subscribedRows)
        {
            ((INotifyPropertyChanged)row).PropertyChanged -= OnRowPropertyChanged;
        }

        _subscribedRows.Clear();
    }

    private void OnToolCallsChanged(object? sender, NotifyCollectionChangedEventArgs e) => RebuildRows();

    private void RebuildRows()
    {
        var wasFoldable = IsFoldable;
        _rows.Clear();
        foreach (var segment in _segments)
        {
            if (segment.IsReasoning)
            {
                _rows.Add(segment);
            }
            else if (segment.IsToolCallGroup)
            {
                _rows.AddRange(segment.ToolCalls);
            }
        }

        var current = new HashSet<object>(_rows, ReferenceEqualityComparer.Instance);
        foreach (var stale in _subscribedRows.Where(row => !current.Contains(row)).ToList())
        {
            ((INotifyPropertyChanged)stale).PropertyChanged -= OnRowPropertyChanged;
            _subscribedRows.Remove(stale);
            _pinnedRows.Remove(stale);
        }

        foreach (var row in _rows)
        {
            if (_subscribedRows.Add(row))
            {
                ((INotifyPropertyChanged)row).PropertyChanged += OnRowPropertyChanged;
            }
        }

        RecomputeStats();
        OnPropertyChanged(nameof(RowCount));
        if (wasFoldable != IsFoldable)
        {
            OnPropertyChanged(nameof(IsFoldable));
            OnPropertyChanged(nameof(Header));
        }

        SyncVisibleRows();
    }

    private void OnRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ToolCallEntry.Status):
                RecomputeStats();
                break;
            case nameof(ToolCallEntry.UserToggled) when sender is ToolCallEntry { UserToggled: true }:
            case nameof(ChatMessageSegment.UserToggled) when sender is ChatMessageSegment { UserToggled: true }:
                if (_pinnedRows.Add(sender))
                {
                    SyncVisibleRows();
                }

                break;
        }
    }

    private void RecomputeStats()
    {
        int reasoning = 0, tools = 0, read = 0, search = 0, write = 0, command = 0, other = 0, failed = 0, running = 0;
        foreach (var row in _rows)
        {
            if (row is not ToolCallEntry entry)
            {
                reasoning++;
                continue;
            }

            tools++;
            switch (ToolCallDisplay.Category(entry.Name))
            {
                case ToolCallCategory.Read: read++; break;
                case ToolCallCategory.Search: search++; break;
                case ToolCallCategory.Write: write++; break;
                case ToolCallCategory.Command: command++; break;
                default: other++; break;
            }

            if (entry.IsFailed) failed++;
            else if (entry.IsRunning) running++;
        }

        var stats = new ProcessRunStats(reasoning, tools, read, search, write, command, other, failed, running);
        if (stats != Stats)
        {
            Stats = stats;
        }
    }

    private List<object> DesiredRows()
    {
        if (IsExpanded || !IsFoldable)
        {
            return _rows.ToList();
        }

        var windowStart = IsOpen ? Math.Max(0, _rows.Count - LiveWindowRows) : _rows.Count;
        var desired = new List<object>();
        for (var index = 0; index < _rows.Count; index++)
        {
            if (index >= windowStart || _pinnedRows.Contains(_rows[index]))
            {
                desired.Add(_rows[index]);
            }
        }

        return desired;
    }

    /// <summary>
    /// 把 <see cref="VisibleRows"/> 调成目标序列，只做增删、不整体替换：
    /// 整体替换会让每一行都重新实例化，窗口每滑一格就重建三行，正在展开的详情也会被打断。
    /// </summary>
    private void SyncVisibleRows()
    {
        var desired = DesiredRows();
        var desiredSet = new HashSet<object>(desired, ReferenceEqualityComparer.Instance);

        for (var index = VisibleRows.Count - 1; index >= 0; index--)
        {
            if (!desiredSet.Contains(VisibleRows[index]))
            {
                VisibleRows.RemoveAt(index);
            }
        }

        for (var index = 0; index < desired.Count; index++)
        {
            if (index >= VisibleRows.Count || !ReferenceEquals(VisibleRows[index], desired[index]))
            {
                VisibleRows.Insert(index, desired[index]);
            }
        }

        // 两边都是 _rows 的保序子序列时上面已经对齐；行序被外部改过（理论上不会）才会走到这里。
        if (VisibleRows.Count != desired.Count)
        {
            VisibleRows.Clear();
            foreach (var row in desired)
            {
                VisibleRows.Add(row);
            }
        }
    }
}
