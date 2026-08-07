using Athena.UI.Models;
using Athena.UI.Services.Context;
using Athena.UI.Services.Interfaces;
using OpenAI.Chat;
using OpenAI.Responses;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ModelChatMessage = Athena.UI.Models.ChatMessage;
// OpenAI SDK Experimental 面（OPENAI001）：本文件直接使用 Responses 类型。
#pragma warning disable OPENAI001

namespace Athena.UI.Services;

/// <summary>滚动压缩主对话上下文。失败或取消时不修改会话。</summary>
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
        cancellationToken.ThrowIfCancellationRequested();
        var active = messages.Where(message => !message.IsCompressed).ToList();
        var userIndices = active.Select((message, index) => (message, index))
            .Where(pair => pair.message.Role?.Equals("user", StringComparison.OrdinalIgnoreCase) == true)
            .Select(pair => pair.index).ToList();
        if (userIndices.Count <= keepRecentRounds) return CompressionResult.None;

        var splitIndex = userIndices[userIndices.Count - keepRecentRounds];
        if (splitIndex <= 0) return CompressionResult.None;
        var olderMessages = active.Take(splitIndex).ToList();
        string? summary = null;

        try
        {
            var effective = _modelFactory.Resolve(AiModelRole.ContextCompression);
            var prompt = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(existingSummary))
            {
                prompt.AppendLine("[Previous running summary]:");
                prompt.AppendLine(StripSummaryPrefix(existingSummary));
                prompt.AppendLine();
            }
            prompt.AppendLine(_promptService.GetPrompt(PromptType.ContextCompressionStrategy));
            prompt.AppendLine();
            prompt.Append(BuildCompressionMaterial(olderMessages));

            var systemPrompt = _promptService.GetPrompt(PromptType.ContextCompression);
            if (ResponsesCallHelpers.ShouldUseResponses(effective))
            {
                var responses = ResponsesCallHelpers.CreateResponsesClient(effective, _modelFactory.TimeoutSeconds);
                var options = ResponsesCallHelpers.CreateOptions(effective, systemPrompt, (float)effective.Temperature, effective.MaxOutputTokens);
                options.InputItems.Add(ResponseItem.CreateUserMessageItem(prompt.ToString()));
                var result = await responses.CreateResponseAsync(options, cancellationToken);
                summary = ResponsesCallHelpers.GetFirstOutputText(result.Value)?.Trim();
            }
            else
            {
                var client = _modelFactory.CreateChatClient(AiModelRole.ContextCompression);
                var completion = await client.CompleteChatAsync(
                    [
                        new SystemChatMessage(systemPrompt),
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
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Context compression model failed; conversation remains unchanged");
            return CompressionResult.None;
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(summary))
        {
            _logger.Warning("Context compression model returned an empty summary; conversation remains unchanged");
            return CompressionResult.None;
        }

        foreach (var message in olderMessages) message.IsCompressed = true;
        var summaryFormat = GetString("History.SummaryPrefix", "[Summary]: {0}");
        return new CompressionResult
        {
            Summary = string.Format(summaryFormat, summary),
            CompressedCount = olderMessages.Count,
            CompressedMessages = olderMessages,
            UsedFallback = false
        };
    }

    internal static string BuildCompressionMaterial(IReadOnlyList<ModelChatMessage> messages)
    {
        var builder = new StringBuilder();
        foreach (var message in messages)
        {
            builder.Append("[role=").Append(message.Role);
            if (!string.IsNullOrWhiteSpace(message.ToolCallId))
            {
                builder.Append(" tool_call_id=").Append(message.ToolCallId);
            }
            builder.AppendLine("]");
            builder.AppendLine(FormatMessage(message));
            builder.AppendLine();
        }
        return builder.ToString();
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
        if (!string.IsNullOrWhiteSpace(message.Content)) parts.Add("content:\n" + message.Content);
        if (!string.IsNullOrWhiteSpace(message.ReasoningContent))
            parts.Add("reasoning_conclusions:\n" + message.ReasoningContent);
        if (!string.IsNullOrWhiteSpace(message.ToolCallsJson))
            parts.Add("assistant_tool_calls_json:\n" + message.ToolCallsJson);
        if (message.Attachments.Count > 0)
            parts.Add("attachments:\n" + string.Join("\n", message.Attachments.Select(attachment =>
                $"- id={attachment.Id}; kind={attachment.Kind}; file={attachment.FileName}; stored_path={attachment.StoredPath}; mime={attachment.MimeType}; size={attachment.SizeBytes}; dimensions={attachment.Width}x{attachment.Height}")));
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
            var maxOutput = Math.Min(
                Math.Clamp(tokenBudget, 32, 8192),
                effective.MaxOutputTokens > 0 ? effective.MaxOutputTokens : 8192);
            const string systemPrompt = "Compress this workspace knowledge file. Preserve facts, commands, paths, decisions, constraints, and code identifiers. Return Markdown only and invent nothing.";
            if (ResponsesCallHelpers.ShouldUseResponses(effective))
            {
                var responses = ResponsesCallHelpers.CreateResponsesClient(effective, _modelFactory.TimeoutSeconds);
                var options = ResponsesCallHelpers.CreateOptions(effective, systemPrompt, (float)effective.Temperature, maxOutput);
                options.InputItems.Add(ResponseItem.CreateUserMessageItem(content));
                var result = await responses.CreateResponseAsync(options, cancellationToken);
                return ResponsesCallHelpers.GetFirstOutputText(result.Value)?.Trim();
            }

            var client = _modelFactory.CreateChatClient(AiModelRole.ContextCompression);
            var chatOptions = new ChatCompletionOptions
            {
                Temperature = (float)effective.Temperature,
                MaxOutputTokenCount = maxOutput
            };
            var response = await client.CompleteChatAsync(
                [
                    new SystemChatMessage(systemPrompt),
                    new UserChatMessage(content)
                ],
                chatOptions,
                cancellationToken);
            return response.Value.Content.FirstOrDefault()?.Text?.Trim();
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Workspace knowledge compression failed");
            return null;
        }
    }
}
