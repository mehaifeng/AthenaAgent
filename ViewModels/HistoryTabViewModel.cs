using Athena.UI.Models;
using Athena.UI.Services.Interfaces;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

namespace Athena.UI.ViewModels;

/// <summary>
/// 对话历史标签页 ViewModel
/// </summary>
public partial class HistoryTabViewModel : ViewModelBase
{
    private readonly IConversationHistoryService _historyService;

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
    /// 构造函数
    /// </summary>
    public HistoryTabViewModel(IConversationHistoryService historyService)
    {
        _historyService = historyService;
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
            var items = await _historyService.LoadAllAsync();
            HistoryItems.Clear();
            foreach (var item in items)
            {
                HistoryItems.Add(item);
            }
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
        if (item == null)
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
        if (item == null)
            return;

        try
        {
            await _historyService.DeleteAsync(item.Id);
            HistoryItems.Remove(item);

            if (SelectedItem == item)
            {
                SelectedItem = null;
                OnPropertyChanged(nameof(HasSelectedItem));
            }

            Log.Information("删除历史条目: {Id}", item.Id);
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
}
