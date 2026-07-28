using Athena.UI.Models;
using Athena.UI.Services;
using Athena.UI.Services.Interfaces;
using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;

namespace Athena.UI.ViewModels;

public partial class LogsViewModel : ViewModelBase
{
    private readonly ILogService? _logService;
    private readonly ILocalizationService? _localizationService;
    private readonly IUserInteractionService? _userInteractionService;
    private readonly ILogger _logger = Log.ForContext<LogsViewModel>();

    [ObservableProperty]
    private string _searchLogText = string.Empty;

    public ObservableCollection<LogEntryViewModel> LogEntries { get; } = new();

    /// <summary>
    /// 时间筛选档位：0=全部时间，1=今天，2=近7天，3=近30天，4=自定义范围（与历史页一致）
    /// </summary>
    [ObservableProperty]
    private int _timeFilterIndex = 2;

    /// <summary>自定义范围起点（按整天，含当天）</summary>
    [ObservableProperty]
    private DateTime? _customStartDate;

    /// <summary>自定义范围终点（按整天，含当天）</summary>
    [ObservableProperty]
    private DateTime? _customEndDate;

    /// <summary>当前是否为"自定义范围"档（控制日期选择行的展开）</summary>
    public bool IsCustomRange => TimeFilterIndex == 4;

    /// <summary>自定义起止倒置时视为无效：不过滤，仅用于 UI 警示</summary>
    public bool IsCustomRangeInvalid =>
        IsCustomRange
        && CustomStartDate.HasValue
        && CustomEndDate.HasValue
        && CustomStartDate.Value.Date > CustomEndDate.Value.Date;

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

    public LogsViewModel() : this(null, null, null) { }

    public LogsViewModel(ILogService? logService, ILocalizationService? localizationService = null, IUserInteractionService? userInteractionService = null)
    {
        _logService = logService;
        _localizationService = localizationService;
        _userInteractionService = userInteractionService;
        App.ThemeChanged += OnThemeChanged;
        RefreshLogsAsync().ConfigureAwait(false);
    }

    private void OnThemeChanged(string _)
    {
        foreach (var entry in LogEntries)
        {
            entry.RefreshTheme();
        }
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

        if (_userInteractionService == null) return;
        var file = await _userInteractionService.PickSaveFileAsync(
            _localizationService?.GetString("Logs.ExportPickerTitle", "Export logs") ?? "Export logs",
            $"logs_export_{DateTime.Now:yyyyMMdd_HHmmss}.txt",
            "Log files", ["*.txt", "*.log"]);
        if (string.IsNullOrWhiteSpace(file)) return;

        var query = BuildQuery(page: 1, pageSize: int.MaxValue);
        await _logService.ExportLogsAsync(query, file);
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

    partial void OnTimeFilterIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsCustomRange));
        OnPropertyChanged(nameof(IsCustomRangeInvalid));
        CurrentPage = 1;
        _ = LoadLogsAsync();
    }

    partial void OnCustomStartDateChanged(DateTime? value)
    {
        OnPropertyChanged(nameof(IsCustomRangeInvalid));
        CurrentPage = 1;
        _ = LoadLogsAsync();
    }

    partial void OnCustomEndDateChanged(DateTime? value)
    {
        OnPropertyChanged(nameof(IsCustomRangeInvalid));
        CurrentPage = 1;
        _ = LoadLogsAsync();
    }

    /// <summary>清空自定义日期并回到"全部时间"</summary>
    [RelayCommand]
    private void ClearCustomRange()
    {
        CustomStartDate = null;
        CustomEndDate = null;
        TimeFilterIndex = 0;
    }

    /// <summary>
    /// 当前时间档对应的 [起, 止) 窗口；快捷档为滚动窗口（从所在日 00:00 起），
    /// 自定义档起止倒置时视为无效，不做时间过滤。
    /// </summary>
    private (DateTime? Start, DateTime? End) GetActiveTimeWindow()
    {
        var today = DateTime.Today;
        return TimeFilterIndex switch
        {
            1 => (today, null),
            2 => (today.AddDays(-7), null),
            3 => (today.AddDays(-30), null),
            4 when !IsCustomRangeInvalid => (
                CustomStartDate?.Date,
                CustomEndDate?.Date.AddDays(1)),
            _ => (null, null)
        };
    }

    partial void OnSelectedLogPageSizeChanged(int value)
    {
        CurrentPage = 1;
        _ = LoadLogsAsync();
    }

    private LogQueryParams BuildQuery(int? page = null, int? pageSize = null)
    {
        var (start, end) = GetActiveTimeWindow();
        return new LogQueryParams
        {
            StartTime = start,
            EndTime = end,
            Level = SelectedLogLevel,
            SearchKeyword = string.IsNullOrWhiteSpace(SearchLogText) ? null : SearchLogText.Trim(),
            Page = page ?? CurrentPage,
            PageSize = pageSize ?? SelectedLogPageSize
        };
    }

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
public class LogEntryViewModel : ObservableObject
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

    public IBrush LevelBackground => IsDarkTheme()
        ? _entry.Level switch
        {
            "Debug" => new SolidColorBrush(Color.Parse("#8A8A8A")),
            "Information" => new SolidColorBrush(Color.Parse("#B0B0B0")),
            "Warning" => new SolidColorBrush(Color.Parse("#C6C6C6")),
            "Error" => new SolidColorBrush(Color.Parse("#DFDFDF")),
            "Fatal" => new SolidColorBrush(Color.Parse("#F2F2F2")),
            _ => new SolidColorBrush(Color.Parse("#A0A0A0"))
        }
        : _entry.Level switch
        {
            "Debug" => new SolidColorBrush(Color.Parse("#D9D9D9")),
            "Information" => new SolidColorBrush(Color.Parse("#B8B8B8")),
            "Warning" => new SolidColorBrush(Color.Parse("#8E8E8E")),
            "Error" => new SolidColorBrush(Color.Parse("#5E5E5E")),
            "Fatal" => new SolidColorBrush(Color.Parse("#2E2E2E")),
            _ => new SolidColorBrush(Color.Parse("#C8C8C8"))
        };

    public IBrush LevelForeground => IsDarkTheme()
        ? Brushes.Black
        : _entry.Level switch
        {
            "Error" => Brushes.White,
            "Fatal" => Brushes.White,
            _ => Brushes.Black
        };

    public void RefreshTheme()
    {
        OnPropertyChanged(nameof(LevelBackground));
        OnPropertyChanged(nameof(LevelForeground));
    }

    private static bool IsDarkTheme() =>
        Application.Current?.RequestedThemeVariant == ThemeVariant.Dark;
}
