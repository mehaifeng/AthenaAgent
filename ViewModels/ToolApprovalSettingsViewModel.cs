using Athena.UI.Models;
using Athena.UI.Services.Interfaces;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;

namespace Athena.UI.ViewModels;

public sealed partial class ToolApprovalSettingsViewModel : ViewModelBase, IDisposable
{
    private AppConfig _observedConfig;
    private readonly ILocalizationService? _localizationService;
    private bool _disposed;

    public ToolApprovalSettingsViewModel(
        AppSettingsState state,
        ILocalizationService? localizationService = null)
    {
        State = state;
        _localizationService = localizationService;
        Modes =
        [
            new(ToolApprovalMode.Balanced,
                "Settings.Approval.Balanced.Title", "Balanced",
                "Settings.Approval.Balanced.Description", "Confirm writes, deletes, and terminal commands.",
                localizationService),
            new(ToolApprovalMode.Strict,
                "Settings.Approval.Strict.Title", "Strict",
                "Settings.Approval.Strict.Description", "Confirm every tool call.",
                localizationService),
            new(ToolApprovalMode.Automatic,
                "Settings.Approval.Automatic.Title", "Automatic",
                "Settings.Approval.Automatic.Description", "Let the approval model evaluate undecided calls.",
                localizationService),
            new(ToolApprovalMode.Off,
                "Settings.Approval.Off.Title", "Off",
                "Settings.Approval.Off.Description", "Allow all tool calls without confirmation.",
                localizationService)
        ];
        _selectedMode = FindMode(State.Config.ToolApprovalMode);
        _observedConfig = State.Config;
        State.PropertyChanged += OnStatePropertyChanged;
        _observedConfig.PropertyChanged += OnConfigPropertyChanged;
        if (_localizationService != null)
            _localizationService.LanguageChanged += OnLanguageChanged;
    }

    public AppSettingsState State { get; }

    public IReadOnlyList<ToolApprovalModeOption> Modes { get; }

    [ObservableProperty]
    private ToolApprovalModeOption _selectedMode;

    partial void OnSelectedModeChanged(ToolApprovalModeOption value)
    {
        if (State.Config.ToolApprovalMode != value.Mode)
            State.Config.ToolApprovalMode = value.Mode;
    }

    [RelayCommand]
    private async Task RevokeAutoAllowedToolAsync(string? tool)
    {
        if (!string.IsNullOrEmpty(tool) && State.Config.AutoAllowedTools.Remove(tool))
            await State.SaveNowAsync();
    }

    [RelayCommand]
    private async Task RevokeTerminalAllowlistAsync(string? command)
    {
        if (!string.IsNullOrEmpty(command) && State.Config.TerminalAllowlist.Remove(command))
            await State.SaveNowAsync();
    }

    private ToolApprovalModeOption FindMode(ToolApprovalMode mode) =>
        Modes.First(option => option.Mode == mode);

    private void OnStatePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(AppSettingsState.Config)) return;
        _observedConfig.PropertyChanged -= OnConfigPropertyChanged;
        _observedConfig = State.Config;
        _observedConfig.PropertyChanged += OnConfigPropertyChanged;
        SelectedMode = FindMode(State.Config.ToolApprovalMode);
    }

    private void OnConfigPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppConfig.ToolApprovalMode))
            SelectedMode = FindMode(State.Config.ToolApprovalMode);
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        foreach (var mode in Modes)
            mode.RefreshText(_localizationService);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_localizationService != null)
            _localizationService.LanguageChanged -= OnLanguageChanged;
        State.PropertyChanged -= OnStatePropertyChanged;
        _observedConfig.PropertyChanged -= OnConfigPropertyChanged;
    }
}

public sealed partial class ToolApprovalModeOption : ViewModelBase
{
    private readonly string _titleKey;
    private readonly string _fallbackTitle;
    private readonly string _descriptionKey;
    private readonly string _fallbackDescription;

    public ToolApprovalModeOption(
        ToolApprovalMode mode,
        string titleKey,
        string fallbackTitle,
        string descriptionKey,
        string fallbackDescription,
        ILocalizationService? localizationService)
    {
        Mode = mode;
        _titleKey = titleKey;
        _fallbackTitle = fallbackTitle;
        _descriptionKey = descriptionKey;
        _fallbackDescription = fallbackDescription;
        _title = fallbackTitle;
        _description = fallbackDescription;
        RefreshText(localizationService);
    }

    public ToolApprovalMode Mode { get; }

    [ObservableProperty]
    private string _title;

    [ObservableProperty]
    private string _description;

    public void RefreshText(ILocalizationService? localizationService)
    {
        Title = localizationService?.GetString(_titleKey, _fallbackTitle) ?? _fallbackTitle;
        Description = localizationService?.GetString(_descriptionKey, _fallbackDescription) ?? _fallbackDescription;
    }
}
