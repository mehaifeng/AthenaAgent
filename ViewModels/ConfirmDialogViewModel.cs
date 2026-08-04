using Athena.UI.Services.Interfaces;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;

namespace Athena.UI.ViewModels;

/// <summary>
/// Confirmation dialog ViewModel. Localization-aware: when the caller leaves
/// Title / Message / ConfirmText / CancelText empty, the corresponding *Display
/// property resolves a localized fallback via ILocalizationService.
/// </summary>
public partial class ConfirmDialogViewModel : ObservableObject
{
    private readonly ILocalizationService? _localization;

    public ConfirmDialogViewModel(ILocalizationService? localization = null)
    {
        _localization = localization;
    }

    [ObservableProperty]
    private string _title = string.Empty;

    [ObservableProperty]
    private string _message = string.Empty;

    [ObservableProperty]
    private string _confirmText = string.Empty;

    [ObservableProperty]
    private string _cancelText = string.Empty;

    [ObservableProperty]
    private bool _dontAskAgain;

    [ObservableProperty]
    private bool _showDontAskAgain = true;

    [ObservableProperty]
    private bool _isLoading;

    public string TitleDisplay => !string.IsNullOrEmpty(Title)
        ? Title
        : L("Dialog.Confirm.Title", "Confirm");

    public string MessageDisplay => !string.IsNullOrEmpty(Message)
        ? Message
        : L("Dialog.Confirm.Message", "Are you sure?");

    public string ConfirmTextDisplay => !string.IsNullOrEmpty(ConfirmText)
        ? ConfirmText
        : L("Dialog.Confirm.Yes", "Yes");

    public string CancelTextDisplay => !string.IsNullOrEmpty(CancelText)
        ? CancelText
        : L("Dialog.Confirm.No", "No");

    private string L(string key, string fallback)
        => _localization?.GetString(key, fallback) ?? fallback;

    partial void OnTitleChanged(string value) => OnPropertyChanged(nameof(TitleDisplay));
    partial void OnMessageChanged(string value) => OnPropertyChanged(nameof(MessageDisplay));
    partial void OnConfirmTextChanged(string value) => OnPropertyChanged(nameof(ConfirmTextDisplay));
    partial void OnCancelTextChanged(string value) => OnPropertyChanged(nameof(CancelTextDisplay));

    /// <summary>
    /// User selection result
    /// </summary>
    public bool? Result { get; private set; }

    /// <summary>
    /// Whether the user opted out of being asked again (only set together with a confirmed result)
    /// </summary>
    public bool ShouldNotAskAgain => DontAskAgain && Result == true;

    public Action? RequestClose { get; set; }

    [RelayCommand]
    private void Confirm()
    {
        Result = true;
        RequestClose?.Invoke();
    }

    [RelayCommand]
    private void Cancel()
    {
        Result = false;
        RequestClose?.Invoke();
    }
}