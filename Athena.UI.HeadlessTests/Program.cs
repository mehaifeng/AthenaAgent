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
var globalConversationGroup = new WorkspaceConversationGroupViewModel(null);
globalConversationGroup.Conversations.Add(new ConversationSessionItemViewModel(new ChatTabViewModel(), null, null)
{
    Title = "全局产品讨论"
});
mainViewModel.ConversationGroups.Add(globalConversationGroup);
var workspaceProfile = new WorkspaceProfile
{
    Name = "AthenaAgent",
    DirectoryPath = "/Users/example/AthenaAgent"
};
var conversationGroup = new WorkspaceConversationGroupViewModel(workspaceProfile);
var activeSession = new ConversationSessionItemViewModel(new ChatTabViewModel(), workspaceProfile, null)
{
    Title = "正在整理发布说明"
};
conversationGroup.Conversations.Add(activeSession);
var pinnedSession = new ConversationSessionItemViewModel(new ChatTabViewModel(), workspaceProfile, null)
{
    Title = "比较两套工作区方案",
    ForkedFromConversationId = "parent-conversation",
    HasUnreadCompletion = true,
    IsPinned = true
};
conversationGroup.Conversations.Add(pinnedSession);
mainViewModel.ConversationGroups.Add(conversationGroup);
mainViewModel.PinnedConversations.Add(pinnedSession);
mainViewModel.SelectedConversation = activeSession;

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
var settingsButton = window.FindControl<Button>("AppSettingsButton")
                     ?? throw new InvalidOperationException("Settings command was not moved into the workspace-side footer.");
var globalConversationButton = window.FindControl<Button>("GlobalConversationButton")
                               ?? throw new InvalidOperationException("Global conversation command was not created.");
if (settingsButton.Bounds.X >= globalConversationButton.Bounds.X || globalConversationButton.Bounds.Width <= settingsButton.Bounds.Width * 2)
    throw new InvalidOperationException("The workspace footer must place settings left of a stretched global conversation command.");
var searchBox = window.FindControl<TextBox>("WorkspaceSearchBox")
                ?? throw new InvalidOperationException("Workspace search field was not created.");
var addWorkspaceButton = window.FindControl<Button>("AddWorkspaceButton")
                         ?? throw new InvalidOperationException("Add-workspace command was not created.");
if (addWorkspaceButton.Bounds.Left - searchBox.Bounds.Right < 5)
    throw new InvalidOperationException("Workspace search and add controls need a visible gap.");
var workspaceCards = window.GetVisualDescendants().OfType<Border>()
    .Where(border => border.Classes.Contains("workspace-card"))
    .ToList();
if (workspaceCards.Count < 2
    || workspaceCards.Any(border => border.BorderThickness.Left < 1.1 || border.BorderBrush == null || border.Background == null))
    throw new InvalidOperationException("Workspace items must have a visible outline.");
if (workspaceCards.Any(card => !card.GetVisualDescendants().OfType<Expander>().Any()))
    throw new InvalidOperationException("Every workspace card must retain its native expander behavior.");
var conversationCards = window.GetVisualDescendants().OfType<Border>()
    .Where(border => border.Classes.Contains("conversation-card"))
    .ToList();
if (conversationCards.Count < 4
    || conversationCards.Any(border => border.BorderThickness.Left < 1.1 || border.BorderBrush == null || border.Background == null))
    throw new InvalidOperationException("Conversation items must have a visible outline.");
if (window.GetVisualDescendants().OfType<TextBlock>().Any(text => text.Text == workspaceProfile.DirectoryPath))
    throw new InvalidOperationException("Workspace cards must not render directory paths.");
var pinnedList = window.FindControl<ItemsControl>("PinnedConversationsList")
                 ?? throw new InvalidOperationException("Pinned conversation list was not created.");
if (pinnedList.ItemCount != 1)
    throw new InvalidOperationException("Pinned conversations were not materialized in the dedicated list.");
