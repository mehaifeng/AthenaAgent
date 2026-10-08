using Athena.UI.Models;
using Athena.UI.Services.Context;
using Athena.UI.Services.Interfaces;
using OpenAI.Chat;
using OpenAI.Responses;
using Serilog;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
// OpenAI SDK Experimental 面（OPENAI001）：本文件直接使用 Responses 类型。
#pragma warning disable OPENAI001

namespace Athena.UI.Services;

public sealed class WorkspaceKnowledgeCompressor : IWorkspaceKnowledgeCompressor
{
    private readonly OpenAiModelRuntimeFactory _modelFactory;
    private readonly ILogger _logger;

    public WorkspaceKnowledgeCompressor(OpenAiModelRuntimeFactory modelFactory, ILogger logger)
    {
        _modelFactory = modelFactory;
        _logger = logger.ForContext<WorkspaceKnowledgeCompressor>();
    }

    public async Task<string?> CompressAsync(string content, int charBudget, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(content) || charBudget <= 0) return null;
        try
        {
            var effective = _modelFactory.Resolve(AiModelRole.ContextCompression);
            // 预算是字符，输出上限是 token：不在两者之间换算——把字符上限写进提示词，输出上限取模型能给的，
            // 超出的部分由调用方按字符截断。
            var maxOutput = Math.Min(8192, effective.MaxOutputTokens > 0 ? effective.MaxOutputTokens : 8192);
            var systemPrompt = "Compress this workspace knowledge file. Preserve facts, commands, paths, decisions, constraints, and code identifiers. "
                               + $"The result must be at most {charBudget} characters long. Return Markdown only and invent nothing.";
            var knowledgeTimeout = OpenAiClientOptionsFactory.ResolveTimeoutSeconds(_modelFactory.TimeoutSeconds, maxOutput);
            if (ResponsesCallHelpers.ShouldUseResponses(effective))
            {
                var responses = ResponsesCallHelpers.CreateResponsesClient(effective, knowledgeTimeout);
                var options = ResponsesCallHelpers.CreateOptions(effective, systemPrompt, (float?)effective.Temperature, maxOutput);
                options.InputItems.Add(ResponseItem.CreateUserMessageItem(content));
                var result = await responses.CreateResponseAsync(options, cancellationToken);
                return ResponsesCallHelpers.GetFirstOutputText(result.Value)?.Trim();
            }

            var client = _modelFactory.CreateChatClient(AiModelRole.ContextCompression);
            var chatOptions = new ChatCompletionOptions
            {
                Temperature = (float?)effective.Temperature,
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
