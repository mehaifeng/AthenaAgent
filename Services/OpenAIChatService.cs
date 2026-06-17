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
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Athena.UI.Services;

/// <summary>
/// OpenAI 对话服务实现
/// </summary>
public class OpenAIChatService : IChatService
{
    private readonly IFunctionRegistry _functionRegistry;
    private readonly IPromptService _promptService;
    private readonly IConversationHistoryService? _historyService;
    private readonly ITokenService? _tokenService;
    private readonly ILocalizationService? _localizationService;
    private readonly IAttachmentStoreService? _attachmentStoreService;
    private readonly IConversationSessionAccessor? _conversationSessionAccessor;
    private readonly ISystemAudioService? _systemAudioService;
    private AppConfig _config;
    private OpenAIClient? _client;
    private ChatClient? _chatClient;

    public OpenAIChatService(
        AppConfig config,
        IPromptService promptService,
        IFunctionRegistry functionRegistry,
        IConversationHistoryService? historyService = null,
        ITokenService? tokenService = null,
        ILocalizationService? localizationService = null,
        IAttachmentStoreService? attachmentStoreService = null,
        IConversationSessionAccessor? conversationSessionAccessor = null,
        ISystemAudioService? systemAudioService = null)
    {
        _config = config;
        _promptService = promptService;
        _functionRegistry = functionRegistry;
        _historyService = historyService;
        _tokenService = tokenService;
        _localizationService = localizationService;
        _attachmentStoreService = attachmentStoreService;
        _conversationSessionAccessor = conversationSessionAccessor;
        _systemAudioService = systemAudioService;
        InitializeClient();
    }

    public void UpdateConfig(AppConfig config)
    {
        _config = config;
        InitializeClient();
    }

