#pragma warning disable CA2000 // Test composition root transfers ownership to windows/aggregate VMs; lifecycle cases dispose explicitly.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Chrome;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Athena.UI;
using Athena.UI.Models;
using Athena.UI.Services;
using Athena.UI.Services.Interfaces;
using Athena.UI.ViewModels;
using Athena.UI.Views;
using System.Diagnostics;
using System.Reflection;

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

Task.Run(TestWorkspaceGitDiffAsync).GetAwaiter().GetResult();
TestLayoutSaveDoesNotReapplyRuntimeClients();
TestConcreteConfigServiceIdentity();
TestConfigurationSession(Path.GetDirectoryName(outputPath)!);
TestLifecycle();

var shellConfigService = new HeadlessConfigService(new AppConfig());
using var shellConfigurationSession = new AppConfigurationSession(shellConfigService);
var mainViewModel = new MainWindowViewModel(
    chatService: null,
    configService: null,
    taskScheduler: null,
    contextCompressionService: null,
    promptService: null,
    logService: null,
    knowledgeBaseService: null,
    localizationService: null,
    fileSystemService: null,
    platformPathService: null,
    functionRegistry: null,
    tokenService: null,
    attachmentStoreService: null,
    systemAudioService: null,
    archiveService: null,
    imageGenerationSessionService: null,
    configurationSession: shellConfigurationSession);
