using System;
using System.Runtime.InteropServices;
using System.Threading;
using Avalonia;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using LiveMarkdown.Avalonia;
using Markdig;
using Markdig.Extensions.Emoji;
using LinkInline = Markdig.Syntax.Inlines.LinkInline;

namespace Athena.UI.Markup;

/// <summary>
/// Configures optional Markdown parsers and renderer nodes before the first
/// <see cref="MarkdownRenderer"/> instance is created.
/// </summary>
public static class MarkdownConfiguration
{
    private static int _configured;
    internal static bool SupportsNativeMath =>
        !(OperatingSystem.IsWindows() && RuntimeInformation.ProcessArchitecture == Architecture.Arm64);
    internal static readonly AttachedProperty<string?> LinkTitleProperty =
        AvaloniaProperty.RegisterAttached<MarkdownHelper, Link, string?>("LinkTitle");

    internal static void ApplyStreamingEffect(string effect)
    {
        var mode = effect switch
        {
            "Line" => MarkdownStreamingRevealMode.Line,
            "None" => MarkdownStreamingRevealMode.None,
            _ => MarkdownStreamingRevealMode.Character
        };
        if (Application.Current?.Resources is { } resources && !Equals(resources["Markdown.StreamingReplyEffect"], mode))
            resources["Markdown.StreamingReplyEffect"] = mode;
    }

    public static void Configure()
    {
        if (Interlocked.Exchange(ref _configured, 1) != 0)
        {
            return;
        }

        // SVG 图片解码器必须在任何 renderer 创建之前注册。
        AsyncImageLoader.DefaultDecoders =
        [
            SvgImageDecoder.Shared,
            DefaultBitmapDecoder.Shared,
        ];

        MarkdownUpdateProducer.ConfigurePipeline += pipeline => ConfigurePipeline(pipeline, SupportsNativeMath);

        MarkdownNode.Edit(builder => builder
            .Unregister<ParagraphBlockNode>().Register<ChatParagraphNode>()
            .Register<EmojiInlineNode>()
            .Register<MathInlineNode>()
            .Register<MathBlockNode>()
            .Register<MermaidBlockNode>()
            .Register<DetailsBlockNode>());

        // The Markdig title belongs to the current link, including when a stream reuses its inline.
        MarkdownNode.GlobalHooks.Register<LinkInlineNode>(
            afterUpdate: context =>
            {
                if (context.Result == NodeUpdateResult.Applied && context.Update.MarkdownObject is LinkInline link)
                    context.Node.Inline.SetValue(LinkTitleProperty, link.Title);
            },
            cleanup: context => context.Node.Inline.ClearValue(LinkTitleProperty));
    }

    internal static void ConfigurePipeline(MarkdownPipelineBuilder pipeline, bool nativeMathAvailable)
    {
        pipeline.UseEmojiAndSmiley().UseMermaid().UseDetailsBlocks();
        if (nativeMathAvailable)
            pipeline.UseExtendedMathematics();
        else
            // RaTeX 1.0.0 ships no win-arm64 binary. Leave TeX visible in the Markdown text.
            pipeline.Extensions.TryRemove<Markdig.Extensions.Mathematics.MathExtension>();
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
