using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;
using System.Collections.Generic;

namespace Athena.UI.Services;

public enum WorkspaceDiffLineKind
{
    Unchanged,
    Added,
    Removed
}

public sealed record WorkspaceDiffLine(
    WorkspaceDiffLineKind Kind,
    int? OldLineNumber,
    int? NewLineNumber,
    string Text)
{
    public string Prefix => Kind switch
    {
        WorkspaceDiffLineKind.Added => "+",
        WorkspaceDiffLineKind.Removed => "−",
        _ => string.Empty
    };

    public string OldLineNumberText => OldLineNumber?.ToString() ?? string.Empty;
    public string NewLineNumberText => NewLineNumber?.ToString() ?? string.Empty;
    public bool IsAdded => Kind == WorkspaceDiffLineKind.Added;
    public bool IsRemoved => Kind == WorkspaceDiffLineKind.Removed;
}

public static class WorkspaceDiffBuilder
{
    public static IReadOnlyList<WorkspaceDiffLine> Build(string oldText, string newText)
    {
        var model = InlineDiffBuilder.Diff(oldText ?? string.Empty, newText ?? string.Empty);
        var result = new List<WorkspaceDiffLine>(model.Lines.Count);
        var oldLine = 0;
        var newLine = 0;

        foreach (var piece in model.Lines)
        {
            switch (piece.Type)
            {
                case ChangeType.Inserted:
                    newLine++;
                    result.Add(new WorkspaceDiffLine(WorkspaceDiffLineKind.Added, null, newLine, piece.Text));
                    break;
                case ChangeType.Deleted:
                    oldLine++;
                    result.Add(new WorkspaceDiffLine(WorkspaceDiffLineKind.Removed, oldLine, null, piece.Text));
                    break;
                default:
                    oldLine++;
                    newLine++;
                    result.Add(new WorkspaceDiffLine(WorkspaceDiffLineKind.Unchanged, oldLine, newLine, piece.Text));
                    break;
            }
        }

        return result;
    }
}
