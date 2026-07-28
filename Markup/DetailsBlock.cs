using System.Net;
using System.Text.RegularExpressions;
using System.Threading;
using Avalonia.Controls;
using LiveMarkdown.Avalonia;
using Markdig;
using Markdig.Parsers;
using Markdig.Renderers;
using Markdig.Syntax;

namespace Athena.UI.Markup;

/// <summary>
/// A deliberately small HTML compatibility block for Markdown
/// &lt;details&gt;/&lt;summary&gt; content. General raw HTML remains unsupported.
/// </summary>
public sealed class DetailsBlock(BlockParser parser) : ContainerBlock(parser)
{
    public string Summary { get; set; } = "Details";

    public bool IsExpandedByDefault { get; set; }
}

public sealed class DetailsBlockParser : BlockParser
{
    private static readonly Regex OpeningTag = new(
        "^<details(?<attributes>(?:\\s+[^>]*)?)>\\s*(?:<summary>(?<summary>.*?)</summary>\\s*)?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex SummaryTag = new(
        "^<summary>(?<summary>.*?)</summary>\\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex ClosingTag = new(
        "^</details>\\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex OpenAttribute = new(
        "(?:^|\\s)open(?:\\s*=\\s*(?:\"[^\"]*\"|'[^']*'|[^\\s>]+))?(?=\\s|$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public DetailsBlockParser()
    {
        OpeningCharacters = ['<'];
    }

    public override bool CanInterrupt(BlockProcessor processor, Block block) =>
        !processor.IsCodeIndent && TryMatchOpeningTag(processor.Line.ToString(), out _, out _);

    public override BlockState TryOpen(BlockProcessor processor)
    {
        if (processor.IsCodeIndent ||
            !TryMatchOpeningTag(processor.Line.ToString(), out var summary, out var isOpen))
        {
            return BlockState.None;
        }

        processor.NewBlocks.Push(new DetailsBlock(this)
        {
            Summary = summary,
            IsExpandedByDefault = isOpen,
            Line = processor.LineIndex,
            Column = processor.Column,
            Span = new SourceSpan(processor.Start, processor.Line.End)
        });

        return BlockState.ContinueDiscard;
    }

    public override BlockState TryContinue(BlockProcessor processor, Block block)
    {
        var details = (DetailsBlock)block;
        var line = processor.Line.ToString().Trim();

        if (ClosingTag.IsMatch(line))
        {
            block.UpdateSpanEnd(processor.Line.End);
            return BlockState.BreakDiscard;
        }

        var summaryMatch = SummaryTag.Match(line);
        if (summaryMatch.Success)
        {
            details.Summary = DecodeSummary(summaryMatch.Groups["summary"].Value);
            block.UpdateSpanEnd(processor.Line.End);
            return BlockState.ContinueDiscard;
        }

        block.UpdateSpanEnd(processor.Line.End);
        processor.GoToColumn(processor.ColumnBeforeIndent);
        return BlockState.Continue;
    }

    private static bool TryMatchOpeningTag(string line, out string summary, out bool isOpen)
    {
        summary = "Details";
        isOpen = false;

        var match = OpeningTag.Match(line.Trim());
        if (!match.Success)
        {
            return false;
        }

        var attributes = match.Groups["attributes"].Value;
        isOpen = OpenAttribute.IsMatch(attributes);

        if (match.Groups["summary"].Success)
        {
            summary = DecodeSummary(match.Groups["summary"].Value);
        }

        return true;
    }

    private static string DecodeSummary(string value)
    {
        var decoded = WebUtility.HtmlDecode(value).Trim();
        return string.IsNullOrWhiteSpace(decoded) ? "Details" : decoded;
    }
}

public sealed class DetailsBlockExtension : IMarkdownExtension
{
    public void Setup(MarkdownPipelineBuilder pipeline)
    {
        if (!pipeline.BlockParsers.Contains<DetailsBlockParser>())
        {
            // Run before Markdig's generic raw-HTML parser.
            pipeline.BlockParsers.Insert(0, new DetailsBlockParser());
        }
    }

    public void Setup(MarkdownPipeline pipeline, IMarkdownRenderer renderer)
    {
    }
}

public static class DetailsBlockExtensions
{
    public static MarkdownPipelineBuilder UseDetailsBlocks(this MarkdownPipelineBuilder pipeline)
    {
        pipeline.Extensions.AddIfNotAlready<DetailsBlockExtension>();

        return pipeline;
    }
}

public sealed class DetailsBlockNode : ContainerBlockNode<DetailsBlock>
{
    private readonly Expander _expander;
    private readonly MarkdownTextBlock _summary;
    private bool? _lastOpenAttribute;

    public DetailsBlockNode()
    {
        _summary = new MarkdownTextBlock
        {
            Classes = { "DetailsSummary" }
        };

        Control = _expander = new Expander
        {
            Classes = { "DetailsBlock" },
            Header = _summary,
            Content = container,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch
        };
    }

    public override Control Control { get; }

    protected override bool UpdateCore(
        DocumentNode documentNode,
        DetailsBlock details,
        in ObservableStringBuilderChangedEventArgs change,
        CancellationToken cancellationToken)
    {
        _summary.Text = details.Summary;

        // Preserve a user's expanded/collapsed choice while streaming new content.
        if (_lastOpenAttribute != details.IsExpandedByDefault)
        {
            _expander.IsExpanded = details.IsExpandedByDefault;
            _lastOpenAttribute = details.IsExpandedByDefault;
        }

        _ = base.UpdateCore(documentNode, details, in change, cancellationToken);
        return true;
    }
}
