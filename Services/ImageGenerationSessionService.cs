using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Athena.UI.Models;
using Athena.UI.Services.Interfaces;
using Serilog;

namespace Athena.UI.Services;

public sealed class ImageGenerationSessionService : IImageGenerationSessionService
{
    private const double ResolveThreshold = 0.22d;
    private const double ResolveMargin = 0.08d;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _sessionDirectory;
    private readonly ILogger _logger;

    public ImageGenerationSessionService(IPlatformPathService platformPathService, ILogger logger)
    {
        _sessionDirectory = platformPathService.GetImageGenerationSessionDirectory();
        _logger = logger.ForContext<ImageGenerationSessionService>();
        Directory.CreateDirectory(_sessionDirectory);
    }

    public async Task<ImageGenerationSessionRecord> GetOrCreateAsync(string conversationId, CancellationToken cancellationToken = default)
    {
        var existing = await LoadAsync(conversationId, cancellationToken);
        if (existing != null)
        {
            return existing;
        }

        var created = new ImageGenerationSessionRecord
        {
            ConversationId = conversationId,
            CreatedAt = DateTime.Now,
            UpdatedAt = DateTime.Now
        };

        await SaveAsync(created, cancellationToken);
        return created;
    }

    public async Task<ImageGenerationSessionRecord?> LoadAsync(string conversationId, CancellationToken cancellationToken = default)
    {
        var path = GetPath(conversationId);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var json = await File.ReadAllTextAsync(path, cancellationToken);
            return JsonSerializer.Deserialize<ImageGenerationSessionRecord>(json, JsonOptions);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to load image generation session: {ConversationId}", conversationId);
            return null;
        }
    }

    public async Task<ImageGenerationSessionRecord?> CaptureAndPersistAsync(
        string conversationId,
        string? historyId,
        ImageGenerationSessionUpdate update,
        CancellationToken cancellationToken = default)
    {
        var session = await GetOrCreateAsync(conversationId, cancellationToken);
        if (!string.IsNullOrWhiteSpace(historyId))
        {
            session.HistoryId = historyId;
        }

        var parent = update.ContinuityMode switch
        {
            ImageContinuityMode.ContinueLast => GetActiveTurn(session),
            ImageContinuityMode.ContinueMatched => session.Turns.FirstOrDefault(turn => string.Equals(turn.Id, update.ReferenceTurnId, StringComparison.Ordinal)),
            _ => null
        };
        if (update.ContinuityMode == ImageContinuityMode.ContinueMatched && parent == null)
        {
            throw new InvalidOperationException("Reference turn was not found for continue_match.");
        }

        var lineageId = (update.ContinuityMode == ImageContinuityMode.ContinueLast || update.ContinuityMode == ImageContinuityMode.ContinueMatched) && parent != null
            ? parent.LineageId
            : Guid.NewGuid().ToString("N");

        var turn = new ImageGenerationTurnRecord
        {
            LineageId = lineageId,
            ParentTurnId = parent?.Id,
            Prompt = update.Prompt,
            RevisedPrompt = update.RevisedPrompt,
            AttachmentId = update.Attachment.Id,
            FileName = update.Attachment.FileName,
            StoredPath = update.Attachment.StoredPath,
            MimeType = update.Attachment.MimeType,
            ContinuityMode = update.ContinuityMode,
            ContinuityStatus = update.UsedPromptOnlyFallback
                ? ImageContinuityStatus.PromptOnlyFallback
                : ImageContinuityStatus.PixelContinuity,
            Warning = update.Warning,
            CreatedAt = update.Attachment.CreatedAt == default ? DateTime.Now : update.Attachment.CreatedAt
        };

        session.Turns.Add(turn);
        session.ActiveLineageId = lineageId;
        session.UpdatedAt = DateTime.Now;
        await SaveAsync(session, cancellationToken);
        return session;
    }

    public async Task<ImageGenerationTurnRecord?> GetActiveTurnAsync(string conversationId, CancellationToken cancellationToken = default)
    {
        var session = await LoadAsync(conversationId, cancellationToken);
        return session == null ? null : GetActiveTurn(session);
    }

    public async Task<ImageReferenceResolutionResult> ResolveReferenceTurnAsync(
        string conversationId,
        string referenceQuery,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(referenceQuery))
        {
            return new ImageReferenceResolutionResult
            {
                Status = ImageReferenceResolutionStatus.InvalidQuery
            };
        }

        var session = await LoadAsync(conversationId, cancellationToken);
        if (session == null || session.Turns.Count == 0)
        {
            return new ImageReferenceResolutionResult
            {
                Status = ImageReferenceResolutionStatus.NoImages
            };
        }

        var usableTurns = session.Turns
            .Where(IsUsableReferenceTurn)
            .OrderBy(turn => turn.CreatedAt)
            .ToList();
        if (usableTurns.Count == 0)
        {
            return new ImageReferenceResolutionResult
            {
                Status = ImageReferenceResolutionStatus.AssetMissing
            };
        }

        var normalizedQuery = NormalizeText(referenceQuery);
        if (string.IsNullOrWhiteSpace(normalizedQuery))
        {
            return new ImageReferenceResolutionResult
            {
                Status = ImageReferenceResolutionStatus.InvalidQuery
            };
        }

        var usableLineages = usableTurns
            .GroupBy(turn => turn.LineageId, StringComparer.Ordinal)
            .Select(group => new LineageMatch(group.OrderBy(turn => turn.CreatedAt).ToList()))
            .ToList();

        var literalMatches = usableLineages
            .Where(lineage => lineage.Turns.Any(turn => LiteralContainsMatch(normalizedQuery, turn)))
            .ToList();
        if (literalMatches.Count == 1)
        {
            return ResolveSingleLineage(literalMatches[0], 1d);
        }

        if (literalMatches.Count > 1)
        {
            return new ImageReferenceResolutionResult
            {
                Status = ImageReferenceResolutionStatus.Ambiguous,
                Candidates = literalMatches
                    .Select(lineage => ToCandidate(lineage.GetLatestTurn(), 1d))
                    .Take(3)
                    .ToList()
            };
        }

        var scoredLineages = usableLineages
            .Select(lineage => new
            {
                Lineage = lineage,
                Score = lineage.Turns.Max(turn => ComputeDiceScore(normalizedQuery, GetTurnSearchText(turn)))
            })
            .OrderByDescending(item => item.Score)
            .ThenByDescending(item => item.Lineage.GetLatestTurn().CreatedAt)
            .ToList();

        var top = scoredLineages[0];
        var secondScore = scoredLineages.Count > 1 ? scoredLineages[1].Score : 0d;
        if (top.Score < ResolveThreshold)
        {
            return new ImageReferenceResolutionResult
            {
                Status = ImageReferenceResolutionStatus.NoMatch
            };
        }

        if (top.Score - secondScore < ResolveMargin)
        {
            return new ImageReferenceResolutionResult
            {
                Status = ImageReferenceResolutionStatus.Ambiguous,
                Candidates = scoredLineages
                    .Take(3)
                    .Select(item => ToCandidate(item.Lineage.GetLatestTurn(), item.Score))
                    .ToList()
            };
        }

        return ResolveSingleLineage(top.Lineage, top.Score);
    }

    public async Task<ImageGenerationSessionSnapshot?> CreateSnapshotAsync(string conversationId, CancellationToken cancellationToken = default)
    {
        var session = await LoadAsync(conversationId, cancellationToken);
        return session == null ? null : ToSnapshot(session);
    }

    public async Task PersistSnapshotAsync(ImageGenerationSessionSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        var record = new ImageGenerationSessionRecord
        {
            ConversationId = snapshot.ConversationId,
            HistoryId = snapshot.HistoryId,
            ActiveLineageId = snapshot.ActiveLineageId,
            CreatedAt = snapshot.CreatedAt,
            UpdatedAt = snapshot.UpdatedAt,
            Turns = snapshot.Turns.Select(CloneTurn).ToList()
        };

        await SaveAsync(record, cancellationToken);
    }

    public async Task<ImageGenerationSessionRecord?> ReconcileAsync(
        string conversationId,
        IReadOnlyCollection<string> survivingAttachmentIds,
        CancellationToken cancellationToken = default)
    {
        var session = await LoadAsync(conversationId, cancellationToken);
        if (session == null)
        {
            return null;
        }

        var survivingIds = survivingAttachmentIds.Count == 0
            ? new HashSet<string>(StringComparer.Ordinal)
            : new HashSet<string>(survivingAttachmentIds, StringComparer.Ordinal);

        var originalCount = session.Turns.Count;
        session.Turns = session.Turns
            .Where(turn => survivingIds.Contains(turn.AttachmentId))
            .OrderBy(turn => turn.CreatedAt)
            .ToList();

        if (session.Turns.Count == 0)
        {
            await DeleteAsync(conversationId, cancellationToken);
            return null;
        }

        if (originalCount == session.Turns.Count && session.ActiveLineageId == GetLatestTurn(session)?.LineageId)
        {
            return session;
        }

        session.ActiveLineageId = GetLatestTurn(session)?.LineageId;
        session.UpdatedAt = DateTime.Now;
        await SaveAsync(session, cancellationToken);
        return session;
    }

    public Task DeleteAsync(string conversationId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = GetPath(conversationId);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return Task.CompletedTask;
    }

    private async Task SaveAsync(ImageGenerationSessionRecord session, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(_sessionDirectory);
        var path = GetPath(session.ConversationId);
        var tempPath = $"{path}.tmp";
        var json = JsonSerializer.Serialize(session, JsonOptions);
        await File.WriteAllTextAsync(tempPath, json, cancellationToken);
        File.Move(tempPath, path, overwrite: true);
    }

    private static ImageGenerationSessionSnapshot ToSnapshot(ImageGenerationSessionRecord session)
    {
        return new ImageGenerationSessionSnapshot
        {
            ConversationId = session.ConversationId,
            HistoryId = session.HistoryId,
            ActiveLineageId = session.ActiveLineageId,
            CreatedAt = session.CreatedAt,
            UpdatedAt = session.UpdatedAt,
            Turns = session.Turns.Select(CloneTurn).ToList()
        };
    }

    private static ImageGenerationTurnRecord CloneTurn(ImageGenerationTurnRecord turn)
    {
        return new ImageGenerationTurnRecord
        {
            Id = turn.Id,
            LineageId = turn.LineageId,
            ParentTurnId = turn.ParentTurnId,
            Prompt = turn.Prompt,
            RevisedPrompt = turn.RevisedPrompt,
            AttachmentId = turn.AttachmentId,
            FileName = turn.FileName,
            StoredPath = turn.StoredPath,
            MimeType = turn.MimeType,
            ContinuityMode = turn.ContinuityMode,
            ContinuityStatus = turn.ContinuityStatus,
            Warning = turn.Warning,
            CreatedAt = turn.CreatedAt
        };
    }

    private static ImageGenerationTurnRecord? GetActiveTurn(ImageGenerationSessionRecord session)
    {
        if (!string.IsNullOrWhiteSpace(session.ActiveLineageId))
        {
            var active = session.Turns
                .Where(turn => string.Equals(turn.LineageId, session.ActiveLineageId, StringComparison.Ordinal))
                .OrderByDescending(turn => turn.CreatedAt)
                .FirstOrDefault();
            if (active != null)
            {
                return active;
            }
        }

        return GetLatestTurn(session);
    }

    private static ImageGenerationTurnRecord? GetLatestTurn(ImageGenerationSessionRecord session) =>
        session.Turns.OrderByDescending(turn => turn.CreatedAt).FirstOrDefault();

    private static bool IsUsableReferenceTurn(ImageGenerationTurnRecord turn) =>
        !string.IsNullOrWhiteSpace(turn.StoredPath) && File.Exists(turn.StoredPath);

    private static ImageReferenceResolutionResult ResolveSingleLineage(LineageMatch lineage, double score)
    {
        var resolvedTurn = lineage.GetLatestTurn();
        return new ImageReferenceResolutionResult
        {
            Status = ImageReferenceResolutionStatus.Resolved,
            ResolvedTurn = resolvedTurn,
            Candidates =
            [
                ToCandidate(resolvedTurn, score)
            ]
        };
    }

    private static bool LiteralContainsMatch(string normalizedQuery, ImageGenerationTurnRecord turn)
    {
        var primary = NormalizeText(string.IsNullOrWhiteSpace(turn.RevisedPrompt) ? turn.Prompt : turn.RevisedPrompt);
        if (!string.IsNullOrWhiteSpace(primary) && primary.Contains(normalizedQuery, StringComparison.Ordinal))
        {
            return true;
        }

        var fallback = NormalizeText(turn.Prompt);
        return !string.IsNullOrWhiteSpace(fallback) && fallback.Contains(normalizedQuery, StringComparison.Ordinal);
    }

    private static string GetTurnSearchText(ImageGenerationTurnRecord turn)
    {
        var primary = string.IsNullOrWhiteSpace(turn.RevisedPrompt) ? turn.Prompt : turn.RevisedPrompt;
        return NormalizeText($"{primary} {turn.Prompt}");
    }

    private static string NormalizeText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(text.Length);
        var previousWasSpace = false;
        foreach (var ch in text)
        {
            var normalized = char.ToLowerInvariant(ch);
            if (char.IsLetterOrDigit(normalized))
            {
                builder.Append(normalized);
                previousWasSpace = false;
                continue;
            }

            if (char.IsWhiteSpace(normalized) && !previousWasSpace && builder.Length > 0)
            {
                builder.Append(' ');
                previousWasSpace = true;
            }
        }

        return builder.ToString().Trim();
    }

    private static double ComputeDiceScore(string left, string right)
    {
        if (string.IsNullOrEmpty(left) || string.IsNullOrEmpty(right))
        {
            return 0d;
        }

        if (string.Equals(left, right, StringComparison.Ordinal))
        {
            return 1d;
        }

        var leftBigrams = BuildBigramMultiset(left);
        var rightBigrams = BuildBigramMultiset(right);
        if (leftBigrams.Count == 0 || rightBigrams.Count == 0)
        {
            return 0d;
        }

        var overlap = 0;
        foreach (var (bigram, leftCount) in leftBigrams)
        {
            if (rightBigrams.TryGetValue(bigram, out var rightCount))
            {
                overlap += Math.Min(leftCount, rightCount);
            }
        }

        var total = leftBigrams.Values.Sum() + rightBigrams.Values.Sum();
        return total == 0 ? 0d : (2d * overlap) / total;
    }

    private static Dictionary<string, int> BuildBigramMultiset(string text)
    {
        var chars = text.Where(ch => !char.IsWhiteSpace(ch)).ToArray();
        if (chars.Length < 2)
        {
            return new Dictionary<string, int>(StringComparer.Ordinal);
        }

        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < chars.Length - 1; i++)
        {
            var key = new string([chars[i], chars[i + 1]]);
            map[key] = map.TryGetValue(key, out var count) ? count + 1 : 1;
        }

        return map;
    }

    private static ImageReferenceTurnCandidate ToCandidate(ImageGenerationTurnRecord turn, double score)
    {
        var promptPreview = string.IsNullOrWhiteSpace(turn.RevisedPrompt) ? turn.Prompt : turn.RevisedPrompt!;
        if (promptPreview.Length > 120)
        {
            promptPreview = promptPreview[..120];
        }

        return new ImageReferenceTurnCandidate
        {
            TurnId = turn.Id,
            LineageId = turn.LineageId,
            CreatedAt = turn.CreatedAt,
            FileName = turn.FileName,
            PromptPreview = promptPreview,
            MatchScore = Math.Round(score, 4)
        };
    }

    private string GetPath(string conversationId) => Path.Combine(_sessionDirectory, $"{conversationId}.json");

    private sealed record LineageMatch(List<ImageGenerationTurnRecord> Turns)
    {
        public ImageGenerationTurnRecord GetLatestTurn() => Turns.OrderByDescending(turn => turn.CreatedAt).First();
    }
}
