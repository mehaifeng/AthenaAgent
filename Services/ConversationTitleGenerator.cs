using Athena.UI.Models;
using Athena.UI.Services.Interfaces;
using OpenAI.Chat;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ModelChatMessage = Athena.UI.Models.ChatMessage;

namespace Athena.UI.Services;

public sealed class ConversationTitleGenerator : IConversationTitleGenerator
{
    internal const int ContextMaxChars = 1000;
    internal const int ContextMaxMessages = 10;
    internal const int ContextMinMessageChars = 100;
    internal const int TitleMaxChars = 20;

    private readonly OpenAiModelRuntimeFactory _modelFactory;
    private readonly IPromptService _promptService;
    private readonly ILocalizationService? _localizationService;
    private readonly ILogger _logger;

    public ConversationTitleGenerator(
        OpenAiModelRuntimeFactory modelFactory,
        IPromptService promptService,
        ILogger logger,
        ILocalizationService? localizationService = null)
    {
        _modelFactory = modelFactory;
        _promptService = promptService;
        _localizationService = localizationService;
        _logger = logger.ForContext<ConversationTitleGenerator>();
    }

    public async Task<string> GenerateAsync(
        IReadOnlyList<ModelChatMessage> messages,
        bool useAi,
        CancellationToken cancellationToken = default)
    {
        if (messages.Count == 0) return GetString("History.EmptyConversation", "Empty conversation");

        if (useAi)
        {
            try
            {
                var effective = _modelFactory.Resolve(AiModelRole.TitleGeneration);
                var client = _modelFactory.CreateChatClient(AiModelRole.TitleGeneration);
                var context = BuildContext(messages);
                if (context.Count > 0)
                {
                    var openAiMessages = new List<OpenAI.Chat.ChatMessage>
                    {
                        new SystemChatMessage(_promptService.GetPrompt(PromptType.SummaryGeneration))
                    };
                    foreach (var entry in context)
                    {
                        openAiMessages.Add(entry.Role == "user"
                            ? new UserChatMessage(entry.Content)
                            : new AssistantChatMessage(entry.Content));
                    }
                    openAiMessages.Add(new UserChatMessage(_promptService.GetPrompt(PromptType.SummaryInstruction)));
                    var completion = await client.CompleteChatAsync(
                        openAiMessages,
                        new ChatCompletionOptions
                        {
                            Temperature = (float)effective.Temperature,
                            MaxOutputTokenCount = effective.MaxOutputTokens
                        },
                        cancellationToken);
                    var title = completion.Value.Content.FirstOrDefault()?.Text?.Trim().Trim('"', '\'', ' ', '。', '.');
                    if (!string.IsNullOrWhiteSpace(title)) return Truncate(title, TitleMaxChars);
                }
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "标题模型失败，使用首条用户消息兜底");
            }
        }

        var first = messages.Where(message => message.Role?.Equals("user", StringComparison.OrdinalIgnoreCase) == true)
            .Select(FormatMessage)
            .FirstOrDefault(content => !string.IsNullOrWhiteSpace(content));
        if (string.IsNullOrWhiteSpace(first)) return GetString("History.NewConversation", "New conversation");
        return first.Length <= TitleMaxChars ? first : Truncate(first, TitleMaxChars - 1) + "…";
    }

    internal static List<TitleContextEntry> BuildContext(IReadOnlyList<ModelChatMessage> messages)
    {
        var entries = new List<TitleContextEntry>();
        for (var i = messages.Count - 1; i >= 0 && entries.Count < ContextMaxMessages; i--)
        {
            var message = messages[i];
            var role = message.Role?.ToLowerInvariant();
            if (role == "user")
            {
                var content = FormatMessage(message);
                if (!string.IsNullOrWhiteSpace(content)) entries.Add(new("user", content.Trim()));
            }
            else if (role is "assistant" or "ai"
                     && string.IsNullOrEmpty(message.ToolCallsJson)
                     && !string.IsNullOrWhiteSpace(message.Content))
            {
                entries.Add(new("assistant", message.Content.Trim()));
            }
        }
        entries.Reverse();
        while (entries.Sum(entry => entry.Content.Length) > ContextMaxChars)
        {
            var longestIndex = entries.Select((entry, index) => (entry, index))
                .OrderByDescending(pair => pair.entry.Content.Length).First().index;
            var longest = entries[longestIndex];
            if (longest.Content.Length <= ContextMinMessageChars) break;
            var overshoot = entries.Sum(entry => entry.Content.Length) - ContextMaxChars;
            entries[longestIndex] = longest with
            {
                Content = Truncate(longest.Content, Math.Max(ContextMinMessageChars, longest.Content.Length - overshoot))
            };
        }
        return entries;
    }

    internal static string Truncate(string text, int maxChars)
    {
        if (text.Length <= maxChars) return text;
        var cut = maxChars;
        if (cut > 0 && char.IsHighSurrogate(text[cut - 1])) cut--;
        return text[..cut];
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

    internal sealed record TitleContextEntry(string Role, string Content);
}
