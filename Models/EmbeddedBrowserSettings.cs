using CommunityToolkit.Mvvm.ComponentModel;
using System;

namespace Athena.UI.Models;

public partial class EmbeddedBrowserSettings : ObservableObject
{
    public const int DefaultTaskTimeoutMinutes = 3;
    public const int MinTaskTimeoutMinutes = 1;
    public const int MaxTaskTimeoutMinutes = 60;
    public const int DefaultActionRetryCount = 1;
    public const int MinActionRetryCount = 0;
    public const int MaxActionRetryCount = 3;

    [ObservableProperty] private string _homePage = string.Empty;
    [ObservableProperty] private string _downloadDirectory = string.Empty;

    private int _taskTimeoutMinutes = DefaultTaskTimeoutMinutes;
    private int _actionRetryCount = DefaultActionRetryCount;

    public int TaskTimeoutMinutes
    {
        get => _taskTimeoutMinutes;
        set => SetProperty(ref _taskTimeoutMinutes, Math.Clamp(value, MinTaskTimeoutMinutes, MaxTaskTimeoutMinutes));
    }

    public int ActionRetryCount
    {
        get => _actionRetryCount;
        set => SetProperty(ref _actionRetryCount, Math.Clamp(value, MinActionRetryCount, MaxActionRetryCount));
    }
}
