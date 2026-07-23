using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Athena.UI;
using Athena.UI.Models;
using Athena.UI.Services;
using Athena.UI.Services.Interfaces;
using Athena.UI.ViewModels;
using Athena.UI.Views;

var outputPath = args.Length > 0
    ? Path.GetFullPath(args[0])
    : Path.Combine(AppContext.BaseDirectory, "main-window.png");

AppBuilder.Configure<App>()
    .UseSkia()
    .UseHeadless(new AvaloniaHeadlessPlatformOptions
    {
        UseHeadlessDrawing = false
    })
    .SetupWithoutStarting();

var mainViewModel = new MainWindowViewModel();
var conversationGroup = new WorkspaceConversationGroupViewModel(null);
conversationGroup.Conversations.Add(new ConversationSessionItemViewModel(new ChatTabViewModel(), null, null)
{
    Title = "正在整理发布说明"
});
conversationGroup.Conversations.Add(new ConversationSessionItemViewModel(new ChatTabViewModel(), null, null)
{
    Title = "比较两套工作区方案",
    ForkedFromConversationId = "parent-conversation",
    HasUnreadCompletion = true,
    IsPinned = true
});
mainViewModel.ConversationGroups.Add(conversationGroup);

var window = new MainWindow
{
    DataContext = mainViewModel,
    Width = 1440,
    Height = 900
};
window.Show();
Dispatcher.UIThread.RunJobs();

var shell = window.FindControl<Grid>("MainShellGrid") ?? throw new InvalidOperationException("Main shell was not created.");
if (shell.ColumnDefinitions.Count != 5) throw new InvalidOperationException("Main shell must contain five grid columns including splitters.");
if (shell.ColumnDefinitions[0].MinWidth < 260 || shell.ColumnDefinitions[4].MinWidth < 360)
    throw new InvalidOperationException("Side panel minimum widths are not applied.");
if (window.FindControl<ChatTabView>("ChatTabView") == null)
    throw new InvalidOperationException("Chat view is not permanently mounted in the center column.");
if (window.GetVisualDescendants().OfType<TabStrip>().Any())
    throw new InvalidOperationException("The main window must not contain a TabStrip.");
var launcherButtons = window.GetVisualDescendants().OfType<Button>()
    .Where(button => button.Classes.Contains("launcher"))
    .ToList();
if (launcherButtons.Count != 4 || launcherButtons.Any(button => Math.Abs(button.Height - 40) > 0.01))
    throw new InvalidOperationException("The four launcher buttons must have one uniform size.");
var visibleFixedFileCommands = window.GetVisualDescendants().OfType<Button>()
    .Where(button => button.IsVisible && button.Content is string text && text is "复制路径" or "绝对路径" or "复制绝对路径")
    .ToList();
if (visibleFixedFileCommands.Count != 0)
    throw new InvalidOperationException("File path commands must only appear in the file-node context menu.");
if (window.GetVisualDescendants().OfType<TextBlock>().Any(text => text.Text == "暂停"))
    throw new InvalidOperationException("The compact log toolbar must not expose a pause command.");
if (window.GetVisualDescendants().OfType<TextBlock>().Any(text => text.Text is "工作区文件" or "编辑区"))
    throw new InvalidOperationException("The workspace file header must not expose redundant section labels or editor buttons.");

Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
using var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("Headless renderer returned no frame.");
await using (var output = File.Create(outputPath)) frame.Save(output, PngBitmapEncoderOptions.Default);
Console.WriteLine($"[PASS] main shell rendered to {outputPath}");
Console.WriteLine("[PASS] three semantic columns, two splitters, side minimum widths, permanent chat, no main TabStrip");
Console.WriteLine("[PASS] launcher sizing and file context-command placement");
window.Close();

