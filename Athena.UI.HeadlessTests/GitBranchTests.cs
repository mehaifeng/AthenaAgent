using Athena.UI.Models;
using Athena.UI;
using Athena.UI.Services;
using Athena.UI.Services.Interfaces;
using Athena.UI.ViewModels;
using Athena.UI.Views;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.Styling;
using Avalonia.VisualTree;
using System.Diagnostics;

internal static class GitBranchTests
{
    public static void Run(string outputPath)
    {
        Task.Run(TestServiceAsync).GetAwaiter().GetResult();
        TestWorkbenchAndWindows(outputPath);
        Console.WriteLine("[PASS] Git branches: real repositories, worktrees, activity exclusion, view models and bilingual windows");
    }

    private static async Task TestServiceAsync()
    {
        var parent = Path.Combine(Path.GetTempPath(), "athena-branches-" + Guid.NewGuid().ToString("N"));
        var root = Path.Combine(parent, "仓库 with spaces");
        Directory.CreateDirectory(root);
        var activity = new RepositoryActivityGate();
        var service = new GitBranchService(activity);
        try
        {
            Init(root);
            Directory.CreateDirectory(Path.Combine(root, "sub"));
            File.WriteAllText(Path.Combine(root, "sub", "keep.txt"), "keep");
            Git(root, "add", "."); Git(root, "commit", "-qm", "subdirectory");
            Git(root, "remote", "add", "origin", root);
            Git(root, "update-ref", "refs/remotes/origin/main", "HEAD");
            Git(root, "symbolic-ref", "refs/remotes/origin/HEAD", "refs/remotes/origin/main");
            var initial = await service.ReadAsync(Path.Combine(root, "sub"));
            Check(initial.RepositoryRoot == root && initial.Branches.Single(b => b.Name == "main").IsDefault, "repository root and default reference");
            await service.CreateAsync(root, "feature/中文", "HEAD", false);
            Check((await service.ReadAsync(root)).CurrentName == "main", "create without switching");
            await service.SwitchAsync(root, await Branch("feature/中文"), "");
            await service.RenameAsync(root, await Branch("feature/中文"), "feature/renamed");
            Check((await service.ReadAsync(root)).CurrentName == "feature/renamed", "rename current branch");
            File.WriteAllText(Path.Combine(root, "file.txt"), "feature content\n");
            File.WriteAllText(Path.Combine(root, "large.txt"), new string('x', 180_000));
            Git(root, "add", "."); Git(root, "commit", "-qm", "feature commit");
            var feature = await Branch("feature/renamed");
            await service.SwitchAsync(root, await Branch("main"), "");
            var comparison = await service.CompareAsync(root, feature);
            Check(comparison.Text.Contains("+feature content", StringComparison.Ordinal), "compare includes actual patch contents");
            Check(comparison.IsTruncated && comparison.Text.Length == 120_000, "large diffs are bounded and explicitly marked");
            await Error("Unmerged", () => service.DeleteAsync(root, feature));
            foreach (var name in new[] { "-force", "@{-1}", "HEAD", "bad..name", "with space", "refs/heads/nested" })
                await Error("InvalidName", () => service.CreateAsync(root, name, "HEAD", false));
            await Error("Duplicate", () => service.CreateAsync(root, "main", "HEAD", false));

            File.WriteAllText(Path.Combine(root, "outside.txt"), "untracked");
            await Error("Dirty", () => service.SwitchAsync(Path.Combine(root, "sub"), feature, ""));
            Check(File.ReadAllText(Path.Combine(root, "outside.txt")) == "untracked", "untracked content survives refusal");
            await service.CreateAsync(root, "create-while-dirty", "HEAD", false);
            File.Delete(Path.Combine(root, "outside.txt"));
            File.WriteAllText(Path.Combine(root, "file.txt"), "staged content\n");
            Git(root, "add", "file.txt");
            await Error("Dirty", () => service.CreateAsync(root, "must-not-exist", "HEAD", true));
            Check(Git(root, "show", ":file.txt") == "staged content\n", "index survives refused switch");
            File.WriteAllText(Path.Combine(root, "file.txt"), "main content\n"); Git(root, "add", "file.txt");
            File.WriteAllText(Path.Combine(root, ".git", "MERGE_HEAD"), Git(root, "rev-parse", "HEAD"));
            await Error("InProgress", () => service.CreateAsync(root, "merge-busy", "HEAD", false));
            File.Delete(Path.Combine(root, ".git", "MERGE_HEAD"));

            var worktree = Path.Combine(parent, "另一工作树");
            Git(root, "worktree", "add", "-qb", "occupied", worktree, "main");
            var occupied = await Branch("occupied");
            Check(occupied.WorktreePath == worktree, "NUL-delimited worktree paths");
            await Error("Occupied", () => service.SwitchAsync(root, occupied, ""));
            await Error("Occupied", () => service.RenameAsync(root, occupied, "must-not-rename"));
            await Error("Occupied", () => service.DeleteAsync(root, occupied));
            await Error("Protected", () => service.DeleteAsync(root, initial.Branches.Single(b => b.Name == "main")));
            await Error("Protected", () => service.RenameAsync(root, initial.Branches.Single(b => b.Name == "main"), "renamed-base"));
            Git(root, "branch", "-f", "feature/renamed", "main");
            await Error("Stale", () => service.RenameAsync(root, feature, "stale-change"));
            await service.CreateAsync(root, "cleanup", "HEAD", false);
            Git(root, "update-ref", "refs/remotes/origin/cleanup", "HEAD");
            await service.DeleteAsync(root, await Branch("cleanup"));
            Check((await service.ReadAsync(root)).Branches.Any(b => b.Name == "origin/cleanup"), "local deletion preserves remote ref");

            Git(root, "update-ref", "refs/remotes/origin/tracking", "HEAD");
            await service.SwitchAsync(root, await Branch("origin/tracking"), "local-tracking");
            Check(Git(root, "rev-parse", "--abbrev-ref", "@{upstream}").Trim() == "origin/tracking", "remote selection establishes upstream");
            Git(root, "switch", "--detach", "HEAD");
            Check((await service.ReadAsync(root)).CurrentReference == null, "detached HEAD is explicit");
            await service.CreateAsync(root, "from-detached", "HEAD", true);
            Check((await service.ReadAsync(root)).CurrentName == "from-detached", "create from detached HEAD");

            using (await activity.EnterTurnAsync(Path.Combine(root, "sub"), CancellationToken.None))
                await Error("Busy", () => service.CreateAsync(root, "during-turn", "HEAD", false));
            if (!OperatingSystem.IsWindows())
            {
                var alias = Path.Combine(parent, "repo-alias"); Directory.CreateSymbolicLink(alias, root);
                using (await activity.EnterTurnAsync(alias, CancellationToken.None))
                    await Error("Busy", () => service.CreateAsync(root, "during-alias-turn", "HEAD", false));
            }
            using (await activity.EnterTurnAsync(null, CancellationToken.None))
                await Error("Busy", () => service.CreateAsync(root, "during-global-turn", "HEAD", false));
            var mutation = activity.EnterMutation(root);
            var waitingTurn = activity.EnterTurnAsync(root, CancellationToken.None);
            Check(!waitingTurn.IsCompleted, "a new conversation waits for an in-flight mutation");
            using var cancelled = new CancellationTokenSource();
            var cancelledTurn = activity.EnterTurnAsync(root, cancelled.Token);
            cancelled.Cancel();
            try { await cancelledTurn; throw new InvalidOperationException("cancelled turn was admitted"); }
            catch (OperationCanceledException) { }
            mutation.Dispose();
            using (await waitingTurn.WaitAsync(TimeSpan.FromSeconds(2))) { }
            using (activity.EnterMutation(root)) { }
            var unborn = Path.Combine(parent, "unborn"); Directory.CreateDirectory(unborn); Git(unborn, "init", "-qb", "main");
            Check(!(await service.ReadAsync(unborn)).HasCommits, "empty repository lists without crashing");
            await Error("Unborn", () => service.CreateAsync(unborn, "new", "HEAD", true));

            async Task<GitBranchInfo> Branch(string name) => (await service.ReadAsync(root)).Branches.Single(b => b.Name == name);
        }
        finally { Directory.Delete(parent, true); }
    }

