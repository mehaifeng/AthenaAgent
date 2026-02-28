using Athena.UI.Models;
using Athena.UI.Services;
using Athena.UI.Services.Interfaces;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;

namespace Athena.UI.ViewModels;

public partial class LogsTabViewModel : ViewModelBase
{
    private readonly ILogService? _logService;

    [ObservableProperty]
    private string _searchLogText = string.Empty;

    public ObservableCollection<LogEntryViewModel> LogEntries { get; } = new();

    [ObservableProperty]
    private DateTime? _logStartTime;

    [ObservableProperty]
    private DateTime? _logEndTime;

    [ObservableProperty]
    private int _currentPage = 1;

    [ObservableProperty]
    private int _totalLogCount;

    [ObservableProperty]
    private int _totalPages;

    [ObservableProperty]
    private bool _hasPrevPage;

    [ObservableProperty]
    private bool _hasNextPage;

    public string CurrentPageInfo => $"Page {CurrentPage}/{TotalPages}";

    [ObservableProperty]
    private string _selectedLogLevel = "All";

    public ObservableCollection<string> LogLevels { get; } = new() { "All", "VERBOSE", "DEBUG", "INFORMATION", "WARNING", "ERROR", "FATAL" };

    [ObservableProperty]
    private int _selectedLogPageSize = 50;

    public ObservableCollection<int> LogPageSizes { get; } = new() { 20, 50, 100, 200 };

    public LogsTabViewModel() : this(null) { }

    public LogsTabViewModel(ILogService? logService)
    {
        _logService = logService;
        LogEndTime = DateTime.Today.AddDays(1);
        LogStartTime = DateTime.Today.AddDays(-7);
        RefreshLogsAsync().ConfigureAwait(false);
    }

    [RelayCommand]
    private async Task SearchLogsAsync() { CurrentPage = 1; await LoadLogsAsync(string.IsNullOrWhiteSpace(SearchLogText) ? null : SearchLogText.Trim()); }

    [RelayCommand]
    public async Task RefreshLogsAsync() => await LoadLogsAsync();

    [RelayCommand]
    private async Task PrevPageAsync() { if (CurrentPage > 1) { CurrentPage--; await LoadLogsAsync(); } }

    [RelayCommand]
    private async Task NextPageAsync() { if (CurrentPage < TotalPages) { CurrentPage++; await LoadLogsAsync(); } }

    [RelayCommand]
    private async Task ClearLogsAsync()
    {
        if (_logService == null) return;
        await _logService.ClearAllLogsAsync();
        LogEntries.Clear();
        TotalLogCount = 0;
        TotalPages = 0;
        HasPrevPage = false;
        HasNextPage = false;
        OnPropertyChanged(nameof(CurrentPageInfo));
    }

    [RelayCommand]
    private async Task ExportLogsAsync() { if (_logService != null) await _logService.ExportLogsAsync(LogStartTime, LogEndTime); }

    private async Task LoadLogsAsync(string? searchKeyword = null)
    {
        if (_logService == null) return;
        var result = await _logService.QueryLogsAsync(new LogQueryParams
        {
            StartTime = LogStartTime, EndTime = LogEndTime, Level = SelectedLogLevel,
            Page = CurrentPage, PageSize = SelectedLogPageSize, SearchKeyword = searchKeyword
        });
        LogEntries.Clear();
        foreach (var entry in result.Entries) LogEntries.Add(new LogEntryViewModel(entry));
        TotalLogCount = result.TotalCount;
        TotalPages = result.TotalPages;
        HasPrevPage = CurrentPage > 1;
        HasNextPage = CurrentPage < TotalPages;
        OnPropertyChanged(nameof(CurrentPageInfo));
    }
}

/// <summary>
/// 日志条目 ViewModel（用于显示）
/// </summary>
public class LogEntryViewModel
{
    private readonly LogEntry _entry;

    public LogEntryViewModel(LogEntry entry)
    {
        _entry = entry;
    }

    public DateTime Timestamp => _entry.Timestamp;
    public string Level => _entry.Level;
    public string Message => _entry.Message;
    public string? Exception => _entry.Exception;

    /// <summary>
    /// 根据日志级别返回颜色
    /// </summary>
    public Avalonia.Media.IBrush LevelColor => _entry.Level.ToUpper() switch
    {
        "VERBOSE" => Avalonia.Media.Brushes.Gray,
        "DEBUG" => Avalonia.Media.Brushes.Gray,
        "INFORMATION" => Avalonia.Media.Brushes.Green,
        "WARNING" => Avalonia.Media.Brushes.Orange,
        "ERROR" => Avalonia.Media.Brushes.Red,
        "FATAL" => Avalonia.Media.Brushes.Red,
        _ => Avalonia.Media.Brushes.White
    };
}
