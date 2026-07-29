using Athena.UI.Models;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Athena.UI.Services.Interfaces;

/// <summary>隔离 ViewModel 与 Avalonia 窗口、对话框和文件选择器。</summary>
public interface IUserInteractionService
{
    Task<bool> ConfirmAsync(
        string title,
        string message,
        string confirmText,
        string cancelText,
        bool showDontAskAgain = true);
    Task<string?> PickFolderAsync(string title);
    Task<IReadOnlyList<string>> PickFilesAsync(string title, string displayName, IReadOnlyList<string> patterns, bool allowMultiple);
    Task<string?> PickSaveFileAsync(string title, string suggestedFileName, string displayName, IReadOnlyList<string> patterns);
    Task ShowImagePreviewAsync(ChatAttachment attachment);
}
