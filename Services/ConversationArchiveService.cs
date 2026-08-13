using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Athena.UI.Models;
using Athena.UI.Services.Interfaces;
using Serilog;

namespace Athena.UI.Services;

public class ConversationArchiveService : IConversationArchiveService, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly IConversationArchiveStore _store;
    private readonly IConversationDraftStore _draftStore;
    private readonly IConversationTitleGenerator _titleGenerator;
    private readonly IImageGenerationSessionService? _imageSessionService;
    private readonly string _pendingArchiveDirectory;
    private readonly ILogger _logger;
    private readonly Channel<string> _channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false
    });
    private readonly CancellationTokenSource _processingCts = new();
    private bool _disposed;

    public ConversationArchiveService(
        IConversationArchiveStore store,
        IConversationDraftStore draftStore,
        IConversationTitleGenerator titleGenerator,
        IPlatformPathService platformPathService,
        ILogger logger,
        IImageGenerationSessionService? imageSessionService = null)
    {
        _store = store;
        _draftStore = draftStore;
        _titleGenerator = titleGenerator;
        _imageSessionService = imageSessionService;
        _pendingArchiveDirectory = platformPathService.GetPendingArchiveDirectory();
        _logger = logger;

        Directory.CreateDirectory(_pendingArchiveDirectory);
        EnqueuePendingFiles();
        _ = Task.Run(ProcessLoopAsync);
    }

    public event EventHandler<ConversationArchiveResultEventArgs>? ArchiveCompleted;

    public event EventHandler<ConversationArchiveResultEventArgs>? ArchiveFailed;

    public event EventHandler<ConversationArchiveResultEventArgs>? ArchiveStaged;

    public Task<System.Collections.Generic.List<ConversationHistoryItem>> LoadAllAsync() => _store.LoadAllAsync();

    public Task<ConversationHistoryItem?> LoadByIdAsync(string id) => _store.LoadByIdAsync(id);

    public void SaveDraft(ConversationDraftSnapshot snapshot) => _draftStore.Save(snapshot);

    public ConversationDraftSnapshot? LoadDraft() => _draftStore.Load();

    public void DeleteDraft() => _draftStore.Delete();

    public async Task DeleteAsync(string id)
    {
        var item = await _store.LoadByIdAsync(id);
        await _store.DeleteAsync(id);
        if (!string.IsNullOrWhiteSpace(item?.ConversationId) && _imageSessionService != null)
        {
            await _imageSessionService.DeleteAsync(item.ConversationId);
        }
    }

    public async Task StageArchiveAsync(ConversationArchiveSnapshot snapshot, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var stagedSnapshot = new ConversationArchiveSnapshot
        {
            SchemaVersion = snapshot.SchemaVersion,
            ConversationId = snapshot.ConversationId,
            HistoryId = snapshot.HistoryId,
            Revision = snapshot.Revision,
            Title = snapshot.Title,
            CreatedAt = snapshot.CreatedAt,
            UpdatedAt = snapshot.UpdatedAt,
            ContextSummary = snapshot.ContextSummary,
            OrphanedLegacySummary = snapshot.OrphanedLegacySummary,
            CompressionHistory = snapshot.CompressionHistory.Select(CloneCompressionCheckpoint).ToList(),
            Anchors = snapshot.Anchors.Select(CloneContextAnchor).ToList(),
            ForkedFromConversationId = snapshot.ForkedFromConversationId,
            ForkedFromHistoryId = snapshot.ForkedFromHistoryId,
            ForkedAtMessageId = snapshot.ForkedAtMessageId,
            WorkspaceId = snapshot.WorkspaceId,
            Draft = snapshot.Draft,
            IsPinned = snapshot.IsPinned,
            RuntimeStatus = snapshot.RuntimeStatus,
            Messages = ConversationPersistenceHelper.CloneMessages(snapshot.Messages),
            ImageSession = snapshot.ImageSession == null
                ? null
                : new ImageGenerationSessionSnapshot
                {
                    ConversationId = snapshot.ImageSession.ConversationId,
                    HistoryId = snapshot.ImageSession.HistoryId,
                    ActiveLineageId = snapshot.ImageSession.ActiveLineageId,
                    CreatedAt = snapshot.ImageSession.CreatedAt,
                    UpdatedAt = snapshot.ImageSession.UpdatedAt,
                    Turns = snapshot.ImageSession.Turns.Select(CloneTurn).ToList()
                },
            CapturedAt = snapshot.CapturedAt,
            ForceGenerateSummary = snapshot.ForceGenerateSummary
        };

        var fileName = $"{stagedSnapshot.CapturedAt:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}.json";
        var tempFilePath = Path.Combine(_pendingArchiveDirectory, $"{fileName}.tmp");
        var finalFilePath = Path.Combine(_pendingArchiveDirectory, fileName);
        var json = JsonSerializer.Serialize(stagedSnapshot, JsonOptions);

        await File.WriteAllTextAsync(tempFilePath, json, ct);
        File.Move(tempFilePath, finalFilePath, overwrite: true);
        await _channel.Writer.WriteAsync(finalFilePath, ct);
        ArchiveStaged?.Invoke(this, new ConversationArchiveResultEventArgs(stagedSnapshot, finalFilePath));
        _logger.Information("Conversation archive written to pending queue: {Path}", finalFilePath);
    }

    private void EnqueuePendingFiles()
    {
        foreach (var filePath in Directory
                     .GetFiles(_pendingArchiveDirectory, "*.json")
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            _channel.Writer.TryWrite(filePath);
        }
    }

    private async Task ProcessLoopAsync()
    {
        await foreach (var stagedFilePath in _channel.Reader.ReadAllAsync(_processingCts.Token))
        {
            await ProcessStagedFileAsync(stagedFilePath, _processingCts.Token);
        }
    }

    private async Task ProcessStagedFileAsync(string stagedFilePath, CancellationToken ct)
    {
        if (!File.Exists(stagedFilePath))
        {
            return;
        }

        ConversationArchiveSnapshot? snapshot = null;

        try
        {
            var json = await File.ReadAllTextAsync(stagedFilePath, ct);
            snapshot = JsonSerializer.Deserialize<ConversationArchiveSnapshot>(json, JsonOptions);
            if (snapshot == null)
            {
                throw new InvalidOperationException("Archived snapshot payload is empty.");
            }

            var historyItem = await UpsertFromSnapshotAsync(snapshot, ct);
            File.Delete(stagedFilePath);
            ArchiveCompleted?.Invoke(this, new ConversationArchiveResultEventArgs(snapshot, stagedFilePath, historyItem));
            _logger.Information("Background archive completed: {HistoryId}", historyItem.Id);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (ConversationRevisionConflictException ex)
        {
            var historyId = snapshot?.HistoryId;
            var current = string.IsNullOrWhiteSpace(historyId) ? null : await _store.LoadByIdAsync(historyId);
            if (snapshot != null && current != null)
            {
                File.Delete(stagedFilePath);
                ArchiveCompleted?.Invoke(this, new ConversationArchiveResultEventArgs(snapshot, stagedFilePath, current));
                _logger.Information(ex, "Stale revision archive safely retired: {HistoryId}, Incoming={Incoming}, Current={Current}",
                    current.Id, snapshot.Revision, current.Revision);
                return;
            }

            _logger.Warning(ex, "Archive revision conflict and current session cannot be located; keeping pending file: {Path}", stagedFilePath);
        }
        catch (Exception ex)
        {
            snapshot ??= SafeReadSnapshot(stagedFilePath);
            if (snapshot != null)
            {
                ArchiveFailed?.Invoke(this, new ConversationArchiveResultEventArgs(snapshot, stagedFilePath, exception: ex));
            }
            _logger.Error(ex, "Background archive failed; pending file kept for retry: {Path}", stagedFilePath);
        }
    }

    internal async Task<ConversationHistoryItem> UpsertFromSnapshotAsync(ConversationArchiveSnapshot snapshot, CancellationToken ct = default)
    {
        var messages = ConversationPersistenceHelper.CloneMessages(snapshot.Messages);
        var title = await _titleGenerator.GenerateAsync(messages, snapshot.ForceGenerateSummary, ct);
        var historyId = string.IsNullOrWhiteSpace(snapshot.HistoryId) ? Guid.NewGuid().ToString() : snapshot.HistoryId!;
        var existing = await _store.LoadByIdAsync(historyId);
        var revision = snapshot.Revision > 0
            ? snapshot.Revision
            : Math.Max(1, (existing?.Revision ?? 0) + 1);
        var item = new ConversationHistoryItem
        {
            SchemaVersion = snapshot.SchemaVersion,
            Revision = revision,
            ConversationId = string.IsNullOrWhiteSpace(snapshot.ConversationId)
                ? existing?.ConversationId ?? Guid.NewGuid().ToString("N")
                : snapshot.ConversationId,
            Id = historyId,
            Summary = title,
            ContextSummary = snapshot.ContextSummary,
            OrphanedLegacySummary = snapshot.OrphanedLegacySummary,
            CompressionHistory = snapshot.CompressionHistory.Select(CloneCompressionCheckpoint).ToList(),
            Anchors = snapshot.Anchors.Select(CloneContextAnchor).ToList(),
            ForkedFromConversationId = snapshot.ForkedFromConversationId ?? existing?.ForkedFromConversationId,
            ForkedFromHistoryId = snapshot.ForkedFromHistoryId ?? existing?.ForkedFromHistoryId,
            ForkedAtMessageId = snapshot.ForkedAtMessageId ?? existing?.ForkedAtMessageId,
            WorkspaceId = snapshot.WorkspaceId ?? existing?.WorkspaceId,
            Draft = snapshot.Draft,
            IsPinned = snapshot.IsPinned || existing?.IsPinned == true,
            RuntimeStatus = snapshot.RuntimeStatus,
            MessageCount = messages.Count(ConversationArchiveStore.IsCountableMessage),
            Messages = messages,
            CreatedAt = existing?.CreatedAt ?? messages.FirstOrDefault()?.Timestamp ?? snapshot.CapturedAt,
            UpdatedAt = snapshot.CapturedAt
        };
        await _store.SaveAsync(item);

        if (snapshot.ImageSession != null && _imageSessionService != null)
        {
            await _imageSessionService.PersistSnapshotAsync(new ImageGenerationSessionSnapshot
            {
                ConversationId = item.ConversationId,
                HistoryId = item.Id,
                ActiveLineageId = snapshot.ImageSession.ActiveLineageId,
                CreatedAt = snapshot.ImageSession.CreatedAt,
                UpdatedAt = snapshot.ImageSession.UpdatedAt,
                Turns = snapshot.ImageSession.Turns.Select(CloneTurn).ToList()
            }, ct);
        }
        return item;
    }

    private static ConversationArchiveSnapshot? SafeReadSnapshot(string stagedFilePath)
    {
        try
        {
            if (!File.Exists(stagedFilePath))
            {
                return null;
            }

            var json = File.ReadAllText(stagedFilePath);
            return JsonSerializer.Deserialize<ConversationArchiveSnapshot>(json, JsonOptions);
        }
        catch
        {
            return null;
        }
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

    private static ContextAnchorRecord CloneContextAnchor(ContextAnchorRecord anchor)
    {
        return new ContextAnchorRecord
        {
            PrefixMessageCount = anchor.PrefixMessageCount,
            PrefixDigest = anchor.PrefixDigest,
            InputTokens = anchor.InputTokens,
            CachedInputTokens = anchor.CachedInputTokens,
            OutputTokens = anchor.OutputTokens,
            ProfileKey = anchor.ProfileKey,
            FixedOverheadFingerprint = anchor.FixedOverheadFingerprint,
            Revision = anchor.Revision,
            ObservedAtUtc = anchor.ObservedAtUtc
        };
    }

    private static CompressionCheckpointRecord CloneCompressionCheckpoint(CompressionCheckpointRecord checkpoint)
    {
        return new CompressionCheckpointRecord
        {
            CompressionId = checkpoint.CompressionId,
            AppliedRevision = checkpoint.AppliedRevision,
            MessageIds = checkpoint.MessageIds.ToList(),
            SummaryBefore = checkpoint.SummaryBefore,
            SummaryAfter = checkpoint.SummaryAfter,
            SummaryAfterHash = checkpoint.SummaryAfterHash,
            Mode = checkpoint.Mode,
            CompressionModelFingerprint = checkpoint.CompressionModelFingerprint,
            PromptVersion = checkpoint.PromptVersion,
            PreCompressionTokens = checkpoint.PreCompressionTokens,
            PostCompressionTokens = checkpoint.PostCompressionTokens,
            UsedLocalFallback = checkpoint.UsedLocalFallback,
            CreatedAt = checkpoint.CreatedAt
        };
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _channel.Writer.TryComplete();
        _processingCts.Cancel();
        _processingCts.Dispose();
    }
}