var workspaceMenu = window.GetVisualDescendants().OfType<Button>()
    .FirstOrDefault(button => button.Classes.Contains("workspace-menu"))
    ?? throw new InvalidOperationException("Workspace overflow menu trigger was not created.");
var workspaceMenuFlyout = workspaceMenu.ContextFlyout as MenuFlyout
                          ?? throw new InvalidOperationException("Workspace menu flyout was not created.");
workspaceMenu.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
Dispatcher.UIThread.RunJobs();
if (!workspaceMenuFlyout.IsOpen)
    throw new InvalidOperationException("Clicking the workspace overflow button did not open its menu.");
var workspaceMenuItems = workspaceMenuFlyout.Items.OfType<MenuItem>().ToList();
if (!workspaceMenuItems.Select(item => item.Header?.ToString()).SequenceEqual(["重命名", "在文件夹中显示", "复制路径", "删除"])
    || workspaceMenuItems.Any(item => item.Icon == null))
    throw new InvalidOperationException("Workspace menu commands or icons are incomplete.");
workspaceMenuFlyout.Hide();
var conversationMenus = window.GetVisualDescendants().OfType<Button>()
    .Where(button => button.Classes.Contains("conversation-menu"))
    .ToList();
if (conversationMenus.Count < 4)
    throw new InvalidOperationException("Conversation items, including pinned shortcuts, need overflow menus.");
var pinnedMenuButton = conversationMenus.First(button => ReferenceEquals(button.DataContext, pinnedSession));
var pinnedMenuFlyout = pinnedMenuButton.ContextFlyout as MenuFlyout
                       ?? throw new InvalidOperationException("Pinned conversation menu flyout was not created.");
pinnedMenuButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
Dispatcher.UIThread.RunJobs();
if (!pinnedMenuFlyout.IsOpen)
    throw new InvalidOperationException("Clicking the conversation overflow button did not open its menu.");
var pinnedMenuItems = pinnedMenuFlyout.Items.OfType<MenuItem>().ToList()
    ?? throw new InvalidOperationException("Pinned conversation menu flyout was not created.");
if (!pinnedMenuItems.Select(item => item.Header?.ToString()).SequenceEqual(["重命名", "取消置顶", "分支", "导出", "删除"])
    || pinnedMenuItems.Any(item => item.Icon == null))
    throw new InvalidOperationException("Pinned conversation menu commands or icons are incomplete.");
pinnedMenuFlyout.Hide();

Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
using var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("Headless renderer returned no frame.");
await using (var output = File.Create(outputPath)) frame.Save(output, PngBitmapEncoderOptions.Default);
Console.WriteLine($"[PASS] main shell rendered to {outputPath}");
Console.WriteLine("[PASS] three semantic columns, two splitters, side minimum widths, permanent chat, no main TabStrip");
Console.WriteLine("[PASS] launcher sizing and file context-command placement");
Console.WriteLine("[PASS] workspace cards, pinned conversations, overflow menus, footer settings, and search spacing");
window.Close();

var groupCommandChecks = new WorkspaceConversationGroupViewModel(workspaceProfile);
var renameCount = 0;
var revealCount = 0;
var copyPathCount = 0;
var deleteWorkspaceCount = 0;
groupCommandChecks.RenameCommitted += (_, _) => renameCount++;
groupCommandChecks.RevealRequested += (_, _) => revealCount++;
groupCommandChecks.CopyPathRequested += (_, _) => copyPathCount++;
groupCommandChecks.DeleteRequested += (_, _) => deleteWorkspaceCount++;
groupCommandChecks.StartRenameCommand.Execute(null);
groupCommandChecks.RenameText = "Renamed workspace";
groupCommandChecks.CommitRenameCommand.Execute(null);
groupCommandChecks.RequestRevealCommand.Execute(null);
groupCommandChecks.RequestCopyPathCommand.Execute(null);
groupCommandChecks.RequestDeleteCommand.Execute(null);
if (workspaceProfile.Name != "Renamed workspace"
    || renameCount != 1
    || revealCount != 1
    || copyPathCount != 1
    || deleteWorkspaceCount != 1)
    throw new InvalidOperationException("Workspace item commands are not fully wired.");
