using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Athena.UI.Models;
using Athena.UI.Services;
using Athena.UI.Services.Functions;
using Athena.UI.Services.Interfaces;
using Serilog;

Log.Logger = new LoggerConfiguration().MinimumLevel.Debug().CreateLogger();

var tests = new (string Name, Func<Task> Run)[]
{
    ("snapshot filters empty loading assistant bubbles", TestSnapshotFilterAsync),
    ("conversation persistence preserves audio metadata", TestAudioPersistenceCloneAsync),
    ("audio config inherits primary settings when audio values are empty", TestAudioConfigInheritanceAsync),
    ("upsert preserves created time and updates content", TestUpsertAsync),
    ("upsert persists linked image session", TestImageSessionUpsertAsync),
    ("image session snapshot reloads persisted lineage", TestImageSessionSnapshotAsync),
    ("continue_match resolves earlier lineage and persists revived active lineage", TestContinueMatchPersistenceAsync),
    ("continue_match ambiguity returns candidates", TestReferenceResolutionAmbiguousAsync),
    ("continue_match skips missing assets and surfaces asset-missing state", TestReferenceResolutionMissingAssetAsync),
    ("continue_match function returns structured failures and success metadata", TestImageGenerationFunctionContinueMatchAsync),
    ("image generation payload includes reference images and doubao extras", TestReferenceImagePayloadAsync),
    ("image generation payload omits continuity fields for new roots", TestPromptOnlyPayloadAsync),
    ("image generation rejects missing continuity image files", TestMissingReferenceImageAsync),
    ("fallback summary still saves without secondary model", TestFallbackSummaryAsync),
    ("archive queue stages locally and retries on restart", TestArchiveQueueReplayAsync),
    ("recurrence migration and validation works", TestRecurrenceMigrationAndValidationAsync),
    ("first trigger handles weekday boundaries", TestFirstTriggerCalculationAsync),
    ("scheduler serializes foreground work and skips recurring collisions", TestSchedulerForegroundSerialPolicyAsync),
    ("scheduler advances long-running recurring task to next future slot", TestSchedulerLongRunningRecurringAsync),
    ("create_task returns structured success and validation failures", TestCreateTaskStructuredResponsesAsync),
    ("diff: exact single-block apply", TestDiffExactApplyAsync),
    ("diff: trailing-whitespace tolerance", TestDiffTrailingWhitespaceAsync),
    ("diff: indentation-tolerant match reindents replacement", TestDiffReindentAsync),
    ("diff: ambiguous match fails with previews and no mutation", TestDiffAmbiguousAsync),
    ("diff: replaceAll changes every occurrence", TestDiffReplaceAllAsync),
    ("diff: empty SEARCH is a parse error", TestDiffEmptySearchAsync),
    ("diff: unmatched block surfaces nearest hint", TestDiffNearestHintAsync),
    ("diff: multi-block failure rolls back atomically", TestDiffMultiBlockAtomicAsync),
    ("diff: strict mode rejects whitespace drift", TestDiffStrictModeAsync)
};

var failures = new List<string>();

foreach (var (name, run) in tests)
{
    try
    {
        await run();
        Console.WriteLine($"[PASS] {name}");
    }
    catch (Exception ex)
    {
        failures.Add($"{name}: {ex.Message}");
        Console.WriteLine($"[FAIL] {name}");
        Console.WriteLine(ex);
    }
}

return failures.Count == 0 ? 0 : 1;

static Task TestSnapshotFilterAsync()
{
    var loadingBubble = new ChatMessage
    {
        Role = "assistant",
        Content = string.Empty,
        IsLoading = true
    };

    var partialBubble = new ChatMessage
    {
        Role = "assistant",
        Content = "partial",
        IsLoading = true
    };

    AssertFalse(ConversationPersistenceHelper.ShouldPersistMessage(loadingBubble), "empty loading bubble should be skipped");
    AssertTrue(ConversationPersistenceHelper.ShouldPersistMessage(partialBubble), "partial assistant text should be archived");
    return Task.CompletedTask;
}

static Task TestAudioPersistenceCloneAsync()
{
    var source = new ChatMessage
    {
        Role = "assistant",
        Content = "audio reply",
        OutputAudioReferenceId = "audio_123",
        AudioErrorMessage = "playback failed",
        Attachments =
        [
            new ChatAttachment
            {
                Kind = AttachmentKind.Audio,
                FileName = "reply.mp3",
                StoredPath = "/tmp/reply.mp3",
                MimeType = "audio/mpeg",
                Duration = TimeSpan.FromSeconds(12),
                SizeBytes = 1024
            }
        ]
    };

    var clone = ConversationPersistenceHelper.CloneMessage(source);

    AssertEqual("audio_123", clone.OutputAudioReferenceId, "audio reference should be preserved");
    AssertEqual("playback failed", clone.AudioErrorMessage, "audio error should be preserved");
    AssertEqual(1, clone.Attachments.Count, "audio attachment should be cloned");
    AssertEqual(AttachmentKind.Audio, clone.Attachments[0].Kind, "attachment kind should stay audio");
    AssertEqual(TimeSpan.FromSeconds(12), clone.Attachments[0].Duration, "audio duration should be preserved");
    return Task.CompletedTask;
}

static Task TestAudioConfigInheritanceAsync()
{
    var config = new AppConfig
    {
        Provider = "OpenAI",
        BaseUrl = "https://api.openai.com/v1",
        ApiKey = "primary-key",
        Model = "gpt-5-mini",
        ChatAudioEnabled = true,
        ChatAudioVoice = "alloy"
    };

    var resolved = AudioConfigResolver.Resolve(config);

    AssertEqual("OpenAI", resolved.Provider, "audio provider should default independently");
    AssertEqual("https://api.openai.com/v1/audio/speech", resolved.BaseUrl, "audio base url should use dedicated audio endpoint");
    AssertEqual("primary-key", resolved.ApiKey, "audio api key should inherit primary key");
    AssertEqual("gpt-4o-mini-tts", resolved.Model, "audio model should use dedicated default");
    AssertEqual("alloy", resolved.Voice, "audio voice should use explicit audio voice");
    AssertFalse(resolved.AutoPlay, "auto play should default to false");
    return Task.CompletedTask;
}

