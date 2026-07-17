using System.Threading;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using LiveMarkdown.Avalonia;
using Markdig;
using Markdig.Extensions.Emoji;
using Markdig.Extensions.Mathematics;

namespace Athena.UI.Markup;

/// <summary>
/// Configures optional Markdown parsers and renderer nodes before the first
/// <see cref="MarkdownRenderer"/> instance is created.
/// </summary>
public static class MarkdownConfiguration
{
    private static int _configured;

    public static void Configure()
    {
        if (Interlocked.Exchange(ref _configured, 1) != 0)
        {
            return;
        }

        MarkdownRenderer.ConfigurePipeline += pipeline => pipeline
            .UseEmojiAndSmiley()
            .UseMathematics()
            .UseDetailsBlocks();

        MarkdownNode.Edit(builder => builder
            .Register<EmojiInlineNode>()
            .Register<MathInlineNode>()
            .Register<MathBlockNode>()
            .Register<DetailsBlockNode>());
    }
}

/// <summary>
/// Gives shortcode-generated emoji an explicit color-emoji font fallback.
/// Direct Unicode emoji are covered by the application's main font fallback list.
/// </summary>
public sealed class EmojiInlineNode : InlineNode<EmojiInline>
{
    private static readonly FontFamily EmojiFontFamily =
        new("Segoe UI Emoji, Apple Color Emoji, Noto Color Emoji");

    private readonly Run _run;

    public EmojiInlineNode()
    {
        Inline = _run = new Run
        {
            FontFamily = EmojiFontFamily,
            Classes = { "Emoji" }
        };
    }

    public override Inline Inline { get; }

    protected override bool UpdateCore(
        DocumentNode documentNode,
        EmojiInline emoji,
        in ObservableStringBuilderChangedEventArgs change,
        CancellationToken cancellationToken)
    {
        _run.Text = emoji.Content.ToString();
        return true;
    }
}
