using Athena.UI.Models;
using Athena.UI.Services.Interfaces;
using Athena.UI.ViewModels;
using Athena.UI.Views;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Athena.UI.Services.Platform;

public sealed class AvaloniaUserInteractionService : IUserInteractionService
{
    public async Task<bool> ConfirmAsync(
        string title,
        string message,
        string confirmText,
        string cancelText,
        bool showDontAskAgain = true)
    {
        var owner = GetMainWindow();
        if (owner == null) return false;
        var viewModel = new ConfirmDialogViewModel
        {
            Title = title,
            Message = message,
            ConfirmText = confirmText,
            CancelText = cancelText,
            ShowDontAskAgain = showDontAskAgain
        };
        await new ConfirmDialog(viewModel).ShowDialog(owner);
        return viewModel.Result == true;
    }

    public async Task<string?> PickFolderAsync(string title)
    {
        var storage = GetMainWindow()?.StorageProvider;
        if (storage == null) return null;
        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title });
        return folders.FirstOrDefault()?.Path.LocalPath;
    }

    public async Task<IReadOnlyList<string>> PickFilesAsync(string title, string displayName, IReadOnlyList<string> patterns, bool allowMultiple)
    {
        var storage = GetMainWindow()?.StorageProvider;
        if (storage == null) return [];
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = allowMultiple,
            FileTypeFilter = [new FilePickerFileType(displayName) { Patterns = patterns }]
        });
        return files.Select(file => file.Path.LocalPath).ToList();
    }

    public async Task<string?> PickSaveFileAsync(string title, string suggestedFileName, string displayName, IReadOnlyList<string> patterns)
    {
        var storage = GetMainWindow()?.StorageProvider;
        if (storage == null) return null;
        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedFileName,
            FileTypeChoices = [new FilePickerFileType(displayName) { Patterns = patterns }]
        });
        return file?.Path.LocalPath;
    }

    public async Task ShowImagePreviewAsync(ChatAttachment attachment)
    {
        var owner = GetMainWindow();
        if (owner != null) await new ImagePreviewWindow(attachment).ShowDialog(owner);
    }

    private static Window? GetMainWindow()
        => (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
}
