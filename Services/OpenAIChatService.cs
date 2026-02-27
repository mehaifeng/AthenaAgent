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
/// 支持两阶段 Function Calling：元工具发现 + 动态加载工具
/// </summary>
public class OpenAIChatService : IChatService
{
    private readonly IFunctionRegistry? _functionRegistry;
    private readonly IToolDiscoveryService? _toolDiscoveryService;
    private readonly IPromptService _promptService;
    private AppConfig _config;
    private OpenAIClient? _client;
    private ChatClient? _chatClient;

    public OpenAIChatService(
        AppConfig config,
        IPromptService promptService,
        IFunctionRegistry? functionRegistry = null,
        IToolDiscoveryService? toolDiscoveryService = null)
    {
        _config = config;
        _promptService = promptService;
        _functionRegistry = functionRegistry;
        _toolDiscoveryService = toolDiscoveryService;
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

        // 构建消息列表
        var messages = BuildMessages(context, userMessage);
        Log.Information("构建消息列表完成，消息数: {Count}", messages.Count);

        // 流式处理，支持两阶段工具调用
        var contentBuilder = new StringBuilder();

        await foreach (var text in ProcessStreamWithToolDiscoveryAsync(
            messages, contentBuilder, cancellationToken))
        {
            yield return text;
        }

        // 添加到上下文
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

    /// <summary>
    /// 处理流式响应，支持两阶段工具发现
    /// </summary>
    private async IAsyncEnumerable<string> ProcessStreamWithToolDiscoveryAsync(
        List<OpenAI.Chat.ChatMessage> messages,
        StringBuilder contentBuilder,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var iteration = 0;
        const int maxIterations = 8; // 防止无限循环（增加了工具发现阶段）

        // 当前激活的工具列表（初始只有元工具）
        List<ChatTool>? activeTools = null;

        // 是否已完成工具发现阶段
        var toolDiscoveryCompleted = false;

        while (iteration < maxIterations)
        {
            iteration++;

            // 创建选项，根据阶段决定携带哪些工具
            var options = CreateChatOptions(activeTools);

            // 获取流式响应
            var (stream, error) = await GetStreamAsync(messages, options, cancellationToken);

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

            // 处理流式响应
            var toolCallBuilders = new Dictionary<int, ToolCallBuilder>();
            ChatFinishReason? finishReason = null;
            var chunkCount = 0;
            var assistantContent = new StringBuilder();

            await foreach (var update in stream.WithCancellation(cancellationToken))
            {
                chunkCount++;

                // 处理文本内容
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

                // 收集工具调用（增量拼接）
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
                        catch (ArgumentNullException)
                        {
                            // BinaryData 内部数据为空时忽略
                        }
                    }
                }

                if (update.FinishReason != null)
                {
                    finishReason = update.FinishReason;
                }
            }

            Log.Debug("流式响应第 {Iteration} 轮: {Chunks} chunks, {Tools} tool calls",
                iteration, chunkCount, toolCallBuilders.Count);

            // 检查是否需要执行工具
            if (finishReason != ChatFinishReason.ToolCalls || toolCallBuilders.Count == 0)
            {
                Log.Information("流式响应完成，无工具调用");
                yield break;
            }

            // 构建完整的工具调用列表
            var toolCalls = toolCallBuilders.Values.Select(b =>
            {
                var id = string.IsNullOrEmpty(b.Id) ? $"call_{Guid.NewGuid():N}" : b.Id;
                return new ToolCallInfo(id, b.FunctionName, b.Arguments.ToString());
            }).ToList();

            Log.Information("检测到 {Count} 个工具调用", toolCalls.Count);

            // 检查是否是 discover_tools 调用
            var discoverCall = toolCalls.FirstOrDefault(tc => tc.FunctionName == "discover_tools");

            if (discoverCall != null && !toolDiscoveryCompleted)
            {
                // 执行工具发现
                Log.Information("执行工具发现...");

                var discoveredTools = await DiscoverToolsAsync(discoverCall.Arguments);

                if (discoveredTools.Count > 0)
                {
                    // 将发现的工具添加到激活列表
                    activeTools = new List<ChatTool>(discoveredTools);
                    toolDiscoveryCompleted = true;

                    Log.Information("发现 {Count} 个相关工具: {Tools}",
                        activeTools.Count, string.Join(", ", activeTools.Select(t => t.FunctionName)));

                    // 添加 assistant 消息和工具结果
                    messages.Add(CreateAssistantMessageWithToolCalls(toolCalls, assistantContent.ToString()));

                    var toolNames = string.Join(", ", activeTools.Select(t => t.FunctionName));
                    var discoverResult = FunctionResult.SuccessResult(
                        $"发现 {activeTools.Count} 个相关工具: {toolNames}",
                        new { tools = activeTools.Select(t => t.FunctionName).ToList() });
                    messages.Add(new ToolChatMessage(discoverCall.Id, discoverResult.ToJson()));
                }
                else
                {
                    // 未发现工具，不携带工具继续
                    Log.Information("未发现相关工具，后续请求将不携带工具");

                    toolDiscoveryCompleted = true;
                    activeTools = new List<ChatTool>(); // 空列表表示不携带工具

                    messages.Add(CreateAssistantMessageWithToolCalls(toolCalls, assistantContent.ToString()));

                    var discoverResult = FunctionResult.SuccessResult(
                        "当前没有与任务相关的工具可用。你可以直接回复用户。",
                        new { tools = new List<string>() });
                    messages.Add(new ToolChatMessage(discoverCall.Id, discoverResult.ToJson()));
                }

                // 继续下一轮，不执行其他工具
                continue;
            }

            // 添加 assistant 消息（包含工具调用）
            messages.Add(CreateAssistantMessageWithToolCalls(toolCalls, assistantContent.ToString()));

            // 执行所有工具调用（非 discover_tools）
            foreach (var toolCall in toolCalls)
            {
                Log.Information("执行工具: {Name}, 参数: {Args}",
                    toolCall.FunctionName, toolCall.Arguments);

                var result = await ExecuteToolCallAsync(toolCall.FunctionName, toolCall.Arguments);

                Log.Information("工具 {Name} 执行结果: {Success}",
                    toolCall.FunctionName, result.Success);

                messages.Add(new ToolChatMessage(toolCall.Id, result.ToJson()));
            }
        }

