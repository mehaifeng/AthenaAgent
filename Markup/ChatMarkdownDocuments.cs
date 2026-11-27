using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using Athena.UI.Models;
using LiveMarkdown.Avalonia;
using Markdig;
using Markdig.Helpers;
using Markdig.Syntax;
using Serilog;

namespace Athena.UI.Markup;

/// <summary>Reuses immutable parsed documents for as long as their source message text is alive.</summary>
internal sealed class ChatMarkdownDocuments : MarkdownTextProjector
{
    private static readonly ConditionalWeakTable<string, MarkdownDocumentUpdate.Full> Documents = new();
    private static readonly ChatMarkdownDocuments Projector = new();

    internal static MarkdownDocumentUpdate.Full? Find(string text) =>
        Documents.TryGetValue(text, out var document) ? document : null;

    internal static void Remember(string text, MarkdownDocumentUpdate update) =>
        Documents.GetValue(text, _ => update as MarkdownDocumentUpdate.Full ?? new MarkdownDocumentUpdate.Full(update.Document));

    // On the archive reader's worker, prepare the end of the conversation that opens on screen.
    // Older or unusually large blocks are parsed on demand and cached by the renderer.
    internal static void PrepareHistory(ConversationHistoryItem history)
    {
        var remainingChars = 64_000;
        var recent = history.Messages.AsEnumerable().Reverse()
            .Where(m => !m.IsHidden && m.Role is "user" or "assistant")
            .SelectMany(m => m.UsesSegmentLayout
                ? m.Segments.Reverse().Where(s => s.IsMarkdown).Select(s => s.Text)
                : [m.Content])
            .Where(text => !string.IsNullOrEmpty(text)).Take(12);
        foreach (var text in recent)
        {
            if (text.Length > remainingChars) continue;
            remainingChars -= text.Length;
            try
            {
                Documents.GetValue(text, static source =>
                    new MarkdownDocumentUpdate.Full(Markdown.Parse(source, MarkdownUpdateProducer.DefaultPipeline)));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // A parser extension failure must not prevent the archive from opening.
                Log.Debug("Could not pre-parse a history block ({ErrorType})", ex.GetType().Name);
            }
        }
    }

    internal static bool Matches(string text, TextSearchPattern pattern, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (text.Contains(pattern.Query, StringComparison.OrdinalIgnoreCase)) return true;
        if (!MayContainFormatting(text)) return false;
        try
        {
            IReadOnlyList<MarkdownTextBuffer> buffers;
            if (Find(text) is { } cached)
            {
                var list = new List<MarkdownTextBuffer>();
                Projector.AppendBlocks(cached.Document, list, token);
                buffers = list;
            }
            else buffers = Projector.Project(new ObservableStringBuilderSnapshot(text, 0), token).Buffers;
            return buffers.Any(buffer => pattern.FindRanges(buffer.Text).Any());
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or OperationCanceledException))
        {
            Log.Debug("Could not project a history block for search ({ErrorType})", ex.GetType().Name);
            return false;
        }
    }

    internal static bool MayContainFormatting(string text) =>
        text.AsSpan().ContainsAny("*_[]`~&\\:<^=+!");

    protected override void AppendBlock(Block block, List<MarkdownTextBuffer> buffers, CancellationToken token)
    {
        if (block is DetailsBlock details)
            buffers.Add(new MarkdownTextBuffer(block.Span, new StringSlice(details.Summary)));
        base.AppendBlock(block, buffers, token);
    }
}