    private void InitializeClient()
    {
        if (string.IsNullOrWhiteSpace(_config.ApiKey))
        {
            _client = null;
            _chatClient = null;
            Log.Warning("API Key 为空，客户端未初始化");
            return;
        }

        try
        {
            var options = new OpenAIClientOptions();
            if (!string.IsNullOrWhiteSpace(_config.BaseUrl))
            {
                options.Endpoint = new Uri(_config.BaseUrl);
                Log.Information("使用自定义 Base URL: {BaseUrl}", _config.BaseUrl);
            }

            _client = new OpenAIClient(new ApiKeyCredential(_config.ApiKey), options);
            _chatClient = _client.GetChatClient(_config.Model);
            Log.Information("OpenAI 客户端初始化成功，模型: {Model}", _config.Model);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "OpenAI 客户端初始化失败");
            _client = null;
            _chatClient = null;
        }
    }

    public async IAsyncEnumerable<string> StreamMessageAsync(
        string userMessage,
        ConversationContext context,
        IReadOnlyList<ChatAttachment>? attachments = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default,
        Action<Models.ChatMessage>? onMessageAdded = null,
        Action<string, int>? onContextCompressed = null,
        bool addToContext = true)
    {
        if (_chatClient == null)
        {
            Log.Error("ChatClient 未初始化");
            yield return "[错误] 请先在设置中配置 API Key";
            yield break;
        }

        // 仅在明确要求时才加入上下文，防止 Regenerate 或 Edit 流程中重复添加
        if ((attachments?.Count > 0 || !string.IsNullOrWhiteSpace(userMessage)) && addToContext)
        {
            context.AddUserMessage(userMessage, attachments: attachments);
        }

        Log.Information("开始处理消息，用户输入长度: {Length}, 附件数: {AttachmentCount}",
            userMessage?.Length ?? 0,
            attachments?.Count ?? 0);

        var messages = BuildMessages(context);
        Log.Information("构建消息列表完成，消息数: {Count}", messages.Count);

        var contentBuilder = new StringBuilder();
        using var conversationScope = _conversationSessionAccessor?.Enter(context.ConversationId);

        await foreach (var text in ProcessStreamAsync(messages, contentBuilder, context, cancellationToken, onMessageAdded, onContextCompressed))
        {
            yield return text;
        }

        Log.Debug("StreamMessageAsync 迭代处理完成");
    }

    private async IAsyncEnumerable<string> ProcessStreamAsync(
        List<OpenAI.Chat.ChatMessage> messages,
        StringBuilder contentBuilder,
        ConversationContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken,
        Action<Models.ChatMessage>? onMessageAdded = null,
        Action<string, int>? onContextCompressed = null)
    {
        var iteration = 0;
        const int maxIterations = 50;
        var disabledToolCallRetries = 0;

        while (iteration < maxIterations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            iteration++;

            // [核心改进]：在每一轮迭代开始前检查 Token，确保工具调用链中也能自动压缩
            if (_tokenService != null && _historyService != null && _config.AutoCompress)
            {
                var currentTokens = context.EstimatedTokenCount;
                if (currentTokens > _config.CompressionThreshold)
                {
                    Log.Information("检测到工具调用循环中 Token 超过阈值 ({Tokens} > {Threshold})，触发中间压缩", 
                        currentTokens, _config.CompressionThreshold);
                    
                    // 将 ContextMessage 转换为 ChatMessage 以供压缩服务处理
                    var tempMessages = context.Messages.Select(m => new Models.ChatMessage 
                    { 
                        Role = m.Role, 
                        Content = m.Content, 
                        ToolCallsJson = m.ToolCallsJson, 
                        ReasoningContent = m.ReasoningContent,
                        Attachments = new System.Collections.ObjectModel.ObservableCollection<ChatAttachment>(m.Attachments),
                        IsCompressed = false 
                    }).ToList();

                    var (summary, compressedCount) = await _historyService.CompressContextAsync(tempMessages, _config.KeepRecentRounds);

                    if (summary != null && compressedCount > 0)
                    {
                        context.SetSummary(summary);
                        _tokenService.CompressionPreview = summary;

                        // [修复]：真正从 context 中移除已压缩的消息
                        context.RemoveMessages(compressedCount);

                        // [同步]：通知 UI 标记对应的消息为已压缩
                        onContextCompressed?.Invoke(summary, compressedCount);

                        // 重新构建消息列表（包含新的 summary 且去掉了已移除的消息）
                        messages = BuildMessages(context);
                        Log.Information("中间压缩完成，已移除 {Count} 条消息并重置消息列表", compressedCount);
                    }
                }
            }

            var options = CreateChatOptions();

            IAsyncEnumerable<StreamingChatCompletionUpdate>? stream = null;
            string? error = null;

            try
            {
                stream = _chatClient!.CompleteChatStreamingAsync(messages, options, cancellationToken);
            }
            catch (Exception ex)
            {
                error = FormatApiError(ex, context.Messages.Any(HasImageAttachment));
            }

            if (error != null)
            {
                Log.Error("API 调用失败: {Error}", error);
                yield return $"[API 错误: {error}]";
                yield break;
            }

            if (stream == null)
            {
                yield return "[API 错误: 无法获取响应流]";
                yield break;
            }

            var toolCallBuilders = new Dictionary<int, ToolCallBuilder>();
            ChatFinishReason? finishReason = null;
            var assistantContent = new StringBuilder();
            var assistantReasoning = new StringBuilder();

            await foreach (var update in stream.WithCancellation(cancellationToken))
            {
                AppendReasoningContent(update, assistantReasoning);

                foreach (var contentPart in update.ContentUpdate)
                {
                    if (!string.IsNullOrEmpty(contentPart.Text))
                    {
                        var text = contentPart.Text;
                        contentBuilder.Append(text);
                        assistantContent.Append(text);
                        yield return text;
                    }
                }

                foreach (var toolCallUpdate in update.ToolCallUpdates)
                {
                    var index = toolCallUpdate.Index;

                    if (!toolCallBuilders.ContainsKey(index))
                    {
                        toolCallBuilders[index] = new ToolCallBuilder
                        {
                            Id = toolCallUpdate.ToolCallId ?? string.Empty,
                            FunctionName = toolCallUpdate.FunctionName ?? string.Empty
                        };
                    }
                    else
                    {
                        var builder = toolCallBuilders[index];
                        if (!string.IsNullOrEmpty(toolCallUpdate.ToolCallId))
                        {
                            builder.Id = toolCallUpdate.ToolCallId;
                        }
                        if (!string.IsNullOrEmpty(toolCallUpdate.FunctionName))
                        {
                            builder.FunctionName = toolCallUpdate.FunctionName;
                        }
                    }

                    if (toolCallUpdate.FunctionArgumentsUpdate != null && toolCallUpdate.FunctionArgumentsUpdate.ToMemory().Length > 0)
                    {
                        try
                        {
                            var argsText = toolCallUpdate.FunctionArgumentsUpdate.ToString();
                            if (!string.IsNullOrEmpty(argsText))
                            {
                                toolCallBuilders[index].Arguments.Append(argsText);
                            }
                        }
                        catch (ArgumentNullException) { }
                    }
                }

                if (update.FinishReason != null)
                {
                    finishReason = update.FinishReason;
                }
            }

            Log.Debug("流式响应第 {Iteration} 轮, {Tools} tool calls", iteration, toolCallBuilders.Count);
            var reasoningContent = assistantReasoning.Length > 0 ? assistantReasoning.ToString() : null;
            var hasToolCalls = finishReason == ChatFinishReason.ToolCalls || toolCallBuilders.Count > 0;

            if (!IsFunctionCallingEnabled() && hasToolCalls)
            {
                Log.Warning("Function Calling is disabled, but the model returned structured tool calls. Retry={Retry}", disabledToolCallRetries);

                if (disabledToolCallRetries == 0)
                {
                    disabledToolCallRetries++;
                    messages.Add(new UserChatMessage("[Internal instruction: function calling is disabled. Do not call tools. Answer the user's last request in plain text only.]"));
                    continue;
                }

                yield return "[错误] 当前已关闭函数调用，但模型仍返回了结构化工具调用。已阻止执行。";
                yield break;
            }

            if (!hasToolCalls)
            {
                ChatAttachment? audioAttachment = null;
                string audioErrorMessage = string.Empty;

                var finalContent = assistantContent.ToString();

                if (_config.ChatAudioEnabled && !string.IsNullOrWhiteSpace(finalContent))
                {
                    var audioResult = await GenerateSpeechAttachmentAsync(finalContent, cancellationToken);
                    audioAttachment = audioResult.Attachment;
                    audioErrorMessage = audioResult.ErrorMessage;
                }

                if (assistantContent.Length == 0 && !string.IsNullOrWhiteSpace(finalContent))
                {
                    yield return finalContent;
                }

                if (!string.IsNullOrWhiteSpace(finalContent) || reasoningContent != null || audioAttachment != null || !string.IsNullOrWhiteSpace(audioErrorMessage))
                {
                    context.AddAssistantMessage(
                        finalContent,
                        reasoningContent: reasoningContent,
                        attachments: audioAttachment != null ? [audioAttachment] : null);

                    if (audioAttachment != null)
                    {
                        onMessageAdded?.Invoke(new Models.ChatMessage
                        {
                            Role = "assistant",
                            Attachments = new System.Collections.ObjectModel.ObservableCollection<ChatAttachment> { audioAttachment },
                            AudioErrorMessage = audioErrorMessage,
                            Timestamp = DateTime.Now
                        });
                    }
                    else if (!string.IsNullOrWhiteSpace(audioErrorMessage))
                    {
                        onMessageAdded?.Invoke(new Models.ChatMessage
                        {
                            Role = "assistant",
                            AudioErrorMessage = audioErrorMessage,
                            Timestamp = DateTime.Now
                        });
                    }

                    if (reasoningContent != null)
                    {
                        onMessageAdded?.Invoke(new Models.ChatMessage
                        {
                            Role = "assistant",
                            ReasoningContent = reasoningContent,
                            Timestamp = DateTime.Now
                        });
                    }
                }
                yield break;
            }
            
            var toolCalls = toolCallBuilders.Values.Select(b =>
            {
                var id = string.IsNullOrEmpty(b.Id) ? $"call_{Guid.NewGuid():N}" : b.Id;
                return new ToolCallInfo(id, b.FunctionName, b.Arguments.ToString());
            }).ToList();

            Log.Information("检测到 {Count} 个工具调用", toolCalls.Count);

            // 保存带工具调用的助手消息到上下文
            var toolCallsJson = JsonSerializer.Serialize(toolCalls);
            context.AddAssistantMessage(assistantContent.ToString(), toolCallsJson, reasoningContent);

            // 通知 UI 产生了带工具调用的助手消息
            var intermediateAssistantMsg = new Models.ChatMessage
            {
                Role = "assistant",
                Content = assistantContent.ToString(),
                ToolCallsJson = toolCallsJson,
                ReasoningContent = reasoningContent,
                Timestamp = DateTime.Now
            };
            onMessageAdded?.Invoke(intermediateAssistantMsg);

            messages.Add(CreateAssistantMessageWithToolCalls(toolCalls, assistantContent.ToString(), reasoningContent));

            foreach (var toolCall in toolCalls)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Log.Information("执行工具: {Name} | 参数: {Args}", toolCall.FunctionName, toolCall.Arguments);
                using var toolConversationScope = _conversationSessionAccessor?.Enter(context.ConversationId);
                var result = await ExecuteToolCallAsync(toolCall.FunctionName, toolCall.Arguments);
                cancellationToken.ThrowIfCancellationRequested();
                
                var resultJson = result.ToJson();
                Log.Information("工具 {Name} 执行完成 | 结果预览: {Result}", 
                    toolCall.FunctionName, 
                    resultJson.Length > 500 ? resultJson.Substring(0, 500) + "..." : resultJson);

                if (result.GeneratedAttachments.Count > 0)
                {
                    onMessageAdded?.Invoke(new Models.ChatMessage
                    {
                        Role = "assistant",
                        Attachments = new System.Collections.ObjectModel.ObservableCollection<ChatAttachment>(result.GeneratedAttachments),
                        Timestamp = DateTime.Now
                    });
                }
                
                // 通知 UI 产生了工具结果消息
                var toolResultMsg = new Models.ChatMessage
                {
                    Role = "tool",
                    Content = resultJson,
                    ToolCallId = toolCall.Id,
                    ToolName = toolCall.FunctionName,
                    Timestamp = DateTime.Now
                };
                onMessageAdded?.Invoke(toolResultMsg);

                messages.Add(new ToolChatMessage(toolCall.Id, resultJson));
                // 保存工具结果到上下文
                context.AddToolMessage(resultJson, toolCall.Id);
            }
        }

        Log.Debug("循环自然结束，迭代次数: {Iteration}", iteration);
    }

    private static AssistantChatMessage CreateAssistantMessageWithToolCalls(
        IEnumerable<ToolCallInfo> toolCalls,
        string? content = null,
        string? reasoningContent = null)
    {
        var message = new AssistantChatMessage(content ?? "");

        foreach (var tc in toolCalls)
        {
            message.ToolCalls.Add(ChatToolCall.CreateFunctionToolCall(
                tc.Id,
                tc.FunctionName,
                BinaryData.FromString(tc.Arguments)
            ));
        }

        ApplyReasoningContent(message, reasoningContent);
        return message;
    }

    private static void AppendReasoningContent(StreamingChatCompletionUpdate update, StringBuilder reasoningBuilder)
    {
#pragma warning disable SCME0001
        if (update.Patch.TryGetValue("$.choices[0].delta.reasoning_content"u8, out string? reasoningChunk)
            && reasoningChunk != null)
        {
            reasoningBuilder.Append(reasoningChunk);
        }
#pragma warning restore SCME0001
    }

    private static void ApplyReasoningContent(AssistantChatMessage message, string? reasoningContent)
    {
        if (reasoningContent == null)
        {
            return;
        }

#pragma warning disable SCME0001
        message.Patch.Set("$.reasoning_content"u8, reasoningContent);
#pragma warning restore SCME0001
    }

    private record ToolCallInfo(string Id, string FunctionName, string Arguments);

    private async Task<FunctionResult> ExecuteToolCallAsync(string functionName, string arguments)
    {
        if (_functionRegistry == null)
        {
            return FunctionResult.FailureResult("Function registry not available");
        }

        return await _functionRegistry.ExecuteAsync(functionName, arguments);
    }

    private ChatCompletionOptions CreateChatOptions()
    {
        var options = new ChatCompletionOptions
        {
            Temperature = (float)_config.Temperature,
            MaxOutputTokenCount = _config.MaxTokens,
            TopP = (float)_config.TopP
        };

        Log.Debug("API 参数: Temperature={Temp}, MaxTokens={MaxTokens}, TopP={TopP}",
            _config.Temperature, _config.MaxTokens, _config.TopP);

        ApplyToolOptions(options);

        return options;
    }

    private bool IsFunctionCallingEnabled()
    {
        return _config.EnableFunctionCalling && _functionRegistry?.HasFunctions == true;
    }

    private void ApplyToolOptions(ChatCompletionOptions options)
    {
        if (IsFunctionCallingEnabled())
        {
            foreach (var tool in _functionRegistry.GetToolDefinitions())
            {
                if (tool is ChatTool chatTool)
                {
                    options.Tools.Add(chatTool);
                }
            }
            Log.Debug("携带 {Count} 个工具", options.Tools.Count);
            return;
        }

        options.ToolChoice = ChatToolChoice.CreateNoneChoice();
        Log.Debug("Function Calling disabled; ToolChoice=None");
    }

    private const string TimestampFormat = "[yyMMddHHmmss-ddd] ";

    private List<OpenAI.Chat.ChatMessage> BuildMessages(ConversationContext context)
    {
        var persona = _promptService.GetPrompt(PromptType.MainPersona);

        // 将所有 system prompt 合并为一条，避免部分 API（如 MiniMax）对多 system 消息的限制
        var baseSystemParts = new List<string>();
        if (IsFunctionCallingEnabled())
        {
            baseSystemParts.Add(_promptService.GetPrompt(PromptType.ToolCallingPolicy));
        }
        baseSystemParts.Add(persona);
        baseSystemParts.Add(GetPlatformContextMessage(IsFunctionCallingEnabled()));
        context.SetMainPersona(string.Join("\n\n---\n\n", baseSystemParts.Where(s => !string.IsNullOrEmpty(s))));

        var systemParts = new List<string>(baseSystemParts);
        if (!string.IsNullOrEmpty(context.Summary))
        {
            systemParts.Add(context.Summary);
        }

        // 收集历史中所有的 system 消息，追加到合并 system prompt 末尾
        var historySystemMessages = context.Messages
            .Where(m => m.Role == "system")
            .Select(m => m.Content)
            .Where(c => !string.IsNullOrEmpty(c))
            .ToList();
        if (historySystemMessages.Count != 0)
        {
            systemParts.AddRange(historySystemMessages);
        }

        var messages = new List<OpenAI.Chat.ChatMessage>
        {
            new SystemChatMessage(string.Join("\n\n---\n\n", systemParts.Where(s => !string.IsNullOrEmpty(s))))
        };

        foreach (var msg in context.Messages)
        {
            switch (msg.Role)
            {
                case "user":
                    var timestamp = msg.Timestamp != default
                        ? msg.Timestamp.ToString(TimestampFormat)
                        : string.Empty;
                    // 文档附件解析出的 Markdown 作为文本注入到用户消息中，供 AI 阅读。
                    var userText = timestamp + msg.Content + BuildDocumentTextBlocks(msg);
                    if (HasImageAttachment(msg))
                    {
                        messages.Add(CreateUserMessageWithAttachments(userText, msg));
                    }
                    else
                    {
                        messages.Add(new UserChatMessage(userText));
                    }
                    break;
                case "assistant":
                    var assistantMsg = new AssistantChatMessage(msg.Content);
                    ApplyReasoningContent(assistantMsg, msg.ReasoningContent);
                    if (!string.IsNullOrWhiteSpace(msg.OutputAudioReferenceId))
                    {
#pragma warning disable OPENAI001
                        assistantMsg.OutputAudioReference = new ChatOutputAudioReference(msg.OutputAudioReferenceId);
#pragma warning restore OPENAI001
                    }
                    if (!string.IsNullOrEmpty(msg.ToolCallsJson))
                    {
                        try
                        {
                            // 使用内部定义的私有记录来兼容解析
                            var toolCalls = JsonSerializer.Deserialize<List<ToolCallJsonInfo>>(msg.ToolCallsJson);
                            if (toolCalls != null)
                            {
                                foreach (var tc in toolCalls)
                                {
                                    assistantMsg.ToolCalls.Add(ChatToolCall.CreateFunctionToolCall(
                                        tc.Id,
                                        tc.FunctionName,
                                        BinaryData.FromString(tc.Arguments)
                                    ));
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Log.Warning(ex, "解析工具调用 JSON 失败");
                        }
                    }
                    messages.Add(assistantMsg);
                    break;
                case "tool":
                    messages.Add(new ToolChatMessage(msg.ToolCallId ?? string.Empty, msg.Content));
                    break;
                // "system" 角色已在上面合并到主 system prompt，无需单独处理
            }
        }

        return messages;
    }

    public IReadOnlyList<RawContextEntry> BuildRawContext(ConversationContext context)
    {
        try
        {
            var messages = BuildMessages(context);
            var entries = new List<RawContextEntry>(messages.Count);

            int index = 0;
            foreach (var message in messages)
            {
                var role = message switch
                {
                    SystemChatMessage => "system",
                    UserChatMessage => "user",
                    AssistantChatMessage => "assistant",
                    ToolChatMessage => "tool",
                    _ => message.GetType().Name
                };

                var header = $"[{index++}] {role}";
                if (message is ToolChatMessage tool)
                {
                    header += $"  (tool_call_id={tool.ToolCallId})";
                }

                var body = new StringBuilder();

                // 工具返回(tool 角色)通常是压缩成单行的 JSON，美化展开以便换行、避免撑大横向滚动条。
                var isToolMessage = message is ToolChatMessage;
                foreach (var part in message.Content)
                {
                    if (part.Kind == ChatMessageContentPartKind.Text)
                    {
                        body.Append(isToolMessage ? UnescapeForDisplay(TryPrettyJson(part.Text)) : part.Text).Append('\n');
                    }
                    else if (part.Kind == ChatMessageContentPartKind.Image)
                    {
                        var descriptor = part.ImageUri?.ToString()
                            ?? $"inline bytes ({part.ImageBytesMediaType}, {part.ImageBytes?.ToArray().Length ?? 0} B)";
                        body.Append("<image: ").Append(descriptor).Append(">\n");
                    }
                    else
                    {
                        body.Append('<').Append(part.Kind).Append(">\n");
                    }
                }

                if (message is AssistantChatMessage assistant && assistant.ToolCalls.Count > 0)
                {
                    foreach (var call in assistant.ToolCalls)
                    {
                        // 工具调用参数同样是单行 JSON，展开为多行缩进。
                        body.Append("↳ tool_call ").Append(call.FunctionName).Append('\n');
                        body.Append(IndentLines(UnescapeForDisplay(TryPrettyJson(call.FunctionArguments?.ToString())), "    ")).Append('\n');
                    }
                }

                entries.Add(new RawContextEntry
                {
                    Role = role,
                    Header = header,
                    Text = body.ToString().TrimEnd('\n')
                });
            }

            return entries;
        }
        catch (Exception ex)
        {
            return new List<RawContextEntry>
            {
                new() { Header = "error", Text = "构建 raw 上下文失败: " + ex.Message }
            };
        }
    }

    private static readonly JsonSerializerOptions RawJsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>尝试把单行 JSON 美化为多行缩进；非 JSON 原样返回。</summary>
    private static string TryPrettyJson(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return raw ?? string.Empty;
        var trimmed = raw.TrimStart();
        if (trimmed.Length == 0 || (trimmed[0] != '{' && trimmed[0] != '['))
        {
            return raw;
        }

        try
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse(raw);
            return node?.ToJsonString(RawJsonOptions) ?? raw;
        }
        catch
        {
            return raw;
        }
    }

    private static string IndentLines(string text, string indent)
    {
        if (string.IsNullOrEmpty(text)) return text;
        return indent + text.Replace("\n", "\n" + indent);
    }

    /// <summary>
    /// 将文本中的转义序列（\n、\t、\"、\\、\uXXXX 等）还原为真实字符，便于调试阅读。
    /// 仅用于展示，不要求结果仍是合法 JSON。
    /// </summary>
    private static string UnescapeForDisplay(string? text)
    {
        if (string.IsNullOrEmpty(text) || text.IndexOf('\\') < 0) return text ?? string.Empty;

        var sb = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c != '\\' || i == text.Length - 1)
            {
                sb.Append(c);
                continue;
            }

            var next = text[++i];
            switch (next)
            {
                case 'n': sb.Append('\n'); break;
                case 'r': break; // 丢弃 CR，避免重复换行
                case 't': sb.Append('\t'); break;
                case '"': sb.Append('"'); break;
                case '\\': sb.Append('\\'); break;
                case '/': sb.Append('/'); break;
                case 'b': sb.Append('\b'); break;
                case 'f': sb.Append('\f'); break;
                case 'u' when i + 4 < text.Length
                    && int.TryParse(text.AsSpan(i + 1, 4), System.Globalization.NumberStyles.HexNumber,
                        System.Globalization.CultureInfo.InvariantCulture, out var code):
                    sb.Append((char)code);
                    i += 4;
                    break;
                default:
                    sb.Append('\\').Append(next);
                    break;
            }
        }

        return sb.ToString();
    }

    private static bool HasImageAttachment(ContextMessage message)
    {
        return message.Attachments.Any(a => a.Kind == AttachmentKind.Image);
    }

    // 延迟载入清单卡里附带的开头预览字符数上限。
    private const int DeferredPreviewChars = 1500;

    /// <summary>
    /// 将消息中的文档/文本/代码附件拼接进上下文：
    /// - 内联（小文件）：直接拼全文；
    /// - 延迟（大文件）：仅拼“清单卡”——文件指针 + 元信息 + 开头预览 + 读取提示，由模型按需调工具读取。
    /// </summary>
    private static string BuildDocumentTextBlocks(ContextMessage message)
    {
        var documents = message.Attachments
            .Where(a => (a.Kind == AttachmentKind.Document || a.Kind == AttachmentKind.Code)
                && (a.RetrievalMode == AttachmentRetrievalMode.Deferred || !string.IsNullOrWhiteSpace(a.ExtractedText)))
            .ToList();
        if (documents.Count == 0)
        {
            return string.Empty;
        }

        var builder = new System.Text.StringBuilder();
        foreach (var doc in documents)
        {
            var name = string.IsNullOrWhiteSpace(doc.FileName) ? "document" : doc.FileName;
            if (doc.RetrievalMode == AttachmentRetrievalMode.Deferred)
            {
                builder.Append("\n\n--- ");
                builder.Append(name);
                builder.Append(" (内容过大，未全部载入 / not fully loaded) ---\n");
                builder.Append($"[类型 {doc.Kind} · {FormatSize(doc.SizeBytes)} · 约 {doc.EstimatedTokens} tokens]\n");
                builder.Append("完整内容位于本地文件: ");
                builder.Append(doc.RetrievalPath);
                builder.Append('\n');
                builder.Append("需要时用 get_document_outline / search_in_file / read_system_file（支持 sectionTitle、startLine-endLine、chunkIndex）读取上述路径，不要凭空作答。\n");

                var preview = ReadHeadPreview(doc.RetrievalPath, DeferredPreviewChars);
                if (!string.IsNullOrWhiteSpace(preview))
                {
                    builder.Append("预览(开头部分):\n");
                    builder.Append(preview);
                }
            }
            else
            {
                builder.Append("\n\n--- ");
                builder.Append(name);
                builder.Append(" ---\n");
                builder.Append(doc.ExtractedText.Trim());
            }
        }

        return builder.ToString();
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024d:0.#} KB";
        return $"{bytes / 1024d / 1024d:0.#} MB";
    }

    private static string ReadHeadPreview(string path, int maxChars)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return string.Empty;
            }

            var buffer = new char[maxChars];
            using var reader = new StreamReader(path);
            int read = reader.Read(buffer, 0, maxChars);
            return read <= 0 ? string.Empty : new string(buffer, 0, read).Trim();
        }
        catch
        {
            return string.Empty;
        }
    }

    private static UserChatMessage CreateUserMessageWithAttachments(string text, ContextMessage message)
    {
        var parts = new List<ChatMessageContentPart>();
        if (!string.IsNullOrWhiteSpace(text))
        {
            parts.Add(ChatMessageContentPart.CreateTextPart(text));
        }

        foreach (var attachment in message.Attachments.Where(a => a.Kind == AttachmentKind.Image))
        {
            if (string.IsNullOrWhiteSpace(attachment.StoredPath) || !File.Exists(attachment.StoredPath))
            {
                throw new FileNotFoundException($"Attachment file not found: {attachment.FileName}", attachment.StoredPath);
            }

            var bytes = File.ReadAllBytes(attachment.StoredPath);
            parts.Add(ChatMessageContentPart.CreateImagePart(
                BinaryData.FromBytes(bytes),
                attachment.MimeType,
                ChatImageDetailLevel.Auto));
        }

        return new UserChatMessage(parts);
    }

    private string FormatApiError(Exception exception, bool requestHasImages)
    {
        if (requestHasImages && IsLikelyImageInputFailure(exception))
        {
            return _localizationService?.GetString(
                "Chat.Error.ImageUnsupported",
                "The current model or endpoint does not support image input. Please switch the main model to a vision-capable model and try again.")
                ?? "The current model or endpoint does not support image input. Please switch the main model to a vision-capable model and try again.";
        }

        return exception.Message;
    }

    private static bool IsLikelyImageInputFailure(Exception exception)
    {
        if (exception is ClientResultException clientException
            && clientException.Status is 400 or 415 or 422)
        {
            return true;
        }

        var message = exception.Message;
        return message.Contains("image", StringComparison.OrdinalIgnoreCase)
            || message.Contains("vision", StringComparison.OrdinalIgnoreCase)
            || message.Contains("modal", StringComparison.OrdinalIgnoreCase)
            || message.Contains("unsupported", StringComparison.OrdinalIgnoreCase);
    }

    private record ToolCallJsonInfo(string Id, string FunctionName, string Arguments);

    private async Task<(ChatAttachment? Attachment, string ErrorMessage)> CreateAssistantAudioAttachmentAsync(byte[] audioBytes, CancellationToken cancellationToken)
    {
        return await CreateAssistantAudioAttachmentAsync(
            audioBytes,
            $"assistant-{DateTime.Now:yyyyMMdd-HHmmss}.mp3",
            "audio/mpeg",
            cancellationToken);
    }

    private static string GetSystemSpeechTempPath()
    {
        var extension = OperatingSystem.IsMacOS() ? ".aiff" : ".wav";
        return Path.Combine(Path.GetTempPath(), $"athena-tts-{Guid.NewGuid():N}{extension}");
    }

    private static void TryDeleteTempFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    public async Task<(bool Success, string? Message)> TestConnectionAsync()
    {
        if (_chatClient == null)
        {
            return (false, "请先配置 API Key");
        }

        try
        {
            var messages = new List<OpenAI.Chat.ChatMessage>
            {
                new SystemChatMessage("Reply with 'OK' only."),
                new UserChatMessage("test")
            };

            var options = new ChatCompletionOptions
            {
                Temperature = (float)_config.Temperature,
                MaxOutputTokenCount = Math.Min(_config.MaxTokens, 10),
                TopP = (float)_config.TopP
            };

            ApplyToolOptions(options);

            var response = await _chatClient.CompleteChatAsync(messages, options);

            if (response?.Value?.Content == null || response.Value.Content.Count == 0)
            {
                return (false, _localizationService?.GetString("Config.TestConnectNoRespond"));
            }

            var content = response.Value.Content[0].Text;

            Log.Information("API 连接测试成功");
            return (true, "连接成功");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "API 连接测试失败");
            return (false, $"连接失败: {ex.Message}");
        }
    }

    public async Task<AudioOutputTestResult> TestAudioOutputAsync(CancellationToken cancellationToken = default)
    {
        if (!_config.ChatAudioEnabled)
        {
            return new AudioOutputTestResult
            {
                Success = false,
                Message = "Please enable chat audio output first."
            };
        }

        var audioConfig = AudioConfigResolver.Resolve(_config);
        if (!string.Equals(audioConfig.Provider, "System", StringComparison.OrdinalIgnoreCase)
            && (string.IsNullOrWhiteSpace(audioConfig.ApiKey) || string.IsNullOrWhiteSpace(audioConfig.BaseUrl)))
        {
            return new AudioOutputTestResult
            {
                Success = false,
                Message = "Please configure the audio API key and base URL."
            };
        }

        try
        {
            var storeResult = await GenerateSpeechAttachmentAsync("Hello from Athena audio output test.", cancellationToken);
            if (storeResult.Attachment == null)
            {
                return new AudioOutputTestResult
                {
                    Success = false,
                    Message = string.IsNullOrWhiteSpace(storeResult.ErrorMessage)
                        ? "Failed to create a playable test audio attachment."
                        : storeResult.ErrorMessage
                };
            }

            return new AudioOutputTestResult
            {
                Success = true,
                Message = "Audio output test succeeded.",
                Attachment = storeResult.Attachment
            };
        }
        catch (Exception ex)
        {
            Log.Error(ex, "音频输出测试失败");
            return new AudioOutputTestResult
            {
                Success = false,
                Message = $"Audio output test failed: {ex.Message}"
            };
        }
    }

    private async Task<(ChatAttachment? Attachment, string ErrorMessage)> GenerateSpeechAttachmentAsync(string text, CancellationToken cancellationToken)
    {
        var audioConfig = AudioConfigResolver.Resolve(_config);
        if (string.Equals(audioConfig.Provider, "System", StringComparison.OrdinalIgnoreCase))
        {
            return await GenerateSystemSpeechAttachmentAsync(text, audioConfig, cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(audioConfig.ApiKey) || string.IsNullOrWhiteSpace(audioConfig.BaseUrl))
        {
            return (null, "Audio output is not fully configured.");
        }

        try
        {
            using var httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(Math.Max(_config.Timeout, 30))
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, audioConfig.BaseUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", audioConfig.ApiKey);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("audio/mpeg"));

            var payload = new JsonObject
            {
                ["model"] = audioConfig.Model,
                ["input"] = text.Length > 4096 ? text[..4096] : text,
                ["voice"] = audioConfig.Voice,
                ["response_format"] = "mp3"
            };

            request.Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");

            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
                return (null, $"Audio output request failed: {(int)response.StatusCode} {response.ReasonPhrase}. {errorBody}".Trim());
            }

            var audioBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            if (audioBytes.Length == 0)
            {
                return (null, "Audio output request returned an empty body.");
            }

            return await CreateAssistantAudioAttachmentAsync(audioBytes, cancellationToken);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "独立音频输出生成失败");
            return (null, $"Audio output generation failed: {ex.Message}");
        }
    }

    private async Task<(ChatAttachment? Attachment, string ErrorMessage)> GenerateSystemSpeechAttachmentAsync(
        string text,
        ResolvedAudioConfig audioConfig,
        CancellationToken cancellationToken)
    {
        if (_systemAudioService == null || !_systemAudioService.IsSupported)
        {
            return (null, "System audio output is unavailable on this device.");
        }

        var input = text.Length > 4096 ? text[..4096] : text;
        var tempFile = GetSystemSpeechTempPath();

        try
        {
            var mimeType = OperatingSystem.IsMacOS() ? "audio/aiff" : "audio/wav";
            var result = await _systemAudioService.SynthesizeToFileAsync(input, audioConfig.Voice, tempFile, cancellationToken);
            if (!result.Success)
            {
                return (null, $"System audio output failed: {result.Message}".Trim());
            }

            if (!File.Exists(tempFile))
            {
                return (null, "System audio output did not produce an audio file.");
            }

            var bytes = await File.ReadAllBytesAsync(tempFile, cancellationToken);
            if (bytes.Length == 0)
            {
                return (null, "System audio output produced an empty audio file.");
            }

            return await CreateAssistantAudioAttachmentAsync(bytes, Path.GetFileName(tempFile), mimeType, cancellationToken);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "系统音频输出生成失败");
            return (null, $"System audio output generation failed: {ex.Message}");
        }
        finally
        {
            TryDeleteTempFile(tempFile);
        }
    }

    private async Task<(ChatAttachment? Attachment, string ErrorMessage)> CreateAssistantAudioAttachmentAsync(
        byte[] audioBytes,
        string fileName,
        string mimeType,
        CancellationToken cancellationToken)
    {
        if (_attachmentStoreService == null || audioBytes.Length == 0)
        {
            return (null, "Audio attachment storage is unavailable.");
        }

        try
        {
            var attachment = await _attachmentStoreService.CreateGeneratedAudioAsync(
                audioBytes,
                fileName,
                mimeType,
                cancellationToken: cancellationToken);
            attachment.AudioProvider = AudioConfigResolver.Resolve(_config).Provider;
            return (attachment, string.Empty);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "保存 assistant 音频附件失败");
            return (null, $"Failed to save audio output: {ex.Message}");
        }
    }

    private class ToolCallBuilder
    {
        public string Id { get; set; } = string.Empty;
        public string FunctionName { get; set; } = string.Empty;
        public StringBuilder Arguments { get; set; } = new StringBuilder();
    }

    /// <summary>
    /// 生成平台上下文 system message，让模型知道当前运行环境
    /// </summary>
    private static string GetPlatformContextMessage(bool includeToolGuidance)
    {
        string os, shell, pathSep, lineEnding, examplePath;

        if (OperatingSystem.IsWindows())
        {
            os = "Windows";
            shell = "PowerShell (pwsh) or cmd.exe";
            pathSep = @"\";
            lineEnding = "CRLF (\\r\\n)";
            examplePath = @"C:\Users\username\Documents";
        }
        else if (OperatingSystem.IsMacOS())
        {
            os = "macOS";
            shell = "zsh";
            pathSep = "/";
            lineEnding = "LF (\\n)";
            examplePath = "/Users/username/Documents";
        }
        else
        {
            os = "Linux";
            shell = "bash";
            pathSep = "/";
            lineEnding = "LF (\\n)";
            examplePath = "/home/username/documents";
        }

        var platformContext = $"""
            ## Runtime Environment (injected — do not modify)
            - OS: {os}
            - Default shell: {shell}
            - Path separator: `{pathSep}`
            - Line endings: {lineEnding}
            - Example path: `{examplePath}`
            """;

        if (!includeToolGuidance)
        {
            return platformContext;
        }

        return platformContext + $"""

            When using `execute_terminal_command`, always use commands and syntax appropriate for **{os}**.
            - On Windows: use PowerShell cmdlets or cmd syntax (e.g., `Get-ChildItem`, `ipconfig`, `tasklist`)
            - On macOS/Linux: use POSIX shell commands (e.g., `ls`, `ps`, `ifconfig`/`ip`)
            Never mix cross-platform commands (e.g., do not use `ls` on Windows or `dir` on macOS).
            """;
    }
}
