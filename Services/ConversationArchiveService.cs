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

public class ConversationArchiveService : IConversationArchiveService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly IConversationHistoryService _historyService;
    private readonly string _pendingArchiveDirectory;
    private readonly ILogger _logger;
    private readonly Channel<string> _channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false
    });
    private readonly CancellationTokenSource _processingCts = new();

    public ConversationArchiveService(
        IConversationHistoryService historyService,
        IPlatformPathService platformPathService,
        ILogger logger)
    {
        _historyService = historyService;
        _pendingArchiveDirectory = platformPathService.GetPendingArchiveDirectory();
        _logger = logger;

        Directory.CreateDirectory(_pendingArchiveDirectory);
        EnqueuePendingFiles();
        _ = Task.Run(ProcessLoopAsync);
    }

    public event EventHandler<ConversationArchiveResultEventArgs>? ArchiveCompleted;

    public event EventHandler<ConversationArchiveResultEventArgs>? ArchiveFailed;

    public event EventHandler<ConversationArchiveResultEventArgs>? ArchiveStaged;

    public async Task StageArchiveAsync(ConversationArchiveSnapshot snapshot, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var stagedSnapshot = new ConversationArchiveSnapshot
        {
            ConversationId = snapshot.ConversationId,
            HistoryId = snapshot.HistoryId,
            ContextSummary = snapshot.ContextSummary,
            ForkedFromConversationId = snapshot.ForkedFromConversationId,
            ForkedFromHistoryId = snapshot.ForkedFromHistoryId,
            ForkedAtMessageId = snapshot.ForkedAtMessageId,
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
        _logger.Information("对话归档已写入待处理队列: {Path}", finalFilePath);
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

            var historyItem = await _historyService.UpsertFromSnapshotAsync(snapshot, ct);
            File.Delete(stagedFilePath);
            ArchiveCompleted?.Invoke(this, new ConversationArchiveResultEventArgs(snapshot, stagedFilePath, historyItem));
            _logger.Information("后台归档完成: {HistoryId}", historyItem.Id);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            snapshot ??= SafeReadSnapshot(stagedFilePath);
            if (snapshot != null)
            {
                ArchiveFailed?.Invoke(this, new ConversationArchiveResultEventArgs(snapshot, stagedFilePath, exception: ex));
            }
            _logger.Error(ex, "后台归档失败，待重试文件已保留: {Path}", stagedFilePath);
        }
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
}
