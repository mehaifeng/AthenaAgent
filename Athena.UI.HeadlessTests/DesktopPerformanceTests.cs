using Athena.UI.Controls;
using Athena.UI.Markup;
using Athena.UI.Models;
using Athena.UI.Services;
using Athena.UI.ViewModels;
using Athena.UI.Views;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using LiveMarkdown.Avalonia;
using Microsoft.Data.Sqlite;
using Serilog;

internal static class DesktopPerformanceTests
{
    internal static async Task RunAsync()
    {
        await ConversationDirectoryAsync();
        BubbleProjection();
        await VariableHeightViewportAsync();
        await InlineDiagramWheelAsync();
        await ParagraphReflowAsync();
        Console.WriteLine("[PASS] lazy archive, literal search, unopened persistence, mixed-height anchors and keyboard navigation");
    }

    private static async Task ParagraphReflowAsync()
    {
        const string original = "原生排版 العربية 世界 glyph shaping and line breaking, ";
        var text = string.Concat(Enumerable.Repeat(original, 18));
        var renderer = new MarkdownRenderer();
        MarkdownHelper.SetText(renderer, text);
        var reference = new MarkdownTextBlock { Text = text.TrimEnd(), Classes = { "ParagraphBlock" } };
        var stack = new StackPanel { Children = { renderer, reference } };
        var window = new Window { Width = 650, Height = 500, Content = stack };
        window.Show();
        try
        {
            MarkdownTextBlock? paragraph = null;
            await UntilAsync(() => (paragraph = renderer.GetVisualDescendants().OfType<MarkdownTextBlock>().FirstOrDefault())?.Text == text.TrimEnd());
            void CheckLines(double width)
            {
                var native = paragraph!;
                reference.FontSize = native.FontSize;
                reference.FontFamily = native.FontFamily;
                reference.TextWrapping = native.TextWrapping;
                var size = new Size(width, double.PositiveInfinity);
                native.Measure(size);
                reference.Measure(size);
                Check(native.TextLayout.TextLines.Select(line => line.Length).SequenceEqual(reference.TextLayout.TextLines.Select(line => line.Length))
                    && Math.Abs(native.TextLayout.Height - reference.TextLayout.Height) < .01, "Plain Markdown wrapping diverged from native text");
            }
            foreach (var width in new[] { 480d, 355, 220, 500 }) CheckLines(width);
            paragraph!.FontSize += 5;
            CheckLines(301);
            paragraph.SelectionStart = 4;
            paragraph.SelectionEnd = 18;
            Check(paragraph.ActualSelectedText == text[4..18], "Literal paragraph lost native selection text");
            CheckLines(275);
            MarkdownHelper.SetText(renderer, text + "**bold** and [link](https://example.org).");
            await UntilAsync(() => renderer.GetVisualDescendants().OfType<MarkdownTextBlock>().Any(b => b.ActualText.Contains("bold and link.")));
            MarkdownHelper.SetText(renderer, "replacement");
            await UntilAsync(() => renderer.GetVisualDescendants().OfType<MarkdownTextBlock>().SingleOrDefault()?.Text == "replacement");
            renderer.SelectAll();
            Check(renderer.SelectedText == "replacement", "Replacing formatted content lost cross-block selection");
        }
        finally { window.Close(); }
    }

    private static async Task InlineDiagramWheelAsync()
    {
        using var chat = new MainConversationViewModel();
        for (var i = 0; i < 28; i++) chat.Messages.Add(new ChatMessage { Role = "user", Content = $"Question {i}" });
        chat.Messages.Add(new ChatMessage { Role = "assistant", Content = "```mermaid\nflowchart LR\n A --> B\n```" });
        var view = new MainConversationView { DataContext = chat };
        var window = new Window { Content = view, Width = 840, Height = 720 };
        window.Show();
        try
        {
            var scroll = view.FindControl<ScrollViewer>("ChatScrollViewer")!;
            LiveMarkdown.Avalonia.PanAndZoom? diagram = null;
            Point point = default;
            await UntilAsync(() =>
            {
                diagram = view.GetVisualDescendants().OfType<LiveMarkdown.Avalonia.PanAndZoom>().FirstOrDefault();
                if (diagram is not { Bounds.Width: > 0 } || scroll.Offset.Y < 100) return false;
                point = diagram.TranslatePoint(new Point(diagram.Bounds.Width / 2, diagram.Bounds.Height / 2), window) ?? default;
                return (window.InputHitTest(point) as Visual)?.FindAncestorOfType<LiveMarkdown.Avalonia.PanAndZoom>(true) == diagram;
            });
            var scale = diagram!.Scale;
            window.MouseWheel(point, new Vector(0, scale < diagram.MaxZoom ? 1 : -1), RawInputModifiers.Control);
            await UntilAsync(() => diagram.Scale != scale);
            scale = diagram.Scale;
            var offset = scroll.Offset.Y;
            window.MouseWheel(point, new Vector(0, 1), RawInputModifiers.None);
            await UntilAsync(() => scroll.Offset.Y < offset);
            Check(diagram.Scale == scale, "Plain wheel must scroll the conversation across an inline diagram");
        }
        finally { window.Close(); }
    }

