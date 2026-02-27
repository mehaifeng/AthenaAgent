using Athena.UI.Models;
using Athena.UI.Services.Interfaces;
using OpenAI;
using OpenAI.Chat;
using Serilog;
using System;
using System.ClientModel;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace Athena.UI.Services;

/// <summary>
/// 对话历史服务实现
/// </summary>
public class ConversationHistoryService : IConversationHistoryService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _historyDirectory;
    private readonly IChatService? _chatService;
    private readonly IPromptService _promptService;
    private AppConfig? _secondaryConfig;
    private OpenAIClient? _secondaryClient;
    private ChatClient? _secondaryChatClient;

    public ConversationHistoryService(IChatService? chatService, IPromptService promptService, IPlatformPathService? platformPathService = null)
    {
        _chatService = chatService;
        _promptService = promptService;

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
        Log.Information("对话历史服务初始化，存储目录: {Dir}", _historyDirectory);
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
            Log.Information("次级模型客户端初始化成功，模型: {Model}", _secondaryConfig.SecondaryModel);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "次级模型客户端初始化失败");
            _secondaryClient = null;
            _secondaryChatClient = null;
        }
    }

    public async Task<List<Models.ConversationHistoryItem>> LoadAllAsync()
    {
        var items = new List<Models.ConversationHistoryItem>();

        try
        {
            var files = Directory.GetFiles(_historyDirectory, "*.json");
            foreach (var file in files)
            {
                try
                {
                    var json = await File.ReadAllTextAsync(file);
                    var item = JsonSerializer.Deserialize<Models.ConversationHistoryItem>(json, JsonOptions);
                    if (item != null)
                    {
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

    public async Task<Models.ConversationHistoryItem> CreateFromMessagesAsync(ObservableCollection<Models.ChatMessage> messages, bool forceGenerateSummary = false)
    {
        var messageList = messages.ToList();

        // 如果不需要强制生成摘要，直接使用现有的摘要逻辑（可能会使用简单的截取方式）
        var summary = await GenerateSummaryAsync(messageList, forceGenerateSummary);

        var item = new Models.ConversationHistoryItem
        {
            Summary = summary,
            MessageCount = messageList.Count,
            Messages = messageList,
            CreatedAt = messageList.FirstOrDefault()?.Timestamp ?? DateTime.Now,
            UpdatedAt = DateTime.Now
        };

        return item;
    }

    public Task<string> GenerateSummaryAsync(List<Models.ChatMessage> messages)
    {
        return GenerateSummaryAsync(messages, false);
    }

    private async Task<string> GenerateSummaryAsync(List<Models.ChatMessage> messages, bool useAi)
    {
        // 过滤出用户消息
        var userMessages = messages.Where(m => m.Role == "user").ToList();

        if (userMessages.Count == 0)
        {
            return "空对话";
        }

        // 如果需要使用 AI 生成摘要
        if (useAi && _secondaryChatClient != null)
        {
            try
            {
                var firstUserMessage = userMessages.First().Content;
                var prompt = $"请用一句话（不超过30个字）概括这个对话的主题：\n\n{firstUserMessage}";

                var openAiMessages = new List<OpenAI.Chat.ChatMessage>
                {
                    new SystemChatMessage(_promptService.GetPrompt(PromptType.SummaryGeneration)),
                    new UserChatMessage(prompt)
                };

                var completion = await _secondaryChatClient.CompleteChatAsync(openAiMessages);
                var summary = completion.Value.Content[0].Text?.Trim();

                if (!string.IsNullOrEmpty(summary) && summary.Length <= 50)
                {
                    return summary;
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "使用次级模型生成摘要失败，使用默认方式");
            }
        }

        // 默认方式：取第一条用户消息的前 30 个字符
        var content = userMessages.First().Content;
        if (content.Length <= 30)
        {
            return content;
        }
        return content.Substring(0, 30) + "...";
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

    public async Task<string?> CompressContextAsync(List<Models.ChatMessage> messages, int keepRecentCount = 10)
    {
        if (messages.Count <= keepRecentCount)
        {
            Log.Debug("上下文消息数不足，无需压缩");
            return null;
        }

        var olderMessages = messages.Take(messages.Count - keepRecentCount).ToList();

        // 如果次级模型不可用，返回简单的截取摘要
        if (_secondaryChatClient == null)
        {
            Log.Warning("次级模型不可用，使用简单截取作为摘要");
            var simpleSummary = string.Join("\n", olderMessages.Select(m => $"[{m.Role}]: {m.Content}"));
            return $"[对话摘要]: {simpleSummary.Substring(0, Math.Min(500, simpleSummary.Length))}...";
        }

        try
        {
            // 构建摘要请求
            var summaryPrompt = @"请将以下对话历史压缩为简洁的摘要，保留关键信息和重要细节：

" + string.Join("\n", olderMessages.Select(m => $"[{m.Role}]: {m.Content}"));

            var openAiMessages = new List<OpenAI.Chat.ChatMessage>
            {
                new SystemChatMessage(_promptService.GetPrompt(PromptType.ContextCompression)
                    ?? "你是一个对话摘要助手。请将对话历史压缩为简洁的摘要，保留关键信息。"),
                new UserChatMessage(summaryPrompt)
            };

            var completion = await _secondaryChatClient.CompleteChatAsync(openAiMessages);
            var summary = completion.Value.Content[0].Text?.Trim();

            Log.Information("上下文压缩完成，从 {Old} 条消息压缩为摘要", olderMessages.Count);
            return !string.IsNullOrEmpty(summary) ? $"[对话摘要]: {summary}" : null;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "压缩上下文失败");
            return null;
        }
    }
}