static async Task TestUpsertAsync()
{
    using var harness = new TestHarness();
    var service = harness.CreateHistoryService();
    var historyId = Guid.NewGuid().ToString("N");
    var createdAt = new DateTime(2026, 5, 1, 8, 0, 0, DateTimeKind.Local);

    var initial = new ConversationArchiveSnapshot
    {
        HistoryId = historyId,
        CapturedAt = createdAt,
        ForceGenerateSummary = false,
        Messages =
        [
            new ChatMessage
            {
                Role = "user",
                Content = "hello world",
                Timestamp = createdAt
            }
        ]
    };

    var saved = await service.UpsertFromSnapshotAsync(initial);

    var updatedAt = createdAt.AddMinutes(5);
    var updated = new ConversationArchiveSnapshot
    {
        HistoryId = historyId,
        CapturedAt = updatedAt,
        ForceGenerateSummary = false,
        Messages =
        [
            new ChatMessage
            {
                Role = "user",
                Content = "changed content",
                Timestamp = createdAt
            },
            new ChatMessage
            {
                Role = "assistant",
                Content = "response",
                Timestamp = updatedAt
            }
        ]
    };

    var reSaved = await service.UpsertFromSnapshotAsync(updated);
    var loaded = await service.LoadByIdAsync(historyId);

    AssertEqual(saved.Id, reSaved.Id, "history id should stay stable");
    AssertEqual(saved.CreatedAt, reSaved.CreatedAt, "created time should be preserved");
    AssertEqual(updatedAt, reSaved.UpdatedAt, "updated time should refresh from snapshot");
    AssertEqual(2, loaded?.Messages.Count ?? 0, "message count should update");
    AssertEqual("changed content", loaded?.Messages.First().Content, "messages should be replaced");
}

static async Task TestFallbackSummaryAsync()
{
    using var harness = new TestHarness();
    var service = harness.CreateHistoryService();
    var content = "This is a deliberately long first user prompt that should fall back to a truncated local summary.";

    var snapshot = new ConversationArchiveSnapshot
    {
        CapturedAt = new DateTime(2026, 5, 2, 9, 30, 0, DateTimeKind.Local),
        ForceGenerateSummary = true,
        Messages =
        [
            new ChatMessage
            {
                Role = "user",
                Content = content,
                Timestamp = new DateTime(2026, 5, 2, 9, 30, 0, DateTimeKind.Local)
            }
        ]
    };

    var item = await service.UpsertFromSnapshotAsync(snapshot);
    AssertEqual("This is a deliberately long fi...", item.Summary, "summary should fall back to first user message");
    AssertTrue(File.Exists(Path.Combine(harness.PathService.GetHistoryDirectory(), $"{item.Id}.json")), "history file should still be written");
}

