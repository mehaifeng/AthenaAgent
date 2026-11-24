using Athena.UI.Models;
using Athena.UI.Services;
using Athena.UI.Services.Interfaces;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;

namespace Athena.UI.ViewModels;

/// <summary>Projects the app-wide approval setting into both the settings page and composer.</summary>
public sealed partial class ToolApprovalModePickerViewModel : ViewModelBase, IDisposable
{
    private readonly AppConfigurationSession _configuration;
    private readonly IUserInteractionService _interaction;
    private readonly ILocalizationService _localization;
    private AppConfig _observedConfig;
    private ToolApprovalModeOption _selectedMode;
    private bool _disposed;

    public ToolApprovalModePickerViewModel(
        AppConfigurationSession configuration,
        IUserInteractionService interaction,
        ILocalizationService localization)
    {
        _configuration = configuration;
        _interaction = interaction;
        _localization = localization;
        Modes =
        [
            new(ToolApprovalMode.Balanced, "Balanced", "Confirm writes, deletes, and terminal commands.", localization, SelectCommand),
            new(ToolApprovalMode.Strict, "Strict", "Confirm every tool call.", localization, SelectCommand),
            new(ToolApprovalMode.Automatic, "Automatic", "Review each tool call. Ask in the originating conversation when confirmation is needed.", localization, SelectCommand),
            new(ToolApprovalMode.Off, "Allow all", "Allow all tool calls without confirmation.", localization, SelectCommand)
        ];
        _observedConfig = configuration.Current;
        _selectedMode = FindMode();
        _selectedMode.IsSelected = true;
        _observedConfig.PropertyChanged += OnConfigPropertyChanged;
        _configuration.CurrentChanged += OnCurrentChanged;
        _localization.LanguageChanged += OnLanguageChanged;
    }

    public IReadOnlyList<ToolApprovalModeOption> Modes { get; }
    public ToolApprovalModeOption SelectedMode => _selectedMode;
    public string AccessibleName => $"{L("Settings.Approval.Title", "Tool approval")} · {SelectedMode.Title}";

    [RelayCommand]
    private async Task SelectAsync(ToolApprovalModeOption? option)
    {
        if (_disposed || option == null || !Modes.Contains(option)
            || _configuration.Current.ToolApprovalMode == option.Mode)
            return;

        try
        {
            if (option.IsAllowAll && !await _interaction.ConfirmAsync(
                    L("Settings.Approval.Off.ConfirmTitle", "Enable Allow all?"),
                    L("Settings.Approval.Off.ConfirmMessage", "Allow all skips approval for tool calls such as writing files, deleting data, and running terminal commands. This setting applies to every conversation."),
                    L("Settings.Approval.Off.ConfirmAction", "Enable Allow all"),
                    L("Common.Cancel", "Cancel"),
                    showDontAskAgain: false))
                return;

            if (!_disposed) _configuration.Current.ToolApprovalMode = option.Mode;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not switch the tool approval mode to {Mode}", option.Mode);
        }
    }

    private string L(string key, string fallback) => _localization.GetString(key, fallback);

    private ToolApprovalModeOption FindMode() => Modes.FirstOrDefault(
        option => option.Mode == _configuration.Current.ToolApprovalMode) ?? Modes[0];

    private void RefreshSelection()
    {
        if (_disposed) return;
        var selected = FindMode();
        if (ReferenceEquals(_selectedMode, selected)) return;
        _selectedMode.IsSelected = false;
        selected.IsSelected = true;
        _selectedMode = selected;
        OnPropertyChanged(nameof(SelectedMode));
        OnPropertyChanged(nameof(AccessibleName));
    }

    private void OnCurrentChanged(object? sender, AppConfig config)
    {
        _observedConfig.PropertyChanged -= OnConfigPropertyChanged;
        _observedConfig = _configuration.Current;
        _observedConfig.PropertyChanged += OnConfigPropertyChanged;
        RefreshSelection();
    }

    private void OnConfigPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(AppConfig.ToolApprovalMode)) return;
        if (Dispatcher.UIThread.CheckAccess()) RefreshSelection();
        else Dispatcher.UIThread.Post(RefreshSelection);
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        foreach (var mode in Modes) mode.RefreshText(_localization);
        OnPropertyChanged(nameof(AccessibleName));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _observedConfig.PropertyChanged -= OnConfigPropertyChanged;
        _configuration.CurrentChanged -= OnCurrentChanged;
        _localization.LanguageChanged -= OnLanguageChanged;
    }
}

public sealed partial class ToolApprovalModeOption : ViewModelBase
{
    private readonly string _fallbackTitle;
    private readonly string _fallbackDescription;

    internal ToolApprovalModeOption(
        ToolApprovalMode mode,
        string fallbackTitle,
        string fallbackDescription,
        ILocalizationService localization,
        IAsyncRelayCommand<ToolApprovalModeOption?> selectCommand)
    {
        Mode = mode;
        SelectCommand = selectCommand;
        _fallbackTitle = _title = fallbackTitle;
        _fallbackDescription = _description = fallbackDescription;
        RefreshText(localization);
    }

    public ToolApprovalMode Mode { get; }
    public bool IsAllowAll => Mode == ToolApprovalMode.Off;
    public IAsyncRelayCommand<ToolApprovalModeOption?> SelectCommand { get; }

    [ObservableProperty] private string _title;
    [ObservableProperty] private string _description;
    [ObservableProperty] private bool _isSelected;

    internal void RefreshText(ILocalizationService localization)
    {
        Title = localization.GetString($"Settings.Approval.{Mode}.Title", _fallbackTitle);
        Description = localization.GetString($"Settings.Approval.{Mode}.Description", _fallbackDescription);
    }
}