var onboardingPath = Path.Combine(Path.GetDirectoryName(outputPath)!, "athena-onboarding.png");
var onboarding = new OnboardingWindow(new OnboardingViewModel())
{
    Width = 720,
    Height = 600
};
onboarding.Show();
Dispatcher.UIThread.RunJobs();
using var onboardingFrame = onboarding.CaptureRenderedFrame() ?? throw new InvalidOperationException("Onboarding renderer returned no frame.");
await using (var output = File.Create(onboardingPath)) onboardingFrame.Save(output, PngBitmapEncoderOptions.Default);
Console.WriteLine($"[PASS] minimal onboarding rendered to {onboardingPath}");
onboarding.Close();

var diffTab = new WorkspaceEditorTabViewModel
{
    FullPath = "/tmp/sample.cs",
    RelativePath = "sample.cs",
    CanDiff = true,
    Mode = WorkspaceEditorMode.Diff
};
diffTab.ReplaceFromDisk("class Athena\n{\n    string Mode = \"new\";\n}", DateTime.UtcNow);
diffTab.SetDiff(WorkspaceDiffBuilder.Build(
    "class Athena\n{\n    string Mode = \"old\";\n}",
    diffTab.Text));
diffTab.Mode = WorkspaceEditorMode.Diff;
var workbench = new WorkspaceWorkbenchViewModel(
    new WorkspaceOperationCoordinator(),
    new HeadlessPathService(),
    new HeadlessInteractionService());
workbench.EditorTabs.Add(diffTab);
for (var index = 1; index <= 8; index++)
{
    var extraTab = new WorkspaceEditorTabViewModel
    {
        FullPath = $"/tmp/long-workspace-file-{index}.md",
        RelativePath = $"long-workspace-file-{index}.md",
        Mode = WorkspaceEditorMode.Preview
    };
    extraTab.ReplaceFromDisk($"# File {index}", DateTime.UtcNow);
    workbench.EditorTabs.Add(extraTab);
}
workbench.SelectedEditorTab = diffTab;
var workbenchView = new WorkspaceWorkbenchView { DataContext = workbench };
var diffWindow = new Window
{
    Content = workbenchView,
    Width = 900,
    Height = 600
};
diffWindow.Show();
Dispatcher.UIThread.RunJobs();
if (!diffWindow.GetVisualDescendants().OfType<TextBlock>().Any(text => text.Text == "    string Mode = \"old\";"))
    throw new InvalidOperationException("Visual diff did not render the removed line.");
if (!diffWindow.GetVisualDescendants().OfType<TextBlock>().Any(text => text.Text == "    string Mode = \"new\";"))
    throw new InvalidOperationException("Visual diff did not render the inserted line.");
var editorTabs = workbenchView.FindControl<ListBox>("EditorTabsList")
                 ?? throw new InvalidOperationException("Scrollable editor tab list was not created.");
var tabItems = editorTabs.GetVisualDescendants().OfType<ListBoxItem>().ToList();
if (tabItems.Count != workbench.EditorTabs.Count)
    throw new InvalidOperationException("The horizontal editor tab list did not materialize every tab.");
if (tabItems.Select(item => item.Bounds.Y).Distinct().Count() != 1)
    throw new InvalidOperationException("Editor tabs wrapped vertically instead of remaining on one horizontal row.");
var tabScroller = editorTabs.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault()
                  ?? throw new InvalidOperationException("Editor tabs do not have a horizontal scroll host.");
if (tabScroller.Extent.Width <= tabScroller.Viewport.Width)
    throw new InvalidOperationException("Overflowing editor tabs did not create a horizontal scroll range.");
tabScroller.Offset = new Vector(120, 0);
Dispatcher.UIThread.RunJobs();
if (tabScroller.Offset.X <= 0)
    throw new InvalidOperationException("The editor tab strip cannot scroll horizontally.");
var saveButton = workbenchView.FindControl<Button>("SaveEditorButton")
                 ?? throw new InvalidOperationException("Save editor command was not created.");
var cancelButton = workbenchView.FindControl<Button>("CancelEditorButton")
                   ?? throw new InvalidOperationException("Cancel editor command was not created.");
if (saveButton.IsVisible || cancelButton.IsVisible)
    throw new InvalidOperationException("Save and cancel commands must be hidden outside edit mode.");