static async Task TestImageSessionUpsertAsync()
{
    using var harness = new TestHarness();
    var service = harness.CreateHistoryService();
    var conversationId = Guid.NewGuid().ToString("N");
    var snapshot = new ConversationArchiveSnapshot
    {
        ConversationId = conversationId,
        CapturedAt = new DateTime(2026, 5, 2, 11, 0, 0, DateTimeKind.Local),
        ForceGenerateSummary = false,
        Messages =
        [
            new ChatMessage
            {
                Role = "user",
                Content = "draw something",
                Timestamp = new DateTime(2026, 5, 2, 11, 0, 0, DateTimeKind.Local)
            }
        ],
        ImageSession = new ImageGenerationSessionSnapshot
        {
            ConversationId = conversationId,
            ActiveLineageId = "lineage-a",
            Turns =
            [
                new ImageGenerationTurnRecord
                {
                    Id = "turn-a",
                    LineageId = "lineage-a",
                    Prompt = "draw something",
                    AttachmentId = "attachment-a",
                    FileName = "generated-a.png",
                    StoredPath = "/tmp/generated-a.png",
                    MimeType = "image/png",
                    ContinuityMode = ImageContinuityMode.NewRoot,
                    ContinuityStatus = ImageContinuityStatus.PixelContinuity
                }
            ]
        }
    };

    var item = await service.UpsertFromSnapshotAsync(snapshot);
    var imageSessionFile = Path.Combine(harness.PathService.GetImageGenerationSessionDirectory(), $"{conversationId}.json");
    AssertEqual(conversationId, item.ConversationId, "history item should preserve conversation id");
    AssertTrue(File.Exists(imageSessionFile), "image session file should be written");

    var json = await File.ReadAllTextAsync(imageSessionFile);
    var restored = JsonSerializer.Deserialize<ImageGenerationSessionRecord>(json, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
    AssertEqual(item.Id, restored?.HistoryId, "image session should bind to history id");
    AssertEqual(1, restored?.Turns.Count ?? 0, "image session should persist turns");
}

static async Task TestImageSessionSnapshotAsync()
{
    using var harness = new TestHarness();
    var service = new ImageGenerationSessionService(harness.PathService, Log.ForContext<ImageGenerationSessionService>());
    var conversationId = Guid.NewGuid().ToString("N");

    await service.CaptureAndPersistAsync(
        conversationId,
        historyId: "history-a",
        new ImageGenerationSessionUpdate
        {
            ContinuityMode = ImageContinuityMode.NewRoot,
            Prompt = "draw a classroom",
            Attachment = new ChatAttachment
            {
                Id = "attachment-a",
                Kind = AttachmentKind.Image,
                FileName = "generated-a.png",
                StoredPath = "/tmp/generated-a.png",
                MimeType = "image/png",
                CreatedAt = new DateTime(2026, 5, 24, 12, 0, 0, DateTimeKind.Local)
            }
        });

    var snapshot = await service.CreateSnapshotAsync(conversationId);
    AssertTrue(snapshot != null, "snapshot should be created for persisted image session");
    AssertEqual(conversationId, snapshot!.ConversationId, "snapshot should preserve conversation id");
    AssertEqual("history-a", snapshot.HistoryId, "snapshot should preserve bound history id");
    AssertEqual(1, snapshot.Turns.Count, "snapshot should include persisted turns");
    AssertEqual("attachment-a", snapshot.Turns[0].AttachmentId, "snapshot should preserve active turn attachment");
}

static async Task TestContinueMatchPersistenceAsync()
{
    using var harness = new TestHarness();
    using var classroomA = new TempFile(".png", [0x89, 0x50, 0x4E, 0x47]);
    using var eagle = new TempFile(".png", [0x89, 0x50, 0x4E, 0x47]);
    using var classroomB = new TempFile(".png", [0x89, 0x50, 0x4E, 0x47]);
    using var revived = new TempFile(".png", [0x89, 0x50, 0x4E, 0x47]);
    using var followUp = new TempFile(".png", [0x89, 0x50, 0x4E, 0x47]);
    var service = new ImageGenerationSessionService(harness.PathService, Log.ForContext<ImageGenerationSessionService>());
    var conversationId = Guid.NewGuid().ToString("N");

    await service.CaptureAndPersistAsync(
        conversationId,
        historyId: null,
        new ImageGenerationSessionUpdate
        {
            ContinuityMode = ImageContinuityMode.NewRoot,
            Prompt = "Chinese high school classroom, a sleepy boy on the desk and a teacher nearby",
            Attachment = CreateImageAttachment("attachment-a", "classroom-a.png", classroomA.Path, new DateTime(2026, 5, 24, 12, 0, 0, DateTimeKind.Local))
        });

    await service.CaptureAndPersistAsync(
        conversationId,
        historyId: null,
        new ImageGenerationSessionUpdate
        {
            ContinuityMode = ImageContinuityMode.ContinueLast,
            Prompt = "Make the classroom scene warmer and more cinematic",
            Attachment = CreateImageAttachment("attachment-b", "classroom-b.png", classroomB.Path, new DateTime(2026, 5, 24, 12, 1, 0, DateTimeKind.Local))
        });

    await service.CaptureAndPersistAsync(
        conversationId,
        historyId: null,
        new ImageGenerationSessionUpdate
        {
            ContinuityMode = ImageContinuityMode.NewRoot,
            Prompt = "A bald eagle gliding above a mountain lake",
            Attachment = CreateImageAttachment("attachment-c", "eagle.png", eagle.Path, new DateTime(2026, 5, 24, 12, 2, 0, DateTimeKind.Local))
        });

    var resolution = await service.ResolveReferenceTurnAsync(conversationId, "classroom sleepy student teacher");
    AssertEqual(ImageReferenceResolutionStatus.Resolved, resolution.Status, "reference query should resolve classroom lineage");
    AssertEqual("attachment-b", resolution.ResolvedTurn?.AttachmentId, "resolved reference should use latest turn in the matched lineage");

    var persisted = await service.CaptureAndPersistAsync(
        conversationId,
        historyId: null,
        new ImageGenerationSessionUpdate
        {
            ContinuityMode = ImageContinuityMode.ContinueMatched,
            ReferenceTurnId = resolution.ResolvedTurn?.Id,
            Prompt = "Now make the sleepy student wake up",
            Attachment = CreateImageAttachment("attachment-d", "revived.png", revived.Path, new DateTime(2026, 5, 24, 12, 3, 0, DateTimeKind.Local))
        });

    var revivedTurn = persisted!.Turns.Last();
    AssertEqual(resolution.ResolvedTurn?.Id, revivedTurn.ParentTurnId, "continue_match should point to the resolved latest turn");
    AssertEqual(resolution.ResolvedTurn?.LineageId, revivedTurn.LineageId, "continue_match should revive the matched lineage");
    AssertEqual(resolution.ResolvedTurn?.LineageId, persisted.ActiveLineageId, "matched lineage should become active");

    await service.CaptureAndPersistAsync(
        conversationId,
        historyId: null,
        new ImageGenerationSessionUpdate
        {
            ContinuityMode = ImageContinuityMode.ContinueLast,
            Prompt = "Add more sunlight on the awakened student",
            Attachment = CreateImageAttachment("attachment-e", "follow-up.png", followUp.Path, new DateTime(2026, 5, 24, 12, 4, 0, DateTimeKind.Local))
        });

    var activeTurn = await service.GetActiveTurnAsync(conversationId);
    AssertEqual("attachment-e", activeTurn?.AttachmentId, "continue_last should now continue the revived lineage");
    AssertEqual(revivedTurn.LineageId, activeTurn?.LineageId, "revived lineage should remain active after continue_last");
}

static async Task TestReferenceResolutionAmbiguousAsync()
{
    using var harness = new TestHarness();
    using var classroomA = new TempFile(".png", [0x89, 0x50, 0x4E, 0x47]);
    using var classroomB = new TempFile(".png", [0x89, 0x50, 0x4E, 0x47]);
    var service = new ImageGenerationSessionService(harness.PathService, Log.ForContext<ImageGenerationSessionService>());
    var conversationId = Guid.NewGuid().ToString("N");

    await service.CaptureAndPersistAsync(
        conversationId,
        historyId: null,
        new ImageGenerationSessionUpdate
        {
            ContinuityMode = ImageContinuityMode.NewRoot,
            Prompt = "Classroom scene with a sleeping student by the window",
            Attachment = CreateImageAttachment("attachment-a", "classroom-a.png", classroomA.Path, new DateTime(2026, 5, 24, 13, 0, 0, DateTimeKind.Local))
        });
    await service.CaptureAndPersistAsync(
        conversationId,
        historyId: null,
        new ImageGenerationSessionUpdate
        {
            ContinuityMode = ImageContinuityMode.NewRoot,
            Prompt = "Classroom scene with a teacher writing on the chalkboard",
            Attachment = CreateImageAttachment("attachment-b", "classroom-b.png", classroomB.Path, new DateTime(2026, 5, 24, 13, 1, 0, DateTimeKind.Local))
        });

    var resolution = await service.ResolveReferenceTurnAsync(conversationId, "classroom scene");
    AssertEqual(ImageReferenceResolutionStatus.Ambiguous, resolution.Status, "shared classroom literal matches should be ambiguous");
    AssertEqual(2, resolution.Candidates.Count, "ambiguous result should include both candidate lineages");
}

static async Task TestReferenceResolutionMissingAssetAsync()
{
    using var harness = new TestHarness();
    var missingPath = Path.Combine(harness.Root, "missing.png");
    var service = new ImageGenerationSessionService(harness.PathService, Log.ForContext<ImageGenerationSessionService>());
    var conversationId = Guid.NewGuid().ToString("N");

    await service.CaptureAndPersistAsync(
        conversationId,
        historyId: null,
        new ImageGenerationSessionUpdate
        {
            ContinuityMode = ImageContinuityMode.NewRoot,
            Prompt = "A classroom with a sleeping student",
            Attachment = CreateImageAttachment("attachment-a", "missing.png", missingPath, new DateTime(2026, 5, 24, 14, 0, 0, DateTimeKind.Local))
        });

    var resolution = await service.ResolveReferenceTurnAsync(conversationId, "sleeping student");
    AssertEqual(ImageReferenceResolutionStatus.AssetMissing, resolution.Status, "missing backing files should surface as asset missing");
}

static async Task TestImageGenerationFunctionContinueMatchAsync()
{
    using var harness = new TestHarness();
    using var source = new TempFile(".png", [0x89, 0x50, 0x4E, 0x47]);
    using var output = new TempFile(".png", [0x89, 0x50, 0x4E, 0x47]);
    var sessionService = new ImageGenerationSessionService(harness.PathService, Log.ForContext<ImageGenerationSessionService>());
    var conversationId = Guid.NewGuid().ToString("N");

    await sessionService.CaptureAndPersistAsync(
        conversationId,
        historyId: null,
        new ImageGenerationSessionUpdate
        {
            ContinuityMode = ImageContinuityMode.NewRoot,
            Prompt = "Chinese high school classroom, sleepy boy on desk, teacher nearby",
            Attachment = CreateImageAttachment("attachment-a", "source.png", source.Path, new DateTime(2026, 5, 24, 15, 0, 0, DateTimeKind.Local))
        });
    var seededSession = await sessionService.LoadAsync(conversationId);
    var seededTurn = seededSession!.Turns.Single();

    var imageService = new StubImageGenerationService
    {
        NextResult = new ImageGenerationResult
        {
            Success = true,
            Message = "generated",
            RevisedPrompt = "revised classroom scene",
            UsedPixelContinuity = true,
            Attachment = CreateImageAttachment("attachment-b", "output.png", output.Path, new DateTime(2026, 5, 24, 15, 1, 0, DateTimeKind.Local))
        }
    };

    var functions = new ImageGenerationFunctions(
        imageService,
        sessionService,
        new TestConversationSessionAccessor(conversationId),
        Log.ForContext<ImageGenerationFunctions>());

    var missingQuery = await functions.GenerateImageAsync("add warm sunset light", "continue_match");
    AssertFalse(missingQuery.Success, "continue_match without referenceQuery should fail");
    AssertJsonPropertyEquals("reference_query_required", missingQuery.Data, "code", "missing query should return the required code");

    var success = await functions.GenerateImageAsync("add warm sunset light", "continue_match", "sleepy boy classroom");
    AssertTrue(success.Success, "continue_match should succeed for a resolvable query");
    AssertEqual(1, imageService.LastRequest?.ReferenceImages.Count ?? 0, "resolved match should supply one reference image");
    AssertEqual("add warm sunset light", imageService.LastRequest?.Prompt, "image generation should use the user's original prompt without wrapper text");
    AssertJsonPropertyEquals("semantic_match", success.Data, "referenceSelectionMode", "success metadata should identify semantic match selection");
    AssertJsonPropertyEquals(seededTurn.Id, success.Data, "resolvedTurnId", "success metadata should expose resolved turn id");

    var ambiguousConversationId = Guid.NewGuid().ToString("N");
    using var classroomB = new TempFile(".png", [0x89, 0x50, 0x4E, 0x47]);
    using var classroomC = new TempFile(".png", [0x89, 0x50, 0x4E, 0x47]);
    await sessionService.CaptureAndPersistAsync(
        ambiguousConversationId,
        historyId: null,
        new ImageGenerationSessionUpdate
        {
            ContinuityMode = ImageContinuityMode.NewRoot,
            Prompt = "Classroom scene with a sleepy student near the window",
            Attachment = CreateImageAttachment("attachment-c", "classroom-b.png", classroomB.Path, new DateTime(2026, 5, 24, 15, 2, 0, DateTimeKind.Local))
        });
    await sessionService.CaptureAndPersistAsync(
        ambiguousConversationId,
        historyId: null,
        new ImageGenerationSessionUpdate
        {
            ContinuityMode = ImageContinuityMode.NewRoot,
            Prompt = "Classroom scene with a teacher at the chalkboard",
            Attachment = CreateImageAttachment("attachment-d", "classroom-c.png", classroomC.Path, new DateTime(2026, 5, 24, 15, 3, 0, DateTimeKind.Local))
        });

    var ambiguousFunctions = new ImageGenerationFunctions(
        imageService,
        sessionService,
        new TestConversationSessionAccessor(ambiguousConversationId),
        Log.ForContext<ImageGenerationFunctions>());

    var ambiguous = await ambiguousFunctions.GenerateImageAsync("change the lighting", "continue_match", "classroom scene");
    AssertFalse(ambiguous.Success, "ambiguous reference query should fail");
    AssertJsonPropertyEquals("reference_ambiguous", ambiguous.Data, "code", "ambiguous query should expose the ambiguity code");
}

static Task TestReferenceImagePayloadAsync()
{
    using var tempFile = new TempFile(".png", [0x89, 0x50, 0x4E, 0x47]);
    var payload = OpenAIImageGenerationService.BuildGenerationRequestPayload(
        "doubao-seedream-5.0-lite",
        "keep the same style",
        OpenAIImageGenerationService.PrepareReferenceImages(
        [
            new ImageGenerationReferenceImage
            {
                StoredPath = tempFile.Path,
                FileName = "source.png",
                MimeType = "image/png"
            }
        ]),
        "https://ark.cn-beijing.volces.com/api/v3");

    using var document = JsonDocument.Parse(payload.ToArray());
    var root = document.RootElement;
    AssertEqual("doubao-seedream-5.0-lite", root.GetProperty("model").GetString(), "payload should preserve model");
    AssertEqual("b64_json", root.GetProperty("response_format").GetString(), "payload should request base64 output");
    AssertEqual("disabled", root.GetProperty("sequential_image_generation").GetString(), "doubao continuity should disable sequential multi-image generation");
    AssertFalse(root.GetProperty("watermark").GetBoolean(), "doubao continuity payload should disable watermark");

    var imageArray = root.GetProperty("image");
    AssertEqual(1, imageArray.GetArrayLength(), "continuity payload should carry exactly one reference image");
    AssertTrue(imageArray[0].GetString()!.StartsWith("data:image/png;base64,", StringComparison.Ordinal), "reference image should be encoded as a data URI");
    return Task.CompletedTask;
}

static Task TestPromptOnlyPayloadAsync()
{
    var payload = OpenAIImageGenerationService.BuildGenerationRequestPayload(
        "gpt-image-1",
        "draw an eagle",
        [],
        "https://api.openai.com/v1");

    using var document = JsonDocument.Parse(payload.ToArray());
    var root = document.RootElement;
    AssertFalse(root.TryGetProperty("image", out _), "new root payload should not include reference images");
    AssertFalse(root.TryGetProperty("watermark", out _), "new root payload should not include doubao-only fields");
    AssertFalse(root.TryGetProperty("sequential_image_generation", out _), "new root payload should not include continuity-specific sequencing controls");
    return Task.CompletedTask;
}

static async Task TestMissingReferenceImageAsync()
{
    var service = new OpenAIImageGenerationService(
        new TestConfigService(new AppConfig
        {
            ImageGenerationEnabled = true,
            ApiKey = "test-key",
            ImageGenerationModel = "gpt-image-1"
        }),
        new TestAttachmentStoreService(),
        Log.ForContext<OpenAIImageGenerationService>());

    var result = await service.GenerateImageAsync(new ImageGenerationRequest
    {
        Prompt = "continue the scene",
        ReferenceImages =
        [
            new ImageGenerationReferenceImage
            {
                StoredPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".png"),
                FileName = "missing.png",
                MimeType = "image/png"
            }
        ]
    });

    AssertFalse(result.Success, "missing continuity image should fail");
    AssertTrue(result.Message.Contains("not found", StringComparison.OrdinalIgnoreCase), "missing continuity image error should be explicit");
}

