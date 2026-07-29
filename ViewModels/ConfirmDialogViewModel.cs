using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;

namespace Athena.UI.ViewModels;

/// <summary>
/// 确认对话框 ViewModel
/// </summary>
public partial class ConfirmDialogViewModel : ObservableObject
{
    [ObservableProperty]
    private string _title = "确认";

    [ObservableProperty]
    private string _message = "确定要执行此操作吗？";

    [ObservableProperty]
    private string _confirmText = "是";

    [ObservableProperty]
    private string _cancelText = "否";

    [ObservableProperty]
    private bool _dontAskAgain;

    [ObservableProperty]
    private bool _showDontAskAgain = true;

    [ObservableProperty]
    private bool _isLoading;

    /// <summary>
    /// 用户选择结果
    /// </summary>
    public bool? Result { get; private set; }

    /// <summary>
    /// 是否不再询问（用户勾选后返回true）
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
