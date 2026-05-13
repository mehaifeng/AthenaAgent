using Athena.UI.Models;
using Athena.UI.Services.Interfaces;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;

namespace Athena.UI.ViewModels;

/// <summary>
/// 对话历史标签页 ViewModel
/// </summary>
public partial class HistoryTabViewModel : ViewModelBase
{
    private readonly IConversationHistoryService _historyService;
    private readonly IConversationArchiveService? _archiveService;
    private readonly ILocalizationService? _localizationService;
    private readonly Dictionary<string, ConversationHistoryItem> _pendingArchiveItems = new(StringComparer.Ordinal);
    private List<ConversationHistoryItem> _persistedHistoryItems = [];

    /// <summary>
    /// 历史记录列表
    /// </summary>
    public ObservableCollection<ConversationHistoryItem> HistoryItems { get; } = new();

    /// <summary>
    /// 选中的历史条目
    /// </summary>
    [ObservableProperty]
    private ConversationHistoryItem? _selectedItem;

    /// <summary>
    /// 是否正在加载
    /// </summary>
    [ObservableProperty]
    private bool _isLoading;

    /// <summary>
    /// 是否有选中的条目
    /// </summary>
    public bool HasSelectedItem => SelectedItem != null;

    /// <summary>
    /// 加载历史对话请求事件
    /// </summary>
    public event EventHandler<ConversationHistoryItem>? LoadHistoryRequested;

    /// <summary>
    /// 历史对话已删除事件
    /// </summary>
    public event EventHandler<string>? HistoryDeleted;

    /// <summary>
    /// 构造函数
    /// </summary>
    public HistoryTabViewModel(
        IConversationHistoryService historyService,
        IConversationArchiveService? archiveService = null,
        ILocalizationService? localizationService = null)
    {
        _historyService = historyService;
        _archiveService = archiveService;
        _localizationService = localizationService;

        if (_archiveService != null)
        {
            _archiveService.ArchiveStaged += OnArchiveStaged;
            _archiveService.ArchiveCompleted += OnArchiveCompleted;
            _archiveService.ArchiveFailed += OnArchiveFailed;
        }

        Log.Information("HistoryTabViewModel 初始化");
    }

    /// <summary>
    /// 加载历史列表
    /// </summary>
    public async Task LoadHistoryAsync()
    {
        IsLoading = true;
        try
        {
            var selectedId = SelectedItem?.Id;
            var items = await _historyService.LoadAllAsync();
            _persistedHistoryItems = items;
            RebuildHistoryItems(selectedId);
            Log.Information("历史列表加载完成，共 {Count} 条", items.Count);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "加载历史列表失败");
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// 选择并加载历史条目
    /// </summary>
    [RelayCommand]
    private void SelectHistory(ConversationHistoryItem? item)
    {
        if (item == null || item.IsArchivePlaceholder)
            return;

        SelectedItem = item;
        OnPropertyChanged(nameof(HasSelectedItem));
        LoadHistoryRequested?.Invoke(this, item);
        Log.Information("加载历史对话: {Id}", item.Id);
    }

    /// <summary>
    /// 删除历史条目
    /// </summary>
    [RelayCommand]
    private async Task DeleteHistoryAsync(ConversationHistoryItem? item)
    {
        if (item == null || item.IsArchivePlaceholder)
            return;

        try
        {
            var id = item.Id;
            await _historyService.DeleteAsync(id);
            HistoryItems.Remove(item);

            if (SelectedItem == item)
            {
                SelectedItem = null;
                OnPropertyChanged(nameof(HasSelectedItem));
            }

            Log.Information("删除历史条目: {Id}", id);
            HistoryDeleted?.Invoke(this, id);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "删除历史条目失败: {Id}", item.Id);
        }
    }

    /// <summary>
    /// 刷新历史列表
    /// </summary>
    [RelayCommand]
    private async Task RefreshAsync()
    {
        await LoadHistoryAsync();
    }

    private void OnArchiveCompleted(object? sender, ConversationArchiveResultEventArgs e)
    {
        Dispatcher.UIThread.Post(async () =>
        {
            _pendingArchiveItems.Remove(e.StagedFilePath);
            await LoadHistoryAsync();
        });
    }

    private void OnArchiveStaged(object? sender, ConversationArchiveResultEventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            _pendingArchiveItems[e.StagedFilePath] = CreatePendingArchiveItem(e.Snapshot, e.StagedFilePath);
            RebuildHistoryItems(SelectedItem?.Id);
        });
    }

    private void OnArchiveFailed(object? sender, ConversationArchiveResultEventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_pendingArchiveItems.Remove(e.StagedFilePath))
            {
                RebuildHistoryItems(SelectedItem?.Id);
            }
        });
    }

    private void RebuildHistoryItems(string? selectedId)
    {
        var pendingDisplayItems = _pendingArchiveItems.Values
            .GroupBy(item => item.Id, StringComparer.Ordinal)
            .Select(group => group.OrderByDescending(item => item.UpdatedAt).First())
            .ToList();

        var pendingPersistedIds = pendingDisplayItems
            .Where(item => !item.Id.StartsWith("pending:", StringComparison.Ordinal))
            .Select(item => item.Id)
            .ToHashSet(StringComparer.Ordinal);

        var mergedItems = pendingDisplayItems
            .Concat(_persistedHistoryItems.Where(item => !pendingPersistedIds.Contains(item.Id)))
            .OrderByDescending(item => item.UpdatedAt)
            .ToList();

        HistoryItems.Clear();
        foreach (var item in mergedItems)
        {
            HistoryItems.Add(item);
        }

        SelectedItem = string.IsNullOrWhiteSpace(selectedId)
            ? null
            : HistoryItems.FirstOrDefault(item => string.Equals(item.Id, selectedId, StringComparison.Ordinal));

        OnPropertyChanged(nameof(HasSelectedItem));
    }

    private ConversationHistoryItem CreatePendingArchiveItem(ConversationArchiveSnapshot snapshot, string stagedFilePath)
    {
        var existingItem = !string.IsNullOrWhiteSpace(snapshot.HistoryId)
            ? _persistedHistoryItems.FirstOrDefault(item => string.Equals(item.Id, snapshot.HistoryId, StringComparison.Ordinal))
            : null;
        var displayId = existingItem?.Id
            ?? snapshot.HistoryId
            ?? $"pending:{Path.GetFileNameWithoutExtension(stagedFilePath)}";

        return new ConversationHistoryItem
        {
            Id = displayId,
            Summary = existingItem?.Summary ?? GetString("History.PendingSummary", "Summarizing conversation..."),
            ContextSummary = existingItem?.ContextSummary,
            MessageCount = snapshot.Messages.Count,
            CreatedAt = existingItem?.CreatedAt ?? snapshot.Messages.FirstOrDefault()?.Timestamp ?? snapshot.CapturedAt,
            UpdatedAt = snapshot.CapturedAt,
            IsArchivePlaceholder = true,
            ArchiveStagePath = stagedFilePath,
            ArchiveStatusText = GetString("History.PendingStatus", "正在总结中")
        };
    }

    private string GetString(string key, string defaultValue)
    {
        return _localizationService?.GetString(key, defaultValue) ?? defaultValue;
    }
}