static async Task TestArchiveQueueReplayAsync()
{
    using var harness = new TestHarness();
    var snapshot = new ConversationArchiveSnapshot
    {
        CapturedAt = new DateTime(2026, 5, 3, 10, 0, 0, DateTimeKind.Local),
        ForceGenerateSummary = false,
        Messages =
        [
            new ChatMessage
            {
                Role = "user",
                Content = "queued",
                Timestamp = new DateTime(2026, 5, 3, 10, 0, 0, DateTimeKind.Local)
            }
        ]
    };

    var failingHistory = new QueueHistoryService(throwOnUpsert: true);
    var firstArchiveService = new ConversationArchiveService(failingHistory, harness.PathService, Log.ForContext<ConversationArchiveService>());
    var failure = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    firstArchiveService.ArchiveFailed += (_, _) => failure.TrySetResult(true);

    await firstArchiveService.StageArchiveAsync(snapshot);
    await AwaitWithTimeout(failure.Task, "archive failure event");

    var stagedFiles = Directory.GetFiles(harness.PathService.GetPendingArchiveDirectory(), "*.json");
    AssertEqual(1, stagedFiles.Length, "failed archive should stay on disk");

    var succeedingHistory = new QueueHistoryService();
    var secondArchiveService = new ConversationArchiveService(succeedingHistory, harness.PathService, Log.ForContext<ConversationArchiveService>());
    var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    secondArchiveService.ArchiveCompleted += (_, _) => completion.TrySetResult(true);

    await AwaitWithTimeout(completion.Task, "archive replay completion");
    AssertEqual(0, Directory.GetFiles(harness.PathService.GetPendingArchiveDirectory(), "*.json").Length, "replayed archive should be deleted");
    AssertEqual(1, succeedingHistory.UpsertedSnapshots.Count, "replayed archive should be delivered to history service");
}

