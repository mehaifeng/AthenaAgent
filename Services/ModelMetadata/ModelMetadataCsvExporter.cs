using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Athena.UI.Services.ModelMetadata;

public sealed record ModelMetadataCsvRow(
    string ProviderId,
    string ProviderName,
    string ExternalModelId,
    string DisplayName,
    string Availability,
    string Capability,
    string MatchStatus,
    int? MatchScore,
    int? MatchMargin,
    string OpenRouterModelId,
    long ContextWindowTokens,
    string ContextWindowSource,
    long? MaxCompletionTokens,
    string MaxCompletionSource,
    string InputModalities,
    string OutputModalities,
    string SupportsTools,
    string SupportsReasoning,
    string SupportsStructuredOutput,
    string Warnings);

/// <summary>
/// Produces an RFC 4180-compatible diagnostic export. Every external text cell is
/// neutralized before quoting so spreadsheet applications cannot interpret model
/// names, IDs, or warnings as formulas.
/// </summary>
public static class ModelMetadataCsvExporter
{
    private static readonly string[] Headers =
    [
        "ProviderId", "ProviderName", "ExternalModelId", "DisplayName", "Availability",
        "Capability", "MatchStatus", "MatchScore", "MatchMargin", "OpenRouterModelId",
        "ContextWindowTokens", "ContextWindowSource", "MaxCompletionTokens", "MaxCompletionSource",
        "InputModalities", "OutputModalities", "SupportsTools", "SupportsReasoning",
        "SupportsStructuredOutput", "Warnings"
    ];

    public static string Build(IEnumerable<ModelMetadataCsvRow> rows)
    {
        var builder = new StringBuilder();
        AppendRow(builder, Headers);
        foreach (var row in rows)
        {
            AppendRow(builder,
            [
                row.ProviderId,
                row.ProviderName,
                row.ExternalModelId,
                row.DisplayName,
                row.Availability,
                row.Capability,
                row.MatchStatus,
                row.MatchScore?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                row.MatchMargin?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                row.OpenRouterModelId,
                row.ContextWindowTokens.ToString(CultureInfo.InvariantCulture),
                row.ContextWindowSource,
                row.MaxCompletionTokens?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                row.MaxCompletionSource,
                row.InputModalities,
                row.OutputModalities,
                row.SupportsTools,
                row.SupportsReasoning,
                row.SupportsStructuredOutput,
                row.Warnings
            ]);
        }
        return builder.ToString();
    }

    public static async Task WriteAtomicallyAsync(
        string path,
        IEnumerable<ModelMetadataCsvRow> rows,
        CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)
                        ?? throw new InvalidOperationException("CSV export path has no parent directory.");
        Directory.CreateDirectory(directory);
        var tempPath = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                             tempPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             16 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            await using (var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true)))
            {
                await writer.WriteAsync(Build(rows).AsMemory(), cancellationToken).ConfigureAwait(false);
                await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            File.Move(tempPath, fullPath, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath)) File.Delete(tempPath);
            }
            catch
            {
                // A failed best-effort cleanup must not hide the original export error.
            }
        }
    }

    public static string EscapeCell(string? value)
    {
        value ??= string.Empty;
        var firstNonWhitespace = value.AsSpan().TrimStart();
        if (!firstNonWhitespace.IsEmpty
            && firstNonWhitespace[0] is '=' or '+' or '-' or '@')
            value = "'" + value;
        else if (value.Length > 0 && value[0] is '\t' or '\r' or '\n')
            value = "'" + value;

        if (value.IndexOfAny([',', '"', '\r', '\n']) < 0) return value;
        return '"' + value.Replace("\"", "\"\"") + '"';
    }

    private static void AppendRow(StringBuilder builder, IReadOnlyList<string> cells)
    {
        builder.AppendJoin(',', cells.Select(EscapeCell));
        builder.Append("\r\n");
    }
}