        Log.Warning("达到最大迭代次数 {Max}", maxIterations);
    }

    /// <summary>
    /// 发现相关工具
    /// </summary>
    private async Task<List<ChatTool>> DiscoverToolsAsync(string argumentsJson)
    {
        var tools = new List<ChatTool>();

        try
        {
            if (_toolDiscoveryService == null)
            {
                Log.Warning("ToolDiscoveryService 未初始化");
                return tools;
            }

            // 解析参数
            string? intent = null;
            try
            {
                // 预处理：清理可能的格式包装
                var cleanedJson = PreprocessToolArguments(argumentsJson, "discover_tools");
                var args = JsonSerializer.Deserialize<JsonElement>(cleanedJson);
                if (args.TryGetProperty("intent", out var intentProp))
                {
                    intent = intentProp.GetString();
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "解析 discover_tools 参数失败");
            }

            if (string.IsNullOrWhiteSpace(intent))
            {
                Log.Warning("discover_tools 参数中缺少 intent");
                return tools;
            }

            // 执行向量检索
            var discoveredDefinitions = await _toolDiscoveryService.DiscoverToolsAsync(intent, maxResults: 5);

            // 转换为 ChatTool
            foreach (var definition in discoveredDefinitions)
            {
                var chatTool = _toolDiscoveryService.ToChatTool(definition);
                tools.Add(chatTool);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "工具发现失败");
        }

        return tools;
    }

    /// <summary>
    /// 预处理工具调用参数，清理可能的格式包装
    /// </summary>
    /// <param name="argumentsJson">原始参数字符串</param>
    /// <param name="toolName">工具名称（用于日志）</param>
    /// <returns>清理后的 JSON 字符串</returns>
    private string PreprocessToolArguments(string argumentsJson, string toolName)
    {
        var originalJson = argumentsJson;
        var cleanedJson = argumentsJson.Trim();

        // 处理 Markdown 代码块包装（如 ```json ... ``` 或 ``` ... ```）
        if (cleanedJson.StartsWith("```"))
        {
            var lines = cleanedJson.Split('\n');
            if (lines.Length >= 2)
            {
                // 移除第一行（```json 或 ```）和最后一行（```）
                var contentLines = lines.Skip(1).ToList();
                if (contentLines.Count > 0 && contentLines[^1].Trim() == "```")
                {
                    contentLines.RemoveAt(contentLines.Count - 1);
                }

                cleanedJson = string.Join('\n', contentLines).Trim();
                Log.Information("[{ToolName}] 检测到 Markdown 代码块包装，已自动清理。原始长度: {OriginalLength}, 清理后长度: {CleanedLength}",
                    toolName, originalJson.Length, cleanedJson.Length);
            }
        }

        // 处理可能的 XML 标签包装（如 <arguments>...</arguments>）
        if (cleanedJson.StartsWith('<') && cleanedJson.EndsWith('>'))
        {
            // 尝试提取标签内的 JSON 内容
            var firstContentStart = cleanedJson.IndexOf('>');
            var lastContentEnd = cleanedJson.LastIndexOf('<');

            if (firstContentStart > 0 && lastContentEnd > firstContentStart)
            {
                cleanedJson = cleanedJson.Substring(firstContentStart + 1, lastContentEnd - firstContentStart - 1).Trim();
                Log.Information("[{ToolName}] 检测到 XML 标签包装，已自动清理。原始长度: {OriginalLength}, 清理后长度: {CleanedLength}",
                    toolName, originalJson.Length, cleanedJson.Length);
            }
        }

        // 如果内容发生了变化，记录调试信息
        if (cleanedJson != originalJson.Trim())
        {
            Log.Debug("[{ToolName}] 预处理前: {OriginalJson}", toolName, originalJson);
            Log.Debug("[{ToolName}] 预处理后: {CleanedJson}", toolName, cleanedJson);
        }

        return cleanedJson;
    }

    /// <summary>
    /// 创建包含工具调用的 assistant 消息
    /// </summary>
    private AssistantChatMessage CreateAssistantMessageWithToolCalls(IEnumerable<ToolCallInfo> toolCalls, string? content = null)
    {
        AssistantChatMessage message;

        if (!string.IsNullOrWhiteSpace(content))
        {
            message = new AssistantChatMessage(content);
        }
        else
        {
            message = new AssistantChatMessage("");
        }

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

    /// <summary>
    /// 工具调用信息
    /// </summary>
    private record ToolCallInfo(string Id, string FunctionName, string Arguments);

    /// <summary>
    /// 执行工具调用
    /// </summary>
    private async Task<FunctionResult> ExecuteToolCallAsync(string functionName, string arguments)
    {
        if (_functionRegistry == null)
        {
            return FunctionResult.FailureResult("Function registry not available");
        }

        return await _functionRegistry.ExecuteAsync(functionName, arguments);
    }

    /// <summary>
    /// 创建聊天选项
    /// </summary>
    /// <param name="activeTools">激活的工具列表。null 表示只携带元工具，空列表表示不携带工具</param>
    private ChatCompletionOptions CreateChatOptions(List<ChatTool>? activeTools)
    {
        var options = new ChatCompletionOptions
        {
            Temperature = (float)_config.Temperature,
            MaxOutputTokenCount = _config.MaxTokens,
            TopP = (float)_config.TopP
        };

        Log.Debug("API 参数: Temperature={Temp}, MaxTokens={MaxTokens}, TopP={TopP}",
            _config.Temperature, _config.MaxTokens, _config.TopP);

        // 添加工具
        if (_config.EnableFunctionCalling && _toolDiscoveryService != null)
        {
            if (activeTools == null)
            {
                // 第一阶段：只携带元工具，并强制调用
                options.Tools.Add(_toolDiscoveryService.GetMetaTool());
                // 强制模型调用 discover_tools
                options.ToolChoice = ChatToolChoice.CreateFunctionChoice("discover_tools");
                Log.Debug("携带元工具 discover_tools（强制调用）");
            }
            else if (activeTools.Count > 0)
            {
                // 第二阶段：携带发现的工具，允许模型自主决定
                foreach (var tool in activeTools)
                {
                    options.Tools.Add(tool);
                }
                options.ToolChoice = ChatToolChoice.CreateAutoChoice();
                Log.Debug("携带 {Count} 个工具: {Tools}",
                    options.Tools.Count, string.Join(", ", activeTools.Select(t => t.FunctionName)));
            }
            else
            {
                // 未发现工具：不携带任何工具
                Log.Debug("不携带任何工具");
            }
        }
        else if (_config.EnableFunctionCalling && _functionRegistry?.HasFunctions == true)
        {
            // 回退到传统模式（无 ToolDiscoveryService 时）
            foreach (var tool in _functionRegistry.GetToolDefinitions())
            {
                if (tool is ChatTool chatTool)
                {
                    options.Tools.Add(chatTool);
                }
            }
            Log.Debug("传统模式：携带 {Count} 个工具", options.Tools.Count);
        }

        return options;
    }

    private async Task<(IAsyncEnumerable<StreamingChatCompletionUpdate>? Stream, string? Error)> GetStreamAsync(
        List<OpenAI.Chat.ChatMessage> messages,
        ChatCompletionOptions options,
        CancellationToken cancellationToken)
    {
        try
        {
            var stream = _chatClient!.CompleteChatStreamingAsync(messages, options, cancellationToken);
            return (stream, null);
        }
        catch (Exception ex)
        {
            return (null, ex.Message);
        }
    }

    private List<OpenAI.Chat.ChatMessage> BuildMessages(ConversationContext context, string? userMessage)
    {
        var messages = new List<OpenAI.Chat.ChatMessage>
        {
            new SystemChatMessage(_promptService.GetPrompt(PromptType.MainPersona))
        };

        // 添加历史消息
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

        // 添加当前用户消息
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
            Log.Information("开始测试 API 连接...");

            var messages = new List<OpenAI.Chat.ChatMessage>
            {
                new SystemChatMessage("Reply with 'OK' only."),
                new UserChatMessage("test")
            };

            var options = new ChatCompletionOptions
            {
                MaxOutputTokenCount = 10
            };

            var response = await _chatClient.CompleteChatAsync(messages, options);
            var content = response.Value.Content[0].Text;

            Log.Information("API 连接测试成功，响应: {Response}", content);
            return (true, "连接成功");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "API 连接测试失败");
            return (false, $"连接失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 工具调用构建器（用于流式收集参数）
    /// </summary>
    private class ToolCallBuilder
    {
        public string Id { get; set; } = string.Empty;
        public string FunctionName { get; set; } = string.Empty;
        public StringBuilder Arguments { get; set; } = new StringBuilder();
    }
}