static Task TestRecurrenceMigrationAndValidationAsync()
{
    var service = new RecurrenceService(new TestLocalizationService());
    var anchor = new DateTime(2026, 5, 14, 9, 0, 0, DateTimeKind.Local);

    var none = service.MigrateLegacyRule("none", anchor);
    var daily = service.MigrateLegacyRule("daily", anchor);
    var weekly = service.MigrateLegacyRule("weekly", anchor);
    var everyThreeDays = service.MigrateLegacyRule("every 3 days", anchor);
    var everyTwoWeeks = service.MigrateLegacyRule("every 2 weeks", anchor);

    AssertEqual(RecurrenceMode.None, none.Mode, "none should migrate to none");
    AssertEqual(RecurrenceMode.Interval, daily.Mode, "daily should migrate to interval");
    AssertEqual(1, daily.Interval, "daily interval should be 1");
    AssertEqual(RecurrenceUnit.Day, daily.Unit, "daily unit should be day");
    AssertEqual(RecurrenceMode.WeeklyDays, weekly.Mode, "weekly should migrate to weekly_days");
    AssertEqual(anchor.DayOfWeek, weekly.DaysOfWeek?.Single() ?? DayOfWeek.Sunday, "weekly should keep the anchor weekday");
    AssertEqual(3, everyThreeDays.Interval, "every 3 days interval should be 3");
    AssertEqual(RecurrenceUnit.Day, everyThreeDays.Unit, "every 3 days unit should be day");
    AssertEqual(2, everyTwoWeeks.Interval, "every 2 weeks interval should be 2");
    AssertEqual(RecurrenceUnit.Week, everyTwoWeeks.Unit, "every 2 weeks unit should be week");

    var invalidInterval = service.Validate(new RecurrenceRule
    {
        Mode = RecurrenceMode.Interval,
        Interval = 0,
        Unit = RecurrenceUnit.Minute
    });
    AssertFalse(invalidInterval.IsValid, "zero interval should be invalid");

    var invalidDays = service.Validate(new RecurrenceRule
    {
        Mode = RecurrenceMode.WeeklyDays,
        Interval = 1,
        DaysOfWeek = []
    });
    AssertFalse(invalidDays.IsValid, "weekly_days without days should be invalid");

    return Task.CompletedTask;
}

static Task TestFirstTriggerCalculationAsync()
{
    var service = new RecurrenceService(new TestLocalizationService());
    var now = new DateTime(2026, 5, 14, 8, 0, 0, DateTimeKind.Local);
    var boundary = new DateTime(2026, 5, 16, 9, 0, 0, DateTimeKind.Local); // Saturday
    var rule = new RecurrenceRule
    {
        Mode = RecurrenceMode.WeeklyDays,
        Interval = 1,
        DaysOfWeek =
        [
            DayOfWeek.Monday,
            DayOfWeek.Tuesday,
            DayOfWeek.Wednesday,
            DayOfWeek.Thursday,
            DayOfWeek.Friday
        ]
    };

    var firstTrigger = service.GetFirstTriggerTime(boundary, rule, now);
    AssertEqual(new DateTime(2026, 5, 18, 9, 0, 0, DateTimeKind.Local), firstTrigger, "workday recurrence should skip to the next matching weekday");
    return Task.CompletedTask;
}

static async Task TestSchedulerForegroundSerialPolicyAsync()
{
    using var harness = new TestHarness();
    var recurrenceService = new RecurrenceService(new TestLocalizationService());
    using var scheduler = new Athena.UI.Services.TaskScheduler(Log.ForContext<Athena.UI.Services.TaskScheduler>(), harness.PathService, recurrenceService);
    scheduler.Start();

    var dispatchedTaskIds = new List<string>();
    scheduler.ProactiveMessageTriggered += (_, e) => dispatchedTaskIds.Add(e.TaskId);

    var now = DateTime.Now;
    var oneTimeTask = new ScheduledTask
    {
        Id = "one-time-a",
        TriggerTime = now.AddMinutes(1),
        ScheduleBoundary = now.AddMinutes(1),
        Intent = "one-time-a",
        RecurrenceRule = RecurrenceRule.None(),
        CreatedAt = now,
        TaskType = TaskType.Proactive
    };
    var recurringTask = new ScheduledTask
    {
        Id = "recurring-b",
        TriggerTime = now.AddMinutes(1),
        ScheduleBoundary = now.AddMinutes(1),
        Intent = "recurring-b",
        RecurrenceRule = new RecurrenceRule
        {
            Mode = RecurrenceMode.Interval,
            Interval = 1,
            Unit = RecurrenceUnit.Minute
        },
        CreatedAt = now,
        TaskType = TaskType.Proactive
    };

    await scheduler.ScheduleAsync(oneTimeTask);
    await scheduler.ScheduleAsync(recurringTask);
    oneTimeTask.TriggerTime = DateTime.Now.AddSeconds(-5);
    recurringTask.TriggerTime = DateTime.Now.AddSeconds(-5);

    await scheduler.RunDueTasksAsync();
    AssertEqual(1, dispatchedTaskIds.Count, "only one foreground task should dispatch at a time");
    AssertEqual("one-time-a", dispatchedTaskIds[0], "one-time task should win when due at the same time");
    AssertTrue(recurringTask.TriggerTime > DateTime.Now, "recurring collision should be skipped to a future time");

    var waitingTask = new ScheduledTask
    {
        Id = "one-time-c",
        TriggerTime = DateTime.Now.AddSeconds(-5),
        ScheduleBoundary = DateTime.Now.AddMinutes(2),
        Intent = "one-time-c",
        RecurrenceRule = RecurrenceRule.None(),
        CreatedAt = now,
        TaskType = TaskType.Proactive
    };
    await scheduler.ScheduleAsync(waitingTask);
    waitingTask.TriggerTime = DateTime.Now.AddSeconds(-5);

    await scheduler.RunDueTasksAsync();
    AssertEqual(1, dispatchedTaskIds.Count, "new one-time task should wait while another foreground task is still running");
    AssertFalse(waitingTask.IsExecuted, "waiting one-time task should stay pending");

    await scheduler.CompleteTaskExecutionAsync(oneTimeTask.Id, TaskExecutionOutcome.Succeeded, "done");
    AssertEqual(2, dispatchedTaskIds.Count, "scheduler should dispatch the waiting one-time task after completion");
    AssertEqual("one-time-c", dispatchedTaskIds[1], "waiting task should run next");

    await scheduler.CompleteTaskExecutionAsync(waitingTask.Id, TaskExecutionOutcome.Succeeded, "done");
    AssertTrue(waitingTask.IsExecuted, "one-time waiting task should complete after execution");
}