    private static void BubbleProjection()
    {
        using var chat = new MainConversationViewModel();
        var hidden = new ChatMessage { Role = "assistant", Content = "tool carrier", IsHidden = true };
        var reply = new ChatMessage { Role = "assistant", Content = "visible" };
        chat.Messages.ReplaceAll([hidden, reply]);
        var view = new MainConversationView { DataContext = chat };
        var items = view.FindControl<ItemsControl>("MessagesItemsControl")!;
        Check(items.ItemCount == 1 && items.Items[0] == reply, "Protocol-only carriers entered the bubble list");
        hidden.IsHidden = false;
        Check(items.ItemCount == 2 && items.Items[0] == hidden, "Unhiding a message lost its original position");
        reply.Role = "tool";
        Check(items.ItemCount == 1 && items.Items[0] == hidden, "A role change left a hidden row in the extent");
        chat.Messages.Clear();
        hidden.Role = "user";
        Check(items.ItemCount == 0, "Removed messages must be detached from the projection");
        view.DataContext = null;
    }

    private static async Task ConversationDirectoryAsync()
    {
        var root = Directory.CreateTempSubdirectory("athena-directory-").FullName;
        try
        {
            var paths = new TemporaryPathService(root);
            using var store = new ConversationArchiveStore(paths, Log.Logger);
            var date = DateTime.Now;
            for (var i = 0; i < 9; i++)
                await store.SaveAsync(new ConversationHistoryItem
                {
                    Id = Guid.NewGuid().ToString(), ConversationId = $"chat-{i}", Summary = $"Title {i}",
                    UpdatedAt = date.AddMinutes(-i), Revision = 1,
                    Messages = [new ChatMessage { Role = "user", Content = i == 5 ? "档案 Φ%_hi" : $"Body {i}" }]
                });
            using var shell = new MainWindowViewModel(null, null, null, null, null, null, null, paths, null, null, null, null, null, null, conversationStore: store);
            await UntilAsync(() => !shell.IsConversationTreeLoading && shell.SelectedConversation != null && !shell.IsConversationSwitching);
            var sessions = shell.ConversationGroups.SelectMany(group => group.Conversations).ToList();
            var unopened = sessions.Single(s => s.Title == "Title 7");
            string RawPayload()
            {
                using var db = new SqliteConnection($"Data Source={Path.Combine(root, "conversations.db")}");
                db.Open();
                using var cmd = db.CreateCommand();
                cmd.CommandText = "SELECT payload FROM conversations WHERE id=$id";
                cmd.Parameters.AddWithValue("$id", unopened.HistoryId);
                return (string)cmd.ExecuteScalar()!;
            }
            var original = RawPayload();
            Check(sessions.Count == 9 && sessions.Count(s => s.LoadedChat != null) == 1, "Startup should open exactly the most recent conversation");
            shell.ConversationSearchText = "body 3";
            shell.ConversationSearchText = "φ%_HI";
            await UntilAsync(() => sessions.Single(s => s.Title == "Title 5").IsSearchMatch);
            Check(sessions.Count(s => s.IsSearchMatch) == 1 && sessions.Count(s => s.LoadedChat != null) == 1, "Literal Unicode content search must preserve closed sessions");
            shell.ConversationSearchText = "";
            var target = sessions.Single(s => s.Title == "Title 4");
            shell.SelectedConversation = unopened;
            shell.SelectedConversation = target;
            await UntilAsync(() => target.LoadedChat != null && !shell.IsConversationSwitching);
            Check(unopened.LoadedChat == null && target.Chat.Messages.Single().Content == "Body 4", "A superseded selection prepared the wrong session");
            var renamed = sessions.Single(s => s.Title == "Title 2");
            renamed.RenameCommand.Execute("Renamed");
            renamed.TogglePinnedCommand.Execute(null);
            await shell.PersistSessionStateAsync();
            var stored = await store.LoadByIdAsync(renamed.HistoryId);
            Check(stored is { IsPinned: true, Summary: "Renamed" } && stored.Messages.Single().Content == "Body 2", "Editing a closed row lost messages or metadata");
            Check(unopened.LoadedChat == null && RawPayload() == original, "Shutdown rewrote an unopened payload");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static async Task VariableHeightViewportAsync()
    {
        var messages = new BulkObservableCollection<ChatMessage>();
        for (var i = 0; i < 150; i++) messages.Add(new ChatMessage { Role = "user", Content = i.ToString() });
        var list = new ItemsControl
        {
            ItemsSource = messages,
            ItemsPanel = new FuncTemplate<Panel?>(() => new MessageViewportPanel()),
            ItemTemplate = new FuncDataTemplate<ChatMessage>((m, _) => new Border { Height = m != null && int.Parse(m.Content) % 11 == 0 ? 630 : 48 })
        };
        var scroll = new ScrollViewer { Content = list };
        var window = new Window { Content = scroll, Width = 600, Height = 400 };
        window.Show();
        try
        {
            await UntilAsync(() => list.ContainerFromIndex(0)?.Bounds.Height > 0);
            list.ScrollIntoView(65);
            bool Visible(Control row) => row is { Opacity: > 0, IsHitTestVisible: true, Bounds.Height: > 0 }
                && row.TranslatePoint(default, scroll) is { } point && point.Y < scroll.Bounds.Height && point.Y + row.Bounds.Height > 0;
            await UntilAsync(() => list.ContainerFromIndex(65) is { } middle && Visible(middle),
                () => $"ScrollIntoView(65) offset={scroll.Offset.Y}, extent={scroll.Extent.Height}, row={list.ContainerFromIndex(65)?.Bounds}, screen={list.ContainerFromIndex(65)?.TranslatePoint(default, scroll)}");
            Control? FirstVisible() => list.GetRealizedContainers().Where(Visible).MinBy(list.IndexFromContainer);
            await UntilAsync(() => FirstVisible() is { } top && list.ContainerFromIndex(list.IndexFromContainer(top) - 1) is { Bounds.Height: > 0 },
                () => $"No measured row above the visible anchor, offset={scroll.Offset.Y}, first={FirstVisible()?.Bounds}");
            var top = FirstVisible()!;
            var index = list.IndexFromContainer(top);
            var anchor = top.TranslatePoint(default, scroll)!.Value.Y;
            var before = list.ContainerFromIndex(index - 1)!;
            before.Height = before.Bounds.Height + 75;
            window.UpdateLayout();
            await Task.Delay(60);
            Check(Math.Abs(top.TranslatePoint(default, scroll)!.Value.Y - anchor) < 2, "An offscreen height change shifted the reader's anchor");
            var panel = (INavigableContainer)list.ItemsPanelRoot!;
            var last = (Control?)panel.GetControl(NavigationDirection.Last, top, false);
            Check(last != null && list.IndexFromContainer(last) == 149, "Keyboard navigation failed to realize the last item");
            var first = (Control?)panel.GetControl(NavigationDirection.Next, last, true);
            Check(first != null && list.IndexFromContainer(first) == 0, "Keyboard navigation failed to wrap");
            list.ScrollIntoView(149);
            await UntilAsync(() => list.ContainerFromIndex(149) is { } end && Visible(end));
            messages.Add(new ChatMessage { Role = "user", Content = "150" });
            list.ScrollIntoView(150);
            await UntilAsync(() => list.ContainerFromIndex(150) is { } end && Visible(end));
            Check(list.GetRealizedContainers().Count() < 40, "Offscreen rows were retained");
            messages.ReplaceAll(messages.Take(6));
            await UntilAsync(() => list.ContainerFromIndex(5) is { } end && Visible(end));
            messages.Clear();
            Check(!list.GetRealizedContainers().Any(), "Removing the source must immediately release its visual tree");
        }
        finally { window.Close(); }
    }

    private static async Task UntilAsync(Func<bool> condition, Func<string>? state = null)
    {
        var start = Environment.TickCount64;
        while (!condition())
        {
            if (Environment.TickCount64 - start > 7000) throw new TimeoutException(state?.Invoke() ?? "Desktop interaction did not settle");
            await Task.Delay(15);
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
