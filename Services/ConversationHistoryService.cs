using Athena.UI.Models;
using Athena.UI.Services.Interfaces;
using OpenAI;
using OpenAI.Chat;
using Serilog;
using System;
using System.ClientModel;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Athena.UI.Services;

/// <summary>
/// 对话历史服务实现
/// </summary>
public class ConversationHistoryService : IConversationHistoryService
{
    private const string DraftFileName = "_chat_draft.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _historyDirectory;
    private readonly string _draftFilePath;
    private readonly IPromptService _promptService;
    private readonly ILocalizationService? _localizationService;
    private readonly IImageGenerationSessionService? _imageGenerationSessionService;
    private AppConfig? _secondaryConfig;
    private OpenAIClient? _secondaryClient;
    private ChatClient? _secondaryChatClient;

    public ConversationHistoryService(
        IPromptService promptService,
        IPlatformPathService? platformPathService = null,
        ILocalizationService? localizationService = null,
        IImageGenerationSessionService? imageGenerationSessionService = null)
    {
        _promptService = promptService;
        _localizationService = localizationService;
        _imageGenerationSessionService = imageGenerationSessionService;

        if (platformPathService != null)
        {
            _historyDirectory = platformPathService.GetHistoryDirectory();
        }
        else
        {
            // 兼容旧的调用方式
            _historyDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Athena",
                "history"
            );
        }
        Directory.CreateDirectory(_historyDirectory);
        _draftFilePath = Path.Combine(_historyDirectory, DraftFileName);
        Log.Information("对话历史服务初始化，存储目录: {Dir}", _historyDirectory);
    }

    private string GetString(string key, string defaultValue)
    {
        return _localizationService?.GetString(key, defaultValue) ?? defaultValue;
    }

    public void UpdateSecondaryConfig(Models.AppConfig config)
    {
        _secondaryConfig = config;
        InitializeSecondaryClient();
    }

    private void InitializeSecondaryClient()
    {
        if (_secondaryConfig == null)
        {
            _secondaryClient = null;
            _secondaryChatClient = null;
            return;
        }

        // 统一继承树：凭据解析集中在 ModelCredentialResolver（遗留 provider="Inherit" 已在 ConfigService 加载时迁移）。
        var effective = ModelCredentialResolver.Resolve(
            _secondaryConfig.SecondaryCredentialSource, _secondaryConfig,
            _secondaryConfig.SecondaryProvider, _secondaryConfig.SecondaryBaseUrl, _secondaryConfig.SecondaryApiKey,
            _secondaryConfig.SecondaryModel);
        var provider = effective.Provider;
        var apiKey = effective.ApiKey;
        var baseUrl = effective.BaseUrl;

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            _secondaryClient = null;
            _secondaryChatClient = null;
            Log.Warning("次级模型 API Key 为空，客户端未初始化");
            return;
        }

        try
        {
            var options = new OpenAIClientOptions();
            if (!string.IsNullOrWhiteSpace(baseUrl))
            {
                options.Endpoint = new Uri(baseUrl);
            }

            _secondaryClient = new OpenAIClient(new ApiKeyCredential(apiKey), options);
            _secondaryChatClient = _secondaryClient.GetChatClient(_secondaryConfig.SecondaryModel);
            Log.Information("次级模型客户端初始化成功，提供商: {Provider}, 模型: {Model}", provider, _secondaryConfig.SecondaryModel);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "次级模型客户端初始化失败");
            _secondaryClient = null;
            _secondaryChatClient = null;
        }
    }

    /// <summary>
    /// 判断某条消息是否计入“消息数”：仅 user 与真正的 assistant 回复，
    /// 排除 system / tool 消息，以及仅携带工具调用的中间 assistant 消息。
    /// </summary>
    private static bool IsCountableMessage(Models.ChatMessage m)
    {
        if (m.Role == "user")
        {
            return true;
        }

        return m.Role == "assistant" && string.IsNullOrEmpty(m.ToolCallsJson);
    }

    public async Task<List<Models.ConversationHistoryItem>> LoadAllAsync()
    {
        var items = new List<Models.ConversationHistoryItem>();

        try
        {
            var files = Directory
                .GetFiles(_historyDirectory, "*.json")
                .Where(file => !string.Equals(Path.GetFileName(file), DraftFileName, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            foreach (var file in files)
            {
                try
                {
                    var json = await File.ReadAllTextAsync(file);
                    var item = JsonSerializer.Deserialize<Models.ConversationHistoryItem>(json, JsonOptions);
                    if (item != null)
                    {
                        // 仅统计 user / assistant 消息（排除工具调用中间消息；旧记录可能包含 system/tool 计数，这里重新校正）
                        if (item.Messages is { Count: > 0 })
                        {
                            item.MessageCount = item.Messages.Count(IsCountableMessage);
                        }
                        items.Add(item);
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "加载历史文件失败: {File}", file);
                }
            }

            // 按更新时间倒序排列
            items = items.OrderByDescending(i => i.UpdatedAt).ToList();
            Log.Information("加载了 {Count} 条对话历史", items.Count);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "加载对话历史列表失败");
        }

        return items;
    }

    public async Task SaveAsync(Models.ConversationHistoryItem item)
    {
        try
        {
            var filePath = Path.Combine(_historyDirectory, $"{item.Id}.json");
            var json = JsonSerializer.Serialize(item, JsonOptions);
            await File.WriteAllTextAsync(filePath, json);
            Log.Information("保存对话历史: {Id} - {Summary}", item.Id, item.Summary);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "保存对话历史失败: {Id}", item.Id);
            throw;
        }
    }

    public Task DeleteAsync(string id)
    {
        try
        {
            var filePath = Path.Combine(_historyDirectory, $"{id}.json");
            if (File.Exists(filePath))
            {
                try
                {
                    var json = File.ReadAllText(filePath);
                    var item = JsonSerializer.Deserialize<Models.ConversationHistoryItem>(json, JsonOptions);
                    DeleteImageSessionAsync(item?.ConversationId).GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "删除历史时读取图像会话关联失败: {Id}", id);
                }

                File.Delete(filePath);
                Log.Information("删除对话历史: {Id}", id);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "删除对话历史失败: {Id}", id);
        }

        return Task.CompletedTask;
    }

    public async Task DeleteImageSessionAsync(string? conversationId)
    {
        if (_imageGenerationSessionService == null || string.IsNullOrWhiteSpace(conversationId))
        {
            return;
        }

        await _imageGenerationSessionService.DeleteAsync(conversationId);
    }

    public async Task<ConversationHistoryItem> UpsertFromSnapshotAsync(ConversationArchiveSnapshot snapshot, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var messageList = ConversationPersistenceHelper.CloneMessages(snapshot.Messages);
        var summary = await GenerateSummaryAsync(messageList, snapshot.ForceGenerateSummary);
        var historyId = string.IsNullOrWhiteSpace(snapshot.HistoryId)
            ? Guid.NewGuid().ToString()
            : snapshot.HistoryId!;

        var existingItem = await LoadByIdAsync(historyId);
        var createdAt = existingItem?.CreatedAt
            ?? messageList.FirstOrDefault()?.Timestamp
            ?? snapshot.CapturedAt;

        var item = new ConversationHistoryItem
        {
            ConversationId = string.IsNullOrWhiteSpace(snapshot.ConversationId)
                ? (existingItem?.ConversationId ?? Guid.NewGuid().ToString("N"))
                : snapshot.ConversationId,
            Id = historyId,
            Summary = summary,
            ContextSummary = snapshot.ContextSummary,
            ForkedFromConversationId = snapshot.ForkedFromConversationId ?? existingItem?.ForkedFromConversationId,
            ForkedFromHistoryId = snapshot.ForkedFromHistoryId ?? existingItem?.ForkedFromHistoryId,
            ForkedAtMessageId = snapshot.ForkedAtMessageId ?? existingItem?.ForkedAtMessageId,
            MessageCount = messageList.Count(IsCountableMessage),
            Messages = messageList,
            CreatedAt = createdAt,
            UpdatedAt = snapshot.CapturedAt
        };

        await SaveAsync(item);
        if (snapshot.ImageSession != null && _imageGenerationSessionService != null)
        {
            var imageSessionSnapshot = new ImageGenerationSessionSnapshot
            {
                ConversationId = item.ConversationId,
                HistoryId = item.Id,
                ActiveLineageId = snapshot.ImageSession.ActiveLineageId,
                CreatedAt = snapshot.ImageSession.CreatedAt,
                UpdatedAt = snapshot.ImageSession.UpdatedAt,
                Turns = snapshot.ImageSession.Turns.Select(CloneTurn).ToList()
            };

            await _imageGenerationSessionService.PersistSnapshotAsync(imageSessionSnapshot, ct);
        }

        return item;
    }

    public Task<string> GenerateSummaryAsync(List<Models.ChatMessage> messages)
    {
        return GenerateSummaryAsync(messages, false);
    }

    private async Task<string> GenerateSummaryAsync(List<Models.ChatMessage> messages, bool useAi)
    {
        if (messages == null || messages.Count == 0)
        {
            return GetString("History.EmptyConversation", "Empty conversation");
        }

        // 如果需要使用 AI 生成摘要
        if (useAi && _secondaryChatClient != null)
        {
            try
            {
                // 上下文预算：最近 10 条非工具消息、总量 ≤1000 字符（超预算时对最长消息硬截断）
                var contextEntries = BuildSummaryContext(messages);
                if (contextEntries.Count > 0)
                {
                    var openAiMessages = new List<OpenAI.Chat.ChatMessage>
                    {
                        new SystemChatMessage(_promptService.GetPrompt(PromptType.SummaryGeneration))
                    };

                    foreach (var entry in contextEntries)
                    {
                        if (entry.Role == "user")
                        {
                            openAiMessages.Add(new UserChatMessage(entry.Content));
                        }
                        else
                        {
                            openAiMessages.Add(CreateAssistantHistoryMessage(entry.Content, null));
                        }
                    }

                    // 添加总结引导词
                    openAiMessages.Add(new UserChatMessage(_promptService.GetPrompt(PromptType.SummaryInstruction)));

                    var completion = await _secondaryChatClient.CompleteChatAsync(openAiMessages);
                    var summary = completion?.Value?.Content?.FirstOrDefault()?.Text?.Trim();

                    if (!string.IsNullOrEmpty(summary))
                    {
                        // 清理可能出现的首尾标点或引号，并强制标题 ≤20 字
                        summary = summary.Trim('\"', '\'', ' ', '。', '.');
                        return TruncateAtChar(summary, SummaryTitleMaxChars);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "使用次级模型生成摘要失败，使用默认方式");
            }
        }

        // 默认方式：取第一条用户消息，硬截断到标题上限
        var firstUserMessage = messages
            .Where(m => m.Role?.ToLower() == "user")
            .Select(FormatMessageForSummary)
            .FirstOrDefault(content => !string.IsNullOrWhiteSpace(content));
        if (string.IsNullOrEmpty(firstUserMessage))
        {
            return GetString("History.NewConversation", "New conversation");
        }

        if (firstUserMessage.Length <= SummaryTitleMaxChars)
        {
            return firstUserMessage;
        }
        return TruncateAtChar(firstUserMessage, SummaryTitleMaxChars - 1) + "…";
    }

    // ===== 摘要上下文预算（标题生成用） =====
    internal const int SummaryContextMaxChars = 1000;
    internal const int SummaryContextMaxMessages = 10;
    internal const int SummaryContextMinMessageChars = 100;
    internal const int SummaryTitleMaxChars = 20;

    internal sealed record SummaryContextEntry(string Role, string Content);

    /// <summary>
    /// 取最近 10 条非工具消息（不足按实际条数），总字符预算 1000：
    /// 超预算时反复对当前最长的一条做硬截断（保留开头），单条下限 100 字符；
    /// 收敛极限为 10 条 × 100 字符。
    /// </summary>
    internal static List<SummaryContextEntry> BuildSummaryContext(List<Models.ChatMessage> messages)
    {
        var entries = new List<SummaryContextEntry>();
        for (int i = messages.Count - 1; i >= 0 && entries.Count < SummaryContextMaxMessages; i--)
        {
            var msg = messages[i];
            var role = msg.Role?.ToLowerInvariant();
            if (role == "user")
            {
                var content = FormatMessageForSummary(msg);
                if (!string.IsNullOrWhiteSpace(content))
                {
                    entries.Add(new SummaryContextEntry("user", content.Trim()));
                }
            }
            else if ((role == "assistant" || role == "ai")
                && string.IsNullOrEmpty(msg.ToolCallsJson)
                && !string.IsNullOrWhiteSpace(msg.Content))
            {
                entries.Add(new SummaryContextEntry("assistant", msg.Content.Trim()));
            }
        }

        entries.Reverse();

        while (entries.Sum(e => e.Content.Length) > SummaryContextMaxChars)
        {
            int longestIndex = 0;
            for (int i = 1; i < entries.Count; i++)
            {
                if (entries[i].Content.Length > entries[longestIndex].Content.Length)
                {
                    longestIndex = i;
                }
            }

            var longest = entries[longestIndex];
            if (longest.Content.Length <= SummaryContextMinMessageChars)
            {
                break;
            }

            var overshoot = entries.Sum(e => e.Content.Length) - SummaryContextMaxChars;
            var targetLength = Math.Max(SummaryContextMinMessageChars, longest.Content.Length - overshoot);
            entries[longestIndex] = longest with { Content = TruncateAtChar(longest.Content, targetLength) };
        }

        return entries;
    }

    /// <summary>按字符数硬截断，避免拆散代理对（emoji 等）。</summary>
    internal static string TruncateAtChar(string text, int maxChars)
    {
        if (text.Length <= maxChars)
        {
            return text;
        }

        var cut = maxChars;
        if (cut > 0 && char.IsHighSurrogate(text[cut - 1]))
        {
            cut--;
        }

        return text[..cut];
    }

    public async Task<Models.ConversationHistoryItem?> LoadByIdAsync(string id)
    {
        try
        {
            var filePath = Path.Combine(_historyDirectory, $"{id}.json");
            if (File.Exists(filePath))
            {
                var json = await File.ReadAllTextAsync(filePath);
                return JsonSerializer.Deserialize<Models.ConversationHistoryItem>(json, JsonOptions);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "加载对话历史失败: {Id}", id);
        }

        return null;
    }

    public void SaveDraft(ConversationDraftSnapshot snapshot)
    {
        try
        {
            var json = JsonSerializer.Serialize(snapshot, JsonOptions);
            File.WriteAllText(_draftFilePath, json);
            Log.Information("保存主对话草稿，消息数: {Count}", snapshot.Messages.Count);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "保存主对话草稿失败");
            throw;
        }
    }

    public ConversationDraftSnapshot? LoadDraft()
    {
        try
        {
            if (!File.Exists(_draftFilePath))
            {
                return null;
            }

            var json = File.ReadAllText(_draftFilePath);
            return JsonSerializer.Deserialize<ConversationDraftSnapshot>(json, JsonOptions);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "加载主对话草稿失败");
            return null;
        }
    }

    public void DeleteDraft()
    {
        try
        {
            if (!File.Exists(_draftFilePath))
            {
                return;
            }

            File.Delete(_draftFilePath);
            Log.Information("删除主对话草稿");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "删除主对话草稿失败");
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

    public async Task<CompressionResult> CompressContextAsync(List<Models.ChatMessage> messages, string? existingSummary, int keepRecentRounds = 3)
    {
        // 1. 获取当前所有未压缩的活跃消息
        var activeMessages = messages.Where(m => !m.IsCompressed).ToList();

        // 2. 统计当前活跃消息包含的轮次（以 user 消息作为每一轮的开始）
        var userMessageIndices = activeMessages
            .Select((m, i) => new { Role = m.Role?.ToLower(), Index = i })
            .Where(x => x.Role == "user")
            .Select(x => x.Index)
            .ToList();

        // 如果活跃轮次不足，则不压缩
        if (userMessageIndices.Count <= keepRecentRounds)
        {
            Log.Debug("活跃轮次不足 ({Current} <= {Keep})，跳过压缩", userMessageIndices.Count, keepRecentRounds);
            return CompressionResult.None;
        }

        // 3. 确定切分点：我们要保留最后 keepRecentRounds 个轮次
        // splitIndex 指向倒数第 keepRecentRounds 个 user 消息的索引
        int splitIndex = userMessageIndices[userMessageIndices.Count - keepRecentRounds];

        // 安全性检查：如果 splitIndex 为 0，说明第一条就是我们要保留的起点，无法压缩
        if (splitIndex <= 0)
        {
            Log.Debug("切分点位于首条消息，无法执行压缩");
            return CompressionResult.None;
        }

        // 现在 activeMessages[0...splitIndex-1] 是要被压缩的旧消息块（包含完整的历史轮次）
        var olderMessages = activeMessages.Take(splitIndex).ToList();

        // 4. 优先用次级模型生成"旧摘要 ⊕ 旧消息"的滚动合并摘要
        string? summary = null;
        bool usedFallback = false;

        if (_secondaryChatClient != null)
        {
            try
            {
                var summaryPrompt = new System.Text.StringBuilder();
                // 关键：把已有摘要作为既定事实喂回，避免多次压缩丢失更早历史
                if (!string.IsNullOrWhiteSpace(existingSummary))
                {
                    summaryPrompt.AppendLine("[Previous running summary]:");
                    summaryPrompt.AppendLine(StripSummaryPrefix(existingSummary));
                    summaryPrompt.AppendLine();
                }
                summaryPrompt.AppendLine(_promptService.GetPrompt(PromptType.ContextCompressionStrategy));
                summaryPrompt.AppendLine();
                foreach (var m in olderMessages
                    .Where(m => m.Role == "user" || (m.Role == "assistant" && string.IsNullOrEmpty(m.ToolCallsJson))))
                {
                    summaryPrompt.AppendLine($"[{m.Role}]: {FormatMessageForSummary(m)}");
                }

                var openAiMessages = new List<OpenAI.Chat.ChatMessage>
                {
                    new SystemChatMessage(_promptService.GetPrompt(PromptType.ContextCompression)),
                    new UserChatMessage(summaryPrompt.ToString())
                };

                var completion = await _secondaryChatClient.CompleteChatAsync(openAiMessages);
                summary = completion.Value.Content[0].Text?.Trim();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "AI 压缩失败，转本地兜底裁剪");
            }
        }
        else
        {
            Log.Warning("次级模型不可用，转本地兜底裁剪");
        }

        // 5. 兜底：AI 不可用 / 失败时用本地抽取式摘要，确保 token 一定下降而非无界增长
        if (string.IsNullOrEmpty(summary))
        {
            summary = BuildExtractiveFallback(existingSummary, olderMessages);
            usedFallback = true;
        }

        // 6. 标记旧消息块为已压缩（AI 与兜底路径都执行，保证上下文一定收缩）
        foreach (var msg in olderMessages)
        {
            msg.IsCompressed = true;
        }

        Log.Information("上下文压缩完成({Mode})，从 {Old} 条消息（约 {Rounds} 轮）压缩为摘要。活跃部分从索引 {Index} 开始",
            usedFallback ? "本地兜底" : "AI", olderMessages.Count, userMessageIndices.Count - keepRecentRounds, splitIndex);

        var summaryPrefix = GetString("History.SummaryPrefix", "[Summary]: {0}");
        var formattedSummary = !string.IsNullOrEmpty(summary) ? string.Format(summaryPrefix, summary) : null;
        return new CompressionResult
        {
            Summary = formattedSummary,
            CompressedCount = olderMessages.Count,
            CompressedMessages = olderMessages,
            UsedFallback = usedFallback
        };
    }

    /// <summary>
    /// 去掉摘要文本上的 <c>[Summary]:</c> 前缀，避免把已有摘要喂回压缩时产生嵌套前缀。
    /// </summary>
    private string StripSummaryPrefix(string summary)
    {
        var fmt = GetString("History.SummaryPrefix", "[Summary]: {0}");
        int ph = fmt.IndexOf("{0}", StringComparison.Ordinal);
        if (ph > 0)
        {
            var prefix = fmt.Substring(0, ph);
            if (summary.StartsWith(prefix, StringComparison.Ordinal))
            {
                return summary.Substring(prefix.Length).TrimStart();
            }
        }
        return summary;
    }

    /// <summary>
    /// 本地抽取式兜底摘要：合并已有摘要 + 旧消息中各 user 提问的截断片段，整体限长。
    /// 用于次级模型不可用或调用失败时，至少保留主线脉络而不让上下文无界膨胀。
    /// </summary>
    private string BuildExtractiveFallback(string? existingSummary, List<Models.ChatMessage> olderMessages, int maxChars = 1500)
    {
        var sb = new System.Text.StringBuilder();
        if (!string.IsNullOrWhiteSpace(existingSummary))
        {
            sb.AppendLine(StripSummaryPrefix(existingSummary));
        }

        foreach (var m in olderMessages.Where(m => m.Role?.ToLower() == "user"))
        {
            var text = FormatMessageForSummary(m);
            if (string.IsNullOrWhiteSpace(text)) continue;
            text = text.Replace('\n', ' ').Trim();
            if (text.Length > 200) text = text.Substring(0, 200) + "…";
            sb.Append("• ").AppendLine(text);
            if (sb.Length >= maxChars) break;
        }

        var result = sb.ToString().Trim();
        if (result.Length > maxChars) result = result.Substring(0, maxChars) + "…";
        return string.IsNullOrEmpty(result)
            ? GetString("History.NewConversation", "New conversation")
            : result;
    }

    public async Task<(bool Success, string Message)> TestSecondaryConnectionAsync()
    {
        if (_secondaryChatClient == null)
        {
            return (false, GetString("History.ConfigureApiKeyFirst", "Please configure API Key and model first"));
        }

        try
        {
            var messages = new List<OpenAI.Chat.ChatMessage>
            {
                new SystemChatMessage("Reply with 'OK' only."),
                new UserChatMessage("test")
            };

            var options = new ChatCompletionOptions { MaxOutputTokenCount = 10 };
            var response = await _secondaryChatClient.CompleteChatAsync(messages, options);

            if (response?.Value?.Content == null || response.Value.Content.Count == 0)
            {
                return (false, GetString("History.ConnectionSuccessNoResponse", "Connection successful but no valid response"));
            }

            return (true, GetString("History.ConnectionSuccess", "Connection successful"));
        }
        catch (Exception ex)
        {
            var failedTemplate = GetString("History.ConnectionFailed", "Connection failed: {0}");
            return (false, string.Format(failedTemplate, ex.Message));
        }
    }

    private static AssistantChatMessage CreateAssistantHistoryMessage(string content, string? reasoningContent)
    {
        var message = new AssistantChatMessage(content);
        if (reasoningContent == null)
        {
            return message;
        }

#pragma warning disable SCME0001
        message.Patch.Set("$.reasoning_content"u8, reasoningContent);
#pragma warning restore SCME0001
        return message;
    }

    private static string FormatMessageForSummary(Models.ChatMessage message)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(message.Content))
        {
            parts.Add(message.Content);
        }

        if (message.Attachments.Count > 0)
        {
            parts.Add(string.Join(" ", message.Attachments.Select(a => $"[{a.DisplayKind}: {a.FileName}]")));
        }

        return string.Join("\n", parts);
    }
}
