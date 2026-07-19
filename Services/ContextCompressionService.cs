using Athena.UI.Models;
using Athena.UI.Services.Interfaces;
using OpenAI.Chat;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ModelChatMessage = Athena.UI.Models.ChatMessage;

namespace Athena.UI.Services;

/// <summary>滚动压缩主对话上下文。模型失败时仍用本地抽取式摘要保证上下文收缩。</summary>
public sealed class ContextCompressionService : IContextCompressionService
{
    private readonly OpenAiModelRuntimeFactory _modelFactory;
    private readonly IPromptService _promptService;
    private readonly ILocalizationService? _localizationService;
    private readonly ILogger _logger;

    public ContextCompressionService(
        OpenAiModelRuntimeFactory modelFactory,
        IPromptService promptService,
        ILogger logger,
        ILocalizationService? localizationService = null)
    {
        _modelFactory = modelFactory;
        _promptService = promptService;
        _localizationService = localizationService;
        _logger = logger.ForContext<ContextCompressionService>();
    }

    public async Task<CompressionResult> CompressAsync(
        IReadOnlyList<ModelChatMessage> messages,
        string? existingSummary,
        int keepRecentRounds = 3,
        CancellationToken cancellationToken = default)
    {
        var active = messages.Where(message => !message.IsCompressed).ToList();
        var userIndices = active.Select((message, index) => (message, index))
            .Where(pair => pair.message.Role?.Equals("user", StringComparison.OrdinalIgnoreCase) == true)
            .Select(pair => pair.index).ToList();
        if (userIndices.Count <= keepRecentRounds) return CompressionResult.None;

        var splitIndex = userIndices[userIndices.Count - keepRecentRounds];
        if (splitIndex <= 0) return CompressionResult.None;
        var olderMessages = active.Take(splitIndex).ToList();
        string? summary = null;
        var usedFallback = false;

        try
        {
            var effective = _modelFactory.Resolve(AiModelRole.ContextCompression);
            var client = _modelFactory.CreateChatClient(AiModelRole.ContextCompression);
            var prompt = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(existingSummary))
            {
                prompt.AppendLine("[Previous running summary]:");
                prompt.AppendLine(StripSummaryPrefix(existingSummary));
                prompt.AppendLine();
            }
            prompt.AppendLine(_promptService.GetPrompt(PromptType.ContextCompressionStrategy));
            prompt.AppendLine();
            foreach (var message in olderMessages.Where(message =>
                         message.Role == "user"
                         || (message.Role == "assistant" && string.IsNullOrEmpty(message.ToolCallsJson))))
            {
                prompt.AppendLine($"[{message.Role}]: {FormatMessage(message)}");
            }

            var completion = await client.CompleteChatAsync(
                [
                    new SystemChatMessage(_promptService.GetPrompt(PromptType.ContextCompression)),
                    new UserChatMessage(prompt.ToString())
                ],
                new ChatCompletionOptions
                {
                    Temperature = (float)effective.Temperature,
                    MaxOutputTokenCount = effective.MaxOutputTokens
                },
                cancellationToken);
            summary = completion.Value.Content.FirstOrDefault()?.Text?.Trim();
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "上下文压缩模型失败，转本地兜底");
        }

        if (string.IsNullOrWhiteSpace(summary))
        {
            summary = BuildExtractiveFallback(existingSummary, olderMessages);
            usedFallback = true;
        }

        foreach (var message in olderMessages) message.IsCompressed = true;
        var summaryFormat = GetString("History.SummaryPrefix", "[Summary]: {0}");
        return new CompressionResult
        {
            Summary = string.Format(summaryFormat, summary),
            CompressedCount = olderMessages.Count,
            CompressedMessages = olderMessages,
            UsedFallback = usedFallback
        };
    }

    private string BuildExtractiveFallback(string? existingSummary, IReadOnlyList<ModelChatMessage> messages, int maxChars = 1500)
    {
        var builder = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(existingSummary))
        {
            builder.AppendLine(StripSummaryPrefix(existingSummary));
        }
        foreach (var message in messages.Where(message => message.Role == "user"))
        {
            var text = FormatMessage(message).Replace('\n', ' ').Trim();
            if (string.IsNullOrWhiteSpace(text)) continue;
            if (text.Length > 200) text = text[..200] + "…";
            builder.Append("• ").AppendLine(text);
            if (builder.Length >= maxChars) break;
        }
        var result = builder.ToString().Trim();
        if (result.Length > maxChars) result = result[..maxChars] + "…";
        return string.IsNullOrEmpty(result) ? GetString("History.NewConversation", "New conversation") : result;
    }

    private string StripSummaryPrefix(string summary)
    {
        var format = GetString("History.SummaryPrefix", "[Summary]: {0}");
        var placeholder = format.IndexOf("{0}", StringComparison.Ordinal);
        if (placeholder <= 0) return summary;
        var prefix = format[..placeholder];
        return summary.StartsWith(prefix, StringComparison.Ordinal)
            ? summary[prefix.Length..].TrimStart()
            : summary;
    }

    private static string FormatMessage(ModelChatMessage message)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(message.Content)) parts.Add(message.Content);
        if (message.Attachments.Count > 0)
            parts.Add(string.Join(" ", message.Attachments.Select(attachment => $"[{attachment.DisplayKind}: {attachment.FileName}]")));
        return string.Join("\n", parts);
    }

    private string GetString(string key, string fallback)
        => _localizationService?.GetString(key, fallback) ?? fallback;
}

public sealed class WorkspaceKnowledgeCompressor : IWorkspaceKnowledgeCompressor
{
    private readonly OpenAiModelRuntimeFactory _modelFactory;
    private readonly ILogger _logger;

    public WorkspaceKnowledgeCompressor(OpenAiModelRuntimeFactory modelFactory, ILogger logger)
    {
        _modelFactory = modelFactory;
        _logger = logger.ForContext<WorkspaceKnowledgeCompressor>();
    }

    public async Task<string?> CompressAsync(string content, int tokenBudget, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(content) || tokenBudget <= 0) return null;
        try
        {
            var effective = _modelFactory.Resolve(AiModelRole.ContextCompression);
            var client = _modelFactory.CreateChatClient(AiModelRole.ContextCompression);
            var options = new ChatCompletionOptions
            {
                Temperature = (float)effective.Temperature,
                MaxOutputTokenCount = Math.Min(
                    Math.Clamp(tokenBudget, 32, 8192),
                    effective.MaxOutputTokens > 0 ? effective.MaxOutputTokens : 8192)
            };
            var response = await client.CompleteChatAsync(
                [
                    new SystemChatMessage("Compress this workspace knowledge file. Preserve facts, commands, paths, decisions, constraints, and code identifiers. Return Markdown only and invent nothing."),
                    new UserChatMessage(content)
                ],
                options,
                cancellationToken);
            return response.Value.Content.FirstOrDefault()?.Text?.Trim();
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "工作区知识压缩失败");
            return null;
        }
    }
}
