using Athena.UI.Models;
using Athena.UI.Services.Interfaces;
using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace Athena.UI.Services;

/// <summary>对话归档 JSON 的单一持久化模块；不生成标题、不压缩上下文。</summary>
public sealed class ConversationArchiveStore : IConversationArchiveStore, IConversationDraftStore
{
    private const string DraftFileName = "_chat_draft.json";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _historyDirectory;
    private readonly string _draftFilePath;
    private readonly ILogger _logger;

    public ConversationArchiveStore(IPlatformPathService platformPathService, ILogger logger)
    {
        _historyDirectory = platformPathService.GetHistoryDirectory();
        Directory.CreateDirectory(_historyDirectory);
        _draftFilePath = Path.Combine(_historyDirectory, DraftFileName);
        _logger = logger.ForContext<ConversationArchiveStore>();
    }

    public async Task<List<ConversationHistoryItem>> LoadAllAsync()
    {
        var items = new List<ConversationHistoryItem>();
        foreach (var file in Directory.GetFiles(_historyDirectory, "*.json")
                     .Where(file => !string.Equals(Path.GetFileName(file), DraftFileName, StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                var item = JsonSerializer.Deserialize<ConversationHistoryItem>(await File.ReadAllTextAsync(file), JsonOptions);
                if (item == null) continue;
                if (item.Messages.Count > 0) item.MessageCount = item.Messages.Count(IsCountableMessage);
                items.Add(item);
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "加载历史文件失败: {File}", file);
            }
        }
        return items.OrderByDescending(item => item.UpdatedAt).ToList();
    }

    public async Task<ConversationHistoryItem?> LoadByIdAsync(string id)
    {
        try
        {
            var path = Path.Combine(_historyDirectory, $"{ValidateId(id)}.json");
            return File.Exists(path)
                ? JsonSerializer.Deserialize<ConversationHistoryItem>(await File.ReadAllTextAsync(path), JsonOptions)
                : null;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "加载对话历史失败: {Id}", id);
            return null;
        }
    }

    public async Task SaveAsync(ConversationHistoryItem item)
    {
        var path = Path.Combine(_historyDirectory, $"{ValidateId(item.Id)}.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(item, JsonOptions));
    }

    public Task DeleteAsync(string id)
    {
        var path = Path.Combine(_historyDirectory, $"{ValidateId(id)}.json");
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }

    public void Save(ConversationDraftSnapshot snapshot)
        => File.WriteAllText(_draftFilePath, JsonSerializer.Serialize(snapshot, JsonOptions));

    public ConversationDraftSnapshot? Load()
    {
        try
        {
            return File.Exists(_draftFilePath)
                ? JsonSerializer.Deserialize<ConversationDraftSnapshot>(File.ReadAllText(_draftFilePath), JsonOptions)
                : null;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "加载主对话草稿失败");
            return null;
        }
    }

    public void Delete()
    {
        if (File.Exists(_draftFilePath)) File.Delete(_draftFilePath);
    }

    public static bool IsCountableMessage(ChatMessage message)
        => message.Role == "user"
           || (message.Role == "assistant" && string.IsNullOrEmpty(message.ToolCallsJson));

    private static string ValidateId(string id)
    {
        if (!Guid.TryParse(id, out var parsed))
            throw new ArgumentException("Conversation history ID must be a GUID.", nameof(id));
        return parsed.ToString();
    }
}
