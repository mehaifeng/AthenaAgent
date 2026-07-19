using Athena.UI.Services.Interfaces;
using OpenAI;
using OpenAI.Chat;
using Serilog;
using System;
using System.ClientModel;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Athena.UI.Services;

/// <summary>
/// 无 UI 的知识库整理 Agent 执行循环（非流式逐轮）。使用次级/后台模型，
/// 只在受限工具集内合并去重知识库文件，最终返回一段简报。结构对齐 SubAgentRunner，
/// 但不依赖任何 ViewModel，可在后台静默运行。
/// </summary>
public sealed class KnowledgeBaseMaintenanceRunner
{
    private readonly IConfigService _configService;
    private readonly IFunctionRegistry _functionRegistry;
    private readonly ILogger _logger;
    private readonly ILocalizationService? _localizationService;

    private string GetLocalized(string key, string defaultValue)
        => _localizationService?.GetString(key, defaultValue) ?? defaultValue;

    private const int MaxIterations = 30;
    // 整理只需读/搜/改/删 —— 明确不含 create_new_memory（整理阶段不应新建）与 dispatch_subagents。
    private static readonly string[] AllowedTools =
    {
        "recall_from_memory", "get_file_info", "search_in_file", "get_document_outline",
        "read_system_file", "list_system_directory", "modify_system_file", "write_system_file",
        "delete_system_file"
    };

    private const string SystemPrompt =
        "You are the knowledge-base maintenance agent. Your ONLY job is to consolidate duplicate or near-duplicate " +
        "memory files into a single canonical file per topic, WITHOUT losing any unique fact.\n\n" +
        "Rules:\n" +
        "- For each duplicate group provided, choose the best canonical file (the most complete, or the most clearly named).\n" +
        "- MERGE every unique fact, nuance, date, and preference from the other files into the canonical file using " +
        "modify_system_file or write_system_file. Lose nothing. When two facts conflict, keep the more recent / more specific one.\n" +
        "- Only AFTER the canonical file safely contains all merged content, delete the now-redundant files with delete_system_file. " +
        "NEVER delete a file before its content has been merged.\n" +
        "- Do NOT invent facts. If a group actually contains files about different topics, skip that group and leave it untouched.\n" +
        "- Memory file paths are relative to the knowledge base root; pass them directly to the file tools.\n" +
        "- Work autonomously; do not ask questions.\n" +
        "- When finished, reply with a concise plain-text summary: which files were merged into which, and which were deleted.";

    public KnowledgeBaseMaintenanceRunner(IConfigService configService, IFunctionRegistry functionRegistry, ILogger logger, ILocalizationService? localizationService = null)
    {
        _configService = configService;
        _functionRegistry = functionRegistry;
        _logger = logger.ForContext<KnowledgeBaseMaintenanceRunner>();
        _localizationService = localizationService;
    }

    /// <summary>
    /// 运行一轮整理。instruction 由调用方（服务）拼装：知识库根路径 + 疑似重复文件分组清单。
    /// 返回 (是否成功, 简报文本)。
    /// </summary>
    public async Task<(bool Success, string Summary)> RunAsync(string instruction, CancellationToken cancellationToken)
    {
        var config = _configService.Load();
        var effective = KnowledgeMaintenanceModelResolver.Resolve(config);

        if (string.IsNullOrWhiteSpace(effective.ApiKey) || string.IsNullOrWhiteSpace(effective.Model))
        {
            return (false, GetLocalized("KbMaint.ModelNotConfigured", "Maintenance model is not configured (missing API Key or model)."));
        }

        ChatClient chatClient;
        try
        {
            var clientOptions = new OpenAIClientOptions();
            if (!string.IsNullOrWhiteSpace(effective.BaseUrl))
            {
                clientOptions.Endpoint = new Uri(effective.BaseUrl);
            }

            var client = new OpenAIClient(new ApiKeyCredential(effective.ApiKey), clientOptions);
            chatClient = client.GetChatClient(effective.Model);
        }
        catch (Exception ex)
        {
            return (false, string.Format(GetLocalized("KbMaint.ClientInitFailed", "Failed to initialize the maintenance model client: {0}"), ex.Message));
        }

        var tools = _functionRegistry.GetToolDefinitions(AllowedTools).OfType<ChatTool>().ToList();

        var messages = new List<ChatMessage>
        {
            new SystemChatMessage(SystemPrompt),
            new UserChatMessage(instruction)
        };

        var options = new ChatCompletionOptions
        {
            Temperature = (float)effective.Temperature,
            MaxOutputTokenCount = effective.MaxOutputTokens
        };
        foreach (var tool in tools)
        {
            options.Tools.Add(tool);
        }

        // 知识库定期整理是第一方、沙箱化、用户已显式开启的后台例程：审批闸门自动放行，
        // 否则会把例程自身的 KB 写入/清理全部拒掉。绝不弹窗。
        using var approvalScope = Athena.UI.Services.ToolApprovalContext.EnterTrusted();

        try
        {
            for (var iteration = 0; iteration < MaxIterations; iteration++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                ChatCompletion value;
                try
                {
                    var completion = await chatClient.CompleteChatAsync(messages, options, cancellationToken);
                    value = completion.Value;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "整理 Agent 模型调用失败");
                    return (false, string.Format(GetLocalized("KbMaint.ModelCallFailed", "Model call failed: {0}"), ex.Message));
                }

                var toolCalls = value.ToolCalls;
                if (toolCalls == null || toolCalls.Count == 0)
                {
                    var finalText = value.Content.Count > 0 ? value.Content[0].Text : string.Empty;
                    return (true, string.IsNullOrWhiteSpace(finalText) ? GetLocalized("KbMaint.CompletedNoSummary", "(Maintenance completed, no summary)") : finalText.Trim());
                }

                messages.Add(new AssistantChatMessage(value));

                foreach (var toolCall in toolCalls)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var argsText = toolCall.FunctionArguments?.ToString() ?? "{}";
                    FunctionResult toolResult;
                    try
                    {
                        toolResult = await _functionRegistry.ExecuteAsync(toolCall.FunctionName, argsText);
                    }
                    catch (Exception ex)
                    {
                        toolResult = FunctionResult.FailureResult($"工具执行异常: {ex.Message}");
                    }

                    _logger.Information("整理 Agent 调用 {Tool} → {Ok}", toolCall.FunctionName, toolResult.Success);
                    messages.Add(new ToolChatMessage(toolCall.Id, toolResult.ToJson()));
                }
            }

            return (false, GetLocalized("KbMaint.MaxStepsReached", "Maximum steps reached before maintenance completed."));
        }
        catch (OperationCanceledException)
        {
            return (false, GetLocalized("KbMaint.Canceled", "Maintenance was canceled."));
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "整理 Agent 运行异常");
            return (false, string.Format(GetLocalized("KbMaint.Exception", "Maintenance exception: {0}"), ex.Message));
        }
    }
}
