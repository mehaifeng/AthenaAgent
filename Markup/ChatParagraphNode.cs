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

    protected override bool UpdateCore(DocumentNode document, ParagraphBlock block,
        in ObservableStringBuilderChangedEventArgs change, CancellationToken token)
    {
        if (block.Inline?.FirstChild is LiteralInline { NextSibling: null } literal)
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
            if (SelectionStart != SelectionEnd || LineSpacing != 0)
                return base.CreateTextLayout(text);
            // Wrapping changes at every width. Glyph shaping is independent of width.
            var size = GetMaxSizeFromConstraint();
            return new TextLayout(text ?? string.Empty, new Typeface(FontFamily, FontStyle, FontWeight, FontStretch),
                FontSize, Foreground, TextAlignment, TextWrapping, TextTrimming, TextDecorations,
                FlowDirection, size.Width, size.Height, LineHeight, LetterSpacing, MaxLines, FontFeatures,
                null, _shaping ??= new TextRunCache());
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
