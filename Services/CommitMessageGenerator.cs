using Athena.UI.Models;
using Athena.UI.Services.Context;
using Athena.UI.Services.Interfaces;
using OpenAI.Chat;
using Serilog;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Athena.UI.Services;

/// <summary>基于已暂存 diff 生成 Conventional Commits 风格提交信息。使用主对话角色模型。</summary>
public sealed class CommitMessageGenerator : ICommitMessageGenerator
{
    private const float Temperature = 0.3f;
    private const int MaxOutputTokens = 200;

    private readonly OpenAiModelRuntimeFactory _modelFactory;
    private readonly IPromptService _promptService;
    private readonly ILogger _logger;

    public CommitMessageGenerator(
        OpenAiModelRuntimeFactory modelFactory,
        IPromptService promptService,
        ILogger logger)
    {
        _modelFactory = modelFactory;
        _promptService = promptService;
        _logger = logger.ForContext<CommitMessageGenerator>();
    }

    public async Task<string?> GenerateAsync(
        string? branchName,
        string diffStat,
        string diffContent,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(diffStat) && string.IsNullOrWhiteSpace(diffContent))
        {
            return null;
        }

        try
        {
            var effective = _modelFactory.Resolve(AiModelRole.MainConversation);
            var messages = new OpenAI.Chat.ChatMessage[]
            {
                new SystemChatMessage(_promptService.GetPrompt(PromptType.CommitMessage)),
                new UserChatMessage(BuildContext(branchName, diffStat, diffContent))
            };

            string? text;
            if (ResponsesCallHelpers.ShouldUseResponses(effective))
            {
                var responses = ResponsesCallHelpers.CreateResponsesClient(effective, _modelFactory.TimeoutSeconds);
                var options = ResponsesCallHelpers.CreateOptions(effective, _promptService.GetPrompt(PromptType.CommitMessage), Temperature, MaxOutputTokens);
                ResponsesCallHelpers.AddInputItems(options, messages.Skip(1));
                var result = await responses.CreateResponseAsync(options, cancellationToken);
                text = ResponsesCallHelpers.GetFirstOutputText(result.Value);
            }
            else
            {
                var client = _modelFactory.CreateChatClient(AiModelRole.MainConversation);
                var completion = await client.CompleteChatAsync(
                    messages,
                    new ChatCompletionOptions
                    {
                        Temperature = Temperature,
                        MaxOutputTokenCount = MaxOutputTokens
                    },
                    cancellationToken);
                text = completion.Value.Content.FirstOrDefault()?.Text;
            }

            if (string.IsNullOrWhiteSpace(text)) return null;

            var message = text.Trim().Trim('"', '\'', '“', '”');
            return string.IsNullOrWhiteSpace(message) ? null : message;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to generate commit message");
            return null;
        }
    }

    internal static string BuildContext(string? branchName, string diffStat, string diffContent)
    {
        var builder = new System.Text.StringBuilder();
        if (!string.IsNullOrWhiteSpace(branchName))
        {
            builder.Append("当前分支：").Append(branchName).Append('\n');
        }
        builder.Append("以下是已暂存的更改（diff --cached）：\n");
        builder.Append("--stat--\n").Append(diffStat.Trim()).Append('\n');
        builder.Append("--diff--\n").Append(diffContent.Trim()).Append('\n');
        builder.Append("请据此生成提交信息。");
        return builder.ToString();
    }
}
