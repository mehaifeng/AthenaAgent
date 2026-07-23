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
workbench.SelectedEditorTab = diffTab;
var diffWindow = new Window
{
    Content = new WorkspaceWorkbenchView { DataContext = workbench },
    Width = 900,
    Height = 600
};
diffWindow.Show();
Dispatcher.UIThread.RunJobs();
if (!diffWindow.GetVisualDescendants().OfType<TextBlock>().Any(text => text.Text == "    string Mode = \"old\";"))
    throw new InvalidOperationException("Visual diff did not render the removed line.");
if (!diffWindow.GetVisualDescendants().OfType<TextBlock>().Any(text => text.Text == "    string Mode = \"new\";"))
    throw new InvalidOperationException("Visual diff did not render the inserted line.");
var diffPath = Path.Combine(Path.GetDirectoryName(outputPath)!, "athena-workbench-diff.png");
using var diffFrame = diffWindow.CaptureRenderedFrame() ?? throw new InvalidOperationException("Diff renderer returned no frame.");
await using (var output = File.Create(diffPath)) diffFrame.Save(output, PngBitmapEncoderOptions.Default);
Console.WriteLine($"[PASS] visual workspace diff rendered to {diffPath}");
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