static async Task TestSchedulerLongRunningRecurringAsync()
{
    using var harness = new TestHarness();
    var recurrenceService = new RecurrenceService(new TestLocalizationService());
    using var scheduler = new Athena.UI.Services.TaskScheduler(Log.ForContext<Athena.UI.Services.TaskScheduler>(), harness.PathService, recurrenceService);
    scheduler.Start();
    scheduler.ProactiveMessageTriggered += (_, _) => { };

    var boundary = DateTime.Now.AddMinutes(-5);
    var task = new ScheduledTask
    {
        Id = "recurring-long",
        TriggerTime = DateTime.Now.AddMinutes(1),
        ScheduleBoundary = boundary,
        Intent = "recurring-long",
        RecurrenceRule = new RecurrenceRule
        {
            Mode = RecurrenceMode.Interval,
            Interval = 1,
            Unit = RecurrenceUnit.Minute
        },
        CreatedAt = DateTime.Now,
        TaskType = TaskType.Proactive
    };

    await scheduler.ScheduleAsync(task);
    task.TriggerTime = DateTime.Now.AddSeconds(-5);
    await scheduler.RunDueTasksAsync();
    await scheduler.CompleteTaskExecutionAsync(task.Id, TaskExecutionOutcome.Succeeded, "completed after long run");

    AssertTrue(task.TriggerTime > DateTime.Now, "recurring task should advance to a future slot after completion");
    AssertTrue(task.TriggerTime < DateTime.Now.AddMinutes(2), "recurring task should not replay every missed interval");
}

static async Task TestCreateTaskStructuredResponsesAsync()
{
    using var harness = new TestHarness();
    var recurrenceService = new RecurrenceService(new TestLocalizationService());
    using var scheduler = new Athena.UI.Services.TaskScheduler(Log.ForContext<Athena.UI.Services.TaskScheduler>(), harness.PathService, recurrenceService);
    var functions = new ProactiveMessagingFunctions(scheduler, recurrenceService, Log.ForContext<ProactiveMessagingFunctions>());

    var success = await functions.ScheduleProactiveMessage(
        DateTime.Now.AddHours(2).ToString("yyyy-MM-dd HH:mm"),
        "check progress",
        new RecurrenceRuleInput
        {
            Mode = "interval",
            Interval = 1,
            Unit = "day"
        });

    AssertTrue(success.Success, "valid create_task request should succeed");
    var successData = JsonSerializer.SerializeToElement(success.Data);
    AssertTrue(successData.GetProperty("validation").GetProperty("isValid").GetBoolean(), "success result should include valid validation data");
    AssertTrue(successData.TryGetProperty("normalizedRecurrence", out _), "success result should include normalized recurrence");

    var failure = await functions.ScheduleProactiveMessage(
        DateTime.Now.AddHours(2).ToString("yyyy-MM-dd HH:mm"),
        "broken rule",
        new RecurrenceRuleInput
        {
            Mode = "weekly_days",
            Interval = 1,
            DaysOfWeek = []
        });

    AssertFalse(failure.Success, "invalid create_task request should fail");
    var failureData = JsonSerializer.SerializeToElement(failure.Data);
    AssertFalse(failureData.GetProperty("validation").GetProperty("isValid").GetBoolean(), "failure result should include invalid validation data");
    AssertTrue(failureData.GetProperty("validation").GetProperty("issues").GetArrayLength() > 0, "failure result should expose structured validation issues");
}

static List<string> DiffLines(string s) => s.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n').ToList();
static string DiffJoin(List<string> lines) => string.Join("\n", lines);

static Task TestDiffExactApplyAsync()
{
    var lines = DiffLines("int a = 1;\nint b = 2;\nint c = 3;");
    var parse = DiffApplier.Parse("<<<<<<< SEARCH\nint b = 2;\n=======\nint b = 20;\n>>>>>>> REPLACE");
    var result = DiffApplier.Apply(lines, parse.Blocks, true, false);
    AssertTrue(result.Success, "exact match should apply");
    AssertEqual(DiffMatchTier.Exact, result.MatchTier, "exact match should report Exact tier");
    AssertEqual("int a = 1;\nint b = 20;\nint c = 3;", DiffJoin(lines), "only the target line should change");
    return Task.CompletedTask;
}

static Task TestDiffTrailingWhitespaceAsync()
{
    var lines = DiffLines("foo   \nbar\nbaz");
    var parse = DiffApplier.Parse("<<<<<<< SEARCH\nfoo\nbar\n=======\nFOO\nBAR\n>>>>>>> REPLACE");
    var result = DiffApplier.Apply(lines, parse.Blocks, true, false);
    AssertTrue(result.Success, "trailing whitespace should be tolerated");
    AssertEqual(DiffMatchTier.TrailingWhitespace, result.MatchTier, "match should be at trailing-whitespace tier");
    AssertEqual("FOO\nBAR\nbaz", DiffJoin(lines), "replacement should land despite trailing spaces");
    return Task.CompletedTask;
}

