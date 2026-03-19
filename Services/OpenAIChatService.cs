using Athena.UI.Models;
using Athena.UI.Services.Interfaces;
using OpenAI;
using OpenAI.Chat;
using Serilog;
using System;
using System.ClientModel;
using System.Collections.Generic;
using System.Linq;
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
    private readonly IFunctionRegistry? _functionRegistry;
    private readonly IPromptService _promptService;
    private AppConfig _config;
    private OpenAIClient? _client;
    private ChatClient? _chatClient;

    public OpenAIChatService(
        AppConfig config,
        IPromptService promptService,
        IFunctionRegistry? functionRegistry = null)
    {
        _config = config;
        _promptService = promptService;
        _functionRegistry = functionRegistry;
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
        [EnumeratorCancellation] CancellationToken cancellationToken = default,
        Action<Models.ChatMessage>? onMessageAdded = null,
        bool addToContext = true)
    {
        if (_chatClient == null)
        {
            Log.Error("ChatClient 未初始化");
            yield return "[错误] 请先在设置中配置 API Key";
            yield break;
        }

        // 仅在明确要求时才加入上下文，防止 Regenerate 或 Edit 流程中重复添加
        if (!string.IsNullOrWhiteSpace(userMessage) && addToContext)
        {
            context.AddUserMessage(userMessage);
        }

        Log.Information("开始处理消息，用户输入长度: {Length}", userMessage?.Length ?? 0);

        var messages = BuildMessages(context);
        Log.Information("构建消息列表完成，消息数: {Count}", messages.Count);

        var contentBuilder = new StringBuilder();

        await foreach (var text in ProcessStreamAsync(messages, contentBuilder, context, cancellationToken, onMessageAdded))
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
        Action<Models.ChatMessage>? onMessageAdded = null)
    {
        var iteration = 0;
        const int maxIterations = 25;

        while (iteration < maxIterations)
        {
            iteration++;
            var options = CreateChatOptions();

            IAsyncEnumerable<StreamingChatCompletionUpdate>? stream = null;
            string? error = null;

            try
            {
                stream = _chatClient!.CompleteChatStreamingAsync(messages, options, cancellationToken);
            }
            catch (Exception ex)
            {
                error = ex.Message;
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

            await foreach (var update in stream.WithCancellation(cancellationToken))
            {
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

            if (finishReason != ChatFinishReason.ToolCalls || toolCallBuilders.Count == 0)
            {
                if (assistantContent.Length > 0)
                {
                    context.AddAssistantMessage(assistantContent.ToString());
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
            context.AddAssistantMessage(assistantContent.ToString(), toolCallsJson);

            // 通知 UI 产生了带工具调用的助手消息
            var intermediateAssistantMsg = new Models.ChatMessage
            {
                Role = "assistant",
                Content = assistantContent.ToString(),
                ToolCallsJson = toolCallsJson,
                Timestamp = DateTime.Now
            };
            onMessageAdded?.Invoke(intermediateAssistantMsg);

            messages.Add(CreateAssistantMessageWithToolCalls(toolCalls, assistantContent.ToString()));

            foreach (var toolCall in toolCalls)
            {
                Log.Information("执行工具: {Name} | 参数: {Args}", toolCall.FunctionName, toolCall.Arguments);
                var result = await ExecuteToolCallAsync(toolCall.FunctionName, toolCall.Arguments);
                
                var resultJson = result.ToJson();
                Log.Information("工具 {Name} 执行完成 | 结果预览: {Result}", 
                    toolCall.FunctionName, 
                    resultJson.Length > 500 ? resultJson.Substring(0, 500) + "..." : resultJson);
                
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

    private static AssistantChatMessage CreateAssistantMessageWithToolCalls(IEnumerable<ToolCallInfo> toolCalls, string? content = null)
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

        return message;
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

        if (_config.EnableFunctionCalling && _functionRegistry?.HasFunctions == true)
        {
            foreach (var tool in _functionRegistry.GetToolDefinitions())
            {
                if (tool is ChatTool chatTool)
                {
                    options.Tools.Add(chatTool);
                }
            }
            Log.Debug("携带 {Count} 个工具", options.Tools.Count);
        }

        return options;
    }

    private const string TimestampPrefix = "[TIMESTAMP]";

    private List<OpenAI.Chat.ChatMessage> BuildMessages(ConversationContext context)
    {
        var persona = _promptService.GetPrompt(PromptType.MainPersona);
        context.SetMainPersona(persona);

        var messages = new List<OpenAI.Chat.ChatMessage>
        {
            new SystemChatMessage(persona),
            new SystemChatMessage(GetPlatformContextMessage())
        };

        // 如果存在摘要，作为第一条系统消息插入（在人格之后）
        if (!string.IsNullOrEmpty(context.Summary))
        {
            messages.Add(new SystemChatMessage(context.Summary));
        }

        // 找到最后一条用户消息的索引（用于插入时间戳）
        var contextMessages = context.Messages.ToList();
        int lastUserIndex = -1;
        for (int i = contextMessages.Count - 1; i >= 0; i--)
        {
            if (contextMessages[i].Role == "user")
            {
                lastUserIndex = i;
                break;
            }
        }

        for (int i = 0; i < contextMessages.Count; i++)
        {
            var msg = contextMessages[i];

            // 在最后一条用户消息前插入当前时间戳（动态注入，不存入 Context.Messages）
            if (i == lastUserIndex)
            {
                var now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss dddd");
                messages.Add(new SystemChatMessage(
                    $"{TimestampPrefix} Current time: {now}. Use this as your time reference for all time-related reasoning."));
            }

            switch (msg.Role)
            {
                case "user":
                    messages.Add(new UserChatMessage(msg.Content));
                    break;
                case "assistant":
                    var assistantMsg = new AssistantChatMessage(msg.Content);
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
                case "system":
                    messages.Add(new SystemChatMessage(msg.Content));
                    break;
                case "tool":
                    messages.Add(new ToolChatMessage(msg.ToolCallId ?? string.Empty, msg.Content));
                    break;
            }
        }

        // 安全检查：如果消息列表以 system 消息结尾，或者完全没有用户消息，
        // 许多 API（如智谱、DeepSeek、甚至部分 OpenAI 端点）会返回 400 错误。
        if (messages.Count > 0 && messages.Last() is SystemChatMessage lastSystemMsg)
        {
            // 如果只有系统消息，我们将最后一条改为用户消息角色，以触发模型响应
            var content = lastSystemMsg.Content.FirstOrDefault()?.Text ?? string.Empty;
            messages.RemoveAt(messages.Count - 1);
            messages.Add(new UserChatMessage(content));
            Log.Debug("检测到以 System 消息结尾，已自动转换为 User 消息角色以规避 API 限制");
        }

        return messages;
    }

    private record ToolCallJsonInfo(string Id, string FunctionName, string Arguments);

    public async Task<(bool Success, string Message)> TestConnectionAsync()
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

            var options = new ChatCompletionOptions { MaxOutputTokenCount = 10 };
            var response = await _chatClient.CompleteChatAsync(messages, options);
            
            if (response?.Value?.Content == null || response.Value.Content.Count == 0)
            {
                return (false, "连接成功但未收到有效响应（可能是安全过滤或模型拒绝回答）");
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

    private class ToolCallBuilder
    {
        public string Id { get; set; } = string.Empty;
        public string FunctionName { get; set; } = string.Empty;
        public StringBuilder Arguments { get; set; } = new StringBuilder();
    }

    /// <summary>
    /// 生成平台上下文 system message，让模型知道当前运行环境
    /// </summary>
    private static string GetPlatformContextMessage()
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

        return $"""
            ## Runtime Environment (injected — do not modify)
            - OS: {os}
            - Default shell: {shell}
            - Path separator: `{pathSep}`
            - Line endings: {lineEnding}
            - Example path: `{examplePath}`

            When using `execute_terminal_command`, always use commands and syntax appropriate for **{os}**.
            - On Windows: use PowerShell cmdlets or cmd syntax (e.g., `Get-ChildItem`, `ipconfig`, `tasklist`)
            - On macOS/Linux: use POSIX shell commands (e.g., `ls`, `ps`, `ifconfig`/`ip`)
            Never mix cross-platform commands (e.g., do not use `ls` on Windows or `dir` on macOS).
            """;
    }
}