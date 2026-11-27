using System.Net;
using System.Text;
using Athena.UI.Markup;
using Athena.UI.Models;
using Athena.UI.Services.Preview;
using Athena.UI.ViewModels;
using Athena.UI.Views;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.LogicalTree;
using Avalonia.Rendering.Composition;
using Avalonia.VisualTree;
using LiveMarkdown.Avalonia;
using Markdig;
using RaTeX.Avalonia;

internal static partial class DesktopPerformanceTests
{
    internal static async Task RunMarkdownAsync()
    {
        var pipeline = new MarkdownPipelineBuilder().UseAdvancedExtensions();
        MarkdownConfiguration.ConfigurePipeline(pipeline, nativeMathAvailable: false);
        Check(Markdown.ToPlainText("公式 $x^2$ 与 **加粗**", pipeline.Build()).Contains("公式 $x^2$ 与 加粗"),
            "Hosts without the native math library must still display the formula and the surrounding reply");
        await ChatMarkdownAsync();
        await WebPreviewAsync();
    }

    private static async Task ChatMarkdownAsync()
    {
        var history = new ChatMessage { Role = "user", Content = "历史公式 $x^2$、\\(y^2\\)\n\n\\[\nz^2\n\\]" };
        await Task.Run(() => ChatMarkdownDocuments.PrepareHistory(new ConversationHistoryItem { Messages = [history] }));
        var cached = ChatMarkdownDocuments.Find(history.Content);
        Check(cached != null, "History text was not pre-parsed");
        var first = new ChatMessageSegment { Kind = ChatMessageSegmentKind.Markdown, Text = "答复开头" };
        var last = new ChatMessageSegment { Kind = ChatMessageSegmentKind.Markdown, Text = "中文正文" };
        var reply = new ChatMessage { Role = "assistant", IsStreaming = true, Segments = [first, last] };
        using var chat = new MainConversationViewModel();
        chat.Messages.ReplaceAll([history, reply]);
        var view = new MainConversationView { DataContext = chat };
        var window = new Window { Width = 800, Height = 780, Content = view };
        window.Show();
        try
        {
            MarkdownRenderer? For(object data) => view.GetVisualDescendants().OfType<MarkdownRenderer>()
                .FirstOrDefault(r => ReferenceEquals(r.DataContext, data));
            await UntilAsync(() => For(last)?.DocumentUpdate != null && For(first)?.DocumentUpdate != null && For(history)?.DocumentUpdate != null);
            var stream = For(last)!;
            var settled = For(history)!;
            Check(ReferenceEquals(cached, settled.DocumentUpdate) && settled.StreamingRevealMode == MarkdownStreamingRevealMode.None,
                "Reopening history should apply the cached full document immediately");
            if (MarkdownConfiguration.SupportsNativeMath)
                await UntilAsync(() => settled.GetVisualDescendants().OfType<FormulaControl>().Count(f => f.IsVisible && f.DisplayList != null) == 3);
            else
                await UntilAsync(() => settled.GetVisualDescendants().OfType<MarkdownTextBlock>().Any(b => b.LayoutText.Contains("$x^2$")));
            Check(stream.StreamingRevealMode == MarkdownStreamingRevealMode.Character, "The assistant template did not enable reveal");
            MarkdownConfiguration.ApplyStreamingEffect("Line");
            Check(stream.StreamingRevealMode == MarkdownStreamingRevealMode.Line && settled.StreamingRevealMode == MarkdownStreamingRevealMode.None,
                "Changing reply animation must update only the live reply");
            MarkdownConfiguration.ApplyStreamingEffect("None");
            Check(stream.StreamingRevealMode == MarkdownStreamingRevealMode.None, "Disabling reply animation was ignored");
            MarkdownConfiguration.ApplyStreamingEffect("Character");
            bool HasReveal(MarkdownTextBlock? block) => block != null && ElementComposition.GetElementChildVisual(block) is CompositionCustomVisual;
            bool PaintAndCheck(Func<bool> condition)
            {
                window.UpdateLayout();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                return condition();
            }

            last.Text += "继续";
            await UntilAsync(() => PaintAndCheck(() => stream.GetVisualDescendants().OfType<MarkdownTextBlock>()
                .Any(b => b.LayoutText == last.Text && HasReveal(b))), () => "Plain Chinese appends did not reach the compositor");
            last.Text += "\n\n```csharp\nint answer = 41";
            await UntilAsync(() => stream.GetVisualDescendants().OfType<CodeBlock>().Any(c => c.CodeTextBlock?.LayoutText.Contains("41") == true));
            var code = stream.GetVisualDescendants().OfType<CodeBlock>().Single();
            Check(code.CustomColorTheme?.StartsWith("Athena.", StringComparison.Ordinal) == true, "Code block did not inherit the app palette");
            last.Text += " + 1;";
            await UntilAsync(() => PaintAndCheck(() => code.CodeTextBlock!.LayoutText.Contains("+ 1;") && HasReveal(code.CodeTextBlock)),
                () => "Code appends did not reach the compositor");

            reply.IsStreaming = false;
            var label = string.Concat(Enumerable.Repeat("项目资料", 20));
            last.Text += $"\n```\n\n[{label}](file:///tmp/athena-preview.pdf \"资料卡\")";
            Link? link = null;
            MarkdownTextBlock? linkBlock = null;
            await UntilAsync(() =>
            {
                link = stream.GetLogicalDescendants().OfType<Link>().FirstOrDefault(l => l.HRef?.IsFile == true);
                linkBlock = link?.FindLogicalAncestorOfType<MarkdownTextBlock>();
                return linkBlock != null && linkBlock.GetInlineGeometry(link!)?.Rects.Count > 1;
            }, () => "The link did not acquire inline geometry");
            Check(stream.StreamingRevealMode == MarkdownStreamingRevealMode.None && !HasReveal(code.CodeTextBlock), "Completed reply retained its animation");
            stream.SelectAll();
            Check(stream.SelectedText?.Contains(first.Text) == true && stream.SelectedText.Contains("中文正文继续") &&
                !stream.SelectedText.Contains("历史公式") && For(first)!.CanCopy && !settled.CanCopy, "Selection must span one reply's Markdown segments");
            stream.ClearSelection();
            MarkdownHelper.SetSearchText(view, "中文正文");
            Check(stream.TextSearchMatches.Count == 1 && stream.TextSearchMatches[0].Block.HighlightStyles != null,
                "The sidebar query did not highlight visible Markdown");
            MarkdownHelper.SetSearchText(view, "");
            await UntilAsync(() => PaintAndCheck(() => linkBlock!.GetInlineGeometry(link!) is { Rects.Count: > 1 }));
            var rect = linkBlock!.GetInlineGeometry(link!)!.Rects.Last();
            var point = linkBlock.TranslatePoint(rect.Center, window)!.Value;
            window.MouseMove(point);
            var popup = view.FindControl<Popup>("MarkdownLinkPreviewPopup")!;
            await UntilAsync(() => popup.IsOpen, () => "Link hover did not open its preview");
            Check(view.FindControl<TextBlock>("LinkPreviewTitle")!.Text == "资料卡" && popup.PlacementRect == rect,
                "The link hook or the preview fragment anchor was lost");
            window.MouseMove(new Point(3, 3));
            await UntilAsync(() => !popup.IsOpen);
            Console.WriteLine("[PASS] cached history, RaTeX, compositor reveals for Chinese and code, reply selection, search and link previews");
        }
        finally { MarkdownConfiguration.ApplyStreamingEffect("Character"); window.Close(); }
    }

    private sealed class PreviewEndpoint : HttpMessageHandler
    {
        public int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests++;
            if (request.RequestUri!.AbsolutePath == "/moved")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = new Uri("http://127.0.0.1/") } });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(
                "<html><head><title>Other</title><meta property='og:title' content='官方 &amp; 文档'><meta name='description' content='说明'></head></html>",
                Encoding.UTF8, "text/html") });
        }
    }

    private static async Task WebPreviewAsync()
    {
        using var endpoint = new PreviewEndpoint();
        using var client = new HttpClient(endpoint, disposeHandler: false);
        using var previews = new MarkdownWebPreview(client);
        var uri = new Uri("https://docs.example.org/article");
        var page = await previews.ReadAsync(uri, default);
        Check(page is { Title: "官方 & 文档", Description: "说明" } && ReferenceEquals(page, await previews.ReadAsync(uri, default)) &&
            endpoint.Requests == 1, "Web preview did not read and reuse page metadata");
        Check(await previews.ReadAsync(new Uri(uri, "/moved"), default) == null && endpoint.Requests == 2,
            "Link preview followed a redirect to a local address");
    }
}
