using Athena.UI;
using Athena.UI.Models;
using Athena.UI.Services;
using Athena.UI.ViewModels;
using Athena.UI.Views;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Documents;
using TextMateSharp.Grammars;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;

internal static class ThemeContrastTests
{
    public static void Run(string artifactDirectory)
    {
        Directory.CreateDirectory(artifactDirectory);
        var previous = Application.Current!.RequestedThemeVariant;
        var previousScheme = App.CurrentColorScheme;
        try
        {
            VerifyPalettes();
            VerifyLiveControls();
            VerifySettingsGallery(artifactDirectory);
            VerifyShellLayouts(artifactDirectory);
            CaptureGallery(artifactDirectory);
        }
        finally
        {
            App.SetColorScheme(previousScheme);
            Application.Current.RequestedThemeVariant = previous;
            Dispatcher.UIThread.RunJobs();
        }
        Console.WriteLine("[PASS] 9 palettes x light/dark: contrast, live controls, terminal, shell layouts and previews");
    }

    private static void VerifyPalettes()
    {
        var baseline = App.GetColorSchemeDictionaries("Default").Dark;
        var lowest = double.MaxValue;
        string lowestPair = "";
        foreach (var scheme in AppPalettes.Names)
        foreach (var dark in new[] { true, false })
        {
            var pair = App.GetColorSchemeDictionaries(scheme);
            var dict = dark ? pair.Dark : pair.Light;
            foreach (var key in baseline.Keys)
                Check(dict.ContainsKey(key) && dict[key]!.GetType() == baseline[key]!.GetType(), $"{scheme}: missing/type-mismatched {key}");
            void Contrast(string foreground, string background)
            {
                var ratio = Ratio(Colour(dict[foreground]), Colour(dict[background]));
                var label = $"{scheme}/{(dark ? "Dark" : "Light")}: {foreground} on {background}";
                Check(ratio >= 4.5, $"{label} contrast {ratio:F2}, expected >= 4.5");
                if (ratio < lowest) { lowest = ratio; lowestPair = label; }
            }
            foreach (var foreground in new[] { "App.TextPrimary", "App.TextSecondary", "App.TextMuted", "App.EmphasisForeground" })
            foreach (var background in new[] { "App.CanvasBackground", "App.SurfaceBackground", "App.RaisedBackground",
                         "App.SelectedBackground", "App.SelectedBackgroundPointerover", "App.SelectedBackgroundActive" })
                Contrast(foreground, background);
            foreach (var state in new[] { "", "Pointerover", "Active" })
                Contrast("App.InverseForeground", "SemiColorPrimary" + state);
            foreach (var status in new[] { "App.SuccessForeground", "App.WarningForeground", "App.DangerForeground" })
                Contrast(status, "App.RaisedBackground");
            Contrast("App.DangerForeground", "App.DangerBackground");
            Contrast("Chat.UserForeground", "Chat.UserBubbleBg");
            Contrast("Chat.AssistantForeground", "Chat.AssistantBubbleBg");
            Contrast("CodeBlock.ToolButtonForeground", "SecondaryCardBackgroundColor");
            Contrast("CodeInlineColor", "CardBackgroundColor");
            Contrast("Terminal.Foreground", "Terminal.Background");
            Contrast("Editor.Foreground", "Editor.AddedBackground");
            Contrast("Editor.Foreground", "Editor.RemovedBackground");
            foreach (var background in new[] { "History.PendingCardBackground", "History.PendingCardPointeroverBackground", "History.PendingCardSelectedBackground" })
            foreach (var foreground in new[] { "History.PendingTitleForeground", "History.PendingMetaForeground", "History.PendingStatusForeground" })
                Contrast(foreground, background);
        }
        Check(App.NormalizeColorScheme("nOrD") == "Nord" && App.NormalizeColorScheme("unknown") == "Default", "configuration normalization");
        Console.WriteLine($"[PASS] minimum tested text contrast {lowest:F2}:1 ({lowestPair})");
    }

