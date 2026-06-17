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

        var provider = string.IsNullOrWhiteSpace(_secondaryConfig.SecondaryProvider) || _secondaryConfig.SecondaryProvider == "Inherit"
            ? _secondaryConfig.Provider
            : _secondaryConfig.SecondaryProvider;

        var apiKey = string.IsNullOrWhiteSpace(_secondaryConfig.SecondaryApiKey)
            ? _secondaryConfig.ApiKey
            : _secondaryConfig.SecondaryApiKey;

        var baseUrl = string.IsNullOrWhiteSpace(_secondaryConfig.SecondaryBaseUrl)
            ? _secondaryConfig.BaseUrl
            : _secondaryConfig.SecondaryBaseUrl;

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
                // 截取最后 50 轮对话（约 100 条消息）作为上下文
                var contextMessages = messages.Skip(Math.Max(0, messages.Count - 100)).ToList();
                
                var openAiMessages = new List<OpenAI.Chat.ChatMessage>();
                
                // 添加系统提示词
                openAiMessages.Add(new SystemChatMessage(_promptService.GetPrompt(PromptType.SummaryGeneration)));

                // 将历史消息转换为 OpenAI 消息格式，过滤掉工具相关的隐形消息以节省总结时的 token
                foreach (var msg in contextMessages)
                {
                    // 仅保留纯文本对话进行总结，忽略 tool 角色和带工具调用的 assistant 消息
                    if (msg.Role?.ToLower() == "user")
                    {
                        var summaryContent = FormatMessageForSummary(msg);
                        if (!string.IsNullOrWhiteSpace(summaryContent))
                        {
                            openAiMessages.Add(new UserChatMessage(summaryContent));
                        }
                    }
                    else if ((msg.Role?.ToLower() == "assistant" || msg.Role?.ToLower() == "ai") 
                             && string.IsNullOrEmpty(msg.ToolCallsJson) 
                             && !string.IsNullOrWhiteSpace(msg.Content))
                    {
                        openAiMessages.Add(CreateAssistantHistoryMessage(msg.Content, msg.ReasoningContent));
                    }
                }

                // 添加总结引导词
                openAiMessages.Add(new UserChatMessage(_promptService.GetPrompt(PromptType.SummaryInstruction)));

                var completion = await _secondaryChatClient.CompleteChatAsync(openAiMessages);
                var summary = completion?.Value?.Content?.FirstOrDefault()?.Text?.Trim();

                if (!string.IsNullOrEmpty(summary))
                {
                    // 清理可能出现的首尾标点或引号
                    summary = summary.Trim('\"', '\'', ' ', '。', '.');
                    return summary;
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "使用次级模型生成摘要失败，使用默认方式");
            }
        }

        // 默认方式：取第一条用户消息的前 30 个字符
        var firstUserMessage = messages
            .Where(m => m.Role?.ToLower() == "user")
            .Select(FormatMessageForSummary)
            .FirstOrDefault(content => !string.IsNullOrWhiteSpace(content));
        if (string.IsNullOrEmpty(firstUserMessage))
        {
            return GetString("History.NewConversation", "New conversation");
        }
        
        if (firstUserMessage.Length <= 30)
        {
            return firstUserMessage;
        }
        return firstUserMessage.Substring(0, 30) + "...";
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

    public async Task<(string? Summary, int CompressedCount)> CompressContextAsync(List<Models.ChatMessage> messages, int keepRecentRounds = 3)
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
            return (null, 0);
        }

        // 3. 确定切分点：我们要保留最后 keepRecentRounds 个轮次
        // splitIndex 指向倒数第 keepRecentRounds 个 user 消息的索引
        int splitIndex = userMessageIndices[userMessageIndices.Count - keepRecentRounds];

        // 安全性检查：如果 splitIndex 为 0，说明第一条就是我们要保留的起点，无法压缩
        if (splitIndex <= 0)
        {
            Log.Debug("切分点位于首条消息，无法执行压缩");
            return (null, 0);
        }

        // 现在 activeMessages[0...splitIndex-1] 是要被压缩的旧消息块（包含完整的历史轮次）
        var olderMessages = activeMessages.Take(splitIndex).ToList();

        // 如果次级模型不可用，直接跳过压缩
        if (_secondaryChatClient == null)
        {
            Log.Warning("次级模型不可用，跳过上下文压缩");
            return (null, 0);
        }

        try
        {
            // 构建摘要请求：排除 tool 消息，仅保留 user 和 assistant 的纯文本
            var summaryPrompt = _promptService.GetPrompt(PromptType.ContextCompressionStrategy) + "\n\n" +
                string.Join("\n", olderMessages
                    .Where(m => m.Role == "user" || (m.Role == "assistant" && string.IsNullOrEmpty(m.ToolCallsJson)))
                    .Select(m => $"[{m.Role}]: {FormatMessageForSummary(m)}"));

            var openAiMessages = new List<OpenAI.Chat.ChatMessage>
            {
                new SystemChatMessage(_promptService.GetPrompt(PromptType.ContextCompression)),
                new UserChatMessage(summaryPrompt)
            };

            var completion = await _secondaryChatClient.CompleteChatAsync(openAiMessages);
            var summary = completion.Value.Content[0].Text?.Trim();

            // 5. 标记旧消息块为已压缩（包含其中的 tool 消息，整块移除）
            foreach (var msg in olderMessages)
            {
                msg.IsCompressed = true;
            }

            Log.Information("上下文压缩完成，从 {Old} 条消息（约 {Rounds} 轮）压缩为摘要。活跃部分从索引 {Index} 开始", 
                olderMessages.Count, userMessageIndices.Count - keepRecentRounds, splitIndex);
            
            var summaryPrefix = GetString("History.SummaryPrefix", "[Summary]: {0}");
            var formattedSummary = !string.IsNullOrEmpty(summary) ? string.Format(summaryPrefix, summary) : null;
            return (formattedSummary, olderMessages.Count);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "压缩上下文失败");
            return (null, 0);
        }
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
