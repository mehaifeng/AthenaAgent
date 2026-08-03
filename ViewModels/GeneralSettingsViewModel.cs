using Athena.UI.Models;
using System;
using System.ComponentModel;

namespace Athena.UI.ViewModels;

public sealed class GeneralSettingsViewModel : ViewModelBase, IDisposable
{
    private bool _disposed;

    public GeneralSettingsViewModel(AppSettingsState state)
    {
        State = state;
        State.Config.PropertyChanged += OnConfigPropertyChanged;
    }

    public AppSettingsState State { get; }

    /// <summary>字号档位在 ComboBox 中的索引（0=最小 … 4=最大），映射到 Config.FontScale 字符串。</summary>
    public int FontScaleIndex
    {
        get => ConfigScaleToIndex(State.Config.FontScale);
        set
        {
            var next = IndexToConfigScale(value);
            if (!string.Equals(State.Config.FontScale, next, StringComparison.Ordinal))
                State.Config.FontScale = next;
        }
    }

    private void OnConfigPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppConfig.FontScale))
            OnPropertyChanged(nameof(FontScaleIndex));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        State.Config.PropertyChanged -= OnConfigPropertyChanged;
    }

    private static int ConfigScaleToIndex(string? scale) => scale switch
    {
        "Tiny" => 0,
        "Small" => 1,
        "Medium" => 2,
        "Large" => 3,
        "Maximum" => 4,
        _ => 2,
    };

    private static string IndexToConfigScale(int index) => index switch
    {
        0 => "Tiny",
        1 => "Small",
        2 => "Medium",
        3 => "Large",
        4 => "Maximum",
        _ => "Medium",
    };
}
