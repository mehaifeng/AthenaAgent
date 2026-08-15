using System;
using System.ComponentModel;
using Athena.UI.Models;
using Athena.UI.Services.Interfaces;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace Athena.UI.Controls;

/// <summary>
/// 包裹一条消息内所有工具调用的折叠容器：卡片列表用 <see cref="ToolCallStackPanel"/> 承载
/// （收起露约 3 个 + 第 4 个 peek + 底部朦胧，展开全显，带高度过渡）。
/// 本控件只负责底部触发条的文案/图标与展开-收起、单卡详情的点击切换。
/// DataContext 为工具调用组 <see cref="ChatMessageSegment"/>（Kind = ToolCallGroup）。
/// </summary>
public partial class ToolCallStackView : UserControl
{
    private ChatMessageSegment? _group;

    public ToolCallStackView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_group != null)
        {
            _group.PropertyChanged -= OnGroupPropertyChanged;
        }

        _group = DataContext as ChatMessageSegment;

        if (_group != null)
        {
            _group.PropertyChanged += OnGroupPropertyChanged;
        }

        RefreshToggleBar();
    }

    private void OnGroupPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ChatMessageSegment.IsGroupExpanded)
            || e.PropertyName == nameof(ChatMessageSegment.ToolCallCount))
        {
            RefreshToggleBar();
        }
    }

    private void OnEntryHeaderClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control control && control.DataContext is ToolCallEntry entry)
        {
            entry.IsExpanded = !entry.IsExpanded;
        }
    }

    private void OnToggleGroupClick(object? sender, RoutedEventArgs e)
    {
        if (_group == null)
        {
            return;
        }

        _group.UserToggledGroup = true;
        _group.IsGroupExpanded = !_group.IsGroupExpanded;
    }

    private void RefreshToggleBar()
    {
        if (_group == null)
        {
            return;
        }

        var expanded = _group.IsGroupExpanded;
        var iconKey = expanded ? "AthenaIconChevronUp" : "AthenaIconChevronDown";
        if (Application.Current?.TryGetResource(iconKey, null, out var geo) == true && geo is Geometry geometry)
        {
            PART_ToggleIcon.Data = geometry;
        }

        var localization = App.Services?.GetService(typeof(ILocalizationService)) as ILocalizationService;
        if (expanded)
        {
            PART_ToggleText.Text = localization?.GetString("Tool.GroupCollapse") ?? "Collapse";
        }
        else
        {
            var template = localization?.GetString("Tool.GroupExpand") ?? "Show all {0} tool calls";
            PART_ToggleText.Text = string.Format(template, _group.ToolCallCount);
        }
    }
}