var globalConversationGroup = new WorkspaceConversationGroupViewModel(null);
globalConversationGroup.Conversations.Add(new ConversationSessionItemViewModel(new MainConversationViewModel(), null, null)
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
var activeSession = new ConversationSessionItemViewModel(new MainConversationViewModel(), workspaceProfile, null)
{
    Title = "正在整理发布说明"
};
conversationGroup.Conversations.Add(activeSession);
var pinnedSession = new ConversationSessionItemViewModel(new MainConversationViewModel(), workspaceProfile, null)
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
var leftSideSplitter = window.FindControl<GridSplitter>("LeftSideSplitter")
                       ?? throw new InvalidOperationException("The left shell splitter was not created.");
var rightSideSplitter = window.FindControl<GridSplitter>("RightSideSplitter")
                        ?? throw new InvalidOperationException("The right shell splitter was not created.");
if (!leftSideSplitter.ShowsPreview
    || !rightSideSplitter.ShowsPreview
    || leftSideSplitter.ResizeBehavior != GridResizeBehavior.PreviousAndNext
    || rightSideSplitter.ResizeBehavior != GridResizeBehavior.PreviousAndNext)
    throw new InvalidOperationException("Shell splitters must preview and resize only their adjacent columns.");
await mainViewModel.ToggleSidePanelsCommand.ExecuteAsync(null);
Dispatcher.UIThread.RunJobs();
if (shell.ColumnDefinitions[0].MinWidth < 360 || shell.ColumnDefinitions[4].MinWidth < 260)
    throw new InvalidOperationException("Swapping side panels did not swap their physical column minimum widths.");
await mainViewModel.ToggleSidePanelsCommand.ExecuteAsync(null);
Dispatcher.UIThread.RunJobs();
if (window.FindControl<MainConversationView>("MainConversationView") == null)
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
if (window.FindControl<Button>("AppSettingsButton") != null)
    throw new InvalidOperationException("The old workspace-footer settings button must be removed.");
var titleBarThemeButton = window.FindControl<Button>("TitleBarThemeButton")
                          ?? throw new InvalidOperationException("The theme command was not moved into the title bar.");
var titleBarSettingsButton = window.FindControl<Button>("TitleBarAppSettingsButton")
                             ?? throw new InvalidOperationException("The settings command was not moved into the title bar.");
var titleBarDragArea = window.FindControl<Grid>("TitleBarDragArea")
                       ?? throw new InvalidOperationException("The title bar has no dedicated drag area.");
if (titleBarDragArea.Background == null
    || WindowDecorationProperties.GetElementRole(titleBarDragArea) != WindowDecorationsElementRole.TitleBar)
    throw new InvalidOperationException("The title-bar drag area must be hit-testable and marked with the native title-bar role.");
var titleBarMinimizeButton = window.FindControl<Button>("TitleBarMinimizeButton")
                             ?? throw new InvalidOperationException("The title bar has no minimize button.");
var titleBarMaximizeButton = window.FindControl<Button>("TitleBarMaximizeButton")
                             ?? throw new InvalidOperationException("The title bar has no maximize/restore button.");
var titleBarCloseButton = window.FindControl<Button>("TitleBarCloseButton")
                          ?? throw new InvalidOperationException("The title bar has no close button.");
if (window.WindowDecorations != WindowDecorations.BorderOnly)
    throw new InvalidOperationException("The custom title bar must retain only the native resize border.");
if (!ReferenceEquals(titleBarThemeButton.Command, mainViewModel.MainConversationViewModel.ToggleThemeCommand)
    || !ReferenceEquals(titleBarSettingsButton.Command, mainViewModel.OpenAppSettingsCommand))
    throw new InvalidOperationException("Title-bar commands are not bound to the existing theme and settings commands.");
if (window.FindControl<Button>("TitleBarFullScreenButton") != null)
    throw new InvalidOperationException("The title bar must not expose a full-screen button.");
titleBarMaximizeButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
Dispatcher.UIThread.RunJobs();
if (window.WindowState != WindowState.Maximized)
    throw new InvalidOperationException("The title-bar maximize button did not maximize the window.");
titleBarMaximizeButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
Dispatcher.UIThread.RunJobs();
if (window.WindowState != WindowState.Normal)
    throw new InvalidOperationException("The title-bar maximize button did not restore the window.");
titleBarMinimizeButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
Dispatcher.UIThread.RunJobs();
if (window.WindowState != WindowState.Minimized)
    throw new InvalidOperationException("The title-bar minimize button did not minimize the window.");
window.WindowState = WindowState.Normal;
Dispatcher.UIThread.RunJobs();
if (window.GetVisualDescendants().OfType<Button>()
    .Any(button => ReferenceEquals(button.Command, mainViewModel.MainConversationViewModel.NewConversationCommand)))
    throw new InvalidOperationException("The main conversation view must not expose a new-conversation button.");
var globalConversationButton = window.FindControl<Button>("GlobalConversationButton")
                               ?? throw new InvalidOperationException("Global conversation command was not created.");
if (globalConversationButton.Bounds.Width <= 0)
    throw new InvalidOperationException("The global conversation command must stretch across the workspace footer.");
var searchBox = window.FindControl<TextBox>("WorkspaceSearchBox")
                ?? throw new InvalidOperationException("Workspace search field was not created.");
var addWorkspaceButton = window.FindControl<Button>("AddWorkspaceButton")
                         ?? throw new InvalidOperationException("Add-workspace command was not created.");
if (addWorkspaceButton.Bounds.Left - searchBox.Bounds.Right < 5)
    throw new InvalidOperationException("Workspace search and add controls need a visible gap.");
var workspaceCards = window.GetVisualDescendants().OfType<StackPanel>()
    .Where(panel => panel.Classes.Contains("nav-group"))
    .ToList();
if (workspaceCards.Count < 2
    || workspaceCards.Any(panel => panel.GetVisualDescendants().OfType<Expander>().Any()))
    throw new InvalidOperationException("Conversation groups must use stacked nav-group layout without card expanders.");
var expandButtons = window.GetVisualDescendants().OfType<Button>()
    .Where(button => button.Classes.Contains("nav-expand"))
    .ToList();
if (expandButtons.Count < 2)
    throw new InvalidOperationException("Every conversation group header must expose a dedicated expand toggle.");
var conversationCards = window.GetVisualDescendants().OfType<Grid>()
    .Where(grid => grid.Classes.Contains("conversation-row"))
    .ToList();
if (conversationCards.Count < 4
    || conversationCards.Any(row => row.DataContext is not ConversationSessionItemViewModel))
    throw new InvalidOperationException("Every session item must render as a conversation-row.");
var pinnedConversationsSection = window.FindControl<StackPanel>("PinnedConversationsSection")
                                   ?? throw new InvalidOperationException("Pinned conversation container was not created.");
if (window.GetVisualDescendants().OfType<TextBlock>().Any(text => text.Text == "已完成"))
    throw new InvalidOperationException("Session rows must not render status text on the right.");
var activeSessionTitle = conversationCards
    .Where(row => ReferenceEquals(row.DataContext, activeSession))
    .SelectMany(row => row.GetVisualDescendants().OfType<TextBlock>())
    .FirstOrDefault(text => text.Text == activeSession.Title)
    ?? throw new InvalidOperationException("Active conversation title was not created.");
if (activeSession.HasStatusIndicator || activeSession.IsForked || activeSessionTitle.Bounds.Left < 5)
    throw new InvalidOperationException("Main conversation title must leave room for the leading bullet glyph.");
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
Console.WriteLine("[PASS] stacked navigation groups, pinned conversations, overflow menus, title-bar commands, and search spacing");
window.Close();

{
    var connectorConfigService = new HeadlessConfigService(new AppConfig());
    using var connectorSession = new AppConfigurationSession(connectorConfigService);
    var skillsPage = new SkillsViewModel();
    skillsPage.Initialize(connectorSession);
    var mcpPage = new McpConnectionsViewModel();
    mcpPage.Initialize(connectorSession);
    var speechPage = new SpeechSettingsViewModel(connectorSession);
    var imagePage = new ImageGenerationSettingsViewModel(connectorSession);
    var webPage = new WebSearchSettingsViewModel(connectorSession);
    var documentPage = new DocumentParserSettingsViewModel(connectorSession);
    var connectorViewModel = new SkillsConnectorsWindowViewModel(
        skillsPage,
        mcpPage,
        speechPage,
        imagePage,
        webPage,
        documentPage);
    var skillsWindow = new SkillsConnectorsWindow
    {
        DataContext = connectorViewModel,
        Width = 1160,
        Height = 800
    };
    skillsWindow.Show();
    Dispatcher.UIThread.RunJobs();

    var connectorNavigation = skillsWindow.FindControl<ListBox>("ConnectorNavigation")
        ?? throw new InvalidOperationException("Skills/Connectors navigation host was not created.");
    var connectorContentHost = skillsWindow.FindControl<ContentControl>("ConnectorContentHost")
        ?? throw new InvalidOperationException("Skills/Connectors must use one ContentControl host.");
    if (connectorNavigation.ItemCount != 6
        || skillsWindow.GetVisualDescendants().OfType<ContentControl>().Count(control => control.Name == "ConnectorContentHost") != 1)
        throw new InvalidOperationException("Skills/Connectors must expose six navigation items and one content host.");

    var expectedViews = new[]
    {
        typeof(SkillsView),
        typeof(McpConnectionsView),
        typeof(SpeechSettingsView),
        typeof(ImageGenerationSettingsView),
        typeof(WebSearchSettingsView),
        typeof(DocumentParserSettingsView)
    };
    skillsPage.Status = "unsaved-characterization";
    for (var section = 0; section < connectorViewModel.Sections.Count; section++)
    {
        connectorViewModel.SelectedSection = connectorViewModel.Sections[section];
        Dispatcher.UIThread.RunJobs();
        if (!ReferenceEquals(connectorContentHost.Content, connectorViewModel.Sections[section].Content)
            || !connectorContentHost.GetVisualDescendants().Any(control => control.GetType() == expectedViews[section]))
            throw new InvalidOperationException($"Connector section {section} did not render its matching page type and data context.");
    }
    connectorViewModel.SelectedSection = connectorViewModel.Sections[0];
    Dispatcher.UIThread.RunJobs();
    if (!ReferenceEquals(connectorContentHost.Content, skillsPage)
        || skillsPage.Status != "unsaved-characterization")
        throw new InvalidOperationException("Switching connector sections recreated a view model or reset page state.");
    SaveWindowFrame(skillsWindow, Path.Combine(Path.GetDirectoryName(outputPath)!, "skills-connectors-window.png"));
    Console.WriteLine("[PASS] six connector page types share one content host and preserve page view models and state");
    skillsWindow.Close();
}

{
    var knowledgeViewModel = new KnowledgeBaseViewModel();
    var knowledgeWindow = new KnowledgeBaseWindow(knowledgeViewModel);
    knowledgeWindow.Show();
    Dispatcher.UIThread.RunJobs();
    var knowledgeView = knowledgeWindow.Content as KnowledgeBaseView
        ?? throw new InvalidOperationException("Knowledge Base window did not host the semantic KnowledgeBaseView.");
    if (knowledgeView.FindControl<Button>("RunKnowledgeMaintenanceButton") == null
        || knowledgeView.FindControl<Button>("RebuildVectorIndexButton") == null
        || !ReferenceEquals(knowledgeView.DataContext, knowledgeViewModel))
        throw new InvalidOperationException("Knowledge Base must expose maintenance and vector-index rebuild commands.");
    var featureWindowTypes = new[]
    {
        (Window: typeof(KnowledgeBaseWindow), ViewModel: typeof(KnowledgeBaseViewModel)),
        (Window: typeof(TasksWindow), ViewModel: typeof(TasksViewModel)),
        (Window: typeof(DetailedLogsWindow), ViewModel: typeof(LogsViewModel))
    };
    if (featureWindowTypes.Any(pair =>
            pair.Window.GetConstructors().Single().GetParameters().Single().ParameterType != pair.ViewModel))
        throw new InvalidOperationException("Feature windows must expose one strongly typed view-model constructor.");
    var semanticViewMappings = new (ViewModelBase ViewModel, Type View)[]
    {
        (new MainConversationViewModel(), typeof(MainConversationView)),
        (new KnowledgeBaseViewModel(), typeof(KnowledgeBaseView)),
        (new TasksViewModel(), typeof(TasksView)),
        (new LogsViewModel(), typeof(LogsView)),
        (new AboutViewModel(), typeof(AboutView))
    };
    var viewLocator = new ViewLocator();
    if (semanticViewMappings.Any(pair => viewLocator.Build(pair.ViewModel)?.GetType() != pair.View))
        throw new InvalidOperationException("Semantic view names no longer satisfy the ViewLocator naming convention.");
    SaveWindowFrame(knowledgeWindow, Path.Combine(Path.GetDirectoryName(outputPath)!, "knowledge-base-window.png"));
    Console.WriteLine("[PASS] semantic views satisfy ViewLocator naming and strongly typed feature windows own their content");
    knowledgeWindow.Close();
}

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
var sessionCommandChecks = new ConversationSessionItemViewModel(new MainConversationViewModel(), workspaceProfile, null);
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
    localizationService: null,
    fileSystemService: null,
    platformPathService: null,
    functionRegistry: null,
    tokenService: null,
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
var forkSource = new ConversationSessionItemViewModel(forkViewModel.MainConversationViewModel, forkWorkspace, forkStore)
{
    Title = "Pinned parent",
    IsPinned = true
};
forkSource.Chat.Messages.Add(new ChatMessage { Role = "user", Content = "Parent content" });
forkGroup.Conversations.Add(forkSource);
forkViewModel.ConversationGroups.Add(forkGroup);
forkViewModel.SelectedConversation = forkSource;
forkViewModel.MainConversationViewModel = new MainConversationViewModel();
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

var archiveTreeStore = new HeadlessConversationStore();
var archiveWorkspace = new WorkspaceProfile
{
    Id = "archive-workspace",
    Name = "Archive workspace",
    DirectoryPath = "/tmp/archive-workspace"
};
var archivedHistory = new ConversationHistoryItem
{
    Id = "archive-existing",
    ConversationId = "archive-conversation-existing",
    Summary = "Existing archived conversation",
    WorkspaceId = archiveWorkspace.Id,
    UpdatedAt = DateTime.Now.AddMinutes(-5),
    Messages = [new ChatMessage { Role = "user", Content = "existing archive body" }]
};
await archiveTreeStore.SaveAsync(archivedHistory);
var archiveServiceChecks = new HeadlessArchiveService(archiveTreeStore);
var archiveWorkspaceService = new HeadlessWorkspaceService([archiveWorkspace]);
var archiveTreeViewModel = new MainWindowViewModel(
    chatService: null,
    configService: null,
    taskScheduler: null,
    contextCompressionService: null,
    promptService: null,
    logService: null,
    knowledgeBaseService: null,
    localizationService: null,
    fileSystemService: null,
    platformPathService: null,
    functionRegistry: null,
    tokenService: null,
    attachmentStoreService: null,
    systemAudioService: null,
    archiveService: archiveServiceChecks,
    imageGenerationSessionService: null,
    workspaceService: archiveWorkspaceService,
    conversationStore: archiveTreeStore);
while (archiveTreeViewModel.IsConversationTreeLoading)
{
    await Task.Delay(10);
    Dispatcher.UIThread.RunJobs();
}

var archiveGroup = archiveTreeViewModel.ConversationGroups
    .Single(group => group.Workspace?.Id == archiveWorkspace.Id);
var archiveSession = archiveGroup.Conversations
    .Single(session => session.HistoryId == archivedHistory.Id);
var archiveSnapshot = new ConversationArchiveSnapshot
{
    HistoryId = archivedHistory.Id,
    ConversationId = archivedHistory.ConversationId,
    WorkspaceId = archiveWorkspace.Id,
    CapturedAt = DateTime.Now,
    Messages = ConversationPersistenceHelper.CloneMessages(archivedHistory.Messages)
};
archiveServiceChecks.PublishStaged(archiveSnapshot);
Dispatcher.UIThread.RunJobs();
if (!archiveSession.IsArchivePending || !archiveSession.ShowArchivePendingIndicator)
    throw new InvalidOperationException("Conversation tree did not surface a staged archive.");

archiveServiceChecks.PublishFailed(archiveSnapshot);
Dispatcher.UIThread.RunJobs();
if (!archiveSession.IsArchiveFailed || !archiveSession.ShowArchiveFailedIndicator)
    throw new InvalidOperationException("Conversation tree did not surface a failed archive retained for retry.");

archivedHistory.Summary = "Archive title refreshed";
archivedHistory.UpdatedAt = DateTime.Now;
await archiveTreeStore.SaveAsync(archivedHistory);
archiveServiceChecks.PublishStaged(archiveSnapshot);
archiveServiceChecks.PublishCompleted(archiveSnapshot, archivedHistory);
for (var attempt = 0; attempt < 50; attempt++)
{
    Dispatcher.UIThread.RunJobs();
    if (!archiveSession.IsArchivePending
        && !archiveSession.IsArchiveFailed
        && archiveSession.Title == archivedHistory.Summary)
        break;
    await Task.Delay(10);
}
if (archiveSession.IsArchivePending
    || archiveSession.IsArchiveFailed
    || archiveSession.Title != archivedHistory.Summary)
    throw new InvalidOperationException("Conversation tree did not refresh the completed archive metadata.");

var externalHistory = new ConversationHistoryItem
{
    Id = "archive-external",
    ConversationId = "archive-conversation-external",
    Summary = "External archive",
    WorkspaceId = archiveWorkspace.Id,
    UpdatedAt = DateTime.Now.AddMinutes(1),
    Messages = [new ChatMessage { Role = "assistant", Content = "externally completed body" }]
};
await archiveTreeStore.SaveAsync(externalHistory);
var externalSnapshot = new ConversationArchiveSnapshot
{
    HistoryId = externalHistory.Id,
    ConversationId = externalHistory.ConversationId,
    WorkspaceId = externalHistory.WorkspaceId,
    CapturedAt = externalHistory.UpdatedAt,
    Messages = ConversationPersistenceHelper.CloneMessages(externalHistory.Messages)
};
archiveServiceChecks.PublishCompleted(externalSnapshot, externalHistory);
ConversationSessionItemViewModel? externalSession =
    archiveGroup.Conversations.FirstOrDefault(session => session.HistoryId == externalHistory.Id);
for (var attempt = 0; attempt < 50 && externalSession == null; attempt++)
{
    Dispatcher.UIThread.RunJobs();
    externalSession = archiveGroup.Conversations.FirstOrDefault(session => session.HistoryId == externalHistory.Id);
    await Task.Delay(10);
}
if (externalSession == null)
    throw new InvalidOperationException("Externally completed archive was not inserted into its workspace group.");

archiveTreeViewModel.ConversationSearchText = "externally completed body";
if (!externalSession.IsSearchMatch || archiveSession.IsSearchMatch)
    throw new InvalidOperationException("Conversation-tree search did not replace the legacy History keyword filter.");

archiveTreeViewModel.SelectedConversation = externalSession;
await archiveTreeViewModel.DeleteConversationCommand.ExecuteAsync(archiveSession);
if (archiveGroup.Conversations.Contains(archiveSession)
    || archiveTreeStore.Items.ContainsKey(archivedHistory.Id))
    throw new InvalidOperationException("Deleting a non-current conversation did not update both tree and store.");

await archiveTreeViewModel.DeleteConversationCommand.ExecuteAsync(externalSession);
if (archiveGroup.Conversations.Contains(externalSession)
    || archiveTreeStore.Items.ContainsKey(externalHistory.Id)
    || ReferenceEquals(archiveTreeViewModel.SelectedConversation, externalSession))
    throw new InvalidOperationException("Deleting the current conversation did not select a surviving/new session.");
Console.WriteLine("[PASS] conversation tree owns archive status, external completion, grouping, search, and current/non-current deletion");

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
var lineEndingOnlyDiff = WorkspaceDiffBuilder.Build("first\nsecond\n", "first\r\nsecond\r\n");
if (lineEndingOnlyDiff.Any(line => line.IsAdded || line.IsRemoved))
    throw new InvalidOperationException("LF/CRLF conversion must not appear as an uncommitted workspace diff.");
var longDiffLine = "    string LongLine = \"" + new string('x', 240) + "\";";
diffTab.ReplaceFromDisk($"class Athena\n{{\n    string Mode = \"new\";\n{longDiffLine}\n}}", DateTime.UtcNow);
diffTab.SetDiff(WorkspaceDiffBuilder.Build(
    "class Athena\n{\n    string Mode = \"old\";\n}",
    diffTab.Text));
diffTab.Mode = WorkspaceEditorMode.Diff;
var workbench = new WorkspaceWorkbenchViewModel(
    new WorkspaceOperationCoordinator(),
    new HeadlessPathService(),
    new HeadlessInteractionService());
workbench.HasGitRepository = true;
workbench.CurrentBranchName = "codex/review-layout";
workbench.IsReviewVisible = true;
workbench.CommitMessage = "Refine the workspace review experience";
workbench.HasStagedChanges = true;
workbench.GitChanges.Add(new GitChangeFileViewModel
{
    RelativePath = "Views/WorkspaceWorkbenchView.axaml",
    FullPath = "/tmp/Views/WorkspaceWorkbenchView.axaml",
    StatusCode = "M ",
    StatusLabel = "已暂存",
    HasStagedChange = true
});
workbench.GitChanges.Add(new GitChangeFileViewModel
{
    RelativePath = "ViewModels/WorkspaceWorkbenchViewModel.cs",
    FullPath = "/tmp/ViewModels/WorkspaceWorkbenchViewModel.cs",
    StatusCode = " M",
    StatusLabel = "已修改",
    HasWorkingTreeChange = true
});
var fileTreeFolder = new WorkspaceFileNodeViewModel
{
    Name = "folder",
    FullPath = "/tmp/folder",
    RelativePath = "folder",
    IsDirectory = true
};
fileTreeFolder.Children.Add(new WorkspaceFileNodeViewModel
{
    Name = "child.txt",
    FullPath = "/tmp/folder/child.txt",
    RelativePath = "folder/child.txt"
});
workbench.Files.Add(fileTreeFolder);
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
workbench.IsEditorVisible = true;
var workbenchView = new WorkspaceWorkbenchView { DataContext = workbench };
var diffWindow = new Window
{
    Content = workbenchView,
    Width = 900,
    Height = 600
};
diffWindow.Show();
Dispatcher.UIThread.RunJobs();
var workspaceFileTree = workbenchView.FindControl<TreeView>("WorkspaceFileTree")
                        ?? throw new InvalidOperationException("Workspace file tree was not created.");
var folderTreeItem = workspaceFileTree.GetVisualDescendants()
                         .OfType<TreeViewItem>()
                         .FirstOrDefault(item => ReferenceEquals(item.DataContext, fileTreeFolder))
                     ?? throw new InvalidOperationException("Workspace folder tree item was not materialized.");
var closedFolderIcon = folderTreeItem.GetVisualDescendants()
                           .OfType<PathIcon>()
                           .Single(icon => icon.Classes.Contains("workspace-folder-icon"));
var openFolderIcon = folderTreeItem.GetVisualDescendants()
                         .OfType<PathIcon>()
                         .Single(icon => icon.Classes.Contains("workspace-folder-open-icon"));
if (!closedFolderIcon.IsVisible || openFolderIcon.IsVisible)
    throw new InvalidOperationException("A collapsed workspace directory must display only the closed-folder icon.");
folderTreeItem.IsExpanded = true;
Dispatcher.UIThread.RunJobs();
if (!fileTreeFolder.IsExpanded)
    throw new InvalidOperationException("Expanding a workspace folder did not persist to its node view model.");
if (closedFolderIcon.IsVisible || !openFolderIcon.IsVisible)
    throw new InvalidOperationException("An expanded workspace directory must display only the open-folder icon.");
var childTreeItem = workspaceFileTree.GetVisualDescendants()
                        .OfType<TreeViewItem>()
                        .FirstOrDefault(item => ReferenceEquals(item.DataContext, fileTreeFolder.Children[0]))
                    ?? throw new InvalidOperationException("Workspace file tree item was not materialized.");
var fileIcon = childTreeItem.GetVisualDescendants()
                   .OfType<PathIcon>()
                   .Single(icon => icon.Classes.Contains("workspace-file-icon"));
if (!fileIcon.IsVisible)
    throw new InvalidOperationException("A workspace file must display the file icon.");
if (!diffWindow.GetVisualDescendants().OfType<TextBlock>().Any(text => text.Text == "    string Mode = \"old\";"))
    throw new InvalidOperationException("Visual diff did not render the removed line.");
if (!diffWindow.GetVisualDescendants().OfType<TextBlock>().Any(text => text.Text == "    string Mode = \"new\";"))
    throw new InvalidOperationException("Visual diff did not render the inserted line.");
var diffLinesList = diffWindow.GetVisualDescendants()
                        .OfType<ListBox>()
                        .FirstOrDefault(list => ReferenceEquals(list.ItemsSource, diffTab.DiffLines))
                    ?? throw new InvalidOperationException("Visual diff line list was not created.");
var diffScroller = diffLinesList.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault()
                   ?? throw new InvalidOperationException("Visual diff does not have a scroll host.");
if (diffScroller.Extent.Width <= diffScroller.Viewport.Width)
    throw new InvalidOperationException("A long diff line did not create a horizontal scroll range.");
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

var branchConversation = new MainConversationViewModel();
var branchView = new MainConversationView
{
    DataContext = branchConversation,
    Workbench = workbench
};
var branchWindow = new Window
{
    Content = branchView,
    Width = 720,
    Height = 360
};
branchWindow.Show();
Dispatcher.UIThread.RunJobs();
if (!branchWindow.GetVisualDescendants().OfType<TextBlock>()
        .Any(text => text.IsVisible && text.Text == "codex/review-layout"))
    throw new InvalidOperationException("A detected repository did not expose the branch selector in the message composer.");
var branchPath = Path.Combine(Path.GetDirectoryName(outputPath)!, "athena-branch-selector.png");
using var branchFrame = branchWindow.CaptureRenderedFrame() ?? throw new InvalidOperationException("Branch selector renderer returned no frame.");
await using (var output = File.Create(branchPath)) branchFrame.Save(output, PngBitmapEncoderOptions.Default);
Console.WriteLine($"[PASS] branch selector rendered to {branchPath}");
branchWindow.Close();
branchConversation.Dispose();

diffTab.Mode = WorkspaceEditorMode.Edit;
diffTab.Text += "\n// unsaved";
var workbenchGrid = workbenchView.FindControl<Grid>("WorkbenchGrid")
                    ?? throw new InvalidOperationException("Workbench grid was not created.");
var reviewSplitter = workbenchView.FindControl<GridSplitter>("ReviewSplitter")
                     ?? throw new InvalidOperationException("The review splitter was not created.");
var editorSplitter = workbenchView.FindControl<GridSplitter>("EditorSplitter")
                     ?? throw new InvalidOperationException("The editor splitter was not created.");
if (workbenchGrid.ColumnDefinitions[0].MinWidth < 260
    || workbenchGrid.ColumnDefinitions[2].MinWidth < 248
    || !reviewSplitter.ShowsPreview
    || !editorSplitter.ShowsPreview
    || reviewSplitter.ResizeBehavior != GridResizeBehavior.PreviousAndNext
    || editorSplitter.ResizeBehavior != GridResizeBehavior.PreviousAndNext)
    throw new InvalidOperationException("Workbench panes do not enforce VS Code-style adjacent-column resize constraints.");
workbenchGrid.ColumnDefinitions[0].Width = new GridLength(100);
workbenchGrid.ColumnDefinitions[2].Width = new GridLength(200);
Dispatcher.UIThread.RunJobs();
if (workbenchGrid.ColumnDefinitions[0].ActualWidth < 260
    || workbenchGrid.ColumnDefinitions[2].ActualWidth < 248)
    throw new InvalidOperationException("Workbench pane columns resized below their declared minimum widths.");
workbench.IsReviewVisible = false;
Dispatcher.UIThread.RunJobs();
if (workbenchGrid.ColumnDefinitions[0].ActualWidth > 0
    || workbenchGrid.ColumnDefinitions[1].ActualWidth > 0)
    throw new InvalidOperationException("Closing review did not collapse its pane and splitter columns.");
workbench.IsReviewVisible = true;
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
Dispatcher.UIThread.RunJobs();
if (saveButton.IsVisible || cancelButton.IsVisible)
    throw new InvalidOperationException("Save and cancel commands must hide again when edit mode is clean.");
Console.WriteLine("[PASS] edit-only save/cancel commands, cancel restore, compact link styling, and horizontal tabs");
diffWindow.Close();
workbench.Dispose();
sessionCommandChecks.Dispose();
forkViewModel.Dispose();
archiveTreeViewModel.Dispose();
mainViewModel.Dispose();

static void TestConfigurationSession(string artifactDirectory)
{
    var service = new HeadlessConfigService(new AppConfig());
    using var session = new AppConfigurationSession(service);
    var settingsLocalization = new LocalizationService();
    settingsLocalization.SwitchLanguage("zh-CN");
    var appSettings = new AppSettingsWindowViewModel(
        session,
        new AboutViewModel(),
        localizationService: settingsLocalization);
    var settingsState = appSettings.General.State;
    var toolApprovalPage = appSettings.ToolApproval;
    var diagnosticsPage = appSettings.RuntimeDiagnostics;
    var skillsPage = new SkillsViewModel();
    skillsPage.Initialize(session);
    var mcpPage = new McpConnectionsViewModel();
    mcpPage.Initialize(session);
    var providerPage = new ProviderModelsViewModel(session, new HeadlessModelCatalogService());
    var chat = new HeadlessChatService();
    var webSearch = new HeadlessWebSearchService();
    var audio = new HeadlessSystemAudioService();
    var speechPage = new SpeechSettingsViewModel(session, chat, audio);
    var imagePage = new ImageGenerationSettingsViewModel(session);
    var webSearchPage = new WebSearchSettingsViewModel(session, webSearch);
    var documentPage = new DocumentParserSettingsViewModel(session);

    Thread.Sleep(650);
    service.ResetSaveCount();

    session.Current.Theme = "Light";
    Thread.Sleep(650);
    AssertSaveCount(service, 1, "root configuration edit");

    service.ResetSaveCount();
    var server = new McpServerConfig { Name = "characterization" };
    session.Current.McpServers.Add(server);
    server.Command = "node";
    server.Arguments.Add(new McpArgEntry("--stdio"));
    server.Arguments[0].Value = "--stdio-updated";
    Thread.Sleep(650);
    AssertSaveCount(service, 1, "MCP nested edit burst");

    service.ResetSaveCount();
    session.Current.McpServers.Remove(server);
    Thread.Sleep(650);
    AssertSaveCount(service, 1, "MCP removal");
    service.ResetSaveCount();
    server.Command = "must-not-be-observed";
    server.Arguments[0].Value = "must-not-be-observed";
    Thread.Sleep(650);
    AssertSaveCount(service, 0, "removed MCP item edit");

    service.ResetSaveCount();
    speechPage.ProviderCards[0].Settings.Voice = "characterization-voice";
    Thread.Sleep(650);
    AssertSaveCount(service, 1, "extension provider setting edit");

    session.Current.AutoAllowedTools.Add("characterization_tool");
    Thread.Sleep(650);
    service.ResetSaveCount();
    toolApprovalPage.RevokeAutoAllowedToolCommand.ExecuteAsync("characterization_tool").GetAwaiter().GetResult();
    AssertSaveCount(service, 1, "revoking an always-allowed tool");
    if (session.Current.AutoAllowedTools.Contains("characterization_tool"))
        throw new InvalidOperationException("App Settings did not revoke the always-allowed tool.");

    session.Current.TerminalAllowlist.Add("characterization-command");
    Thread.Sleep(650);
    service.ResetSaveCount();
    toolApprovalPage.RevokeTerminalAllowlistCommand.ExecuteAsync("characterization-command").GetAwaiter().GetResult();
    AssertSaveCount(service, 1, "revoking a terminal allowlist entry");
    if (session.Current.TerminalAllowlist.Contains("characterization-command"))
        throw new InvalidOperationException("App Settings did not revoke the terminal allowlist entry.");

    diagnosticsPage.TestBrowserRuntimeCommand.ExecuteAsync(null).GetAwaiter().GetResult();
    diagnosticsPage.TestBrowserAgentCommand.ExecuteAsync(null).GetAwaiter().GetResult();
    if (string.IsNullOrWhiteSpace(diagnosticsPage.BrowserRuntimeStatus)
        || string.IsNullOrWhiteSpace(diagnosticsPage.BrowserAgentTestStatus))
        throw new InvalidOperationException("App Settings browser diagnostics did not surface unavailable-service status.");

    documentPage.Config.DocumentParserEnabled = true;
    documentPage.Config.DocumentParserMode = DocumentParserMode.AgentLightweight;
    if (documentPage.CanEditToken)
        throw new InvalidOperationException("AgentLightweight parser mode must not enable the precision token field.");
    documentPage.Config.DocumentParserMode = DocumentParserMode.Precision;
    if (!documentPage.CanEditToken)
        throw new InvalidOperationException("Precision parser mode must enable the token field when parsing is enabled.");

    service.ResetSaveCount();
    session.Current.Language = "en-US";
    service.SaveAsync(session.Current).GetAwaiter().GetResult();
    Thread.Sleep(650);
    AssertSaveCount(service, 1, "explicit same-instance persistence");

    session.Current.WebSearchEnabled = true;
    webSearch.Result = (true, "web-ok");
    webSearchPage.TestConnectionCommand.ExecuteAsync(null).GetAwaiter().GetResult();
    if (webSearchPage.IsTesting || webSearchPage.TestStatus != "web-ok")
        throw new InvalidOperationException("Web Search success state was not surfaced.");
    webSearch.Result = (false, "web-failed");
    webSearchPage.TestConnectionCommand.ExecuteAsync(null).GetAwaiter().GetResult();
    if (webSearchPage.IsTesting || webSearchPage.TestStatus != "web-failed")
        throw new InvalidOperationException("Web Search failure state was not surfaced.");

    session.Current.ChatAudioEnabled = true;
    chat.AudioResult = new AudioOutputTestResult
    {
        Success = true,
        Message = "audio-ok",
        Attachment = new ChatAttachment { Kind = AttachmentKind.Audio, StoredPath = "/tmp/test.wav" }
    };
    speechPage.TestOutputCommand.ExecuteAsync(null).GetAwaiter().GetResult();
    if (speechPage.IsTesting
        || speechPage.TestStatus != "audio-ok"
        || speechPage.TestAttachment == null)
        throw new InvalidOperationException("Audio success state and test attachment were not surfaced.");
    speechPage.TogglePlaybackCommand.Execute(null);
    Thread.Sleep(20);
    speechPage.TogglePlaybackCommand.Execute(null);
    Thread.Sleep(20);
    if (!audio.WasCancelled || speechPage.TestAttachment.IsPlaying)
        throw new InvalidOperationException("Stopping test audio did not cancel playback and clear its playing state.");
    chat.AudioResult = new AudioOutputTestResult { Success = false, Message = "audio-failed" };
    speechPage.TestOutputCommand.ExecuteAsync(null).GetAwaiter().GetResult();
    if (speechPage.IsTesting
        || speechPage.TestStatus != "audio-failed"
        || speechPage.TestAttachment != null)
        throw new InvalidOperationException("Audio failure state was not surfaced.");

    var replacement = new AppConfig { Theme = "Dark" };
    service.PublishExternal(replacement);
    Dispatcher.UIThread.RunJobs();
    if (!ReferenceEquals(session.Current, replacement)
        || !ReferenceEquals(settingsState.Config, replacement)
        || !ReferenceEquals(skillsPage.Config, replacement)
        || !ReferenceEquals(mcpPage.Config, replacement)
        || !ReferenceEquals(providerPage.Config, replacement)
        || !ReferenceEquals(speechPage.Config, replacement)
        || !ReferenceEquals(imagePage.Config, replacement)
        || !ReferenceEquals(webSearchPage.Config, replacement)
        || !ReferenceEquals(documentPage.Config, replacement))
        throw new InvalidOperationException("All settings pages must follow an externally replaced configuration instance.");

    if (appSettings.Sections.Count != 6
        || !ReferenceEquals(appSettings.General.State, appSettings.ConversationContext.State)
        || !ReferenceEquals(appSettings.General.State, appSettings.ToolApproval.State)
        || !ReferenceEquals(appSettings.General.State, appSettings.AgentRuntime.State)
        || !ReferenceEquals(appSettings.General.State, appSettings.RuntimeDiagnostics.State))
        throw new InvalidOperationException("App Settings pages must share one settings state and expose six semantic sections.");
    settingsLocalization.SwitchLanguage("en-US");
    if (appSettings.Sections[0].Title != "General"
        || appSettings.ToolApproval.Modes[0].Title != "Balanced · Recommended")
        throw new InvalidOperationException("App Settings semantic navigation and approval modes did not refresh after a language change.");
    settingsLocalization.SwitchLanguage("zh-CN");
    if (appSettings.Sections[0].Title != "通用"
        || appSettings.ToolApproval.Modes[0].Title != "均衡 · 推荐")
        throw new InvalidOperationException("App Settings semantic navigation and approval modes did not return to Chinese.");

    var appSettingsWindow = new AppSettingsWindow { DataContext = appSettings };
    appSettingsWindow.Show();
    Dispatcher.UIThread.RunJobs();
    if (appSettingsWindow.DataContext is MainWindowViewModel
        || appSettingsWindow.FindControl<ListBox>("SettingsNavigation") == null
        || appSettingsWindow.FindControl<ContentControl>("SettingsContentHost") == null
        || appSettingsWindow.GetVisualDescendants().OfType<Expander>().Any()
        || appSettingsWindow.Content is not Grid)
        throw new InvalidOperationException("App Settings must use one navigation surface, one content host, and page-owned scrolling.");

    var expectedViews = new[]
    {
        typeof(GeneralSettingsView),
        typeof(ConversationContextSettingsView),
        typeof(ToolApprovalSettingsView),
        typeof(AgentRuntimeSettingsView),
        typeof(RuntimeDiagnosticsView),
        typeof(AboutView)
    };
    var settingsFrameNames = new[]
    {
        "general",
        "conversation-context",
        "tool-approval",
        "agent-runtime",
        "runtime-diagnostics",
        "about"
    };
    for (var index = 0; index < appSettings.Sections.Count; index++)
    {
        appSettings.SelectedSection = appSettings.Sections[index];
        Dispatcher.UIThread.RunJobs();
        if (!appSettingsWindow.GetVisualDescendants().Any(view => view.GetType() == expectedViews[index]))
            throw new InvalidOperationException($"App Settings section {index} did not render {expectedViews[index].Name}.");
        SaveWindowFrame(
            appSettingsWindow,
            Path.Combine(artifactDirectory, $"app-settings-{settingsFrameNames[index]}.png"));
    }
    appSettings.SelectedSection = appSettings.Sections[0];
    Dispatcher.UIThread.RunJobs();
    SaveWindowFrame(appSettingsWindow, Path.Combine(artifactDirectory, "app-settings-window.png"));
    appSettingsWindow.Close();
    var closedSettingsConfig = settingsState.Config;
    service.PublishExternal(new AppConfig());
    Dispatcher.UIThread.RunJobs();
    if (!ReferenceEquals(settingsState.Config, closedSettingsConfig))
        throw new InvalidOperationException("Closed App Settings pages still observed configuration replacement.");
    var closedSectionTitle = appSettings.Sections[0].Title;
    settingsLocalization.SwitchLanguage("en-US");
    if (appSettings.Sections[0].Title != closedSectionTitle)
        throw new InvalidOperationException("Closed App Settings still observed localization changes.");
    settingsLocalization.SwitchLanguage("zh-CN");

    Console.WriteLine("[PASS] one debounced owner saves root, MCP, and extension settings and detaches removed items");
    Console.WriteLine("[PASS] external configuration replacement reaches every settings page as one shared instance");
    Console.WriteLine("[PASS] document parser mode controls precision-token availability");
    Console.WriteLine("[PASS] Web Search and audio diagnostics expose success, failure, and playback cancellation");
    Console.WriteLine("[PASS] App Settings page VMs own approval-list revocation and browser diagnostics");
    Console.WriteLine("[PASS] App Settings uses six semantic pages, shared state, and a single content host");
    Console.WriteLine("[PASS] App Settings navigation follows live localization changes");
    Console.WriteLine("[PASS] closing App Settings releases configuration and localization subscriptions");
}

static void SaveWindowFrame(Window window, string path)
{
    using var frame = window.CaptureRenderedFrame()
        ?? throw new InvalidOperationException($"Headless renderer returned no frame for {window.GetType().Name}.");
    using var output = File.Create(path);
    frame.Save(output, PngBitmapEncoderOptions.Default);
}

static void TestLifecycle()
{
    var configService = new HeadlessConfigService(new AppConfig());
    var archiveStore = new HeadlessConversationStore();
    var archiveService = new HeadlessArchiveService(archiveStore);
    var localizationService = new HeadlessLocalizationService();

    var onboarding = new OnboardingViewModel(configService, localizationService, null, null);
    if (CountThemeSubscriptions(onboarding) != 1)
        throw new InvalidOperationException("Onboarding did not attach exactly one theme subscription.");
    onboarding.Dispose();
    onboarding.Dispose();
    if (CountThemeSubscriptions(onboarding) != 0)
        throw new InvalidOperationException("Disposed Onboarding still has a static theme subscription.");

    var logs = new LogsViewModel();
    if (CountThemeSubscriptions(logs) != 1)
        throw new InvalidOperationException("Logs did not attach exactly one theme subscription.");
    logs.Dispose();
    logs.Dispose();
    if (CountThemeSubscriptions(logs) != 0)
        throw new InvalidOperationException("Disposed Logs still has a static theme subscription.");

    var knowledgeConfigService = new HeadlessConfigService(new AppConfig());
    using var knowledgeSession = new AppConfigurationSession(knowledgeConfigService);
    var maintenance = new HeadlessKnowledgeMaintenanceService();
    var knowledge = new KnowledgeBaseViewModel(
        null, null, null, null, null, maintenance, knowledgeSession);
    if (maintenance.SubscriberCount != 1)
        throw new InvalidOperationException("Knowledge Base did not attach its maintenance subscription.");
    var originalKnowledgeConfig = knowledge.Config;
    knowledge.Dispose();
    knowledge.Dispose();
    knowledgeConfigService.PublishExternal(new AppConfig());
    Dispatcher.UIThread.RunJobs();
    if (maintenance.SubscriberCount != 0 || !ReferenceEquals(knowledge.Config, originalKnowledgeConfig))
        throw new InvalidOperationException("Disposed Knowledge Base still observed a long-lived publisher.");

    for (var iteration = 0; iteration < 10; iteration++)
    {
        var conversation = new MainConversationViewModel(
            null,
            configService,
            null,
            null,
            null,
            null,
            null,
            localizationService,
            archiveService: archiveService);
        var session = new ConversationSessionItemViewModel(conversation, null, null);
        if (configService.ConfigSubscriberCount != 1
            || archiveService.CompletedSubscriberCount != 1
            || archiveService.FailedSubscriberCount != 1
            || localizationService.LanguageSubscriberCount != 1)
            throw new InvalidOperationException("Main Conversation did not attach exactly one lifecycle subscription.");

        session.Dispose();
        session.Dispose();
        if (!conversation.IsDisposed
            || configService.ConfigSubscriberCount != 0
            || archiveService.CompletedSubscriberCount != 0
            || archiveService.FailedSubscriberCount != 0
            || localizationService.LanguageSubscriberCount != 0)
            throw new InvalidOperationException("Deleting a conversation did not release its Main Conversation subscriptions.");
    }

    for (var iteration = 0; iteration < 5; iteration++)
    {
        var connectorConfigService = new HeadlessConfigService(new AppConfig());
        using var connectorSession = new AppConfigurationSession(connectorConfigService);
        var skills = new SkillsViewModel();
        skills.Initialize(connectorSession);
        var mcp = new McpConnectionsViewModel();
        mcp.Initialize(connectorSession);
        var speech = new SpeechSettingsViewModel(connectorSession);
        var image = new ImageGenerationSettingsViewModel(connectorSession);
        var web = new WebSearchSettingsViewModel(connectorSession);
        var document = new DocumentParserSettingsViewModel(connectorSession);
        var connectorWindow = new SkillsConnectorsWindow
        {
            DataContext = new SkillsConnectorsWindowViewModel(skills, mcp, speech, image, web, document)
        };

        connectorWindow.Show();
        Dispatcher.UIThread.RunJobs();
        connectorWindow.Close();
        Dispatcher.UIThread.RunJobs();

        var replacementConfig = new AppConfig();
        connectorConfigService.PublishExternal(replacementConfig);
        if (ReferenceEquals(skills.Config, replacementConfig)
            || ReferenceEquals(mcp.Config, replacementConfig)
            || ReferenceEquals(speech.Config, replacementConfig)
            || ReferenceEquals(image.Config, replacementConfig)
            || ReferenceEquals(web.Config, replacementConfig)
            || ReferenceEquals(document.Config, replacementConfig))
            throw new InvalidOperationException("Closed Skills & Connectors pages still observed configuration replacement.");
    }

    var providerConfigService = new HeadlessConfigService(new AppConfig());
    using var configurationSession = new AppConfigurationSession(providerConfigService);
    var provider = new OpenAiProviderConfiguration
    {
        DisplayName = "Lifecycle provider",
        BaseUrl = "https://example.invalid/v1",
        ApiKey = "test"
    };
    configurationSession.Current.AiModels.Providers.Add(provider);
    var blockingCatalog = new BlockingModelCatalogService();
    var providerViewModel = new ProviderModelsViewModel(configurationSession, blockingCatalog)
    {
        SelectedProvider = provider
    };
    var refreshTask = providerViewModel.RefreshModelsCommand.ExecuteAsync(provider);
    if (!blockingCatalog.Started.Task.Wait(TimeSpan.FromSeconds(2)))
        throw new InvalidOperationException("Provider Models refresh did not start.");
    providerViewModel.Dispose();
    for (var attempt = 0; attempt < 100 && !blockingCatalog.WasCancelled; attempt++)
    {
        Thread.Sleep(10);
    }
    _ = refreshTask;
    if (!blockingCatalog.WasCancelled)
        throw new InvalidOperationException("Disposing Provider Models did not cancel its in-flight model refresh.");

    var replacement = new AppConfig();
    providerConfigService.PublishExternal(replacement);
    if (ReferenceEquals(providerViewModel.Config, replacement))
        throw new InvalidOperationException("Closed Provider Models still observed configuration replacement.");

    Console.WriteLine("[PASS] repeated conversation disposal releases config, archive, localization, and background work");
    Console.WriteLine("[PASS] repeated Skills & Connectors close disposes every settings page");
    Console.WriteLine("[PASS] Provider Models disposal cancels refresh and detaches configuration subscription");
    Console.WriteLine("[PASS] Onboarding, Logs, and Knowledge Base release their long-lived subscriptions");
}

static int CountThemeSubscriptions(object target)
{
    var eventField = typeof(App).GetField("ThemeChanged", BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Unable to inspect the App theme event.");
    return (eventField.GetValue(null) as Delegate)?.GetInvocationList()
        .Count(handler => ReferenceEquals(handler.Target, target)) ?? 0;
}

static void TestConcreteConfigServiceIdentity()
{
    var root = Path.Combine(Path.GetTempPath(), $"athena-config-identity-{Guid.NewGuid():N}");
    Directory.CreateDirectory(root);
    try
    {
        var service = new ConfigService(new TemporaryPathService(root));
        var first = service.Load();
        var second = service.Load();
        var third = service.LoadAsync().GetAwaiter().GetResult();
        if (!ReferenceEquals(first, second) || !ReferenceEquals(first, third))
            throw new InvalidOperationException("ConfigService returned multiple default instances before the first save.");
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }

    Console.WriteLine("[PASS] ConfigService owns one default instance before the first save");
}

static void AssertSaveCount(HeadlessConfigService service, int expected, string scenario)
{
    if (service.SaveCount != expected)
        throw new InvalidOperationException($"{scenario} expected {expected} save(s), got {service.SaveCount}.");
}

static void TestLayoutSaveDoesNotReapplyRuntimeClients()
{
    var provider = new OpenAiProviderConfiguration
    {
        Id = "runtime-provider",
        DisplayName = "Runtime provider",
        ProviderPreset = "OpenAI",
        BaseUrl = "https://example.invalid/v1",
        ApiKey = "test-key"
    };
    var config = new AppConfig { Theme = "Light", Timeout = 60 };
    config.AiModels.Providers.Add(provider);
    config.AiModels.MainConversation.ProviderId = provider.Id;
    config.AiModels.MainConversation.Model = "chat-model";
    config.AiModels.Embedding.ProviderId = provider.Id;
    config.AiModels.Embedding.Model = "embedding-model";

    var configService = new HeadlessConfigService(config);
    var chatService = new HeadlessChatService();
    var embeddingService = new HeadlessEmbeddingService();
    var themeApplyCount = 0;
    void OnThemeChanged(string _) => themeApplyCount++;
    App.ThemeChanged += OnThemeChanged;
    try
    {
        using var applier = new AppConfigurationApplier(
            configService,
            chatService,
            embeddingService,
            knowledgeBaseService: null,
            tokenService: null,
            localizationService: null);

        if (themeApplyCount != 1
            || chatService.UpdateConfigCount != 1
            || embeddingService.UpdateConfigCount != 1)
            throw new InvalidOperationException("Initial runtime configuration was not applied exactly once.");

        config.MainLayout.LeftWidth += 20;
        configService.SaveAsync(config).GetAwaiter().GetResult();
        if (themeApplyCount != 1
            || chatService.UpdateConfigCount != 1
            || embeddingService.UpdateConfigCount != 1)
            throw new InvalidOperationException("Saving layout changes reapplied an unchanged runtime subsystem.");

        config.AiModels.MainConversation.Model = "chat-model-2";
        configService.SaveAsync(config).GetAwaiter().GetResult();
        if (chatService.UpdateConfigCount != 2 || embeddingService.UpdateConfigCount != 1)
            throw new InvalidOperationException("A main-model change did not update only the chat runtime.");

        config.AiModels.Embedding.Model = "embedding-model-2";
        configService.SaveAsync(config).GetAwaiter().GetResult();
        if (chatService.UpdateConfigCount != 2 || embeddingService.UpdateConfigCount != 2)
            throw new InvalidOperationException("An embedding-model change did not update only the embedding runtime.");
    }
    finally
    {
        App.ThemeChanged -= OnThemeChanged;
    }

    Console.WriteLine("[PASS] layout saves do not reapply unchanged AI runtime clients");
}

static async Task TestWorkspaceGitDiffAsync()
{
    var root = Path.Combine(Path.GetTempPath(), "athena-workspace-diff-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    WorkspaceWorkbenchViewModel? workbench = null;
    try
    {
        File.WriteAllText(Path.Combine(root, "clean.txt"), "中文保持不变\n");
        File.WriteAllText(Path.Combine(root, "modified.txt"), "中文保持不变\nbefore\n");
        Directory.CreateDirectory(Path.Combine(root, "folder"));
        File.WriteAllText(Path.Combine(root, "folder", "child.txt"), "child\n");
        RunGitForWorkspaceTest(root, "init", "--quiet");
        RunGitForWorkspaceTest(root, "add", ".");
        RunGitForWorkspaceTest(
            root,
            "-c", "user.name=Athena Test",
            "-c", "user.email=athena@example.invalid",
            "commit", "--quiet", "-m", "baseline");
        File.WriteAllText(Path.Combine(root, "modified.txt"), "中文保持不变\nafter\n");
        File.WriteAllText(Path.Combine(root, "added.txt"), "new\n");

        var interaction = new HeadlessInteractionService(confirmResult: true);
        workbench = new WorkspaceWorkbenchViewModel(
            new WorkspaceOperationCoordinator(),
            new HeadlessPathService(),
            interaction);
        await workbench.SetWorkspaceAsync(new WorkspaceProfile
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = "Git diff fixture",
            DirectoryPath = root
        });

        await workbench.OpenFileCommand.ExecuteAsync(
            workbench.Files.Single(node => node.Name == "clean.txt"));
        if (workbench.SelectedEditorTab?.CanDiff != false)
            throw new InvalidOperationException("A Git-clean file must not expose workspace diff mode.");

        var expandedFolder = workbench.Files.Single(node => node.Name == "folder");
        expandedFolder.IsExpanded = true;
        var treeResetCount = 0;
        workbench.Files.CollectionChanged += (_, args) =>
        {
            if (args.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset)
                treeResetCount++;
        };
        await workbench.RefreshFilesCommand.ExecuteAsync(null);
        if (!workbench.Files.Single(node => node.Name == "folder").IsExpanded)
            throw new InvalidOperationException("Refreshing the workspace tree did not preserve expanded folders.");
        if (treeResetCount != 0 || !ReferenceEquals(expandedFolder, workbench.Files.Single(node => node.Name == "folder")))
            throw new InvalidOperationException("Refreshing an unchanged workspace tree must preserve node identity without resetting the collection.");

        await workbench.OpenFileCommand.ExecuteAsync(
            workbench.Files.Single(node => node.Name == "modified.txt"));
        var modifiedTab = workbench.SelectedEditorTab
                          ?? throw new InvalidOperationException("Modified Git fixture did not open.");
        if (!modifiedTab.CanDiff)
            throw new InvalidOperationException("A modified tracked file must expose workspace diff mode.");
        await workbench.RefreshDiffCommand.ExecuteAsync(modifiedTab);
        if (modifiedTab.DiffAddedCount != 1 || modifiedTab.DiffRemovedCount != 1)
            throw new InvalidOperationException("Unchanged Chinese text was incorrectly counted in the Git diff.");
        if (!modifiedTab.DiffLines.Any(
                line => line.Kind == WorkspaceDiffLineKind.Unchanged && line.Text == "中文保持不变"))
            throw new InvalidOperationException("Committed Chinese text was not decoded as UTF-8 in the Git baseline.");

        await workbench.OpenFileCommand.ExecuteAsync(
            workbench.Files.Single(node => node.Name == "added.txt"));
        var addedTab = workbench.SelectedEditorTab
                       ?? throw new InvalidOperationException("Untracked Git fixture did not open.");
        if (!addedTab.CanDiff)
            throw new InvalidOperationException("An untracked file must expose workspace diff mode.");
        await workbench.RefreshDiffCommand.ExecuteAsync(addedTab);
        if (addedTab.DiffAddedCount == 0 || addedTab.DiffRemovedCount != 0)
            throw new InvalidOperationException("Untracked file must render as added content against an empty HEAD baseline.");

        if (!workbench.HasGitRepository
            || string.IsNullOrWhiteSpace(workbench.CurrentBranchName)
            || workbench.GitChanges.Count != 2)
            throw new InvalidOperationException(
                $"Repository detection did not publish the branch and complete changed-file list "
                + $"(repo={workbench.HasGitRepository}, branch={workbench.CurrentBranchName}, "
                + $"changes={workbench.GitChanges.Count}, status={workbench.GitStatusText}).");

        await workbench.ToggleReviewCommand.ExecuteAsync(null);
        if (!workbench.IsReviewVisible)
            throw new InvalidOperationException("The branch selector did not open the changes review.");
        await workbench.ToggleReviewCommand.ExecuteAsync(null);
        if (workbench.IsReviewVisible)
            throw new InvalidOperationException("A second branch-selector activation did not close the changes review.");

        var selectedModifiedChange = workbench.GitChanges.Single(change => change.RelativePath == "modified.txt");
        workbench.SelectedGitChange = selectedModifiedChange;
        var selectionDeadline = DateTime.UtcNow.AddSeconds(5);
        while (workbench.SelectedEditorTab?.RelativePath != "modified.txt")
        {
            if (DateTime.UtcNow >= selectionDeadline)
                throw new InvalidOperationException("Selecting a review change did not open its editor diff tab.");
            await Task.Delay(25);
        }

        workbench.SelectedEditorTab = addedTab;
        await workbench.RefreshWorkbenchCommand.ExecuteAsync(null);
        await Task.Delay(100);
        if (!ReferenceEquals(workbench.SelectedEditorTab, addedTab))
            throw new InvalidOperationException("Refreshing Git state stole editor focus back to the selected review change.");

        workbench.SelectedEditorTab = modifiedTab;
        await workbench.CloseEditorTabCommand.ExecuteAsync(modifiedTab);
        if (workbench.SelectedGitChange != null)
            throw new InvalidOperationException("Closing a review-opened tab did not release its review selection.");
        await workbench.RefreshWorkbenchCommand.ExecuteAsync(null);
        await Task.Delay(100);
        if (workbench.EditorTabs.Any(tab => tab.RelativePath == "modified.txt"))
            throw new InvalidOperationException("A closed review-opened tab was reopened by a Git state refresh.");

        using (var singleTabWorkbench = new WorkspaceWorkbenchViewModel(
                   new WorkspaceOperationCoordinator(),
                   new HeadlessPathService(),
                   new HeadlessInteractionService()))
        {
            if (singleTabWorkbench.IsEditorVisible)
                throw new InvalidOperationException("An editor pane without tabs must start closed.");
            var onlyTab = new WorkspaceEditorTabViewModel
            {
                FullPath = Path.Combine(root, "clean.txt"),
                RelativePath = "clean.txt"
            };
            onlyTab.ReplaceFromDisk("clean", DateTime.UtcNow);
            singleTabWorkbench.EditorTabs.Add(onlyTab);
            singleTabWorkbench.SelectedEditorTab = onlyTab;
            singleTabWorkbench.IsEditorVisible = true;
            await singleTabWorkbench.CloseEditorTabCommand.ExecuteAsync(onlyTab);
            if (singleTabWorkbench.IsEditorVisible
                || singleTabWorkbench.SelectedEditorTab != null
                || singleTabWorkbench.EditorTabs.Count != 0)
                throw new InvalidOperationException("Closing the last editor tab did not close the editor pane.");
        }

        var addedChange = workbench.GitChanges.Single(change => change.RelativePath == "added.txt");
        await workbench.StageFileCommand.ExecuteAsync(addedChange);
        var stagedAddition = workbench.GitChanges.Single(change => change.RelativePath == "added.txt");
        if (!stagedAddition.HasStagedChange || !workbench.HasStagedChanges)
            throw new InvalidOperationException("Per-file staging did not update the review state.");
        await workbench.RestoreFileCommand.ExecuteAsync(stagedAddition);
        if (File.Exists(Path.Combine(root, "added.txt"))
            || workbench.GitChanges.Any(change => change.RelativePath == "added.txt"))
            throw new InvalidOperationException("Restoring a staged addition did not remove it from both index and working tree.");
        if (interaction.LastShowDontAskAgain != false)
            throw new InvalidOperationException("Restore confirmation must not expose a persistent don't-ask-again option.");

        var modifiedChange = workbench.GitChanges.Single(change => change.RelativePath == "modified.txt");
        await workbench.RestoreFileCommand.ExecuteAsync(modifiedChange);
        if (File.ReadAllText(Path.Combine(root, "modified.txt")) != "中文保持不变\nbefore\n"
            || workbench.GitChanges.Count != 0)
            throw new InvalidOperationException("Restoring a tracked change did not return the file and review state to HEAD.");

        Console.WriteLine("[PASS] workspace diff is offered only for tracked changes and untracked files");
    }
    finally
    {
        workbench?.Dispose();
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(root, recursive: true);
    }
}

static void RunGitForWorkspaceTest(string workingDirectory, params string[] arguments)
{
    var start = new ProcessStartInfo("git")
    {
        WorkingDirectory = workingDirectory,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true
    };
    foreach (var argument in arguments) start.ArgumentList.Add(argument);
    using var process = Process.Start(start)
                        ?? throw new InvalidOperationException("Unable to start Git for workspace diff test.");
    var standardError = process.StandardError.ReadToEnd();
    process.WaitForExit();
    if (process.ExitCode != 0)
        throw new InvalidOperationException($"Git workspace diff fixture failed: {standardError}");
}

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

sealed class TemporaryPathService(string root) : IPlatformPathService
{
    public string GetAppDataDirectory() => root;
    public string GetConfigFilePath() => Path.Combine(root, "config.json");
    public string GetLogDirectory() => Path.Combine(root, "logs");
    public string GetKnowledgeBaseDirectory() => Path.Combine(root, "knowledge");
    public string GetHistoryDirectory() => Path.Combine(root, "history");
    public string GetPendingArchiveDirectory() => Path.Combine(root, "pending");
    public string GetAttachmentDirectory() => Path.Combine(root, "attachments");
    public string GetImageGenerationSessionDirectory() => Path.Combine(root, "images");
    public string GetTaskSchedulerFilePath() => Path.Combine(root, "tasks.json");
    public string GetVectorStoreFilePath() => Path.Combine(root, "vectors.db");
    public string GetWorkspacesDirectory() => Path.Combine(root, "workspaces");
    public string GetWorkspaceKnowledgeDirectory(string workspaceId) => Path.Combine(root, workspaceId);
}

sealed class HeadlessConfigService(AppConfig initial) : IConfigService
{
    private AppConfig _current = initial;
    private EventHandler<AppConfig>? _configChanged;

    public int SaveCount { get; private set; }
    public int ConfigSubscriberCount { get; private set; }
    public string ConfigFilePath => "/tmp/athena-headless-config.json";
    public event EventHandler<AppConfig>? ConfigChanged
    {
        add
        {
            _configChanged += value;
            ConfigSubscriberCount++;
        }
        remove
        {
            _configChanged -= value;
            ConfigSubscriberCount--;
        }
    }

    public Task<AppConfig> LoadAsync() => Task.FromResult(_current);
    public AppConfig Load() => _current;

    public Task SaveAsync(AppConfig config)
    {
        _current = config;
        SaveCount++;
        _configChanged?.Invoke(this, config);
        return Task.CompletedTask;
    }

    public void PublishExternal(AppConfig config)
    {
        _current = config;
        _configChanged?.Invoke(this, config);
    }

    public void ResetSaveCount() => SaveCount = 0;
}

sealed class HeadlessKnowledgeMaintenanceService : IKnowledgeBaseMaintenanceService
{
    public int SubscriberCount { get; private set; }
    public KnowledgeMaintenanceState State { get; } = new();
    public bool IsRunning => false;

    public event EventHandler? StateChanged
    {
        add
        {
            SubscriberCount++;
        }
        remove
        {
            SubscriberCount--;
        }
    }

    public void Start()
    {
    }

    public void Stop()
    {
    }

    public Task<KnowledgeMaintenanceState> RunNowAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(State);
}

sealed class HeadlessModelCatalogService : IModelCatalogService
{
    private static readonly ModelCatalogResult Empty = ModelCatalogResult.Ok([]);
    public Task<ModelCatalogResult> GetModelsAsync(string? baseUrl, string? apiKey, CancellationToken cancellationToken = default) => Task.FromResult(Empty);
    public Task<ModelCatalogResult> GetTextModelsAsync(string? baseUrl, string? apiKey, CancellationToken cancellationToken = default) => Task.FromResult(Empty);
    public Task<ModelCatalogResult> GetEmbeddingModelsAsync(string? baseUrl, string? apiKey, CancellationToken cancellationToken = default) => Task.FromResult(Empty);
}

sealed class BlockingModelCatalogService : IModelCatalogService
{
    public TaskCompletionSource Started { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool WasCancelled { get; private set; }

    public async Task<ModelCatalogResult> GetModelsAsync(
        string? baseUrl,
        string? apiKey,
        CancellationToken cancellationToken = default)
    {
        Started.TrySetResult();
        using var registration = cancellationToken.Register(() => WasCancelled = true);
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return ModelCatalogResult.Ok([]);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
    }

    public Task<ModelCatalogResult> GetTextModelsAsync(
        string? baseUrl,
        string? apiKey,
        CancellationToken cancellationToken = default) =>
        GetModelsAsync(baseUrl, apiKey, cancellationToken);

    public Task<ModelCatalogResult> GetEmbeddingModelsAsync(
        string? baseUrl,
        string? apiKey,
        CancellationToken cancellationToken = default) =>
        GetModelsAsync(baseUrl, apiKey, cancellationToken);
}

sealed class HeadlessLocalizationService : ILocalizationService
{
    private EventHandler? _languageChanged;

    public int LanguageSubscriberCount { get; private set; }
    public string CurrentLanguage => "en-US";
    public IReadOnlyList<string> AvailableLanguages => ["en-US"];
    public IReadOnlyList<string> AvailableLanguageNames => ["English"];
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged
    {
        add { }
        remove { }
    }
    public event EventHandler? LanguageChanged
    {
        add
        {
            _languageChanged += value;
            LanguageSubscriberCount++;
        }
        remove
        {
            _languageChanged -= value;
            LanguageSubscriberCount--;
        }
    }

    public int GetLanguageIndex(string languageCode) => languageCode == "en-US" ? 0 : -1;
    public void SwitchLanguage(string languageCode) => _languageChanged?.Invoke(this, EventArgs.Empty);
    public string GetString(string key) => key;
    public string GetString(string key, string defaultValue) => defaultValue;
}

sealed class HeadlessWebSearchService : IWebSearchService
{
    public (bool Success, string Message) Result { get; set; } = (true, "ok");
    public bool IsConfigured => true;
    public Task<List<WebSearchResult>> SearchAsync(string query, int maxResults = 5, CancellationToken cancellationToken = default) =>
        Task.FromResult(new List<WebSearchResult>());
    public Task<(bool Success, string Message)> TestConnectionAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(Result);
}

sealed class HeadlessSystemAudioService : ISystemAudioService
{
    public bool IsSupported => true;
    public bool WasCancelled { get; private set; }

    public async Task<SystemAudioResult> PlayFileAsync(string filePath, CancellationToken cancellationToken = default)
    {
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            return new SystemAudioResult { Success = true };
        }
        catch (OperationCanceledException)
        {
            WasCancelled = true;
            throw;
        }
    }
}

sealed class HeadlessChatService : IChatService
{
    public AudioOutputTestResult AudioResult { get; set; } = new() { Success = true, Message = "ok" };
    public int UpdateConfigCount { get; private set; }

    public async IAsyncEnumerable<string> StreamMessageAsync(
        string userMessage,
        ConversationContext context,
        IReadOnlyList<ChatAttachment>? attachments = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default,
        Action<ChatMessage>? onMessageAdded = null,
        Action<string, int>? onContextCompressed = null,
        Action<TokenUsageSnapshot>? onUsageReported = null,
        Action<string>? onToolCallArgumentsStreaming = null,
        bool addToContext = true)
    {
        await Task.CompletedTask;
        yield break;
    }

    public Task<(bool Success, string? Message)> TestConnectionAsync() => Task.FromResult<(bool, string?)>((true, "ok"));
    public IReadOnlyList<RawContextEntry> BuildRawContext(ConversationContext context) => [];
    public void UpdateConfig(AppConfig config) => UpdateConfigCount++;
    public Task<AudioOutputTestResult> TestAudioOutputAsync(CancellationToken cancellationToken = default) => Task.FromResult(AudioResult);
    public Task<(ChatAttachment? Attachment, string ErrorMessage)> GenerateAssistantSpeechAsync(string text, CancellationToken cancellationToken = default) =>
        Task.FromResult<(ChatAttachment?, string)>((null, string.Empty));
}

sealed class HeadlessEmbeddingService : IEmbeddingService
{
    public int UpdateConfigCount { get; private set; }
    public bool IsConfigured => true;
    public string? ModelId => "headless-embedding";

    public void UpdateConfig(AppConfig config) => UpdateConfigCount++;
    public Task<float[]?> GenerateEmbeddingAsync(string text) => Task.FromResult<float[]?>([]);
    public Task<List<float[]?>> GenerateEmbeddingsAsync(IEnumerable<string> texts) =>
        Task.FromResult(texts.Select(_ => (float[]?)[]).ToList());
    public float CosineSimilarity(float[] a, float[] b) => 0;
    public Task<(bool Success, string Message)> TestConnectionAsync() =>
        Task.FromResult((true, "ok"));
}

sealed class HeadlessInteractionService(bool confirmResult = false) : IUserInteractionService
{
    public bool? LastShowDontAskAgain { get; private set; }

    public Task<bool> ConfirmAsync(
        string title,
        string message,
        string confirmText,
        string cancelText,
        bool showDontAskAgain = true)
    {
        LastShowDontAskAgain = showDontAskAgain;
        return Task.FromResult(confirmResult);
    }
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

sealed class HeadlessArchiveService(HeadlessConversationStore store) : IConversationArchiveService
{
    private EventHandler<ConversationArchiveResultEventArgs>? _archiveStaged;
    private EventHandler<ConversationArchiveResultEventArgs>? _archiveCompleted;
    private EventHandler<ConversationArchiveResultEventArgs>? _archiveFailed;

    public int StagedSubscriberCount { get; private set; }
    public int CompletedSubscriberCount { get; private set; }
    public int FailedSubscriberCount { get; private set; }

    public event EventHandler<ConversationArchiveResultEventArgs>? ArchiveStaged
    {
        add { _archiveStaged += value; StagedSubscriberCount++; }
        remove { _archiveStaged -= value; StagedSubscriberCount--; }
    }
    public event EventHandler<ConversationArchiveResultEventArgs>? ArchiveCompleted
    {
        add { _archiveCompleted += value; CompletedSubscriberCount++; }
        remove { _archiveCompleted -= value; CompletedSubscriberCount--; }
    }
    public event EventHandler<ConversationArchiveResultEventArgs>? ArchiveFailed
    {
        add { _archiveFailed += value; FailedSubscriberCount++; }
        remove { _archiveFailed -= value; FailedSubscriberCount--; }
    }

    public Task<List<ConversationHistoryItem>> LoadAllAsync() => store.LoadAllAsync();
    public Task<ConversationHistoryItem?> LoadByIdAsync(string id) => store.LoadByIdAsync(id);
    public Task DeleteAsync(string id) => store.DeleteAsync(id);
    public void SaveDraft(ConversationDraftSnapshot snapshot) { }
    public ConversationDraftSnapshot? LoadDraft() => null;
    public void DeleteDraft() { }

    public Task StageArchiveAsync(ConversationArchiveSnapshot snapshot, CancellationToken ct = default)
    {
        PublishStaged(snapshot);
        return Task.CompletedTask;
    }

    public void PublishStaged(ConversationArchiveSnapshot snapshot) =>
        _archiveStaged?.Invoke(this, new ConversationArchiveResultEventArgs(snapshot, "/tmp/archive-staged.json"));

    public void PublishCompleted(ConversationArchiveSnapshot snapshot, ConversationHistoryItem history) =>
        _archiveCompleted?.Invoke(this, new ConversationArchiveResultEventArgs(snapshot, "/tmp/archive-staged.json", history));

    public void PublishFailed(ConversationArchiveSnapshot snapshot) =>
        _archiveFailed?.Invoke(
            this,
            new ConversationArchiveResultEventArgs(
                snapshot,
                "/tmp/archive-staged.json",
                exception: new InvalidOperationException("characterized failure")));
}

sealed class HeadlessWorkspaceService(List<WorkspaceProfile> workspaces) : IWorkspaceService
{
    public WorkspaceProfile? ActiveWorkspace { get; private set; }
    public event EventHandler<WorkspaceProfile?>? ActiveWorkspaceChanged;

    public Task<List<WorkspaceProfile>> LoadAllAsync() => Task.FromResult(workspaces.ToList());
    public Task<WorkspaceProfile?> LoadByIdAsync(string id) =>
        Task.FromResult(workspaces.FirstOrDefault(workspace => workspace.Id == id));
    public Task SaveAsync(WorkspaceProfile workspace)
    {
        var existing = workspaces.FindIndex(candidate => candidate.Id == workspace.Id);
        if (existing >= 0) workspaces[existing] = workspace;
        else workspaces.Add(workspace);
        return Task.CompletedTask;
    }
    public Task<bool> DeleteAsync(string id) =>
        Task.FromResult(workspaces.RemoveAll(workspace => workspace.Id == id) > 0);
    public Task<WorkspaceProfile?> FindByDirectoryAsync(string directoryPath) =>
        Task.FromResult(workspaces.FirstOrDefault(workspace => workspace.DirectoryPath == directoryPath));
    public void SetActiveWorkspace(WorkspaceProfile? workspace)
    {
        ActiveWorkspace = workspace;
        ActiveWorkspaceChanged?.Invoke(this, workspace);
    }
    public string GetKnowledgeFilePath(WorkspaceProfile workspace) => $"/tmp/{workspace.Id}/workspace.md";
    public Task<string?> GetKnowledgeFilePathAsync(string workspaceId) =>
        Task.FromResult<string?>($"/tmp/{workspaceId}/workspace.md");
    public string? BuildWorkspaceKnowledgeContext(string workspaceId, string? knowledgeFilePath, int tokenBudget) => null;
    public Task EnforceKnowledgeFileBudgetAsync(string fullPath, CancellationToken ct = default) => Task.CompletedTask;
}
