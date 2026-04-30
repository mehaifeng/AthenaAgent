using Athena.UI.Models;
using Athena.UI.Services;
using Athena.UI.Services.Interfaces;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;

namespace Athena.UI.ViewModels;

public partial class LogsTabViewModel : ViewModelBase
{
    private readonly ILogService? _logService;
    private readonly ILogger _logger = Log.ForContext<LogsTabViewModel>();

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

    public ObservableCollection<string> LogLevels { get; } = new() { "All", "DEBUG", "INFO", "WARN", "ERROR", "FATAL" };

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
    private async Task SearchLogsAsync()
    {
        CurrentPage = 1;
        await LoadLogsAsync();
    }

    [RelayCommand]
    public async Task RefreshLogsAsync() => await LoadLogsAsync();

    [RelayCommand]
    private async Task PrevPageAsync()
    {
        if (CurrentPage > 1)
        {
            CurrentPage--;
            await LoadLogsAsync();
        }
    }

    [RelayCommand]
    private async Task NextPageAsync()
    {
        if (CurrentPage < TotalPages)
        {
            CurrentPage++;
            await LoadLogsAsync();
        }
    }

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
    private async Task ExportLogsAsync()
    {
        if (_logService == null)
        {
            return;
        }

        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
        {
            return;
        }

        var storageProvider = desktop.MainWindow?.StorageProvider;
        if (storageProvider == null)
        {
            return;
        }

        var file = await storageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "导出日志",
            SuggestedFileName = $"logs_export_{DateTime.Now:yyyyMMdd_HHmmss}.txt",
            FileTypeChoices =
            [
                new FilePickerFileType("Text File") { Patterns = ["*.txt"] },
                new FilePickerFileType("Log File") { Patterns = ["*.log"] }
            ]
        });

        if (file == null || string.IsNullOrWhiteSpace(file.Path.LocalPath))
        {
            return;
        }

        var query = BuildQuery(page: 1, pageSize: int.MaxValue);
        await _logService.ExportLogsAsync(query, file.Path.LocalPath);
    }

    [RelayCommand]
    private void CopyLogDetails(LogEntryViewModel? entry)
    {
        if (entry == null)
        {
            return;
        }

        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var clipboard = TopLevel.GetTopLevel(desktop.MainWindow)?.Clipboard;
            clipboard?.SetTextAsync(entry.DetailsText);
            _logger.Debug("Copied log details to clipboard. LogId={LogId}", entry.Id);
        }
    }

    partial void OnSelectedLogLevelChanged(string value)
    {
        CurrentPage = 1;
        _ = LoadLogsAsync();
    }

    partial void OnSelectedLogPageSizeChanged(int value)
    {
        CurrentPage = 1;
        _ = LoadLogsAsync();
    }

    private LogQueryParams BuildQuery(int? page = null, int? pageSize = null) => new()
    {
        StartTime = LogStartTime,
        EndTime = LogEndTime,
        Level = SelectedLogLevel,
        SearchKeyword = string.IsNullOrWhiteSpace(SearchLogText) ? null : SearchLogText.Trim(),
        Page = page ?? CurrentPage,
        PageSize = pageSize ?? SelectedLogPageSize
    };

    private async Task LoadLogsAsync()
    {
        if (_logService == null) return;
        var result = await _logService.QueryLogsAsync(BuildQuery());
        LogEntries.Clear();
        foreach (var entry in result.Entries) LogEntries.Add(new LogEntryViewModel(entry));
        TotalLogCount = result.TotalCount;
        TotalPages = result.TotalPages;
        if (TotalPages > 0 && CurrentPage > TotalPages)
        {
            CurrentPage = TotalPages;
        }
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
    public long Id => _entry.Id;
    public string Level => _entry.Level switch
    {
        "Information" => "INFO",
        "Debug" => "DEBUG",
        "Warning" => "WARN",
        "Error" => "ERROR",
        "Fatal" => "FATAL",
        "Verbose" => "VERBOSE",
        _ => "None"
    };
    public string Message => _entry.Message;
    public string? Exception => _entry.Exception;
    public string? Properties => _entry.Properties;
    public string DetailsText =>
        $"[{Timestamp:yyyy-MM-dd HH:mm:ss.fff}] [{Level}] {Message}" +
        (string.IsNullOrWhiteSpace(Exception) ? string.Empty : $"{Environment.NewLine}Exception: {Exception}") +
        (string.IsNullOrWhiteSpace(Properties) ? string.Empty : $"{Environment.NewLine}Properties: {Properties}");

    /// <summary>
    /// 根据日志级别返回颜色
    /// </summary>
    public Avalonia.Media.IBrush LevelColor => _entry.Level switch
    {
        "Debug" => Avalonia.Media.Brushes.Gray,
        "Information" => Avalonia.Media.Brushes.Green,
        "Warning" => Avalonia.Media.Brushes.Orange,
        "Error" => Avalonia.Media.Brushes.Red,
        "Fatal" => Avalonia.Media.Brushes.Red,
        _ => Avalonia.Media.Brushes.White
    };
}