    private static void TestWorkbenchAndWindows(string outputPath)
    {
        var root = Path.Combine(Path.GetTempPath(), "athena-branch-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var localization = (ILocalizationService)App.Services!.GetService(typeof(ILocalizationService))!;
        localization.SwitchLanguage("zh-CN");
        try
        {
            Init(root);
            Git(root, "switch", "-c", "feature/ui");
            File.WriteAllText(Path.Combine(root, "file.txt"), "new branch content\n");
            Git(root, "add", "."); Git(root, "commit", "-qm", "feature UI content");
            Git(root, "switch", "main");
            var service = new GitBranchService(new RepositoryActivityGate());
            using var watcher = new WorkspaceWatcherService();
            using var workbench = new WorkspaceWorkbenchViewModel(new WorkspaceOperationCoordinator(), new HeadlessPathService(),
                new HeadlessInteractionService(), watcher, service, localizationService: localization);
            Wait(workbench.SetWorkspaceAsync(new WorkspaceProfile { Name = "Branch fixture", DirectoryPath = root }));
            Wait(workbench.OpenFileByPathAsync(Path.Combine(root, "file.txt")));
            using (var create = workbench.CreateBranchManager(GitBranchDialogMode.Create))
            {
                Wait(create.RefreshCommand.ExecuteAsync(null));
                create.NewBranchName = "feature/created";
                create.SwitchAfterCreate = false;
                workbench.SelectedEditorTab!.Text = "unsaved editor";
                Wait(create.CreateCommand.ExecuteAsync(null));
                Check(create.ErrorText.Contains("尚未保存", StringComparison.Ordinal), "unsaved editor blocks mutation");
                workbench.SelectedEditorTab.CancelEdits();
                Wait(create.CreateCommand.ExecuteAsync(null));
                Check(!create.HasError && workbench.CurrentBranchName == "main", "create-only refreshes without changing checkout");
            }
            using (var manage = workbench.CreateBranchManager(GitBranchDialogMode.Manage))
            {
                Wait(manage.RefreshCommand.ExecuteAsync(null));
                manage.SelectedBranch = manage.Branches.Single(b => b.Name == "feature/ui");
                Wait(manage.SwitchCommand.ExecuteAsync(null));
                Check(!manage.HasError && workbench.CurrentBranchName == "feature/ui", "switch refreshes composer branch name");
                Check(workbench.SelectedEditorTab?.Text == "new branch content\n", "switch reloads clean editor contents");
                manage.RenameCommand.Execute(null); manage.RenameText = "feature/renamed-ui";
                Wait(manage.SaveRenameCommand.ExecuteAsync(null));
                Check(!manage.HasError && workbench.CurrentBranchName == "feature/renamed-ui", "rename refreshes composer");
                manage.SelectedBranch = manage.Branches.Single(b => b.Name == "feature/created");
                manage.DeleteCommand.Execute(null);
                Check(manage.IsDeleteConfirmVisible, "delete requires explicit confirmation");
                manage.CancelEditCommand.Execute(null);
                Check(Git(root, "branch", "--list", "feature/created").Contains("feature/created", StringComparison.Ordinal), "cancel deletion preserves ref");
                manage.DeleteCommand.Execute(null); Wait(manage.ConfirmDeleteCommand.ExecuteAsync(null));
                Check(!manage.HasError && !manage.Branches.Any(b => b.Name == "feature/created"), "confirm deletion refreshes list");
            }
            TestMainConversationActivity(root, localization);
            TestComposerMenu(workbench);
            foreach (var language in new[] { "zh-CN", "en-US" })
            {
                localization.SwitchLanguage(language);
                foreach (var mode in Enum.GetValues<GitBranchDialogMode>())
                {
                    var vm = workbench.CreateBranchManager(mode);
                    var window = new GitBranchesWindow(vm) { Width = 720, Height = 710 };
                    window.Show(); Pump(() => !vm.IsBusy && vm.CurrentBranch.Length > 0);
                    window.UpdateLayout();
                    Check(!vm.HasError && !window.Title!.StartsWith("Git.", StringComparison.Ordinal), "localized dialog loads");
                    Check(window.GetVisualDescendants().OfType<TextBlock>().Any(block => block.Text == localization.GetString("Git.Branches.Current")), "static labels use the same language as the view model");
                    var file = Path.Combine(Path.GetDirectoryName(outputPath)!, $"git-branches-{language}-{mode}.png");
                    Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                    using (var bitmap = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("No branch window frame"))
                    using (var stream = File.Create(file)) bitmap.Save(stream, PngBitmapEncoderOptions.Default);
                    window.Width = 460; window.Height = 620; Dispatcher.UIThread.RunJobs();
                    Check(window.FindControl<ListBox>("BranchList")!.Bounds.Width <= window.ClientSize.Width, "branch list fits compact window");
                    if (mode == GitBranchDialogMode.Manage && language == "zh-CN")
                    {
                        window.RequestedThemeVariant = ThemeVariant.Dark;
                        window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
                        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                        using var dark = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("No dark branch frame");
                        using var darkStream = File.Create(Path.Combine(Path.GetDirectoryName(outputPath)!, "git-branches-dark-compact.png"));
                        dark.Save(darkStream, PngBitmapEncoderOptions.Default);
                    }
                    window.Close();
                }
            }
        }
        finally { localization.SwitchLanguage("zh-CN"); Directory.Delete(root, true); }
    }

    private static void TestComposerMenu(WorkspaceWorkbenchViewModel workbench)
    {
        using var chat = new MainConversationViewModel();
        var view = new MainConversationView { DataContext = chat, Workbench = workbench };
        var owner = new Window { Content = view, Width = 1000, Height = 720 };
        owner.Show(); Dispatcher.UIThread.RunJobs();
        var button = view.FindControl<Button>("GitBranchMenuButton")!;
        Check(button.IsEffectivelyVisible, "repository menu is visible above the composer");
        var menu = (MenuFlyout)button.Flyout!;
        foreach (var mode in Enum.GetValues<GitBranchDialogMode>())
        {
            menu.ShowAt(button); Dispatcher.UIThread.RunJobs();
            var items = menu.Items.OfType<MenuItem>().ToArray();
            Check(items.Length == 3 && items.All(item => item.IsEnabled), "all three menu entries are enabled");
            items[(int)mode].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Pump(() => owner.OwnedWindows.OfType<GitBranchesWindow>().Any());
            var window = owner.OwnedWindows.OfType<GitBranchesWindow>().Single();
            var vm = (GitBranchManagerViewModel)window.DataContext!;
            Pump(() => !vm.IsBusy && vm.CurrentBranch.Length > 0);
            Check(vm.Mode == mode && !vm.HasError, "composer menu opens the requested branch dialog");
            window.Close(); menu.Hide(); Dispatcher.UIThread.RunJobs();
        }
        owner.Close();
    }

    private static void TestMainConversationActivity(string root, ILocalizationService localization)
    {
        var config = new HeadlessConfigService(new AppConfig());
        var coordinator = new ConversationExecutionCoordinator(config);
        var service = new GitBranchService(coordinator.RepositoryActivity);
        var model = new GatedUsageChatService();
        using var chat = new MainConversationViewModel(model, config, null, null, new TokenService(), localization, executionCoordinator: coordinator);
        chat.AssignWorkspace(new WorkspaceProfile { DirectoryPath = root });
        chat.InputText = "first"; Wait(chat.SendMessageCommand.ExecuteAsync(null));
        chat.InputText = "hold turn";
        var sending = chat.SendMessageCommand.ExecuteAsync(null);
        Pump(() => model.AtFirstGate);
        Wait(Error("Busy", () => service.CreateAsync(root, "during-real-chat", "HEAD", false)));
        model.FirstGate.SetResult(); model.SecondGate.SetResult(); Wait(sending);
        using (coordinator.RepositoryActivity.EnterMutation(root))
        {
            model.AtFirstGate = false;
            chat.InputText = "queued during mutation";
            var waiting = chat.SendMessageCommand.ExecuteAsync(null);
            Pump(() => chat.IsQueued);
            Check(!model.AtFirstGate, "a real conversation does not reach its model during a branch mutation");
            chat.StopResponseCommand.Execute(null);
            Wait(waiting);
        }
        using (coordinator.RepositoryActivity.EnterMutation(root)) { }
    }

    private static void Init(string root)
    {
        Git(root, "init", "-qb", "main"); Git(root, "config", "user.name", "Athena Tests"); Git(root, "config", "user.email", "athena@example.invalid");
        File.WriteAllText(Path.Combine(root, "file.txt"), "main content\n");
        Git(root, "add", "."); Git(root, "commit", "-qm", "initial commit");
    }

    private static string Git(string root, params string[] args)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in args) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(10000)) { process.Kill(true); throw new InvalidOperationException("Git fixture timed out"); }
        if (process.ExitCode != 0) throw new InvalidOperationException(error.GetAwaiter().GetResult());
        return output.GetAwaiter().GetResult();
    }

    private static async Task Error(string expected, Func<Task> operation)
    {
        try { await operation(); throw new InvalidOperationException($"Expected Git refusal: {expected}"); }
        catch (GitBranchException ex) when (ex.Code == expected) { }
    }
    private static void Wait(Task task) { Pump(() => task.IsCompleted); task.GetAwaiter().GetResult(); }
    private static void Pump(Func<bool> done)
    {
        var until = Environment.TickCount64 + 15000;
        while (!done()) { Dispatcher.UIThread.RunJobs(); if (Environment.TickCount64 > until) throw new TimeoutException("Git branch UI did not finish"); Thread.Sleep(2); }
        Dispatcher.UIThread.RunJobs();
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