var sessionCommandChecks = new ConversationSessionItemViewModel(new ChatTabViewModel(), workspaceProfile, null);
var exportCount = 0;
sessionCommandChecks.ExportRequested += (_, _) => exportCount++;
sessionCommandChecks.TogglePinnedCommand.Execute(null);
sessionCommandChecks.RequestExportCommand.Execute(null);
if (!sessionCommandChecks.IsPinned || sessionCommandChecks.PinActionText != "取消置顶" || exportCount != 1)
    throw new InvalidOperationException("Conversation pin and export commands are not fully wired.");
sessionCommandChecks.Dispose();
Console.WriteLine("[PASS] workspace command events and conversation pin/export behavior");

var forkStore = new HeadlessConversationStore();
var forkViewModel = new MainWindowViewModel(
    chatService: null,
    configService: null,
    taskScheduler: null,
    contextCompressionService: null,
    promptService: null,
    logService: null,
    knowledgeBaseService: null,
    embeddingService: null,
    localizationService: null,
    fileSystemService: null,
    platformPathService: null,
    functionRegistry: null,
    tokenService: null,
    webSearchService: null,
    updateService: null,
    attachmentStoreService: null,
    systemAudioService: null,
    archiveService: null,
    imageGenerationSessionService: null,
    conversationStore: forkStore);
while (forkViewModel.IsConversationTreeLoading)
{
    await Task.Delay(10);
    Dispatcher.UIThread.RunJobs();
}
forkViewModel.ConversationGroups.Clear();
var forkWorkspace = new WorkspaceProfile { Name = "Fork workspace", DirectoryPath = "/tmp/fork-workspace" };
var forkGroup = new WorkspaceConversationGroupViewModel(forkWorkspace);
var forkSource = new ConversationSessionItemViewModel(forkViewModel.ChatTabViewModel, forkWorkspace, forkStore)
{
    Title = "Pinned parent",
    IsPinned = true
};
forkSource.Chat.Messages.Add(new ChatMessage { Role = "user", Content = "Parent content" });
forkGroup.Conversations.Add(forkSource);
forkViewModel.ConversationGroups.Add(forkGroup);
forkViewModel.SelectedConversation = forkSource;
forkViewModel.ChatTabViewModel = new ChatTabViewModel();
await forkViewModel.ForkConversationCommand.ExecuteAsync(forkSource);
var forkChild = forkGroup.Conversations.ElementAtOrDefault(1);
if (forkChild == null
    || !forkChild.IsForked
    || forkChild.IsPinned
    || forkChild.Chat.Messages.Count != 0
    || !ReferenceEquals(forkViewModel.SelectedConversation, forkChild)
    || forkStore.Items[forkChild.HistoryId].Messages.Count != 0)
    throw new InvalidOperationException("Conversation branching must create and select an empty unpinned child directly after its parent.");
forkSource.Dispose();
forkChild.Dispose();
Console.WriteLine("[PASS] pinned-session branch placement, empty content, persistence, and selection");

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

sealed class HeadlessConversationStore : IConversationArchiveStore
{
    public Dictionary<string, ConversationHistoryItem> Items { get; } = new();

    public Task<List<ConversationHistoryItem>> LoadAllAsync() => Task.FromResult(Items.Values.ToList());

    public Task<ConversationHistoryItem?> LoadByIdAsync(string id)
        => Task.FromResult(Items.GetValueOrDefault(id));

    public Task SaveAsync(ConversationHistoryItem item)
    {
        Items[item.Id] = item;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string id)
    {
        Items.Remove(id);
        return Task.CompletedTask;
    }
}