    private static void VerifyLiveControls()
    {
        var body = new TextBlock { Text = "正文 Body" };
        var secondary = new TextBlock { Text = "辅助文字 Metadata" };
        secondary.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("App.TextMuted"));
        var input = new TextBox { Text = "输入 Input", PlaceholderText = "Placeholder" };
        var select = new ListBox { ItemsSource = new[] { "Selected item", "Other item" }, SelectedIndex = 0 };
        var button = new Button { Content = "Action" };
        var tabs = new TabControl { Height = 60, Items = { new TabItem { Header = "Live tab", Content = "Tab content" } }, SelectedIndex = 0 };
        var terminal = new TerminalPanelView { Height = 100 };
        var panel = new StackPanel { Spacing = 8, Children = { body, secondary, input, select, button, tabs, terminal } };
        var window = new Window { Content = panel, Width = 500, Height = 400 };
        window.Show();
        try
        {
            foreach (var scheme in AppPalettes.Names)
            foreach (var variant in new[] { ThemeVariant.Dark, ThemeVariant.Light })
            {
                App.SetColorScheme(scheme);
                Application.Current!.RequestedThemeVariant = variant;
                Dispatcher.UIThread.RunJobs();
                var pair = App.GetColorSchemeDictionaries(scheme);
                var dict = variant == ThemeVariant.Dark ? pair.Dark : pair.Light;
                Check(Colour(window.Background) == Colour(dict["App.CanvasBackground"]), $"{scheme}: window background stale");
                Check(Colour(body.Foreground) == Colour(dict["App.TextPrimary"]), $"{scheme}: inherited body text stale");
                Check(Colour(secondary.Foreground) == Colour(dict["App.TextMuted"]), $"{scheme}: muted text stale");
                Check(Ratio(Colour(input.Foreground), Colour(input.Background)) >= 4.5, $"{scheme}: input text contrast");
                var selected = select.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == "Selected item");
                Check(Ratio(Colour(selected.Foreground), Colour(dict["App.SelectedBackground"])) >= 4.5, $"{scheme}: selected item contrast");
                var tabHeader = tabs.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == "Live tab");
                Check(Colour(tabHeader.Foreground) == Colour(dict["App.EmphasisForeground"]), $"{scheme}: selected tab accent stale");
                var terminalControl = terminal.FindControl<Border>("TerminalHost")!.Child!;
                Check(Colour(terminalControl.Resources["AvaloniaTerminalColor0"]) == Colour(dict["Terminal.Background"]), $"{scheme}: terminal background stale");
                Check(Colour(terminalControl.Resources["AvaloniaTerminalColor7"]) == Colour(dict["Terminal.Foreground"]), $"{scheme}: terminal foreground stale");
            }
        }
        finally { window.Close(); }
    }

    private static void VerifyShellLayouts(string directory)
    {
        var config = new AppConfig();
        using var session = new AppConfigurationSession(new HeadlessConfigService(config));
        using var watcher = new WorkspaceWatcherService();
        using var workbench = new WorkspaceWorkbenchViewModel(new WorkspaceOperationCoordinator(),
            new HeadlessPathService(), new HeadlessInteractionService(), watcher,
            new GitBranchService(new RepositoryActivityGate()));
        using var vm = new MainWindowViewModel(null, null, null, null, null, null, null, null,
            null, null, null, null, null, null, configurationSession: session, workbench: workbench);
        var group = new WorkspaceConversationGroupViewModel(new WorkspaceProfile { Name = "AthenaAgent", DirectoryPath = "/example/AthenaAgent" });
        using var normalChat = new MainConversationViewModel();
        using var pendingChat = new MainConversationViewModel();
        group.Conversations.Add(new ConversationSessionItemViewModel(normalChat, null, null) { Title = "主题与布局检查" });
        group.Conversations.Add(new ConversationSessionItemViewModel(pendingChat, null, null) { Title = "等待确认的工具操作", HasUnreadCompletion = true });
        vm.ConversationGroups.Add(group);
        var window = new MainWindow { DataContext = vm, Width = 1440, Height = 900 };
        window.Show();
        Settle();
        vm.DisplayedConversation.Messages.Add(new ChatMessage { Role = "user", Content = "请检查主题的文字与容器配色。" });
        vm.DisplayedConversation.Messages.Add(new ChatMessage { Role = "assistant", Content = "## 清晰的工作空间\n\n正文、**强调内容**与辅助信息使用同一套语义配色。\n\n> 浅色与深色模式均保持可读性。\n\n`theme.Apply()` · [查看文档](https://example.com)\n\n```csharp\nvar theme = \"Forest\";\nConsole.WriteLine(theme);\n```" });
        using var tab = new WorkspaceEditorTabViewModel { FullPath = "/example/theme.cs", RelativePath = "theme.cs", Mode = WorkspaceEditorMode.Edit };
        tab.ReplaceFromDisk("// 主题资源 / Theme resources\nvar palette = \"Forest\";\nvar contrast = 4.5;\n", DateTime.UtcNow);
        workbench.EditorTabs.Add(tab);
        workbench.SelectedEditorTab = tab;
        workbench.IsEditorVisible = true;
        workbench.Files.Add(new WorkspaceFileNodeViewModel { Name = "theme.cs", RelativePath = "theme.cs", FullPath = "/example/theme.cs" });
        Settle();
        var editor = window.GetVisualDescendants().OfType<TextBox>().Single(x => x.Text?.Contains("Theme resources", StringComparison.Ordinal) == true);
        Check(editor.Bounds.Height > 200 && editor.VerticalContentAlignment == Avalonia.Layout.VerticalAlignment.Top,
            "editor text must fill its pane and start at the top");
        Check(window.GetVisualDescendants().OfType<Border>().Count(b => b.Classes.Contains("message-bubble")) >= 2,
            "shell preview must contain actual user and assistant messages");

        try
        {
            foreach (var scheme in AppPalettes.Names)
            foreach (var variant in new[] { ThemeVariant.Dark, ThemeVariant.Light })
            {
                App.SetColorScheme(scheme);
                Application.Current!.RequestedThemeVariant = variant;
                foreach (var layout in new[] { 0, 1, 2 })
                {
                    if (vm.IsSidePanelsSwapped != (layout == 1)) vm.ToggleSidePanelsCommand.Execute(null);
                    if (vm.IsRightPanelCollapsed != (layout == 2)) vm.ToggleRightPanelCommand.Execute(null);
                    config.MainLayout.PanelGlassEnabled = layout == 1;
                    config.MainLayout.PanelTransparency = layout == 2 ? 0.8 : 0;
                    Settle();
                    var dict = variant == ThemeVariant.Dark ? App.GetColorSchemeDictionaries(scheme).Dark : App.GetColorSchemeDictionaries(scheme).Light;
                    var blocks = window.GetVisualDescendants().OfType<LiveMarkdown.Avalonia.CodeBlock>().ToList();
                    Check(blocks.Count > 0, "shell preview must include a real syntax-highlighted code block");
                    foreach (var block in blocks)
                    {
                        Check(block.ColorTheme == (variant == ThemeVariant.Dark ? ThemeName.DarkPlus : ThemeName.LightPlus),
                            $"{scheme}: code syntax theme must follow the mode");
                        var runs = Runs(block.CodeTextBlock!.Inlines!).Where(r => !string.IsNullOrWhiteSpace(r.Text)).ToList();
                        Check(runs.Count > 0, "syntax highlighting must render text");
                        foreach (var run in runs)
                            Check(Ratio(Colour(run.Foreground), Colour(dict["Editor.Background"])) >= 4.5,
                                $"{scheme}/{variant.Key}: code token '{run.Text}' has insufficient contrast ({Colour(run.Foreground)})");
                    }
                    foreach (var border in window.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("shell-panel")))
                        Check(Colour(border.Background) == Colour(dict["SemiColorBackground0"]), $"{scheme}/{layout}: shell colour stale");
                    // Capture actual controls at full width, swapped with glass and collapsed at minimum width.
                    if (scheme is "Nord" or "Forest" or "Rose" or "Sandstone")
                    {
                        window.Width = layout == 2 ? 1180 : 1440;
                        Settle();
                        using var frame = window.CaptureRenderedFrame();
                        Check(frame is not null, "shell capture");
                        using var output = File.Create(Path.Combine(directory, $"theme-{scheme}-{variant.Key}-{layout}.png"));
                        frame!.Save(output, PngBitmapEncoderOptions.Default);
                    }
                }
            }
        }
        finally { window.Close(); }
    }

    private static void CaptureGallery(string directory)
    {
        foreach (var variant in new[] { ThemeVariant.Dark, ThemeVariant.Light })
        {
            Application.Current!.RequestedThemeVariant = variant;
            App.SetColorScheme("Default");
            var grid = new UniformGrid { Columns = 3, Margin = new Thickness(20) };
            foreach (var scheme in AppPalettes.Names)
                grid.Children.Add(new ColorSchemeThumbnailView { SchemeName = scheme, DataContext = scheme, Margin = new Thickness(10) });
            var window = new Window { Width = 990, Height = 440, Content = grid };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            using var frame = window.CaptureRenderedFrame();
            using var output = File.Create(Path.Combine(directory, $"theme-gallery-{variant.Key}.png"));
            frame!.Save(output, PngBitmapEncoderOptions.Default);
            window.Close();
        }
    }

    private static void VerifySettingsGallery(string directory)
    {
        using var session = new AppConfigurationSession(new HeadlessConfigService(new AppConfig()));
        using var state = new AppSettingsState(session);
        using var vm = new GeneralSettingsViewModel(state);
        var view = new GeneralSettingsView { DataContext = vm };
        var window = new Window { Content = view, Width = 950, Height = 920 };
        window.Show();
        try
        {
            foreach (var variant in new[] { ThemeVariant.Dark, ThemeVariant.Light })
            {
                Application.Current!.RequestedThemeVariant = variant;
                App.SetColorScheme("Nord");
                state.Config.Theme = variant == ThemeVariant.Dark ? "Dark" : "Light";
                state.Config.ColorScheme = "Forest";
                Dispatcher.UIThread.RunJobs();
                var list = view.GetVisualDescendants().OfType<ListBox>().Single(x => x.Classes.Contains("color-schemes"));
                Check(list.Items.Count == 9 && (string?)list.SelectedItem == "Forest", "settings includes and selects all nine schemes");
                var selected = list.GetVisualDescendants().OfType<ListBoxItem>().Single(x => x.IsSelected);
                Check(selected.BorderThickness == new Thickness(2)
                    && selected.BorderBrush is ISolidColorBrush rim && rim.Color.A == 255,
                    "selected palette must have a visible selection rim");
                using var frame = window.CaptureRenderedFrame();
                using var output = File.Create(Path.Combine(directory, $"theme-settings-{variant.Key}.png"));
                frame!.Save(output, PngBitmapEncoderOptions.Default);
            }
        }
        finally { window.Close(); }
    }

    private static void Settle()
    {
        var until = Environment.TickCount64 + 450;
        do
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Thread.Sleep(10);
        } while (Environment.TickCount64 < until);
        Dispatcher.UIThread.RunJobs();
    }

    private static IEnumerable<Run> Runs(InlineCollection inlines)
    {
        foreach (var inline in inlines)
        {
            if (inline is Run run) yield return run;
            if (inline is Span span)
                foreach (var child in Runs(span.Inlines)) yield return child;
        }
    }

    private static Color Colour(object? value) => value switch
    {
        ISolidColorBrush brush when brush.Opacity == 1 => brush.Color,
        ISolidColorBrush brush => brush.Color, // Shell assertions compare the tint, independently of its material opacity.
        Color colour => colour,
        _ => throw new InvalidOperationException($"Expected a theme colour, got {value ?? "null"}")
    };

    private static double Ratio(Color a, Color b)
    {
        static double Channel(byte x) { var s = x / 255.0; return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); }
        static double Luminance(Color c) => 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
        var x = Luminance(a); var y = Luminance(b);
        return (Math.Max(x, y) + 0.05) / (Math.Min(x, y) + 0.05);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
