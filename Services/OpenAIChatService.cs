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
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (_chatClient == null)
        {
            Log.Error("ChatClient 未初始化");
            yield return "[错误] 请先在设置中配置 API Key";
            yield break;
        }

        Log.Information("开始处理消息，用户输入长度: {Length}", userMessage?.Length ?? 0);

        var messages = BuildMessages(context, userMessage);
        Log.Information("构建消息列表完成，消息数: {Count}", messages.Count);

        var contentBuilder = new StringBuilder();

        await foreach (var text in ProcessStreamAsync(messages, contentBuilder, cancellationToken))
        {
            yield return text;
        }

        if (!string.IsNullOrWhiteSpace(userMessage))
        {
            context.AddUserMessage(userMessage);
        }
        if (contentBuilder.Length > 0)
        {
            context.AddAssistantMessage(contentBuilder.ToString());
            Log.Debug("已将 AI 响应添加到上下文");
        }
    }

    private async IAsyncEnumerable<string> ProcessStreamAsync(
        List<OpenAI.Chat.ChatMessage> messages,
        StringBuilder contentBuilder,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var iteration = 0;
        const int maxIterations = 5;

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

                    if (toolCallUpdate.FunctionArgumentsUpdate != null)
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
                yield break;
            }

            var toolCalls = toolCallBuilders.Values.Select(b =>
            {
                var id = string.IsNullOrEmpty(b.Id) ? $"call_{Guid.NewGuid():N}" : b.Id;
                return new ToolCallInfo(id, b.FunctionName, b.Arguments.ToString());
            }).ToList();

            Log.Information("检测到 {Count} 个工具调用", toolCalls.Count);

            messages.Add(CreateAssistantMessageWithToolCalls(toolCalls, assistantContent.ToString()));

            foreach (var toolCall in toolCalls)
            {
                Log.Information("执行工具: {Name}", toolCall.FunctionName);
                var result = await ExecuteToolCallAsync(toolCall.FunctionName, toolCall.Arguments);
                Log.Information("工具 {Name} 执行结果: {Success}", toolCall.FunctionName, result.Success);
                messages.Add(new ToolChatMessage(toolCall.Id, result.ToJson()));
            }
        }

        Log.Warning("达到最大迭代次数 {Max}", maxIterations);
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

    private List<OpenAI.Chat.ChatMessage> BuildMessages(ConversationContext context, string? userMessage)
    {
        var messages = new List<OpenAI.Chat.ChatMessage>
        {
            new SystemChatMessage(_promptService.GetPrompt(PromptType.MainPersona))
        };

        foreach (var msg in context.Messages)
        {
            switch (msg.Role)
            {
                case "user":
                    messages.Add(new UserChatMessage(msg.Content));
                    break;
                case "assistant":
                    messages.Add(new AssistantChatMessage(msg.Content));
                    break;
                case "system":
                    messages.Add(new SystemChatMessage(msg.Content));
                    break;
            }
        }

        if (!string.IsNullOrWhiteSpace(userMessage))
        {
            messages.Add(new UserChatMessage(userMessage));
        }

        return messages;
    }

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
}