static Task TestDiffReindentAsync()
{
    var lines = DiffLines("class C {\n        void M() {\n            X();\n        }\n}");
    var diff = "<<<<<<< SEARCH\n    void M() {\n        X();\n    }\n=======\n    void M() {\n        Y();\n        Z();\n    }\n>>>>>>> REPLACE";
    var parse = DiffApplier.Parse(diff);
    var result = DiffApplier.Apply(lines, parse.Blocks, true, false);
    AssertTrue(result.Success, "indentation drift should be tolerated");
    AssertEqual(DiffMatchTier.Trimmed, result.MatchTier, "match should be at trimmed tier");
    AssertEqual("class C {\n        void M() {\n            Y();\n            Z();\n        }\n}", DiffJoin(lines), "replacement should be reindented to the file's indentation");
    return Task.CompletedTask;
}

static Task TestDiffAmbiguousAsync()
{
    var lines = DiffLines("x();\ny();\nx();");
    var before = DiffJoin(lines);
    var parse = DiffApplier.Parse("<<<<<<< SEARCH\nx();\n=======\nz();\n>>>>>>> REPLACE");
    var result = DiffApplier.Apply(lines, parse.Blocks, true, false);
    AssertFalse(result.Success, "ambiguous SEARCH should fail");
    AssertEqual(2, result.MultipleMatches?.Count ?? 0, "both candidate locations should be reported");
    AssertEqual(before, DiffJoin(lines), "an ambiguous failure must not mutate the file");
    return Task.CompletedTask;
}

static Task TestDiffReplaceAllAsync()
{
    var lines = DiffLines("x();\ny();\nx();");
    var parse = DiffApplier.Parse("<<<<<<< SEARCH\nx();\n=======\nz();\n>>>>>>> REPLACE");
    var result = DiffApplier.Apply(lines, parse.Blocks, true, true);
    AssertTrue(result.Success, "replaceAll should succeed despite multiple matches");
    AssertEqual("z();\ny();\nz();", DiffJoin(lines), "every occurrence should be replaced");
    return Task.CompletedTask;
}

static Task TestDiffEmptySearchAsync()
{
    var parse = DiffApplier.Parse("<<<<<<< SEARCH\n=======\nnew\n>>>>>>> REPLACE");
    AssertTrue(parse.Error != null, "empty SEARCH should be a parse error");
    AssertEqual(0, parse.Blocks.Count, "no blocks should be produced from an empty SEARCH");
    return Task.CompletedTask;
}

static Task TestDiffNearestHintAsync()
{
    var lines = DiffLines("alpha\nbeta\ngamma\ndelta");
    var parse = DiffApplier.Parse("<<<<<<< SEARCH\nbeta\nGAMMA_TYPO\n=======\nX\nY\n>>>>>>> REPLACE");
    var result = DiffApplier.Apply(lines, parse.Blocks, true, false);
    AssertFalse(result.Success, "a typo'd SEARCH should not match");
    AssertTrue(result.NearestHint != null && result.NearestHint.Contains("第 2 行"), "failure should point to the nearest window");
    return Task.CompletedTask;
}

static Task TestDiffMultiBlockAtomicAsync()
{
    var lines = DiffLines("a\nb\nc");
    var before = DiffJoin(lines);
    var diff = "<<<<<<< SEARCH\na\n=======\nA\n>>>>>>> REPLACE\n<<<<<<< SEARCH\nNOPE\n=======\nX\n>>>>>>> REPLACE";
    var parse = DiffApplier.Parse(diff);
    var result = DiffApplier.Apply(lines, parse.Blocks, true, false);
    AssertFalse(result.Success, "a failing second block should fail the whole edit");
    AssertEqual(2, result.FailedBlockIndex ?? 0, "the failing block index should be reported");
    AssertEqual(before, DiffJoin(lines), "a partial multi-block edit must roll back");
    return Task.CompletedTask;
}

static Task TestDiffStrictModeAsync()
{
    var lines = DiffLines("foo   \nbar");
    var parse = DiffApplier.Parse("<<<<<<< SEARCH\nfoo\n=======\nFOO\n>>>>>>> REPLACE");
    var result = DiffApplier.Apply(lines, parse.Blocks, false, false);
    AssertFalse(result.Success, "strict mode should reject whitespace drift");
    return Task.CompletedTask;
}

static async Task AwaitWithTimeout(Task task, string operation)
{
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    var completed = await Task.WhenAny(task, Task.Delay(Timeout.InfiniteTimeSpan, cts.Token));
    if (completed != task)
    {
        throw new TimeoutException($"Timed out waiting for {operation}.");
    }

    await task;
}

static void AssertTrue(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

static void AssertFalse(bool condition, string message)
{
    if (condition)
    {
        throw new InvalidOperationException(message);
    }
}

static void AssertEqual<T>(T expected, T actual, string message)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException($"{message}. Expected: {expected}; Actual: {actual}");
    }
}

static void AssertJsonPropertyEquals(string expected, object? data, string propertyName, string message)
{
    if (data == null)
    {
        throw new InvalidOperationException($"{message}. Data was null.");
    }

    using var document = JsonDocument.Parse(JsonSerializer.Serialize(data));
    if (!document.RootElement.TryGetProperty(propertyName, out var property))
    {
        throw new InvalidOperationException($"{message}. Property '{propertyName}' was missing.");
    }

    AssertEqual(expected, property.GetString(), message);
}

static ChatAttachment CreateImageAttachment(string id, string fileName, string storedPath, DateTime createdAt)
{
    return new ChatAttachment
    {
        Id = id,
        Kind = AttachmentKind.Image,
        FileName = fileName,
        StoredPath = storedPath,
        MimeType = "image/png",
        CreatedAt = createdAt
    };
}

sealed class TestHarness : IDisposable
{
    public TestHarness()
    {
        Root = Path.Combine(Path.GetTempPath(), "athena-archive-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        PathService = new TestPlatformPathService(Root);
    }

    public string Root { get; }

    public TestPlatformPathService PathService { get; }

    public ConversationHistoryService CreateHistoryService()
    {
        return new ConversationHistoryService(new TestPromptService(), PathService, new TestLocalizationService(), new ImageGenerationSessionService(PathService, Log.ForContext<ImageGenerationSessionService>()));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
        catch
        {
        }
    }
}

sealed class TestPromptService : IPromptService
{
    public event EventHandler<PromptType>? PromptUpdated;

    public string GetPrompt(PromptType type)
    {
        return type switch
        {
            PromptType.SummaryGeneration => "Summarize.",
            PromptType.SummaryInstruction => "Summarize in one sentence.",
            PromptType.ContextCompression => "Compress.",
            PromptType.ContextCompressionStrategy => "Compress strategy.",
            _ => "Prompt"
        };
    }

    public string GetProactiveMessagePrompt(string intent, DateTime currentTime) => $"{intent} @ {currentTime:O}";

    public Task ReloadAsync() => Task.CompletedTask;
}

sealed class TestLocalizationService : ILocalizationService
{
    public string CurrentLanguage => "en-US";

