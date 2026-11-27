using System;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using LiveMarkdown.Avalonia;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Athena.UI.Markup;

/// <summary>Use the parsed literal directly for a plain paragraph. Formatted paragraphs
/// retain the renderer’s inline nodes, links, images and selection handling.</summary>
public sealed class ChatParagraphNode : BlockNode<ParagraphBlock>
{
    private LiteralTextBlock? _literal;
    private ParagraphBlockNode? _formatted;

    public override Control Control => _formatted?.Control ?? _literal!;

    protected override void ReleaseChildren() => _formatted?.Release();

    protected override bool UpdateCore(DocumentNode document, ParagraphBlock block,
        in ObservableStringBuilderChangedEventArgs change, CancellationToken token)
    {
        // The upstream paragraph captures the previous glyph positions for a streamed append.
        if (!MarkdownHelper.GetIsStreaming(document.Owner) && document.Owner.StreamingRevealMode == MarkdownStreamingRevealMode.None &&
            block.Inline?.FirstChild is LiteralInline { NextSibling: null } literal)
        {
            if (_formatted != null) return false; // Streaming changed the kind of paragraph.
            _literal ??= new LiteralTextBlock { Classes = { "ParagraphBlock" } };
            _literal.Text = literal.Content.ToString();
            return true;
        }
        if (_literal != null) return false;
        _formatted ??= new ParagraphBlockNode();
        return _formatted.Update(document, block, in change, token) != false;
    }

    private sealed class LiteralTextBlock : MarkdownTextBlock
    {
        private TextRunCache? _shaping;
        protected override Type StyleKeyOverride => typeof(MarkdownTextBlock);

        protected override TextLayout CreateTextLayout(string? text)
        {
            if (SelectionStart != SelectionEnd || Highlights.Count > 0 || LineSpacing != 0 ||
                text.AsSpan().ContainsAny("\r\n\u0085\u2028\u2029"))
                return base.CreateTextLayout(text);
            // Wrapping changes at every width. Glyph shaping is independent of width.
            var size = GetMaxSizeFromConstraint();
            var run = new GenericTextRunProperties(new Typeface(FontFamily, FontStyle, FontWeight, FontStretch),
                FontSize, TextDecorations, Foreground, fontFeatures: FontFeatures);
            var paragraph = new GenericTextParagraphProperties(FlowDirection, TextAlignment, true, false,
                run, TextWrapping, LineHeight, 0, LetterSpacing);
            return new TextLayout(new LiteralSource(text ?? string.Empty, run), paragraph, TextTrimming,
                size.Width, size.Height, MaxLines, _shaping ??= new TextRunCache());
        }

        // LiveMarkdown 2.8 includes the terminal paragraph marker in the final line's coordinates.
        private readonly struct LiteralSource(string text, TextRunProperties run) : ITextSource
        {
            public TextRun GetTextRun(int index) => index >= text.Length
                ? new TextEndOfParagraph() : new TextCharacters(text.AsMemory(index), run);
        }

        protected override void OnMeasureInvalidated()
        {
            _shaping?.Invalidate(); // Text, inherited typography or selection changed.
            base.OnMeasureInvalidated();
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            _shaping?.Dispose();
            _shaping = null;
            base.OnDetachedFromVisualTree(e);
        }
    }
}
