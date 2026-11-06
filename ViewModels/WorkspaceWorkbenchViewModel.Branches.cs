using Avalonia.Media.Imaging;
using System;
using System.IO;
using System.Linq;

namespace Athena.UI.ViewModels;

public partial class WorkspaceWorkbenchViewModel
{
    public GitBranchManagerViewModel CreateBranchManager(GitBranchDialogMode mode)
    {
        if (_workspace == null || !HasGitRepository) throw new InvalidOperationException("No repository is active.");
        var localization = _localizationService ?? throw new InvalidOperationException("Branch management requires localization.");
        var workspaceId = _workspace.Id;
        var directory = _workspace.DirectoryPath;
        string? Guard()
        {
            if (_disposed || _workspace?.Id != workspaceId || _workspace.DirectoryPath != directory)
                return localization.GetString("Git.Branches.Error.WorkspaceChanged");
            if (IsGitBusy) return localization.GetString("Git.Branches.Error.Busy");
            if (EditorTabs.Any(tab => tab.IsDirty)) return localization.GetString("Git.Branches.Error.Unsaved");
            return null;
        }
        return new GitBranchManagerViewModel(_branchService, directory, localization, Guard, async worktreeChanged =>
        {
            if (_workspace?.Id != workspaceId || _workspace.DirectoryPath != directory) return;
            if (worktreeChanged)
            {
                // An in-flight old diff must not overwrite freshly loaded branch content.
                ++_gitChangeSelectionVersion;
                _gitChangeOpenCts?.Cancel();
                SelectedGitChange = null;
                await RefreshFilesAsync();
                foreach (var tab in EditorTabs.ToList())
                {
                    if (!File.Exists(tab.FullPath) || tab.IsOffice)
                    {
                        // Office previews hold an immutable session; reopen on demand on the new branch.
                        await CloseEditorTabAsync(tab);
                        continue;
                    }
                    if (tab.IsImage)
                    {
                        tab.Image?.Dispose();
                        tab.Image = new Bitmap(tab.FullPath);
                    }
                    else if (new FileInfo(tab.FullPath).Length > MaxEditableFileSize || IsProbablyBinary(tab.FullPath))
                    {
                        tab.ReplaceFromDisk(string.Empty, File.GetLastWriteTimeUtc(tab.FullPath));
                        tab.IsBinary = true;
                        tab.Mode = WorkspaceEditorMode.Binary;
                    }
                    else
                    {
                        tab.IsBinary = false;
                        tab.ReplaceFromDisk(await File.ReadAllTextAsync(tab.FullPath), File.GetLastWriteTimeUtc(tab.FullPath));
                        if (tab.Mode == WorkspaceEditorMode.Binary) tab.Mode = WorkspaceEditorMode.Edit;
                    }
                }
                CommitMessage = string.Empty;
            }
            await RefreshRepositoryStateAsync();
            await RefreshOpenTabGitStateAsync();
        }, mode);
    }
}