    public IReadOnlyList<string> AvailableLanguages => ["en-US"];

    public IReadOnlyList<string> AvailableLanguageNames => ["English"];

    public event PropertyChangedEventHandler? PropertyChanged;

    public event EventHandler? LanguageChanged;

    public int GetLanguageIndex(string languageCode) => 0;

    public void SwitchLanguage(string languageCode)
    {
    }

    public string GetString(string key) => key;

    public string GetString(string key, string defaultValue) => defaultValue;
}

sealed class TestPlatformPathService(string root) : IPlatformPathService
{
    public string GetAppDataDirectory() => root;

    public string GetConfigFilePath() => Path.Combine(root, "config.json");

    public string GetLogDirectory() => Path.Combine(root, "logs");

    public string GetKnowledgeBaseDirectory() => Path.Combine(root, "knowledge");

    public string GetHistoryDirectory()
    {
        var path = Path.Combine(root, "history");
        Directory.CreateDirectory(path);
        return path;
    }

    public string GetPendingArchiveDirectory()
    {
        var path = Path.Combine(root, "pending");
        Directory.CreateDirectory(path);
        return path;
    }

    public string GetAttachmentDirectory()
    {
        var path = Path.Combine(root, "attachments");
        Directory.CreateDirectory(path);
        return path;
    }

    public string GetImageGenerationSessionDirectory()
    {
        var path = Path.Combine(root, "image-sessions");
        Directory.CreateDirectory(path);
        return path;
    }

    public string GetTaskSchedulerFilePath() => Path.Combine(root, "tasks.json");

    public string GetVectorStoreFilePath() => Path.Combine(root, "vectors.db");
}

sealed class QueueHistoryService(bool throwOnUpsert = false) : IConversationHistoryService
{
    public List<ConversationArchiveSnapshot> UpsertedSnapshots { get; } = new();

    public Task<List<ConversationHistoryItem>> LoadAllAsync() => Task.FromResult(new List<ConversationHistoryItem>());

    public Task SaveAsync(ConversationHistoryItem item) => Task.CompletedTask;

    public Task DeleteAsync(string id) => Task.CompletedTask;

    public Task<string> GenerateSummaryAsync(List<ChatMessage> messages) => Task.FromResult("summary");

    public Task<ConversationHistoryItem?> LoadByIdAsync(string id) => Task.FromResult<ConversationHistoryItem?>(null);

    public Task DeleteImageSessionAsync(string? conversationId) => Task.CompletedTask;

    public void SaveDraft(ConversationDraftSnapshot snapshot)
    {
    }

    public ConversationDraftSnapshot? LoadDraft() => null;

    public void DeleteDraft()
    {
    }

    public void UpdateSecondaryConfig(AppConfig config)
    {
    }

    public Task<(string? Summary, int CompressedCount)> CompressContextAsync(List<ChatMessage> messages, int keepRecentRounds = 3)
    {
        return Task.FromResult<(string?, int)>((null, 0));
    }

    public Task<(bool Success, string Message)> TestSecondaryConnectionAsync()
    {
        return Task.FromResult((true, "ok"));
    }

    public Task<ConversationHistoryItem> UpsertFromSnapshotAsync(ConversationArchiveSnapshot snapshot, CancellationToken ct = default)
    {
        if (throwOnUpsert)
        {
            throw new InvalidOperationException("simulated failure");
        }

        UpsertedSnapshots.Add(snapshot);
        return Task.FromResult(new ConversationHistoryItem
        {
            ConversationId = snapshot.ConversationId,
            Id = snapshot.HistoryId ?? Guid.NewGuid().ToString("N"),
            CreatedAt = snapshot.CapturedAt,
            UpdatedAt = snapshot.CapturedAt,
            Summary = "queued",
            MessageCount = snapshot.Messages.Count,
            Messages = snapshot.Messages.ToList()
        });
    }
}

sealed class TestConfigService(AppConfig config) : IConfigService
{
    public Task<AppConfig> LoadAsync() => Task.FromResult(config);

    public AppConfig Load() => config;

    public Task SaveAsync(AppConfig config)
    {
        throw new NotSupportedException();
    }

    public string ConfigFilePath => string.Empty;

    public event EventHandler<AppConfig>? ConfigChanged;
}

sealed class TestAttachmentStoreService : IAttachmentStoreService
{
    public int MaxPendingAttachments => 10;

    public long MaxImageBytes => 20 * 1024 * 1024;

    public long MaxDocumentBytes => 200L * 1024 * 1024;

    public long MaxTextBytes => 5L * 1024 * 1024;

    public IReadOnlyCollection<string> SupportedDocumentExtensions => System.Array.Empty<string>();

    public IReadOnlyCollection<string> SupportedTextExtensions => System.Array.Empty<string>();

    public Task<IReadOnlyList<ChatAttachment>> ImportFilesAsync(IEnumerable<Avalonia.Platform.Storage.IStorageFile> files, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }

    public Task<ChatAttachment> ImportBitmapAsync(Avalonia.Media.Imaging.Bitmap bitmap, string fileName, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }

    public Task<ChatAttachment> CreateGeneratedImageAsync(byte[] bytes, string fileName, string mimeType, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }

    public Task<ChatAttachment> CreateGeneratedAudioAsync(byte[] bytes, string fileName, string mimeType, TimeSpan? duration = null, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }

    public Task<string> WriteParsedSidecarAsync(ChatAttachment attachment, string markdown, CancellationToken cancellationToken = default) => Task.FromResult(string.Empty);

    public Task LoadPreviewAsync(ChatAttachment attachment, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task LoadPreviewsAsync(IEnumerable<ChatAttachment> attachments, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public void DeleteStoredAttachment(ChatAttachment attachment)
    {
    }
}

sealed class TestConversationSessionAccessor(string conversationId) : IConversationSessionAccessor
{
    public string? CurrentConversationId => conversationId;

    public IDisposable Enter(string nextConversationId) => throw new NotSupportedException();
}

sealed class StubImageGenerationService : IImageGenerationService
{
    public bool IsConfigured { get; set; } = true;

    public ImageGenerationRequest? LastRequest { get; private set; }

    public required ImageGenerationResult NextResult { get; init; }

    public Task<ImageGenerationResult> GenerateImageAsync(ImageGenerationRequest request, CancellationToken cancellationToken = default)
    {
        LastRequest = request;
        return Task.FromResult(NextResult);
    }
}

sealed class TempFile : IDisposable
{
    public TempFile(string extension, byte[] bytes)
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"{Guid.NewGuid():N}{extension}");
        File.WriteAllBytes(Path, bytes);
    }

    public string Path { get; }

    public void Dispose()
    {
        try
        {
            if (File.Exists(Path))
            {
                File.Delete(Path);
            }
        }
        catch
        {
        }
    }
}