var diffPath = Path.Combine(Path.GetDirectoryName(outputPath)!, "athena-workbench-diff.png");
using var diffFrame = diffWindow.CaptureRenderedFrame() ?? throw new InvalidOperationException("Diff renderer returned no frame.");
await using (var output = File.Create(diffPath)) diffFrame.Save(output, PngBitmapEncoderOptions.Default);
Console.WriteLine($"[PASS] visual workspace diff rendered to {diffPath}");

diffTab.Mode = WorkspaceEditorMode.Edit;
diffTab.Text += "\n// unsaved";
var workbenchGrid = workbenchView.FindControl<Grid>("WorkbenchGrid")
                    ?? throw new InvalidOperationException("Workbench grid was not created.");
workbenchGrid.ColumnDefinitions[0].Width = new GridLength(240);
Dispatcher.UIThread.RunJobs();
if (!saveButton.IsVisible || !cancelButton.IsVisible || !saveButton.IsEnabled || !cancelButton.IsEnabled)
    throw new InvalidOperationException("Dirty edit mode must expose enabled save and cancel link commands.");
if (saveButton.FontSize != 10 || saveButton.BorderThickness != default)
    throw new InvalidOperationException("Editor commands must use the compact borderless link style.");
var editPath = Path.Combine(Path.GetDirectoryName(outputPath)!, "athena-workbench-edit.png");
using var editFrame = diffWindow.CaptureRenderedFrame() ?? throw new InvalidOperationException("Edit renderer returned no frame.");
await using (var output = File.Create(editPath)) editFrame.Save(output, PngBitmapEncoderOptions.Default);
Console.WriteLine($"[PASS] minimum-width edit toolbar rendered to {editPath}");
workbench.CancelFileEditsCommand.Execute(diffTab);
if (diffTab.IsDirty || diffTab.Text.Contains("// unsaved", StringComparison.Ordinal))
    throw new InvalidOperationException("Cancel must restore the latest disk/saved buffer.");
Console.WriteLine("[PASS] edit-only save/cancel commands, cancel restore, compact link styling, and horizontal tabs");
diffWindow.Close();
workbench.Dispose();

sealed class HeadlessPathService : IPlatformPathService
{
    private static string Root => Path.Combine(Path.GetTempPath(), "athena-headless");
    public string GetAppDataDirectory() => Root;
    public string GetConfigFilePath() => Path.Combine(Root, "config.json");
    public string GetLogDirectory() => Path.Combine(Root, "logs");
    public string GetKnowledgeBaseDirectory() => Path.Combine(Root, "knowledge");
    public string GetHistoryDirectory() => Path.Combine(Root, "history");
    public string GetPendingArchiveDirectory() => Path.Combine(Root, "pending");
    public string GetAttachmentDirectory() => Path.Combine(Root, "attachments");
    public string GetImageGenerationSessionDirectory() => Path.Combine(Root, "images");
    public string GetTaskSchedulerFilePath() => Path.Combine(Root, "tasks.json");
    public string GetVectorStoreFilePath() => Path.Combine(Root, "vectors.db");
    public string GetWorkspacesDirectory() => Path.Combine(Root, "workspaces");
    public string GetWorkspaceKnowledgeDirectory(string workspaceId) => Path.Combine(Root, workspaceId);
}

sealed class HeadlessInteractionService : IUserInteractionService
{
    public Task<bool> ConfirmAsync(string title, string message, string confirmText, string cancelText) => Task.FromResult(false);
    public Task<string?> PickFolderAsync(string title) => Task.FromResult<string?>(null);
    public Task<IReadOnlyList<string>> PickFilesAsync(string title, string displayName, IReadOnlyList<string> patterns, bool allowMultiple)
        => Task.FromResult<IReadOnlyList<string>>([]);
    public Task<string?> PickSaveFileAsync(string title, string suggestedFileName, string displayName, IReadOnlyList<string> patterns)
        => Task.FromResult<string?>(null);
    public Task ShowImagePreviewAsync(ChatAttachment attachment) => Task.CompletedTask;
}
